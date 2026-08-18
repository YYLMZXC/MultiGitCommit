using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiGitCommit
{
    /// <summary>
    /// 项目配置文件模型与读写帮助类。
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
        public List<string> RemoteUrls { get; set; } = new List<string>();

        /// <summary>是否默认执行 git add .（默认为 false，避免误提交垃圾文件）。</summary>
        [JsonPropertyName("CheckAddAll")]
        public bool CheckAddAll { get; set; } = false;

        /// <summary>推送时是否追加 --tags（默认为 false，避免误推送本地标签）。</summary>
        [JsonPropertyName("CheckPushTags")]
        public bool CheckPushTags { get; set; } = false;

        /// <summary>配置文件完整路径（仓库根目录 + 文件名）。</summary>
        [JsonIgnore]
        public string ConfigFilePath { get; private set; } = string.Empty;

        /// <summary>
        /// 从指定仓库根目录读取项目配置。
        /// 规则：目录下存在 multigitcommit.config.json 则读取；不存在则返回新建的空白配置（不落盘，由调用方在首次保存时写入）。
        /// </summary>
        /// <param name="repoRootDir">git 仓库根目录（与 .git 同级）。</param>
        /// <returns>项目配置对象；目录为空或非法时返回 null。</returns>
        public static ProjectConfig? Load(string repoRootDir)
        {
            if (string.IsNullOrWhiteSpace(repoRootDir))
                return null;

            var config = new ProjectConfig
            {
                ConfigFilePath = Path.Combine(repoRootDir, ConfigFileName)
            };

            try
            {
                if (File.Exists(config.ConfigFilePath))
                {
                    string json = File.ReadAllText(config.ConfigFilePath, System.Text.Encoding.UTF8);
                    var loaded = JsonSerializer.Deserialize<ProjectConfig>(json, JsonOptions);
                    if (loaded != null)
                    {
                        loaded.ConfigFilePath = config.ConfigFilePath;
                        loaded.RemoteUrls ??= new List<string>();
                        return loaded;
                    }
                }
            }
            catch (Exception)
            {
                // 读取失败不抛异常，返回空白配置由上层处理
            }

            return config;
        }

        /// <summary>
        /// 保存配置到当前项目根目录的 multigitcommit.config.json。
        /// 仅操作工具自己的配置文件，绝不触碰 git 原生 .git/config。
        /// </summary>
        /// <returns>保存是否成功；失败时抛出由调用方捕获。</returns>
        public bool Save()
        {
            if (string.IsNullOrWhiteSpace(ConfigFilePath))
                throw new InvalidOperationException("配置路径为空，无法保存（未选择有效的仓库目录）。");

            string dir = Path.GetDirectoryName(ConfigFilePath) ?? string.Empty;
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException($"仓库根目录不存在：{dir}");

            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigFilePath, json, new System.Text.UTF8Encoding(false));
            return true;
        }

        /// <summary>序列化选项：缩进、保留中文、忽略 null 集合。</summary>
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
