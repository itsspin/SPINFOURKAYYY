using System.Text;
using SpinFourKay.Core.IO;

namespace SpinFourKay.Core.Configuration;

/// <summary>
/// A formatting-preserving INI editor. Existing comments, ordering, key casing,
/// blank lines, and per-line newline sequences are retained.
/// </summary>
public sealed class IniDocument
{
    private readonly List<IniLine> _lines;
    private readonly Encoding _encoding;
    private readonly string _defaultNewLine;

    private IniDocument(List<IniLine> lines, Encoding encoding, string defaultNewLine)
    {
        _lines = lines;
        _encoding = encoding;
        _defaultNewLine = defaultNewLine;
    }

    public static IniDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ParseCore(text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static async Task<IniDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        (Encoding encoding, int preambleLength) = DetectEncoding(bytes);
        string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return ParseCore(text, encoding);
    }

    public string? GetValue(string section, string key)
    {
        ValidateName(section, nameof(section), allowEmpty: true);
        ValidateName(key, nameof(key), allowEmpty: false);

        string currentSection = string.Empty;
        string? result = null;

        foreach (IniLine line in _lines)
        {
            if (TryReadSection(line.Content, out string? parsedSection))
            {
                currentSection = parsedSection;
                continue;
            }

            if (SectionEquals(currentSection, section)
                && TryReadKey(line.Content, out KeyParts parts)
                && KeyEquals(parts.Key, key))
            {
                result = parts.Value;
            }
        }

        return result;
    }

    public void SetValue(string section, string key, string value)
    {
        ValidateName(section, nameof(section), allowEmpty: true);
        ValidateName(key, nameof(key), allowEmpty: false);
        ArgumentNullException.ThrowIfNull(value);

        if (value.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("INI values cannot contain a newline.", nameof(value));
        }

        string currentSection = string.Empty;
        bool updated = false;
        int insertionIndex = section.Length == 0 ? 0 : -1;

        for (int index = 0; index < _lines.Count; index++)
        {
            IniLine line = _lines[index];
            if (TryReadSection(line.Content, out string? parsedSection))
            {
                if (SectionEquals(currentSection, section))
                {
                    insertionIndex = index;
                }

                currentSection = parsedSection;
                if (SectionEquals(currentSection, section))
                {
                    insertionIndex = index + 1;
                }

                continue;
            }

            if (!SectionEquals(currentSection, section))
            {
                continue;
            }

            insertionIndex = index + 1;
            if (TryReadKey(line.Content, out KeyParts parts)
                && KeyEquals(parts.Key, key))
            {
                _lines[index] = line with { Content = parts.WithValue(value) };
                updated = true;
            }
        }

        if (updated)
        {
            return;
        }

        if (insertionIndex >= 0)
        {
            InsertLine(insertionIndex, $"{key}={value}");
            return;
        }

        AppendSection(section, key, value);
    }

    public int RemoveKey(string section, string key)
    {
        ValidateName(section, nameof(section), allowEmpty: true);
        ValidateName(key, nameof(key), allowEmpty: false);

        string currentSection = string.Empty;
        int removed = 0;

        for (int index = 0; index < _lines.Count;)
        {
            IniLine line = _lines[index];
            if (TryReadSection(line.Content, out string? parsedSection))
            {
                currentSection = parsedSection;
                index++;
                continue;
            }

            if (SectionEquals(currentSection, section)
                && TryReadKey(line.Content, out KeyParts parts)
                && KeyEquals(parts.Key, key))
            {
                _lines.RemoveAt(index);
                removed++;
                continue;
            }

            index++;
        }

        return removed;
    }

    public string Serialize()
    {
        StringBuilder builder = new();
        foreach (IniLine line in _lines)
        {
            builder.Append(line.Content);
            builder.Append(line.NewLine);
        }

        return builder.ToString();
    }

    public Task SaveAtomicAsync(string path, CancellationToken cancellationToken = default)
    {
        return AtomicFile.WriteAllTextAsync(path, Serialize(), _encoding, cancellationToken);
    }

    private static IniDocument ParseCore(string text, Encoding encoding)
    {
        List<IniLine> lines = [];
        Dictionary<string, int> newlineCounts = new(StringComparer.Ordinal);

        int position = 0;
        while (position < text.Length)
        {
            int lineEnd = position;
            while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n'))
            {
                lineEnd++;
            }

            int newlineEnd = lineEnd;
            if (newlineEnd < text.Length && text[newlineEnd] == '\r')
            {
                newlineEnd++;
            }

            if (newlineEnd < text.Length && text[newlineEnd] == '\n')
            {
                newlineEnd++;
            }

            string content = text[position..lineEnd];
            string newline = text[lineEnd..newlineEnd];
            lines.Add(new IniLine(content, newline));

            if (newline.Length > 0)
            {
                newlineCounts[newline] = newlineCounts.GetValueOrDefault(newline) + 1;
            }

            position = newlineEnd;
        }

        if (text.Length == 0)
        {
            lines.Clear();
        }

        string defaultNewline = newlineCounts.Count == 0
            ? Environment.NewLine
            : newlineCounts.MaxBy(pair => pair.Value).Key;

        return new IniDocument(lines, encoding, defaultNewline);
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        }

        if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF))
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        }

        if (StartsWith(bytes, 0xFF, 0xFE))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (StartsWith(bytes, 0xFE, 0xFF))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        try
        {
            Encoding strictUtf8 = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
            _ = strictUtf8.GetString(bytes);
            return (strictUtf8, 0);
        }
        catch (DecoderFallbackException)
        {
            // ISO-8859-1 is a byte-for-byte fallback. This preserves legacy ANSI
            // bytes on unchanged lines while all values written by this tool are ASCII.
            return (Encoding.Latin1, 0);
        }
    }

    private static bool StartsWith(ReadOnlySpan<byte> bytes, params ReadOnlySpan<byte> prefix)
    {
        return bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);
    }

    private void InsertLine(int index, string content)
    {
        if (index > 0 && _lines[index - 1].NewLine.Length == 0)
        {
            _lines[index - 1] = _lines[index - 1] with { NewLine = _defaultNewLine };
        }

        string newline = index < _lines.Count
            ? _defaultNewLine
            : string.Empty;
        _lines.Insert(index, new IniLine(content, newline));
    }

    private void AppendSection(string section, string key, string value)
    {
        if (_lines.Count > 0)
        {
            int last = _lines.Count - 1;
            if (_lines[last].NewLine.Length == 0)
            {
                _lines[last] = _lines[last] with { NewLine = _defaultNewLine };
            }

            if (_lines[last].Content.Length > 0)
            {
                _lines.Add(new IniLine(string.Empty, _defaultNewLine));
            }
        }

        if (section.Length > 0)
        {
            _lines.Add(new IniLine($"[{section}]", _defaultNewLine));
        }

        _lines.Add(new IniLine($"{key}={value}", string.Empty));
    }

    private static bool TryReadSection(string content, out string section)
    {
        string trimmed = content.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[')
        {
            int closingBracket = trimmed.IndexOf(']');
            if (closingBracket > 1)
            {
                string suffix = trimmed[(closingBracket + 1)..].TrimStart();
                if (suffix.Length == 0 || suffix[0] is ';' or '#')
                {
                    section = trimmed[1..closingBracket].Trim();
                    return section.Length > 0;
                }
            }
        }

        section = string.Empty;
        return false;
    }

    private static bool TryReadKey(string content, out KeyParts parts)
    {
        ReadOnlySpan<char> span = content.AsSpan();
        int first = 0;
        while (first < span.Length && char.IsWhiteSpace(span[first]))
        {
            first++;
        }

        if (first >= span.Length || span[first] is ';' or '#')
        {
            parts = default;
            return false;
        }

        int equals = content.IndexOf('=', first);
        if (equals <= first)
        {
            parts = default;
            return false;
        }

        string key = content[first..equals].Trim();
        if (key.Length == 0)
        {
            parts = default;
            return false;
        }

        string tail = content[(equals + 1)..];
        int leadingWhitespaceLength = 0;
        while (leadingWhitespaceLength < tail.Length
            && char.IsWhiteSpace(tail[leadingWhitespaceLength]))
        {
            leadingWhitespaceLength++;
        }

        int commentIndex = FindInlineComment(tail, leadingWhitespaceLength);
        int rawValueEnd = commentIndex >= 0 ? commentIndex : tail.Length;
        int valueEnd = rawValueEnd;
        while (valueEnd > leadingWhitespaceLength && char.IsWhiteSpace(tail[valueEnd - 1]))
        {
            valueEnd--;
        }

        string value = tail[leadingWhitespaceLength..valueEnd];
        string suffix = tail[valueEnd..];
        parts = new KeyParts(
            key,
            value,
            content[..(equals + 1)],
            tail[..leadingWhitespaceLength],
            suffix);
        return true;
    }

    private static int FindInlineComment(string tail, int start)
    {
        for (int index = start; index < tail.Length; index++)
        {
            if (tail[index] is ';' or '#'
                && (index == start || char.IsWhiteSpace(tail[index - 1])))
            {
                return index;
            }
        }

        return -1;
    }

    private static void ValidateName(string value, string parameterName, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The name cannot be empty.", parameterName);
        }

        if (value.IndexOfAny(['\r', '\n', '[', ']', '=']) >= 0)
        {
            throw new ArgumentException("The name contains an invalid INI character.", parameterName);
        }
    }

    private static bool SectionEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool KeyEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private readonly record struct IniLine(string Content, string NewLine);

    private readonly record struct KeyParts(
        string Key,
        string Value,
        string Prefix,
        string ValueLeadingWhitespace,
        string Suffix)
    {
        public string WithValue(string value) => $"{Prefix}{ValueLeadingWhitespace}{value}{Suffix}";
    }
}
