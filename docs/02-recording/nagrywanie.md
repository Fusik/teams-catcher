# Nagrywanie, finalizacja, odzyskiwanie

## Dźwięk Teams

- WASAPI **process loopback** (własny interop `ProcessLoopback.cs`, NAudio tylko do zapisu WAV). Nagrywa dźwięk wybranego procesu i nic poza nim. Wymaga Windows 10 2004+ / 11.
- Nowy Teams odtwarza dźwięk w procesach dzieci (WebView2 `msedgewebview2`, proces mediów). Loopback "z drzewem" na `ms-teams.exe` łapał ciszę (-96,7 dB). Dlatego `TeamsProcesses.AudioPids` znajduje wszystkie procesy z rodziny Teams, które mają sesję audio. Każdy z nich nagrywa się osobno (`teams_PID.wav`), nowe dochodzą w trakcie rozmowy, a przy finalizacji wszystkie są miksowane.
- Aktywacja WASAPI zawsze z wątku MTA. Ręczny start z wątku UI (STA) dawał błąd `E_NOINTERFACE`.
- Spike: izolacja procesów ok. 88 dB (440 Hz vs 1000 Hz). Proces bez dźwięku daje ciszę w czasie rzeczywistym, bez rozjazdu z wideo.

## Mikrofon

Osobna ścieżka `mic.wav`, wyłączalna. Brak mikrofonu w systemie: wpis w logu i nagranie bez tej ścieżki.

## Obraz

- ffmpeg `gfxcapture` (Windows.Graphics.Capture, jak "Window Capture (WGC)" w OBS) z `hwnd=` okna spotkania. Przykrycie innym oknem nie trafia do nagrania.
- libx264 CRF 18, ramka 16:9: 1080p / 1440p / 2160p, 5 / 15 / 30 fps (domyślnie 1080p, 15 fps). `h264_mf` ignorował bitrate (136 kb/s).
- Zapis do MKV (odporny na zabicie procesu) z `-tune zerolatency -flush_packets 1 -g 30 -cluster_time_limit 1000`. ffmpeg kończy się przez "q", a po 2 s jest ubijany, bo zminimalizowane okno blokuje "q". Strata to najwyżej ok. 1 s.
- Okno zminimalizowane: brak klatek (wideo pokazuje ostatnią), dźwięk ciągły.
- `RecordVideo = false`: bez obrazu, `meeting.mp4` ma sam dźwięk.

## Prezentacja (udostępnianie)

- Teams minimalizuje okno spotkania i pokazuje ramkę `ScreenBorderWindow`. Nagrywany jest wtedy udostępniony monitor albo okno pod ramką, a po zakończeniu znowu okno spotkania. Zmiana celu musi trwać 2 s.
- Dźwięk prezentacji nagrywa się tylko w czasie udostępniania:
  - udostępnione okno: procesy tej aplikacji z sesją audio;
  - udostępniony ekran: wszystkie aplikacje z dźwiękiem poza Teams.
- Segmenty wideo sklejane w jedno MP4 (concat z `duration`, przerwy zachowane na osi czasu).

## Synchronizacja

Każda klatka ma znacznik czasu uniksowego (`setpts=RTCTIME`), a audio zapisuje czas pierwszego pakietu. Przy finalizacji ffmpeg odczytuje pierwszą klatkę (`-copyts -f framecrc`) i robi mux z `-itsoffset`. Wcześniejsze metody były błędne: ffmpeg startuje 0,3-3 s, a WGC nie wysyła klatek statycznego okna.

## Finalizacja

1. `meeting.mp4`: obraz + miks Teams, mikrofonu i prezentacji.
2. `transcript.wav`: 16 kHz stereo (L mikrofon, R Teams + prezentacja), czas 0 = najwcześniejszy z: pierwsza klatka wideo, dźwięk Teams, mikrofon.
3. Sprzątanie: usuwane `teams_*.wav`, `mic.wav`, `video_01.mkv` (są już w MP4). Przy błędzie ffmpeg źródła zostają. Sesja bez danych powoduje usunięcie folderu.

## Odzyskiwanie po awarii

- `session.json` zapisywany co 10 s.
- Przy starcie `RecordingSession.RecoverAsync` kończy sesje z `Finalized = false`: naprawia nagłówki WAV (po zabiciu procesu rozmiary = 0), potem wykonuje normalną finalizację. Gdy awaria nastąpiła przed pierwszym zapisem, start audio przyjmuje się jako start sesji.
- Wylogowanie lub wyłączenie Windows: nagranie domykane (do 20 s).

## Ograniczenia

- WAV max 4 GB (ok. 6 h audio Teams), dłuższe nagrania wymagają RF64.
- Minimalne wolne miejsce: `MinFreeBytes` (2 GB).
- Spotkanie z ochroną treści (Teams Premium) może dać czarny obraz. Dźwięk dalej się nagrywa.
