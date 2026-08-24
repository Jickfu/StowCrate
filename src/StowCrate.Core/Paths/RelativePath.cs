namespace StowCrate.Core.Paths;

public readonly record struct RelativePath
{
    private readonly string? _value;

    public RelativePath(string value)
    {
        _value = PathSyntax.NormalizeRelative(value, nameof(value));
    }

    public static RelativePath Root => new(string.Empty);

    public string Value => _value ?? string.Empty;

    public bool IsRoot => Value.Length == 0;

    public string Name => IsRoot ? string.Empty : Value[(Value.LastIndexOf('/') + 1)..];

    public IEnumerable<RelativePath> GetAncestors()
    {
        var current = Value;

        while (current.Length > 0)
        {
            var separatorIndex = current.LastIndexOf('/');
            if (separatorIndex < 0)
            {
                yield break;
            }

            current = current[..separatorIndex];
            if (current.Length > 0)
            {
                yield return new RelativePath(current);
            }
        }
    }

    public override string ToString()
    {
        return Value;
    }
}
