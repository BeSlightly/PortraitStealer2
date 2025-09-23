using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PortraitStealer.Windows.Controls;

public class CachedPortraitDisplay : IDisposable
{
    public static readonly Vector2 PortraitSize = new(576, 960);
    private static readonly int TextureCacheLimit = 10;

    private readonly IPluginLog _log;
    private readonly ITextureProvider _textureProvider;

    private CachedPortraitData? _currentDataForDisplay;
    private string? _requestedImagePathForLoad;

    private bool _isLoading;
    private bool _loadFailed;
    private IDalamudTextureWrap? _activeTextureWrap;

    private CancellationTokenSource? _currentLoadOperationCts;

    private sealed record TextureCacheEntry(
        IDalamudTextureWrap TextureWrap,
        LinkedListNode<string> LruNode
    );

    private readonly Dictionary<string, TextureCacheEntry> _textureCache = new();
    private readonly LinkedList<string> _lruList = new();
    private readonly object _textureCacheLock = new();

    public CachedPortraitDisplay(IPluginLog log, ITextureProvider textureProvider)
    {
        _log = log;
        _textureProvider = textureProvider;
    }

    public void Dispose()
    {
        CancelCurrentLoadOperation(true);
        ClearTextureLruCache();
        _activeTextureWrap = null;
        _currentDataForDisplay = null;
        _requestedImagePathForLoad = null;
    }

    private void ClearTextureLruCache()
    {
        lock (_textureCacheLock)
        {
            foreach (var kvp in _textureCache)
            {
                kvp.Value.TextureWrap?.Dispose();
            }
            _textureCache.Clear();
            _lruList.Clear();
        }
    }

    private void CancelCurrentLoadOperation(bool disposeCts = false)
    {
        if (_currentLoadOperationCts != null)
        {
            if (!_currentLoadOperationCts.IsCancellationRequested)
            {
                _currentLoadOperationCts.Cancel();
            }
            if (disposeCts)
            {
                _currentLoadOperationCts.Dispose();
                _currentLoadOperationCts = null;
            }
        }
        // _activeLoadTask is not explicitly cancelled here; the CancellationToken handles it.
        // We don't join or wait for _activeLoadTask to prevent blocking SetData.
    }

    private void SetActiveTexture(
        IDalamudTextureWrap? newTexture,
        CachedPortraitData? associatedData
    )
    {
        _activeTextureWrap = newTexture;
        _currentDataForDisplay = associatedData; // Keep track of what data this texture represents
    }

    private bool TryGetFromLruCache(string imagePath, out IDalamudTextureWrap? textureWrap)
    {
        lock (_textureCacheLock)
        {
            if (_textureCache.TryGetValue(imagePath, out var entry))
            {
                _lruList.Remove(entry.LruNode);
                _lruList.AddFirst(entry.LruNode);
                textureWrap = entry.TextureWrap;
                return true;
            }
        }
        textureWrap = null;
        return false;
    }

    private void AddToLruCache(string imagePath, IDalamudTextureWrap textureWrap)
    {
        lock (_textureCacheLock)
        {
            if (_textureCache.TryGetValue(imagePath, out var existingEntry))
            {
                _lruList.Remove(existingEntry.LruNode);
                _textureCache.Remove(imagePath);
                existingEntry.TextureWrap?.Dispose();
            }

            // Skip eviction if this is the currently active texture
            if (_textureCache.Count >= TextureCacheLimit && _activeTextureWrap != textureWrap)
            {
                var lruNodeToRemove = _lruList.Last;
                if (lruNodeToRemove != null)
                {
                    string lruPath = lruNodeToRemove.Value;
                    if (_textureCache.Remove(lruPath, out var evictedEntry))
                    {
                        if (evictedEntry.TextureWrap != _activeTextureWrap)
                        {
                            evictedEntry.TextureWrap?.Dispose();
                            _lruList.RemoveLast();
                        }
                        else
                        {
                            _lruList.Remove(evictedEntry.LruNode);
                            _lruList.AddFirst(evictedEntry.LruNode);
                        }
                    }
                }
            }

            var newNode = new LinkedListNode<string>(imagePath);
            _lruList.AddFirst(newNode);
            _textureCache[imagePath] = new TextureCacheEntry(textureWrap, newNode);
        }
    }

    public void SetData(CachedPortraitData? data)
    {
        var newImagePath = data?.TemporaryImagePath;

        if (
            newImagePath == _requestedImagePathForLoad
            && (
                (_isLoading && data?.Timestamp == _currentDataForDisplay?.Timestamp)
                ||
                (
                    !_isLoading
                    && _activeTextureWrap != null
                    && data?.Timestamp == _currentDataForDisplay?.Timestamp
                )
            )
        )
        {
            return;
        }

        CancelCurrentLoadOperation();

        _requestedImagePathForLoad = newImagePath;
        _currentDataForDisplay = data;
        _loadFailed = false;

        if (string.IsNullOrEmpty(newImagePath))
        {
            SetActiveTexture(null, null);
            _isLoading = false;
            return;
        }

        if (TryGetFromLruCache(newImagePath, out var cachedWrap))
        {
            SetActiveTexture(cachedWrap, data);
            _isLoading = false;
            _loadFailed = false;
            _requestedImagePathForLoad = newImagePath;
            return;
        }

        _isLoading = true;
        var newCts = new CancellationTokenSource();
        _currentLoadOperationCts = newCts;

        var pathForThisLoad = newImagePath;
        var dataForThisLoad = data;

        Task.Run(async () =>
        {
            try
            {
                if (
                    newCts.Token.IsCancellationRequested
                    || _requestedImagePathForLoad != pathForThisLoad
                )
                {
                    return;
                }
                await LoadImageInternal(pathForThisLoad, dataForThisLoad, newCts.Token);
            }
            finally
            {
                newCts.Dispose();
                if (
                    Interlocked.CompareExchange(ref _currentLoadOperationCts, null, newCts)
                    == newCts
                )
                {
                }
                else
                {
                }
            }
        });
    }

    private async Task LoadImageInternal(
        string imagePath,
        CachedPortraitData? associatedData,
        CancellationToken token
    )
    {
        bool success = false;
        IDalamudTextureWrap? loadedTexture = null;

        try
        {
            token.ThrowIfCancellationRequested();

            if (TryGetFromLruCache(imagePath, out var cachedWrapOnEntry))
            {
                loadedTexture = cachedWrapOnEntry;
                success = true;
            }
            else if (File.Exists(imagePath))
            {
                byte[]? rentedPixelData = null;
                try
                {
                    using var image = await Image.LoadAsync<Bgra32>(imagePath, token);
                    token.ThrowIfCancellationRequested();

                    int pixelDataSize = 4 * image.Width * image.Height;
                    rentedPixelData = ArrayPool<byte>.Shared.Rent(pixelDataSize);
                    image.CopyPixelDataTo(rentedPixelData.AsSpan(0, pixelDataSize));
                    token.ThrowIfCancellationRequested();

                    loadedTexture = _textureProvider.CreateFromRaw(
                        RawImageSpecification.Bgra32(image.Width, image.Height),
                        rentedPixelData.AsSpan(0, pixelDataSize)
                    );
                    token.ThrowIfCancellationRequested();

                    if (loadedTexture == null) throw new Exception("TextureProvider failed to create texture wrap.");

                    AddToLruCache(imagePath, loadedTexture);
                    success = true;
                }
                finally
                {
                    if (rentedPixelData != null)
                        ArrayPool<byte>.Shared.Return(rentedPixelData);
                }
            }
            else
            {
                _log.Warning($"File not found in LoadImageInternal: {imagePath}");
            }
        }
        catch (OperationCanceledException)
        {
            loadedTexture?.Dispose();
            return;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"LoadImageInternal: Failed to load/create texture: {imagePath}");
            loadedTexture?.Dispose();
            loadedTexture = null;
            success = false;
        }

        if (_requestedImagePathForLoad == imagePath && !token.IsCancellationRequested)
        {
            if (success && loadedTexture != null)
            {
                SetActiveTexture(loadedTexture, associatedData);
                _loadFailed = false;
            }
            else
            {
                SetActiveTexture(null, associatedData);
                _loadFailed = true;
            }
            _isLoading = false;
        }
        else if (loadedTexture != null && !success)
        {
            if (!success)
                loadedTexture?.Dispose();
        }
    }

    public void Draw(Vector2 availableSize)
    {
        if (_activeTextureWrap != null)
        {
            lock (_textureCacheLock)
            {
                var kvp = _textureCache.FirstOrDefault(x => x.Value.TextureWrap == _activeTextureWrap);
                if (kvp.Value != null)
                {
                    _lruList.Remove(kvp.Value.LruNode);
                    _lruList.AddFirst(kvp.Value.LruNode);
                }
            }
        }
        if (availableSize.X <= 0 || availableSize.Y <= 0)
            return;
        float aspectRatio = PortraitSize.X / PortraitSize.Y;
        float targetWidth = availableSize.X;
        float targetHeight = targetWidth / aspectRatio;
        if (targetHeight > availableSize.Y)
        {
            targetHeight = availableSize.Y;
            targetWidth = targetHeight * aspectRatio;
        }
        Vector2 displaySize = new Vector2(targetWidth, targetHeight);
        Vector2 currentCursorPos = ImGui.GetCursorPos();
        Vector2 imageTopLeft = currentCursorPos + (availableSize - displaySize) / 2f;
        ImGui.SetCursorPos(imageTopLeft);
        var textureToDraw = _activeTextureWrap;
        if (_isLoading && (textureToDraw == null || textureToDraw.Handle == IntPtr.Zero))
        {
            DrawLoadingSpinner(
                ImGui.GetWindowPos()
                    - new Vector2(ImGui.GetScrollX(), ImGui.GetScrollY())
                    + imageTopLeft
                    + displaySize / 2f,
                Math.Min(displaySize.X, displaySize.Y) * 0.08f
            );
        }
        else if (_loadFailed)
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            var icon = FontAwesomeIcon.TimesCircle.ToIconString();
            var iconSize = ImGui.CalcTextSize(icon);
            ImGui.SetCursorPos(imageTopLeft + (displaySize - iconSize) / 2f);
            ImGui.TextUnformatted(icon);
        }
        else if (textureToDraw != null && textureToDraw.Handle != IntPtr.Zero)
        {
            try
            {
                ImGui.Image(textureToDraw.Handle, displaySize);
            }
            catch (ObjectDisposedException odEx)
            {
                _log.Error(
                    odEx,
                    $"Attempted to draw a disposed texture. Path: {_requestedImagePathForLoad}"
                );
                _activeTextureWrap = null;
                if (_requestedImagePathForLoad != null)
                {
                    lock (_textureCacheLock)
                    {
                        if (_textureCache.Remove(_requestedImagePathForLoad, out var badEntry))
                        {
                            _lruList.Remove(badEntry.LruNode);
                        }
                    }
                }
            }
        }
        else
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            var text = "No Portrait";
            if (_requestedImagePathForLoad != null && !_isLoading && !_loadFailed)
                text = "Image Error";
            var textSize = ImGui.CalcTextSize(text);
            ImGui.SetCursorPos(imageTopLeft + (displaySize - textSize) / 2f);
            ImGui.TextUnformatted(text);
        }
        ImGui.SetCursorPos(currentCursorPos + new Vector2(0, availableSize.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    private static void DrawLoadingSpinner(Vector2 center, float radius = 10f)
    {
        // ... DrawLoadingSpinner remains the same ...
        float thickness = Math.Max(2f, radius * 0.2f) * ImGuiHelpers.GlobalScale;
        radius *= ImGuiHelpers.GlobalScale;
        var numSegments = 12;
        var time = (float)ImGui.GetTime();
        var drawList = ImGui.GetWindowDrawList();
        float startAngle = time * 4f;
        for (int i = 0; i < numSegments; i++)
        {
            float segmentAngle = (float)(2.0 * Math.PI / numSegments);
            float angle1 = startAngle + i * segmentAngle;
            float angle2 = startAngle + (i + 0.7f) * segmentAngle;
            float alpha = (float)Math.Sin((i / (float)numSegments) * Math.PI);
            alpha = Math.Max(0.1f, alpha * alpha);
            var color = ImGui.GetColorU32(new Vector4(0.8f, 0.8f, 0.8f, alpha));
            drawList.PathArcTo(center, radius, angle1, angle2, numSegments / 2);
            drawList.PathStroke(color, ImDrawFlags.None, thickness);
        }
    }
}
