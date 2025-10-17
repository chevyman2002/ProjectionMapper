using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// RenderLoop drives periodic render invocations on a dedicated background thread.
    /// It provides a simple, testable abstraction: consumers can register an async callback to produce a frame.
    /// The loop supports fixed target FPS or unlimited (as-fast-as-possible).
    /// </summary>
    public sealed class RenderLoop : IDisposable
    {
        private readonly Func<CancellationToken, Task> _renderCallback;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private readonly double? _targetFps;

        /// <summary>
        /// Create a render loop.
        /// - renderCallback: called each frame on the render thread.
        /// - targetFps: optional target frames per second (null = run as fast as possible).
        /// </summary>
        public RenderLoop(Func<CancellationToken, Task> renderCallback, double? targetFps = 60.0)
        {
            _renderCallback = renderCallback ?? throw new ArgumentNullException(nameof(renderCallback));
            _targetFps = targetFps;
        }

        /// <summary>
        /// Start the background render loop.
        /// </summary>
        public void Start()
        {
            if (_loopTask != null) throw new InvalidOperationException("Render loop already started.");

            _loopTask = Task.Run(async () =>
            {
                try
                {
                    var sw = new Stopwatch();
                    var minFrameTimeMs = _targetFps.HasValue && _targetFps > 0 ? 1000.0 / _targetFps.Value : 0.0;

                    while (!_cts.IsCancellationRequested)
                    {
                        sw.Restart();
                        try
                        {
                            await _renderCallback(_cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception)
                        {
                            // Renderer exceptions should be caught/logged at the renderCallback level; swallow here to keep the loop alive.
                        }

                        if (minFrameTimeMs > 0)
                        {
                            var elapsed = sw.Elapsed.TotalMilliseconds;
                            var delay = minFrameTimeMs - elapsed;
                            if (delay > 1)
                            {
                                try
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(delay), _cts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { break; }
                            }
                        }
                    }
                }
                catch (ObjectDisposedException) { }
                catch (Exception) { }
            }, _cts.Token);
        }

        /// <summary>
        /// Stop the loop and wait for shutdown.
        /// </summary>
        public async Task StopAsync()
        {
            _cts.Cancel();
            if (_loopTask != null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                _loopTask = null;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}