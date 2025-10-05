using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ProjectionMapper.Models;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// ViewModel that exposes the ProjectModel and operations on it (Add surface, Import, Save stub).
    /// Commands are placeholders and should be connected to Services (IProjectService / IFileDialogService) later.
    /// </summary>
    public sealed class ProjectViewModel : BaseViewModel
    {
        private ProjectModel _project;

        public ProjectViewModel(ProjectModel project)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            Surfaces = new ObservableCollection<SurfaceModel>(_project.Surfaces);
            AddSurfaceCommand = new RelayCommand(ExecuteAddSurface);
            RemoveSurfaceCommand = new RelayCommand(ExecuteRemoveSurface, CanRemoveSurface);
            ImportProjectCommand = new AsyncRelayCommand(ExecuteImportAsync);
            SaveProjectCommand = new AsyncRelayCommand(ExecuteSaveAsync);
        }

        public ObservableCollection<SurfaceModel> Surfaces { get; }

        private SurfaceModel? _selectedSurface;
        public SurfaceModel? SelectedSurface
        {
            get => _selectedSurface;
            set => SetProperty(ref _selectedSurface, value);
        }

        public ICommand AddSurfaceCommand { get; }
        public ICommand RemoveSurfaceCommand { get; }
        public ICommand ImportProjectCommand { get; }
        public ICommand SaveProjectCommand { get; }

        private void ExecuteAddSurface()
        {
            var surface = new SurfaceModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"Surface {Surfaces.Count + 1}"
            };
            _project.Surfaces.Add(surface);
            Surfaces.Add(surface);
            SelectedSurface = surface;
        }

        private bool CanRemoveSurface(object? _) => SelectedSurface != null;

        private void ExecuteRemoveSurface(object? _)
        {
            if (SelectedSurface == null) return;
            _project.Surfaces.Remove(SelectedSurface);
            Surfaces.Remove(SelectedSurface);
            SelectedSurface = Surfaces.FirstOrDefault();
        }

        private async Task ExecuteImportAsync(object? _)
        {
            // Placeholder: integrate MapMapProjectImporter or file-picking service in future.
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task ExecuteSaveAsync(object? _)
        {
            // Placeholder: serialize project to disk via a ProjectService.
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}