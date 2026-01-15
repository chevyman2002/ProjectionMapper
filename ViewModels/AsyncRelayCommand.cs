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
    public sealed class AsyncRelayCommand : ICommand, IDisposable
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Predicate<object?>? _canExecute;
        private int _isExecutingInt; // Use int for Interlocked operations
        private volatile bool _isRaisingCanExecuteChanged;
        private volatile bool _isDisposed;

        public AsyncRelayCommand(Func<Task> executeAsync) : this(_ => executeAsync(), null) { }

        public AsyncRelayCommand(Func<Task> executeAsync, Func<bool> canExecute) : this(_ => executeAsync(), canExecute is null ? null : new Predicate<object?>(_ => canExecute())) { }

        public AsyncRelayCommand(Func<object?, Task> executeAsync) : this(executeAsync, null) { }

        public AsyncRelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) 
        {
            if (_isDisposed) return false;
            
            try
            {
                return _isExecutingInt == 0 && (_canExecute?.Invoke(parameter) ?? true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AsyncRelayCommand.CanExecute failed: {ex}");
                return false; // Safer to return false on error
            }
        }

        public async void Execute(object? parameter)
        {
            if (_isDisposed) return;

            // Thread-safe check and set - if already executing (value is 1), return immediately
            if (Interlocked.CompareExchange(ref _isExecutingInt, 1, 0) != 0)
            {
                Debug.WriteLine("AsyncRelayCommand.Execute: Already executing, skipping");
                return;
            }

            try
            {
                RaiseCanExecuteChanged();
                
                // Additional null check and disposal check before execution
                if (_executeAsync == null || _isDisposed)
                {
                    Debug.WriteLine("AsyncRelayCommand.Execute: Command is null or disposed");
                    return;
                }
                
                // Execute the command - keep on original thread context to avoid cross-thread issues
                // Use ConfigureAwait(false) to prevent potential deadlocks
                await _executeAsync(parameter).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Expected if command is disposed during execution
                Debug.WriteLine("AsyncRelayCommand.Execute: Command was disposed during execution");
            }
            catch (OperationCanceledException)
            {
                // Expected if operation is cancelled
                Debug.WriteLine("AsyncRelayCommand.Execute: Operation was cancelled");
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
                
                // Only raise if not disposed
                if (!_isDisposed)
                {
                    RaiseCanExecuteChanged();
                }
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            if (_isDisposed) return;
            
            // Prevent recursive calls that cause infinite loops and memory leaks
            if (_isRaisingCanExecuteChanged)
            {
                Debug.WriteLine("AsyncRelayCommand.RaiseCanExecuteChanged: Already raising, preventing recursion");
                return;
            }
            
            try
            {
                _isRaisingCanExecuteChanged = true;
                
                // Marshal to UI thread to prevent cross-thread exceptions
                var app = System.Windows.Application.Current;
                if (app != null && app.Dispatcher != null && !app.Dispatcher.HasShutdownStarted && !app.Dispatcher.HasShutdownFinished)
                {
                    if (!app.Dispatcher.CheckAccess())
                    {
                        // We're on a background thread, marshal to UI thread
                        app.Dispatcher.BeginInvoke((Action)(() =>
                        {
                            try
                            {
                                var handler = CanExecuteChanged;
                                if (handler != null && !_isDisposed)
                                {
                                    handler.Invoke(this, EventArgs.Empty);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"AsyncRelayCommand.RaiseCanExecuteChanged (dispatched) failed: {ex}");
                            }
                        }));
                        return;
                    }
                }
                
                // Raise the event directly - we're on the UI thread
                var handlerDirect = CanExecuteChanged;
                if (handlerDirect != null && !_isDisposed)
                {
                    handlerDirect.Invoke(this, EventArgs.Empty);
                }
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

        public void Dispose()
        {
            if (_isDisposed) return;
            
            _isDisposed = true;
            
            try
            {
                // Clear event handlers to prevent memory leaks
                CanExecuteChanged = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AsyncRelayCommand.Dispose failed: {ex}");
            }
        }
    }
}