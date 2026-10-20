using System.IO;
using System.Text;

namespace TeamsCatcher.Core;

// Notatki pracy w folderze nagrania (analysis\): jeden plik .md na model/narzędzie z rolą, instrukcją, statusem i wynikiem,
// plus 00-status.md z przeglądem. Modele ładowane są po kolei - z notatek widać, kto co zrobił i na czym stanęło.
public sealed class WorkNotes(string folder)
{
    public const string Dir = "analysis";
    readonly string _dir = Path.Combine(folder, Dir);
    readonly Dictionary<string, (string Title, string Status)> _steps = [];

    public void Write(string file, string title, string role, string instructions, string status, string result = "")
    {
        Directory.CreateDirectory(_dir);
        var text = new StringBuilder($"# {title}\n\n**Rola:** {role}\n\n## Instrukcja\n{instructions.Trim()}\n\n## Status\n{status}\n");
        if (result.Length > 0) text.Append($"\n## Wynik\n{result.Trim()}\n");
        File.WriteAllText(Path.Combine(_dir, file), text.ToString(), Encoding.UTF8);
        _steps[file] = (title, status);
        var overview = new StringBuilder("# Status analizy\n\n| Plik | Model / narzędzie | Status |\n|---|---|---|\n");
        foreach (var (name, step) in _steps.OrderBy(x => x.Key))
            overview.Append($"| [{name}]({name}) | {step.Title} | {step.Status.Split('\n')[0]} |\n");
        File.WriteAllText(Path.Combine(_dir, "00-status.md"), overview.ToString(), Encoding.UTF8);
    }
}
