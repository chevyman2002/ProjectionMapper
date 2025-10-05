using System;
using System.Collections.Generic;

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

        public List<SurfaceModel> Surfaces { get; } = new List<SurfaceModel>();
    }
}