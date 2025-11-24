using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace ProjectionMapper.Views
{
    /// <summary>
    /// About dialog showing application information, author details, and icon.
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            LoadApplicationInfo();
        }

        private void LoadApplicationInfo()
        {
            try
            {
                // Load the application icon
                try
                {
                    var iconUri = new Uri("pack://application:,,,/ProjectionMapper.ico");
                    var iconSource = new BitmapImage(iconUri);
                    PART_AppIcon.Source = iconSource;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load application icon: {ex.Message}");
                    // Hide the image if icon fails to load
                    PART_AppIcon.Visibility = Visibility.Collapsed;
                }

                // Get version from assembly
                try
                {
                    var version = Assembly.GetExecutingAssembly().GetName().Version;
                    if (version != null)
                    {
                        PART_VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to get assembly version: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadApplicationInfo failed: {ex}");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CloseButton_Click failed: {ex}");
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                // Open the URL in the default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open hyperlink: {ex}");
                MessageBox.Show(
                    $"Could not open link: {e.Uri.AbsoluteUri}\n\nError: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
