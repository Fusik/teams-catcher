# Wersja portable: mała paczka (exe + ffmpeg + modele sherpa + model pyannote), reszta pobiera się u użytkownika przy pierwszym starcie
# (okno "Components": Whisper, Java + LanguageTool, Ollama i modele tekstu, Python + PyTorch (pyannote, Qwen3-ASR), Qwen3-ASR - wersje i SHA-256
# przypięte w Core/Components.cs). Model pyannote jedzie w paczce, bo na Hugging Face wymaga akceptacji warunków (konto + token).
# -Full: paczka offline ze wszystkim, co jest na tym komputerze (bez pobierania u użytkownika; bez Ollamy).
# Użycie: powershell -File publish.ps1 [-Out dist] [-NoZip] [-Full]
param(
    [string]$Out = "$PSScriptRoot\dist\TeamsCatcher",
    [string]$WhisperBin = "C:\Users\WF\Videos\TeamsCatcher\whisper-cuda\bin\Release",
    [string]$WhisperModels = "C:\Users\WF\Videos\TeamsCatcher\whisper.cpp",
    [string]$Sherpa = "C:\Users\WF\Videos\TeamsCatcher\sherpa",
    [string]$Tools = "C:\Users\WF\Videos\TeamsCatcher\tools",
    [string]$Ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source,
    [switch]$NoZip,
    # folder modelu Qwen3-ASR (tylko -Full)
    [string]$QwenAsr = "$PSScriptRoot\TeamsCatcher\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\models\Qwen3-ASR-1.7B",
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
    foreach ($f in "$WhisperBin\whisper-cli.exe", "$QwenAsr\config.json", "$Tools\jre\bin\java.exe", "$Tools\languagetool\languagetool-server.jar") { if (-not (Test-Path $f)) { throw "Missing: $f" } }
}
foreach ($c in $copies) { if (-not (Test-Path $c[0])) { throw "Missing: $($c[0])" } }
$pyModel = "$Tools\pyannote-models\community-1"
if (-not (Test-Path "$pyModel\config.yaml")) { throw "Missing pyannote model: $pyModel (download once: hf download pyannote/speaker-diarization-community-1)" }

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
# self-contained single-file: nie wymaga zainstalowanego .NET; biblioteki natywne (onnxruntime) rozpakowywane przy starcie
dotnet publish "$PSScriptRoot\TeamsCatcher\TeamsCatcher.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force "$Out\diarization", "$Out\pyannote-models" | Out-Null
foreach ($c in $copies) { New-Item -ItemType Directory -Force (Split-Path "$Out\$($c[1])") | Out-Null; Copy-Item $c[0] "$Out\$($c[1])" }
# model pyannote bez kopii z cache Hugging Face
Copy-Item $pyModel "$Out\pyannote-models\community-1" -Recurse -Exclude ".cache"
Remove-Item "$Out\pyannote-models\community-1\.cache" -Recurse -Force -ErrorAction SilentlyContinue

if ($Full) {
    Get-ChildItem $WhisperBin -File | Where-Object { $_.Name -match '^(whisper-cli\.exe|whisper\.dll|ggml.*\.dll|cublas.*\.dll|cudart.*\.dll|nvrtc.*\.dll)$' } |
        Copy-Item -Destination "$Out\whisper"
    Copy-Item "$Tools\jre" "$Out\jre" -Recurse
    Copy-Item "$Tools\languagetool" "$Out\languagetool" -Recurse
    # wersja C (Qwen3-ASR) - folder modelu jak po pobraniu w oknie Components
    New-Item -ItemType Directory -Force "$Out\models" | Out-Null
    Copy-Item $QwenAsr "$Out\models\Qwen3-ASR-1.7B" -Recurse -Exclude ".cache"
    # venv uv wskazuje na Pythona w profilu użytkownika, więc do paczki idzie samodzielny Python + pakiety venv
    $pyEnv = "$Tools\pyannote"
    $pyHome = ((Get-Content "$pyEnv\pyvenv.cfg") -match '^home\s*=' -replace '^home\s*=\s*', '').Trim()
    Copy-Item $pyHome "$Out\pyannote" -Recurse
    Copy-Item "$pyEnv\Lib\site-packages\*" "$Out\pyannote\Lib\site-packages" -Recurse -Force
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

$size = (Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1GB
Write-Host ("Folder: {0} ({1:N2} GB)" -f $Out, $size)

if (-not $NoZip) {
    $zip = "$Out.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host ("Zip: {0} ({1:N2} GB)" -f $zip, ((Get-Item $zip).Length / 1GB))
}
