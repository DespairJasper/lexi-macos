using System;

namespace Lexi.Core;

public sealed class ArchiveMetadata
{
    public string Uuid { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string SourceTitle { get; set; } = "";
    public string SourceExcerpt { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
    public string UpdatedAtUtc { get; set; } = "";
    public string LastEncounteredAtUtc { get; set; } = "";
    public string[] Tags { get; set; } = Array.Empty<string>();
    public int EncounterCount { get; set; } = 1;
    public int Revision { get; set; } = 1;
}
