namespace MultiGitCommit;

/// <summary>
/// 应用设置的访问契约。
///
/// 抽取原因：此前业务代码直接读写静态可变的 <c>GitLocator.Settings</c>，
/// 任何文件都能在不被察觉的情况下替换 / 改写全局状态，测试时也无法隔离。
/// 现在调用方依赖这个接口，<see cref="SettingsProvider.Instance"/> 只是它的默认实现；
/// 默认实现由 <see cref="AppServices"/> 组合根统一装配，调用方可换成测试替身而无需改业务代码。
/// </summary>
public interface ISettingsProvider
{
    /// <summary>当前设置对象（同一进程内实例稳定，读取总是拿到最新值）。</summary>
    AppSettings Current { get; }

    /// <summary>把当前设置写回磁盘。</summary>
    void Save();
}

/// <summary>
/// 默认实现：首次访问时从 %APPDATA%\MultiGitCommit\settings.json
/// （macOS 为 ~/Library/Application Support/MultiGitCommit/settings.json，
/// Linux 为 $XDG_CONFIG_HOME/MultiGitCommit/settings.json 或 ~/.config/MultiGitCommit/settings.json）加载。
/// </summary>
public sealed class SettingsProvider : ISettingsProvider
{
    /// <summary>进程内共享实例：git 定位器与各窗口都通过它取设置。</summary>
    public static SettingsProvider Instance { get; } = new();

    private AppSettings? _current;

    /// <inheritdoc />
    public AppSettings Current => _current ??= AppSettings.Load();

    /// <inheritdoc />
    public void Save() => Current.Save();
}
