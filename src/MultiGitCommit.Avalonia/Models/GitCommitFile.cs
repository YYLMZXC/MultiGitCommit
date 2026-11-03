namespace MultiGitCommit;

/// <summary>
/// 某次提交中一个被修改的文件（由 <c>git show --name-status</c> 的一行解析而来）。
/// </summary>
public sealed class GitCommitFile
{
    /// <summary>原始状态：A 新增 / M 修改 / D 删除 / R100 重命名 / C75 复制 / T 类型变更。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>新路径（删除时为被删除的路径）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>重命名 / 复制前的旧路径；其他状态为 null。</summary>
    public string? OldPath { get; init; }

    /// <summary>状态首字母（列表里紧凑显示）。</summary>
    public string StatusLabel => Status.Length > 0 ? Status[..1] : "?";

    /// <summary>列表显示文本：重命名显示「旧路径 → 新路径」。</summary>
    public string Display => string.IsNullOrEmpty(OldPath) ? Path : $"{OldPath} → {Path}";

    public override string ToString() => $"{StatusLabel}  {Display}";

    /// <summary>
    /// 解析 <c>git show --name-status</c> 输出，每行形如
    /// <c>M\tpath</c> 或 <c>R100\told\tnew</c>。
    /// </summary>
    public static List<GitCommitFile> Parse(string stdout)
    {
        var files = new List<GitCommitFile>();
        if (string.IsNullOrWhiteSpace(stdout)) return files;

        foreach (string raw in stdout.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            string[] parts = line.Split('\t');
            if (parts.Length < 2) continue;

            string status = parts[0].Trim();
            if (status.Length == 0) continue;

            // R / C 状态带相似度分数，后面跟「旧路径 + 新路径」两列
            bool hasOld = (status[0] == 'R' || status[0] == 'C') && parts.Length >= 3;
            files.Add(new GitCommitFile
            {
                Status = status,
                OldPath = hasOld ? parts[1] : null,
                Path = hasOld ? parts[2] : parts[1]
            });
        }

        return files;
    }
}
