using System.Text.Json.Serialization;

namespace MultiGitCommit;

/// <summary>提交身份写入 git config 的作用域。</summary>
public enum IdentityScope
{
    /// <summary>仅当前仓库（git config，写入 .git/config）。</summary>
    Local = 0,

    /// <summary>全局（git config --global，对所有仓库生效）。</summary>
    Global = 1
}

/// <summary>
/// 提交身份：git 的 user.name 与 user.email。
/// git 生成 commit 的**前置条件**——二者缺一时 git 直接报错
/// （Please tell me who you are / unable to auto-detect email address），根本生成不了提交。
/// </summary>
public class GitIdentity
{
    /// <summary>提交用户名（git config user.name）。</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>提交邮箱（git config user.email）。</summary>
    [JsonPropertyName("Email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>写入作用域：仓库级 / 全局。</summary>
    [JsonPropertyName("Scope")]
    public IdentityScope Scope { get; set; } = IdentityScope.Local;

    /// <summary>是否已完整填写（用户名与邮箱都非空）。</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Email);

    /// <summary>展示文本。</summary>
    public override string ToString() => IsComplete ? $"{Name} <{Email}>" : "（未设置）";
}
