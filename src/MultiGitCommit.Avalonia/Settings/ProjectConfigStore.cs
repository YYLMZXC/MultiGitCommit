using System.Text;
using System.Text.Json;

namespace MultiGitCommit;

/// <summary>
/// 项目配置的持久化契约：模型只表示数据，磁盘读写、JSON 选项、路径计算全部由仓储负责。
/// </summary>
public interface IProjectConfigStore
{
    /// <summary>从仓库根目录读取配置；文件不存在或损坏时返回空白配置（不落盘）。</summary>
    ProjectConfig Load(string repoRootDir);

    /// <summary>把配置写回仓库根目录的 <c>multigitcommit.config.json</c>（绝不触碰 .git/config）。</summary>
    void Save(ProjectConfig config);
}

/// <summary><see cref="IProjectConfigStore"/> 的默认实现。</summary>
public sealed class ProjectConfigStore : IProjectConfigStore
{
    public ProjectConfig Load(string repoRootDir)
    {
        if (string.IsNullOrWhiteSpace(repoRootDir))
            throw new ArgumentException("仓库根目录不能为空。", nameof(repoRootDir));

        var config = new ProjectConfig
        {
            ConfigFilePath = Path.Combine(repoRootDir, ProjectConfig.ConfigFileName)
        };

        try
        {
            if (!File.Exists(config.ConfigFilePath)) return config;

            string json = File.ReadAllText(config.ConfigFilePath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<ProjectConfig>(json, ProjectConfig.JsonOptions);
            if (loaded == null) return config;

            loaded.ConfigFilePath = config.ConfigFilePath;
            loaded.RemoteUrls ??= new List<string>();
            return loaded;
        }
        catch (Exception)
        {
            // 读取失败不抛异常，返回空白配置由上层处理
            return config;
        }
    }

    public void Save(ProjectConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.ConfigFilePath))
            throw new InvalidOperationException("配置路径为空，无法保存（未选择有效的仓库目录）。");

        string dir = Path.GetDirectoryName(config.ConfigFilePath) ?? string.Empty;
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"仓库根目录不存在：{dir}");

        string json = JsonSerializer.Serialize(config, ProjectConfig.JsonOptions);
        File.WriteAllText(config.ConfigFilePath, json, new UTF8Encoding(false));
    }
}
