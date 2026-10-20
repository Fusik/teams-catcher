using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Pisownia nazw ze słowniczka użytkownika (zaufane źródło). Whisper bywa pewny źle zapisanego nazwiska
// (wywiad TVN: "Doroczyński", "Sztur", "Kawalski" zamiast Dorociński, Stuhr, Kawulski) - ani prompt, ani korekta niepewnych słów tego nie łapią.
public static class Glossary
{
    // Pojedyncze słowo: najwyżej 1/4 liter inaczej. Nazwisko po zgodnym imieniu: do 0,45 ("Sztur" -> "Stuhr" brzmi po polsku tak samo).
    const double SingleWordMaxDistance = 0.25, AfterExactWordMaxDistance = 0.45;
    const int MinWordLength = 4;

    public static List<TranscriptSegment> Apply(IReadOnlyList<TranscriptSegment> segments, string glossary, out int replaced)
    {
        var terms = glossary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(t => t.Length > 0).ToList();
        var count = 0;
        var result = segments.Select(s =>
        {
            var text = s.Text;
            foreach (var term in terms) text = ApplyTerm(text, term, ref count);
            return text == s.Text ? s : s with { Text = text };
        }).ToList();
        replaced = count;
        return result;
    }

    public static string Fix(string text, string glossary) => Apply([new TranscriptSegment(0, 0, "", text)], glossary, out _)[0].Text;

    static string ApplyTerm(string text, string[] term, ref int count)
    {
        // słowo = litery/cyfry; reszta (interpunkcja, spacje) zostaje bez zmian
        var words = Regex.Matches(text, @"[\p{L}\p{N}]+").ToList();
        for (var i = 0; i + term.Length <= words.Count; i++)
        {
            var window = words.Skip(i).Take(term.Length).ToList();
            // wyrazy muszą stać obok siebie (tylko spacja między nimi)
            if (window.Zip(window.Skip(1)).Any(p => text[(p.First.Index + p.First.Length)..p.Second.Index].Trim().Length > 0)) continue;
            if (!Matches(window.Select(w => w.Value).ToArray(), term)) continue;
            var start = window[0].Index;
            var end = window[^1].Index + window[^1].Length;
            var replacement = string.Join(" ", term);
            if (text[start..end] == replacement) continue;
            count++;
            return ApplyTerm(text[..start] + replacement + text[end..], term, ref count);
        }
        return text;
    }

    static bool Matches(string[] words, string[] term)
    {
        var previousExact = false;
        for (var i = 0; i < term.Length; i++)
        {
            var (w, t) = (words[i].ToLowerInvariant(), term[i].ToLowerInvariant());
            var exact = w == t;
            if (!exact)
            {
                if (t.Length < MinWordLength || w[0] != t[0] || IsInflection(w, t)) return false;
                var limit = previousExact ? AfterExactWordMaxDistance : SingleWordMaxDistance;
                if (Distance(w, t) / (double)Math.Max(w.Length, t.Length) > limit) return false;
            }
            previousExact = exact;
        }
        return true;
    }

    // Różnica tylko w końcówce (ostatnie <= 3 litery) = polska odmiana ("Tekli" od "Tekla"), nie literówka - nie zmieniamy.
    public static bool IsInflection(string word, string term)
    {
        var common = 0;
        while (common < Math.Min(word.Length, term.Length) && word[common] == term[common]) common++;
        return common >= Math.Min(word.Length, term.Length) - 3;
    }

    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
