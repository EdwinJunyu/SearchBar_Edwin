internal sealed record ReverseSearchResult(IReadOnlyList<LookupHit> Hits, bool ModelReady, string? ModelError);

// CLI 可直接尝试现有模型；桌面还需报告已准备索引的状态，并尊重语义查询开关。
internal static class ReverseSearchService
{
    internal static ReverseSearchResult Search(DictionaryStore store, string description, string? only,
        string? targetLanguage, CancellationToken token, string modelDirectory, bool semanticRequested,
        bool requirePreparedIndex = true, Action? beforeModelLoad = null)
    {
        bool modelReady = false;
        string? modelError = null;
        if (ReverseEncoder.CanOpen(modelDirectory))
        {
            try
            {
                var encoder = ReverseEncoder.Open(modelDirectory);
                if (requirePreparedIndex)
                    modelReady = store.HasPreparedReverseLanguage(only, encoder, ReverseEncoder.CacheDirectory, token, targetLanguage);
                if (semanticRequested && (!requirePreparedIndex || modelReady))
                {
                    if (beforeModelLoad != null && !encoder.IsSessionReady)
                    {
                        beforeModelLoad();
                        token.ThrowIfCancellationRequested();
                    }
                    var hits = store.ReverseByLanguage(description, only, targetLanguage, token, encoder, ReverseEncoder.CacheDirectory);
                    if (!requirePreparedIndex)
                        modelReady = hits.Any(hit => hit.UsedSemanticIndex);
                    // 部分来源可能只有词语索引。读后复验也没有模型索引时才提示重新准备。
                    else if (hits.Count > 0 && !hits.Any(hit => hit.UsedSemanticIndex)
                        && !store.HasPreparedReverseLanguage(only, encoder, ReverseEncoder.CacheDirectory, token, targetLanguage))
                    {
                        modelReady = false;
                        modelError = "语义索引未通过校验，可重新准备。";
                    }
                    return new(hits, modelReady, modelError);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or TypeInitializationException or DllNotFoundException)
            {
                modelReady = false;
                modelError = ex.GetBaseException().Message;
            }
        }
        return new(store.ReverseByLanguage(description, only, targetLanguage, token), modelReady, modelError);
    }
}
