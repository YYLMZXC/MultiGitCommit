using System.Diagnostics;

namespace MultiGitCommit;

/// <summary>
/// 用系统默认关联程序打开文件、或在文件管理器中定位文件（Windows / macOS / Linux）。
///
/// 说明：Windows 的 ShellExecute 会按文件类型关联启动程序；macOS 用 <c>open</c>；
/// Linux 显式走 <c>xdg-open</c>（缺失时回退 gio / kde-open 等），
/// 这样在无桌面 opener 的环境里也能给出明确报错，而不是静默失败。
/// 本类只负责「打开」，不读取、不修改文件内容。
/// </summary>
public static class FileOpener
{
    /// <summary>
    /// 把 git 输出的仓库相对路径（统一用 / 分隔）解析为仓库内的绝对路径，并阻止目录穿越。
    /// </summary>
    public static bool TryResolveUnder(string repoRoot, string relativePath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(relativePath))
        {
            error = Loc.T("文件路径为空。");
            return false;
        }

        try
        {
            string root = Path.GetFullPath(repoRoot);
            string normalized = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(root, normalized));

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

            if (!candidate.Equals(root, comparison) && !candidate.StartsWith(prefix, comparison))
            {
                error = Loc.F($"路径超出仓库范围，已拒绝打开：{relativePath}");
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex)
        {
            error = Loc.T("路径无效：") + ex.Message;
            return false;
        }
    }

    /// <summary>用系统默认关联程序打开文件；文件不存在时返回 false。</summary>
    public static bool TryOpen(string path, out string error)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            error = Loc.T("移动端不支持用系统默认程序打开本地文件。");
            return false;
        }

        if (!File.Exists(path))
        {
            error = Loc.F($"文件不存在：{path}");
            return false;
        }

        if (OperatingSystem.IsWindows())
            return TryShellExecute(path, out error);

        if (OperatingSystem.IsMacOS())
            return TryStartAny(new[] { ("open", new[] { path }) }, out error);

        // Linux：优先 xdg-open（freedesktop 标准），再试桌面环境自带的 opener
        return TryStartAny(new[]
        {
            ("xdg-open", new[] { path }),
            ("gio", new[] { "open", path }),
            ("gnome-open", new[] { path }),
            ("kde-open", new[] { path })
        }, out error);
    }

    /// <summary>
    /// 直接打开文件夹本身（Windows 资源管理器 / macOS Finder / Linux 文件管理器；
    /// Android 调用外部文件管理器，见 <see cref="AndroidGitPlatform.TryOpenFolderInFileManager"/>）；
    /// 与 <see cref="TryReveal"/> 的「在父目录中定位并选中」不同，这里进入目录内容，
    /// 供仓库管理首页的【打开文件夹】使用。
    /// </summary>
    public static bool TryOpenFolder(string dir, out string error)
    {
        if (OperatingSystem.IsAndroid())
            return AndroidGitPlatform.TryOpenFolderInFileManager(dir, out error);

        if (OperatingSystem.IsIOS())
        {
            error = Loc.T("移动端不支持在系统文件管理器中打开文件夹。");
            return false;
        }

        if (!Directory.Exists(dir))
        {
            error = Loc.F($"所在目录不存在：{dir}");
            return false;
        }

        // Windows：ShellExecute 传目录路径即由资源管理器打开该目录（不用 explorer /select，那会停在父目录）
        if (OperatingSystem.IsWindows())
            return TryShellExecute(dir, out error);

        if (OperatingSystem.IsMacOS())
            return TryStartAny(new[] { ("open", new[] { dir }) }, out error);

        return TryStartAny(new[]
        {
            ("xdg-open", new[] { dir }),
            ("gio", new[] { "open", dir }),
            ("gnome-open", new[] { dir }),
            ("kde-open", new[] { dir })
        }, out error);
    }

    /// <summary>
    /// 在文件管理器中定位文件（Windows 资源管理器选中 / macOS Finder 显示 / Linux 打开所在文件夹）。
    /// 文件已被删除时自动退到最近一个仍然存在的父目录，因此 D 状态的文件也可用。
    /// </summary>
    public static bool TryReveal(string path, out string error)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            error = Loc.T("移动端不支持在系统文件管理器中定位文件。");
            return false;
        }

        string target = path;
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            string? dir = Path.GetDirectoryName(target);
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);

            if (string.IsNullOrEmpty(dir))
            {
                error = Loc.F($"所在目录不存在：{path}");
                return false;
            }

            target = dir;
        }

        if (OperatingSystem.IsWindows())
        {
            // explorer 的 /select 参数要求把带空格的路径整体加引号；
            // 这里手工拼 Arguments（ArgumentList 会二次加引号，反而破坏 /select 语法）
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + target + "\"",
                    UseShellExecute = true
                });
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        if (OperatingSystem.IsMacOS())
            return TryStartAny(new[] { ("open", new[] { "-R", target }) }, out error);

        // Linux 没有统一的「选中文件」接口，打开其所在文件夹即可
        string folder = Directory.Exists(target)
            ? target
            : Path.GetDirectoryName(target) ?? target;

        return TryStartAny(new[]
        {
            ("xdg-open", new[] { folder }),
            ("gio", new[] { "open", folder }),
            ("gnome-open", new[] { folder }),
            ("kde-open", new[] { folder })
        }, out error);
    }

    /// <summary>Windows：交给 ShellExecute，按文件扩展名关联启动对应程序。</summary>
    private static bool TryShellExecute(string path, out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>依次尝试多个系统 opener，命中第一个可用的即返回；全部失败时汇总错误。</summary>
    private static bool TryStartAny((string Exe, string[] Args)[] candidates, out string error)
    {
        var errors = new List<string>();
        foreach ((string exe, string[] args) in candidates)
        {
            if (TryStart(exe, args, out string oneError))
            {
                error = string.Empty;
                return true;
            }

            errors.Add($"{exe}：{oneError}");
        }

        error = errors.Count == 0 ? Loc.T("没有可用的系统打开程序。") : string.Join("；", errors);
        return false;
    }

    /// <summary>启动外部 opener；不等待其退出，避免阻塞 UI。</summary>
    private static bool TryStart(string exe, string[] args, out string error)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            Process.Start(psi);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
