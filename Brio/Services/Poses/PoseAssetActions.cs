using Brio.Capabilities.Actor;
using Brio.Capabilities.Posing;
using Brio.Core;
using Brio.Entities;
using Brio.Files;
using Brio.Game.GPose;
using Brio.Game.Posing;
using System;
using System.Linq;

namespace Brio.Services.Poses;

public sealed class PoseAssetActions(EntityManager entities, GPoseService gpose, FaceEditingService faceEditor)
{
    public bool TryActor(out PosingCapability capability)
    {
        capability = null!;
        if(!gpose.IsGPosing || !entities.TryGetCapabilityFromSelectedEntity<PosingCapability>(out var selected, considerParents: false)
            || selected.SkeletonPosing.CharacterSkeleton is not { IsValid: true } || selected.SkeletonPosing.HasTransitiveActions)
            return false;
        capability = selected;
        return true;
    }

    public (LibraryPoseDocument Pose, PoseAssetMetadata Metadata) Capture(PoseAssetKind kind)
    {
        if(!TryActor(out var cap)) throw new InvalidOperationException("Select a ready GPose actor first.");
        var skeleton = cap.SkeletonPosing.CharacterSkeleton!;
        var faces = skeleton.Bones.Where(b => b.IsFaceBone).Select(b => b.Name).ToHashSet();
        var document = PoseGeometry.Select(cap.ExportPoseAsFileData(), kind, faces);
        var metadata = new PoseAssetMetadata { Kind = kind };
        foreach(var bone in skeleton.Bones)
            if(bone.Parent is { } parent && document.Bones.ContainsKey(bone.Name) && document.Bones.ContainsKey(parent.Name))
                metadata.Parents[bone.Name] = parent.Name;
        if(cap.Actor.TryGetCapability<ActorAppearanceCapability>(out var appearance))
        {
            metadata.Gender = (int)appearance.CurrentAppearance.Customize.Gender == 0 ? PoseAssetGender.Male : PoseAssetGender.Female;
        }
        return (document, metadata);
    }

    public void Apply(LibraryPoseDocument document, PoseAssetKind kind)
    {
        if(!TryActor(out var cap)) throw new InvalidOperationException("Select a ready GPose actor first.");
        if(kind == PoseAssetKind.Expression)
        {
            if(!faceEditor.ApplyStoredExpression(document)) throw new InvalidOperationException("No compatible facial bones in the pose.");
            return;
        }
        var file = JsonSerializer.Deserialize<PoseFile>(JsonSerializer.Serialize(document));
        if(kind is PoseAssetKind.Hands)
        {
            file.Bones = file.Bones.Where(x => PoseGeometry.IsHandName(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            file.MainHand.Clear(); file.OffHand.Clear(); file.Prop.Clear(); file.Ornament.Clear();
        }
        var options = new PoseImporterOptions(new BoneFilter(cap.PosingService), TransformComponents.All, false);
        faceEditor.End();
        // Reuse Brio's body/import and undo pipeline, without world placement.
        cap.ImportPose(file, options, asBody: kind == PoseAssetKind.Body, applyModelTransformOverride: false);
    }
}
