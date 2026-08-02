using Lumina.Excel.Sheets;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace Brio.Resources;

[Flags]
public enum ActionTimelineCategory
{
    None = 0,
    Emote = 1 << 0,
    PlayerAction = 1 << 1,
    NonPlayerAction = 1 << 2,
    AmbientNpc = 1 << 3,
    Cutscene = 1 << 4,
    HumanSpecial = 1 << 5,
    Monster = 1 << 6,
    Demihuman = 1 << 7,
    Combat = 1 << 8,
    Common = 1 << 9,
    Unclassified = 1 << 10,
}

public enum ActionTimelineModelKind
{
    Human,
    Monster,
    Demihuman,
}

public readonly record struct ActionTimelineContext(
    ActionTimelineModelKind ModelKind,
    ushort ModelId,
    ushort AnimationVariant)
{
    public string ModelCode => ModelKind switch
    {
        ActionTimelineModelKind.Human => $"c{ModelId:D4}",
        ActionTimelineModelKind.Monster => $"m{ModelId:D4}",
        ActionTimelineModelKind.Demihuman => $"d{ModelId:D4}",
        _ => $"{ModelId:D4}",
    };

    public string AnimationVariantCode => $"a{AnimationVariant:D4}";

    public override string ToString() => $"{ModelCode}/{AnimationVariantCode}";
}

public sealed record ActionTimelineMetadata(
    uint TimelineId,
    string Key,
    ActionTimelineCategory Categories,
    IReadOnlyList<ActionTimelineContext> Contexts)
{
    private static readonly ushort[] StandardPlayableHumanModels =
    [
        101, 201, 301, 401, 501, 601, 701, 801, 901,
        1001, 1101, 1201, 1301, 1401, 1501, 1601, 1701, 1801,
    ];

    public bool HasExactContexts => Contexts.Count > 0;
    public bool HasHumanContexts => Contexts.Any(context => context.ModelKind == ActionTimelineModelKind.Human);
    public bool HasMonsterContexts => Contexts.Any(context => context.ModelKind == ActionTimelineModelKind.Monster);
    public bool HasDemihumanContexts => Contexts.Any(context => context.ModelKind == ActionTimelineModelKind.Demihuman);

    public bool IsHumanRaceRestricted
    {
        get
        {
            var humanContexts = Contexts
                .Where(context => context.ModelKind == ActionTimelineModelKind.Human)
                .ToArray();

            if(humanContexts.Length == 0)
                return false;

            if(humanContexts.Any(context => context.AnimationVariant != 1))
                return true;

            return StandardPlayableHumanModels.Any(modelId =>
                !humanContexts.Any(context => context.ModelId == modelId && context.AnimationVariant == 1));
        }
    }

    public bool Supports(ActionTimelineContext context) => Contexts.Contains(context);

    public bool CanEmulateOnHuman => HasHumanContexts;
}

public sealed class ActionTimelineMetadataDatabase
{
    private sealed class MetadataBuilder(uint timelineId, string key)
    {
        public uint TimelineId { get; } = timelineId;
        public string Key { get; } = key;
        public ActionTimelineCategory Categories { get; set; }
        public List<ActionTimelineContext> Contexts { get; } = [];
    }

    private readonly FrozenDictionary<uint, ActionTimelineMetadata> _metadata;

    public int IndexedTimelineCount { get; }
    public int TimelineCount => _metadata.Count;

    public ActionTimelineMetadataDatabase(GameDataProvider gameDataProvider, ResourceProvider resourceProvider)
    {
        Dictionary<uint, MetadataBuilder> builders = [];

        foreach(var timeline in gameDataProvider.ActionTimelines)
        {
            var key = timeline.Key.ToString();
            builders[timeline.RowId] = new(timeline.RowId, key)
            {
                Categories = ClassifyKey(key),
            };
        }

        LoadContexts(resourceProvider, builders);
        ApplyExcelReferences(gameDataProvider, builders);

        foreach(var builder in builders.Values)
        {
            var hasHuman = builder.Contexts.Any(context => context.ModelKind == ActionTimelineModelKind.Human);
            var hasMonster = builder.Contexts.Any(context => context.ModelKind == ActionTimelineModelKind.Monster);
            var hasDemihuman = builder.Contexts.Any(context => context.ModelKind == ActionTimelineModelKind.Demihuman);

            if(hasMonster && !hasHuman)
                builder.Categories |= ActionTimelineCategory.Monster;

            if(hasDemihuman && !hasHuman && !hasMonster)
                builder.Categories |= ActionTimelineCategory.Demihuman;

            if(builder.Categories == ActionTimelineCategory.None)
                builder.Categories = ActionTimelineCategory.Unclassified;
        }

        IndexedTimelineCount = builders.Values.Count(builder => builder.Contexts.Count > 0);
        _metadata = builders.Values.ToFrozenDictionary(
            builder => builder.TimelineId,
            builder => new ActionTimelineMetadata(
                builder.TimelineId,
                builder.Key,
                builder.Categories,
                builder.Contexts
                    .Distinct()
                    .OrderBy(context => context.ModelKind)
                    .ThenBy(context => context.ModelId)
                    .ThenBy(context => context.AnimationVariant)
                    .ToArray()));
    }

    public bool TryGet(uint timelineId, out ActionTimelineMetadata metadata)
        => _metadata.TryGetValue(timelineId, out metadata!);

    public ActionTimelineMetadata Get(uint timelineId)
        => _metadata.TryGetValue(timelineId, out var metadata)
            ? metadata
            : new(timelineId, string.Empty, ActionTimelineCategory.Unclassified, []);

    private static void ApplyExcelReferences(GameDataProvider gameDataProvider, Dictionary<uint, MetadataBuilder> builders)
    {
        foreach(var emote in gameDataProvider.GetExcelSheet<Emote>())
        {
            foreach(var timeline in emote.ActionTimeline)
                AddCategory(builders, timeline.RowId, ActionTimelineCategory.Emote);
        }

        foreach(var action in gameDataProvider.GetExcelSheet<ActionSheet>())
        {
            var category = action.IsPlayerAction || action.ClassJob.RowId != 0
                ? ActionTimelineCategory.PlayerAction
                : ActionTimelineCategory.NonPlayerAction;

            AddCategory(builders, action.AnimationEnd.RowId, category);
            AddCategory(builders, action.ActionTimelineHit.RowId, category);

            var startTimelineId = action.AnimationStart.ValueNullable?.Name.RowId ?? 0;
            AddCategory(builders, startTimelineId, category);
        }
    }

    private static void AddCategory(
        Dictionary<uint, MetadataBuilder> builders,
        uint timelineId,
        ActionTimelineCategory category)
    {
        if(timelineId != 0 && builders.TryGetValue(timelineId, out var builder))
            builder.Categories |= category;
    }

    private static ActionTimelineCategory ClassifyKey(string key)
    {
        if(string.IsNullOrWhiteSpace(key))
            return ActionTimelineCategory.None;

        if(key.StartsWith("event_base/", StringComparison.OrdinalIgnoreCase))
            return ActionTimelineCategory.AmbientNpc;

        if(key.StartsWith("event/", StringComparison.OrdinalIgnoreCase))
            return ActionTimelineCategory.Cutscene;

        if(key.StartsWith("human_sp/", StringComparison.OrdinalIgnoreCase))
            return ActionTimelineCategory.HumanSpecial;

        if(key.StartsWith("mon_sp/", StringComparison.OrdinalIgnoreCase)
            || key.Contains("/mon_sp/", StringComparison.OrdinalIgnoreCase))
            return ActionTimelineCategory.Monster;

        if(key.StartsWith("emote/", StringComparison.OrdinalIgnoreCase))
            return ActionTimelineCategory.Emote;

        if(key.StartsWith("ability/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("magic/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ws/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("limitbreak/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("rol_common/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("pc_contentsaction/", StringComparison.OrdinalIgnoreCase))
            return ActionTimelineCategory.Combat;

        if(key.StartsWith("normal/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("idle_sp/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("resident/", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("move/", StringComparison.OrdinalIgnoreCase))
            return ActionTimelineCategory.Common;

        return ActionTimelineCategory.None;
    }

    private static void LoadContexts(
        ResourceProvider resourceProvider,
        Dictionary<uint, MetadataBuilder> builders)
    {
        using var resourceStream = resourceProvider.GetRawResourceStream("Data.ActionTimelineContexts.json.gz");
        using var gzipStream = new GZipStream(resourceStream, CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzipStream);

        if(!document.RootElement.TryGetProperty("timelines", out var timelines))
            return;

        foreach(var timeline in timelines.EnumerateObject())
        {
            if(!uint.TryParse(timeline.Name, out var timelineId)
                || !builders.TryGetValue(timelineId, out var builder))
                continue;

            foreach(var contextElement in timeline.Value.EnumerateArray())
            {
                if(TryParseContext(contextElement.GetString(), out var context))
                    builder.Contexts.Add(context);
            }
        }
    }

    private static bool TryParseContext(string? value, out ActionTimelineContext context)
    {
        context = default;

        if(string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('/');
        if(parts.Length != 2
            || parts[0].Length != 5
            || parts[1].Length != 5
            || parts[1][0] != 'a'
            || !ushort.TryParse(parts[0].AsSpan(1), out var modelId)
            || !ushort.TryParse(parts[1].AsSpan(1), out var animationVariant))
            return false;

        var modelKind = parts[0][0] switch
        {
            'c' => ActionTimelineModelKind.Human,
            'm' => ActionTimelineModelKind.Monster,
            'd' => ActionTimelineModelKind.Demihuman,
            _ => (ActionTimelineModelKind?)null,
        };

        if(modelKind is null)
            return false;

        context = new(modelKind.Value, modelId, animationVariant);
        return true;
    }
}
