using System.Collections.Generic;
using System.Linq;

namespace Brio.Resources;

public enum CommonPoseKind
{
    ChairSit,
    Standing,
    GroundSit,
    Sleep,
}

public readonly record struct CommonPoseDefinition(
    uint TimelineId,
    CommonPoseKind Kind,
    int VariantIndex,
    uint BaseEmoteId)
{
    public string DisplayName => Kind switch
    {
        CommonPoseKind.ChairSit => Localize.Format("Chair Sit Pose {0}", VariantIndex),
        CommonPoseKind.Standing => Localize.Format("Standing Pose {0}", VariantIndex),
        CommonPoseKind.GroundSit => Localize.Format("Ground Sit Pose {0}", VariantIndex),
        CommonPoseKind.Sleep => Localize.Format("Sleep Pose {0}", VariantIndex),
        _ => Localize.Format("Timeline {0}", TimelineId),
    };
}

public static class CommonPoseCatalog
{
    public static IReadOnlyList<CommonPoseDefinition> All { get; } =
    [
        new(3, CommonPoseKind.Standing, 0, 0),
        new(3124, CommonPoseKind.Standing, 1, 0),
        new(3126, CommonPoseKind.Standing, 2, 0),
        new(3182, CommonPoseKind.Standing, 3, 0),
        new(3184, CommonPoseKind.Standing, 4, 0),
        new(7405, CommonPoseKind.Standing, 5, 0),
        new(7407, CommonPoseKind.Standing, 6, 0),

        new(643, CommonPoseKind.ChairSit, 0, 50),
        new(3132, CommonPoseKind.ChairSit, 1, 50),
        new(3134, CommonPoseKind.ChairSit, 2, 50),
        new(8002, CommonPoseKind.ChairSit, 3, 50),
        new(8004, CommonPoseKind.ChairSit, 4, 50),

        new(654, CommonPoseKind.GroundSit, 0, 52),
        new(3136, CommonPoseKind.GroundSit, 1, 52),
        new(3138, CommonPoseKind.GroundSit, 2, 52),
        new(3771, CommonPoseKind.GroundSit, 3, 52),

        // The bed loop is the base, and l_pose01/02 are the two variants.
        // The ordinary /doze emote is separate, not the bed animation.
        new(585, CommonPoseKind.Sleep, 0, 0),
        new(3142, CommonPoseKind.Sleep, 1, 0),
        new(3140, CommonPoseKind.Sleep, 2, 0),
    ];

    private static readonly Dictionary<uint, int> Order = All.Select((pose, index) => (pose.TimelineId, index))
        .ToDictionary(entry => entry.TimelineId, entry => entry.index);

    public static int GetOrder(uint timelineId) => Order.GetValueOrDefault(timelineId, int.MaxValue);
    public static bool IsBaseEmote(uint emoteId) => emoteId != 0 && All.Any(pose => pose.BaseEmoteId == emoteId);
}
