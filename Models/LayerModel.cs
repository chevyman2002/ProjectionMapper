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
        /// If this layer is a mesh derived from an imported source, this links to the host source layer id.
        /// </summary>
        public string? SourceId { get; set; }

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
        /// Separate normalized mesh corner points for the output mapping.
        /// These control how the source (cropped or full) is mapped/warped onto the output surface.
        /// Kept independent from MeshPoints so input cropping and output mapping do not interfere.
        /// Order: TopLeft, TopRight, BottomLeft, BottomRight
        /// Defaults to a small centered rectangle (~1/5 of output area) for easier initial placement.
        /// </summary>
        public Vector2[] OutputMeshPoints { get; } = new[]
        {
            new Vector2(0.3f, 0.3f), // TL - centered, ~40% width/height
            new Vector2(0.7f, 0.3f), // TR
            new Vector2(0.3f, 0.7f), // BL
            new Vector2(0.7f, 0.7f)  // BR
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

        /// <summary>
        /// The index of the monitor (0-based) to which this layer's output should be sent. -1 = not assigned / use default.
        /// </summary>
        public int TargetMonitorIndex { get; set; } = -1;

        /// <summary>
        /// Whether to show the mesh overlay (quad outline and meshpoints) for this layer on the output.
        /// This is a UI preference stored per-layer and defaults to true.
        /// </summary>
        public bool ShowOverlay { get; set; } = true;

        /// <summary>
        /// When true, the layer's audio should be played alongside decoded video frames (if supported).
        /// This is a user preference stored on the model so services can resume/pause playback appropriately.
        /// </summary>
        public bool PlayAudio { get; set; } = false;

        /// <summary>
        /// Per-layer volume (0.0 = silent, 1.0 = original). Used by audio playback pipeline.
        /// </summary>
        public double Volume { get; set; } = 1.0;

        /// <summary>
        /// When true, audio playback for this layer is muted.
        /// </summary>
        public bool Muted { get; set; } = false;

        // Future: Add blend mode, transform, mask, timeline/keyframes, etc.
    }
}