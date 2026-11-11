using System.Globalization;
using System.Windows.Data;

namespace ArchiveBox.App;
public sealed class ViewEqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && Equals(values[0], values[1]);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
