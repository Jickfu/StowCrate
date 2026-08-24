namespace StowCrate.Core.Rules;

public enum RuleAction
{
    Include,
    Exclude,
}

public enum RuleMode
{
    Exclude,
    IncludeOnly,
}

public enum RuleSource
{
    UiManaged,
    FileManaged,
}

public enum CaseSensitivity
{
    Auto,
    Sensitive,
    Insensitive,
}

public enum SourceEntryKind
{
    File,
    Directory,
}
