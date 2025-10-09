using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Simple ffmpeg-based decoder that streams raw BGRA frames via stdout and raises FrameDecoded for each frame.
    /// Requires a working ffmpeg executable available on PATH or specified by ffmpegPath.
    /// This implementation decodes frames scaled to the requested width/height.
    /// Added support for looping playback.
    /// </summary>
    public sealed class FFmpegVideoDecoder : IDisposable
    {
        private readonly string _inputPath;
        private readonly int _width;
        private readonly int _height;
        private readonly string? _ffmpegPath;
        private Process? _process;
        private CancellationTokenSource? _cts;

        // Backwards-compatible simple frame event (no timestamp)
        public event Action<BitmapSource>? FrameDecoded;

        // New: timestamped frame event (presentation timestamp relative to decoder start)
        public event Action<BitmapSource, TimeSpan>? FrameDecodedWithTimestamp;

        /// <summary>
        /// When true, the decoder will automatically restart the ffmpeg read when EOF is reached.
        /// </summary>
        public bool Loop { get; set; }

        // captured frames-per-second parsed from ffmpeg stderr (0 = unknown)
        private double _capturedFps = 0.0;

        public FFmpegVideoDecoder(string inputPath, int width, int height, string? ffmpegPath = null)
        {
            _inputPath = inputPath ?? throw new ArgumentNullException(nameof(inputPath));
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _ffmpegPath = string.IsNullOrEmpty(ffmpegPath) ? "ffmpeg" : ffmpegPath;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_process != null) throw new InvalidOperationException("Decoder already started.");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Try to probe a framerate up-front using ffprobe if present (best-effort)
            try
            {
                var fps = TryProbeFps(_inputPath, _ffmpegPath);
                if (fps > 0) Interlocked.Exchange(ref _capturedFps, fps);
            }
            catch { }

            // We'll loop the whole decoding session if Loop==true: restart ffmpeg process on EOF.
            try
            {
                do
                {
                    if (_cts.IsCancellationRequested) break;

                    // Request informational logging so ffmpeg prints stream info (including fps) to stderr
                    // Include -re so ffmpeg reads the input at native rate and we don't need to flood-read frames.
                    var args = $"-hide_banner -loglevel info -re -i \"{_inputPath}\" -f rawvideo -pix_fmt bgra -vf scale={_width}:{_height} -an -sn pipe:1";

                    var psi = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath!,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    _process = Process.Start(psi);
                    if (_process == null) throw new InvalidOperationException("Failed to start ffmpeg process.");

                    // Read standard error in background to avoid blocking due to buffer fills and to capture fps
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var sr = _process.StandardError;
                            var re = new Regex(@"(?<fps>\d+(?:\.\d+)?)\s+fps", RegexOptions.Compiled);
                            while (!_cts.Token.IsCancellationRequested && !sr.EndOfStream)
                            {
                                var line = await sr.ReadLineAsync().ConfigureAwait(false);
                                if (string.IsNullOrEmpty(line)) continue;

                                // Try to parse fps from lines like ", 29.97 fps, "
                                try
                                {
                                    var m = re.Match(line);
                                    if (m.Success && double.TryParse(m.Groups["fps"].Value, out var fps) && fps > 0)
                                    {
                                        // store in a threadsafe manner
                                        Interlocked.Exchange(ref _capturedFps, fps);
                                    }
                                }
                                catch { }
                                // optionally log
                            }
                        }
                        catch { }
                    }, _cts.Token);

                    var stdout = _process.StandardOutput.BaseStream!;
                    var frameSize = _width * _height * 4;
                    var buffer = new byte[frameSize];

                    var sw = Stopwatch.StartNew();
                    var prevTime = sw.Elapsed;
                    long frameIndex = 0;

                    try
                    {
                        while (!_cts.Token.IsCancellationRequested)
                        {
                            int read = 0;
                            while (read < frameSize)
                            {
                                var chunk = await stdout.ReadAsync(buffer, read, frameSize - read, _cts.Token).ConfigureAwait(false);
                                if (chunk == 0) // EOF
                                {
                                    // break out to outer loop to either restart or finish
                                    break;
                                }
                                read += chunk;
                            }

                            if (read < frameSize) break; // EOF reached

                            // Create BitmapSource from buffer (BGRA32)
                            var stride = _width * 4;
                            var bmp = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
                            bmp.Freeze();

                            // Compute presentation timestamp for this frame relative to decoder start
                            var pts = sw.Elapsed; // best-effort; reflects decoding time aligned with -re

                            // Deliver frame (both events for compatibility)
                            FrameDecoded?.Invoke(bmp);
                            FrameDecodedWithTimestamp?.Invoke(bmp, pts);

                            frameIndex++;

                            // Throttle to captured fps if available
                            try
                            {
                                var fps = Interlocked.CompareExchange(ref _capturedFps, 0.0, 0.0);
                                if (fps > 0)
                                {
                                    var desired = TimeSpan.FromSeconds(1.0 / fps);
                                    var now = sw.Elapsed;
                                    var elapsed = now - prevTime;
                                    var toWait = desired - elapsed;
                                    if (toWait > TimeSpan.Zero)
                                    {
                                        await Task.Delay(toWait, _cts.Token).ConfigureAwait(false);
                                        now = sw.Elapsed;
                                    }
                                    prevTime = now;
                                }
                                else
                                {
                                    // If unknown fps and we still used -re, ffmpeg will have paced input; still update prevTime
                                    prevTime = sw.Elapsed;
                                }
                            }
                            catch (OperationCanceledException) { break; }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception)
                    {
                        // swallow; callers may handle via events
                    }
                    finally
                    {
                        try
                        {
                            if (_process != null && !_process.HasExited)
                            {
                                try { _process.Kill(true); } catch { }
                            }
                        }
                        catch { }

                        try { _process?.Dispose(); } catch { }
                        _process = null;
                    }

                    // If not looping, exit
                    if (!Loop) break;

                    // Small delay before restarting to avoid busy fast-restarts on a problematic file
                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);

                } while (!_cts.Token.IsCancellationRequested);
            }
            finally
            {
                // ensure process cleaned
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        try { _process.Kill(true); } catch { }
                    }
                }
                catch { }

                try { _process?.Dispose(); } catch { }
                _process = null;
            }
        }

        private static double TryProbeFps(string path, string? ffmpegPath)
        {
            try
            {
                // try ffprobe first (commonly distributed with ffmpeg)
                var ffprobe = (ffmpegPath ?? "ffmpeg").Replace("ffmpeg", "ffprobe");
                var psi = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=r_frame_rate -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var outp = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(1000);
                    if (!string.IsNullOrEmpty(outp))
                    {
                        // output might be "30000/1001" or "25/1" or "29.97"
                        var txt = outp.Trim();
                        if (txt.Contains('/'))
                        {
                            var parts = txt.Split('/');
                            if (parts.Length == 2 && double.TryParse(parts[0], out var num) && double.TryParse(parts[1], out var den) && den != 0)
                            {
                                return num / den;
                            }
                        }
                        else if (double.TryParse(txt, out var v))
                        {
                            return v;
                        }
                    }
                }
            }
            catch { }

            // fallback: try to parse FPS from a quick ffmpeg -i output (stderr contains stream info)
            try
            {
                var psi2 = new ProcessStartInfo
                {
                    FileName = ffmpegPath ?? "ffmpeg",
                    Arguments = $"-hide_banner -i \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc2 = Process.Start(psi2);
                if (proc2 != null)
                {
                    var err = proc2.StandardError.ReadToEnd();
                    proc2.WaitForExit(1000);
                    if (!string.IsNullOrEmpty(err))
                    {
                        var re = new Regex(@"(?<fps>\d+(?:\.\d+)?)\s+fps", RegexOptions.Compiled);
                        var m = re.Match(err);
                        if (m.Success && double.TryParse(m.Groups["fps"].Value, out var fps) && fps > 0) return fps;
                    }
                }
            }
            catch { }

            return 0.0;
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            try { _cts?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }
        }
    }
}