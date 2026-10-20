using System.Text;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Poprawa jakości transkrypcji lokalnymi modelami (Ollama). Swobodne poprawianie tekstu przez LLM wprowadza nowe błędy (Idiap, INTERSPEECH 2024),
// dlatego tylko podmiana pojedynczych, podobnie brzmiących słów, zatwierdzana przez drugi model.
public static class TranscriptLlm
{
    const double MaxChangedWords = 0.3;
    // Whisper bierze z promptu najwyżej 224 tokeny; słowniczek musi się zmieścić.
    const int MaxGlossaryChars = 600;

    // Nazwy własne i terminy z pierwszego przebiegu (+ słowniczek użytkownika) -> prompt drugiego przebiegu Whispera.
    public static async Task<string> BuildGlossaryAsync(string userGlossary, IReadOnlyList<TranscriptSegment> firstPass, Settings s, CancellationToken ct)
    {
        var terms = SplitTerms(userGlossary);
        if (firstPass.Count > 0)
        {
            var text = string.Join("\n", firstPass.Select(x => x.Text));
            if (text.Length > 15000) text = text[..15000];
            var answer = await Ollama.GenerateAsync(
                "List the proper names (people, companies, places, products) and specialist terms that appear in this meeting transcript " +
                "and that a speech recognizer could misspell. Use the most likely correct spelling. Answer with one comma-separated line, " +
                $"at most 40 items, nothing else.\n\n{text}", s.CorrectionModel, s, ct, 8192, 400);
            // tylko nazwy własne: pospolite słowa z pierwszego przebiegu bywają błędne ("stanarzysty") i utrwalałyby błąd
            terms.AddRange(SplitTerms(answer.Split('\n').FirstOrDefault(l => l.Contains(',')) ?? answer).Where(IsProperName));
        }
        return SanitizePrompt(string.Join(", ", terms.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    public static bool IsProperName(string term) => term.Length > 0 && char.IsUpper(term[0]) && term.Any(char.IsLower);

    static List<string> SplitTerms(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length is > 1 and < 60).ToList();

    // Prompt trafia do linii poleceń whisper-cli: tylko litery, cyfry i zwykła interpunkcja, bez cudzysłowów.
    public static string SanitizePrompt(string glossary)
    {
        var clean = Regex.Replace(glossary, @"[^\p{L}\p{N} ,.\-'&/+]", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', ',');
        if (clean.Length > MaxGlossaryChars) clean = clean[..clean.LastIndexOf(',', MaxGlossaryChars)];
        return clean;
    }

    // Dziedzina rozmowy w 1 zdaniu - bez tego model nie wie, że "golem" i "armageddon" to Heroes, a nie błędy. Bez listy słów:
    // lista z błędnej transkrypcji utrwalała błąd ("Jament Golem" w temacie -> model kontrolny odrzucił "diamentowy").
    internal static async Task<string> TopicAsync(List<TranscriptSegment> segments, Settings s, CancellationToken ct)
    {
        var text = string.Join("\n", segments.Select(x => x.Text));
        if (text.Length > 12000) text = text[..12000];
        var answer = await Ollama.GenerateAsync(
            "In one sentence, name the subject area of this conversation (for example which game, product or field). " +
            $"Do not quote or list words from the text. Answer in English, nothing else.\n\n{text}", s.CorrectionModel, s, ct, 8192, 80);
        return Regex.Replace(answer.Replace("*", ""), @"\s+", " ").Trim();
    }

    // Zdanie z sąsiednimi zdaniami w nawiasach - model widzi kontekst, ale poprawia tylko środek.
    internal static (string Before, string Text, string After) Context(List<TranscriptSegment> all, int i) =>
        (i > 0 ? all[i - 1].Text : "", all[i].Text, i + 1 < all.Count ? all[i + 1].Text : "");

    // Tylko podmiana słów, najwyżej 30% zdania (min. 1). Każda zmiana słowa osobno:
    // - brzmi podobnie ("jamentowym" -> "diamentowym") albo obie pozostałe hipotezy słyszą to słowo w tym miejscu: przyjęta;
    // - słowo z hipotezy, ale w innym miejscu zdania albo z jednej hipotezy i niepodobne: odrzucone ("nie ucierpisz" -> "nie że");
    // - słowo A potwierdzone przez inną hipotezę: nietykalne (model zmienił "zastanawiasz" na "zastanawiam");
    // - sama wielkość liter / interpunkcja: cofnięta (zmiana "," na "." psuła fragmenty zdań).
    // TVN: "do stanarzysty. Pytanie," -> "do Maciej Sztur." odrzucone. Null = nic nie zostało.
    // Słowo oznaczone przez model jako niepewne (??słowo??) zostaje oznaczone - dopuszczalne dla każdego słowa, nie liczy się do limitu zmian.
    public static string? SafeCorrection(string original, string corrected, params string?[] alternatives)
    {
        var a = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = corrected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // słowa każdej hipotezy (B, C) stojące w tym samym miejscu zdania co słowo A
        var heard = a.Select(_ => new List<string>()).ToArray();
        foreach (var alt in alternatives.Where(x => x != null))
        {
            var words = alt!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var (i, k) in AlignWords(a, words)) heard[i].Add(Bare(words[k]));
        }
        var kept = (string[])a.Clone();
        var marked = false;
        // dopasowanie słów jak w diff: model zgubił "w" w "golema w Armagedonem" - podmiany obok mimo to się liczą,
        // dopisane i usunięte słowa są pomijane (oryginał zostaje)
        foreach (var (i, k) in AlignWords(a, b))
        {
            var unsure = b[k].Contains("??");
            var (w, v) = (Bare(a[i]), Bare(b[k]));
            // nie: słowo A potwierdzone przez inną hipotezę; cyfra zamiast słowa ("jednego" -> "1") to zmiana zapisu, nie poprawka
            if (w != v && v.Length > 0 && !heard[i].Contains(w) && !(v.All(char.IsDigit) && !w.All(char.IsDigit))
                && (SoundsSimilar(w, v) || heard[i].Count(x => x == v) >= 2))
                kept[i] = SameCase(a[i], b[k].Replace("??", ""));
            if (unsure) (kept[i], marked) = (Mark(kept[i]), true);
        }
        var changed = a.Zip(kept).Count(x => Bare(x.First) != Bare(x.Second));
        if (changed > Math.Max(1, (int)(a.Length * MaxChangedWords)) || (changed == 0 && !marked)) return null;
        return string.Join(' ', kept);
    }
    // Pary (słowo oryginału, słowo poprawki) z najtańszego dopasowania (Levenshtein na słowach: zgodne 0, podmiana/wstawienie/usunięcie 1).
    public static List<(int A, int B)> AlignWords(string[] a, string[] b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (Bare(a[i - 1]) == Bare(b[j - 1]) ? 0 : 1));
        var pairs = new List<(int A, int B)>();
        for (int i = a.Length, j = b.Length; i > 0 && j > 0;)
        {
            if (d[i, j] == d[i - 1, j - 1] + (Bare(a[i - 1]) == Bare(b[j - 1]) ? 0 : 1)) pairs.Add((--i, --j));
            else if (d[i, j] == d[i - 1, j] + 1) i--;
            else j--;
        }
        pairs.Reverse();
        return pairs;
    }
    // "golem," -> "??golem??," (interpunkcja poza oznaczeniem)
    public static string Mark(string word)
    {
        var core = word.Replace("??", "").TrimEnd('.', ',', '!', '?', ';', ':', '…');
        return core.Length == 0 ? word : $"??{core}??{word.Replace("??", "")[core.Length..]}";
    }
    // wielkość pierwszej litery jak w oryginale ("jednego Diamentowego golema" -> "diamentowego")
    static string SameCase(string original, string word) => word.Length == 0 || original.Length == 0 ? word
        : (char.IsUpper(original[0]) ? char.ToUpper(word[0]) : char.ToLower(word[0])) + word[1..];

    internal static string Bare(string word) => Regex.Replace(word.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    // Najwyżej 40% liter inaczej (jamentowym/diamentowym: 2 z 11).
    static bool SoundsSimilar(string a, string b) => a.Length > 0 && b.Length > 0 && Glossary.Distance(a, b) <= 0.4 * Math.Max(a.Length, b.Length);


}
