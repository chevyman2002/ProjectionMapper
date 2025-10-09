using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Abstraction for interacting with file dialogs.
    /// Allows easier unit testing by decoupling UI dialogs from ViewModels / services.
    /// </summary>
    public interface IFileDialogService
    {
        /// <summary>
        /// Show an Open File dialog and return the selected path or null if cancelled.
        /// </summary>
        Task<string?> ShowOpenFileDialogAsync(string title, string filter, bool multiselect = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Show a Save File dialog and return the selected path or null if cancelled.
        /// </summary>
        Task<string?> ShowSaveFileDialogAsync(string title, string defaultFileName, string filter, CancellationToken cancellationToken = default);
    }
}