namespace MultiGitCommit;

/// <summary>应用启动时默认显示的页面（当前目录所在仓库存在时始终优先进项目页）。</summary>
public enum StartPageMode
{
    /// <summary>项目页：直接进入仓库提交推送界面（历史行为）。</summary>
    RepoPage = 0,

    /// <summary>仓库管理首页：最近项目记录 + 本地 Git 仓库自动发现，从列表快速跳转。</summary>
    RepoHome = 1,
}
