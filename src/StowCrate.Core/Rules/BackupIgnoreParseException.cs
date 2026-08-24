namespace StowCrate.Core.Rules;

public sealed class BackupIgnoreParseException : FormatException
{
    public BackupIgnoreParseException(int lineNumber, string message)
        : base($"第 {lineNumber} 行：{message}")
    {
        LineNumber = lineNumber;
    }

    public int LineNumber { get; }
}
