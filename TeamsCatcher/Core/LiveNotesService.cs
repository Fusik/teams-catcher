using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Notatka i pytania "do wyjaśnienia" w trakcie rozmowy, z transkrypcji na żywo (live-transcript.md, Whisper turbo). Co LiveNotesMinutes przybywa
// kawałek: jego notatka (Qwen) jest dopisywana do notatek poprzednich kawałków - po dwóch kawałkach notatka z 2, po trzech z 3.
// Nieznane słowa (LanguageTool na CPU, bez VRAM) trafiają od razu do questions.json nagrania. Model tekstu pracuje tylko wtedy, gdy Whisper turbo
// stoi (LiveTranscript.Gpu) i z małym kontekstem, po pracy jest zwalniany - całość mieści się w 6 GB VRAM.
public sealed partial class LiveNotesService(Settings settings, LiveTranscript text, Action<string> log) : IAsyncDisposable
{
    public const string FileName = "live-notes.md";
    // kawałek to ~3 min rozmowy (2-3 tys. tokenów) - większy kontekst tylko zajmuje VRAM obok Whispera
    const int ContextTokens = 8192;

    readonly List<MeetingNotes.Notes> _pieces = [];
    readonly List<UnclearTerm> _questions = [];
    LanguageTool? _tool;
    bool _toolFailed;
    Task? _run;

    public Task Completion => _run ?? Task.CompletedTask;

    // tytuł, notatka (Markdown), plik - dymek w trayu
    public event Action<string, string, string>? Ready;

    double PieceSeconds => settings.LiveNotesMinutes * 60.0;

    [GeneratedRegex(@"^\[(\d+:\d\d:\d\d)\]\s+[^:]+:\s*(.*)$")]
    private static partial Regex LineRegex();

    public void Schedule(RecordingSession s, CancellationToken ct)
    {
        if (settings.LiveNotesMinutes <= 0 || _run is { IsCompleted: false } || text.CoveredSeconds < (_pieces.Count + 1) * PieceSeconds) return;
        _run = RunAsync(s.Folder, _pieces.Count + 1, ct);
    }

    async Task RunAsync(string folder, int piece, CancellationToken ct)
    {
        MeetingNotes.Notes? notes = null;
        var language = settings.TranscriptLanguage;
        try
        {
            var lines = Piece(folder, (piece - 1) * PieceSeconds, piece * PieceSeconds);
            if (lines.Count > 0)
            {
                var transcript = string.Join("\n", lines.Select(l => l.Line));
                language = Language(transcript);
                await FindUnclearAsync(folder, lines, language, ct);
                notes = await WriteNotesAsync(transcript, language, ct);
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException
            or KeyNotFoundException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            log($"Live notes, part {piece}, failed: {e.Message}");
        }
        // też po błędzie: kawałek policzony, bez ponawiania co sekundę
        _pieces.Add(notes ?? new MeetingNotes.Notes([], []));
        if (notes == null) return;
        var end = TimeSpan.FromSeconds(piece * PieceSeconds);
        var title = $"Notatka na żywo 0:00-{end:h\\:mm\\:ss}";
        var rendered = MeetingNotes.Render(Merge(_pieces), language, title);
        var file = Path.Combine(folder, LiveChunks.Dir, $"{piece:00}", MeetingNotes.FileName(language));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, rendered, ct);
        await File.WriteAllTextAsync(Path.Combine(folder, LiveChunks.Dir, FileName), rendered, ct);
        log($"Live notes: {title} ({piece} part(s)).");
        Ready?.Invoke(title, rendered, file);
    }

    // Wypowiedzi z okna czasu [from, to) z pliku, do którego Whisper dopisuje (czytanie bez blokowania zapisu).
    static List<(double Seconds, string Line)> Piece(string folder, double from, double to)
    {
        using var reader = new StreamReader(new FileStream(LiveTranscript.PathIn(folder), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Select(l => (Match: LineRegex().Match(l), Line: l))
            .Where(x => x.Match.Success).Select(x => (Seconds: TimeSpan.Parse(x.Match.Groups[1].Value).TotalSeconds, x.Line))
            .Where(x => x.Seconds >= from && x.Seconds < to).ToList();
    }

    string Language(string transcript) => settings.TranscriptLanguage is "pl" or "en" ? settings.TranscriptLanguage
        : transcript.Count(c => "ąćęłńóśźż".Contains(char.ToLowerInvariant(c))) * 100 > transcript.Length ? "pl" : "en";

    // Nieznane słowniku słowa kawałka -> questions.json nagrania (panel TO CLARIFY od razu, bez czekania na krok Correct).
    async Task FindUnclearAsync(string folder, List<(double Seconds, string Line)> lines, string language, CancellationToken ct)
    {
        if (_tool == null && !_toolFailed)
        {
            if (LanguageTool.MissingFiles(settings) is { } missing)
            {
                _toolFailed = true;
                log($"Live clarify skipped, not found: {missing}");
            }
            else
                try { _tool = await LanguageTool.StartAsync(settings, ct); }
                catch (Exception e) when (e is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    _toolFailed = true;
                    log($"Live clarify skipped, LanguageTool failed: {e.Message}");
                }
        }
        if (_tool == null) return;
        var kb = KnowledgeBase.Load(KnowledgeBase.DefaultPath);
        var added = false;
        foreach (var (_, line) in lines)
        {
            var m = LineRegex().Match(line);
            foreach (var x in (await _tool.CheckAsync(m.Groups[2].Value, language, ct))
                .Where(x => x.Spelling && x.Word.Length > 0 && !kb.Knows(x.Word)))
                if (!_questions.Any(q => KnowledgeBase.SameWord(TranscriptLlm.Bare(q.Term), TranscriptLlm.Bare(x.Word))))
                {
                    _questions.Add(new UnclearTerm(x.Word, m.Groups[2].Value, m.Groups[1].Value, language == "pl" ? "nieznane słowo" : "unknown word"));
                    added = true;
                }
        }
        if (added) KnowledgeBase.SaveQuestions(folder, _questions);
    }

    // null = Ollama albo model niedostępne (bez notatki, pytania i tak idą).
    async Task<MeetingNotes.Notes?> WriteNotesAsync(string transcript, string language, CancellationToken ct)
    {
        if (!await Ollama.IsAvailableAsync(settings, ct, [settings.CorrectionModel, settings.CheckModel])) return null;
        await LiveTranscript.Gpu.WaitAsync(ct);
        try
        {
            Ollama.ContextLimit.Value = ContextTokens;
            return await MeetingNotes.WriteAsync(transcript, language, settings, null, ct, log, KnowledgeBase.Load(KnowledgeBase.DefaultPath), MeetingNotes.Partial);
        }
        finally
        {
            Ollama.ContextLimit.Value = 0;
            await Ollama.UnloadAsync(settings, CancellationToken.None);
            LiveTranscript.Gpu.Release();
        }
    }

    public static MeetingNotes.Notes Merge(IEnumerable<MeetingNotes.Notes> pieces)
    {
        var sections = new Dictionary<MeetingNotes.Section, List<MeetingNotes.Item>>();
        foreach (var piece in pieces)
            foreach (var (section, items) in piece.Sections)
            {
                if (!sections.TryGetValue(section, out var all)) sections[section] = all = [];
                all.AddRange(items);
            }
        return new MeetingNotes.Notes(sections, pieces.SelectMany(p => p.Parts).ToList());
    }

    public async ValueTask DisposeAsync()
    {
        if (_tool != null) await _tool.DisposeAsync();
    }
}
