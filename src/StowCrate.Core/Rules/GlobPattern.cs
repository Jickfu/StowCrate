using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using StowCrate.Core.Paths;

namespace StowCrate.Core.Rules;

internal sealed class GlobPattern
{
    private static readonly SearchValues<char> EscapableCharacters = SearchValues.Create("!#@*?[]\\ \t");
    private readonly Regex _sensitiveRegex;
    private readonly Regex _insensitiveRegex;

    public GlobPattern(string pattern)
    {
        Pattern = TrimPatternWhitespace(pattern).Normalize(NormalizationForm.FormC);
        if (Pattern.Length == 0)
        {
            throw new ArgumentException("Pattern 不能为空。", nameof(pattern));
        }

        var body = Pattern;
        IsRootAnchored = body.StartsWith('/');
        if (IsRootAnchored)
        {
            body = body[1..];
        }

        DirectoryOnly = body.EndsWith('/');
        if (DirectoryOnly)
        {
            body = body[..^1];
        }

        if (body.Length == 0)
        {
            throw new ArgumentException("Pattern 必须包含路径内容。", nameof(pattern));
        }

        ValidateSegments(body, pattern);
        var hasPathSeparator = ContainsUnescaped(body, '/');
        var regexBody = CompileBody(body, pattern);
        var prefix = IsRootAnchored || hasPathSeparator ? "^" : "(?:^|.*/)";
        var regexPattern = $"{prefix}{regexBody}$";

        try
        {
            _sensitiveRegex = CreateRegex(regexPattern, ignoreCase: false);
            _insensitiveRegex = CreateRegex(regexPattern, ignoreCase: true);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"无效 pattern：{pattern}", nameof(pattern), exception);
        }
    }

    public string Pattern { get; }

    public bool IsRootAnchored { get; }

    public bool DirectoryOnly { get; }

    public bool IsMatch(RelativePath path, SourceEntryKind entryKind, CaseSensitivity caseSensitivity)
    {
        if (caseSensitivity is CaseSensitivity.Auto)
        {
            throw new ArgumentException("匹配前必须解析 auto case sensitivity。", nameof(caseSensitivity));
        }

        if (path.IsRoot)
        {
            return false;
        }

        var regex = caseSensitivity is CaseSensitivity.Sensitive ? _sensitiveRegex : _insensitiveRegex;
        if (regex.IsMatch(path.Value) && (!DirectoryOnly || entryKind is SourceEntryKind.Directory))
        {
            return true;
        }

        return path.GetAncestors().Any(ancestor => regex.IsMatch(ancestor.Value));
    }

    public static string TrimPatternWhitespace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var start = 0;
        while (start < value.Length && IsHorizontalWhitespace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsHorizontalWhitespace(value[end - 1]))
        {
            var backslashCount = 0;
            for (var index = end - 2; index >= start && value[index] == '\\'; index--)
            {
                backslashCount++;
            }

            if (backslashCount % 2 == 1)
            {
                break;
            }

            end--;
        }

        return value[start..end];
    }

    private static Regex CreateRegex(string pattern, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, options, TimeSpan.FromSeconds(1));
    }

    private static string CompileBody(string body, string originalPattern)
    {
        var result = new StringBuilder();

        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            switch (character)
            {
                case '\\':
                    index++;
                    if (index >= body.Length || !EscapableCharacters.Contains(body[index]))
                    {
                        throw new ArgumentException($"无效 escape：{originalPattern}", nameof(originalPattern));
                    }

                    result.Append(Regex.Escape(body[index].ToString()));
                    break;

                case '*':
                    if (index + 1 < body.Length && body[index + 1] == '*')
                    {
                        index++;
                        if (index + 1 < body.Length && body[index + 1] == '/')
                        {
                            index++;
                            result.Append("(?:.*/)?");
                        }
                        else
                        {
                            result.Append(".*");
                        }
                    }
                    else
                    {
                        result.Append("[^/]*");
                    }

                    break;

                case '?':
                    result.Append("[^/]");
                    break;

                case '[':
                    index = CompileCharacterClass(body, index, result, originalPattern);
                    break;

                case '/':
                    result.Append('/');
                    break;

                default:
                    result.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return result.ToString();
    }

    private static int CompileCharacterClass(
        string body,
        int openingIndex,
        StringBuilder result,
        string originalPattern)
    {
        var closingIndex = FindClosingBracket(body, openingIndex + 1);
        if (closingIndex < 0 || closingIndex == openingIndex + 1)
        {
            throw new ArgumentException($"未闭合或为空的 character class：{originalPattern}", nameof(originalPattern));
        }

        var index = openingIndex + 1;
        result.Append('[');
        if (body[index] == '!')
        {
            result.Append('^');
            index++;
        }

        if (index >= closingIndex)
        {
            throw new ArgumentException($"无效 character class：{originalPattern}", nameof(originalPattern));
        }

        for (; index < closingIndex; index++)
        {
            var character = body[index];
            if (character == '\\')
            {
                index++;
                if (index >= closingIndex || !EscapableCharacters.Contains(body[index]))
                {
                    throw new ArgumentException($"无效 character class escape：{originalPattern}", nameof(originalPattern));
                }

                AppendCharacterClassLiteral(result, body[index]);
            }
            else
            {
                AppendCharacterClassLiteral(result, character, preserveRangeMarker: true);
            }
        }

        result.Append(']');
        return closingIndex;
    }

    private static void AppendCharacterClassLiteral(
        StringBuilder result,
        char character,
        bool preserveRangeMarker = false)
    {
        if (character is '\\' or ']' or '^' || (character == '-' && !preserveRangeMarker))
        {
            result.Append('\\');
        }

        result.Append(character);
    }

    private static int FindClosingBracket(string body, int startIndex)
    {
        var escaped = false;
        for (var index = startIndex; index < body.Length; index++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (body[index] == '\\')
            {
                escaped = true;
                continue;
            }

            if (body[index] == ']')
            {
                return index;
            }
        }

        return -1;
    }

    private static void ValidateSegments(string body, string originalPattern)
    {
        var segment = new StringBuilder();

        for (var index = 0; index <= body.Length; index++)
        {
            if (index == body.Length || body[index] == '/')
            {
                if (segment.Length == 0 || segment.ToString() is "." or "..")
                {
                    throw new ArgumentException($"Pattern 包含非法路径 segment：{originalPattern}", nameof(originalPattern));
                }

                segment.Clear();
                continue;
            }

            if (body[index] == '\\')
            {
                index++;
                if (index >= body.Length || !EscapableCharacters.Contains(body[index]))
                {
                    throw new ArgumentException($"无效 escape：{originalPattern}", nameof(originalPattern));
                }
            }

            segment.Append(body[index]);
        }
    }

    private static bool ContainsUnescaped(string value, char target)
    {
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == target)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHorizontalWhitespace(char character)
    {
        return character is ' ' or '\t';
    }
}
