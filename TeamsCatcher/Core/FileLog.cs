using System.IO;

namespace TeamsCatcher.Core;

// Log na dysku (logs\yyyy-MM-dd.log obok exe), żeby po crashu było co czytać. Tylko komunikaty aplikacji, bez treści transkrypcji.
public static class FileLog
{
    public static string Folder { get; set; } = Path.Combine(AppContext.BaseDirectory, "logs");
    static readonly Lock Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // brak zapisu logu nie może zatrzymać nagrania
    }
}
