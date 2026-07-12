using Brio.Capabilities.Core;
using Brio.Entities.Core;
using Brio.UI.Controls.Stateless;
using Brio.UI.Theming;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Linq;

namespace Brio.UI.Widgets.Core;

public class EntityManagerWidget(EntitManagerCapability capability) : Widget<EntitManagerCapability>(capability)
{
    public override string HeaderName => "Multi-Selection";

    public override WidgetFlags Flags => Capability.Entity.EntityManager.SelectedEntities.Count > 1 ?
        WidgetFlags.DrawQuickIcons | WidgetFlags.DrawPopup | WidgetFlags.DrawBody | WidgetFlags.DefaultOpen :
        WidgetFlags.DrawQuickIcons | WidgetFlags.DrawPopup;

    public override void DrawBody()
    {
        if(Capability.Entity.EntityManager.SelectedEntities.Count > 1)
        {
            ImBrio.VerticalPadding(3);

            ImGui.AlignTextToFramePadding();
            using(ImRaii.PushColor(ImGuiCol.Text, ThemeManager.CurrentTheme.Accent.AccentColor))
                ImGui.Text($"{Capability.Entity.EntityManager.SelectedEntities.Count} Selected");

            ImBrio.VerticalPadding(7);

            ImBrio.SeparatorText(global::Brio.Resources.Localize.Text("Transform"));
            Capability.DrawMultiTransform();

            ImBrio.VerticalPadding(10);
        }
    }

    public override void DrawPopup()
    {
        if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Destroy All...###containerwidgetpopup_destroy")))
        {
            using(ImRaii.Disabled(Capability.HasFolders == false))
            {
                if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Folders###entitymanager_destroyall_folders")))
                {
                    if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Return Children to Root###entitymanager_destroyall_folders_return")))
                    {
                        if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Confirm###entitymanager_destroyall_folders_return_confirm")))
                            Capability.ReturnAllFolderChildren();

                        ImGui.EndMenu();
                    }

                    if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("Destroy All Children###entitymanager_destroyall_folders_destroy")))
                    {
                        if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Confirm###entitymanager_destroyall_folders_destroy_confirm")))
                            Capability.DestroyAllFolderChildren();

                        ImGui.EndMenu();
                    }

                    ImGui.EndMenu();
                }
            }

            using(ImRaii.Disabled(Capability.HasWorldObjects == false))
            {
                if(ImGui.BeginMenu(global::Brio.Resources.Localize.Text("World Objects###entitymanager_destroyall_worldobjects")))
                {
                    if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Confirm Destruction###entitymanager_destroyall_worldobjects_confirm")))
                        Capability.DestroyAllWorldObjects();

                    ImGui.EndMenu();
                }
            }

            ImGui.EndMenu();
        }
    }

    public override void DrawQuickIcons()
    {
        using(ImRaii.Disabled(Capability.CanControlCharacters is false))
        {
            bool hasSelection = Capability.Entity.EntityManager.SelectedEntity != null;

            if(ImBrio.FontIconButton("Manager_clone", FontAwesomeIcon.Clone, global::Brio.Resources.Localize.Text("Clone Selected"), hasSelection))
            {
                Capability.CloneSelected();
            }

            ImGui.SameLine();

            if(ImBrio.FontIconButton("Manager_selectinhierarchy", FontAwesomeIcon.CheckSquare, global::Brio.Resources.Localize.Text("Select All")))
            {
                Capability.SelectAllInHierarchy();
            }

            ImBrio.VerticalSeparator(24, 1);

            if(ImBrio.HoldButton("manager_destroyall", global::Brio.Resources.Localize.Text(""), FontAwesomeIcon.Bomb, 1f, new(40, 0), centerTest: true, tooltip: global::Brio.Resources.Localize.Text("[HOLD TO DESTROY ALL]"), onlyIcon: true))
            {
                Capability.DestroyAllSelected();
            }

            ImBrio.VerticalSeparator(24, 1);

            if(ImBrio.FontIconButton("Manager_move", FontAwesomeIcon.FolderTree, global::Brio.Resources.Localize.Text("Move to Folder..."), hasSelection))
            {
                ImGui.OpenPopup("manager_move_to_folder_popup");
            }

            using(var popup = ImRaii.Popup("manager_move_to_folder_popup"))
            {
                if(popup.Success)
                {
                    foreach(var folder in Capability.Entity.Children.OfType<FolderEntity>().Where(f => f.IsEditable))
                    {
                        if(ImGui.MenuItem($"{folder.FriendlyName}###manager_move_to_folder_{folder.Id}"))
                            Capability.MoveSelectedToFolder(folder);
                    }

                    ImGui.Separator();

                    if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("New Folder...###manager_move_to_new_folder")))
                        Capability.MoveSelectedToNewFolder();
                }
            }

            ImGui.SameLine();

            if(ImBrio.FontIconButton("Manager_folderoptions", FontAwesomeIcon.EllipsisV, global::Brio.Resources.Localize.Text("Folder Options"), Capability.HasFolders))
            {
                ImGui.OpenPopup("manager_folder_options_popup");
            }

            using(var popup = ImRaii.Popup("manager_folder_options_popup"))
            {
                if(popup.Success)
                {
                    if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Return All Children to Entity Manager###manager_folderoptions_return")))
                        Capability.ReturnAllFolderChildren();

                    if(ImGui.MenuItem(global::Brio.Resources.Localize.Text("Destroy All Folders + Children###manager_folderoptions_destroy")))
                        Capability.DestroyAllFolderChildren();
                }
            }
        }
    }
}
