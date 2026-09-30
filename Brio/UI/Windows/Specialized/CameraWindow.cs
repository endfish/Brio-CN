using Brio.Capabilities.Camera;
using Brio.Config;
using Brio.Entities;
using Brio.Entities.Camera;
using Brio.Game.Camera;
using Brio.Game.Cutscene;
using Brio.Game.GPose;
using Brio.UI.Controls.Editors;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System;
using System.Linq;

namespace Brio.UI.Windows.Specialized;

public class CameraWindow : Window, IDisposable
{
    private readonly EntityManager _entityManager;
    private readonly GPoseService _gPoseService;
    private readonly VirtualCameraManager _virtualCameraService;
    private readonly CutsceneManager _cutsceneManager;
    private readonly ConfigurationService _configService;

    public CameraWindow(EntityManager entityManager, GPoseService gPoseService, CutsceneManager cutsceneManager, ConfigurationService configService, VirtualCameraManager virtualCameraService) : base(global::Brio.Resources.Localize.Format("{0} - CAMERA###brio_camera_window", Brio.Name))
    {
        Namespace = "brio_camera_namespace";

        _entityManager = entityManager;
        _gPoseService = gPoseService;
        _virtualCameraService = virtualCameraService;
        _cutsceneManager = cutsceneManager;
        _configService = configService;

        WindowSizeConstraints constraints = new()
        {
            MinimumSize = new(280, 300),
            MaximumSize = new(385, 485)
        };
        this.SizeConstraints = constraints;

        this.AllowBackgroundBlur = false;

        _gPoseService.OnGPoseStateChange += OnGPoseStateChange;
    }

    public override void Draw()
    {
        ImBrio.BlurWindow();

        ImBrio.VerticalPadding(2);

        ImGui.Text(global::Brio.Resources.Localize.Text("Select Camera to Edit:"));
        ImBrio.CenterNextElementWithPadding(15);
        using(ImRaii.Disabled(_virtualCameraService.CamerasCount == 0))
            if(ImGui.BeginCombo(global::Brio.Resources.Localize.Text("###setCamera"), $"{_virtualCameraService.SelectedCameraEntity?.FriendlyName}"))
            {
                var list = _virtualCameraService.SpawnedCameraEntities;
                list.Add(_virtualCameraService.GetDefaultCamera()!);
                foreach(var value in list)
                {
                    if(ImGui.Selectable(global::Brio.Resources.Localize.Format("Camera: [ {0} ] [ {1} ]", value.FriendlyName, global::Brio.Resources.Localize.Text(value.CameraType.ToString()))))
                    {
                        _virtualCameraService.SelectedCameraEntity = value;
                    }
                }
                ImGui.EndCombo();
            }

        ImBrio.AttachToolTip(global::Brio.Resources.Localize.Text("Current Camera"));


        if(_virtualCameraService.SelectedCameraEntity is null || _virtualCameraService.SelectedCameraEntity.IsAttached == false)
        {
            _virtualCameraService.SelectedCameraEntity = _virtualCameraService.CamerasCount > 0
                ? _virtualCameraService.SpawnedCameraEntities.First()
                : null;
        }

        ImBrio.SeparatorText(global::Brio.Resources.Localize.Format("Camera - [{0}]", _virtualCameraService.SelectedCameraEntity?.FriendlyName));

        //
        // Hedder

        _virtualCameraService.SelectedCameraEntity ??= _virtualCameraService.GetDefaultCamera();

        if(!_virtualCameraService!.SelectedCameraEntity!.TryGetCapability<BrioCameraCapability>(out var camBrioCap))
        {
            return;
        }

        //
        // Body

        switch(camBrioCap.CameraEntity.CameraType)
        {
            case CameraType.Free:
                WindowName = global::Brio.Resources.Localize.Format("{0} - CAMERA (FREE CAM)###brio_camera_window", Brio.Name);
                CameraEditor.DrawFreeCam("camera_widget_editor", camBrioCap);
                break;
            case CameraType.Cutscene:
                WindowName = global::Brio.Resources.Localize.Format("{0} - CAMERA (CUTSCENE)###brio_camera_window", Brio.Name);
                CameraEditor.DrawBrioCutscene("camera_widget_editor", camBrioCap, _cutsceneManager, _configService);
                break;
            case CameraType.Game:
            case CameraType.Default:
                WindowName = global::Brio.Resources.Localize.Format("{0} - CAMERA (BRIO CAM)###brio_camera_window", Brio.Name);
                CameraEditor.DrawBrioCam("camera_widget_editor", camBrioCap);
                break;
        }


    }

    private void OnGPoseStateChange(bool newState)
    {
        if(!newState)
            IsOpen = false;
    }

    public void Dispose()
    {
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChange;
    }
}
