using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProjectionMapper.Converters
{
    /// <summary>
    /// Converts a boolean value to Visibility with inverted logic. 
    /// True = Collapsed, False = Visible.
    /// </summary>
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = false;
            
            if (value is bool b)
            {
                boolValue = b;
            }
            else if (value != null)
            {
                // Try to parse as string
                bool.TryParse(value.ToString(), out boolValue);
            }

            // Inverted: true -> Collapsed, false -> Visible
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility v)
            {
                // Inverted: Visible -> false, Collapsed/Hidden -> true
                return v != Visibility.Visible;
            }

            return true;
        }
    }
}
