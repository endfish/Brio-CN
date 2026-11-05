using Brio.Core;
using Brio.Files;
using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Brio.Services.Poses;

public enum PoseAssetKind { Body, Expression, Hands, Complete }
public enum PoseAssetGender { Unspecified, Male, Female, Any }

// Uses the standard .pose fields without PoseFile's game-memory initializer.
// Reading previews and indexing files never touches the game or a texture.
public sealed class LibraryPoseDocument : PoseData
{
    public int FileVersion { get; set; } = 3;
    public string TypeName { get; set; } = "Brio Pose";
    public string? GameVersion { get; set; }
    public int ModelId { get; set; }
    public string? RaceSexId { get; set; }
    public int? FaceID { get; set; }
}

public sealed class PoseAssetMetadata
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Group { get; set; } = "";
    public string Tags { get; set; } = "";
    public bool Favorite { get; set; }
    public PoseAssetKind Kind { get; set; }
    public PoseAssetGender Gender { get; set; }
    public Dictionary<string, string> Parents { get; set; } = [];
}

public sealed record PoseAssetEntry(string Path, string Name, PoseAssetMetadata Metadata, long Stamp);

public sealed class PoseLibraryService
{
    public string Root { get; }
    public PoseAssetEntry[] Entries { get; private set; } = [];
    public bool IsScanning => Volatile.Read(ref _scanning) != 0;
    public int FailedFiles { get; private set; }
    private int _scanning;
    private int _rescanRequested;
    private readonly ConcurrentDictionary<string, Task<LibraryPoseDocument?>> _previews = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _io = new();
    private const long MaxFileSize = 32 * 1024 * 1024;

    public PoseLibraryService() : this(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Brio", "Poses", "Managed")) { }
    internal PoseLibraryService(string root) => Root = System.IO.Path.GetFullPath(root);

    public Task ScanAsync()
    {
        if(Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _rescanRequested, 1);
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            try
            {
                lock(_io)
                {
                    Directory.CreateDirectory(Root);
                    var entries = new List<PoseAssetEntry>();
                    var failures = 0;
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach(var path in Directory.EnumerateFiles(Root, "*.pose", options).Take(20000))
                    {
                        try
                        {
                            Validate(path);
                            var file = new FileInfo(path);
                            if(file.Length > MaxFileSize) { failures++; continue; }
                            var metadata = File.Exists(path + ".brio.json")
                                ? JsonSerializer.Deserialize<PoseAssetMetadata>(ReadBounded(Validate(path + ".brio.json"))) : new PoseAssetMetadata();
                            metadata.Group ??= ""; metadata.Tags ??= ""; metadata.Parents ??= [];
                            if(!Enum.IsDefined(metadata.Kind)) metadata.Kind = PoseAssetKind.Body;
                            if(!Enum.IsDefined(metadata.Gender)) metadata.Gender = PoseAssetGender.Unspecified;
                            entries.Add(new(path, System.IO.Path.GetFileNameWithoutExtension(path), metadata, file.LastWriteTimeUtc.Ticks));
                        }
                        catch { failures++; }
                    }
                    Entries = [.. entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)];
                    FailedFiles = failures;
                    _previews.Clear();
                }
            }
            catch(Exception ex) { Brio.Log.Error(ex, "Could not scan the managed pose library."); }
            finally
            {
                Volatile.Write(ref _scanning, 0);
                if(Interlocked.Exchange(ref _rescanRequested, 0) != 0) _ = ScanAsync();
            }
        });
    }

    public LibraryPoseDocument? Preview(PoseAssetEntry entry)
    {
        if(_previews.Count > 192) _previews.Clear();
        var task = _previews.GetOrAdd(entry.Path + ":" + entry.Stamp, _ => Task.Run(() =>
        {
            try { return Read(entry.Path); }
            catch { return null; }
        }));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    public LibraryPoseDocument Read(string path)
    {
        var document = JsonSerializer.Deserialize<LibraryPoseDocument>(ReadBounded(Validate(path)));
        Normalize(document);
        document.SanitizeBoneNames();
        // Imported bytes are kept on disk. Don't retain images in the preview
        // cache or send them back through the application/undo pipeline.
        document.Base64Image = null;
        return document;
    }

    public string Save(string name, LibraryPoseDocument document, PoseAssetMetadata metadata)
    {
        lock(_io)
        {
            Directory.CreateDirectory(Root);
            var path = UniquePath(name);
            Normalize(document);
            // A thumbnail is generated from bones. Never embed a screenshot or
            // strip the image from an imported original file.
            document.Base64Image = null;
            WriteAtomic(path, JsonSerializer.Serialize(document));
            try { WriteAtomic(path + ".brio.json", JsonSerializer.Serialize(metadata)); }
            catch { File.Delete(path); throw; }
            return path;
        }
    }

    public string Import(string source)
    {
        lock(_io)
        {
            var info = new FileInfo(source);
            if(!info.Exists || !info.Extension.Equals(".pose", StringComparison.OrdinalIgnoreCase) || info.Length > MaxFileSize)
                throw new InvalidDataException("Not a supported pose file.");
            var text = ReadBounded(source);
            var pose = JsonSerializer.Deserialize<LibraryPoseDocument>(text);
            Normalize(pose);
            pose.SanitizeBoneNames();
            Directory.CreateDirectory(Root);
            var destination = UniquePath(System.IO.Path.GetFileNameWithoutExtension(source));
            WriteAtomic(destination, text);
            var kind = pose.Bones.Keys.All(PoseGeometry.IsFaceName) ? PoseAssetKind.Expression : PoseAssetKind.Body;
            try { WriteAtomic(destination + ".brio.json", JsonSerializer.Serialize(new PoseAssetMetadata { Kind = kind })); }
            catch { File.Delete(destination); throw; }
            return destination;
        }
    }

    public void Update(PoseAssetEntry entry, string name, PoseAssetMetadata metadata)
    {
        lock(_io)
        {
            var old = Validate(entry.Path);
            var newName = CleanName(name);
            var destination = Validate(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(old)!, newName + ".pose"));
            var rename = !old.Equals(destination, StringComparison.OrdinalIgnoreCase);
            if(rename && (File.Exists(destination) || File.Exists(destination + ".brio.json")))
                throw new IOException("A pose with this name already exists.");
            // Write metadata first, then move both files with rollback on error.
            WriteAtomic(old + ".brio.json", JsonSerializer.Serialize(metadata));
            if(rename)
            {
                File.Move(old, destination);
                try { File.Move(old + ".brio.json", destination + ".brio.json"); }
                catch { File.Move(destination, old); throw; }
            }
        }
    }

    public void Recycle(PoseAssetEntry entry)
    {
        lock(_io)
        {
            var path = Validate(entry.Path);
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            if(File.Exists(path + ".brio.json"))
                FileSystem.DeleteFile(path + ".brio.json", UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    private string UniquePath(string name)
    {
        var stem = CleanName(name);
        var candidate = Validate(System.IO.Path.Combine(Root, stem + ".pose"));
        for(var i = 2; File.Exists(candidate) || File.Exists(candidate + ".brio.json"); i++)
            candidate = Validate(System.IO.Path.Combine(Root, $"{stem} ({i}).pose"));
        return candidate;
    }

    private string Validate(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if(!full.StartsWith(Root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Pose path is outside the managed library.");
        if(File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked library files are not supported.");
        for(var dir = System.IO.Path.GetDirectoryName(full); dir is not null && dir.Length >= Root.Length; dir = System.IO.Path.GetDirectoryName(dir))
            if(Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked library directories are not supported.");
        return full;
    }

    private static string CleanName(string name)
    {
        var result = string.Concat(name.Trim().Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if(string.IsNullOrWhiteSpace(result) || result is "." or ".." || result.Length > 100)
            throw new IOException("Enter a valid pose name (up to 100 characters).");
        return result;
    }

    private static string ReadBounded(string path)
    {
        if(new FileInfo(path).Length > MaxFileSize) throw new InvalidDataException("Pose file is too large.");
        return File.ReadAllText(path);
    }

    private static void Normalize(LibraryPoseDocument document)
    {
        if(document.Bones is null || document.Bones.Count is 0 or > 10000)
            throw new InvalidDataException("Invalid pose bone count.");
        document.MainHand ??= []; document.OffHand ??= []; document.Prop ??= []; document.Ornament ??= [];
        document.ModelDifference ??= Transform.Identity;
        document.ModelAbsoluteValues ??= Transform.Identity;
        foreach(var bones in new[] { document.Bones, document.MainHand, document.OffHand, document.Prop, document.Ornament })
        {
            if(bones is null || bones.Count > 10000 || bones.Values.Any(b => b is null || !PoseGeometry.IsFinite(b)))
                throw new InvalidDataException("Invalid pose transforms.");
            foreach(var bone in bones.Values) bone.Rotation = System.Numerics.Quaternion.Normalize(bone.Rotation);
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content); File.Move(temporary, path, true); }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
}
