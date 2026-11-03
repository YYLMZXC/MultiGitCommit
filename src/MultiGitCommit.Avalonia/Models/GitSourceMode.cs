namespace MultiGitCommit;

/// <summary>git 可执行文件来源模式（与 Windows 版枚举数值一致，配置文件可通用）。</summary>
public enum GitSourceMode
{
    /// <summary>自动检测：软件内嵌 Git → 常见安装位置 → 系统 PATH。</summary>
    Auto = 0,

    /// <summary>仅使用系统 PATH 环境变量中的 git（环境 git）。</summary>
    System = 1,

    /// <summary>用户指定的 git 可执行文件 / Git 目录（便携版、软件自带 git）。</summary>
    Custom = 2,

    /// <summary>仅使用软件内嵌的 Git（.app 内 Resources/git 或软件目录 git/ 子目录）。</summary>
    Embedded = 3
}
