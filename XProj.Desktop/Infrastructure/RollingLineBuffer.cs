using System.Text;

namespace ProjectManager.Wpf.Infrastructure;

internal sealed class RollingLineBuffer
{
    private readonly LinkedList<int> _completeLineLengths = new();
    private readonly int _maximumCharacters;
    private readonly int _maximumLines;
    private readonly int _retainedCharacters;
    private readonly StringBuilder _text = new();
    private int _currentLineLength;

    public RollingLineBuffer(int maximumLines, int maximumCharacters, int retainedCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedCharacters);
        if (retainedCharacters >= maximumCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedCharacters));
        }

        _maximumLines = maximumLines;
        _maximumCharacters = maximumCharacters;
        _retainedCharacters = retainedCharacters;
    }

    public int LineCount => _completeLineLengths.Count + (_currentLineLength > 0 ? 1 : 0);

    public LogBufferChange Append(string text)
    {
        var previousLength = _text.Length;
        _text.Append(text);
        foreach (var character in text)
        {
            _currentLineLength++;
            if (character != '\n')
            {
                continue;
            }

            _completeLineLengths.AddLast(_currentLineLength);
            _currentLineLength = 0;
        }

        var trimmedCharacters = TrimExcessLines();
        if (trimmedCharacters > 0)
        {
            _text.Remove(0, trimmedCharacters);
        }

        if (_text.Length > _maximumCharacters)
        {
            var characterTrimCount = _text.Length - _retainedCharacters;
            ConsumePrefixCharacters(characterTrimCount);
            _text.Remove(0, characterTrimCount);
            trimmedCharacters += characterTrimCount;
        }

        var charactersToRemove = Math.Min(previousLength, trimmedCharacters);
        var appendedTextOffset = Math.Min(text.Length, Math.Max(0, trimmedCharacters - previousLength));
        return new LogBufferChange(charactersToRemove, text[appendedTextOffset..]);
    }

    public override string ToString() => _text.ToString();

    private int TrimExcessLines()
    {
        var trimmedCharacters = 0;
        while (LineCount > _maximumLines && _completeLineLengths.First is not null)
        {
            trimmedCharacters += _completeLineLengths.First.Value;
            _completeLineLengths.RemoveFirst();
        }

        return trimmedCharacters;
    }

    private void ConsumePrefixCharacters(int characterCount)
    {
        var remainingCharacters = characterCount;
        while (remainingCharacters > 0 && _completeLineLengths.First is not null)
        {
            var firstLineLength = _completeLineLengths.First.Value;
            if (remainingCharacters < firstLineLength)
            {
                _completeLineLengths.First.Value -= remainingCharacters;
                return;
            }

            remainingCharacters -= firstLineLength;
            _completeLineLengths.RemoveFirst();
        }

        if (remainingCharacters > 0)
        {
            _currentLineLength = Math.Max(0, _currentLineLength - remainingCharacters);
        }
    }
}
