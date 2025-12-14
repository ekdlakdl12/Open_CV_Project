using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WpfApp1.Views
{
    // bool 값을 Visibility 값으로 변환합니다. (True -> Visible, False -> Collapsed)
    // ProgressBar의 IsIndeterminate={Binding IsProcessing} 바인딩에 사용됩니다.
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool booleanValue)
            {
                // parameter가 "Inverse"인 경우 반전
                if (parameter?.ToString() == "Inverse")
                {
                    booleanValue = !booleanValue;
                }
                return booleanValue ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}