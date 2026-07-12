using Brio.Capabilities.Folder;
using Brio.UI.Widgets.Core;
using Dalamud.Bindings.ImGui;

namespace Brio.UI.Widgets.Folder;

public class FolderWidget(FolderCapability capability) : Widget<FolderCapability>(capability)
{
    public override string HeaderName => "Folder";
    public override WidgetFlags Flags => WidgetFlags.DrawPopup;

    public override void DrawPopup()
    {
        if(ImGui.MenuItem(global::Brio.Resources.Localize.Format("Rename {0}###folder_rename", Capability.FolderEntity.FriendlyName)))
        {
            ImGui.CloseCurrentPopup();
            ModalManager.Instance.OpenRenameModal(Capability.FolderEntity);
        }

        string visLabel = Capability.FolderEntity.AreChildrenHidden
            ? "Show All Children###folder_visibility"
            : "Hide All Children###folder_visibility";

        if(ImGui.MenuItem(visLabel))
            Capability.ToggleChildrenVisibility();

        ImGui.Separator();

        if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Delete Folder###folder_delete")))
        {
            if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Return Children to Parent###folder_delete_return")))
            {
                if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Confirm###folder_delete_return_confirm")))
                    Capability.DeleteFolderReturnChildren();
                ImGui.EndMenu();
            }

            if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Delete All Children###folder_delete_children")))
            {
                if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Confirm###folder_delete_children_confirm")))
                    Capability.DeleteFolderDestroyChildren();
                ImGui.EndMenu();
            }

            ImGui.EndMenu();
        }
    }
}
