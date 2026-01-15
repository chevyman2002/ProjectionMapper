using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Manages video decoding instances for project layers.
    /// </summary>
    public interface IVideoService
    {
        /// <summary>
        /// Register a layer with an associated video source and start decoding.
        /// Returns true if decoder successfully started.
        /// </summary>
        Task<bool> RegisterLayerAsync(LayerModel layer);

        /// <summary>
        /// Unregister and stop decoding for a layer.
        /// </summary>
        Task UnregisterLayerAsync(string layerId);

        /// <summary>
        /// Forces a refresh of rendering for a specific mesh layer using the cached last frame from its source.
        /// This is useful when mesh points are edited while video is paused.
        /// </summary>
        void RefreshMeshLayerRendering(string meshLayerId);

        /// <summary>
        /// Forces a refresh of rendering for all mesh layers of a given source.
        /// </summary>
        void RefreshAllMeshLayersForSource(string sourceId);
    }
}