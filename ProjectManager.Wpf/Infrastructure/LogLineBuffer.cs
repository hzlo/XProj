namespace ProjectManager.Wpf.Infrastructure;

internal sealed class LogLineBuffer
{
    private readonly List<string> _lines = new();
    private readonly int _maximumLines;

    public LogLineBuffer(int maximumLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);
        _maximumLines = maximumLines;
    }

    public int LineCount => _lines.Count;

    public LogLinesChange Append(string text)
    {
        if (text.Length == 0)
        {
            return new LogLinesChange(0, Array.Empty<string>());
        }

        var addedLines = EnumerateCompleteLines(text.ReplaceLineEndings("\n")).ToArray();
        if (addedLines.Length == 0)
        {
            return new LogLinesChange(0, Array.Empty<string>());
        }

        _lines.AddRange(addedLines);
        var removedLineCount = TrimExcessLines();
        return new LogLinesChange(removedLineCount, addedLines);
    }

    public void Clear() => _lines.Clear();

    public IReadOnlyList<string> Snapshot() => _lines.ToArray();

    private static IEnumerable<string> EnumerateCompleteLines(string normalized)
    {
        var start = 0;
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index] != '\n')
            {
                continue;
            }

            yield return normalized[start..index];
            start = index + 1;
        }

        if (start < normalized.Length)
        {
            yield return normalized[start..];
        }
    }

    private int TrimExcessLines()
    {
        if (_lines.Count <= _maximumLines)
        {
            return 0;
        }

        var removedLineCount = _lines.Count - _maximumLines;
        _lines.RemoveRange(0, removedLineCount);
        return removedLineCount;
    }
}

internal sealed record LogLinesChange(int RemovedLineCount, IReadOnlyList<string> AddedLines);
