using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// JSON-based implementation of project persistence.
    /// Uses System.Text.Json for simplicity and version tolerance.
    /// Delegates legacy import logic to MapMapProjectImporter.
    /// </summary>
    public sealed class ProjectService : IProjectService
    {
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public async Task<ProjectModel?> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            var project = await JsonSerializer.DeserializeAsync<ProjectModel>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            return project;
        }

        public async Task<bool> SaveAsync(ProjectModel project, string path, CancellationToken cancellationToken = default)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, project, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        public async Task<ProjectModel?> ImportLegacyAsync(string legacyPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(legacyPath)) throw new ArgumentNullException(nameof(legacyPath));
            // Delegate to MapMapProjectImporter which contains parsing logic.
            var imported = await MapMapProjectImporter.ImportAsync(legacyPath, cancellationToken).ConfigureAwait(false);
            return imported;
        }
    }
}