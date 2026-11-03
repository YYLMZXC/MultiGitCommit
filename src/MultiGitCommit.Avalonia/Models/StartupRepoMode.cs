namespace MultiGitCommit;

/// <summary>
/// 启动时加载哪个仓库的模式（各平台枚举数值一致，配置文件可通用）。
/// 由【Git 环境】设置窗口「启动时加载的仓库」选择，作用于下次启动。
/// </summary>
public enum StartupRepoMode
{
    /// <summary>当前目录优先：当前目录（或其上级目录）是 Git 仓库时直接加载，否则回退到上次使用的仓库。</summary>
    CurrentDirectory = 0,

    /// <summary>上次使用的仓库优先：直接打开上次成功加载的仓库，不看当前目录。</summary>
    LastUsed = 1
}
