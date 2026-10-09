using System.Text.RegularExpressions;

namespace Lexi;

/// <summary>
/// 词身份解析：把界面上的各种入口（复习页、查词页单词卡、每日计划卡）统一归约到 <see cref="WordKey"/>。
/// 纯静态逻辑，不读写任何存储。
/// </summary>
/// <remarks>
/// <para>
/// <b>同词多入口的状态共享规则</b>：长期记忆层（FSRS 卡、交互轨迹、canonical 信号）一律按
/// <see cref="WordKey"/> 共享——同一个词无论从复习页、查词页单词卡还是计划卡进入，都落到同一张卡、
/// 同一条轨迹上；不因为入口不同而各记一份。
/// </para>
/// <para>
/// <b>各源原有进度互相隔离</b>：档案表的 <c>words.stage</c> / <c>words.status</c>、计划 JSON 的
/// <c>CompletedWordIds</c>、IELTS JSON 的 <c>Typed</c> / <c>Errors</c> 都由各自原有代码维护，
/// 本层**不读也不写**它们；本层只认 <see cref="WordKey"/>。
/// </para>
/// <para>
/// <b>不同 source 的同形词不合并</b>：档案里的 apple（<c>archive:&lt;uuid&gt;</c>）与 IELTS 目录里的
/// apple（<c>ielts:&lt;catalogId&gt;</c>）是两个身份，各自一张 FSRS 卡；只有两处都查不到时，
/// 才退化成按词形共享的 <c>form:&lt;formC&gt;</c>。
/// </para>
/// </remarks>
public static class WordKeyResolver
{
    /// <summary>连续空白（含 NBSP 等 Unicode 空白），用于 <see cref="FormC"/> 的内部空白折叠。</summary>
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>查词页的 pageKind 取值（与其它入口区分：查词页优先按 IELTS 目录解析）。</summary>
    public const string IeltsPageKind = "ielts";

    /// <summary>档案词身份：取 <c>word_archives.uuid</c>，**不用 words.id**（rowid 在恢复备份后会重排）。</summary>
    /// <exception cref="InvalidOperationException">uuid 为空——此时身份不稳定，宁可失败也不要生成会串卡的 <c>archive:</c> 键。</exception>
    public static WordKey FromArchive(WordItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return FromArchiveUuid(item.Archive.Uuid);
    }

    /// <summary>档案词身份（已知 uuid 时直接构造）。</summary>
    public static WordKey FromArchiveUuid(string uuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
        return WordKey.Archive(uuid);
    }

    /// <summary>IELTS 目录词身份：取目录条目的稳定 Id。</summary>
    public static WordKey FromIelts(LearningWord word)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentException.ThrowIfNullOrWhiteSpace(word.Id);
        return WordKey.Ielts(word.Id);
    }

    /// <summary>裸词形身份（档案与目录都查不到时的退化口径）。</summary>
    public static WordKey FromForm(string form) => WordKey.Form(FormC(form));

    /// <summary>
    /// 归一化词形：<c>Trim</c> + 把内部连续空白折叠成单个半角空格 + <c>ToLowerInvariant</c>。
    /// 例：<c>"  Ice  Cream  "</c> → <c>"ice cream"</c>。注意不做词形还原（running / runs 不合并）。
    /// </summary>
    public static string FormC(string form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return WhitespaceRun.Replace(form.Trim(), " ").ToLowerInvariant();
    }

    /// <summary>
    /// 按界面入口解析词身份。优先顺序：
    /// <list type="number">
    /// <item>查词页（<paramref name="pageKind"/> == "ielts"）先查 IELTS 目录，命中即用 <c>ielts:</c>；</item>
    /// <item>否则先查档案，命中用 <c>archive:</c>；</item>
    /// <item>再查 IELTS 目录，命中用 <c>ielts:</c>；</item>
    /// <item>都不中，退化为 <c>form:&lt;formC&gt;</c>。</item>
    /// </list>
    /// 注意第 1 步只影响优先级，不影响最终口径：查词页里遇到已在档案中的词，仍落到同一个 <c>archive:</c> 身份。
    /// </summary>
    public static WordKey Resolve(string form, string? pageKind,
        Func<string, WordItem?> findArchive, Func<string, LearningWord?> findIelts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(form);
        ArgumentNullException.ThrowIfNull(findArchive);
        ArgumentNullException.ThrowIfNull(findIelts);

        if (string.Equals(pageKind, IeltsPageKind, StringComparison.OrdinalIgnoreCase))
        {
            var catalogHit = findIelts(form);
            if (catalogHit is not null) return FromIelts(catalogHit);
        }

        var archiveHit = findArchive(form);
        if (archiveHit is not null) return FromArchive(archiveHit);

        var ieltsHit = findIelts(form);
        if (ieltsHit is not null) return FromIelts(ieltsHit);

        return FromForm(form);
    }

    /// <summary>
    /// 解析每日计划里的一个词。计划 JSON 只存引用（Archive 源存 <c>words.id</c> 的十进制字符串，
    /// Ielts 源存目录 Id），必须换成长期层口径：
    /// Archive → 用 <paramref name="word"/>.Id 反查 uuid 得 <c>archive:</c>；Ielts → <c>ielts:</c>。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Archive 源的计划词反查不到档案行（档案被删或计划陈旧）。本层不猜身份，由调用方决定降级策略
    /// （例如跳过该词、或整批延后）。
    /// </exception>
    public static WordKey ResolvePlanWord(DailyStudyPlan plan, DailyStudyPlanWord word,
        Func<string, WordItem?> findArchiveById)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(findArchiveById);
        ArgumentException.ThrowIfNullOrWhiteSpace(word.Id);

        if (plan.Source == DailyStudyPlanSource.Ielts) return WordKey.Ielts(word.Id);

        var item = findArchiveById(word.Id)
            ?? throw new InvalidOperationException(
                $"计划「{plan.Name}」中的词 {word.Word}（words.id={word.Id}）在档案中不存在，无法解析词身份。");
        return FromArchive(item);
    }
}
