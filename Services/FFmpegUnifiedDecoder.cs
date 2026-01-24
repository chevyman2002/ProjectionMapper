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
        
        /// <summary>
        /// Event raised when the video reaches EOF (end of file).
        /// This event is fired once per playback cycle (before looping, if enabled).
        /// </summary>
        public event Action? VideoEnded;

        // Audio control properties
        private bool _audioEnabled = false;
        private float _volume = 1.0f;
        private bool _muted = false;

        // Position tracking for pause/resume with loop support
        private TimeSpan _currentPosition = TimeSpan.Zero;
      private Stopwatch? _playbackTimer;
        private readonly object _positionLock = new();
        
        // Video duration for loop-aware position tracking
        private TimeSpan _videoDuration = TimeSpan.Zero;

        // Flag indicating if the video has reached its end
        private volatile bool _isAtEnd = false;

        /// <summary>
        /// When true, the decoder will automatically restart when EOF is reached.
      /// </summary>
      public bool Loop { get; set; }

        /// <summary>
        /// Gets whether the video has reached its end (EOF).
        /// This is reset when the video restarts (loop) or is restarted manually.
        /// </summary>
        public bool IsAtEnd => _isAtEnd;

        /// <summary>
        /// Gets the total duration of the video, if known.
        /// </summary>
        public TimeSpan Duration => _videoDuration;

        /// <summary>
        /// When true, audio will be decoded and played. When false, audio is muted but still decoded for sync.
        /// </summary>
        public bool AudioEnabled
        {
            get => _audioEnabled;
            set
            {
                _audioEnabled = value;
                // Only call UpdateAudioPlayback if the wave player has been initialized
                if (_wavePlayer != null)
                {
                    UpdateAudioPlayback();
                }
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
                // Only call UpdateAudioPlayback if the wave player has been initialized
                if (_wavePlayer != null)
                {
                    UpdateAudioPlayback();
                }
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
                // Only call UpdateAudioPlayback if the wave player has been initialized
                if (_wavePlayer != null)
                {
                    UpdateAudioPlayback();
                }
            }
        }

        /// <summary>
        /// Gets the current amount of decoded audio that is buffered and ready for playback.
        /// This helps higher-level services decide when it is safe to enable audio without artifacts.
        /// </summary>
        public TimeSpan BufferedAudioDuration
        {
            get
            {
                try
                {
                    return _waveProvider?.BufferedDuration ?? TimeSpan.Zero;
                }
                catch
                {
                    return TimeSpan.Zero;
                }
            }
        }

        /// <summary>
        /// Clears any pending audio samples from the buffer to avoid replaying stale data when toggling playback.
        /// </summary>
        public void ClearAudioBuffer()
        {
            try
            {
                _waveProvider?.ClearBuffer();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FFmpegUnifiedDecoder.ClearAudioBuffer: Failed to clear buffer: {ex}");
            }
        }

        /// <summary>
        /// Current playback position (read-only).
        /// </summary>
        public TimeSpan CurrentPosition
        {
            get
            {
                lock (_positionLock)
                {
                    if (_playbackTimer != null && _playbackTimer.IsRunning)
                    {
                        return _currentPosition + _playbackTimer.Elapsed;
                    }
                    return _currentPosition;
                }
            }
        }

        /// <summary>
        /// Save current position and stop the playback timer.
        /// </summary>
        public void SaveCurrentPosition()
        {
            lock (_positionLock)
            {
                if (_playbackTimer != null && _playbackTimer.IsRunning)
                {
                    _currentPosition += _playbackTimer.Elapsed;
                    _playbackTimer.Stop();
                    _playbackTimer = null;
                }
            }
        }

        /// <summary>
        /// Set the position to resume from.
        /// </summary>
        public void SetResumePosition(TimeSpan position)
        {
            lock (_positionLock)
            {
                _currentPosition = position;
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

            // CRITICAL: Probe video duration for loop-aware position tracking
  try
            {
     var duration = TryProbeDuration(_inputPath, _ffmpegPath);
       if (duration > TimeSpan.Zero)
     {
    _videoDuration = duration;
      Debug.WriteLine($"FFmpegUnifiedDecoder: Video duration detected: {duration.TotalSeconds:F2}s");
           }
      else
 {
        Debug.WriteLine("FFmpegUnifiedDecoder: Could not detect video duration - loop position tracking may be inaccurate");
 }
        }
            catch (Exception ex)
            {
                Debug.WriteLine($"FFmpegUnifiedDecoder: Duration probe failed: {ex}");
            }

            // Try to probe framerate up-front
      try
            {
          var fps = TryProbeFps(_inputPath, _ffmpegPath);
  if (fps > 0) Interlocked.Exchange(ref _capturedFps, fps);
          }
          catch { }

         // Initialize audio components
         InitializeAudio();

            // Start playback timer
     lock (_positionLock)
            {
 _playbackTimer = Stopwatch.StartNew();
         }

       try
            {
       do
   {
       if (_cts.IsCancellationRequested) break;

        // Reset the IsAtEnd flag when starting a new session
        _isAtEnd = false;

        await StartSingleDecodingSession();

        // Mark video as completed when the session ends (EOF reached)
        _isAtEnd = true;
        Debug.WriteLine("FFmpegUnifiedDecoder: Video reached end of file");

        // Fire the VideoEnded event to notify listeners
        try
        {
            VideoEnded?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FFmpegUnifiedDecoder: Error invoking VideoEnded event: {ex}");
        }

   // If not looping, exit
     if (!Loop) break;

   // CRITICAL: Reset position to 0 for next loop iteration
       lock (_positionLock)
       {
         _currentPosition = TimeSpan.Zero;
           if (_playbackTimer != null)
           {
           _playbackTimer.Restart();
          }
  Debug.WriteLine("FFmpegUnifiedDecoder: Loop iteration complete, position reset to 0");
       }

   // Small delay before restarting
     await Task.Delay(100, _cts.Token).ConfigureAwait(false);

          } while (!_cts.Token.IsCancellationRequested);
 }
     finally
      {
         // Save position before cleanup
        lock (_positionLock)
    {
if (_playbackTimer != null)
         {
              _currentPosition += _playbackTimer.Elapsed;
            _playbackTimer.Stop();
              _playbackTimer = null;
      }
    }

           CleanupProcess();
            }
        }

    private async Task StartSingleDecodingSession()
        {
         // Get current seek position
            TimeSpan seekPos;
            lock (_positionLock)
            {
       seekPos = _currentPosition;
           
       // CRITICAL: Clamp seek position to video duration to prevent seeking past end
             if (_videoDuration > TimeSpan.Zero && seekPos >= _videoDuration)
       {
           seekPos = TimeSpan.Zero;
               _currentPosition = TimeSpan.Zero;
   if (_playbackTimer != null)
   {
 _playbackTimer.Restart();
     }
              Debug.WriteLine("FFmpegUnifiedDecoder: Seek position clamped to 0 (was >= duration)");
            }
            }

            // CRITICAL: Clear audio buffer at start of each session to prevent loop artifacts
    if (_waveProvider != null)
      {
  try
      {
     _waveProvider.ClearBuffer();
    Debug.WriteLine("FFmpegUnifiedDecoder.StartSingleDecodingSession: Cleared audio buffer to prevent loop artifacts");
       }
      catch (Exception ex)
       {
   Debug.WriteLine($"FFmpegUnifiedDecoder.StartSingleDecodingSession: Failed to clear buffer: {ex}");
      }
   }

        // Use FFmpeg to output video to stdout as raw BGRA frames
         // Use -re for real-time playback to maintain natural timing
            // NOTE: We do NOT use -stream_loop because it breaks position tracking
  // Instead we handle looping ourselves by restarting the process
            string seekArg = seekPos.TotalSeconds > 0.1 ? $"-ss {seekPos.TotalSeconds:F3}" : "";
  var videoArgs = $"-hide_banner -loglevel info {seekArg} -re -i \"{_inputPath}\" " +
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

    // Capture process and cancellation token in local variables to prevent race conditions
    // when Dispose() is called from another thread during playback
    var localProcess = _process;
    var localCts = _cts;
    if (localCts == null) throw new InvalidOperationException("Cancellation token source is null.");

    // Verify streams are available before proceeding
    var stdout = localProcess.StandardOutput?.BaseStream;
    var stderr = localProcess.StandardError;
    if (stdout == null)
    {
        Debug.WriteLine("FFmpegUnifiedDecoder: StandardOutput.BaseStream is null, cannot read video frames.");
        throw new InvalidOperationException("FFmpeg process StandardOutput stream is null.");
    }

    // Start separate FFmpeg process for audio (synchronized using -re)
    var audioTask = StartAudioDecoding(seekPos);

    // Start stderr reading for FPS detection
    var stderrTask = Task.Run(() => ReadStderr(stderr, localCts.Token), localCts.Token);

    try
    {
        // Read video frames from stdout
        await ReadVideoFrames(stdout, localCts.Token);
    }
    finally
    {
        CleanupProcess();

        // Wait for tasks to complete
        try { await audioTask; } catch { }
        try { await stderrTask; } catch { }
    }
}

   private async Task StartAudioDecoding(TimeSpan seekPosition)
        {
   try
  {
        // Create separate FFmpeg process for audio that runs in sync with video
      // NOTE: We do NOT use -stream_loop here either
    string seekArg = seekPosition.TotalSeconds > 0.1 ? $"-ss {seekPosition.TotalSeconds:F3}" : "";
     var audioArgs = $"-hide_banner -loglevel error {seekArg} -re -i \"{_inputPath}\" " +
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

      // CRITICAL: Auto-start playback when buffer has enough data
            // This runs in the decode loop thread, ensuring perfect timing
  if (_audioEnabled && _wavePlayer is WaveOutEvent waveOut && 
     waveOut.PlaybackState != PlaybackState.Playing && 
   _waveProvider.BufferedBytes > 8192) // Wait for at least ~185ms of audio buffered
         {
 try
   {
       waveOut.Play();
   Debug.WriteLine($"FFmpegUnifiedDecoder.StartAudioDecoding: Auto-started playback (buffer has {_waveProvider.BufferedBytes} bytes)");
       }
      catch (Exception ex)
       {
         Debug.WriteLine($"FFmpegUnifiedDecoder.StartAudioDecoding: Auto-start failed: {ex}");
       }
       }
       }
   catch (Exception ex)
 {
  Debug.WriteLine($"FFmpegUnifiedDecoder: Audio buffer add failed: {ex}");
    }
           }
   else if (bytesRead == 0)
    {
  // EOF reached - exit cleanly to allow restart
      Debug.WriteLine("FFmpegUnifiedDecoder.StartAudioDecoding: Audio EOF reached");
       break;
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
              if (chunk == 0) 
    {
    // EOF reached - return to allow outer loop to restart if Loop is enabled
     return;
       }
       read += chunk;
  }

  if (read < frameSize) 
   {
      // Incomplete frame read - likely at EOF
       return;
   }

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
                // Null check to prevent NullReferenceException on restart
                if (stderr == null) return;

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

                // CRITICAL: Only start playback if audio is already enabled
                // Otherwise, let the decode loop auto-start when buffer has data
                if (_audioEnabled)
                {
                    _wavePlayer.Play();
                    Debug.WriteLine($"FFmpegUnifiedDecoder: Started playback immediately, AudioEnabled={_audioEnabled}");
                }
                else
                {
                    Debug.WriteLine($"FFmpegUnifiedDecoder: Playback NOT started (AudioEnabled={_audioEnabled}), will auto-start when enabled");
                }
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

                        // CRITICAL FIX: Start playback immediately if we have buffered audio data
                        // Don't wait for the decode loop - this ensures audio starts when enabled
                        if (waveOut.PlaybackState != PlaybackState.Playing && 
                            _waveProvider != null && 
                            _waveProvider.BufferedBytes > 4096) // Need at least some buffer
                        {
                            try
                            {
                                waveOut.Play();
                                Debug.WriteLine($"FFmpegUnifiedDecoder.UpdateAudioPlayback: Started playback immediately, buffer={_waveProvider.BufferedBytes} bytes");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"FFmpegUnifiedDecoder.UpdateAudioPlayback: Failed to start playback: {ex}");
                            }
                        }

                        Debug.WriteLine($"FFmpegUnifiedDecoder.UpdateAudioPlayback: Audio ENABLED, volume={_volume}, state={waveOut.PlaybackState}");
                    }
                    else
                    {
                        waveOut.Volume = 0f;

                        // Stop playback when disabling audio
                        if (waveOut.PlaybackState == PlaybackState.Playing)
                        {
                            try
                            {
                                waveOut.Stop();
                                Debug.WriteLine("FFmpegUnifiedDecoder.UpdateAudioPlayback: Stopped WaveOut playback");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"FFmpegUnifiedDecoder.UpdateAudioPlayback: Failed to stop playback: {ex}");
                            }
                        }

                        Debug.WriteLine("FFmpegUnifiedDecoder.UpdateAudioPlayback: Audio DISABLED (volume = 0)");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FFmpegUnifiedDecoder: Audio update failed: {ex}");
                }
            }
            else
            {
                Debug.WriteLine("FFmpegUnifiedDecoder.UpdateAudioPlayback: _wavePlayer is not WaveOutEvent or is null");
            }
        }

        private static TimeSpan TryProbeDuration(string path, string? ffmpegPath)
        {
   try
         {
    // Use ffprobe to get duration
       var ffprobe = (ffmpegPath ?? "ffmpeg").Replace("ffmpeg", "ffprobe");
         var psi = new ProcessStartInfo
       {
         FileName = ffprobe,
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
            UseShellExecute = false,
     RedirectStandardOutput = true,
   RedirectStandardError = true,
   CreateNoWindow = true
        };

      using var proc = Process.Start(psi);
         if (proc != null)
                {
           var output = proc.StandardOutput.ReadToEnd();
         proc.WaitForExit(1000);
 if (!string.IsNullOrEmpty(output))
     {
      var durationStr = output.Trim();
       if (double.TryParse(durationStr, out var durationSeconds) && durationSeconds > 0)
           {
   return TimeSpan.FromSeconds(durationSeconds);
              }
           }
  }
    }
         catch { }

       return TimeSpan.Zero;
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
              if (parts.Length == 2 && double.TryParse(parts[0], out var numer) && double.TryParse(parts[1], out var denom) && denom > 0)
     {
   return Math.Round(numer / denom, 4);
              }
  }
    else if (double.TryParse(txt, out var fps) && fps > 0)
        {
  return Math.Round(fps, 4);
       }
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
                if (_process != null)
                {
       try
          {
             if (!_process.HasExited)
     {
   // Send SIGTERM first (graceful exit)
   _process.Kill();
          _process.WaitForExit(1000);
             }
        }
          catch { }

      _process.Dispose();
            _process = null;
     }
      }
         catch (Exception ex)
            {
      Debug.WriteLine($"FFmpegUnifiedDecoder: Process cleanup failed: {ex}");
      }
 }

        public void Dispose()
        {
       // Stop any ongoing decoding
            _cts?.Cancel();

            // Wait briefly to allow tasks to observe cancellation
        try { Thread.Sleep(50); } catch { }

  // Final cleanup
 CleanupProcess();

   // Dispose audio components
            try
         {
          if (_wavePlayer != null)
       {
  _wavePlayer.Stop();
        _wavePlayer.Dispose();
   _wavePlayer = null;
          }
     }
  catch { }

         try
       {
     if (_waveProvider != null)
     {
 _waveProvider.ClearBuffer();
       _waveProvider = null;
       }
            }
   catch { }
        }
    }
}