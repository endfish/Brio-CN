using Brio.Game.Actor.Extensions;
using Brio.Game.Actor.Interop;
using Brio.Game.Core;
using Brio.Game.GPose;
using Brio.Resources;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.STD;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Brio.Game.Actor;

/// <summary>
/// Selects the animation model only inside CharacterBase's motion-pack request.
/// Human.RaceSexId, FaceId, customize data and the actor's skeleton are never changed.
/// </summary>
public sealed unsafe class CrossRaceAnimationService : IDisposable
{
    // CharacterBase::LoadAnimation returns a 28-byte descriptor through its
    // second argument. The signature, v88 getter and staging layout are checked
    // against the client before this service is used; no hard-coded RVA is used.
    internal const string LoadAnimationSignature =
        "40 55 53 57 41 54 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 8B 85 ?? ?? ?? ?? 4C 8B E9 41 8B C8";

    internal delegate nint LoadAnimationDelegate(nint drawObject, nint result, int animationType,
        nint name, uint weapon1, uint weapon2, uint resourceType);
    internal delegate ushort GetAnimationModelDelegate(nint drawObject);

    internal sealed class Session(nint actor, ushort index, ActionTimelineContext native)
    {
        public readonly object Sync = new();
        public readonly nint Actor = actor;
        public readonly ushort Index = index;
        public ActionTimelineContext Native = native;
        public readonly ConcurrentDictionary<(int, string), byte> Failures = new();
        public readonly ConcurrentDictionary<string, byte> NativeTimelines = new(StringComparer.Ordinal);
        public ActionTimelineContext? Playback;
        public volatile bool Destroyed;
        public volatile bool Cancelled;
    }

    private sealed record LoadScope(CrossRaceAnimationService Service, nint DrawObject, ushort Model);
    [ThreadStatic] private static LoadScope? _scope;

    private readonly ConcurrentDictionary<nint, Session> _sessions = new();
    private readonly IGameInteropProvider _interop;
    private readonly IFramework _framework;
    private readonly ObjectMonitorService _monitor;
    private readonly GPoseService _gpose;
    private readonly Hook<LoadAnimationDelegate>? _loadHook;
    private Hook<GetAnimationModelDelegate>? _modelHook;
    private LoadAnimationDelegate? _loadOriginal;
    private GetAnimationModelDelegate? _modelOriginal;
    private volatile bool _disposed;
    private int _activeLoads;
    private int _hooksDisposed;

    public bool IsAvailable => !_disposed && _loadOriginal is not null;

    public CrossRaceAnimationService(IGameInteropProvider interop, ISigScanner scanner,
        IFramework framework, ObjectMonitorService monitor, GPoseService gpose)
    {
        _interop = interop;
        _framework = framework;
        _monitor = monitor;
        _gpose = gpose;
        try
        {
            var address = scanner.ScanText(LoadAnimationSignature);
            var code = new ReadOnlySpan<byte>((void*)address, 0x4E0);
            if(code.IndexOf(new byte[] { 0x49, 0x8D, 0x85, 0xE8, 0x08, 0, 0 }) < 0
                || code.IndexOf(new byte[] { 0xFF, 0x90, 0xC0, 0x02, 0, 0 }) < 0
                || code.IndexOf(new byte[] { 0x8D, 0x4A, 0x48 }) < 0
                || code.IndexOf(new byte[] { 0xF2, 0x0F, 0x11, 0x48, 0x40 }) < 0
                || Marshal.OffsetOf<Human>(nameof(Human.RaceSexId)) != 0xAA0
                || Marshal.OffsetOf<CharacterBase>(nameof(CharacterBase.AnimationVariant)) != 0x958)
                throw new InvalidOperationException("Unexpected animation staging/model-getter layout.");
            _loadHook = interop.HookFromAddress<LoadAnimationDelegate>(address, LoadAnimation);
            _loadOriginal = _loadHook.Original;
            _loadHook.Enable();
        }
        catch(Exception ex)
        {
            _loadHook?.Dispose();
            _loadHook = null;
            _loadOriginal = null;
            Brio.Log.Error(ex, "Could not initialize cross-race motion-pack loading.");
        }
        _monitor.CharacterBaseDestroyed += OnDrawObjectDestroyed;
        _monitor.CharacterDestroyed += OnCharacterDestroyed;
        _monitor.CharacterBaseMaterialsUpdated += OnDrawObjectReady;
        _gpose.OnGPoseStateChange += OnGPoseChanged;
    }

    // Same request pipeline with a supplied native adapter for deterministic
    // tests. The public constructor always installs the verified client hooks.
    internal CrossRaceAnimationService(LoadAnimationDelegate load, GetAnimationModelDelegate model,
        IFramework framework, ObjectMonitorService monitor, GPoseService gpose)
    {
        _interop = null!;
        _framework = framework;
        _monitor = monitor;
        _gpose = gpose;
        _loadOriginal = load;
        _modelOriginal = model;
    }

    public bool SetContext(ICharacter actor, ActionTimelineContext native, ActionTimelineContext? playback)
    {
        if(!IsAvailable || !_gpose.IsGPosing || native.ModelKind != ActionTimelineModelKind.Human || native.ModelId == 0
            || (playback is { } context && (context.ModelKind != ActionTimelineModelKind.Human
                || context.AnimationVariant > byte.MaxValue || context.ModelId == 0)))
            return false;
        var drawObject = actor.GetCharacterBase();
        if(drawObject is null || drawObject->CharacterBase.GetModelType() != CharacterBase.ModelType.Human)
            return false;
        if(!EnsureModelHook((nint)drawObject))
            return false;
        var session = _sessions.GetOrAdd((nint)drawObject, _ => new(actor.Address, actor.ObjectIndex, native));
        lock(session.Sync)
        {
            if(session.Destroyed || session.Actor != actor.Address)
                return false;
            var changed = session.Cancelled || session.Playback != (playback == native ? null : playback);
            if(session.Cancelled)
                session.Native = native;
            session.Playback = playback == native ? null : playback;
            session.Cancelled = false;
            if(changed) session.Failures.Clear();
        }
        return true;
    }

    private bool EnsureModelHook(nint human)
    {
        if(_modelOriginal is not null)
            return true;
        try
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(human), 88 * IntPtr.Size);
            // Human v88 is the model-ID getter used by LoadAnimation. Refuse a
            // mismatched layout instead of hooking an unrelated virtual method.
            var code = new ReadOnlySpan<byte>((void*)address, 8);
            if(!code.SequenceEqual(new byte[] { 0x0F, 0xB7, 0x81, 0xA0, 0x0A, 0, 0, 0xC3 }))
                throw new InvalidOperationException("Unexpected Human animation model getter.");
            _modelHook = _interop.HookFromAddress<GetAnimationModelDelegate>(address, GetAnimationModel);
            _modelOriginal = _modelHook.Original;
            _modelHook.Enable();
            return true;
        }
        catch(Exception ex)
        {
            _modelHook?.Dispose();
            _modelHook = null;
            _modelOriginal = null;
            Brio.Log.Error(ex, "Could not initialize the animation-only model getter.");
            return false;
        }
    }

    public ActionTimelineContext? GetPlaybackContext(nint actor)
    {
        foreach(var session in _sessions.Values)
            if(session.Actor == actor && !session.Destroyed && !session.Cancelled)
                lock(session.Sync) return session.Playback;
        return null;
    }

    public ActionTimelineContext? GetNativeContext(nint actor)
    {
        foreach(var session in _sessions.Values)
            if(session.Actor == actor && !session.Destroyed && !session.Cancelled)
                lock(session.Sync) return session.Native;
        return null;
    }

    public void ClearContext(nint actor)
    {
        foreach(var session in _sessions.Values)
            if(session.Actor == actor)
                lock(session.Sync) session.Playback = null;
    }

    public void Remove(nint actor)
    {
        foreach(var session in _sessions.Values)
            if(session.Actor == actor)
                lock(session.Sync) session.Cancelled = true;
    }

    public void MarkNativeTimeline(nint actor, string key)
    {
        if(string.IsNullOrEmpty(key)) return;
        foreach(var session in _sessions.Values)
            if(session.Actor == actor && !session.Cancelled && !session.Destroyed && session.NativeTimelines.Count < 1024)
                session.NativeTimelines.TryAdd(key, 0);
    }

    private ushort GetAnimationModel(nint drawObject)
        => _scope is { } scope && scope.Service == this && scope.DrawObject == drawObject
            ? scope.Model : _modelOriginal!(drawObject);

    private nint LoadAnimation(nint drawObject, nint result, int animationType,
        nint name, uint weapon1, uint weapon2, uint resourceType)
    {
        Interlocked.Increment(ref _activeLoads);
        try { return LoadAnimationCore(drawObject, result, animationType, name, weapon1, weapon2, resourceType); }
        finally
        {
            if(Interlocked.Decrement(ref _activeLoads) == 0 && _disposed)
                FinishDispose();
        }
    }

    private nint LoadAnimationCore(nint drawObject, nint result, int animationType,
        nint name, uint weapon1, uint weapon2, uint resourceType)
    {
        if(!_sessions.TryGetValue(drawObject, out var session))
            return _loadOriginal!(drawObject, result, animationType, name, weapon1, weapon2, resourceType);
        lock(session.Sync)
        {
            if(session.Destroyed)
            {
                if(result != 0) ((AnimationLoadResult*)result)->SetEmpty();
                return result;
            }
            var previousScope = _scope;
            var characterBase = (CharacterBase*)drawObject;
            var entryVariant = characterBase->AnimationVariant;
            // 3/4 are nonresident facial requests in the native loader. They
            // must retain the actor's real model, face and animation variant,
            // even when invoked recursively by a cross-race body timeline.
            var requestName = ReadName(name);
            var facial = animationType is 3 or 4 || session.NativeTimelines.ContainsKey(requestName)
                || requestName.StartsWith("facial/", StringComparison.Ordinal)
                || requestName.StartsWith("speak/", StringComparison.Ordinal);
            var playback = facial || session.Cancelled || _disposed ? null : session.Playback;
            _scope = playback is { } donor ? new(this, drawObject, donor.ModelId) : null;
            var variant = playback?.AnimationVariant
                ?? (facial || (previousScope is { } outer && outer.Service == this && outer.DrawObject == drawObject)
                    ? session.Native.AnimationVariant : entryVariant);
            characterBase->AnimationVariant = (byte)variant;
            if(result != 0) ((AnimationLoadResult*)result)->SetEmpty();
            try
            {
                var returned = _loadOriginal!(drawObject, result, animationType, name, weapon1, weapon2, resourceType);
                if(!session.Destroyed && returned == result && result != 0)
                {
                    try
                    {
                        if(RemoveFailedRequest(characterBase, (AnimationLoadResult*)result))
                            ReportFailedRequest(session, animationType, requestName);
                    }
                    catch(Exception ex) { Brio.Log.Error(ex, "Could not clean a failed animation resource request."); }
                }
                return returned;
            }
            catch(Exception ex)
            {
                // Never unwind a managed adapter/hook exception through the
                // game's native timeline executor. Preserve other channels.
                if(!session.Destroyed && result != 0)
                {
                    try { RemoveFailedRequest(characterBase, (AnimationLoadResult*)result); }
                    catch(Exception cleanupError) { Brio.Log.Error(cleanupError, "Could not clean animation staging after a loader exception."); }
                    ((AnimationLoadResult*)result)->SetEmpty();
                }
                Brio.Log.Error(ex, "Animation resource loading failed.");
                ReportFailedRequest(session, animationType, requestName);
                return result;
            }
            finally
            {
                _scope = previousScope;
                // Destruction invalidates the lease before native memory is freed.
                if(!session.Destroyed)
                    characterBase->AnimationVariant = entryVariant;
            }
        }
    }

    // The staging map is std::map<uint64_t, PendingAnimation>. Native node size
    // is 0x48: key at 0x20, POD value at 0x28, ResourceHandle* at 0x40.
    // The crash dereferenced that pointer without checking it. Remove only the
    // failed request, never the timeline or other successfully loaded channels.
    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    internal struct PendingAnimation
    {
        [FieldOffset(0x18)] public nint Resource;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct AnimationLoadResult
    {
        public ulong Key;
        public uint Model, Variant, Weapon1, Weapon2, Type;

        public void SetEmpty()
        {
            Key = 0;
            Model = uint.MaxValue;
            Variant = 1;
            Weapon1 = Weapon2 = 0x656D70;
            Type = 0;
        }
    }

    internal static bool RemoveFailedRequest(CharacterBase* characterBase, AnimationLoadResult* result)
    {
        var pending = (StdMap<ulong, PendingAnimation>*)((byte*)characterBase + 0x8E8);
        if(result->Model == uint.MaxValue || pending->LongCount is <= 0 or > 16384
            || !pending->TryGetValue(result->Key, out var entry, copyCtor: false) || entry.Resource != 0)
            return false;
        pending->Remove(result->Key);
        result->SetEmpty();
        return true;
    }

    private void ReportFailedRequest(Session session, int type, string path)
    {
        if(session.Failures.Count >= 128 || !session.Failures.TryAdd((type, path), 0))
            return;
        Brio.Log.Warning($"Skipped failed animation resource request: actor {session.Index}, type {type}, name {path}.");
        try
        {
            _ = _framework.RunOnTick(() =>
            {
                if(!_disposed && !session.Destroyed && _gpose.IsGPosing)
                    Brio.NotifyError(Localize.Format("Animation resource {0} could not be loaded. Only this request was skipped; other animation channels continue.", path));
            });
        }
        catch(Exception ex) { Brio.Log.Error(ex, "Could not schedule an animation failure notice."); }
    }

    private static string ReadName(nint name)
    {
        if(name == 0) return "?";
        var length = 0;
        while(length < 256 && ((byte*)name)[length] != 0) length++;
        return System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)name, length));
    }

    private void OnDrawObjectDestroyed(BrioCharacterBase* drawObject)
    {
        if(_sessions.TryRemove((nint)drawObject, out var session))
            session.Destroyed = true;
    }

    private void OnCharacterDestroyed(FFXIVClientStructs.FFXIV.Client.Game.Character.Character* actor)
    {
        foreach(var (draw, session) in _sessions)
            if(session.Actor == (nint)actor && _sessions.TryRemove(draw, out _))
                session.Destroyed = true;
    }

    private void OnDrawObjectReady(BrioCharacterBase* drawObject)
    {
        if(!_sessions.TryGetValue((nint)drawObject, out var session)
            || System.Threading.Monitor.IsEntered(session.Sync))
            return;
        lock(session.Sync)
        {
            if(!session.Destroyed && drawObject->CharacterBase.GetModelType() == CharacterBase.ModelType.Human)
                session.Native = new(ActionTimelineModelKind.Human, ((Human*)drawObject)->RaceSexId,
                    drawObject->CharacterBase.AnimationVariant);
        }
    }

    private void OnGPoseChanged(bool active)
    {
        if(!active)
            foreach(var session in _sessions.Values)
                lock(session.Sync) session.Cancelled = true;
    }

    public void Dispose()
    {
        _disposed = true;
        _loadHook?.Disable();
        // A resource callback can trigger unload while inside Original. Let its
        // finally restore the per-request variant before releasing trampolines
        // or lifetime subscriptions; never wait on our own native stack.
        if(Volatile.Read(ref _activeLoads) == 0)
            FinishDispose();
    }

    private void FinishDispose()
    {
        if(Interlocked.Exchange(ref _hooksDisposed, 1) != 0)
            return;
        _sessions.Clear();
        _monitor.CharacterBaseDestroyed -= OnDrawObjectDestroyed;
        _monitor.CharacterDestroyed -= OnCharacterDestroyed;
        _monitor.CharacterBaseMaterialsUpdated -= OnDrawObjectReady;
        _gpose.OnGPoseStateChange -= OnGPoseChanged;
        _modelHook?.Dispose();
        _loadHook?.Dispose();
    }
}
