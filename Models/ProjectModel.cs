using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ProjectionMapper.Models
{
    /// <summary>
    /// Represents a top-level project containing surfaces and resources.
    /// Enhanced to include imported videos with their mesh layers and settings.
    /// </summary>
    public class ProjectModel
    {
        public string? Name { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        // Make surfaces observable for UI binding
        public ObservableCollection<SurfaceModel> Surfaces { get; } = new ObservableCollection<SurfaceModel>();

        // Imported videos collection for project persistence
        public List<ImportedVideoData> ImportedVideos { get; set; } = new List<ImportedVideoData>();

        // Global project settings
        public bool ShowMeshOverlay { get; set; } = true;
        public bool ShowCoordinateGrid { get; set; } = false;
        public double InputZoom { get; set; } = 1.0;
        public double OutputZoom { get; set; } = 1.0;
    }

    /// <summary>
    /// Serializable data for an imported video source with its configuration.
    /// </summary>
    public class ImportedVideoData
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public int TargetMonitorIndex { get; set; } = -1;
        public bool PlayAudio { get; set; } = false;
        public bool Visible { get; set; } = true;
        
        // Mesh layers associated with this imported video
        public List<MeshLayerData> MeshLayers { get; set; } = new List<MeshLayerData>();
    }

    /// <summary>
    /// Serializable data for a mesh layer.
    /// </summary>
    public class MeshLayerData
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Opacity { get; set; } = 1.0;
        public bool Visible { get; set; } = true;
        public double RotationDegrees { get; set; } = 0.0;
        public int TargetMonitorIndex { get; set; } = -1;
        public bool ShowOverlay { get; set; } = true;
        
        // Mesh points stored as flat arrays for JSON serialization
        public float[] MeshPoints { get; set; } = new float[8]; // 4 points * 2 coords (X,Y)
        public float[] OutputMeshPoints { get; set; } = new float[8]; // 4 points * 2 coords (X,Y)
    }
}