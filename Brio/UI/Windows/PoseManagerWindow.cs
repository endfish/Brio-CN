using Brio.Resources;
using Brio.Services.Poses;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Brio.UI.Windows;

public sealed class PoseManagerWindow(PoseLibraryService library, PoseAssetActions actions) : Window(Localize.Text("Pose Manager###brio_pose_manager"))
{
    private string _search = "", _group = "", _name = "", _tags = "", _editGroup = "", _status = "";
    private int _genderFilter, _kindFilter;
    private bool _favorites, _openSave;
    private string? _selectedPath;
    private PoseAssetEntry? _selected;
    private PoseAssetMetadata _edit = new();
    private PoseAssetKind _saveKind;
    private readonly PosePreviewView _view = new();
    private Task? _import;
    private int _importFailures;
    public static readonly string[] KindNames = ["Body", "Expression", "Hands", "Complete pose"];
    public static readonly string[] GenderNames = ["Unspecified", "Male", "Female", "Unisex"];

    public override void OnOpen() => _ = library.ScanAsync();
    public override void PreDraw()
    {
        WindowName = Localize.Text("Pose Manager###brio_pose_manager");
        ImGui.SetNextWindowSize(new(1050, 680), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new(750, 450), new(2500, 1800));
        base.PreDraw();
    }
    public void RequestSave(PoseAssetKind kind)
    {
        IsOpen = true; _saveKind = kind; _openSave = true;
        _name = Localize.Text("Pose") + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss");
    }

    public override void Draw()
    {
        if(_import is { IsCompleted: true })
        {
            _status = _import.IsFaulted || _importFailures > 0 ? "Some pose files could not be imported. See the log for details." : "Pose operation completed.";
            if(_import.IsFaulted) Brio.Log.Error(_import.Exception!, "Pose folder import failed.");
            _import = null;
        }
        if(ImGui.Button(Localize.Text("Save current pose###pm_save"))) RequestSave(PoseAssetKind.Body);
        ImGui.SameLine();
        if(ImGui.Button(Localize.Text("Import pose files###pm_import")))
            UIManager.Instance.FileDialogManager.OpenFileDialog(Localize.Text("Import pose files"), ".pose{.pose}", (ok, files) =>
            {
                if(ok) ImportFiles([.. files]);
            }, 0);
        ImGui.SameLine();
        if(ImGui.Button(Localize.Text("Import folder###pm_folder")))
            UIManager.Instance.FileDialogManager.OpenFolderDialog(Localize.Text("Import folder"), (ok, folder) =>
            {
                if(ok) Run(() =>
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                    ImportFiles(Directory.EnumerateFiles(folder, "*.pose", options).Take(2000)
                        .Where(file => !Path.GetFullPath(file).StartsWith(library.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray());
                });
            });
        ImGui.SameLine();
        if(ImGui.Button(Localize.Text("Refresh###pm_refresh"))) _ = library.ScanAsync();
        ImGui.TextDisabled(Localize.Text(library.IsScanning ? "Scanning..." : "Bone previews only; original screenshots are ignored."));
        ImGui.SetNextItemWidth(210);
        ImGui.InputTextWithHint("###pm_search", Localize.Text("Search name, group or tags"), ref _search, 200);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(135);
        Combo("###pm_gender", ref _genderFilter, new[] { "All body types" }.Concat(GenderNames).ToArray());
        ImGui.SameLine(); ImGui.SetNextItemWidth(135);
        Combo("###pm_kind", ref _kindFilter, new[] { "All pose types" }.Concat(KindNames).ToArray());
        ImGui.SameLine(); ImGui.Checkbox(Localize.Text("Favorites only###pm_favorites"), ref _favorites);

        var entries = library.Entries;
        using(var groups = ImRaii.Child("###pm_groups", new(145, -32), true))
        {
            if(groups.Success)
            {
                if(ImGui.Selectable(Localize.Text("All groups"), _group.Length == 0)) _group = "";
                foreach(var group in entries.Select(x => x.Metadata.Group).Where(x => x.Length > 0).Distinct().OrderBy(x => x))
                    if(ImGui.Selectable(group, _group == group)) _group = group;
            }
        }
        ImGui.SameLine();
        var gridWidth = Math.Max(260, ImGui.GetContentRegionAvail().X - 330);
        using(var grid = ImRaii.Child("###pm_grid", new(gridWidth, -32), true))
        {
            if(grid.Success)
            {
                var filtered = entries.Where(Matches).ToArray();
                if(filtered.Length == 0) ImGui.TextWrapped(Localize.Text("No poses found. Save the current actor or import .pose files."));
                var columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / 145));
                var card = new Vector2((ImGui.GetContentRegionAvail().X - (columns - 1) * ImGui.GetStyle().ItemSpacing.X) / columns, 174);
                for(var i = 0; i < filtered.Length; i++)
                {
                    var entry = filtered[i];
                    ImGui.PushID(entry.Path);
                    ImGui.BeginGroup();
                    var start = ImGui.GetCursorPos();
                    var visible = ImGui.IsRectVisible(card);
                    if(ImGui.Selectable("###pose_card", _selectedPath == entry.Path, ImGuiSelectableFlags.None, card)) Select(entry);
                    var end = ImGui.GetCursorPos();
                    if(visible)
                    {
                        ImGui.SetCursorPos(start + new Vector2(5));
                        PoseMannequin.Draw("", library.Preview(entry), entry.Metadata, new(card.X - 10, 125));
                        ImGui.TextColored(PoseMannequin.Color(entry.Metadata.Gender), entry.Name.Length > 19 ? entry.Name[..18] + "…" : entry.Name);
                        ImGui.TextDisabled(Localize.Text(GenderNames[(int)entry.Metadata.Gender]));
                        ImGui.SetCursorPos(end);
                    }
                    ImGui.EndGroup();
                    ImGui.PopID();
                    if(i % columns != columns - 1) ImGui.SameLine();
                }
            }
        }
        ImGui.SameLine();
        using(var detail = ImRaii.Child("###pm_detail", new(0, -32), true))
            if(detail.Success) DrawDetails();
        ImGui.TextDisabled(_import is { IsCompleted: false } ? Localize.Text("Importing pose files...") : Localize.Text(_status.Length == 0 ? "Library folder (hover for path)" : _status));
        if(ImGui.IsItemHovered()) ImGui.SetTooltip(library.Root);
        if(library.FailedFiles > 0) ImGui.TextWrapped(Localize.Format("Skipped {0} unreadable library entries.", library.FailedFiles));
        DrawSave();
    }

    private bool Matches(PoseAssetEntry e) => (_group.Length == 0 || e.Metadata.Group == _group)
        && (!_favorites || e.Metadata.Favorite) && (_genderFilter == 0 || (int)e.Metadata.Gender == _genderFilter - 1)
        && (_kindFilter == 0 || (int)e.Metadata.Kind == _kindFilter - 1)
        && (_search.Length == 0 || (e.Name + " " + e.Metadata.Group + " " + e.Metadata.Tags).Contains(_search, StringComparison.OrdinalIgnoreCase));

    private void Select(PoseAssetEntry entry)
    {
        _selected = entry; _selectedPath = entry.Path; _name = entry.Name;
        _edit = global::Brio.Core.JsonSerializer.Deserialize<PoseAssetMetadata>(global::Brio.Core.JsonSerializer.Serialize(entry.Metadata));
        _tags = _edit.Tags; _editGroup = _edit.Group; _view.Reset();
    }

    private void DrawDetails()
    {
        if(_selected is not { } entry) { ImGui.TextWrapped(Localize.Text("Select a pose to inspect its mannequin. Browsing never changes the actor.")); return; }
        PoseMannequin.Draw("###pm_view", library.Preview(entry), _edit, new(ImGui.GetContentRegionAvail().X, 240), _view);
        ImGui.TextDisabled(Localize.Text("Drag to orbit; wheel to zoom."));
        if(ImGui.Button(Localize.Text("Reset view###pm_reset_view"))) _view.Reset();
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("###pm_name", Localize.Text("Name"), ref _name, 100);
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("###pm_group", Localize.Text("Group"), ref _editGroup, 100);
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("###pm_tags", Localize.Text("Tags"), ref _tags, 300);
        var gender = (int)_edit.Gender; Combo("###pm_edit_gender", ref gender, GenderNames); _edit.Gender = (PoseAssetGender)gender;
        var kind = (int)_edit.Kind; Combo("###pm_edit_kind", ref kind, KindNames); _edit.Kind = (PoseAssetKind)kind;
        var favorite = _edit.Favorite;
        if(ImGui.Checkbox(Localize.Text("Favorite###pm_favorite"), ref favorite)) _edit.Favorite = favorite;
        if(ImGui.Button(Localize.Text("Save details###pm_details"))) Run(() =>
        {
            _edit.Group = _editGroup.Trim(); _edit.Tags = _tags.Trim(); library.Update(entry, _name, _edit);
            _selected = null; _selectedPath = null; _ = library.ScanAsync();
        });
        using(ImRaii.Disabled(!actions.TryActor(out _)))
        {
            if(ImGui.Button(Localize.Text("Apply pose###pm_apply"))) Run(() => actions.Apply(library.Read(entry.Path), _edit.Kind));
            ImGui.SameLine();
            if(ImGui.Button(Localize.Text("Apply expression only###pm_face"))) Run(() => actions.Apply(library.Read(entry.Path), PoseAssetKind.Expression));
        }
        if(ImGui.Button(Localize.Text("Delete to recycle bin###pm_delete"))) ImGui.OpenPopup("###pm_confirm_delete");
        if(ImGui.BeginPopup("###pm_confirm_delete"))
        {
            ImGui.TextWrapped(Localize.Text("Move this pose and its classification to the Windows recycle bin?"));
            if(ImGui.Button(Localize.Text("Confirm deletion###pm_confirm")))
            {
                Run(() => { library.Recycle(entry); _selected = null; _selectedPath = null; _ = library.ScanAsync(); });
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void DrawSave()
    {
        if(_openSave) { ImGui.OpenPopup("###pm_save_popup"); _openSave = false; }
        if(!ImGui.BeginPopup("###pm_save_popup")) return;
        ImGui.TextUnformatted(Localize.Text("Save current actor to the pose manager"));
        ImGui.InputText(Localize.Text("Name###pm_save_name"), ref _name, 100);
        var kind = (int)_saveKind; Combo("###pm_save_kind", ref kind, KindNames); _saveKind = (PoseAssetKind)kind;
        using(ImRaii.Disabled(!actions.TryActor(out _)))
            if(ImGui.Button(Localize.Text("Save as new pose###pm_save_new")))
            {
                Run(() => { var capture = actions.Capture(_saveKind); library.Save(_name, capture.Pose, capture.Metadata); _ = library.ScanAsync(); });
                ImGui.CloseCurrentPopup();
            }
        ImGui.EndPopup();
    }

    public static void Combo(string id, ref int index, string[] names)
    {
        index = Math.Clamp(index, 0, names.Length - 1);
        if(ImGui.BeginCombo(id, Localize.Text(names[index])))
        {
            for(var i = 0; i < names.Length; i++) if(ImGui.Selectable(Localize.Text(names[i]), i == index)) index = i;
            ImGui.EndCombo();
        }
    }

    private void Run(Action action)
    {
        try { action(); _status = "Pose operation completed."; }
        catch(Exception ex)
        {
            Brio.Log.Error(ex, "Pose manager operation failed.");
            _status = "Pose operation failed. See the log for details.";
            Brio.NotifyError(Localize.Text(_status));
        }
    }

    private void ImportFiles(string[] files)
    {
        if(_import is { IsCompleted: false }) return;
        _status = "Importing pose files..."; _importFailures = 0;
        _import = Task.Run(async () =>
        {
            foreach(var file in files)
                try { library.Import(file); }
                catch(Exception ex) { _importFailures++; Brio.Log.Error(ex, "Could not import a pose file."); }
            await library.ScanAsync();
        });
    }
}
