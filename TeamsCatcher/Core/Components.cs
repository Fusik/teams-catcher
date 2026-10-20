using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

public enum ComponentKind { File, Zip, OllamaModel, PythonEnv, HfModel }

// Składnik pobierany u użytkownika przy pierwszym starcie (paczka ma tylko exe, ffmpeg, modele sherpa i model pyannote - bramkowany
// licencją na Hugging Face, więc jedzie w paczce). Wersje i SHA-256 przypięte: plik o innej sumie jest odrzucany.
// Marker = plik, którego obecność oznacza "zainstalowane" (ścieżki z settings.json - własna konfiguracja też się liczy).
public sealed record Component(string Id, string Name, string Purpose, ComponentKind Kind, long Size, string Marker,
    string Url = "", string Sha256 = "", string Target = "", string? Strip = null, string? Filter = null);

public static class Components
{
    public const string OllamaDir = "ollama";
    static string Base => AppContext.BaseDirectory;
    public static string OllamaExe => Path.Combine(Base, OllamaDir, "ollama.exe");
    public static string PyannoteRequirements => Path.Combine(Base, "pyannote-requirements.txt");

    const string Hf = "https://huggingface.co";
    // pliki potrzebne z wydania whisper.cpp (bez narzędzi testowych), b5454 = v1.9.5
    const string WhisperFiles = @"^(whisper-cli\.exe|whisper\.dll|ggml.*\.dll|cublas.*\.dll|cudart.*\.dll|nvrtc.*\.dll)$";
    // środowisko pyannote: uv instaluje Pythona i pakiety z pyannote-requirements.txt
    const string UvUrl = "https://github.com/astral-sh/uv/releases/download/0.12.18/uv-x86_64-pc-windows-msvc.zip";
    const string UvSha = "cae6a3bc25239f83dffb467a4b180508d9da23986c04639ebfa44e43e6a84bff";
    const string PythonVersion = "3.12.14";
    const string TorchIndex = "https://download.pytorch.org/whl/cu126";
    const long PyannoteDownload = 2_800_000_000;
    // Qwen3-ASR-1.7B (bf16, safetensors) z Hugging Face przez huggingface_hub ze środowiska Pythona
    const string QwenAsrRepo = "Qwen/Qwen3-ASR-1.7B";
    const long QwenAsrSize = 4_700_000_000;

    public static IReadOnlyList<Component> All(Settings s)
    {
        var whisperDir = Path.GetDirectoryName(s.WhisperCli)!;
        List<Component> list =
        [
            new("whisper", "Whisper engine (CUDA 12)", "speech recognition on the GPU", ComponentKind.Zip, 684_913_404, s.WhisperCli,
                "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-win-cuda-12.4.0-x64.zip",
                "afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a", whisperDir, "Release/", WhisperFiles),
            new("large-v3", "Whisper large-v3", "main speech recognition model", ComponentKind.File, 3_095_033_483, s.WhisperModel,
                $"{Hf}/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin", "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2", s.WhisperModel),
            new("vad", "Silero VAD", "finds speech, no phantom phrases in silence", ComponentKind.File, 885_098, s.WhisperVadModel,
                $"{Hf}/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin", "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf", s.WhisperVadModel),
            new("turbo", "Whisper large-v3-turbo", "speech version B for the notes; live transcript during a call", ComponentKind.File, 1_624_555_275, s.WhisperAltModel,
                $"{Hf}/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin", "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", s.WhisperAltModel),
            new("jre", "Java 21 (Temurin JRE)", "runs LanguageTool (only for the Correct option)", ComponentKind.Zip, 48_999_141, s.JavaExe,
                "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip",
                "d35f31e712f0fcf6ac5a093edc90204fbff22f720ba3950bd09d331d5e621636", Path.GetDirectoryName(Path.GetDirectoryName(s.JavaExe)!)!, "jdk-21.0.12.1+1-jre/"),
            new("languagetool", "LanguageTool 6.6", "spelling and grammar check (only for the Correct option)", ComponentKind.Zip, 251_998_221, Path.Combine(s.LanguageToolDir, "languagetool-server.jar"),
                "https://languagetool.org/download/LanguageTool-6.6.zip", "53600506b399bb5ffe1e4c8dec794fd378212f14aaf38ccef9b6f89314d11631", s.LanguageToolDir, "LanguageTool-6.6/"),
            new("ollama", "Ollama 0.40.6 (portable)", "runs the text models locally; skipped when Ollama is already installed", ComponentKind.Zip, 1_471_164_488, OllamaExe,
                "https://github.com/ollama/ollama/releases/download/v0.40.6/ollama-windows-amd64.zip",
                "e359637575302f288d76c0007c408a4be9c7376d59c4f28684d63b232a44b427", Path.Combine(Base, OllamaDir)),
            new("notes", $"Text model {s.NotesModel}", "writes the meeting notes in Polish and English from 3 speech versions", ComponentKind.OllamaModel,
                s.NotesModel == "hf.co/unsloth/Qwen3.5-4B-GGUF:Q3_K_M" ? 3_000_000_000 : 0, "", s.NotesModel),
        ];
        // korekta (opcja Correct): piszący i sprawdzający, jeśli to inne modele niż model notatki
        foreach (var (id, model, purpose, size) in new[]
        {
            ("writer", s.CorrectionModel, "Correct option: proposes fixes of unsure sentences", s.CorrectionModel == "hf.co/LiquidAI/LFM2-2.6B-Transcript-GGUF:Q8_0" ? 2_700_000_000L : 0),
            ("checker", s.CheckModel, "Correct option: checks the writer", s.CheckModel == "hf.co/cenkierpiotr/MEMORYDistillPL-1.5b:Q8_0" ? 1_600_000_000L : 0),
        })
            if (list.All(c => c.Url != model)) list.Add(new(id, $"Text model {model}", purpose, ComponentKind.OllamaModel, size, "", model));
        list.AddRange([
            new("pyannote", "Python + PyTorch CUDA (pyannote, Qwen3-ASR)", "tells the other participants apart on the GPU and runs speech version C",
                ComponentKind.PythonEnv, PyannoteDownload, s.PyannotePython, Target: Path.GetDirectoryName(s.PyannotePython)!),
            // po środowisku Pythona (pobiera nim)
            new("qwen3-asr", "Qwen3-ASR-1.7B", "speech version C for the notes (Alibaba, Polish and English)", ComponentKind.HfModel, QwenAsrSize,
                Path.Combine(s.QwenAsrModel, "config.json"), QwenAsrRepo, Target: s.QwenAsrModel),
        ]);
        return list;
    }

    // Brakujące składniki. Ollama: działający serwer (zainstalowana przez użytkownika) = portable niepotrzebna.
    public static async Task<List<Component>> MissingAsync(Settings s, CancellationToken ct)
    {
        var models = await Ollama.ModelsAsync(s, ct);
        return All(s).Where(c => c switch
        {
            { Id: "ollama" } => models == null && !File.Exists(c.Marker),
            { Kind: ComponentKind.OllamaModel } => models == null || !models.Contains(c.Url),
            _ => !File.Exists(c.Marker),
        }).ToList();
    }

    // Instalacja jednego składnika; progress(status, zrobione bajty, razem bajtów; razem 0 = nieznane).
    public static async Task InstallAsync(Component c, Settings s, Action<string, long, long> progress, CancellationToken ct)
    {
        switch (c.Kind)
        {
            case ComponentKind.File:
                Directory.CreateDirectory(Path.GetDirectoryName(c.Target)!);
                await DownloadAsync(c.Url, c.Sha256, c.Size, c.Target, progress, ct);
                break;
            case ComponentKind.Zip:
                var zip = Path.Combine(DownloadDir, Path.GetFileName(new Uri(c.Url).LocalPath));
                await DownloadAsync(c.Url, c.Sha256, c.Size, zip, progress, ct);
                progress("Extracting...", 0, 0);
                await Task.Run(() => Extract(zip, c.Target, c.Strip, c.Filter), ct);
                File.Delete(zip);
                if (c.Id == "ollama") StartOllama(s);
                break;
            case ComponentKind.OllamaModel:
                if (await Ollama.ModelsAsync(s, ct) == null && StartOllama(s) != null) await WaitForOllamaAsync(s, ct);
                await Ollama.PullAsync(s, c.Url, progress, ct);
                break;
            case ComponentKind.PythonEnv:
                await InstallPythonEnvAsync(c.Target, progress, ct);
                break;
            case ComponentKind.HfModel:
                await DownloadHfModelAsync(c, s, progress, ct);
                break;
        }
    }

    static string DownloadDir => Directory.CreateDirectory(Path.Combine(Base, "downloads")).FullName;

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TeamsCatcher/1.0");
        return http;
    }

    // Pobieranie z wznawianiem (plik .part + Range) i kontrolą SHA-256; zła suma = plik usunięty, wyjątek.
    public static async Task DownloadAsync(string url, string sha256, long size, string file, Action<string, long, long> progress, CancellationToken ct)
    {
        var part = file + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) have = size; // plik .part jest już cały
            else
            {
                response.EnsureSuccessStatusCode();
                if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0; // serwer bez wznawiania - od nowa
                var total = have + (response.Content.Headers.ContentLength ?? Math.Max(0, size - have));
                await using var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[1 << 20];
                var clock = Stopwatch.StartNew();
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    have += read;
                    if (clock.ElapsedMilliseconds > 300)
                    {
                        progress("Downloading...", have, total);
                        clock.Restart();
                    }
                }
            }
        }
        progress("Verifying...", 0, 0);
        string actual;
        await using (var stream = File.OpenRead(part)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException($"{Path.GetFileName(file)}: checksum mismatch (expected {sha256}, got {actual}) - file deleted, try again.");
        }
        File.Move(part, file, true);
    }

    // Rozpakowanie: strip = folder w archiwum, którego zawartość trafia do target; filter = wzorzec nazw plików.
    // Najpierw do target.new, potem przeniesienie plików (target może już mieć inne pliki, np. modele w whisper\).
    public static void Extract(string zip, string target, string? strip, string? filter)
    {
        var temp = Path.GetFullPath(target.TrimEnd('\\', '/') + ".new");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        using (var archive = ZipFile.OpenRead(zip))
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0) continue; // folder
                var relative = entry.FullName.Replace('\\', '/');
                if (strip != null)
                {
                    if (!relative.StartsWith(strip, StringComparison.Ordinal)) continue;
                    relative = relative[strip.Length..];
                }
                if (filter != null && (relative.Contains('/') || !Regex.IsMatch(relative, filter, RegexOptions.IgnoreCase))) continue;
                var destination = Path.GetFullPath(Path.Combine(temp, relative));
                // archiwum z sieci = granica zaufania: ścieżka nie może wyjść poza folder (zip slip)
                if (!destination.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe path in archive: {entry.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        foreach (var file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(temp, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file, destination, true);
        }
        Directory.Delete(temp, true);
    }

    // Ollama portable (folder ollama\ obok exe) - uruchamiana przez aplikację, gdy żadna Ollama nie odpowiada; modele w ollama\models.
    static Process? _ollama;

    public static Process? StartOllama(Settings s)
    {
        if (_ollama is { HasExited: false }) return _ollama;
        if (!File.Exists(OllamaExe)) return null;
        var uri = new Uri(s.OllamaUrl);
        var info = new ProcessStartInfo(OllamaExe, "serve") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(OllamaExe)! };
        info.Environment["OLLAMA_MODELS"] = Path.Combine(Base, OllamaDir, "models");
        info.Environment["OLLAMA_HOST"] = $"{uri.Host}:{uri.Port}";
        // kontekst w pamięci q8_0 = połowa VRAM na token przy praktycznie tej samej jakości (wymaga flash attention);
        // zmienna też w tym procesie - Ollama.ContextAsync liczy wtedy dwa razy większy kontekst
        Environment.SetEnvironmentVariable("OLLAMA_KV_CACHE_TYPE", "q8_0");
        Environment.SetEnvironmentVariable("OLLAMA_FLASH_ATTENTION", "1");
        info.Environment["OLLAMA_KV_CACHE_TYPE"] = "q8_0";
        info.Environment["OLLAMA_FLASH_ATTENTION"] = "1";
        _ollama = Process.Start(info);
        return _ollama;
    }

    public static void StopOllama()
    {
        try { if (_ollama is { HasExited: false }) _ollama.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    static async Task WaitForOllamaAsync(Settings s, CancellationToken ct)
    {
        for (var i = 0; i < 30 && await Ollama.ModelsAsync(s, ct) == null; i++) await Task.Delay(1000, ct);
    }

    // Python (uv, wersja przypięta) do folderu pyannote\ jako samodzielny interpreter + pakiety z pyannote-requirements.txt
    // (ten sam układ co w paczce: pyannote\python.exe). Pobieranie ~2,8 GB, głównie PyTorch z CUDA.
    static async Task InstallPythonEnvAsync(string target, Action<string, long, long> progress, CancellationToken ct)
    {
        var work = Directory.CreateDirectory(Path.Combine(DownloadDir, "uv")).FullName;
        var uvZip = Path.Combine(DownloadDir, "uv.zip");
        await DownloadAsync(UvUrl, UvSha, 17_891_221, uvZip, progress, ct);
        Extract(uvZip, work, null, @"^uv\.exe$");
        File.Delete(uvZip);
        var uv = Path.Combine(work, "uv.exe");
        var pythons = Path.Combine(work, "python");
        var env = new Dictionary<string, string> { ["UV_CACHE_DIR"] = Path.Combine(work, "cache"), ["UV_PYTHON_INSTALL_DIR"] = pythons };
        progress("Installing Python...", 0, 0);
        await RunAsync(uv, $"python install {PythonVersion}", env, progress, ct);
        var installed = Directory.GetDirectories(pythons, $"cpython-{PythonVersion}-windows-x86_64*").FirstOrDefault()
            ?? throw new InvalidOperationException("uv did not install Python.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(installed, target);
        // uv nie podaje postępu pobierania (PyTorch 2,4 GB = kilka minut bez zmian w logu): postęp = rozmiar cache uv
        var cache = env["UV_CACHE_DIR"];
        var status = "Installing PyTorch and pyannote";
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Task.Run(async () =>
        {
            while (!watch.Token.IsCancellationRequested)
            {
                long size = 0;
                try { size = Directory.Exists(cache) ? new DirectoryInfo(cache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
                catch (IOException) { } // pliki w trakcie zapisu
                progress(status, Math.Min(size, PyannoteDownload), PyannoteDownload);
                await Task.Delay(2000, watch.Token).ContinueWith(_ => { });
            }
        });
        try
        {
            await RunAsync(uv, $"pip install --python \"{Path.Combine(target, "python.exe")}\" --break-system-packages --link-mode copy " +
                $"--extra-index-url {TorchIndex} --index-strategy unsafe-best-match -r \"{PyannoteRequirements}\"", env, (line, _, _) => status = line, ct);
        }
        finally
        {
            watch.Cancel();
            await watcher;
        }
        // sprzątanie: cache uv ma pliki tylko do odczytu i dowiązanie do przeniesionego Pythona; nieudane nie psuje instalacji
        try
        {
            foreach (var file in new DirectoryInfo(work).EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            Directory.Delete(work, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Model z Hugging Face do folderu (snapshot_download środowiskiem Pythona); postęp = rozmiar folderu.
    static async Task DownloadHfModelAsync(Component c, Settings s, Action<string, long, long> progress, CancellationToken ct)
    {
        if (!File.Exists(s.PyannotePython)) throw new InvalidOperationException("Install the Python environment first (pyannote).");
        Directory.CreateDirectory(c.Target);
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Task.Run(async () =>
        {
            while (!watch.Token.IsCancellationRequested)
            {
                long size = 0;
                try { size = new DirectoryInfo(c.Target).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
                catch (IOException) { }
                progress("Downloading...", Math.Min(size, c.Size), c.Size);
                await Task.Delay(2000, watch.Token).ContinueWith(_ => { });
            }
        });
        try
        {
            await RunAsync(s.PyannotePython, $"-c \"from huggingface_hub import snapshot_download; snapshot_download('{c.Url}', local_dir=r'{c.Target}')\"",
                new Dictionary<string, string> { ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1" }, (_, _, _) => { }, ct);
        }
        finally
        {
            watch.Cancel();
            await watcher;
        }
    }

    static async Task RunAsync(string exe, string args, Dictionary<string, string> env, Action<string, long, long> progress, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (key, value) in env) info.Environment[key] = value;
        using var p = Process.Start(info)!;
        var last = "";
        void Line(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            last = text.Trim();
            progress(last.Length > 90 ? last[..90] + "..." : last, 0, 0);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw;
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {last}");
    }

    // Miejsce na dysku z folderem aplikacji.
    public static long FreeSpace() => new DriveInfo(Path.GetPathRoot(Base)!).AvailableFreeSpace;
}
