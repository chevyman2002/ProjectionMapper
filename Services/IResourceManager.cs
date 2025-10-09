using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Manages project resources such as images and videos.
    /// This simple abstraction lets the UI request information about resources without touching file I/O directly.
    /// </summary>
    public interface IResourceManager : IDisposable
    {
        /// <summary>
        /// Register a resource (file path) with the manager. Returns a resource id or key.
        /// </summary>
        string RegisterResource(string filePath);

        /// <summary>
        /// Check whether a registered resource exists on disk.
        /// </summary>
        bool Exists(string resourceId);

        /// <summary>
        /// Get a path for a registered resource.
        /// </summary>
        string? GetPath(string resourceId);

        /// <summary>
        /// Request a thumbnail for the resource. Returns a path to a thumbnail file or null if none available.
        /// This method may return a placeholder synchronously while a background extraction runs in some implementations.
        /// </summary>
        Task<string?> GetThumbnailAsync(string resourceId, int width = 256, int height = 256, CancellationToken token = default);
    }
}