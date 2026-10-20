using System.Text;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Notatki ze spotkania ze sprawdzonej transkrypcji PL, dwa modele (6 GB VRAM): CorrectionModel (Llama) pisze, CheckModel (Qwen) sprawdza.
// Test na wywiadzie TVN: mały model (Bielik 4.5B) pisząc całą notatkę naraz powtarzał punkty, a "popraw całość wg uwag" zamienił w zadanie
// z matematyki. Dlatego małe zadania: każda sekcja to krótka lista, każdy punkt osobno sprawdzany (TAK/NIE z transkrypcją),
// odrzucony punkt wraca do piszącego, po kolejnym NIE znika. Czasy tematów i format Markdown składa kod, nie model.
public static class MeetingNotes
{
    const int ChunkChars = 12000;
    const int ContextTokens = 8192;
    // tematy: jeden na tyle sekund rozmowy (czas = pierwsza wypowiedź okna); godzina = ~20 zapytań
    const int TopicSeconds = 180;
    const int MaxSummary = 7;
    // więcej punktów z jednej części = zmyślanie (otwarte kwestie TVN: 15 wymyślonych pytań)
    const int MaxPerChunk = 8;

    public enum Section { Summary, Assumptions, Conclusions, Decisions, Tasks, Open }

    // Całość: wszystkie sekcje. Notatka częściowa (kawałek ~5 min): główny sens, założenia, wnioski, uwagi, ustalenia - bez zadań.
    public static readonly Section[] Full = Enum.GetValues<Section>();
    public static readonly Section[] Partial = [Section.Summary, Section.Assumptions, Section.Conclusions, Section.Open, Section.Decisions];

    public sealed record Notes(Dictionary<Section, List<string>> Sections, List<(string Time, string Text)> Topics);

    public static string FileName(string language) => $"notes.{language}.md";

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
        (Section.Summary, "pl") => "główny sens rozmowy - najważniejsze informacje (najwyżej 5 punktów)",
        (Section.Assumptions, "pl") => "założenia, które uczestnicy przyjęli w rozmowie (to, co traktują jako dane)",
        (Section.Conclusions, "pl") => "wnioski, do których doszli uczestnicy",
        (Section.Decisions, "pl") => "decyzje i ustalenia, które zapadły (tylko wyraźnie podjęte)",
        (Section.Tasks, "pl") => "zadania do zrobienia w formacie \"Osoba: zadanie (termin, jeśli padł)\"",
        (Section.Open, "pl") => "otwarte kwestie, uwagi i pytania bez odpowiedzi",
        (Section.Summary, _) => "the main point of the conversation - the most important information (at most 5 points)",
        (Section.Assumptions, _) => "assumptions the participants made (what they take as given)",
        (Section.Conclusions, _) => "conclusions the participants reached",
        (Section.Decisions, _) => "decisions and agreements that were made (only clearly made ones)",
        (Section.Tasks, _) => "action items in the format \"Person: task (deadline if mentioned)\"",
        _ => "open issues, remarks and unanswered questions",
    };

    sealed record Candidate(Section Section, int Chunk, string Text);

    // Fazami, żeby nie przełączać modeli przy każdym punkcie (przełączenie = przeładowanie VRAM): Llama wypisuje wszystkie listy,
    // model sprawdzający ocenia je paczkami (jedno zapytanie na sekcję i część transkrypcji), Llama poprawia odrzucone paczką, sprawdzający ocenia
    // poprawki - najwyżej DebateRounds rund. Tematy: jeden na ~3 min rozmowy.
    // sections: Full (całość) albo Partial (kawałek na żywo). context: notatki częściowe z kawałków - kontekst całej rozmowy dla notatki z całości.
    public static async Task<Notes> WriteAsync(string transcript, string language, Settings s, WorkNotes? work, CancellationToken ct, Action<string>? log = null,
        KnowledgeBase? kb = null, IReadOnlyList<Section>? sections = null, string context = "")
    {
        var wanted = sections ?? Full;
        var chunks = Chunk(transcript, ChunkChars);
        var history = new StringBuilder();
        // objaśnienia użytkownika dla pojęć z każdej części (baza wiedzy) + notatki częściowe jako kontekst
        var known = chunks.Select(chunk => (kb == null ? "" : KnowledgeBase.Describe(kb.Relevant(chunk)))
            + (context.Length > 0 ? (language == "pl" ? "\nNotatki częściowe z kolejnych fragmentów rozmowy (kontekst):\n" : "\nPartial notes from consecutive parts (context):\n") + context : "")).ToList();
        // nazwy z objaśnień użytkownika ("Heroes III") są znane, choć nie padły w rozmowie
        var namesSource = transcript + "\n" + string.Join("\n", known);

        // 1. listy punktów (model piszący)
        var pending = new List<Candidate>();
        foreach (var section in wanted)
            for (var k = 0; k < chunks.Count; k++)
                pending.AddRange(Bullets(await Ollama.GenerateAsync(ListPrompt(section, language, chunks[k], known[k]), s.CorrectionModel, s, ct, ContextTokens, 400))
                    .Where(b => Usable(b, chunks[k])).Take(MaxPerChunk).Select(b => new Candidate(section, k, b)));

        log?.Invoke($"  step: notes lists ({chunks.Count} parts x {wanted.Count} sections, {s.CorrectionModel}) -> {pending.Count} points");

        // 2. sprawdzanie paczkami (model kontrolny) <-> poprawki paczkami (model piszący)
        var accepted = new List<Candidate>();
        int kept = 0, fixedCount = 0, dropped = 0;
        for (var round = 1; round <= s.DebateRounds && pending.Count > 0; round++)
        {
            var rejected = new List<Candidate>();
            foreach (var group in pending.GroupBy(c => (c.Section, c.Chunk)))
            {
                var items = group.ToList();
                var answers = Translator.ParseNumbered(await Ollama.GenerateAsync(CheckBatchPrompt(group.Key.Section, chunks[group.Key.Chunk], items.Select(c => c.Text).ToList(), language, known[group.Key.Chunk]),
                    s.CheckModel, s, ct, ContextTokens, 8 * items.Count + 16), items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    // nazwy spoza transkrypcji odpadają bez pytania; brak odpowiedzi = NIE
                    var ok = UnsupportedNames(items[i].Text, namesSource).Count == 0 && answers[i] is { } a && IsYes(a);
                    history.Append($"- [{items[i].Section}] {(ok ? "OK" : "NIE")}{(ok && round > 1 ? $" (po poprawce {round - 1})" : "")}: {items[i].Text}\n");
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
                var fixes = Translator.ParseNumbered(await Ollama.GenerateAsync(FixBatchPrompt(language, chunks[group.Key.Chunk], items.Select(c => c.Text).ToList(), known[group.Key.Chunk]),
                    s.CorrectionModel, s, ct, ContextTokens, 80 * items.Count), items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    var fix = fixes[i] is { } f ? Bullets("- " + f).FirstOrDefault(b => Usable(b, chunks[group.Key.Chunk])) : null;
                    if (fix == null) dropped++;
                    else pending.Add(items[i] with { Text = fix });
                }
            }
            log?.Invoke($"  step: notes fixes round {round} (Llama)");
        }
        var result = wanted.ToDictionary(section => section, section => accepted.Where(c => c.Section == section)
            .Select(c => c.Text).Distinct().Take(section == Section.Summary ? MaxSummary : 20).ToList());

        // 3. tematy: czas z transkrypcji (pierwsza wypowiedź okna ~3 min), model pisze tylko krótki opis fragmentu
        var topics = new List<(string Time, string Text)>();
        foreach (var group in TopicWindows(Turns(transcript)))
        {
            var answer = await Ollama.GenerateAsync(TopicPrompt(language, string.Join("\n", group.Select(t => t.Line)), kb == null ? "" : KnowledgeBase.Describe(kb.Relevant(string.Join(" ", group.Select(t => t.Line))))), s.CorrectionModel, s, ct, ContextTokens, 60);
            var text = Bullets("- " + answer.Trim().Split('\n')[0]).FirstOrDefault();
            // mały model potrafi odpowiedzieć powtórzeniem pytania ("O czym jest ten fragment rozmowy?")
            if (text != null && !text.TrimEnd('"', ' ').EndsWith('?') && UnsupportedNames(text, namesSource).Count == 0) topics.Add((group[0].Time, text));
        }
        log?.Invoke($"  step: notes topics ({topics.Count}, Llama)");
        var notes = new Notes(result, topics);
        work?.Write("09-notes.md", $"Notatki: {s.CorrectionModel} pisze, {s.CheckModel} sprawdza", "Notatka ze spotkania zgodna z transkrypcją.",
            $"{s.CorrectionModel}: dla każdej sekcji (podsumowanie, decyzje, zadania, otwarte kwestie) wypisz krótkie punkty tylko z transkrypcji; " +
            $"dla każdych ~3 min rozmowy jedno zdanie tematu. {s.CheckModel}: punkty paczkami - czy transkrypcja je potwierdza (TAK/NIE). " +
            $"NIE = punkt wraca do piszącego, po {s.DebateRounds}. NIE znika. Nazwy spoza transkrypcji odrzucane bez pytania.",
            $"gotowe: {kept} punktów przyjętych od razu, {fixedCount} po poprawce, {dropped} usuniętych; tematów {topics.Count}",
            history + "\n" + Render(notes, language, ""));
        return notes;
    }

    // Okna tematów: kolejne wypowiedzi, aż minie TopicSeconds od pierwszej w oknie.
    public static List<List<(string Time, string Line)>> TopicWindows(List<(string Time, string Line)> turns)
    {
        var windows = new List<List<(string Time, string Line)>>();
        foreach (var turn in turns)
        {
            if (windows.Count == 0 || Seconds(turn.Time) - Seconds(windows[^1][0].Time) >= TopicSeconds) windows.Add([]);
            windows[^1].Add(turn);
        }
        return windows;
    }

    static double Seconds(string time) => TimeSpan.TryParse(time, out var t) ? t.TotalSeconds : 0;

    public static string Render(Notes notes, string language, string title)
    {
        var h = Headings.GetValueOrDefault(language) ?? Headings["en"];
        var none = language == "pl" ? "brak" : "none";
        var text = new StringBuilder(title.Length > 0 ? $"# {title}\n\n" : "");
        foreach (var section in notes.Sections.Keys.Order())
        {
            text.Append($"## {h[section]}\n");
            var items = notes.Sections.GetValueOrDefault(section) ?? [];
            if (items.Count == 0) text.Append($"- {none}\n");
            foreach (var item in items) text.Append(section == Section.Tasks ? $"- [ ] {item}\n" : $"- {item}\n");
            text.Append('\n');
        }
        text.Append($"## {(language == "pl" ? "Tematy" : "Topics")}\n");
        if (notes.Topics.Count == 0) text.Append($"- {none}\n");
        foreach (var (time, topic) in notes.Topics) text.Append($"- [{time}] {topic}\n");
        return text.ToString();
    }

    // Notatka w drugim języku = tłumaczenie punktów notatki głównej (nie osobna notatka: dwie niezależne notatki z wywiadu TVN
    // mówiły różne rzeczy - EN "single doll", PL film "Lalka"). tłumaczenie krzyżowe (Translator: jeden model tłumaczy, drugi sprawdza); nagłówki i czasy bez zmian.
    public static async Task<Notes> TranslateAsync(Notes notes, string from, string to, Settings s, WorkNotes? work, CancellationToken ct)
    {
        var ordered = notes.Sections.OrderBy(x => x.Key).ToList();
        var texts = ordered.SelectMany(x => x.Value).Concat(notes.Topics.Select(t => t.Text)).ToList();
        var translated = texts.Count == 0 ? [] : await Translator.TranslateAsync(texts, from, to, s, ct);
        // nazwy z oryginału muszą przetrwać (TVN: Gemma zamieniła "Maciej Kawulski" na "Maciej Pawłowski"): jedna próba z listą nazw,
        // potem zostaje oryginał punktu
        for (var i = 0; i < texts.Count; i++)
        {
            var names = Names(texts[i]);
            if (translated[i] == null || MissingNames(names, translated[i]!).Count == 0) continue;
            var retry = await Ollama.GenerateAsync($"Translate from {Translator.Name(from)} to {Translator.Name(to)}. Keep these names exactly: " +
                $"{string.Join(", ", names)}. Answer with the translation only.\n\n{texts[i]}", Translator.Models(to, s).Translator, s, ct, 2048, 300);
            retry = retry.Replace("*", "").Trim().Split('\n')[0].Trim();
            translated[i] = MissingNames(names, retry).Count == 0 && retry.Length > 0 ? retry : null;
        }
        // brak tłumaczenia punktu = zostaje oryginał
        var queue = new Queue<string>(texts.Select((t, i) => translated[i] ?? t));
        var result = new Notes(ordered.ToDictionary(x => x.Key, x => x.Value.Select(_ => queue.Dequeue()).ToList()),
            notes.Topics.Select(t => (t.Time, queue.Dequeue())).ToList());
        work?.Write("10-notes-translation.md", $"Tłumaczenie notatek {from.ToUpperInvariant()} -> {to.ToUpperInvariant()}",
            $"{s.CheckModel} tłumaczy punkty, {s.CorrectionModel} sprawdza każdy z oryginałem.",
            "Przetłumacz każdy punkt notatki; drugi model porównuje oryginał z tłumaczeniem i poprawia różnice. Nagłówki, czasy i nazwy bez zmian.",
            $"gotowe: {texts.Count} punktów", Render(result, to, ""));
        return result;
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

    static string ListPrompt(Section section, string language, string transcript, string known = "") => language == "pl"
        ? Known(language, known) + $"Oto transkrypcja rozmowy. Wypisz {Ask(section, language)}. Każdy punkt w osobnej linii zaczynającej się od \"- \", krótko, po polsku. " +
          "Tylko to, co wprost jest w transkrypcji - nie dopowiadaj, nie cytuj wypowiedzi, bez etykiet typu \"Decyzja:\". " +
          "\"Ja\" to autor notatki, \"Rozmówca 1\", \"Rozmówca 2\" itd. to inni uczestnicy; " +
          "jeśli ktoś przedstawia się z imienia, użyj imienia. Jeśli nic takiego nie ma, odpowiedz: - brak\n\n## TRANSKRYPCJA\n" + transcript
        : Known(language, known) + $"Here is a conversation transcript. List {Ask(section, language)}. One short point per line starting with \"- \", in English. " +
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

    public static string FixBatchPrompt(string language, string transcript, IReadOnlyList<string> statements, string known = "") => (language == "pl"
        ? "## TRANSKRYPCJA\n" + transcript + "\n\n" + Known(language, known) + "Te numerowane punkty notatki nie zgadzają się z transkrypcją. Napisz każdy od nowa tak, żeby mówił tylko to, " +
          "co wprost jest w transkrypcji, krótko, po polsku, bez cytatów. Jeśli transkrypcja w ogóle tego nie mówi, napisz: brak. " +
          "Odpowiedz numerowanymi liniami, jedna na punkt.\n\n"
        : "## TRANSCRIPT\n" + transcript + "\n\n" + Known(language, known) + "These numbered notes do not match the transcript. Rewrite each so it says only what the transcript " +
          "directly says, briefly, in English, without quotes. If the transcript does not say it at all, write: none. " +
          "Answer with numbered lines, one per note.\n\n")
        + string.Concat(statements.Select((x, i) => $"{i + 1}. {x}\n"));

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

    // Punkt do notatki: nie cytat, nie przepisana wypowiedź, nie półzdanie.
    static bool Usable(string bullet, string transcript) => !IsQuote(bullet) && !IsCopied(bullet, transcript) && !IsFragment(bullet);

    public static bool IsYes(string answer) => Regex.IsMatch(answer.Replace("*", ""), @"^\W*(YES|TAK)\b", RegexOptions.IgnoreCase);

    // Punkty z odpowiedzi: linie od "-", "*" albo numeru; "brak"/"none" = brak punktów. Bez Markdown, pól wyboru i powtórzeń.
    public static List<string> Bullets(string answer) =>
        Regex.Matches(answer, @"^\s*(?:[-*•]|\d+[.)])\s+(.+?)\s*$", RegexOptions.Multiline)
            .Select(m => Regex.Replace(Regex.Replace(m.Groups[1].Value.Replace("*", ""), @"^\[\s?x?\]\s*", ""),
                @"^(Decyzja|Decision|Zadanie|Task|Pytanie|Question|Temat|Topic|Otwarta kwestia|Open question)\s*:\s*", "", RegexOptions.IgnoreCase).Trim())
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
