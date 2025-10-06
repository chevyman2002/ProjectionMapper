using System;
using System.Diagnostics;
using System.IO;
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
    /// </summary>
    public sealed class FFmpegVideoDecoder : IDisposable
    {
        private readonly string _inputPath;
        private readonly int _width;
        private readonly int _height;
        private readonly string? _ffmpegPath;
        private Process? _process;
        private CancellationTokenSource? _cts;

        public event Action<BitmapSource>? FrameDecoded;

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

            var args = $"-hide_banner -loglevel error -i \"{_inputPath}\" -f rawvideo -pix_fmt bgra -vf scale={_width}:{_height} -an -sn pipe:1";

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

            // Read standard error in background to avoid blocking due to buffer fills (log if necessary)
            _ = Task.Run(async () =>
            {
                try
                {
                    var sr = _process.StandardError;
                    while (!_cts.Token.IsCancellationRequested && !sr.EndOfStream)
                    {
                        var line = await sr.ReadLineAsync().ConfigureAwait(false);
                        // Optionally log the line
                    }
                }
                catch { }
            }, _cts.Token);

            var stdout = _process.StandardOutput.BaseStream!;
            var frameSize = _width * _height * 4;
            var buffer = new byte[frameSize];

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
                            _cts.Token.ThrowIfCancellationRequested();
                            break;
                        }
                        read += chunk;
                    }

                    if (read < frameSize) break;

                    // Create BitmapSource from buffer (BGRA32)
                    var stride = _width * 4;
                    var bmp = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
                    bmp.Freeze();
                    FrameDecoded?.Invoke(bmp);
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
                    if (!_process.HasExited)
                    {
                        _process.Kill(true);
                    }
                }
                catch { }

                _process.Dispose();
                _process = null;
            }
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
            _cts?.Dispose();
            _process?.Dispose();
        }
    }
}