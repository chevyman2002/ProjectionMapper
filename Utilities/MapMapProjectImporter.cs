using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Utilities
{
    /// <summary>
    /// Skeleton importer for MapMap / ProjectionMapper-v1 project files.
    /// The importer will be responsible for reading the legacy format and mapping it to ProjectModel/SurfaceModel/LayerModel.
    /// This skeleton returns a default ProjectModel; implement parsing logic as a follow-up.
    /// </summary>
    public static class MapMapProjectImporter
    {
        public static Task<ProjectModel> ImportAsync(string path, CancellationToken token = default)
        {
            // TODO: parse the MapMap v1 project file (XML/JSON or custom format) and map to ProjectModel
            var project = new ProjectModel { Name = "Imported Project (placeholder)" };
            return Task.FromResult(project);
        }
    }
}