using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using static System.Diagnostics.Debug;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// Thread-safe async ICommand wrapper that prevents deadlocks and infinite recursion.
    /// Designed specifically to handle cross-thread scenarios without blocking or excessive memory usage.
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Predicate<object?>? _canExecute;
        private int _isExecutingInt; // Use int for Interlocked operations
        private volatile bool _isRaisingCanExecuteChanged;

        public AsyncRelayCommand(Func<Task> executeAsync) : this(_ => executeAsync(), null) { }

        public AsyncRelayCommand(Func<Task> executeAsync, Func<bool> canExecute) : this(_ => executeAsync(), canExecute is null ? null : new Predicate<object?>(_ => canExecute())) { }

        public AsyncRelayCommand(Func<object?, Task> executeAsync) : this(executeAsync, null) { }

        public AsyncRelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _isExecutingInt == 0 && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            // Thread-safe check and set - if already executing (value is 1), return immediately
            if (Interlocked.CompareExchange(ref _isExecutingInt, 1, 0) != 0)
                return;

            try
            {
                RaiseCanExecuteChanged();
                
                // Execute the command - keep on original thread context to avoid cross-thread issues
                await _executeAsync(parameter);
            }
            catch (Exception ex)
            {
                // Log error but don't try to show UI - that causes the infinite loops and memory leaks
                WriteLine($"AsyncRelayCommand.Execute failed: {ex}");
                Debug.WriteLine($"AsyncRelayCommand operation failed: {ex.Message}");
            }
            finally
            {
                // Thread-safe reset - set back to 0 (not executing)
                Interlocked.Exchange(ref _isExecutingInt, 0);
                RaiseCanExecuteChanged();
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            // Prevent recursive calls that cause infinite loops and memory leaks
            if (_isRaisingCanExecuteChanged) return;
            
            try
            {
                _isRaisingCanExecuteChanged = true;
                
                // Raise the event directly - no dispatcher marshaling to prevent deadlocks
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // Swallow any exceptions from event handlers to prevent cascading failures
                Debug.WriteLine($"AsyncRelayCommand.RaiseCanExecuteChanged failed: {ex}");
            }
            finally
            {
                _isRaisingCanExecuteChanged = false;
            }
        }
    }
}