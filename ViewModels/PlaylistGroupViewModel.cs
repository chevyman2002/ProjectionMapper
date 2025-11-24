using System;
using System.Collections.ObjectModel;
using ProjectionMapper.Models;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// ViewModel for a playlist group. Wraps PlaylistGroupModel and provides
    /// observable properties for UI binding.
    /// </summary>
    public class PlaylistGroupViewModel : BaseViewModel
    {
        private readonly PlaylistGroupModel _model;

        /// <summary>
        /// Creates a new PlaylistGroupViewModel wrapping the given model.
        /// </summary>
        /// <param name="model">The underlying PlaylistGroupModel.</param>
        /// <exception cref="ArgumentNullException">Thrown if model is null.</exception>
        public PlaylistGroupViewModel(PlaylistGroupModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            Videos = new ObservableCollection<ImportedVideoViewModel>();
        }

        /// <summary>
        /// Gets the underlying model.
        /// </summary>
        public PlaylistGroupModel Model => _model;

        /// <summary>
        /// Gets the unique identifier for this group.
        /// </summary>
        public string Id => _model.Id;

        /// <summary>
        /// Gets or sets the display name of the group.
        /// </summary>
        public string Name
        {
            get => _model.Name;
            set
            {
                if (_model.Name == value) return;
                _model.Name = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the order of this group in the playlist.
        /// </summary>
        public int Order
        {
            get => _model.Order;
            set
            {
                if (_model.Order == value) return;
                _model.Order = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Observable collection of videos in this group for UI binding.
        /// This is populated from the SourceIds in the model.
        /// </summary>
        public ObservableCollection<ImportedVideoViewModel> Videos { get; }

        private bool _isActive;
        /// <summary>
        /// Gets or sets whether this is the currently playing group.
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                RaisePropertyChanged();
            }
        }

        private bool _isExpanded = true;
        /// <summary>
        /// Gets or sets whether the group is expanded in the tree view.
        /// </summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Gets the number of videos in this group.
        /// </summary>
        public int VideoCount => Videos.Count;

        /// <summary>
        /// Refreshes the VideoCount property binding.
        /// </summary>
        public void RefreshVideoCount()
        {
            RaisePropertyChanged(nameof(VideoCount));
        }
    }
}
