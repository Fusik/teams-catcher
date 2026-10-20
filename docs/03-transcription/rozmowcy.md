# Rozmówcy (diaryzacja)

## Ja / Rozmówcy

Podział po kanałach `transcript.wav`: lewy kanał to mikrofon (Ja), prawy to Teams (Rozmówcy). To jest pewne, bo pochodzi z osobnych źródeł dźwięku.

## Rozmówca 1/2/3 po stronie Teams

1. **pyannote community-1 na GPU** (domyślnie):
   - `diarize.py` uruchamiany Pythonem z folderu `pyannote` (Python 3.12, PyTorch 2.14 cu126, pyannote.audio 4.0.7);
   - model w `pyannote-models\community-1`;
   - wynik `pyannote.json`: tury mówców w wersji `exclusive` (jeden mówca naraz);
   - wav czytany modułem `wave` (16-bit PCM): `torchaudio.load` w nowym torchaudio wymaga bibliotek FFmpeg (torchcodec), których w paczce nie ma;
   - `DiarizationSpeakers > 0` jest przekazywane jako `num_speakers`.
2. **sherpa-onnx na CPU** (fallback, gdy brak Pythona, modelu albo jest błąd):
   - segmentacja pyannote-3.0 (onnx), odcisk głosu 3D-Speaker CAM++ (VoxCeleb);
   - klastrowanie z progiem `DiarizationThreshold` (0,6) albo podaną liczbą rozmówców.
3. **Przypisanie zdań:** segment Whispera dostaje mówcę z największym pokryciem w czasie.
4. **Weryfikacja odciskiem głosu:** zdania ≥ 2 s porównywane z wzorcami mówców (podobieństwo ≥ 0,6, margines 0,15). Krótkie wtrącenia (< 1,5 s) są nierozróżnialne tym modelem, to fizyczne ograniczenie.
5. **Mówca stały w zdaniu** (`Diarizer.RealignBySentence`, jak whisper-diarization): segmenty aż do kropki, pytajnika albo przerwy > 1,5 s dostają mówcę, który mówi w nich najdłużej.
6. **Kawałki na żywo:** diaryzacja raz, na całym nagraniu przy składaniu, żeby numery Rozmówców były spójne.

## Wyniki (nagranie testowe 9,6 min: wywiad TVN ~5 osób + komentarz Heroes ~3 osoby)

| Silnik | Rozmówców | Czas |
|---|---|---|
| pyannote community-1 (GPU) | 9 | 0:39 (z ładowaniem modelu) |
| sherpa-onnx (CPU) | 11 | 0:49-4:10 (duże wahania) |

## Echo mikrofonu

Mikrofon bez słuchawek łapie dźwięk z głośników.
- Korelacja sygnału nie działa: przetwarzanie mikrofonu Realtek psuje ją po ~79 s.
- Windows AEC nic nie zmienia.

Reguła w kodzie: zdanie "Ja" jest echem, gdy mikrofon jest ≥ 3 dB cichszy od Teams i ≥ 30% słów pochodzi od rozmówców w oknie ±2 s.

## Odłożone

Rozpoznawanie, kto mówi, z okna Teams (podświetlony kafelek, nazwisko przez UIA/OCR) wymaga testu na prawdziwej rozmowie z kilkoma osobami.
