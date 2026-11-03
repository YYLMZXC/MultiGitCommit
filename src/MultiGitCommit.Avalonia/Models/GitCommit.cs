namespace MultiGitCommit;

/// <summary>
/// 一条历史提交记录（由 <c>git log</c> 的一行解析而来）。
/// 字段用 ASCII 单元分隔符（0x1F）分隔，避免提交标题中的空格 / 制表符破坏解析。
/// </summary>
public sealed class GitCommit
{
    /// <summary>完整 hash（执行 git reset 时使用）。</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>短 hash（界面显示）。</summary>
    public string ShortHash { get; init; } = string.Empty;

    /// <summary>作者时间，已格式化为 <c>yyyy-MM-dd HH:mm</c>。</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>作者名。</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>提交说明（首行标题）。</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>列表未使用模板时的兜底显示文本。</summary>
    public override string ToString() => $"{ShortHash}  {Date}  {Author}  {Subject}";

    /// <summary>
    /// 解析 <c>git log</c> 输出，每行格式：
    /// <c>完整hash%x1f短hash%x1f作者时间%x1f作者%x1f标题</c>。
    /// 格式不符的行（如警告输出）直接跳过。
    /// </summary>
    public static List<GitCommit> Parse(string stdout)
    {
        var commits = new List<GitCommit>();
        if (string.IsNullOrWhiteSpace(stdout)) return commits;

        foreach (string raw in stdout.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            string[] parts = line.Split('\u001f');
            if (parts.Length < 5) continue;

            string hash = parts[0].Trim();
            if (hash.Length == 0) continue;

            commits.Add(new GitCommit
            {
                Hash = hash,
                ShortHash = parts[1].Trim(),
                Date = FormatDate(parts[2].Trim()),
                Author = parts[3].Trim(),
                Subject = parts[4].Trim()
            });
        }

        return commits;
    }

    /// <summary>
    /// 把 <c>--date=iso</c> 的 <c>2026-11-03 12:34:56 +0800</c> 截成分钟精度
    /// <c>2026-11-03 12:34</c>；长度不足时原样返回。
    /// </summary>
    private static string FormatDate(string iso)
        => iso.Length >= 16 ? iso[..16] : iso;
}
