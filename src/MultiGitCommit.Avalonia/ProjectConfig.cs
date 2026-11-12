using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiGitCommit;

/// <summary>
/// 项目配置文件模型（纯数据，跨平台版字段完全兼容）；读写由 <see cref="IProjectConfigStore"/> 负责。
///
/// 配置规则：
/// 1. 配置文件 <c>multigitcommit.config.json</c> 存放在【每个 git 仓库根目录】，与 .git 文件夹同级；
/// 2. 该文件是【工具自己的独立配置文件】，程序【不会修改 git 原生 .git/config】，也不调用 git remote 命令；
/// 3. 推送时直接使用 url 进行 git push {url} {branch}，不依赖 git remote 别名；
/// 4. 每个 git 仓库拥有独立配置，切换仓库自动加载对应项目的远端列表。
/// </summary>
public class ProjectConfig
{
    /// <summary>配置文件固定文件名，存放在 git 仓库根目录，与 .git 同级。</summary>
    public const string ConfigFileName = "multigitcommit.config.json";

    /// <summary>推送远端 url 列表（支持 ssh / https 任意格式，可配置 1 个或多个）。</summary>
    [JsonPropertyName("RemoteUrls")]
    public List<string> RemoteUrls { get; set; } = new();

    /// <summary>是否默认执行 git add .（默认为 false，避免误提交垃圾文件）。</summary>
    [JsonPropertyName("CheckAddAll")]
    public bool CheckAddAll { get; set; }

    /// <summary>推送时是否追加 --tags（默认为 false，避免误推送本地标签）。</summary>
    [JsonPropertyName("CheckPushTags")]
    public bool CheckPushTags { get; set; }

    /// <summary>是否强制提交（commit 追加 --allow-empty）：工作区无变更时仍生成空提交。</summary>
    [JsonPropertyName("CheckForceCommit")]
    public bool CheckForceCommit { get; set; }

    /// <summary>是否强制推送（push 追加 --force）：覆盖远端历史，用于非快进被拒场景。</summary>
    [JsonPropertyName("CheckForcePush")]
    public bool CheckForcePush { get; set; }

    /// <summary>配置文件完整路径（由 <see cref="IProjectConfigStore"/> 加载时填充）。</summary>
    [JsonIgnore]
    public string ConfigFilePath { get; internal set; } = string.Empty;

    /// <summary>序列化选项：缩进、保留中文、忽略 null 集合（仅供仓储使用）。</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
