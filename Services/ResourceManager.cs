using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Minimal ResourceManager that tracks registered file paths and offers basic thumbnail stub.
    /// For images it returns the original path (quick path); for video it returns null (thumbnail extraction to be implemented).
    /// This implementation is intentionally conservative and synchronous-safe.
    /// </summary>
    public sealed class ResourceManager : IResourceManager
    {
        // resourceId -> filePath
        private readonly ConcurrentDictionary<string, string> _resources = new();

        // thumbnail cache: resourceId -> thumbnailPath (temporary in-memory cache)
        private readonly ConcurrentDictionary<string, string?> _thumbnailCache = new();

        public string RegisterResource(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentNullException(nameof(filePath));
            var id = Guid.NewGuid().ToString("N");
            _resources[id] = filePath;
            return id;
        }

        public bool Exists(string resourceId)
        {
            return resourceId != null && _resources.TryGetValue(resourceId, out var path) && File.Exists(path);
        }

        public string? GetPath(string resourceId)
        {
            if (resourceId == null) return null;
            return _resources.TryGetValue(resourceId, out var path) ? path : null;
        }

        public async Task<string?> GetThumbnailAsync(string resourceId, int width = 256, int height = 256, CancellationToken token = default)
        {
            if (resourceId == null) return null;
            if (!_resources.TryGetValue(resourceId, out var path)) return null;
            if (!File.Exists(path)) return null;

            // If cached, return immediately
            if (_thumbnailCache.TryGetValue(resourceId, out var cached) && !string.IsNullOrEmpty(cached) && File.Exists(cached))
            {
                return cached;
            }

            // Quick heuristic: if image file, return path (UI can load scaled bitmap); for video, return null (TODO: extract thumbnail via FFmpeg)
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
            {
                _thumbnailCache[resourceId] = path;
                return await Task.FromResult(path).ConfigureAwait(false);
            }

            // For video and other media, we should extract a frame using FFmpeg in the future.
            // For now return null to indicate no thumbnail.
            await Task.CompletedTask.ConfigureAwait(false);
            return null;
        }

        public void Dispose()
        {
            _resources.Clear();
            _thumbnailCache.Clear();
        }
    }
}