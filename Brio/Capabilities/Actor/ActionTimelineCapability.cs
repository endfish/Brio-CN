using Brio.Config;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.Actor.Interop;
using Brio.Game.GPose;
using Brio.Game.Posing;
using Brio.Game.Types;
using Brio.Resources;
using Brio.UI.Widgets.Actor;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using static Brio.Game.Actor.ActionTimelineService;

namespace Brio.Capabilities.Actor;

public class ActionTimelineCapability : ActorCharacterCapability
{
    private readonly IFramework _framework;

    public ushort ActorObjectIndex { get; }

    public unsafe float SpeedMultiplier => SpeedMultiplierOverride ?? Character.Native()->Timeline.OverallSpeed;
    public bool HasSpeedMultiplierOverride => SpeedMultiplierOverride.HasValue;
    public float? SpeedMultiplierOverride { get; private set; }
    public bool IsPaused { get; private set; } = false;

    public bool HasOverride => (SlotedBlendAnimation != 0 || SlotedBaseAnimation != 0)
        && (HasBaseOverride
            || HasSpeedMultiplierOverride
            || HasAnimationContextOverride
            || DoBaseInterrupt is false
            || LipsOverride > 0);

    public bool DoBaseInterrupt = true;
    public int SlotedBaseAnimation = 0;
    public int SlotedBlendAnimation = 0;

    public unsafe ushort LipsOverride
    {
        get => Character.Native()->Timeline.LipsOverride;
        set => Character.Native()->Timeline.SetLipsOverrideTimeline(value);
    }

    private readonly Dictionary<ActionTimelineSlots, float> _actionTimelineSlotSpeedOverrides = [];
    private OriginalBaseAnimation? _originalBaseAnimation = null;
    private OriginalAnimationContext? _originalAnimationContext = null;
    private bool _baseOverrideUsesAnimationContext;
    private ushort _configuredAnimationTimeline;
    private ActionTimelineContext? _configuredAnimationContext;
    private bool _crossRaceAnimationEmulationEnabled;
    private int _timelinePlaybackGeneration;
    private bool _isDisposed;
    private bool _slotsDirty = false;

    public bool HasAnimationContextOverride => _originalAnimationContext.HasValue;
    public bool CanUseCrossRaceAnimationEmulation => _gPoseService.IsGPosing;
    public bool CrossRaceAnimationEmulationEnabled
    {
        get => _crossRaceAnimationEmulationEnabled;
        set
        {
            if(_crossRaceAnimationEmulationEnabled == value)
                return;

            _crossRaceAnimationEmulationEnabled = value;
            if(!value)
            {
                // Restoring RaceSexId while a foreign-race base override keeps
                // running creates the same mixed animation state that facial
                // playback must avoid. Stop that override as part of disabling
                // emulation, regardless of whether the user or a safety guard
                // disabled it.
                if(_baseOverrideUsesAnimationContext && HasBaseOverride)
                    ResetBaseOverride();

                ClearConfiguredAnimationContext();
            }
        }
    }

    public ActionTimelineCapability(
        IFramework framework,
        ActorEntity parent,
        EntityManager entityManager,
        PhysicsService physicsService,
        ConfigurationService configService,
        GPoseService gPoseService) : base(parent)
    {
        _framework = framework;
        ActorObjectIndex = parent.GameObject.ObjectIndex;
        gPoseService.OnGPoseStateChange += OnGPoseStateChange;
        _gPoseService = gPoseService;

        Widget = new ActionTimelineWidget(this, entityManager, physicsService, configService);
    }

    private readonly GPoseService _gPoseService;

    public unsafe void SetOverallSpeedOverride(float speed)
    {
        SpeedMultiplierOverride = speed;
        Character.Native()->Timeline.OverallSpeed = speed;
    }

    public void ResetOverallSpeedOverride()
    {
        SpeedMultiplierOverride = null;
    }

    public unsafe ActionTimelineUnion GetSlotAction(ActionTimelineSlots slot)
    {
        var timeline = Character.Native()->Timeline.TimelineSequencer.TimelineIds[(int)slot];
        return new ActionTimelineId(timeline);
    }

    public unsafe bool TryReadActiveTimelineIds(Span<ushort> destination)
    {
        var character = Character;
        if(!character.IsValid()
            || character.Address == nint.Zero
            || character.Address != Actor.ObjectAddress
            || character.ObjectIndex != ActorObjectIndex)
            return false;

        // Read raw IDs, including timelines absent from our action database.
        // Reading must never configure or replay the actor's animation.
        return character.Native()->Timeline.TimelineSequencer.TimelineIds.TryCopyTo(destination);
    }

    public unsafe float GetSlotSpeed(ActionTimelineSlots slot)
    {
        if(_actionTimelineSlotSpeedOverrides.TryGetValue(slot, out float speed))
            return speed;

        return Character.Native()->Timeline.TimelineSequencer.TimelineSpeeds[(int)slot];
    }

    public unsafe void SetSlotSpeedOverride(ActionTimelineSlots slot, float speed)
    {
        _actionTimelineSlotSpeedOverrides[slot] = speed;
        Character.Native()->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
        _slotsDirty = true;
    }

    public bool HasSlotSpeedOverride(ActionTimelineSlots slot)
    {
        return _actionTimelineSlotSpeedOverrides.ContainsKey(slot);
    }

    public void ResetSlotSpeedOverride(ActionTimelineSlots slot)
    {
        _actionTimelineSlotSpeedOverrides.Remove(slot);
        _slotsDirty = true;
    }

    public bool CheckAndResetDirtySlots() => _slotsDirty && !(_slotsDirty = false);

    public unsafe void ApplyBaseOverride(ushort actionTimeline, bool interrupt)
    {
        if(_originalBaseAnimation == null)
            _originalBaseAnimation = new(Character.Native()->Mode, Character.Native()->ModeParam, Character.Native()->Timeline.BaseOverride);

        var chara = Character.Native();

        chara->SetMode(CharacterModes.AnimLock, 0);
        chara->Timeline.BaseOverride = actionTimeline;
        _baseOverrideUsesAnimationContext = HasAnimationContextOverride;

        if(interrupt)
            BlendTimeline(actionTimeline);
    }

    public async void StopSpeedAndResetTimeline(Action? postStopAction = null, bool resetSpeedAfterAction = false)
    {
        Brio.Log.Verbose($"StopSpeedAndResetTimeline {postStopAction is not null} {resetSpeedAfterAction}");

        var oldSpeed = SpeedMultiplier;

        SetOverallSpeedOverride(0);

        Brio.Log.Verbose($"SetOverallSpeedOverride {oldSpeed} {SpeedMultiplier}");

        await _framework.RunOnTick(() =>
        {
            unsafe
            {
                var drawObj = Character.Native()->GameObject.DrawObject;
                if(drawObj == null)
                    return;

                if(drawObj->Object.GetObjectType() != ObjectType.CharacterBase)
                    return;

                var charaBase = (CharacterBase*)drawObj;
                if(charaBase->Skeleton == null)
                    return;

                var skeleton = charaBase->Skeleton;
                for(int p = 0; p < skeleton->PartialSkeletonCount; ++p)
                {
                    var partial = &skeleton->PartialSkeletons[p];

                    var animatedSkele = partial->GetHavokAnimatedSkeleton(0);
                    if(animatedSkele == null)
                        continue;

                    for(int c = 0; c < animatedSkele->AnimationControls.Length; ++c)
                    {
                        var control = animatedSkele->AnimationControls[c].Value;
                        if(control == null)
                            continue;

                        var binding = control->hkaAnimationControl.Binding;
                        if(binding.ptr == null)
                            continue;

                        var anim = binding.ptr->Animation.ptr;
                        if(anim == null)
                            continue;

                        if(control->PlaybackSpeed == 0)
                        {
                            control->hkaAnimationControl.LocalTime = 0;
                        }
                    }
                }
            }
        }, delayTicks: 4);

        postStopAction?.Invoke();

        if(resetSpeedAfterAction)
        {
            await _framework.RunOnTick(() =>
            {
                SetOverallSpeedOverride(oldSpeed);
            }, delayTicks: 2);
        }
    }

    public unsafe void ResetBaseOverride()
    {
        _timelinePlaybackGeneration++;

        var resetTimeline = false;
        if(_originalBaseAnimation is OriginalBaseAnimation original)
        {
            var chara = Character.Native();

            chara->Timeline.BaseOverride = original.OriginalTimeline;
            chara->Mode = original.OriginalMode;
            chara->ModeParam = original.OriginalInput;

            _originalBaseAnimation = null;
            resetTimeline = true;
        }
        _baseOverrideUsesAnimationContext = false;

        RestoreAnimationContext();

        if(resetTimeline)
            BlendTimeline(3);
    }

    public bool HasBaseOverride => _originalBaseAnimation != null;

    public unsafe void BlendTimeline(ushort actionTimeline)
    {
        _timelinePlaybackGeneration++;

        if(TryStageFacialTimeline(actionTimeline))
            return;

        // Some callers (most notably Dynamic Face Control) play timelines
        // directly instead of going through ActionTimelineEditor. Always
        // reconcile the emulated animation context here so those paths can not
        // leave a spoofed RaceSexId active while loading a facial timeline.
        PrepareAnimationContext(actionTimeline);
        PlayTimelineNow(actionTimeline);
    }

    private bool TryStageFacialTimeline(ushort timelineId)
    {
        if(!IsFacialTimeline(timelineId))
            return false;

        var hadActiveCrossRacePlayback = HasAnimationContextOverride
            || (_baseOverrideUsesAnimationContext && HasBaseOverride);
        var wasEmulating = CrossRaceAnimationEmulationEnabled || hadActiveCrossRacePlayback;

        // Disabling emulation also restores the original base override before
        // restoring the actor's native animation context. This order prevents
        // a foreign base timeline from surviving under the native RaceSexId.
        CrossRaceAnimationEmulationEnabled = false;
        ClearConfiguredAnimationContext();

        if(!hadActiveCrossRacePlayback)
        {
            if(wasEmulating)
                Brio.Log.Warning($"Disabled cross-race animation emulation before playing facial timeline {timelineId}.");

            return false;
        }

        // Replace any pending foreign animation resources with a native idle,
        // then give the renderer two framework ticks to settle before loading
        // the race-specific facial PAP. The delayed callback is invalidated by
        // any newer playback/reset request.
        PlayTimelineNow(3);
        var playbackGeneration = _timelinePlaybackGeneration;
        _ = _framework.RunOnTick(() =>
        {
            if(_isDisposed
                || playbackGeneration != _timelinePlaybackGeneration
                || Character.Address == nint.Zero)
                return;

            PlayTimelineNow(timelineId);

            if(_actionTimelineSlotSpeedOverrides.TryGetValue(ActionTimelineSlots.Facial, out var speed))
                SetFacialSlotSpeed(speed);
        }, delayTicks: 2);

        Brio.Log.Warning($"Stopped cross-race animation playback and deferred facial timeline {timelineId} until native animation resources settle.");
        return true;
    }

    private unsafe void PlayTimelineNow(ushort timelineId)
    {
        Character.Native()->Timeline.TimelineSequencer.PlayTimeline(timelineId);
    }

    private unsafe void SetFacialSlotSpeed(float speed)
    {
        Character.Native()->Timeline.TimelineSequencer.SetSlotSpeed((uint)ActionTimelineSlots.Facial, speed);
    }

    private static bool IsFacialTimeline(ushort timelineId)
    {
        return Brio.TryGetService<TimelineIdentification>(out var identification)
            && identification.IsFacialExpression(timelineId);
    }

    public void Stop()
    {
        _timelinePlaybackGeneration++;

        if(HasBaseOverride)
        {
            ResetBaseOverride();
            ResetOverallSpeedOverride();
        }

        RestoreAnimationContext();
    }

    public void Reset()
    {
        _timelinePlaybackGeneration++;
        DoBaseInterrupt = true;

        SlotedBaseAnimation = 0;
        SlotedBlendAnimation = 0;
        LipsOverride = 0;

        IsPaused = false;

        ResetBaseOverride();
        ResetOverallSpeedOverride();
        ClearConfiguredAnimationContext();
    }

    public override void Dispose()
    {
        _timelinePlaybackGeneration++;
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChange;
        SpeedMultiplierOverride = null;
        _actionTimelineSlotSpeedOverrides.Clear();
        ResetBaseOverride();
        ClearConfiguredAnimationContext();
        _isDisposed = true;

        base.Dispose();
    }

    public unsafe bool ApplyAnimationContext(ActionTimelineContext? context)
    {
        if(!CrossRaceAnimationEmulationEnabled || !_gPoseService.IsGPosing)
        {
            RestoreAnimationContext();
            return context is null;
        }

        if(context is null)
        {
            RestoreAnimationContext();
            return true;
        }

        if(context.Value.ModelKind != ActionTimelineModelKind.Human)
            return false;
        if(context.Value.AnimationVariant > byte.MaxValue)
            return false;

        var characterBase = Character.GetCharacterBase();
        if(characterBase is null
            || characterBase->CharacterBase.GetModelType() != CharacterBase.ModelType.Human)
            return false;

        var human = (BrioHuman*)characterBase;
        var current = new ActionTimelineContext(
            ActionTimelineModelKind.Human,
            human->Human.RaceSexId,
            characterBase->CharacterBase.AnimationVariant);

        if(_originalAnimationContext is null)
            _originalAnimationContext = new(
                current.ModelId,
                characterBase->CharacterBase.AnimationVariant);

        var original = _originalAnimationContext.Value;
        if(context.Value.ModelId == original.RaceSexId
            && context.Value.AnimationVariant == original.AnimationVariant)
        {
            RestoreAnimationContext();
            return true;
        }

        human->Human.RaceSexId = context.Value.ModelId;
        characterBase->CharacterBase.AnimationVariant = (byte)context.Value.AnimationVariant;
        return true;
    }

    public void ConfigureAnimationContext(
        ushort timelineId,
        ActionTimelineContext? context)
    {
        if(!CrossRaceAnimationEmulationEnabled || !_gPoseService.IsGPosing)
        {
            ClearConfiguredAnimationContext();
            return;
        }

        _configuredAnimationTimeline = timelineId;
        _configuredAnimationContext = context;
    }

    public void PrepareAnimationContext(ushort timelineId)
    {
        // Facial PAPs and face skeletons are race-specific. Playing one while
        // Human.RaceSexId is temporarily spoofed for a cross-race body action
        // can leave the game's pending resource list with an invalid handle and
        // crash Human.UpdateRender. Facial playback must always use the actor's
        // native animation context.
        if(IsFacialTimeline(timelineId))
        {
            var wasEmulating = CrossRaceAnimationEmulationEnabled || HasAnimationContextOverride;
            CrossRaceAnimationEmulationEnabled = false;
            ClearConfiguredAnimationContext();

            if(wasEmulating)
                Brio.Log.Warning($"Disabled cross-race animation emulation before playing facial timeline {timelineId}.");

            return;
        }

        if(!CrossRaceAnimationEmulationEnabled || !_gPoseService.IsGPosing)
        {
            ClearConfiguredAnimationContext();
            return;
        }

        if(_configuredAnimationTimeline != timelineId)
        {
            RestoreAnimationContext();
            return;
        }

        ApplyAnimationContext(_configuredAnimationContext);
    }

    public void ClearConfiguredAnimationContext()
    {
        _configuredAnimationTimeline = 0;
        _configuredAnimationContext = null;
        RestoreAnimationContext();
    }

    public unsafe ActionTimelineContext? GetAnimationContext(bool preferOriginal = true)
    {
        if(preferOriginal
            && GetOriginalAnimationContext() is ActionTimelineContext originalContext)
            return originalContext;

        var characterBase = Character.GetCharacterBase();
        if(characterBase is null)
            return null;

        var animationVariant = characterBase->CharacterBase.AnimationVariant;
        if(characterBase->CharacterBase.GetModelType() == CharacterBase.ModelType.Human)
        {
            var human = (BrioHuman*)characterBase;
            return new(
                ActionTimelineModelKind.Human,
                human->Human.RaceSexId,
                animationVariant);
        }

        return null;
    }

    public ActionTimelineContext? GetOriginalAnimationContext()
    {
        if(_originalAnimationContext is not OriginalAnimationContext original)
            return null;

        return new(
            ActionTimelineModelKind.Human,
            original.RaceSexId,
            original.AnimationVariant);
    }

    public unsafe void RestoreAnimationContext()
    {
        if(_originalAnimationContext is not OriginalAnimationContext original)
            return;

        var characterBase = Character.GetCharacterBase();
        if(characterBase is not null
            && characterBase->CharacterBase.GetModelType() == CharacterBase.ModelType.Human)
        {
            var human = (BrioHuman*)characterBase;
            human->Human.RaceSexId = original.RaceSexId;
            characterBase->CharacterBase.AnimationVariant = original.AnimationVariant;
        }

        _originalAnimationContext = null;
    }

    private void OnGPoseStateChange(bool isGPosing)
    {
        if(!isGPosing)
            CrossRaceAnimationEmulationEnabled = false;
    }

    public static ActionTimelineCapability? CreateIfEligible(IServiceProvider provider, ActorEntity entity)
    {
        if(entity.GameObject is ICharacter)
            return ActivatorUtilities.CreateInstance<ActionTimelineCapability>(provider, entity);

        return null;
    }

    public record struct OriginalBaseAnimation(CharacterModes OriginalMode, byte OriginalInput, ushort OriginalTimeline);
    public record struct OriginalAnimationContext(ushort RaceSexId, byte AnimationVariant);
}
