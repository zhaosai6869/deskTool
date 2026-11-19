using System;
using System.Globalization;
using System.Windows.Data;

namespace DeskApp;

/// <summary>登录状态布尔值 → 「已登录 / 未登录」文字。</summary>
public sealed class BoolToLoginTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? "已登录" : "未登录";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
