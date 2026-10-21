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

    // Kontekst modelu: największy, przy którym wagi + pamięć kontekstu mieszczą się w budżecie VRAM (model w 100% na GPU),
    // między 4096 a 16k: notatki idą 5-minutowymi częściami (~2-3 tys. tokenów), większy kontekst tylko zajmuje VRAM,
    // a kontekst wypchnięty do RAM liczy się wielokrotnie wolniej. Ten sam dla wszystkich zapytań do modelu: Ollama 0.40 nie przeładowuje modelu, gdy kolejne zapytanie
    // chce większego num_ctx - dyskusja (4096) ładowała model pierwsza, a notatki (8192) dostawały ucięty początek promptu.
    public const int MinContext = 4096, MaxContext = 262144; // praktycznie limit modelu (Qwen 3.5: 256k), reszta = ile mieści VRAM
    // bufory obliczeń i reszta poza wagami i kontekstem: RTX 4060 8 GB - Llama 8B z 16384 weszła w 13% na CPU, z 12288 w 100% na GPU
    // (zapas 500 MB dawał 16384 i przeładowanie przy każdym starcie)
    const long Overhead = 1536L * 1024 * 1024;
    static readonly Dictionary<string, int> Contexts = [];

    // nowy budżet VRAM (zakładka MODELS) = konteksty liczone od nowa
    public static void ResetContexts() => Contexts.Clear();

    // Górny limit kontekstu dla zapytań z tego przepływu zadań (notatki w trakcie rozmowy, obok Whispera: mały kontekst = mało VRAM); 0 = bez limitu.
    public static readonly AsyncLocal<int> ContextLimit = new();

    public static async Task<int> ContextAsync(string model, Settings s, CancellationToken ct)
    {
        var context = await FittingContextAsync(model, s, ct);
        return ContextLimit.Value is > 0 and var limit ? Math.Min(context, limit) : context;
    }

    static async Task<int> FittingContextAsync(string model, Settings s, CancellationToken ct)
    {
        if (Contexts.TryGetValue(model, out var known)) return known;
        int context;
        try
        {
            using var show = await Http.PostAsJsonAsync(new Uri(new Uri(s.OllamaUrl), "/api/show"), new { model }, ct);
            show.EnsureSuccessStatusCode();
            using var info = JsonDocument.Parse(await show.Content.ReadAsStringAsync(ct));
            var weights = await WeightsAsync(model, s, ct);
            var (perToken, modelMax) = KvPerToken(info.RootElement.GetProperty("model_info"));
            // KV cache q8_0 (OLLAMA_KV_CACHE_TYPE, ustawiane dla Ollamy portable) = połowa pamięci na token
            if (KvCacheType() == "q8_0") perToken /= 2;
            context = FitContext(await BudgetAsync(s), weights, perToken, modelMax);
            Log?.Invoke($"  model {model}: context {context} tokens (fits the {(await BudgetAsync(s)) >> 20} MB VRAM budget, KV cache {KvCacheType()})");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            context = 8192;
        }
        return Contexts[model] = context;
    }

    // Największy kontekst (wielokrotność 2048) mieszczący się w budżecie razem z wagami; min 4096, max 128k i limit modelu.
    public static int FitContext(long budget, long weights, long perToken, int modelMax)
    {
        if (perToken <= 0 || weights <= 0) return 8192;
        var tokens = (budget - weights - Overhead) / perToken;
        var max = modelMax > 0 ? Math.Min(modelMax, MaxContext) : MaxContext;
        return (int)Math.Max(MinContext, Math.Min(max, tokens) / 2048 * 2048);
    }

    // Pamięć kontekstu na token (f16): K + V w każdej warstwie z uwagą. Modele hybrydowe (Qwen 3.5): uwaga tylko co n-tą warstwę.
    public static (long PerToken, int ModelMax) KvPerToken(JsonElement modelInfo)
    {
        var arch = modelInfo.GetProperty("general.architecture").GetString() ?? "";
        long Get(string key, long fallback = 0) => modelInfo.TryGetProperty($"{arch}.{key}", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : fallback;
        var layers = Get("block_count");
        if (Get("full_attention_interval") is > 1 and var interval) layers /= interval;
        var heads = Get("attention.head_count", 1);
        var headDim = Get("embedding_length") / Math.Max(1, heads);
        var key = Get("attention.key_length", headDim);
        var value = Get("attention.value_length", headDim);
        return (layers * Get("attention.head_count_kv", heads) * (key + value) * 2, (int)Get("context_length"));
    }

    static string KvCacheType() =>
        Environment.GetEnvironmentVariable("OLLAMA_KV_CACHE_TYPE") ?? Environment.GetEnvironmentVariable("OLLAMA_KV_CACHE_TYPE", EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable("OLLAMA_KV_CACHE_TYPE", EnvironmentVariableTarget.Machine) ?? "f16";

    static async Task<long> WeightsAsync(string model, Settings s, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await Http.GetStringAsync(new Uri(new Uri(s.OllamaUrl), "/api/tags"), ct));
        return json.RootElement.GetProperty("models").EnumerateArray()
            .Where(m => m.GetProperty("name").GetString() is { } n && (n == model || n == model + ":latest"))
            .Select(m => m.GetProperty("size").GetInt64()).FirstOrDefault();
    }

    static async Task<long> BudgetAsync(Settings s) => s.VramBudgetMb > 0 ? s.VramBudgetMb * 1024L * 1024 : await GpuTotalAsync() - 400L * 1024 * 1024;

    // Log aplikacji: na czym model się załadował (GPU/CPU) i ostrzeżenia o uciętych promptach.
    public static Action<string>? Log { get; set; }

    public static async Task<string> GenerateAsync(string prompt, string model, Settings s, CancellationToken ct, int maxTokens = 1024, string? system = null,
        double temperature = 0.1)
    {
        var contextTokens = await ContextAsync(model, s, ct);
        var loaded = await LoadedAsync(s, ct);
        // załadowany wcześniej z mniejszym kontekstem (inny program, stara wersja) = przeładowanie, inaczej prompt byłby ucięty
        if (loaded.FirstOrDefault(m => m.Name == model) is { Context: > 0 } current && current.Context < contextTokens)
        {
            await UnloadModelAsync(s, model, ct);
            loaded = await LoadedAsync(s, ct);
        }
        var wasLoaded = loaded.Any(m => m.Name == model);
        if (!wasLoaded)
        {
            var budget = await BudgetAsync(s);
            // nieznany rozmiar (pierwsze użycie) = zwolnij, bezpiecznie dla 6 GB
            if (!Sizes.TryGetValue(model, out var need) || loaded.Sum(m => m.Vram) + need > budget)
                foreach (var other in loaded) await UnloadModelAsync(s, other.Name, ct);
        }
        // think: false - modele z trybem "myślenia" tylko wydłużają odpowiedź; num_predict + repeat_penalty: Llama 3.1 zapętliła się
        var body = new { model, prompt, system, stream = false, think = false, options = new { temperature, num_ctx = contextTokens, num_predict = maxTokens, repeat_penalty = 1.1 } };
        using var response = await Http.PostAsJsonAsync(new Uri(new Uri(s.OllamaUrl), "/api/generate"), body, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var answer = json.RootElement.GetProperty("response").GetString() ?? "";
        if (!wasLoaded)
        {
            var now = await LoadedAsync(s, ct);
            foreach (var m in now) Sizes[m.Name] = m.Vram;
            if (now.FirstOrDefault(m => m.Name == model) is { Name.Length: > 0 } info)
            {
                Log?.Invoke($"  model {model} loaded: {Placement(info.Size, info.Vram)}, context {info.Context}");
                // Ollama liczy własne bufory - część na CPU = mniejszy kontekst i przeładowanie przy następnym zapytaniu
                if (info.Vram < info.Size && contextTokens > MinContext && ContextLimit.Value == 0)
                {
                    Contexts[model] = Math.Max(MinContext, contextTokens * 3 / 4 / 2048 * 2048);
                    Log?.Invoke($"  model {model} did not fit on the GPU - context reduced to {Contexts[model]}");
                    await UnloadModelAsync(s, model, ct);
                }
            }
        }
        // prompt dłuższy niż kontekst = Ollama ucina jego początek (transkrypcję) - widać to w liczbie przeczytanych tokenów
        if (json.RootElement.TryGetProperty("prompt_eval_count", out var evaluated) && evaluated.GetInt32() >= contextTokens - 8)
            Log?.Invoke($"  WARNING: prompt for {model} filled the whole context ({evaluated.GetInt32()} tokens) - its beginning may be cut off.");
        // diagnostyka jakości: TC_LLM_LOG=plik -> zapytania i odpowiedzi modeli (treść spotkania, tylko lokalnie)
        if (Environment.GetEnvironmentVariable("TC_LLM_LOG") is { Length: > 0 } logFile)
            await File.AppendAllTextAsync(logFile, $"===== {model}\n{prompt}\n----- answer\n{answer}\n\n", ct);
        return answer;
    }

    // Zwalnia VRAM przed Whisperem: Whisper large-v3 (3 GB) + model tekstu (~5 GB) nie mieszczą się razem na 8 GB RTX 4060.
    public static async Task UnloadAsync(Settings s, CancellationToken ct)
    {
        foreach (var model in new[] { s.CorrectionModel, s.CheckModel, s.NotesModel }) await UnloadModelAsync(s, model, ct);
    }

    // "100% GPU" albo "35% CPU / 65% GPU" - jak w "ollama ps" (część na CPU = wielokrotnie wolniej).
    public static string Placement(long size, long vram) =>
        size <= 0 ? "unknown" : vram >= size ? "100% GPU" : vram <= 0 ? "100% CPU"
        : $"{Math.Round(100.0 * (size - vram) / size)}% CPU / {Math.Round(100.0 * vram / size)}% GPU";

    // Modele w pamięci: nazwa, rozmiar, zajęte bajty VRAM, kontekst.
    static async Task<List<(string Name, long Vram, long Size, int Context)>> LoadedAsync(Settings s, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await Http.GetStringAsync(new Uri(new Uri(s.OllamaUrl), "/api/ps"), ct));
            return json.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => (m.GetProperty("name").GetString() ?? "", m.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0,
                    m.TryGetProperty("size", out var size) ? size.GetInt64() : 0, m.TryGetProperty("context_length", out var c) ? c.GetInt32() : 0)).ToList();
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
    public static async Task<bool> IsAvailableAsync(Settings s, CancellationToken ct, IReadOnlyList<string>? models = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var json = JsonDocument.Parse(await Http.GetStringAsync(new Uri(new Uri(s.OllamaUrl), "/api/tags"), timeout.Token));
            var names = json.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "").ToHashSet();
            return (models ?? [s.CorrectionModel, s.CheckModel]).All(m => names.Contains(m) || names.Contains(m + ":latest"));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }
}
