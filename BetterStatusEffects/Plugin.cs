using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using BetterStatusEffects.Windows;
using Lumina.Excel.Sheets;
using System;
using Dalamud.Game.ClientState.Objects.Types;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

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

    private bool partyFilterEnabled;
    private bool debugEnabled;
    private bool debugOnceQueued;

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
            HelpMessage = "Open Better Status Effects. Commands: /bse debug, /bse debug once, /bse debug on, /bse debug off, /bse partyfilter."
        });

        AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "_PartyList", OnPartyListUpdateOrDraw);
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

        AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "_PartyList", OnPartyListUpdateOrDraw);
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

            ChatGui.Print("[BSE] Debug ON. Party filter ON. Next party-list draw will dump diagnostics to /xllog.");
            return;
        }

        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            debugEnabled = false;
            debugOnceQueued = false;

            ChatGui.Print("[BSE] Debug OFF.");
            return;
        }

        if (mode.Equals("once", StringComparison.OrdinalIgnoreCase))
        {
            debugOnceQueued = true;
            partyFilterEnabled = true;

            ChatGui.Print("[BSE] One debug dump queued. Party filter ON. Check /xllog after the party list draws.");
            return;
        }

        debugEnabled = !debugEnabled;

        if (debugEnabled)
        {
            partyFilterEnabled = true;
            debugOnceQueued = true;
            ChatGui.Print("[BSE] Debug ON. Party filter ON. Next party-list draw will dump diagnostics to /xllog.");
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

        var statusSheet = DataManager.GetExcelSheet<Status>();

        ChatGui.Print($"[BSE] Statuses on {target.Name}:");

        var foundAny = false;

        foreach (var status in battleChara.StatusList)
        {
            if (status.StatusId == 0)
                continue;

            foundAny = true;

            var statusName = "Unknown";

            if (statusSheet.TryGetRow(status.StatusId, out var statusRow))
                statusName = statusRow.Name.ToString();

            ChatGui.Print(
                $"[BSE] ID {status.StatusId} | {statusName} | Param {status.Param} | Time {status.RemainingTime:0.0}s | Source {status.SourceId}"
            );
        }

        if (!foundAny)
            ChatGui.Print("[BSE] Target has no visible statuses.");
    }

    private void PrintLoadedCategories()
    {
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

    private List<StatusCategoryEntry> LoadStatusCategoryEntries()
    {
        var pluginDirectory = PluginInterface.AssemblyLocation.Directory?.FullName;

        if (pluginDirectory == null)
            return new List<StatusCategoryEntry>();

        var statusesDirectory = Path.Combine(pluginDirectory, "Data", "Statuses");

        if (!Directory.Exists(statusesDirectory))
            return new List<StatusCategoryEntry>();

        var jsonFiles = Directory.GetFiles(statusesDirectory, "*.json");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var allEntries = new List<StatusCategoryEntry>();

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

        return allEntries;
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

        var statusSheet = DataManager.GetExcelSheet<Status>();

        ChatGui.Print($"[BSE] Checking statuses on {target.Name}:");

        var foundAny = false;
        var hideCount = 0;
        var showCount = 0;

        foreach (var status in battleChara.StatusList)
        {
            if (status.StatusId == 0)
                continue;

            foundAny = true;

            var statusName = "Unknown";

            if (statusSheet.TryGetRow(status.StatusId, out var statusRow))
                statusName = statusRow.Name.ToString();

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

        var shouldDebugThisDraw = type == AddonEvent.PreDraw && (debugEnabled || debugOnceQueued);

        if (shouldDebugThisDraw && debugOnceQueued)
            debugOnceQueued = false;

        if (partyFilterEnabled)
            ApplyPartyFilterToPartyList(addon, shouldDebugThisDraw);

        if (shouldDebugThisDraw)
            DumpPartyListDebug(addon);
    }


    private HashSet<ushort> LoadHiddenStatusIds()
    {
        return LoadStatusCategoryEntries()
            .Where(entry => entry.DefaultBehavior.Equals("HideFromOthers", StringComparison.OrdinalIgnoreCase))
            .Select(entry => (ushort)entry.Id)
            .ToHashSet();
    }

    private unsafe void ApplyPartyFilterToPartyList(AtkUnitBase* addon, bool debugThisRun)
    {
        if (addon == null)
            return;

        var hiddenStatusIds = LoadHiddenStatusIds();

        if (debugThisRun)
            Log.Information($"[BSE] Party filter running. PartyList.Length = {PartyList.Length}, hidden IDs = {hiddenStatusIds.Count}");

        if (hiddenStatusIds.Count == 0)
        {
            if (debugThisRun)
                Log.Information("[BSE] No hidden status IDs loaded.");

            return;
        }


        if (PartyList.Length == 0)
        {
            if (debugThisRun)
                Log.Information("[BSE] PartyList is empty. Applying solo fallback to row node id 10.");

            ApplySoloFilterToPartyList(addon, hiddenStatusIds, debugThisRun);
            return;
        }

        for (var partyIndex = 0; partyIndex < PartyList.Length; partyIndex++)
        {
            var partyMember = PartyList[partyIndex];

            if (partyMember == null)
            {
                if (debugThisRun)
                    Log.Information($"[BSE] Party index {partyIndex}: party member is null.");

                continue;
            }

            var visibleStatusIds = partyMember.Statuses
                .Where(status => status.StatusId != 0)
                .Select(status => status.StatusId)
                .Take(10)
                .ToList();

            var hiddenSlotIndexes = new HashSet<int>();

            for (var slotIndex = 0; slotIndex < visibleStatusIds.Count; slotIndex++)
            {
                if (hiddenStatusIds.Contains((ushort)visibleStatusIds[slotIndex]))
                    hiddenSlotIndexes.Add(slotIndex);
            }

            if (debugThisRun)
            {
                Log.Information($"[BSE] Party index {partyIndex} name={partyMember.Name}");
                Log.Information($"[BSE] Party index {partyIndex} entityId={partyMember.EntityId}");
                Log.Information($"[BSE] Party index {partyIndex} statuses: {FormatStatusIds(visibleStatusIds)}");
                Log.Information($"[BSE] Party index {partyIndex} hidden slots: {string.Join(", ", hiddenSlotIndexes)}");
            }

            var rowNodeId = 10 + partyIndex;
            var rowNode = FindPartyRowNode(addon, rowNodeId);

            if (rowNode == null)
            {
                if (debugThisRun)
                    Log.Information($"[BSE] Party index {partyIndex}: could not find row node id {rowNodeId}.");

                continue;
            }

            HidePartyListStatusSlots(rowNode, hiddenSlotIndexes, visibleStatusIds.Count);
        }
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

  private unsafe void CompactPartyListStatusSlots(AtkResNode* partyRowNode, HashSet<int> hiddenSlotIndexes, int visibleStatusCount)
    {
        if (partyRowNode == null)
            return;

        var componentNode = (AtkComponentNode*)partyRowNode;
        var component = componentNode->Component;

        if (component == null)
            return;

        const int firstStatusChildIndex = 5;
        const int maxStatusSlots = 10;
        const short firstSlotX = 263;
        const short slotSpacing = 24;
        const short hiddenX = 9999;

        var writeSlotIndex = 0;

        for (var slotIndex = 0; slotIndex < maxStatusSlots; slotIndex++)
        {
            var childIndex = firstStatusChildIndex + slotIndex;

            if (childIndex < 0 || childIndex >= component->UldManager.NodeListCount)
                continue;

            var statusNode = component->UldManager.NodeList[childIndex];

            if (statusNode == null)
                continue;

            var isRealStatusSlot = statusNode->NodeId == 18 || statusNode->NodeId >= 180001;

            if (!isRealStatusSlot)
                continue;

            var isKnownVisibleStatus = slotIndex < visibleStatusCount;
            var shouldHide = isKnownVisibleStatus && hiddenSlotIndexes.Contains(slotIndex);

            if (!isKnownVisibleStatus || shouldHide)
            {
                statusNode->ToggleVisibility(false);
                statusNode->SetPositionShort(hiddenX, (short)statusNode->Y);
                continue;
            }

            var newX = (short)(firstSlotX + (writeSlotIndex * slotSpacing));
            statusNode->SetPositionShort(newX, (short)statusNode->Y);
            statusNode->ToggleVisibility(true);

            writeSlotIndex++;
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
        Log.Information($"[BSE] Hidden status IDs: {FormatStatusIds(hiddenStatusIds.OrderBy(id => id).Select(id => (uint)id))}");

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

            var hiddenSlotIndexes = new List<int>();

            for (var slotIndex = 0; slotIndex < visibleStatusIds.Count; slotIndex++)
            {
                if (hiddenStatusIds.Contains((ushort)visibleStatusIds[slotIndex]))
                    hiddenSlotIndexes.Add(slotIndex);
            }

            Log.Information($"[BSE] Party index {partyIndex} name={partyMember.Name}");
            Log.Information($"[BSE] Party index {partyIndex} entityId={partyMember.EntityId}");
            Log.Information($"[BSE] Party index {partyIndex} statuses: {FormatStatusIds(visibleStatusIds)}");
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

        const int firstStatusChildIndex = 5;
        const int maxStatusSlots = 10;

        Log.Information($"[BSE] {label}: component child count = {component->UldManager.NodeListCount}");

        for (var slotIndex = 0; slotIndex < maxStatusSlots; slotIndex++)
        {
            var childIndex = firstStatusChildIndex + slotIndex;

            if (childIndex < 0 || childIndex >= component->UldManager.NodeListCount)
            {
                Log.Information($"[BSE] {label}: slot {slotIndex}, childIndex {childIndex}: out of range.");
                continue;
            }

            var statusNode = component->UldManager.NodeList[childIndex];

            if (statusNode == null)
            {
                Log.Information($"[BSE] {label}: slot {slotIndex}, childIndex {childIndex}: null.");
                continue;
            }

            Log.Information(
                $"[BSE] {label}: slot={slotIndex} childIndex={childIndex} nodeId={statusNode->NodeId} type={statusNode->Type} visible={statusNode->IsVisible()} x={statusNode->X} y={statusNode->Y} w={statusNode->Width} h={statusNode->Height}"
            );
        }
    }


    private string FormatStatusId(uint statusId)
    {
        var statusSheet = DataManager.GetExcelSheet<Status>();

        if (statusSheet.TryGetRow(statusId, out var statusRow))
            return $"{statusId} {statusRow.Name.ToString()}";

        return $"{statusId} Unknown";
    }

    private string FormatStatusIds(IEnumerable<uint> statusIds)
    {
        return string.Join(", ", statusIds.Select(FormatStatusId));
    }



    private unsafe void ApplySoloFilterToPartyList(AtkUnitBase* addon, HashSet<ushort> hiddenStatusIds, bool debugThisRun)
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

        var hiddenSlotIndexes = new HashSet<int>();

        for (var slotIndex = 0; slotIndex < visibleStatusIds.Count; slotIndex++)
        {
            if (hiddenStatusIds.Contains((ushort)visibleStatusIds[slotIndex]))
                hiddenSlotIndexes.Add(slotIndex);
        }

        if (debugThisRun)
        {
            Log.Information($"[BSE] Solo fallback name={battleChara.Name}");
            Log.Information($"[BSE] Solo fallback statuses: {FormatStatusIds(visibleStatusIds)}");
            Log.Information($"[BSE] Solo fallback hidden slots: {string.Join(", ", hiddenSlotIndexes)}");
            Log.Information("[BSE] Solo fallback expected row node id: 10");
        }

        var rowNode = FindPartyRowNode(addon, 10);

        if (rowNode == null)
        {
            if (debugThisRun)
                Log.Information("[BSE] Solo fallback: could not find row node id 10.");

            return;
        }

        HidePartyListStatusSlots(rowNode, hiddenSlotIndexes, visibleStatusIds.Count);
    }

    private unsafe void HidePartyListStatusSlots(AtkResNode* partyRowNode, HashSet<int> hiddenSlotIndexes, int visibleStatusCount)
    {
        if (partyRowNode == null)
            return;

        var componentNode = (AtkComponentNode*)partyRowNode;
        var component = componentNode->Component;

        if (component == null)
            return;

        const int firstStatusChildIndex = 5;
        const int maxStatusSlots = 10;
        const short firstSlotX = 263;
        const short slotSpacing = 24;
        const short hiddenX = 9999;

        for (var slotIndex = 0; slotIndex < maxStatusSlots; slotIndex++)
        {
            var childIndex = firstStatusChildIndex + slotIndex;

            if (childIndex < 0 || childIndex >= component->UldManager.NodeListCount)
                continue;

            var statusNode = component->UldManager.NodeList[childIndex];

            if (statusNode == null)
                continue;

            var isRealStatusSlot = statusNode->NodeId == 18 || statusNode->NodeId >= 180001;

            if (!isRealStatusSlot)
                continue;

            var normalX = (short)(firstSlotX + (slotIndex * slotSpacing));

            if (slotIndex >= visibleStatusCount)
            {
                statusNode->ToggleVisibility(false);
                statusNode->SetPositionShort(hiddenX, (short)statusNode->Y);
                continue;
            }

            if (hiddenSlotIndexes.Contains(slotIndex))
            {
                statusNode->ToggleVisibility(false);
                statusNode->SetPositionShort(hiddenX, (short)statusNode->Y);
                continue;
            }

            statusNode->SetPositionShort(normalX, (short)statusNode->Y);
            statusNode->ToggleVisibility(true);
        }
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