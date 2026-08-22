using Brio.Entities.Actor;
using Brio.Entities.Core;
using Brio.Game.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Brio.Game.GPose;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Brio.Entities;

public unsafe class EntityActorManager : IDisposable
{
    private readonly EntityManager _entityManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ObjectMonitorService _monitorService;
    private readonly IObjectTable _objects;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly ActorSpawnService _actorSpawnService;
    private readonly GPoseService _gPoseService;

    private bool _initialized;
    private bool _reconcileScheduled;
    private bool _disposed;
    private int _reconcileGeneration;

    public EntityActorManager(EntityManager entityManager, GPoseService gPoseService, ActorSpawnService actorSpawnService, IServiceProvider serviceProvider, ObjectMonitorService monitorService, IObjectTable objects, IFramework framework, IClientState clientState)
    {
        _entityManager = entityManager;
        _gPoseService = gPoseService;
        _serviceProvider = serviceProvider;
        _monitorService = monitorService;
        _objects = objects;
        _framework = framework;
        _clientState = clientState;
        _actorSpawnService = actorSpawnService;

        _monitorService.CharacterInitialized += OnCharacterInitialized;
        _monitorService.CharacterDestroyed += OnCharacterDestroyed;
        _gPoseService.OnGPoseStateChange += OnGPoseStateChanged;
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
    }

    public void Initialize()
    {
        _initialized = true;
        ReconcileActors("initialization");
    }

    private void AttachActor(IGameObject go, Entity parent)
    {
        if(!IsTrackableActor(go))
            return;

        RemoveActorsUsingReusedSlot(go.ObjectIndex, go.Address);

        if(_entityManager.TryGetEntity(new EntityId(go), out var entity))
        {
            // Already attached to the correct parent
            if(parent.Equals(entity.Parent))
                return;
        }
        else
        {
            entity = ActivatorUtilities.CreateInstance<ActorEntity>(_serviceProvider, go);
        }
        entity.SetSpawnFlags(_actorSpawnService.GetSpawnFlagsByIndex((ushort)(go.ObjectIndex - 200)));

        _entityManager.AttachEntity(entity, parent, true);


        // This is ew, but we need to handle companions here for now.
        // This would be a stack overflow but the parenting check above prevents it.
        HandleCompanions(entity, true);
    }

    private void DetachActor(EntityId actorId)
    {
        if(_entityManager.TryGetEntity(actorId, out var entity))
            DetachEntityTree(entity);
    }

    private void HandleCompanions(Entity entity, bool checkParent)
    {
        if(entity is ActorEntity actorEntity)
        {
            var currentActor = actorEntity.GameObject;

            if(currentActor is ICharacter character)
            {
                if(character.HasSpawnedCompanion())
                {
                    var companion = character.Native()->CompanionObject;
                    if(companion != null)
                    {
                        var companionObject = _objects.CreateObjectReference((nint)companion);
                        if(companionObject != null)
                        {
                            AttachActor(companionObject, entity);
                        }
                    }
                    return;
                }

                if(checkParent)
                {
                    var maybeParentId = currentActor.ObjectIndex - 1;
                    if(maybeParentId < 0)
                        return;

                    var maybeParent = _objects[maybeParentId];
                    if(maybeParent == null)
                        return;

                    _entityManager.TryGetEntity(new EntityId(maybeParent), out var maybeParentEntity);

                    if(maybeParentEntity == null)
                        return;

                    HandleCompanions(maybeParentEntity, false);
                }
            }
        }
    }

    private void OnCharacterDestroyed(NativeCharacter* chara)
    {
        if(chara == null)
            return;

        // The native pointer is the entity ID. Looking it up through the object table
        // can fail while the game is finalizing the slot, which previously left a
        // permanently attached ghost actor behind.
        DetachActor(new EntityId($"actor_{(nint)chara}"));
        ScheduleReconcile("character destroyed");
    }

    private void OnCharacterInitialized(NativeCharacter* chara)
    {
        // We wait for one frame on create to ensure that the actor is fully initialized
        var generation = _reconcileGeneration;
        _framework.RunOnTick(() =>
        {
            if(!_initialized || _disposed || generation != _reconcileGeneration || !_gPoseService.IsGPosing)
                return;

            var go = _objects.CreateObjectReference((nint)chara);
            if(go != null)
                AttachActor(go, _entityManager.EntityManagerContainer);
        });
    }

    private bool IsTrackableActor(IGameObject go)
    {
        // GPose object-table slots can transiently contain stale characters during
        // login and character switching. They are not Brio actors unless GPose (or
        // Brio's fake GPose mode) is actually active.
        if(!_gPoseService.IsGPosing)
            return false;

        if(go.Address == nint.Zero || !go.IsValid())
            return false;

        if(go.ObjectIndex is 200 or 0 || !go.IsGPose())
            return false;

        if(go.ObjectKind == ObjectKind.Ornament)
            return false;

        return go.Native()->IsCharacter();
    }

    private void RemoveActorsUsingReusedSlot(ushort objectIndex, nint currentAddress)
    {
        var replacedActors = _entityManager.TryGetAllActors()
            .Where(actor => actor.ObjectIndex == objectIndex && actor.ObjectAddress != currentAddress)
            .ToArray();

        foreach(var actor in replacedActors)
        {
            if(actor.IsAttached)
            {
                Brio.Log.Debug($"Removing replaced actor from GPose slot {objectIndex}: {actor.Id}");
                DetachEntityTree(actor);
            }
        }
    }

    private void ReconcileActors(string reason)
    {
        if(!_initialized || _disposed)
            return;

        var currentActors = _objects
            .Where(IsTrackableActor)
            .GroupBy(actor => actor.Address)
            .Select(group => group.First())
            .OrderBy(actor => actor.ObjectIndex)
            .ToArray();

        var currentByAddress = currentActors.ToDictionary(actor => actor.Address);
        var trackedActors = _entityManager.TryGetAllActors().ToArray();
        var staleActors = trackedActors
            .Where(actor => !currentByAddress.TryGetValue(actor.ObjectAddress, out var current)
                || current.ObjectIndex != actor.ObjectIndex)
            .ToArray();

        var removed = 0;
        foreach(var actor in staleActors)
        {
            if(!actor.IsAttached)
                continue;

            DetachEntityTree(actor);
            removed++;
        }

        var attached = 0;
        foreach(var actor in currentActors)
        {
            if(_entityManager.TryGetEntity(new EntityId(actor), out _))
                continue;

            AttachActor(actor, _entityManager.EntityManagerContainer);
            attached++;
        }

        // Re-establish companion parenting after stale parents or slots were replaced.
        foreach(var actor in currentActors)
        {
            if(_entityManager.TryGetEntity<ActorEntity>(new EntityId(actor), out var entity)
                && entity.Parent == _entityManager.EntityManagerContainer)
            {
                HandleCompanions(entity, true);
            }
        }

        if(removed > 0 || attached > 0)
            Brio.Log.Info($"Reconciled GPose actors after {reason}: removed {removed}, attached {attached}.");
    }

    private void ScheduleReconcile(string reason, int delayTicks = 1)
    {
        if(!_initialized || _disposed || _reconcileScheduled)
            return;

        var generation = _reconcileGeneration;
        _reconcileScheduled = true;
        _framework.RunOnTick(() =>
        {
            if(_disposed || generation != _reconcileGeneration)
                return;

            _reconcileScheduled = false;
            ReconcileActors(reason);
        }, delayTicks: delayTicks);
    }

    private void RemoveAllTrackedActors(string reason)
    {
        if(!_initialized || _disposed)
            return;

        // Invalidate delayed reconciliations so an entry or destruction callback can
        // not reattach objects that are still disappearing after this lifecycle event.
        _reconcileGeneration++;
        _reconcileScheduled = false;

        var trackedActors = _entityManager.TryGetAllActors().ToArray();
        var removed = 0;

        foreach(var actor in trackedActors)
        {
            if(!actor.IsAttached)
                continue;

            DetachEntityTree(actor);
            removed++;
        }

        if(removed > 0)
            Brio.Log.Info($"Removed {removed} tracked GPose actors after {reason}.");
    }

    private void DetachEntityTree(Entity entity)
    {
        foreach(var child in entity.Children.ToArray())
            DetachEntityTree(child);

        _entityManager.RemoveSelectedEntity(entity.Id);
        if(entity.IsAttached)
            _entityManager.DetachEntity(entity, true);
    }

    private void OnGPoseStateChanged(bool newState)
    {
        if(newState)
            ScheduleReconcile("GPose entry", delayTicks: 2);
        else
            RemoveAllTrackedActors("GPose exit");
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        if(_gPoseService.IsGPosing)
            ScheduleReconcile($"territory change to {territoryId}", delayTicks: 2);
        else
            RemoveAllTrackedActors($"territory change to {territoryId}");
    }

    private void OnLogout(int type, int code)
    {
        RemoveAllTrackedActors("logout");
    }


    public void Dispose()
    {
        _disposed = true;
        _reconcileGeneration++;
        _reconcileScheduled = false;
        _monitorService.CharacterInitialized -= OnCharacterInitialized;
        _monitorService.CharacterDestroyed -= OnCharacterDestroyed;
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChanged;
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
    }
}
