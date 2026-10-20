using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace TeamsCatcher;

// Powiadomienie z notatką częściową: prawy dolny róg ekranu, kolejne nad poprzednimi, znika po minucie (nie pod kursorem).
public partial class NoteToast : Window
{
    static readonly List<NoteToast> Open = [];
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    readonly string _file;
    readonly DispatcherTimer _timer = new() { Interval = Lifetime };

    NoteToast(string title, string recording, IReadOnlyList<(string Heading, List<string> Points)> sections, string file)
    {
        InitializeComponent();
        _file = file;
        TitleText.Text = title;
        RecordingText.Text = recording;
        foreach (var (heading, points) in sections)
        {
            Body.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("WF.Brush.Cyan"), Margin = new Thickness(0, 4, 0, 2) });
            foreach (var point in points)
                Body.Children.Add(new TextBlock { Text = "•  " + point, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 0, 2) });
        }
        _timer.Tick += (_, _) => { if (!IsMouseOver) Close(); };
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();
        Loaded += (_, _) => Arrange();
        Closed += (_, _) => { _timer.Stop(); Open.Remove(this); Arrange(); };
    }

    // Sekcje z punktami z notatki Markdown ("## Nagłówek", "- punkt"); bez pustych ("brak") - pusty fragment = bez powiadomienia.
    public static void Show(string title, string recording, string notes, string file)
    {
        var sections = new List<(string Heading, List<string> Points)>();
        foreach (var raw in notes.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ")) sections.Add((line[3..], []));
            else if (line.StartsWith("- ") && sections.Count > 0 && line is not ("- brak" or "- none")) sections[^1].Points.Add(line[2..].Replace("[ ] ", ""));
        }
        sections.RemoveAll(s => s.Points.Count == 0);
        if (sections.Count == 0) return;
        // poza ekranem do pierwszego ułożenia (wysokość znana dopiero po załadowaniu)
        var toast = new NoteToast(title, recording, sections, file) { Left = SystemParameters.WorkArea.Right, Top = SystemParameters.WorkArea.Bottom };
        Open.Add(toast);
        toast.Show();
        toast._timer.Start();
    }

    // Od dołu obszaru roboczego (nad paskiem zadań) w górę; najstarsze najniżej.
    static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 8;
        foreach (var toast in Open)
        {
            toast.Left = area.Right - toast.Width - 12;
            toast.Top = Math.Max(area.Top, bottom - toast.ActualHeight);
            bottom = toast.Top;
        }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_file)) Process.Start(new ProcessStartInfo(_file) { UseShellExecute = true });
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _timer.Stop(); // kliknięcie = czytam, nie zamykaj
    }
}
