namespace MultiGitCommit;

/// <summary>
/// 提交身份（git config user.name / user.email）的唯一读写入口。
///
/// 抽取原因：此前主窗口与 Git 环境设置窗口各写了一套「读身份 / 写身份 / 值转义」实现，
/// 关键字面量、转义规则、失败判定都在两处维护，很容易改一处漏一处。
/// 现在两处窗口都只依赖本服务，语义只有一份。
/// </summary>
public sealed class GitIdentityService
{
    private readonly IGitCliRunner _cli;

    /// <summary>构造服务；<paramref name="cliRunner"/> 为 null 时使用默认执行器。</summary>
    public GitIdentityService(IGitCliRunner? cliRunner = null)
        => _cli = cliRunner ?? GitCliRunner.Default;

    /// <summary>
    /// 读取当前生效的提交身份。<c>git config --get</c> 取的是生效值（仓库级 → 全局 → 系统级），
    /// 任一项缺失返回空串。
    /// </summary>
    /// <param name="workingDirectory">仓库目录；为空时在进程当前目录读取。</param>
    public (string Name, string Email) Read(string? workingDirectory)
    {
        string dir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory!;

        string name = ReadValue(dir, "user.name");
        string email = ReadValue(dir, "user.email");
        return (name, email);
    }

    /// <summary>
    /// 把身份写入 git config（<see cref="IdentityScope.Local"/> 写本仓库，
    /// <see cref="IdentityScope.Global"/> 写 <c>--global</c>）。
    /// </summary>
    /// <param name="error">失败时返回 git 的报错文本，成功时为空串。</param>
    public bool TryApply(GitIdentity identity, string? workingDirectory, out string error)
    {
        error = string.Empty;
        string dir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory!;

        string scopeArg = identity.Scope == IdentityScope.Global ? "--global " : string.Empty;

        GitCommandResult nameResult = SetValue(dir, $"{scopeArg}user.name", identity.Name);
        if (!nameResult.Success)
        {
            error = nameResult.ErrorText;
            return false;
        }

        GitCommandResult emailResult = SetValue(dir, $"{scopeArg}user.email", identity.Email);
        if (!emailResult.Success)
        {
            error = emailResult.ErrorText;
            return false;
        }

        return true;
    }

    /// <summary>
    /// git config 值的命令行转义：整体包在双引号里，内部的反斜杠与双引号先转义。
    /// 这样名字里带引号 / Windows 路径反斜杠时不会破坏命令行。
    /// </summary>
    public static string EscapeConfigValue(string? value)
        => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    private string ReadValue(string dir, string key)
    {
        GitCommandResult result = _cli.Run(new GitCommandRequest
        {
            Arguments = "config --get " + key,
            WorkingDirectory = dir,
            Timeout = TimeSpan.FromSeconds(5)
        });

        return result.Success ? result.Output : string.Empty;
    }

    private GitCommandResult SetValue(string dir, string keyWithScope, string? value)
        => _cli.Run(new GitCommandRequest
        {
            Arguments = $"config {keyWithScope} \"{EscapeConfigValue(value)}\"",
            WorkingDirectory = dir,
            Timeout = TimeSpan.FromSeconds(5)
        });
}
