using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Unified FFmpeg decoder that handles both video and audio streams from a single process.
    /// This ensures perfect synchronization between video frames and audio samples.
    /// Audio can be enabled/disabled without affecting synchronization.
    /// </summary>
    public sealed class FFmpegUnifiedDecoder : IDisposable
    {
        private readonly string _inputPath;
        private readonly int _width;
        private readonly int _height;
  private readonly string? _ffmpegPath;
        private Process? _process;
    private CancellationTokenSource? _cts;

        // Audio components
        private IWavePlayer? _wavePlayer;
    private BufferedWaveProvider? _waveProvider;
        private readonly WaveFormat _audioFormat = new(44100, 16, 2); // 44.1kHz, 16-bit, stereo

        // Video events
        public event Action<BitmapSource>? FrameDecoded;
        public event Action<BitmapSource, TimeSpan>? FrameDecodedWithTimestamp;

        // Audio control properties
        private bool _audioEnabled = false;
    private float _volume = 1.0f;
 private bool _muted = false;

     /// <summary>
 /// When true, the decoder will automatically restart when EOF is reached.
        /// </summary>
        public bool Loop { get; set; }

        /// <summary>
      /// When true, audio will be decoded and played. When false, audio is muted but still decoded for sync.
        /// </summary>
        public bool AudioEnabled
      {
    get => _audioEnabled;
            set
    {
     _audioEnabled = value;
       UpdateAudioPlayback();
            }
    }

        /// <summary>
      /// Audio volume (0.0 - 1.0).
        /// </summary>
        public float Volume
        {
            get => _volume;
       set
        {
        _volume = Math.Max(0f, Math.Min(1f, value));
  UpdateAudioPlayback();
            }
      }

     /// <summary>
        /// When true, audio output is muted (but still decoded for sync).
        /// </summary>
   public bool Muted
        {
     get => _muted;
       set
    {
   _muted = value;
  UpdateAudioPlayback();
            }
 }

      // Captured frames-per-second parsed from ffmpeg stderr (0 = unknown)
  private double _capturedFps = 0.0;

   public FFmpegUnifiedDecoder(string inputPath, int width, int height, string? ffmpegPath = null)
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

            // Try to probe framerate up-front
     try
    {
              var fps = TryProbeFps(_inputPath, _ffmpegPath);
             if (fps > 0) Interlocked.Exchange(ref _capturedFps, fps);
     }
            catch { }

            // Initialize audio components
            InitializeAudio();

            try
            {
    do
      {
          if (_cts.IsCancellationRequested) break;

     await StartSingleDecodingSession();

            // If not looping, exit
        if (!Loop) break;

      // Small delay before restarting
        await Task.Delay(100, _cts.Token).ConfigureAwait(false);

                } while (!_cts.Token.IsCancellationRequested);
       }
            finally
            {
         CleanupProcess();
            }
        }

     private async Task StartSingleDecodingSession()
 {
// Use FFmpeg to output video to stdout as raw BGRA frames
            // Use -re for real-time playback to maintain natural timing
  string loopArg = Loop ? "-stream_loop -1" : "";
   var videoArgs = $"-hide_banner -loglevel info {loopArg} -re -i \"{_inputPath}\" " +
         $"-f rawvideo -pix_fmt bgra -vf scale={_width}:{_height} -an pipe:1";

   var psi = new ProcessStartInfo
  {
          FileName = _ffmpegPath!,
       Arguments = videoArgs,
          UseShellExecute = false,
     RedirectStandardOutput = true,
   RedirectStandardError = true,
     CreateNoWindow = true
            };

            _process = Process.Start(psi);
 if (_process == null) throw new InvalidOperationException("Failed to start ffmpeg process.");

     // Start separate FFmpeg process for audio (synchronized using -re)
            var audioTask = StartAudioDecoding();

       // Start stderr reading for FPS detection
        var stderrTask = Task.Run(() => ReadStderr(_process.StandardError, _cts.Token), _cts.Token);

            try
          {
    // Read video frames from stdout
     await ReadVideoFrames(_process.StandardOutput.BaseStream!, _cts.Token);
 }
  finally
            {
       CleanupProcess();
    
                // Wait for tasks to complete
        try { await audioTask; } catch { }
    try { await stderrTask; } catch { }
          }
        }

        private async Task StartAudioDecoding()
        {
  try
            {
 // Create separate FFmpeg process for audio that runs in sync with video
   string loopArg = Loop ? "-stream_loop -1" : "";
     var audioArgs = $"-hide_banner -loglevel error {loopArg} -re -i \"{_inputPath}\" " +
          $"-f s16le -acodec pcm_s16le -ac 2 -ar 44100 -vn pipe:1";

        var audioPsi = new ProcessStartInfo
            {
    FileName = _ffmpegPath!,
  Arguments = audioArgs,
         UseShellExecute = false,
    RedirectStandardOutput = true,
   RedirectStandardError = false,
   CreateNoWindow = true
      };

                using var audioProcess = Process.Start(audioPsi);
       if (audioProcess == null) return;

             var buffer = new byte[8192];
    var audioStream = audioProcess.StandardOutput.BaseStream;

     while (!_cts.Token.IsCancellationRequested && !audioProcess.HasExited)
        {
   try
       {
             var bytesRead = await audioStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
      
    if (bytesRead > 0 && _waveProvider != null)
   {
             try
    {
_waveProvider.AddSamples(buffer, 0, bytesRead);
       }
          catch (Exception ex)
         {
        Debug.WriteLine($"FFmpegUnifiedDecoder: Audio buffer add failed: {ex}");
    }
           }
   else if (bytesRead == 0)
    {
  // EOF or no data, small delay
await Task.Delay(10, _cts.Token);
               }
  }
        catch (OperationCanceledException) { break; }
              catch (Exception ex)
   {
        Debug.WriteLine($"FFmpegUnifiedDecoder: Audio read error: {ex}");
               await Task.Delay(50, _cts.Token);
         }
     }

         // Clean up audio process
 try
                {
   if (!audioProcess.HasExited)
{
       audioProcess.Kill(true);
       }
      }
         catch { }
      }
            catch (Exception ex)
  {
             Debug.WriteLine($"FFmpegUnifiedDecoder: Audio decoding failed: {ex}");
         }
        }

        private async Task ReadVideoFrames(Stream stdout, CancellationToken cancellationToken)
        {
    var frameSize = _width * _height * 4;
var buffer = new byte[frameSize];
            var sw = Stopwatch.StartNew();

     try
    {
   while (!cancellationToken.IsCancellationRequested)
    {
              int read = 0;
         while (read < frameSize)
           {
    var chunk = await stdout.ReadAsync(buffer, read, frameSize - read, cancellationToken).ConfigureAwait(false);
    if (chunk == 0) return; // EOF
     read += chunk;
  }

        if (read < frameSize) return; // EOF reached

      // Create BitmapSource from buffer (BGRA32)
 var stride = _width * 4;
   var bmp = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        bmp.Freeze();

            // Compute presentation timestamp
          var pts = sw.Elapsed;

 // Deliver frame events
    FrameDecoded?.Invoke(bmp);
               FrameDecodedWithTimestamp?.Invoke(bmp, pts);

        // Note: We rely on FFmpeg's -re flag for pacing, so no additional throttling needed
    }
      }
    catch (OperationCanceledException) { }
            catch (Exception ex)
          {
            Debug.WriteLine($"FFmpegUnifiedDecoder: Video reading failed: {ex}");
       }
        }

        private async Task ReadStderr(StreamReader stderr, CancellationToken cancellationToken)
        {
            try
 {
      var re = new Regex(@"(?<fps>\d+(?:\.\d+)?)\s+fps", RegexOptions.Compiled);
     while (!cancellationToken.IsCancellationRequested && !stderr.EndOfStream)
                {
           var line = await stderr.ReadLineAsync().ConfigureAwait(false);
    if (string.IsNullOrEmpty(line)) continue;

         // Try to parse fps
      try
    {
             var m = re.Match(line);
          if (m.Success && double.TryParse(m.Groups["fps"].Value, out var fps) && fps > 0)
            {
       Interlocked.Exchange(ref _capturedFps, fps);
     }
            }
       catch { }
    }
      }
   catch (OperationCanceledException) { }
            catch { }
        }

        private void InitializeAudio()
        {
       try
          {
        // Create wave provider with buffer
          _waveProvider = new BufferedWaveProvider(_audioFormat)
           {
BufferDuration = TimeSpan.FromMilliseconds(750),
        DiscardOnBufferOverflow = true,
ReadFully = true
              };

         // Create wave player
   _wavePlayer = new WaveOutEvent
   {
              DesiredLatency = 120,
  NumberOfBuffers = 3
   };

    _wavePlayer.Init(_waveProvider);
  UpdateAudioPlayback();

                // Always start playback - we'll control volume/muting
       _wavePlayer.Play();
        }
  catch (Exception ex)
            {
   Debug.WriteLine($"FFmpegUnifiedDecoder: Audio initialization failed: {ex}");
            }
        }

        private void UpdateAudioPlayback()
 {
          if (_wavePlayer is WaveOutEvent waveOut)
      {
            try
          {
           if (_audioEnabled && !_muted)
        {
               waveOut.Volume = _volume;
        }
        else
   {
        waveOut.Volume = 0f;
       }
            }
    catch (Exception ex)
         {
     Debug.WriteLine($"FFmpegUnifiedDecoder: Audio update failed: {ex}");
   }
        }
        }

        private static double TryProbeFps(string path, string? ffmpegPath)
        {
        try
            {
        // Try ffprobe first
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

         // Fallback: try to parse FPS from ffmpeg -i output
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

   private void CleanupProcess()
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

      try { _wavePlayer?.Stop(); } catch { }
 try { _wavePlayer?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }

     CleanupProcess();
  }
    }
}