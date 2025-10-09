using System.Windows;
using System.Windows.Input;

namespace ProjectionMapper.Views
{
    public partial class FullScreenOutputWindow : Window
    {
        public FullScreenOutputWindow()
        {
            InitializeComponent();
        }

        public RenderHostControl HostControl => PART_OutputHost;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }
    }
}
