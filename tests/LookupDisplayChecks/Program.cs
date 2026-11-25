using System.Buffers;
using System.Reflection;

int assertions = 0;
void Check(bool condition, string scenario)
{
    assertions++;
    if (!condition) throw new Exception(scenario);
}

const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
var searchPhrases = typeof(Lookup).GetMethod("SearchPhrases", HiddenStatic)!.CreateDelegate<
    Func<string, Func<IEnumerable<string>, bool, string, HashSet<string>>,
        Func<string, bool, IEnumerable<LookupEntry>>, Func<string, IEnumerable<LookupEntry>>,
        CancellationToken, IReadOnlyList<LookupHit>>>();
var searchWithFallback = typeof(Lookup).GetMethod("Search", HiddenStatic)!.CreateDelegate<
    Func<string, Func<string, bool, IEnumerable<LookupEntry>>, Func<IReadOnlyList<LookupHit>>, IReadOnlyList<LookupHit>>>();

LookupEntry[] entries = [
    new("shogakukan", 1, "ja", 1, "食べる", "たべる", "吃"),
    new("shogakukan", 2, "ja", 1, "読む", "よむ", "读"),
    new("shogakukan", 3, "ja", 1, "書く", "かく", "写"),
    new("shogakukan", 4, "ja", 1, "勉強する", "べんきょうする", "学习"),
    new("shogakukan", 5, "ja", 1, "着る", "きる", "穿"),
    new("shogakukan", 6, "ja", 1, "切る", "きる", "切"),
    new("shogakukan", 7, "ja", 1, "来る", "くる", "来"),
    new("shogakukan", 8, "ja", 1, "読める", "よめる", "能读"),
    new("shogakukan", 9, "ja", 1, "食ぶ", "たぶ", "古语"),
    new("shogakukan", 10, "ja", 1, "話す", "はなす", "说"),
    new("shogakukan", 11, "ja", 1, "話される", "はなされる", "被说"),
    new("shogakukan", 12, "ja", 1, "話されました", "はなされました", "原词记录"),
    new("shogakukan", 13, "ja", 1, "加工", "かこう", "加工"),
    new("shogakukan", 14, "ja", 1, "河口", "かこう", "河口"),
    new("shogakukan", 15, "ja", 1, "核", "かく", "核"),
    new("shogakukan", 16, "ja", 1, "繰る", "くる", "卷绕")
];

IEnumerable<LookupEntry> Find(string key, bool reading) => entries.Where(entry => reading
    ? entry.Reading != null && Lookup.NormalizeReading(entry.Reading) == key
    : Lookup.Normalize(entry.Headword) == key);
IReadOnlyList<LookupHit> Phrases(string input) => searchPhrases(input,
    (keys, reading, language) => keys.Where(key => Find(key, reading).Any(entry => entry.Language == language))
        .ToHashSet(StringComparer.Ordinal), Find, _ => [], CancellationToken.None);
IReadOnlyList<LookupHit> Search(string input) => searchWithFallback(input, Find, () => Phrases(input));
string Heads(IReadOnlyList<LookupHit> hits) => string.Join("|", hits.Select(hit => hit.Entry.Headword));

foreach (var (input, expected) in new[] {
    ("来たい", "来る"), ("読みたい", "読む"), ("きたい", "着る|来る"),
    ("かこう", "加工|河口"), ("食べました", "食べる"), ("読めません", "読める|読む"),
    ("よめません", "読める"), ("話されました", "話されました|話される|話す"),
    ("食べちゃった", "食べる"), ("書けちゃった", "書く"), ("かけ", "書く") })
    Check(Heads(Search(input)) == expected, "word ordering and verb guards: " + input);

var oldPhrase = Search("勉強したい");
Check(oldPhrase.Count == 1 && oldPhrase[0].Method == "phrase" && oldPhrase[0].Term == "勉強した",
    "existing phrase stays ahead of whole-word common fallback");

foreach (var (input, surface, expected) in new[] {
    ("明日は来たい。", "来たい", "来る"), ("本を読みたい。", "読みたい", "読む"),
    ("本をヨミタイ。", "ヨミタイ", "読む"), ("ここへきたい。", "きたい", "着る|来る"),
    ("かこう。", "かこう", "加工|河口"), ("ご飯を食べました。", "食べました", "食べる"),
    ("本を読めません。", "読めません", "読める"),
    ("彼は書けませんでした。", "書けませんでした", "書く") })
{
    var hits = Phrases(input);
    Check(Heads(hits) == expected, "sentence candidates and verb guards: " + input);
    Check(hits.All(hit => hit.Method == "phrase" && hit.Term == surface), "original sentence surface: " + input);
}
Check(Phrases("明日来たい。").Count == 0, "continuous kanji compound boundary stays protected");
Check(Phrases("本を書いじゃった。").Count == 0, "mismatched te-form voicing is not guessed");
entries = [.. entries, new("shogakukan", 17, "ja", 1, "期待", "きたい", "期待")];
Check(Heads(Phrases("きたい。")) == "期待", "exact noun reading suppresses common-verb fallback");
Check(Heads(Search("きたい")) == "期待", "exact word reading still suppresses common-verb fallback");

var parser = typeof(Lookup).Assembly.GetType("EntryParser")!;
var displayText = parser.GetMethod("DisplayText", HiddenStatic)!.CreateDelegate<Func<string, string, string>>();
var rulesBySource = (Dictionary<string, (string Before, string After)[]>)parser.GetField("DisplayRules", HiddenStatic)!.GetValue(null)!;
string Sequential((string Before, string After)[] rules, string input)
{
    foreach (var (before, after) in rules) input = input.Replace(before, after, StringComparison.Ordinal);
    return input;
}
foreach (var (source, rules) in rulesBySource)
{
    foreach (var (before, _) in rules)
        Check(displayText(source, "前" + before + "后") == Sequential(rules, "前" + before + "后"),
            "every rule reaches the fast-path replacement: " + source + "/" + before);
    var combined = string.Join(" / ", rules.Select(rule => rule.Before));
    Check(displayText(source, combined) == Sequential(rules, combined), "ordered overlapping display rules: " + source);
    string ordinary = "plain text 正常文本 かな (question?)";
    Check(ReferenceEquals(displayText(source, ordinary), ordinary), "unchanged text reuses the string: " + source);
}
string unknown = "防\uE40C D?bel \u2072";
Check(ReferenceEquals(displayText("unknown", unknown), unknown), "unknown dictionary retains every source character");

// Artificial chaining catches implementations that select matching rules only once from the original input.
var triggers = (Dictionary<string, SearchValues<string>>)parser.GetField("DisplayTriggers", HiddenStatic)!.GetValue(null)!;
const string SyntheticSource = "__display_cascade_test";
rulesBySource.Add(SyntheticSource, [("alpha", "beta"), ("beta", "gamma")]);
triggers.Add(SyntheticSource, SearchValues.Create(["alpha", "beta"], StringComparison.Ordinal));
try
{
    Check(displayText(SyntheticSource, "alpha beta") == "gamma gamma", "later replacement sees earlier replacement output");
}
finally
{
    rulesBySource.Remove(SyntheticSource);
    triggers.Remove(SyntheticSource);
}

Console.WriteLine($"PASS {assertions} lookup/display assertions");
