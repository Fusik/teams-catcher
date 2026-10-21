namespace TeamsCatcher.Core;

// Podobieństwo słów (odmiana, odległość edycyjna) dla notatek i filtra nazw własnych. Dawny słowniczek użytkownika (pole GLOSSARY) usunięty:
// pisownię objaśnia użytkownik tylko tam, gdzie coś jest niejasne (TO CLARIFY -> baza wiedzy).
public static class Glossary
{
    // Różnica tylko w końcówce (ostatnie <= 3 litery) = polska odmiana ("Tekli" od "Tekla"), nie literówka - nie zmieniamy.
    public static bool IsInflection(string word, string term)
    {
        var common = 0;
        while (common < Math.Min(word.Length, term.Length) && word[common] == term[common]) common++;
        return common >= Math.Min(word.Length, term.Length) - 3;
    }

    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
