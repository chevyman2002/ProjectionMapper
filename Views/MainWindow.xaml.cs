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

        private class MonitorItem
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public override string ToString() => Name;
        }

        private void PART_MonitorCombo_DropDownOpened(object? sender, EventArgs e)
        {
            try
            {
                RefreshMonitors();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PART_MonitorCombo_DropDownOpened: failed to refresh monitors: {ex}");
            }
        }

        private void RefreshMonitors()
        {
            try
            {
                // remember current selection so we can restore if possible
                int prevSelected = -1;
                try { prevSelected = PART_MonitorCombo != null ? PART_MonitorCombo.SelectedIndex : -1; } catch { prevSelected = -1; }

                var newMonitors = EnumerateMonitors();

                // update internal list
                _monitors = newMonitors;

                // rebuild ObservableCollection of items on UI thread
                Dispatcher.Invoke(() =>
                {
                    _monitorItems.Clear();
                    for (int i = 0; i < _monitors.Count; i++)
                    {
                        var m = _monitors[i];
                        _monitorItems.Add(new MonitorItem { Index = i, Name = $"Display {i + 1} ({m.Width}x{m.Height})" });
                    }

                    // try to restore previous selection if still valid
                    if (PART_MonitorCombo != null)
                    {
                        if (prevSelected >= 0 && prevSelected < _monitorItems.Count)
                        {
                            PART_MonitorCombo.SelectedIndex = prevSelected;
                        }
                        else
                        {
                            PART_MonitorCombo.SelectedIndex = -1;
                        }
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshMonitors: failed: {ex}");
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            _vm = new MainWindowViewModel();
            DataContext = _vm;

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
            _playlistService.GroupChanged += OnPlaylistGroupChanged;
            _playlistService.PlaylistCompleted += OnPlaylistCompleted;

            // Expose VideoService so MeshEditorControl can subscribe for isolated previews
            this.Resources["VideoService"] = _videoService;

            // Wire ViewModel events
            _vm.ImportRequested += async () => await HandleImportAsync();
            _vm.PreviewRequested += () => HandlePreview();
            _vm.DeleteImportedRequested += async imported => await HandleDeleteImportedAsync(imported);
            _vm.SaveProjectRequested += async () => await HandleSaveProjectAsync();
            _vm.SaveAsProjectRequested += async () => await HandleSaveAsProjectAsync();
            _vm.LoadProjectRequested += async () => await HandleLoadProjectAsync();
            _vm.NewProjectRequested += async () => await HandleNewProjectAsync();

            // Update window title when HasUnsavedChanges property changes
            _vm.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.HasUnsavedChanges))
                {
                    UpdateWindowTitle();
                }
            };

            // Wire playback controls to VideoService
            _vm.PlayPauseRequestedAsync += async () =>
            {
                try
                {
                    // Check if in playlist mode
                    if (_vm.IsPlaylistMode && _vm.PlaylistGroups.Count > 0)
                    {
                        // Playlist mode: use PlaylistService for group-based playback
                        if (_vm.IsPlaying)
                        {
                            if (_playlistService.IsPaused)
                            {
                                Debug.WriteLine("PlaylistService: Resuming current group");
                                await _playlistService.ResumeCurrentGroupAsync();
                            }
                            else
                            {
                                Debug.WriteLine("PlaylistService: Starting playlist");
                                var groups = _vm.BuildPlaylistGroupModels();
                                await _playlistService.StartPlaylistAsync(groups);
                            }
                        }
                        else
                        {
                            Debug.WriteLine("PlaylistService: Pausing current group");
                            await _playlistService.PauseCurrentGroupAsync();
                        }
                    }
                    else
                    {
                        // Legacy mode: use VideoService for independent playback
                        if (_vm.IsPlaying)
                        {
                            Debug.WriteLine("VideoService: Resuming all layers");
                            await _videoService.ResumeAllAsync();
                            Debug.WriteLine("VideoService: Resume completed");
                        }
                        else
                        {
                            Debug.WriteLine("VideoService: Pausing all layers");
                            await _videoService.PauseAllAsync();
                            Debug.WriteLine("VideoService: Pause completed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PlayPause operation failed: {ex}");
                }
            };

            _vm.RestartRequestedAsync += async () =>
            {
                try
                {
                    if (_vm.IsPlaylistMode && _vm.PlaylistGroups.Count > 0)
                    {
                        Debug.WriteLine("PlaylistService: Restarting playlist");
                        await _playlistService.RestartPlaylistAsync();
                    }
                    else
                    {
                        Debug.WriteLine("VideoService: Restarting all layers");
                        await _videoService.RestartAllAsync();
                        Debug.WriteLine("VideoService: Restart completed");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Restart operation failed: {ex}");
                }
            };

            // populate monitor list for UI using Win32 EnumDisplayMonitors and store monitor info
            try
            {
                _monitors = EnumerateMonitors();
                for (int i = 0; i < _monitors.Count; i++)
                {
                    var m = _monitors[i];
                    _monitorItems.Add(new MonitorItem { Index = i, Name = $"Display {i + 1} ({m.Width}x{m.Height})" });
                }
            }
            catch { }

            // assign itemsource for combo (ComboBox defined in XAML with x:Name PART_MonitorCombo)
            Loaded += (_, __) =>
            {
                if (PART_MonitorCombo != null)
                {
                    PART_MonitorCombo.ItemsSource = _monitorItems;
                    PART_MonitorCombo.SelectionChanged += PART_MonitorCombo_SelectionChanged;
                    // Re-scan displays each time the drop-down is opened so available monitors are up-to-date
                    try { PART_MonitorCombo.DropDownOpened += PART_MonitorCombo_DropDownOpened; } catch { }
                }
                // We prefer a single monitor combo for selecting the output monitor for the selected source.
                // Hide the per-mesh combo to avoid duplicate controls; the PART_MonitorCombo will be used to
                // assign the host and all its mesh layers to the chosen monitor.
                try { if (PART_MeshMonitorCombo != null) PART_MeshMonitorCombo.Visibility = Visibility.Collapsed; } catch { }

                // Wire up audio control event handlers
                HookAudioControls();

                // Wire mesh overlay checkbox
                try
                {
                    if (PART_ShowMeshOverlayCheckbox != null)
                    {
                        PART_ShowMeshOverlayCheckbox.Checked += (s, e) => { _rendererManager.ShowMeshOverlay = true; };
                        PART_ShowMeshOverlayCheckbox.Unchecked += (s, e) => { _rendererManager.ShowMeshOverlay = false; _rendererManager.ClearAllOverlays(); };
                        // default checked
                        PART_ShowMeshOverlayCheckbox.IsChecked = _rendererManager.ShowMeshOverlay;
                    }
                    if (PART_GlobalShowMeshOverlayCheckbox != null)
                    {
                        PART_GlobalShowMeshOverlayCheckbox.Checked += (s, e) => { _rendererManager.ShowMeshOverlay = true; };
                        PART_GlobalShowMeshOverlayCheckbox.Unchecked += (s, e) => { _rendererManager.ShowMeshOverlay = false; _rendererManager.ClearAllOverlays(); };
                        PART_GlobalShowMeshOverlayCheckbox.IsChecked = _rendererManager.ShowMeshOverlay;
                    }
                    // Show grid checkbox - call SetCoordinateGrid on output host when checked
                    if (PART_ShowGridCheckbox != null)
                    {
                        PART_ShowGridCheckbox.Checked += (s, e) =>
                        {
                            try
                            {
                                // display grid every 100 renderer pixels by default
                                PART_OutputHost?.SetCoordinateGrid(100);
                            }
                            catch { }
                        };
                        PART_ShowGridCheckbox.Unchecked += (s, e) =>
                        {
                            try { PART_OutputHost?.ClearGridOverlay(); } catch { }
                        };
                        PART_ShowGridCheckbox.IsChecked = false;
                    }
                }
                catch { }
            };

            // Start renderer (use output host size; fallback to 1280x720)
            Loaded += async (_, __) =>
            {
                var hostForSize = PART_OutputHost ?? PART_InputHost;
                var w = (int)Math.Max(1, hostForSize.ActualWidth);
                var h = (int)Math.Max(1, hostForSize.ActualHeight);
                if (w == 0 || h == 0) { w = 1280; h = 720; }
                await _rendererManager.StartAsync(w, h);
            };

            // Handle window closing properly without blocking the UI thread
            Closing += MainWindow_Closing;

            // Hook mesh layer created event so we can register with VideoService
            _vm.MeshLayerCreated += async mesh =>
            {
                if (mesh != null && !string.IsNullOrEmpty(mesh.Id))
                {
                    try
                    {
                        // If the user has selected a monitor for the imported video, ensure the new mesh
                        // inherits that target so overlays and compositor mapping apply to the same output host.
                        var imported = _vm.SelectedImportedVideo;
                        if (imported?.HostLayer != null)
                        {
                            mesh.TargetMonitorIndex = imported.HostLayer.TargetMonitorIndex;
                        }
                    }
                    catch { }

                    await _videoService.RegisterMeshLayerAsync(mesh);

                    // Also update all mesh overlays for the imported video
                    UpdateAllMeshOverlaysForImportedVideo(_vm.SelectedImportedVideo);
                }
            };
        }

        private void HookAudioControls()
        {
            try
            {
                // Wire PlayAudio checkbox
                if (PART_PlayAudioCheckbox != null)
                {
                    PART_PlayAudioCheckbox.Checked += PlayAudioCheckbox_CheckedChanged;
                    PART_PlayAudioCheckbox.Unchecked += PlayAudioCheckbox_CheckedChanged;
                }

                // Volume slider removed - volume is now fixed at 100%
      // Mute checkbox removed - audio is off by default until "Play Audio" is checked
            }
     catch { }
        }

        private void PlayAudioCheckbox_CheckedChanged(object sender, RoutedEventArgs e)
        {
try
{
          var imported = _vm.SelectedImportedVideo;
  if (imported?.HostLayer == null) return;

        var playAudio = imported.PlayAudio; // This reads from the bound property

       if (playAudio)
     {
        // Start audio for host layer without re-registering the decoder (prevents duplicate decoders)
         if (!string.IsNullOrEmpty(imported.HostLayer.Id))
       {
      _videoService.StartAudioForLayer(imported.HostLayer.Id);
         }
     }
    else
      {
       // Stop audio for host layer
      if (!string.IsNullOrEmpty(imported.HostLayer.Id))
     {
     _videoService.StopAudioForLayer(imported.HostLayer.Id);
           }
        }
  }
      catch { }
  }

        private void PART_MeshMonitorCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo == null) return;
            var item = combo.SelectedItem as MonitorItem;
            var meshVm = _vm.SelectedMeshLayer;
            if (meshVm == null) return;

            // set target monitor on underlying model
            if (meshVm.Model != null)
            {
                meshVm.Model.TargetMonitorIndex = item != null ? item.Index : -1;

                // show fullscreen for this mesh if assigned
                if (meshVm.Model.TargetMonitorIndex >= 0 && meshVm.Model.TargetMonitorIndex < _monitors.Count)
                {
                    CreateOrShowFullScreenForMonitor(meshVm.Model.TargetMonitorIndex);
                }
            }
        }

        private void CreateOrShowFullScreenForMonitor(int monitorIndex)
        {
            if (monitorIndex < 0 || monitorIndex >= _monitors.Count) return;
            var mon = _monitors[monitorIndex];

            // create fullscreen window and position it using monitor bounds converted to DIPs
            var win = new FullScreenOutputWindow
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true
            };

            // Convert monitor pixel boundaries to WPF DIPs
            var source = PresentationSource.FromVisual(this);
            double dpiX = 1.0, dpiY = 1.0;
            if (source != null && source.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformFromDevice.M11;
                dpiY = source.CompositionTarget.TransformFromDevice.M22;
            }

            win.Left = mon.Left * dpiX;
            win.Top = mon.Top * dpiY;
            win.Width = mon.Width * dpiX;
            win.Height = mon.Height * dpiY;

            win.WindowStartupLocation = WindowStartupLocation.Manual;

            // Ensure native handle exists so we can position the window before showing it
            try
            {
                var helper = new WindowInteropHelper(win);
                var hwnd = helper.EnsureHandle();

                const uint SWP_SHOWWINDOW = 0x0040;
                var flags = SWP_SHOWWINDOW;

                Debug.WriteLine($"CreateOrShowFullScreenForMonitor: calling SetWindowPos for monitor {monitorIndex} -> rect {mon.Left},{mon.Top} {mon.Width}x{mon.Height}");
                bool ok = SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, flags);
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Debug.WriteLine($"CreateOrShowFullScreenForMonitor: SetWindowPos returned {ok}, GetLastError={err}");

                if (!ok)
                {
                    try
                    {
                        const int GWL_STYLE = -16;
                        const int WS_POPUP = unchecked((int)0x80000000);
                        var prev = GetWindowLong(hwnd, GWL_STYLE);
                        SetWindowLong(hwnd, GWL_STYLE, prev | WS_POPUP);
                        ok = SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, flags);
                        err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        Debug.WriteLine($"CreateOrShowFullScreenForMonitor: retry SetWindowPos returned {ok}, GetLastError={err}");
                    }
                    catch (Exception ex) { Debug.WriteLine($"CreateOrShowFullScreenForMonitor: retry SetWindowPos exception: {ex}"); }
                }

                // Show the window after native positioning
                win.Show();

                try { _rendererManager.ShowFullScreenWindow(monitorIndex, win); Debug.WriteLine($"CreateOrShowFullScreenForMonitor: ShowFullScreenWindow success for monitor {monitorIndex}"); } catch (Exception ex) { Debug.WriteLine($"CreateOrShowFullScreenForMonitor: ShowFullScreenWindow failed: {ex}"); }
                try { _rendererManager.AttachHost(monitorIndex, win); Debug.WriteLine($"CreateOrShowFullScreenForMonitor: AttachHost success for monitor {monitorIndex}"); } catch (Exception ex) { Debug.WriteLine($"CreateOrShowFullScreenForMonitor: AttachHost failed: {ex}"); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CreateOrShowFullScreenForMonitor: placement/show failed: {ex}");
                try { win.Show(); _rendererManager.ShowFullScreenWindow(monitorIndex, win); _rendererManager.AttachHost(monitorIndex, win); } catch (Exception ex2) { Debug.WriteLine($"CreateOrShowFullScreenForMonitor: fallback show failed: {ex2}"); }
            }
        }

        private void PART_MonitorCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo == null) return;
            var item = combo.SelectedItem as MonitorItem;
            var imported = _vm.SelectedImportedVideo;
            if (imported?.HostLayer != null && item != null)
            {
                // Apply selection to host layer
                imported.HostLayer.TargetMonitorIndex = item.Index;

                // Apply same monitor to all mesh layers of this imported video so they collectively display on the chosen monitor
                foreach (var meshVm in imported.MeshLayers)
                {
                    try { if (meshVm.Model != null) meshVm.Model.TargetMonitorIndex = item.Index; } catch { }
                }

                // Create or show fullscreen window on the selected monitor
                if (item.Index >= 0 && item.Index < _monitors.Count)
                {
                    _rendererManager.HideFullScreenWindow(item.Index);
                    CreateOrShowFullScreenForMonitor(item.Index);
                }
                else
                {
                    // hide any existing fullscreen for this index
                    _rendererManager.HideFullScreenWindow(item.Index);
                }

                // Update all mesh overlays for the new monitor
                UpdateAllMeshOverlaysForImportedVideo(imported);
            }
        }

        private async Task HandleImportAsync()
        {
            try
            {
                // Show open file dialog - only single file in this simple UI
                var path = await _fileDialog.ShowOpenFileDialogAsync("Import video", "Video files|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm|All files|*.*");
                if (string.IsNullOrEmpty(path)) return;
                if (!File.Exists(path)) return;

                // Create an ImportedVideoViewModel (parent node)
                var id = Guid.NewGuid().ToString("N");
                var importedVm = new ImportedVideoViewModel(id, Path.GetFileName(path), path);

                // Create a host decoding layer so the video is available for isolated preview
                var hostLayer = new LayerModel
                {
                    Id = id,
                    Name = importedVm.Name,
                    SourcePath = path,
                    // play immediately for isolated preview only; do not submit frames to main renderer until mesh created
                    PreviewOnly = true
                };

                // Determine default decode size from input host if available
                var hostForLayer = PART_InputHost ?? PART_OutputHost;
                var w = (int)Math.Max(1, hostForLayer.ActualWidth);
                var h = (int)Math.Max(1, hostForLayer.ActualHeight);
                if (w == 0 || h == 0) { w = 1280; h = 720; }
                hostLayer.X = 0; hostLayer.Y = 0; hostLayer.Width = w; hostLayer.Height = h;

                // Register decoder for the imported video so MeshEditor can show isolated preview and so output is submitted to renderer
                await _videoService.RegisterLayerAsync(hostLayer);
                importedVm.HostLayer = hostLayer;

                // Notify the ImportedVideoViewModel that HostLayer changed so bindings update
                importedVm.NotifyHostLayerChanged();

                // Add to view model collection (parent node). Do NOT create mesh layer automatically.
                // Ensure we marshal back to UI thread
                await Dispatcher.InvokeAsync(() =>
                {
                    _vm.ImportedVideos.Add(importedVm);
                    // Select the imported video in the VM
                    _vm.SelectedImportedVideo = importedVm;

                    // update monitor combo selection if available
                    if (PART_MonitorCombo != null && importedVm.HostLayer != null)
                    {
                        PART_MonitorCombo.SelectedIndex = importedVm.HostLayer.TargetMonitorIndex >= 0 ? importedVm.HostLayer.TargetMonitorIndex : -1;
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Failed to import video: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private async Task HandleDeleteImportedAsync(ImportedVideoViewModel? imported)
        {
            if (imported == null) return;

            var res = MessageBox.Show(this, $"Delete imported source '{imported.Name}' and all its mesh layers?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            try
            {
                // Unregister any host decoder
                if (imported.HostLayer != null && !string.IsNullOrEmpty(imported.HostLayer.Id))
                {
                    await _videoService.UnregisterLayerAsync(imported.HostLayer.Id);
                }
            }
            catch { }

            // Remove all nested mesh layers
            try
            {
                imported.MeshLayers.Clear();
            }
            catch { }

            // Remove from VM collection
            await Dispatcher.InvokeAsync(() =>
            {
                _vm.ImportedVideos.Remove(imported);

                // Clear selection if it was the deleted one
                if (_vm.SelectedImportedVideo == imported) _vm.SelectedImportedVideo = null;
                if (_vm.SelectedMeshLayer != null && imported.MeshLayers.Contains(_vm.SelectedMeshLayer)) _vm.SelectedMeshLayer = null;
            });
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // If a mesh LayerViewModel is selected, set SelectedMeshLayer on VM
            if (e.NewValue is LayerViewModel layerVm)
            {
                _vm.SelectedMeshLayer = layerVm;
                // reflect current mesh monitor selection in UI
                if (PART_MeshMonitorCombo != null && layerVm.Model != null)
                {
                    PART_MeshMonitorCombo.SelectedIndex = layerVm.Model.TargetMonitorIndex >= 0 ? layerVm.Model.TargetMonitorIndex : -1;
                }
                
                // Update all mesh overlays for the target monitor
                UpdateAllMeshOverlaysForImportedVideo(_vm.SelectedImportedVideo);
                return;
            }

            // If an imported video (parent) is selected, set SelectedImportedVideo
            if (e.NewValue is ImportedVideoViewModel imported)
            {
                _vm.SelectedImportedVideo = imported;
                // Optionally select nothing for mesh layer
                _vm.SelectedMeshLayer = null;

                // update monitor combo selection
                if (PART_MonitorCombo != null && imported.HostLayer != null)
                {
                    PART_MonitorCombo.SelectedIndex = imported.HostLayer.TargetMonitorIndex >= 0 ? imported.HostLayer.TargetMonitorIndex : -1;
                }

                // Ensure PlayAudio checkbox reflects state; it's bound in XAML so this is just to ensure service state
                try
                {
                    // If PlayAudio is true and no audio player exists, start audio
                    if (imported.HostLayer != null && imported.HostLayer.PlayAudio)
                    {
                        _ = _videoService.RegisterLayerAsync(imported.HostLayer, playAudio: true);
                    }
                }
                catch { }

                // Update all mesh overlays for this imported video
                UpdateAllMeshOverlaysForImportedVideo(imported);
                return;
            }

            // Otherwise clear selections
            _vm.SelectedImportedVideo = null;
            _vm.SelectedMeshLayer = null;
        }

        private void UpdateAllMeshOverlaysForImportedVideo(ImportedVideoViewModel? imported)
        {
            if (imported == null) return;

            try
            {
                var targetMonitor = imported.HostLayer?.TargetMonitorIndex ?? -1;
                
                // Update overlays for all mesh layers of this imported video
                foreach (var meshVm in imported.MeshLayers)
                {
                    if (meshVm?.Model == null) continue;
                    
                    var layerId = meshVm.Model.Id;
                    if (string.IsNullOrEmpty(layerId)) continue;
                    
                    var showOverlayPref = meshVm.Model.ShowOverlay;
                    if (!showOverlayPref) continue;

                    try
                    {
                        // Map normalized output mesh points to renderer coordinates
                        Point[]? quadForRenderer = null;
                        try
                        {
                            quadForRenderer = _rendererManager?.MapNormalizedToRendererPoints(meshVm.OutputMeshPoints, targetMonitor >= 0 ? targetMonitor : null);
                        }
                        catch { quadForRenderer = null; }

                        if (quadForRenderer != null && quadForRenderer.Length >= 4)
                        {
                            _rendererManager?.AddMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, quadForRenderer, true, layerId);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Ensure right-click selects the TreeViewItem under the mouse so context menu actions apply to it
        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var clickedElement = e.OriginalSource as DependencyObject;
            if (clickedElement == null) return;

            var tvi = VisualUpwardSearch<TreeViewItem>(clickedElement);
            if (tvi != null)
            {
                tvi.IsSelected = true;
                // do not mark handled so ContextMenu still opens
            }
        }

        private static T? VisualUpwardSearch<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null && !(source is T))
            {
                source = VisualTreeHelper.GetParent(source);
            }
            return source as T;
        }

        // Context menu click handlers
        private void CreateMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var imported = (cm?.PlacementTarget as FrameworkElement)?.DataContext as ImportedVideoViewModel;
            if (imported != null)
            {
                _vm.SelectedImportedVideo = imported;
                if (_vm.CreateMeshCommand.CanExecute(null)) _vm.CreateMeshCommand.Execute(null);
            }
        }

        private void DeleteSourceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var imported = (cm?.PlacementTarget as FrameworkElement)?.DataContext as ImportedVideoViewModel;
            if (imported != null)
            {
                // Use VM event so confirmation and deletion flow is centralized
                _vm.DeleteImportedCommand.Execute(imported);
            }
        }

        private void ImportedContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            var cm = sender as ContextMenu;
            if (cm == null) return;
            var fe = cm.PlacementTarget as FrameworkElement;
            var imported = fe?.DataContext as ImportedVideoViewModel;
            if (imported == null) return;

            // find hide/show menu item by Tag
            var mi = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "HideShowToggle");
            if (mi == null) return;

            // If host layer is paused/hidden, show 'Show', otherwise 'Hide'
            if (imported.HostLayer == null)
            {
                mi.Header = "Hide Source Output/Meshes";
                return;
            }

            var visible = imported.HostLayer.Visible;
            mi.Header = visible ? "Hide Source Output/Meshes" : "Show Source Output/Meshes";
        }

        private async void HideSourceOutputMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var imported = (cm?.PlacementTarget as FrameworkElement)?.DataContext as ImportedVideoViewModel;
            if (imported != null)
            {
                try
                {
                    if (imported.HostLayer != null)
                    {
                        // toggle
                        if (imported.HostLayer.Visible)
                        {
                            // hide: update UI immediately
                            await Dispatcher.InvokeAsync(() =>
                            {
                                foreach (var meshVm in imported.MeshLayers.ToList()) meshVm.Visible = false;
                                imported.HostLayer.Visible = false;
                            }, DispatcherPriority.Normal);

                            // pause decoding to hide output
                            await _videoService.PauseLayerAsync(imported.HostLayer.Id);

                            // hide any fullscreen assigned to host
                            if (imported.HostLayer.TargetMonitorIndex >= 0)
                            {
                                _rendererManager.HideFullScreenWindow(imported.HostLayer.TargetMonitorIndex);
                            }

                            // hide any fullscreen assigned to mesh layers
                            foreach (var meshVm in imported.MeshLayers)
                            {
                                if (meshVm.Model?.TargetMonitorIndex >= 0)
                                {
                                    _rendererManager.HideFullScreenWindow(meshVm.Model.TargetMonitorIndex);
                                }
                            }
                        }
                        else
                        {
                            // show: resume decoding first
                            await _videoService.ResumeLayerAsync(imported.HostLayer.Id);

                            // update UI on UI thread
                            await Dispatcher.InvokeAsync(() =>
                            {
                                imported.HostLayer.Visible = true;
                                foreach (var meshVm in imported.MeshLayers.ToList()) meshVm.Visible = true;
                            }, DispatcherPriority.Normal);

                            // restore fullscreen for host if assigned
                            if (imported.HostLayer.TargetMonitorIndex >= 0)
                            {
                                CreateOrShowFullScreenForMonitor(imported.HostLayer.TargetMonitorIndex);
                            }

                            // restore fullscreen for meshes if assigned
                            foreach (var meshVm in imported.MeshLayers)
                            {
                                if (meshVm.Model?.TargetMonitorIndex >= 0)
                                {
                                    CreateOrShowFullScreenForMonitor(meshVm.Model.TargetMonitorIndex);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void DeleteMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var layer = (cm?.PlacementTarget as FrameworkElement)?.DataContext as LayerViewModel;
            if (layer != null)
            {
                var parent = _vm.ImportedVideos.FirstOrDefault(iv => iv.MeshLayers.Contains(layer));
                if (parent != null)
                {
                    parent.MeshLayers.Remove(layer);
                    if (_vm.SelectedMeshLayer == layer) _vm.SelectedMeshLayer = null;
                }
            }
        }

        private void CopyMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var layer = (cm?.PlacementTarget as FrameworkElement)?.DataContext as LayerViewModel;
            if (layer != null)
            {
                _vm.SelectedMeshLayer = layer;
                if (_vm.CopyMeshCommand.CanExecute(null)) _vm.CopyMeshCommand.Execute(null);
            }
        }

        private void PasteMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var fe = cm?.PlacementTarget as FrameworkElement;
            if (fe?.DataContext is ImportedVideoViewModel imported)
            {
                _vm.SelectedImportedVideo = imported;
                if (_vm.PasteMeshCommand.CanExecute(null)) _vm.PasteMeshCommand.Execute(null);
            }
            else if (fe?.DataContext is LayerViewModel layer)
            {
                var parent = _vm.ImportedVideos.FirstOrDefault(iv => iv.MeshLayers.Contains(layer));
                if (parent != null)
                {
                    _vm.SelectedImportedVideo = parent;
                    if (_vm.PasteMeshCommand.CanExecute(null)) _vm.PasteMeshCommand.Execute(null);
                }
            }
        }

        private void RenameMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            LayerViewModel? layerVm = null;

            // Try command parameter first (safer when context menu is separated)
            try
            {
                layerVm = mi?.CommandParameter as LayerViewModel;
            }
            catch { }

            // Fallback: attempt to find the placement target's DataContext
            if (layerVm == null)
            {
                try
                {
                    var cm = FindParentContextMenu(mi);
                    layerVm = (cm?.PlacementTarget as FrameworkElement)?.DataContext as LayerViewModel;
                }
                catch { }
            }

            // Final fallback: use currently selected mesh in VM
            if (layerVm == null) layerVm = _vm.SelectedMeshLayer;
            if (layerVm == null) return;

            // Simple prompt dialog
            var prompt = new Window
            {
                Title = "Rename Mesh",
                Width = 400,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(10) };
            sp.Children.Add(new TextBlock { Text = "Enter new name:", Margin = new Thickness(0,0,0,6) });
            var tb = new TextBox { Text = layerVm.Name ?? string.Empty };
            sp.Children.Add(tb);
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
            var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true, Margin = new Thickness(8,0,0,0) };
            btnPanel.Children.Add(ok); btnPanel.Children.Add(cancel);
            sp.Children.Add(btnPanel);
            prompt.Content = sp;

            ok.Click += (_, __) => prompt.DialogResult = true;

            if (prompt.ShowDialog() == true)
            {
                var newName = tb.Text?.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    // Ensure uniqueness - if name exists, append numeric suffix
                    var baseName = newName;
                    int suffix = 1;
                    bool exists;
                    do
                    {
                        exists = _vm.ImportedVideos.SelectMany(iv => iv.MeshLayers).Any(m => m != layerVm && string.Equals(m.Name, newName, StringComparison.OrdinalIgnoreCase));
                        if (exists)
                        {
                            newName = baseName + " " + (++suffix).ToString();
                        }
                    } while (exists);

                    // Update viewmodel property which updates underlying model and notifies UI
                    layerVm.Name = newName;

                    // Ensure selection reflects renamed item
                    try { _vm.SelectedMeshLayer = layerVm; } catch { }
                }
            }
        }

        private static ContextMenu? FindParentContextMenu(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ContextMenu cm) return cm;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var aboutWindow = new Views.AboutWindow
                {
                    Owner = this
                };
                aboutWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AboutMenuItem_Click failed: {ex}");
                MessageBox.Show(
                    $"Failed to open About dialog: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExitMenuItem_Click failed: {ex}");
            }
        }

        private void UpdateWindowTitle()
        {
            try
            {
                var baseTitle = "Projection Mapper";
                
                if (!string.IsNullOrEmpty(_currentProjectPath))
                {
                    baseTitle += $" - {Path.GetFileName(_currentProjectPath)}";
                }
                
                if (_vm.HasUnsavedChanges)
                {
                    baseTitle += " *";
                }
                
                Title = baseTitle;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateWindowTitle failed: {ex}");
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Check if there are unsaved changes
            if (_vm.HasUnsavedChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save your project before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    // User wants to save - cancel closing and try to save
                    e.Cancel = true;
                    
                    // Attempt to save the project
                    Task.Run(async () =>
                    {
                        try
                        {
                            await HandleSaveProjectAsync();
                            
                            // If save was successful (no more unsaved changes), close the window
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (!_vm.HasUnsavedChanges)
                                {
                                    // Temporarily remove the Closing handler to prevent recursion
                                    Closing -= MainWindow_Closing;
                                    Close();
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Save before close failed: {ex}");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                MessageBox.Show(
                                    "Failed to save project. Your changes will be lost if you close without saving.", 
                                    "Save Error", 
                                    MessageBoxButton.OK, 
                                    MessageBoxImage.Error);
                            });
                        }
                    });
                    return; // Cancel the close for now
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    // User cancelled - don't close
                    e.Cancel = true;
                    return;
                }
                // If result is No, continue with closing (don't save)
            }

            // Don't block the closing - let the window close immediately
            // Start async cleanup on background thread
            Task.Run(async () =>
            {
                try
                {
                    Debug.WriteLine("MainWindow_Closing: Starting async cleanup");
                    
                    // Stop all video services first
                    try 
                    { 
                        await _videoService.StopAllAsync().ConfigureAwait(false); 
                        Debug.WriteLine("MainWindow_Closing: VideoService stopped");
                    } 
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"MainWindow_Closing: VideoService stop failed: {ex}"); 
                    }

                    // Stop renderer manager
                    try 
                    { 
                        await _rendererManager.StopAsync().ConfigureAwait(false);
                        Debug.WriteLine("MainWindow_Closing: RendererManager stopped"); 
                    } 
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"MainWindow_Closing: RendererManager stop failed: {ex}"); 
                    }

                    // Dispose services
                    try 
                    { 
                        _videoService.Dispose(); 
                        Debug.WriteLine("MainWindow_Closing: VideoService disposed");
                    } 
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"MainWindow_Closing: VideoService dispose failed: {ex}"); 
                    }

                    try 
                    { 
                        _rendererManager.Dispose(); 
                        Debug.WriteLine("MainWindow_Closing: RendererManager disposed");
                    } 
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"MainWindow_Closing: RendererManager dispose failed: {ex}"); 
                    }

                    Debug.WriteLine("MainWindow_Closing: Async cleanup completed");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow_Closing: Cleanup failed: {ex}");
                }
            });
        }

        private void HandlePreview()
        {
            try
            {
                var imported = _vm.SelectedImportedVideo;
                if (imported?.HostLayer == null) 
                {
                    MessageBox.Show("Please select a video source first.", "Preview", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var targetMonitor = imported.HostLayer.TargetMonitorIndex;
                if (targetMonitor < 0 || targetMonitor >= _monitors.Count)
                {
                    MessageBox.Show("Please assign an output display to the selected source first.", "Preview", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Toggle preview: if fullscreen is already shown, hide it; otherwise show it
                if (_previewMonitorStates.ContainsKey(targetMonitor) && _previewMonitorStates[targetMonitor])
                {
                    // Hide/restore
                    _rendererManager.HideFullScreenWindow(targetMonitor);
                    _previewMonitorStates[targetMonitor] = false;
                    _vm.StatusText = $"Preview closed for Display {targetMonitor + 1}";
                }
                else
                {
                    // Show fullscreen
                    _rendererManager.HideFullScreenWindow(targetMonitor); // Hide any existing first
                    CreateOrShowFullScreenForMonitor(targetMonitor);
                    _previewMonitorStates[targetMonitor] = true;
                    _vm.StatusText = $"Preview showing on Display {targetMonitor + 1} (press Escape to close)";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HandlePreview failed: {ex}");
                MessageBox.Show($"Preview failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task HandleSaveProjectAsync()
        {
            try
            {
                string? path = _currentProjectPath;
                
                // If no current path, show save dialog (this MUST be on UI thread)
                if (string.IsNullOrEmpty(path))
                {
                    // Ensure we're on UI thread when showing file dialog
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        path = await _fileDialog.ShowSaveFileDialogAsync(
                            "Save Project",
                            "Untitled.pmproj",
                            "Projection Mapper Project (*.pmproj)|*.pmproj|All files|*.*");
                    });
                    
                    if (string.IsNullOrEmpty(path)) return; // User cancelled
                }

                // Build project model from current state - MUST be done on UI thread
                ProjectModel? project = null;
                try
                {
                    // Ensure we're on UI thread when building project model
                    await Dispatcher.InvokeAsync(() =>
                    {
                        project = BuildProjectModel();
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HandleSaveProjectAsync: BuildProjectModel failed: {ex}");
                    MessageBox.Show("Failed to build project model.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (project == null)
                {
                    MessageBox.Show("Failed to build project model.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Save project on background thread to avoid blocking UI
                bool success = false;
                try
                {
                    success = await Task.Run(async () =>
                    {
                        return await _projectService.SaveAsync(project, path);
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HandleSaveProjectAsync: Save operation failed: {ex}");
                    success = false;
                }
                
                // Update UI based on result (ensure on UI thread)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (success)
                    {
                        _currentProjectPath = path;
                        _vm.StatusText = $"Project saved to {Path.GetFileName(path)}";
                        
                        // Mark project as clean after successful save
                        _vm.MarkProjectClean();
                        UpdateWindowTitle();
                    }
                    else
                    {
                        MessageBox.Show("Failed to save project. Check the logs for details.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HandleSaveProjectAsync failed: {ex}");
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Failed to save project: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private async Task HandleSaveAsProjectAsync()
        {
            try
            {
                string? path = null;
                
                // Always show save dialog for Save As (this MUST be on UI thread)
                await Dispatcher.InvokeAsync(async () =>
                {
                    path = await _fileDialog.ShowSaveFileDialogAsync(
                        "Save Project As",
                        string.IsNullOrEmpty(_currentProjectPath) ? "Untitled.pmproj" : Path.GetFileName(_currentProjectPath),
                        "Projection Mapper Project (*.pmproj)|*.pmproj|All files|*.*");
                });
                
                if (string.IsNullOrEmpty(path)) return; // User cancelled

                // Build project model from current state - MUST be done on UI thread
                ProjectModel? project = null;
                try
                {
                    // Ensure we're on UI thread when building project model
                    await Dispatcher.InvokeAsync(() =>
                    {
                        project = BuildProjectModel();
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HandleSaveAsProjectAsync: BuildProjectModel failed: {ex}");
                    MessageBox.Show("Failed to build project model.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (project == null)
                {
                    MessageBox.Show("Failed to build project model.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Save project on background thread to avoid blocking UI
                bool success = false;
                try
                {
                    success = await Task.Run(async () =>
                    {
                        return await _projectService.SaveAsync(project, path);
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HandleSaveAsProjectAsync: Save operation failed: {ex}");
                    success = false;
                }
                
                // Update UI based on result
                if (success)
                {
                    _currentProjectPath = path;
                    _vm.StatusText = $"Project saved to {Path.GetFileName(path)}";
                    
                    // Mark project as clean after successful save
                    _vm.MarkProjectClean();
                    UpdateWindowTitle();
                }
                else
                {
                    MessageBox.Show("Failed to save project. Check the logs for details.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HandleSaveAsProjectAsync failed: {ex}");
                MessageBox.Show($"Failed to save project: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task HandleLoadProjectAsync()
        {
            try
            {
                string? path = null;
                
                // Show open file dialog (this MUST be on UI thread)
                await Dispatcher.InvokeAsync(async () =>
                {
                    path = await _fileDialog.ShowOpenFileDialogAsync(
                        "Open Project",
                        "Projection Mapper Project (*.pmproj)|*.pmproj|All files|*.*");
                });
                
                if (string.IsNullOrEmpty(path)) return; // User cancelled

                // Load project
                var project = await _projectService.LoadAsync(path);
                
                if (project == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show("Failed to load project. The file may be corrupt or in an incompatible format.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return;
                }

                // Clear current project state
                await ClearCurrentProjectAsync();

                // Apply loaded project
                await ApplyLoadedProjectAsync(project);

                // Update UI state (ensure on UI thread)
                await Dispatcher.InvokeAsync(() =>
                {
                    _currentProjectPath = path;
                    _vm.StatusText = $"Project loaded from {Path.GetFileName(path)}";
                    
                    // Mark project as clean after successful load
                    _vm.MarkProjectClean();
                    
                    // Update window title
                    UpdateWindowTitle();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HandleLoadProjectAsync failed: {ex}");
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Failed to load project: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private async Task HandleNewProjectAsync()
        {
            try
            {
                // Check if there are unsaved changes and prompt user
                if (_vm.HasUnsavedChanges)
                {
                    var result = MessageBox.Show(
                        "You have unsaved changes. Do you want to save your project before creating a new one?",
                        "Unsaved Changes",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // User wants to save first
                        await HandleSaveProjectAsync();
                        
                        // If save failed or was cancelled, don't proceed with new project
                        if (_vm.HasUnsavedChanges)
                        {
                            return; // Save failed or was cancelled
                        }
                    }
                    else if (result == MessageBoxResult.Cancel)
                    {
                        // User cancelled - don't create new project
                        return;
                    }
                    // If result is No, continue with creating new project (discard changes)
                }

                // Clear current project state
                await ClearCurrentProjectAsync();

                // Reset to a clean project state
                await Dispatcher.InvokeAsync(() =>
                {
                    // Clear the current project path
                    _currentProjectPath = null;
                    
                    // Reset view model to clean state
                    _vm.ImportedVideos.Clear();
                    _vm.SelectedImportedVideo = null;
                    _vm.SelectedMeshLayer = null;
                    
                    // Reset global settings to defaults
                    _rendererManager.ShowMeshOverlay = true;
                    _vm.InputZoom = 1.0;
                    _vm.OutputZoom = 1.0;
                    
                    // Reset UI checkboxes to defaults
                    if (PART_ShowGridCheckbox != null) PART_ShowGridCheckbox.IsChecked = false;
                    if (PART_GlobalShowMeshOverlayCheckbox != null) PART_GlobalShowMeshOverlayCheckbox.IsChecked = true;
                    if (PART_ShowMeshOverlayCheckbox != null) PART_ShowMeshOverlayCheckbox.IsChecked = true;
                    
                    // Clear any monitor selections
                    if (PART_MonitorCombo != null) PART_MonitorCombo.SelectedIndex = -1;
                    
                    // Mark as clean and update UI
                    _vm.MarkProjectClean();
                    _vm.StatusText = "New project created";
                    UpdateWindowTitle();
                    
                }, DispatcherPriority.Normal);

                // Hide all fullscreen windows
                foreach (var monitorIndex in _previewMonitorStates.Keys.ToList())
                {
                    try
                    {
                        _rendererManager.HideFullScreenWindow(monitorIndex);
                        _previewMonitorStates[monitorIndex] = false;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"HandleNewProjectAsync: Failed to hide fullscreen: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HandleNewProjectAsync failed: {ex}");
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"Failed to create new project: {ex.Message}", "New Project Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private ProjectModel BuildProjectModel()
        {
            try
            {
                var project = new ProjectModel
                {
                    Name = _vm.ActiveProject?.Name ?? "Untitled Project",
                    CreatedAt = _vm.ActiveProject?.CreatedAt ?? DateTime.UtcNow,
                    ShowMeshOverlay = _rendererManager.ShowMeshOverlay,
                    ShowCoordinateGrid = PART_ShowGridCheckbox?.IsChecked ?? false,
                    InputZoom = _vm.InputZoom,
                    OutputZoom = _vm.OutputZoom
                };

                // Convert imported videos to serializable format
                // We need to copy all data on the UI thread to avoid cross-thread access violations
                foreach (var imported in _vm.ImportedVideos)
                {
                    try
                    {
                        if (imported?.HostLayer == null) continue;

                        var importedData = new ImportedVideoData
                        {
                            Id = imported.HostLayer.Id ?? Guid.NewGuid().ToString("N"),
                            Name = imported.Name ?? string.Empty,
                            SourcePath = imported.SourcePath ?? string.Empty,
                            TargetMonitorIndex = imported.HostLayer.TargetMonitorIndex,
                            PlayAudio = imported.PlayAudio,
                            Visible = imported.HostLayer.Visible
                        };

                        // Convert mesh layers
                        foreach (var meshVm in imported.MeshLayers)
                        {
                            try
                            {
                                if (meshVm?.Model == null) continue;

                                var meshData = new MeshLayerData
                                {
                                    Id = meshVm.Model.Id ?? Guid.NewGuid().ToString("N"),
                                    Name = meshVm.Name ?? string.Empty,
                                    SourceId = meshVm.Model.SourceId ?? string.Empty,
                                    X = meshVm.X,
                                    Y = meshVm.Y,
                                    Width = meshVm.Width,
                                    Height = meshVm.Height,
                                    Opacity = meshVm.Opacity,
                                    Visible = meshVm.Visible,
                                    RotationDegrees = meshVm.RotationDegrees,
                                    TargetMonitorIndex = meshVm.Model.TargetMonitorIndex,
                                    ShowOverlay = meshVm.Model.ShowOverlay
                                };

                                // Convert mesh points to flat array - read from the model arrays directly
                                var meshPts = meshVm.Model.MeshPoints;
                                if (meshPts != null && meshPts.Length >= 4)
                                {
                                    meshData.MeshPoints = new float[]
                                    {
                                        meshPts[0].X, meshPts[0].Y,
                                        meshPts[1].X, meshPts[1].Y,
                                        meshPts[2].X, meshPts[2].Y,
                                        meshPts[3].X, meshPts[3].Y
                                    };
                                }

                                var outputPts = meshVm.Model.OutputMeshPoints;
                                if (outputPts != null && outputPts.Length >= 4)
                                {
                                    meshData.OutputMeshPoints = new float[]
                                    {
                                        outputPts[0].X, outputPts[0].Y,
                                        outputPts[1].X, outputPts[1].Y,
                                        outputPts[2].X, outputPts[2].Y,
                                        outputPts[3].X, outputPts[3].Y
                                    };
                                }

                                importedData.MeshLayers.Add(meshData);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"BuildProjectModel: Failed to convert mesh layer: {ex}");
                            }
                        }

                        project.ImportedVideos.Add(importedData);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"BuildProjectModel: Failed to convert imported video: {ex}");
                    }
                }

                // Save playlist groups
                project.PlaylistGroups = _vm.BuildPlaylistGroupModels();
                project.PlaylistMode = _vm.IsPlaylistMode;

                return project;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BuildProjectModel failed: {ex}");
                throw;
            }
        }

        private async Task ClearCurrentProjectAsync()
        {
            try
            {
                // Stop and unregister all video layers
                foreach (var imported in _vm.ImportedVideos.ToList())
                {
                    try
                    {
                        if (imported.HostLayer != null && !string.IsNullOrEmpty(imported.HostLayer.Id))
                        {
                            await _videoService.UnregisterLayerAsync(imported.HostLayer.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ClearCurrentProjectAsync: Failed to unregister layer: {ex}");
                    }
                }

                // Clear UI collections
                await Dispatcher.InvokeAsync(() =>
                {
                    _vm.ImportedVideos.Clear();
                    _vm.SelectedImportedVideo = null;
                    _vm.SelectedMeshLayer = null;
                    
                    // Clear playlist groups
                    _vm.PlaylistGroups.Clear();
                    _vm.SelectedPlaylistGroup = null;
                    _vm.CurrentPlaylistGroup = null;
                    _vm.IsPlaylistMode = false;
                }, DispatcherPriority.Normal);

                // Stop playlist service if running
                try
                {
                    await _playlistService.StopPlaylistAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ClearCurrentProjectAsync: Failed to stop playlist: {ex}");
                }

                // Hide all fullscreen windows
                foreach (var monitorIndex in _previewMonitorStates.Keys.ToList())
                {
                    try
                    {
                        _rendererManager.HideFullScreenWindow(monitorIndex);
                        _previewMonitorStates[monitorIndex] = false;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ClearCurrentProjectAsync: Failed to hide fullscreen: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClearCurrentProjectAsync failed: {ex}");
            }
        }

        private async Task ApplyLoadedProjectAsync(ProjectModel project)
        {
            try
            {
                // Restore global settings
                _rendererManager.ShowMeshOverlay = project.ShowMeshOverlay;
                _vm.InputZoom = project.InputZoom;
                _vm.OutputZoom = project.OutputZoom;
                
                await Dispatcher.InvokeAsync(() =>
                {
                    if (PART_ShowGridCheckbox != null) PART_ShowGridCheckbox.IsChecked = project.ShowCoordinateGrid;
                    if (PART_GlobalShowMeshOverlayCheckbox != null) PART_GlobalShowMeshOverlayCheckbox.IsChecked = project.ShowMeshOverlay;
                }, DispatcherPriority.Normal);

                // Restore imported videos
                foreach (var importedData in project.ImportedVideos)
                {
                    try
                    {
                        // Check if source file exists
                        if (!File.Exists(importedData.SourcePath))
                        {
                            Debug.WriteLine($"ApplyLoadedProjectAsync: Source file not found: {importedData.SourcePath}");
                            MessageBox.Show($"Source file not found: {importedData.SourcePath}\n\nThis video will be skipped.", "Missing File", MessageBoxButton.OK, MessageBoxImage.Warning);
                            continue;
                        }

                        // Create ImportedVideoViewModel
                        var importedVm = new ImportedVideoViewModel(importedData.Id, importedData.Name, importedData.SourcePath);

                        // Create host layer
                        var hostLayer = new LayerModel
                        {
                            Id = importedData.Id,
                            Name = importedData.Name,
                            SourcePath = importedData.SourcePath,
                            TargetMonitorIndex = importedData.TargetMonitorIndex,
                            PlayAudio = importedData.PlayAudio,
                            Visible = importedData.Visible,
                            PreviewOnly = importedData.MeshLayers.Count > 0 // Preview only if meshes exist
                        };

                        // Determine decode size
                        var hostForLayer = PART_InputHost ?? PART_OutputHost;
                        var w = (int)Math.Max(1, hostForLayer.ActualWidth);
                        var h = (int)Math.Max(1, hostForLayer.ActualHeight);
                        if (w == 0 || h == 0) { w = 1280; h = 720; }
                        hostLayer.X = 0; hostLayer.Y = 0; hostLayer.Width = w; hostLayer.Height = h;

                        // Register decoder
                        await _videoService.RegisterLayerAsync(hostLayer);
                        importedVm.HostLayer = hostLayer;
                        importedVm.NotifyHostLayerChanged();

                        // Restore mesh layers
                        foreach (var meshData in importedData.MeshLayers)
                        {
                            var layerModel = new LayerModel
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
                                RotationDegrees = meshData.RotationDegrees,
                                TargetMonitorIndex = meshData.TargetMonitorIndex,
                                ShowOverlay = meshData.ShowOverlay
                            };

                            // Restore mesh points from flat array
                            if (meshData.MeshPoints != null && meshData.MeshPoints.Length >= 8)
                            {
                                layerModel.MeshPoints[0] = new Vector2(meshData.MeshPoints[0], meshData.MeshPoints[1]);
                                layerModel.MeshPoints[1] = new Vector2(meshData.MeshPoints[2], meshData.MeshPoints[3]);
                                layerModel.MeshPoints[2] = new Vector2(meshData.MeshPoints[4], meshData.MeshPoints[5]);
                                layerModel.MeshPoints[3] = new Vector2(meshData.MeshPoints[6], meshData.MeshPoints[7]);
                            }

                            if (meshData.OutputMeshPoints != null && meshData.OutputMeshPoints.Length >= 8)
                            {
                                layerModel.OutputMeshPoints[0] = new Vector2(meshData.OutputMeshPoints[0], meshData.OutputMeshPoints[1]);
                                layerModel.OutputMeshPoints[1] = new Vector2(meshData.OutputMeshPoints[2], meshData.OutputMeshPoints[3]);
                                layerModel.OutputMeshPoints[2] = new Vector2(meshData.OutputMeshPoints[4], meshData.OutputMeshPoints[5]);
                                layerModel.OutputMeshPoints[3] = new Vector2(meshData.OutputMeshPoints[6], meshData.OutputMeshPoints[7]);
                            }

                            var meshVm = new LayerViewModel(layerModel);
                            importedVm.MeshLayers.Add(meshVm);

                            // Register mesh layer with video service
                            await _videoService.RegisterMeshLayerAsync(layerModel);
                        }

                        // Add to UI
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _vm.ImportedVideos.Add(importedVm);
                            
                            // Update monitor combo if this is the first imported video
                            if (_vm.ImportedVideos.Count == 1)
                            {
                                _vm.SelectedImportedVideo = importedVm;
                                if (PART_MonitorCombo != null && importedVm.HostLayer != null)
                                {
                                    PART_MonitorCombo.SelectedIndex = importedVm.HostLayer.TargetMonitorIndex >= 0 ? importedVm.HostLayer.TargetMonitorIndex : -1;
                                }
                            }
                        }, DispatcherPriority.Normal);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ApplyLoadedProjectAsync: Failed to load imported video {importedData.Name}: {ex}");
                    }
                }

                // Load playlist groups
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        _vm.LoadPlaylistGroups(project.PlaylistGroups);
                        _vm.IsPlaylistMode = project.PlaylistMode;
                        
                        // Populate video references in groups
                        _vm.UpdatePlaylistGroupVideos();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ApplyLoadedProjectAsync: Failed to load playlist groups: {ex}");
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyLoadedProjectAsync failed: {ex}");
            }
        }

        private record MonitorInfo(int Width, int Height, int Left, int Top);
        private static List<MonitorInfo> EnumerateMonitors()
        {
            try
            {
                var list = new List<MonitorInfo>();

                MonitorEnumDelegate del = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    try
                    {
                        var mi = new MONITORINFOEX();
                        mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
                        if (GetMonitorInfo(hMonitor, ref mi))
                        {
                            var w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                            var h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                            var l = mi.rcMonitor.Left;
                            var t = mi.rcMonitor.Top;
                            list.Add(new MonitorInfo(w, h, l, t));
                        }
                    }
                    catch { }
                    return true;
                };

                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, del, IntPtr.Zero);

                return list;
            }
            catch
            {
                return new List<MonitorInfo>();
            }
        }

        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        // P/Invoke for positioning windows
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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

        #region Playlist Event Handlers

        private void OnPlaylistGroupChanged(int newGroupIndex)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Update IsActive flag on all groups
                        foreach (var group in _vm.PlaylistGroups)
                        {
                            group.IsActive = group.Order == newGroupIndex;
                        }

                        // Update CurrentPlaylistGroup
                        if (newGroupIndex >= 0 && newGroupIndex < _vm.PlaylistGroups.Count)
                        {
                            _vm.CurrentPlaylistGroup = _vm.PlaylistGroups.FirstOrDefault(g => g.Order == newGroupIndex);
                        }
                        else
                        {
                            _vm.CurrentPlaylistGroup = null;
                        }

                        _vm.StatusText = $"Playing Group {newGroupIndex + 1}: {_vm.CurrentPlaylistGroup?.Name ?? "Unknown"}";
                        Debug.WriteLine($"PlaylistGroupChanged: Now playing group {newGroupIndex}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OnPlaylistGroupChanged: Error updating UI: {ex}");
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnPlaylistGroupChanged: Error: {ex}");
            }
        }

        private void OnPlaylistCompleted()
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        _vm.StatusText = "Playlist completed - looping back to start";
                        Debug.WriteLine("PlaylistCompleted: Playlist cycle completed");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OnPlaylistCompleted: Error updating UI: {ex}");
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnPlaylistCompleted: Error: {ex}");
            }
        }

        #endregion
    }
}