using System.Text;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Notatka (krok 4 Notes) jednym modelem: qwen3.5 4B Q3_K_M czyta każdą wypowiedź w 3 wersjach rozpoznania mowy - A Whisper large-v3,
// B Whisper turbo, C Qwen3-ASR - i tam, gdzie się różnią, wybiera wersję spójną z kontekstem. Notatka po polsku i po angielsku.
// Wcześniej: Llama + Qwen ~20 min na 1350 słów z samej wersji A; LFM2 tylko po angielsku (z maszynowego tłumaczenia transkrypcji).
public static class SttNotes
{
    // ~10 min rozmowy x 3 wersje = ~8 tys. tokenów: z kontekstem na GPU
    const int PartSeconds = 600;
    const double Temperature = 0.3;

    // Wypowiedź z czasem, mówcą i wersjami A/B/C (B, C puste, gdy model nic nie usłyszał w tym czasie).
    public sealed record Line(long FromMs, string Speaker, string A, string B, string C);

    public static async Task<Dictionary<string, string>> WriteAsync(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<HypothesisWord> hypotheses,
        string title, Settings s, Action<string> log, CancellationToken ct)
    {
        var parts = Parts(Lines(segments, hypotheses));
        var rules = MeetingNotes.LoadRules();
        var result = new Dictionary<string, string>();
        foreach (var language in Transcriber.Languages)
        {
            var course = new StringBuilder();
            foreach (var part in parts)
            {
                var answer = await Ollama.GenerateAsync(PartPrompt(language, part, rules), s.NotesModel, s, ct, 1500, temperature: Temperature);
                course.Append($"### [{Time(part[0].FromMs)}]\n{string.Join("\n", Bullets(answer).Select(b => "- " + b))}\n\n");
                log($"  step: notes {language.ToUpperInvariant()} part {Time(part[0].FromMs)} ({s.NotesModel})");
            }
            var summary = await Ollama.GenerateAsync(SummaryPrompt(language, course.ToString(), rules), s.NotesModel, s, ct, 1500, temperature: Temperature);
            result[language] = $"# {title}\n\n{Sections(summary, language)}\n\n## {Heading(language, 4)}\n\n{course.ToString().TrimEnd()}\n";
        }
        return result;
    }

    // Wersje B i C dla zdania A: słowa hipotezy z tego samego kanału (Ja / Rozmówcy), których środek wypada w czasie zdania.
    public static List<Line> Lines(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<HypothesisWord> hypotheses)
    {
        string Channel(string speaker) => speaker == Transcriber.Me ? Transcriber.Me : Transcriber.Others;
        string Words(string source, TranscriptSegment seg) => string.Join(" ", hypotheses
            .Where(h => h.Source == source && Channel(h.Speaker) == Channel(seg.Speaker) && (h.FromMs + h.ToMs) / 2 >= seg.FromMs && (h.FromMs + h.ToMs) / 2 < seg.ToMs)
            .OrderBy(h => h.FromMs).Select(h => h.Text.Trim())).Trim();
        return segments.OrderBy(x => x.FromMs).Select(seg => new Line(seg.FromMs, seg.Speaker, Plain(seg.Text), Words("B", seg), Words("C", seg))).ToList();
    }

    public static List<List<Line>> Parts(List<Line> lines)
    {
        var parts = new List<List<Line>>();
        foreach (var line in lines)
        {
            if (parts.Count == 0 || line.FromMs - parts[^1][0].FromMs >= PartSeconds * 1000L) parts.Add([]);
            parts[^1].Add(line);
        }
        return parts;
    }

    // ??niepewne?? słowa z korekty - same słowa
    static string Plain(string text) => Regex.Replace(text, @"<span[^>]*>\?\?(.*?)\?\?</span>|\?\?(.*?)\?\?", m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);

    static string Time(long ms) => TimeSpan.FromMilliseconds(ms).ToString(@"h\:mm\:ss");

    static string Versions(List<Line> part) => string.Join("\n", part.Select(l =>
        $"[{Time(l.FromMs)}] {l.Speaker}:\nA: {l.A}" + (l.B.Length > 0 ? $"\nB: {l.B}" : "") + (l.C.Length > 0 ? $"\nC: {l.C}" : "")));

    static string Rules(string language, string rules) => rules.Length == 0 ? "" : language == "pl"
        ? $"Zasady notatki od użytkownika:\n{rules}\n\n" : $"User's rules for the notes (may be written in Polish):\n{rules}\n\n";

    public static string PartPrompt(string language, List<Line> part, string rules) => Rules(language, rules) + (part.All(l => l.B.Length + l.C.Length == 0)
        ? (language == "pl"
            ? "Poniżej fragment rozmowy (ok. 10 min) z automatycznej transkrypcji - mogą w niej być przekręcone słowa; przyjmij wersję spójną z kontekstem rozmowy. Napisz po polsku przebieg tego fragmentu: 4-8 punktów, " +
              "każdy w osobnej linii od \"- [g:mm:ss] \" (czas wypowiedzi), jedno-dwa pełne zdania z konkretami: kto, co, nazwy, liczby, powody. Pomiń powitania i rozmowy o niczym.\n\n## ROZMOWA\n"
            : "Below is a part of a conversation (about 10 minutes) from an automatic transcript - some words may be garbled; take the version consistent with the context. Write in English what happened in this part: 4-8 points, " +
              "each on its own line starting with \"- [h:mm:ss] \" (time of the statement), one or two full sentences with specifics: who, what, names, numbers, reasons. Skip greetings and small talk.\n\n## CONVERSATION\n")
        : language == "pl"
        ? "Poniżej fragment rozmowy (ok. 10 min). Każdą wypowiedź rozpoznały 3 niezależne modele mowy: A, B, C. Porównaj je: gdzie się różnią, " +
          "przyjmij wersję spójną z kontekstem rozmowy (nazwy, liczby, sens); błędy rozpoznania pomiń. Napisz po polsku przebieg tego fragmentu: 4-8 punktów, " +
          "każdy w osobnej linii od \"- [g:mm:ss] \" (czas wypowiedzi), jedno-dwa pełne zdania z konkretami: kto, co, nazwy, liczby, powody. " +
          "Nie wspominaj o wersjach A/B/C. Pomiń powitania i rozmowy o niczym.\n\n## ROZMOWA\n"
        : "Below is a part of a conversation (about 10 minutes). Each statement was recognised by 3 independent speech models: A, B, C. Compare them: where they differ, " +
          "take the version consistent with the context (names, numbers, meaning); skip recognition errors. Write in English what happened in this part: 4-8 points, " +
          "each on its own line starting with \"- [h:mm:ss] \" (time of the statement), one or two full sentences with specifics: who, what, names, numbers, reasons. " +
          "Do not mention versions A/B/C. Skip greetings and small talk.\n\n## CONVERSATION\n") + Versions(part);

    static string SummaryPrompt(string language, string course, string rules) => Rules(language, rules) + (language == "pl"
        ? "Poniżej przebieg rozmowy w kolejnych częściach. Napisz po polsku sekcje z nagłówkami \"## Podsumowanie\" (5-10 punktów: o czym była rozmowa i do czego doprowadziła), " +
          "\"## Decyzje\", \"## Zadania\" (kto, co, termin, jeśli padł) i \"## Otwarte kwestie\". Pod nagłówkiem bez treści wpisz \"- brak\". Tylko to, co jest w przebiegu.\n\n"
        : "Below is the course of a conversation in consecutive parts. Write in English the sections \"## Summary\" (5-10 points: what it was about and what it led to), " +
          "\"## Decisions\", \"## Action items\" (who, what, deadline if said) and \"## Open questions\". Write \"- none\" under a heading with nothing to list. Only what the course says.\n\n") + course;

    static readonly Dictionary<string, string[]> Headings = new()
    {
        ["pl"] = ["Podsumowanie", "Decyzje", "Zadania", "Otwarte kwestie", "Przebieg rozmowy"],
        ["en"] = ["Summary", "Decisions", "Action items", "Open questions", "Course of the conversation"],
    };

    static string Heading(string language, int i) => Headings[language][i];

    // Same punkty odpowiedzi ("* **[0:14:55]**: tekst" -> "[0:14:55] tekst"), bez nagłówków i wstępów modelu.
    public static List<string> Bullets(string answer) =>
        Regex.Matches(answer, @"^\s*[-*•]\s+(.+?)\s*$", RegexOptions.Multiline)
            .Select(m => Regex.Replace(m.Groups[1].Value.Replace("**", ""), @"^\[?(\d+:\d\d:\d\d)\]?\s*:?\s*", "[$1] ").Trim())
            .Where(x => x.Length > 3).ToList();

    // Odpowiedź podsumowania: tylko nasze 4 sekcje jako "## X" (model pisze "## Meeting Summary - ..." albo "### ## Summary"), bez wstępu.
    public static string Sections(string answer, string language)
    {
        var text = new StringBuilder();
        var inSection = false;
        foreach (var line in answer.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.TrimStart().StartsWith('#'))
            {
                var name = line.Trim().TrimStart('#', ' ').Trim();
                var heading = Headings[language].Take(4).FirstOrDefault(h => name.StartsWith(h, StringComparison.OrdinalIgnoreCase));
                inSection = heading != null;
                if (inSection) text.Append(text.Length > 0 ? "\n" : "").Append($"## {heading}\n");
            }
            // "- - brak": model dopisuje własny punktor przed podanym "- brak"
            else if (inSection && line.Trim().Length > 0) text.Append(Regex.Replace(line, @"^(\s*[-*]\s+)[-*]\s+", "$1")).Append('\n');
        }
        return text.Length > 0 ? text.ToString().Trim() : $"## {Heading(language, 0)}\n" + answer.Trim();
    }
}
