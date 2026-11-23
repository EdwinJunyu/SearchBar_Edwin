using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0 || args[0] is "help" or "--help")
{
    Console.WriteLine("""
        从项目根目录运行 E_Search。默认数据库 .data/dictionaries.db，可用 E_SEARCH_DB 指定。
        import <辞书根目录>          导入四本指定词典
        import <词典ID> <MDX路径>    单独导入或替换
        list                        查看词典标识、语言、条数、哈希与文件名
        query <输入> [词典ID]        查询全部词库或限定一本
        entry <词典ID> <序号>        查看读音依据与 MDX 原始正文
        verify                      对已导入的四本真实词典执行第一阶段验收
        """);
    return 0;
}
try
{
    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    using var store = new DictionaryStore(Environment.GetEnvironmentVariable("E_SEARCH_DB") ?? ".data/dictionaries.db");
    Source Resolve(string id) => Source.All.SingleOrDefault(s => s.Id == id)
        ?? throw new ArgumentException($"未知词典 {id}");
    IReadOnlyList<LookupHit> Search(string input,string? id) =>
        Lookup.Search(input,(key,reading) => store.Find(key,reading,id));
    switch (args)
    {
        case ["import",var root]:
            foreach (var source in Source.All)
                store.Import(source,Path.Combine(root,source.RelativePath));
            break;
        case ["import",var id,var path]:
            store.Import(Resolve(id),path);
            break;
        case ["list"]:
            store.List();
            break;
        case ["query",_] or ["query",_,_]:
            var only=args.Length==3 ? Resolve(args[2]).Id : null;
            foreach(var hit in Search(args[1],only))
                Console.WriteLine($"{hit.Entry.DictionaryId}:{hit.Entry.EntryId}\t{hit.Method}\t{hit.Term}\t{hit.Display}");
            break;
        case ["entry",var dict,var n] when long.TryParse(n,out var ord):
            var detail=store.Detail(Resolve(dict).Id,ord);
            Console.WriteLine(detail.Evidence);
            Console.WriteLine(detail.Raw);
            break;
        case ["verify"]:
            long? expectedId=null;
            foreach(var (input,method,term,display) in new[]{
                ("食べる","headword","食べる","たべる · 吃"),("たべる","reading","たべる","食べる（たべる）· 吃"),
                ("taberu","romaji","たべる","食べる（たべる）· 吃"),("食べました","deinflection","食べる","食べる（たべる）· 吃")})
            {
                var hit=Search(input,"shogakukan").FirstOrDefault() ?? throw new InvalidDataException($"{input} 无命中");
                expectedId ??= hit.Entry.EntryId;
                var evidence=store.Detail(hit.Entry.DictionaryId,hit.Entry.EntryId).Evidence;
                if (hit.Entry.EntryId!=expectedId || hit.Entry.Headword!="食べる" || hit.Entry.Reading!="たべる"
                    || hit.Method!=method || hit.Term!=term || hit.Display.Replace(" ","")!=display.Replace(" ","") || string.IsNullOrWhiteSpace(evidence))
                    throw new InvalidDataException($"{input} 命中与验收预期不同：{hit}");
                Console.WriteLine($"PASS\t{input}\t{hit.Method}\t{hit.Term}\t{hit.Entry.DictionaryId}:{hit.Entry.EntryId}\t{evidence}\t{hit.Display}");
            }
            foreach(var (dict,input,gloss) in new[]{("sanseido","食べる","eat"),("oxford","apple","苹果"),("hanying","苹果","apple")})
            {
                var hit=Search(input,dict).FirstOrDefault() ?? throw new InvalidDataException($"{dict} 未命中 {input}");
                if (hit.Entry.Gloss != gloss || string.IsNullOrWhiteSpace(store.Detail(dict,hit.Entry.EntryId).Raw))
                    throw new InvalidDataException($"{dict} 词条或首义与样本不符");
                Console.WriteLine($"PASS\t{input}\t{dict}:{hit.Entry.EntryId}\t{hit.Display}");
            }
            break;
        default: throw new ArgumentException("参数无效；运行 help 查看用法");
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

internal sealed record Source(string Id,string Language,int Priority,string RelativePath)
{
    // 同级命中优先使用双语词典；三省堂还混有日语和英语释义。
    public static readonly Source[] All = [
        new("shogakukan","ja",1,"中日/[5《小学馆V2日汉辞典》.mdx"),
        new("sanseido","ja",4,"中日/[3三省堂超级大辞林第二版.mdx"),
        new("oxford","en",2,"英语/[英-汉] ◆【牛津高阶学习词典英汉双解第7版】【全新孤雷排版】【2011.7.31】.mdx"),
        new("hanying","zh",3,"英语/现代汉英综合大辞典.mdx")
    ];
}
