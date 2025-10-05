using System;
using System.Collections.ObjectModel;
using ProjectionMapper.Models;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// Main window view model holding the project and basic commands.
    /// Kept small and testable.
    /// </summary>
    public class MainWindowViewModel : BaseViewModel
    {
        private ProjectModel? _project;

        public MainWindowViewModel()
        {
            // Start with an empty project
            _project = new ProjectModel { Name = "Untitled Project" };
            Projects = new ObservableCollection<ProjectModel> { _project };
        }

        public ObservableCollection<ProjectModel> Projects { get; }

        public ProjectModel? ActiveProject
        {
            get => _project;
            set => SetProperty(ref _project, value);
        }

        // Example property for status text shown in the status bar
        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        // TODO: Add ICommand implementations (Open, Save, New Surface, Import)
    }
}