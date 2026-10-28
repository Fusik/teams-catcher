using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Pojęcie z bazy wiedzy: "diamentowy golem" = "jednostka z gry". Aliases = inne zapisy ("konsa" = "consa").
public sealed record KnowledgeEntry(string Term, string Meaning, List<string>? Aliases = null)
{
    public IEnumerable<string> AllNames => new[] { Term }.Concat(Aliases ?? []);
}

// Niejasne słowo z nagrania, które użytkownik ma objaśnić (panel "Do wyjaśnienia").
public sealed record UnclearTerm(string Term, string Sentence, string Time, string Reason);

// Baza wiedzy użytkownika (knowledge.json obok exe): pojęcia, skróty, nazwy z jego spotkań. Modele nie zgadują znaczeń -
// niejasne słowa trafiają do użytkownika, a jego objaśnienia wracają do każdej dyskusji i notatki (te same tematy się powtarzają).
public sealed class KnowledgeBase
{
    public const string FileName = "knowledge.json", QuestionsFile = "questions.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<KnowledgeEntry> Entries { get; set; } = [];

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static KnowledgeBase Load(string path)
    {
        if (!File.Exists(path)) return new KnowledgeBase();
        try { return JsonSerializer.Deserialize<KnowledgeBase>(File.ReadAllText(path)) ?? new KnowledgeBase(); }
        catch (JsonException) { return new KnowledgeBase(); }
    }

    public void Save(string path) => SafeFile.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    // Dopisanie albo zmiana znaczenia (ten sam termin bez względu na wielkość liter).
    public void Add(KnowledgeEntry entry)
    {
        Entries.RemoveAll(e => string.Equals(e.Term, entry.Term, StringComparison.OrdinalIgnoreCase));
        Entries.Add(entry);
    }

    // "[diamentowy golem - jednostka z gry], [konsa - consa - budynek z gry, gdzie zdobywa się anioły]":
    // pierwsza część = termin, ostatnia = znaczenie, środkowe = inne zapisy.
    public static List<KnowledgeEntry> Parse(string text) =>
        Regex.Matches(text, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2)
            .Select(p => new KnowledgeEntry(p[0], p[^1], p.Length > 2 ? p[1..^1].ToList() : null)).ToList();

    public bool Knows(string word) => Entries.Any(e => e.AllNames.Any(n => SameWord(n, word)));

    // Pojęcia występujące w tekście (z odmianą: pierwsze litery każdego słowa nazwy) - tylko one trafiają do promptu.
    public List<KnowledgeEntry> Relevant(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToList();
        return Entries.Where(e => e.AllNames.Any(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(part => words.Any(w => SameWord(part, w))))).ToList();
    }

    public static string Describe(IEnumerable<KnowledgeEntry> entries) =>
        string.Join("; ", entries.Select(e => $"{e.Term}{(e.Aliases is { Count: > 0 } a ? $" ({string.Join(", ", a)})" : "")} = {e.Meaning}"));

    // Poprawki pisowni od użytkownika (odpowiedź "= diamentowy" przy "Jamentowy"): wpis ze znaczeniem "=" + poprawne słowo,
    // źle usłyszane zapisy w Aliases. Podmieniane w tekście na stałe (z wielkością pierwszej litery), bez udziału modeli.
    public const string CorrectionMark = "=";

    public List<TranscriptSegment> ApplyCorrections(IReadOnlyList<TranscriptSegment> segments, out int replaced)
    {
        var corrections = Entries.Where(e => e.Meaning.StartsWith(CorrectionMark)).ToList();
        var count = 0;
        var result = segments.Select(s =>
        {
            var text = s.Text;
            foreach (var e in corrections)
                foreach (var alias in e.Aliases ?? [])
                    text = Regex.Replace(text, $@"(?<!\p{{L}})(\?\?)?{Regex.Escape(alias)}(\?\?)?(?!\p{{L}})", m =>
                    {
                        count++;
                        var word = e.Term;
                        return char.IsUpper(m.Value.TrimStart('?')[0]) ? char.ToUpper(word[0]) + word[1..] : word;
                    }, RegexOptions.IgnoreCase);
            return text == s.Text ? s : s with { Text = text };
        }).ToList();
        replaced = count;
        return result;
    }

    // ta sama nazwa w innej formie: "golema" = "golem", "consy" = "consa" (wspólny początek, różnica tylko w końcówce)
    public static bool SameWord(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return true;
        var common = 0;
        while (common < Math.Min(a.Length, b.Length) && a[common] == b[common]) common++;
        return common >= 4 && common >= Math.Max(a.Length, b.Length) - 3;
    }

    public static List<UnclearTerm> LoadQuestions(string folder)
    {
        var path = Path.Combine(folder, QuestionsFile);
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<UnclearTerm>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void SaveQuestions(string folder, IReadOnlyList<UnclearTerm> questions)
    {
        var path = Path.Combine(folder, QuestionsFile);
        if (questions.Count == 0) File.Delete(path);
        else File.WriteAllText(path, JsonSerializer.Serialize(questions, Json));
    }
}
