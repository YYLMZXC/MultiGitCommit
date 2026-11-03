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
