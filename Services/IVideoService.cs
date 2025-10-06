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
    }
}