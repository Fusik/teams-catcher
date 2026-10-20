# Diaryzacja rozmówców pyannote community-1 na GPU (wywoływana przez TeamsCatcher, Diarizer.cs).
# Użycie: python diarize.py <model_dir> <wav 16 kHz mono> <out.json> [liczba rozmówców, 0 = auto]
# Wynik: {"turns": [{"start": s, "end": s, "speaker": "SPEAKER_00"}, ...], "device": "cuda"}
# Model lokalnie (pobrany raz z Hugging Face po akceptacji warunków) - nagranie nie wychodzi z komputera.
import json
import sys
import wave

import numpy as np
import torch
from pyannote.audio import Pipeline


def main() -> None:
    model_dir, wav, out = sys.argv[1], sys.argv[2], sys.argv[3]
    speakers = int(sys.argv[4]) if len(sys.argv) > 4 else 0
    pipeline = Pipeline.from_pretrained(model_dir)
    device = "cuda" if torch.cuda.is_available() else "cpu"
    pipeline.to(torch.device(device))
    # dźwięk z pamięci przez moduł wave: torchaudio.load (2.9+) wymaga torchcodec z bibliotekami FFmpeg, których w paczce nie ma
    with wave.open(wav, "rb") as f:
        if f.getsampwidth() != 2:
            raise ValueError(f"expected 16-bit PCM wav, got {8 * f.getsampwidth()}-bit")
        sample_rate, channels = f.getframerate(), f.getnchannels()
        samples = np.frombuffer(f.readframes(f.getnframes()), dtype=np.int16).reshape(-1, channels)
    waveform = torch.from_numpy(samples.T.astype(np.float32) / 32768.0)
    args = {"num_speakers": speakers} if speakers > 0 else {}
    result = pipeline({"waveform": waveform, "sample_rate": sample_rate}, **args)
    # community-1: wersja "exclusive" = jeden mówca naraz, łatwiej dopasować do zdań transkrypcji
    annotation = getattr(result, "exclusive_speaker_diarization", None) or getattr(result, "speaker_diarization", result)
    turns = [{"start": turn.start, "end": turn.end, "speaker": speaker} for turn, _, speaker in annotation.itertracks(yield_label=True)]
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"turns": turns, "device": device}, f)


if __name__ == "__main__":
    main()
