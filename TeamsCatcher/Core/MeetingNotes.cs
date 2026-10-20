using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Notatki ze spotkania ze sprawdzonej transkrypcji PL, dwa modele (6 GB VRAM): CorrectionModel (Llama) pisze, CheckModel (Qwen) sprawdza.
// Test na wywiadzie TVN: mały model (Bielik 4.5B) pisząc całą notatkę naraz powtarzał punkty, a "popraw całość wg uwag" zamienił w zadanie
// z matematyki. Dlatego małe zadania: każda sekcja to krótka lista, każdy punkt osobno sprawdzany (TAK/NIE z transkrypcją),
// odrzucony punkt wraca do piszącego, po kolejnym NIE znika. Czasy tematów i format Markdown składa kod, nie model.
public static class MeetingNotes
{
    // Podsumowanie całości z przebiegu: tyle przebiegu na jedno zapytanie, ile zmieści mniejszy z kontekstów obu modeli
    // po odjęciu zasad i odpowiedzi; ~2,5 znaku na token (polski tekst w tokenizerach Llamy/Qwena).
    const int MinChunkChars = 4000;
    const double CharsPerToken = 2.5;
    const int ReservedTokens = 2000;

    public static int ChunkCharsFor(int context, int extraChars) =>
        Math.Max(MinChunkChars, (int)((context - ReservedTokens) * CharsPerToken) - extraChars);
    // Część rozmowy = ~5 min (od pierwszej wypowiedzi): mały prompt mieści się z kontekstem na GPU, a z każdej części powstaje
    // dokładny przebieg. Test 1,5 h (Heroes): części po ~40 min dały 6 ogólnych punktów na całą notatkę.
    const int PartSeconds = 300;
    const int MaxPointsPerPart = 6;
    // więcej punktów z jednej części = zmyślanie (otwarte kwestie TVN: 15 wymyślonych pytań)
    const int MaxPerPart = 3;
    const int MaxSummary = 10;

    public enum Section { Summary, Assumptions, Conclusions, Decisions, Tasks, Open }

    // Całość: wszystkie sekcje. Notatka częściowa (kawałek ~5 min): główny sens, założenia, wnioski, uwagi, ustalenia - bez zadań.
    public static readonly Section[] Full = Enum.GetValues<Section>();
    public static readonly Section[] Partial = [Section.Summary, Section.Assumptions, Section.Conclusions, Section.Open, Section.Decisions];

    // Punkt notatki z czasem wypowiedzi ("0:15:09"; pusty = bez czasu, np. podsumowanie całości).
    public sealed record Item(string Time, string Text);
    // Część rozmowy w przebiegu: czas początku, temat i punkty.
    public sealed record Part(string Time, string Topic, List<Item> Points);
    public sealed record Notes(Dictionary<Section, List<Item>> Sections, List<Part> Parts);

    public static string FileName(string language) => $"notes.{language}.md";

    // Zasady notatek od użytkownika (notes-rules.md obok exe, edycja w zakładce MODELS): trafiają do list i poprawek. Nie do kontroli:
    // Qwen 3.5 4B z zasadami (nagłówki Markdown) zamiast "1. TAK" pisał własną notatkę - 163 ze 165 punktów odpadło jako NIE.
    public static string RulesPath => Path.Combine(AppContext.BaseDirectory, "notes-rules.md");
    const int MaxRulesChars = 6000;

    // Bez komentarzy <!-- --> i z limitem długości (każde zapytanie o notatkę je zawiera); brak pliku = bez zasad.
    public static string LoadRules(string? path = null)
    {
        path ??= RulesPath;
        if (!File.Exists(path)) return "";
        var text = Regex.Replace(File.ReadAllText(path), @"<!--.*?-->[ \t]*(?=\r?\n|$)", "", RegexOptions.Singleline).Trim();
        return text.Length > MaxRulesChars ? text[..MaxRulesChars] : text;
    }

    static string Rules(string language, string rules) => rules.Length == 0 ? "" : language == "pl"
        ? $"Zasady notatki od użytkownika (obowiązują przy pisaniu i sprawdzaniu):\n{rules}\n\n"
        : $"User's rules for the notes (apply them when writing and checking; they may be written in Polish):\n{rules}\n\n";

    static readonly Dictionary<string, Dictionary<Section, string>> Headings = new()
    {
        ["pl"] = new()
        {
            [Section.Summary] = "Podsumowanie", [Section.Assumptions] = "Założenia", [Section.Conclusions] = "Wnioski",
            [Section.Decisions] = "Decyzje i ustalenia", [Section.Tasks] = "Zadania", [Section.Open] = "Otwarte kwestie i uwagi",
        },
        ["en"] = new()
        {
            [Section.Summary] = "Summary", [Section.Assumptions] = "Assumptions", [Section.Conclusions] = "Conclusions",
            [Section.Decisions] = "Decisions and agreements", [Section.Tasks] = "Action items", [Section.Open] = "Open questions and remarks",
        },
    };

    // Co model ma wypisać w każdej sekcji (w języku notatki - model trzyma się wtedy tego języka).
    static string Ask(Section section, string language) => (section, language) switch
    {
        (Section.Assumptions, "pl") => "założenia, które uczestnicy przyjęli w rozmowie (to, co traktują jako dane)",
        (Section.Conclusions, "pl") => "wnioski, do których doszli uczestnicy",
        (Section.Decisions, "pl") => "decyzje i ustalenia, które zapadły (tylko wyraźnie podjęte)",
        (Section.Tasks, "pl") => "zadania do zrobienia w formacie \"Osoba: zadanie (termin, jeśli padł)\"",
        (Section.Open, "pl") => "otwarte kwestie, uwagi i pytania bez odpowiedzi",
        (Section.Assumptions, _) => "assumptions the participants made (what they take as given)",
        (Section.Conclusions, _) => "conclusions the participants reached",
        (Section.Decisions, _) => "decisions and agreements that were made (only clearly made ones)",
        (Section.Tasks, _) => "action items in the format \"Person: task (deadline if mentioned)\"",
        _ => "open issues, remarks and unanswered questions",
    };

    sealed record Candidate(Section Section, int Chunk, string Text, string Time);

    // Fazami, żeby nie przełączać modeli przy każdym punkcie (przełączenie = przeładowanie VRAM): Llama wypisuje wszystkie listy,
    // model sprawdzający ocenia je paczkami (jedno zapytanie na sekcję i część rozmowy), Llama poprawia odrzucone paczką, sprawdzający ocenia
    // poprawki - najwyżej DebateRounds rund. Summary z części = przebieg (3-6 punktów z czasami na ~5 min); podsumowanie całości
    // Llama pisze potem z przebiegu. sections: Full (całość) albo Partial (kawałek na żywo, ~5 min: przebieg jako podsumowanie).
    public static async Task<Notes> WriteAsync(string transcript, string language, Settings s, WorkNotes? work, CancellationToken ct, Action<string>? log = null,
        KnowledgeBase? kb = null, IReadOnlyList<Section>? sections = null)
    {
        var wanted = sections ?? Full;
        var rules = LoadRules();
        // niepewne słowa oznaczone w transkrypcji (<span ...>??słowo??</span>) - model przepisywał znaczniki do notatki
        transcript = Regex.Replace(transcript, @"<span[^>]*>\?\?(.*?)\?\?</span>", "$1");
        var parts = Parts(transcript);
        var history = new StringBuilder();
        // objaśnienia użytkownika dla pojęć z każdej części (baza wiedzy)
        var known = parts.Select(p => kb == null ? "" : KnowledgeBase.Describe(kb.Relevant(p.Text))).ToList();
        // nazwy z objaśnień użytkownika ("Heroes III") są znane, choć nie padły w rozmowie
        var namesSource = transcript + "\n" + string.Join("\n", known);

        // 1. listy punktów (model piszący)
        var pending = new List<Candidate>();
        foreach (var section in wanted)
            for (var k = 0; k < parts.Count; k++)
            {
                var detail = section == Section.Summary;
                var answer = await Ollama.GenerateAsync(detail ? DetailPrompt(language, parts[k].Text, known[k], rules) : ListPrompt(section, language, parts[k].Text, known[k], rules),
                    s.CorrectionModel, s, ct, detail ? 700 : 300);
                pending.AddRange(Bullets(answer).Select(b => Timed(b, parts, k)).Where(x => Usable(x.Text, parts[k].Text, rules, detail))
                    .Take(detail ? MaxPointsPerPart : MaxPerPart).Select(x => new Candidate(section, k, x.Text, x.Time)));
            }

        log?.Invoke($"  step: notes lists ({parts.Count} parts x {wanted.Count} sections, {s.CorrectionModel}) -> {pending.Count} points");

        // 2. sprawdzanie paczkami (model kontrolny) <-> poprawki paczkami (model piszący)
        var accepted = new List<Candidate>();
        int kept = 0, fixedCount = 0, dropped = 0;
        for (var round = 1; round <= s.DebateRounds && pending.Count > 0; round++)
        {
            var rejected = new List<Candidate>();
            foreach (var group in pending.GroupBy(c => (c.Section, c.Chunk)))
            {
                var items = group.ToList();
                var answers = Translator.ParseNumbered(await Ollama.GenerateAsync(CheckBatchPrompt(group.Key.Section, parts[group.Key.Chunk].Text, items.Select(c => c.Text).ToList(), language, known[group.Key.Chunk]),
                    s.CheckModel, s, ct, CheckTokens(items.Count)), items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    // nazwy spoza transkrypcji odpadają bez pytania; brak odpowiedzi = NIE
                    var ok = UnsupportedNames(items[i].Text, namesSource).Count == 0 && answers[i] is { } a && IsYes(a);
                    history.Append($"- [{items[i].Section}] {(ok ? "OK" : "NIE")}{(ok && round > 1 ? $" (po poprawce {round - 1})" : "")}: [{items[i].Time}] {items[i].Text}\n");
                    if (ok)
                    {
                        accepted.Add(items[i]);
                        if (round > 1) fixedCount++; else kept++;
                    }
                    else rejected.Add(items[i]);
                }
            }
            log?.Invoke($"  step: notes check round {round} ({s.CheckModel}): {rejected.Count} rejected");
            if (round == s.DebateRounds)
            {
                dropped += rejected.Count;
                break;
            }
            pending = [];
            foreach (var group in rejected.GroupBy(c => (c.Section, c.Chunk)))
            {
                var items = group.ToList();
                var fixes = Translator.ParseNumbered(await Ollama.GenerateAsync(FixBatchPrompt(language, parts[group.Key.Chunk].Text, items.Select(c => c.Text).ToList(), known[group.Key.Chunk], rules),
                    s.CorrectionModel, s, ct, 120 * items.Count), items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    var fix = fixes[i] is { } f ? Bullets("- " + f).Select(b => Timed(b, parts, group.Key.Chunk).Text).FirstOrDefault(b => Usable(b, parts[group.Key.Chunk].Text, rules, group.Key.Section == Section.Summary)) : null;
                    if (fix == null) dropped++;
                    else pending.Add(items[i] with { Text = fix });
                }
            }
            log?.Invoke($"  step: notes fixes round {round} ({s.CorrectionModel})");
        }
        List<Item> Accepted(Section section, int? part = null) => accepted.Where(c => c.Section == section && (part == null || c.Chunk == part))
            .OrderBy(c => c.Chunk).ThenBy(c => Seconds(c.Time)).DistinctBy(c => c.Text).Select(c => new Item(c.Time, c.Text)).ToList();
        var result = wanted.ToDictionary(section => section, section => Accepted(section).Take(section == Section.Summary ? 100 : 20).ToList());

        // 3. całość: przebieg (temat każdej części + jej punkty) i podsumowanie z przebiegu; notatka częściowa (bez zadań) zostaje przy przebiegu
        var notesParts = new List<Part>();
        if (wanted.Contains(Section.Summary) && wanted.Contains(Section.Tasks) && parts.Count > 1)
        {
            for (var k = 0; k < parts.Count; k++)
            {
                var answer = await Ollama.GenerateAsync(TopicPrompt(language, parts[k].Text, known[k]), s.CorrectionModel, s, ct, 60);
                var topic = Bullets("- " + answer.Trim().Split('\n')[0]).FirstOrDefault();
                // mały model potrafi odpowiedzieć powtórzeniem pytania ("O czym jest ten fragment rozmowy?")
                if (topic == null || topic.TrimEnd('"', ' ').EndsWith('?') || UnsupportedNames(topic, namesSource).Count > 0)
                    topic = language == "pl" ? "Fragment rozmowy" : "Part of the conversation";
                notesParts.Add(new Part(parts[k].Time, topic.Trim('"'), Accepted(Section.Summary, k)));
            }
            log?.Invoke($"  step: notes topics ({notesParts.Count}, {s.CorrectionModel})");
            result[Section.Summary] = await SummaryAsync(notesParts, language, s, rules, history, ct);
            log?.Invoke($"  step: notes summary ({result[Section.Summary].Count} points)");
        }
        var notes = new Notes(result, notesParts);
        work?.Write("09-notes.md", $"Notatki: {s.CorrectionModel} pisze, {s.CheckModel} sprawdza", "Notatka ze spotkania zgodna z transkrypcją.",
            $"Rozmowa w częściach po ~5 min. {s.CorrectionModel}: z każdej części przebieg (3-6 punktów z czasami) i krótkie listy sekcji " +
            $"(założenia, wnioski, decyzje, zadania, otwarte kwestie); temat części; podsumowanie całości z przebiegu. {s.CheckModel}: punkty paczkami - " +
            $"czy transkrypcja je potwierdza (TAK/NIE). NIE = punkt wraca do piszącego, po {s.DebateRounds}. NIE znika. Nazwy spoza transkrypcji odrzucane bez pytania.",
            $"gotowe: {kept} punktów przyjętych od razu, {fixedCount} po poprawce, {dropped} usuniętych; części {parts.Count}",
            history + "\n" + Render(notes, language, ""));
        return notes;
    }

    // Podsumowanie całości z przebiegu (nie z transkrypcji - 1,5 h się nie mieści): Llama pisze, sprawdzający ocenia z tym samym przebiegiem, NIE = punkt odpada.
    static async Task<List<Item>> SummaryAsync(List<Part> parts, string language, Settings s, string rules, StringBuilder history, CancellationToken ct)
    {
        var source = string.Concat(parts.Select(p => $"[{p.Time}] {p.Topic}\n" + string.Concat(p.Points.Select(x => $"- {x.Text}\n"))));
        var tokens = Math.Min(await Ollama.ContextAsync(s.CorrectionModel, s, ct), await Ollama.ContextAsync(s.CheckModel, s, ct));
        var result = new List<Item>();
        foreach (var chunk in Chunk(source, ChunkCharsFor(tokens, rules.Length)))
        {
            var points = Bullets(await Ollama.GenerateAsync(SummaryPrompt(language, chunk, rules), s.CorrectionModel, s, ct, 700)).Where(b => !IsFragment(b)).Take(MaxSummary).ToList();
            var answers = Translator.ParseNumbered(await Ollama.GenerateAsync(CheckBatchPrompt(Section.Summary, chunk, points, language), s.CheckModel, s, ct, CheckTokens(points.Count)), points.Count);
            for (var i = 0; i < points.Count; i++)
            {
                var ok = answers[i] is { } a && IsYes(a);
                history.Append($"- [Overall] {(ok ? "OK" : "NIE")}: {points[i]}\n");
                if (ok) result.Add(new Item("", points[i]));
            }
        }
        return result;
    }

    // "[0:15:09] tekst" -> czas wypowiedzi z tej części; bez czasu albo spoza części = początek części.
    static Item Timed(string bullet, List<(string Time, string Text)> parts, int k)
    {
        var m = Regex.Match(bullet, @"^\[?(\d+:\d\d:\d\d)\]?\s*[-–:]?\s*");
        var inPart = m.Success && Seconds(m.Groups[1].Value) >= Seconds(parts[k].Time) && (k == parts.Count - 1 || Seconds(m.Groups[1].Value) < Seconds(parts[k + 1].Time));
        return new Item(inPart ? m.Groups[1].Value : parts[k].Time, bullet[m.Length..].Trim());
    }

    // Części po ~5 min (czas = pierwsza wypowiedź), końcówka krótsza niż minuta dołączona do poprzedniej; bez czasów - po 5000 znaków.
    public static List<(string Time, string Text)> Parts(string transcript)
    {
        var windows = TopicWindows(Turns(transcript));
        if (windows.Count > 1 && Seconds(windows[^1][^1].Time) - Seconds(windows[^1][0].Time) < 60)
        {
            windows[^2].AddRange(windows[^1]);
            windows.RemoveAt(windows.Count - 1);
        }
        return windows.Count == 0 ? Chunk(transcript, 5000).Select(c => ("", c)).ToList()
            : windows.Select(w => (w[0].Time, string.Join("\n", w.Select(t => t.Line)))).ToList();
    }

    // Okna części: kolejne wypowiedzi, aż minie PartSeconds od pierwszej w oknie.
    public static List<List<(string Time, string Line)>> TopicWindows(List<(string Time, string Line)> turns)
    {
        var windows = new List<List<(string Time, string Line)>>();
        foreach (var turn in turns)
        {
            if (windows.Count == 0 || Seconds(turn.Time) - Seconds(windows[^1][0].Time) >= PartSeconds) windows.Add([]);
            windows[^1].Add(turn);
        }
        return windows;
    }

    static double Seconds(string time) => TimeSpan.TryParse(time, out var t) ? t.TotalSeconds : 0;

    public static string Render(Notes notes, string language, string title)
    {
        var h = Headings.GetValueOrDefault(language) ?? Headings["en"];
        var none = language == "pl" ? "brak" : "none";
        static string Line(Item x) => x.Time.Length > 0 ? $"[{x.Time}] {x.Text}" : x.Text;
        var text = new StringBuilder(title.Length > 0 ? $"# {title}\n\n" : "");
        foreach (var section in notes.Sections.Keys.Order())
        {
            text.Append($"## {h[section]}\n");
            var items = notes.Sections.GetValueOrDefault(section) ?? [];
            if (items.Count == 0) text.Append($"- {none}\n");
            foreach (var item in items) text.Append(section == Section.Tasks ? $"- [ ] {Line(item)}\n" : $"- {Line(item)}\n");
            text.Append('\n');
        }
        if (notes.Parts.Count == 0) return text.ToString();
        text.Append($"## {(language == "pl" ? "Przebieg rozmowy" : "Course of the conversation")}\n\n");
        foreach (var part in notes.Parts)
        {
            text.Append($"### [{part.Time}] {part.Topic}\n");
            if (part.Points.Count == 0) text.Append($"- {none}\n");
            foreach (var item in part.Points) text.Append($"- {Line(item)}\n");
            text.Append('\n');
        }
        return text.ToString();
    }

    // known: objaśnienia użytkownika z bazy wiedzy dla pojęć z tej części ("diamentowy golem = jednostka z gry") - model nie zgaduje.
    // Tylko do zrozumienia słów - po teście Heroes: objaśnienie "SP = siła czarów" trafiło do notatki jako punkt.
    static string Known(string language, string known) => known.Length == 0 ? "" : language == "pl"
        ? $"Objaśnienia użytkownika (tylko po to, żeby rozumieć słowa z rozmowy - nie wpisuj ich do notatki): {known}\n\n"
        : $"User's explanations (only to understand words in the conversation - do not put them in the notes): {known}\n\n";

    // Punkt przepisany prawie dosłownie z rozmowy (od 8 słów, >= 80% słów kolejnej wypowiedzi) to cytat, nie notatka (Heroes).
    public static bool IsCopied(string bullet, string transcript)
    {
        var words = Regex.Matches(bullet.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToList();
        if (words.Count < 8) return false;
        return transcript.Split('\n').Any(line =>
        {
            var source = Regex.Matches(line.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToHashSet();
            return words.Count(source.Contains) >= 0.8 * words.Count;
        });
    }

    static string ListPrompt(Section section, string language, string transcript, string known = "", string rules = "") => language == "pl"
        ? Rules(language, rules) + Known(language, known) + $"Oto transkrypcja rozmowy. Wypisz {Ask(section, language)}. Każdy punkt w osobnej linii zaczynającej się od \"- \", krótko, po polsku. " +
          "Tylko to, co wprost jest w transkrypcji - nie dopowiadaj, nie cytuj wypowiedzi, bez etykiet typu \"Decyzja:\". " +
          "\"Ja\" to autor notatki, \"Rozmówca 1\", \"Rozmówca 2\" itd. to inni uczestnicy; " +
          "jeśli ktoś przedstawia się z imienia, użyj imienia. Jeśli nic takiego nie ma, odpowiedz: - brak\n\n## TRANSKRYPCJA\n" + transcript
        : Rules(language, rules) + Known(language, known) + $"Here is a conversation transcript. List {Ask(section, language)}. One short point per line starting with \"- \", in English. " +
          "Only what the transcript directly says - do not invent, do not quote, no labels like \"Decision:\". " +
          "\"Me\" is the author of the notes, \"Speaker 1\", \"Speaker 2\" etc. are the other participants; " +
          "if someone introduces themselves, use their name. If there is nothing like that, answer: - none\n\n## TRANSCRIPT\n" + transcript;

    // Kontrola w języku notatki (dyskusja modeli zawsze w języku rozmowy). Pytanie zależy od sekcji: TVN - "decyzje"
    // i "zadania" były zwykłymi faktami i cytatami, a samo "czy transkrypcja to potwierdza" je przepuszczało.
    // Transkrypcja na początku promptu: kolejne zapytania o tę samą część zaczynają się tak samo, więc Ollama używa ponownie jej obliczeń.
    public static string CheckBatchPrompt(Section section, string transcript, IReadOnlyList<string> statements, string language = "en", string known = "") =>
        (language == "pl" ? "## TRANSKRYPCJA\n" : "## TRANSCRIPT\n") + transcript + "\n\n" + Known(language, known) + (language, section) switch
        {
            ("pl", Section.Decisions) => "Dla każdego numerowanego punktu: czy z transkrypcji wynika, że uczestnicy wyraźnie podjęli tę decyzję (uzgodnili albo postanowili), a nie tylko wspomnieli fakt?",
            ("pl", Section.Assumptions) => "Dla każdego numerowanego punktu: czy z transkrypcji wynika, że uczestnicy przyjęli to założenie (traktują je jako dane)?",
            ("pl", Section.Conclusions) => "Dla każdego numerowanego punktu: czy uczestnicy wyraźnie doszli w rozmowie do tego wniosku?",
            (_, Section.Assumptions) => "For each numbered statement: does the transcript show that the participants made this assumption (take it as given)?",
            (_, Section.Conclusions) => "For each numbered statement: did the participants explicitly reach this conclusion in the conversation?",
            ("pl", Section.Tasks) => "Dla każdego numerowanego punktu: czy z transkrypcji wynika, że ta osoba wyraźnie wzięła na siebie albo dostała to zadanie?",
            ("pl", Section.Open) => "Dla każdego numerowanego punktu: czy to pytanie lub sprawa padły wprost w rozmowie i zostały bez odpowiedzi?",
            ("pl", _) => "Dla każdego numerowanego punktu: czy transkrypcja wprost to potwierdza (osoby, nazwy, liczby i fakty tak, jak padły), a zdanie jest logiczne i poprawne?",
            (_, Section.Decisions) => "For each numbered statement: does the transcript show that the participants explicitly made this decision (agreed or decided something), not just mentioned a fact?",
            (_, Section.Tasks) => "For each numbered statement: does the transcript show that this person explicitly took on or was given this task to do?",
            (_, Section.Open) => "For each numbered statement: was this question or issue explicitly raised in the transcript and left unanswered?",
            _ => "For each numbered statement: is it directly supported by the transcript (people, names, numbers and facts as said), logical and correct?",
        } + (language == "pl" ? " Odpowiedz jedną linią na punkt: numer i TAK albo NIE, nic więcej.\n\n" : " Answer with one line per statement: the number and YES or NO, nothing else.\n\n") +
        string.Concat(statements.Select((x, i) => $"{i + 1}. {x}\n"));

    public static string FixBatchPrompt(string language, string transcript, IReadOnlyList<string> statements, string known = "", string rules = "") => (language == "pl"
        ? "## TRANSKRYPCJA\n" + transcript + "\n\n" + Known(language, known) + Rules(language, rules) + "Te numerowane punkty notatki nie zgadzają się z transkrypcją. Napisz każdy od nowa tak, żeby mówił tylko to, " +
          "co wprost jest w transkrypcji, krótko, po polsku, bez cytatów. Jeśli transkrypcja w ogóle tego nie mówi, napisz: brak. " +
          "Odpowiedz numerowanymi liniami, jedna na punkt.\n\n"
        : "## TRANSCRIPT\n" + transcript + "\n\n" + Known(language, known) + Rules(language, rules) + "These numbered notes do not match the transcript. Rewrite each so it says only what the transcript " +
          "directly says, briefly, in English, without quotes. If the transcript does not say it at all, write: none. " +
          "Answer with numbered lines, one per note.\n\n")
        + string.Concat(statements.Select((x, i) => $"{i + 1}. {x}\n"));

    static string DetailPrompt(string language, string transcript, string known = "", string rules = "") => language == "pl"
        ? Rules(language, rules) + Known(language, known) + "Oto fragment rozmowy (ok. 5 minut). Opisz dokładnie, co w nim powiedziano: 3-6 punktów, każdy w osobnej linii " +
          "zaczynającej się od \"- [g:mm:ss] \" - czas z transkrypcji wypowiedzi, której punkt dotyczy. Każdy punkt to jedno-dwa pełne zdania po polsku z konkretami: " +
          "kto, co, nazwy, liczby, powody. Własnymi słowami, bez wklejania wypowiedzi. Pomiń powitania, podziękowania i rozmowy o niczym. " +
          "\"Ja\" to autor notatki, \"Rozmówca 1\", \"Rozmówca 2\" itd. to inni uczestnicy; jeśli ktoś przedstawia się z imienia, użyj imienia.\n\n## TRANSKRYPCJA\n" + transcript
        : Rules(language, rules) + Known(language, known) + "Here is a part of a conversation (about 5 minutes). Describe exactly what was said: 3-6 points, each on its own line " +
          "starting with \"- [h:mm:ss] \" - the transcript time of the statement the point is about. Each point is one or two full sentences in English with specifics: " +
          "who, what, names, numbers, reasons. In your own words, do not paste statements. Skip greetings, thanks and small talk. " +
          "\"Me\" is the author of the notes, \"Speaker 1\", \"Speaker 2\" etc. are the other participants; if someone introduces themselves, use their name.\n\n## TRANSCRIPT\n" + transcript;

    static string SummaryPrompt(string language, string course, string rules = "") => language == "pl"
        ? Rules(language, rules) + "Oto przebieg rozmowy: tematy kolejnych części (z czasem) i ich punkty. Napisz podsumowanie całości: 5-10 punktów, każdy w osobnej linii " +
          "zaczynającej się od \"- \", jedno-dwa pełne zdania po polsku: o czym była rozmowa, najważniejsze informacje i do czego doprowadziła. Tylko to, co jest w przebiegu.\n\n## PRZEBIEG\n" + course
        : Rules(language, rules) + "Here is the course of a conversation: topics of consecutive parts (with times) and their points. Write a summary of the whole: 5-10 points, each on its own line " +
          "starting with \"- \", one or two full sentences in English: what the conversation was about, the most important information and what it led to. Only what the course says.\n\n## COURSE\n" + course;

    static string TopicPrompt(string language, string fragment, string known = "") => language == "pl"
        ? Known(language, known) + "Fragment rozmowy:\n" + fragment + "\n\nNapisz temat tego fragmentu: jedno krótkie zdanie oznajmujące po polsku (np. \"Zapowiedź gości programu.\"). Tylko temat."
        : Known(language, known) + "Part of a conversation:\n" + fragment + "\n\nWrite the topic of this part: one short statement in English (e.g. \"Introduction of the show's guests.\"). Topic only.";

    // Nazwy własne punktu: słowa z wielkiej litery poza początkiem zdania ("Marcin Dorociński, Maciej Kawulski").
    public static List<string> Names(string text) =>
        Regex.Matches(text, @"(?<![.!?:]\s*|^[\s\-""„]*)\p{Lu}\p{Ll}{2,}").Select(m => m.Value).Distinct().ToList();

    // Nazwa zgubiona w tłumaczeniu; odmiana dozwolona (pierwsze 4 litery: "Kawulski" = "Kawulskiego").
    public static List<string> MissingNames(IEnumerable<string> names, string translation) =>
        names.Where(n => !Regex.Matches(translation, @"\p{L}+").Any(w => w.Value.Length >= 4 && n.Length >= 4
            && string.Equals(w.Value[..4], n[..4], StringComparison.OrdinalIgnoreCase))).ToList();

    // "Rozmówca 1: <cała wypowiedź>" to cytat, nie punkt notatki (TVN: Llama wkleiła wypowiedź do podsumowania)
    public static bool IsQuote(string bullet) => Regex.IsMatch(bullet, @"^""?(Ja|Rozmówc[ay]\s*\d*|Me|Speakers?\s*\d*|Others)\s*:", RegexOptions.IgnoreCase);

    // Półzdanie z rozmowy, nie punkt notatki (symulacja Heroes, notatki częściowe): "To moment.", "Tak, zrobiła regen...",
    // "Chyba, że dobra, nie...", doklejona odpowiedź "... - Tak, to jest prawda.", szablon "Rozmówca N" z polecenia.
    public static bool IsFragment(string bullet) =>
        Regex.Matches(bullet, @"\p{L}+").Count < 4
        || Regex.IsMatch(bullet, @"^\W*(tak|nie|no|chyba|dobra|okej|ok|aha|yes|yeah|yep|nope)\b\s*[,.!]", RegexOptions.IgnoreCase)
        || Regex.IsMatch(bullet, @"\s[-–]\s+(tak|nie|yes|no)\b", RegexOptions.IgnoreCase)
        || Regex.IsMatch(bullet, @"\b(Rozmówc\w*|Speakers?)\s+N\b");

    // Punkt do notatki: nie cytat, nie przepisana wypowiedź ani zasada z notes-rules.md (Llama wpisała "Tylko to, co padło w rozmowie..."
    // jako założenie), nie półzdanie, nie pusty szablon zadania ("Krotor: Zadanie (H3DM, brak terminu)").
    // Przebieg (detail) może zaczynać się od "Rozmówca 1:" - to opis, kto co powiedział (test 1,5 h: filtr cytatów opróżnił 9 z 17 części).
    static bool Usable(string bullet, string transcript, string rules = "", bool detail = false) =>
        (detail || !IsQuote(bullet)) && !IsCopied(bullet, transcript) && !IsCopied(bullet, rules) && !IsFragment(bullet)
        && !Regex.IsMatch(bullet, @"\b(Zadanie|Task)\s*\(");

    // Qwen dopisuje uzasadnienie do każdego "N. NIE (...)"; limit 8 tokenów na punkt ucinał odpowiedź po 2-3 punktach, reszta = NIE.
    static int CheckTokens(int count) => 80 * count + 40;

    public static bool IsYes(string answer) => Regex.IsMatch(answer.Replace("*", ""), @"^\W*(YES|TAK)\b", RegexOptions.IgnoreCase);

    // Punkty z odpowiedzi: linie od "-", "*" albo numeru; "brak"/"none" = brak punktów. Bez Markdown, pól wyboru i powtórzeń.
    public static List<string> Bullets(string answer) =>
        Regex.Matches(answer, @"^\s*(?:[-*•]|\d+[.)])\s+(.+?)\s*$", RegexOptions.Multiline)
            .Select(m => Regex.Replace(Regex.Replace(m.Groups[1].Value.Replace("*", ""), @"^\[\s?x?\]\s*", ""),
                @"^(Decyzja|Decision|Zadanie|Task|Pytanie( bez odpowiedzi)?|(Unanswered )?Question|Temat|Topic|Otwarta kwestia|Open question|Wniosek|Conclusion|Założenie|Assumption|Dane wejściowe)\s*:\s*|^(Wnioskuję|Wnioskujemy),?\s*że\s+", "", RegexOptions.IgnoreCase).Trim())
            .Where(x => x.Length > 3 && !Regex.IsMatch(x, @"^\W*(brak|none|nic|nothing)\b", RegexOptions.IgnoreCase))
            .Distinct().ToList();

    // Wypowiedzi transkrypcji: "[0:00:05] Rozmówca 1: tekst".
    static List<(string Time, string Line)> Turns(string transcript) =>
        Regex.Matches(transcript, @"^\[(\d+:\d\d:\d\d)\].*$", RegexOptions.Multiline).Select(m => (m.Groups[1].Value, m.Value.TrimEnd('\r'))).ToList();

    // Słowa z wielkiej litery w środku zdania (imiona, nazwiska, tytuły), których nie ma w transkrypcji nawet w innej formie ("Lalka" = "lalkę").
    // Test TVN: model dopisał "reżyser Stanisław Bareja" (w transkrypcji "do stanarzysty") - taki punkt jest odrzucany bez pytania.
    public static List<string> UnsupportedNames(string notes, string transcript)
    {
        var known = Regex.Matches(transcript, @"\p{L}+").Select(m => m.Value.ToLowerInvariant()).ToHashSet();
        var names = new List<string>();
        foreach (var line in notes.Split('\n').Where(l => !l.TrimStart().StartsWith('#')))
            foreach (Match m in Regex.Matches(line, @"(?<![.!?:]\s*|^[\s\-\[\]x*\d:]*)\p{Lu}\p{Ll}{2,}"))
            {
                var word = m.Value.ToLowerInvariant();
                if (!known.Any(k => k.Length >= 4 && word.Length >= 4 && Glossary.IsInflection(k, word) && k[..4] == word[..4]) && !known.Contains(word))
                    names.Add(m.Value);
            }
        return names.Distinct().ToList();
    }

    // Podział po liniach (wypowiedzi nie są cięte w połowie).
    public static List<string> Chunk(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length > maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }
}
