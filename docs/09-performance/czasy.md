# Czasy przetwarzania

Sprzęt: laptop z RTX 4060 8 GB. Nagranie: polska sklejka 9,6 min (TVN + Heroes + gra ×2), kanały Ja + Rozmówcy.

## Podsumowanie

| Scenariusz | Czas |
|---|---|
| Pełne przetwarzanie 9,6 min | 10:39-11:26 (diaryzacja na CPU wahała się 0:53-4:10) |
| Szacunkowo 1 h, pełne przetwarzanie po rozmowie | ~63-83 min |
| Kawałek na żywo (293 s dźwięku) | 4:42, szybciej niż czas rzeczywisty |
| Po rozmowie z kawałkami (9,6 min) | 9:38 |
| Szacunkowo 1 h z kawałkami, po końcu rozmowy | ~10-11 min |
| Przeliczenie po objaśnieniu (od stanu, bez Whispera) | 3:07-3:45 zamiast 10:39 |
| Składanie z kawałków z pyannote | 3:49 |

## Krok po kroku (pełne przetwarzanie, przed przejściem na Qwen i pyannote)

| Krok | 9,6 min | ≈ 1 h |
|---|---|---|
| Przebieg 1: próbki języka i Whisper A | 1:53 | ~7,5 min |
| Słowniczek nazw (Llama) | 0:07 | ~0:45 |
| Hipotezy B, C i mowa → EN | 1:56 | ~12 min |
| Diaryzacja (sherpa CPU) | 0:53-4:10 | ~5,5-26 min |
| Dyskusja modeli (LanguageTool + propozycje + rundy) | 3:39 | ~22 min |
| Tłumaczenie transkrypcji | 1:30 | ~9:20 |
| Notatki | 1:09 | ~6 min |
| Tłumaczenie notatek | 0:19 | ~0:30 |

Pełna tabela z podkrokami: [PLAN.md](../../PLAN.md), sekcja 1o.

## Zmiany od tego pomiaru

- pyannote na GPU: diaryzacja ~0:39 zamiast 0:53-4:10.
- Qwen 3.5 4B zamiast Gemmy: podobny czas, mniej szumu.
- Kawałki na żywo przenoszą większość pracy na czas rozmowy.

## Gdzie idzie czas

1. Dyskusja modeli (~22 min/h): jedno zapytanie na sporne zdanie.
2. Whisper i hipotezy (~20 min/h).
3. Tłumaczenie transkrypcji (~9 min/h).
4. Przełączanie modeli tekstu w VRAM: ~7-10 s każde.
