using System.Text;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Tłumaczenie PL <-> EN lokalnymi modelami przez Ollamę (treść spotkania nie wychodzi z komputera), krzyżowo:
// PL -> EN tłumaczy CheckModel (Qwen), sprawdza CorrectionModel (Llama); EN -> PL odwrotnie. Jeden model zawsze weryfikuje drugi.
// hints: niezależne tłumaczenie mowy przez Whisper (-tr) dla tej samej wypowiedzi - podpowiedź sensu, gdy tekst PL ma błąd rozpoznawania.
public static class Translator
{
    // Paczka wypowiedzi w jednym zapytaniu: szybciej niż pojedynczo, a model widzi kontekst rozmowy.
    const int BatchSize = 20;

    public static string Other(string language) => language == "pl" ? "en" : "pl";
    public static string Name(string language) => language == "pl" ? "Polish" : "English";

    // Wynik ma tyle elementów co wejście; null = model nie zwrócił tej linii.
    public static async Task<string?[]> TranslateAsync(IReadOnlyList<string> texts, string from, string to, Settings s, CancellationToken ct,
        IReadOnlyList<string?>? hints = null)
    {
        var (translator, checker) = Models(to, s);
        // fazami: najpierw wszystkie paczki tłumaczy jeden model, potem wszystkie sprawdza drugi (przełączenie modelu = przeładowanie VRAM)
        var parsed = new string?[texts.Count];
        for (var start = 0; start < texts.Count; start += BatchSize)
        {
            var batch = texts.Skip(start).Take(BatchSize).ToList();
            var batchHints = hints?.Skip(start).Take(BatchSize).ToList();
            var response = await Ollama.GenerateAsync(BuildPrompt(batch, from, to, batchHints), translator, s, ct, 8192, 2048);
            Array.Copy(ParseNumbered(response, batch.Count), 0, parsed, start, batch.Count);
        }
        // weryfikacja: drugi model "czyta" oryginał i tłumaczenie obok siebie; brak linii w odpowiedzi = zostaje pierwsze tłumaczenie
        var result = new string?[texts.Count];
        for (var start = 0; start < texts.Count; start += BatchSize)
        {
            var batch = texts.Skip(start).Take(BatchSize).ToList();
            var translations = parsed.Skip(start).Take(BatchSize).ToList();
            var verified = ParseNumbered(await Ollama.GenerateAsync(BuildVerifyPrompt(batch, translations, from, to), checker, s, ct, 8192, 2048), batch.Count);
            for (var i = 0; i < batch.Count; i++) result[start + i] = PickVerified(batch[i], translations[i], verified[i]);
        }
        return result;
    }

    public static (string Translator, string Checker) Models(string to, Settings s) =>
        to == "en" ? (s.CheckModel, s.CorrectionModel) : (s.CorrectionModel, s.CheckModel);

    // Wywiad TVN: notatki EN mówiły o "single doll", PL o filmie "Lalka" - tłumaczenie bez sprawdzenia rozjeżdża sens.
    // shortcut: spójność terminów sprawdzana w obrębie paczki (20 wypowiedzi), dla długich spotkań rozważyć słowniczek tłumaczeń.
    public static string BuildVerifyPrompt(IReadOnlyList<string> originals, IReadOnlyList<string?> translations, string from, string to)
    {
        var prompt = new StringBuilder(
            $"Check this {Name(from)} to {Name(to)} translation of a meeting transcript, line by line against the original. " +
            "Fix every line whose meaning differs from the original, that adds or drops information, changes a name or title, " +
            "or translates the same term differently than other lines. Keep correct lines unchanged. " +
            $"Answer with all {originals.Count} numbered lines of the final {Name(to)} translation only, no comments.\n\n");
        for (var i = 0; i < originals.Count; i++)
            prompt.Append($"{i + 1}. {originals[i].ReplaceLineEndings(" ")}\n   => {translations[i]?.ReplaceLineEndings(" ") ?? "(missing)"}\n");
        return prompt.ToString();
    }

    public static string BuildPrompt(IReadOnlyList<string> lines, string from, string to, IReadOnlyList<string?>? hints = null)
    {
        var prompt = new StringBuilder(
            $"Translate each numbered line of this meeting transcript from {Name(from)} to {Name(to)}. " +
            "Keep the numbering, exactly one output line per input line, keep names unchanged, no comments.\n");
        if (hints?.Any(h => h != null) == true)
            prompt.Append("\"Heard:\" is an independent speech-to-English translation of the same audio - use it to understand the meaning " +
                          "when the line looks misheard, but translate the line itself. Do not output the \"Heard:\" lines.\n");
        prompt.Append('\n');
        for (var i = 0; i < lines.Count; i++)
        {
            prompt.Append($"{i + 1}. {lines[i].ReplaceLineEndings(" ")}\n");
            if (hints?.ElementAtOrDefault(i) is { } hint) prompt.Append($"   Heard: {hint.ReplaceLineEndings(" ")}\n");
        }
        return prompt.ToString();
    }

    // Model bywa, że przepisze oryginał zamiast tłumaczenia albo zostawi strzałkę z promptu - wtedy zostaje pierwsze tłumaczenie.
    public static string? PickVerified(string original, string? translation, string? verified)
    {
        if (translation == null) return null;
        verified = verified?.Replace("*", "").TrimStart('=', '>', ' ');
        // notatki TVN: model "sprawdzający" przepisał polskie oryginały z pogrubieniami - odpowiedź bliższa oryginałowi niż tłumaczeniu odpada
        return string.IsNullOrWhiteSpace(verified) || verified == original.Trim() || Overlap(verified, original) > Overlap(verified, translation) ? translation : verified;
    }

    static double Overlap(string a, string b)
    {
        var words = Regex.Matches(b.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToHashSet();
        var mine = Regex.Matches(a.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToList();
        return mine.Count == 0 ? 0 : mine.Count(words.Contains) / (double)mine.Count;
    }

    public static string?[] ParseNumbered(string response, int count)
    {
        var result = new string?[count];
        foreach (Match m in Regex.Matches(response, @"^\s*(\d+)[.)]\s*(.+?)\s*$", RegexOptions.Multiline))
            if (int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n <= count && result[n - 1] == null)
                result[n - 1] = m.Groups[2].Value;
        return result;
    }
}
