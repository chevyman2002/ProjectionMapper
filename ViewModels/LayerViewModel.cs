using System;
using System.ComponentModel;
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
    }
}