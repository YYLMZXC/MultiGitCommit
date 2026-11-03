namespace MultiGitCommit;

/// <summary>
/// 执行 git add 时的文件添加方式（各平台枚举数值一致，配置文件可通用）。
/// 由【Git 环境】设置窗口「文件添加方式」选择，保存到应用设置目录的 settings.json。
/// </summary>
public enum StageMode
{
    /// <summary>全提交（默认）：直接执行 <c>git add .</c>，添加工作区全部变更，不弹窗。</summary>
    AddAll = 0,

    /// <summary>可选提交：先列出工作区变更，由用户勾选要添加的文件，只对勾选项执行 git add。</summary>
    Select = 1
}
