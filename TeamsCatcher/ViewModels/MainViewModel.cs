using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TeamsCatcher.Core;
using TeamsCatcher.Infrastructure;

namespace TeamsCatcher.ViewModels;

public sealed record RecordingItem(string Name, string Folder, string Details);

// Niejasne słowo z nagrania do objaśnienia przez użytkownika (panel "TO CLARIFY").
public sealed class QuestionItem(UnclearTerm term, string folder) : ObservableObject
{
    string _meaning = "";
    public UnclearTerm Term { get; } = term;
    public string Folder { get; } = folder;
    public string Recording => Path.GetFileName(Folder);
    public string Meaning { get => _meaning; set => Set(ref _meaning, value); }
}

public sealed record SpeakersOption(int Count)
{
    public override string ToString() => Count == 0 ? "auto" : Count.ToString();
}

public sealed record ResolutionOption(int Height)
{
    public override string ToString() => $"{Height}p";
}

public sealed class MainViewModel : ObservableObject
{
    const int MaxLogLines = 500;

    readonly Settings _settings;
    readonly string _settingsPath;
    readonly CatcherService _service;
    readonly Queue<string> _logLines = new();
    bool _wasBusy;
    string? _lastJob;
    string _log = "";
    string _teamsStatus = "";
    string _state = "";
    Brush _stateBrush = Brushes.Gray;
    string _windowTitle = "-";
    string _elapsed = "-";
    string _freeSpace = "-";
    bool _isRecording;
    bool _isFinalizing;

    public MainViewModel(Settings settings, string settingsPath, CatcherService service)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _service = service;
        ToggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync, () => !IsFinalizing);
        BrowseCommand = new RelayCommand(Browse);
        OpenOutputFolderCommand = new RelayCommand(() => OpenFolder(_settings.OutputFolder));
        OpenRecordingCommand = new RelayCommand<RecordingItem>(r => OpenFolder(r.Folder));
        TranscribeCommand = new RelayCommand<RecordingItem>(r => _service.QueueTranscription(r.Folder));
        // "Save and recalculate" (pod pytaniami) i "Redo after clarification" (panel notatki) robią to samo dla wybranego nagrania
        RedoCommand = new RelayCommand(Redo, () => SelectedRecording != null);
        SaveKnowledgeCommand = RedoCommand;
        OpenNotesCommand = new RelayCommand(() => { if (NotesPath() is { } p) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); });
        ImportVideoCommand = new RelayCommand(ChooseVideo);
        SaveRulesCommand = new RelayCommand(SaveRules);
        OpenRulesCommand = new RelayCommand(() => { if (File.Exists(MeetingNotes.RulesPath)) Process.Start(new ProcessStartInfo(MeetingNotes.RulesPath) { UseShellExecute = true }); });
        SaveKnowledgeTextCommand = new RelayCommand(SaveKnowledgeText);
        try { _notesRules = File.Exists(MeetingNotes.RulesPath) ? File.ReadAllText(MeetingNotes.RulesPath) : ""; }
        catch (IOException e) { AddLog($"Notes rules not read: {e.Message}"); }
        _knowledgeText = KnowledgeBase.Format(KnowledgeBase.Load(KnowledgeBase.DefaultPath).Entries);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
        LoadRecordings();
    }

    public string UserName { get; } = Environment.UserName;
    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    public ObservableCollection<QuestionItem> Questions { get; } = [];
    public bool HasQuestions => Questions.Count > 0;
    string _bulkKnowledge = "";
    // objaśnienia wpisane naraz: "[diamentowy golem - jednostka z gry], [konsa - consa - budynek z gry]"
    public string BulkKnowledge { get => _bulkKnowledge; set => Set(ref _bulkKnowledge, value); }
    public RelayCommand SaveKnowledgeCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand OpenNotesCommand { get; }

    // Wybrane nagranie: jego niejasne słowa (TO CLARIFY) i notatka w panelu bocznym.
    RecordingItem? _selectedRecording;
    public RecordingItem? SelectedRecording
    {
        get => _selectedRecording;
        set
        {
            if (!Set(ref _selectedRecording, value)) return;
            LoadQuestions();
            LoadNotes();
        }
    }

    string _notes = "";
    public string Notes { get => _notes; private set => Set(ref _notes, value); }

    // Co jest teraz analizowane: nagranie (albo jego kawałek na żywo) i ostatni krok z logu.
    string _analyzing = "";
    public string Analyzing { get => _analyzing; private set => Set(ref _analyzing, value); }
    string _lastStep = "";

    string? NotesPath() => SelectedRecording == null ? null
        : new[] { "pl", "en" }.Select(l => Path.Combine(SelectedRecording.Folder, MeetingNotes.FileName(l))).FirstOrDefault(File.Exists);

    void LoadNotes()
    {
        try { Notes = NotesPath() is { } p ? File.ReadAllText(p) : SelectedRecording == null ? "" : "No notes yet for this recording."; }
        catch (IOException) { } // plik zapisywany w tej chwili - następne odświeżenie
    }

    // Po objaśnieniu niejasnych słów: zapis do bazy wiedzy i analiza od nowa (dyskusja, tłumaczenie, notatki) z tym kontekstem, bez Whispera.
    void Redo()
    {
        if (SelectedRecording is not { } recording) return;
        SaveKnowledge(recalculate: false);
        _service.QueueTranscription(recording.Folder, fromState: Transcriber.LoadState(recording.Folder) != null);
        AddLog($"Redo after clarification: {recording.Name}");
    }
    public int[] FpsOptions { get; } = [5, 15, 30];
    public ResolutionOption[] ResolutionOptions { get; } = [new(1080), new(1440), new(2160)];
    public SpeakersOption[] SpeakersOptions { get; } = [new(0), new(1), new(2), new(3), new(4), new(5), new(6)];

    public AsyncRelayCommand ToggleRecordingCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand<RecordingItem> OpenRecordingCommand { get; }
    public RelayCommand<RecordingItem> TranscribeCommand { get; }
    public RelayCommand ImportVideoCommand { get; }

    // --- zakładka MODELS: wszystko, co zmienia pracę modeli tekstu ---
    public RelayCommand SaveRulesCommand { get; }
    public RelayCommand OpenRulesCommand { get; }
    public RelayCommand SaveKnowledgeTextCommand { get; }
    string _notesRules = "", _knowledgeText = "", _modelsStatus = "";
    // notes-rules.md: zasady, jak powstają notatki (wnioski, decyzje...) - trafiają do każdego zapytania o notatkę
    public string NotesRules { get => _notesRules; set => Set(ref _notesRules, value); }
    // knowledge.json jako tekst: jedna linia na wpis "[termin - inny zapis - znaczenie]"
    public string KnowledgeText { get => _knowledgeText; set => Set(ref _knowledgeText, value); }
    public string ModelsStatus { get => _modelsStatus; private set => Set(ref _modelsStatus, value); }
    public string[] LanguageOptions { get; } = ["auto", "pl", "en"];

    public string CorrectionModel
    {
        get => _settings.CorrectionModel;
        set { if (value.Trim().Length > 0) { _settings.CorrectionModel = value.Trim(); SaveSettings(); } OnPropertyChanged(); }
    }

    public string CheckModel
    {
        get => _settings.CheckModel;
        set { if (value.Trim().Length > 0) { _settings.CheckModel = value.Trim(); SaveSettings(); } OnPropertyChanged(); }
    }

    public int DebateRounds
    {
        get => _settings.DebateRounds;
        set { _settings.DebateRounds = Math.Clamp(value, 1, 6); SaveSettings(); OnPropertyChanged(); }
    }

    public int VramBudgetMb
    {
        get => _settings.VramBudgetMb;
        set { _settings.VramBudgetMb = Math.Max(0, value); SaveSettings(); OnPropertyChanged(); }
    }

    public string TranscriptLanguage
    {
        get => _settings.TranscriptLanguage;
        set { _settings.TranscriptLanguage = value; SaveSettings(); OnPropertyChanged(); }
    }

    void SaveRules()
    {
        try
        {
            File.WriteAllText(MeetingNotes.RulesPath, NotesRules);
            ModelsStatus = $"Notes rules saved ({DateTime.Now:HH:mm}) - used from the next notes; old recording: Redo after clarification.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ModelsStatus = $"Not saved: {e.Message}"; }
    }

    // Cała baza wiedzy z edytora (zastępuje plik); linie bez nawiasów [ ] albo bez znaczenia są pomijane.
    void SaveKnowledgeText()
    {
        var entries = KnowledgeBase.Parse(KnowledgeText);
        var kb = KnowledgeBase.Load(KnowledgeBase.DefaultPath);
        kb.Entries = entries;
        try { kb.Save(KnowledgeBase.DefaultPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ModelsStatus = $"Knowledge base not saved: {e.Message}";
            return;
        }
        KnowledgeText = KnowledgeBase.Format(entries);
        ModelsStatus = $"Knowledge base saved: {entries.Count} entries.";
        LoadQuestions();
    }

    // Transkrypcja dowolnych plików wideo/audio (przycisk albo przeciągnięcie na zakładkę).
    public void ImportVideos(IEnumerable<string> files)
    {
        foreach (var file in files) _service.ImportVideo(file);
        LoadRecordings();
    }

    void ChooseVideo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Video or audio to transcribe", Multiselect = true,
            Filter = "Video and audio|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.m4v;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma|All files|*.*",
        };
        if (dialog.ShowDialog() == true) ImportVideos(dialog.FileNames);
    }

    public string Log { get => _log; private set => Set(ref _log, value); }
    public string TeamsStatus { get => _teamsStatus; private set => Set(ref _teamsStatus, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public string WindowTitle { get => _windowTitle; private set => Set(ref _windowTitle, value); }
    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }
    public string FreeSpace { get => _freeSpace; private set => Set(ref _freeSpace, value); }
    public bool IsFinalizing
    {
        get => _isFinalizing;
        private set
        {
            if (Set(ref _isFinalizing, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (Set(ref _isRecording, value)) OnPropertyChanged(nameof(ToggleText));
        }
    }

    public string ToggleText => IsRecording ? "STOP RECORDING" : "START RECORDING";

    public string OutputFolder
    {
        get => _settings.OutputFolder;
        set { _settings.OutputFolder = value; SaveSettings(); LoadRecordings(); }
    }

    public bool AutoRecord
    {
        get => _settings.AutoRecord;
        set { _settings.AutoRecord = value; SaveSettings(); }
    }

    public bool RecordMicrophone
    {
        get => _settings.RecordMicrophone;
        set { _settings.RecordMicrophone = value; SaveSettings(); }
    }

    // Notatki częściowe co LiveChunkMinutes w trakcie rozmowy (dymek w trayu); wyłączone = wszystko po rozmowie.
    public bool LiveNotes
    {
        get => _settings.LiveChunkMinutes > 0;
        set { _settings.LiveChunkMinutes = value ? 5 : 0; SaveSettings(); }
    }

    public bool RecordVideo
    {
        get => _settings.RecordVideo;
        set { _settings.RecordVideo = value; SaveSettings(); }
    }

    public int Fps
    {
        get => _settings.Fps;
        set { _settings.Fps = value; SaveSettings(); OnPropertyChanged(); }
    }

    public ResolutionOption Resolution
    {
        get => new(_settings.VideoHeight);
        set { _settings.VideoHeight = value.Height; SaveSettings(); OnPropertyChanged(); }
    }

    // Ilu rozmówców po stronie Teams (bez Ciebie); auto = wg progu podobieństwa głosów.
    public SpeakersOption Speakers
    {
        get => new(_settings.DiarizationSpeakers);
        set { _settings.DiarizationSpeakers = value.Count; SaveSettings(); OnPropertyChanged(); }
    }

    public string TranscriptGlossary
    {
        get => _settings.TranscriptGlossary;
        set { _settings.TranscriptGlossary = value; SaveSettings(); }
    }

    public bool WriteNotes
    {
        get => _settings.WriteNotes;
        set { _settings.WriteNotes = value; SaveSettings(); }
    }

    public bool TranslateTranscript
    {
        get => _settings.TranslateTranscript;
        set { _settings.TranslateTranscript = value; SaveSettings(); }
    }

    public bool AutoTranscribe
    {
        get => _settings.AutoTranscribe;
        set { _settings.AutoTranscribe = value; SaveSettings(); }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            _settings.StartWithWindows = value;
            Autostart.Apply(value, Environment.ProcessPath!);
            SaveSettings();
        }
    }

    // Wywoływane z dowolnego wątku (CatcherService, RecordingSession).
    public void AddLog(string message) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (message.TrimStart().StartsWith("step:")) _lastStep = message.Trim()[5..].Trim();
        _logLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        Log = string.Join(Environment.NewLine, _logLines);
    });

    void Refresh()
    {
        var session = _service.Session;
        TeamsStatus = _service.TeamsRunning ? "Running" : "Not running";
        IsRecording = session != null;
        IsFinalizing = _service.Finalizing;
        (State, StateBrush) = (session, IsFinalizing, _service.CallState) switch
        {
            (not null, _, _) => ("Recording", Brush("WF.Brush.Err")),
            (_, true, _) => ("Finalizing", Brush("WF.Brush.Warn")),
            (_, _, CallState.InCall) => ("In call (not recording)", Brush("WF.Brush.Warn")),
            _ when _service.Transcribing => ("Transcribing", Brush("WF.Brush.Warn")),
            _ => ("Idle", Brush("WF.Brush.Ok")),
        };
        WindowTitle = session == null ? "-" : session.VideoTarget?.Name ?? "(audio only)";
        Elapsed = session == null ? "-" : session.Elapsed.ToString(@"hh\:mm\:ss");
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.OutputFolder))!;
            FreeSpace = $"{new DriveInfo(root).AvailableFreeSpace / 1e9:F1} GB on {root}";
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            FreeSpace = "-";
        }

        var job = _service.CurrentJob;
        if (job == null) _lastStep = "";
        Analyzing = job == null ? "-  (queue empty)" : Path.GetRelativePath(_settings.OutputFolder, job).Replace(Path.DirectorySeparatorChar + LiveChunks.Dir + Path.DirectorySeparatorChar, "  ·  live part ")
            + (_lastStep.Length > 0 ? $"  ·  {_lastStep}" : "");
        if (job != _lastJob)
        {
            // zadanie skończone -> nowa notatka / pytania wybranego nagrania
            LoadNotes();
            LoadQuestions();
            _lastJob = job;
        }

        var busy = IsRecording || IsFinalizing || _service.Transcribing;
        if (_wasBusy && !busy) LoadRecordings(); // sesja zakończona -> nowe nagranie na liście
        _wasBusy = busy;
    }

    Task ToggleRecordingAsync() => IsRecording ? _service.StopRecordingAsync() : _service.StartRecordingAsync();

    void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Recordings folder", InitialDirectory = _settings.OutputFolder };
        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
            OnPropertyChanged(nameof(OutputFolder));
        }
    }

    void LoadRecordings()
    {
        Recordings.Clear();
        if (!Directory.Exists(_settings.OutputFolder)) return;
        foreach (var dir in new DirectoryInfo(_settings.OutputFolder).GetDirectories()
                     .Where(d => File.Exists(Path.Combine(d.FullName, RecordingSession.InfoJson))) // np. folder whisper obok nagrań
                     .OrderByDescending(d => d.Name).Take(10))
        {
            var files = dir.GetFiles();
            var size = files.Sum(f => f.Length) / 1e6;
            // plik zaimportowany ma od razu dźwięk do transkrypcji, bez meeting.mp4
            var hasMp4 = files.Any(f => f.Name is RecordingSession.MeetingMp4 or RecordingSession.TranscriptWav);
            var hasTranscript = files.Any(f => f.Name == Transcriber.TranscriptTxt);
            Recordings.Add(new RecordingItem(dir.Name, dir.FullName,
                $"{size:F0} MB{(hasMp4 ? "" : "  ·  not finalized")}{(hasTranscript ? "  ·  transcript" : "")}"));
        }
        // wybór zostaje na tym samym nagraniu po odświeżeniu listy
        var selected = _selectedRecording?.Folder;
        _selectedRecording = null;
        SelectedRecording = Recordings.FirstOrDefault(r => r.Folder == selected) ?? Recordings.FirstOrDefault();
        LoadQuestions();
    }

    // Pytania wybranego nagrania (questions.json), bez tych, które baza wiedzy już objaśnia; wpisane objaśnienia zostają przy odświeżeniu.
    void LoadQuestions()
    {
        var kb = KnowledgeBase.Load(KnowledgeBase.DefaultPath);
        var typed = Questions.Where(q => q.Meaning.Length > 0).ToDictionary(q => (q.Folder, q.Term.Term), q => q.Meaning);
        Questions.Clear();
        if (SelectedRecording is { } recording)
            foreach (var term in KnowledgeBase.LoadQuestions(recording.Folder).Where(q => !kb.Knows(q.Term)))
                Questions.Add(new QuestionItem(term, recording.Folder) { Meaning = typed.GetValueOrDefault((recording.Folder, term.Term), "") });
        OnPropertyChanged(nameof(HasQuestions));
        OnPropertyChanged(nameof(HasNoQuestions));
    }

    public bool HasNoQuestions => SelectedRecording != null && Questions.Count == 0;

    // Objaśnienia do bazy wiedzy, odpowiedziane pytania znikają; recalculate = nagranie przeliczane od dyskusji (bez Whispera).
    void SaveKnowledge(bool recalculate = true)
    {
        var kb = KnowledgeBase.Load(KnowledgeBase.DefaultPath);
        var answered = Questions.Where(q => q.Meaning.Trim().Length > 0).ToList();
        foreach (var q in answered)
        {
            var meaning = q.Meaning.Trim();
            // "= diamentowy": źle usłyszane słowo -> poprawne (podmieniane w tekście); "konsa - consa - budynek z gry" = inne zapisy + znaczenie
            if (meaning.StartsWith(KnowledgeBase.CorrectionMark))
            {
                var correct = meaning[1..].Trim();
                kb.Add(new KnowledgeEntry(correct, KnowledgeBase.CorrectionMark + correct, [q.Term.Term]));
                continue;
            }
            var parts = meaning.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            kb.Add(new KnowledgeEntry(q.Term.Term, parts[^1], parts.Length > 1 ? parts[..^1].ToList() : null));
        }
        var bulk = KnowledgeBase.Parse(BulkKnowledge);
        foreach (var entry in bulk) kb.Add(entry);
        if (answered.Count == 0 && bulk.Count == 0) return;
        try { kb.Save(KnowledgeBase.DefaultPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AddLog($"Knowledge base not saved: {e.Message}");
            return;
        }
        BulkKnowledge = "";
        var folders = Questions.Where(q => kb.Knows(q.Term.Term)).Select(q => q.Folder).Distinct().ToList();
        foreach (var folder in folders)
        {
            KnowledgeBase.SaveQuestions(folder, KnowledgeBase.LoadQuestions(folder).Where(q => !kb.Knows(q.Term)).ToList());
            if (recalculate) _service.QueueTranscription(folder, fromState: true);
        }
        AddLog($"Knowledge base: {answered.Count + bulk.Count} explanations saved{(recalculate ? $", recalculating {folders.Count} recording(s)" : "")}.");
        LoadQuestions();
    }

    void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    void SaveSettings()
    {
        try { _settings.Save(_settingsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AddLog($"Settings not saved: {e.Message}"); }
    }

    static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
