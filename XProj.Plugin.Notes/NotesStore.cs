using System.IO;
using System.Text;

namespace XProj.Plugin.Notes;

internal sealed class NotesStore
{
    private readonly string _notesDirectory;

    public NotesStore(string dataDirectory)
    {
        _notesDirectory = Path.Combine(dataDirectory, "notes");
        Directory.CreateDirectory(_notesDirectory);
    }

    public IReadOnlyList<NoteDocument> ListDocuments()
    {
        return Directory
            .EnumerateFiles(_notesDirectory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(file => new NoteDocument
            {
                FileName = file.Name,
                FullPath = file.FullName,
                LastWriteTimeUtc = file.LastWriteTimeUtc
            })
            .ToArray();
    }

    public async Task<string> ReadAsync(NoteDocument document)
    {
        return await File.ReadAllTextAsync(document.FullPath, Encoding.UTF8);
    }

    public async Task SaveAsync(NoteDocument document, string content)
    {
        var temporaryPath = document.FullPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, document.FullPath, overwrite: true);
        document.LastWriteTimeUtc = File.GetLastWriteTimeUtc(document.FullPath);
    }

    public NoteDocument CreateDocument(string baseName = "新笔记")
    {
        var index = 0;
        string fileName;
        do
        {
            var suffix = index == 0 ? string.Empty : $" {index}";
            fileName = $"{baseName}{suffix}.md";
            index++;
        }
        while (File.Exists(Path.Combine(_notesDirectory, fileName)));

        var fullPath = Path.Combine(_notesDirectory, fileName);
        File.WriteAllText(fullPath, $"# {baseName}{Environment.NewLine}{Environment.NewLine}", new UTF8Encoding(false));
        return new NoteDocument
        {
            FileName = fileName,
            FullPath = fullPath,
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath)
        };
    }

    public NoteDocument Rename(NoteDocument document, string displayName)
    {
        var normalizedName = displayName.Trim();
        if (normalizedName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            normalizedName = normalizedName[..^3].TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName is "." or "..")
        {
            throw new InvalidOperationException("笔记名称不能为空。");
        }

        if (normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("笔记名称不能包含 \\/ : * ? \" < > | 等非法字符。");
        }

        var fileName = normalizedName + ".md";
        var fullPath = Path.Combine(_notesDirectory, fileName);
        if (string.Equals(document.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return document;
        }

        if (File.Exists(fullPath))
        {
            throw new InvalidOperationException("已经存在同名笔记。");
        }

        File.Move(document.FullPath, fullPath);
        return new NoteDocument
        {
            FileName = fileName,
            FullPath = fullPath,
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath)
        };
    }

    public void Delete(NoteDocument document)
    {
        if (File.Exists(document.FullPath))
        {
            File.Delete(document.FullPath);
        }
    }
}
