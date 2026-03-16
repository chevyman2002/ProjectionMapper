using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Service that manages playlist playback with group-based video playback.
    /// Groups play sequentially. Videos within a group can play either:
    /// - Simultaneously (all at once, for projection mapping)
    /// - Sequentially (one after another, traditional playlist)
    /// After a group completes, the service advances to the next group.
    /// After the last group completes, playback loops back to the first group.
    /// </summary>
    public sealed class PlaylistService : IDisposable
    {
        private readonly VideoService _videoService;
        private readonly object _lock = new object();
        
        private List<PlaylistGroupModel> _groups = new List<PlaylistGroupModel>();
        private int _currentGroupIndex = -1;
        private bool _isPlaying;
        private bool _isPaused;
        private CancellationTokenSource? _cts;

        // Track video completion status for the current group (for simultaneous mode)
        private readonly ConcurrentDictionary<string, bool> _videoCompletionStatus = new ConcurrentDictionary<string, bool>();

        // Track current video index within a sequential group
        private int _sequentialVideoIndex = 0;
        private List<string> _sequentialActiveSourceIds = new List<string>();

        // Pre-buffering: track which groups have been pre-warmed
        private readonly ConcurrentDictionary<int, bool> _preWarmedGroups = new ConcurrentDictionary<int, bool>();

        // Configuration
        private int _groupTransitionDelayMs = 0; // Reduced from 500ms - pre-buffering eliminates the need for delay
        private bool _enableLooping = true;

        /// <summary>
        /// Event raised when the active group changes. Parameter is the new group index.
        /// </summary>
        public event Action<int>? GroupChanged;

        /// <summary>
        /// Event raised when the playlist completes a full cycle (last group finished).
        /// </summary>
        public event Action? PlaylistCompleted;

        /// <summary>
        /// Event raised just before advancing to the next group.
        /// </summary>
        public event Action<int, int>? GroupAdvancing;

        /// <summary>
        /// Event raised when playback state changes (playing, paused, stopped).
        /// </summary>
        public event Action<PlaylistPlaybackState>? PlaybackStateChanged;

        /// <summary>
        /// Creates a new PlaylistService with the specified VideoService.
        /// </summary>
        /// <param name="videoService">The VideoService to use for video playback.</param>
        /// <exception cref="ArgumentNullException">Thrown if videoService is null.</exception>
        public PlaylistService(VideoService videoService)
        {
            _videoService = videoService ?? throw new ArgumentNullException(nameof(videoService));
            
            // Subscribe to video completion events from VideoService
            _videoService.VideoCompleted += OnVideoCompleted;
            
            Debug.WriteLine("PlaylistService: Constructor completed, VideoCompleted event subscribed");
        }

        /// <summary>
        /// Gets the current group index (0-based), or -1 if not playing.
        /// </summary>
        public int CurrentGroupIndex
        {
            get
            {
                lock (_lock)
                {
                    return _currentGroupIndex;
                }
            }
        }

        /// <summary>
        /// Gets the currently active group, or null if not playing.
        /// </summary>
        public PlaylistGroupModel? CurrentGroup
        {
            get
            {
                lock (_lock)
                {
                    if (_currentGroupIndex >= 0 && _currentGroupIndex < _groups.Count)
                    {
                        return _groups[_currentGroupIndex];
                    }
                    return null;
                }
            }
        }

        /// <summary>
        /// Gets whether the playlist is currently playing.
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                lock (_lock)
                {
                    return _isPlaying && !_isPaused;
                }
            }
        }

        /// <summary>
        /// Gets whether the playlist is currently paused.
        /// </summary>
        public bool IsPaused
        {
            get
            {
                lock (_lock)
                {
                    return _isPaused;
                }
            }
        }

        /// <summary>
        /// Gets or sets the delay in milliseconds between group transitions.
        /// </summary>
        public int GroupTransitionDelayMs
        {
            get => _groupTransitionDelayMs;
            set => _groupTransitionDelayMs = Math.Max(0, value);
        }

        /// <summary>
        /// Gets or sets whether the playlist loops back to the first group after the last group completes.
        /// </summary>
        public bool EnableLooping
        {
            get => _enableLooping;
            set => _enableLooping = value;
        }

        /// <summary>
        /// Starts playlist playback with the specified groups.
        /// </summary>
        /// <param name="groups">The ordered list of playlist groups.</param>
        /// <returns>A task representing the async operation.</returns>
        public async Task StartPlaylistAsync(List<PlaylistGroupModel> groups)
        {
            if (groups == null || groups.Count == 0)
            {
                Debug.WriteLine("PlaylistService.StartPlaylistAsync: No groups provided, cannot start");
                return;
            }

            try
            {
                lock (_lock)
                {
                    // Cancel any existing playback
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();

                    _groups = groups.OrderBy(g => g.Order).ToList();
                    _currentGroupIndex = 0;
                    _isPlaying = true;
                    _isPaused = false;
                    _videoCompletionStatus.Clear();
                    _preWarmedGroups.Clear(); // Clear pre-warmed state on playlist start
                }

                Debug.WriteLine($"PlaylistService.StartPlaylistAsync: Starting playlist with {groups.Count} groups");

                // CRITICAL: Enable playlist mode to block all rendering until we explicitly set active layers
                _videoService.EnablePlaylistMode();

                // Disable looping for ALL videos in the project when playlist mode starts
                // This prevents videos from restarting independently and allows proper group-based advancement
                try
                {
                    Debug.WriteLine("PlaylistService.StartPlaylistAsync: Disabling Loop for all videos in project");
                    _videoService.DisableLoopingForAll();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PlaylistService.StartPlaylistAsync: Error disabling looping: {ex}");
                }

                PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Playing);

                await StartCurrentGroupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.StartPlaylistAsync: Error starting playlist: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Pauses playback of the current group. All videos in the group are paused.
        /// </summary>
        /// <returns>A task representing the async operation.</returns>
        public async Task PauseCurrentGroupAsync()
        {
            try
            {
                PlaylistGroupModel? currentGroup;
                lock (_lock)
                {
                    if (!_isPlaying || _isPaused)
                    {
                        Debug.WriteLine("PlaylistService.PauseCurrentGroupAsync: Not playing or already paused");
                        return;
                    }

                    _isPaused = true;
                    currentGroup = CurrentGroup;
                }

                if (currentGroup != null)
                {
                    // Filter to only active videos (those with registered decoders)
                    var activeSourceIds = _videoService.GetActiveLayerIds(currentGroup.SourceIds);
                    Debug.WriteLine($"PlaylistService.PauseCurrentGroupAsync: Pausing group {currentGroup.Name} ({activeSourceIds.Count} active videos)");
                    
                    // Pause all active videos in the current group
                    var pauseTasks = activeSourceIds
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Select(id => _videoService.PauseLayerAsync(id));
                    
                    await Task.WhenAll(pauseTasks).ConfigureAwait(false);
                }

                PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Paused);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.PauseCurrentGroupAsync: Error pausing: {ex}");
            }
        }

        /// <summary>
        /// Resumes playbook of the current group from where it was paused.
        /// </summary>
        /// <returns>A task representing the async operation.</returns>
        public async Task ResumeCurrentGroupAsync()
        {
            try
            {
                PlaylistGroupModel? currentGroup;
                lock (_lock)
                {
                    if (!_isPlaying || !_isPaused)
                    {
                        Debug.WriteLine("PlaylistService.ResumeCurrentGroupAsync: Not playing or not paused");
                        return;
                    }

                    _isPaused = false;
                    currentGroup = CurrentGroup;
                }

                if (currentGroup != null)
                {
                    // Filter to only active videos (those with registered decoders)
                    var activeSourceIds = _videoService.GetActiveLayerIds(currentGroup.SourceIds);
                    Debug.WriteLine($"PlaylistService.ResumeCurrentGroupAsync: Resuming group {currentGroup.Name} ({activeSourceIds.Count} active videos)");
                    
                    // Resume all active videos in the current group
                    var resumeTasks = activeSourceIds
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Select(id => _videoService.ResumeLayerAsync(id));
                    
                    await Task.WhenAll(resumeTasks).ConfigureAwait(false);
                }

                PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Playing);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.ResumeCurrentGroupAsync: Error resuming: {ex}");
            }
        }

        /// <summary>
        /// Stops the playlist and resets to the beginning.
        /// </summary>
        /// <param name="reEnableLooping">Whether to re-enable looping for all videos (true when exiting playlist mode completely).</param>
        /// <returns>A task representing the async operation.</returns>
        public async Task StopPlaylistAsync(bool reEnableLooping = false)
        {
            try
            {
                lock (_lock)
                {
                    _cts?.Cancel();
                    _isPlaying = false;
                    _isPaused = false;
                    _currentGroupIndex = -1;
                    _videoCompletionStatus.Clear();
                }

                Debug.WriteLine("PlaylistService.StopPlaylistAsync: Stopping all playback");

                // Disable playlist mode so all videos can render again
                _videoService.DisablePlaylistMode();

                await _videoService.StopAllAsync().ConfigureAwait(false);

                // Only re-enable looping when explicitly exiting playlist mode (e.g., switching to legacy mode)
                // Do NOT re-enable looping during normal stop operations (e.g., project load, restart)
                if (reEnableLooping)
                {
                    try
                    {
                        Debug.WriteLine("PlaylistService.StopPlaylistAsync: Re-enabling Loop for all videos (exiting playlist mode)");
                        _videoService.EnableLoopingForAll();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"PlaylistService.StopPlaylistAsync: Error re-enabling looping: {ex}");
                    }
                }

                PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Stopped);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.StopPlaylistAsync: Error stopping: {ex}");
            }
        }

        /// <summary>
        /// Restarts the playlist from the first group.
        /// </summary>
        /// <returns>A task representing the async operation.</returns>
        public async Task RestartPlaylistAsync()
        {
            try
            {
                List<PlaylistGroupModel> groups;
                lock (_lock)
                {
                    groups = _groups.ToList();
                    
                    // Reset playlist state without calling StopPlaylistAsync which unregisters decoders
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();
                    _isPlaying = false;
                    _isPaused = false;
                    _currentGroupIndex = -1;
                    _videoCompletionStatus.Clear();
                }

                Debug.WriteLine("PlaylistService.RestartPlaylistAsync: Restarting all decoders");
                
                // Use RestartAllAsync which properly re-registers decoders instead of just unregistering them
                await _videoService.RestartAllAsync().ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false); // Brief delay before restart

                if (groups.Count > 0)
                {
                    await StartPlaylistAsync(groups).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.RestartPlaylistAsync: Error restarting: {ex}");
            }
        }

        /// <summary>
        /// Manually advances to the next group. If at the last group, loops to first or stops based on EnableLooping.
        /// </summary>
        /// <returns>A task representing the async operation.</returns>
        public async Task AdvanceToNextGroupAsync()
        {
            try
            {
                int nextIndex;
                bool shouldLoop;
                
                lock (_lock)
                {
                    if (!_isPlaying)
                    {
                        Debug.WriteLine("PlaylistService.AdvanceToNextGroupAsync: Not playing, cannot advance");
                        return;
                    }

                    var currentIndex = _currentGroupIndex;
                    nextIndex = currentIndex + 1;
                    shouldLoop = _enableLooping;

                    if (nextIndex >= _groups.Count)
                    {
                        if (shouldLoop)
                        {
                            nextIndex = 0;
                            Debug.WriteLine("PlaylistService.AdvanceToNextGroupAsync: Looping back to first group");
                        }
                        else
                        {
                            Debug.WriteLine("PlaylistService.AdvanceToNextGroupAsync: Reached end of playlist, stopping");
                            nextIndex = -1;
                        }
                    }

                    GroupAdvancing?.Invoke(currentIndex, nextIndex);
                }

                if (nextIndex < 0)
                {
                    // Playlist completed and not looping
                    PlaylistCompleted?.Invoke();
                    await StopPlaylistAsync().ConfigureAwait(false);
                    return;
                }

                // Stop current group (runs in parallel with group index update for speed)
                var currentGroup = CurrentGroup;
                var stopTask = currentGroup != null 
                    ? StopGroupVideosAsync(currentGroup.SourceIds) 
                    : Task.CompletedTask;

                // Start next group setup immediately (don't wait for full stop)
                lock (_lock)
                {
                    _currentGroupIndex = nextIndex;
                    _videoCompletionStatus.Clear();
                }

                GroupChanged?.Invoke(nextIndex);

                if (nextIndex == 0)
                {
                    // We've looped back
                    PlaylistCompleted?.Invoke();
                }

                // Wait for stop to complete before starting new group
                await stopTask.ConfigureAwait(false);

                // OPTIMIZATION: Skip transition delay since we use fast resume
                // The pre-warming ensures next group is ready

                await StartCurrentGroupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.AdvanceToNextGroupAsync: Error advancing: {ex}");
            }
        }

        /// <summary>
        /// Jumps to a specific group by index.
        /// </summary>
        /// <param name="groupIndex">The 0-based index of the group to jump to.</param>
        /// <returns>A task representing the async operation.</returns>
        public async Task JumpToGroupAsync(int groupIndex)
        {
            try
            {
                lock (_lock)
                {
                    if (groupIndex < 0 || groupIndex >= _groups.Count)
                    {
                        Debug.WriteLine($"PlaylistService.JumpToGroupAsync: Invalid group index {groupIndex}");
                        return;
                    }
                }

                // Stop current group
                var currentGroup = CurrentGroup;
                if (currentGroup != null)
                {
                    await StopGroupVideosAsync(currentGroup.SourceIds).ConfigureAwait(false);
                }

                lock (_lock)
                {
                    _currentGroupIndex = groupIndex;
                    _videoCompletionStatus.Clear();
                    _isPlaying = true;
                    _isPaused = false;
                }

                GroupChanged?.Invoke(groupIndex);
                PlaybackStateChanged?.Invoke(PlaylistPlaybackState.Playing);

                await StartCurrentGroupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.JumpToGroupAsync: Error jumping to group {groupIndex}: {ex}");
            }
        }

        private async Task StartCurrentGroupAsync()
        {
            try
            {
                PlaylistGroupModel? currentGroup;
                CancellationToken ct;

                lock (_lock)
                {
                    currentGroup = CurrentGroup;
                    ct = _cts?.Token ?? CancellationToken.None;
                }

                if (currentGroup == null)
                {
                    Debug.WriteLine("PlaylistService.StartCurrentGroupAsync: No current group");
                    return;
                }

                Debug.WriteLine($"PlaylistService.StartCurrentGroupAsync: Starting group '{currentGroup.Name}' (index {_currentGroupIndex}) with {currentGroup.SourceIds.Count} videos, mode={currentGroup.PlaybackMode}");

                // CRITICAL FIX: Wait for all videos in this group to have their decoders ready
                // This ensures we don't start the group before all videos can actually play
                var ready = await _videoService.WaitForDecodersReadyAsync(currentGroup.SourceIds, timeoutMs: 5000).ConfigureAwait(false);
                if (!ready)
                {
                    Debug.WriteLine($"PlaylistService.StartCurrentGroupAsync: WARNING - Not all decoders ready for group '{currentGroup.Name}', proceeding with available decoders");
                }

                // CRITICAL FIX: Filter source IDs to only include videos that have registered decoders
                // This prevents tracking completion for videos that were deleted or never loaded
                var activeSourceIds = _videoService.GetActiveLayerIds(currentGroup.SourceIds);
                if (activeSourceIds.Count == 0)
                {
                    Debug.WriteLine($"PlaylistService.StartCurrentGroupAsync: No active videos in group '{currentGroup.Name}', skipping to next group");
                    // Clear render layers and hide all outputs so empty-group transitions don't leave stale frames on screen
                    _videoService.SetActiveRenderLayers(Enumerable.Empty<string>());
                    await _videoService.HideAllExceptGroupAsync(new System.Collections.Generic.List<string>()).ConfigureAwait(false);
                    await AdvanceToNextGroupAsync().ConfigureAwait(false);
                    return;
                }

                if (activeSourceIds.Count < currentGroup.SourceIds.Count)
                {
                    Debug.WriteLine($"PlaylistService.StartCurrentGroupAsync: WARNING - Only {activeSourceIds.Count} of {currentGroup.SourceIds.Count} videos have registered decoders");
                }

                // CRITICAL: Pause ALL video decoders first to prevent race conditions
                // This ensures no old frames are still being rendered while we set up the new group
                // Use PauseAllAsync instead of StopAllAsync to preserve decoder registrations
                await _videoService.PauseAllAsync().ConfigureAwait(false);

                // Small delay to ensure all decoders have fully stopped
                await Task.Delay(100).ConfigureAwait(false);

                // Now stop audio (already stopped but be sure)
                _videoService.StopAllAudio();

                // CRITICAL: Set which layers are allowed to render BEFORE starting videos
                // This ensures only the current group's videos appear on screen
                _videoService.SetActiveRenderLayers(activeSourceIds);

                // CRITICAL: Clear stale renderer frames for any layer NOT in this group.
                // PauseAllAsync stops decoding but leaves the last submitted bitmap visible on the
                // renderer until something overwrites it. Without this, every non-group video keeps
                // its frozen last-frame on the projector output indefinitely.
                await _videoService.HideAllExceptGroupAsync(activeSourceIds).ConfigureAwait(false);

                // Handle based on playback mode
                if (currentGroup.PlaybackMode == GroupPlaybackMode.Sequential)
                {
                    // Sequential mode: start only the first video
                    await StartSequentialGroupAsync(currentGroup, activeSourceIds).ConfigureAwait(false);
                }
                else
                {
                    // Simultaneous mode: start all videos at once
                    await StartSimultaneousGroupAsync(currentGroup, activeSourceIds).ConfigureAwait(false);
                }

                // OPTIMIZATION: Pre-warm the next group in background to eliminate transition delay
                _ = PreWarmNextGroupAsync();

                GroupChanged?.Invoke(_currentGroupIndex);
                Debug.WriteLine($"PlaylistService.StartCurrentGroupAsync: Group '{currentGroup.Name}' started with {activeSourceIds.Count} active videos");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.StartCurrentGroupAsync: Error starting group: {ex}");

                // If a group fails to start, try to advance to the next group
                await AdvanceToNextGroupAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Starts a group in simultaneous mode - all videos play at once.
        /// </summary>
        private async Task StartSimultaneousGroupAsync(PlaylistGroupModel group, List<string> activeSourceIds)
        {
            // Initialize completion tracking for ALL videos (wait for all to complete)
            _videoCompletionStatus.Clear();
            foreach (var sourceId in activeSourceIds)
            {
                if (!string.IsNullOrEmpty(sourceId))
                {
                    _videoCompletionStatus[sourceId] = false;
                    Debug.WriteLine($"PlaylistService.StartSimultaneousGroupAsync: Initialized completion tracking for {sourceId}");
                }
            }

            // Clear sequential state (not used in simultaneous mode)
            lock (_lock)
            {
                _sequentialVideoIndex = 0;
                _sequentialActiveSourceIds.Clear();
            }

            // OPTIMIZATION: Single call to disable looping
            try
            {
                Debug.WriteLine("PlaylistService.StartSimultaneousGroupAsync: Disabling loop for all videos");
                _videoService.DisableLoopingForAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.StartSimultaneousGroupAsync: Error disabling looping: {ex}");
            }

            // Start all videos in the group
            await _videoService.StartGroupVideosAsync(activeSourceIds).ConfigureAwait(false);

            // Enable audio for the preferred video (with delay to allow decoder initialization)
            await EnableGroupAudioAsync(group, activeSourceIds).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts a group in sequential mode - videos play one after another.
        /// </summary>
        private async Task StartSequentialGroupAsync(PlaylistGroupModel group, List<string> activeSourceIds)
        {
            // Store the list of videos to play sequentially
            lock (_lock)
            {
                _sequentialVideoIndex = 0;
                _sequentialActiveSourceIds = activeSourceIds.ToList();
            }

            // Clear simultaneous completion tracking (not used in sequential mode - we track one at a time)
            _videoCompletionStatus.Clear();

            // OPTIMIZATION: Single call to disable looping
            try
            {
                Debug.WriteLine("PlaylistService.StartSequentialGroupAsync: Disabling loop for all videos");
                _videoService.DisableLoopingForAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.StartSequentialGroupAsync: Error disabling looping: {ex}");
            }

            // Start only the first video
            await StartCurrentSequentialVideoAsync(group).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts the current video in a sequential group.
        /// Only hides/shows within the group since non-group layers are already hidden by StartCurrentGroupAsync.
        /// </summary>
        private async Task StartCurrentSequentialVideoAsync(PlaylistGroupModel? group)
        {
            string? currentVideoId;
            List<string> allActiveIds;
            lock (_lock)
            {
                if (_sequentialVideoIndex < 0 || _sequentialVideoIndex >= _sequentialActiveSourceIds.Count)
                {
                    Debug.WriteLine($"PlaylistService.StartCurrentSequentialVideoAsync: Invalid video index {_sequentialVideoIndex}");
                    return;
                }
                currentVideoId = _sequentialActiveSourceIds[_sequentialVideoIndex];
                allActiveIds = new List<string>(_sequentialActiveSourceIds);
            }

            if (string.IsNullOrEmpty(currentVideoId))
            {
                Debug.WriteLine("PlaylistService.StartCurrentSequentialVideoAsync: Current video ID is empty");
                return;
            }

            Debug.WriteLine($"PlaylistService.StartCurrentSequentialVideoAsync: Starting video {_sequentialVideoIndex + 1}/{_sequentialActiveSourceIds.Count}: {currentVideoId}");

            // Track completion for only the current video
            _videoCompletionStatus.Clear();
            _videoCompletionStatus[currentVideoId] = false;

            // Soft-pause other videos in the group (not all videos — non-group ones are already hidden)
            var otherGroupIds = allActiveIds.Where(id => id != currentVideoId).ToList();
            if (otherGroupIds.Count > 0)
            {
                var hideTasks = new List<Task>();
                foreach (var otherId in otherGroupIds)
                {
                    hideTasks.Add(_videoService.SoftPauseLayerAsync(otherId));
                    hideTasks.Add(_videoService.HideSourceOutputAndMeshesAsync(otherId));
                }
                await Task.WhenAll(hideTasks).ConfigureAwait(false);
            }

            // Start just this video
            await _videoService.StartGroupVideosAsync(new List<string> { currentVideoId }).ConfigureAwait(false);

            // Enable audio if this video should play audio (with delay to allow decoder initialization)
            if (group != null)
            {
                await EnableGroupAudioAsync(group, new List<string> { currentVideoId }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Advances to the next video within a sequential group.
        /// Returns true if advanced to next video, false if group is complete.
        /// </summary>
        private async Task<bool> AdvanceToNextSequentialVideoAsync()
        {
            PlaylistGroupModel? currentGroup;
            int nextIndex;

            lock (_lock)
            {
                currentGroup = CurrentGroup;
                nextIndex = _sequentialVideoIndex + 1;

                if (nextIndex >= _sequentialActiveSourceIds.Count)
                {
                    Debug.WriteLine($"PlaylistService.AdvanceToNextSequentialVideoAsync: Reached end of sequential group (index {nextIndex} >= {_sequentialActiveSourceIds.Count})");
                    return false; // Group is complete
                }

                // Stop audio for current video
                if (_sequentialVideoIndex >= 0 && _sequentialVideoIndex < _sequentialActiveSourceIds.Count)
                {
                    var currentId = _sequentialActiveSourceIds[_sequentialVideoIndex];
                    try
                    {
                        _videoService.StopAudioForLayer(currentId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"PlaylistService.AdvanceToNextSequentialVideoAsync: Error stopping audio for {currentId}: {ex}");
                    }
                }

                _sequentialVideoIndex = nextIndex;
            }

            Debug.WriteLine($"PlaylistService.AdvanceToNextSequentialVideoAsync: Advancing to video {nextIndex + 1}/{_sequentialActiveSourceIds.Count}");

            // Pause the previous video
            if (nextIndex > 0 && nextIndex <= _sequentialActiveSourceIds.Count)
            {
                var prevId = _sequentialActiveSourceIds[nextIndex - 1];
                try
                {
                    await _videoService.PauseLayerAsync(prevId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PlaylistService.AdvanceToNextSequentialVideoAsync: Error pausing previous video {prevId}: {ex}");
                }
            }

            // Start the next video
            await StartCurrentSequentialVideoAsync(currentGroup).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Enables audio for the preferred video in the group.
        /// Includes a delay to allow the decoder to fully initialize before enabling audio,
        /// preventing crashes from accessing uninitialized audio components.
        /// </summary>
        private async Task EnableGroupAudioAsync(PlaylistGroupModel group, List<string> activeSourceIds)
        {
            try
            {
                var preferredAudioLayerId = GetPreferredAudioSourceId(group);
                if (!string.IsNullOrEmpty(preferredAudioLayerId) && activeSourceIds.Contains(preferredAudioLayerId))
                {
                    // CRITICAL: Wait for the decoder to initialize its audio components
                    // before enabling audio. This prevents crashes from accessing
                    // uninitialized NAudio/WaveOut components.
                    Debug.WriteLine($"PlaylistService.EnableGroupAudioAsync: Waiting for decoder to initialize before enabling audio for {preferredAudioLayerId}");
                    await Task.Delay(500).ConfigureAwait(false);

                    Debug.WriteLine($"PlaylistService.EnableGroupAudioAsync: Enabling audio for preferred layer {preferredAudioLayerId}");
                    _videoService.StartAudioForLayer(preferredAudioLayerId);
                }
                else
                {
                    Debug.WriteLine("PlaylistService.EnableGroupAudioAsync: No preferred audio layer found for this group (PlayAudio=false for all or preferred layer not active)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.EnableGroupAudioAsync: Error enabling preferred group audio: {ex}");
            }
        }

        /// <summary>
        /// Pre-warms the next group's videos by ensuring their decoders are ready.
        /// This eliminates the decoder initialization delay during group transitions.
        /// </summary>
        private async Task PreWarmNextGroupAsync()
        {
            try
            {
                int nextIndex;
                PlaylistGroupModel? nextGroup;

                lock (_lock)
                {
                    if (!_isPlaying || _groups.Count == 0) return;

                    nextIndex = _currentGroupIndex + 1;
                    if (nextIndex >= _groups.Count)
                    {
                        nextIndex = _enableLooping ? 0 : -1;
                    }

                    if (nextIndex < 0 || nextIndex >= _groups.Count) return;
                    
                    // Skip if already pre-warmed
                    if (_preWarmedGroups.TryGetValue(nextIndex, out var warmed) && warmed)
                    {
                        Debug.WriteLine($"PlaylistService.PreWarmNextGroupAsync: Group {nextIndex} already pre-warmed");
                        return;
                    }

                    nextGroup = _groups[nextIndex];
                }

                if (nextGroup == null) return;

                Debug.WriteLine($"PlaylistService.PreWarmNextGroupAsync: Pre-warming group '{nextGroup.Name}' (index {nextIndex})");

                // Ensure all videos in the next group have registered decoders
                var activeIds = _videoService.GetActiveLayerIds(nextGroup.SourceIds);
                if (activeIds.Count > 0)
                {
                    // The decoders should already exist from project load, but calling soft-resume 
                    // ensures they are in a ready state with frames buffered
                    await _videoService.PreWarmLayersAsync(activeIds).ConfigureAwait(false);
                    
                    _preWarmedGroups[nextIndex] = true;
                    Debug.WriteLine($"PlaylistService.PreWarmNextGroupAsync: Group '{nextGroup.Name}' pre-warmed with {activeIds.Count} videos");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.PreWarmNextGroupAsync: Error pre-warming: {ex}");
            }
        }

        private async Task StopGroupVideosAsync(List<string> sourceIds)
        {
            try
            {
                if (sourceIds == null || sourceIds.Count == 0) return;

                // CRITICAL FIX: Stop audio for all videos in the group before stopping video playback
                Debug.WriteLine($"PlaylistService.StopGroupVideosAsync: Stopping audio for {sourceIds.Count} videos");
                foreach (var sourceId in sourceIds)
                {
                    if (string.IsNullOrEmpty(sourceId)) continue;
                    
                    try
                    {
                        _videoService.StopAudioForLayer(sourceId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"PlaylistService.StopGroupVideosAsync: Error stopping audio for {sourceId}: {ex}");
                    }
                }

                await _videoService.StopGroupVideosAsync(sourceIds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.StopGroupVideosAsync: Error stopping group: {ex}");
            }
        }

        /// <summary>
        /// Determines which video within the supplied group should provide audio output.
        /// Prefers the first source that currently has PlayAudio enabled.
        /// </summary>
        /// <param name="group">The playlist group under evaluation.</param>
        /// <returns>The layer ID that should provide audio, or null if none qualify.</returns>
        private string? GetPreferredAudioSourceId(PlaylistGroupModel? group)
        {
            if (group == null || group.SourceIds == null || group.SourceIds.Count == 0)
            {
                return null;
            }

            foreach (var sourceId in group.SourceIds)
            {
                if (string.IsNullOrEmpty(sourceId))
                {
                    continue;
                }

                try
                {
                    if (_videoService.ShouldPlayAudio(sourceId))
                    {
                        return sourceId;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PlaylistService.GetPreferredAudioSourceId: Failed to evaluate {sourceId}: {ex}");
                }
            }

            return null;
        }

        private void OnVideoCompleted(string layerId)
        {
            try
            {
                if (string.IsNullOrEmpty(layerId)) return;

                PlaylistGroupModel? currentGroup;
                bool isPaused;
                GroupPlaybackMode playbackMode;

                lock (_lock)
                {
                    if (!_isPlaying)
                    {
                        Debug.WriteLine($"PlaylistService.OnVideoCompleted: Playlist not playing, ignoring completion for {layerId}");
                        return;
                    }

                    isPaused = _isPaused;
                    currentGroup = CurrentGroup;
                    playbackMode = currentGroup?.PlaybackMode ?? GroupPlaybackMode.Simultaneous;
                    
                    // Check if this video is being tracked for completion (only active videos are tracked)
                    if (!_videoCompletionStatus.ContainsKey(layerId))
                    {
                        // This video is not being tracked (either not in current group or has no decoder)
                        Debug.WriteLine($"PlaylistService.OnVideoCompleted: Video '{layerId}' not being tracked for completion, ignoring");
                        return;
                    }

                    // Mark this video as completed
                    _videoCompletionStatus[layerId] = true;
                }

                if (isPaused)
                {
                    Debug.WriteLine($"PlaylistService.OnVideoCompleted: Playlist is paused, not advancing");
                    return;
                }

                // Handle based on playback mode
                if (playbackMode == GroupPlaybackMode.Sequential)
                {
                    HandleSequentialVideoCompleted(layerId, currentGroup);
                }
                else
                {
                    HandleSimultaneousVideoCompleted(layerId, currentGroup);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.OnVideoCompleted: Error handling video completion: {ex}");
            }
        }

        /// <summary>
        /// Handles video completion in sequential mode - advances to next video in sequence or next group.
        /// </summary>
        private void HandleSequentialVideoCompleted(string layerId, PlaylistGroupModel? currentGroup)
        {
            Debug.WriteLine($"PlaylistService.HandleSequentialVideoCompleted: Video '{layerId}' completed in group '{currentGroup?.Name}' (sequential mode)");

            // In sequential mode, when a video completes, try to advance to the next video
            Task.Run(async () =>
            {
                try
                {
                    bool hasMoreVideos = await AdvanceToNextSequentialVideoAsync().ConfigureAwait(false);
                    if (!hasMoreVideos)
                    {
                        // All videos in the sequential group have played, advance to next group
                        Debug.WriteLine($"PlaylistService.HandleSequentialVideoCompleted: All videos in sequential group completed, advancing to next group");
                        await AdvanceToNextGroupAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("PlaylistService.HandleSequentialVideoCompleted: Advance operation cancelled");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PlaylistService.HandleSequentialVideoCompleted: Error advancing: {ex}");
                    try
                    {
                        await RestartPlaylistAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex2)
                    {
                        Debug.WriteLine($"PlaylistService.HandleSequentialVideoCompleted: Recovery failed: {ex2}");
                    }
                }
            }).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    Debug.WriteLine($"PlaylistService.HandleSequentialVideoCompleted: Unhandled task exception: {t.Exception}");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Handles video completion in simultaneous mode - advances to next group when ALL videos complete.
        /// </summary>
        private void HandleSimultaneousVideoCompleted(string layerId, PlaylistGroupModel? currentGroup)
        {
            bool allCompleted;

            lock (_lock)
            {
                // Count only videos that are being tracked (have decoders)
                var trackedVideos = _videoCompletionStatus.Keys.ToList();
                var completedVideos = trackedVideos.Where(id => _videoCompletionStatus.TryGetValue(id, out var completed) && completed).ToList();
                
                Debug.WriteLine($"PlaylistService.HandleSimultaneousVideoCompleted: Video '{layerId}' completed in group '{currentGroup?.Name}' ({completedVideos.Count} of {trackedVideos.Count} tracked videos completed)");
                
                allCompleted = completedVideos.Count == trackedVideos.Count && trackedVideos.Count > 0;
                
                Debug.WriteLine($"PlaylistService.HandleSimultaneousVideoCompleted: Group completion status: {completedVideos.Count}/{trackedVideos.Count} videos completed (allCompleted={allCompleted})");
            }

            if (allCompleted)
            {
                Debug.WriteLine($"PlaylistService.HandleSimultaneousVideoCompleted: All tracked videos in group '{currentGroup?.Name}' completed, advancing to next group");
                
                // Clear pre-warmed status for next group since we're advancing
                lock (_lock)
                {
                    var nextIdx = _currentGroupIndex + 1;
                    if (nextIdx >= _groups.Count && _enableLooping)
                    {
                        nextIdx = 0;
                    }
                    _preWarmedGroups.TryRemove(nextIdx, out _);
                }
                
                // OPTIMIZATION: Advance immediately without artificial delay
                // Pre-warming ensures the next group is ready
                Task.Run(async () =>
                {
                    try
                    {
                        await AdvanceToNextGroupAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when playlist is stopped
                        Debug.WriteLine("PlaylistService.HandleSimultaneousVideoCompleted: Advance operation cancelled");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"PlaylistService.HandleSimultaneousVideoCompleted: Error advancing to next group: {ex}");
                        // Try to recover by restarting the playlist from the beginning
                        try
                        {
                            await RestartPlaylistAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex2)
                        {
                            Debug.WriteLine($"PlaylistService.HandleSimultaneousVideoCompleted: Recovery failed: {ex2}");
                        }
                    }
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        Debug.WriteLine($"PlaylistService.HandleSimultaneousVideoCompleted: Unhandled task exception: {t.Exception}");
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        /// <summary>
        /// Disposes resources used by the service.
        /// </summary>
        public void Dispose()
        {
            try
            {
                Debug.WriteLine("PlaylistService.Dispose: Unsubscribing from VideoCompleted event");
                _videoService.VideoCompleted -= OnVideoCompleted;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.Dispose: Error disposing: {ex}");
            }
        }
    }

    /// <summary>
    /// Represents the playback state of the playlist.
    /// </summary>
    public enum PlaylistPlaybackState
    {
        /// <summary>
        /// Playlist is stopped and not playing.
        /// </summary>
        Stopped,

        /// <summary>
        /// Playlist is playing.
        /// </summary>
        Playing,

        /// <summary>
        /// Playlist is paused.
        /// </summary>
        Paused
    }
}
