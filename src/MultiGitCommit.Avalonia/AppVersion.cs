using System.Reflection;

namespace MultiGitCommit;

/// <summary>
/// 程序版本号：读取编译时自动写入的 <see cref="AssemblyInformationalVersionAttribute"/>
/// （由 csproj 的 Version 属性生成，形如 26.11.03-1119）。
/// 界面不再硬编码版本，发版时无需改任何文件。
/// </summary>
public static class AppVersion
{
    /// <summary>当前版本号；取不到信息版本时退化为程序集版本。</summary>
    public static string Current { get; } = Resolve();

    /// <summary>
    /// 标题用的版本号（大版本号 + 月 + 日）：如 26.11.03-1119 → 26.11.03，
    /// 即去掉构建时间（-HHmm）后的日期部分，够短又能在标题上区分不同天的构建。
    /// 取不到数字开头时返回空字符串（调用方据此决定是否显示版本）。
    /// </summary>
    public static string TitleVersion { get; } = ResolveTitleVersion(Current);

    /// <summary>
    /// 从完整版本号（yy.MM.dd-HHmm）截出「年.月.日」：
    /// 先去掉 -HHmm / +哈希 后缀，再最多保留三段，避免标题过长。
    /// </summary>
    private static string ResolveTitleVersion(string version)
    {
        int cut = version.IndexOfAny(new[] { '-', '+' });
        string core = cut > 0 ? version[..cut] : version;

        // 非数字开头（如 "unknown"）时不显示版本
        if (core.Length == 0 || core[0] is < '0' or > '9') return string.Empty;

        string[] parts = core.Split('.');
        return parts.Length <= 3 ? core : string.Join('.', parts.Take(3));
    }

    private static string Resolve()
    {
        Assembly assembly = typeof(AppVersion).Assembly;

        string? info = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(info))
        {
            // 去掉可能存在的 +提交哈希 后缀（csproj 已关闭，这里兜底）
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        Version? version = assembly.GetName().Version;
        return version == null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
