using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TeamsCatcher;

// Okno pobierania składników; zamknięcie = schowanie (pobieranie trwa dalej w SetupViewModel).
public partial class SetupWindow : Window
{
    public SetupWindow() => InitializeComponent();

    void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}

// Negacja bool w bindingu (składnik gotowy = bez zaznaczania).
public sealed class Not : IValueConverter
{
    public static readonly Not Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
