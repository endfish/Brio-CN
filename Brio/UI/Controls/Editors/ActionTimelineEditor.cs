using Brio.Capabilities.Actor;
using Brio.Config;
using Brio.Entities;
using Brio.Files;
using Brio.Game.Actor.Extensions;
using Brio.Game.Cutscene;
using Brio.Game.GPose;
using Brio.Game.Posing;
using Brio.Resources;
using Brio.UI.Controls.Selectors;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using System.IO;
using System.Numerics;
using static Brio.Game.Actor.ActionTimelineService;

namespace Brio.UI.Controls.Editors;

public class ActionTimelineEditor(CutsceneManager cutsceneManager, GPoseService gPoseService, EntityManager entityManager, PhysicsService physicsService, ConfigurationService configService)
{
    private readonly CutsceneManager _cutsceneManager = cutsceneManager;
    private readonly GPoseService _gPoseService = gPoseService;
    private readonly PhysicsService _physicsService = physicsService;
    private readonly ConfigurationService _configService = configService;
    private readonly EntityManager _entityManager = entityManager;

    private static float MaxItemWidth => ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("XXXXXXXXXXXXXXXXXX").X;
    private static float LabelStart => MaxItemWidth + ImGui.GetCursorPosX() + (ImGui.GetStyle().FramePadding.X * 2f);

    private static readonly ActionTimelineSelector _globalTimelineSelector = new("global_timeline_selector");

    private static bool _startAnimationOnSelect = true;
    private static bool _isBaseMode = false;

    private string _cameraPath = string.Empty;
    private ActionTimelineCapability _capability = null!;
    private bool _delimitSpeed = false;

    private void HandleSelectorChanges()
    {
        if(_globalTimelineSelector.SoftSelectionChanged && _globalTimelineSelector.SoftSelected != null)
        {
            if(_isBaseMode)
            {
                _capability.SlotedBaseAnimation = _globalTimelineSelector.SoftSelected.TimelineId;
            }
            else
            {
                _capability.SlotedBlendAnimation = _globalTimelineSelector.SoftSelected.TimelineId;
            }
        }

        if(_globalTimelineSelector.SelectionChanged && _globalTimelineSelector.Selected != null)
        {
            var selected = _globalTimelineSelector.Selected;
            var playbackContext = _globalTimelineSelector.GetPlaybackContext(selected);
            _capability.ConfigureAnimationContext(
                selected.TimelineId,
                playbackContext);

            if(_isBaseMode)
            {
                _capability.SlotedBaseAnimation = selected.TimelineId;
                if(_startAnimationOnSelect)
                    ApplyBaseOverride(_capability, true);
            }
            else
            {
                _capability.SlotedBlendAnimation = selected.TimelineId;
                ApplyBlend(_capability);
            }

            // Close popup if not pinned
            if(!_globalTimelineSelector.IsPinned)
                ImGui.CloseCurrentPopup();
        }
    }

    public void Draw(bool drawAdvanced, ActionTimelineCapability capability)
    {
        _capability = capability;
        _globalTimelineSelector.SetActor(capability);

        _globalTimelineSelector.DrawAsWindow();

        HandleSelectorChanges();

        DrawHeder();

        DrawActiveTimelines();

        ImBrio.SeparatorText(Localize.Text("Animation to Play"));

        DrawBaseOverride();
        DrawBlend();
        DrawOverallSpeed(drawAdvanced);

        if(!drawAdvanced)
        {
            ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Animation Scruber"));

            DrawFirstScrub();
        }
        else
        {
            ImBrio.VerticalPadding(2);
            DrawLips();

            ImBrio.VerticalPadding(4);

            if(ImGui.CollapsingHeader(global::Brio.Resources.Localize.Text("Scrub")))
            {
                ImBrio.VerticalPadding(2);
                DrawScrub();
                ImBrio.VerticalPadding(2);
            }

            if(ImGui.CollapsingHeader(global::Brio.Resources.Localize.Text("Slots")))
            {
                ImBrio.VerticalPadding(2);
                DrawSlots();
                ImBrio.VerticalPadding(2);
            }

            if(ImGui.CollapsingHeader(global::Brio.Resources.Localize.Text("Cutscene Control")))
            {
                ImBrio.VerticalPadding(2);
                DrawCutscene();
                ImBrio.VerticalPadding(2);
            }
        }
    }

    private void DrawHeder()
    {
        if(ImBrio.ToggelButton(global::Brio.Resources.Localize.Text("Freeze Physics"), new Vector2(110, 25), _physicsService.IsFreezeEnabled,
            hoverText: global::Brio.Resources.Localize.Text(_physicsService.IsFreezeEnabled ? "Un-Freeze Physics" : "Freeze Physics")))
        {
            _physicsService.FreezeToggle();
        }

        ImGui.SameLine();
        ImBrio.HorizontalPadding(2);

        ImBrio.RightAlign(100 * ImGuiHelpers.GlobalScale, 1);

        if(ImGui.Button(global::Brio.Resources.Localize.Text("Actors  ▼"), new Vector2(70, 25) * ImGuiHelpers.GlobalScale))
        {
            ImGui.OpenPopup("animation_control");
        }

        ImGui.SameLine();
        ImBrio.HorizontalPadding(2);

        if(ImBrio.FontIconButtonRight("reset", FontAwesomeIcon.Undo, 1, global::Brio.Resources.Localize.Text("Reset Animation"), _capability.HasOverride))
        {
            _capability.Reset();
            _cutsceneManager?.StopPlayback();
            if(_cutsceneManager is not null)
                _cutsceneManager.CameraPath = null;
            _cameraPath = string.Empty;
        }

        using var popup = ImRaii.Popup("animation_control");
        if(popup.Success)
        {
            ImBrio.VerticalPadding(1);

            if(ImBrio.Button(global::Brio.Resources.Localize.Text("Freeze All Actors"), FontAwesomeIcon.Snowflake, new Vector2(180, 0)))
            {
                foreach(var actor in _entityManager.TryGetAllActors())
                {
                    if(actor.TryGetCapability<ActionTimelineCapability>(out ActionTimelineCapability? atCap))
                    {
                        if(atCap is null)
                            return;

                        if(atCap.SpeedMultiplier > 0f)
                        {
                            atCap.SetOverallSpeedOverride(0f);
                        }
                    }
                }
            }

            ImBrio.VerticalPadding(1);

            if(ImBrio.Button(global::Brio.Resources.Localize.Text("  Un-Freeze All Actors"), FontAwesomeIcon.Fire, new Vector2(180, 0)))
            {
                foreach(var actor in _entityManager.TryGetAllActors())
                {
                    if(actor.TryGetCapability<ActionTimelineCapability>(out ActionTimelineCapability? atCap))
                    {
                        if(atCap is null)
                            return;

                        if(atCap.HasSpeedMultiplierOverride)
                        {
                            atCap.ResetOverallSpeedOverride();
                        }
                    }
                }
            }

            ImBrio.VerticalPadding(1);

            if(ImBrio.Button(global::Brio.Resources.Localize.Text("  Play all Animations"), FontAwesomeIcon.PlayCircle, new Vector2(180, 0)))
            {
                foreach(var actor in _entityManager.TryGetAllActors())
                {
                    if(actor.TryGetCapability<ActionTimelineCapability>(out ActionTimelineCapability? atCap))
                    {
                        if(atCap is null)
                            return;

                        ApplyBaseOverride(atCap, true);
                    }
                }
            }

            ImBrio.VerticalPadding(1);

            if(ImBrio.Button(global::Brio.Resources.Localize.Text("  Stop all Animations"), FontAwesomeIcon.StopCircle, new Vector2(180, 0)))
            {
                foreach(var actor in _entityManager.TryGetAllActors())
                {
                    if(actor.TryGetCapability<ActionTimelineCapability>(out ActionTimelineCapability? atCap))
                    {
                        if(atCap is null)
                            return;

                        atCap.Stop();
                    }
                }
            }
        }
    }

    private void DrawActiveTimelines()
    {
        ImBrio.SeparatorText(Localize.Text("Playing Now (Live)"));

        Span<ushort> timelines = stackalloc ushort[14];
        if(!_capability.TryReadActiveTimelineIds(timelines))
        {
            ImGui.TextDisabled(Localize.Text("Current animation is unavailable."));
            return;
        }

        var anyActive = false;
        for(var slot = 0; slot < timelines.Length; slot++)
        {
            var timelineId = timelines[slot];
            if(timelineId == 0)
                continue;

            anyActive = true;
            using var id = ImRaii.PushId($"live_timeline_{slot}");
            ImGui.TextUnformatted(slot == 0
                ? Localize.Format("Base: {0}", timelineId)
                : Localize.Format("Slot {0}: {1}", slot, timelineId));
            ImGui.SameLine();
            if(ImBrio.FontIconButtonRight("copy", FontAwesomeIcon.Copy, 2, Localize.Text("Copy animation ID")))
                ImGui.SetClipboardText(timelineId.ToString());

            ImGui.SameLine();
            if(ImBrio.FontIconButtonRight("use", FontAwesomeIcon.ArrowDown, 1,
                Localize.Text("Fill the animation input without playing it")))
            {
                if(slot == 0)
                    _capability.SlotedBaseAnimation = timelineId;
                else
                    _capability.SlotedBlendAnimation = timelineId;
            }
        }

        if(!anyActive)
            ImGui.TextDisabled(Localize.Text("No active animation timeline."));
    }

    private void DrawBaseOverride()
    {
        var baseLabel = global::Brio.Resources.Localize.Text("Base Animation");
        ImGui.SetNextItemWidth(MaxItemWidth - ImGui.CalcTextSize("XXXX").X);
        ImGui.InputInt($"###base_animation", ref _capability.SlotedBaseAnimation, 0, 0);
        if(ImBrio.IsItemConfirmed())
        {
            ApplyBaseOverride(_capability, true);
        }

        ImGui.SameLine();
        ImGui.Checkbox(global::Brio.Resources.Localize.Text("###base_interrupt"), ref _capability.DoBaseInterrupt);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(global::Brio.Resources.Localize.Text("Interrupt"));

        ImGui.SameLine();
        ImGui.SetCursorPosX(LabelStart);
        ImGui.Text(baseLabel);

        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight("base_play", FontAwesomeIcon.PlayCircle, 3, global::Brio.Resources.Localize.Text("Play"), _capability.SlotedBaseAnimation != 0))
            ApplyBaseOverride(_capability);

        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight("base_reset", FontAwesomeIcon.StopCircle, 2, global::Brio.Resources.Localize.Text("Stop"), _capability.HasBaseOverride))
        {
            _capability.ResetBaseOverride();
            _capability.ResetOverallSpeedOverride();
        }

        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight("base_search", FontAwesomeIcon.Search, 1, global::Brio.Resources.Localize.Text("Search")))
        {
            _isBaseMode = true;
            _globalTimelineSelector.Select(null, false);
            _globalTimelineSelector.AllowBlending = false;
            ImGui.OpenPopup("base_search_popup");
        }

        using(var popup = ImRaii.Popup("base_search_popup"))
        {
            if(popup.Success)
            {
                ImGui.Checkbox(global::Brio.Resources.Localize.Text("Start Animation On Select"), ref _startAnimationOnSelect);
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip(global::Brio.Resources.Localize.Text("Start Animation On Select"));

                ImGui.SameLine();
                var crossRaceEnabled = _capability.CrossRaceAnimationEmulationEnabled;
                using(ImRaii.Disabled(!_capability.CanUseCrossRaceAnimationEmulation))
                {
                    if(ImGui.Checkbox(
                        global::Brio.Resources.Localize.Text("Cross-race###current_actor_cross_race_animation"),
                        ref crossRaceEnabled))
                    {
                        _capability.CrossRaceAnimationEmulationEnabled = crossRaceEnabled;
                    }
                }

                ImGui.SameLine();
                ImBrio.FontIcon(FontAwesomeIcon.ExclamationTriangle);
                if(ImGui.IsItemHovered())
                {
                    using(ImRaii.Tooltip())
                    {
                        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetFontSize() * 28f);
                        ImGui.TextUnformatted(global::Brio.Resources.Localize.Get(
                            "ui.pose.crossRaceWarning",
                            "Only affects the current actor and resets when GPose ends.\nSome modded animations or custom skeletons may be incompatible.\nDisable it immediately if animation or rendering behaves abnormally."));
                        ImGui.PopTextWrapPos();
                    }
                }

                _globalTimelineSelector.Draw();
            }
        }
    }

    private void DrawBlend()
    {
        var blendLabel = global::Brio.Resources.Localize.Text("Blend Animation");

        ImGui.SetNextItemWidth(MaxItemWidth);
        ImGui.InputInt($"###blend_animation", ref _capability.SlotedBlendAnimation, 0, 0);
        if(ImBrio.IsItemConfirmed())
        {
            ApplyBlend(_capability);
        }

        ImGui.SameLine();

        ImGui.SetCursorPosX(LabelStart);
        ImGui.Text(blendLabel);

        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight("blend_play", FontAwesomeIcon.PlayCircle, 2, global::Brio.Resources.Localize.Text("Play"), _capability.SlotedBlendAnimation != 0))
            ApplyBlend(_capability);

        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight("blend_search", FontAwesomeIcon.Search, 1, global::Brio.Resources.Localize.Text("Search")))
        {
            _isBaseMode = false;
            _globalTimelineSelector.Select(null, false);
            _globalTimelineSelector.AllowBlending = true;
            ImGui.OpenPopup("blend_search_popup");
        }

        using(var popup = ImRaii.Popup("blend_search_popup"))
        {
            if(popup.Success)
            {
                _globalTimelineSelector.Draw();
            }
        }
    }

    private void DrawLips()
    {
        var lipsOverride = _capability.LipsOverride;

        string preview = "None";
        if(lipsOverride != 0)
            preview = GameDataProvider.Instance.ActionTimelines[lipsOverride].Key.ToString();

        ImGui.SetNextItemWidth(MaxItemWidth);
        using(var combo = ImRaii.Combo("###lips", preview))
        {
            if(combo.Success)
            {
                if(ImGui.Selectable(global::Brio.Resources.Localize.Text("None"), lipsOverride == 0))
                {
                    _capability.LipsOverride = 0;
                }

                for(uint i = 0x272; i <= 0x272 + 8; ++i)
                {
                    var entry = GameDataProvider.Instance.ActionTimelines[i];
                    bool selected = lipsOverride == i;
                    if(ImGui.Selectable($"{entry.Key} ({i})", selected))
                    {
                        _capability.LipsOverride = (ushort)i;
                    }
                }
            }
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(LabelStart);
        ImGui.Text(global::Brio.Resources.Localize.Text("Lips"));
    }

    private unsafe void DrawScrub()
    {
        float width = -ImGui.CalcTextSize("XXXX").X;

        var drawObj = _capability.Character.Native()->GameObject.DrawObject;
        if(drawObj == null)
            return;

        if(drawObj->Object.GetObjectType() != ObjectType.CharacterBase)
            return;

        var charaBase = (CharacterBase*)drawObj;

        if(charaBase->Skeleton == null)
            return;

        var skeleton = charaBase->Skeleton;

        for(int p = 0; p < skeleton->PartialSkeletonCount; ++p)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animatedSkele = partial->GetHavokAnimatedSkeleton(0);
            if(animatedSkele == null)
                continue;

            for(int c = 0; c < animatedSkele->AnimationControls.Length; ++c)
            {
                var control = animatedSkele->AnimationControls[c].Value;
                if(control == null)
                    continue;

                var binding = control->hkaAnimationControl.Binding;
                if(binding.ptr == null)
                    continue;

                var anim = binding.ptr->Animation.ptr;
                if(anim == null)
                    continue;

                var duration = anim->Duration;
                var time = control->hkaAnimationControl.LocalTime;
                ImGui.SetNextItemWidth(width);
                if(ImGui.SliderFloat($"###scrub_{p}_{c}", ref time, 0f, duration, "%.2f", ImGuiSliderFlags.AlwaysClamp))
                {
                    control->hkaAnimationControl.LocalTime = time;
                }
                if(ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _capability.SetOverallSpeedOverride(0f);
                }
                ImGui.SameLine();
                ImGui.Text($"{p}.{c}");
            }
        }
    }

    private unsafe void DrawFirstScrub()
    {
        var drawObj = _capability.Character.Native()->GameObject.DrawObject;
        if(drawObj == null)
            return;

        if(drawObj->Object.GetObjectType() != ObjectType.CharacterBase)
            return;

        var charaBase = (CharacterBase*)drawObj;

        if(charaBase->Skeleton == null)
            return;

        var skeleton = charaBase->Skeleton;

        if(!(skeleton->PartialSkeletonCount > 0))
            return;

        var partial = &skeleton->PartialSkeletons[0];
        var animatedSkele = partial->GetHavokAnimatedSkeleton(0);
        if(animatedSkele == null)
            return;

        if(!(animatedSkele->AnimationControls.Length > 0))
            return;

        var control = animatedSkele->AnimationControls[0].Value;
        if(control == null)
            return;

        var binding = control->hkaAnimationControl.Binding;
        if(binding.ptr == null)
            return;

        var anim = binding.ptr->Animation.ptr;
        if(anim == null)
            return;

        var duration = anim->Duration;
        var time = control->hkaAnimationControl.LocalTime;

        ImBrio.CenterNextElementWithPadding(10);
        if(ImGui.SliderFloat($"###scrub_001", ref time, 0f, duration, "%.2f", ImGuiSliderFlags.AlwaysClamp))
        {
            control->hkaAnimationControl.LocalTime = time;
            _capability.SetOverallSpeedOverride(0f);
        }
    }

    private void DrawSlots()
    {
        var slots = Enum.GetValues<ActionTimelineSlots>();

        foreach(var slot in slots)
        {
            using(ImRaii.PushId((int)slot))
            {
                DrawSlot(slot);
                ImBrio.VerticalPadding(2);
                ImGui.Separator();
            }
        }
    }

    private void DrawSlot(ActionTimelineSlots slot)
    {
        var actionInfo = _capability.GetSlotAction(slot).Match(
                   action => $"{action.RowId} ({action.Key})",
                   none => "None"
               );

        var slotDescription = $"{slot} ({(int)slot}): {actionInfo}";

        using(ImRaii.PushId($"slot_{slot}"))
        {
            ImGui.Text(slotDescription);

            ImBrio.VerticalPadding(2);

            float existingSpeed = _capability.GetSlotSpeed(slot);
            float newSpeed = existingSpeed;
            var speedLabel = global::Brio.Resources.Localize.Text("Slot Speed");
            ImGui.SetNextItemWidth(ImGui.CalcTextSize($"XXXXXXXXXXXXXXXXXi").X);
            if(ImGui.SliderFloat($"{speedLabel}", ref newSpeed, 0f, 5f))
                _capability.SetSlotSpeedOverride(slot, newSpeed);

            ImGui.SameLine();
            ImBrio.HorizontalPadding(4);

            if(ImBrio.FontIconButtonRight("reset", FontAwesomeIcon.Undo, 1, global::Brio.Resources.Localize.Text("Reset Speed"), _capability.HasSlotSpeedOverride(slot)))
                _capability.ResetSlotSpeedOverride(slot);

            ImGui.SameLine();

            var speed = _capability.GetSlotSpeed(slot);
            if(ImBrio.FontIconButtonRight("speed_pause", FontAwesomeIcon.PauseCircle, 2, global::Brio.Resources.Localize.Text("Pause"), speed > 0f))
                _capability.SetSlotSpeedOverride(slot, 0.0f);
        }
    }

    private void DrawOverallSpeed(bool drawAdvanced)
    {
        float existingSpeed = _capability.SpeedMultiplier;
        float newSpeed = existingSpeed;

        var speedLabel = global::Brio.Resources.Localize.Text("Speed");
        ImGui.SetNextItemWidth(drawAdvanced ? MaxItemWidth - ImGui.CalcTextSize("XXXX").X : MaxItemWidth);
        if(ImGui.SliderFloat($"###speed_slider", ref newSpeed, _delimitSpeed ? -5f : 0f, _delimitSpeed ? 10f : 5f))
            _capability.SetOverallSpeedOverride(newSpeed);

        if(drawAdvanced)
        {
            ImGui.SameLine();
            if(ImGui.Checkbox(global::Brio.Resources.Localize.Text("###delimit_speed"), ref _delimitSpeed))
                if(_delimitSpeed == false)
                {
                    _capability.ResetOverallSpeedOverride();
                }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip(global::Brio.Resources.Localize.Text("Delimit Speed"));
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(LabelStart);
        ImGui.Text(speedLabel);

        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight("speed_reset", FontAwesomeIcon.Undo, 1, global::Brio.Resources.Localize.Text("Reset Speed"), _capability.HasSpeedMultiplierOverride))
            _capability.ResetOverallSpeedOverride();

        ImGui.SameLine();

        if(ImBrio.FontIconButtonRight("speed_pause", FontAwesomeIcon.PauseCircle, 2, global::Brio.Resources.Localize.Text("Pause"), _capability.SpeedMultiplier != 0f))
        {
            _capability.SetOverallSpeedOverride(0f);
        }
    }

    private void DrawCutscene()
    {
        ImGui.Text(global::Brio.Resources.Localize.Text("Camera Path"));

        ImGui.SameLine();

        ImGui.InputText(string.Empty, ref _cameraPath, 260, ImGuiInputTextFlags.ReadOnly);

        ImGui.SameLine();

        if(ImGui.Button(global::Brio.Resources.Localize.Text("Browse")))
        {
            UIManager.Instance.FileDialogManager.OpenFileDialog("Browse for XAT Camera File", "XAT Camera File {.xcp}",
                (success, path) =>
                {
                    if(success)
                    {
                        _cameraPath = path[0];

                        string? folderPath = Path.GetDirectoryName(_cameraPath);
                        if(folderPath is not null)
                        {
                            _configService.Configuration.LastXATPath = folderPath;
                            _configService.Save();

                            _cutsceneManager.CameraPath = new XATCameraFile(new BinaryReader(File.OpenRead(_cameraPath)));
                        }
                    }
                    else
                    {
                        _cameraPath = string.Empty;
                        _cutsceneManager.CameraPath = null;
                    }
                }, 1, _configService.Configuration.LastXATPath, false);
        }

        ImGui.Separator();
        ImBrio.VerticalPadding(2);

        using(ImRaii.Disabled(string.IsNullOrEmpty(_cameraPath)))
        {
            ImGui.Checkbox(global::Brio.Resources.Localize.Text("Enable FOV"), ref _cutsceneManager.CameraSettings.EnableFOV);

            ImGui.Separator();
            ImBrio.VerticalPadding(2);

            ImGui.TextWrapped(global::Brio.Resources.Localize.Text("Disabling FOV will make for a less accurate Camera, but might provide for an easer way to support more character sizes without changing the Camera's Scale & Offset!"));

            ImGui.Separator();
            ImBrio.VerticalPadding(2);

            ImGui.InputFloat3(global::Brio.Resources.Localize.Text("Camera Scale"), ref _cutsceneManager.CameraSettings.Scale);
            ImGui.InputFloat3(global::Brio.Resources.Localize.Text("Camera Offset"), ref _cutsceneManager.CameraSettings.Offset);

            ImGui.Separator();
            ImBrio.VerticalPadding(2);

            ImGui.Checkbox(global::Brio.Resources.Localize.Text("Loop"), ref _cutsceneManager.CameraSettings.Loop);

            ImGui.Checkbox(global::Brio.Resources.Localize.Text("Hide Brio On Play  (Press 'Shift + B' to Stop Cutscene)"), ref _cutsceneManager.CloseWindowsOnPlay);

            ImGui.Separator();
            ImBrio.VerticalPadding(2);

            ImGui.Checkbox(global::Brio.Resources.Localize.Text("###delay_Start"), ref _cutsceneManager.DelayStart);
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip(global::Brio.Resources.Localize.Text("Start Delay"));

            ImGui.SameLine();
            ImGui.SetNextItemWidth(MaxItemWidth);

            using(ImRaii.Disabled(_cutsceneManager.DelayStart == false))
            {
                ImGui.InputInt($"###delay_Start_Chek", ref _cutsceneManager.DelayTime, 0, 0);
            }

            ImGui.SameLine();
            ImGui.SetCursorPosX(LabelStart);
            ImGui.Text(global::Brio.Resources.Localize.Text("Start Delay"));

            ImGui.Separator();
            ImBrio.VerticalPadding(2);

            ImGui.Checkbox(global::Brio.Resources.Localize.Text("Start All Actors Animations On Play"), ref _cutsceneManager.StartAllActorAnimationsOnPlay);

            using(ImRaii.Disabled(_cutsceneManager.StartAllActorAnimationsOnPlay == false))
            {
                ImGui.Checkbox(global::Brio.Resources.Localize.Text("###animation_delay_Start"), ref _cutsceneManager.DelayAnimationStart);
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip(global::Brio.Resources.Localize.Text("Animation Start Delay"));

                ImGui.SameLine();
                ImGui.SetNextItemWidth(MaxItemWidth);

                using(ImRaii.Disabled(_cutsceneManager.DelayAnimationStart == false))
                {
                    ImGui.InputInt($"###animation_delay_Start_Chek", ref _cutsceneManager.DelayAnimationTime, 0, 0);
                }

                ImGui.SameLine();
                ImGui.SetCursorPosX(LabelStart);
                ImGui.Text(global::Brio.Resources.Localize.Text("Animation Delay"));
            }

            ImGui.Separator();
            ImBrio.VerticalPadding(2);

            ImGui.TextWrapped(global::Brio.Resources.Localize.Text("The time-scale for the delay functions are in Milliseconds!"));
            ImGui.TextWrapped(global::Brio.Resources.Localize.Text("1000 Milliseconds = 1 Second"));

            ImGui.Separator();
            ImBrio.VerticalPadding(2);

            var isrunning = _cutsceneManager.IsRunning;
            using(ImRaii.Disabled(isrunning))
            {
                if(ImBrio.Button(global::Brio.Resources.Localize.Text("Play"), FontAwesomeIcon.Play, new Vector2(-1, 30), centerTest: true))
                {
                    _cutsceneManager.StartPlayback();
                }
            }

            ImBrio.VerticalPadding(2);

            using(ImRaii.Disabled(!isrunning))
            {
                if(ImBrio.Button(global::Brio.Resources.Localize.Text("Stop"), FontAwesomeIcon.Stop, new Vector2(-1, 30), centerTest: true))
                {
                    _cutsceneManager.StopPlayback();
                }
            }
        }
    }

    //

    public static void ApplyBaseOverride(ActionTimelineCapability cap, bool resetSpeed = false)
    {
        if(cap.SlotedBaseAnimation == 0 || cap.IsPaused)
            return;

        if(resetSpeed || cap.SpeedMultiplier == 0)
            cap.ResetOverallSpeedOverride();

        cap.PrepareAnimationContext((ushort)cap.SlotedBaseAnimation);
        cap.ApplyBaseOverride((ushort)cap.SlotedBaseAnimation, cap.DoBaseInterrupt);
    }
    public static void ApplyBlend(ActionTimelineCapability cap)
    {
        if(cap.SlotedBlendAnimation == 0 || cap.IsPaused)
            return;

        cap.BlendTimeline((ushort)cap.SlotedBlendAnimation);
    }
}
