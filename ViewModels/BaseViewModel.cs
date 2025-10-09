using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// Base ViewModel with INotifyPropertyChanged implemented.
    /// Use this for simple MVVM binding scenarios.
    /// </summary>
    public abstract class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value)) return false;
            storage = value;
            RaisePropertyChanged(propertyName);
            return true;
        }
    }
}