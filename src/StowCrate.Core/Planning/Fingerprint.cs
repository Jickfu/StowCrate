using System.Security.Cryptography;
using System.Text;

namespace StowCrate.Core.Planning;

internal static class Fingerprint
{
    public static string Compute(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var canonical = string.Join('\n', values);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
