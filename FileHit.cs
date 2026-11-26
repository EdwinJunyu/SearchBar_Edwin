
namespace ESearch;

internal sealed record FileHit(string Name, string Parent, string FullPath)
{
    public bool IsFolder { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayParent => distinguishingParent ?? Parent;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WrapDisplayParent => distinguishingParent is not null;
    private string? distinguishingParent;

    // 只比较当前结果页的同名项。完整路径仍是打开、预览和定位所用的原始标识。
    internal static IReadOnlyList<FileHit> WithDisplayPaths(IReadOnlyList<FileHit> files)
    {
        var result = files.ToArray();
        var comparer = StringComparer.OrdinalIgnoreCase;
        foreach (var group in Enumerable.Range(0, files.Count).GroupBy(i => files[i].Name, comparer))
        {
            var parents = group.GroupBy(i => Path.TrimEndingDirectorySeparator(files[i].Parent), comparer).ToArray();
            if (parents.Length == 1)
            {
                foreach (var i in group)
                    if (result[i].distinguishingParent is not null) result[i] = result[i] with { distinguishingParent = null };
                continue;
            }

            // 倒序按目录组件计数。每条真实目录仅计一次，即使同一文件在结果中出现多次。
            var root = new DirectorySuffix();
            foreach (var parent in parents)
            {
                var node = root;
                foreach (var (part, _) in DirectoryParts(parent.Key))
                {
                    if (!node.Children.TryGetValue(part, out var child))
                        node.Children[part] = child = new DirectorySuffix();
                    child.Count++;
                    node = child;
                }
            }
            foreach (var parent in parents)
            {
                var node = root;
                foreach (var (part, offset) in DirectoryParts(parent.Key))
                {
                    node = node.Children[part];
                    if (node.Count != 1) continue;
                    foreach (var i in parent)
                        result[i] = files[i] with { distinguishingParent = Path.TrimEndingDirectorySeparator(files[i].Parent)[offset..] };
                    break;
                }
            }
        }
        return result;
    }

    private sealed class DirectorySuffix
    {
        public readonly Dictionary<string, DirectorySuffix> Children = new(StringComparer.OrdinalIgnoreCase);
        public int Count;
    }

    private static IEnumerable<(string Part, int Offset)> DirectoryParts(string parent)
    {
        // 路径根作为整体参与比较，UNC 服务器和共享名始终一起显示。
        int rootLength = Path.GetPathRoot(parent)?.Length ?? 0;
        int end = parent.Length;
        while (end > rootLength)
        {
            int start = end;
            while (start > rootLength && parent[start - 1] is not ('\\' or '/')) start--;
            if (start < end) yield return (parent[start..end], start);
            end = start - 1;
        }
        yield return (parent[..rootLength], 0);
    }
}

// 单独存放在当前词库旁，随 E_SEARCH_DB 隔离；发布种子与导入导出都不含个人打开记录。
