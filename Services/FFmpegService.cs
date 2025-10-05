using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Skeleton for an FFmpeg-based decoding service.
    /// This class is the high-level entry point which will:
    /// - manage FFmpeg process/native library initialization,
    /// - open media files,
    /// - run decode loops on background threads,
    /// - push decoded frames to a thread-safe queue for the renderer.
    ///
    /// NOTE: The actual FFmpeg.AutoGen bindings require careful native setup and are not implemented here yet.
    /// This class provides the async/cancellation-friendly surface for integration.
    /// </summary>
    public sealed class FFmpegService : IDisposable
    {
        private bool _disposed;

        public FFmpegService()
        {
            // TODO: Accept logger + configuration via constructor for DI
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            // TODO: call av_register_all / avcodec_register_all if required by the FFmpeg wrapper and initialize hw accel
            return Task.CompletedTask;
        }

        /// <summary>
        /// Open a media file and start decoding in the background.
        /// For now this is a placeholder that completes immediately.
        /// </summary>
        public Task StartDecodingAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required", nameof(path));

            // TODO: create background decode loop which pushes frames to a ConcurrentQueue and returns when stopped
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // TODO: free native resources
        }
    }
}