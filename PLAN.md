# WF BIM - Teams Catcher: plan, audyt, testy

Portable aplikacja WPF (C#) działająca w tle (ikona w trayu). Wykrywa rozmowę/spotkanie w Microsoft Teams, nagrywa **tylko okno Teams** (obraz) i **tylko dźwięk Teams** (+ opcjonalnie własny mikrofon), zapisuje plik gotowy do transkrypcji.

Status: plan. Kod: brak. Data: 2026-10-19.

---

## 1. Decyzje architektoniczne (do potwierdzenia)

| # | Decyzja | Wybór | Dlaczego |
|---|---|---|---|
| D1 | Framework | **.NET 10, `net10.0-windows10.0.19041.0`, x64**, zamiast `net48` z UI_STYLE | `net48` jest w UI_STYLE z powodu Tekli; tu Tekli nie ma. .NET 10 daje self-contained single-file (prawdziwe portable) i bezpośredni dostęp do WinRT. SDK 10.0.400 jest zainstalowane. |
| D2 | Wykrywanie rozmowy | **Rejestr Windows: kto używa mikrofonu** (`ConsentStore\microphone\MSTeams_8wekyb3d8bbwe`, `LastUsedTimeStop == 0`) | Zero konfiguracji, nie zależy od UI Teams ani języka. Teams otwiera mikrofon dopiero po odebraniu połączenia (dzwonienie przychodzące nie startuje nagrania). Klucz istnieje na tej maszynie (sprawdzone). |
| D3 | Obraz okna | **ffmpeg `gfxcapture`** (Windows.Graphics.Capture, to samo co OBS "Window Capture (WGC)"), parametr `hwnd=` | Sprawdzone: zainstalowany ffmpeg 9.0.2 ma `gfxcapture` z `hwnd`, `display_border=false`, `max_framerate`. Zero własnego kodu D3D11. Plan B: NuGet `ScreenRecorderLib`. |
| D4 | Dźwięk Teams | **WASAPI process loopback - własny interop** (`spike/ProcessLoopback.cs`, ok. 100 linii); NAudio tylko do zapisu WAV | NAudio 2.2.1 nie ma gotowej klasy (tylko struktury). Nagrywa tylko dźwięk drzewa procesów `ms-teams.exe`, nic innego. Wymaga Windows 10 2004+ / 11. |
| D5 | Własny głos | **Mikrofon nagrywany jako osobna ścieżka** (domyślnie włączone, wyłączalne) | Teams nie odtwarza Twojego głosu, więc bez mikrofonu transkrypcja ma tylko połowę rozmowy. |
| D6 | Format | Nagrywanie do **MKV** (odporne na zabicie procesu) -> po zakończeniu remux do **MP4** + **WAV 16 kHz mono** (miks) dla Whisper | MP4 przerwany w trakcie = plik nie do odtworzenia. MKV przeżywa crash. |
| D7 | ffmpeg | `ffmpeg.exe` leży obok `.exe` aplikacji (folder portable) | Build LGPL "essentials" wystarczy; licencja OK przy osobnym exe. |

Poza zakresem (świadomie): sama transkrypcja i notatki (aplikacja przygotowuje plik `*.wav`; Whisper/LLM to osobny krok), szyfrowanie nagrań, wiele kont Teams, Teams w przeglądarce.

---

## 1a. Wyniki spike'a (2026-10-19, kod: `spike/`)

| Test | Wynik |
|---|---|
| Izolacja audio: proces A 440 Hz, proces B 1000 Hz, nagrywany A | **PASS** 440 Hz: +10 dB, 1000 Hz: -78 dB (separacja ok. 88 dB); odwrotnie analogicznie |
| Proces bez dźwięku | **PASS** loopback i tak dostarcza ciszę w czasie rzeczywistym (2,99 s / 3 s) - brak rozjazdu z wideo |
| Obraz okna przykrytego innym oknem (gfxcapture `hwnd=`, `h264_mf`) | **PASS** środek klatki = magenta okna docelowego, nie zielone okno na wierzchu; 640x360, 15 fps |
| Okno zminimalizowane | brak klatek przez czas minimalizacji (MKV ze zmienną klatkażą, plik poprawny) |
| Zamknięcie okna w trakcie nagrania | ffmpeg kończy się sam, plik MKV poprawny (5,1 s) |
| Detektor mikrofonu, prawdziwe Teams ("Spotkaj się teraz") | **PASS** `micActive=True` od ekranu przed dołączeniem, `False` 2 s po zamknięciu okna spotkania |
| Okno spotkania | osobne okno `TeamsWebView` procesu `ms-teams.exe`, **ten sam HWND** dla ekranu przed dołączeniem i spotkania; znika po wyjściu |

Wnioski do implementacji:
- Wybór okna: nowe okno `ms-teams.exe` klasy `TeamsWebView`, które pojawiło się po aktywacji mikrofonu (główne okno istniało wcześniej).
- Koniec rozmowy: zniknięcie okna spotkania **lub** mikrofon nieaktywny 10 s (pierwsze, co nastąpi).
- Ekran przed dołączeniem jest nagrywany (potwierdzone, akceptujemy kilka sekund).
- **Nie sprawdzone**: process loopback i gfxcapture na prawdziwym spotkaniu z drugą osobą (spotkanie solo nie ma zdalnego dźwięku). Do sprawdzenia w etapie 4 (E2E).

---

## 1b. Etap 1 - rdzeń (2026-10-19): GOTOWE, 36/36 testów zielonych

`TeamsCatcher/Core/`: `Settings`, `CallDetector`, `TeamsWindowFinder`, `ProcessLoopback`, `RecordingSession`, `CatcherService`.
Testy: `dotnet test TeamsCatcher.slnx` (31 jednostkowych + 5 integracyjnych, ok. 30 s; integracyjne: `--filter Category=Integration`).

Zmiany względem planu:
- **Synchronizacja przez czas systemowy w klatkach**: `setpts=RTCTIME` stempluje każdą klatkę czasem uniksowym, audio zapisuje czas uniksowy pierwszego pakietu; przy finalizacji pierwsza klatka odczytywana przez `-copyts -f framecrc`, mux z `-copyts` i jawnym `-itsoffset`. Dwie wcześniejsze metody (pomiar `-progress`, "koniec minus długość") okazały się błędne: ffmpeg startuje 0,3-3 s, a **WGC nie wysyła klatek, gdy okno się nie zmienia** (statyczne okno = 1 klatka). Testy: zgodność końców audio/wideo (±0,5 s) i statyczne okno.
- Audio i wideo zatrzymywane równolegle (wolny ffmpeg nie wydłuża audio).
- Koniec rozmowy = tylko mikrofon nieaktywny 10 s; zamknięcie okna kończy segment wideo, nowe okno spotkania = nowy segment (`video_02.mkv`, poza MP4 - shortcut w kodzie).
- Nazwa folderu z sekundami (`yyyy-MM-dd_HH-mm-ss_<tytuł>`), żeby dwie rozmowy w tej samej minucie się nie zderzyły.
- Brak mikrofonu w systemie = log i nagranie bez ścieżki mikrofonu (nie przerywa sesji).

---

## 1c. Etap 2 - UI (2026-10-19): GOTOWE, 42/42 testów zielonych

- Okno WF BIM wg UI_STYLE (wzorzec: `Tekla-2025/Apps/PhaseGenerator`): pasek tytułu z kotwicą, nagłówek z blokiem TEAMS, sekcje STATUS / SETTINGS / RECENT RECORDINGS, log 176 px, pasek postępu przy finalizacji.
- Tray (WinForms `NotifyIcon`): ikona z czerwoną kropką w trakcie nagrania, dymek "Recording started", menu Show / Start-Stop / Exit. Zamknięcie okna = schowanie do traya.
- Single instance (Mutex + EventWaitHandle): druga instancja pokazuje okno pierwszej - sprawdzone.
- Autostart: `HKCU\...\Run` z `--tray`, ścieżka exe odświeżana przy starcie (portable) - **nie sprawdzony ręcznie** (zmienia rejestr).
- Wylogowanie/wyłączenie Windows: nagranie domykane (do 20 s).
- Nowa ikona `WF.TeamsCatcher.ico` (TC) w `WF_assets/`, render SVG przez headless Edge.

---

## 1d. Etap 3 - odzyskiwanie i sprzątanie (2026-10-19): GOTOWE, 46/46 testów zielonych

- `session.json` zapisywany co 10 s w trakcie nagrania (starty strumieni znane po crashu).
- Przy starcie aplikacji `RecordingSession.RecoverAsync` kończy sesje z `Finalized=false`: naprawia nagłówki WAV (po zabiciu procesu rozmiary = 0), MKV jest czytelny, potem normalna finalizacja. Crash przed pierwszym zapisem = start audio przybliżony startem sesji.
- Sprzątanie po udanej finalizacji: usuwane `teams.wav`, `mic.wav`, `video_01.mkv` (są w `meeting.mp4`); zostają `meeting.mp4`, `transcript.wav`, `session.json` i ewentualne kolejne segmenty wideo. Przy błędzie ffmpeg źródła zostają. Sesja bez żadnych danych = folder usuwany.
- Shortcut: WAV max 4 GB (ok. 6 h audio Teams, ok. 3 h mikrofonu float) - dłuższe wymagają RF64.

---

## 1e. Testy na żywo z Teams (2026-10-19, solo "Spotkaj się teraz")

Test 1 - znalezione 3 błędy:
- **Cisza na ścieżce Teams**: dźwięk nowego Teams odtwarza WebView2 (`msedgewebview2`, dziecko `ms-teams.exe`) i proces mediów `ms-teams --process_type=nat...`. Process loopback "z drzewem" na `ms-teams.exe` ich nie łapie (-96,7 dB), na samym procesie WebView2 już tak (-39 dB). Poprawka: `TeamsProcesses.AudioPids` = procesy z rodziny Teams (Toolhelp, PID rodzica), które mają sesję audio; każdy nagrywany osobno (`teams_PID.wav`), nowe dochodzą w trakcie rozmowy, przy finalizacji miks do jednej ścieżki Teams.
- **Złe okno** (główne zamiast spotkania): okno spotkania pojawia się ok. 1 s PRZED mikrofonem. Poprawka: zapamiętany czas pojawienia się każdego okna; okno spotkania = najnowsze okno Teams z ostatnich 15 s.
- **Za długie wykrywanie końca** (10 s): skrócone do 5 s. Dodatkowo rozmowa trwa, dopóki okno spotkania jest otwarte (odporność na wyciszenie).

Test 2 - PASS: właściwe okno spotkania, obie ścieżki audio procesów Teams, wyciszenie nie przerwało nagrania (Teams trzyma mikrofon w trakcie wyciszenia), YouTube poza nagraniem, automatyczny koniec 7 s po zwolnieniu mikrofonu, finalizacja i sprzątanie OK.

**Nadal niesprawdzone**: głos rozmówcy (test solo nie ma zdalnego dźwięku). Zmiana rozmiaru okna w trakcie = czarne pasy (`resize_mode=scale_aspect`).

---

## 1f. Poprawki po testach na żywo (2026-10-19): GOTOWE, 65/65 testów zielonych

- **Wykrywanie rozmowy = pasek rozmowy w oknie Teams** (UI Automation, przycisk `microphone-button`, id niezależne od języka). Rejestr mikrofonu usunięty: ekran przed dołączeniem i "Dołącz ponownie" też trzymały mikrofon (nagrywał się ogon 15-20 s). Okno rozmowy = największe niezminimalizowane okno z paskiem. Błąd UI Automation nie kończy rozmowy. Przez 20 s po końcu to samo okno nie zaczyna nowej (fantom po wyjściu).
- **Wyciszenie**: nazwa przycisku ("Wycisz mikrofon" / "Wyłącz wyciszenie mikrofonu", PL+EN) -> ścieżka mikrofonu = cisza, odczyt co 200 ms.
- **MP4**: jedna zmiksowana ścieżka audio (Teams + mikrofon + dźwięk prezentacji); `transcript.wav` osobno.
- **Jakość**: libx264 CRF 18 (`h264_mf` ignorował bitrate: 136 kb/s), ramka 16:9 do wyboru 1080p / 1440p / 2160p.
- **Prezentacja**: gdy użytkownik udostępnia, Teams minimalizuje okno spotkania i pokazuje ramkę `ScreenBorderWindow` = udostępniany obszar. Nagrywany jest wtedy monitor (udostępniony ekran) albo okno pod ramką (udostępnione okno), po zakończeniu powrót do okna spotkania. Zmiana celu musi trwać 2 s. Wszystkie segmenty sklejane w jedno MP4 (concat z `duration` = przerwy zachowane na osi czasu).
- **Dźwięk prezentacji**: tylko w czasie udostępniania; okno -> procesy tej aplikacji z sesją audio, ekran -> wszystkie aplikacje z dźwiękiem poza Teams. Sprawdzone: YouTube nagrany w trakcie, cisza po zatrzymaniu.
- **ffmpeg**: `-tune zerolatency -flush_packets 1 -g 30 -cluster_time_limit 1000`, ubijany po 2 s (zminimalizowane okno blokuje "q") - strata najwyżej ok. 1 s.

Niesprawdzone (wymaga rozmówcy): głos drugiej osoby, prezentacja drugiej osoby, rozmowa 1:1 (czy pasek ma ten sam `microphone-button`).

## 1g. Transkrypcja (2026-10-19): GOTOWE, 73/73 testów zielonych

- whisper.cpp v1.9.5 (`whisper-bin-win-cuda-12.4.0-x64.zip` z wydania `b5454`; sterownik 555 = CUDA 12.5, CUDA 13 wymaga 580+), model `ggml-large-v3.bin` (3,1 GB), VAD `ggml-silero-v5.1.2.bin`. RTX 4060: 45 s audio w ok. 15 s razem z ładowaniem modelu.
- `transcript.wav` = 16 kHz **stereo**: lewy kanał mikrofon (Ja), prawy Teams + dźwięk prezentacji (Rozmówcy).
- **Diaryzacja po kanałach**: każdy kanał osobno przez `whisper-cli` z VAD (`-sns -ml 80 -sow -oj`), segmenty scalone po czasie -> `transcript.txt` (`[0:00:05] Ja: ...`). Sprawdzone: `--diarize` z VAD skleja wszystko w jeden segment; bez VAD Whisper wymyśla "Dziękuję za oglądanie!" w ciszy/muzyce.
- Po nagraniu automatycznie (opcja "Transcribe after recording"), po jednej transkrypcji naraz, w tle; przycisk "Transcribe selected" dla starszych nagrań.
- WhisperX odrzucony: nie używa modeli ggml, wymaga Pythona + torch + tokenu HF (pyannote), nie portable.
- Ograniczenie: "Rozmówcy" to wszyscy po stronie Teams razem; rozróżnienie osób (Rozmówca 1/2) = sherpa-onnx (C#, portable) - osobny etap.

## 1h. Rozmówcy, języki, tłumaczenie (2026-10-19): GOTOWE, 88/88 testów zielonych

- **Rozmówca 1/2/3**: sherpa-onnx 1.13.8 (NuGet, CPU, portable) na kanale Teams: segmentacja pyannote-3.0 + odcisk głosu 3D-Speaker CAM++ (VoxCeleb) + klastrowanie (próg 0,6 albo podana liczba rozmówców, UI: OTHERS auto/1-6). Segment Whispera -> mówca z największym pokryciem. Pliki testowe k2-fsa: 4/2/3 mówców przy prawdziwych 4/2/2; wywiad z TVN na żywo: 4 rozmówców, poprawny podział. Ok. 15x czasu rzeczywistego.
- **Echo**: mikrofon bez słuchawek łapie głośniki; wypowiedź "Ja" powtarzająca >= 60% słów nakładającej się wypowiedzi rozmówcy jest usuwana.
- **Język PL/EN**: każdy kanał transkrybowany po polsku i po angielsku, wygrywa wersja z wyższą średnią pewnością tokenów. Wbudowane wykrywanie Whispera myliło angielski z polskim akcentem (p = 0,54 dla "pl"). Język główny = ten, w którym powiedziano najwięcej.
- **Tłumaczenie**: każda wypowiedź na drugi język (PL<->EN) lokalnie przez Ollamę (`qwen3.5:9b`, paczki po 20 wypowiedzi, think=false); format: tłumaczenie, pod nim oryginał, oba z oznaczeniem języka. Ollama niedostępna = transkrypt bez tłumaczenia + wpis w logu.
- **Poprawka**: ręczny start nagrania (wątek UI = STA) psuł nagrywanie dźwięku Teams (`E_NOINTERFACE`); aktywacja WASAPI zawsze z wątku MTA.

## 1i. Jakość transkrypcji + notatki (2026-10-19): GOTOWE, 115/115 testów zielonych

Research (źródła w rozmowie): whisper.cpp nie ma natywnej diaryzacji, DTW (`-dtw large.v3`) nie działa z VAD (czasy tokenów w skompresowanym czasie), prompt Whispera działa tylko na pierwsze 30 s bez `--carry-initial-prompt`, korekta LLM pomaga tylko przy bramkowaniu pewnością (Idiap 2024), nie ma lepszego modelu PL niż large-v3 w ggml.

Potok (jakość ponad szybkość, ok. 5 min na 1 min nagrania z notatkami):
1. Przebieg 1: każdy kanał PL i EN, wybór po pewności modelu.
2. Przebieg 2 z `--prompt` słowniczka **tylko gdy słowniczek ma terminy nieobecne w przebiegu 1** i **tylko gdy pewność nie spada** (test TVN: prompt z samymi znanymi nazwiskami zgubił słowo "Pytanie").
3. Diaryzacja + weryfikacja zdań >= 2 s odciskiem głosu (podobieństwo >= 0,6 i margines 0,15). Krótkie zdania (< 1,5 s, np. "I Marcin.") nie są rozróżnialne tym modelem - fizyczne ograniczenie, nie próg.
4. Echo. 5. **Pisownia ze słowniczka użytkownika** (GLOSSARY w UI): podobne słowa zamieniane na pisownię ze słowniczka ("Doroczyński/Sztur/Kawalski" -> "Dorociński/Stuhr/Kawulski"), odmiana nie jest ruszana ("w Tekli"). Model sam nie dopisuje do słowniczka słów pospolitych (utrwalał błąd "stanarzysty").
6. Korekta LLM tylko zdań ze słowem o pewności < 0,4 i **tylko podmiana słów** (ta sama liczba słów, <= 20%) - test TVN: luźniejsza reguła zamieniła "do stanarzysty. Pytanie," na "do Maciej Sztur.".
7. Tłumaczenie. 8. Pliki: `transcript.txt` (zbiorczy), `transcript.pl.txt`, `transcript.en.txt` (etykiety Me / Speaker N).
9. Notatki `notes.pl.md` / `notes.en.md` (podsumowanie, decyzje, zadania, otwarte kwestie, tematy z czasem) + **drugi przebieg sprawdzający notatkę z transkrypcją** (test TVN: usunął zmyślone "reżyser Stanisław Marcin Dorociński").

Niezrobione / do decyzji: fine-tuning Whispera na własnych nagraniach, większy model LLM (32B nie mieści się w 8 GB VRAM), rozpoznawanie krótkich wtrąceń innych osób.

## 1j. Spójność PL/EN + GPU (2026-10-19): GOTOWE, 125/125 testów zielonych
1. GPU: Whisper już działał na RTX 4060 (CUDA0). Ollama działała na CPU, bo wystartowała przed instalacją sterownika NVIDIA. Po restarcie Ollamy jest na CUDA, a przebieg TVN skrócił się z ~8 min do ~3:45.
2. VRAM: Whisper (3 GB) i qwen3.5:9b (~6 GB) nie mieszczą się razem na 8 GB. `Ollama.UnloadAsync` zwalnia model przed 2. przebiegiem Whispera i na końcu (następne nagranie w kolejce).
3. Tłumaczenie transkrypcji: po każdej paczce idzie weryfikacja modelem (oryginał obok tłumaczenia). `PickVerified` odrzuca odpowiedź, która przepisuje oryginał.
4. Notatki: pisane raz w języku głównym, a drugi język to tłumaczenie z kontrolą spójności, a nie osobna notatka. Wcześniej EN mówiło "single doll", a PL o filmie "Lalka".
5. Twarde kontrole notatek:
   - nazwy spoza transkrypcji (z tolerancją odmiany) wracają do modelu maksymalnie 2 razy, a potem punkt jest usuwany (złapane "Stanisław Bareja");
   - czasy [h:mm:ss] w punktach są brane z wypowiedzi o największej liczbie wspólnych słów, a nazwy własne ważą podwójnie.
6. Mówca stały w obrębie zdania (`Diarizer.RealignBySentence`, jak whisper-diarization): segmenty aż do kropki, pytajnika albo przerwy dłuższej niż 1,5 s dostają mówcę, który mówi w nich najdłużej. Komentarz gry: "bo ja się" i "gubię w takich momentach" miały wcześniej 2 mówców. Wzorce głosu nie rozróżniają osób: ta sama osoba ma podobieństwo 0,81-0,92, a różne osoby w TVN 0,74-0,88. Dlatego nie ma scalania klastrów po progu.
7. Echo w mikrofonie (przebicie z głośników): korelacja sygnału nie działa, bo przetwarzanie mikrofonu Realtek ją psuje po ~79 s. Windows AEC (strumień Communications) na tym mikrofonie nic nie zmienia (sprawdzone, -24,8 dB w obu trybach). Teraz zdanie "Ja" jest echem, gdy mikrofon jest o 3 dB lub więcej cichszy od Teams i co najmniej 30% słów pochodzi od rozmówców w oknie ±2 s. Gra: 6/6 usuniętych, nagranie z prawdziwą mową: wypowiedź zostaje. Testy 129/129.

## 1k. Etap 5: wersja portable (2026-10-20): GOTOWE
1. `publish.ps1` buduje `dist\TeamsCatcher` (4,46 GB) i `dist\TeamsCatcher.zip`:
   - `TeamsCatcher.exe`: self-contained single-file, nie wymaga zainstalowanego .NET;
   - `ffmpeg.exe`;
   - `whisper\`: whisper-cli z bibliotekami CUDA 12, large-v3 i Silero VAD;
   - `diarization\`: pyannote i CAM++;
   - `README.txt`.
2. Sprawdzone:
   - whisper z folderu działa na CUDA0;
   - exe startuje, a settings.json z pierwszego startu wskazuje pliki obok exe (wszystkie 5 istnieją);
   - onnxruntime i sherpa rozpakowują się do `%TEMP%\.net\TeamsCatcher`.
3. Nie w paczce: Ollama + qwen3.5:9b (instalacja osobno, opis w README). Bez Ollamy powstaje sama transkrypcja.
4. Niesprawdzone: pełna transkrypcja klikana z exe portable (diaryzacja w trybie single-file) i start na komputerze bez NVIDIA.

## 1l. Korekta transkrypcji dwoma modelami (2026-10-20): GOTOWE, 135/135 testów
1. Druga hipoteza: Whisper large-v3-turbo na tych samych kanałach. Inny model myli się w innych miejscach: na nagraniu Heroes large-v3 usłyszał "jamentowy golem", a turbo "diamentowy golem".
2. Bielik 4.5B v3 (polski, 5,1 GB) poprawia zdania. Dostaje dziedzinę rozmowy (bez listy słów, bo lista z błędnego tekstu utrwalała "Jament Golem"), sąsiednie zdania i drugą hipotezę.
3. `SafeCorrection` sprawdza każde słowo osobno:
   - słowo z drugiej hipotezy jest przyjmowane (dwa niezależne źródła);
   - słowo zgadnięte i podobnie brzmiące idzie do Gemma 4 E2B (4,3 GB) do zatwierdzenia;
   - słowo usłyszane przez oba Whispery jest nietykalne;
   - zmiany samej wielkości liter i interpunkcji są cofane.
4. Bez drugiej hipotezy małe modele nie znają domeny: Bielik i qwen3.5:9b zgadywały "jamowym" i "jedenastym".
5. Wynik:
   - Heroes: 3/3 golemy poprawione;
   - TVN: "stanarzysty" → "scenarzysty", bez regresji.
6. Diagnostyka: zmienna `TC_LLM_LOG=plik` zapisuje zapytania i odpowiedzi modeli.
7. Do zrobienia:
   - notatki: Bielik pisze, Gemma sprawdza, wszystko w 6 GB VRAM;
   - rozpoznawanie rozmówców z okna Teams (UIA/OCR), wymaga testu na żywo.
## 1m. Dyskusja modeli: 3 modele mowy + LanguageTool + 2 modele tekstu (2026-10-20): GOTOWE, 147/147 testów
1. Mowa:
   - A: Whisper large-v3 (podstawa);
   - B: Whisper large-v3-turbo;
   - C: NVIDIA Parakeet TDT 0.6B v3 (inna architektura, kawałki po 5 min, inne cięcia niż VAD).

   Najmniejszy Whisper (tiny) odrzucony, bo w polskim myli się co kilka słów.
2. Hipotezy B i C są przycinane do zdania A po czasach słów. Offsety tokenów Whispera przy VAD są przesunięte o ~20 s, więc czasy są brane z segmentów.
3. LanguageTool 6.x (Java 21 portable) wskazuje nieistniejące słowa ("jamentowym") i gramatykę ("w Armagedonem").
4. Dyskusja dotyczy tylko zdań spornych (hipotezy się różnią albo LanguageTool zgłasza błąd):
   - Bielik proponuje, Gemma recenzuje ("OK" albo poprawka), Bielik odpowiada, do zgody lub 3 rund;
   - jedno zdanie to jedno zapytanie (w paczkach 4B model przesuwał numerację);
   - modele są ładowane po kolei.
5. `SafeCorrection`:
   - dopasowanie słów jak w diff (zgubione słowo nie przekreśla podmian obok);
   - nowe słowo musi brzmieć podobnie albo słyszą je B i C w tym samym miejscu;
   - słowo A potwierdzone przez inną hipotezę jest nietykalne;
   - cyfra nie zastępuje słowa.
6. Niepewne słowa:
   - ??słowo??, gdy modele się nie zgodziły albo słowo nie istnieje i nie potwierdza go żadna hipoteza;
   - zgadnięte nieistniejące słowo wraca do oryginału;
   - `transcript.pl.md` i `transcript.en.md` pokazują je na czerwono.
7. Notatki pracy `analysis\`: 00-status, 01 large-v3, 02 turbo, 03 Parakeet, 06 LanguageTool, 07 Bielik, 08 Gemma (rola, instrukcja, status, wynik).
8. Słowniczek użytkownika jest nakładany ponownie po dyskusji, bo B i C słyszą "Sztur", a słowniczek mówi "Stuhr".
9. Wynik:
   - Heroes: 3/3 "diamentowy golem", "z pulką" → "z półką", ??armatrap?? i ??moralówką?? oznaczone;
   - TVN: "scenarzysty", bez regresji;
   - czas: ~2 min na 80 s nagrania (bez notatek).
10. Do zrobienia:
    - notatki (Bielik pisze, Gemma sprawdza);
    - rozmówcy z okna Teams;
    - test na długim spotkaniu (czas!).
## 1n. Llama + Gemma krzyżowo, kontrola sensu przez angielski, notatki z małych zadań (2026-10-20): 163/163 testów
1. Bielik 4.5B został odrzucony:
   - przy notatce w całości powtarzał punkty;
   - "popraw całość wg uwag" zamienił w zadanie z matematyki;
   - sprawdzając tłumaczenie, oddawał polski oryginał.

   Teraz modele to `llama3.1:8b` (5,66 GB przy kontekście 8192) i `gemma4:e2b-it-qat` (2,9 GB). Przed przełączeniem modelu poprzedni jest zwalniany, więc w VRAM jest zawsze jeden: razem miały 7,9 GB.
2. Limit długości odpowiedzi (`num_predict`) i `repeat_penalty`: Llama zapętliła się przy liście i jedno zapytanie trwało 15 min. Limity: TAK/NIE 5, lista 400, tłumaczenie 2048.
3. Kontrola sensu:
   - Whisper `-tr` tłumaczy mowę PL od razu na angielski (z dźwięku, nie z tekstu);
   - Llama tłumaczy to z powrotem na PL, Gemma sprawdza tłumaczenie;
   - Gemma ocenia, czy zdanie PL mówi to samo (TAK/NIE); inny sens = zdanie sporne w dyskusji.

   Notatka pracy: `04-whisper-english.md`, `05-meaning-check.md`.
4. Tłumaczenia są krzyżowe:
   - PL → EN: tłumaczy Gemma (z podpowiedzią z Whisper `-tr`), sprawdza Llama;
   - EN → PL: tłumaczy Llama, sprawdza Gemma;
   - odpowiedź "sprawdzającego" bliższa oryginałowi niż tłumaczeniu jest odrzucana.
5. Notatki ze sprawdzonej transkrypcji PL, z małych zadań:
   - sekcje to krótkie listy (najwyżej 8 punktów);
   - Gemma sprawdza każdy punkt pytaniem zależnym od sekcji (czy to faktycznie decyzja, zadanie, otwarte pytanie);
   - punkt odrzucony wraca do Llamy, a po kolejnym NIE znika;
   - cytaty "Rozmówca N: ..." i nazwy spoza transkrypcji są odrzucane;
   - czasy tematów i Markdown składa kod;
   - tłumaczenie notatek idzie punkt po punkcie.
6. TVN:
   - notatki bez zmyśleń, decyzje, zadania i otwarte kwestie to poprawnie "brak";
   - transkrypcja EN dobra;
   - notatki trwają ~4,5 min na 80 s nagrania (wolno: każdy punkt to 2-6 zapytań).
7. Poprawki po teście:
   - nieistniejące słowo ma w dyskusji wskazanie wprost, co w tym miejscu słyszą B i C (Heroes: znowu 3/3 "diamentowy golem"; Llama wcześniej uznała "jamentowy" za "znany termin");
   - kontrola sensu jest pokazywana tylko dla porównywalnych fragmentów (Whisper ``-tr`` też usłyszał "Jament Golem");
   - nazwy z oryginału muszą przetrwać tłumaczenie notatek ("Kawulski" ≠ "Pawłowski"): jedna próba z listą nazw, potem zostaje oryginał;
   - słowniczek użytkownika nie jest oznaczany ??.
8. VRAM zmierzony w pełnym przebiegu: Llama 5,68 GB, Whisper 4,1 GB, Gemma 2,9 GB, Parakeet 2,2 GB, zawsze jeden model naraz.
9. Ograniczenia małych modeli:
   - w podsumowaniu Heroes zdarza się zdanie przepisane z transkrypcji albo zniekształcony punkt ("dwie kąsy są potrzebne, aby nie minąć moralu");
   - czas ~5,5-6 min na 80 s nagrania, a dla godzinnego spotkania szacunkowo ponad godzina.

## 1o. Przyspieszenie (2026-10-20): pomiar na 9,6 min polskiej sklejki (TVN + Heroes + gra ×2)
1. Przed zmianami: 15:19 (≈ 95 min na godzinę).

   | Etap | Czas | Etap | Czas |
   |---|---|---|---|
   | Whisper PL+EN na całości | 2:03 | kontrola sensu | 2:29 |
   | słowniczek + B/C/-tr | 2:35 | dyskusja (108/111 spornych) | 3:43 |
   | diaryzacja | 1:06 | tłumaczenie | 1:32 |
   | notatki | 1:36 | | |
2. Zmiany:
   - notatki fazami i paczkami (Gemma sprawdza numerowaną listę, Llama poprawia listę), mniej przełączeń modeli;
   - tematy co 3 min;
   - tłumaczenie fazami (najpierw wszystkie paczki jeden model, potem drugi);
   - język kanału wybierany na próbce 2 min;
   - spór wywołuje A≠B albo LanguageTool, a sama różnica z Parakeetem nie;
   - recenzja tylko zmienionych albo podejrzanych zdań;
   - kontrola sensu jest teraz podpowiedzią EN bez wyroku: Gemma uznawała 82% zdań za "inny sens", czyli szum;
   - drugi przebieg ze słowniczkiem tylko dla słowniczka użytkownika.
3. Po zmianach: 11:26 (≈ 70 min na godzinę), przed ostatnią zmianą (drugi przebieg) jeszcze ~-1 min. Notatki bez utraty jakości (golem 45 życia/95% obrażeń, bez zmyślonych decyzji).

### Czasy krok po kroku (2026-10-20, sklejka 9,6 min = 578 s, kanały: Ja + Rozmówcy, RTX 4060 8 GB)
Kolumna "≈ 1 h" przelicza kroki zależne od długości nagrania razy 6,2. Kroki stałe (start LanguageTool, próbki języka) liczone raz.

| # | Krok | Kto | Czas 9,6 min | ≈ 1 h |
|---|---|---|---|---|
| **1** | **Przebieg 1: wybór języka i transkrypcja A** | | **1:53** | **~7,5 min** |
| 1.1 | Ja: próbka 120 s PL (z podziałem kanałów ffmpeg) | Whisper large-v3 | 0:14 | 0:14 |
| 1.2 | Ja: próbka 120 s EN | Whisper large-v3 | 0:06 | 0:06 |
| 1.3 | Ja: całość PL (kanał prawie cichy) | Whisper large-v3 | 0:14 | ~1:30 |
| 1.4 | Rozmówcy: próbka 120 s PL | Whisper large-v3 | 0:16 | 0:16 |
| 1.5 | Rozmówcy: próbka 120 s EN | Whisper large-v3 | 0:12 | 0:12 |
| 1.6 | Rozmówcy: całość PL | Whisper large-v3 | 0:51 | ~5:15 |
| **2** | **Słowniczek nazw** | Llama | **0:07** | ~0:45 |
| 2.1 | Przebieg 2 ze słowniczkiem (tylko przy nowych nazwach użytkownika) | Whisper large-v3 | pominięty | 0 (albo ~6 min) |
| **3** | **Hipotezy B, C i mowa po angielsku** | | **1:56** | **~12 min** |
| 3.1 | Ja: hipoteza B | Whisper turbo | 0:10 | ~1:00 |
| 3.2 | Ja: hipoteza C | Parakeet v3 | 0:13 | ~1:20 |
| 3.3 | Ja: mowa → angielski (-tr) | Whisper large-v3 | 0:13 | ~1:20 |
| 3.4 | Rozmówcy: hipoteza B | Whisper turbo | 0:24 | ~2:30 |
| 3.5 | Rozmówcy: hipoteza C | Parakeet v3 | 0:12 | ~1:15 |
| 3.6 | Rozmówcy: mowa → angielski (-tr) | Whisper large-v3 | 0:44 | ~4:30 |
| **4** | **Diaryzacja (Rozmówca 1/2/3) + weryfikacja odciskiem głosu** | sherpa-onnx (CPU) | **0:53-4:10** | **~5,5-26 min** |
| 4.1 | Echo mikrofonu, pisownia ze słowniczka | kod | 0:00 | 0:00 |
| **5** | **Dyskusja modeli (95 ze 111 zdań spornych)** | | **3:39** | **~22 min** |
| 5.1 | Start LanguageTool (Java) | LanguageTool | 0:06 | 0:06 |
| 5.2 | Pisownia i gramatyka 111 zdań | LanguageTool | 0:23 | ~2:20 |
| 5.3 | Dziedzina rozmowy | Llama | 0:06 | 0:06 |
| 5.4 | Propozycje (95 zdań, jedno zapytanie na zdanie) | Llama | 1:31 | ~9:25 |
| 5.5 | Runda 1: recenzja (Gemma) / odpowiedź (Llama, 3 zdania) | Gemma, Llama | 0:28 / 0:10 | ~3:55 |
| 5.6 | Runda 2: recenzja / odpowiedź (3 zdania) | Gemma, Llama | 0:09 / 0:12 | ~2:10 |
| 5.7 | Runda 3: recenzja (3 zdania, bez zgody → ??) | Gemma | 0:10 | ~1:00 |
| 5.8 | Końcowa pisownia, oznaczenia ?? | LanguageTool | 0:24 | ~2:30 |
| **6** | **Tłumaczenie transkrypcji PL → EN (73 wypowiedzi)** | Gemma tłumaczy, Llama sprawdza | **1:30** | **~9:20** |
| **7** | **Notatki PL** (1 część transkrypcji) | | **1:09** | **~6 min** (6 części) |
| 7.1 | Listy 4 sekcji → 8 punktów | Llama | 0:19 | ~2:00 |
| 7.2 | Sprawdzenie paczkami, runda 1 (2 odrzucone) | Gemma | 0:08 | ~0:50 |
| 7.3 | Poprawki odrzuconych, runda 1 | Llama | 0:11 | ~1:05 |
| 7.4 | Sprawdzenie runda 2 / poprawki runda 2 | Gemma, Llama | 0:08 / 0:09 | ~1:45 |
| 7.5 | Sprawdzenie runda 3 (2 usunięte) | Gemma | 0:07 | ~0:45 |
| 7.6 | Tematy co 3 min (4) | Llama | 0:07 | ~0:45 |
| **8** | **Tłumaczenie notatek PL → EN** | Gemma tłumaczy, Llama sprawdza | **0:19** | ~0:30 |
| | **Razem** | | **14:43** (z diaryzacją 4:10); **11:26** (z diaryzacją 0:53) | **~63 min** (szybka diaryzacja) do **~83 min** |

Wnioski:
- największe pozycje to dyskusja (~22 min/h), Whisper i hipotezy (~20 min/h razem), tłumaczenie transkrypcji (~9 min/h);
- notatki to już tylko ~6,5 min/h;
- diaryzacja na CPU waha się 5x między przebiegami (0:53 i 4:10 na tym samym nagraniu), prawdopodobnie przez zwolnienie CPU w laptopie; do zbadania.

## 1p. Zmiany 2026-10-20 (popołudnie): wybór wyników, baza wiedzy, kawałki na żywo, pyannote, Qwen
1. Ustawienia: Video / Transcript / Live notes / Meeting notes. Bez wideo ``meeting.mp4`` = sam dźwięk.
2. Dyskusja i kontrola notatek w języku zdania: polskie zdanie dostaje polecenia po polsku. Model ocenia poprawność językową, logikę i spójność z rozmową. Nie zgaduje skrótów ani nazw, tylko oznacza je ??.
3. Baza wiedzy ``knowledge.json`` (obok exe):
   - wpisy ``[termin - znaczenie]`` i ``[termin - inny zapis - znaczenie]``;
   - poprawki pisowni ``= poprawne`` (np. "Jamentowy" → "diamentowy"), podmieniane w tekście na stałe przed dyskusją;
   - objaśnienia z bazy trafiają do dyskusji, notatek i ich kontroli, tylko jako pomoc w rozumieniu (objaśnienie "SP = siła czarów" trafiło raz do notatki jako punkt, więc jest teraz wprost zakazane).
4. Panel "TO CLARIFY":
   - niejasne słowa z ostatnich nagrań (??, nieznane słownikowi, skróty), odmiany scalone;
   - pole na znaczenie i wpis zbiorczy;
   - "Save and recalculate": zapis do bazy, a nagranie przeliczane od dyskusji z ``analysis/state.json``, bez Whispera i diaryzacji (Heroes: 3:07-3:45 zamiast 10:39).
5. Notatki:
   - sekcje: Podsumowanie (główny sens), Założenia, Wnioski, Decyzje i ustalenia, Zadania, Otwarte kwestie i uwagi;
   - notatka częściowa bez zadań;
   - odrzucane: cytaty "Rozmówca N: ..." i zdania przepisane prawie dosłownie (od 8 słów, ≥80% wspólnych słów);
   - nazwy z bazy wiedzy liczą się jako znane.
6. Model sprawdzający: Qwen 3.5 4B zamiast Gemma 4 E2B. Pomiar na 14 punktach z nagrań: Gemma 10/14 i 2 zmyślenia przepuszczone, Qwen 11/14 i 1, Llama 12/14 (pisze, więc nie sprawdza samej siebie).
7. VRAM:
   - model zostaje w pamięci, jeśli razem z innymi mieści się w budżecie (``VramBudgetMb``, 0 = cała karta minus 400 MB);
   - Llama 5,8 GB + Qwen ~3,7 GB > 8 GB, więc na tej karcie przełączanie zostaje;
   - przełączenie to ~7-10 s.
8. Diaryzacja pyannote community-1 na GPU:
   - Python 3.12 + PyTorch 2.14 cu126 + pyannote.audio 4.0.7 (``tools\pyannote``, 4,3 GB), skrypt ``diarize.py`` obok exe;
   - brak modelu albo błąd = sherpa-onnx na CPU;
   - model jest bramkowany: użytkownik akceptuje warunki na Hugging Face i pobiera go raz (``hf download``).
9. Kawałki na żywo (``LiveChunkMinutes`` = 5):
   - co 5 min zrzut dźwięku (``Flush`` pod blokadą, ten sam miks co na końcu), cięcie w najcichszym 0,5 s w oknie ±20 s;
   - w kolejce: rozpoznanie, dyskusja, notatka częściowa w dymku w trayu (kliknięcie otwiera plik, pusty fragment nie daje dymka);
   - po rozmowie: ostatni kawałek, potem składanie: zdania z kawałków przesunięte na oś końcowego nagrania (czas 0 = najwcześniejsza z klatki wideo / Teams / mikrofonu), jedna diaryzacja na całości, tłumaczenie i notatka z całości z notatkami częściowymi jako kontekstem;
   - bez kompletu kawałków przetwarzana jest całość jak dotąd.
10. Testy: 172/172.
11. Symulacja kawałków (Heroes, 9,6 min, 2 kawałki, RTX, sherpa na CPU):
    - kawałek 1 (289 s dźwięku): 5:18, czyli ~1,1x czasu rzeczywistego - kolejka rośnie o ~30 s na kawałek (godzina = ~6 min opóźnienia dymków);
    - po rozmowie: 10:33 = ostatni kawałek 5:40 + składanie 4:54 (diaryzacja 0:53, tłumaczenie 1:44, notatka z całości 2:17);
    - przy 9,6 min zysku nie ma (pełne przetwarzanie 10:39); przy godzinie po rozmowie zostaje ~10-11 min zamiast ~63-83 min;
    - słabe: 11 rozmówców z sherpa (czeka na pyannote), w notatkach częściowych zostają półzdania ("To moment.", "Tak, ...") w Decyzjach i zmyślony temat ("serial Dzień Dobry").
12. Poprawki po symulacji:
    - kawałek 5 min ± 1 min, cięcie w środku ciszy ≥ 2 s najbliższej 5. minuty (cisza = blisko szumu tła okna, max -34 dBFS); bez takiej ciszy najcichsze 0,5 s; kolejny kawałek liczony od końca poprzedniego;
    - punkty notatek odrzucane kodem (IsFragment): poniżej 4 słów, zaczynające się od "Tak,", "Nie,", "Chyba,", "Dobra," itp., z doklejoną odpowiedzią " - Tak, ...", z szablonem "Rozmówca N";
    - testy 181/181;
    - symulacja po poprawkach: cięcie w ciszy w 293 s, kawałek 1 w 4:42 (szybciej niż czas rzeczywisty), po rozmowie 9:38; w notatkach częściowych nie ma już półzdań, a Decyzje są puste ("brak" zamiast faktów); nadal zmyślony temat "serial Dzień Dobry" w kawałku 1.
13. pyannote community-1 pobrany (``tools\pyannote-models\community-1``, ~33 MB):
    - ``torchaudio.load`` w torchaudio 2.9+ wymaga torchcodec + bibliotek FFmpeg (brak w paczce) - ``diarize.py`` czyta wav modułem ``wave`` (16-bit PCM);
    - nagranie testowe (wywiad TVN ~5 osób + komentarz Heroes ~3 osoby): pyannote 9 rozmówców w 0:39 na GPU (z ładowaniem modelu), sherpa 11 w 0:49-4:10 na CPU;
    - składanie z kawałków z pyannote: 3:49.
14. Zakładka "VIDEO FILE": dowolny plik wideo/audio (przycisk albo przeciągnięcie, kilka naraz):
    - nowy folder w OutputFolder (``data_nazwa-pliku``), ``session.json`` ze ścieżką źródła, dźwięk ffmpeg 16 kHz mono do ``transcript.wav``, oryginał zostaje na miejscu;
    - mono = wszyscy jako Rozmówcy, rozdzieleni diaryzacją; dalej ta sama kolejka (korekta, tłumaczenie, notatki wg ustawień);
    - test (55 s wywiadu TVN): 3 rozmówców z pyannote, całość 7:06, w tym notatki 3:54 (podejrzanie długo jak na 55 s).
15. Okno pracy z nagraniem:
    - zakładki TEAMS CALLS / VIDEO FILE, pod nimi sekcja RECORDING: lista rozwijana nagrań, ANALYZING (co się teraz liczy: nagranie, kawałek live, ostatni krok), TO CLARIFY tylko dla wybranego nagrania;
    - wpis zbiorczy i "Save and recalculate" zawsze widoczne;
    - panel boczny NOTES: notatka wybranego nagrania (odświeżana po każdym zadaniu), "Open", "Redo after clarification" (zapis objaśnień + analiza od dyskusji ze stanu, bez Whispera) - to samo co "Save and recalculate";
    - notatki 5-min jako własne powiadomienia w prawym dolnym rogu (sekcje z punktami, bez "brak", kolejne nad poprzednimi, znikają po 60 s, nie pod kursorem); dymek traya usunięty, bo ucinał treść do 250 znaków.
16. Pakowanie: mała paczka + pobieranie u użytkownika (``Core/Components.cs``, okno "Components", opis: ``docs/07-setup/instalacja.md``):
    - w paczce: exe, ffmpeg, modele sherpa, model pyannote community-1 (bramkowany na HF, licencja CC-BY-4.0 pozwala dołączyć), ``diarize.py``, ``pyannote-requirements.txt``;
    - pobierane: whisper.cpp b5454 (oficjalne wydanie ma parakeet-cli), modele Whisper/Parakeet/VAD z HF, Temurin JRE 21.0.12.1, LanguageTool 6.6, Ollama 0.40.6 portable (tylko gdy żadna nie działa), llama3.1:8b + qwen3.5:4b przez ``/api/pull``, Python 3.12.14 + pakiety (uv 0.12.18, wersje z ``uv pip freeze``);
    - wszystko przypięte wersją i SHA-256 (z API HF / GitHub / Adoptium), wznawianie (``.part`` + Range), ochrona przed zip slip, błąd jednego składnika nie zatrzymuje reszty;
    - ``publish.ps1 -Full`` = dawna paczka offline;
    - test na żywo (folder tymczasowy): whisper.cpp 0:23, JRE 0:02, LanguageTool 0:10, VAD; pyannote: Python + 96 pakietów 8:40 (PyTorch 2,4 GB pobrany w 2:24, instalacja 6 min), środowisko 4,4 GB, ``diarize.py`` na nim: cuda, 9 rozmówców jak na środowisku deweloperskim, 41 s (pierwsze uruchomienie 3:04 - rozruch); Ollama portable 2:55 + pull qwen2.5:0.5b + odpowiedź modelu OK; oficjalne parakeet-cli daje identyczne tokeny jak lokalny build;
    - poprawka po teście: sprzątanie cache uv (pliki tylko do odczytu) wywalało całą instalację;
    - test paczki (zip 0,22 GB, folder 0,48 GB, rozpakowany do czystego folderu): pierwszy start = okno Components z 8 składnikami (9,1 GB; Ollama i modele pominięte, bo Ollama użytkownika działa), wszystkie "Ready" w < 10 min; drugi start bez okna; import 55 s nagrania przez VIDEO FILE: cały potok 5:40 (pyannote pierwszy raz 3:09 - rozruch Pythona), notatki 45 s;
    - poprawki z testu: przyciski z ikoną nie miały nazwy dla czytników ekranu (``AutomationProperties.Name``), treść zakładek była niewidoczna dla UI Automation (``PART_SelectedContentHost`` w szablonie TabControl); po instalacji zostaje pusty ``downloads\uv\python``.
17. UI: panel NOTES z regulowaną szerokością (GridSplitter, kolumny proporcjonalne, min. 800 + 300 px, okno min. 1140 px), listy rozwijane z ciemnym hoverem (domyślny styl ToggleButton w motywie).
---

## 2. Architektura

```
TeamsCatcher.exe (WPF, tray)
├─ CallDetector        polling rejestru co 1 s -> zdarzenia CallStarted / CallEnded (debounce)
├─ TeamsWindowFinder   EnumWindows + PID ms-teams.exe -> HWND okna spotkania
├─ RecordingSession    jedna sesja = jeden folder
│   ├─ VideoRecorder   proces ffmpeg (gfxcapture hwnd -> h264 -> video.mkv)
│   ├─ AudioRecorder   NAudio: teams.wav (process loopback) + mic.wav
│   └─ Finalizer       ffmpeg: video + 2 ścieżki audio -> meeting.mp4 + transcript.wav
├─ Settings            settings.json obok exe (portable)
└─ MainWindow          UI WF BIM (status, ustawienia, ostatnie nagrania, log)
```

### Przepływ
1. Start aplikacji -> tray, okno ukryte (lub widoczne przy pierwszym uruchomieniu).
2. `CallDetector`: mikrofon Teams aktywny >= 2 s -> `CallStarted`.
3. `TeamsWindowFinder` wybiera okno: widoczne okno top-level procesu `ms-teams.exe`, **nie** główne okno Teams, najnowsze (nowe Teams otwiera spotkanie w osobnym oknie). Brak osobnego okna -> główne okno Teams.
4. `RecordingSession` startuje audio i wideo w tym samym momencie, zapisuje znacznik czasu startu każdego strumienia (do synchronizacji).
5. Mikrofon Teams nieaktywny >= 10 s -> `CallEnded` -> stop -> finalizacja w tle.
6. Wynik: `Recordings\2026-10-19_14-03_<tytuł-okna>\meeting.mp4`, `transcript.wav`, `session.json` (start, koniec, tytuł, HWND, ścieżki).

### Struktura plików
```
TeamsCatcher/
  TeamsCatcher.csproj
  App.xaml(.cs)                 single instance (Mutex), tray
  MainWindow.xaml(.cs)
  Assets/WF/                    WF.Colors/Icons/Controls/Theme.xaml, WF.TeamsCatcher.ico/-48.png
  Controls/WfIcon.cs
  Infrastructure/               ObservableObject, RelayCommand, AsyncRelayCommand
  Core/CallDetector.cs
  Core/TeamsWindowFinder.cs
  Core/RecordingSession.cs      (Video + Audio + Finalizer, rozbić dopiero gdy > 300 linii)
  Core/Settings.cs
TeamsCatcher.Tests/             xUnit
tools/ffmpeg.exe                (kopiowany do publish)
```

---

## 3. UI (zgodnie z UI_STYLE.md)

- Okno 900x560, ciemny motyw, własny pasek tytułu (`User: …`, kotwica Always on top), nagłówek "TEAMS CATCHER", podtytuł "WF | BIM · Microsoft Teams".
- Zamknięcie okna = schowanie do traya; wyjście tylko z menu traya (Exit).
- Sekcje:
  1. **STATUS**: Teams running/not running, stan `Idle / In call / Recording / Finalizing`, tytuł nagrywanego okna, czas trwania, wolne miejsce na dysku.
  2. **SETTINGS**: folder nagrań + Browse…, "Record my microphone", FPS (5/15/30, domyślnie 15), "Start with Windows" (HKCU\Run), "Auto-record" (on/off).
  3. **RECORDINGS**: lista ostatnich 10 nagrań, przyciski "Open folder".
  4. **LOG**: 176 px, mono 12.
- Główny przycisk: **Start / Stop recording** (ręczne nadpisanie automatu).
- Tray: ikona szara (idle) / czerwona (recording), menu: Show, Stop recording, Exit. Dymek "Recording started" przy starcie nagrania.
- Ikona aplikacji: `WF.TeamsCatcher.ico` ze skrótem **TC** (do wygenerowania z szablonu SVG istniejących ikon modułów).

---

## 4. Etapy i czas

| Etap | Co | Czas | Gotowe gdy |
|---|---|---|---|
| 0. Spike | 3 konsolowe próby: (a) ffmpeg gfxcapture na HWND okna Teams, (b) NAudio process loopback Teams, (c) odczyt klucza mikrofonu w trakcie testowego połączenia Teams | 2-3 h | 3x "działa" albo decyzja o planie B |
| 1. Rdzeń | `CallDetector`, `TeamsWindowFinder`, `RecordingSession`, `Settings` + testy jednostkowe | 1 dzień | testy zielone, nagranie z konsoli |
| 2. UI | okno WF BIM, tray, single instance, autostart | 0,5 dnia | checklista UI_STYLE odhaczona |
| 3. Finalizacja | remux MP4, `transcript.wav`, `session.json`, sprzątanie po crashu | 0,5 dnia | testy integracyjne zielone |
| 4. Testy E2E | scenariusze z sekcji 6.3 na prawdziwym Teams | 0,5 dnia | wszystkie scenariusze PASS |
| 5. Publish | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` + ffmpeg.exe, zip | 1 h | zip działa na czystej maszynie bez .NET |

Razem: ok. 3,5 dnia roboczego.

---

## 5. Audyt ryzyk

### 5.1 Prawo i prywatność (najważniejsze)
| Ryzyko | Skutek | Mitygacja |
|---|---|---|
| Nagrywanie rozmówców bez ich wiedzy (RODO, regulamin pracodawcy, umowy z klientami) | odpowiedzialność prawna/dyscyplinarna | Aplikacja **nie ukrywa się**: ikona traya czerwona, dymek przy starcie. W README: obowiązek poinformowania uczestników. Opcja "Auto-record" można wyłączyć (wtedy tylko ręczny start). |
| Polityka IT firmy zabrania nagrywania / DLP | konflikt z IT | Sprawdzić przed wdrożeniem na komputerze służbowym. |
| Nagrania zawierają dane wrażliwe | wyciek | Domyślny folder w profilu użytkownika (`%USERPROFILE%\Videos\TeamsCatcher`), brak wysyłki gdziekolwiek, brak sieci w aplikacji. |

### 5.2 Techniczne
| Ryzyko | Prawdop. | Mitygacja |
|---|---|---|
| Spotkanie z **ochroną treści** (Teams Premium "Prevent screen capture", watermark) -> czarny obraz | średnie | Wykryć czarne klatki (test ffmpeg `blackdetect` przy finalizacji) -> wpis w logu i `session.json`; audio dalej nagrywane. |
| Okno zminimalizowane -> WGC nie dostarcza klatek (zamrożony obraz) | wysokie | Akceptowalne (audio ciągłe, wideo pokazuje ostatnią klatkę). Zapis w README. |
| Teams przenosi spotkanie do innego okna (pop-out, "compact view", udostępnianie ekranu) | średnie | Co 5 s sprawdzać, czy HWND nadal istnieje i jest widoczny; jeśli nie -> nowy segment wideo z nowym HWND, sklejenie przy finalizacji (concat). |
| Pre-join screen (podgląd kamery/mikrofonu) też otwiera mikrofon -> nagranie zaczyna się przed dołączeniem | średnie | Akceptowalne (kilka sekund nadmiaru). Sprawdzić w spike'u. |
| Krótkie zwolnienie mikrofonu (zmiana urządzenia audio) -> fałszywy koniec rozmowy | średnie | Debounce końca 10 s. |
| Klasyczne Teams (`Teams.exe`, `NonPackaged`) zamiast nowego | niskie (wycofane) | Detektor sprawdza oba klucze; klasyczne nie testowane E2E. |
| Microsoft zmieni nazwę pakietu Teams | niskie | Nazwa pakietu w `settings.json`, nie w kodzie. |
| Rozjazd audio/wideo | średnie | Znaczniki startu każdego strumienia (QPC) -> `-itsoffset` przy muxie. Test: klaskanie na kamerze. |
| Zabicie aplikacji / BSOD w trakcie | niskie | MKV + WAV są czytelne do momentu przerwania; przy starcie aplikacja szuka sesji bez `session.json` z końcem i finalizuje je. |
| Brak miejsca na dysku (ok. 0,5-1 GB/h przy 1080p/15 fps) | niskie | Przed startem i co minutę: < 2 GB wolne -> stop + wpis w logu + dymek. |
| ffmpeg.exe brak / zablokowany przez AV | niskie | Sprawdzenie przy starcie, czytelny komunikat w STATUS. |
| Dwie instancje aplikacji | niskie | Mutex, druga instancja pokazuje okno pierwszej i kończy się. |
| Uśpienie/blokada ekranu w trakcie | niskie | Test E2E; blokada nie przerywa WGC, uśpienie kończy rozmowę i tak. |
| Kilka monitorów, DPI 150% | średnie | WGC łapie okno w natywnej rozdzielczości; `resize_mode=scale_aspect` + stała szerokość parzysta. |

### 5.3 Wydajność
- Cel: CPU < 10% w trakcie nagrania (sprzętowy enkoder `h264_nvenc`/`h264_qsv`/`h264_amf`, fallback `h264_mf`), < 1% w spoczynku (polling rejestru 1 s).
- RAM < 150 MB.

---

## 6. Testy

### 6.1 Jednostkowe (xUnit, bez Teams, bez sprzętu)
| Klasa | Test |
|---|---|
| `CallDetector` | sygnał "mic on" 1 s -> brak startu; 2 s -> `CallStarted` dokładnie raz |
| | "mic off" 5 s i powrót -> brak `CallEnded` (debounce) |
| | "mic off" 10 s -> `CallEnded` dokładnie raz |
| | Teams nie działa / klucz nie istnieje -> stan Idle, bez wyjątku |
| | `LastUsedTimeStop` != 0 -> nieaktywny; == 0 -> aktywny (parsowanie wartości rejestru) |
| `TeamsWindowFinder` | lista okien (fake): wybiera najnowsze widoczne okno nie-główne procesu Teams |
| | tylko główne okno -> wybiera główne |
| | brak okien Teams -> `null` |
| | pomija okna niewidoczne, narzędziowe, o zerowym rozmiarze |
| `RecordingSession` | budowanie argumentów ffmpeg (hwnd, fps, enkoder, ścieżka) - snapshot |
| | nazwa folderu: data + tytuł okna oczyszczony z `\ / : * ? " < > |`, max 60 znaków |
| | brak miejsca na dysku -> odmowa startu |
| `Settings` | brak pliku -> wartości domyślne; uszkodzony JSON -> domyślne + log; zapis/odczyt round-trip |

Detektor dostaje sygnał przez interfejs `Func<bool>` (rejestr w produkcji, fake w testach) i czas przez `TimeProvider` (`FakeTimeProvider` w testach) - bez czekania w testach.

### 6.2 Integracyjne (prawdziwy Windows, bez Teams)
| Test | Jak | Kryterium |
|---|---|---|
| Izolacja audio | Proces A gra sinus 440 Hz, proces B gra 1000 Hz. Process loopback na PID A przez 3 s. | W nagraniu energia 440 Hz >> 1000 Hz (algorytm Goertzla, stosunek > 20 dB) |
| Izolacja obrazu | Okno testowe WPF wypełnione kolorem #FF00FF, na nim częściowo inne okno zielone. ffmpeg gfxcapture na HWND okna testowego, 2 s. | `ffprobe`/klatka PNG: piksel środkowy = #FF00FF (zakrycie nie przechodzi do nagrania) |
| Pełna sesja | `RecordingSession` na oknie testowym + procesie z sinusem, 5 s, stop | `meeting.mp4` ma 1 ścieżkę wideo + 2 audio, długość 5 s ± 0,5 s; `transcript.wav` = 16 kHz mono |
| Odporność na crash | Start sesji, `Process.Kill` na ffmpeg po 3 s, ponowne uruchomienie finalizacji | MP4 powstaje z ok. 3 s materiału |
| Zmiana okna | Zamknięcie okna testowego w trakcie, otwarcie nowego | Dwa segmenty sklejone w jeden plik |

### 6.3 E2E z prawdziwym Teams (ręczna checklista, ok. 1 h)
1. Połączenie 1:1 przychodzące: dzwonienie nie nagrywa, odebranie nagrywa, rozłączenie kończy w ciągu 15 s.
2. Połączenie 1:1 wychodzące.
3. Spotkanie z kalendarza (osobne okno spotkania).
4. "Meet now".
5. Wyciszenie mikrofonu w trakcie (Teams trzyma mikrofon -> nagranie trwa).
6. Udostępnianie ekranu (własne i cudze) - widać w nagraniu.
7. Okno spotkania zminimalizowane / przykryte innym oknem / przeniesione na drugi monitor.
8. YouTube gra w tle w trakcie rozmowy -> brak w nagraniu.
9. Dwie rozmowy jedna po drugiej -> dwa osobne foldery.
10. Teams zamknięty w trakcie rozmowy -> sesja kończy się poprawnie.
11. Aplikacja zabita w trakcie -> po restarcie nagranie odzyskane.
12. Spotkanie 60 min -> rozmiar pliku, CPU, synchronizacja audio/wideo na końcu.
13. Publish zip na komputerze bez .NET -> uruchamia się i nagrywa.

### 6.4 Kontrola UI (checklista z UI_STYLE.md)
Wszystkie punkty "Lista kontrolna nowej aplikacji" + zamknięcie do traya + ikona traya zmienia kolor.

---

## 7. Decyzje i pytania
1. D1: .NET 10 - **zatwierdzone** 2026-10-19.
2. D5: mikrofon jako osobna ścieżka - **zatwierdzone** 2026-10-19.
3. Otwarte: komputer służbowy czy prywatny? (wpływa na 5.1)
