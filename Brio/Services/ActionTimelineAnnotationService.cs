using Brio.Resources;
using Dalamud.Plugin;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Brio.Services;

public sealed record ActionTimelineAnnotation(
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> RaceTags,
    IReadOnlyList<string> Props)
{
    public static ActionTimelineAnnotation Empty { get; } = new([], [], []);

    public string PrimaryNote { get; } = Notes.FirstOrDefault() ?? string.Empty;
    public string SearchText { get; } = string.Join(' ', Notes.Concat(RaceTags).Concat(Props));
    public bool IsEmpty => Notes.Count == 0 && RaceTags.Count == 0 && Props.Count == 0;
}

public sealed class ActionTimelineAnnotationService
{
    private const int FormatVersion = 1;

    private sealed class AnnotationDto
    {
        public string[] Notes { get; set; } = [];
        public string[] RaceTags { get; set; } = [];
        public string[] Props { get; set; } = [];
    }

    private sealed class AnnotationStoreDto
    {
        public int Version { get; set; } = FormatVersion;
        public string Source { get; set; } = string.Empty;
        public DateTime? GeneratedUtc { get; set; }
        public DateTime? ExportedUtc { get; set; }
        public Dictionary<uint, AnnotationDto> Entries { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _userFile;
    private readonly FrozenDictionary<uint, ActionTimelineAnnotation> _builtInAnnotations;
    private FrozenDictionary<uint, ActionTimelineAnnotation> _userAnnotations =
        FrozenDictionary<uint, ActionTimelineAnnotation>.Empty;
    private int _version;

    public int Version => Volatile.Read(ref _version);
    public int BuiltInCount => _builtInAnnotations.Count;
    public int UserCount => Volatile.Read(ref _userAnnotations).Count;

    public ActionTimelineAnnotationService(
        IDalamudPluginInterface pluginInterface,
        ResourceProvider resourceProvider)
    {
        var dataDirectory = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "Data");
        Directory.CreateDirectory(dataDirectory);
        _userFile = Path.Combine(dataDirectory, "ActionTimelineAnnotations.user.json");

        _builtInAnnotations = LoadBuiltIn(resourceProvider);
        _userAnnotations = LoadUserFile();
    }

    public ActionTimelineAnnotation GetEffective(uint timelineId)
    {
        var user = Volatile.Read(ref _userAnnotations);
        if(user.TryGetValue(timelineId, out var annotation))
            return annotation;

        return _builtInAnnotations.TryGetValue(timelineId, out annotation)
            ? annotation
            : ActionTimelineAnnotation.Empty;
    }

    public ActionTimelineAnnotation GetBuiltIn(uint timelineId)
        => _builtInAnnotations.TryGetValue(timelineId, out var annotation)
            ? annotation
            : ActionTimelineAnnotation.Empty;

    public bool HasUserOverride(uint timelineId)
        => Volatile.Read(ref _userAnnotations).ContainsKey(timelineId);

    public void SetUser(
        uint timelineId,
        IEnumerable<string> notes,
        IEnumerable<string> raceTags,
        IEnumerable<string> props)
    {
        lock(_sync)
        {
            var mutable = Volatile.Read(ref _userAnnotations).ToDictionary();
            mutable[timelineId] = Normalize(notes, raceTags, props);
            PublishUserAnnotations(mutable);
        }
    }

    public bool RemoveUser(uint timelineId)
    {
        lock(_sync)
        {
            var mutable = Volatile.Read(ref _userAnnotations).ToDictionary();
            if(!mutable.Remove(timelineId))
                return false;

            PublishUserAnnotations(mutable);
            return true;
        }
    }

    public void Export(string file)
    {
        var store = new AnnotationStoreDto
        {
            Version = FormatVersion,
            Source = "Brio-CN user action annotations",
            ExportedUtc = DateTime.UtcNow,
            Entries = ToDto(Volatile.Read(ref _userAnnotations)),
        };

        File.WriteAllText(file, JsonSerializer.Serialize(store, JsonOptions));
    }

    public int Import(string file)
    {
        var store = JsonSerializer.Deserialize<AnnotationStoreDto>(
            File.ReadAllText(file),
            JsonOptions) ?? throw new InvalidDataException("The annotation backup is empty.");

        if(store.Version != FormatVersion)
            throw new InvalidDataException($"Unsupported annotation backup version: {store.Version}.");

        lock(_sync)
        {
            Dictionary<uint, ActionTimelineAnnotation> mutable = [];
            foreach(var (timelineId, annotation) in store.Entries)
                mutable[timelineId] = Normalize(annotation);

            PublishUserAnnotations(mutable);
        }

        return store.Entries.Count;
    }

    private void PublishUserAnnotations(Dictionary<uint, ActionTimelineAnnotation> annotations)
    {
        var snapshot = annotations.ToFrozenDictionary();
        Volatile.Write(ref _userAnnotations, snapshot);
        SaveUserFile(snapshot);
        Interlocked.Increment(ref _version);
    }

    private FrozenDictionary<uint, ActionTimelineAnnotation> LoadBuiltIn(ResourceProvider resourceProvider)
    {
        try
        {
            using var stream = resourceProvider.GetRawResourceStream(
                "Data.ActionTimelineAnnotations.zh-CN.json.gz");
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            var store = JsonSerializer.Deserialize<AnnotationStoreDto>(gzip, JsonOptions);
            return store?.Entries.ToFrozenDictionary(
                pair => pair.Key,
                pair => Normalize(pair.Value))
                ?? FrozenDictionary<uint, ActionTimelineAnnotation>.Empty;
        }
        catch(Exception ex)
        {
            Brio.Log.Error(ex, "Failed to load built-in action timeline annotations");
            return FrozenDictionary<uint, ActionTimelineAnnotation>.Empty;
        }
    }

    private FrozenDictionary<uint, ActionTimelineAnnotation> LoadUserFile()
    {
        if(!File.Exists(_userFile))
            return FrozenDictionary<uint, ActionTimelineAnnotation>.Empty;

        try
        {
            var store = JsonSerializer.Deserialize<AnnotationStoreDto>(
                File.ReadAllText(_userFile),
                JsonOptions);
            return store?.Entries.ToFrozenDictionary(
                pair => pair.Key,
                pair => Normalize(pair.Value))
                ?? FrozenDictionary<uint, ActionTimelineAnnotation>.Empty;
        }
        catch(Exception ex)
        {
            Brio.Log.Error(ex, "Failed to load user action timeline annotations");
            return FrozenDictionary<uint, ActionTimelineAnnotation>.Empty;
        }
    }

    private void SaveUserFile(
        FrozenDictionary<uint, ActionTimelineAnnotation> annotations)
    {
        try
        {
            var store = new AnnotationStoreDto
            {
                Version = FormatVersion,
                Source = "Brio-CN user action annotations",
                Entries = ToDto(annotations),
            };

            var temporaryFile = _userFile + ".tmp";
            File.WriteAllText(
                temporaryFile,
                JsonSerializer.Serialize(store, JsonOptions));
            File.Move(temporaryFile, _userFile, true);
        }
        catch(Exception ex)
        {
            Brio.Log.Error(ex, "Failed to save user action timeline annotations");
        }
    }

    private static Dictionary<uint, AnnotationDto> ToDto(
        IReadOnlyDictionary<uint, ActionTimelineAnnotation> annotations)
        => annotations.ToDictionary(
            pair => pair.Key,
            pair => new AnnotationDto
            {
                Notes = [.. pair.Value.Notes],
                RaceTags = [.. pair.Value.RaceTags],
                Props = [.. pair.Value.Props],
            });

    private static ActionTimelineAnnotation Normalize(AnnotationDto annotation)
        => Normalize(annotation.Notes, annotation.RaceTags, annotation.Props);

    private static ActionTimelineAnnotation Normalize(
        IEnumerable<string> notes,
        IEnumerable<string> raceTags,
        IEnumerable<string> props)
        => new(
            NormalizeValues(notes),
            NormalizeValues(raceTags),
            NormalizeValues(props));

    private static string[] NormalizeValues(IEnumerable<string> values)
        => [.. values
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}
