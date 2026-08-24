namespace StowCrate.Core.Paths;

public readonly record struct LogicalPath
{
    private readonly string? _value;

    public LogicalPath(string value)
    {
        _value = PathSyntax.NormalizeRelative(value, nameof(value));
    }

    public static LogicalPath Root => new(string.Empty);

    public string Value => _value ?? string.Empty;

    public bool IsRoot => Value.Length == 0;

    public string Name => IsRoot ? string.Empty : Value[(Value.LastIndexOf('/') + 1)..];

    public LogicalPath Parent
    {
        get
        {
            if (IsRoot)
            {
                return Root;
            }

            var separatorIndex = Value.LastIndexOf('/');
            return separatorIndex < 0 ? Root : new LogicalPath(Value[..separatorIndex]);
        }
    }

    public bool IsDescendantOf(LogicalPath ancestor)
    {
        return this != ancestor && PathSyntax.IsSameOrDescendant(Value, ancestor.Value);
    }

    public bool IsSameOrDescendantOf(LogicalPath ancestor)
    {
        return PathSyntax.IsSameOrDescendant(Value, ancestor.Value);
    }

    public RelativePath RelativeTo(LogicalPath ancestor)
    {
        if (!IsSameOrDescendantOf(ancestor))
        {
            throw new ArgumentException($"'{Value}' 不在 '{ancestor.Value}' 下。", nameof(ancestor));
        }

        if (this == ancestor)
        {
            return RelativePath.Root;
        }

        var offset = ancestor.IsRoot ? 0 : ancestor.Value.Length + 1;
        return new RelativePath(Value[offset..]);
    }

    public LogicalPath Combine(RelativePath relativePath)
    {
        if (relativePath.IsRoot)
        {
            return this;
        }

        return IsRoot
            ? new LogicalPath(relativePath.Value)
            : new LogicalPath($"{Value}/{relativePath.Value}");
    }

    public override string ToString()
    {
        return Value;
    }
}
