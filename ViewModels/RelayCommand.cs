using System;
using System.Windows.Input;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// Simple, synchronous ICommand implementation for MVVM binding.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action execute) : this(_ => execute(), null) { }

        public RelayCommand(Action execute, Func<bool> canExecute) : this(_ => execute(), canExecute is null ? null : new Predicate<object?>(_ => canExecute())) { }

        public RelayCommand(Action<object?> execute) : this(execute, null) { }

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}