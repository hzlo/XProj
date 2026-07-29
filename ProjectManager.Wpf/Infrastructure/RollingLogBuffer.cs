using System.Text;

namespace ProjectManager.Wpf.Infrastructure;

internal sealed class RollingLogBuffer
{
    private const int MaximumBoundarySearchCharacters = 4096;
    private readonly StringBuilder _text = new();
    private readonly int _maximumCharacters;
    private readonly int _retainedCharacters;

    public RollingLogBuffer(int maximumCharacters, int retainedCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedCharacters);
        if (retainedCharacters >= maximumCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedCharacters));
        }

        _maximumCharacters = maximumCharacters;
        _retainedCharacters = retainedCharacters;
    }

    public LogBufferChange Append(string text)
    {
        var previousLength = _text.Length;
        _text.Append(text);

        var trimmedCharacters = GetTrimmedCharacterCount();
        if (trimmedCharacters > 0)
        {
            _text.Remove(0, trimmedCharacters);
        }

        var charactersToRemove = Math.Min(previousLength, trimmedCharacters);
        var appendedTextOffset = Math.Min(text.Length, Math.Max(0, trimmedCharacters - previousLength));
        return new LogBufferChange(charactersToRemove, text[appendedTextOffset..]);
    }

    public string GetTailText(int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        if (_text.Length <= maximumCharacters)
        {
            return _text.ToString();
        }

        var startIndex = _text.Length - maximumCharacters;
        var boundarySearchCharacters = Math.Min(
            MaximumBoundarySearchCharacters,
            Math.Max(1, maximumCharacters / 4));
        var searchLimit = Math.Min(_text.Length, startIndex + boundarySearchCharacters);
        for (var index = startIndex; index < searchLimit; index++)
        {
            if (_text[index] == '\n' && _text.Length - index - 1 >= maximumCharacters / 2)
            {
                startIndex = index + 1;
                break;
            }
        }

        return _text.ToString(startIndex, _text.Length - startIndex);
    }

    public string GetTailLines(int maximumLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);
        var remainingLineBreaks = maximumLines;
        var searchIndex = _text.Length - 1;
        if (searchIndex >= 0 && _text[searchIndex] == '\n')
        {
            searchIndex--;
        }

        for (; searchIndex >= 0; searchIndex--)
        {
            if (_text[searchIndex] != '\n' || --remainingLineBreaks > 0)
            {
                continue;
            }

            var startIndex = searchIndex + 1;
            return _text.ToString(startIndex, _text.Length - startIndex);
        }

        return _text.ToString();
    }

    public override string ToString() => _text.ToString();

    private int GetTrimmedCharacterCount()
    {
        if (_text.Length <= _maximumCharacters)
        {
            return 0;
        }

        var desiredTrimCount = _text.Length - _retainedCharacters;
        var boundarySearchCharacters = Math.Min(
            MaximumBoundarySearchCharacters,
            Math.Max(1, _retainedCharacters / 4));
        var searchLimit = Math.Min(_text.Length, desiredTrimCount + boundarySearchCharacters);
        for (var index = desiredTrimCount; index < searchLimit; index++)
        {
            if (_text[index] == '\n')
            {
                return index + 1;
            }
        }

        return desiredTrimCount;
    }
}

internal readonly record struct LogBufferChange(int CharactersToRemove, string TextToAppend);
