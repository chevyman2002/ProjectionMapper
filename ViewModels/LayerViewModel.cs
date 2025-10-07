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

        public Vector2[] MeshPoints => _model.MeshPoints;

        public void SetMeshPoint(int index, Vector2 pt)
        {
            if (index < 0 || index >= _model.MeshPoints.Length) throw new ArgumentOutOfRangeException(nameof(index));
            _model.MeshPoints[index] = pt;
            RaisePropertyChanged(nameof(MeshPoints));
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
    }
}