using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Proteus.App.Converters;

/// <summary>
/// Two-way bridge between an enum property and a RadioButton's IsChecked.
/// ConverterParameter is the enum value the button stands for.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value != null && parameter != null && value.Equals(parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : BindingOperations.DoNothing;
}

public sealed class EnumNotEqualsConverter : IValueConverter
{
    public static readonly EnumNotEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !(value != null && parameter != null && value.Equals(parameter));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
