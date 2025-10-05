using System.Windows;
using ProjectionMapper.ViewModels;

namespace ProjectionMapper
{
    /// <summary>
    /// Main window code-behind: sets DataContext to MainWindowViewModel.
    /// Keep code-behind minimal; wire View -> ViewModel interactions through bindings and commands.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainWindowViewModel();
            DataContext = _vm;
        }
    }
}