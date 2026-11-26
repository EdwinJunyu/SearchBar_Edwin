// Summary compatibility stays SQL-filtered: only a matched, stale source loads its body.
// Projection aliases connect the policy to the reader without positional column contracts.
internal sealed partial class DictionaryStore
{
    private enum SummaryBody { Shogakukan, Oxford, Sanseido, Hanying, Shinseiki }

    private sealed class SummaryPolicy(MetadataSchema schema, bool phrasalSenses)
    {
        internal static int PrefixLength(SummaryBody body) => body switch
        {
            SummaryBody.Shogakukan => 4096,
            SummaryBody.Sanseido => 2048,
            _ => 0
        };

        internal string HanyingParser => $"""
            d.language='zh' AND d.filename LIKE '%.mdx' AND
            COALESCE({(schema.HasParser ? "m.parser_id" : "NULL")},
                CASE WHEN d.id='hanying' OR d.filename=$hanyingName THEN 'hanying' END)='hanying'
            """;

        internal string CurrentSummary => schema.IsCurrent ? "COALESCE(m.summary_stamp = ($summaryFormat || m.revision),0)" : "0";
        // 三省堂旧戳既可能保留属性残片，也可能只保留词性或词头。按来源版本刷新实际命中项。
        private string JapanesePrefix(string format) => schema.IsCurrent && (schema.HasMdxRecords || schema.HasMergedRanges)
            ? $"AND ({schema.RangeColumn("record_start")} IS NULL OR NOT COALESCE(m.summary_stamp=('{format}' || m.revision),0))" : "";

        internal string Projection => $"""
                CASE WHEN d.filename LIKE '%.mdx' AND COALESCE({(schema.HasParser ? "m.parser_id" : "NULL")},
                    CASE WHEN e.dict='shogakukan' OR e.evidence='MDX 正文首行假名' THEN 'shogakukan' END)='shogakukan'
                    AND (({(schema.HasRevision ? "m.revision IS NULL AND" : "")}
                        (e.reading IS NULL OR replace(replace(e.gloss,'・',''),'－','')=e.reading))
                        OR substr(e.gloss,-1)='〕' OR e.gloss GLOB '〈*〉' OR e.gloss GLOB '《*》')
                    {JapanesePrefix(JapaneseSummaryFormat)} THEN substr(e.raw,1,{PrefixLength(SummaryBody.Shogakukan)}) END AS Shogakukan,
                CASE WHEN d.filename LIKE '%.mdx' AND COALESCE({(schema.HasParser ? "m.parser_id" : "NULL")},
                    CASE WHEN d.id='oxford' OR d.filename=$oxfordName THEN 'oxford' END)='oxford'
                    AND {(phrasalSenses ? "1" : $"NOT ({CurrentSummary})")} THEN e.raw END AS Oxford,
                CASE WHEN d.filename LIKE '%.mdx' AND COALESCE({(schema.HasParser ? "m.parser_id" : "NULL")},
                    CASE WHEN d.id='sanseido' OR d.filename=$sanseidoName THEN 'sanseido' END)='sanseido'
                    {JapanesePrefix(SanseidoSummaryFormat)} THEN substr(e.raw,1,{PrefixLength(SummaryBody.Sanseido)}) END AS Sanseido,
                CASE WHEN {HanyingParser} AND (instr(e.head,' ')>0 OR instr(e.head,'　')>0 OR instr(e.head,char(123))>0)
                    THEN e.raw END AS Hanying,
                CASE WHEN d.filename LIKE '%.mdx' AND COALESCE({(schema.HasParser ? "m.parser_id" : "NULL")},
                    CASE WHEN d.id='shinseiki' OR d.filename=$shinseikiName THEN 'shinseiki' END)='shinseiki'
                    AND {(schema.IsCurrent ? $"NOT COALESCE(m.summary_stamp=('{ShinseikiSummaryFormat}' || m.revision),0)" : "1")}
                    AND e.gloss GLOB '*[A-Za-z0-9０-９]*'
                    AND ({schema.RangeColumn("record_start")} IS NOT NULL OR instr(e.raw,'<su')>0)
                    THEN e.raw END AS Shinseiki
            """;

        internal IEnumerable<LookupEntry> Apply(LookupEntry entry, string key, bool legacyChinese,
            bool currentSummary, Func<SummaryBody, string?> matchedBody, Func<string?> originalBody)
        {
            if (phrasalSenses)
            {
                // Oxford 投影只给已识别的牛津 MDX 原文；继续保留父词条的词典、序号及版本以打开详情。
                if (entry.Language == "en" && Lookup.Normalize(entry.Headword) == key && matchedBody(SummaryBody.Oxford) is { } phraseBody)
                    foreach (var phrase in EntryParser.OxfordPhrasalSenses(phraseBody, key))
                        yield return entry with { Headword = phrase.Headword, Gloss = phrase.Gloss, Anchor = phrase.Anchor };
                yield break;
            }
            var gloss = entry.Gloss;
            var child = matchedBody(SummaryBody.Hanying) is { } childBody ? EntryParser.HanyingReference(entry.Headword, childBody) : null;
            if (legacyChinese && (child == null || Lookup.Normalize(child.Value.Headword) != key)) yield break;
            if (child is { } subentry)
            {
                yield return entry with { Headword = subentry.Headword, Gloss = subentry.Gloss };
                yield break;
            }
            if (matchedBody(SummaryBody.Shogakukan) is { } japanesePrefix) gloss = EntryParser.RefreshShogakukanGloss(gloss, japanesePrefix);
            if (matchedBody(SummaryBody.Sanseido) is { } sanseidoPrefix) gloss = EntryParser.RefreshSanseidoGloss(gloss, sanseidoPrefix);
            // 旧新世纪索引只为本次命中的可疑摘要取正文。原模板和原摘要同时验证成功才恢复上下标。
            if (matchedBody(SummaryBody.Shinseiki) is { } shinBody) gloss = EntryParser.RefreshShinseikiScriptGloss(entry.Headword, gloss, shinBody);
            // 无版本或旧格式索引需要重算。新索引的普通释义直接来自 gloss 列。
            (string Headword, string? Form)? reference = null;
            if (matchedBody(SummaryBody.Oxford) is { } body)
            {
                gloss = EntryParser.OxfordGloss(body);
                if (Lookup.Normalize(gloss) == Lookup.Normalize(entry.Headword))
                    reference = EntryParser.OxfordReferenceHeadword(body);
            }
            else if (currentSummary && Lookup.Normalize(gloss) == Lookup.Normalize(entry.Headword))
            {
                // 参见词条可能只存有词头。仅在这种情况下按主键读取正文中的目标和词形。
                var original = originalBody();
                if (original != null) reference = EntryParser.OxfordReferenceHeadword(original);
            }
            yield return entry with { Gloss = gloss, Reference = reference };
        }
    }
}
