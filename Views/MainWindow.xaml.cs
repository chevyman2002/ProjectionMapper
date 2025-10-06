using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.Models;

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

        public MainWindow()
        {
            InitializeComponent();

            _vm = new MainWindowViewModel();
            DataContext = _vm;

            // Create a software renderer and renderer manager, attach the host
            _softwareRenderer = new SoftwareRenderer();
            _rendererManager = new RendererManager(_softwareRenderer);
            _rendererManager.AttachHost(PART_RenderHost);

            // Create VideoService (ffmpeg path empty -> expects ffmpeg on PATH)
            _videoService = new VideoService(_rendererManager, ffmpegPath: null);
            _fileDialog = new FileDialogService();

            // Start renderer (use center host size; fallback to 1280x720)
            Loaded += async (_, __) =>
            {
                var w = (int)Math.Max(1, PART_RenderHost.ActualWidth);
                var h = (int)Math.Max(1, PART_RenderHost.ActualHeight);
                if (w == 0 || h == 0) { w = 1280; h = 720; }
                await _rendererManager.StartAsync(w, h);
            };

            Closing += async (_, __) =>
            {
                await _videoService.UnregisterLayerAsync(""); // no-op
                _videoService.Dispose();
                _rendererManager.Dispose();
            };
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Show open file dialog - only single file in this simple UI
                var path = await _fileDialog.ShowOpenFileDialogAsync("Import video", "Video files|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm|All files|*.*");
                if (string.IsNullOrEmpty(path)) return;
                if (!File.Exists(path)) return;

                // Create a layer for this video. For now default bounds to full renderer size.
                var layer = new LayerModel
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = Path.GetFileName(path),
                    SourcePath = path
                };

                // Use current host size as default mapping rectangle
                var w = (int)Math.Max(1, PART_RenderHost.ActualWidth);
                var h = (int)Math.Max(1, PART_RenderHost.ActualHeight);
                if (w == 0 || h == 0) { w = 1280; h = 720; }
                layer.X = 0;
                layer.Y = 0;
                layer.Width = w;
                layer.Height = h;

                // Register and start decoding for this layer
                await _videoService.RegisterLayerAsync(layer);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import video: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}