using ESearch;

namespace ESearch.Tests;

// PowerShell adapters use compiled calls to the real internal implementation.
// Object parameters carry internal domain records without exposing them in production's public API.
public static class CoreLogic
{
    public static object FormatDetail(string raw, object? anchor, string source) =>
        EntryDetailFormatter.FormatDetail(raw, (EntryAnchor?)anchor, source);

    public static byte[] EncodeMergedDocument(string html, string path, IReadOnlyDictionary<string, string>? aliases) =>
        EntryDetailFormatter.EncodeMergedDocument(html, path, aliases);

    public static string RewriteMergedLinks(string html, string path) =>
        EntryDetailFormatter.RewriteMergedLinks(html, path);

    public static object? HighlightMergedPhrase(object article, LookupEntry hit, string raw) =>
        EntryDetailFormatter.HighlightMergedPhrase((MergedArticle)article, hit, raw);

    public static int CheckCalculator()
    {
        int checks = 0;
        foreach (var (input, expected) in new (string, string)[]
        {
            ("2+3*4", "14"),
            ("(2+3)*4", "20"),
            (" -(2 + (3 - 1)) * 4", "-16"),
            ("18 ÷ 3 × 2", "12"),
            ("-0.5", "-0.5"),
            ("1.2 / -0.4", "-3"),
            ("0.1+0.2", "0.3"),
            ("9007199254740993+1", "9007199254740994"),
            ("18/3/2", "3"),
            ("10-3-2", "5"),
            ("2^3^2", "512"),
            ("-2^2", "-4"),
            ("(-2)^2", "4"),
            ("2^-3", "0.125"),
            ("sqrt(2^4)+abs(-3)", "7"),
            ("sin(30)", "0.5"),
            ("cos(180)", "-1"),
            ("cos(90)", "0"),
            ("tan(45)", "1"),
            ("exp(ln(e)-1)", "1"),
            ("log(1000)", "3"),
            ("LOG10(100)", "2"),
            ("10^-40", "1E-40"),
            ("π/pi", "1"),
            ("1e3+1", "1001"),
            ("1E-3*2", "0.002"),
        })
        {
            if (!Calculator.TryCalculate(input, out var result) || result != expected)
                throw new Exception($"Calculator {input}: expected {expected}, got {result}");
            checks++;
        }
        foreach (string input in new[]
        {
            "2026",
            "2.5",
            "pi",
            "π",
            "e",
            "report(2).pdf",
            "2+3.txt",
            "(2+3",
            "2+3)",
            "1/0",
            "2(3)",
            "sqrt(16).pdf",
            "unknown(16)",
            "sqrt(-1)",
            "ln(0)",
            "tan(90)",
            "(-2)^0.5",
            "exp(10000)",
            "1/(1e308*1e308)",
            "sqrt(-1)^0",
            "2^",
            "1e+",
            "1e-3",
            string.Concat(Enumerable.Repeat("2^", 1000)) + "1",
            string.Concat(Enumerable.Repeat("sqrt(", 1000)) + "1" + new string(')', 1000)
        })
        {
            if (Calculator.TryCalculate(input, out _))
                throw new Exception($"Ordinary or invalid search was recognized as calculation: {input}");
            checks++;
        }
        return checks;
    }
}
