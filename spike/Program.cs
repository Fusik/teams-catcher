// Spike etapu 0: sprawdza 3 kluczowe techniki zanim powstanie właściwa aplikacja.
//   Spike mic [sekundy]                 - polling rejestru: czy Teams używa mikrofonu
//   Spike windows [nazwaProcesu]        - okna top-level procesu (HWND, klasa, tytuł)
//   Spike audio <pid> <sekundy> <out>   - WASAPI process loopback (tylko dźwięk drzewa procesu)
//   Spike tone <wav> <hz>               - energia częstotliwości w WAV (Goertzel, dB)
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

using NAudio.Wave;

switch (args.ElementAtOrDefault(0))
{
    case "mic": Mic(int.Parse(args.ElementAtOrDefault(1) ?? "60")); break;
    case "windows": Windows(args.ElementAtOrDefault(1) ?? "ms-teams"); break;
    case "audio": await Audio(int.Parse(args[1]), int.Parse(args[2]), args[3]); break;
    case "sessions": Sessions(); break;
    case "diarize":
    {
        var dir = @"C:\Users\WF\Videos\TeamsCatcher\sherpa";
        var config = new SherpaOnnx.OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = Path.Combine(dir, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
        config.Segmentation.NumThreads = 4;
        config.Embedding.Model = Path.Combine(dir, "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx");
        config.Embedding.NumThreads = 4;
        config.Clustering.NumClusters = args.Length > 2 ? int.Parse(args[2]) : -1;
        config.Clustering.Threshold = args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0.5f;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;
        using var sd = new SherpaOnnx.OfflineSpeakerDiarization(config);
        float[] samples;
        using (var r = new AudioFileReader(args[1]))
        {
            var mono = r.WaveFormat.Channels == 1 ? r.ToSampleProvider() : new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(r);
            var res = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(mono, 16000);
            var list = new List<float>(); var buf = new float[16000]; int n;
            while ((n = res.Read(buf, 0, buf.Length)) > 0) list.AddRange(buf.Take(n));
            samples = list.ToArray();
        }
        var sw = Stopwatch.StartNew();
        var segs = sd.Process(samples);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {samples.Length / 16000.0:F1} s audio, {sw.Elapsed.TotalSeconds:F1} s, speakers={segs.Select(x => x.Speaker).Distinct().Count()}");
        foreach (var x in segs) Console.WriteLine($"  {x.Start,7:F2} - {x.End,7:F2}  speaker_{x.Speaker}");
        break;
    }
    case "verify":
    {
        // verify <wav16k mono> <whisper.json> : centroidy mówców z diaryzacji, potem podobieństwo każdego zdania do centroidów
        var dir = @"C:\Users\WF\Videos\TeamsCatcher\sherpa";
        var config = new SherpaOnnx.OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = Path.Combine(dir, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
        config.Embedding.Model = Path.Combine(dir, "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx");
        config.Clustering.NumClusters = -1; config.Clustering.Threshold = 0.6f; config.MinDurationOn = 0.3f; config.MinDurationOff = 0.5f;
        float[] samples;
        using (var r = new WaveFileReader(args[1])) { var sp = r.ToSampleProvider(); samples = new float[r.SampleCount]; sp.Read(samples, 0, samples.Length); }
        using var sd = new SherpaOnnx.OfflineSpeakerDiarization(config);
        var turns = sd.Process(samples);
        var ecfg = new SherpaOnnx.SpeakerEmbeddingExtractorConfig { Model = config.Embedding.Model, NumThreads = 4 };
        using var ex = new SherpaOnnx.SpeakerEmbeddingExtractor(ecfg);
        float[] Emb(double from, double to)
        {
            var a = (int)(from * 16000); var b = Math.Min(samples.Length, (int)(to * 16000));
            var s = ex.CreateStream(); s.AcceptWaveform(16000, samples[a..b]); s.InputFinished(); return ex.Compute(s);
        }
        double Cos(float[] x, float[] y) { double d = 0, nx = 0, ny = 0; for (var i = 0; i < x.Length; i++) { d += x[i] * y[i]; nx += x[i] * x[i]; ny += y[i] * y[i]; } return d / Math.Sqrt(nx * ny); }
        var centroids = turns.Where(t => t.End - t.Start >= 1.5).GroupBy(t => t.Speaker).ToDictionary(g => g.Key, g =>
        {
            var embs = g.Select(t => Emb(t.Start, t.End)).ToList();
            return Enumerable.Range(0, embs[0].Length).Select(i => embs.Average(e => e[i])).ToArray();
        });
        Console.WriteLine("centroids: " + string.Join(",", centroids.Keys));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[2]));
        foreach (var seg in doc.RootElement.GetProperty("transcription").EnumerateArray())
        {
            double from = seg.GetProperty("offsets").GetProperty("from").GetInt64() / 1000.0, to = seg.GetProperty("offsets").GetProperty("to").GetInt64() / 1000.0;
            var overlap = turns.OrderByDescending(t => Math.Min(to, t.End) - Math.Max(from, t.Start)).First().Speaker;
            var e = Emb(from, to);
            var sims = centroids.OrderByDescending(c => Cos(e, c.Value)).Select(c => $"s{c.Key}={Cos(e, c.Value):F2}");
            Console.WriteLine($"{from,6:F2}-{to,6:F2} ({to - from:F1}s) overlap=s{overlap} | {string.Join(" ", sims.Take(3))} | {seg.GetProperty("text").GetString()!.Trim()}");
        }
        break;
    }
    case "api":
        foreach (var t in typeof(SherpaOnnx.OfflineSpeakerDiarization).Assembly.GetExportedTypes().Where(t => t.Name.Contains(args[1])))
        {
            Console.WriteLine($"== {t.FullName}");
            foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                Console.WriteLine($"   {m.MemberType} {m}");
        }
        break;
    case "share": Share(args[1], int.Parse(args[2])); break;
    case "probe": await Probe(args[1]); break;
    case "tone": Tone(args[1], double.Parse(args[2])); break;
    default: Console.WriteLine("usage: mic [s] | windows [proc] | audio <pid> <s> <out.wav> | tone <wav> <hz>"); break;
}

static void Mic(int seconds)
{
    const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    bool? last = null;
    for (var i = 0; i < seconds; i++)
    {
        using var key = Registry.CurrentUser.OpenSubKey(root + @"\MSTeams_8wekyb3d8bbwe");
        var stop = key?.GetValue("LastUsedTimeStop") as long?;
        var active = stop == 0;
        if (active != last) Console.WriteLine($"{DateTime.Now:HH:mm:ss} teams mic active={active} (stop={stop})");
        last = active;
        Thread.Sleep(1000);
    }
}

static void Windows(string processName)
{
    var pids = Process.GetProcessesByName(processName).Select(p => (uint)p.Id).ToHashSet();
    Console.WriteLine($"pids: {string.Join(",", pids)}");
    EnumWindows((h, _) =>
    {
        GetWindowThreadProcessId(h, out var pid);
        if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
        var title = new StringBuilder(512); GetWindowText(h, title, title.Capacity);
        var cls = new StringBuilder(256); GetClassName(h, cls, cls.Capacity);
        GetWindowRect(h, out var r);
        Console.WriteLine($"hwnd={h} pid={pid} {r.Right - r.Left}x{r.Bottom - r.Top} class='{cls}' title='{title}'");
        return true;
    }, 0);
}

static async Task Audio(int pid, int seconds, string outPath)
{
    var format = new WaveFormat(ProcessLoopback.SampleRate, ProcessLoopback.Bits, ProcessLoopback.Channels);
    using var cts = new CancellationTokenSource(seconds * 1000);
    (long bytes, long silent) result;
    using (var writer = new WaveFileWriter(File.Create(outPath), format))
        result = await ProcessLoopback.CaptureAsync(pid, (b, n) => writer.Write(b, 0, n), cts.Token);
    // jeśli audio krótsze niż czas zegara, to proces cichy = brak pakietów = rozjazd z wideo
    Console.WriteLine($"saved {outPath}: {result.bytes / (double)format.AverageBytesPerSecond:F2} s audio / {seconds} s wall, silent packets={result.silent}");
}

// Czeka na mikrofon Teams, potem 30 s nagrywa równolegle kilka wariantów process loopback i loguje okna.
static async Task Probe(string outDir)
{
    Directory.CreateDirectory(outDir);
    const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\MSTeams_8wekyb3d8bbwe";
    var firstSeen = new Dictionary<string, DateTime>();
    Console.WriteLine("waiting for Teams mic...");
    while ((Registry.CurrentUser.OpenSubKey(key)?.GetValue("LastUsedTimeStop") as long?) != 0)
    {
        LogWindows(firstSeen);
        await Task.Delay(500);
    }
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} MIC ACTIVE");
    await Task.Delay(3000);
    var teams = Process.GetProcessesByName("ms-teams");
    var targets = new List<(int pid, bool tree, string name)>();
    // tryb "drzewo" na każdym procesie osobno (proces bez dzieci = tylko on)
    foreach (var p in teams) targets.Add((p.Id, true, $"ms-teams_{p.Id}"));
    foreach (var p in Process.GetProcessesByName("msedgewebview2")) targets.Add((p.Id, true, $"webview_{p.Id}"));
    using var cts = new CancellationTokenSource(30000);
    var tasks = targets.Select(async t =>
    {
        var path = Path.Combine(outDir, t.name + ".wav");
        try
        {
            using var w = new WaveFileWriter(path, new WaveFormat(48000, 16, 2));
            await ProcessLoopback.CaptureAsync(t.pid, (b, n) => w.Write(b, 0, n), cts.Token, t.tree);
        }
        catch (Exception e) { Console.WriteLine($"{t.name}: {e.Message}"); }
    }).ToList();
    while (!cts.IsCancellationRequested) { LogWindows(firstSeen); await Task.Delay(500); }
    await Task.WhenAll(tasks);
    foreach (var f in Directory.GetFiles(outDir, "*.wav"))
    {
        using var r = new AudioFileReader(f);
        var buf = new float[48000]; double sum = 0; long n = 0; int read;
        while ((read = r.Read(buf, 0, buf.Length)) > 0) for (var i = 0; i < read; i++) { sum += buf[i] * buf[i]; n++; }
        Console.WriteLine($"{Path.GetFileName(f)}: {10 * Math.Log10(sum / Math.Max(n, 1) + 1e-12):F1} dB");
    }
}

static void LogWindows(Dictionary<string, DateTime> firstSeen)
{
    var pids = Process.GetProcessesByName("ms-teams").Select(p => (uint)p.Id).ToHashSet();
    EnumWindows((h, _) =>
    {
        GetWindowThreadProcessId(h, out var pid);
        if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
        var title = new StringBuilder(512); GetWindowText(h, title, title.Capacity);
        var cls = new StringBuilder(256); GetClassName(h, cls, cls.Capacity);
        var id = $"hwnd={h} class={cls}";
        if (firstSeen.TryAdd(id, DateTime.Now)) Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} NEW WINDOW {id} title='{title}'");
        return true;
    }, 0);
}

// Sonda udostępniania: wszystkie widoczne okna ms-teams (z ramką udostępniania) + przyciski share w oknach TeamsWebView.
static void Share(string logPath, int seconds)
{
    var prev = "";
    var end = DateTime.Now.AddSeconds(seconds);
    using var log = new StreamWriter(logPath, false) { AutoFlush = true };
    log.WriteLine($"start {DateTime.Now:HH:mm:ss}");
    while (DateTime.Now < end)
    {
        var pids = Process.GetProcessesByName("ms-teams").Select(p => (uint)p.Id).ToHashSet();
        var lines = new List<string>();
        var handles = new List<(nint h, string cls)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            GetWindowRect(h, out var r);
            lines.Add($"WIN h={h} class='{c}' title='{t}' rect={r.Left},{r.Top},{r.Right},{r.Bottom} ex={GetWindowLong(h, -20):X}");
            handles.Add((h, c.ToString()));
            return true;
        }, 0);
        foreach (var (h, cls) in handles.Where(x => x.cls == "TeamsWebView"))
        {
            try
            {
                var buttons = System.Windows.Automation.AutomationElement.FromHandle(h).FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Button));
                foreach (System.Windows.Automation.AutomationElement b in buttons)
                {
                    var n = b.Current.Name; var id = b.Current.AutomationId;
                    if (System.Text.RegularExpressions.Regex.IsMatch($"{n} {id}", "share|udost|prezent|present|microphone-button", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        lines.Add($"   BTN in {h} id='{id}' name='{n}'");
                }
            }
            catch (Exception e) { lines.Add($"   UIA error {e.GetType().Name}"); }
        }
        var cur = string.Join(Environment.NewLine, lines);
        if (cur != prev) { log.WriteLine($"--- {DateTime.Now:HH:mm:ss}{Environment.NewLine}{cur}"); prev = cur; }
        Console.Write(".");
        Thread.Sleep(700);
    }
    log.WriteLine("end");
}

static void Sessions()
{
    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    foreach (var dev in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
    {
        var sessions = dev.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            string name; try { name = Process.GetProcessById((int)s.GetProcessID).ProcessName; } catch { name = "?"; }
            Console.WriteLine($"{dev.FriendlyName} | pid={s.GetProcessID} {name} | state={s.State} | {s.DisplayName}");
        }
    }
}

static void Tone(string path, double hz)
{
    // Goertzel na pierwszym kanale, całe nagranie
    using var reader = new AudioFileReader(path);
    var sp = reader.ToSampleProvider();
    int ch = reader.WaveFormat.Channels, rate = reader.WaveFormat.SampleRate;
    double coeff = 2 * Math.Cos(2 * Math.PI * hz / rate), s1 = 0, s2 = 0;
    long n = 0; var buf = new float[rate * ch];
    int read;
    while ((read = sp.Read(buf, 0, buf.Length)) > 0)
        for (var i = 0; i < read; i += ch) { var s = buf[i] + coeff * s1 - s2; s2 = s1; s1 = s; n++; }
    var power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
    Console.WriteLine($"{hz} Hz: {10 * Math.Log10(power / Math.Max(n, 1) + 1e-12):F1} dB");
}

[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, nint lParam);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
[DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, StringBuilder s, int n);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint h, StringBuilder s, int n);
[DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out Rect r);
[DllImport("user32.dll")] static extern int GetWindowLong(nint h, int i);

delegate bool EnumProc(nint h, nint lParam);
struct Rect { public int Left, Top, Right, Bottom; }
