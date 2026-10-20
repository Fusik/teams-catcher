using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TeamsCatcher.ViewModels;

namespace TeamsCatcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateWindowState();
    }

    MainViewModel Vm => (MainViewModel)DataContext;

    // Zamknięcie okna = schowanie do traya; aplikacja kończy się tylko z menu traya.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!((App)Application.Current).IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // Przy maksymalizacji okno bez systemowej ramki wychodzi poza ekran o szerokość ramki zmiany rozmiaru.
    void UpdateWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootBorder.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    void MaxRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void FpsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        Vm.Fps = (int)e.AddedItems[0]!;
        FpsToggle.IsChecked = false;
        FpsList.SelectedIndex = -1; // pozwala wybrać ten sam element ponownie
    }

    void ResolutionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        Vm.Resolution = (ResolutionOption)e.AddedItems[0]!;
        ResolutionToggle.IsChecked = false;
        ResolutionList.SelectedIndex = -1;
    }

    void SpeakersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        Vm.Speakers = (SpeakersOption)e.AddedItems[0]!;
        SpeakersToggle.IsChecked = false;
        SpeakersList.SelectedIndex = -1;
    }

    void RecordingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        Vm.SelectedRecording = (RecordingItem)e.AddedItems[0]!;
        RecordingToggle.IsChecked = false;
        RecordingList.SelectedIndex = -1; // pozwala wybrać ten sam element ponownie
    }

    void VideoDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void VideoDrop_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Vm.ImportVideos(files.Where(System.IO.File.Exists));
    }

    void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
