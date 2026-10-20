# Benchmarki i nagrania testowe

## Nagrania testowe

| Nagranie | Co sprawdza |
|---|---|
| wywiad TVN ("Lalka", ~80 s, kilka osób) | nazwiska (Dorociński, Stuhr, Kawulski), zmyślenia w notatkach, tłumaczenie |
| komentarz Heroes III (gra) | żargon ("diamentowy golem", "consa", "armatrap", "DDK"), błędy słuchu ("jamentowy") |
| sklejka 9,6 min (TVN + Heroes + gra ×2) | czasy etap po etapie, kawałki na żywo, diaryzacja |
| nagrania Teams solo | wykrywanie rozmowy, wyciszenie, okno, prezentacja |

## Model sprawdzający notatki (14 punktów z prawdziwych nagrań)

Zadanie: czy transkrypcja potwierdza punkt notatki (TAK/NIE). Część punktów była zmyślona.

| Model | Trafnie | Zmyślenia przepuszczone |
|---|---|---|
| Gemma 4 E2B | 10/14 | 2 |
| Qwen 3.5 4B | 11/14 | 1 |
| Llama 3.1 8B | 12/14 | - (pisze notatki, więc nie sprawdza samej siebie) |

## Korekta transkrypcji

| Przypadek | Wynik |
|---|---|
| Heroes "jamentowy golem" ×3 | 3/3 → "diamentowy golem" (dzięki hipotezie B i wskazaniu LanguageTool) |
| Heroes "z pulką" | → "z półką" |
| TVN "stanarzysty" | → "scenarzysty", bez regresji |
| Bez drugiej hipotezy | Bielik i qwen3.5:9b zgadywały "jamowym", "jedenastym", więc druga hipoteza jest konieczna |

## Diaryzacja (sklejka 9,6 min)

| Silnik | Rozmówców | Czas |
|---|---|---|
| pyannote community-1 (GPU) | 9 | 0:39 |
| sherpa-onnx (CPU) | 11 | 0:49-4:10 |

Pliki testowe k2-fsa (sherpa): 4/2/3 mówców przy prawdziwych 4/2/2.
