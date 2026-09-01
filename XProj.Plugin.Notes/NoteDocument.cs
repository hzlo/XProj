using System.IO;

namespace XProj.Plugin.Notes;

public sealed class NoteDocument
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public DateTime LastWriteTimeUtc { get; set; }

    public string DisplayName => Path.GetFileNameWithoutExtension(FileName);

    public string LastEditedText => LastWriteTimeUtc.ToLocalTime().ToString("MM月dd日 HH:mm");
}
