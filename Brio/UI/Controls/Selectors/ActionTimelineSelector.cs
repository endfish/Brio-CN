using Brio.Capabilities.Actor;
using Brio.Config;
using Brio.Game.Actor.Extensions;
using Brio.Game.Actor.Interop;
using Brio.Game.GPose;
using Brio.IPC;
using Brio.Resources;
using Brio.Resources.Sheets;
using Brio.Services;
using Brio.UI;
using Brio.UI.Controls.Core;
using Brio.UI.Controls.Stateless;
using Brio.UI.Theming;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static Brio.Game.Actor.ActionTimelineService;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace Brio.UI.Controls.Selectors;

public class ActionTimelineSelector(string id) : Selector<ActionTimelineSelectorEntry>(id)
{
    private static readonly ActionLibraryCategory[] QuickLibraryCategories =
    [
        ActionLibraryCategory.Emotes,
        ActionLibraryCategory.Emotes,
        ActionLibraryCategory.NpcActions,
        ActionLibraryCategory.Mods,
        ActionLibraryCategory.Poses,
        ActionLibraryCategory.PlayerSkills,
    ];

    private static readonly string[] QuickLibraryCategoryLabels =
    [
        "Emotes",
        "Expression",
        "NPC",
        "Action Mods",
        "Poses",
        "Skills",
    ];

    protected override Vector2 MinimumListSize { get; } = new(300, 300);

    protected override float EntrySize => ImGui.GetTextLineHeight() * 3.2f;
    protected virtual Vector2 IconSize => new(ImGui.GetTextLineHeight() * 3f);

    // This selector is normally drawn inside an auto-sized popup. AdaptiveSizing feeds the
    // popup's previous width back into its next requested width and can make it grow every frame.
    // Pinned selector windows still opt into the available space through _useAvailableSpace.
    protected override SelectorFlags Flags { get; } = SelectorFlags.AllowSearch | SelectorFlags.ShowOptions;

    private ActionLibraryCategory _libraryCategory = ActionLibraryCategory.Emotes;
    private NpcActionSubtype _npcActionSubtype = NpcActionSubtype.All;
    private ActionCompatibilityFilter _compatibilityFilter = ActionCompatibilityFilter.All;
    private bool _onlyRaceRestricted = false;
    private bool _onlyIndexed = false;
    private ushort _humanRaceFilterModelId = 0;

    private bool _showBlendable = true;

    private bool _filterByDrawsWeapon = false;
    private bool _drawsWeaponValue = false;

    private bool _filterByEmoteCategory = false;
    private int _emoteCategoryValue = 0;

    private bool _showNonBlendInBlendMode = false;

    private bool _isPinned = false;
    private bool _isWindowOpen = false;

    private IGameObject? _modActionActor;
    private ActionTimelineCapability? _playbackCapability;
    private ActionTimelineContext? _actorContext;
    private IReadOnlyList<PenumbraModAction> _modActions = [];
    private int _modActionVersion = -1;
    private int _annotationVersion = -1;
    private ActionTimelineAnnotationService? _annotationService;

    private uint _annotationEditingTimelineId;
    private string _annotationEditingNotes = string.Empty;
    private string _annotationEditingRaceTags = string.Empty;
    private string _annotationEditingProps = string.Empty;

    private uint _manualEmulationTimelineId;
    private ActionTimelineContext? _manualEmulationContext;

    public bool IsPinned => _isPinned;

    public IGameObject? ModActionActor
    {
        get => _modActionActor;
        set
        {
            var changed = _modActionActor?.ObjectIndex != value?.ObjectIndex;
            _modActionActor = value;
            if(changed)
            {
                _modActionVersion = -1;
                RefreshActorContext();
            }
        }
    }

    public ActionTimelineCapability? PlaybackCapability
    {
        get => _playbackCapability;
        set
        {
            _playbackCapability = value;
            RefreshActorContext();
        }
    }

    //TODO(KEN) at some point make all of them use `field`

    public bool AllowBlending
    {
        get => _showBlendable;
        set
        {
            _showBlendable = value;
            UpdateList();
        }
    }

    public bool ExpressionsOnly
    {
        get => field;
        set
        {
            field = value;
            UpdateList();
        }
    }

    public void TogglePin()
    {
        _isPinned = !_isPinned;
        if(_isPinned)
        {
            _isWindowOpen = true;
        }
    }

    public void DrawAsWindow()
    {
        RefreshActorContext();
        RefreshModActions();
        RefreshAnnotations();

        if(!_isPinned)
            return;

        ImGui.SetNextWindowSize(new Vector2(400, 500), ImGuiCond.FirstUseEver);

        if(ImGui.Begin($"{Localize.Text("Animation Search Selector")} ###{_id}_window2", ref _isWindowOpen, ImGuiWindowFlags.NoCollapse))
        {
            if(!_isWindowOpen)
            {
                _isPinned = false;
                ImGui.End();
                return;
            }

            ImBrio.BlurWindow(ImGuiWindowFlags.None);

            DrawPinButton();

            // Use available window space instead of adaptive sizing
            _useAvailableSpace = true;
            base.Draw();
            _useAvailableSpace = false;
        }
        ImGui.End();
    }

    private void DrawPinButton()
    {
        var pinIcon = _isPinned ? FontAwesomeIcon.Thumbtack : FontAwesomeIcon.Thumbtack;
        var pinColor = _isPinned ? UIConstants.GizmoRed : ThemeManager.CurrentTheme.Text.Text;

        var tooltip = Localize.Text(_isPinned ? "Unpin (close window)" : "Pin to keep open");

        if(ImBrio.FontIconButton($"pin_toggle_{_id}", pinIcon, tooltip, true, true, pinColor))
        {
            TogglePin();
        }

        ImGui.SameLine();
    }

    public new void Draw()
    {
        RefreshActorContext();
        RefreshModActions();
        RefreshAnnotations();

        if(_isPinned)
        {
            ImGui.TextDisabled(global::Brio.Resources.Localize.Text("(Selector is pinned as separate window)"));
            return;
        }

        DrawPinButton();

        base.Draw();
    }

    private void RefreshActorContext()
    {
        var actorContext = ReadActorContext();
        if(actorContext == _actorContext)
            return;

        _actorContext = actorContext;
        UpdateList();
    }

    private unsafe ActionTimelineContext? ReadActorContext()
    {
        if(_playbackCapability?.GetAnimationContext() is ActionTimelineContext capabilityContext)
            return capabilityContext;

        if(_modActionActor is not ICharacter character)
            return null;

        var characterBase = character.GetCharacterBase();
        if(characterBase is null)
            return null;

        var animationVariant = characterBase->CharacterBase.AnimationVariant;
        var modelType = characterBase->CharacterBase.GetModelType();

        if(modelType == CharacterBase.ModelType.Human)
        {
            var human = (BrioHuman*)characterBase;
            return new(
                ActionTimelineModelKind.Human,
                human->Human.RaceSexId,
                animationVariant);
        }

        var modelCharaId = character.Native()->ModelContainer.ModelCharaId;
        if(modelCharaId == 0
            || !GameDataProvider.Instance.GetExcelSheet<ModelChara>().TryGetRow((uint)modelCharaId, out var modelChara)
            || modelChara.Model == 0)
            return null;

        return modelType switch
        {
            CharacterBase.ModelType.Monster => new(
                ActionTimelineModelKind.Monster,
                modelChara.Model,
                animationVariant),
            CharacterBase.ModelType.DemiHuman => new(
                ActionTimelineModelKind.Demihuman,
                modelChara.Model,
                animationVariant),
            _ => null,
        };
    }

    private void RefreshModActions()
    {
        if(_modActionActor is null || !Brio.TryGetService<PenumbraModActionService>(out var service))
            return;

        var actions = service.GetActiveActions(_modActionActor);
        if(_modActionVersion == service.Version)
            return;

        _modActions = actions;
        _modActionVersion = service.Version;
        ReloadList();
    }

    private void RefreshAnnotations()
    {
        var service = AnnotationService;
        if(service is null
            || _annotationVersion == service.Version)
            return;

        _annotationVersion = service.Version;
        UpdateList();
    }

    protected override void PopulateList()
    {
        foreach(var timeline in GameDataProvider.Instance.ActionTimelines)
        {
            if(!string.IsNullOrEmpty(timeline.Key.ToString()))
                AddItem(new ActionTimelineSelectorEntry(
                    timeline.Key.ToString(),
                    (ushort)timeline.RowId,
                    timeline.RowId,
                    timeline.Key.ToString(),
                    ActionTimelineSelectorEntry.OriginalType.Raw,
                    ActionTimelineSelectorEntry.AnimationPurpose.Unknown,
                    (ActionTimelineSlots)timeline.Slot,
                    0,
                    false,
                    0,
                    ActionTimelineCategory.None,
                    GameDataProvider.Instance.ActionTimelineMetadata.Get(timeline.RowId)));
        }

        foreach(var emote in GameDataProvider.Instance.GetExcelSheet<Emote>())
        {
            BrioActionTimeline timeline;
            bool drawsWeapon = emote.DrawsWeapon;
            byte emoteCategory = (byte)emote.EmoteCategory.RowId;

            // Loop
            if(emote.ActionTimeline[0].RowId != 0 && GameDataProvider.Instance.ActionTimelines.TryGetRow(emote.ActionTimeline[0].RowId, out timeline))
            {
                AddItem(new ActionTimelineSelectorEntry(
                    emote.Name.ToString(),
                    (ushort)timeline.RowId,
                    emote.RowId,
                    timeline.Key.ToString(),
                    ActionTimelineSelectorEntry.OriginalType.Emote,
                    ActionTimelineSelectorEntry.AnimationPurpose.Standard,
                    (ActionTimelineSlots)timeline.Slot,
                    emote.Icon,
                    drawsWeapon,
                    emoteCategory,
                    ActionTimelineCategory.Emote,
                    GameDataProvider.Instance.ActionTimelineMetadata.Get(timeline.RowId)));
            }

            // Intro
            if(emote.ActionTimeline[1].RowId != 0 && GameDataProvider.Instance.ActionTimelines.TryGetRow(emote.ActionTimeline[1].RowId, out timeline))
            {
                AddItem(new ActionTimelineSelectorEntry(
                    emote.Name.ToString(),
                    (ushort)timeline.RowId,
                    emote.RowId,
                    timeline.Key.ToString(),
                    ActionTimelineSelectorEntry.OriginalType.Emote,
                    ActionTimelineSelectorEntry.AnimationPurpose.Intro,
                    (ActionTimelineSlots)timeline.Slot,
                    emote.Icon,
                    drawsWeapon,
                    emoteCategory,
                    ActionTimelineCategory.Emote,
                    GameDataProvider.Instance.ActionTimelineMetadata.Get(timeline.RowId)));
            }

            // Ground
            if(emote.ActionTimeline[2].RowId != 0 && GameDataProvider.Instance.ActionTimelines.TryGetRow(emote.ActionTimeline[2].RowId, out timeline))
            {
                AddItem(new ActionTimelineSelectorEntry(
                    emote.Name.ToString(),
                    (ushort)timeline.RowId,
                    emote.RowId,
                    timeline.Key.ToString(),
                    ActionTimelineSelectorEntry.OriginalType.Emote,
                    ActionTimelineSelectorEntry.AnimationPurpose.Ground,
                    (ActionTimelineSlots)timeline.Slot,
                    emote.Icon,
                    drawsWeapon,
                    emoteCategory,
                    ActionTimelineCategory.Emote,
                    GameDataProvider.Instance.ActionTimelineMetadata.Get(timeline.RowId)));
            }

            // Chair
            if(emote.ActionTimeline[3].RowId != 0 && GameDataProvider.Instance.ActionTimelines.TryGetRow(emote.ActionTimeline[3].RowId, out timeline))
            {
                AddItem(new ActionTimelineSelectorEntry(
                    emote.Name.ToString(),
                    (ushort)timeline.RowId,
                    emote.RowId,
                    timeline.Key.ToString(),
                    ActionTimelineSelectorEntry.OriginalType.Emote,
                    ActionTimelineSelectorEntry.AnimationPurpose.Chair,
                    (ActionTimelineSlots)timeline.Slot,
                    emote.Icon,
                    drawsWeapon,
                    emoteCategory,
                    ActionTimelineCategory.Emote,
                    GameDataProvider.Instance.ActionTimelineMetadata.Get(timeline.RowId)));
            }

            // Upper Body
            if(emote.ActionTimeline[4].RowId != 0 && GameDataProvider.Instance.ActionTimelines.TryGetRow(emote.ActionTimeline[4].RowId, out timeline))
            {
                AddItem(new ActionTimelineSelectorEntry(
                    emote.Name.ToString(),
                    (ushort)timeline.RowId,
                    emote.RowId,
                    timeline.Key.ToString(),
                    ActionTimelineSelectorEntry.OriginalType.Emote,
                    ActionTimelineSelectorEntry.AnimationPurpose.Blend,
                    (ActionTimelineSlots)timeline.Slot,
                    emote.Icon,
                    drawsWeapon,
                    emoteCategory,
                    ActionTimelineCategory.Emote,
                    GameDataProvider.Instance.ActionTimelineMetadata.Get(timeline.RowId)));
            }
        }

        foreach(var action in GameDataProvider.Instance.GetExcelSheet<ActionSheet>())
        {
            HashSet<uint> addedTimelines = [];

            AddActionReference(
                action,
                action.AnimationStart.ValueNullable?.Name.RowId ?? 0,
                ActionTimelineSelectorEntry.AnimationPurpose.ActionStart,
                addedTimelines);

            AddActionReference(
                action,
                action.AnimationEnd.RowId,
                ActionTimelineSelectorEntry.AnimationPurpose.Action,
                addedTimelines);

            AddActionReference(
                action,
                action.ActionTimelineHit.RowId,
                ActionTimelineSelectorEntry.AnimationPurpose.ActionHit,
                addedTimelines);
        }

        foreach(var pose in CommonPoseCatalog.All)
        {
            if(pose.TimelineId > ushort.MaxValue
                || !GameDataProvider.Instance.ActionTimelines.TryGetRow(pose.TimelineId, out BrioActionTimeline timeline))
                continue;

            AddItem(new ActionTimelineSelectorEntry(
                pose.DisplayName,
                (ushort)pose.TimelineId,
                pose.TimelineId,
                timeline.Key.ToString(),
                ActionTimelineSelectorEntry.OriginalType.Pose,
                ActionTimelineSelectorEntry.AnimationPurpose.Standard,
                (ActionTimelineSlots)timeline.Slot,
                0,
                false,
                0,
                ActionTimelineCategory.None,
                GameDataProvider.Instance.ActionTimelineMetadata.Get(pose.TimelineId)));
        }

        foreach(var modAction in _modActions)
        {
            if(modAction.TimelineId is uint timelineId)
            {
                AddModTimeline(modAction, timelineId, ActionTimelineSelectorEntry.AnimationPurpose.Standard);
            }
            else
            {
                var hasStandardTimeline = AddModTimeline(modAction, 0, ActionTimelineSelectorEntry.AnimationPurpose.Standard);
                if(!hasStandardTimeline)
                    AddModTimeline(modAction, 1, ActionTimelineSelectorEntry.AnimationPurpose.Intro);

                AddModTimeline(modAction, 2, ActionTimelineSelectorEntry.AnimationPurpose.Ground);
                AddModTimeline(modAction, 3, ActionTimelineSelectorEntry.AnimationPurpose.Chair);
                AddModTimeline(modAction, 4, ActionTimelineSelectorEntry.AnimationPurpose.Blend);
            }
        }
    }

    private void AddActionReference(
        ActionSheet action,
        uint timelineId,
        ActionTimelineSelectorEntry.AnimationPurpose purpose,
        HashSet<uint> addedTimelines)
    {
        if(timelineId == 0
            || timelineId > ushort.MaxValue
            || !addedTimelines.Add(timelineId)
            || !GameDataProvider.Instance.ActionTimelines.TryGetRow(timelineId, out BrioActionTimeline timeline))
            return;

        var name = action.Name.ToString();
        if(string.IsNullOrWhiteSpace(name))
            name = timeline.Key.ToString();

        AddItem(new ActionTimelineSelectorEntry(
            name,
            (ushort)timelineId,
            action.RowId,
            timeline.Key.ToString(),
            ActionTimelineSelectorEntry.OriginalType.Action,
            purpose,
            (ActionTimelineSlots)timeline.Slot,
            action.Icon,
            false,
            0,
            action.IsPlayerAction || action.ClassJob.RowId != 0
                ? ActionTimelineCategory.PlayerAction
                : ActionTimelineCategory.NonPlayerAction,
            GameDataProvider.Instance.ActionTimelineMetadata.Get(timelineId)));
    }

    private bool AddModTimeline(PenumbraModAction modAction, int timelineIndex, ActionTimelineSelectorEntry.AnimationPurpose purpose)
    {
        if(modAction.Emote is not Emote emote)
            return false;

        var timelineId = emote.ActionTimeline[timelineIndex].RowId;
        return AddModTimeline(modAction, timelineId, purpose);
    }

    private bool AddModTimeline(PenumbraModAction modAction, uint timelineId, ActionTimelineSelectorEntry.AnimationPurpose purpose)
    {
        var emote = modAction.Emote;
        if(timelineId == 0 || timelineId > ushort.MaxValue
            || !GameDataProvider.Instance.ActionTimelines.TryGetRow(timelineId, out BrioActionTimeline timeline))
            return false;

        AddItem(new ActionTimelineSelectorEntry(
            $"{modAction.ModName} - {modAction.EmoteName}",
            (ushort)timelineId,
            emote?.RowId ?? timelineId,
            timeline.Key.ToString(),
            ActionTimelineSelectorEntry.OriginalType.Mod,
            purpose,
            (ActionTimelineSlots)timeline.Slot,
            emote?.Icon ?? 0,
            emote?.DrawsWeapon ?? false,
            emote is Emote value ? (byte)value.EmoteCategory.RowId : (byte)0,
            ActionTimelineCategory.None,
            GameDataProvider.Instance.ActionTimelineMetadata.Get(timelineId)));

        return true;
    }

    protected override void DrawItem(ActionTimelineSelectorEntry item, bool isSoftSelected)
    {
        var annotation = GetAnnotation(item.TimelineId);
        var displayName = string.IsNullOrWhiteSpace(annotation.PrimaryNote)
            ? item.Name
            : annotation.PrimaryNote;
        var category = GetCategoryLabel(item);
        var compatibility = GetCompatibilityLabel(GetCompatibility(item.Metadata));
        var contextSummary = GetContextSummary(item.Metadata, annotation);
        var description =
            $"{TruncateForList(displayName, 42)}\n" +
            $"{TruncateForList(contextSummary, 48)}\n" +
            $"{item.TimelineId} · {category} · {compatibility}";

        ImBrio.BorderedGameIcon("icon", item.Icon, "Images.ActionTimeline.png", description, flags: ImGuiButtonFlags.None, size: IconSize);
    }

    protected override void DrawTooltip(ActionTimelineSelectorEntry item)
    {
        var metadata = item.Metadata;
        var annotation = GetAnnotation(item.TimelineId);
        var lines = new List<string>
        {
            item.Name,
            $"{item.TimelineId} - {item.Key}",
            $"{Localize.Text("Category")}: {GetCategoryLabel(item)}",
            $"{Localize.Text("Compatibility")}: {GetCompatibilityLabel(GetCompatibility(metadata))}",
        };

        if(!annotation.IsEmpty)
        {
            if(annotation.Notes.Count > 0)
                lines.Add($"{Localize.Text("Notes")}: {string.Join(" / ", annotation.Notes)}");
            if(annotation.RaceTags.Count > 0)
                lines.Add($"{Localize.Text("Manual race tags")}: {string.Join(" / ", annotation.RaceTags)}");
            if(annotation.Props.Count > 0)
                lines.Add($"{Localize.Text("Props")}: {string.Join(" / ", annotation.Props)}");
        }

        if(metadata.Contexts.Count > 0)
        {
            lines.Add($"{Localize.Text("Animation contexts")}:");
            lines.AddRange(metadata.Contexts
                .Take(24)
                .Select(context => $"  {GetContextDisplayName(context)}"));

            if(metadata.Contexts.Count > 24)
                lines.Add(Localize.Format("  ... and {0} more", metadata.Contexts.Count - 24));
        }
        else
        {
            lines.Add(Localize.Text("No exact PAP context is indexed for this timeline."));
        }

        ImGui.SetTooltip(string.Join('\n', lines));
    }

    protected override void DrawOptions()
    {
        if(ExpressionsOnly)
            return;

        var filterButtonWidth = ImGui.GetFrameHeight();
        var stableRowWidth = _isPinned
            ? ImGui.GetContentRegionAvail().X
            : MinimumListSize.X * ImGuiHelpers.GlobalScale;
        var categoryWidth = Math.Max(
            100,
            stableRowWidth - filterButtonWidth - ImGui.GetStyle().ItemSpacing.X);

        ImGui.SetNextItemWidth(categoryWidth);
        if(ImGui.BeginCombo("###action_library_category", GetLibraryCategoryLabel(_libraryCategory)))
        {
            foreach(var category in Enum.GetValues<ActionLibraryCategory>())
            {
                var selected = category == _libraryCategory;
                if(ImGui.Selectable(GetLibraryCategoryLabel(category), selected))
                {
                    _libraryCategory = category;
                    UpdateList();
                }

                if(selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if(ImBrio.FontIconButton(
            "advanced_action_filters",
            FontAwesomeIcon.SlidersH,
            global::Brio.Resources.Localize.Text("Advanced animation filters")))
        {
            ImGui.OpenPopup("advanced_action_filters_popup");
        }

        DrawAdvancedFiltersPopup();
        DrawQuickLibraryCategoryTabs();

        if(_libraryCategory == ActionLibraryCategory.Mods
            && _modActions.Count == 0
            && Brio.TryGetService<PenumbraModActionService>(out var modActionService))
        {
            ImGui.TextDisabled(modActionService.StatusMessage);
        }

        var actorContextLabel = _actorContext is ActionTimelineContext actorContext
            ? GetContextDisplayName(actorContext)
            : Localize.Text("No compatible actor selected");

        ImGui.TextDisabled(Localize.Format(
            "Current animation context: {0}",
            actorContextLabel));

        DrawAnnotationToolbar();
        DrawAnnotationEditorPopup();

        ImBrio.VerticalPadding(3);
    }

    private void DrawAnnotationToolbar()
    {
        var selected = SoftSelected;
        var annotation = selected is null
            ? ActionTimelineAnnotation.Empty
            : GetAnnotation(selected.TimelineId);
        var note = selected is null
            ? Localize.Text("Select an animation to edit its notes")
            : string.IsNullOrWhiteSpace(annotation.PrimaryNote)
                ? Localize.Text("No custom notes")
                : annotation.PrimaryNote;
        if(note.Length > 36)
            note = $"{note[..33]}...";

        ImGui.TextDisabled(Localize.Format("Annotation: {0}", note));

        using(ImRaii.Disabled(selected is null))
        {
            if(ImBrio.FontIconButton(
                "edit_action_annotation",
                FontAwesomeIcon.Edit,
                Localize.Text("Edit action annotation")))
            {
                OpenAnnotationEditor(selected!);
            }
        }

        ImGui.SameLine();
        if(ImBrio.FontIconButton(
            "export_action_annotations",
            FontAwesomeIcon.FileExport,
            Localize.Text("Export user action annotations")))
        {
            ExportAnnotations();
        }

        ImGui.SameLine();
        if(ImBrio.FontIconButton(
            "import_action_annotations",
            FontAwesomeIcon.FileImport,
            Localize.Text("Import user action annotations")))
        {
            ImportAnnotations();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(Localize.Get(
                "ui.actionTimeline.importBackupWarning",
                "Importing a backup replaces all current user action annotations."));

        if(selected is not null
            && _playbackCapability?.CrossRaceAnimationEmulationEnabled == true
            && GetPlaybackContext(selected) is ActionTimelineContext playbackContext)
        {
            ImGui.TextDisabled(Localize.Format(
                "Playback context: {0}",
                GetContextDisplayName(playbackContext)));
        }
    }

    private void OpenAnnotationEditor(ActionTimelineSelectorEntry item)
    {
        var annotation = GetAnnotation(item.TimelineId);
        _annotationEditingTimelineId = item.TimelineId;
        _annotationEditingNotes = string.Join(Environment.NewLine, annotation.Notes);
        _annotationEditingRaceTags = string.Join(Environment.NewLine, annotation.RaceTags);
        _annotationEditingProps = string.Join(Environment.NewLine, annotation.Props);
        ImGui.OpenPopup(Localize.Text("Edit action annotation###action_annotation_editor"));
    }

    private void DrawAnnotationEditorPopup()
    {
        ImGui.SetNextWindowSize(
            new Vector2(430, 390) * ImGuiHelpers.GlobalScale,
            ImGuiCond.FirstUseEver);

        using var popup = ImRaii.PopupModal(
            Localize.Text("Edit action annotation###action_annotation_editor"),
            ImGuiWindowFlags.NoCollapse);
        if(!popup.Success)
            return;

        ImGui.Text(Localize.Format(
            "Animation ID: {0}",
            _annotationEditingTimelineId));
        ImGui.TextWrapped(Localize.Text(
            "Enter multiple values on separate lines. Saving creates a user override; restoring removes it and reveals the built-in annotation."));

        ImGui.Separator();
        ImGui.Text(Localize.Text("Notes"));
        ImGui.InputTextMultiline(
            "###action_annotation_notes",
            ref _annotationEditingNotes,
            4096,
            new Vector2(-1, 100 * ImGuiHelpers.GlobalScale));

        ImGui.Text(Localize.Text("Manual race tags"));
        ImGui.InputTextMultiline(
            "###action_annotation_races",
            ref _annotationEditingRaceTags,
            1024,
            new Vector2(-1, 55 * ImGuiHelpers.GlobalScale));

        ImGui.Text(Localize.Text("Props"));
        ImGui.InputTextMultiline(
            "###action_annotation_props",
            ref _annotationEditingProps,
            1024,
            new Vector2(-1, 55 * ImGuiHelpers.GlobalScale));

        if(ImGui.Button(Localize.Text("Save")))
        {
            if(Brio.TryGetService<ActionTimelineAnnotationService>(out var service))
            {
                service.SetUser(
                    _annotationEditingTimelineId,
                    SplitAnnotationValues(_annotationEditingNotes),
                    SplitAnnotationValues(_annotationEditingRaceTags),
                    SplitAnnotationValues(_annotationEditingProps));
                UpdateList();
            }
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        var hasUserOverride = Brio.TryGetService<ActionTimelineAnnotationService>(out var annotationService)
            && annotationService.HasUserOverride(_annotationEditingTimelineId);
        using(ImRaii.Disabled(!hasUserOverride))
        {
            if(ImGui.Button(Localize.Text("Restore built-in annotation")))
            {
                annotationService!.RemoveUser(_annotationEditingTimelineId);
                UpdateList();
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if(ImGui.Button(Localize.Text("Cancel")))
            ImGui.CloseCurrentPopup();
    }

    private static IEnumerable<string> SplitAnnotationValues(string value)
        => value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ExportAnnotations()
    {
        UIManager.Instance.FileDialogManager.SaveFileDialog(
            Localize.Text("Export Action Annotations###export_action_annotations"),
            Localize.Text("Brio Action Annotations (*.json){.json}"),
            $"Brio-Action-Annotations-{DateTime.Now:yyyyMMdd}",
            ".json",
            (success, path) =>
            {
                if(!success
                    || !Brio.TryGetService<ActionTimelineAnnotationService>(out var service))
                    return;

                try
                {
                    service.Export(path);
                    Brio.NotifyInfo(Localize.Text("Action annotations exported."));
                }
                catch(Exception ex)
                {
                    Brio.Log.Error(ex, "Failed to export action annotations");
                    Brio.NotifyError(Localize.Text("Failed to export action annotations."));
                }
            });
    }

    private void ImportAnnotations()
    {
        UIManager.Instance.FileDialogManager.OpenFileDialog(
            Localize.Text("Import Action Annotations###import_action_annotations"),
            Localize.Text("Brio Action Annotations (*.json){.json}"),
            (success, path) =>
            {
                if(!success
                    || !Brio.TryGetService<ActionTimelineAnnotationService>(out var service))
                    return;

                try
                {
                    var count = service.Import(path);
                    UpdateList();
                    Brio.NotifyInfo(Localize.Format(
                        "Imported {0} action annotations.",
                        count));
                }
                catch(Exception ex)
                {
                    Brio.Log.Error(ex, "Failed to import action annotations");
                    Brio.NotifyError(Localize.Text("Failed to import action annotations."));
                }
            });
    }

    private void DrawAdvancedFiltersPopup()
    {
        if(!ImGui.IsPopupOpen("advanced_action_filters_popup"))
            return;

        var posingConfiguration = ConfigurationService.Instance.Configuration.Posing;
        if(posingConfiguration.AdvancedAnimationFilterWindowPosition is Vector2 savedPosition)
        {
            ImGui.SetNextWindowPos(savedPosition, ImGuiCond.Appearing);
        }
        else
        {
            var estimatedSize = new Vector2(300, 520) * ImGuiHelpers.GlobalScale;
            var displaySize = ImGui.GetIO().DisplaySize;
            var defaultPosition = Vector2.Max(
                new Vector2(24) * ImGuiHelpers.GlobalScale,
                (displaySize - estimatedSize) / 2);
            ImGui.SetNextWindowPos(defaultPosition, ImGuiCond.Appearing);
        }

        if(!ImGui.BeginPopup("advanced_action_filters_popup"))
            return;

        var currentPosition = ImGui.GetWindowPos();
        if(!ImGui.IsMouseDown(ImGuiMouseButton.Left)
            && (posingConfiguration.AdvancedAnimationFilterWindowPosition is not Vector2 previousPosition
                || Vector2.DistanceSquared(previousPosition, currentPosition) > 1f))
        {
            posingConfiguration.AdvancedAnimationFilterWindowPosition = currentPosition;
            ConfigurationService.Instance.Save();
        }

        var changed = false;

        var emulationEnabled = _playbackCapability?.CrossRaceAnimationEmulationEnabled == true;

        if(emulationEnabled
            && SoftSelected is ActionTimelineSelectorEntry selectedAnimation)
        {
            var humanContexts = selectedAnimation.Metadata.Contexts
                .Where(context => context.ModelKind == ActionTimelineModelKind.Human)
                .ToArray();

            if(humanContexts.Length > 0)
            {
                ImGui.Text(Localize.Text("Playback context"));
                ImGui.SetNextItemWidth(260);

                var isManualForSelected = _manualEmulationTimelineId == selectedAnimation.TimelineId
                    && _manualEmulationContext is ActionTimelineContext;
                var preview = isManualForSelected
                    ? GetContextDisplayName(_manualEmulationContext!.Value)
                    : Localize.Text("Automatic (prefer matching sex)");

                if(ImGui.BeginCombo("###animation_playback_context", preview))
                {
                    if(ImGui.Selectable(
                        Localize.Text("Automatic (prefer matching sex)"),
                        !isManualForSelected))
                    {
                        _manualEmulationTimelineId = 0;
                        _manualEmulationContext = null;
                    }

                    foreach(var context in humanContexts)
                    {
                        var selected = isManualForSelected
                            && _manualEmulationContext == context;
                        if(ImGui.Selectable(GetContextDisplayName(context), selected))
                        {
                            _manualEmulationTimelineId = selectedAnimation.TimelineId;
                            _manualEmulationContext = context;
                        }
                    }
                    ImGui.EndCombo();
                }
            }
        }

        ImGui.Separator();
        ImGui.Text(Localize.Text("Compatibility"));
        ImGui.SetNextItemWidth(260);
        if(ImGui.BeginCombo("###animation_compatibility", GetCompatibilityFilterLabel(_compatibilityFilter)))
        {
            foreach(var filter in Enum.GetValues<ActionCompatibilityFilter>())
            {
                var selected = filter == _compatibilityFilter;
                if(ImGui.Selectable(GetCompatibilityFilterLabel(filter), selected))
                {
                    _compatibilityFilter = filter;
                    changed = true;
                }

                if(selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        changed |= ImGui.Checkbox(
            global::Brio.Resources.Localize.Text("Only race-specific human animations"),
            ref _onlyRaceRestricted);
        changed |= ImGui.Checkbox(
            global::Brio.Resources.Localize.Text("Only animations with an exact PAP index"),
            ref _onlyIndexed);
        ImGui.TextDisabled(Localize.Format(
            "Exact PAP index: {0} / {1} timelines",
            GameDataProvider.Instance.ActionTimelineMetadata.IndexedTimelineCount,
            GameDataProvider.Instance.ActionTimelineMetadata.TimelineCount));

        ImGui.Text(Localize.Text("Human race / sex"));
        ImGui.SetNextItemWidth(260);
        if(ImGui.BeginCombo("###human_race_filter", GetHumanRaceFilterLabel(_humanRaceFilterModelId)))
        {
            foreach(var modelId in HumanRaceFilterModelIds)
            {
                var selected = modelId == _humanRaceFilterModelId;
                if(ImGui.Selectable(GetHumanRaceFilterLabel(modelId), selected))
                {
                    _humanRaceFilterModelId = modelId;
                    changed = true;
                }

                if(selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if(_libraryCategory == ActionLibraryCategory.NpcActions)
        {
            ImGui.Separator();
            ImGui.Text(Localize.Text("NPC action type"));
            ImGui.SetNextItemWidth(260);
            if(ImGui.BeginCombo("###npc_action_subtype", GetNpcActionSubtypeLabel(_npcActionSubtype)))
            {
                foreach(var subtype in Enum.GetValues<NpcActionSubtype>())
                {
                    var selected = subtype == _npcActionSubtype;
                    if(ImGui.Selectable(GetNpcActionSubtypeLabel(subtype), selected))
                    {
                        _npcActionSubtype = subtype;
                        changed = true;
                    }

                    if(selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        ImGui.Separator();
        ImGui.Text(global::Brio.Resources.Localize.Text("Emote Category"));
        ImGui.SetNextItemWidth(260);
        if(ImGui.BeginCombo("###emote_category_filter", GetEmoteCategoryLabel(_emoteCategoryValue)))
        {
            for(var category = 0; category <= 3; category++)
            {
                var selected = category == _emoteCategoryValue;
                if(ImGui.Selectable(GetEmoteCategoryLabel(category), selected))
                {
                    _emoteCategoryValue = category;
                    _filterByEmoteCategory = category is >= 1 and <= 3;
                    changed = true;
                }

                if(selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if(_showBlendable)
        {
            changed |= ImGui.Checkbox(
                global::Brio.Resources.Localize.Text("Show Non-Blend Animations"),
                ref _showNonBlendInBlendMode);
        }
        else
        {
            ImGui.Text(global::Brio.Resources.Localize.Text("Draws Weapon"));
            ImGui.SetNextItemWidth(260);

            var drawsWeaponSelection = !_filterByDrawsWeapon ? 0 : (_drawsWeaponValue ? 2 : 1);
            if(ImGui.BeginCombo("###draws_weapon_filter", GetDrawsWeaponFilterLabel(drawsWeaponSelection)))
            {
                for(var selection = 0; selection <= 2; selection++)
                {
                    var selected = selection == drawsWeaponSelection;
                    if(ImGui.Selectable(GetDrawsWeaponFilterLabel(selection), selected))
                    {
                        _filterByDrawsWeapon = selection != 0;
                        _drawsWeaponValue = selection == 2;
                        changed = true;
                    }

                    if(selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        ImGui.Separator();
        if(ImGui.Button(global::Brio.Resources.Localize.Text("Reset Filters")))
        {
            _npcActionSubtype = NpcActionSubtype.All;
            _compatibilityFilter = ActionCompatibilityFilter.All;
            _onlyRaceRestricted = false;
            _onlyIndexed = false;
            _humanRaceFilterModelId = 0;
            _filterByDrawsWeapon = false;
            _drawsWeaponValue = false;
            _filterByEmoteCategory = false;
            _emoteCategoryValue = 0;
            _showNonBlendInBlendMode = false;
            changed = true;
        }

        if(changed)
            UpdateList();

        ImGui.EndPopup();
    }

    protected override int Compare(ActionTimelineSelectorEntry itemA, ActionTimelineSelectorEntry itemB)
    {
        var typeCompare = TypePriority(itemA.TimelineType).CompareTo(TypePriority(itemB.TimelineType));
        if(typeCompare != 0)
            return typeCompare;

        // Blank to last
        if(string.IsNullOrEmpty(itemA.Name) && !string.IsNullOrEmpty(itemB.Name))
            return 1;

        if(!string.IsNullOrEmpty(itemA.Name) && string.IsNullOrEmpty(itemB.Name))
            return -1;

        // Alphabetical
        var comp = string.Compare(itemA.Name, itemB.Name, StringComparison.OrdinalIgnoreCase);
        if(comp != 0)
            return comp;

        // Base Actions first
        if(itemA.Slot == ActionTimelineSlots.Base && itemB.Slot != ActionTimelineSlots.Base)
            return -1;

        if(itemA.Slot != ActionTimelineSlots.Base && itemB.Slot == ActionTimelineSlots.Base)
            return 1;

        return 0;
    }

    private static int TypePriority(ActionTimelineSelectorEntry.OriginalType type)
        => type switch
        {
            ActionTimelineSelectorEntry.OriginalType.Emote => 0,
            ActionTimelineSelectorEntry.OriginalType.Pose => 1,
            ActionTimelineSelectorEntry.OriginalType.Mod => 2,
            ActionTimelineSelectorEntry.OriginalType.Action => 3,
            _ => 4,
        };

    protected override bool Filter(ActionTimelineSelectorEntry item, string search)
    {
        var isEmote = item.TimelineType is ActionTimelineSelectorEntry.OriginalType.Emote
            or ActionTimelineSelectorEntry.OriginalType.Mod
            or ActionTimelineSelectorEntry.OriginalType.Pose;

        if(ExpressionsOnly)
        {
            return isEmote
                && item.EmoteCategory == 3
                && item.Purpose == ActionTimelineSelectorEntry.AnimationPurpose.Blend
                && MatchesSearch(item, search);
        }

        if(!MatchesLibraryCategory(item))
            return false;

        if(_onlyIndexed && !item.Metadata.HasExactContexts)
            return false;

        if(_onlyRaceRestricted && !item.Metadata.IsHumanRaceRestricted)
            return false;

        if(_humanRaceFilterModelId != 0
            && !item.Metadata.Contexts.Any(context =>
                context.ModelKind == ActionTimelineModelKind.Human
                && NormalizeHumanModelId(context.ModelId) == _humanRaceFilterModelId))
            return false;

        if(_compatibilityFilter != ActionCompatibilityFilter.All)
        {
            var compatibility = GetCompatibility(item.Metadata);
            if(_compatibilityFilter switch
            {
                ActionCompatibilityFilter.Native => compatibility != ActionTimelineCompatibility.Native,
                ActionCompatibilityFilter.NativeOrEmulatable => compatibility is not (
                    ActionTimelineCompatibility.Native or ActionTimelineCompatibility.HumanEmulationAvailable),
                ActionCompatibilityFilter.EmulationRequired => compatibility != ActionTimelineCompatibility.HumanEmulationAvailable,
                ActionCompatibilityFilter.Incompatible => compatibility != ActionTimelineCompatibility.Incompatible,
                _ => false,
            })
                return false;
        }

        if(item.Slot != ActionTimelineSlots.Base && !_showBlendable)
            return false;

        // When in blend mode, filter out non-blend animations unless option is enabled
        if(_showBlendable && !_showNonBlendInBlendMode)
        {
            if(isEmote)
            {
                if(item.Purpose != ActionTimelineSelectorEntry.AnimationPurpose.Blend)
                    return false;
            }
        }

        if(_filterByDrawsWeapon)
        {
            if(isEmote)
            {
                if(item.DrawsWeapon != _drawsWeaponValue)
                    return false;
            }
        }

        if(_filterByEmoteCategory)
        {
            if(item.TimelineType == ActionTimelineSelectorEntry.OriginalType.Emote)
            {
                if(item.EmoteCategory != _emoteCategoryValue)
                    return false;
            }
        }

        return MatchesSearch(item, search);
    }

    private bool MatchesSearch(ActionTimelineSelectorEntry item, string search)
    {
        if(item.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase))
            return true;

        return GetAnnotation(item.TimelineId).SearchText.Contains(
            search,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesLibraryCategory(ActionTimelineSelectorEntry item)
    {
        var categories = item.Metadata.Categories;
        var type = item.TimelineType;

        return _libraryCategory switch
        {
            ActionLibraryCategory.Emotes => type == ActionTimelineSelectorEntry.OriginalType.Emote,
            ActionLibraryCategory.PlayerSkills =>
                type == ActionTimelineSelectorEntry.OriginalType.Action
                && item.ReferenceCategory == ActionTimelineCategory.PlayerAction,
            ActionLibraryCategory.NpcActions => MatchesNpcActionSubtype(item),
            ActionLibraryCategory.Monsters =>
                type == ActionTimelineSelectorEntry.OriginalType.Raw
                && (categories.HasFlag(ActionTimelineCategory.Monster)
                    || categories.HasFlag(ActionTimelineCategory.Demihuman)),
            ActionLibraryCategory.OtherCombat =>
                type == ActionTimelineSelectorEntry.OriginalType.Raw
                && categories.HasFlag(ActionTimelineCategory.Combat)
                && !categories.HasFlag(ActionTimelineCategory.PlayerAction)
                && !categories.HasFlag(ActionTimelineCategory.NonPlayerAction),
            ActionLibraryCategory.Mods => type == ActionTimelineSelectorEntry.OriginalType.Mod,
            ActionLibraryCategory.Poses => type == ActionTimelineSelectorEntry.OriginalType.Pose,
            ActionLibraryCategory.Unclassified =>
                type == ActionTimelineSelectorEntry.OriginalType.Raw
                && categories.HasFlag(ActionTimelineCategory.Unclassified),
            ActionLibraryCategory.All => IsPreferredRepresentation(item),
            _ => false,
        };
    }

    private bool MatchesNpcActionSubtype(ActionTimelineSelectorEntry item)
    {
        var categories = item.Metadata.Categories;
        var type = item.TimelineType;

        return _npcActionSubtype switch
        {
            NpcActionSubtype.NonPlayerSkills =>
                type == ActionTimelineSelectorEntry.OriginalType.Action
                && item.ReferenceCategory == ActionTimelineCategory.NonPlayerAction,
            NpcActionSubtype.Ambient =>
                type == ActionTimelineSelectorEntry.OriginalType.Raw
                && categories.HasFlag(ActionTimelineCategory.AmbientNpc),
            NpcActionSubtype.Cutscene =>
                type == ActionTimelineSelectorEntry.OriginalType.Raw
                && categories.HasFlag(ActionTimelineCategory.Cutscene),
            NpcActionSubtype.HumanSpecial =>
                type == ActionTimelineSelectorEntry.OriginalType.Raw
                && categories.HasFlag(ActionTimelineCategory.HumanSpecial),
            _ =>
                (type == ActionTimelineSelectorEntry.OriginalType.Action
                    && item.ReferenceCategory == ActionTimelineCategory.NonPlayerAction)
                || (type == ActionTimelineSelectorEntry.OriginalType.Raw
                    && (categories.HasFlag(ActionTimelineCategory.AmbientNpc)
                        || categories.HasFlag(ActionTimelineCategory.Cutscene)
                        || categories.HasFlag(ActionTimelineCategory.HumanSpecial))),
        };
    }

    private static bool IsPreferredRepresentation(ActionTimelineSelectorEntry item)
    {
        if(item.TimelineType != ActionTimelineSelectorEntry.OriginalType.Raw)
            return true;

        var categories = item.Metadata.Categories;
        return !categories.HasFlag(ActionTimelineCategory.Emote)
            && !categories.HasFlag(ActionTimelineCategory.PlayerAction)
            && !categories.HasFlag(ActionTimelineCategory.NonPlayerAction);
    }

    private ActionTimelineCompatibility GetCompatibility(ActionTimelineMetadata metadata)
    {
        if(_actorContext is not ActionTimelineContext actorContext || !metadata.HasExactContexts)
            return ActionTimelineCompatibility.Unknown;

        if(metadata.Supports(actorContext))
            return ActionTimelineCompatibility.Native;

        if(actorContext.ModelKind == ActionTimelineModelKind.Human && metadata.CanEmulateOnHuman)
            return ActionTimelineCompatibility.HumanEmulationAvailable;

        return ActionTimelineCompatibility.Incompatible;
    }

    public ActionTimelineContext? GetPlaybackContext(ActionTimelineSelectorEntry item)
    {
        if(_playbackCapability?.CrossRaceAnimationEmulationEnabled != true
            || !Brio.TryGetService<GPoseService>(out var gPoseService)
            || !gPoseService.IsGPosing
            || _actorContext is not ActionTimelineContext actorContext
            || actorContext.ModelKind != ActionTimelineModelKind.Human
            || item.Metadata.Supports(actorContext))
            return null;

        var humanContexts = item.Metadata.Contexts
            .Where(context => context.ModelKind == ActionTimelineModelKind.Human)
            .ToArray();
        if(humanContexts.Length == 0)
            return null;

        if(_manualEmulationTimelineId == item.TimelineId
            && _manualEmulationContext is ActionTimelineContext manual
            && humanContexts.Contains(manual))
            return manual;

        var actorIsFemale = IsFemaleHumanModel(actorContext.ModelId);
        return humanContexts
            .OrderBy(context => IsFemaleHumanModel(context.ModelId) == actorIsFemale ? 0 : 1)
            .ThenBy(context => context.ModelId % 10 == 1 ? 0 : 1)
            .ThenBy(context => context.ModelId)
            .ThenBy(context => context.AnimationVariant)
            .First();
    }

    private void DrawQuickLibraryCategoryTabs()
    {
        var selectedCategory = _libraryCategory == ActionLibraryCategory.Emotes
            && _filterByEmoteCategory
            && _emoteCategoryValue == 3
                ? 1
                : Array.IndexOf(QuickLibraryCategories, _libraryCategory);
        if(!ImBrio.ButtonSelectorStrip(
            "action_timeline_quick_categories",
            Vector2.Zero,
            ref selectedCategory,
            QuickLibraryCategoryLabels))
        {
            return;
        }

        _libraryCategory = QuickLibraryCategories[selectedCategory];
        _filterByEmoteCategory = selectedCategory == 1;
        _emoteCategoryValue = selectedCategory == 1 ? 3 : 0;
        UpdateList();
    }

    private ActionTimelineAnnotationService? AnnotationService
    {
        get
        {
            if(_annotationService is null
                && Brio.TryGetService<ActionTimelineAnnotationService>(out var service))
                _annotationService = service;

            return _annotationService;
        }
    }

    private ActionTimelineAnnotation GetAnnotation(uint timelineId)
        => AnnotationService is ActionTimelineAnnotationService service
            ? service.GetEffective(timelineId)
            : ActionTimelineAnnotation.Empty;

    private static string GetCategoryLabel(ActionTimelineSelectorEntry item)
    {
        if(item.TimelineType == ActionTimelineSelectorEntry.OriginalType.Mod)
            return Localize.Text("Mod");

        if(item.TimelineType == ActionTimelineSelectorEntry.OriginalType.Pose)
            return Localize.Text("Poses");

        if(item.ReferenceCategory == ActionTimelineCategory.PlayerAction)
            return Localize.Text("Player Skills");

        if(item.ReferenceCategory == ActionTimelineCategory.NonPlayerAction)
            return Localize.Text("Non-Player Skills");

        var categories = item.Metadata.Categories;

        if(categories.HasFlag(ActionTimelineCategory.Emote))
            return Localize.Text("Emotes");
        if(categories.HasFlag(ActionTimelineCategory.PlayerAction))
            return Localize.Text("Player Skills");
        if(categories.HasFlag(ActionTimelineCategory.NonPlayerAction))
            return Localize.Text("Non-Player Skills");
        if(categories.HasFlag(ActionTimelineCategory.AmbientNpc))
            return Localize.Text("Ambient NPC Actions");
        if(categories.HasFlag(ActionTimelineCategory.Cutscene))
            return Localize.Text("Cutscene Actions");
        if(categories.HasFlag(ActionTimelineCategory.HumanSpecial))
            return Localize.Text("Human-Specific Actions");
        if(categories.HasFlag(ActionTimelineCategory.Monster))
            return Localize.Text("Monster Actions");
        if(categories.HasFlag(ActionTimelineCategory.Demihuman))
            return Localize.Text("Demihuman Actions");
        if(categories.HasFlag(ActionTimelineCategory.Combat))
            return Localize.Text("Other Combat Actions");
        if(categories.HasFlag(ActionTimelineCategory.Common))
            return Localize.Text("Common Actions");

        return Localize.Text("Unclassified");
    }

    private static string GetLibraryCategoryLabel(ActionLibraryCategory category) => category switch
    {
        ActionLibraryCategory.Emotes => Localize.Text("Emotes"),
        ActionLibraryCategory.PlayerSkills => Localize.Text("Player Skills"),
        ActionLibraryCategory.NpcActions => Localize.Text("NPC Actions"),
        ActionLibraryCategory.Monsters => Localize.Text("Monster & Demihuman"),
        ActionLibraryCategory.OtherCombat => Localize.Text("Other Combat Actions"),
        ActionLibraryCategory.Mods => Localize.Text("Mod"),
        ActionLibraryCategory.Poses => Localize.Text("Poses"),
        ActionLibraryCategory.Unclassified => Localize.Text("Unclassified"),
        ActionLibraryCategory.All => Localize.Text("All Animations"),
        _ => category.ToString(),
    };

    private static string GetNpcActionSubtypeLabel(NpcActionSubtype subtype) => subtype switch
    {
        NpcActionSubtype.All => Localize.Text("All NPC Actions"),
        NpcActionSubtype.NonPlayerSkills => Localize.Text("Non-Player Skills"),
        NpcActionSubtype.Ambient => Localize.Text("Ambient NPC Actions"),
        NpcActionSubtype.Cutscene => Localize.Text("Cutscene Actions"),
        NpcActionSubtype.HumanSpecial => Localize.Text("Human-Specific Actions"),
        _ => subtype.ToString(),
    };

    private static string GetCompatibilityFilterLabel(ActionCompatibilityFilter filter) => filter switch
    {
        ActionCompatibilityFilter.All => Localize.Text("All Compatibility States"),
        ActionCompatibilityFilter.Native => Localize.Text("Native for Current Actor"),
        ActionCompatibilityFilter.NativeOrEmulatable => Localize.Text("Native or Human-Emulatable"),
        ActionCompatibilityFilter.EmulationRequired => Localize.Text("Requires Human Emulation"),
        ActionCompatibilityFilter.Incompatible => Localize.Text("Incompatible with Current Actor"),
        _ => filter.ToString(),
    };

    private static string GetCompatibilityLabel(ActionTimelineCompatibility compatibility) => compatibility switch
    {
        ActionTimelineCompatibility.Native => Localize.Text("Native"),
        ActionTimelineCompatibility.HumanEmulationAvailable => Localize.Text("Human emulation available"),
        ActionTimelineCompatibility.Incompatible => Localize.Text("Incompatible"),
        _ => Localize.Text("Compatibility unknown"),
    };

    private static string GetEmoteCategoryLabel(int category) => category switch
    {
        1 => Localize.Text("General"),
        2 => Localize.Text("Special"),
        3 => Localize.Text("Expression"),
        _ => Localize.Text("All"),
    };

    private static string GetDrawsWeaponFilterLabel(int selection) => selection switch
    {
        1 => Localize.Text("Sheathed"),
        2 => Localize.Text("Drawn"),
        _ => Localize.Text("All"),
    };

    private static string GetHumanRaceFilterLabel(ushort modelId)
    {
        if(modelId == 0)
            return Localize.Text("All human races");

        return GetHumanRaceSexLabel(modelId);
    }

    private static string GetContextSummary(
        ActionTimelineMetadata metadata,
        ActionTimelineAnnotation annotation)
    {
        var manualTags = annotation.RaceTags.Count > 0
            ? Localize.Format(
                "Manual: {0}",
                string.Join(" / ", annotation.RaceTags))
            : string.Empty;

        if(!metadata.HasExactContexts)
            return string.IsNullOrEmpty(manualTags)
                ? Localize.Text("Unindexed")
                : manualTags;

        if(metadata.Contexts.Count == 1)
            return JoinContextAndManual(
                GetContextDisplayName(metadata.Contexts[0]),
                manualTags);

        if(metadata.HasHumanContexts && !metadata.IsHumanRaceRestricted)
            return JoinContextAndManual(
                Localize.Text("All playable races"),
                manualTags);

        var humanContexts = metadata.Contexts
            .Where(context => context.ModelKind == ActionTimelineModelKind.Human)
            .ToArray();
        if(humanContexts.Length is > 0 and <= 4)
        {
            var exact = string.Join(
                " / ",
                humanContexts
                    .GroupBy(context => context.AnimationVariant)
                    .Select(group =>
                        $"{string.Join("/", group.Select(context => GetHumanRaceSexShortLabel(context.ModelId)))} · a{group.Key:D4}"));
            return JoinContextAndManual(exact, manualTags);
        }

        return JoinContextAndManual(
            Localize.Format("{0} contexts", metadata.Contexts.Count),
            manualTags);
    }

    private static string JoinContextAndManual(string context, string manual)
        => string.IsNullOrEmpty(manual)
            ? context
            : $"{context} · {manual}";

    private static string TruncateForList(string value, int maximumLength)
        => value.Length <= maximumLength
            ? value
            : $"{value[..(maximumLength - 3)]}...";

    private static string GetContextDisplayName(ActionTimelineContext context)
    {
        if(context.ModelKind != ActionTimelineModelKind.Human)
        {
            var modelType = context.ModelKind == ActionTimelineModelKind.Monster
                ? Localize.Text("Monster")
                : Localize.Text("Demihuman");

            return $"{modelType} {context.ModelCode} / {context.AnimationVariantCode}";
        }

        var isNpcPath = context.ModelId % 10 == 4;
        var raceSex = GetHumanRaceSexLabel(context.ModelId);
        var npcSuffix = isNpcPath ? $" · {Localize.Text("NPC data path")}" : string.Empty;
        return $"{raceSex} {context.ModelCode} / {context.AnimationVariantCode}{npcSuffix}";
    }

    private static string GetHumanRaceSexLabel(ushort modelId)
    {
        var playableModelId = NormalizeHumanModelId(modelId);
        return playableModelId switch
        {
            101 => Localize.Text("Midlander Male"),
            201 => Localize.Text("Midlander Female"),
            301 => Localize.Text("Highlander Male"),
            401 => Localize.Text("Highlander Female"),
            501 => Localize.Text("Elezen Male"),
            601 => Localize.Text("Elezen Female"),
            701 => Localize.Text("Miqote Male"),
            801 => Localize.Text("Miqote Female"),
            901 => Localize.Text("Roegadyn Male"),
            1001 => Localize.Text("Roegadyn Female"),
            1101 => Localize.Text("Lalafell Male"),
            1201 => Localize.Text("Lalafell Female"),
            1301 => Localize.Text("Au Ra Male"),
            1401 => Localize.Text("Au Ra Female"),
            1501 => Localize.Text("Hrothgar Male"),
            1601 => Localize.Text("Hrothgar Female"),
            1701 => Localize.Text("Viera Male"),
            1801 => Localize.Text("Viera Female"),
            _ => Localize.Text("Human"),
        };
    }

    private static string GetHumanRaceSexShortLabel(ushort modelId)
    {
        var playableModelId = NormalizeHumanModelId(modelId);
        return playableModelId switch
        {
            101 => Localize.Text("Midlander M."),
            201 => Localize.Text("Midlander F."),
            301 => Localize.Text("Highlander M."),
            401 => Localize.Text("Highlander F."),
            501 => Localize.Text("Elezen M."),
            601 => Localize.Text("Elezen F."),
            701 => Localize.Text("Miqote M."),
            801 => Localize.Text("Miqote F."),
            901 => Localize.Text("Roegadyn M."),
            1001 => Localize.Text("Roegadyn F."),
            1101 => Localize.Text("Lalafell M."),
            1201 => Localize.Text("Lalafell F."),
            1301 => Localize.Text("Au Ra M."),
            1401 => Localize.Text("Au Ra F."),
            1501 => Localize.Text("Hrothgar M."),
            1601 => Localize.Text("Hrothgar F."),
            1701 => Localize.Text("Viera M."),
            1801 => Localize.Text("Viera F."),
            _ => Localize.Text("Human"),
        };
    }

    private static ushort NormalizeHumanModelId(ushort modelId)
        => modelId % 10 == 4 ? (ushort)(modelId - 3) : modelId;

    private static bool IsFemaleHumanModel(ushort modelId)
        => (NormalizeHumanModelId(modelId) / 100) % 2 == 0;

    private static readonly ushort[] HumanRaceFilterModelIds =
    [
        0,
        101, 201, 301, 401, 501, 601, 701, 801, 901,
        1001, 1101, 1201, 1301, 1401, 1501, 1601, 1701, 1801,
    ];
}

public record class ActionTimelineSelectorEntry(
    string Name,
    ushort TimelineId,
    uint SecondaryId,
    string Key,
    ActionTimelineSelectorEntry.OriginalType TimelineType,
    ActionTimelineSelectorEntry.AnimationPurpose Purpose,
    ActionTimelineSlots Slot,
    uint Icon,
    bool DrawsWeapon,
    byte EmoteCategory,
    ActionTimelineCategory ReferenceCategory,
    ActionTimelineMetadata Metadata)
{
    public string SearchText { get; } = string.Join(
        ' ',
        Name,
        TimelineId,
        TimelineType,
        Slot,
        Purpose,
        Key,
        SecondaryId,
        ReferenceCategory,
        Metadata.Categories,
        string.Join(' ', Metadata.Contexts));

    public enum AnimationPurpose
    {
        Unknown,
        Action,
        ActionStart,
        ActionHit,
        Standard,
        Intro,
        Ground,
        Chair,
        Blend,
    }

    public enum OriginalType
    {
        Raw,
        Emote,
        Action,
        Mod,
        Pose,
    }
}

public enum ActionLibraryCategory
{
    Emotes,
    PlayerSkills,
    NpcActions,
    Monsters,
    OtherCombat,
    Mods,
    Poses,
    Unclassified,
    All,
}

public enum NpcActionSubtype
{
    All,
    NonPlayerSkills,
    Ambient,
    Cutscene,
    HumanSpecial,
}

public enum ActionCompatibilityFilter
{
    All,
    Native,
    NativeOrEmulatable,
    EmulationRequired,
    Incompatible,
}

public enum ActionTimelineCompatibility
{
    Unknown,
    Native,
    HumanEmulationAvailable,
    Incompatible,
}
