using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProjectionMapper.Converters
{
    /// <summary>
    /// Converts a boolean value to Visibility. True = Visible, False = Collapsed.
    /// Use ConverterParameter="Invert" to reverse the logic.
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
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

            // Check for invert parameter
            bool invert = parameter is string p && 
                          p.Equals("Invert", StringComparison.OrdinalIgnoreCase);

            if (invert)
            {
                boolValue = !boolValue;
            }

            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility v)
            {
                bool result = v == Visibility.Visible;
                
                // Check for invert parameter
                bool invert = parameter is string p && 
                              p.Equals("Invert", StringComparison.OrdinalIgnoreCase);

                if (invert)
                {
                    result = !result;
                }

                return result;
            }

            return false;
        }
    }
}
