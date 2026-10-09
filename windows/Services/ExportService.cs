using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Lexi;

public static class ExportService
{
    public static string EscapeHtml(string? value) => PrintDocumentFormatter.EscapeHtml(value);
    public static string GenerateHtml(IEnumerable<WordItem> words) => PrintDocumentFormatter.GenerateHtml(words);

    public static string SaveAndOpen(string htmlContent, string? customPath = null, IExternalDocumentLauncher? launcher = null)
    {
        string targetFile;
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            targetFile = customPath;
        }
        else
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var exportFolder = Path.Combine(downloads, "LexiExports");
            Directory.CreateDirectory(exportFolder);
            var filename = $"Lexi-{DateTime.Now:yyyyMMdd-HHmmss-fff}.html";
            targetFile = Path.Combine(exportFolder, filename);
        }

        var dir = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(targetFile, htmlContent, Encoding.UTF8);

        (launcher ?? new WindowsDocumentLauncher()).Open(targetFile);

        return targetFile;
    }
}
