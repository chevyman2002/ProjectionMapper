using System;

namespace ProjectionMapper.Models
{
    /// <summary>
    /// Represents a single layer inside a surface.
    /// Mirrors the properties used by LayerViewModel. Includes mapping bounds for output composition.
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

        /// <summary>
        /// Mapping rectangle (in surface coordinates) where this layer's content should be drawn.
        /// X,Y are the top-left coordinates; Width/Height define the bounding box.
        /// Defaults to 0,0,0,0 — set after creating the layer.
        /// </summary>
        public int X { get; set; } = 0;
        public int Y { get; set; } = 0;
        public int Width { get; set; } = 0;
        public int Height { get; set; } = 0;

        // Future: Add blend mode, transform, mask, timeline/keyframes, etc.
    }
}