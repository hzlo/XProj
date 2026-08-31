namespace XProj.Plugin.Notes;

public sealed class NoteDocument
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public DateTime LastWriteTimeUtc { get; set; }
}
