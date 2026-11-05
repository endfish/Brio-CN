using Brio.Config;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Game.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.Actor.Interop;
using Brio.Game.Core;
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
using ModelChara = Lumina.Excel.Sheets.ModelChara;
using System;
using System.Collections.Generic;
using System.Linq;
using static Brio.Game.Actor.ActionTimelineService;
using static Brio.Game.Actor.ActorRedrawService;

namespace Brio.Capabilities.Actor;

public class ActionTimelineCapability : ActorCharacterCapability
{
    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly ActorRedrawService _redraw;
    private readonly ObjectMonitorService _monitor;
    private readonly CrossRaceAnimationService _crossRaceAnimations;

    public ushort ActorObjectIndex { get; }

    public unsafe float SpeedMultiplier => SpeedMultiplierOverride
        ?? (TryGetLiveCharacter(out var character) ? character.Native()->Timeline.OverallSpeed : 1);
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
        get => TryGetLiveCharacter(out var character) ? character.Native()->Timeline.LipsOverride : (ushort)0;
        set
        {
            _timelinePlaybackGeneration++;
            if(value != 0 && !PrepareAnimationContext(value))
                return;
            if(TryGetLiveCharacter(out var character))
                character.Native()->Timeline.SetLipsOverrideTimeline(value);
        }
    }

    private readonly Dictionary<ActionTimelineSlots, float> _actionTimelineSlotSpeedOverrides = [];
    private OriginalBaseAnimation? _originalBaseAnimation = null;
    private int _timelinePlaybackGeneration;
    private volatile bool _isDisposed;
    private volatile bool _actorDestroyed;
    private volatile bool _drawObjectInvalidated;
    private bool _slotsDirty = false;
    private (ushort Timeline, string Reason)? _lastRejectedPlayback;
    private long _lastRejectedPlaybackAt;
    private bool _crossRaceAnimationEmulationEnabled;
    private bool _baseOverrideUsesAnimationContext;
    private ushort _configuredAnimationTimeline;
    private ActionTimelineContext? _configuredAnimationContext;
    private ActionTimelineContext? _activeAnimationContext;
    private ActionTimelineContext? _redrawAnimationContext;
    private bool _needsAnimationContextRebind;

    public bool HasAnimationContextOverride => _crossRaceAnimations.GetPlaybackContext(Actor.ObjectAddress) is not null;
    public bool CanUseCrossRaceAnimationEmulation => !_isDisposed && _gPoseService.IsGPosing
        && (_crossRaceAnimationEmulationEnabled || GetAnimationContext() is { ModelKind: ActionTimelineModelKind.Human });
    public bool CrossRaceAnimationEmulationEnabled
    {
        get => _crossRaceAnimationEmulationEnabled;
        set
        {
            if(value == _crossRaceAnimationEmulationEnabled)
                return;
            if(value)
            {
                if(!CanUseCrossRaceAnimationEmulation || !TryGetLiveCharacter(out var character)
                    || GetAnimationContext() is not { } native)
                    return;
                if(!_crossRaceAnimations.SetContext(character, native, null))
                {
                    RejectPlayback(0, "The cross-race animation loader could not be initialized. See the log for details.");
                    return;
                }
                _crossRaceAnimationEmulationEnabled = true;
            }
            else
            {
                var hadForeignPlayback = HasAnimationContextOverride;
                var hadForeignBase = _baseOverrideUsesAnimationContext && HasBaseOverride;
                _crossRaceAnimationEmulationEnabled = false;
                if(hadForeignBase)
                    ResetBaseOverride();
                ClearConfiguredAnimationContext();
                if(hadForeignPlayback && !hadForeignBase)
                    PlayTimelineNow(3);
                _crossRaceAnimations.Remove(Actor.ObjectAddress);
            }
        }
    }

    public ActionTimelineCapability(
        IFramework framework,
        ActorEntity parent,
        EntityManager entityManager,
        PhysicsService physicsService,
        ConfigurationService configService,
        GPoseService gPoseService,
        IObjectTable objectTable,
        ActorRedrawService redraw,
        ObjectMonitorService monitor,
        CrossRaceAnimationService crossRaceAnimations) : base(parent)
    {
        _framework = framework;
        _objectTable = objectTable;
        _redraw = redraw;
        _monitor = monitor;
        _crossRaceAnimations = crossRaceAnimations;
        ActorObjectIndex = parent.GameObject.ObjectIndex;
        gPoseService.OnGPoseStateChange += OnGPoseStateChange;
        _gPoseService = gPoseService;
        _redraw.ActorRedrawEvent += OnActorRedraw;
        unsafe
        {
            _monitor.CharacterDestroyed += OnCharacterDestroyed;
            _monitor.CharacterBaseDestroyed += OnDrawObjectDestroyed;
            _monitor.CharacterBaseMaterialsUpdated += OnDrawObjectReady;
        }

        Widget = new ActionTimelineWidget(this, entityManager, physicsService, configService);
    }

    private readonly GPoseService _gPoseService;

    private bool TryGetLiveCharacter(out ICharacter character)
    {
        character = null!;
        if(_isDisposed || _actorDestroyed || ActorObjectIndex >= _objectTable.Length
            || _objectTable[ActorObjectIndex] is not ICharacter current
            || current.Address == nint.Zero || current.Address != Actor.ObjectAddress)
            return false;
        character = current;
        return true;
    }

    public unsafe void SetOverallSpeedOverride(float speed)
    {
        _timelinePlaybackGeneration++;
        SetOverallSpeedOverrideCore(speed);
    }

    private unsafe void SetOverallSpeedOverrideCore(float speed)
    {
        if(!float.IsFinite(speed) || !TryGetLiveCharacter(out var character))
            return;
        SpeedMultiplierOverride = speed;
        character.Native()->Timeline.OverallSpeed = speed;
    }

    public void ResetOverallSpeedOverride()
    {
        _timelinePlaybackGeneration++;
        SpeedMultiplierOverride = null;
    }

    public unsafe ActionTimelineUnion GetSlotAction(ActionTimelineSlots slot)
    {
        var timeline = (uint)slot < 14 && TryGetLiveCharacter(out var character)
            ? character.Native()->Timeline.TimelineSequencer.TimelineIds[(int)slot] : (ushort)0;
        return new ActionTimelineId(timeline);
    }

    public unsafe bool TryReadActiveTimelineIds(Span<ushort> destination)
    {
        if(!TryGetLiveCharacter(out var character))
            return false;

        // Read raw IDs, including timelines absent from our action database.
        // Reading must never configure or replay the actor's animation.
        return character.Native()->Timeline.TimelineSequencer.TimelineIds.TryCopyTo(destination);
    }

    public unsafe float GetSlotSpeed(ActionTimelineSlots slot)
    {
        if(_actionTimelineSlotSpeedOverrides.TryGetValue(slot, out float speed))
            return speed;

        return (uint)slot < 14 && TryGetLiveCharacter(out var character)
            ? character.Native()->Timeline.TimelineSequencer.TimelineSpeeds[(int)slot] : 1;
    }

    public unsafe void SetSlotSpeedOverride(ActionTimelineSlots slot, float speed)
    {
        if((uint)slot >= 14 || !float.IsFinite(speed) || !TryGetLiveCharacter(out var character))
            return;
        _actionTimelineSlotSpeedOverrides[slot] = speed;
        character.Native()->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
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
        _timelinePlaybackGeneration++;
        if(!PrepareAnimationContext(actionTimeline) || !TryGetLiveCharacter(out var character))
            return;
        if(_originalBaseAnimation == null)
            _originalBaseAnimation = new(character.Native()->Mode, character.Native()->ModeParam, character.Native()->Timeline.BaseOverride);

        var chara = character.Native();

        chara->SetMode(CharacterModes.AnimLock, 0);
        chara->Timeline.BaseOverride = actionTimeline;
        _baseOverrideUsesAnimationContext = !IsFacialTimeline(actionTimeline) && HasAnimationContextOverride;

        if(interrupt)
            PlayTimelineNow(actionTimeline);
    }

    public async void StopSpeedAndResetTimeline(Action? postStopAction = null, bool resetSpeedAfterAction = false)
    {
        if(!_gPoseService.IsGPosing || !TryGetLiveCharacter(out var character))
            return;
        var generation = ++_timelinePlaybackGeneration;
        nint drawAddress;
        unsafe { drawAddress = (nint)character.Native()->GameObject.DrawObject; }
        if(drawAddress == 0)
            return;
        var oldSpeed = SpeedMultiplier;
        SetOverallSpeedOverrideCore(0);
        try
        {
            await _framework.RunOnTick(() =>
            {
                if(!IsCurrentPlayback(generation, drawAddress))
                    return;
                ResetAnimationControlTime();
                postStopAction?.Invoke();
                // The requested action may itself advance the playback generation.
                generation = _timelinePlaybackGeneration;
            }, delayTicks: 4);

            if(resetSpeedAfterAction)
                await _framework.RunOnTick(() =>
                {
                    if(IsCurrentPlayback(generation, drawAddress))
                        SetOverallSpeedOverrideCore(oldSpeed);
                }, delayTicks: 2);
        }
        catch(Exception ex)
        {
            Brio.Log.Error(ex, "Could not reset animation control time.");
        }
    }

    private unsafe bool IsCurrentPlayback(int generation, nint drawAddress)
        => _gPoseService.IsGPosing && generation == _timelinePlaybackGeneration
            && drawAddress != 0 && TryGetLiveCharacter(out var character)
            && (nint)character.Native()->GameObject.DrawObject == drawAddress;

    private unsafe void ResetAnimationControlTime()
    {
        if(!TryGetLiveCharacter(out var character))
            return;
        var drawObj = character.Native()->GameObject.DrawObject;
        if(drawObj == null || drawObj->Object.GetObjectType() != ObjectType.CharacterBase)
            return;
        var charaBase = (CharacterBase*)drawObj;
        var skeleton = charaBase->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletons == null)
            return;
        for(int p = 0; p < skeleton->PartialSkeletonCount; ++p)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animatedSkele = partial->GetHavokAnimatedSkeleton(0);
            if(animatedSkele == null)
                continue;
            for(int c = 0; c < animatedSkele->AnimationControls.Length; ++c)
            {
                var control = animatedSkele->AnimationControls[c].Value;
                if(control == null || control->hkaAnimationControl.Binding.ptr == null
                    || control->hkaAnimationControl.Binding.ptr->Animation.ptr == null)
                    continue;
                if(control->PlaybackSpeed == 0)
                    control->hkaAnimationControl.LocalTime = 0;
            }
        }
    }

    public void ResetBaseOverride() => ResetBaseOverride(playIdle: true);

    private unsafe void ResetBaseOverride(bool playIdle, bool restoreTimeline = true)
    {
        _timelinePlaybackGeneration++;

        var resetTimeline = false;
        if(_originalBaseAnimation is OriginalBaseAnimation original)
        {
            if(TryGetLiveCharacter(out var character))
            {
                var chara = character.Native();
                chara->Timeline.BaseOverride = restoreTimeline ? original.OriginalTimeline : (ushort)0;
                chara->Mode = original.OriginalMode;
                chara->ModeParam = original.OriginalInput;
            }

            _originalBaseAnimation = null;
            resetTimeline = true;
        }

        RestoreAnimationContext();
        _baseOverrideUsesAnimationContext = false;

        if(playIdle && resetTimeline && !_isDisposed && _gPoseService.IsGPosing && TryGetLiveCharacter(out _))
            PlayTimelineNow(3);
    }

    public bool HasBaseOverride => _originalBaseAnimation != null;

    public unsafe void BlendTimeline(ushort actionTimeline)
    {
        _timelinePlaybackGeneration++;
        if(PrepareAnimationContext(actionTimeline))
            PlayTimelineNow(actionTimeline);
    }

    private unsafe void PlayTimelineNow(ushort timelineId)
    {
        if(_gPoseService.IsGPosing && TryGetLiveCharacter(out var character))
            character.Native()->Timeline.TimelineSequencer.PlayTimeline(timelineId);
    }

    public void Stop()
    {
        _timelinePlaybackGeneration++;

        if(HasBaseOverride)
        {
            ResetBaseOverride();
            ResetOverallSpeedOverride();
        }
        else if(HasAnimationContextOverride)
        {
            RestoreAnimationContext();
            PlayTimelineNow(3);
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
        if(_isDisposed)
            return;
        _timelinePlaybackGeneration++;
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChange;
        _redraw.ActorRedrawEvent -= OnActorRedraw;
        unsafe
        {
            _monitor.CharacterDestroyed -= OnCharacterDestroyed;
            _monitor.CharacterBaseDestroyed -= OnDrawObjectDestroyed;
            _monitor.CharacterBaseMaterialsUpdated -= OnDrawObjectReady;
        }
        SpeedMultiplierOverride = null;
        _actionTimelineSlotSpeedOverrides.Clear();
        ResetBaseOverride(playIdle: false);
        ClearConfiguredAnimationContext();
        _crossRaceAnimations.Remove(Actor.ObjectAddress);
        _crossRaceAnimationEmulationEnabled = false;
        _isDisposed = true;

        base.Dispose();
    }

    public bool ApplyAnimationContext(ActionTimelineContext? context)
    {
        if(!CrossRaceAnimationEmulationEnabled || !TryGetLiveCharacter(out var character)
            || GetAnimationContext() is not { ModelKind: ActionTimelineModelKind.Human } native)
            return context is null;
        if(!_crossRaceAnimations.SetContext(character, native, context))
            return false;
        _activeAnimationContext = context == native ? null : context;
        _redrawAnimationContext = null;
        _needsAnimationContextRebind = false;
        return true;
    }

    public void ConfigureAnimationContext(
        ushort timelineId,
        ActionTimelineContext? context)
    {
        _configuredAnimationTimeline = timelineId;
        _configuredAnimationContext = context;
    }

    public bool PrepareAnimationContext(ushort timelineId)
    {
        if(!_gPoseService.IsGPosing || _drawObjectInvalidated || !TryGetLiveCharacter(out _))
            return false;
        if(IsFacialTimeline(timelineId))
        {
            if(Brio.TryGetService<GameDataProvider>(out var faceData))
                _crossRaceAnimations.MarkNativeTimeline(Actor.ObjectAddress, faceData.ActionTimelineMetadata.Get(timelineId).Key);
            return true;
        }
        if(!CrossRaceAnimationEmulationEnabled)
        {
            RestoreAnimationContext();
            return true;
        }
        var context = _configuredAnimationTimeline == timelineId ? _configuredAnimationContext : null;
        if(context is null && GetAnimationContext() is { ModelKind: ActionTimelineModelKind.Human } native
            && Brio.TryGetService<GameDataProvider>(out var data)
            && data.ActionTimelineMetadata.TryGet(timelineId, out var metadata) && !metadata.Supports(native))
        {
            context = metadata.Contexts.Where(c => c.ModelKind == ActionTimelineModelKind.Human)
                .OrderBy(c => c.ModelId / 100 % 2 == native.ModelId / 100 % 2 ? 0 : 1)
                .ThenBy(c => c.ModelId % 10 == 1 ? 0 : 1).ThenBy(c => c.ModelId).ThenBy(c => c.AnimationVariant)
                .Select(c => (ActionTimelineContext?)c).FirstOrDefault();
        }
        return ApplyAnimationContext(context)
            || RejectPlayback(timelineId, "The selected cross-race playback context could not be applied. See the log for details.");
    }

    private static bool IsFacialTimeline(ushort timelineId)
        => timelineId is >= 626 and <= 634
            || (Brio.TryGetService<TimelineIdentification>(out var identification) && identification.IsFacialExpression(timelineId));

    private bool RejectPlayback(ushort timelineId, string reason)
    {
        var now = Environment.TickCount64;
        if(_lastRejectedPlayback != (timelineId, reason) || now - _lastRejectedPlaybackAt >= 5000)
        {
            _lastRejectedPlayback = (timelineId, reason);
            _lastRejectedPlaybackAt = now;
            Brio.Log.Warning($"Blocked animation {timelineId} on actor {ActorObjectIndex}: {reason}");
            Brio.NotifyError(Localize.Format("Animation {0} was blocked: {1}", timelineId, Localize.Text(reason)));
        }
        return false;
    }

    public void ClearConfiguredAnimationContext()
    {
        _configuredAnimationTimeline = 0;
        _configuredAnimationContext = null;
        RestoreAnimationContext();
    }

    public unsafe ActionTimelineContext? GetAnimationContext(bool preferOriginal = true)
    {
        if(_drawObjectInvalidated || !TryGetLiveCharacter(out var character))
            return null;
        if(preferOriginal && _crossRaceAnimations.GetNativeContext(Actor.ObjectAddress) is { } original)
            return original;
        var characterBase = character.GetCharacterBase();
        if(characterBase is null)
            return null;

        var animationVariant = characterBase->CharacterBase.AnimationVariant;
        var modelType = characterBase->CharacterBase.GetModelType();
        if(modelType == CharacterBase.ModelType.Human)
        {
            var human = (BrioHuman*)characterBase;
            return new(
                ActionTimelineModelKind.Human,
                human->Human.RaceSexId,
                animationVariant);
        }

        var modelCharaId = character.Native()->ModelContainer.ModelCharaId;
        if(modelCharaId <= 0 || !Brio.TryGetService<GameDataProvider>(out var data)
            || !data.GetExcelSheet<ModelChara>().TryGetRow((uint)modelCharaId, out var model)
            || model.Model == 0)
            return null;
        return modelType switch
        {
            CharacterBase.ModelType.Monster => new(ActionTimelineModelKind.Monster, model.Model, animationVariant),
            CharacterBase.ModelType.DemiHuman => new(ActionTimelineModelKind.Demihuman, model.Model, animationVariant),
            _ => null,
        };
    }

    public ActionTimelineContext? GetOriginalAnimationContext() => _crossRaceAnimations.GetNativeContext(Actor.ObjectAddress);

    public void RestoreAnimationContext()
    {
        _crossRaceAnimations.ClearContext(Actor.ObjectAddress);
        _activeAnimationContext = null;
        _redrawAnimationContext = null;
        _needsAnimationContextRebind = false;
    }

    private void InvalidatePlayback(bool endSession = false)
    {
        // Called before native model/character destruction. Do not touch the
        // dying DrawObject or enqueue an idle animation from this callback.
        // An old base timeline may not fit a replacement model. Clear our
        // override instead of transferring its snapshot across a redraw.
        var rebind = _activeAnimationContext ?? _redrawAnimationContext;
        ResetBaseOverride(playIdle: false, restoreTimeline: false);
        _needsAnimationContextRebind = !endSession && _crossRaceAnimationEmulationEnabled;
        _redrawAnimationContext = _needsAnimationContextRebind ? rebind : null;
        if(endSession)
        {
            _crossRaceAnimationEmulationEnabled = false;
            _configuredAnimationTimeline = 0;
            _configuredAnimationContext = null;
        }
        _crossRaceAnimations.Remove(Actor.ObjectAddress);
        SpeedMultiplierOverride = null;
        _actionTimelineSlotSpeedOverrides.Clear();
    }

    private void OnActorRedraw(IGameObject actor, RedrawStage stage)
    {
        if(stage == RedrawStage.Before && actor.Address == Actor.ObjectAddress)
            InvalidatePlayback();
        else if(stage == RedrawStage.After && actor.Address == Actor.ObjectAddress)
        {
            _drawObjectInvalidated = false;
            RebindAnimationContext();
        }
    }

    private unsafe void OnDrawObjectDestroyed(BrioCharacterBase* drawObject)
    {
        if(TryGetLiveCharacter(out var character)
            && character.Native()->GameObject.DrawObject == (DrawObject*)drawObject)
        {
            _drawObjectInvalidated = true;
            InvalidatePlayback();
        }
    }

    private unsafe void OnDrawObjectReady(BrioCharacterBase* drawObject)
    {
        if(TryGetLiveCharacter(out var character)
            && character.Native()->GameObject.DrawObject == (DrawObject*)drawObject)
        {
            _drawObjectInvalidated = false;
            RebindAnimationContext();
        }
    }

    private void RebindAnimationContext()
    {
        if(!_needsAnimationContextRebind || !_crossRaceAnimationEmulationEnabled || !_gPoseService.IsGPosing
            || !TryGetLiveCharacter(out var character) || GetAnimationContext() is not { } native)
            return;
        if(_crossRaceAnimations.SetContext(character, native, _redrawAnimationContext))
        {
            _activeAnimationContext = _redrawAnimationContext;
            _redrawAnimationContext = null;
            _needsAnimationContextRebind = false;
        }
    }

    private unsafe void OnCharacterDestroyed(Character* character)
    {
        if((nint)character != Actor.ObjectAddress)
            return;
        _actorDestroyed = true;
        InvalidatePlayback(endSession: true);
    }

    private void OnGPoseStateChange(bool isGPosing)
    {
        if(!isGPosing)
            InvalidatePlayback(endSession: true);
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
