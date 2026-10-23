using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Wynik wyszukiwania: fragment transkrypcji albo punkt notatki. View = nazwa widoku w panelu RESULT (do przejścia), Match = "words" / "meaning".
public sealed record SearchHit(string Folder, string Kind, string View, string Time, string Text, string Match, double Score)
{
    public string Recording => Path.GetFileName(Folder);
}

// Wyszukiwanie w transkrypcjach i notatkach wszystkich nagrań:
// - po słowach: bez ogonków i wielkości liter, odmiana przez wspólny początek słowa ("projekt" -> "projektu", "projekcie"), "fraza w cudzysłowie" dosłownie;
// - po znaczeniu: wektory z lokalnego modelu w Ollamie ("termin oddania" -> "deadline na piątek"), liczone raz na nagranie (analysis\search-index.json).
public static class MeetingSearch
{
    public const string Teams = "Teams calls", Video = "Video files";
    public const string IndexFile = "search-index.json";
    // shortcut: stały próg dobrany dla qwen3-embedding (pełne 1024 wymiary, zapytanie z instrukcją: trafne 0,54-0,60, obce <= 0,38);
    // inny model może wymagać innego progu. Przycięcie do 256 wymiarów podnosiło obce do 0,45 - dlatego pełne wektory, zapisane jako float16.
    public const double MinSimilarity = 0.5;
    const int MaxMeaningHits = 30;

    // Przeszukiwane pliki: (widok w panelu RESULT, plik źródłowy z czystym tekstem).
    static readonly (string View, string File)[] Sources =
    [
        ("Transcript PL", "transcript.pl.txt"), ("Transcript EN", "transcript.en.txt"),
        ("Notes PL", MeetingNotes.FileName("pl")), ("Notes EN", MeetingNotes.FileName("en")), ("Copilot", CopilotNotes.FileName),
    ];

    public sealed record Passage(string View, string Time, string Text);

    sealed class Index
    {
        public string Stamp { get; set; } = "";
        public string Model { get; set; } = "";
        public List<Passage> Passages { get; set; } = [];
        // base64 float16, po jednym na fragment (~2 kB); null = bez wyszukiwania po znaczeniu
        public List<string>? Vectors { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public List<float[]>? Decoded { get; set; }
    }

    static readonly Dictionary<string, Index> Cache = [];

    public static string KindOf(string folder)
    {
        try { return JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(Path.Combine(folder, RecordingSession.InfoJson)))?.Imported == true ? Video : Teams; }
        catch (Exception e) when (e is IOException or JsonException) { return Teams; }
    }

    // Foldery nagrań (z session.json), od najnowszego.
    public static List<string> Recordings(string outputFolder) => !Directory.Exists(outputFolder) ? [] :
        Directory.GetDirectories(outputFolder).Where(d => File.Exists(Path.Combine(d, RecordingSession.InfoJson))).OrderByDescending(Path.GetFileName).ToList();

    // --- po słowach ---

    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace('ł', 'l').Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    static List<string> Words(string normalized) => Regex.Matches(normalized, @"[\p{L}\p{N}]+").Select(m => m.Value).ToList();

    // Wspólny początek słowa zamiast słownika odmiany: krótkie słowa dosłownie, dłuższe bez 2 ostatnich liter (min. 4).
    public static string Stem(string word) => word.Length <= 4 ? word : word[..Math.Max(4, word.Length - 2)];

    // Zapytanie: "frazy w cudzysłowie" (dosłownie, bez ogonków) + pojedyncze słowa (każde musi wystąpić w jakiejś odmianie).
    public static (List<string> Phrases, List<string> Stems) Parse(string query)
    {
        var normalized = Normalize(query);
        var phrases = Regex.Matches(normalized, "\"([^\"]+)\"").Select(m => string.Join(' ', Words(m.Groups[1].Value))).Where(p => p.Length > 0).ToList();
        var rest = Regex.Replace(normalized, "\"[^\"]*\"?", " ");
        return (phrases, Words(rest).Select(Stem).Distinct().ToList());
    }

    // 0 = brak; 1 = wszystkie słowa w jakiejś odmianie; do 2 = tyle słów w dokładnie tej formie.
    public static double WordScore(string text, List<string> phrases, List<string> stems, IReadOnlyCollection<string> exact)
    {
        if (phrases.Count == 0 && stems.Count == 0) return 0;
        var words = Words(Normalize(text));
        var joined = " " + string.Join(' ', words) + " ";
        if (phrases.Any(p => !joined.Contains(" " + p + " "))) return 0;
        if (stems.Any(s => !words.Any(w => w.StartsWith(s, StringComparison.Ordinal)))) return 0;
        return 1 + (exact.Count == 0 ? 1 : (double)exact.Count(words.Contains) / exact.Count);
    }

    // --- fragmenty i indeks ---

    // "[0:01:05] Speaker 1: tekst" -> czas + "Speaker 1: tekst"; notatki: linie z treścią bez znaczników Markdown.
    public static List<Passage> Passages(string folder)
    {
        var list = new List<Passage>();
        foreach (var (view, file) in Sources)
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path)) continue;
            foreach (var raw in File.ReadLines(path))
            {
                var line = Regex.Replace(raw, @"<span[^>]*>\?\?(.*?)\?\?</span>|<!--.*?-->", "$1").Trim();
                if (Regex.Match(line, @"^\[(\d+:\d\d:\d\d)\]\s*(.+)$") is { Success: true } m) list.Add(new(view, m.Groups[1].Value, m.Groups[2].Value));
                // nagłówki (tytuł, nazwy sekcji) to nie treść; punkt = co najmniej 2 słowa (nie sam czas "[0:00:00]")
                else if ((view.StartsWith("Notes") || view == "Copilot") && !Regex.IsMatch(line, @"^#+\s"))
                {
                    var text = Regex.Replace(line, @"^([-*]|\d+\.)\s*|\*\*", "").Trim();
                    if (Regex.Matches(text, @"\p{L}{2,}").Count >= 2) list.Add(new(view, Regex.Match(text, @"\[?(\d+:\d\d(:\d\d)?)\]?") is { Success: true } t ? t.Groups[1].Value : "", text));
                }
            }
        }
        return list;
    }

    static string Stamp(string folder) => string.Join("|", Sources.Select(s => Path.Combine(folder, s.File)).Where(File.Exists)
        .Select(f => $"{Path.GetFileName(f)}:{File.GetLastWriteTimeUtc(f).Ticks}"));

    // Indeks nagrania: z pamięci, z pliku albo na nowo (zmienione pliki). embed == null = bez wektorów (np. Ollama niedostępna).
    static async Task<Index> LoadAsync(string folder, string model, Func<IReadOnlyList<string>, Task<List<float[]>>>? embed)
    {
        var stamp = Stamp(folder);
        var file = Path.Combine(folder, WorkNotes.Dir, IndexFile);
        Index? index;
        lock (Cache) Cache.TryGetValue(folder, out index);
        if (index == null && File.Exists(file))
            try { index = JsonSerializer.Deserialize<Index>(await File.ReadAllTextAsync(file)); }
            catch (Exception e) when (e is IOException or JsonException) { }
        if (index == null || index.Stamp != stamp) index = new Index { Stamp = stamp, Passages = Passages(folder) };
        var needVectors = embed != null && index.Passages.Count > 0 && (index.Vectors == null || index.Model != model);
        if (needVectors)
        {
            var vectors = new List<float[]>();
            // zdanie razem z sąsiednimi z tego samego pliku: sens wypowiedzi zależy od kontekstu ("Tak, w piątek.")
            var texts = index.Passages.Select((p, i) => string.Join(" ", new[] { i - 1, i, i + 1 }
                .Where(j => j >= 0 && j < index.Passages.Count && index.Passages[j].View == p.View).Select(j => index.Passages[j].Text))).ToList();
            foreach (var batch in texts.Chunk(64)) vectors.AddRange((await embed!(batch)).Select(Normalize));
            index.Model = model;
            index.Decoded = vectors;
            index.Vectors = vectors.Select(v => Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.Select(x => (Half)x).ToArray().AsSpan()))).ToList();
        }
        if (needVectors || !File.Exists(file) || index.Stamp != stamp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            SafeFile.WriteAllText(file, JsonSerializer.Serialize(index));
        }
        index.Decoded ??= index.Vectors?.Select(b => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(Convert.FromBase64String(b)).ToArray().Select(x => (float)x).ToArray()).ToList();
        lock (Cache) Cache[folder] = index;
        return index;
    }

    // Długość 1: podobieństwo = iloczyn skalarny.
    public static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : v.Select(x => x / norm).ToArray();
    }

    // Qwen3-Embedding: zapytanie z instrukcją, fragmenty bez (zalecenie autorów modelu).
    public static string QueryText(string query) => $"Instruct: Given a search query, retrieve meeting transcript fragments that answer it\nQuery: {query}";

    // Indeks po każdej analizie (w kolejce, po kroku): wyszukiwanie od razu gotowe. Błąd = indeks powstanie przy wyszukiwaniu.
    public static async Task IndexAsync(string folder, Settings s, CancellationToken ct)
    {
        var embed = s.SearchModel.Length > 0 && (await Ollama.ModelsAsync(s, ct))?.Contains(s.SearchModel) == true
            ? (Func<IReadOnlyList<string>, Task<List<float[]>>>)(texts => Ollama.EmbedAsync(s, s.SearchModel, texts, ct)) : null;
        await LoadAsync(folder, s.SearchModel, embed);
    }

    // Wyszukiwanie w podanych nagraniach. progress(zrobione, razem) przy budowaniu indeksów. Wynik: po słowach najpierw, potem po znaczeniu.
    public static async Task<List<SearchHit>> SearchAsync(string query, IReadOnlyList<string> folders, Settings s, bool meaning,
        Action<int, int>? progress, CancellationToken ct)
    {
        var (phrases, stems) = Parse(query);
        var exact = Words(Normalize(Regex.Replace(query, "\"[^\"]*\"?", " "))).ToList();
        Func<IReadOnlyList<string>, Task<List<float[]>>>? embed = meaning ? texts => Ollama.EmbedAsync(s, s.SearchModel, texts, ct) : null;
        var queryVector = meaning ? Normalize((await Ollama.EmbedAsync(s, s.SearchModel, [QueryText(query)], ct))[0]) : null;

        var words = new List<SearchHit>();
        var meanings = new List<SearchHit>();
        for (var f = 0; f < folders.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(f, folders.Count);
            var folder = folders[f];
            var index = await LoadAsync(folder, s.SearchModel, embed);
            var kind = KindOf(folder);
            for (var i = 0; i < index.Passages.Count; i++)
            {
                var p = index.Passages[i];
                if (WordScore(p.Text, phrases, stems, exact) is > 0 and var score) words.Add(new(folder, kind, p.View, p.Time, p.Text, "words", score));
                else if (queryVector != null && index.Decoded is { } v && i < v.Count && Dot(queryVector, v[i]) is var similarity && similarity >= MinSimilarity)
                    meanings.Add(new(folder, kind, p.View, p.Time, p.Text, "meaning", similarity));
            }
        }
        progress?.Invoke(folders.Count, folders.Count);
        // kolejność folderów = od najnowszego; OrderBy jest stabilne. Ta sama wypowiedź w kilku plikach (transkrypcja wklejona do notatki Copilota) = raz.
        return [.. words.OrderByDescending(h => h.Score).DistinctBy(h => (h.Folder, h.Time, h.Text)),
            .. meanings.OrderByDescending(h => h.Score).DistinctBy(h => (h.Folder, h.Time, h.Text)).Take(MaxMeaningHits)];
    }

    static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i];
        return sum;
    }
}
