using System;
using System.Collections.ObjectModel;
using ProjectionMapper.Models;

namespace ProjectionMapper.ViewModels
{
    public class ImportedVideoViewModel : BaseViewModel
    {
        public ImportedVideoViewModel(string id, string name, string sourcePath)
        {
            Id = id;
            Name = name;
            SourcePath = sourcePath;
            MeshLayers = new ObservableCollection<LayerViewModel>();
        }

        public string Id { get; }
        public string Name { get; set; }
        public string SourcePath { get; set; }
        public ObservableCollection<LayerViewModel> MeshLayers { get; }

        // The host layer used by the renderer for this imported video
        public LayerModel? HostLayer { get; set; }

        // Expose audio properties for binding convenience
        public bool PlayAudio
        {
            get => HostLayer?.PlayAudio ?? false;
            set
            {
                if (HostLayer == null) return;
                if (HostLayer.PlayAudio == value) return;
                HostLayer.PlayAudio = value;
                RaisePropertyChanged();
            }
        }

        public double Volume
        {
            get => HostLayer?.Volume ?? 1.0;
            set
            {
                if (HostLayer == null) return;
                if (Math.Abs(HostLayer.Volume - value) < 1e-6) return;
                HostLayer.Volume = value;
                RaisePropertyChanged();
            }
        }

        public bool Muted
        {
            get => HostLayer?.Muted ?? false;
            set
            {
                if (HostLayer == null) return;
                if (HostLayer.Muted == value) return;
                HostLayer.Muted = value;
                RaisePropertyChanged();
            }
        }

        // Call this when HostLayer changes to notify UI of property changes
        public void NotifyHostLayerChanged()
        {
            RaisePropertyChanged(nameof(PlayAudio));
            RaisePropertyChanged(nameof(Volume));
            RaisePropertyChanged(nameof(Muted));
        }
    }
}