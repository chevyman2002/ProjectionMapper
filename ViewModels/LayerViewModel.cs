using System;
using System.ComponentModel;
using System.Numerics;
using ProjectionMapper.Models;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// ViewModel wrapper for a single LayerModel; exposes properties for binding and change notifications.
    /// </summary>
    public sealed class LayerViewModel : BaseViewModel
    {
        private readonly LayerModel _model;

        public LayerViewModel(LayerModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        // Expose underlying model for scenarios where the host needs to register mesh layers with services
        public LayerModel Model => _model;

        public string? Id => _model.Id;

        public string Name
        {
            get => _model.Name ?? string.Empty;
            set
            {
                if (_model.Name == value) return;
                _model.Name = value;
                RaisePropertyChanged();
            }
        }

        public string? SourcePath
        {
            get => _model.SourcePath;
            set
            {
                if (_model.SourcePath == value) return;
                _model.SourcePath = value;
                RaisePropertyChanged();
            }
        }

        public double Opacity
        {
            get => _model.Opacity;
            set
            {
                if (Math.Abs(_model.Opacity - value) < 1e-6) return;
                _model.Opacity = value;
                RaisePropertyChanged();
            }
        }

        public bool Visible
        {
            get => _model.Visible;
            set
            {
                if (_model.Visible == value) return;
                _model.Visible = value;
                RaisePropertyChanged();
            }
        }

        public int X
        {
            get => _model.X;
            set
            {
                if (_model.X == value) return;
                _model.X = value;
                RaisePropertyChanged();
            }
        }

        public int Y
        {
            get => _model.Y;
            set
            {
                if (_model.Y == value) return;
                _model.Y = value;
                RaisePropertyChanged();
            }
        }

        public int Width
        {
            get => _model.Width;
            set
            {
                if (_model.Width == value) return;
                _model.Width = value;
                RaisePropertyChanged();
            }
        }

        public int Height
        {
            get => _model.Height;
            set
            {
                if (_model.Height == value) return;
                _model.Height = value;
                RaisePropertyChanged();
            }
        }

        // Source-side mesh points (used for cropping/input selection)
        public Vector2[] MeshPoints => _model.MeshPoints;

        // Output-side mesh points (used for output mapping/warping)
        public Vector2[] OutputMeshPoints => _model.OutputMeshPoints;

        public void SetMeshPoint(int index, Vector2 pt)
        {
            if (index < 0 || index >= _model.MeshPoints.Length) throw new ArgumentOutOfRangeException(nameof(index));

            // Clamp to normalized range [0,1] to prevent runaway coordinates from UI drags
            var clampedX = Math.Max(0f, Math.Min(1f, pt.X));
            var clampedY = Math.Max(0f, Math.Min(1f, pt.Y));

            var old = _model.MeshPoints[index];
            if (Math.Abs(old.X - clampedX) < 1e-6f && Math.Abs(old.Y - clampedY) < 1e-6f) return;

            _model.MeshPoints[index] = new Vector2(clampedX, clampedY);
            RaisePropertyChanged(nameof(MeshPoints));
        }

        public void SetOutputMeshPoint(int index, Vector2 pt)
        {
            if (index < 0 || index >= _model.OutputMeshPoints.Length) throw new ArgumentOutOfRangeException(nameof(index));

            var clampedX = Math.Max(0f, Math.Min(1f, pt.X));
            var clampedY = Math.Max(0f, Math.Min(1f, pt.Y));

            var old = _model.OutputMeshPoints[index];
            if (Math.Abs(old.X - clampedX) < 1e-6f && Math.Abs(old.Y - clampedY) < 1e-6f) return;

            _model.OutputMeshPoints[index] = new Vector2(clampedX, clampedY);
            RaisePropertyChanged(nameof(OutputMeshPoints));
        }

        public double RotationDegrees
        {
            get => _model.RotationDegrees;
            set
            {
                if (Math.Abs(_model.RotationDegrees - value) < 1e-6) return;
                _model.RotationDegrees = value;
                RaisePropertyChanged();
            }
        }

        public bool ShowOverlay
        {
            get => _model.ShowOverlay;
            set
            {
                if (_model.ShowOverlay == value) return;
                _model.ShowOverlay = value;
                RaisePropertyChanged();
            }
        }

        public int TargetMonitorIndex
        {
            get => _model.TargetMonitorIndex;
            set
            {
                if (_model.TargetMonitorIndex == value) return;
                _model.TargetMonitorIndex = value;
                RaisePropertyChanged();
            }
        }
    }
}