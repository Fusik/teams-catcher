using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace TeamsCatcher.Core;

// Lokalne modele językowe przez Ollamę (treść spotkania nie wychodzi z komputera): CorrectionModel (Llama) i CheckModel (Qwen).
public static class Ollama
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    // Modele zostają w VRAM, jeśli mieszczą się w budżecie (przełączanie = przeładowanie); inaczej zwalniane przed załadowaniem
    // kolejnego. Docelowa karta ma 6 GB, ta 8 GB - budżet z ustawień albo z karty. Zajętość z Ollamy (/api/ps), rozmiary zapamiętane.
    static readonly Dictionary<string, long> Sizes = [];
    static long? _gpuTotal;

    public static async Task<string> GenerateAsync(string prompt, string model, Settings s, CancellationToken ct, int contextTokens = 8192, int maxTokens = 1024)
    {
        var loaded = await LoadedAsync(s, ct);
        if (!loaded.Any(m => m.Name == model))
        {
            var budget = s.VramBudgetMb > 0 ? s.VramBudgetMb * 1024L * 1024 : await GpuTotalAsync() - 400L * 1024 * 1024;
            // nieznany rozmiar (pierwsze użycie) = zwolnij, bezpiecznie dla 6 GB
            if (!Sizes.TryGetValue(model, out var need) || loaded.Sum(m => m.Vram) + need > budget)
                foreach (var other in loaded) await UnloadModelAsync(s, other.Name, ct);
        }
        // think: false - modele z trybem "myślenia" tylko wydłużają odpowiedź; num_predict + repeat_penalty: Llama 3.1 zapętliła się
        var body = new { model, prompt, stream = false, think = false, options = new { temperature = 0.1, num_ctx = contextTokens, num_predict = maxTokens, repeat_penalty = 1.1 } };
        using var response = await Http.PostAsJsonAsync(new Uri(new Uri(s.OllamaUrl), "/api/generate"), body, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var answer = json.RootElement.GetProperty("response").GetString() ?? "";
        if (!Sizes.ContainsKey(model))
            foreach (var m in await LoadedAsync(s, ct)) Sizes[m.Name] = m.Vram;
        // diagnostyka jakości: TC_LLM_LOG=plik -> zapytania i odpowiedzi modeli (treść spotkania, tylko lokalnie)
        if (Environment.GetEnvironmentVariable("TC_LLM_LOG") is { Length: > 0 } logFile)
            await File.AppendAllTextAsync(logFile, $"===== {model}\n{prompt}\n----- answer\n{answer}\n\n", ct);
        return answer;
    }

    // Zwalnia VRAM przed Whisperem: Whisper large-v3 (3 GB) + model tekstu (~5 GB) nie mieszczą się razem na 8 GB RTX 4060.
    public static async Task UnloadAsync(Settings s, CancellationToken ct)
    {
        foreach (var model in new[] { s.CorrectionModel, s.CheckModel }) await UnloadModelAsync(s, model, ct);
    }

    // Modele w pamięci karty: nazwa i zajęte bajty VRAM.
    static async Task<List<(string Name, long Vram)>> LoadedAsync(Settings s, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await Http.GetStringAsync(new Uri(new Uri(s.OllamaUrl), "/api/ps"), ct));
            return json.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => (m.GetProperty("name").GetString() ?? "", m.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0)).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException)
        {
            return [];
        }
    }

    // Pamięć karty z nvidia-smi (raz); brak karty NVIDIA = 6 GB jak na docelowym komputerze.
    static async Task<long> GpuTotalAsync()
    {
        if (_gpuTotal is { } total) return total;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            var mib = long.Parse((await p.StandardOutput.ReadToEndAsync()).Split('\n')[0].Trim());
            _gpuTotal = mib * 1024 * 1024;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FormatException or InvalidOperationException)
        {
            _gpuTotal = 6L * 1024 * 1024 * 1024;
        }
        return _gpuTotal.Value;
    }

    static async Task UnloadModelAsync(Settings s, string model, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(new Uri(new Uri(s.OllamaUrl), "/api/generate"), new { model, keep_alive = 0 }, ct);
        }
        catch (HttpRequestException)
        {
            // Ollama niedostępna = nic nie trzyma pamięci
        }
    }

    // Zainstalowane modele; null = serwer Ollamy nie odpowiada.
    public static async Task<HashSet<string>?> ModelsAsync(Settings s, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var json = JsonDocument.Parse(await Http.GetStringAsync(new Uri(new Uri(s.OllamaUrl), "/api/tags"), timeout.Token));
            return json.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "")
                .SelectMany(n => n.EndsWith(":latest") ? [n, n[..^7]] : new[] { n }).ToHashSet();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    // Pobranie modelu (/api/pull, strumień JSON: status, total, completed); progress(status, zrobione, razem).
    public static async Task PullAsync(Settings s, string model, Action<string, long, long> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(s.OllamaUrl), "/api/pull")) { Content = JsonContent.Create(new { model, stream = true }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException($"Ollama pull {model}: {error.GetString()}");
            progress(root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                root.TryGetProperty("completed", out var done) ? done.GetInt64() : 0, root.TryGetProperty("total", out var total) ? total.GetInt64() : 0);
        }
    }

    // Oba modele (piszący i sprawdzający) - bez jednego z nich nie ma korekty, tłumaczenia ani notatek.
    public static async Task<bool> IsAvailableAsync(Settings s, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var json = JsonDocument.Parse(await Http.GetStringAsync(new Uri(new Uri(s.OllamaUrl), "/api/tags"), timeout.Token));
            var names = json.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "").ToHashSet();
            return new[] { s.CorrectionModel, s.CheckModel }.All(m => names.Contains(m) || names.Contains(m + ":latest"));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }
}
