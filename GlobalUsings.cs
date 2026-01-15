// Global using directives to resolve ambiguity between System.Windows and System.Windows.Forms types
// These ensure WPF types are used by default throughout the project

global using Point = System.Windows.Point;
global using Application = System.Windows.Application;
global using UserControl = System.Windows.Controls.UserControl;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Color = System.Windows.Media.Color;
global using MessageBox = System.Windows.MessageBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using Brushes = System.Windows.Media.Brushes;
