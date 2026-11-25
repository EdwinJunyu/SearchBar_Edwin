int assertions = 0;
void Check(bool condition, string scenario)
{
    assertions++;
    if (!condition) throw new Exception(scenario);
}
void Throws<T>(Action action, string scenario) where T : Exception
{
    try { action(); }
    catch (T) { assertions++; return; }
    throw new Exception(scenario);
}
ReverseSearchResult Search(DictionaryStore store, bool semanticRequested = true, bool requirePreparedIndex = true,
    Action? beforeModelLoad = null, CancellationToken token = default) =>
    ReverseSearchService.Search(store, "中文描述", "dictionary", "ja", token, "model-folder",
        semanticRequested, requirePreparedIndex, beforeModelLoad);

ReverseEncoder.Reset();
ReverseEncoder.Available = false;
var missing = new DictionaryStore();
var missingResult = Search(missing);
Check(missing.Calls.SequenceEqual(["lexical"]), "missing model uses lexical search once");
Check(!missingResult.ModelReady && missingResult.ModelError == null, "missing model is not a model failure");
Check(ReferenceEquals(missingResult.Hits, missing.LexicalHits), "lexical results are returned intact");
Check(ReverseEncoder.OpenCalls == 0, "missing model is not opened");

ReverseEncoder.Reset();
var cli = new DictionaryStore { PreparationError = new InvalidDataException("CLI must not probe readiness") };
var cliResult = Search(cli, requirePreparedIndex: false);
Check(cli.Calls.SequenceEqual(["model"]), "CLI attempts semantic search without prepared-index preflight");
Check(cliResult.ModelReady && ReferenceEquals(cliResult.Hits, cli.ModelHits), "CLI returns semantic results");
Check(cli.LastScope == ("中文描述", "dictionary", "ja"), "query and scope reach semantic search unchanged");

ReverseEncoder.Reset();
var disabled = new DictionaryStore();
disabled.Prepared.Enqueue(true);
var disabledResult = Search(disabled, semanticRequested: false);
Check(disabled.Calls.SequenceEqual(["prepared", "lexical"]), "GUI still probes readiness when semantic search is off");
Check(disabledResult.ModelReady && disabledResult.ModelError == null, "GUI keeps ready state when semantic search is off");
Check(disabled.LastScope == ("中文描述", "dictionary", "ja"), "fallback preserves query and scope");

ReverseEncoder.Reset();
var unprepared = new DictionaryStore();
var unpreparedResult = Search(unprepared);
Check(unprepared.Calls.SequenceEqual(["prepared", "lexical"]), "GUI does not use an unprepared semantic index");
Check(!unpreparedResult.ModelReady, "unprepared index is reported as not ready");

ReverseEncoder.Reset();
var ready = new DictionaryStore();
ready.Prepared.Enqueue(true);
int loadingCalls = 0;
var readyResult = Search(ready, beforeModelLoad: () =>
{
    loadingCalls++;
    Check(ready.Calls.SequenceEqual(["prepared"]), "loading notification precedes semantic search");
});
Check(ready.Calls.SequenceEqual(["prepared", "model"]), "semantic hits do not cause another readiness probe");
Check(loadingCalls == 1 && readyResult.ModelReady, "cold model reports loading once");
ReverseEncoder.Instance.IsSessionReady = true;
ready.Prepared.Enqueue(true);
Search(ready, beforeModelLoad: () => loadingCalls++);
Check(loadingCalls == 1, "warm model does not report loading");

ReverseEncoder.Reset();
var invalid = new DictionaryStore { ModelHits = [new(false)] };
invalid.Prepared.Enqueue(true);
invalid.Prepared.Enqueue(false);
var invalidResult = Search(invalid);
Check(invalid.Calls.SequenceEqual(["prepared", "model", "prepared"]), "lexical-only results recheck an initially prepared index");
Check(!invalidResult.ModelReady && invalidResult.ModelError == "语义索引未通过校验，可重新准备。",
    "invalidated index reports the existing preparation message");
Check(ReferenceEquals(invalidResult.Hits, invalid.ModelHits), "failed post-read validation keeps returned hits");

ReverseEncoder.Reset();
var mixedSources = new DictionaryStore { ModelHits = [new(false)] };
mixedSources.Prepared.Enqueue(true);
mixedSources.Prepared.Enqueue(true);
var mixedResult = Search(mixedSources);
Check(mixedResult.ModelReady && mixedResult.ModelError == null, "a remaining prepared source keeps the model ready");

ReverseEncoder.Reset();
var empty = new DictionaryStore { ModelHits = [] };
empty.Prepared.Enqueue(true);
var emptyResult = Search(empty);
Check(empty.Calls.SequenceEqual(["prepared", "model"]) && emptyResult.ModelReady,
    "empty semantic results do not invalidate preparation");

foreach (Exception failure in new Exception[]
{
    new InvalidDataException("invalid index"), new IOException("read failed"),
    new UnauthorizedAccessException("denied"), new Microsoft.ML.OnnxRuntime.OnnxRuntimeException("ONNX failed"),
    new TypeInitializationException("runtime", new Exception("base failure")), new DllNotFoundException("missing native runtime")
})
{
    ReverseEncoder.Reset();
    var failed = new DictionaryStore { ModelError = failure };
    var failedResult = Search(failed, requirePreparedIndex: false);
    Check(failed.Calls.SequenceEqual(["model", "lexical"]), failure.GetType().Name + " retries lexical search exactly once");
    Check(!failedResult.ModelReady && failedResult.ModelError == failure.GetBaseException().Message,
        failure.GetType().Name + " reports its base error");
}

ReverseEncoder.Reset();
ReverseEncoder.OpenError = new InvalidDataException("bad vocabulary");
var badModel = new DictionaryStore();
var badModelResult = Search(badModel);
Check(badModel.Calls.SequenceEqual(["lexical"]) && badModelResult.ModelError == "bad vocabulary",
    "encoder-open failure falls back once");

ReverseEncoder.Reset();
var badPreparation = new DictionaryStore { PreparationError = new IOException("index unreadable") };
var badPreparationResult = Search(badPreparation);
Check(badPreparation.Calls.SequenceEqual(["prepared", "lexical"]) && badPreparationResult.ModelError == "index unreadable",
    "readiness failure falls back once");

ReverseEncoder.Reset();
using var cancellation = new CancellationTokenSource();
var canceledLoading = new DictionaryStore();
canceledLoading.Prepared.Enqueue(true);
Throws<OperationCanceledException>(() => Search(canceledLoading, beforeModelLoad: cancellation.Cancel, token: cancellation.Token),
    "cancellation during loading notification must propagate");
Check(canceledLoading.Calls.SequenceEqual(["prepared"]), "loading cancellation does not run model or fallback");

ReverseEncoder.Reset();
var canceledSearch = new DictionaryStore { ModelError = new OperationCanceledException() };
Throws<OperationCanceledException>(() => Search(canceledSearch, requirePreparedIndex: false), "search cancellation must propagate");
Check(canceledSearch.Calls.SequenceEqual(["model"]), "search cancellation never degrades to lexical search");

ReverseEncoder.Reset();
var unexpected = new DictionaryStore { ModelError = new InvalidOperationException("unexpected") };
Throws<InvalidOperationException>(() => Search(unexpected, requirePreparedIndex: false), "unexpected errors must propagate");
Check(unexpected.Calls.SequenceEqual(["model"]), "unexpected errors do not trigger fallback");

ReverseEncoder.Reset();
var failedFallback = new DictionaryStore { ModelError = new IOException("model failed"), LexicalError = new IOException("lexical failed") };
Throws<IOException>(() => Search(failedFallback, requirePreparedIndex: false), "lexical failure must propagate");
Check(failedFallback.Calls.SequenceEqual(["model", "lexical"]), "failed lexical search is not retried recursively");

Console.WriteLine($"PASS {assertions} reverse-search policy assertions");
