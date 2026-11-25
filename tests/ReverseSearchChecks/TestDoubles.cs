// 编译并测试真实的服务控制流；不加载 SQLite、模型文件或本机 ONNX 库。
internal sealed record LookupHit(bool UsedSemanticIndex);

internal sealed class ReverseEncoder
{
    internal static bool Available { get; set; }
    internal static Exception? OpenError { get; set; }
    internal static int OpenCalls { get; private set; }
    internal static ReverseEncoder Instance { get; private set; } = new();
    internal static string CacheDirectory => "test-cache";
    internal bool IsSessionReady { get; set; }

    internal static bool CanOpen(string directory) => Available;
    internal static ReverseEncoder Open(string directory)
    {
        OpenCalls++;
        if (OpenError != null) throw OpenError;
        return Instance;
    }
    internal static void Reset()
    {
        Available = true;
        OpenError = null;
        OpenCalls = 0;
        Instance = new();
    }
}

internal sealed class DictionaryStore
{
    internal List<string> Calls { get; } = [];
    internal Queue<bool> Prepared { get; } = new();
    internal IReadOnlyList<LookupHit> ModelHits { get; init; } = [new(true)];
    internal IReadOnlyList<LookupHit> LexicalHits { get; } = [new(false)];
    internal Exception? ModelError { get; init; }
    internal Exception? PreparationError { get; init; }
    internal Exception? LexicalError { get; init; }
    internal (string Description, string? Only, string? Language) LastScope { get; private set; }

    internal bool HasPreparedReverseLanguage(string? only, ReverseEncoder encoder, string cacheDirectory,
        CancellationToken token, string? targetLanguage)
    {
        Calls.Add("prepared");
        token.ThrowIfCancellationRequested();
        if (PreparationError != null) throw PreparationError;
        return Prepared.TryDequeue(out bool ready) && ready;
    }

    internal IReadOnlyList<LookupHit> ReverseByLanguage(string description, string? only, string? targetLanguage,
        CancellationToken token, ReverseEncoder? encoder = null, string? cacheDirectory = null)
    {
        Calls.Add(encoder == null ? "lexical" : "model");
        LastScope = (description, only, targetLanguage);
        token.ThrowIfCancellationRequested();
        if ((encoder == null ? LexicalError : ModelError) is { } error) throw error;
        return encoder == null ? LexicalHits : ModelHits;
    }
}

namespace Microsoft.ML.OnnxRuntime
{
    internal sealed class OnnxRuntimeException(string message) : Exception(message) { }
}
