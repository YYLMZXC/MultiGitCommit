namespace MultiGitCommit;

/// <summary>
/// 工作区中的一个变更项（由 <c>git status --porcelain -z</c> 的一个条目解析而来）。
///
/// 用途：「执行 git add」前把工作区变更列成勾选列表，让用户只添加需要的文件，
/// 避免 <c>git add .</c> 一把梭把临时文件 / 垃圾文件带进提交。
/// </summary>
public sealed class WorkingTreeFile
{
    /// <summary>暂存区状态字符（porcelain 的第一列）：' ' 无变化 / M / A / D / R / C / U / ?。</summary>
    public char IndexStatus { get; init; }

    /// <summary>工作区状态字符（porcelain 的第二列）。</summary>
    public char WorkTreeStatus { get; init; }

    /// <summary>相对仓库根目录的路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>重命名 / 复制前的旧路径；其他状态为 null。</summary>
    public string? OriginalPath { get; init; }

    /// <summary>未跟踪的新文件（<c>??</c>）。</summary>
    public bool IsUntracked => IndexStatus == '?' || WorkTreeStatus == '?';

    /// <summary>暂存区已有该文件的改动（已 add 过）。</summary>
    public bool IsStaged => !IsUntracked && IndexStatus != ' ';

    /// <summary>工作区仍有未进暂存区的改动（未跟踪的文件也算）。</summary>
    public bool HasUnstagedChange => IsUntracked || (WorkTreeStatus != ' ' && WorkTreeStatus != '?');

    /// <summary>用于展示的状态字符：优先工作区状态，其次暂存区状态。</summary>
    public char EffectiveStatus => WorkTreeStatus != ' ' ? WorkTreeStatus : IndexStatus;

    /// <summary>状态的中文说明（列表里紧凑显示）。</summary>
    public string StatusLabel => Describe(EffectiveStatus);

    /// <summary>暂存区状态说明：未暂存 / 已暂存 / 部分暂存。</summary>
    public string StageLabel => IsStaged
        ? (HasUnstagedChange ? "部分暂存" : "已暂存")
        : "未暂存";

    /// <summary>列表显示文本：重命名显示「旧路径 → 新路径」。</summary>
    public string Display => string.IsNullOrEmpty(OriginalPath) ? Path : $"{OriginalPath} → {Path}";

    public override string ToString() => $"{StatusLabel}  {Display}";

    /// <summary>状态字符 → 中文说明。</summary>
    private static string Describe(char status) => status switch
    {
        'M' => "修改",
        'A' => "新增",
        'D' => "删除",
        'R' => "重命名",
        'C' => "复制",
        'T' => "类型变更",
        'U' => "冲突",
        '?' => "未跟踪",
        _ => "变更"
    };

    /// <summary>
    /// 解析 <c>git status --porcelain -z --untracked-files=all</c> 的输出。
    ///
    /// <c>-z</c> 用 NUL 分隔条目、路径不做 C 风格转义（中文 / 空格 / 引号都原样返回），
    /// 重命名 / 复制条目形如 <c>R100 new\0old\0</c>（旧路径紧跟在下一个条目位置）。
    /// </summary>
    public static List<WorkingTreeFile> Parse(string stdout)
    {
        var files = new List<WorkingTreeFile>();
        if (string.IsNullOrEmpty(stdout)) return files;

        string[] tokens = stdout.Split('\0');
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = TrimLineEnding(tokens[i]);

            // 一个条目至少需要「XY + 空格 + 路径」共 4 个字符
            if (token.Length < 4 || token[2] != ' ') continue;

            char index = token[0];
            char workTree = token[1];
            string path = token[3..];

            string? original = null;
            if (index is 'R' or 'C' || workTree is 'R' or 'C')
            {
                // 重命名 / 复制：下一个 NUL 段是旧路径
                if (i + 1 < tokens.Length)
                {
                    string old = TrimLineEnding(tokens[i + 1]);
                    if (old.Length > 0)
                    {
                        original = old;
                        i++;
                    }
                }
            }

            if (path.Length == 0) continue;

            files.Add(new WorkingTreeFile
            {
                IndexStatus = index,
                WorkTreeStatus = workTree,
                Path = path,
                OriginalPath = original
            });
        }

        return files;
    }

    /// <summary>
    /// 去掉命令执行器读行时补上的换行（-z 输出本身没有换行，
    /// 只有整段输出的残尾会被 <see cref="StreamReader.ReadLineAsync"/> 附加一次）。
    /// </summary>
    private static string TrimLineEnding(string value)
        => value.Length == 0 ? value : value.TrimEnd('\r', '\n');
}
