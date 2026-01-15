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
    ///
    /// IMPORTANT: WPF rendering classes (DrawingVisual, RenderTargetBitmap, etc.) require an STA thread
    /// with a Dispatcher. To allow renderers that use WPF APIs to run off the main UI thread safely,
    /// this loop runs on a dedicated STA thread.
    /// </summary>
    public sealed class RenderLoop : IDisposable
    {
        private readonly Func<CancellationToken, Task> _renderCallback;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private Thread? _loopThread;
        private TaskCompletionSource<object?>? _tcs;
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
        /// Start the background render loop on a dedicated STA thread.
        /// </summary>
        public void Start()
        {
            if (_loopThread != null) throw new InvalidOperationException("Render loop already started.");
            _tcs = new TaskCompletionSource<object?>();

            _loopThread = new Thread(() =>
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
                            // Execute the async callback synchronously on this STA thread
                            _renderCallback(_cts.Token).GetAwaiter().GetResult();
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
                                    Thread.Sleep((int)delay);
                                }
                                catch (ThreadInterruptedException) { break; }
                            }
                        }
                    }
                }
                catch (ThreadAbortException) { }
                catch (ObjectDisposedException) { }
                catch (Exception) { }
                finally
                {
                    try { _tcs?.TrySetResult(null); } catch { }
                }
            })
            {
                IsBackground = true
            };

            // WPF and many imaging APIs require STA
            try { _loopThread.SetApartmentState(ApartmentState.STA); } catch { }
            _loopThread.Start();
            _loopTask = _tcs!.Task; // non-null after creation
        }

        /// <summary>
        /// Stop the loop and wait for shutdown.
        /// </summary>
        public async Task StopAsync()
        {
            if (_loopThread == null) return;

            try
            {
                _cts.Cancel();

                // Wait for the thread to finish via the TaskCompletionSource with a timeout
                if (_loopTask != null)
                {
                    // Use a timeout to prevent hanging
                    var timeoutTask = Task.Delay(1000);
                    var completedTask = await Task.WhenAny(_loopTask, timeoutTask).ConfigureAwait(false);
                    
                    if (completedTask == timeoutTask)
                    {
                        Debug.WriteLine("RenderLoop.StopAsync: Timeout waiting for loop task to complete");
                    }
                }

                // Don't block on Thread.Join - it can cause deadlocks during shutdown
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RenderLoop.StopAsync failed: {ex}");
            }
            finally
            {
                _loopTask = null;
                _loopThread = null;
            }
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                
                // Don't wait for thread to finish - just signal cancellation and dispose
                // This prevents deadlocks during application shutdown
                _loopTask = null;
                _loopThread = null;
            }
            catch { }
            
            try { _cts.Dispose(); } catch { }
        }
    }
}