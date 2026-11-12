using Brio.Services.Poses;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Brio.UI.Controls.Stateless;

// The UI thread owns the texture cache. Workers produce only managed pixels;
// eviction, selection changes and unload can never leave an upload in flight.
internal sealed class PoseBodyPreviewCache(Func<int, int, byte[], IDalamudTextureWrap> upload) : IDisposable
{
    public PoseBodyPreviewCache(ITextureProvider textures)
        : this((w, h, pixels) => textures.CreateFromRaw(RawImageSpecification.Rgba32(w, h), pixels, "Brio-CN Pose Mannequin"))
    {
    }

    private const int ThumbnailLimit = 32;
    private readonly record struct Key(string Path, long Stamp);
    private sealed class Slot
    {
        public Task<byte[]?>? Pending;
        public PoseBodyPreview.Camera Requested;
        public PoseBodyPreview.Camera? Completed;
        public IDalamudTextureWrap? Texture;
        public long LastUsed;
    }

    private readonly Dictionary<Key, Slot> _thumbnails = [];
    private readonly List<IDalamudTextureWrap> _retired = [];
    private Slot _detail = new();
    private Task<byte[]?>? _thumbnailJob, _detailJob;
    private CancellationTokenSource? _session;
    private Key? _detailKey;
    private long _frame, _lastDetailStart;
    private bool _thumbnailUploaded;
    private bool _uploadFailed, _disposed;

    public bool IsEnabled => _session is not null;
    public bool HasFailed => _uploadFailed;

    public void SetEnabled(bool enable)
    {
        if(_disposed) return;
        if(!enable) _uploadFailed = false;
        if(enable == IsEnabled) return;
        if(enable)
        {
            _uploadFailed = false;
            _lastDetailStart = 0;
            _session = new();
            return;
        }

        var session = _session!;
        _session = null;
        session.Cancel();
        session.Dispose();
        ClearDetail();
        foreach(var slot in _thumbnails.Values) Retire(slot);
        _thumbnails.Clear();
        // CreateFromRaw textures defer their actual release until the current
        // Dalamud frame ends, even if the window is closed during that frame.
        ReleaseRetired();
        // Keep the in-flight task references until completion so immediately
        // re-enabling does not exceed the worker limits. Their slots are gone.
    }

    public void BeginFrame()
    {
        _frame++;
        _thumbnailUploaded = false;
        ReleaseRetired();
        Prune(ref _thumbnailJob);
        Prune(ref _detailJob);

        static void Prune(ref Task<byte[]?>? task)
        {
            if(task?.IsCompleted != true) return;
            _ = task.Exception; // Observe abandoned jobs as well as current slots.
            task = null;
        }
    }

    public IDalamudTextureWrap? Thumbnail(PoseAssetEntry entry, LibraryPoseDocument? pose)
    {
        if(_session is not { } session || _uploadFailed || !HasBody(entry.Metadata.Kind)) return null;
        var key = new Key(entry.Path, entry.Stamp);
        if(!_thumbnails.TryGetValue(key, out var slot))
        {
            if(pose is null || _thumbnailJob is not null) return null;
            if(_thumbnails.Count >= ThumbnailLimit)
            {
                var old = _thumbnails.MinBy(static p => p.Value.LastUsed);
                // Don't evict a texture already sent to this frame's draw list.
                if(old.Value.LastUsed == _frame) return null;
                Retire(old.Value);
                _thumbnails.Remove(old.Key);
            }
            _thumbnails.Add(key, slot = new());
        }
        slot.LastUsed = _frame;
        if(!_thumbnailUploaded && Finish(slot)) _thumbnailUploaded = true;
        if(pose is not null && !_uploadFailed && slot.Pending is null && slot.Completed is null && _thumbnailJob is null)
            _thumbnailJob = Start(slot, pose, new(136, 136, PoseBodyPreview.DefaultYaw, PoseBodyPreview.DefaultPitch, 1), session.Token);
        return slot.Texture;
    }

    public IDalamudTextureWrap? Detail(PoseAssetEntry entry, LibraryPoseDocument? pose, PoseAssetKind kind, Vector2 size, PosePreviewView view)
    {
        if(_session is not { } session || _uploadFailed || !HasBody(kind))
        {
            ClearDetail();
            return null;
        }
        var key = new Key(entry.Path, entry.Stamp);
        if(_detailKey != key)
        {
            ClearDetail();
            _detailKey = key;
        }
        Finish(_detail);
        if(_uploadFailed) return null;
        // If there was no renderable body, a different camera won't help.
        if(_detail.Completed is not null && _detail.Texture is null) return null;
        var width = Math.Clamp((int)MathF.Round(size.X / 8) * 8, 96, 384);
        var camera = new PoseBodyPreview.Camera(width, 240, view.Yaw, view.Pitch, view.Zoom);
        var now = Stopwatch.GetTimestamp();
        if(pose is not null && _detailJob is null && _detail.Pending is null && _detail.Completed != camera
            && (_lastDetailStart == 0 || now - _lastDetailStart >= Stopwatch.Frequency / 24))
        {
            _lastDetailStart = now;
            _detailJob = Start(_detail, pose, camera, session.Token);
        }
        return _detail.Texture;
    }

    internal static bool HasBody(PoseAssetKind kind) => kind is PoseAssetKind.Body or PoseAssetKind.Complete;

    private static Task<byte[]?> Start(Slot slot, LibraryPoseDocument pose, PoseBodyPreview.Camera camera, CancellationToken cancellation)
    {
        slot.Requested = camera;
        return slot.Pending = Task.Run(() => PoseBodyPreview.Render(pose, camera, cancellation), cancellation);
    }

    private bool Finish(Slot slot)
    {
        if(slot.Pending is not { IsCompleted: true } job) return false;
        slot.Pending = null;
        slot.Completed = slot.Requested;
        try
        {
            if(job.GetAwaiter().GetResult() is not { } pixels) return false;
            var (w, h, _, _, _) = slot.Requested;
            var replacement = upload(w, h, pixels);
            Retire(slot);
            slot.Texture = replacement;
            return true;
        }
        catch(Exception ex)
        {
            SetEnabled(false);
            _uploadFailed = true;
            Brio.Log.Warning(ex, "Pose mannequin preview failed; using the skeleton preview.");
            return false;
        }
    }

    private void Retire(Slot slot)
    {
        if(slot.Texture is { } texture)
            _retired.Add(texture);
        slot.Texture = null;
    }

    public void ClearDetail()
    {
        if(_detailKey is null) return;
        Retire(_detail);
        _detail = new();
        _detailKey = null;
    }

    public void Dispose()
    {
        if(_disposed) return;
        SetEnabled(false);
        _disposed = true;
        ReleaseRetired();
    }

    private void ReleaseRetired()
    {
        foreach(var texture in _retired) texture.Dispose();
        _retired.Clear();
    }
}
