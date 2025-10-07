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
    }
}