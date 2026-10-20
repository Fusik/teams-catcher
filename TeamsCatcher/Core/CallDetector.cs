namespace TeamsCatcher.Core;

public enum CallState { Idle, InCall }

// Debounce sygnału "trwa rozmowa" (pasek rozmowy chwilowo znika np. przy zmianie widoku).
public sealed class CallDetector(TimeSpan startDelay, TimeSpan endDelay)
{
    DateTime? _pendingSince;

    public CallState State { get; private set; }

    // Zwraca true, gdy stan się zmienił.
    public bool Update(bool inCall, DateTime now)
    {
        var wanted = inCall ? CallState.InCall : CallState.Idle;
        if (wanted == State) { _pendingSince = null; return false; }
        _pendingSince ??= now;
        if (now - _pendingSince < (wanted == CallState.InCall ? startDelay : endDelay)) return false;
        State = wanted;
        _pendingSince = null;
        return true;
    }
}
