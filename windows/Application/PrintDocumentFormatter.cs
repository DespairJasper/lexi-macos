using System.Text;

namespace Lexi;

/// <summary>Pure A4 HTML formatting. Hosts decide how to save and open the document.</summary>
public static class PrintDocumentFormatter
{
    public static string EscapeHtml(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    public static string GenerateHtml(IEnumerable<WordItem> words)
    {
        var wordList = words.ToList();
        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"zh-CN\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <title>Lexi 生词复习清单</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    @page { size: A4; margin: 15mm; }");
        sb.AppendLine("    body { font: 12px/1.6 Arial, \"Microsoft YaHei\", sans-serif; color: #182b29; margin: 0; }");
        sb.AppendLine("    h1 { font-size: 24px; margin: 0 0 8px; color: #243f39; }");
        sb.AppendLine("    p.meta { color: #546560; font-size: 11px; margin: 0 0 16px; }");
        sb.AppendLine("    table { border-collapse: collapse; width: 100%; table-layout: fixed; }");
        sb.AppendLine("    th, td { border: 1px solid #bbc9c5; padding: 8px; overflow-wrap: anywhere; text-align: left; }");
        sb.AppendLine("    th { background: #eef3ef; font-weight: 600; font-size: 11px; }");
        sb.AppendLine("    tr { break-inside: avoid; }");
        sb.AppendLine("    thead { display: table-header-group; }");
        sb.AppendLine("    .word { font-weight: bold; font-size: 14px; font-family: Georgia, serif; color: #243f39; }");
        sb.AppendLine("    .sound { font-size: 11px; color: #52605c; margin-top: 3px; }");
        sb.AppendLine("    .check { text-align: center; width: 6%; font-size: 14px; color: #2a6855; }");
        sb.AppendLine("    .meaning { white-space: pre-wrap; font-size: 12px; }");
        sb.AppendLine("    .footer { margin-top: 18px; color: #6e7e78; font-size: 10px; }");
        sb.AppendLine("    @media print { button { display: none; } }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>Lexi / 我的复习清单</h1>");
        sb.AppendLine($"  <p class=\"meta\">{todayStr} · 共 {wordList.Count} 词 · 学习日期：____________　姓名：____________</p>");
        sb.AppendLine("  <table>");
        sb.AppendLine("    <thead>");
        sb.AppendLine("      <tr>");
        sb.AppendLine("        <th style=\"width: 24%;\">单词 / 音标</th>");
        sb.AppendLine("        <th>释义</th>");
        sb.AppendLine("        <th class=\"check\">第1天</th>");
        sb.AppendLine("        <th class=\"check\">第2天</th>");
        sb.AppendLine("        <th class=\"check\">第4天</th>");
        sb.AppendLine("        <th class=\"check\">第7天</th>");
        sb.AppendLine("        <th class=\"check\">第15天</th>");
        sb.AppendLine("      </tr>");
        sb.AppendLine("    </thead>");
        sb.AppendLine("    <tbody>");

        foreach (var w in wordList)
        {
            sb.AppendLine("      <tr>");
            sb.AppendLine("        <td>");
            sb.AppendLine($"          <div class=\"word\">{EscapeHtml(w.Word)}</div>");
            if (!string.IsNullOrEmpty(w.Phonetic))
            {
                sb.AppendLine($"          <div class=\"sound\">{EscapeHtml(w.Phonetic)}</div>");
            }
            sb.AppendLine("        </td>");
            sb.AppendLine($"        <td class=\"meaning\">{EscapeHtml(w.Translation)}</td>");
            sb.AppendLine("        <td class=\"check\">&#9633;</td>");
            sb.AppendLine("        <td class=\"check\">&#9633;</td>");
            sb.AppendLine("        <td class=\"check\">&#9633;</td>");
            sb.AppendLine("        <td class=\"check\">&#9633;</td>");
            sb.AppendLine("        <td class=\"check\">&#9633;</td>");
            sb.AppendLine("      </tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");
        sb.AppendLine("  <p class=\"footer\">复习后在相应节点打卡；可在浏览器中按 Ctrl+P 打印或另存为 PDF。</p>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}
