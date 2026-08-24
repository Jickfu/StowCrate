using System.Text;
using System.Text.RegularExpressions;

namespace StowCrate.Core.Paths;

internal static partial class PathSyntax
{
    public static string NormalizeRelative(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (value.Contains('\0'))
        {
            throw new ArgumentException("逻辑路径不能包含 NUL。", parameterName);
        }

        if (LooksAbsolute(value))
        {
            throw new ArgumentException("逻辑路径必须是相对路径。", parameterName);
        }

        var normalized = value.Replace('\\', '/').Normalize(NormalizationForm.FormC);

        if (normalized.EndsWith('/'))
        {
            normalized = normalized.TrimEnd('/');
        }

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("逻辑路径不能包含空 segment、'.' 或 '..'。", parameterName);
        }

        return string.Join('/', segments);
    }

    public static bool IsSameOrDescendant(string value, string ancestor)
    {
        if (ancestor.Length == 0)
        {
            return true;
        }

        return value.Equals(ancestor, StringComparison.Ordinal)
            || value.StartsWith($"{ancestor}/", StringComparison.Ordinal);
    }

    private static bool LooksAbsolute(string value)
    {
        return value.StartsWith('/')
            || value.StartsWith('\\')
            || WindowsDrivePrefixRegex().IsMatch(value);
    }

    [GeneratedRegex("^[A-Za-z]:[\\\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePrefixRegex();
}
