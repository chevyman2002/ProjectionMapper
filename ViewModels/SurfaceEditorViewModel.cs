using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Input;
using ProjectionMapper.Models;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// ViewModel for the surface editor area (mesh editing). Holds pan/zoom and the active surface being edited.
    /// Renderer and input handling will interact with this VM (or a dedicated presenter) in later steps.
    /// </summary>
    public sealed class SurfaceEditorViewModel : BaseViewModel
    {
        private SurfaceModel? _surface;
        private float _zoom = 1.0f;
        private Vector2 _pan = Vector2.Zero;

        public SurfaceEditorViewModel()
        {
            ResetViewCommand = new RelayCommand(ExecuteResetView);
            FitToViewCommand = new RelayCommand(ExecuteFitToView);
            ToggleGridCommand = new RelayCommand(ExecuteToggleGrid);
        }

        public SurfaceModel? Surface
        {
            get => _surface;
            set => SetProperty(ref _surface, value);
        }

        public float Zoom
        {
            get => _zoom;
            set
            {
                if (Math.Abs(_zoom - value) < 1e-6f) return;
                _zoom = value;
                RaisePropertyChanged();
            }
        }

        public Vector2 Pan
        {
            get => _pan;
            set => SetProperty(ref _pan, value);
        }

        public ICommand ResetViewCommand { get; }
        public ICommand FitToViewCommand { get; }
        public ICommand ToggleGridCommand { get; }

        private void ExecuteResetView()
        {
            Zoom = 1.0f;
            Pan = Vector2.Zero;
        }

        private void ExecuteFitToView()
        {
            // TODO: implement proper fit logic based on surface size and available viewport.
            Task.Run(() => { Zoom = 1.0f; });
        }

        private void ExecuteToggleGrid()
        {
            // Future: toggle visibility of snapping grid.
        }
    }
}