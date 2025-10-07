using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        private readonly ConcurrentDictionary<string, (FFmpegVideoDecoder decoder, CancellationTokenSource cts, LayerModel model)> _decoders
            = new();

        private readonly string? _ffmpegPath;

        public VideoService(RendererManager rendererManager, string? ffmpegPath = null)
        {
            _rendererManager = rendererManager ?? throw new ArgumentNullException(nameof(rendererManager));
            _ffmpegPath = ffmpegPath;
        }

        /// <summary>
        /// Event raised when a decoder produces a new frame for a layer. Parameters: layerId, frozen BitmapSource (may be null).
        /// </summary>
        public event Action<string, BitmapSource?>? FrameDecoded;

        /// <summary>
        /// Register a layer with an associated video source and start decoding.
        /// Throws if ffmpeg is not available or starting the decoder fails.
        /// </summary>
        public async Task<bool> RegisterLayerAsync(LayerModel layer)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (string.IsNullOrWhiteSpace(layer.SourcePath)) return false;
            if (!File.Exists(layer.SourcePath)) throw new FileNotFoundException("Source video not found", layer.SourcePath);

            // Ensure ffmpeg is available before creating the decoder
            string ffmpegExecutable = ResolveFfmpegExecutable();

            // Determine decode size: use layer width/height if set, otherwise some default (e.g., surface size)
            var w = Math.Max(1, layer.Width > 0 ? layer.Width : 640);
            var h = Math.Max(1, layer.Height > 0 ? layer.Height : 480);

            var decoder = new FFmpegVideoDecoder(layer.SourcePath, w, h, ffmpegExecutable);
            var cts = new CancellationTokenSource();

            decoder.FrameDecoded += bmp =>
            {
                try
                {
                    BitmapSource bmpToSubmit = bmp;

                    // Apply rotation if requested on the layer model
                    try
                    {
                        if (Math.Abs(layer.RotationDegrees) > 1e-6 && bmp != null)
                        {
                            bmpToSubmit = RotateBitmap(bmp, layer.RotationDegrees);
                        }
                    }
                    catch
                    {
                        // If rotation fails, fall back to original bitmap
                        bmpToSubmit = bmp;
                    }

                    // Compute dest rect in renderer coordinates
                    var destRect = new Rect(layer.X, layer.Y, layer.Width > 0 ? layer.Width : w, layer.Height > 0 ? layer.Height : h);

                    // Submit to renderer manager only if not a preview-only host layer
                    if (!layer.PreviewOnly)
                    {
                        try
                        {
                            _rendererManager.SubmitLayerFrame(layer.Id ?? Guid.NewGuid().ToString("N"), bmpToSubmit, destRect, layer.Opacity);
                        }
                        catch
                        {
                            // swallow
                        }
                    }

                    // Also forward via service event for isolated previews
                    try
                    {
                        FrameDecoded?.Invoke(layer.Id ?? string.Empty, bmpToSubmit);
                    }
                    catch
                    {
                        // swallow any subscriber exceptions
                    }
                }
                catch
                {
                    // swallow
                }
            };

            // Store before starting so Unregister can find it
            var layerKey = layer.Id ?? Guid.NewGuid().ToString("N");
            _decoders[layerKey] = (decoder, cts, layer);

            // Start decoder on background task, but surface early errors up
            _ = Task.Run(async () =>
            {
                try
                {
                    await decoder.StartAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // on error, unregister
                    await UnregisterLayerAsync(layerKey).ConfigureAwait(false);
                }
            }, cts.Token);

            return true;
        }

        private static BitmapSource RotateBitmap(BitmapSource src, double degrees)
        {
            if (src == null) return src;
            // Normalize degrees to [0,360)
            var deg = degrees % 360.0;
            if (deg < 0) deg += 360.0;
            if (Math.Abs(deg) < 1e-6) return src;

            double theta = deg * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(theta));
            double sin = Math.Abs(Math.Sin(theta));

            int w = src.PixelWidth;
            int h = src.PixelHeight;

            int newW = Math.Max(1, (int)Math.Ceiling(w * cos + h * sin));
            int newH = Math.Max(1, (int)Math.Ceiling(w * sin + h * cos));

            // Use source DPI
            double dpiX = src.DpiX;
            double dpiY = src.DpiY;

            var rtb = new RenderTargetBitmap(newW, newH, dpiX, dpiY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Move origin to center of new bitmap
                dc.PushTransform(new TranslateTransform(newW / 2.0, newH / 2.0));
                dc.PushTransform(new RotateTransform(deg));

                // Draw the source bitmap centered at the origin
                dc.DrawImage(src, new Rect(-w / 2.0, -h / 2.0, w, h));

                dc.Pop(); // Rotate
                dc.Pop(); // Translate
            }

            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
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

        public Task PauseLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return Task.CompletedTask;
            if (_decoders.TryGetValue(layerId, out var t))
            {
                try
                {
                    t.cts.Cancel();
                    t.decoder.Dispose();
                    // keep model in dictionary by re-adding with null decoder to indicate paused state
                    _decoders[layerId] = (null!, null!, t.model);
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        public async Task ResumeLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            if (_decoders.TryGetValue(layerId, out var t) && t.model != null && (t.decoder == null))
            {
                // recreate decoder for model
                var layer = t.model;
                await RegisterLayerAsync(layer).ConfigureAwait(false);
            }
        }

        public Task PauseAllAsync()
        {
            foreach (var id in _decoders.Keys)
            {
                _ = PauseLayerAsync(id);
            }
            return Task.CompletedTask;
        }

        public Task ResumeAllAsync()
        {
            foreach (var kv in _decoders)
            {
                if (kv.Value.model != null && kv.Value.decoder == null)
                {
                    _ = ResumeLayerAsync(kv.Key);
                }
            }
            return Task.CompletedTask;
        }

        public Task RestartAllAsync()
        {
            foreach (var id in _decoders.Keys)
            {
                _ = RestartLayerAsync(id);
            }
            return Task.CompletedTask;
        }

        public async Task RestartLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            if (_decoders.TryGetValue(layerId, out var t) && t.model != null)
            {
                await UnregisterLayerAsync(layerId).ConfigureAwait(false);
                await RegisterLayerAsync(t.model).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Attempts to resolve ffmpeg executable. If neither explicit path nor PATH lookup works,
        /// throws InvalidOperationException with a helpful message.
        /// </summary>
        private string ResolveFfmpegExecutable()
        {
            var exe = string.IsNullOrWhiteSpace(_ffmpegPath) ? "ffmpeg" : _ffmpegPath;

            try
            {
                // Try executing "ffmpeg -version" quickly to verify availability.
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("Failed to start ffmpeg process.");
                // Wait briefly; -version exits quickly.
                if (!proc.WaitForExit(1500))
                {
                    try { proc.Kill(true); } catch { }
                    throw new InvalidOperationException("ffmpeg did not respond in time.");
                }

                // Non-zero exit code is acceptable for some builds, but ensure at least output exists
                // (some builds print version info then exit 0).
                return exe;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"ffmpeg executable not found or failed to run. Ensure ffmpeg is installed and on PATH, or provide a valid ffmpeg path. Underlying error: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            foreach (var kv in _decoders)
            {
                try
                {
                    kv.Value.cts?.Cancel();
                    kv.Value.decoder?.Dispose();
                    kv.Value.cts?.Dispose();
                }
                catch { }
            }
            _decoders.Clear();
        }
    }
}