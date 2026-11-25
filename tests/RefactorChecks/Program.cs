using ESearch;

int assertions = 0;
void Check(bool condition, string scenario)
{
    assertions++;
    if (!condition) throw new Exception(scenario);
}
foreach (var (input, expected) in new[] { ("0.1+0.2", "0.3"), ("2+3*4", "14"), ("2^3^2", "512"),
    ("-2^2", "-4"), ("2^-3", "0.125"), ("cos(90)", "0"), ("9007199254740993+1", "9007199254740994") })
    Check(Calculator.TryCalculate(input, out var result) && result == expected, input);
foreach (string input in new[] { "2026", "report(2).pdf", "1/0", "tan(90)", "sqrt(-1)^0", "1e-3", new string('(', 1000) + "1" })
    Check(!Calculator.TryCalculate(input, out _), "ordinary or invalid: " + input);
using var cancel = new CancellationTokenSource();
var request = new SearchRequestContext(7, 3, cancel.Token);
Check(request.IsCurrent(7, true), "current input");
Check(!request.IsCurrent(8, true), "edited input");
Check(!request.IsCurrent(7, false), "hidden window");
Check(request.IsCatalogCurrent(7, 3, true), "current catalog");
Check(!request.IsCatalogCurrent(7, 4, true), "catalog changed");
Check(request.IsCurrent(7, true), "catalog change does not invalidate files");
Check(!request.CanPublishSuggestions(7, 3, true, true), "IME composition");
Check(request.CanPublishSuggestions(7, 3, true, false), "composition ended");
cancel.Cancel();
Check(!request.IsCurrent(7, true), "cancelled input");
Check(!request.IsCatalogCurrent(7, 3, true), "cancelled catalog");
Console.WriteLine($"PASS {assertions} calculator/context assertions");
