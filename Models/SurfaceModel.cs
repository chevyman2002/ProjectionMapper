using System;
using System.Collections.Generic;
using System.Numerics;

namespace ProjectionMapper.Models
{
    /// <summary>
    /// Represents a "surface" that can be mapped to a projector region.
    /// This skeleton captures basic transform metadata and layer list.
    /// </summary>
    public class SurfaceModel
    {
        public string? Id { get; set; }
        public string? Name { get; set; }

        // Normalized width/height for the surface in project coordinates.
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;

        // Per-surface layers (ordered front-to-back)
        public List<LayerModel> Layers { get; } = new List<LayerModel>();

        // Placeholder matrix describing transform from surface-local to output coordinates
        public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;
    }
}