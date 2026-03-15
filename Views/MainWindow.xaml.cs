using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.Models;
using ProjectionMapper.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using ProjectionMapper.Views;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Numerics;

namespace ProjectionMapper
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _vm;

        // Renderer and manager
        private readonly SoftwareRenderer _softwareRenderer;
        private readonly RendererManager _rendererManager;

        // Services
        private readonly VideoService _videoService;
        private readonly FileDialogService _fileDialog;
        private readonly ProjectService _projectService;
        private readonly PlaylistService _playlistService;

        // Monitor list for UI
        private readonly ObservableCollection<MonitorItem> _monitorItems = new();
        private List<MonitorInfo> _monitors = new();

        // Track current project file path
        private string? _currentProjectPath;

        // Track fullscreen windows for preview restore
        private readonly Dictionary<int, bool> _previewMonitorStates = new();
        private readonly Dictionary<int, FullScreenOutputWindow> _activeMonitorWindows = new();

        // P/Invoke for positioning windows
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        /// <summary>
        /// Stores both physical (native resolution) and virtual (DPI-scaled) monitor dimensions.
        /// PhysicalWidth/Height = the actual pixel count of the display
        /// Width/Height/Left/Top = the virtual screen coordinates (used for window positioning)
        /// </summary>
        private record MonitorInfo(int Width, int Height, int Left, int Top, int PhysicalWidth, int PhysicalHeight);

        private class MonitorItem
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public override string ToString() => Name;
        }

        public MainWindow()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("MainWindow constructor starting...");
                
                InitializeComponent();

                _vm = new MainWindowViewModel();
                DataContext = _vm;
                _vm.PropertyChanged += OnViewModelPropertyChanged;

                // Track audio flag changes for imported videos to keep VideoService audio state in sync
                try
                {
                    _vm.ImportedVideos.CollectionChanged += OnImportedVideosCollectionChanged;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow: Failed to subscribe to ImportedVideos changes: {ex}");
                }

                // Create a software renderer and renderer manager, attach the host (output preview)
                _softwareRenderer = new SoftwareRenderer();
                _rendererManager = new RendererManager(_softwareRenderer);
                // Attach to the output host so the composed output shows on the right panel
                _rendererManager.AttachHost(PART_OutputHost);

                // Make the renderer manager available as a resource so child controls can find it if needed
                this.Resources["RendererManager"] = _rendererManager;

                // Create VideoService (ffmpeg path empty -> expects ffmpeg on PATH)
                _videoService = new VideoService(_rendererManager, ffmpegPath: null);
                _fileDialog = new FileDialogService();
                _projectService = new ProjectService();
                
                // Create PlaylistService for group-based playback
                _playlistService = new PlaylistService(_videoService);
                UpdateLoopingMode(_vm.IsPlaylistMode);
                
                // Expose VideoService so MeshEditorControl can subscribe for isolated previews
                this.Resources["VideoService"] = _videoService;

                // Wire event handlers to actual implementations
                _vm.ImportRequested += OnImportRequested;
                _vm.PreviewRequested += OnPreviewRequested;
                _vm.DeleteImportedRequested += OnDeleteImportedRequested;
                _vm.SaveProjectRequested += OnSaveProjectRequested;
                _vm.SaveAsProjectRequested += OnSaveAsProjectRequested;
                _vm.LoadProjectRequested += OnLoadProjectRequested;
                _vm.NewProjectRequested += OnNewProjectRequested;
                _vm.PlayPauseRequestedAsync += OnPlayPauseRequestedAsync;
                _vm.RestartRequestedAsync += OnRestartRequestedAsync;
                _vm.MeshLayerCreated += OnMeshLayerCreated;

                // Hook up playlist service events
                _playlistService.GroupChanged += (index) =>
                {
                    try
                    {
                        Debug.WriteLine($"MainWindow: Playlist group changed to index {index}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: Playlist group changed handler error: {ex}");
                    }
                };
                _playlistService.PlaylistCompleted += () =>
                {
                    try
                    {
                        Debug.WriteLine("MainWindow: Playlist completed");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: Playlist completed handler error: {ex}");
                    }
                };

                // Basic Loaded handler - initialize renderer before loading any project
                Loaded += async (_, __) =>
                {
                    try
                    {
                        // Initialize renderer with default HD size
                        var w = 1920;
                        var h = 1080;
                        await _rendererManager.StartAsync(w, h);
                        Debug.WriteLine($"MainWindow: Renderer initialized with size {w}x{h}");

                        // Enumerate monitors and populate the dropdown
                        EnumerateMonitors();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Renderer start failed: {ex}");
                    }
                };

                // Handle window closing with proper cleanup and shutdown
                Closing += async (sender, e) =>
                {
                    try
                    {
                        Debug.WriteLine("MainWindow: Window closing, checking for unsaved changes");
                        
                        // Check for unsaved changes
                        if (_vm.HasUnsavedChanges)
                        {
                            var result = MessageBox.Show(
                                "You have unsaved changes. Do you want to save before closing?",
                                "Unsaved Changes",
                                MessageBoxButton.YesNoCancel,
                                MessageBoxImage.Question);

                            if (result == MessageBoxResult.Cancel)
                            {
                                e.Cancel = true;
                                return;
                            }

                            if (result == MessageBoxResult.Yes)
                            {
                                await OnSaveProjectRequested();
                                // If save failed or was cancelled, don't close
                                if (_vm.HasUnsavedChanges)
                                {
                                    e.Cancel = true;
                                    return;
                                }
                            }
                        }

                        Debug.WriteLine("MainWindow: Disposing services");
                        
                        // Stop all video playback first - use Task.Run to avoid deadlock on UI thread
                        try 
                        { 
                            Task.Run(async () => await _videoService.StopAllAsync()).Wait(2000); 
                        } 
                        catch (Exception ex) 
                        { 
                            Debug.WriteLine($"MainWindow: StopAllAsync failed: {ex}"); 
                        }
                        
                        // Close all fullscreen windows first
                        foreach (var kv in _activeMonitorWindows.ToList())
                        {
                            try { kv.Value.Close(); } catch (Exception ex) { Debug.WriteLine($"MainWindow: Closing fullscreen window failed: {ex}"); }
                        }
                        _activeMonitorWindows.Clear();
                        
                        try { _rendererManager?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"MainWindow: RendererManager dispose failed: {ex}"); }
                        try { _videoService?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"MainWindow: VideoService dispose failed: {ex}"); }
                        try { _playlistService?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"MainWindow: PlaylistService dispose failed: {ex}"); }

                        Debug.WriteLine("MainWindow: Cleanup completed, shutting down application");
                        
                        // Force application shutdown
                        Application.Current.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: Closing handler failed: {ex}");
                        // Force shutdown even if cleanup fails
                        Application.Current.Shutdown();
                    }
                };

                System.Diagnostics.Debug.WriteLine("MainWindow constructor completed successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MainWindow constructor failed: {ex}");
                
                // Show error dialog
                try
                {
                    MessageBox.Show($"Failed to initialize application: {ex.Message}\n\nCheck debug output for details.", 
                        "Initialization Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                }
                catch { }
                
                throw; // Re-throw to prevent partially initialized window
            }
        }

        #region Global Mesh Overlay Toggle

        /// <summary>
        /// Handles the global mesh overlay visibility toggle.
        /// When checked, enables Visible for all mesh layers on all videos.
        /// When unchecked, disables Visible for all mesh layers on all videos.
        /// Individual mesh layers can still be toggled independently after this.
        /// </summary>
        private void OnGlobalShowMeshOverlayChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                // Guard against null _rendererManager during initialization
                // (checkbox events can fire during InitializeComponent before services are created)
                if (_rendererManager == null)
                {
                    Debug.WriteLine("**_rendererManager** was null.");
                    return;
                }

                bool show = PART_GlobalShowMeshOverlayCheckbox.IsChecked == true;

                // NOTE: We do NOT use _rendererManager.ShowMeshOverlay as a global blocker.
                // The global checkbox simply checks/unchecks the individual Visible property
                // for each mesh layer. Individual layers can be toggled independently afterward.

                Debug.WriteLine($"MainWindow: Global mesh overlay toggle - setting all mesh layers Visible={show}");

                int toggledCount = 0;
                foreach (var video in _vm.ImportedVideos)
                {
                    foreach (var mesh in video.MeshLayers)
                    {
                        if (mesh == null) continue;

                        // Toggle the Visible property - this controls overlay visibility
                        // The PropertyChanged event will trigger overlay updates automatically
                        mesh.Visible = show;
                        toggledCount++;
                    }
                }

                Debug.WriteLine($"MainWindow: Toggled Visible for {toggledCount} mesh layers");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnGlobalShowMeshOverlayChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Refreshes mesh overlays for all mesh layers to reflect current visibility state.
        /// </summary>
        private void RefreshAllMeshOverlays()
        {
            try
            {
                foreach (var video in _vm.ImportedVideos)
                {
                    foreach (var mesh in video.MeshLayers)
                    {
                        if (mesh?.Model == null) continue;
                        if (!mesh.ShowOverlay) continue;

                        var layerId = mesh.Model.Id;
                        if (string.IsNullOrEmpty(layerId)) continue;

                        var targetMonitor = mesh.TargetMonitorIndex;
                        var quadPoints = _rendererManager.MapNormalizedToRendererPoints(mesh.OutputMeshPoints, targetMonitor >= 0 ? targetMonitor : null);

                        if (quadPoints != null && quadPoints.Length >= 4)
                        {
                            _rendererManager.AddMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, quadPoints, true, layerId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: RefreshAllMeshOverlays failed: {ex}");
            }
        }

        #endregion

        #region Event Handler Implementations

        private async void OnImportRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Import requested");
                
                var path = await _fileDialog.ShowOpenFileDialogAsync(
                    "Import Video",
                    "Video Files (*.mp4;*.avi;*.mov;*.mkv;*.wmv)|*.mp4;*.avi;*.mov;*.mkv;*.wmv|All Files (*.*)|*.*");

                if (string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine("MainWindow: Import cancelled by user");
                    return;
                }

                Debug.WriteLine($"MainWindow: Importing video from {path}");

                // Create imported video view model
                var id = Guid.NewGuid().ToString("N");
                var name = Path.GetFileNameWithoutExtension(path);
                var imported = new ImportedVideoViewModel(id, name, path);

                // Create host layer for this video
                var hostLayer = new LayerModel
                {
                    Id = id,
                    Name = $"{name} (Host)",
                    SourcePath = path,
                    Width = _rendererManager.OutputWidth,
                    Height = _rendererManager.OutputHeight,
                    Visible = true,
                    PreviewOnly = false
                };

                imported.HostLayer = hostLayer;
                imported.NotifyHostLayerChanged();

                // Add to view model
                _vm.ImportedVideos.Add(imported);
                _vm.SelectedImportedVideo = imported;

                // Register with video service
                await _videoService.RegisterLayerAsync(hostLayer, playAudio: false);

                Debug.WriteLine($"MainWindow: Successfully imported video '{name}'");
                _vm.StatusText = $"Imported: {name}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Import failed: {ex}");
                MessageBox.Show($"Failed to import video: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnPreviewRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Preview requested - feature not yet implemented");
                MessageBox.Show("Full-screen preview feature coming soon!", "Preview", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Preview failed: {ex}");
            }
        }

        private async void OnDeleteImportedRequested(ImportedVideoViewModel? imported)
        {
            try
            {
                if (imported == null)
                {
                    Debug.WriteLine("MainWindow: Delete imported requested with null video");
                    return;
                }

                Debug.WriteLine($"MainWindow: Delete imported requested for '{imported.Name}'");

                var result = MessageBox.Show(
                    $"Are you sure you want to delete '{imported.Name}' and all its mesh layers?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    Debug.WriteLine("MainWindow: Delete cancelled by user");
                    return;
                }

                // Unregister host layer
                if (imported.HostLayer != null)
                {
                    await _videoService.UnregisterLayerAsync(imported.HostLayer.Id ?? string.Empty);
                }

                // Unregister all mesh layers
                foreach (var mesh in imported.MeshLayers.ToArray())
                {
                    if (mesh.Model?.Id != null)
                    {
                        await _videoService.UnregisterMeshLayerAsync(mesh.Model.Id);
                    }
                }

                // Remove from view model
                _vm.ImportedVideos.Remove(imported);

                Debug.WriteLine($"MainWindow: Successfully deleted '{imported.Name}'");
                _vm.StatusText = $"Deleted: {imported.Name}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Delete imported failed: {ex}");
                MessageBox.Show($"Failed to delete video: {ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnSaveProjectRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Save project requested");

                // If no current path, do Save As
                if (string.IsNullOrEmpty(_currentProjectPath))
                {
                    await OnSaveAsProjectRequested();
                    return;
                }

                Debug.WriteLine($"MainWindow: Saving project to {_currentProjectPath}");

                // Build project model from current state
                var project = BuildProjectModel();

                // Save project
                var success = await _projectService.SaveAsync(project, _currentProjectPath);

                if (success)
                {
                    _vm.MarkProjectClean();
                    Debug.WriteLine("MainWindow: Project saved successfully");
                    _vm.StatusText = $"Saved: {Path.GetFileName(_currentProjectPath)}";
                }
                else
                {
                    Debug.WriteLine("MainWindow: Project save failed");
                    MessageBox.Show("Failed to save project. Check debug output for details.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Save project failed: {ex}");
                MessageBox.Show($"Failed to save project: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnSaveAsProjectRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Save As project requested");

                var path = await _fileDialog.ShowSaveFileDialogAsync(
                    "Save Project As",
                    "Untitled.pmproj",
                    "ProjectionMapper Project (*.pmproj)|*.pmproj|All Files (*.*)|*.*");

                if (string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine("MainWindow: Save As cancelled by user");
                    return;
                }

                Debug.WriteLine($"MainWindow: Saving project to {path}");

                // Build project model from current state
                var project = BuildProjectModel();

                // Save project
                var success = await _projectService.SaveAsync(project, path);

                if (success)
                {
                    _currentProjectPath = path;
                    _vm.MarkProjectClean();
                    Debug.WriteLine("MainWindow: Project saved successfully");
                    _vm.StatusText = $"Saved: {Path.GetFileName(path)}";
                    Title = $"ProjectionMapper - {Path.GetFileName(path)}";
                }
                else
                {
                    Debug.WriteLine("MainWindow: Project save failed");
                    MessageBox.Show("Failed to save project. Check debug output for details.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Save As project failed: {ex}");
                MessageBox.Show($"Failed to save project: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnLoadProjectRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Load project requested");

                // Check for unsaved changes
                if (_vm.HasUnsavedChanges)
                {
                    var result = MessageBox.Show(
                        "You have unsaved changes. Do you want to save before loading a new project?",
                        "Unsaved Changes",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel)
                    {
                        Debug.WriteLine("MainWindow: Load cancelled by user");
                        return;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        await OnSaveProjectRequested();
                        // If save failed, don't load
                        if (_vm.HasUnsavedChanges)
                        {
                            return;
                        }
                    }
                }

                var path = await _fileDialog.ShowOpenFileDialogAsync(
                    "Load Project",
                    "ProjectionMapper Project (*.pmproj)|*.pmproj|All Files (*.*)|*.*");

                if (string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine("MainWindow: Load cancelled by user");
                    return;
                }

                Debug.WriteLine($"MainWindow: Loading project from {path}");

                // Load project
                var project = await _projectService.LoadAsync(path);

                if (project == null)
                {
                    Debug.WriteLine("MainWindow: Project load failed");
                    MessageBox.Show("Failed to load project. The file may be corrupted or incompatible.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Clear current project state
                await ClearProjectState();

                // Load project state into UI
                await LoadProjectState(project);

                _currentProjectPath = path;
                _vm.MarkProjectClean();
                Debug.WriteLine("MainWindow: Project loaded successfully");
                _vm.StatusText = $"Loaded: {Path.GetFileName(path)}";
                Title = $"ProjectionMapper - {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Load project failed: {ex}");
                MessageBox.Show($"Failed to load project: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnNewProjectRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: New project requested");

                // Check for unsaved changes
                if (_vm.HasUnsavedChanges)
                {
                    var result = MessageBox.Show(
                        "You have unsaved changes. Do you want to save before creating a new project?",
                        "Unsaved Changes",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel)
                    {
                        Debug.WriteLine("MainWindow: New project cancelled by user");
                        return;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        await OnSaveProjectRequested();
                        // If save failed, don't create new project
                        if (_vm.HasUnsavedChanges)
                        {
                            return;
                        }
                    }
                }

                // Clear current project state
                await ClearProjectState();

                _currentProjectPath = null;
                _vm.MarkProjectClean();
                Debug.WriteLine("MainWindow: New project created");
                _vm.StatusText = "New project created";
                Title = "ProjectionMapper - Untitled";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: New project failed: {ex}");
                MessageBox.Show($"Failed to create new project: {ex.Message}", "New Project Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnPlayPauseRequestedAsync()
        {
            try
            {
                Debug.WriteLine($"MainWindow: Play/Pause requested, IsPlaying={_vm.IsPlaying}");

                if (_vm.IsPlaying)
                {
                    if (!_vm.IsPlaylistMode && _vm.PlaylistGroups.Count > 0)
                    {
                        Debug.WriteLine("MainWindow: Playlist groups detected - enabling playlist mode automatically");
                        _vm.IsPlaylistMode = true;
                    }

                    // Start or resume playback
                    if (_vm.IsPlaylistMode)
                    {
                        // Check if playlist is paused - resume instead of starting fresh
                        if (_playlistService.IsPaused)
                        {
                            Debug.WriteLine("MainWindow: Resuming playlist playback from pause");
                            await _playlistService.ResumeCurrentGroupAsync();
                        }
                        else
                        {
                            Debug.WriteLine("MainWindow: Starting playlist playback");
                            // Build list of playlist groups from the view model
                            var groups = _vm.BuildPlaylistGroupModels();
                            await _playlistService.StartPlaylistAsync(groups);
                        }
                    }
                    else
                    {
                        Debug.WriteLine("MainWindow: Starting legacy playback");
                        await _videoService.ResumeAllAsync();
                    }
                    _vm.StatusText = "Playing";
                }
                else
                {
                    // Pause playback
                    if (_vm.IsPlaylistMode)
                    {
                        Debug.WriteLine("MainWindow: Pausing playlist playback");
                        await _playlistService.PauseCurrentGroupAsync();
                    }
                    else
                    {
                        Debug.WriteLine("MainWindow: Pausing legacy playback");
                        await _videoService.PauseAllAsync();
                    }
                    _vm.StatusText = "Paused";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Play/Pause failed: {ex}");
                MessageBox.Show($"Playback error: {ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnRestartRequestedAsync()
        {
            try
            {
                Debug.WriteLine("MainWindow: Restart requested");

                if (_vm.IsPlaylistMode)
                {
                    Debug.WriteLine("MainWindow: Restarting playlist playback");
                    await _playlistService.RestartPlaylistAsync();
                }
                else
                {
                    Debug.WriteLine("MainWindow: Restarting legacy playback");
                    await _videoService.RestartAllAsync();
                }

                _vm.StatusText = "Restarted";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Restart failed: {ex}");
                MessageBox.Show($"Restart error: {ex.Message}", "Restart Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnMeshLayerCreated(LayerModel? layer)
        {
            try
            {
                if (layer == null)
                {
                    Debug.WriteLine("MainWindow: Mesh layer created with null model");
                    return;
                }

                Debug.WriteLine($"MainWindow: Mesh layer created: {layer.Name}");
                await _videoService.RegisterMeshLayerAsync(layer);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Mesh layer registration failed: {ex}");
            }
        }

        /// <summary>
        /// Reacts to view-model property changes so service behavior (like looping) stays in sync with UI state.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsPlaylistMode))
                {
                    UpdateLoopingMode(_vm.IsPlaylistMode);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnViewModelPropertyChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Ensures decoder looping behavior matches the active playback mode. Playlist mode disables looping so clips stop at EOF.
        /// </summary>
        private void UpdateLoopingMode(bool playlistModeActive)
        {
            try
            {
                if (playlistModeActive)
                {
                    _videoService.DisableLoopingForAll();
                    Debug.WriteLine("MainWindow: Looping disabled (playlist mode)");
                }
                else
                {
                    _videoService.EnableLoopingForAll();
                    Debug.WriteLine("MainWindow: Looping enabled (legacy mode)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: UpdateLoopingMode failed: {ex}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Subscribes to property changes on imported videos so we can react to PlayAudio toggles.
        /// </summary>
        private void AttachImportedVideoHandlers(ImportedVideoViewModel? video)
        {
            if (video == null) return;

            try
            {
                video.PropertyChanged -= ImportedVideo_PropertyChanged;
                video.PropertyChanged += ImportedVideo_PropertyChanged;

                video.MeshLayers.CollectionChanged -= MeshLayers_CollectionChanged;
                video.MeshLayers.CollectionChanged += MeshLayers_CollectionChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: AttachImportedVideoHandlers failed: {ex}");
            }
        }

        // Track when last EnsureMonitorWindows was called to prevent rapid calls
        private DateTime _lastEnsureMonitorWindowsCall = DateTime.MinValue;
        private readonly object _ensureMonitorWindowsLock = new();

        /// <summary>
        /// Ensures fullscreen output windows exist for all monitors referenced by any mesh layer.
        /// Also removes windows that are no longer needed.
        /// </summary>
        private void EnsureMonitorWindows()
        {
            try
            {
                // Throttle calls to prevent rapid window creation/destruction
                lock (_ensureMonitorWindowsLock)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastEnsureMonitorWindowsCall).TotalMilliseconds < 100)
                    {
                        return;
                    }
                    _lastEnsureMonitorWindowsCall = now;
                }

                if (_monitors == null || _monitors.Count == 0)
                {
                    // Nothing to reconcile without monitor metadata
                    foreach (var idx in _activeMonitorWindows.Keys.ToList())
                    {
                        HideMonitorWindow(idx);
                    }
                    return;
                }

                var required = new HashSet<int>();

                // Only mesh layers target monitors - collect all target monitor indices
                foreach (var video in _vm.ImportedVideos)
                {
                    foreach (var mesh in video.MeshLayers)
                    {
                        var meshMonitor = mesh.Model?.TargetMonitorIndex ?? -1;
                        if (meshMonitor >= 0)
                        {
                            required.Add(meshMonitor);
                        }
                    }
                }

                // Hide windows no longer referenced by any layer
                foreach (var active in _activeMonitorWindows.Keys.ToList())
                {
                    if (!required.Contains(active))
                    {
                        HideMonitorWindow(active);
                    }
                }

                // Spin up windows for newly required monitors
                foreach (var monitorIndex in required)
                {
                    ShowMonitorWindow(monitorIndex);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: EnsureMonitorWindows failed: {ex}");
            }
        }

        // P/Invoke to place window at exact monitor pixel bounds
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>
        /// Creates (or reuses) a fullscreen output window for the requested monitor.
        /// Uses raw pixel coordinates via SetWindowPos for reliable positioning on wireless displays.
        /// </summary>
        private void ShowMonitorWindow(int monitorIndex)
        {
            if (monitorIndex < 0)
            {
                return;
            }

            // Check if monitors list is valid and has this index
            if (_monitors == null || monitorIndex >= _monitors.Count)
            {
                Debug.WriteLine($"MainWindow: ShowMonitorWindow - monitor index {monitorIndex} out of range (count: {_monitors?.Count ?? 0})");
                return;
            }

            if (_activeMonitorWindows.ContainsKey(monitorIndex))
            {
                return;
            }

            try
            {
                var mon = _monitors[monitorIndex];
                if (mon == null)
                {
                    Debug.WriteLine($"MainWindow: ShowMonitorWindow - monitor {monitorIndex} is null");
                    return;
                }

                // Create fullscreen window - we'll position it using raw Win32 SetWindowPos
                // to avoid DPI scaling issues with wireless displays
                var win = new FullScreenOutputWindow
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    WindowState = WindowState.Normal,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };

                win.Closed += FullScreenWindow_Closed;
                win.Tag = monitorIndex;

                // Ensure native handle exists so we can position the window before showing it
                try
                {
                    var helper = new WindowInteropHelper(win);
                    var hwnd = helper.EnsureHandle();

                    const int GWL_STYLE = -16;
                    const int GWL_EXSTYLE = -20;
                    const int WS_POPUP = unchecked((int)0x80000000);
                    const int WS_EX_TOPMOST = 0x00000008;
                    const uint SWP_FRAMECHANGED = 0x0020;
                    const uint SWP_NOACTIVATE = 0x0010;


                    // Set window style to popup for borderless fullscreen
                    var prevStyle = GetWindowLong(hwnd, GWL_STYLE);
                    SetWindowLong(hwnd, GWL_STYLE, (prevStyle | WS_POPUP) & ~0x00C00000); // Remove WS_CAPTION

                    Debug.WriteLine($"MainWindow: Calling SetWindowPos for monitor {monitorIndex} -> virtual rect {mon.Left},{mon.Top} {mon.Width}x{mon.Height}, physical: {mon.PhysicalWidth}x{mon.PhysicalHeight}");
                    
                    // Use SetWindowPos with virtual screen coordinates (what Windows expects)
                    bool ok = SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, 
                        SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                    var err = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"MainWindow: SetWindowPos returned {ok}, GetLastError={err}");

                    if (!ok)
                    {
                        // Retry with different flags
                        ok = SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, 
                            SWP_SHOWWINDOW | SWP_FRAMECHANGED | SWP_NOACTIVATE);
                        err = Marshal.GetLastWin32Error();
                        Debug.WriteLine($"MainWindow: Retry SetWindowPos returned {ok}, GetLastError={err}");
                    }

                    // Show the window AFTER native positioning - do NOT use WindowState.Maximized
                    // as it will override our SetWindowPos positioning
                    win.Show();

                    _activeMonitorWindows[monitorIndex] = win;

                    // Initialize the fullscreen renderer for this monitor using PHYSICAL pixel dimensions
                    // This ensures the renderer outputs at the native resolution for crisp display
                    _rendererManager.ShowFullScreenWindow(monitorIndex, win, mon.PhysicalWidth, mon.PhysicalHeight);
                    _rendererManager.AttachHost(monitorIndex, win);

                    // Ensure window stays on top and at correct position after showing
                    SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, SWP_SHOWWINDOW);

                    Debug.WriteLine($"MainWindow: ShowMonitorWindow success for monitor {monitorIndex}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow: ShowMonitorWindow placement/show failed: {ex}");
                    try
                    {
                        // Fallback: use WPF properties but still use physical dimensions for renderer
                        win.Left = mon.Left;
                        win.Top = mon.Top;
                        win.Width = mon.Width;
                        win.Height = mon.Height;
                        win.Show();
                        _activeMonitorWindows[monitorIndex] = win;
                        _rendererManager.ShowFullScreenWindow(monitorIndex, win, mon.PhysicalWidth, mon.PhysicalHeight);
                        _rendererManager.AttachHost(monitorIndex, win);
                    }
                    catch (Exception ex2)
                    {
                        Debug.WriteLine($"MainWindow: ShowMonitorWindow fallback show failed: {ex2}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: ShowMonitorWindow failed for monitor {monitorIndex}: {ex}");
            }
        }

        /// <summary>
        /// Hides and disposes the fullscreen output window assigned to the specified monitor.
        /// </summary>
        private void HideMonitorWindow(int monitorIndex)
        {
            if (!_activeMonitorWindows.TryGetValue(monitorIndex, out var window))
            {
                return;
            }

            try
            {
                window.Closed -= FullScreenWindow_Closed;
                _activeMonitorWindows.Remove(monitorIndex);
                _rendererManager.HideFullScreenWindow(monitorIndex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: HideMonitorWindow failed for monitor {monitorIndex}: {ex}");
            }
        }

        /// <summary>
        /// Cleans up renderer bookkeeping if a fullscreen window is closed directly by the user (e.g., via ESC).
        /// </summary>
        private void FullScreenWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is not FullScreenOutputWindow window)
            {
                return;
            }

            if (window.Tag is int monitorIndex)
            {
                window.Closed -= FullScreenWindow_Closed;
                _activeMonitorWindows.Remove(monitorIndex);

                try
                {
                    _rendererManager.HideFullScreenWindow(monitorIndex);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow: FullScreenWindow_Closed cleanup failed for monitor {monitorIndex}: {ex}");
                }
            }
        }

        /// <summary>
        /// Removes property change handlers from imported videos to prevent memory leaks.
        /// </summary>
        private void DetachImportedVideoHandlers(ImportedVideoViewModel? video)
        {
            if (video == null) return;

            try
            {
                video.PropertyChanged -= ImportedVideo_PropertyChanged;
                video.MeshLayers.CollectionChanged -= MeshLayers_CollectionChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: DetachImportedVideoHandlers failed: {ex}");
            }
        }

        private void MeshLayers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: MeshLayers_CollectionChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Handles changes to imported video properties so audio state stays consistent with the UI.
        /// </summary>
        private void ImportedVideo_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ImportedVideoViewModel video || e.PropertyName != nameof(ImportedVideoViewModel.PlayAudio))
            {
                return;
            }

            try
            {
                var layerId = video.HostLayer?.Id;
                if (string.IsNullOrEmpty(layerId))
                {
                    return;
                }

                // CRITICAL FIX: In playlist mode, only allow audio changes for videos in the current group
                // Otherwise, toggling audio on one video would affect all videos
                if (_vm.IsPlaylistMode && _playlistService.IsPlaying)
                {
                    var currentGroup = _playlistService.CurrentGroup;
                    if (currentGroup == null || !currentGroup.SourceIds.Contains(layerId))
                    {
                        Debug.WriteLine($"MainWindow: Ignoring audio toggle for {layerId} - not in current playlist group");
                        return;
                    }
                }

                if (video.PlayAudio)
                {
                    _videoService.StartAudioForLayer(layerId);
                }
                else
                {
                    _videoService.StopAudioForLayer(layerId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: ImportedVideo_PropertyChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Keeps handler subscriptions aligned with the imported videos collection.
        /// </summary>
        private void OnImportedVideosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    foreach (var video in _vm.ImportedVideos)
                    {
                        AttachImportedVideoHandlers(video);
                    }
                    EnsureMonitorWindows();
                    return;
                }

                if (e.NewItems != null)
                {
                    foreach (ImportedVideoViewModel video in e.NewItems)
                    {
                        AttachImportedVideoHandlers(video);
                    }
                }

                if (e.OldItems != null)
                {
                    foreach (ImportedVideoViewModel video in e.OldItems)
                    {
                        DetachImportedVideoHandlers(video);
                    }
                }

                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnImportedVideosCollectionChanged failed: {ex}");
            }
        }

        // P/Invoke helpers to query physical display mode (to avoid DPI scaling artifacts)
        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;

            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            // remaining fields omitted
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        // Additional P/Invoke for reliable monitor enumeration
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint MONITORINFOF_PRIMARY = 1;





        /// <summary>
        /// Enumerates connected monitors and populates the monitor dropdown.
        /// Uses EnumDisplayMonitors for reliable physical pixel bounds, especially for wireless displays.
        /// </summary>
        private void EnumerateMonitors()
        {
            try
            {
                Debug.WriteLine("MainWindow: Enumerating monitors...");
                _monitors.Clear();
                _monitorItems.Clear();

                // Collect monitor info using EnumDisplayMonitors for accurate physical coordinates
                var monitorList = new List<(IntPtr hMonitor, RECT bounds, string deviceName, bool isPrimary)>();
                
                MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    try
                    {
                        var mi = new MONITORINFOEX();
                        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                        if (GetMonitorInfo(hMonitor, ref mi))
                        {
                            bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                            monitorList.Add((hMonitor, mi.rcMonitor, mi.szDevice, isPrimary));
                            Debug.WriteLine($"MainWindow: EnumDisplayMonitors found device '{mi.szDevice}' at ({mi.rcMonitor.Left},{mi.rcMonitor.Top}) size {mi.rcMonitor.Right - mi.rcMonitor.Left}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top} Primary={isPrimary}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: EnumDisplayMonitors callback failed: {ex}");
                    }
                    return true; // continue enumeration
                };

                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

                // Sort monitors: primary first, then by Left position
                monitorList = monitorList
                    .OrderByDescending(m => m.isPrimary)
                    .ThenBy(m => m.bounds.Left)
                    .ThenBy(m => m.bounds.Top)
                    .ToList();

                for (int i = 0; i < monitorList.Count; i++)
                {
                    var (hMonitor, bounds, deviceName, isPrimary) = monitorList[i];
                    
                    // EnumDisplayMonitors returns the virtual screen coordinates (DPI-scaled)
                    // These are used for window positioning with SetWindowPos
                    int virtualW = bounds.Right - bounds.Left;
                    int virtualH = bounds.Bottom - bounds.Top;
                    int virtualLeft = bounds.Left;
                    int virtualTop = bounds.Top;
                    
                    // Physical dimensions default to virtual (will be updated if DPI scaling detected)
                    int physicalW = virtualW;
                    int physicalH = virtualH;
                    int logicalW = virtualW;
                    int logicalH = virtualH;

                    try
                    {
                        // Query current display mode to get the actual physical resolution
                        var dm = new DEVMODE();
                        dm.dmDeviceName = new string('\0', 32);
                        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                        if (!string.IsNullOrEmpty(deviceName) && EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
                        {
                            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                            {
                                logicalW = dm.dmPelsWidth;
                                logicalH = dm.dmPelsHeight;

                                // For displays with DPI scaling:
                                // - EnumDisplayMonitors gives us virtual/scaled coordinates (e.g., 2880x1620 at 150% DPI)
                                // - EnumDisplaySettings gives us the logical resolution (e.g., 1920x1080)
                                // 
                                // If the display settings resolution differs from virtual bounds,
                                // it indicates DPI scaling is in effect. In this case:
                                // - Use virtual bounds for window positioning (SetWindowPos uses virtual coords)
                                // - Use LARGER of the two for rendering (the physical pixel count)
                                //
                                // When virtual > display settings: physical = virtual (150% scaling)
                                // When virtual == display settings: no scaling
                                if (virtualW > dm.dmPelsWidth || virtualH > dm.dmPelsHeight)
                                {
                                    // DPI scaling detected: virtual bounds are larger than display settings
                                    // The virtual bounds represent the physical pixel dimensions
                                    physicalW = virtualW;
                                    physicalH = virtualH;
                                    Debug.WriteLine($"MainWindow: DPI scaling detected for '{deviceName}': physical {physicalW}x{physicalH}, logical {dm.dmPelsWidth}x{dm.dmPelsHeight}");
                                }
                                else
                                {
                                    // No scaling or display settings >= virtual - use display settings as physical
                                    physicalW = dm.dmPelsWidth;
                                    physicalH = dm.dmPelsHeight;
                                }
                                Debug.WriteLine($"MainWindow: EnumDisplaySettings for '{deviceName}': display mode {dm.dmPelsWidth}x{dm.dmPelsHeight} at ({dm.dmPositionX},{dm.dmPositionY})");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: EnumDisplaySettings failed for {deviceName}: {ex}");
                    }

                    // Store both virtual (for positioning) and physical (for rendering) dimensions
                    _monitors.Add(new MonitorInfo(virtualW, virtualH, virtualLeft, virtualTop, physicalW, physicalH));

                    var isPrimaryStr = isPrimary ? " (Primary)" : "";
                    // Show the logical resolution (matches Windows Display Settings) with native res if DPI-scaled
                    string resolutionStr;
                    if (physicalW != logicalW || physicalH != logicalH)
                    {
                        resolutionStr = $"{logicalW}x{logicalH} (native {physicalW}x{physicalH})";
                    }
                    else
                    {
                        resolutionStr = $"{physicalW}x{physicalH}";
                    }
                    var monitorItem = new MonitorItem
                    {
                        Index = i,
                        Name = $"Display {i + 1}: {resolutionStr}{isPrimaryStr}"
                    };
                    _monitorItems.Add(monitorItem);

                    Debug.WriteLine($"MainWindow: Found monitor {i}: {monitorItem.Name} at ({virtualLeft}, {virtualTop}), physical: {physicalW}x{physicalH}");
                }

                // Populate PART_MeshMonitorCombo (for mesh layers)
                if (PART_MeshMonitorCombo != null)
                {
                    // Remember the current selection so we can restore it after repopulating
                    int previousSelectedIndex = PART_MeshMonitorCombo.SelectedIndex;

                    PART_MeshMonitorCombo.SelectionChanged -= OnMeshMonitorComboSelectionChanged;

                    PART_MeshMonitorCombo.Items.Clear();
                    PART_MeshMonitorCombo.Items.Add("(None)");
                    foreach (var item in _monitorItems)
                    {
                        PART_MeshMonitorCombo.Items.Add(item.Name);
                    }

                    // Restore previous selection if still valid, otherwise default to (None)
                    if (previousSelectedIndex >= 0 && previousSelectedIndex < PART_MeshMonitorCombo.Items.Count)
                    {
                        PART_MeshMonitorCombo.SelectedIndex = previousSelectedIndex;
                    }
                    else
                    {
                        PART_MeshMonitorCombo.SelectedIndex = 0;
                    }

                    // Wire up events (idempotent unsubscribe + subscribe)
                    PART_MeshMonitorCombo.SelectionChanged += OnMeshMonitorComboSelectionChanged;
                    PART_MeshMonitorCombo.DropDownOpened -= OnMeshMonitorComboDropDownOpened;
                    PART_MeshMonitorCombo.DropDownOpened += OnMeshMonitorComboDropDownOpened;
                }

                Debug.WriteLine($"MainWindow: Found {_monitorItems.Count} monitors");

                // Reconcile fullscreen output windows with the latest monitor inventory
                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: EnumerateMonitors failed: {ex}");
            }
        }

        private void OnMeshMonitorComboSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_vm.SelectedMeshLayer == null) return;

                var selectedIndex = PART_MeshMonitorCombo.SelectedIndex - 1; // -1 because "None" is at index 0

                // Use the ViewModel property to trigger PropertyChanged notification
                // This allows the overlay system to detect the change and update overlays accordingly
                _vm.SelectedMeshLayer.TargetMonitorIndex = selectedIndex;
                Debug.WriteLine($"MainWindow: Set target monitor to {selectedIndex} for mesh layer");

                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnMeshMonitorComboSelectionChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Re-enumerate monitors each time the dropdown is opened so newly connected displays appear.
        /// </summary>
        private void OnMeshMonitorComboDropDownOpened(object? sender, EventArgs e)
        {
            try
            {
                Debug.WriteLine("MainWindow: Monitor combo dropdown opened, refreshing monitor list");
                EnumerateMonitors();

                // If a mesh layer is selected, restore the combo selection to match its target monitor
                if (_vm.SelectedMeshLayer != null)
                {
                    var targetIdx = _vm.SelectedMeshLayer.TargetMonitorIndex;
                    var comboIdx = targetIdx + 1; // +1 because "(None)" is at index 0
                    if (comboIdx >= 0 && comboIdx < PART_MeshMonitorCombo.Items.Count)
                    {
                        PART_MeshMonitorCombo.SelectedIndex = comboIdx;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnMeshMonitorComboDropDownOpened failed: {ex}");
            }
        }

        private ProjectModel BuildProjectModel()
        {
            try
            {
                var project = new ProjectModel
                {
                    Name = _vm.ActiveProject?.Name ?? "Untitled Project",
                    ProjectVersion = 2,
                    PlaylistMode = _vm.IsPlaylistMode,
                    ShowMeshOverlay = true,
                    ShowCoordinateGrid = false,
                    InputZoom = _vm.InputZoom,
                    OutputZoom = _vm.OutputZoom
                };

                // Add imported videos
                foreach (var imported in _vm.ImportedVideos)
                {
                    var videoData = new ImportedVideoData
                    {
                        Id = imported.Id,
                        Name = imported.Name,
                        SourcePath = imported.SourcePath,
                        PlayAudio = imported.PlayAudio,
                        Visible = true
                    };

                    // Add mesh layers
                    foreach (var mesh in imported.MeshLayers)
                    {
                        var meshData = new MeshLayerData
                        {
                            Id = mesh.Model.Id ?? Guid.NewGuid().ToString("N"),
                            Name = mesh.Name ?? "Mesh",
                            SourceId = mesh.Model.SourceId ?? string.Empty,
                            X = mesh.X,
                            Y = mesh.Y,
                            Width = mesh.Width,
                            Height = mesh.Height,
                            Opacity = mesh.Opacity,
                            Visible = mesh.Visible,
                            // Access TargetMonitorIndex through the Model property
                            TargetMonitorIndex = mesh.Model.TargetMonitorIndex,
                            ShowOverlay = mesh.ShowOverlay
                        };

                        // Copy mesh points
                        var srcPoints = mesh.Model.MeshPoints;
                        var srcOutputPoints = mesh.Model.OutputMeshPoints;
                        meshData.MeshPoints = new float[8];
                        meshData.OutputMeshPoints = new float[8];

                        for (int i = 0; i < 4 && i < srcPoints.Length; i++)
                        {
                            meshData.MeshPoints[i * 2] = srcPoints[i].X;
                            meshData.MeshPoints[i * 2 + 1] = srcPoints[i].Y;
                        }

                        for (int i = 0; i < 4 && i < srcOutputPoints.Length; i++)
                        {
                            meshData.OutputMeshPoints[i * 2] = srcOutputPoints[i].X;
                            meshData.OutputMeshPoints[i * 2 + 1] = srcOutputPoints[i].Y;
                        }

                        videoData.MeshLayers.Add(meshData);
                    }

                    project.ImportedVideos.Add(videoData);
                }

                // Add playlist groups
                project.PlaylistGroups = _vm.BuildPlaylistGroupModels();

                return project;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: BuildProjectModel failed: {ex}");
                throw;
            }
        }

        private async Task ClearProjectState()
        {
            try
            {
                Debug.WriteLine("MainWindow: Clearing project state");

                // Stop playback - pass re-enable looping flag to restore legacy mode behavior
                await _playlistService.StopPlaylistAsync(reEnableLooping: true);
                await _videoService.StopAllAsync();

                // Clear imported videos (detach handlers first to avoid lingering subscriptions)
                foreach (var video in _vm.ImportedVideos.ToList())
                {
                    DetachImportedVideoHandlers(video);
                }
                _vm.ImportedVideos.Clear();

                // Clear playlist groups
                _vm.PlaylistGroups.Clear();

                // Reset playlist mode
                _vm.IsPlaylistMode = false;

                Debug.WriteLine("MainWindow: Project state cleared");

                // Ensure fullscreen outputs are closed when nothing targets them
                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: ClearProjectState failed: {ex}");
                throw;
            }
        }

        private async Task LoadProjectState(ProjectModel project)
        {
            try
            {
                Debug.WriteLine($"MainWindow: Loading project state for '{project.Name}'");

                // Set project properties
                _vm.IsPlaylistMode = project.PlaylistMode;
                _vm.InputZoom = project.InputZoom;
                _vm.OutputZoom = project.OutputZoom;

                // Load imported videos
                foreach (var videoData in project.ImportedVideos)
                {
                    var imported = new ImportedVideoViewModel(videoData.Id, videoData.Name, videoData.SourcePath);

                    // Create host layer
                    var hostLayer = new LayerModel
                    {
                        Id = videoData.Id,
                        Name = $"{videoData.Name} (Host)",
                        SourcePath = videoData.SourcePath,
                        Width = _rendererManager.OutputWidth,
                        Height = _rendererManager.OutputHeight,
                        Visible = videoData.Visible,
                        PlayAudio = videoData.PlayAudio,
                        PreviewOnly = videoData.MeshLayers.Count > 0
                    };

                    imported.HostLayer = hostLayer;
                    imported.NotifyHostLayerChanged();

                    // Load mesh layers
                    foreach (var meshData in videoData.MeshLayers)
                    {
                        var meshModel = new LayerModel
                        {
                            Id = meshData.Id,
                            Name = meshData.Name,
                            SourceId = meshData.SourceId,
                            X = meshData.X,
                            Y = meshData.Y,
                            Width = meshData.Width,
                            Height = meshData.Height,
                            Opacity = meshData.Opacity,
                            Visible = meshData.Visible,
                            TargetMonitorIndex = meshData.TargetMonitorIndex,
                            ShowOverlay = meshData.ShowOverlay
                        };

                        // Copy mesh points
                        for (int i = 0; i < 4 && (i * 2 + 1) < meshData.MeshPoints.Length; i++)
                        {
                            meshModel.MeshPoints[i] = new Vector2(
                                meshData.MeshPoints[i * 2],
                                meshData.MeshPoints[i * 2 + 1]);
                        }

                        for (int i = 0; i < 4 && (i * 2 + 1) < meshData.OutputMeshPoints.Length; i++)
                        {
                            meshModel.OutputMeshPoints[i] = new Vector2(
                                meshData.OutputMeshPoints[i * 2],
                                meshData.OutputMeshPoints[i * 2 + 1]);
                        }

                        var meshVm = new LayerViewModel(meshModel);
                        imported.MeshLayers.Add(meshVm);

                        await _videoService.RegisterMeshLayerAsync(meshModel);
                    }

                    _vm.ImportedVideos.Add(imported);
                    await _videoService.RegisterLayerAsync(hostLayer, playAudio: videoData.PlayAudio);
                }

                // Load playlist groups
                _vm.LoadPlaylistGroups(project.PlaylistGroups);
                _vm.UpdatePlaylistGroupVideos();

                // In playlist mode, looping is already disabled (set earlier in LoadProjectState)
                // Videos will play normally and stop at EOF, waiting for the playlist to advance
                // We don't need to pause them here - let them play so the user can see the preview
               

                Debug.WriteLine("MainWindow: Project state loaded successfully");

                 // Update fullscreen window assignments to reflect the freshly loaded project
                 EnsureMonitorWindows();
 
                // Automatically start playlist playback so only one group runs at a time
                await EnsurePlaylistAutostartAsync().ConfigureAwait(false);
             }
             catch (Exception ex)
             {
                 Debug.WriteLine($"MainWindow: LoadProjectState failed: {ex}");
                 throw;
             }
        }

        /// <summary>
        /// Ensures playlist playback starts automatically when playlist mode is active after loading a project.
        /// Adds a brief delay to allow decoders to fully initialize before starting group transitions.
        /// </summary>
        private async Task EnsurePlaylistAutostartAsync()
        {
            try
            {
                if (!_vm.IsPlaylistMode)
                {
                    return;
                }

                if (_vm.PlaylistGroups == null || _vm.PlaylistGroups.Count == 0)
                {
                    return;
                }

                var groups = _vm.BuildPlaylistGroupModels();
                if (groups == null || groups.Count == 0)
                {
                    return;
                }

                // CRITICAL FIX: Wait for all video decoders to be ready before starting playlist
                // This prevents race conditions where the playlist tries to start videos
                // before their decoders have finished initializing, which can cause crashes.
                var allSourceIds = groups.SelectMany(g => g.SourceIds).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                if (allSourceIds.Count > 0)
                {
                    Debug.WriteLine($"MainWindow: Waiting for {allSourceIds.Count} video decoders to initialize before starting playlist...");
                    var ready = await _videoService.WaitForDecodersReadyAsync(allSourceIds, timeoutMs: 15000).ConfigureAwait(false);
                    if (!ready)
                    {
                        Debug.WriteLine("MainWindow: Warning - Not all decoders ready after timeout, proceeding anyway");
                    }
                }

                Debug.WriteLine("MainWindow: Auto-starting playlist playback after project load");
                await _playlistService.StartPlaylistAsync(groups).ConfigureAwait(false);

                // Update VM on UI thread since we may be on a thread pool thread after ConfigureAwait(false)
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            _vm.SetPlaybackState(true);
                            _vm.StatusText = "Playing (Playlist Mode)";
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"MainWindow: EnsurePlaylistAutostartAsync VM update failed: {ex}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow: EnsurePlaylistAutostartAsync Dispatcher failed: {ex}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: EnsurePlaylistAutostartAsync failed: {ex}");
            }
        }

        #endregion

        // Event handlers required by XAML
        private void Window_Loaded(object sender, RoutedEventArgs e) { }
        
        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("MainWindow: Exit menu clicked");
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: ExitMenuItem_Click failed: {ex}");
                Application.Current.Shutdown();
            }
        }
        
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("MainWindow: About menu clicked");
                var aboutWindow = new AboutWindow
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                aboutWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: AboutMenuItem_Click failed: {ex}");
                MessageBox.Show($"Failed to open About dialog: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            try
            {
                var selectedItem = e.NewValue;
                Debug.WriteLine($"MainWindow: TreeView selection changed to: {selectedItem?.GetType().Name}");

                // Get current modifier keys for multi-selection
                var isCtrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                var isShiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

                // Handle mesh layer selection with multi-select support
                if (selectedItem is LayerViewModel meshVm)
                {
                    // Find the parent imported video that contains this mesh
                    var parentVideo = _vm.ImportedVideos.FirstOrDefault(v => v.MeshLayers.Contains(meshVm));
                    
                    if (isCtrlPressed)
                    {
                        // Ctrl+click: toggle this mesh in selection
                        _vm.ToggleMeshSelection(meshVm);
                        Debug.WriteLine($"MainWindow: Ctrl+click toggled mesh '{meshVm.Name}' (selection count: {_vm.SelectedMeshLayers.Count})");
                    }
                    else if (isShiftPressed && _vm.MeshSelectionAnchor != null && parentVideo != null)
                    {
                        // Shift+click: select range from anchor to this mesh
                        _vm.SelectMeshRange(_vm.MeshSelectionAnchor, meshVm, parentVideo);
                        Debug.WriteLine($"MainWindow: Shift+click selected range (selection count: {_vm.SelectedMeshLayers.Count})");
                    }
                    else
                    {
                        // Normal click: single selection, also set anchor for future Shift+clicks
                        _vm.SetSingleMeshSelection(meshVm);
                        _vm.MeshSelectionAnchor = meshVm;
                        Debug.WriteLine($"MainWindow: Selected mesh layer: {meshVm.Name} (parent: {parentVideo?.Name ?? "unknown"})");
                    }
                    
                    // Set parent video
                    if (parentVideo != null)
                    {
                        _vm.SelectedImportedVideo = parentVideo;
                    }
                    _vm.SelectedPlaylistGroup = null;
                    
                    // Sync PART_MeshMonitorCombo with the mesh layer's target monitor
                    var targetMonitor = meshVm.Model?.TargetMonitorIndex ?? -1;
                    var comboIndex = targetMonitor + 1;
                    if (comboIndex >= 0 && comboIndex < PART_MeshMonitorCombo.Items.Count)
                    {
                        PART_MeshMonitorCombo.SelectedIndex = comboIndex;
                    }
                    else
                    {
                        PART_MeshMonitorCombo.SelectedIndex = 0;
                    }
                }
                else
                {
                    // Not a mesh layer - clear multi-selection and handle normally
                    _vm.ClearMeshSelection();
                    _vm.MeshSelectionAnchor = null;
                    _vm.SelectedMeshLayer = null;
                    _vm.SelectedImportedVideo = null;
                    _vm.SelectedPlaylistGroup = null;

                    if (selectedItem is PlaylistGroupTreeViewModel groupTree)
                    {
                        // Find the corresponding PlaylistGroupViewModel
                        var groupVm = _vm.PlaylistGroups.FirstOrDefault(g => g.Model.Id == groupTree.Id);
                        if (groupVm != null)
                        {
                            _vm.SelectedPlaylistGroup = groupVm;
                            Debug.WriteLine($"MainWindow: Selected playlist group: {groupVm.Name}");
                        }
                        
                        // Reset mesh monitor combo when no mesh is selected
                        PART_MeshMonitorCombo.SelectedIndex = 0;
                    }
                    else if (selectedItem is ImportedVideoTreeViewModel videoTree)
                    {
                        // Set the selected imported video
                        var video = videoTree.ImportedVideo;
                        _vm.SelectedImportedVideo = video;
                        Debug.WriteLine($"MainWindow: Selected imported video: {video.Name}");
                        
                        // Reset mesh monitor combo when no mesh is selected
                        PART_MeshMonitorCombo.SelectedIndex = 0;
                    }
                    else
                    {
                        // Nothing selected - reset combo
                        PART_MeshMonitorCombo.SelectedIndex = 0;
                    }
                }

                // Update command states
                (_vm.CreateMeshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (_vm.DeleteMeshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (_vm.CopyMeshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (_vm.PasteMeshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: TreeView_SelectedItemChanged failed: {ex}");
            }
        }
        
        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Ensure the item under the mouse becomes selected so context menu commands operate on it
                var original = e.OriginalSource as DependencyObject;
                while (original != null && !(original is TreeViewItem))
                {
                    original = VisualTreeHelper.GetParent(original);
                }

                if (original is TreeViewItem item)
                {
                    item.IsSelected = true;
                    // Allow event to continue so context menu opens
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: TreeView_PreviewMouseRightButtonDown failed: {ex}");
            }
        }

        private void CreateMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Invoke the view-model command to create a mesh for the currently selected imported video
                if (_vm?.CreateMeshCommand != null && _vm.CreateMeshCommand.CanExecute(null))
                {
                    _vm.CreateMeshCommand.Execute(null);
                }
                else
                {
                    Debug.WriteLine("MainWindow: CreateMeshCommand cannot execute or is null");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: CreateMeshMenuItem_Click failed: {ex}");
            }
        }

        /// <summary>
        /// Handles clicking on a sliced mesh creation option.
        /// Creates multiple mesh layers that divide the source video into equal horizontal slices.
        /// </summary>
        private void CreateSlicedMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not MenuItem menuItem || menuItem.Tag is not string tagValue)
                {
                    Debug.WriteLine("MainWindow: CreateSlicedMeshMenuItem_Click - invalid sender or tag");
                    return;
                }

                if (!int.TryParse(tagValue, out int sliceCount) || sliceCount < 2)
                {
                    Debug.WriteLine($"MainWindow: CreateSlicedMeshMenuItem_Click - invalid slice count: {tagValue}");
                    return;
                }

                // Invoke the view-model command to create sliced meshes for the currently selected imported video
                if (_vm?.CreateSlicedMeshCommand != null && _vm.CreateSlicedMeshCommand.CanExecute(sliceCount))
                {
                    _vm.CreateSlicedMeshCommand.Execute(sliceCount);
                }
                else
                {
                    Debug.WriteLine("MainWindow: CreateSlicedMeshCommand cannot execute or is null");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: CreateSlicedMeshMenuItem_Click failed: {ex}");
            }
        }

        private void RenameGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_vm.SelectedPlaylistGroup == null)
                {
                    Debug.WriteLine("MainWindow: RenameGroupMenuItem_Click - no group selected");
                    return;
                }

                var currentName = _vm.SelectedPlaylistGroup.Name;
                
                // Create a simple input dialog using standard WPF dialogs
                var dialog = new Window
                {
                    Title = "Rename Group",
                    Width = 350,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize
                };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.Margin = new Thickness(16);

                var label = new TextBlock { Text = "Enter new group name:", Margin = new Thickness(0, 0, 0, 8) };
                Grid.SetRow(label, 0);
                grid.Children.Add(label);

                var textBox = new System.Windows.Controls.TextBox { Text = currentName, Margin = new Thickness(0, 0, 0, 16) };
                textBox.SelectAll();
                Grid.SetRow(textBox, 1);
                grid.Children.Add(textBox);

                var buttonPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                Grid.SetRow(buttonPanel, 2);
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var okButton = new System.Windows.Controls.Button { Content = "OK", Width = 75, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
                okButton.Click += (s, args) => { dialog.DialogResult = true; dialog.Close(); };
                buttonPanel.Children.Add(okButton);

                var cancelButton = new System.Windows.Controls.Button { Content = "Cancel", Width = 75, IsCancel = true };
                cancelButton.Click += (s, args) => { dialog.DialogResult = false; dialog.Close(); };
                buttonPanel.Children.Add(cancelButton);

                grid.Children.Add(buttonPanel);
                dialog.Content = grid;

                // Focus the text box when dialog opens
                dialog.Loaded += (s, args) => { textBox.Focus(); textBox.SelectAll(); };

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    var newName = textBox.Text.Trim();
                    if (newName != currentName)
                    {
                        // Update the ViewModel's name (this updates the Model as well)
                        _vm.SelectedPlaylistGroup.Name = newName;
                        
                        // Also update the underlying model directly to ensure sync
                        _vm.SelectedPlaylistGroup.Model.Name = newName;
                        
                        // Find and update the corresponding tree view item
                        var treeItem = _vm.ProjectTree
                            .OfType<PlaylistGroupTreeViewModel>()
                            .FirstOrDefault(g => g.Id == _vm.SelectedPlaylistGroup.Id);
                        
                        if (treeItem != null)
                        {
                            treeItem.Name = newName;
                            treeItem.RefreshDisplayText();
                        }
                        
                        _vm.MarkProjectDirty();
                        Debug.WriteLine($"MainWindow: Renamed group from '{currentName}' to '{newName}'");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RenameGroupMenuItem_Click failed: {ex}");
            }
        }

        private void DeleteGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_vm?.DeleteGroupCommand != null && _vm.DeleteGroupCommand.CanExecute(null))
                {
                    var groupName = _vm.SelectedPlaylistGroup?.Name ?? "(unknown)";
                    
                    // Confirm deletion
                    var result = MessageBox.Show(
                        $"Are you sure you want to delete the group '{groupName}'?\n\nThis action cannot be undone.",
                        "Delete Group",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        _vm.DeleteGroupCommand.Execute(null);
                        Debug.WriteLine($"MainWindow: Deleted group '{groupName}'");
                    }
                }
                else
                {
                    Debug.WriteLine("MainWindow: DeleteGroupCommand cannot execute or is null");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteGroupMenuItem_Click failed: {ex}");
            }
        }

        private void MoveGroupUpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_vm?.MoveGroupUpCommand != null && _vm.MoveGroupUpCommand.CanExecute(null))
                {
                    _vm.MoveGroupUpCommand.Execute(null);
                    Debug.WriteLine($"MainWindow: Moved group '{_vm.SelectedPlaylistGroup?.Name}' up");
                }
                else
                {
                    Debug.WriteLine("MainWindow: MoveGroupUpCommand cannot execute or is null (group may already be at top)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MoveGroupUpMenuItem_Click failed: {ex}");
            }
        }

        private void MoveGroupDownMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_vm?.MoveGroupDownCommand != null && _vm.MoveGroupDownCommand.CanExecute(null))
                {
                    _vm.MoveGroupDownCommand.Execute(null);
                    Debug.WriteLine($"MainWindow: Moved group '{_vm.SelectedPlaylistGroup?.Name}' down");
                }
                else
                {
                    Debug.WriteLine("MainWindow: MoveGroupDownCommand cannot execute or is null (group may already be at bottom)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MoveGroupDownMenuItem_Click failed: {ex}");
            }
        }

        private async void PlayGroupNowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_vm.SelectedPlaylistGroup == null)
                {
                    Debug.WriteLine("MainWindow: PlayGroupNowMenuItem_Click - no group selected");
                    return;
                }

                var groupIndex = _vm.PlaylistGroups.IndexOf(_vm.SelectedPlaylistGroup);
                if (groupIndex < 0)
                {
                    Debug.WriteLine("MainWindow: PlayGroupNowMenuItem_Click - selected group not found in collection");
                    return;
                }

                // Make sure playlist mode is enabled
                if (!_vm.IsPlaylistMode)
                {
                    _vm.IsPlaylistMode = true;
                    UpdateLoopingMode(true);
                }

                // Load groups into playlist service and start playing
                var groupModels = _vm.PlaylistGroups.Select(g => g.Model).ToList();
                await _playlistService.StartPlaylistAsync(groupModels);
                
                // Jump to the selected group if not already the first one
                if (groupIndex > 0)
                {
                    await _playlistService.JumpToGroupAsync(groupIndex);
                }
                
                _vm.SetPlaybackState(true);

                Debug.WriteLine($"MainWindow: Started playing group '{_vm.SelectedPlaylistGroup.Name}' (index {groupIndex})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlayGroupNowMenuItem_Click failed: {ex}");
            }
        }

        private void SequentialModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_vm.SelectedPlaylistGroup == null)
                {
                    Debug.WriteLine("MainWindow: SequentialModeMenuItem_Click - no group selected");
                    return;
                }

                _vm.SelectedPlaylistGroup.PlaybackMode = GroupPlaybackMode.Sequential;
                Debug.WriteLine($"MainWindow: Set group '{_vm.SelectedPlaylistGroup.Name}' to Sequential playback mode");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SequentialModeMenuItem_Click failed: {ex}");
            }
        }

        private void SimultaneousModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_vm.SelectedPlaylistGroup == null)
                {
                    Debug.WriteLine("MainWindow: SimultaneousModeMenuItem_Click - no group selected");
                    return;
                }

                _vm.SelectedPlaylistGroup.PlaybackMode = GroupPlaybackMode.Simultaneous;
                Debug.WriteLine($"MainWindow: Set group '{_vm.SelectedPlaylistGroup.Name}' to Simultaneous playback mode");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SimultaneousModeMenuItem_Click failed: {ex}");
            }
        }

        private void MeshContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("MainWindow: MeshContextMenu_Opened");
                
                if (sender is not ContextMenu contextMenu)
                {
                    return;
                }
                
                // Get selection count for updating menu item headers
                var selectedCount = _vm.SelectedMeshLayers.Count > 0 
                    ? _vm.SelectedMeshLayers.Count 
                    : (_vm.SelectedMeshLayer != null ? 1 : 0);
                
                // Update headers based on selection count
                foreach (var item in contextMenu.Items)
                {
                    if (item is MenuItem menuItem)
                    {
                        var header = menuItem.Header?.ToString() ?? "";
                        
                        if (header.StartsWith("Copy Mesh"))
                        {
                            menuItem.Header = selectedCount > 1 
                                ? $"Copy {selectedCount} Meshes" 
                                : "Copy Mesh";
                        }
                        else if (header.StartsWith("Delete Mesh"))
                        {
                            menuItem.Header = selectedCount > 1 
                                ? $"Delete {selectedCount} Mesh Layers" 
                                : "Delete Mesh Layer";
                        }
                    }
                }
                
                Debug.WriteLine($"MainWindow: Mesh context menu opened with {selectedCount} mesh(es) selected");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MeshContextMenu_Opened failed: {ex}");
            }
        }

        private void VideoContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("MainWindow: VideoContextMenu_Opened");
                
                if (sender is not ContextMenu contextMenu)
                {
                    return;
                }
                
                // Find the "Add to Group" menu item
                MenuItem? addToGroupItem = null;
                MenuItem? removeFromGroupItem = null;
                
                foreach (var item in contextMenu.Items)
                {
                    if (item is MenuItem menuItem)
                    {
                        if (menuItem.Header?.ToString() == "Add to Group")
                        {
                            addToGroupItem = menuItem;
                        }
                        else if (menuItem.Header?.ToString() == "Remove from Group")
                        {
                            removeFromGroupItem = menuItem;
                        }
                    }
                }
                
                // Get the selected video from the tree view
                var selectedVideoTree = PART_ProjectTree.SelectedItem as ImportedVideoTreeViewModel;
                var selectedVideo = selectedVideoTree?.ImportedVideo ?? _vm.SelectedImportedVideo;
                
                // Determine if the video is currently in a group
                bool isInGroup = false;
                PlaylistGroupViewModel? currentGroup = null;
                
                if (selectedVideo != null)
                {
                    foreach (var group in _vm.PlaylistGroups)
                    {
                        if (group.Model.SourceIds.Contains(selectedVideo.Id))
                        {
                            isInGroup = true;
                            currentGroup = group;
                            break;
                        }
                    }
                }
                
                // Configure "Remove from Group" visibility
                if (removeFromGroupItem != null)
                {
                    removeFromGroupItem.Visibility = isInGroup ? Visibility.Visible : Visibility.Collapsed;
                    if (isInGroup && currentGroup != null)
                    {
                        removeFromGroupItem.Header = $"Remove from '{currentGroup.Name}'";
                    }
                }
                
                // Populate "Add to Group" submenu with available groups
                if (addToGroupItem != null)
                {
                    addToGroupItem.Items.Clear();
                    
                    if (_vm.PlaylistGroups.Count == 0)
                    {
                        var noGroupsItem = new MenuItem { Header = "(No groups available)", IsEnabled = false };
                        addToGroupItem.Items.Add(noGroupsItem);
                    }
                    else
                    {
                        foreach (var group in _vm.PlaylistGroups.OrderBy(g => g.Order))
                        {
                            // Skip the current group if the video is already in it
                            bool alreadyInThisGroup = group.Model.SourceIds.Contains(selectedVideo?.Id ?? string.Empty);
                            
                            var groupMenuItem = new MenuItem
                            {
                                Header = alreadyInThisGroup ? $"{group.Name} (already added)" : group.Name,
                                Tag = group,
                                IsEnabled = !alreadyInThisGroup
                            };
                            
                            groupMenuItem.Click += AddVideoToGroupSubmenuItem_Click;
                            addToGroupItem.Items.Add(groupMenuItem);
                        }
                        
                        // Add separator and "New Group..." option
                        addToGroupItem.Items.Add(new Separator());
                        var newGroupItem = new MenuItem { Header = "New Group..." };
                        newGroupItem.Click += AddVideoToNewGroup_Click;
                        addToGroupItem.Items.Add(newGroupItem);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoContextMenu_Opened failed: {ex}");
            }
        }
        
        private void AddVideoToGroupSubmenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not MenuItem menuItem || menuItem.Tag is not PlaylistGroupViewModel targetGroup)
                {
                    Debug.WriteLine("MainWindow: AddVideoToGroupSubmenuItem_Click - invalid sender or tag");
                    return;
                }
                
                // Get the selected video
                var selectedVideoTree = PART_ProjectTree.SelectedItem as ImportedVideoTreeViewModel;
                var selectedVideo = selectedVideoTree?.ImportedVideo ?? _vm.SelectedImportedVideo;
                
                if (selectedVideo == null)
                {
                    Debug.WriteLine("MainWindow: AddVideoToGroupSubmenuItem_Click - no video selected");
                    return;
                }
                
                // Remove from any existing group first (video can only be in one group)
                RemoveVideoFromAllGroups(selectedVideo);
                
                // Add to the target group
                if (!targetGroup.Model.SourceIds.Contains(selectedVideo.Id))
                {
                    targetGroup.Model.SourceIds.Add(selectedVideo.Id);
                    targetGroup.Videos.Add(selectedVideo);
                    targetGroup.RefreshVideoCount();
                }
                
                _vm.MarkProjectDirty();
                _vm.RefreshProjectTree();
                Debug.WriteLine($"MainWindow: Added video '{selectedVideo.Name}' to group '{targetGroup.Name}'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AddVideoToGroupSubmenuItem_Click failed: {ex}");
            }
        }
        
        private void AddVideoToNewGroup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get the selected video
                var selectedVideoTree = PART_ProjectTree.SelectedItem as ImportedVideoTreeViewModel;
                var selectedVideo = selectedVideoTree?.ImportedVideo ?? _vm.SelectedImportedVideo;
                
                if (selectedVideo == null)
                {
                    Debug.WriteLine("MainWindow: AddVideoToNewGroup_Click - no video selected");
                    return;
                }
                
                // Remove from any existing group first (video can only be in one group)
                RemoveVideoFromAllGroups(selectedVideo);
                
                // Create a new group
                if (_vm.CreateGroupCommand.CanExecute(null))
                {
                    _vm.CreateGroupCommand.Execute(null);
                }
                
                // Add the video to the newly created group (should be the selected one now)
                if (_vm.SelectedPlaylistGroup != null)
                {
                    if (!_vm.SelectedPlaylistGroup.Model.SourceIds.Contains(selectedVideo.Id))
                    {
                        _vm.SelectedPlaylistGroup.Model.SourceIds.Add(selectedVideo.Id);
                        _vm.SelectedPlaylistGroup.Videos.Add(selectedVideo);
                        _vm.SelectedPlaylistGroup.RefreshVideoCount();
                    }
                    
                    _vm.MarkProjectDirty();
                    _vm.RefreshProjectTree();
                    Debug.WriteLine($"MainWindow: Created new group '{_vm.SelectedPlaylistGroup.Name}' and added video '{selectedVideo.Name}'");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AddVideoToNewGroup_Click failed: {ex}");
            }
        }
        
        /// <summary>
        /// Helper method to remove a video from all playlist groups.
        /// Videos can only be in one group at a time.
        /// </summary>
        private void RemoveVideoFromAllGroups(ImportedVideoViewModel video)
        {
            if (video == null) return;
            
            foreach (var group in _vm.PlaylistGroups)
            {
                if (group.Model.SourceIds.Contains(video.Id))
                {
                    group.Model.SourceIds.Remove(video.Id);
                    group.Videos.Remove(video);
                    group.RefreshVideoCount();
                    Debug.WriteLine($"MainWindow: Removed video '{video.Name}' from group '{group.Name}'");
                }
            }
        }

        private void DeleteSourceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: DeleteSourceMenuItem_Click");
                DeleteSelectedVideo();
            }
            catch (Exception ex) { Debug.WriteLine($"DeleteSourceMenuItem_Click failed: {ex}"); }
        }

        private void RemoveVideoFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: RemoveVideoFromGroupMenuItem_Click");
                
                // Get the selected video from the tree view (more reliable than _vm.SelectedImportedVideo)
                var selectedVideoTree = PART_ProjectTree.SelectedItem as ImportedVideoTreeViewModel;
                var video = selectedVideoTree?.ImportedVideo ?? _vm.SelectedImportedVideo;
                
                if (video == null)
                {
                    Debug.WriteLine("MainWindow: RemoveVideoFromGroupMenuItem_Click - no video selected");
                    return;
                }
                
                // Remove the video from all groups (should only be in one, but check all to be safe)
                RemoveVideoFromAllGroups(video);
                
                _vm.MarkProjectDirty();
                _vm.RefreshProjectTree();
                Debug.WriteLine($"MainWindow: Removed '{video.Name}' from all groups");
            }
            catch (Exception ex) { Debug.WriteLine($"RemoveVideoFromGroupMenuItem_Click failed: {ex}"); }
        }

        private void CopyMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                var selectedCount = _vm.SelectedMeshLayers.Count > 0 ? _vm.SelectedMeshLayers.Count : (_vm.SelectedMeshLayer != null ? 1 : 0);
                Debug.WriteLine($"MainWindow: CopyMeshMenuItem_Click ({selectedCount} mesh(es) selected)");
                if (_vm.CopyMeshCommand?.CanExecute(null) == true)
                {
                    _vm.CopyMeshCommand.Execute(null);
                    Debug.WriteLine($"MainWindow: {selectedCount} mesh(es) copied successfully");
                }
                else
                {
                    Debug.WriteLine("MainWindow: CopyMeshCommand cannot execute (no mesh selected?)");
                }
            }
            catch (Exception ex) { Debug.WriteLine($"CopyMeshMenuItem_Click failed: {ex}"); }
        }

        private void PasteMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: PasteMeshMenuItem_Click");
                if (_vm.PasteMeshCommand?.CanExecute(null) == true)
                {
                    _vm.PasteMeshCommand.Execute(null);
                    Debug.WriteLine("MainWindow: Mesh(es) pasted successfully");
                    _vm.MarkProjectDirty();
                }
                else
                {
                    Debug.WriteLine("MainWindow: PasteMeshCommand cannot execute (no video selected or no mesh copied?)");
                }
            }
            catch (Exception ex) { Debug.WriteLine($"PasteMeshMenuItem_Click failed: {ex}"); }
        }

        private void HideSourceOutputMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: HideSourceOutputMenuItem_Click");
                if (_vm.SelectedImportedVideo?.HostLayer != null)
                {
                    _vm.SelectedImportedVideo.HostLayer.PreviewOnly = !_vm.SelectedImportedVideo.HostLayer.PreviewOnly;
                    Debug.WriteLine($"MainWindow: Source output visibility toggled to PreviewOnly={_vm.SelectedImportedVideo.HostLayer.PreviewOnly}");
                    _vm.MarkProjectDirty();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"HideSourceOutputMenuItem_Click failed: {ex}"); }
        }

        private void RenameMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: RenameMeshMenuItem_Click");
                if (_vm.SelectedMeshLayer == null)
                {
                    Debug.WriteLine("MainWindow: No mesh layer selected");
                    return;
                }

                var currentName = _vm.SelectedMeshLayer.Name;
                
                // Create a simple input dialog
                var dialog = new Window
                {
                    Title = "Rename Mesh Layer",
                    Width = 350,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize
                };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.Margin = new Thickness(16);

                var label = new TextBlock { Text = "Enter new mesh layer name:", Margin = new Thickness(0, 0, 0, 8) };
                Grid.SetRow(label, 0);
                grid.Children.Add(label);

                var textBox = new System.Windows.Controls.TextBox { Text = currentName, Margin = new Thickness(0, 0, 0, 16) };
                textBox.SelectAll();
                Grid.SetRow(textBox, 1);
                grid.Children.Add(textBox);

                var buttonPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                Grid.SetRow(buttonPanel, 2);

                var okButton = new System.Windows.Controls.Button { Content = "OK", Width = 75, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
                okButton.Click += (s, args) => { dialog.DialogResult = true; dialog.Close(); };
                buttonPanel.Children.Add(okButton);

                var cancelButton = new System.Windows.Controls.Button { Content = "Cancel", Width = 75, IsCancel = true };
                cancelButton.Click += (s, args) => { dialog.DialogResult = false; dialog.Close(); };
                buttonPanel.Children.Add(cancelButton);

                grid.Children.Add(buttonPanel);
                dialog.Content = grid;

                dialog.Loaded += (s, args) => { textBox.Focus(); textBox.SelectAll(); };

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    var newName = textBox.Text.Trim();
                    if (newName != currentName)
                    {
                        _vm.SelectedMeshLayer.Name = newName;
                        _vm.MarkProjectDirty();
                        Debug.WriteLine($"MainWindow: Renamed mesh layer from '{currentName}' to '{newName}'");
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"RenameMeshMenuItem_Click failed: {ex}"); }
        }

        private void DeleteMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: DeleteMeshMenuItem_Click");
                DeleteSelectedMeshLayer();
            }
            catch (Exception ex) { Debug.WriteLine($"DeleteMeshMenuItem_Click failed: {ex}"); }
        }

        private void DeleteSelectedVideo()
        {
            try
            {
                if (_vm.SelectedImportedVideo == null) return;

                var videoToDelete = _vm.SelectedImportedVideo;

                // Stop the video first
                _videoService.UnregisterLayerAsync(videoToDelete.Id).GetAwaiter().GetResult();

                // Remove from collection
                _vm.ImportedVideos.Remove(videoToDelete);

                Debug.WriteLine($"MainWindow: Deleted video '{videoToDelete.Name}'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: DeleteSelectedVideo failed: {ex}");
            }
        }

        private void DeleteSelectedMeshLayer()
        {
            try
            {
                // Use the ViewModel's delete command which supports multi-selection
                if (_vm.DeleteMeshCommand?.CanExecute(null) == true)
                {
                    var count = _vm.SelectedMeshLayers.Count > 0 
                        ? _vm.SelectedMeshLayers.Count 
                        : (_vm.SelectedMeshLayer != null ? 1 : 0);
                    
                    _vm.DeleteMeshCommand.Execute(null);
                    _vm.MarkProjectDirty();
                    
                    Debug.WriteLine($"MainWindow: Deleted {count} mesh layer(s)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: DeleteSelectedMeshLayer failed: {ex}");
            }
        }

        private void DeleteVideo_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedVideo();
        }

        private void DeleteMeshLayer_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedMeshLayer();
        }
    }
}
