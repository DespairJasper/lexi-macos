using System.Globalization;
using System.Text;
using Lexi.Core;

namespace Lexi;

public static partial class ArchiveTransferService
{
    private static readonly string[] Headers = ["单词", "音标", "中文释义", "英文释义", "备注", "来源类型", "来源", "来源原句", "标签", "UUID", "阶段", "状态", "入库日期", "学习起始日期", "下次复习日期", "上次复习时间", "复习次数", "遇见次数", "版本", "创建时间UTC", "更新时间UTC", "最后遇见时间UTC", "包含AI扩展", "例句英文", "例句中文", "同义词", "反义词", "常用词组", "词组中文"];
    private static string Pack(IEnumerable<string> items) => string.Join("\n", items.Select(s => s.Length == 0 ? "\\0" : s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n")));
    private static List<string> Unpack(string value)
    {
        if (value.Length == 0) return [];
        return value.Split('\n').Select(line => {
            if (line == "\\0") return "";
            var result = new StringBuilder();
            for (var i = 0; i < line.Length; i++) {
                var c = line[i];
                if (c == '\\') { if (++i >= line.Length) throw Invalid("CSV 列表转义不完整。"); c = line[i] switch { 'n' => '\n', 'r' => '\r', '\\' => '\\', _ => throw Invalid("CSV 列表转义无效。") }; }
                result.Append(c);
            }
            return result.ToString();
        }).ToList();
    }
    private static bool Risky(string text) => (text.TrimStart() is { Length: > 0 } t && "=+-@\t\r\n".Contains(t[0])) || (text.Length > 0 && text[0] is '\t' or '\r' or '\n');
    private static string Unprotect(string text) => text.StartsWith("'") && (text[1..].StartsWith("'") || Risky(text[1..])) ? text[1..] : text;
    // Portable CSV v1: identical columns and list escaping to Android ArchiveCsv.
    // Old 26-column exports merged bilingual text and cannot resolve an embedded " / " unambiguously.
    public static string GenerateCsv(IEnumerable<WordItem> words)
    {
        var entries = words.Take(MaximumEntries + 1).ToList();
        GenerateJson(entries);
        var output = new StringBuilder();
        void Row(IEnumerable<string?> cells) => output.Append(string.Join(",", cells.Select(Quote))).Append("\r\n");
        Row(Headers);
        foreach (var w in entries) {
            var a = w.Archive; var ai = w.AiResult;
            Row([w.Word,w.Phonetic,w.Translation,w.Definition,w.Notes,a.SourceType,a.SourceTitle,a.SourceExcerpt,Pack(a.Tags),a.Uuid,
                Number(w.Stage),w.Status,w.CreatedAt,w.LearningStartDate,w.NextReviewDate,w.LastReviewedAt,Number(w.ReviewCount),Number(a.EncounterCount),Number(a.Revision),a.CreatedAtUtc,a.UpdatedAtUtc,a.LastEncounteredAtUtc,
                ai == null ? "否" : "是",Pack(ai?.Examples.Select(e=>e.English) ?? []),Pack(ai?.Examples.Select(e=>e.Chinese) ?? []),Pack(ai?.Synonyms ?? []),Pack(ai?.Antonyms ?? []),Pack(ai?.Phrases.Select(e=>e.English) ?? []),Pack(ai?.Phrases.Select(e=>e.Chinese) ?? [])]);
        }
        if (Encoding.UTF8.GetByteCount(output.ToString()) > MaximumJsonBytes) throw Invalid("CSV 超过 50 MB。");
        return output.ToString();
    }
    public static List<WordItem> ParseCsv(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumJsonBytes) throw Invalid("CSV 超过 50 MB。");
        var rows = ReadCsv(text.TrimStart('\uFEFF'));
        if (rows.Count == 0) throw Invalid("CSV 为空。");
        var legacy = rows[0].Contains("档案标识");
        string Alias(string n) => n.Trim() switch {
            "word"=>"单词", "translation" or "释义"=>"中文释义", "phonetic"=>"音标", "学习阶段"=>"阶段", "创建时间"=>"入库日期", "学习开始日期"=>"学习起始日期", "下次重逢日期"=>"下次复习日期", "档案标识"=>"UUID", "来源标题"=>"来源", "首次收藏UTC"=>"创建时间UTC", "最近遇见UTC"=>"最后遇见时间UTC", "修订版本"=>"版本", "AI同义词"=>"同义词", "AI反义词"=>"反义词", _=>n.Trim() };
        var names=rows[0].Select(Alias).ToArray();
        if (!names.Contains("单词") || names.Distinct().Count()!=names.Length || names.Any(n=>!Headers.Contains(n) && n is not ("AI例句" or "AI词组"))) throw Invalid("CSV 列名无效、重复或缺少单词列。");
        var result=new List<WordItem>();
        foreach(var values in rows.Skip(1).Where(r=>r.Any(c=>!string.IsNullOrWhiteSpace(c)))) {
            if(values.Count!=names.Length) throw Invalid("CSV 行列数不一致。");
            var row=names.Zip(values.Select(Unprotect)).ToDictionary(p=>p.First,p=>p.Second);
            string S(string name,string fallback="")=>row.GetValueOrDefault(name,fallback);
            int N(string name,int fallback)=>!row.ContainsKey(name)?fallback:int.TryParse(S(name),NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)?n:throw Invalid("CSV 整数字段无效："+name);
            List<string> List(string name)=>legacy?S(name).Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToList():Unpack(S(name));
            List<(string En,string Zh)> Pairs(string en,string zh) {
                var left=Unpack(S(en));var right=Unpack(S(zh));if(left.Count!=right.Count)throw Invalid("CSV 中英文数量不匹配。");return left.Zip(right).ToList();
            }
            List<(string En,string Zh)> OldPairs(string name)=>S(name).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>{var i=line.IndexOf(" / ",StringComparison.Ordinal);return i<0?(line,""):(line[..i],line[(i+3)..]);}).ToList();
            var status=S("状态","learning") switch {"已掌握"=>"mastered","学习中"=>"learning",var v=>v};
            var now=DateTime.UtcNow.ToString("O");var today=DateTime.Today.ToString("yyyy-MM-dd");
            var hasAi=S("包含AI扩展",new[]{"例句英文","同义词","反义词","常用词组","AI例句","AI词组"}.Any(n=>S(n).Length>0)?"是":"否");
            if(hasAi is not ("是" or "否"))throw Invalid("包含AI扩展应为是或否。");
            var w=new WordItem { Word=S("单词").Trim(),Phonetic=S("音标"),Translation=S("中文释义"),Definition=S("英文释义"),Notes=S("备注"),Status=status,Stage=N("阶段",status=="mastered"?5:0),CreatedAt=S("入库日期",today),LearningStartDate=S("学习起始日期",today),NextReviewDate=row.ContainsKey("下次复习日期")?(S("下次复习日期")==""?null:S("下次复习日期")):status=="mastered"?null:DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"),LastReviewedAt=S("上次复习时间")==""?null:S("上次复习时间"),ReviewCount=N("复习次数",0),
                Archive=new ArchiveMetadata {Uuid=S("UUID",Guid.NewGuid().ToString()),SourceType=S("来源类型"),SourceTitle=S("来源"),SourceExcerpt=S("来源原句"),Tags=List("标签").ToArray(),EncounterCount=N("遇见次数",1),Revision=N("版本",1),CreatedAtUtc=S("创建时间UTC",now),UpdatedAtUtc=S("更新时间UTC",now),LastEncounteredAtUtc=S("最后遇见时间UTC",now)} };
            if(hasAi=="是")w.AiResult=new LlmResult { Examples=(legacy?OldPairs("AI例句"):Pairs("例句英文","例句中文")).Select(p=>new ExampleItem(p.En,p.Zh)).ToList(),Synonyms=List("同义词"),Antonyms=List("反义词"),Phrases=(legacy?OldPairs("AI词组"):Pairs("常用词组","词组中文")).Select(p=>new PhraseItem(p.En,p.Zh)).ToList() };
            result.Add(w);
        }
        GenerateJson(result);
        return result;
    }
    private static List<List<string>> ReadCsv(string text)
    {
        var rows=new List<List<string>>();var row=new List<string>();var cell=new StringBuilder();bool quoted=false,ended=false;
        void Field(){row.Add(cell.ToString());cell.Clear();ended=false;if(row.Count>64)throw Invalid("CSV 列数过多。");}
        void Row(){Field();rows.Add(row);row=[];if(rows.Count>MaximumEntries+1)throw Invalid("CSV 词条过多。");}
        for(var i=0;i<text.Length;i++) {var c=text[i];
            if(quoted){if(c=='"'){if(i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else{quoted=false;ended=true;}}else cell.Append(c);}
            else switch(c){case '"':if(cell.Length>0||ended)throw Invalid("CSV 引号位置无效。");quoted=true;break;case ',':Field();break;case '\r':case '\n':if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;Row();break;default:if(ended)throw Invalid("CSV 引号后存在多余字符。");cell.Append(c);break;}
        }
        if(quoted)throw Invalid("CSV 引号未闭合。");if(cell.Length>0||row.Count>0||ended)Row();return rows;
    }
}
