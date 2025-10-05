using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System;
using System.Windows;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// Simple implementation of IFileDialogService using WPF's OpenFileDialog / SaveFileDialog.
    /// Note: calls must be made from the UI thread (dialogs require STA).
    /// For unit tests, mock IFileDialogService instead of this concrete class.
    /// </summary>
    public sealed class FileDialogService : IFileDialogService
    {
        public Task<string?> ShowOpenFileDialogAsync(string title, string filter, bool multiselect = false, CancellationToken cancellationToken = default)
        {
            // Dialogs must be shown on STA/UI thread; this method assumes caller is already UI-threaded.
            var tcs = new TaskCompletionSource<string?>();
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = title ?? "Open",
                    Filter = filter ?? "All files (*.*)|*.*",
                    Multiselect = multiselect
                };

                bool? result = dlg.ShowDialog(Application.Current?.MainWindow);
                if (result == true)
                {
                    // If multiselect, return first path (ViewModels can be adapted to accept multiple)
                    var path = multiselect ? (dlg.FileNames.Length > 0 ? dlg.FileNames[0] : null) : dlg.FileName;
                    tcs.SetResult(path);
                }
                else
                {
                    tcs.SetResult(null);
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }

            return tcs.Task;
        }

        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultFileName, string filter, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<string?>();
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = title ?? "Save",
                    FileName = defaultFileName ?? string.Empty,
                    Filter = filter ?? "All files (*.*)|*.*"
                };

                bool? result = dlg.ShowDialog(Application.Current?.MainWindow);
                if (result == true)
                {
                    tcs.SetResult(dlg.FileName);
                }
                else
                {
                    tcs.SetResult(null);
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }

            return tcs.Task;
        }
    }
}