using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using BetterStatusEffects.Windows;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BetterStatusEffects;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;

    private const string CommandName = "/bse";
    private const int DebugEveryNPreDraws = 10;

    private bool partyFilterEnabled = true;
    private bool debugEnabled;
    private bool debugOnceQueued;
    private int debugPreDrawCounter;

    private List<StatusCategoryEntry>? statusCategoryEntriesCache;
    private HashSet<uint>? hiddenStatusIdsCache;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("BetterStatusEffects");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var goatImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "goat.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, goatImagePath);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Better Status Effects. Commands: /bse debug, /bse debug once, /bse debug on, /bse debug off, /bse partyfilter, /bse reload."
        });

        AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "_PartyList", OnPartyListUpdateOrDraw);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"[BSE] Loaded {PluginInterface.Manifest.Name}.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, "_PartyList", OnPartyListUpdateOrDraw);

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim();

        if (args.Equals("target", StringComparison.OrdinalIgnoreCase))
        {
            PrintTargetStatuses();
            return;
        }

        if (args.Equals("categories", StringComparison.OrdinalIgnoreCase))
        {
            PrintLoadedCategories();
            return;
        }

        if (args.Equals("hidden", StringComparison.OrdinalIgnoreCase))
        {
            PrintHiddenStatuses();
            return;
        }

        if (args.Equals("checktarget", StringComparison.OrdinalIgnoreCase))
        {
            CheckTargetStatuses();
            return;
        }

        if (args.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            ClearStatusCaches();
            var hiddenStatusIds = LoadHiddenStatusIds();
            ChatGui.Print($"[BSE] Reloaded status data. Hidden status IDs: {hiddenStatusIds.Count}");
            return;
        }

        if (args.Equals("partyfilter", StringComparison.OrdinalIgnoreCase))
        {
            partyFilterEnabled = !partyFilterEnabled;
            ChatGui.Print($"[BSE] Party filter: {(partyFilterEnabled ? "ON" : "OFF")}");
            return;
        }

        if (args.Equals("debug", StringComparison.OrdinalIgnoreCase) ||
            args.StartsWith("debug ", StringComparison.OrdinalIgnoreCase))
        {
            HandleDebugCommand(args);
            return;
        }

        MainWindow.Toggle();
    }

    private void HandleDebugCommand(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mode = parts.Length >= 2 ? parts[1] : "toggle";

        if (mode.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            debugEnabled = true;
            partyFilterEnabled = true;
            debugOnceQueued = true;
            debugPreDrawCounter = 0;

            ChatGui.Print($"[BSE] Debug ON. Party filter ON. Dumps every {DebugEveryNPreDraws} party-list PreDraws.");
            return;
        }

        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            debugEnabled = false;
            debugOnceQueued = false;
            debugPreDrawCounter = 0;

            ChatGui.Print("[BSE] Debug OFF.");
            return;
        }

        if (mode.Equals("once", StringComparison.OrdinalIgnoreCase))
        {
            debugOnceQueued = true;
            partyFilterEnabled = true;

            ChatGui.Print("[BSE] One debug dump queued. Party filter ON. Check /xllog.");
            return;
        }

        debugEnabled = !debugEnabled;
        debugPreDrawCounter = 0;

        if (debugEnabled)
        {
            partyFilterEnabled = true;
            debugOnceQueued = true;
            ChatGui.Print($"[BSE] Debug ON. Party filter ON. Dumps every {DebugEveryNPreDraws} party-list PreDraws.");
        }
        else
        {
            debugOnceQueued = false;
            ChatGui.Print("[BSE] Debug OFF.");
        }
    }

    private void PrintTargetStatuses()
    {
        var target = TargetManager.Target;

        if (target is not IBattleChara battleChara)
        {
            ChatGui.Print("[BSE] Target a player, enemy, or battle NPC first.");
            return;
        }

        ChatGui.Print($"[BSE] Statuses on {target.Name}:");

        var foundAny = false;

        foreach (var status in battleChara.StatusList)
        {
            if (status.StatusId == 0)
                continue;

            foundAny = true;

            ChatGui.Print(
                $"[BSE] ID {status.StatusId} | {FormatStatusId(status.StatusId)} | Param {status.Param} | Time {status.RemainingTime:0.0}s | Source {status.SourceId}"
            );
        }

        if (!foundAny)
            ChatGui.Print("[BSE] Target has no visible statuses.");
    }

    private void PrintLoadedCategories()
    {
        ClearStatusCaches();

        var pluginDirectory = PluginInterface.AssemblyLocation.Directory?.FullName;

        if (pluginDirectory == null)
        {
            ChatGui.Print("[BSE] Could not find plugin directory.");
            return;
        }

        var statusesDirectory = Path.Combine(pluginDirectory, "Data", "Statuses");

        if (!Directory.Exists(statusesDirectory))
        {
            ChatGui.Print($"[BSE] Status category folder not found: {statusesDirectory}");
            return;
        }

        var jsonFiles = Directory.GetFiles(statusesDirectory, "*.json")
            .OrderBy(file => file)
            .ToList();

        if (jsonFiles.Count == 0)
        {
            ChatGui.Print($"[BSE] No JSON files found in: {statusesDirectory}");
            return;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        ChatGui.Print("[BSE] Loaded status categories:");

        var totalStatuses = 0;
        var totalHideFromOthers = 0;
        var totalAlwaysShow = 0;

        foreach (var file in jsonFiles)
        {
            try
            {
                var json = File.ReadAllText(file);
                var entries = JsonSerializer.Deserialize<List<StatusCategoryEntry>>(json, options) ?? new List<StatusCategoryEntry>();

                var categoryName = Path.GetFileNameWithoutExtension(file);

                var hideFromOthersCount = entries.Count(entry =>
                    entry.DefaultBehavior.Equals("HideFromOthers", StringComparison.OrdinalIgnoreCase));

                var alwaysShowCount = entries.Count(entry =>
                    entry.DefaultBehavior.Equals("AlwaysShow", StringComparison.OrdinalIgnoreCase));

                totalStatuses += entries.Count;
                totalHideFromOthers += hideFromOthersCount;
                totalAlwaysShow += alwaysShowCount;

                ChatGui.Print($"[BSE] {categoryName}: {entries.Count} statuses | HideFromOthers: {hideFromOthersCount} | AlwaysShow: {alwaysShowCount}");
            }
            catch (Exception ex)
            {
                var categoryName = Path.GetFileNameWithoutExtension(file);
                ChatGui.Print($"[BSE] Failed to load {categoryName}: {ex.Message}");
            }
        }

        ChatGui.Print($"[BSE] Total: {totalStatuses} statuses | HideFromOthers: {totalHideFromOthers} | AlwaysShow: {totalAlwaysShow}");
    }

    private void PrintHiddenStatuses()
    {
        ClearStatusCaches();

        var entries = LoadStatusCategoryEntries();

        var hiddenEntries = entries
            .Where(entry => entry.DefaultBehavior.Equals("HideFromOthers", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Category)
            .ThenBy(entry => entry.Id)
            .ToList();

        if (hiddenEntries.Count == 0)
        {
            ChatGui.Print("[BSE] No HideFromOthers statuses found.");
            return;
        }

        ChatGui.Print($"[BSE] HideFromOthers statuses: {hiddenEntries.Count}");

        foreach (var entry in hiddenEntries)
            ChatGui.Print($"[BSE] {entry.Id} | {entry.Name} | {entry.Category}");
    }

    private void CheckTargetStatuses()
    {
        var target = TargetManager.Target;

        if (target is not IBattleChara battleChara)
        {
            ChatGui.Print("[BSE] Target a player, enemy, or battle NPC first.");
            return;
        }

        var entries = LoadStatusCategoryEntries();

        var hideEntriesById = entries
            .Where(entry => entry.DefaultBehavior.Equals("HideFromOthers", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var alwaysShowEntriesById = entries
            .Where(entry => entry.DefaultBehavior.Equals("AlwaysShow", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.First());

        ChatGui.Print($"[BSE] Checking statuses on {target.Name}:");

        var foundAny = false;
        var hideCount = 0;
        var showCount = 0;

        foreach (var status in battleChara.StatusList)
        {
            if (status.StatusId == 0)
                continue;

            foundAny = true;

            var statusName = GetStatusName(status.StatusId);

            if (alwaysShowEntriesById.TryGetValue(status.StatusId, out var alwaysShowEntry))
            {
                showCount++;
                ChatGui.Print($"[BSE] SHOW | {status.StatusId} | {statusName} | {alwaysShowEntry.Category}");
                continue;
            }

            if (hideEntriesById.TryGetValue(status.StatusId, out var hideEntry))
            {
                hideCount++;
                ChatGui.Print($"[BSE] HIDE | {status.StatusId} | {statusName} | {hideEntry.Category}");
                continue;
            }

            showCount++;
            ChatGui.Print($"[BSE] SHOW | {status.StatusId} | {statusName} | Unlisted");
        }

        if (!foundAny)
        {
            ChatGui.Print("[BSE] Target has no visible statuses.");
            return;
        }

        ChatGui.Print($"[BSE] Result: would hide {hideCount}, would show {showCount}.");
    }

    private unsafe void OnPartyListUpdateOrDraw(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;

        if (addon == null)
            return;

        var shouldDebugThisRun = false;

        if (type == AddonEvent.PreDraw)
        {
            if (debugOnceQueued)
            {
                shouldDebugThisRun = true;
                debugOnceQueued = false;
            }
            else if (debugEnabled)
            {
                debugPreDrawCounter++;

                if (debugPreDrawCounter >= DebugEveryNPreDraws)
                {
                    debugPreDrawCounter = 0;
                    shouldDebugThisRun = true;
                }
            }
        }

        if (partyFilterEnabled)
            ApplyPartyFilterToPartyList(addon, shouldDebugThisRun);

        if (shouldDebugThisRun)
            DumpPartyListDebug(addon);
    }

    private void ClearStatusCaches()
    {
        statusCategoryEntriesCache = null;
        hiddenStatusIdsCache = null;
    }

    private List<StatusCategoryEntry> LoadStatusCategoryEntries()
    {
        if (statusCategoryEntriesCache != null)
            return statusCategoryEntriesCache;

        var allEntries = new List<StatusCategoryEntry>();

        var pluginDirectory = PluginInterface.AssemblyLocation.Directory?.FullName;

        if (pluginDirectory == null)
        {
            statusCategoryEntriesCache = allEntries;
            return statusCategoryEntriesCache;
        }

        var statusesDirectory = Path.Combine(pluginDirectory, "Data", "Statuses");

        if (!Directory.Exists(statusesDirectory))
        {
            statusCategoryEntriesCache = allEntries;
            return statusCategoryEntriesCache;
        }

        var jsonFiles = Directory.GetFiles(statusesDirectory, "*.json");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        foreach (var file in jsonFiles)
        {
            try
            {
                var json = File.ReadAllText(file);
                var entries = JsonSerializer.Deserialize<List<StatusCategoryEntry>>(json, options);

                if (entries != null)
                    allEntries.AddRange(entries);
            }
            catch (Exception ex)
            {
                ChatGui.Print($"[BSE] Failed to load {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        statusCategoryEntriesCache = allEntries;
        return statusCategoryEntriesCache;
    }

    private HashSet<uint> LoadHiddenStatusIds()
    {
        if (hiddenStatusIdsCache != null)
            return hiddenStatusIdsCache;

        hiddenStatusIdsCache = LoadStatusCategoryEntries()
            .Where(entry => entry.DefaultBehavior.Equals("HideFromOthers", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Id)
            .ToHashSet();

        return hiddenStatusIdsCache;
    }

    private unsafe void ApplyPartyFilterToPartyList(AtkUnitBase* addon, bool debugThisRun)
    {
        if (addon == null)
            return;

        var hiddenStatusIds = LoadHiddenStatusIds();

        if (debugThisRun)
            Log.Information($"[BSE] Party filter running. PartyList.Length={PartyList.Length}, hidden IDs={hiddenStatusIds.Count}");

        if (hiddenStatusIds.Count == 0)
            return;

        if (PartyList.Length == 0)
        {
            ApplySoloFilterToPartyList(addon, hiddenStatusIds, debugThisRun);
            return;
        }

        for (var partyIndex = 0; partyIndex < PartyList.Length; partyIndex++)
        {
            var partyMember = PartyList[partyIndex];

            if (partyMember == null)
                continue;

            var visibleStatusIds = partyMember.Statuses
                .Where(status => status.StatusId != 0)
                .Select(status => status.StatusId)
                .Take(10)
                .ToList();

            var hiddenSlotIndexes = BuildHiddenSlotIndexes(visibleStatusIds, hiddenStatusIds);

            if (debugThisRun)
            {
                Log.Information($"[BSE] Party index {partyIndex} name={partyMember.Name}");
                Log.Information($"[BSE] Party index {partyIndex} raw status list: {FormatSlotMap(visibleStatusIds)}");
                Log.Information($"[BSE] Party index {partyIndex} hidden slots: {string.Join(", ", hiddenSlotIndexes)}");
                Log.Information($"[BSE] Party index {partyIndex} expected row node id: {10 + partyIndex}");
            }

            var rowNode = FindPartyRowNode(addon, 10 + partyIndex);

            if (rowNode == null)
                continue;

            ApplyPartyListStatusVisibilityOnly(rowNode, hiddenSlotIndexes, visibleStatusIds.Count);
        }
    }

    private unsafe void ApplySoloFilterToPartyList(AtkUnitBase* addon, HashSet<uint> hiddenStatusIds, bool debugThisRun)
    {
        if (addon == null)
            return;

        var battleChara = ObjectTable
            .OfType<IBattleChara>()
            .FirstOrDefault(obj => obj.IsTargetable);

        if (battleChara == null)
        {
            if (debugThisRun)
                Log.Information("[BSE] Solo fallback: could not find a targetable battle character.");

            return;
        }

        var visibleStatusIds = battleChara.StatusList
            .Where(status => status.StatusId != 0)
            .Select(status => status.StatusId)
            .Take(10)
            .ToList();

        var hiddenSlotIndexes = BuildHiddenSlotIndexes(visibleStatusIds, hiddenStatusIds);

        if (debugThisRun)
        {
            Log.Information($"[BSE] Solo fallback name={battleChara.Name}");
            Log.Information($"[BSE] Solo fallback raw status list: {FormatSlotMap(visibleStatusIds)}");
            Log.Information($"[BSE] Solo fallback hidden slots: {string.Join(", ", hiddenSlotIndexes)}");
            Log.Information("[BSE] Solo fallback expected row node id: 10");
        }

        var rowNode = FindPartyRowNode(addon, 10);

        if (rowNode == null)
            return;

        ApplyPartyListStatusVisibilityOnly(rowNode, hiddenSlotIndexes, visibleStatusIds.Count);
    }

    private HashSet<int> BuildHiddenSlotIndexes(IReadOnlyList<uint> visibleStatusIds, HashSet<uint> hiddenStatusIds)
    {
        var hiddenSlotIndexes = new HashSet<int>();

        for (var slotIndex = 0; slotIndex < visibleStatusIds.Count; slotIndex++)
        {
            if (hiddenStatusIds.Contains(visibleStatusIds[slotIndex]))
                hiddenSlotIndexes.Add(slotIndex);
        }

        return hiddenSlotIndexes;
    }

    private unsafe AtkResNode* FindPartyRowNode(AtkUnitBase* addon, int rowNodeId)
    {
        if (addon == null)
            return null;

        for (var nodeIndex = 0; nodeIndex < addon->UldManager.NodeListCount; nodeIndex++)
        {
            var node = addon->UldManager.NodeList[nodeIndex];

            if (node == null)
                continue;

            if (node->NodeId == rowNodeId)
                return node;
        }

        return null;
    }

    private static bool IsPartyStatusCloneNode(uint nodeId)
    {
        return nodeId >= 180001 && nodeId <= 180009;
    }

    private unsafe void HidePartyStatusSlot(AtkResNode* statusNode)
    {
        if (statusNode == null)
            return;

        // Hide only the outer status slot.
        // Do not hide inner image/text nodes, or recycled nodes can keep broken timers/icons.
        statusNode->ToggleVisibility(false);
    }

    private unsafe void RestorePartyStatusSlot(AtkResNode* statusNode)
    {
        if (statusNode == null)
            return;

        statusNode->ToggleVisibility(true);

        if (statusNode->Type.ToString() != "1002")
            return;

        var componentNode = (AtkComponentNode*)statusNode;
        var component = componentNode->Component;

        if (component == null)
            return;

        for (var i = 0; i < component->UldManager.NodeListCount; i++)
        {
            var child = component->UldManager.NodeList[i];

            if (child == null)
                continue;

            // NodeId 4 is the extra overlay/bar from the debug logs.
            // Leave nodeId 3 icon and nodeId 2 timer text alone.
            if (child->NodeId == 4)
                child->ToggleVisibility(false);
        }
    }

    private unsafe void ApplyPartyListStatusVisibilityOnly(
        AtkResNode* partyRowNode,
        HashSet<int> hiddenSlotIndexes,
        int visibleStatusCount)
    {
        if (partyRowNode == null)
            return;

        var componentNode = (AtkComponentNode*)partyRowNode;
        var component = componentNode->Component;

        if (component == null)
            return;

        const int maxStatusSlots = 10;
        const int firstStatusChildIndex = 5;

        for (var slotIndex = 0; slotIndex < maxStatusSlots; slotIndex++)
        {
            var childIndex = firstStatusChildIndex + slotIndex;

            if (childIndex < 0 || childIndex >= component->UldManager.NodeListCount)
                continue;

            var statusNode = component->UldManager.NodeList[childIndex];

            if (statusNode == null)
                continue;

            var isStatusIconNode =
                statusNode->Type.ToString() == "1002" &&
                (statusNode->NodeId == 18 || IsPartyStatusCloneNode(statusNode->NodeId));

            if (!isStatusIconNode)
                continue;

            var shouldHide =
                slotIndex >= visibleStatusCount ||
                hiddenSlotIndexes.Contains(slotIndex);

            if (shouldHide)
                HidePartyStatusSlot(statusNode);
            else
                RestorePartyStatusSlot(statusNode);
        }
    }

    private unsafe void DumpPartyListDebug(AtkUnitBase* addon)
    {
        if (addon == null)
            return;

        Log.Information("[BSE] ===== PARTY DEBUG START =====");
        Log.Information($"[BSE] Party filter enabled: {partyFilterEnabled}");
        Log.Information($"[BSE] Persistent debug enabled: {debugEnabled}");
        Log.Information($"[BSE] PartyList.Length: {PartyList.Length}");
        Log.Information($"[BSE] Addon NodeListCount: {addon->UldManager.NodeListCount}");

        var hiddenStatusIds = LoadHiddenStatusIds();

        Log.Information($"[BSE] Hidden status IDs loaded: {hiddenStatusIds.Count}");
        Log.Information($"[BSE] Hidden status IDs: {FormatStatusIds(hiddenStatusIds.OrderBy(id => id))}");

        if (PartyList.Length == 0)
        {
            var battleChara = ObjectTable
                .OfType<IBattleChara>()
                .FirstOrDefault(obj => obj.IsTargetable);

            if (battleChara == null)
            {
                Log.Information("[BSE] Debug solo raw status list: no targetable battle character found.");
            }
            else
            {
                var visibleStatusIds = battleChara.StatusList
                    .Where(status => status.StatusId != 0)
                    .Select(status => status.StatusId)
                    .Take(10)
                    .ToList();

                var hiddenSlotIndexes = BuildHiddenSlotIndexes(visibleStatusIds, hiddenStatusIds);

                Log.Information($"[BSE] Debug solo name={battleChara.Name}");
                Log.Information($"[BSE] Debug solo raw status list: {FormatSlotMap(visibleStatusIds)}");
                Log.Information($"[BSE] Debug solo hidden slots: {string.Join(", ", hiddenSlotIndexes)}");
            }
        }

        for (var partyIndex = 0; partyIndex < PartyList.Length; partyIndex++)
        {
            var partyMember = PartyList[partyIndex];

            if (partyMember == null)
            {
                Log.Information($"[BSE] Party index {partyIndex}: party member is null.");
                continue;
            }

            var visibleStatusIds = partyMember.Statuses
                .Where(status => status.StatusId != 0)
                .Select(status => status.StatusId)
                .Take(10)
                .ToList();

            var hiddenSlotIndexes = BuildHiddenSlotIndexes(visibleStatusIds, hiddenStatusIds);

            Log.Information($"[BSE] Party index {partyIndex} name={partyMember.Name}");
            Log.Information($"[BSE] Party index {partyIndex} entityId={partyMember.EntityId}");
            Log.Information($"[BSE] Party index {partyIndex} raw status list: {FormatSlotMap(visibleStatusIds)}");
            Log.Information($"[BSE] Party index {partyIndex} hidden slots: {string.Join(", ", hiddenSlotIndexes)}");
            Log.Information($"[BSE] Party index {partyIndex} expected row node id: {10 + partyIndex}");
        }

        Log.Information("[BSE] ----- Addon nodes -----");

        for (var nodeIndex = 0; nodeIndex < addon->UldManager.NodeListCount; nodeIndex++)
        {
            var node = addon->UldManager.NodeList[nodeIndex];

            if (node == null)
                continue;

            Log.Information(
                $"[BSE] AddonNode index={nodeIndex} nodeId={node->NodeId} type={node->Type} visible={node->IsVisible()} x={node->X} y={node->Y} w={node->Width} h={node->Height}"
            );

            if (node->NodeId >= 10 && node->NodeId <= 17)
                DumpPartyRowStatusSlots(node, $"rowNodeId={node->NodeId}");
        }

        Log.Information("[BSE] ===== PARTY DEBUG END =====");

        ChatGui.Print("[BSE] Party debug dumped to /xllog.");
    }

    private unsafe void DumpPartyRowStatusSlots(AtkResNode* partyRowNode, string label)
    {
        if (partyRowNode == null)
            return;

        var componentNode = (AtkComponentNode*)partyRowNode;
        var component = componentNode->Component;

        if (component == null)
        {
            Log.Information($"[BSE] {label}: row component null.");
            return;
        }

        Log.Information($"[BSE] {label}: component child count = {component->UldManager.NodeListCount}");

        var statusIconCount = 0;

        for (var childIndex = 0; childIndex < component->UldManager.NodeListCount; childIndex++)
        {
            var node = component->UldManager.NodeList[childIndex];

            if (node == null)
                continue;

            var isStatusIconNode =
                node->Type.ToString() == "1002" &&
                (node->NodeId == 18 || IsPartyStatusCloneNode(node->NodeId));

            if (!isStatusIconNode)
                continue;

            statusIconCount++;

            Log.Information(
                $"[BSE] {label}: OUTER childIndex={childIndex} nodeId={node->NodeId} type={node->Type} visible={node->IsVisible()} x={node->X} y={node->Y} w={node->Width} h={node->Height}"
            );

            var iconComponentNode = (AtkComponentNode*)node;
            var iconComponent = iconComponentNode->Component;

            if (iconComponent == null)
            {
                Log.Information($"[BSE] {label}: OUTER nodeId={node->NodeId}: inner component null.");
                continue;
            }

            Log.Information($"[BSE] {label}: OUTER nodeId={node->NodeId}: inner child count = {iconComponent->UldManager.NodeListCount}");

            for (var innerIndex = 0; innerIndex < iconComponent->UldManager.NodeListCount; innerIndex++)
            {
                var innerNode = iconComponent->UldManager.NodeList[innerIndex];

                if (innerNode == null)
                    continue;

                Log.Information(
                    $"[BSE] {label}: INNER outerNodeId={node->NodeId} innerIndex={innerIndex} innerNodeId={innerNode->NodeId} type={innerNode->Type} visible={innerNode->IsVisible()} x={innerNode->X} y={innerNode->Y} w={innerNode->Width} h={innerNode->Height}"
                );
            }
        }

        Log.Information($"[BSE] {label}: status icon node count = {statusIconCount}");
    }

    private string GetStatusName(uint statusId)
    {
        var statusSheet = DataManager.GetExcelSheet<Status>();

        if (statusSheet.TryGetRow(statusId, out var statusRow))
            return statusRow.Name.ToString();

        return "Unknown";
    }

    private string FormatStatusId(uint statusId)
    {
        return $"{statusId} {GetStatusName(statusId)}";
    }

    private string FormatStatusIds(IEnumerable<uint> statusIds)
    {
        return string.Join(", ", statusIds.Select(FormatStatusId));
    }

    private string FormatSlotMap(IReadOnlyList<uint> statusIds)
    {
        var parts = new List<string>();

        for (var i = 0; i < statusIds.Count; i++)
            parts.Add($"{i}={FormatStatusId(statusIds[i])}");

        return string.Join(", ", parts);
    }

    private sealed class StatusCategoryEntry
    {
        public uint Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string DefaultBehavior { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}