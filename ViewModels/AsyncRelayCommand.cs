using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// Lightweight async ICommand wrapper. Notifies CanExecute while running.
    /// Note: exceptions are not swallowed — callers should handle/log them or extend with a provided handler.
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> executeAsync) : this(_ => executeAsync(), null) { }

        public AsyncRelayCommand(Func<Task> executeAsync, Func<bool> canExecute) : this(_ => executeAsync(), canExecute is null ? null : new Predicate<object?>(_ => canExecute())) { }

        public AsyncRelayCommand(Func<object?, Task> executeAsync) : this(executeAsync, null) { }

        public AsyncRelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _executeAsync(parameter).ConfigureAwait(false);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}