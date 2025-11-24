using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjectionMapper.Models;
using ProjectionMapper.Utilities;
using System.Diagnostics;

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
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public async Task<ProjectModel?> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
                if (!File.Exists(path))
                {
                    Debug.WriteLine($"ProjectService.LoadAsync: File not found: {path}");
                    return null;
                }

                using var stream = File.OpenRead(path);
                var project = await JsonSerializer.DeserializeAsync<ProjectModel>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                
                if (project != null)
                {
                    Debug.WriteLine($"ProjectService.LoadAsync: Successfully loaded project '{project.Name}' with {project.ImportedVideos?.Count ?? 0} imported videos");
                }
                
                return project;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProjectService.LoadAsync failed: {ex}");
                return null;
            }
        }

        public async Task<bool> SaveAsync(ProjectModel project, string path, CancellationToken cancellationToken = default)
        {
            try
            {
                if (project == null) throw new ArgumentNullException(nameof(project));
                if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));

                // Update last modified timestamp
                project.LastModified = DateTime.UtcNow;

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = File.Create(path);
                await JsonSerializer.SerializeAsync(stream, project, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                
                Debug.WriteLine($"ProjectService.SaveAsync: Successfully saved project to {path}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProjectService.SaveAsync failed: {ex}");
                return false;
            }
        }

        public async Task<ProjectModel?> ImportLegacyAsync(string legacyPath, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(legacyPath)) throw new ArgumentNullException(nameof(legacyPath));
                // Delegate to MapMapProjectImporter which contains parsing logic.
                var imported = await MapMapProjectImporter.ImportAsync(legacyPath, cancellationToken).ConfigureAwait(false);
                return imported;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProjectService.ImportLegacyAsync failed: {ex}");
                return null;
            }
        }
    }
}