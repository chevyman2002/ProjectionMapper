using System;

namespace ProjectionMapper.Models
{
    /// <summary>
    /// Represents a single layer inside a surface.
    /// Mirrors the properties used by LayerViewModel.
    /// </summary>
    public class LayerModel
    {
        /// <summary>
        /// Unique identifier for the layer.
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Display name for the layer.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Path to the source media (image/video) for this layer.
        /// </summary>
        public string? SourcePath { get; set; }

        /// <summary>
        /// Layer opacity (0.0 = transparent, 1.0 = opaque).
        /// </summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>
        /// Whether the layer is visible.
        /// </summary>
        public bool Visible { get; set; } = true;

        // Future: Add blend mode, transform, mask, timeline/keyframes, etc.
    }
}