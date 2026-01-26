using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ProjectionMapper.Models;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// Base class for all tree items in the project tree (groups, videos, meshes).
    /// Provides common properties like Name, Id, and parent tracking.
    /// </summary>
    public abstract class ProjectTreeItemViewModel : BaseViewModel
    {
        private string? _name;
        private bool _isSelected;
        private bool _isExpanded;

        public abstract string? Id { get; }

        public virtual string? Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        /// <summary>
        /// Gets the type of tree item for template selection.
        /// </summary>
        public abstract ProjectTreeItemType ItemType { get; }
    }

    /// <summary>
    /// Represents a playlist group in the tree. Contains videos as children.
    /// </summary>
    public class PlaylistGroupTreeViewModel : ProjectTreeItemViewModel
    {
        private readonly PlaylistGroupModel _model;
        private bool _isActive;

        public PlaylistGroupTreeViewModel(PlaylistGroupModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            Name = model.Name;
            Videos = new ObservableCollection<ImportedVideoTreeViewModel>();
        }

        public PlaylistGroupModel Model => _model;

        public override string? Id => _model.Id;

        public override ProjectTreeItemType ItemType => ProjectTreeItemType.PlaylistGroup;

        /// <summary>
        /// Order/sequence of this group in the playlist (0-based).
        /// </summary>
        public int Order
        {
            get => _model.Order;
            set
            {
                if (_model.Order != value)
                {
                    _model.Order = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>
        /// Whether this group is currently active/playing.
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        /// <summary>
        /// Videos in this group.
        /// </summary>
        public ObservableCollection<ImportedVideoTreeViewModel> Videos { get; }

        /// <summary>
        /// Number of videos in this group.
        /// </summary>
        public int VideoCount => Videos.Count;


        /// <summary>
        /// Display text showing video count.
        /// </summary>
        public string DisplayText => $"{Name} ({VideoCount} video{(VideoCount != 1 ? "s" : "")})";

        /// <summary>
        /// Gets or sets how videos within this group are played.
        /// </summary>
        public GroupPlaybackMode PlaybackMode
        {
            get => _model.PlaybackMode;
            set
            {
                if (_model.PlaybackMode != value)
                {
                    _model.PlaybackMode = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(IsSequentialMode));
                    RaisePropertyChanged(nameof(IsSimultaneousMode));
                }
            }
        }

        /// <summary>
        /// Gets whether the group is in sequential playback mode.
        /// </summary>
        public bool IsSequentialMode => _model.PlaybackMode == GroupPlaybackMode.Sequential;

        /// <summary>
        /// Gets whether the group is in simultaneous playback mode.
        /// </summary>
        public bool IsSimultaneousMode => _model.PlaybackMode == GroupPlaybackMode.Simultaneous;

        /// <summary>
        /// Refreshes the display text when video count changes.
        /// </summary>
        public void RefreshDisplayText()
        {
            RaisePropertyChanged(nameof(VideoCount));
            RaisePropertyChanged(nameof(DisplayText));
        }

        /// <summary>
        /// Adds a video to this group.
        /// </summary>
        public void AddVideo(ImportedVideoTreeViewModel video)
        {
            if (video == null) throw new ArgumentNullException(nameof(video));

            // Remove from current parent group if any
            video.ParentGroup?.RemoveVideo(video);

            // Add to this group
            if (!Videos.Contains(video))
            {
                Videos.Add(video);
                video.ParentGroup = this;

                // Update model
                var sourceId = video.Id;
                if (!string.IsNullOrEmpty(sourceId) && !_model.SourceIds.Contains(sourceId))
                {
                    _model.SourceIds.Add(sourceId);
                }

                RefreshDisplayText();
            }
        }

        /// <summary>
        /// Removes a video from this group.
        /// </summary>
        public void RemoveVideo(ImportedVideoTreeViewModel video)
        {
            if (video == null) throw new ArgumentNullException(nameof(video));

            if (Videos.Remove(video))
            {
                video.ParentGroup = null;

                // Update model
                var sourceId = video.Id;
                if (!string.IsNullOrEmpty(sourceId))
                {
                    _model.SourceIds.Remove(sourceId);
                }

                RefreshDisplayText();
            }
        }
    }

    /// <summary>
    /// Represents an imported video in the tree. Can be in a group or unassigned.
    /// Contains mesh layers as children.
    /// </summary>
    public class ImportedVideoTreeViewModel : ProjectTreeItemViewModel
    {
        private readonly ImportedVideoViewModel _importedVideo;
        private PlaylistGroupTreeViewModel? _parentGroup;

        public ImportedVideoTreeViewModel(ImportedVideoViewModel importedVideo)
        {
            _importedVideo = importedVideo ?? throw new ArgumentNullException(nameof(importedVideo));
            Name = importedVideo.Name;

            // Forward property changes from underlying ImportedVideoViewModel
            importedVideo.PropertyChanged += OnImportedVideoPropertyChanged;
        }

        public ImportedVideoViewModel ImportedVideo => _importedVideo;

        public override string? Id => _importedVideo.Id;

        public override ProjectTreeItemType ItemType => ProjectTreeItemType.ImportedVideo;

        /// <summary>
        /// The group this video belongs to, or null if unassigned.
        /// </summary>
        public PlaylistGroupTreeViewModel? ParentGroup
        {
            get => _parentGroup;
            set => SetProperty(ref _parentGroup, value);
        }

        /// <summary>
        /// Whether this video is unassigned (not in any group).
        /// </summary>
        public bool IsUnassigned => _parentGroup == null;

        /// <summary>
        /// Mesh layers for this video.
        /// </summary>
        public ObservableCollection<LayerViewModel> MeshLayers => _importedVideo.MeshLayers;

        /// <summary>
        /// Host layer for this video.
        /// </summary>
        public LayerModel HostLayer => _importedVideo.HostLayer;

        /// <summary>
        /// Whether audio is enabled for this video.
        /// </summary>
        public bool PlayAudio
        {
            get => _importedVideo.PlayAudio;
            set => _importedVideo.PlayAudio = value;
        }

        /// <summary>
        /// Source file path.
        /// </summary>
        public string SourcePath => _importedVideo.SourcePath;

        /// <summary>
        /// Display text with audio indicator.
        /// </summary>
        public string DisplayText => $"{Name}{(PlayAudio ? " ??" : "")}";

        private void OnImportedVideoPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Forward relevant property changes
            if (e.PropertyName == nameof(ImportedVideoViewModel.Name))
            {
                RaisePropertyChanged(nameof(Name));
                RaisePropertyChanged(nameof(DisplayText));
            }
            else if (e.PropertyName == nameof(ImportedVideoViewModel.PlayAudio))
            {
                RaisePropertyChanged(nameof(PlayAudio));
                RaisePropertyChanged(nameof(DisplayText));
            }
        }
    }

    /// <summary>
    /// Types of items that can appear in the project tree.
    /// </summary>
    public enum ProjectTreeItemType
    {
        PlaylistGroup,
        ImportedVideo,
        MeshLayer
    }
}
