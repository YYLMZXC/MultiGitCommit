namespace MultiGitCommit;

/// <summary>
/// git 引用（分支）命名规则：纯领域规则，不依赖任何 UI 类型或外部进程。
/// </summary>
public static class GitRefName
{
    private static readonly char[] ForbiddenChars = { '~', '^', ':', '?', '*', '[', '\\' };

    /// <summary>校验分支名是否符合 git 引用命名的基本规则。</summary>
    public static bool IsValidBranchName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Any(char.IsWhiteSpace)) return false;
        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.')) return false;
        if (name.Contains("..", StringComparison.Ordinal) ||
            name.Contains("//", StringComparison.Ordinal) ||
            name.Contains("@{", StringComparison.Ordinal)) return false;
        return name.IndexOfAny(ForbiddenChars) < 0;
    }
}
