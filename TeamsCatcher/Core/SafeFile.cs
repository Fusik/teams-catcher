using System.IO;

namespace TeamsCatcher.Core;

public static class SafeFile
{
    // Zapis przez plik tymczasowy i podmianę: przerwa w trakcie (prąd, zabicie procesu) zostawia stary plik, nie ucięty.
    public static void WriteAllText(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    // Kopia pliku przed nadpisaniem wynikiem modelu (np. ręcznie poprawiona notatka) -> analysis\history\nazwa.yyyyMMdd-HHmmss.md.
    public static void Backup(string path)
    {
        if (!File.Exists(path)) return;
        var dir = Path.Combine(Path.GetDirectoryName(path)!, WorkNotes.Dir, "history");
        Directory.CreateDirectory(dir);
        File.Copy(path, Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}.{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}"), overwrite: true);
    }
}
