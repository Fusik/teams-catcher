using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamsCatcher.Core;

// Ustawienia trzymane w settings.json obok exe (aplikacja portable).
public sealed class Settings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "TeamsCatcher");
    public bool AutoRecord { get; set; } = true;
    public bool RecordMicrophone { get; set; } = true;
    // Użytkownik wybiera wyniki: nagranie wideo (bez niego meeting.mp4 = sam dźwięk), transkrypcja (AutoTranscribe), notatka (WriteNotes).
    public bool RecordVideo { get; set; } = true;
    // Kawałki w trakcie rozmowy co tyle minut (transkrypcja + notatka częściowa w dymku); 0 = tylko po rozmowie.
    public int LiveChunkMinutes { get; set; } = 5;
    public int Fps { get; set; } = 15;
    // Wysokość wideo (ramka 16:9): 1080 / 1440 / 2160.
    public int VideoHeight { get; set; } = 1080;
    public bool StartWithWindows { get; set; }
    // Transkrypcja whisper.cpp po nagraniu; domyślnie folder "whisper" obok exe (portable).
    public bool AutoTranscribe { get; set; } = true;
    // auto = Whisper wykrywa język każdego kanału (PL/EN), "pl"/"en" = wymuszony.
    public string TranscriptLanguage { get; set; } = "auto";
    // Tłumaczenie PL <-> EN lokalnym modelem w Ollamie.
    public bool TranslateTranscript { get; set; } = true;
    // Nazwiska, firmy, terminy (po przecinku) - podpowiedź dla Whispera i korekty; uzupełniana nazwami z pierwszego przebiegu.
    public string TranscriptGlossary { get; set; } = "";
    // Korekta zdań, w których Whisper był niepewny jakiegoś słowa.
    public bool CorrectTranscript { get; set; } = true;
    // Notatki ze spotkania (podsumowanie, decyzje, zadania) po polsku i po angielsku.
    public bool WriteNotes { get; set; } = true;
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
   // Dwa modele tekstu z różnych rodzin, sprawdzające się krzyżowo (korekta, tłumaczenia, notatki); każdy mieści się w 6 GB VRAM.
    // Bielik 4.5B odrzucony: rozsypywał się przy notatkach i tłumaczeniach (test TVN).
    public string CorrectionModel { get; set; } = "llama3.1:8b";
    // Gemma 4 E2B zastąpiona: pomiar na 14 punktach notatek z nagrań - Gemma 10/14 i 2 zmyślenia przepuszczone, Qwen 3.5 4B 11/14 i 1.
    public string CheckModel { get; set; } = "qwen3.5:4b";
    // Budżet VRAM na modele tekstu (MB); 0 = cała karta. Docelowy komputer: 6 GB. Mieszczą się oba = zostają w pamięci.
    public int VramBudgetMb { get; set; }
    public string WhisperCli { get; set; } = Path.Combine(AppContext.BaseDirectory, "whisper", "whisper-cli.exe");
    public string WhisperModel { get; set; } = Path.Combine(AppContext.BaseDirectory, "whisper", "ggml-large-v3.bin");
    // Druga hipoteza (inny model = inne błędy) dla korekty; brak pliku = bez drugiej hipotezy.
    public string WhisperAltModel { get; set; } = Path.Combine(AppContext.BaseDirectory, "whisper", "ggml-large-v3-turbo.bin");
    public string WhisperVadModel { get; set; } = Path.Combine(AppContext.BaseDirectory, "whisper", "ggml-silero-v5.1.2.bin");
    // Trzecia hipoteza: NVIDIA Parakeet (inna architektura niż Whisper), parakeet-cli obok whisper-cli; brak pliku = bez niej.
    public string ParakeetModel { get; set; } = Path.Combine(AppContext.BaseDirectory, "whisper", "ggml-parakeet-tdt-0.6b-v3-f16.bin");
    // Pisownia i gramatyka (LanguageTool na Javie); brak plików = bez sprawdzania.
    public string JavaExe { get; set; } = Path.Combine(AppContext.BaseDirectory, "jre", "bin", "java.exe");
    public string LanguageToolDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "languagetool");
    // Najwięcej rund dyskusji modeli o jednym zdaniu; bez zgody słowo zostaje oznaczone ??tak??.
    public int DebateRounds { get; set; } = 3;
    // Rozróżnianie rozmówców po stronie Teams (sherpa-onnx). Speakers 0 = automatycznie (wg progu).
    public string DiarizationSegmentationModel { get; set; } = Path.Combine(AppContext.BaseDirectory, "diarization", "pyannote-segmentation-3-0.onnx");
    public string DiarizationEmbeddingModel { get; set; } = Path.Combine(AppContext.BaseDirectory, "diarization", "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx");
    // Diaryzacja na GPU: pyannote community-1 (Python + PyTorch CUDA w folderze pyannote, model pobrany raz z Hugging Face).
    // Brak któregoś z nich = sherpa-onnx na CPU (modele powyżej).
    public string PyannotePython { get; set; } = Path.Combine(AppContext.BaseDirectory, "pyannote", "python.exe");
    public string PyannoteModel { get; set; } = Path.Combine(AppContext.BaseDirectory, "pyannote-models", "community-1");
    public float DiarizationThreshold { get; set; } = 0.6f;
    public int DiarizationSpeakers { get; set; }
    public long MinFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    // Nazwa procesu nowego Teams; w pliku, bo Microsoft może ją zmienić.
    public string TeamsProcess { get; set; } = "ms-teams";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load(string path, Action<string> log)
    {
        if (!File.Exists(path)) return new Settings();
        Settings? s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)); }
        catch (JsonException e)
        {
            log($"Settings file is invalid, using defaults: {e.Message}");
            return new Settings();
        }
        s ??= new Settings();
        // plik edytowalny ręcznie = granica zaufania
        s.Fps = Math.Clamp(s.Fps, 1, 60);
        if (s.VideoHeight is not (1080 or 1440 or 2160)) s.VideoHeight = 1080;
        var defaults = new Settings();
        if (string.IsNullOrWhiteSpace(s.OutputFolder)) s.OutputFolder = defaults.OutputFolder;
        // język i ścieżki trafiają do linii poleceń whisper-cli
        if (!Regex.IsMatch(s.TranscriptLanguage ?? "", "^[a-z]{2,3}$|^auto$")) s.TranscriptLanguage = defaults.TranscriptLanguage;
        if (string.IsNullOrWhiteSpace(s.WhisperCli) || s.WhisperCli.Contains('"')) s.WhisperCli = defaults.WhisperCli;
        if (string.IsNullOrWhiteSpace(s.WhisperModel) || s.WhisperModel.Contains('"')) s.WhisperModel = defaults.WhisperModel;
        if (string.IsNullOrWhiteSpace(s.WhisperVadModel) || s.WhisperVadModel.Contains('"')) s.WhisperVadModel = defaults.WhisperVadModel;
        if (s.WhisperAltModel == null || s.WhisperAltModel.Contains('"')) s.WhisperAltModel = defaults.WhisperAltModel;
        if (s.ParakeetModel == null || s.ParakeetModel.Contains('"')) s.ParakeetModel = defaults.ParakeetModel;
        if (string.IsNullOrWhiteSpace(s.JavaExe)) s.JavaExe = defaults.JavaExe;
        if (string.IsNullOrWhiteSpace(s.LanguageToolDir)) s.LanguageToolDir = defaults.LanguageToolDir;
        s.DebateRounds = Math.Clamp(s.DebateRounds, 1, 6);
        s.LiveChunkMinutes = Math.Clamp(s.LiveChunkMinutes, 0, 60);
        if (string.IsNullOrWhiteSpace(s.CorrectionModel)) s.CorrectionModel = defaults.CorrectionModel;
        if (string.IsNullOrWhiteSpace(s.CheckModel)) s.CheckModel = defaults.CheckModel;
        if (!Uri.TryCreate(s.OllamaUrl, UriKind.Absolute, out var ollama) || ollama.Scheme is not ("http" or "https")) s.OllamaUrl = defaults.OllamaUrl;
       s.TranscriptGlossary ??= "";
        if (string.IsNullOrWhiteSpace(s.DiarizationSegmentationModel)) s.DiarizationSegmentationModel = defaults.DiarizationSegmentationModel;
        if (string.IsNullOrWhiteSpace(s.DiarizationEmbeddingModel)) s.DiarizationEmbeddingModel = defaults.DiarizationEmbeddingModel;
        s.DiarizationThreshold = Math.Clamp(s.DiarizationThreshold, 0.1f, 1.5f);
        s.DiarizationSpeakers = Math.Clamp(s.DiarizationSpeakers, 0, 20);
        return s;
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
