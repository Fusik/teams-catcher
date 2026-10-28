# Wersja portable: mała paczka (exe + ffmpeg + modele sherpa + model pyannote), reszta pobiera się u użytkownika przy pierwszym starcie
# (okno "Components": Whisper, Java + LanguageTool, Ollama i modele tekstu, Python + PyTorch (pyannote, Qwen3-ASR), Qwen3-ASR - wersje i SHA-256
# przypięte w Core/Components.cs). Model pyannote jedzie w paczce, bo na Hugging Face wymaga akceptacji warunków (konto + token).
# -Full: paczka offline ze wszystkim, co jest na tym komputerze (bez pobierania u użytkownika; z Ollamą i modelem tekstu Qwen).
# Użycie: powershell -File publish.ps1 [-Out dist] [-NoZip] [-Full]
param(
    [string]$Out = "$PSScriptRoot\dist\TeamsCatcher",
    [string]$WhisperBin = "C:\Users\WF\Videos\TeamsCatcher\whisper-cuda\bin\Release",
    [string]$WhisperModels = "C:\Users\WF\Videos\TeamsCatcher\whisper.cpp",
    [string]$Sherpa = "C:\Users\WF\Videos\TeamsCatcher\sherpa",
    [string]$Tools = "C:\Users\WF\Videos\TeamsCatcher\tools",
    [string]$Ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source,
    [switch]$NoZip,
    # gdzie zapisać zip; inny dysk niż źródło = szybciej (czyta z jednego, pisze na drugi)
    [string]$ZipDir = (Split-Path $Out -Parent),
    # folder modelu Qwen3-ASR (tylko -Full)
    [string]$QwenAsr = "$PSScriptRoot\TeamsCatcher\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\models\Qwen3-ASR-1.7B",
    # Ollama i modele tekstu na tym komputerze (tylko -Full); nazwy jak w Settings.cs (notatki, korekta, sprawdzający)
    [string]$OllamaHome = "$env:LOCALAPPDATA\Programs\Ollama",
    [string]$OllamaModels = "$env:USERPROFILE\.ollama\models",
    [string[]]$TextModels = @("unsloth/Qwen3.5-4B-GGUF:Q3_K_M"),
    [switch]$Full
)
$ErrorActionPreference = "Stop"

# źródła sprawdzane przed kasowaniem poprzedniej wersji
$copies = @(
    @("$Sherpa\sherpa-onnx-pyannote-segmentation-3-0\model.onnx", "diarization\pyannote-segmentation-3-0.onnx"),
    @("$Sherpa\3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx", "diarization\3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx"),
    @($Ffmpeg, "ffmpeg.exe")
)
if ($Full) {
    $copies += @(
        @("$WhisperModels\ggml-large-v3.bin", "whisper\ggml-large-v3.bin"),
        @("$WhisperModels\ggml-large-v3-turbo.bin", "whisper\ggml-large-v3-turbo.bin"),
        @("$WhisperModels\ggml-silero-v5.1.2.bin", "whisper\ggml-silero-v5.1.2.bin")
    )
    foreach ($f in "$WhisperBin\whisper-cli.exe", "$QwenAsr\config.json", "$Tools\jre\bin\java.exe", "$Tools\languagetool\languagetool-server.jar", "$OllamaHome\ollama.exe", "$OllamaModels\manifests\hf.co\$($TextModels[0] -replace ':', '\')") { if (-not (Test-Path $f)) { throw "Missing: $f" } }
}
foreach ($c in $copies) { if (-not (Test-Path $c[0])) { throw "Missing: $($c[0])" } }
$pyModel = "$Tools\pyannote-models\community-1"
if (-not (Test-Path "$pyModel\config.yaml")) { throw "Missing pyannote model: $pyModel (download once: hf download pyannote/speaker-diarization-community-1)" }

Write-Host "1/4 Czyszczenie starego folderu i dotnet publish..."
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
# self-contained single-file: nie wymaga zainstalowanego .NET; biblioteki natywne (onnxruntime) rozpakowywane przy starcie
dotnet publish "$PSScriptRoot\TeamsCatcher\TeamsCatcher.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "2/4 Kopiowanie modeli i narzedzi do paczki (robocopy, wielowatkowo)..."
# robocopy: wielowątkowe, bez bufora (/J) - dużo szybsze niż Copy-Item; kody < 8 = sukces; .cache z Hugging Face pomijane
function Rc($src, $dst, [string[]]$files = @()) {
    robocopy $src $dst @files /E /MT:16 /J /NFL /NDL /NJH /NJS /NP /XD .cache | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $src -> $dst" }
}
function RcFile($file, $dstDir) { Write-Host "  $(Split-Path $file -Leaf)"; Rc (Split-Path $file) $dstDir @(Split-Path $file -Leaf) }
New-Item -ItemType Directory -Force "$Out\diarization", "$Out\pyannote-models" | Out-Null
foreach ($c in $copies) {
    New-Item -ItemType Directory -Force (Split-Path "$Out\$($c[1])") | Out-Null
    if ((Split-Path $c[0] -Leaf) -eq (Split-Path $c[1] -Leaf)) { RcFile $c[0] (Split-Path "$Out\$($c[1])") } else { Copy-Item $c[0] "$Out\$($c[1])" }
}
Rc $pyModel "$Out\pyannote-models\community-1"

if ($Full) {
    Write-Host "  whisper (silnik + dll)"
    Rc $WhisperBin "$Out\whisper" @("whisper-cli.exe", "whisper.dll", "ggml*.dll", "cublas*.dll", "cudart*.dll", "nvrtc*.dll")
    Write-Host "  jre"; Rc "$Tools\jre" "$Out\jre"
    Write-Host "  languagetool"; Rc "$Tools\languagetool" "$Out\languagetool"
    # wersja C (Qwen3-ASR) - folder modelu jak po pobraniu w oknie Components
    Write-Host "  Qwen3-ASR"; Rc $QwenAsr "$Out\models\Qwen3-ASR-1.7B"
    # venv uv wskazuje na Pythona w profilu użytkownika, więc do paczki idzie samodzielny Python + pakiety venv
    # Ollama (exe + lib z instalacji) i modele tekstu (manifesty + blob-y z ~\.ollama\models) w ollama\ - jak portable: OLLAMA_MODELS = ollama\models
    New-Item -ItemType Directory -Force "$Out\ollama\models\blobs" | Out-Null
    Write-Host "  ollama"; RcFile "$OllamaHome\ollama.exe" "$Out\ollama"; Rc "$OllamaHome\lib" "$Out\ollama\lib"
    foreach ($m in $TextModels) {
        $manifest = "manifests\hf.co\$($m -replace ':', '\')"
        New-Item -ItemType Directory -Force (Split-Path "$Out\ollama\models\$manifest") | Out-Null
        Copy-Item "$OllamaModels\$manifest" "$Out\ollama\models\$manifest"
        $json = Get-Content "$OllamaModels\$manifest" -Raw | ConvertFrom-Json
        foreach ($d in @($json.config.digest) + @($json.layers.digest)) { RcFile "$OllamaModels\blobs\$($d -replace ':', '-')" "$Out\ollama\models\blobs" }
    }
    $pyEnv = "$Tools\pyannote"
    $pyHome = ((Get-Content "$pyEnv\pyvenv.cfg") -match '^home\s*=' -replace '^home\s*=\s*', '').Trim()
    Write-Host "  python + pakiety"; Rc $pyHome "$Out\pyannote"
    Rc "$pyEnv\Lib\site-packages" "$Out\pyannote\Lib\site-packages"
}

@"
WF BIM - Teams Catcher (portable)

Start: TeamsCatcher.exe (pierwszy start tworzy settings.json obok exe).
Nagrania: Wideo\TeamsCatcher (zmiana w oknie: SETTINGS).

Pierwszy start: okno "Components" pobiera modele i silniki (ok. 15-18 GB, jednorazowo, z oficjalnych źródeł, sprawdzane SHA-256):
- Whisper large-v3 / turbo, Parakeet, Silero VAD i silnik whisper.cpp (CUDA 12),
- Java 21 + LanguageTool 6.6,
- Ollama (portable, chyba że jest już zainstalowana) i modele llama3.1:8b, qwen3.5:4b,
- Python + PyTorch + pyannote (rozdzielanie rozmówców na GPU).
Nagrywanie działa od razu. Pobieranie można schować i wrócić do niego z menu traya: Components...
Folder musi mieć prawo zapisu (np. nie Program Files).

Wymagania:
- Windows 10/11 x64, Microsoft Teams (nowy).
- Karta NVIDIA ze sterownikiem 551 lub nowszym (CUDA 12). Bez niej Whisper liczy na CPU (wolno).

Wyniki w folderze nagrania:
- transcript.pl.md / transcript.en.md - transkrypcja, niepewne słowa ??tak?? na czerwono.
- notes.pl.md / notes.en.md - notatki.
- analysis\ - notatki pracy modeli (00-status.md = przegląd).

Wszystko działa lokalnie, nagrania nie są nigdzie wysyłane.
Model pyannote community-1: CC-BY-4.0, pyannote (https://huggingface.co/pyannote/speaker-diarization-community-1).
Dokumentacja: docs\ w repozytorium.
"@ | Set-Content "$Out\README.txt" -Encoding utf8

Write-Host "3/4 Liczenie rozmiaru folderu..."
$size = (Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1GB
Write-Host ("Folder: {0} ({1:N2} GB)" -f $Out, $size)

if (-not $NoZip) {
    Write-Host "4/4 Pakowanie zip..."
    New-Item -ItemType Directory -Force $ZipDir | Out-Null
    $zip = Join-Path $ZipDir ((Split-Path $Out -Leaf) + ".zip")
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $sevenZip -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) { $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe" }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ($sevenZip) {
        # 7-Zip: store (-mx=0), wielowątkowo, pasek procentów w oknie; wpisy z nazwą folderu na początku
        Write-Host "Zip przez 7-Zip (bez kompresji) -> $zip"
        Push-Location (Get-Item $Out).Parent.FullName
        try { & $sevenZip a -tzip -mx=0 -mmt=on -bsp1 -bso0 $zip (Split-Path $Out -Leaf); if ($LASTEXITCODE -ne 0) { throw "7z failed ($LASTEXITCODE)" } } finally { Pop-Location }
    } else {
    Add-Type -AssemblyName System.IO.Compression
    # ręczne pakowanie plik po pliku (bez kompresji), żeby co kilka sekund pokazać postęp; wpisy z nazwą folderu na początku
    $root = (Get-Item $Out).Parent.FullName
    $files = Get-ChildItem $Out -Recurse -File
    $total = ($files | Measure-Object Length -Sum).Sum
    Write-Host ("Zip (bez kompresji): {0} plików, {1:N2} GB -> {2}" -f $files.Count, ($total / 1GB), $zip)
    $done = 0L; $n = 0; $last = 0
    $buf = New-Object byte[] (4MB)
    $fs = [IO.File]::Create($zip)
    $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $files) {
            $n++
            $entry = $za.CreateEntry($f.FullName.Substring($root.Length + 1).Replace('\', '/'), [IO.Compression.CompressionLevel]::NoCompression)
            $es = $entry.Open(); $in = [IO.File]::OpenRead($f.FullName)
            try {
                while (($r = $in.Read($buf, 0, $buf.Length)) -gt 0) {
                    $es.Write($buf, 0, $r); $done += $r
                    if ($sw.ElapsedMilliseconds - $last -ge 3000) {
                        $last = $sw.ElapsedMilliseconds
                        Write-Host ("Zip: {0:N2} / {1:N2} GB ({2:P0}), plik {3}/{4}: {5}" -f ($done / 1GB), ($total / 1GB), ($done / $total), $n, $files.Count, $f.Name)
                    }
                }
            } finally { $in.Dispose(); $es.Dispose() }
        }
    } finally { $za.Dispose(); $fs.Dispose() }
    }
    Write-Host ("Zip gotowy: {0} ({1:N2} GB) w {2:N0} s" -f $zip, ((Get-Item $zip).Length / 1GB), $sw.Elapsed.TotalSeconds)
}
