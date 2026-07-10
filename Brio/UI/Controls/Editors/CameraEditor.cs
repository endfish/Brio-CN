using Brio.Capabilities.Camera;
using Brio.Config;
using Brio.Core;
using Brio.Entities.Actor;
using Brio.Entities.World;
using Brio.Entities.WorldObjects;
using Brio.Files;
using Brio.Game.Actor.Extensions;
using Brio.Game.Cutscene;
using Brio.Game.WorldObjects;
using Brio.Input;
using Brio.Services;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System.IO;
using System.Numerics;

namespace Brio.UI.Controls.Editors;

public static class CameraEditor
{
    public unsafe static void DrawFreeCam(string id, BrioCameraCapability capability)
    {
        var camera = capability.VirtualCamera;
        bool anyActiveThisFrame = false;

        using(ImRaii.PushId(id))
        {
            using(ImRaii.Disabled(!capability.IsAllowed))
            {
                var width = -ImGui.CalcTextSize("XxxxxX").X;

                if(ImBrio.ToggelFontIconButton("portraitMode", FontAwesomeIcon.Mobile, new Vector2(0, 0), camera.IsPortraitMode, tooltip: global::Brio.Resources.Localize.Text("Portrait Mode")))
                {
                    camera.TogglePortraitMode();
                    capability.Snapshot();
                }

                ImBrio.VerticalSeparator(24);

                if(ImBrio.ToggelFontIconButton("save", FontAwesomeIcon.BookBookmark, new Vector2(25, 0), false, tooltip: global::Brio.Resources.Localize.Text("Camera Presets")))
                {
                    ImGui.OpenPopup($"DrawPresetPopup");
                }

                FileUIHelpers.DrawPresetPopup(PresetType.Camera, capability.Entity);

                ImBrio.VerticalSeparator(24);

                if(ImBrio.FontIconButton("undo", FontAwesomeIcon.Reply, global::Brio.Resources.Localize.Text("Undo"), capability.CanUndo) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Undo) && capability.CanUndo))
                {
                    capability.Undo();
                }

                ImGui.SameLine();

                if(ImBrio.FontIconButton("redo", FontAwesomeIcon.Share, global::Brio.Resources.Localize.Text("Redo"), capability.CanRedo) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Redo) && capability.CanRedo))
                {
                    capability.Redo();
                }

                ImBrio.VerticalSeparator(24);

                if(ImBrio.ToggelFontIconButton("EnableMovement", FontAwesomeIcon.Walking, new Vector2(25, 0), camera.FreeCamValues.IsMovementEnabled, tooltip: camera.FreeCamValues.IsMovementEnabled ?
                    "Disable Free-Cam Movement" : "Enable Free-Cam Movement"))
                {
                    camera.FreeCamValues.IsMovementEnabled = !camera.FreeCamValues.IsMovementEnabled;
                }

                ImGui.SameLine();

                using(ImRaii.Disabled(camera.FreeCamValues.IsMovementEnabled == false))
                {
                    if(ImBrio.ToggelFontIconButton("LateralMovement", FontAwesomeIcon.SolarPanel, new Vector2(25, 0), camera.FreeCamValues.Move2D, tooltip: global::Brio.Resources.Localize.Text("Lateral Movement")))
                    {
                        camera.FreeCamValues.Move2D = !camera.FreeCamValues.Move2D;
                        capability.Snapshot();
                    }
                }

                //

                using(ImRaii.Disabled(camera.Position == camera.SpawnPosition))
                    if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Transform"), FontAwesomeIcon.Undo, global::Brio.Resources.Localize.Text("Reset Transform")))
                    {
                        camera.Position = camera.SpawnPosition;
                        capability.Snapshot();
                    }

                ImBrio.VerticalPadding(2);

                Vector3 pos = camera.Position;
                (var panyActive1, var pdidChange1) = ImBrio.DragFloat3($"###_transformPosition_2", ref pos, 0.001f, FontAwesomeIcon.ArrowsUpDownLeftRight, "Position Offset", enableExpanded: false);
                if(pdidChange1)
                    camera.Position = pos;
                anyActiveThisFrame |= panyActive1;

                //

                ImBrio.Icon(FontAwesomeIcon.ArrowsSpin);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(width);

                var rotation = camera.Rotation;
                (var panyActive2, var pdidChange2) = ImBrio.DragFloat2V3("###Pan", ref rotation, -360, 360, false, 0.001f, Vector2.Zero, "Pan");
                if(pdidChange2)
                    camera.Rotation = rotation;
                anyActiveThisFrame |= panyActive2;

                //

                DrawCameraActorSelect(capability);

                //

                using(ImRaii.Disabled(!camera.IsOverridden))
                    if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Properties"), FontAwesomeIcon.Undo, global::Brio.Resources.Localize.Text("Reset to Default")))
                    {
                        camera.FoV = 0f;
                        camera.PivotRotation = 0;
                        camera.FreeCamValues.MovementSpeed = capability._configurationService.Configuration.Interface.DefaultFreeCameraMovementSpeed;
                        camera.FreeCamValues.MouseSensitivity = capability._configurationService.Configuration.Interface.DefaultFreeCameraMouseSensitivity;
                        capability.Snapshot();
                    }

                //

                ImBrio.Icon(FontAwesomeIcon.Panorama);
                ImGui.SameLine();
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("FOV"));

                float fov = camera.FoV;
                ImBrio.CenterNextElementWithPadding(5);
                (var fovDidChange, var fovAnyActive) = ImBrio.SliderAngle("###fov", ref fov, -44, 120, "%.2f", ImGuiSliderFlags.AlwaysClamp, toolTip: "FOV");
                if(fovDidChange)
                    camera.FoV = fov;
                anyActiveThisFrame |= fovAnyActive;

                ImGui.SameLine();

                if(ImBrio.FontIconButtonRight("resetFoV", FontAwesomeIcon.Undo, 1f, global::Brio.Resources.Localize.Text("Reset"), fov != 0))
                {
                    camera.FoV = 0f;
                    capability.Snapshot();
                }

                //

                ImBrio.Icon(FontAwesomeIcon.CameraRotate);
                ImGui.SameLine();
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Pivot"));

                ImBrio.CenterNextElementWithPadding(5);

                float pivoRotation = camera.PivotRotation;
                (var pivotDidChange, var pivotAnyActive) = ImBrio.SliderAngle("###rotation", ref pivoRotation, -180, 180, "%.2f", ImGuiSliderFlags.AlwaysClamp, toolTip: "Pivot");
                if(pivotDidChange)
                    camera.PivotRotation = pivoRotation;
                anyActiveThisFrame |= pivotAnyActive;

                ImGui.SameLine();

                if(ImBrio.FontIconButtonRight("resetRotation", FontAwesomeIcon.Undo, 1f, global::Brio.Resources.Localize.Text("Reset"), pivoRotation != 0))
                {
                    camera.PivotRotation = 0;
                    capability.Snapshot();
                }

                //

                ImBrio.Icon(FontAwesomeIcon.Walking);
                ImGui.SameLine();
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Movement Speed"));

                ImBrio.CenterNextElementWithPadding(5);

                float moveSpeed = camera.FreeCamValues.MovementSpeed;
                (var moveSpeedDidChange, var moveSpeedAnyActive) = ImBrio.SliderFloat("##MovementSpeed", ref moveSpeed, 0.005f, 0.3f, "%.4f", ImGuiSliderFlags.AlwaysClamp, step: 0.001f, toolTip: "Movement Speed");
                if(moveSpeedDidChange)
                    camera.FreeCamValues.MovementSpeed = moveSpeed;
                anyActiveThisFrame |= moveSpeedAnyActive;

                ImGui.SameLine();

                if(ImBrio.FontIconButtonRight("resetMovementSpeed", FontAwesomeIcon.Undo, 1f, global::Brio.Resources.Localize.Text("Reset Movement Speed"), moveSpeed != capability._configurationService.Configuration.Interface.DefaultFreeCameraMovementSpeed))
                {
                    camera.FreeCamValues.MovementSpeed = capability._configurationService.Configuration.Interface.DefaultFreeCameraMovementSpeed;
                    capability.Snapshot();
                }

                //

                ImBrio.Icon(FontAwesomeIcon.Mouse);
                ImGui.SameLine();
                ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Mouse Sensitivity"));

                ImBrio.CenterNextElementWithPadding(5);

                float mouseSpeed = camera.FreeCamValues.MouseSensitivity;
                (var mouseSpeedDidChange, var mouseSpeedAnyActive) = ImBrio.SliderFloat("##MouseSensitivity", ref mouseSpeed, 0.001f, 0.2f, "%.4f", ImGuiSliderFlags.AlwaysClamp, step: 0.001f, toolTip: "Mouse Sensitivity");
                if(mouseSpeedDidChange)
                    camera.FreeCamValues.MouseSensitivity = mouseSpeed;
                anyActiveThisFrame |= mouseSpeedAnyActive;

                ImGui.SameLine();

                if(ImBrio.FontIconButtonRight("resetMouseSensitivity", FontAwesomeIcon.Undo, 1f, global::Brio.Resources.Localize.Text("Reset Mouse Sensitivity"), mouseSpeed != capability._configurationService.Configuration.Interface.DefaultFreeCameraMouseSensitivity))
                {
                    camera.FreeCamValues.MouseSensitivity = capability._configurationService.Configuration.Interface.DefaultFreeCameraMouseSensitivity;
                    capability.Snapshot();
                }

                ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Advanced"));

                var delimit = camera.FreeCamValues.DelimitAngle;
                if(ImGui.Checkbox(global::Brio.Resources.Localize.Text("Delimit Camera Angle"), ref delimit))
                {
                    camera.FreeCamValues.DelimitAngle = delimit;
                    capability.Snapshot();
                }
            }
        }

        capability.TrackEdit(anyActiveThisFrame);
    }

    public unsafe static void DrawBrioCam(string id, BrioCameraCapability capability)
    {
        var camera = capability.VirtualCamera;
        bool anyActiveThisFrame = false;

        using(ImRaii.PushId(id))
        {
            using(ImRaii.Disabled(!capability.IsAllowed))
            {
                if(camera is not null)
                {
                    var width = -ImGui.CalcTextSize("XxxxxX").X;

                    if(ImBrio.ToggelFontIconButton("portraitMode", FontAwesomeIcon.Mobile, new Vector2(0, 0), camera.IsPortraitMode, tooltip: global::Brio.Resources.Localize.Text("Portrait Mode")))
                    {
                        camera.TogglePortraitMode();
                        capability.Snapshot();
                    }

                    ImBrio.VerticalSeparator(24);

                    if(ImBrio.ToggelFontIconButton("save", FontAwesomeIcon.BookBookmark, new Vector2(25, 0), camera.FreeCamValues.Move2D, tooltip: global::Brio.Resources.Localize.Text("Presets")))
                    {
                        ImGui.OpenPopup("DrawPresetPopup");
                    }

                    FileUIHelpers.DrawPresetPopup(PresetType.Camera, capability.Entity);

                    ImBrio.VerticalSeparator(24);

                    if(ImBrio.FontIconButton("undo", FontAwesomeIcon.Reply, global::Brio.Resources.Localize.Text("Undo"), capability.CanUndo) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Undo) && capability.CanUndo))
                    {
                        capability.Undo();
                    }

                    ImGui.SameLine();

                    if(ImBrio.FontIconButton("redo", FontAwesomeIcon.Share, global::Brio.Resources.Localize.Text("Redo"), capability.CanRedo) || (InputManagerService.ActionKeysPressedLastFrame(InputAction.Posing_Redo) && capability.CanRedo))
                    {
                        capability.Redo();
                    }

                    //

                    using(ImRaii.Disabled(camera.PositionOffset == Vector3.Zero))
                        if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Transform"), FontAwesomeIcon.Undo, global::Brio.Resources.Localize.Text("Reset Transform")))
                        {
                            camera.PositionOffset = Vector3.Zero;
                            capability.Snapshot();
                        }

                    ImBrio.VerticalPadding(2);

                    using(ImRaii.Disabled(true))
                    {
                        var position = camera.RealPosition;
                        (var panyActive, var pdidChange) = ImBrio.DragFloat3($"###_transformPosition_1", ref position, 0.01f, FontAwesomeIcon.ArrowsToCircle, "Absolute Position", enableExpanded: false);
                    }

                    ImBrio.VerticalPadding(2);

                    {
                        Vector3 pos = camera.PositionOffset;
                        (var panyActive, var pdidChange) = ImBrio.DragFloat3($"###_transformPosition_2", ref pos, 0.001f, FontAwesomeIcon.ArrowsUpDownLeftRight, "Position Offset", enableExpanded: false);

                        if(pdidChange)
                        {
                            camera.PositionOffset = pos;
                        }
                        anyActiveThisFrame |= panyActive;
                    }

                    //

                    DrawCameraActorSelect(capability);

                    //

                    using(ImRaii.Disabled(!camera.IsOverridden))
                        if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Properties"), FontAwesomeIcon.Undo, global::Brio.Resources.Localize.Text("Reset to Default")))
                        {
                            camera.Zoom = 2.5f;
                            camera.FoV = 0f;
                            camera.PivotRotation = 0;
                            capability.Snapshot();
                        }

                    //

                    ImBrio.Icon(FontAwesomeIcon.ArrowsUpDownLeftRight);
                    ImGui.SameLine();
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Zoom"));

                    float zoom = camera.Zoom;
                    ImBrio.CenterNextElementWithPadding(5);
                    (var zoomDidChange, var zoomAnyActive) = ImBrio.SliderFloat("###zoom", ref zoom, camera.BrioCamera->Camera.MaxDistance, camera.BrioCamera->Camera.MinDistance, "%.2f", ImGuiSliderFlags.AlwaysClamp, toolTip: "Zoom");
                    if(zoomDidChange)
                        camera.Zoom = zoom;
                    anyActiveThisFrame |= zoomAnyActive;

                    ImGui.SameLine();

                    if(ImBrio.FontIconButtonRight("resetZoom", FontAwesomeIcon.Undo, 1f, global::Brio.Resources.Localize.Text("Reset"), zoom != 2.5))
                    {
                        camera.Zoom = 2.5f;
                        capability.Snapshot();
                    }

                    //

                    ImBrio.Icon(FontAwesomeIcon.Panorama);
                    ImGui.SameLine();
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("FOV"));

                    float fov = camera.FoV;
                    ImBrio.CenterNextElementWithPadding(5);
                    (var fovDidChange, var fovAnyActive) = ImBrio.SliderAngle("###fov", ref fov, -44, 120, "%.2f", ImGuiSliderFlags.AlwaysClamp, toolTip: "FOV");
                    if(fovDidChange)
                        camera.FoV = fov;
                    anyActiveThisFrame |= fovAnyActive;

                    ImGui.SameLine();

                    if(ImBrio.FontIconButtonRight("resetFoV", FontAwesomeIcon.Undo, 1f, global::Brio.Resources.Localize.Text("Reset"), fov != 0))
                    {
                        camera.FoV = 0f;
                        capability.Snapshot();
                    }

                    //

                    ImBrio.Icon(FontAwesomeIcon.CameraRotate);
                    ImGui.SameLine();
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Pivot"));

                    ImBrio.CenterNextElementWithPadding(5);

                    float pivotRotation = camera.PivotRotation;
                    (var pivotDidChange, var pivotAnyActive) = ImBrio.SliderAngle("###rotation", ref pivotRotation, -180, 180, "%.2f", toolTip: "Pivot");
                    if(pivotDidChange)
                        camera.PivotRotation = pivotRotation;
                    anyActiveThisFrame |= pivotAnyActive;

                    ImGui.SameLine();

                    if(ImBrio.FontIconButtonRight("resetRotation", FontAwesomeIcon.Undo, 1f, global::Brio.Resources.Localize.Text("Reset"), pivotRotation != 0))
                    {
                        camera.PivotRotation = 0;
                        capability.Snapshot();
                    }

                    ///

                    ImBrio.VerticalPadding(2);
                    ImGui.Separator();
                    ImBrio.VerticalPadding(2);

                    ImBrio.Icon(FontAwesomeIcon.UsersViewfinder);
                    ImGui.SameLine();
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Pan"));

                    Vector2 pan = camera.Pan;
                    ImBrio.CenterNextElementWithPadding(5);
                    if(ImGui.DragFloat2(global::Brio.Resources.Localize.Text("###pan"), ref pan, 0.001f))
                        camera.Pan = pan;
                    anyActiveThisFrame |= ImGui.IsItemActive();
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Pan"));

                    //

                    ImBrio.Icon(FontAwesomeIcon.ArrowsSpin);
                    ImGui.SameLine();
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Angle"));

                    Vector2 angle = camera.Angle;
                    ImBrio.CenterNextElementWithPadding(5);
                    if(ImGui.DragFloat2(global::Brio.Resources.Localize.Text("###angle"), ref angle, 0.001f))
                        camera.Angle = angle;
                    anyActiveThisFrame |= ImGui.IsItemActive();
                    ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Angle"));

                    //

                    ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Advanced"));

                    var disable = camera.DisableCollision;
                    if(ImGui.Checkbox(global::Brio.Resources.Localize.Text("Disable Collision"), ref disable))
                    {
                        camera.DisableCollision = disable;
                    }

                    ImGui.SameLine();

                    var delimit = camera.DelimitCamera;
                    if(ImGui.Checkbox(global::Brio.Resources.Localize.Text("Delimit Camera"), ref delimit))
                    {
                        camera.DelimitCamera = delimit;
                    }
                }
            }
        }

        capability.TrackEdit(anyActiveThisFrame);
    }


    public static unsafe void DrawCameraActorSelect(BrioCameraCapability capability)
    {
        var camera = capability.VirtualCamera;

        using(ImRaii.Disabled(camera.IsSelectingActor))
            if(ImBrio.SeparatorTextButton(global::Brio.Resources.Localize.Text("Target Entity"), FontAwesomeIcon.Undo, global::Brio.Resources.Localize.Text("Target Entity")))
            {
                camera.TargetOffset = new Vector3(0);
                camera.SelectedActorName = "Select an actor to track";
                capability.Snapshot();
            }

        using(ImRaii.Disabled(capability._entityManager.SelectedEntity is not ActorEntity))
            if(ImBrio.FontIconButton("recenter_on_selected", FontAwesomeIcon.Bullseye, global::Brio.Resources.Localize.Text("Recenter on Selected Actor")))
            {
                var entity = capability._entityManager.SelectedEntity;
                if(entity is ActorEntity actor)
                {
                    capability.VirtualCamera.SelectedActorName = $"Selected: [ {actor.FriendlyName} ]";
                    camera.TargetOffset = (actor.GameObject.GetDrawObject<DrawObject>()->Object.Position - ((GameObject*)actor.GameObject.Address)->Position);
                    capability.Snapshot();
                }
            }

        ImGui.SameLine();

        float btnWidth = ImGui.GetFrameHeight();

        ImGui.SetNextItemWidth(-float.Epsilon);
        if(ImGui.BeginCombo($"###CameraContainerActorsWidget_{capability.Entity.Id}_list", capability.VirtualCamera.SelectedActorName))
        {
            foreach(var value in capability._entityManager.TryGetAllTransformableActors())
            {
                if(ImGui.Selectable($"[ {value.FriendlyName} ]"))
                {
                    camera.TargetOffset = GetEntityDrawOffset(value);
                    capability.Snapshot();
                }
            }
            ImGui.EndCombo();
        }
    }

    // TODO FIX (ken) this does not yet work
    private static unsafe Vector3 GetEntityDrawOffset(TransformableEntity entity)
    {
        if(entity is ActorEntity actorEntity)
            return actorEntity.GameObject.GetDrawObject<DrawObject>()->Object.Position - ((GameObject*)actorEntity.GameObject.Address)->Position;

        if(entity is WorldObjectEntity worldObjectEntity && worldObjectEntity.GameBgObject.ObjectType == WorldObjectType.BgObject)
        {
            var bgo = (BgObject*)worldObjectEntity.GameBgObject.Address;

            if(bgo != null)
                return bgo->DrawObject.Object.Position - bgo->Position;
        }

        if(entity is LightEntity lightEntity)
        {
            if(lightEntity.GameLight != null)
                return lightEntity.GameLight.GameLight->DrawObject.Object.Position - lightEntity.GameLight.GameLight->Transform.Position;
        }

        return entity.Transform.Position;
    }

    //
    private static float MaxItemWidth => ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("XXXXXXXXXXXXXXXXXX").X;
    private static float LabelStart => MaxItemWidth + ImGui.GetCursorPosX() + (ImGui.GetStyle().FramePadding.X * 2f);

    public unsafe static void DrawBrioCutscene(string id, BrioCameraCapability _cameraCapability, CutsceneManager _cutsceneManager, ConfigurationService _configService)
    {
        var cameraValues = _cameraCapability.CameraEntity.VirtualCamera.CutsceneCamValues;

        ImGui.Text(global::Brio.Resources.Localize.Text("Camera Path "));

        ImGui.InputText(string.Empty, ref cameraValues.CameraPath, 0, ImGuiInputTextFlags.ReadOnly);

        ImGui.SameLine();

        if(ImGui.Button(global::Brio.Resources.Localize.Text("Browse")))
        {
            UIManager.Instance.FileDialogManager.OpenFileDialog("Browse for XAT Camera File", "XAT Camera File {.xcp}",
                (success, path) =>
                {
                    if(success)
                    {
                        cameraValues.CameraPath = path[0];

                        string? folderPath = Path.GetDirectoryName(cameraValues.CameraPath);
                        if(folderPath is not null)
                        {
                            _configService.Configuration.LastXATPath = folderPath;
                            _configService.Save();

                            _cutsceneManager.CameraPath = new XATCameraFile(new BinaryReader(File.OpenRead(cameraValues.CameraPath)));
                        }
                    }
                    else
                    {
                        cameraValues.CameraPath = string.Empty;
                        _cutsceneManager.CameraPath = null;
                    }
                }, 1, _configService.Configuration.LastXATPath, false);
        }

        ImGui.Separator();

        using(ImRaii.Disabled(string.IsNullOrEmpty(cameraValues.CameraPath)))
        {
            ImGui.Checkbox(global::Brio.Resources.Localize.Text("Enable FOV"), ref _cutsceneManager.CameraSettings.EnableFOV);

            ImGui.Separator();

            ImGui.Text(global::Brio.Resources.Localize.Text("Disabling FOV will make for a less accurate Camera, but might"));
            ImGui.Text(global::Brio.Resources.Localize.Text("provide for an easer way to support more character sizes without"));
            ImGui.Text(global::Brio.Resources.Localize.Text("changing the Camera's Scale & Offset!"));

            ImGui.Separator();

            ImGui.InputFloat3("Camera Scale", ref _cutsceneManager.CameraSettings.Scale);
            ImGui.InputFloat3("Camera Offset", ref _cutsceneManager.CameraSettings.Offset);

            ImGui.Separator();

            ImGui.Checkbox(global::Brio.Resources.Localize.Text("Loop"), ref _cutsceneManager.CameraSettings.Loop);

            ImGui.Checkbox(global::Brio.Resources.Localize.Text("Hide Brio On Play  (Press 'Shift + B' to Stop Cutscene)"), ref _cutsceneManager.CloseWindowsOnPlay);

            ImGui.Checkbox(global::Brio.Resources.Localize.Text("###delay_Start"), ref _cutsceneManager.DelayStart);
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Start Delay");

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

            ImGui.Checkbox(global::Brio.Resources.Localize.Text("Start All Actors Animations On Play"), ref _cutsceneManager.StartAllActorAnimationsOnPlay);

            using(ImRaii.Disabled(_cutsceneManager.StartAllActorAnimationsOnPlay == false))
            {
                ImGui.Checkbox(global::Brio.Resources.Localize.Text("###animation_delay_Start"), ref _cutsceneManager.DelayAnimationStart);
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip("Animation Start Delay");

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

            ImGui.Text(global::Brio.Resources.Localize.Text("The time-scale for the delay functions are in Milliseconds!"));
            ImGui.Text(global::Brio.Resources.Localize.Text("1000 Milliseconds = 1 Second"));

            ImGui.Separator();

            var isrunning = _cutsceneManager.IsRunning;
            using(ImRaii.Disabled(isrunning))
            {
                if(ImGui.Button(global::Brio.Resources.Localize.Text("Play")))
                {
                    _cutsceneManager.StartPlayback();
                }
            }

            ImGui.SameLine();

            using(ImRaii.Disabled(!isrunning))
            {
                if(ImGui.Button(global::Brio.Resources.Localize.Text("Stop")))
                {
                    _cutsceneManager.StopPlayback();
                }
            }
        }
    }
}
