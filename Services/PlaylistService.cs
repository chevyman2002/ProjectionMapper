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
    /// Service that manages playlist playback with group-based sequential video playback.
    /// All videos in a group play simultaneously, and groups play sequentially.
    /// When all videos in a group complete, the service advances to the next group.
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

        // Track video completion status for the current group
        private readonly ConcurrentDictionary<string, bool> _videoCompletionStatus = new ConcurrentDictionary<string, bool>();

        // Configuration
        private int _groupTransitionDelayMs = 500;
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
                }

                Debug.WriteLine($"PlaylistService.StartPlaylistAsync: Starting playlist with {groups.Count} groups");
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
                    Debug.WriteLine($"PlaylistService.PauseCurrentGroupAsync: Pausing group {currentGroup.Name} ({currentGroup.SourceIds.Count} videos)");
                    
                    // Pause all videos in the current group
                    var pauseTasks = currentGroup.SourceIds
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
        /// Resumes playback of the current group from where it was paused.
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
                    Debug.WriteLine($"PlaylistService.ResumeCurrentGroupAsync: Resuming group {currentGroup.Name} ({currentGroup.SourceIds.Count} videos)");
                    
                    // Resume all videos in the current group
                    var resumeTasks = currentGroup.SourceIds
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
        /// <returns>A task representing the async operation.</returns>
        public async Task StopPlaylistAsync()
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
                await _videoService.StopAllAsync().ConfigureAwait(false);

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
                }

                await StopPlaylistAsync().ConfigureAwait(false);
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

                // Stop current group
                var currentGroup = CurrentGroup;
                if (currentGroup != null)
                {
                    await StopGroupVideosAsync(currentGroup.SourceIds).ConfigureAwait(false);
                }

                // Wait for transition delay
                if (_groupTransitionDelayMs > 0)
                {
                    await Task.Delay(_groupTransitionDelayMs).ConfigureAwait(false);
                }

                // Start next group
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

                Debug.WriteLine($"PlaylistService.StartCurrentGroupAsync: Starting group '{currentGroup.Name}' (index {_currentGroupIndex}) with {currentGroup.SourceIds.Count} videos");

                // Hide all videos except those in the current group
                await _videoService.HideAllExceptGroupAsync(currentGroup.SourceIds).ConfigureAwait(false);

                // Initialize completion tracking for all videos in the group
                foreach (var sourceId in currentGroup.SourceIds)
                {
                    if (!string.IsNullOrEmpty(sourceId))
                    {
                        _videoCompletionStatus[sourceId] = false;
                    }
                }

                // Start all videos in the group
                await _videoService.StartGroupVideosAsync(currentGroup.SourceIds).ConfigureAwait(false);

                GroupChanged?.Invoke(_currentGroupIndex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.StartCurrentGroupAsync: Error starting group: {ex}");
                
                // If a group fails to start, try to advance to the next group
                await AdvanceToNextGroupAsync().ConfigureAwait(false);
            }
        }

        private async Task StopGroupVideosAsync(List<string> sourceIds)
        {
            try
            {
                if (sourceIds == null || sourceIds.Count == 0) return;

                await _videoService.StopGroupVideosAsync(sourceIds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.StopGroupVideosAsync: Error stopping group: {ex}");
            }
        }

        private void OnVideoCompleted(string layerId)
        {
            try
            {
                if (string.IsNullOrEmpty(layerId)) return;

                PlaylistGroupModel? currentGroup;
                bool allCompleted;
                bool isPaused;

                lock (_lock)
                {
                    if (!_isPlaying)
                    {
                        return;
                    }

                    isPaused = _isPaused;
                    currentGroup = CurrentGroup;
                    
                    if (currentGroup == null || !currentGroup.SourceIds.Contains(layerId))
                    {
                        // This video is not in the current group, ignore
                        return;
                    }

                    // Mark this video as completed
                    _videoCompletionStatus[layerId] = true;
                    Debug.WriteLine($"PlaylistService.OnVideoCompleted: Video '{layerId}' completed in group '{currentGroup.Name}'");

                    // Check if all videos in the group have completed
                    allCompleted = currentGroup.SourceIds
                        .Where(id => !string.IsNullOrEmpty(id))
                        .All(id => _videoCompletionStatus.TryGetValue(id, out var completed) && completed);
                }

                if (allCompleted && !isPaused)
                {
                    Debug.WriteLine($"PlaylistService.OnVideoCompleted: All videos in group '{currentGroup?.Name}' completed, advancing to next group");
                    
                    // Advance to next group asynchronously
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AdvanceToNextGroupAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"PlaylistService.OnVideoCompleted: Error advancing to next group: {ex}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlaylistService.OnVideoCompleted: Error handling video completion: {ex}");
            }
        }

        /// <summary>
        /// Disposes resources used by the service.
        /// </summary>
        public void Dispose()
        {
            try
            {
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
