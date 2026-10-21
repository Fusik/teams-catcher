# Wersja C rozpoznania mowy: Qwen3-ASR (Alibaba, Apache-2.0) na zdaniach z Whispera.
# asr.py <model> <wav 16 kHz mono> <segments.json [[from_s, to_s], ...]> <out.json> <pl|en|auto> [context]
# Aligner czasów Qwen3 nie obsługuje polskiego, więc każde zdanie A to osobny fragment dźwięku - tekst C trafia od razu do swojego zdania.
import json
import sys
import wave

import numpy as np
import torch
from qwen_asr import Qwen3ASRModel

model_id, wav_path, segments_path, out_path, language = sys.argv[1:6]
context = sys.argv[6] if len(sys.argv) > 6 else ""

with wave.open(wav_path, "rb") as w:
    rate = w.getframerate()
    audio = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0

with open(segments_path, encoding="utf-8") as f:
    segments = json.load(f)

# Whisper bywa, że oddaje jedno "zdanie" na minuty (ciągła mowa, muzyka, pętla halucynacji): taki fragment naraz dał 26 GB RAM i godziny liczenia,
# dlatego fragment dłuższy niż MAX_PIECE_S jest cięty na okna i sklejany z powrotem (owner[i] = zdanie, do którego należy okno).
MAX_PIECE_S = 30.0
pieces = []
owner = []
for n, (start, end) in enumerate(segments):
    # bardzo krótkie zdanie ("Tak.") z zapasem - model potrzebuje trochę dźwięku wokół słowa
    first = max(0, int((start - 0.2) * rate))
    last = min(len(audio), int((max(end, start + 0.8) + 0.2) * rate))
    step = int(MAX_PIECE_S * rate)
    for a in range(first, last, step):
        pieces.append((audio[a:min(a + step, last)], rate))
        owner.append(n)

device = "cuda:0" if torch.cuda.is_available() else "cpu"
model = Qwen3ASRModel.from_pretrained(model_id, dtype=torch.bfloat16 if device != "cpu" else torch.float32, device_map=device,
                                      max_inference_batch_size=16, max_new_tokens=256)
name = {"pl": "Polish", "en": "English"}.get(language)
results = [r.text for r in model.transcribe(audio=pieces, context=context, language=name)] if pieces else []
texts = [""] * len(segments)
for n, piece in zip(owner, results):
    texts[n] = (texts[n] + " " + piece).strip()

with open(out_path, "w", encoding="utf-8") as f:
    json.dump({"device": "cuda" if device != "cpu" else "cpu", "texts": texts}, f, ensure_ascii=False)
