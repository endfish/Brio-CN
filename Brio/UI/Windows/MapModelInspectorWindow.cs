using Brio.Game.GPose;
using Brio.Game.Input;
using Brio.Game.WorldObjects;
using Brio.Resources;
using Brio.Resources.Extra;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Brio.UI.Windows;

public sealed class MapModelInspectorWindow : Window
{
    private readonly MapModelInspectorService _inspector;
    private readonly CatalogWindow _catalogWindow;
    private readonly GPoseService _gPoseService;
    private readonly GameInputService _gameInputService;
    private readonly IClientState _clientState;
    private readonly IGameGui _gameGui;

    private bool _isScanning;
    private bool _pickMode;
    private bool _strictPickAssetType = true;
    private string _search = string.Empty;
    private string _assetType = string.Empty;
    private string _selectedPath = string.Empty;
    private int _selectedInstanceIndex;
    private bool _scrollToSelection;
    private IReadOnlyList<MapModelInstance> _pickCandidates = [];
    private int _pickCandidateIndex = -1;
    private int _scanRequest;
    private int _pickRequest;
    private long _lastHandledLeftClickSequence;
    private Vector2 _lastPickScreenPosition = new(float.MinValue);
    private string _lastPickAssetType = string.Empty;
    private bool _lastPickStrictAssetType;
    private GameMouseInputSnapshot _mouseInputSnapshot;

    public MapModelInspectorWindow(
        MapModelInspectorService inspector,
        CatalogWindow catalogWindow,
        GPoseService gPoseService,
        GameInputService gameInputService,
        IClientState clientState,
        IGameGui gameGui)
        : base($"{Brio.Name} - {L("title", "Map Model Inspector")}###brio_map_model_inspector")
    {
        Namespace = "brio_map_model_inspector_namespace";

        _inspector = inspector;
        _catalogWindow = catalogWindow;
        _gPoseService = gPoseService;
        _gameInputService = gameInputService;
        _clientState = clientState;
        _gameGui = gameGui;

        AllowBackgroundBlur = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 480),
            MaximumSize = new Vector2(1000, 1000),
        };
    }

    public void OpenAndScan()
    {
        _mouseInputSnapshot = _gameInputService.GetMouseInputSnapshot();
        _lastHandledLeftClickSequence = _mouseInputSnapshot.LeftClickSequence;
        IsOpen = true;
        BringToFront();
        RefreshSnapshot();
    }

    public override void Draw()
    {
        ImBrio.BlurWindow();
        _mouseInputSnapshot = _gameInputService.GetMouseInputSnapshot();

        DrawToolbar();

        var snapshot = _inspector.Snapshot;
        if(snapshot.Entries.Count == 0)
        {
            if(!_isScanning)
                ImGui.TextWrapped(L("emptySnapshot", "No map model snapshot is loaded. Click Scan to inspect the currently loaded map."));
            return;
        }

        if(snapshot.TerritoryType != _clientState.TerritoryType)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.7f, 0.2f, 1f),
                L("staleSnapshot", "The map has changed. This snapshot is stale; click Refresh to scan the new map."));
        }

        DrawFilters(snapshot);
        DrawModelList(snapshot);
        DrawSelectedModel(snapshot);
        HandleWorldPick();
        DrawSelectionMarker(snapshot);
    }

    public override void OnClose()
    {
        _scanRequest++;
        _pickRequest++;
        _isScanning = false;
        _inspector.Clear();
        _pickMode = false;
        _pickCandidates = [];
        _pickCandidateIndex = -1;
        _selectedPath = string.Empty;
        _selectedInstanceIndex = 0;
        base.OnClose();
    }

    private void DrawToolbar()
    {
        using(ImRaii.Disabled(_isScanning))
        {
            var buttonLabel = _inspector.Snapshot.Entries.Count == 0
                ? L("scan", "Scan")
                : L("refresh", "Refresh");
            if(ImGui.Button(buttonLabel))
                RefreshSnapshot();
        }

        ImGui.SameLine();

        var pickMode = _pickMode;
        if(ImGui.Checkbox(L("pick", "Pick from the game world"), ref pickMode))
        {
            _pickMode = pickMode;
            _pickCandidates = [];
            _pickCandidateIndex = -1;
        }
        ImBrio.AttachToolTip(L(
            "pickHint",
            "When enabled, left-click a world object to identify it. Ctrl+left-click repeatedly to cycle through deeper candidates."));

        ImGui.SameLine();

        using(ImRaii.Disabled(_inspector.Snapshot.Entries.Count == 0))
        {
            if(ImGui.Button(L("clear", "Clear")))
            {
                _inspector.Clear();
                _selectedPath = string.Empty;
                _pickCandidates = [];
                _pickCandidateIndex = -1;
            }
        }

        if(_isScanning)
            ImGui.TextDisabled(L("scanning", "Scanning the currently loaded map..."));
        else
            ImGui.TextDisabled(L("onDemandNote", "Scanning occurs only when this window is opened or Refresh is clicked. Closing the window releases the snapshot."));

        if(_pickMode)
        {
            ImGui.TextColored(
                new Vector4(0.65f, 0.5f, 1f, 1f),
                L(
                    "pickActive",
                    "Pick mode is active: left-click a world object; use Ctrl+left-click repeatedly to cycle through deeper candidates."));

            DrawPickDiagnostics();
        }
    }

    private void DrawFilters(MapModelScanResult snapshot)
    {
        ImGui.SetNextItemWidth(-190 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint(
            "###map_model_search",
            L("searchHint", "Search by name, model code, path, or category..."),
            ref _search,
            256);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);

        var preview = string.IsNullOrEmpty(_assetType) ? L("allAssetTypes", "All Asset Types") : _assetType;
        using(var combo = ImRaii.Combo("###map_model_asset_type", preview))
        {
            if(combo.Success)
            {
                if(ImGui.Selectable(L("allAssetTypes", "All Asset Types"), string.IsNullOrEmpty(_assetType)))
                {
                    _assetType = string.Empty;
                    ResetPickCandidates();
                }

                foreach(var assetType in snapshot.Entries
                    .Select(entry => entry.PathInfo.AssetType)
                    .Distinct()
                    .Order())
                {
                    if(ImGui.Selectable(assetType, assetType == _assetType))
                    {
                        _assetType = assetType;
                        ResetPickCandidates();
                    }
                }
            }
        }

        if(!string.IsNullOrEmpty(_assetType))
        {
            var strictPickAssetType = _strictPickAssetType;
            if(ImGui.Checkbox(
                L("strictPickAssetType", "Only pick the current resource type"),
                ref strictPickAssetType))
            {
                _strictPickAssetType = strictPickAssetType;
                ResetPickCandidates();
            }
            ImBrio.AttachToolTip(L(
                "strictPickAssetTypeHint",
                "When enabled, world picking excludes every model outside the selected resource type. When disabled, matching models are only preferred."));
        }

        var filteredCount = FilterEntries(snapshot).Count();
        ImGui.TextDisabled(LF(
            "counts",
            "{0:N0} models / {1:N0} instances on territory {2}",
            filteredCount,
            snapshot.Instances.Count,
            snapshot.TerritoryType));
    }

    private void DrawModelList(MapModelScanResult snapshot)
    {
        using var child = ImRaii.Child("###map_model_list", new Vector2(0, 220 * ImGuiHelpers.GlobalScale), true);
        if(!child.Success)
            return;

        var entries = FilterEntries(snapshot).ToArray();
        if(entries.Length == 0)
        {
            ImGui.TextWrapped(L("noMatches", "No models match the current filter."));
            return;
        }

        string? lastAssetType = null;
        foreach(var entry in entries)
        {
            if(entry.PathInfo.AssetType != lastAssetType)
            {
                ImBrio.SeparatorText(entry.PathInfo.AssetType);
                lastAssetType = entry.PathInfo.AssetType;
            }

            var isSelected = entry.Path == _selectedPath;
            var label = $"{entry.DisplayName} [{entry.ModelCode}]  ×{entry.Instances.Count}###{PathData.Hash(entry.Path)}";
            if(ImGui.Selectable(label, isSelected))
                SelectEntry(entry);

            if(isSelected && _scrollToSelection)
            {
                ImGui.SetScrollHereY(0.5f);
                _scrollToSelection = false;
            }

            if(ImGui.IsItemHovered())
                ImBrio.AttachToolTip($"{entry.PathInfo.Subtype} / {entry.PathInfo.AssetType}{ImBrio.TooltipSeparator}{entry.Path}");
        }
    }

    private void DrawSelectedModel(MapModelScanResult snapshot)
    {
        var selected = snapshot.Entries.FirstOrDefault(entry => entry.Path == _selectedPath);
        if(selected is null)
        {
            ImGui.TextWrapped(L("selectHint", "Select a model from the list, or enable world picking and click a model in the game view."));
            return;
        }

        _selectedInstanceIndex = Math.Clamp(_selectedInstanceIndex, 0, selected.Instances.Count - 1);
        var instance = selected.Instances[_selectedInstanceIndex];

        ImBrio.SeparatorText(L("selectedModel", "Selected Model"));
        ImGui.TextWrapped($"{selected.DisplayName} [{selected.ModelCode}]");
        ImGui.TextDisabled($"{selected.PathInfo.Expansion} / {selected.PathInfo.Subtype} / {selected.PathInfo.AssetType}");
        ImGui.TextWrapped(selected.Path);

        if(_pickCandidates.Count > 0)
        {
            ImGui.TextDisabled(LF(
                "candidateCount",
                "Pick candidate {0} of {1}",
                _pickCandidateIndex + 1,
                _pickCandidates.Count));

            var candidateButtonWidth = GetPairedButtonWidth();
            using(ImRaii.Disabled(_pickCandidateIndex <= 0))
                if(ImGui.Button(
                    L("previousCandidate", "Previous Candidate"),
                    new Vector2(candidateButtonWidth, 0)))
                    SelectPickCandidate(_pickCandidateIndex - 1, snapshot);

            ImGui.SameLine();
            using(ImRaii.Disabled(_pickCandidateIndex >= _pickCandidates.Count - 1))
                if(ImGui.Button(
                    L("nextCandidate", "Next Candidate"),
                    new Vector2(candidateButtonWidth, 0)))
                    SelectPickCandidate(_pickCandidateIndex + 1, snapshot);
        }

        ImGui.TextDisabled(LF(
            "instanceDetails",
            "Instance {0} of {1} · Key {2} · SubId {3} · Position {4:F2}, {5:F2}, {6:F2}",
            _selectedInstanceIndex + 1,
            selected.Instances.Count,
            instance.InstanceKey,
            instance.SubId,
            instance.Position.X,
            instance.Position.Y,
            instance.Position.Z));

        var instanceButtonWidth = GetPairedButtonWidth();
        using(ImRaii.Disabled(_selectedInstanceIndex <= 0))
            if(ImGui.Button(
                L("previousInstance", "Previous Instance"),
                new Vector2(instanceButtonWidth, 0)))
                _selectedInstanceIndex--;

        ImGui.SameLine();
        using(ImRaii.Disabled(_selectedInstanceIndex >= selected.Instances.Count - 1))
            if(ImGui.Button(
                L("nextInstance", "Next Instance"),
                new Vector2(instanceButtonWidth, 0)))
                _selectedInstanceIndex++;

        var copyButtonWidth = GetPairedButtonWidth();
        if(ImGui.Button(
            L("copyModelCode", "Copy Model Code"),
            new Vector2(copyButtonWidth, 0)))
            ImGui.SetClipboardText(selected.ModelCode);

        ImGui.SameLine();
        if(ImGui.Button(
            L("copyPath", "Copy Path"),
            new Vector2(copyButtonWidth, 0)))
            ImGui.SetClipboardText(selected.Path);

        if(!_gPoseService.IsGPosing)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.7f, 0.2f, 1f),
                L("catalogRequiresGPose", "The Object Catalog requires Brio's GPose mode. Scanning, picking, and copying remain available outside GPose."));
        }

        using(ImRaii.Disabled(!_gPoseService.IsGPosing))
        {
            var catalogButtonWidth = GetPairedButtonWidth();
            if(ImGui.Button(
                L("locateInCatalog", "Locate in Object Catalog"),
                new Vector2(catalogButtonWidth, 0)))
                _catalogWindow.OpenMapModels(snapshot.Entries.Select(entry => entry.Path), selected.Path);

            ImGui.SameLine();
            if(ImGui.Button(
                L("browseMapInCatalog", "Browse This Map in Object Catalog"),
                new Vector2(catalogButtonWidth, 0)))
                _catalogWindow.OpenMapModels(snapshot.Entries.Select(entry => entry.Path));
        }
    }

    private async void RefreshSnapshot()
    {
        if(_isScanning)
            return;

        _isScanning = true;
        var request = ++_scanRequest;
        _pickRequest++;
        _pickCandidates = [];
        _pickCandidateIndex = -1;
        _selectedPath = string.Empty;
        _selectedInstanceIndex = 0;

        try
        {
            var snapshot = await _inspector.ScanAsync();
            if(request == _scanRequest && IsOpen)
                _inspector.UseSnapshot(snapshot);
        }
        catch(Exception ex)
        {
            if(request != _scanRequest)
                return;

            Brio.Log.Error(ex, "Failed to scan map models");
            Brio.NotifyError(L("scanFailed", "Failed to scan map models. See the Dalamud log for details."));
            _inspector.Clear();
        }
        finally
        {
            if(request == _scanRequest)
                _isScanning = false;
        }
    }

    private async void HandleWorldPick()
    {
        var leftClickSequence = _mouseInputSnapshot.LeftClickSequence;
        var hasNewGameClick = leftClickSequence != _lastHandledLeftClickSequence;
        if(hasNewGameClick)
            _lastHandledLeftClickSequence = leftClickSequence;

        if(!_pickMode || !hasNewGameClick)
            return;

        if(ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
            return;

        var screenPosition = new Vector2(_mouseInputSnapshot.PositionX, _mouseInputSnapshot.PositionY);
        if(screenPosition.X < 0 || screenPosition.Y < 0)
            screenPosition = ImGui.GetMousePos() - ImGui.GetMainViewport().Pos;

        var strictAssetType = !string.IsNullOrEmpty(_assetType) && _strictPickAssetType;
        var canCycleExistingCandidates =
            _mouseInputSnapshot.CtrlPressed
            && _pickCandidates.Count > 0
            && Vector2.Distance(screenPosition, _lastPickScreenPosition) <= 18f * ImGuiHelpers.GlobalScale
            && _lastPickAssetType.Equals(_assetType, StringComparison.Ordinal)
            && _lastPickStrictAssetType == strictAssetType;
        if(canCycleExistingCandidates)
        {
            SelectPickCandidate(
                (_pickCandidateIndex + 1) % _pickCandidates.Count,
                _inspector.Snapshot);
            return;
        }

        Brio.Log.Info(
            $"Map model pick input detected from game hook: position={screenPosition}, " +
            $"ctrl={_mouseInputSnapshot.CtrlPressed}, " +
            $"click={_mouseInputSnapshot.LeftClickSequence}");

        var request = ++_pickRequest;
        try
        {
            var candidates = await _inspector.PickAsync(
                screenPosition,
                _assetType,
                strictAssetType);
            if(request != _pickRequest || !IsOpen)
                return;

            Brio.Log.Info($"Map model pick diagnostics: {_inspector.LastPickDiagnostics}");
            _pickCandidates = candidates;
            _pickCandidateIndex = _pickCandidates.Count > 0 ? 0 : -1;
            _lastPickScreenPosition = screenPosition;
            _lastPickAssetType = _assetType;
            _lastPickStrictAssetType = strictAssetType;

            if(_pickCandidateIndex >= 0)
                SelectPickCandidate(_pickCandidateIndex, _inspector.Snapshot);
            else
                Brio.NotifyInfo(L("noPickResult", "No scanned map model was found under the cursor."));
        }
        catch(Exception ex)
        {
            Brio.Log.Error(ex, "Failed to pick a map model");
            Brio.NotifyError(L("pickFailed", "Failed to identify the model under the cursor."));
        }
    }

    private void DrawPickDiagnostics()
    {
        ImBrio.SeparatorText(L("diagnostics", "Pick Diagnostics"));
        var diagnostics = _inspector.LastPickDiagnostics;
        ImGui.TextWrapped(LF(
            "candidateDiagnostics",
            "Sphere candidates {0}, screen candidates {1}, total {2}. {3}",
            diagnostics.SphereCandidates,
            diagnostics.ScreenCandidates,
            diagnostics.TotalCandidates,
            LocalizePickStatus(diagnostics.Status)));
    }

    private void SelectPickCandidate(int index, MapModelScanResult snapshot)
    {
        if(index < 0 || index >= _pickCandidates.Count)
            return;

        _pickCandidateIndex = index;
        var candidate = _pickCandidates[index];
        var entry = snapshot.Entries.FirstOrDefault(item => item.Path == candidate.Path);
        if(entry is null)
            return;

        _selectedPath = entry.Path;
        _selectedInstanceIndex = entry.Instances
            .Select((instance, instanceIndex) => (instance, instanceIndex))
            .FirstOrDefault(pair =>
                pair.instance.InstanceKey == candidate.InstanceKey
                && pair.instance.SubId == candidate.SubId)
            .instanceIndex;
        _scrollToSelection = true;
    }

    private void SelectEntry(MapModelEntry entry)
    {
        _selectedPath = entry.Path;
        _selectedInstanceIndex = 0;
        ResetPickCandidates();
    }

    private void ResetPickCandidates()
    {
        _pickCandidates = [];
        _pickCandidateIndex = -1;
        _lastPickScreenPosition = new Vector2(float.MinValue);
        _lastPickAssetType = string.Empty;
        _lastPickStrictAssetType = false;
    }

    private IEnumerable<MapModelEntry> FilterEntries(MapModelScanResult snapshot)
    {
        var entries = snapshot.Entries.AsEnumerable();

        if(!string.IsNullOrEmpty(_assetType))
            entries = entries.Where(entry => entry.PathInfo.AssetType == _assetType);

        if(!string.IsNullOrWhiteSpace(_search))
        {
            entries = entries.Where(entry =>
                entry.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || entry.ModelCode.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || entry.Path.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || entry.PathInfo.AssetType.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || entry.PathInfo.Subtype.Contains(_search, StringComparison.OrdinalIgnoreCase));
        }

        return entries;
    }

    private void DrawSelectionMarker(MapModelScanResult snapshot)
    {
        var entry = snapshot.Entries.FirstOrDefault(item => item.Path == _selectedPath);
        if(entry is null || entry.Instances.Count == 0)
            return;

        var instance = entry.Instances[Math.Clamp(_selectedInstanceIndex, 0, entry.Instances.Count - 1)];
        if(!_gameGui.WorldToScreen(instance.BoundsCenter, out var screenPosition, out var inView))
            return;

        var viewport = ImGui.GetMainViewport();
        var margin = 24f * ImGuiHelpers.GlobalScale;
        var min = viewport.Pos + new Vector2(margin);
        var max = viewport.Pos + viewport.Size - new Vector2(margin);
        var markerPosition = inView
            ? screenPosition
            : Vector2.Clamp(screenPosition, min, max);
        var screenCenter = viewport.Pos + (viewport.Size * 0.5f);

        var drawList = ImGui.GetForegroundDrawList();
        var fill = ImGui.GetColorU32(new Vector4(0.45f, 0.25f, 1f, 0.9f));
        var outline = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f));
        var line = ImGui.GetColorU32(new Vector4(0.55f, 0.35f, 1f, 0.75f));
        var radius = 7f * ImGuiHelpers.GlobalScale;
        drawList.AddLine(screenCenter, markerPosition, line, 2f * ImGuiHelpers.GlobalScale);
        drawList.AddCircleFilled(markerPosition, radius, fill);
        drawList.AddCircle(markerPosition, radius + 2f, outline, 0, 2f * ImGuiHelpers.GlobalScale);
        drawList.AddText(
            markerPosition + new Vector2(11f, -ImGui.GetTextLineHeight() * 0.5f),
            outline,
            entry.ModelCode);
    }

    private static string L(string key, string fallback)
        => Localize.Get($"ui.mapModelInspector.{key}", fallback);

    private static string LF(string key, string fallback, params object?[] args)
        => string.Format(L(key, fallback), args);

    private static float GetPairedButtonWidth()
        => MathF.Max(
            1,
            (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f);

    private static string LocalizePickStatus(string status)
        => status switch
        {
            "No pick has been attempted." => L("statusNoPick", status),
            "The snapshot is empty or belongs to another territory." => L("statusSnapshotUnavailable", status),
            "Control.Instance() is null." => L("statusControlUnavailable", status),
            "The active game camera is null." => L("statusCameraUnavailable", status),
            "The screen ray direction is empty." => L("statusRayUnavailable", status),
            "The game mouseover target was matched." => L("statusMouseoverMatched", status),
            "Candidates found." => L("statusCandidatesFound", status),
            "The ray was valid, but no scanned model matched it." => L("statusNoCandidates", status),
            _ => status,
        };
}
