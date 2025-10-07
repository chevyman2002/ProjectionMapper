using System;
using System.Collections.ObjectModel;

namespace ProjectionMapper.Models
{
    /// <summary>
    /// Represents a top-level project containing surfaces and resources.
    /// Kept intentionally minimal for initial scaffolding.
    /// </summary>
    public class ProjectModel
    {
        public string? Name { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Make surfaces observable for UI binding
        public ObservableCollection<SurfaceModel> Surfaces { get; } = new ObservableCollection<SurfaceModel>();
    }
}