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
using System.Reflection;
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
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/bse";
    private const string PartyListAddonName = "_PartyList";

    private const int DebugEveryNPreDraws = 10;

    private const int FirstPartyStatusChildIndex = 5;
    private const int MaxPartyStatusSlots = 10;

    private const short PartyStatusSlotBaseX = 263;
    private const short PartyStatusSlotSpacingX = 24;
    private const short PartyStatusSlotY = 12;
    private const ushort PartyStatusSlotWidth = 24;
    private const ushort PartyStatusSlotHeight = 41;

    private static readonly HashSet<uint> IgnoredPartyListNoiseStatusIds = new()
    {
        48,
        360,
        361,
        362,
        364,
        365,
        1411,
    };

    private static readonly AddonEvent[] PartyListAddonEvents =
    {
        AddonEvent.PostSetup,
        AddonEvent.PostShow,
        AddonEvent.PostRequestedUpdate,
        AddonEvent.PostRefresh,
        AddonEvent.PostUpdate,
        AddonEvent.PreDraw,
    };

    private bool partyFilterEnabled = true;
    private bool debugEnabled;
    private bool debugOnceQueued;
    private int debugPreDrawCounter;

    private nint lastPartyListAddonAddress;
    private bool applyingPartyFilter;

    private readonly Dictionary<nint, PartyStatusNodeSnapshot> hiddenPartyStatusNodeSnapshots = new();

    private string lastPartyListStatusSignature = string.Empty;

    private List<StatusCategoryEntry>? statusCategoryEntriesCache;
    private HashSet<uint>? hiddenStatusIdsCache;
    private HashSet<uint>? hiddenStatusIconIdsCache;

    private static bool partyListPriorityReflectionResolved;
    private static MemberInfo? partyListPriorityMember;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("BetterStatusEffects");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var pluginDirectory = PluginInterface.AssemblyLocation.Directory?.FullName
            ?? PluginInterface.ConfigDirectory.FullName;

        var goatImagePath = Path.Combine(pluginDirectory, "goat.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, goatImagePath);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Better Status Effects. Commands: /bse target, /bse categories, /bse hidden, /bse checktarget, /bse debug, /bse debug once, /bse debug on, /bse debug off, /bse partyfilter, /bse reload."
        });

        foreach (var addonEvent in PartyListAddonEvents)
            AddonLifecycle.RegisterListener(addonEvent, PartyListAddonName, OnPartyListUpdateOrDraw);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"[BSE] Loaded {PluginInterface.Manifest.Name}.");
    }

    public void Dispose()
    {
        RestoreHiddenPartyStatusNodesFromLastAddon();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        foreach (var addonEvent in PartyListAddonEvents)
            AddonLifecycle.UnregisterListener(addonEvent, PartyListAddonName, OnPartyListUpdateOrDraw);

        CommandManager.RemoveHandler(CommandName);

        lastPartyListAddonAddress = nint.Zero;
        hiddenPartyStatusNodeSnapshots.Clear();
        lastPartyListStatusSignature = string.Empty;
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
            lastPartyListStatusSignature = string.Empty;

            var hiddenStatusIds = LoadHiddenStatusIds();
            var hiddenStatusIconIds = LoadHiddenStatusIconIds();

            ChatGui.Print($"[BSE] Reloaded status data. Hidden status IDs: {hiddenStatusIds.Count}. Hidden icon IDs: {hiddenStatusIconIds.Count}.");
            return;
        }

        if (args.Equals("partyfilter", StringComparison.OrdinalIgnoreCase))
        {
            if (partyFilterEnabled)
            {
                partyFilterEnabled = false;
                RestoreHiddenPartyStatusNodesFromLastAddon();
                lastPartyListStatusSignature = string.Empty;
                ChatGui.Print("[BSE] Party filter: OFF");
            }
            else
            {
                partyFilterEnabled = true;
                lastPartyListStatusSignature = string.Empty;
                ChatGui.Print("[BSE] Party filter: ON");
                ApplyPartyFilterToLastAddon(false);
            }

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

            var priorityText = TryGetPartyListPriority(status.StatusId, out var priority)
                ? priority.ToString()
                : "unknown";

            ChatGui.Print(
                $"[BSE] ID {status.StatusId} | {FormatStatusId(status.StatusId)} | Icon {GetStatusIconId(status.StatusId)} | PartyListPriority {priorityText} | Param {status.Param} | Time {status.RemainingTime:0.0}s | Source {status.SourceId}"
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
                    string.Equals(entry.DefaultBehavior, "HideFromOthers", StringComparison.OrdinalIgnoreCase));

                var alwaysShowCount = entries.Count(entry =>
                    string.Equals(entry.DefaultBehavior, "AlwaysShow", StringComparison.OrdinalIgnoreCase));

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
            .Where(entry => string.Equals(entry.DefaultBehavior, "HideFromOthers", StringComparison.OrdinalIgnoreCase))
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
        {
            var priorityText = TryGetPartyListPriority(entry.Id, out var priority)
                ? priority.ToString()
                : "unknown";

            ChatGui.Print($"[BSE] {entry.Id} | {entry.Name} | Icon {GetStatusIconId(entry.Id)} | PartyListPriority {priorityText} | {entry.Category}");
        }
    }

    private void CheckTargetStatuses()
    {
        var target = TargetManager.Target;

        if (target is not IBattleChara battleChara)
        {
            ChatGui.Print("[BSE] Target a player, enemy, or battle NPC first.");
            return;
        }

        var hiddenStatusIds = LoadHiddenStatusIds();

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
            var iconId = GetStatusIconId(status.StatusId);
            var hiddenByStatusId = hiddenStatusIds.Contains(status.StatusId);

            var priorityText = TryGetPartyListPriority(status.StatusId, out var priority)
                ? priority.ToString()
                : "unknown";

            if (hiddenByStatusId)
            {
                hideCount++;
                ChatGui.Print($"[BSE] HIDE | {status.StatusId} | {statusName} | Icon {iconId} | PartyListPriority {priorityText}");
                continue;
            }

            showCount++;
            ChatGui.Print($"[BSE] SHOW | {status.StatusId} | {statusName} | Icon {iconId} | PartyListPriority {priorityText}");
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
        var addonAddress = args.Addon.Address;
        var addon = (AtkUnitBase*)addonAddress;

        if (addon == null)
            return;

        if (lastPartyListAddonAddress != addonAddress)
        {
            lastPartyListAddonAddress = addonAddress;
            hiddenPartyStatusNodeSnapshots.Clear();
            lastPartyListStatusSignature = string.Empty;
        }

        if (type != AddonEvent.PreDraw)
            return;

        var shouldDebugThisRun = false;

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

        if (partyFilterEnabled)
            ApplyPartyFilterToAddonWithGuard(addon, shouldDebugThisRun);

        if (shouldDebugThisRun)
            DumpPartyListDebug(addon);
    }

    private unsafe void ApplyPartyFilterToLastAddon(bool debugThisRun)
    {
        if (lastPartyListAddonAddress == nint.Zero)
            return;

        var addon = (AtkUnitBase*)lastPartyListAddonAddress;

        if (addon == null)
            return;

        ApplyPartyFilterToAddonWithGuard(addon, debugThisRun);
    }

    private unsafe void ApplyPartyFilterToAddonWithGuard(AtkUnitBase* addon, bool debugThisRun)
    {
        if (addon == null)
            return;

        if (applyingPartyFilter)
            return;

        applyingPartyFilter = true;

        try
        {
            ApplyPartyFilterToPartyList(addon, debugThisRun);
        }
        catch (Exception ex)
        {
            Log.Error($"[BSE] Party filter failed: {ex}");
        }
        finally
        {
            applyingPartyFilter = false;
        }
    }

    private void ClearStatusCaches()
    {
        statusCategoryEntriesCache = null;
        hiddenStatusIdsCache = null;
        hiddenStatusIconIdsCache = null;
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
            .Where(entry => string.Equals(entry.DefaultBehavior, "HideFromOthers", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Id)
            .ToHashSet();

        return hiddenStatusIdsCache;
    }

    private HashSet<uint> LoadHiddenStatusIconIds()
    {
        if (hiddenStatusIconIdsCache != null)
            return hiddenStatusIconIdsCache;

        var hiddenStatusIds = LoadHiddenStatusIds();
        var statusSheet = DataManager.GetExcelSheet<Status>();

        var iconIds = new HashSet<uint>();

        foreach (var statusId in hiddenStatusIds)
        {
            if (!statusSheet.TryGetRow(statusId, out var statusRow))
                continue;

            if (statusRow.Icon != 0)
                iconIds.Add(statusRow.Icon);
        }

        hiddenStatusIconIdsCache = iconIds;
        return hiddenStatusIconIdsCache;
    }

    private unsafe void ApplyPartyFilterToPartyList(AtkUnitBase* addon, bool debugThisRun)
    {
        if (addon == null)
            return;

        NormalizePartyStatusSlotGeometry(addon, debugThisRun);

        var hiddenStatusIds = LoadHiddenStatusIds();
        var hiddenIconIds = LoadHiddenStatusIconIds();

        if (debugThisRun)
        {
            Log.Information($"[BSE] Party filter running. PartyList.Length = {PartyList.Length}, hidden IDs = {hiddenStatusIds.Count}, hidden icon IDs = {hiddenIconIds.Count}, BSE-hidden-node-count = {hiddenPartyStatusNodeSnapshots.Count}");
        }

        if (hiddenIconIds.Count == 0)
        {
            lastPartyListStatusSignature = string.Empty;

            if (debugThisRun)
                Log.Information("[BSE] No hidden status icon IDs loaded. Nothing to hide.");

            return;
        }

        if (PartyList.Length == 0)
        {
            if (debugThisRun)
                Log.Information("[BSE] PartyList is empty. Applying solo UI-icon fallback to row node id 10.");

            ApplySoloFilterToPartyList(addon, hiddenStatusIds, hiddenIconIds, debugThisRun);
            return;
        }

        var renderStates = BuildPartyMemberRenderStates(hiddenStatusIds, debugThisRun);
        lastPartyListStatusSignature = BuildCombinedStatusSignature(renderStates);

        foreach (var state in renderStates)
        {
            if (debugThisRun)
            {
                Log.Information($"[BSE] Party index {state.PartyIndex} name={state.PartyMemberName}");
                Log.Information($"[BSE] Party index {state.PartyIndex} entityId={state.PartyMemberKey}");
                Log.Information($"[BSE] Party index {state.PartyIndex} raw IPartyMember.Statuses: {FormatSlotMap(state.RawStatusIds)}");
                Log.Information($"[BSE] Party index {state.PartyIndex} DEBUG raw combat-safe guess only: {FormatRenderedSlotMap(state.PartyListSlotStatusIds)}");
                Log.Information($"[BSE] Party index {state.PartyIndex} DEBUG raw guessed hidden slots only: {FormatSlotIndexes(state.HiddenSlotIndexes)}");
            }

            var rowNode = FindPartyRowNodeForMember(addon, state.PartyMemberName, state.PartyIndex, debugThisRun);

            if (rowNode == null)
            {
                if (debugThisRun)
                    Log.Information($"[BSE] Party index {state.PartyIndex} name={state.PartyMemberName}: could not find matching party row.");

                continue;
            }

            if (debugThisRun)
                Log.Information($"[BSE] Party index {state.PartyIndex} name={state.PartyMemberName}: matched UI row node id {rowNode->NodeId}.");

            ApplyPartyListStatusCompactByActualIcon(
                rowNode,
                hiddenIconIds,
                debugThisRun,
                $"partyIndex={state.PartyIndex} name={state.PartyMemberName}");
        }
    }

    private List<PartyMemberRenderState> BuildPartyMemberRenderStates(
        HashSet<uint> hiddenStatusIds,
        bool debugThisRun)
    {
        var states = new List<PartyMemberRenderState>();
        var partyCount = Math.Min(PartyList.Length, 8);

        for (var partyIndex = 0; partyIndex < partyCount; partyIndex++)
        {
            var partyMember = PartyList[partyIndex];

            if (partyMember == null)
            {
                if (debugThisRun)
                    Log.Information($"[BSE] Party index {partyIndex}: party member is null.");

                continue;
            }

            var partyMemberName = partyMember.Name.ToString();
            var partyMemberKey = partyMember.EntityId.ToString();

            var rawStatusIds = partyMember.Statuses
                .Where(status => status.StatusId != 0)
                .Select(status => status.StatusId)
                .ToList();

            var partyListSlotStatusIds = BuildCombatSafePartyListSlotStatusOrder(rawStatusIds, hiddenStatusIds);
            var hiddenSlotIndexes = BuildHiddenSlotIndexes(partyListSlotStatusIds, hiddenStatusIds);

            states.Add(new PartyMemberRenderState
            {
                PartyIndex = partyIndex,
                PartyMemberName = partyMemberName,
                PartyMemberKey = partyMemberKey,
                RawStatusIds = rawStatusIds,
                PartyListSlotStatusIds = partyListSlotStatusIds,
                HiddenSlotIndexes = hiddenSlotIndexes,
            });
        }

        return states;
    }

    private string BuildCombinedStatusSignature(List<PartyMemberRenderState> states)
    {
        if (states.Count == 0)
            return string.Empty;

        return string.Join("|", states.Select(state =>
            $"{state.PartyIndex}:{state.PartyMemberKey}:raw={string.Join(",", state.RawStatusIds)}:debugRawCombatSafeGuess={string.Join(",", state.PartyListSlotStatusIds)}:debugRawHideGuess={string.Join(",", state.HiddenSlotIndexes.OrderBy(slot => slot))}"));
    }

    private List<uint> BuildCombatSafePartyListSlotStatusOrder(
        IReadOnlyList<uint> rawStatusIds,
        HashSet<uint> hiddenStatusIds)
    {
        if (rawStatusIds.Count == 0)
            return new List<uint>();

        var result = new List<uint>();

        foreach (var statusId in rawStatusIds)
        {
            if (statusId == 0)
                continue;

            if (hiddenStatusIds.Contains(statusId))
            {
                result.Add(statusId);
                continue;
            }

            if (IgnoredPartyListNoiseStatusIds.Contains(statusId))
                continue;

            if (TryGetPartyListPriority(statusId, out var partyListPriority))
            {
                result.Add(statusId);
                continue;
            }

            result.Add(statusId);
        }

        return result
            .Take(MaxPartyStatusSlots)
            .ToList();
    }

    private HashSet<int> BuildHiddenSlotIndexes(
        IReadOnlyList<uint> slotStatusIds,
        HashSet<uint> hiddenStatusIds)
    {
        var hiddenSlotIndexes = new HashSet<int>();

        for (var slotIndex = 0; slotIndex < slotStatusIds.Count && slotIndex < MaxPartyStatusSlots; slotIndex++)
        {
            if (hiddenStatusIds.Contains(slotStatusIds[slotIndex]))
                hiddenSlotIndexes.Add(slotIndex);
        }

        return hiddenSlotIndexes;
    }

    private bool TryGetPartyListPriority(uint statusId, out uint priority)
    {
        priority = 0;

        var statusSheet = DataManager.GetExcelSheet<Status>();

        if (!statusSheet.TryGetRow(statusId, out var statusRow))
            return false;

        ResolvePartyListPriorityReflection();

        if (partyListPriorityMember == null)
            return false;

        try
        {
            object? value = partyListPriorityMember switch
            {
                PropertyInfo propertyInfo => propertyInfo.GetValue(statusRow),
                FieldInfo fieldInfo => fieldInfo.GetValue(statusRow),
                _ => null,
            };

            if (value == null)
                return false;

            priority = Convert.ToUInt32(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ResolvePartyListPriorityReflection()
    {
        if (partyListPriorityReflectionResolved)
            return;

        partyListPriorityReflectionResolved = true;

        var statusType = typeof(Status);

        var possibleNames = new[]
        {
            "PartyListPriority",
            "PartyListPrioritySelf",
            "PartyListPriorityOther",
            "PartyListPriorityOthers",
            "StatusPriority",
            "Priority",
        };

        foreach (var name in possibleNames)
        {
            var property = statusType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

            if (property != null)
            {
                partyListPriorityMember = property;
                return;
            }

            var field = statusType.GetField(name, BindingFlags.Public | BindingFlags.Instance);

            if (field != null)
            {
                partyListPriorityMember = field;
                return;
            }
        }
    }

    private unsafe void ApplySoloFilterToPartyList(
        AtkUnitBase* addon,
        HashSet<uint> hiddenStatusIds,
        HashSet<uint> hiddenIconIds,
        bool debugThisRun)
    {
        if (addon == null)
            return;

        var battleChara = ObjectTable
            .OfType<IBattleChara>()
            .FirstOrDefault(obj => obj.IsTargetable);

        if (battleChara != null)
        {
            var rawStatusIds = GetBattleCharaVisibleStatusIds(battleChara);
            var soloStatusSlotIds = rawStatusIds
                .Take(MaxPartyStatusSlots)
                .ToList();

            var hiddenSoloSlotIndexes = BuildHiddenSlotIndexes(soloStatusSlotIds, hiddenStatusIds);

            lastPartyListStatusSignature =
                $"solo:{battleChara.GameObjectId}:raw={string.Join(",", rawStatusIds)}:debugSoloSlots={string.Join(",", soloStatusSlotIds)}:debugRawHideGuess={string.Join(",", hiddenSoloSlotIndexes.OrderBy(slot => slot))}";

            if (debugThisRun)
            {
                Log.Information($"[BSE] Solo fallback name={battleChara.Name}");
                Log.Information($"[BSE] Solo fallback raw status list: {FormatSlotMap(rawStatusIds)}");
                Log.Information($"[BSE] Solo fallback DEBUG raw solo slot guess only: {FormatSlotMap(soloStatusSlotIds)}");
                Log.Information($"[BSE] Solo fallback DEBUG raw hidden slots only: {FormatSlotIndexes(hiddenSoloSlotIndexes)}");
                Log.Information("[BSE] Solo fallback expected row node id: 10");
            }
        }
        else
        {
            if (debugThisRun)
                Log.Information("[BSE] Solo fallback: could not find a battle character. Still applying UI-icon filter to row node id 10.");

            lastPartyListStatusSignature = string.Empty;
        }

        var rowNode = FindPartyRowNode(addon, 10);

        if (rowNode == null)
        {
            if (debugThisRun)
                Log.Information("[BSE] Solo fallback: could not find row node id 10.");

            return;
        }

        ApplyPartyListStatusCompactByActualIcon(
            rowNode,
            hiddenIconIds,
            debugThisRun,
            "solo rowNodeId=10");
    }

    private static List<uint> GetBattleCharaVisibleStatusIds(IBattleChara battleChara)
    {
        var result = new List<uint>();

        foreach (var status in battleChara.StatusList)
        {
            if (status.StatusId == 0)
                continue;

            result.Add(status.StatusId);
        }

        return result;
    }

    private unsafe AtkResNode* FindPartyRowNodeForMember(
        AtkUnitBase* addon,
        string partyMemberName,
        int fallbackPartyIndex,
        bool debugThisRun)
    {
        if (addon == null)
            return null;

        var normalizedPartyMemberName = NormalizeUiText(partyMemberName);

        if (!string.IsNullOrWhiteSpace(normalizedPartyMemberName))
        {
            AtkResNode* matchedRow = null;
            var matchCount = 0;

            for (var rowNodeId = 10; rowNodeId <= 17; rowNodeId++)
            {
                var rowNode = FindPartyRowNode(addon, rowNodeId);

                if (rowNode == null)
                    continue;

                if (!PartyRowContainsName(rowNode, normalizedPartyMemberName))
                    continue;

                matchedRow = rowNode;
                matchCount++;
            }

            if (matchCount == 1 && matchedRow != null)
                return matchedRow;

            if (debugThisRun && matchCount > 1)
                Log.Information($"[BSE] Ambiguous party row name match for '{partyMemberName}'. Matches={matchCount}. Falling back to index row.");
        }

        var fallbackRowNodeId = 10 + fallbackPartyIndex;

        if (debugThisRun)
            Log.Information($"[BSE] Falling back to row node id {fallbackRowNodeId} for party index {fallbackPartyIndex} name='{partyMemberName}'.");

        return FindPartyRowNode(addon, fallbackRowNodeId);
    }

    private unsafe bool PartyRowContainsName(AtkResNode* partyRowNode, string normalizedPartyMemberName)
    {
        if (partyRowNode == null)
            return false;

        if (partyRowNode->Type.ToString() != "1006")
            return false;

        var componentNode = (AtkComponentNode*)partyRowNode;
        var component = componentNode->Component;

        if (component == null)
            return false;

        for (var childIndex = 0; childIndex < component->UldManager.NodeListCount; childIndex++)
        {
            var child = component->UldManager.NodeList[childIndex];

            if (child == null)
                continue;

            if (child->Type.ToString() != "Text")
                continue;

            var text = ReadAtkTextNode(child);
            var normalizedText = NormalizeUiText(text);

            if (string.IsNullOrWhiteSpace(normalizedText))
                continue;

            if (normalizedText.Equals(normalizedPartyMemberName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalizedText.StartsWith(normalizedPartyMemberName + " ", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private unsafe string ReadAtkTextNode(AtkResNode* node)
    {
        if (node == null)
            return string.Empty;

        if (node->Type.ToString() != "Text")
            return string.Empty;

        var textNode = (AtkTextNode*)node;
        return textNode->NodeText.ToString();
    }

    private static string NormalizeUiText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return new string(text
                .Where(ch => !char.IsControl(ch))
                .ToArray())
            .Trim();
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

    private static unsafe bool IsPartyStatusIconNode(AtkResNode* node)
    {
        if (node == null)
            return false;

        return node->Type.ToString() == "1002" &&
               (node->NodeId == 18 || IsPartyStatusCloneNode(node->NodeId));
    }

    private unsafe void ApplyPartyListStatusCompactByActualIcon(
        AtkResNode* partyRowNode,
        HashSet<uint> hiddenIconIds,
        bool debugThisRun,
        string debugLabel)
    {
        if (partyRowNode == null)
            return;

        if (partyRowNode->Type.ToString() != "1006")
            return;

        var componentNode = (AtkComponentNode*)partyRowNode;
        var component = componentNode->Component;

        if (component == null)
            return;

        var compactSlotIndex = 0;

        for (var slotIndex = 0; slotIndex < MaxPartyStatusSlots; slotIndex++)
        {
            var childIndex = FirstPartyStatusChildIndex + slotIndex;

            if (childIndex < 0 || childIndex >= component->UldManager.NodeListCount)
                continue;

            var statusNode = component->UldManager.NodeList[childIndex];

            if (!IsPartyStatusIconNode(statusNode))
                continue;

            var hiddenByBse = IsPartyStatusSlotHiddenByBse(statusNode);

            if (!TryReadPartyStatusIconId(statusNode, out var actualIconId) || actualIconId == 0)
            {
                if (debugThisRun && (statusNode->IsVisible() || hiddenByBse))
                {
                    Log.Information($"[BSE] UI-icon compact {debugLabel}: slot={slotIndex} childIndex={childIndex} nodeId={statusNode->NodeId} visible={statusNode->IsVisible()} hiddenByBse={hiddenByBse} actualIconId=(none/read-failed) action=skip");
                }

                continue;
            }

            var shouldHide = hiddenIconIds.Contains(actualIconId);

            if (debugThisRun)
            {
                Log.Information($"[BSE] UI-icon compact {debugLabel}: slot={slotIndex} childIndex={childIndex} nodeId={statusNode->NodeId} visible={statusNode->IsVisible()} hiddenByBse={hiddenByBse} actualIconId={actualIconId} hiddenByIcon={shouldHide}");
            }

            if (shouldHide)
            {
                HidePartyStatusSlotVisibilityOnly(statusNode);
                continue;
            }

            RestorePartyStatusSlotIfBseHidIt(statusNode);

            if (!statusNode->IsVisible())
                continue;

            MovePartyStatusSlotToCompactPosition(statusNode, compactSlotIndex);
            compactSlotIndex++;
        }
    }

   private unsafe bool TryReadPartyStatusIconId(AtkResNode* statusNode, out uint iconId)
{
    iconId = 0;

    if (statusNode == null)
        return false;

    if (statusNode->Type.ToString() != "1002")
        return false;

    try
    {
        var componentNode = (AtkComponentNode*)statusNode;
        var component = componentNode->Component;

        if (component == null)
            return false;

        var componentType = component->GetComponentType();

        // Normal icon component, type 15.
        // This is NOT what your party-list status slots currently are,
        // but keep it as a safe fallback.
        if (componentType == ComponentType.Icon)
        {
            var iconComponent = (AtkComponentIcon*)component;

            if (iconComponent->IconId != 0 && iconComponent->IconId < 1_000_000)
            {
                iconId = iconComponent->IconId;
                return true;
            }
        }

        // Party-list status slots are type 16 / IconText.
        // The real icon is on the inner image node's loaded texture resource.
        var imageNode = component->GetImageNodeById(3);

        if (imageNode == null)
        {
            for (var i = 0; i < component->UldManager.NodeListCount; i++)
            {
                var innerNode = component->UldManager.NodeList[i];

                if (innerNode == null)
                    continue;

                if (innerNode->Type.ToString() != "Image")
                    continue;

                imageNode = (AtkImageNode*)innerNode;
                break;
            }
        }

        if (imageNode == null)
            return false;

        return TryReadIconIdFromImageNode(imageNode, out iconId);
    }
    catch
    {
        iconId = 0;
        return false;
    }
}

private unsafe bool TryReadIconIdFromImageNode(AtkImageNode* imageNode, out uint iconId)
{
    iconId = 0;

    if (imageNode == null)
        return false;

    var partsList = imageNode->PartsList;

    if (partsList == null)
        return false;

    if (imageNode->PartId >= partsList->PartCount)
        return false;

    var part = &partsList->Parts[imageNode->PartId];

    if (part == null)
        return false;

    var asset = part->UldAsset;

    if (asset == null)
        return false;

    var texture = &asset->AtkTexture;

    if (texture->TextureType != TextureType.Resource)
        return false;

    var resource = texture->Resource;

    if (resource == null)
        return false;

    iconId = resource->IconId;

    return iconId != 0;
}

    private unsafe bool IsPartyStatusSlotHiddenByBse(AtkResNode* statusNode)
    {
        if (statusNode == null)
            return false;

        return hiddenPartyStatusNodeSnapshots.ContainsKey((nint)statusNode);
    }

    private unsafe void MovePartyStatusSlotToCompactPosition(
        AtkResNode* statusNode,
        int compactSlotIndex)
    {
        if (statusNode == null)
            return;

        var compactX = (short)(PartyStatusSlotBaseX + (compactSlotIndex * PartyStatusSlotSpacingX));

        SetNodeGeometryForBseIfDifferent(
            statusNode,
            compactX,
            PartyStatusSlotY,
            PartyStatusSlotWidth,
            PartyStatusSlotHeight);
    }

    private unsafe void HidePartyStatusSlotVisibilityOnly(AtkResNode* statusNode)
    {
        if (statusNode == null)
            return;

        HideNodeVisibilityOnly(statusNode);

        if (statusNode->Type.ToString() != "1002")
            return;

        var componentNode = (AtkComponentNode*)statusNode;
        var component = componentNode->Component;

        if (component == null)
            return;

        for (var innerIndex = 0; innerIndex < component->UldManager.NodeListCount; innerIndex++)
        {
            var innerNode = component->UldManager.NodeList[innerIndex];

            if (innerNode == null)
                continue;

            HideNodeVisibilityOnly(innerNode);
        }
    }

    private unsafe void HideNodeVisibilityOnly(AtkResNode* node)
    {
        if (node == null)
            return;

        CapturePartyStatusNodeSnapshotIfNeeded(node);
        node->ToggleVisibility(false);
    }

    private unsafe void SetNodeGeometryForBseIfDifferent(
        AtkResNode* node,
        short x,
        short y,
        ushort width,
        ushort height)
    {
        if (node == null)
            return;

        if (node->X == x &&
            node->Y == y &&
            node->Width == width &&
            node->Height == height)
            return;

        node->SetPositionFloat(x, y);
        node->Width = width;
        node->Height = height;
    }

    private unsafe void CapturePartyStatusNodeSnapshotIfNeeded(AtkResNode* node)
    {
        if (node == null)
            return;

        var address = (nint)node;

        if (hiddenPartyStatusNodeSnapshots.ContainsKey(address))
            return;

        hiddenPartyStatusNodeSnapshots[address] = new PartyStatusNodeSnapshot
        {
            WasVisible = node->IsVisible(),
            X = node->X,
            Y = node->Y,
            Width = node->Width,
            Height = node->Height,
        };
    }

    private unsafe void RestorePartyStatusSlotIfBseHidIt(AtkResNode* statusNode)
    {
        if (statusNode == null)
            return;

        RestoreNodeVisibilityIfBseHidIt(statusNode);

        if (statusNode->Type.ToString() != "1002")
            return;

        var componentNode = (AtkComponentNode*)statusNode;
        var component = componentNode->Component;

        if (component == null)
            return;

        for (var innerIndex = 0; innerIndex < component->UldManager.NodeListCount; innerIndex++)
        {
            var innerNode = component->UldManager.NodeList[innerIndex];

            if (innerNode == null)
                continue;

            RestoreNodeVisibilityIfBseHidIt(innerNode);
        }
    }

    private unsafe void RestoreNodeVisibilityIfBseHidIt(AtkResNode* node)
    {
        if (node == null)
            return;

        var address = (nint)node;

        if (!hiddenPartyStatusNodeSnapshots.TryGetValue(address, out var snapshot))
            return;

        node->SetPositionFloat(snapshot.X, snapshot.Y);
        node->Width = snapshot.Width;
        node->Height = snapshot.Height;
        node->ToggleVisibility(snapshot.WasVisible);

        hiddenPartyStatusNodeSnapshots.Remove(address);
    }

    private unsafe void RestoreHiddenPartyStatusNodesFromLastAddon()
    {
        if (lastPartyListAddonAddress == nint.Zero)
        {
            hiddenPartyStatusNodeSnapshots.Clear();
            return;
        }

        var addon = (AtkUnitBase*)lastPartyListAddonAddress;

        if (addon == null)
        {
            hiddenPartyStatusNodeSnapshots.Clear();
            return;
        }

        NormalizePartyStatusSlotGeometry(addon, false);
        RestoreHiddenPartyStatusNodesInAddon(addon);
    }

    private unsafe void RestoreHiddenPartyStatusNodesInAddon(AtkUnitBase* addon)
    {
        if (addon == null)
        {
            hiddenPartyStatusNodeSnapshots.Clear();
            return;
        }

        if (hiddenPartyStatusNodeSnapshots.Count == 0)
            return;

        for (var rowNodeId = 10; rowNodeId <= 17; rowNodeId++)
        {
            var rowNode = FindPartyRowNode(addon, rowNodeId);

            if (rowNode == null)
                continue;

            if (rowNode->Type.ToString() != "1006")
                continue;

            var rowComponentNode = (AtkComponentNode*)rowNode;
            var rowComponent = rowComponentNode->Component;

            if (rowComponent == null)
                continue;

            for (var slotIndex = 0; slotIndex < MaxPartyStatusSlots; slotIndex++)
            {
                var childIndex = FirstPartyStatusChildIndex + slotIndex;

                if (childIndex < 0 || childIndex >= rowComponent->UldManager.NodeListCount)
                    continue;

                var statusNode = rowComponent->UldManager.NodeList[childIndex];

                if (!IsPartyStatusIconNode(statusNode))
                    continue;

                RestorePartyStatusSlotIfBseHidIt(statusNode);
            }
        }

        hiddenPartyStatusNodeSnapshots.Clear();
    }

    private unsafe void NormalizePartyStatusSlotGeometry(AtkUnitBase* addon, bool debugThisRun)
    {
        if (addon == null)
            return;

        var normalizedNodeCount = 0;

        for (var rowNodeId = 10; rowNodeId <= 17; rowNodeId++)
        {
            var rowNode = FindPartyRowNode(addon, rowNodeId);

            if (rowNode == null)
                continue;

            if (rowNode->Type.ToString() != "1006")
                continue;

            var rowComponentNode = (AtkComponentNode*)rowNode;
            var rowComponent = rowComponentNode->Component;

            if (rowComponent == null)
                continue;

            for (var slotIndex = 0; slotIndex < MaxPartyStatusSlots; slotIndex++)
            {
                var childIndex = FirstPartyStatusChildIndex + slotIndex;

                if (childIndex < 0 || childIndex >= rowComponent->UldManager.NodeListCount)
                    continue;

                var statusNode = rowComponent->UldManager.NodeList[childIndex];

                if (!IsPartyStatusIconNode(statusNode))
                    continue;

                var expectedX = (short)(PartyStatusSlotBaseX + (slotIndex * PartyStatusSlotSpacingX));

                SetNodeGeometryIfDifferent(
                    statusNode,
                    expectedX,
                    PartyStatusSlotY,
                    PartyStatusSlotWidth,
                    PartyStatusSlotHeight,
                    ref normalizedNodeCount);

                var iconComponentNode = (AtkComponentNode*)statusNode;
                var iconComponent = iconComponentNode->Component;

                if (iconComponent == null)
                    continue;

                for (var innerIndex = 0; innerIndex < iconComponent->UldManager.NodeListCount; innerIndex++)
                {
                    var innerNode = iconComponent->UldManager.NodeList[innerIndex];

                    if (innerNode == null)
                        continue;

                    NormalizeInnerStatusNodeGeometry(innerNode, innerIndex, ref normalizedNodeCount);
                }
            }
        }

        if (debugThisRun && normalizedNodeCount > 0)
            Log.Information($"[BSE] Normalized {normalizedNodeCount} party status node geometries.");
    }

    private static unsafe void NormalizeInnerStatusNodeGeometry(
        AtkResNode* innerNode,
        int innerIndex,
        ref int normalizedNodeCount)
    {
        if (innerNode == null)
            return;

        switch (innerIndex)
        {
            case 0:
                SetNodeGeometryIfDifferent(innerNode, -4, -4, 32, 12, ref normalizedNodeCount);
                break;

            case 1:
                SetNodeGeometryIfDifferent(innerNode, 0, 0, 24, 32, ref normalizedNodeCount);
                break;

            case 2:
                SetNodeGeometryIfDifferent(innerNode, 0, 23, 24, 18, ref normalizedNodeCount);
                break;
        }
    }

    private static unsafe void SetNodeGeometryIfDifferent(
        AtkResNode* node,
        short x,
        short y,
        ushort width,
        ushort height,
        ref int changedCount)
    {
        if (node == null)
            return;

        if (node->X == x &&
            node->Y == y &&
            node->Width == width &&
            node->Height == height)
            return;

        node->SetPositionFloat(x, y);
        node->Width = width;
        node->Height = height;

        changedCount++;
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
        Log.Information($"[BSE] BSE-hidden-node-count: {hiddenPartyStatusNodeSnapshots.Count}");
        Log.Information($"[BSE] last status signature: {lastPartyListStatusSignature}");
        Log.Information($"[BSE] PartyListPriority metadata available: {IsPartyListPriorityMetadataAvailableForDebug()}");

        var hiddenStatusIds = LoadHiddenStatusIds();
        var hiddenStatusIconIds = LoadHiddenStatusIconIds();

        Log.Information($"[BSE] Hidden status IDs loaded: {hiddenStatusIds.Count}");
        Log.Information($"[BSE] Hidden status icon IDs loaded: {hiddenStatusIconIds.Count}");
        Log.Information($"[BSE] Hidden status IDs: {FormatStatusIds(hiddenStatusIds.OrderBy(id => id))}");
        Log.Information($"[BSE] Hidden icon IDs: {string.Join(", ", hiddenStatusIconIds.OrderBy(id => id))}");
        Log.Information("[BSE] NOTE: actual party-list hiding now uses UI AtkComponentIcon.IconId, not guessed IPartyMember.Statuses slot indexes.");

        var partyCount = Math.Min(PartyList.Length, 8);

        for (var partyIndex = 0; partyIndex < partyCount; partyIndex++)
        {
            var partyMember = PartyList[partyIndex];

            if (partyMember == null)
            {
                Log.Information($"[BSE] Party index {partyIndex}: party member is null.");
                continue;
            }

            var partyMemberName = partyMember.Name.ToString();

            var rawStatusIds = partyMember.Statuses
                .Where(status => status.StatusId != 0)
                .Select(status => status.StatusId)
                .ToList();

            var combatSafePartyListStatusIds = BuildCombatSafePartyListSlotStatusOrder(rawStatusIds, hiddenStatusIds);
            var hiddenSlotIndexes = BuildHiddenSlotIndexes(combatSafePartyListStatusIds, hiddenStatusIds);
            var matchedRow = FindPartyRowNodeForMember(addon, partyMemberName, partyIndex, false);

            Log.Information($"[BSE] Party index {partyIndex} name={partyMember.Name}");
            Log.Information($"[BSE] Party index {partyIndex} entityId={partyMember.EntityId}");
            Log.Information($"[BSE] Party index {partyIndex} raw IPartyMember.Statuses: {FormatSlotMap(rawStatusIds)}");
            Log.Information($"[BSE] Party index {partyIndex} DEBUG raw combat-safe guess only: {FormatRenderedSlotMap(combatSafePartyListStatusIds)}");
            Log.Information($"[BSE] Party index {partyIndex} DEBUG raw guessed hidden slots only: {FormatSlotIndexes(hiddenSlotIndexes)}");
            Log.Information($"[BSE] Party index {partyIndex} fallback row node id: {10 + partyIndex}");
            Log.Information($"[BSE] Party index {partyIndex} matched row node id: {(matchedRow == null ? "null" : matchedRow->NodeId.ToString())}");
        }

        Log.Information("[BSE] ----- Addon nodes -----");

        for (var nodeIndex = 0; nodeIndex < addon->UldManager.NodeListCount; nodeIndex++)
        {
            var node = addon->UldManager.NodeList[nodeIndex];

            if (node == null)
                continue;

            Log.Information(
                $"[BSE] AddonNode index={nodeIndex} nodeId={node->NodeId} type={node->Type} visible={node->IsVisible()} hiddenByBse={hiddenPartyStatusNodeSnapshots.ContainsKey((nint)node)} x={node->X} y={node->Y} w={node->Width} h={node->Height}"
            );

            if (node->NodeId >= 10 && node->NodeId <= 17)
                DumpPartyRowStatusSlots(node, $"rowNodeId={node->NodeId}");
        }

        Log.Information("[BSE] ===== PARTY DEBUG END =====");

        ChatGui.Print("[BSE] Party debug dumped to /xllog.");
    }

    private bool IsPartyListPriorityMetadataAvailableForDebug()
    {
        ResolvePartyListPriorityReflection();
        return partyListPriorityMember != null;
    }

    private unsafe void DumpPartyRowStatusSlots(AtkResNode* partyRowNode, string label)
    {
        if (partyRowNode == null)
            return;

        if (partyRowNode->Type.ToString() != "1006")
        {
            Log.Information($"[BSE] {label}: not a component row. type={partyRowNode->Type}");
            return;
        }

        var hiddenIconIds = LoadHiddenStatusIconIds();

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

            if (!IsPartyStatusIconNode(node))
                continue;

            statusIconCount++;

            var componentTypeText = "unknown";

            try
            {
                var iconComponentNode = (AtkComponentNode*)node;

                if (iconComponentNode->Component != null)
                    componentTypeText = ((int)iconComponentNode->Component->GetComponentType()).ToString();
            }
            catch
            {
                componentTypeText = "read-failed";
            }

            var actualIconText = "read-failed";
            var hiddenByActualIcon = false;

            if (TryReadPartyStatusIconId(node, out var actualIconId))
            {
                actualIconText = actualIconId.ToString();
                hiddenByActualIcon = hiddenIconIds.Contains(actualIconId);
            }

            Log.Information(
                $"[BSE] {label}: OUTER childIndex={childIndex} slotIndex={childIndex - FirstPartyStatusChildIndex} nodeId={node->NodeId} type={node->Type} visible={node->IsVisible()} hiddenByBse={hiddenPartyStatusNodeSnapshots.ContainsKey((nint)node)} componentType={componentTypeText} actualIconId={actualIconText} hiddenByActualIcon={hiddenByActualIcon} x={node->X} y={node->Y} w={node->Width} h={node->Height}"
            );

            var outerComponentNode = (AtkComponentNode*)node;
            var outerComponent = outerComponentNode->Component;

            if (outerComponent == null)
            {
                Log.Information($"[BSE] {label}: OUTER nodeId={node->NodeId}: inner component null.");
                continue;
            }

            Log.Information($"[BSE] {label}: OUTER nodeId={node->NodeId}: inner child count = {outerComponent->UldManager.NodeListCount}");

            for (var innerIndex = 0; innerIndex < outerComponent->UldManager.NodeListCount; innerIndex++)
            {
                var innerNode = outerComponent->UldManager.NodeList[innerIndex];

                if (innerNode == null)
                    continue;

                Log.Information(
                    $"[BSE] {label}: INNER outerNodeId={node->NodeId} innerIndex={innerIndex} innerNodeId={innerNode->NodeId} type={innerNode->Type} visible={innerNode->IsVisible()} hiddenByBse={hiddenPartyStatusNodeSnapshots.ContainsKey((nint)innerNode)} x={innerNode->X} y={innerNode->Y} w={innerNode->Width} h={innerNode->Height}"
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

    private uint GetStatusIconId(uint statusId)
    {
        var statusSheet = DataManager.GetExcelSheet<Status>();

        if (statusSheet.TryGetRow(statusId, out var statusRow))
            return statusRow.Icon;

        return 0;
    }

    private string FormatStatusId(uint statusId)
    {
        return $"{statusId} {GetStatusName(statusId)}";
    }

    private string FormatStatusIds(IEnumerable<uint> statusIds)
    {
        return string.Join(", ", statusIds.Select(id =>
        {
            var priorityText = TryGetPartyListPriority(id, out var priority)
                ? priority.ToString()
                : "unknown";

            return $"{FormatStatusId(id)} icon={GetStatusIconId(id)} priority={priorityText}";
        }));
    }

    private string FormatSlotMap(IReadOnlyList<uint> statusIds)
    {
        if (statusIds.Count == 0)
            return "(none)";

        var parts = new List<string>();

        for (var i = 0; i < statusIds.Count; i++)
            parts.Add($"{i}={FormatStatusId(statusIds[i])}");

        return string.Join(", ", parts);
    }

    private string FormatRenderedSlotMap(IReadOnlyList<uint> statusIds)
    {
        if (statusIds.Count == 0)
            return "(none)";

        var parts = new List<string>();

        for (var i = 0; i < statusIds.Count; i++)
        {
            var statusId = statusIds[i];

            if (TryGetPartyListPriority(statusId, out var priority))
                parts.Add($"{i}={FormatStatusId(statusId)} priority={priority}");
            else
                parts.Add($"{i}={FormatStatusId(statusId)} priority=(unknown)");
        }

        return string.Join(", ", parts);
    }

    private static string FormatSlotIndexes(HashSet<int> slotIndexes)
    {
        return slotIndexes.Count == 0
            ? "(none)"
            : string.Join(", ", slotIndexes.OrderBy(index => index));
    }

    private sealed class PartyMemberRenderState
    {
        public int PartyIndex { get; set; }
        public string PartyMemberName { get; set; } = string.Empty;
        public string PartyMemberKey { get; set; } = string.Empty;
        public List<uint> RawStatusIds { get; set; } = new();
        public List<uint> PartyListSlotStatusIds { get; set; } = new();
        public HashSet<int> HiddenSlotIndexes { get; set; } = new();
    }

    private sealed class PartyStatusNodeSnapshot
    {
        public bool WasVisible { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public ushort Width { get; set; }
        public ushort Height { get; set; }
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