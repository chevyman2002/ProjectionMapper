using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Handles persistence and import of ProjectModel objects.
    /// </summary>
    public interface IProjectService
    {
        /// <summary>
        /// Load a project from disk. Returns null if load fails.
        /// </summary>
        Task<ProjectModel?> LoadAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Save the given project to disk. Returns true on success.
        /// </summary>
        Task<bool> SaveAsync(ProjectModel project, string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Import a legacy MapMap/ProjectionMapper-v1 project file and return the converted ProjectModel.
        /// </summary>
        Task<ProjectModel?> ImportLegacyAsync(string legacyPath, CancellationToken cancellationToken = default);
    }
}