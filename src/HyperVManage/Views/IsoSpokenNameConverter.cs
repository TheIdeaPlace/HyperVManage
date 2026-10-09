using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace HyperVManage.Views;

/// <summary>An ISO in the list as a screen reader says it: "Win11_25H2_Arm64.iso, in C:\Users\kelly\Downloads".
/// Anything that isn't a path, such as Browse for an ISO, is said as it is.</summary>
public sealed class IsoSpokenNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Speak(value as string ?? "");

    internal static string Speak(string item)
    {
        if (!Path.IsPathFullyQualified(item)) return item;
        var folder = Path.GetDirectoryName(item);
        return string.IsNullOrEmpty(folder) ? item : $"{Path.GetFileName(item)}, in {folder}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
