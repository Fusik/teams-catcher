# Rozwiązania odrzucone i dlaczego

| Rozwiązanie | Dlaczego nie |
|---|---|
| Wykrywanie rozmowy z rejestru mikrofonu (`ConsentStore`) | ekran przed dołączeniem i "Dołącz ponownie" też trzymają mikrofon, przez co nagrywał się ogon 15-20 s |
| Process loopback na drzewie `ms-teams.exe` | dźwięk gra w procesach WebView2 i mediów, a na drzewie była cisza (-96,7 dB) |
| `h264_mf` | ignorował bitrate (136 kb/s) |
| Synchronizacja przez `-progress` albo "koniec minus długość" | ffmpeg startuje 0,3-3 s, a WGC nie wysyła klatek statycznego okna |
| Whisper `--diarize` z VAD | skleja wszystko w jeden segment |
| Whisper bez VAD | wymyśla "Dziękuję za oglądanie!" w ciszy |
| Wbudowane wykrywanie języka Whispera | myliło angielski z polskim akcentem (p = 0,54 dla "pl") |
| DTW (`-dtw large.v3`) | nie działa z VAD |
| WhisperX | brak modeli ggml, nie portable |
| Whisper tiny/small jako hipoteza | w polskim myli się co kilka słów |
| Korekta LLM bez drugiej hipotezy | małe modele zgadują ("jamowym", "jedenastym") |
| Luźna korekta całych zdań | "do stanarzysty. Pytanie," → "do Maciej Sztur." |
| Lista słów z błędnego tekstu jako kontekst | utrwalała błąd ("Jament Golem") |
| Paczki zdań w jednym zapytaniu (4B) | model przesuwał numerację |
| Bielik 4.5B | powtarzał punkty, mylił zadania, oddawał oryginał zamiast tłumaczenia |
| Gemma 4 E2B jako sprawdzający | przepuszczała zmyślenia; ocena sensu była szumem (82% "inny sens") |
| Ocena sensu jako wyrok | zastąpiona podpowiedzią EN bez wyroku |
| Jedna długa notatka od modelu | rozsypywała się, Llama zapętliła się na 15 min |
| Dwie niezależne notatki PL i EN | mówiły różne rzeczy ("single doll" vs film "Lalka") |
| Korelacja sygnału i Windows AEC na echo | przetwarzanie mikrofonu Realtek psuje korelację, AEC nic nie zmienia |
| Scalanie klastrów mówców po progu podobieństwa | ta sama osoba 0,81-0,92, różne osoby 0,74-0,88, czyli zakresy się nakładają |
| Dymek traya na notatki częściowe | ucinał treść do 250 znaków, znikał w centrum powiadomień |
| `torchaudio.load` w `diarize.py` | nowy torchaudio wymaga bibliotek FFmpeg (torchcodec) |
| qwen3.5:9b | ~6 GB, nie mieścił się z Whisperem na 8 GB |
| 32B LLM | nie mieści się w VRAM |
