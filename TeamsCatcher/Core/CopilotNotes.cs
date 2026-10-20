using System.IO;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Notatka przez Copilota w przeglądarce: lokalne modele pisały notatkę z 1,5 h ~25 min i słabo. Transkrypcja powstaje lokalnie,
// użytkownik sam wkleja prompt (notes-style.md + transkrypcja) do Copilota i kopiuje odpowiedź z powrotem - aplikacja nic nie wysyła.
public static class CopilotNotes
{
    public static string StylePath => Path.Combine(AppContext.BaseDirectory, "notes-style.md");

    // Transkrypcja nagrania: polska, jeśli jest, inaczej angielska.
    public static string? TranscriptPath(string folder) =>
        new[] { "pl", "en" }.Select(l => Path.Combine(folder, $"transcript.{l}.md")).FirstOrDefault(File.Exists);

    // Notatka z Copilota obok lokalnych (notes.pl.md / notes.en.md zostają) - widok "Copilot" w panelu RESULT.
    public const string FileName = "notes-copilot.md";

    public static string NotesPath(string transcriptPath) => Path.Combine(Path.GetDirectoryName(transcriptPath)!, FileName);

    // Bez komentarzy <!-- --> (komentarz kończy się "-->" na końcu linii) i znaczników niepewnych słów (<span ...>??słowo??</span> -> "słowo (?)").
    public static string Clean(string text) =>
        Regex.Replace(Regex.Replace(text, @"<!--.*?-->[ \t]*(?=\r?\n|$)", "", RegexOptions.Singleline), @"<span[^>]*>\?\?(.*?)\?\?</span>", "$1 (?)").Trim();

    // attached = transkrypcja jako załączony plik (długie spotkanie, limit długości wiadomości w Copilocie), inaczej w treści.
    public static string Prompt(string style, string transcript, string fileName, bool attached) =>
        "Napisz notatkę ze spotkania na podstawie transkrypcji" + (attached ? $" z załączonego pliku {fileName}" : " poniżej") +
        ", zgodnie z zasadami. Odpowiedz tylko notatką w Markdown, bez wstępu i komentarza.\n\n# ZASADY\n" + Clean(style) +
        (attached ? "" : "\n\n# TRANSKRYPCJA\n" + Clean(transcript));
}
