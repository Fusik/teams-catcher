# Diagnostyka

## Gdzie patrzeć

1. **Log w oknie:** każdy krok potoku ma linię `step: ...`, a błędy mają opis ("Transcription failed: ...", "pyannote diarization failed, using sherpa-onnx: ...").
2. **`analysis\00-status.md`** w folderze nagrania: który model co zrobił i na czym stanął.
3. **`analysis\0X-*.md`:** instrukcja i wynik każdego modelu. Widać tam, dlaczego zdanie albo punkt notatki zostało zmienione albo odrzucone.
4. **Zapytania do modeli:** zmienna środowiskowa `TC_LLM_LOG=C:\ścieżka\llm.log` zapisuje każde zapytanie i odpowiedź Ollamy.

## Typowe problemy

| Objaw | Przyczyna | Co zrobić |
|---|---|---|
| "Transcription skipped, file not found: ...whisper-cli.exe" | brak plików Whispera obok exe (np. uruchomienie z `bin\Debug`) | uzupełnij ścieżki w `settings.json` albo skopiuj folder `whisper` |
| "Ollama with ... is not available" | Ollama nie działa albo brak modelu | uruchom Ollamę, `ollama pull llama3.1:8b`, `ollama pull qwen3.5:4b` |
| bardzo wolne notatki i tłumaczenie | Ollama na CPU | zrestartuj Ollamę po instalacji sterownika NVIDIA |
| "pyannote diarization failed" | brak modelu, błąd Pythona albo CUDA | sprawdź `pyannote-models\community-1\config.yaml`; fallback sherpa działa sam |
| za dużo Rozmówców | diaryzacja zgaduje liczbę | ustaw OTHERS na znaną liczbę osób |
| `??słowo??` w transkrypcji | modele się nie zgodziły | objaśnij w TO CLARIFY, Save and recalculate |
| cisza na ścieżce Teams | proces audio Teams się zmienił | log pokazuje nagrywane PID-y; zgłoś przypadek |

## Narzędzia deweloperskie (scratchpad, nie w repo)

Skrypty `dotnet run` używane w testach: `transcribe.cs` (pełny potok na folderze, argument `state` = od stanu), `simulate.cs` (symulacja kawałków na żywo), `import.cs` (import wideo), `bench.cs` (benchmark modelu sprawdzającego), `render.cs` (zrzut okna do PNG bez uruchamiania aplikacji).
