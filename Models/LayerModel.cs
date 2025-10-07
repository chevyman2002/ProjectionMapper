using System;
using System.Numerics;

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

        /// <summary>
        /// Normalized mesh corner points for this layer in source coordinates.
        /// Order: TopLeft, TopRight, BottomLeft, BottomRight
        /// Values are normalized (0..1) relative to source dimensions.
        /// Defaults to full-rect.
        /// </summary>
        public Vector2[] MeshPoints { get; } = new[]
        {
            new Vector2(0f, 0f), // TL
            new Vector2(1f, 0f), // TR
            new Vector2(0f, 1f), // BL
            new Vector2(1f, 1f)  // BR
        };

        /// <summary>
        /// Rotation in degrees to apply to the source when rendering. Rotation is clockwise around the layer center.
        /// </summary>
        public double RotationDegrees { get; set; } = 0.0;

        /// <summary>
        /// When true, decoded frames for this layer are intended only for isolated preview and should not be
        /// submitted to the main renderer for composition (i.e. they should not appear in the output preview).
        /// </summary>
        public bool PreviewOnly { get; set; } = false;

        // Future: Add blend mode, transform, mask, timeline/keyframes, etc.
    }
}