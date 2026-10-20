using System.ComponentModel;
using System.Runtime.InteropServices;
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

    // Okno bez systemowej ramki po maksymalizacji zakrywało pasek zadań (1934x1094 przy obszarze roboczym 1920x1040) - dół (log, Open)
    // był pod paskiem. WM_GETMINMAXINFO: maksymalny rozmiar i pozycja = obszar roboczy monitora, na którym jest okno.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle).AddHook(WndProc);
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024, MonitorDefaultToNearest = 2;
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxPosition = new Point32 { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        mmi.MaxSize = new Point32 { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] struct Point32 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public Point32 Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect32 Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    void UpdateWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
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

    void LanguageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        Vm.TranscriptLanguage = (string)e.AddedItems[0]!;
        LanguageToggle.IsChecked = false;
        LanguageList.SelectedIndex = -1;
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
