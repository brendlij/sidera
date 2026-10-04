using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Sidera.Desktop.Converters;

/// <summary>A heading in the spaced capitals of an eyebrow.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public static UpperCaseConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(CultureInfo.CurrentCulture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
