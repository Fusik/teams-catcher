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

// Wyszukiwanie w transkrypcjach i notatkach wszystkich nagrań oraz w notatkach .md ze wskazanego folderu (np. vault Obsidiana):
// - po słowach: bez ogonków i wielkości liter, odmiana przez wspólny początek słowa ("projekt" -> "projektu", "projekcie"), "fraza w cudzysłowie" dosłownie;
// - po znaczeniu: wektory z lokalnego modelu w Ollamie ("termin oddania" -> "deadline na piątek"), liczone raz na nagranie (analysis\search-index.json).
public static class MeetingSearch
{
    public const string Teams = "Teams calls", Video = "Video files", Notes = "Notes folder";
    // Indeksy notatek z folderu użytkownika - obok exe, nie w jego folderze (vault Obsidiana zostaje nietknięty).
    public static string NotesIndexDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "search-index");
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

    // Źródło wyszukiwania: folder nagrania albo plik .md z folderu notatek.
    static bool IsNote(string source) => source.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && File.Exists(source);

    public static string KindOf(string folder)
    {
        if (IsNote(folder)) return Notes;
        try { return JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(Path.Combine(folder, RecordingSession.InfoJson)))?.Imported == true ? Video : Teams; }
        catch (Exception e) when (e is IOException or JsonException) { return Teams; }
    }

    // Foldery nagrań (z session.json), od najnowszego.
    public static List<string> Recordings(string outputFolder) => !Directory.Exists(outputFolder) ? [] :
        Directory.GetDirectories(outputFolder).Where(d => File.Exists(Path.Combine(d, RecordingSession.InfoJson))).OrderByDescending(Path.GetFileName).ToList();

    // Notatki .md z folderu i podfolderów (bez ustawień i kosza Obsidiana), od ostatnio zmienionej.
    public static List<string> NoteFiles(string folder) => !Directory.Exists(folder) ? [] :
        Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(d => d is ".obsidian" or ".trash"))
            .OrderByDescending(File.GetLastWriteTimeUtc).ToList();

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
        if (IsNote(folder)) return NotePassages(folder);
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

    // Notatka .md: akapity i punkty (bez nagłówków, metadanych "---" na górze, bloków kodu), co najmniej 2 słowa.
    public static List<Passage> NotePassages(string file)
    {
        var list = new List<Passage>();
        var lines = File.ReadAllLines(file);
        var start = lines.Length > 0 && lines[0].Trim() == "---" ? Array.FindIndex(lines, 1, l => l.Trim() == "---") + 1 : 0;
        var code = false;
        foreach (var raw in lines.Skip(start))
        {
            if (raw.TrimStart().StartsWith("```")) code = !code;
            var text = Regex.Replace(raw.Trim(), @"^([-*+]|\d+\.|>)?\s*(\[[ xX]\]\s*)?|\*\*|\[\[|\]\]", "").Trim();
            if (!code && !Regex.IsMatch(raw, @"^#+\s") && Regex.Matches(text, @"\p{L}{2,}").Count >= 2) list.Add(new("Note", "", text));
        }
        return list;
    }

    static string Stamp(string folder) => IsNote(folder) ? File.GetLastWriteTimeUtc(folder).Ticks.ToString() : string.Join("|", Sources.Select(s => Path.Combine(folder, s.File)).Where(File.Exists)
        .Select(f => $"{Path.GetFileName(f)}:{File.GetLastWriteTimeUtc(f).Ticks}"));

    // Indeks nagrania: z pamięci, z pliku albo na nowo (zmienione pliki). embed == null = bez wektorów (np. Ollama niedostępna).
    static async Task<Index> LoadAsync(string folder, string model, Func<IReadOnlyList<string>, Task<List<float[]>>>? embed)
    {
        var stamp = Stamp(folder);
        var file = IsNote(folder)
            ? Path.Combine(NotesIndexDir, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToLowerInvariant())))[..16] + ".json")
            : Path.Combine(folder, WorkNotes.Dir, IndexFile);
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

    // --- pytanie do Qwena: notatki -> nagrania -> transkrypcja -> model czyta i wskazuje fragmenty ---
    const int AskRecordings = 4, AskWindows = 8, WindowRadius = 3, NotesChars = 2000;
    // shortcut: luźniejszy próg niż w wyszukiwaniu (model i tak odrzuca nietrafione); przy złych wyborach nagrań podnieść.
    const double AskMinSimilarity = 0.4, AskRankGap = 0.1;

    // generate: zapytanie -> odpowiedź modelu (domyślnie Qwen z NotesModel); progress: co się teraz dzieje.
    // Wynik: na nagranie odpowiedź modelu ("answer") i fragmenty transkrypcji, które ją potwierdzają ("fragment") - cytowane z pliku, nie przepisywane przez model.
    public static async Task<List<SearchHit>> AskAsync(string question, IReadOnlyList<string> folders, Settings s, bool meaning,
        Action<string>? progress, CancellationToken ct, Func<string, Task<string>>? generate = null)
    {
        generate ??= prompt => Ollama.GenerateAsync(prompt, s.NotesModel, s, ct, 400, temperature: 0.1);
        var (phrases, stems) = Parse(question);
        var exact = Words(Normalize(question));
        Func<IReadOnlyList<string>, Task<List<float[]>>>? embed = meaning ? texts => Ollama.EmbedAsync(s, s.SearchModel, texts, ct) : null;
        var queryVector = meaning ? Normalize((await Ollama.EmbedAsync(s, s.SearchModel, [QueryText(question)], ct))[0]) : null;

        // 1. nagrania: notatka to streszczenie, więc trafienie w notatce liczy się bardziej niż w transkrypcji
        var ranked = new List<(string Folder, Index Index, double[] Scores, double Rank)>();
        for (var f = 0; f < folders.Count; f++)
        {
            progress?.Invoke($"Reading notes... {f}/{folders.Count} recordings");
            var index = await LoadAsync(folders[f], s.SearchModel, embed);
            var scores = index.Passages.Select((p, i) => (WordScore(p.Text, phrases, stems, exact) > 0 ? 0.5 : 0)
                + (queryVector != null && index.Decoded is { } v && i < v.Count ? Dot(queryVector, v[i]) : 0)).ToArray();
            double Best(Func<Passage, bool> where) => index.Passages.Select((p, i) => where(p) ? scores[i] : 0).DefaultIfEmpty(0).Max();
            var rank = Math.Max(Best(p => !p.View.StartsWith("Transcript")), Best(p => p.View.StartsWith("Transcript")) * 0.9);
            if (rank >= AskMinSimilarity) ranked.Add((folders[f], index, scores, rank));
        }

        var result = new List<SearchHit>();
        // nagrania dużo słabsze od najlepszego model i tak "dopasowywał" na siłę (test: pytanie o serial -> odpowiedź ze spotkania BIM)
        var top = ranked.Count > 0 ? ranked.Max(r => r.Rank) : 0;
        var picked = ranked.Where(r => r.Rank >= top - AskRankGap).OrderByDescending(r => r.Rank).Take(AskRecordings).ToList();
        foreach (var (folder, index, scores, rank) in picked)
        {
            ct.ThrowIfCancellationRequested();
            // 2. transkrypcja: okna wokół najlepszych zdań, w tej wersji językowej, w której jest najlepsze trafienie
            // notatka z folderu nie ma transkrypcji - model czyta samą notatkę
            var transcript = Enumerable.Range(0, index.Passages.Count).Where(i => index.Passages[i].View.StartsWith("Transcript") || index.Passages[i].View == "Note").ToList();
            if (transcript.Count == 0) continue;
            var view = index.Passages[transcript.MaxBy(i => scores[i])].View;
            var lines = transcript.Where(i => index.Passages[i].View == view).ToList();
            var best = lines.Select((i, n) => (n, scores[i])).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Take(AskWindows).Select(x => x.n);
            var windows = Windows(best, lines.Count, WindowRadius).Select(w => lines.GetRange(w.From, w.To - w.From + 1).Select(i => index.Passages[i]).ToList()).ToList();

            // 3. Qwen czyta fragmenty i wskazuje te, które odpowiadają na pytanie
            progress?.Invoke($"Qwen reads {Path.GetFileName(folder)}... ({result.Select(h => h.Folder).Distinct().Count()} answers so far)");
            var notes = index.Passages.Where(p => p.View.StartsWith("Notes") && p.View[^2..] == view[^2..]).Select(p => "- " + p.Text);
            var response = await generate(AskPrompt(question, string.Join("\n", notes), windows));
            var (answer, ids) = ParseAnswer(response, windows.Count);
            if (answer == null) continue;
            // kontrola: zdanie-dowód przepisane przez model musi naprawdę być we wskazanych fragmentach i dotyczyć pytania (wynik zdania jak w wyszukiwaniu).
            // Osobne pytanie TAK/NIE do modelu nie działało: odrzucało trafne i przepuszczało zmyślone (test na PLAN.md / instalacja.md).
            var quoted = Quoted(response, ids.Select(id => windows[id - 1]).ToList());
            // próg jak w wyszukiwaniu (0,5): przy 0,4 przechodził prawdziwy, ale obcy cytat (pytanie o crash -> zdanie z serialu)
            if (quoted.Count == 0 || quoted.Max(p => scores[index.Passages.IndexOf(p)]) < MinSimilarity) continue;
            var kind = KindOf(folder);
            var notesView = view == "Note" ? view : File.Exists(Path.Combine(folder, MeetingNotes.FileName(view[^2..].ToLowerInvariant()))) ? "Notes " + view[^2..] : view;
            result.Add(new(folder, kind, notesView, "", answer, "Qwen answer", rank));
            foreach (var id in ids)
            {
                var w = windows[id - 1];
                // zdanie-dowód oznaczone "» " (okno ma kilka wypowiedzi wokół)
                result.Add(new(folder, kind, view, w[0].Time, string.Join("\n", w.Select(p => (quoted.Contains(p) ? "» " : "") + p.Text)), "fragment", rank));
            }
        }
        progress?.Invoke("");
        return result;
    }

    // Okna ±radius wokół wybranych linii, nakładające się i stykające scalone, po kolei.
    public static List<(int From, int To)> Windows(IEnumerable<int> centers, int count, int radius)
    {
        var merged = new List<(int From, int To)>();
        foreach (var c in centers.Order())
        {
            var (from, to) = (Math.Max(0, c - radius), Math.Min(count - 1, c + radius));
            if (merged.Count > 0 && from <= merged[^1].To + 1) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, to));
            else merged.Add((from, to));
        }
        return merged;
    }

    public static string AskPrompt(string question, string notes, IReadOnlyList<List<Passage>> windows)
    {
        var text = new StringBuilder();
        text.AppendLine("Odpowiadasz na pytanie WYŁĄCZNIE na podstawie ponumerowanych fragmentów poniżej (transkrypcja spotkania albo notatka).");
        text.AppendLine().AppendLine($"Pytanie: {question}");
        if (notes.Length > 0) text.AppendLine().AppendLine("Notatka ze spotkania (tylko pomoc w zrozumieniu tematu, nie źródło odpowiedzi):").AppendLine(notes.Length > NotesChars ? notes[..NotesChars] : notes);
        text.AppendLine().AppendLine("Fragmenty:");
        for (var i = 0; i < windows.Count; i++)
            text.AppendLine($"[{i + 1}]{(windows[i][0].Time.Length > 0 ? $" ({windows[i][0].Time})" : "")}").AppendLine(string.Join("\n", windows[i].Select(p => p.Text))).AppendLine();
        text.AppendLine("Zasady:");
        text.AppendLine("- Odpowiedź tylko z tego, co jest we fragmentach. Nie zgaduj i nie dodawaj faktów spoza fragmentów.");
        text.AppendLine("- Jeśli żaden fragment nie odpowiada na pytanie, napisz: ODPOWIEDŹ: BRAK");
        text.AppendLine("- Odpowiedź w 1-3 zdaniach, w języku pytania.");
        text.AppendLine().AppendLine("Format, dokładnie trzy linie:");
        text.AppendLine("ODPOWIEDŹ: <odpowiedź>");
        text.AppendLine("FRAGMENTY: <numery fragmentów, które potwierdzają odpowiedź, np. 2, 5>");
        text.AppendLine("CYTAT: <jedno zdanie przepisane DOSŁOWNIE z tych fragmentów, na którym opierasz odpowiedź>");
        return text.ToString();
    }

    // Zdania fragmentów, które model przepisał (cytat ma >= 80% słów zdania albo zdanie >= 80% słów cytatu - model bywa, że przepisze dwa naraz);
    // pusta lista = NIE albo cytat spoza fragmentów.
    public static List<Passage> Quoted(string response, IReadOnlyList<List<Passage>> fragments)
    {
        if (Regex.IsMatch(response.Trim(), @"^\W*(NIE|NO)\W*$", RegexOptions.IgnoreCase)) return [];
        var quote = Words(Normalize(Regex.Match(response, @"CYTAT\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline) is { Success: true } m ? m.Groups[1].Value : response));
        if (quote.Count < 3) return [];
        static double Covered(List<string> part, List<string> whole) => part.Count == 0 ? 0 : (double)part.Count(whole.Contains) / part.Count;
        return fragments.SelectMany(w => w).Select(p => (Passage: p, Words: Words(Normalize(p.Text))))
            .Where(x => Math.Max(Covered(quote, x.Words), x.Words.Count >= 3 ? Covered(x.Words, quote) : 0) >= 0.8).Select(x => x.Passage).ToList();
    }

    // null = brak odpowiedzi (BRAK albo żaden poprawny numer fragmentu: odpowiedź bez dowodu w transkrypcji jest odrzucana).
    public static (string? Answer, List<int> Fragments) ParseAnswer(string response, int windows)
    {
        var answer = Regex.Match(response, @"ODPOWIED[ŹZ]\s*:\s*(.+)", RegexOptions.IgnoreCase) is { Success: true } a ? a.Groups[1].Value.Trim() : "";
        var ids = Regex.Match(response, @"FRAGMENTY\s*:\s*(.+)", RegexOptions.IgnoreCase) is { Success: true } f
            ? Regex.Matches(f.Groups[1].Value, @"\d+").Select(m => int.Parse(m.Value)).Where(i => i >= 1 && i <= windows).Distinct().ToList() : [];
        return answer.Length == 0 || answer.StartsWith("BRAK", StringComparison.OrdinalIgnoreCase) || ids.Count == 0 ? (null, []) : (answer, ids);
    }

    static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i];
        return sum;
    }
}
