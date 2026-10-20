# VRAM i GPU

## Założenia

- Docelowa karta: **6 GB VRAM**. Każdy model z osobna musi się w niej zmieścić.
- Karta deweloperska: RTX 4060 8 GB. Gdy modele mieszczą się razem, zostają w pamięci (bez wyrzucania na siłę).

## Budżet (`Ollama.cs`)

- `VramBudgetMb` w `settings.json`; `0` = cała karta minus 400 MB (pojemność z `nvidia-smi`).
- Przed każdym zapytaniem kod sprawdza, co jest załadowane (`/api/ps`). Inne modele są zwalniane tylko wtedy, gdy razem z potrzebnym nie mieszczą się w budżecie albo ich rozmiar jest nieznany.
- Przed krokami Whispera i Parakeeta modele tekstu są zwalniane (`UnloadAsync`), bo Whisper large-v3 zajmuje ~4 GB.
- Fazy (najpierw wszystkie zapytania jednego modelu, potem drugiego) ograniczają przełączanie. Przełączenie kosztuje ~7-10 s, a rozgrzana Llama odpowiada w ~0,9 s.

## Zmierzone zajęcie

| Model | VRAM |
|---|---|
| Llama 3.1 8B (kontekst 8192) | 5,68 GB |
| Whisper large-v3 | 4,1 GB |
| Qwen 3.5 4B | ~3,7 GB |
| Gemma 4 E2B (dawniej) | 2,9 GB |
| Parakeet | 2,2 GB |

Llama i Qwen razem (~9,4 GB) nie mieszczą się na 8 GB, więc na obecnej karcie przełączanie zostaje.

## Pułapki

- **Ollama na CPU:** jeśli Ollama wystartowała przed instalacją sterownika NVIDIA, liczy na CPU. Rozwiązanie: restart Ollamy. Po restarcie przebieg TVN skrócił się z ~8 min do ~3:45.
- **Sterownik:** whisper.cpp z CUDA 12 wymaga sterownika 551+ (CUDA 13 wymaga 580+).
