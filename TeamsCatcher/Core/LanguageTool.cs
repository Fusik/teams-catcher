using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TeamsCatcher.Core;

// Category: TYPOS = nieistniejące słowo, GRAMMAR/SYNTAX = gramatyka (np. przypadek po przyimku).
public sealed record LanguageIssue(int Offset, int Length, string Word, string Rule, string Message, string Category, string[]? Replacements = null)
{
    public bool Spelling => Category == "TYPOS";
    public bool Relevant => Category is "TYPOS" or "GRAMMAR" or "SYNTAX";
}

// Sprawdzanie pisowni i gramatyki (LanguageTool: reguły + słownik Morfologik dla polskiego), lokalny serwer na Javie.
// Nieistniejące słowo ("jamentowym") to najpewniejszy sygnał błędu rozpoznawania - modele językowe go nie widzą.
public sealed class LanguageTool : IAsyncDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly Process _server;
    readonly Uri _url;

    LanguageTool(Process server, int port) => (_server, _url) = (server, new Uri($"http://127.0.0.1:{port}/v2/check"));

    public static string? MissingFiles(Settings s) =>
        !File.Exists(s.JavaExe) ? s.JavaExe : !File.Exists(Path.Combine(s.LanguageToolDir, "languagetool-server.jar")) ? s.LanguageToolDir : null;

    // Serwer tylko na czas transkrypcji (Java zajmuje ok. 1 GB RAM).
    public static async Task<LanguageTool> StartAsync(Settings s, CancellationToken ct)
    {
        var port = FreePort();
        var server = Process.Start(new ProcessStartInfo(s.JavaExe,
            $"-Xmx1g -cp languagetool-server.jar org.languagetool.server.HTTPServer --port {port}")
        {
            WorkingDirectory = s.LanguageToolDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        server.OutputDataReceived += (_, _) => { };
        server.ErrorDataReceived += (_, _) => { };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        var tool = new LanguageTool(server, port);
        // start serwera: kilka sekund
        for (var i = 0; i < 60; i++)
        {
            if (server.HasExited) throw new InvalidOperationException($"LanguageTool exited with code {server.ExitCode}.");
            try
            {
                await tool.CheckAsync("Test.", "pl", ct);
                return tool;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(500, ct);
            }
        }
        await tool.DisposeAsync();
        throw new TimeoutException("LanguageTool did not start in 30 s.");
    }

    static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task<List<LanguageIssue>> CheckAsync(string text, string language, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent([new("text", text), new("language", language == "en" ? "en-US" : "pl-PL")]);
        using var response = await Http.PostAsync(_url, content, ct);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct), text);
    }

    public static List<LanguageIssue> Parse(string json, string text)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("matches").EnumerateArray().Select(m =>
        {
            int offset = m.GetProperty("offset").GetInt32(), length = m.GetProperty("length").GetInt32();
            var rule = m.GetProperty("rule");
            var category = rule.GetProperty("category").GetProperty("id").GetString() ?? "";
            var word = offset >= 0 && offset + length <= text.Length ? text.Substring(offset, length) : "";
            var replacements = m.TryGetProperty("replacements", out var r) ? r.EnumerateArray().Select(x => x.GetProperty("value").GetString() ?? "").ToArray() : [];
            return new LanguageIssue(offset, length, word, rule.GetProperty("id").GetString() ?? "", m.GetProperty("message").GetString() ?? "", category, replacements);
        }).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_server.HasExited) _server.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        await _server.WaitForExitAsync();
        _server.Dispose();
    }
}
