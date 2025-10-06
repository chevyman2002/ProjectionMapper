using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ProjectionMapper.Models;
using ProjectionMapper.Rendering;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Simple video service that spawns FFmpegVideoDecoder per layer and forwards frames to the renderer manager.
    /// </summary>
    public sealed class VideoService : IVideoService, IDisposable
    {
        private readonly RendererManager _rendererManager;
        private readonly ConcurrentDictionary<string, (FFmpegVideoDecoder decoder, CancellationTokenSource cts)> _decoders
            = new();

        private readonly string? _ffmpegPath;

        public VideoService(RendererManager rendererManager, string? ffmpegPath = null)
        {
            _rendererManager = rendererManager ?? throw new ArgumentNullException(nameof(rendererManager));
            _ffmpegPath = ffmpegPath;
        }

        public async Task<bool> RegisterLayerAsync(LayerModel layer)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (string.IsNullOrWhiteSpace(layer.SourcePath)) return false;

            // Determine decode size: use layer width/height if set, otherwise some default (e.g., surface size)
            var w = Math.Max(1, layer.Width > 0 ? layer.Width : 640);
            var h = Math.Max(1, layer.Height > 0 ? layer.Height : 480);

            var decoder = new FFmpegVideoDecoder(layer.SourcePath, w, h, _ffmpegPath);
            var cts = new CancellationTokenSource();

            decoder.FrameDecoded += bmp =>
            {
                // Compute dest rect in renderer coordinates
                var destRect = new Rect(layer.X, layer.Y, layer.Width > 0 ? layer.Width : w, layer.Height > 0 ? layer.Height : h);
                // Submit to renderer manager
                // Ensure bitmap is frozen (decoder already freezes)
                _rendererManager.SubmitLayerFrame(layer.Id ?? Guid.NewGuid().ToString("N"), bmp, destRect, layer.Opacity);
            };

            _decoders[layer.Id ?? Guid.NewGuid().ToString("N")] = (decoder, cts);

            // Start decoder on background task
            _ = Task.Run(async () =>
            {
                try
                {
                    await decoder.StartAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // on error, unregister
                    await UnregisterLayerAsync(layer.Id ?? string.Empty).ConfigureAwait(false);
                }
            }, cts.Token);

            return true;
        }

        public Task UnregisterLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return Task.CompletedTask;
            if (_decoders.TryRemove(layerId, out var tuple))
            {
                try
                {
                    tuple.cts.Cancel();
                    tuple.decoder.Dispose();
                    tuple.cts.Dispose();
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            foreach (var kv in _decoders)
            {
                try
                {
                    kv.Value.cts.Cancel();
                    kv.Value.decoder.Dispose();
                    kv.Value.cts.Dispose();
                }
                catch { }
            }
            _decoders.Clear();
        }
    }
}