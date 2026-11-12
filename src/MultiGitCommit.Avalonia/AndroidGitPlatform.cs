using System;
using System.Diagnostics;
using System.IO;

namespace MultiGitCommit;

/// <summary>
/// Android 端内嵌 Git 的运行时支撑（非 Android 平台所有成员均为空操作，调用方无需 #if）。
///
/// 打包方式：git 二进制与依赖库重命名为 lib*.so 打进 APK 的 lib/&lt;abi&gt;/ 目录（见 csproj 的
/// AndroidNativeLibrary 与 scripts/FetchAndroidGit.ps1）。原因：Android 10（targetSdk 29+）起
/// SELinux 禁止执行应用数据目录里的文件，APK 原生库目录（安装后位于
/// ApplicationInfo.NativeLibraryDir）是应用唯一保持可执行的落点。
///
/// 两个限制的处理：
/// 1. 原生库文件必须叫 lib*.so，而 git 按文件名（git-remote-https 等）查找传输辅助程序，
///    且 Android 上无法通过 PATH 直接执行 —— 因此首次使用时在应用私有目录建 gitbin/ 符号链接
///    目录（链接指回原生库目录），作为 GIT_EXEC_PATH 提供给子进程。符号链接的 execve 只校验
///    最终目标的 SELinux 标签（apk 原生库），不会被 app_data_file 的执行禁令拦截。
/// 2. Termux 构建的 git 默认前缀硬编码为 Termux 私有目录，系统级配置 / 属性 / 模板文件均不存在，
///    需要显式关掉（GIT_CONFIG_NOSYSTEM / GIT_ATTR_NOSYSTEM）；TLS 根证书打包为 raw 资源，
///    首次使用时复制到 gitbin/ 并通过 GIT_SSL_CAINFO 指定。
///
/// 已知限制：APK 内没有 ssh 客户端，ssh 协议远端（git@github.com:...）在安卓端不可用，
/// 请使用 https 远端（凭据随 URL 注入，见 CommitPushService）。
/// </summary>
public static class AndroidGitPlatform
{
    /// <summary>符号链接目录名（应用私有 Files 目录下）：作为 git 的 GIT_EXEC_PATH。</summary>
    private const string ExecDirName = "gitbin";

    /// <summary>TLS 根证书文件名（从 raw 资源复制到符号链接目录）。</summary>
    private const string CaBundleName = "ca-bundle.crt";

    /// <summary>
    /// 需要暴露给 git 的辅助程序 → 对应的原生库文件。
    /// git-remote-http 按 argv0 分发 https / ftp / ftps 变体；
    /// git-upload-pack / receive-pack / upload-archive 与主二进制同体（argv0 分发），链接到 libgit.so。
    /// </summary>
    private static readonly (string Helper, string Lib)[] Helpers =
    {
        ("git-remote-http", "libgitremotehttp.so"),
        ("git-remote-https", "libgitremotehttp.so"),
        ("git-remote-ftp", "libgitremotehttp.so"),
        ("git-remote-ftps", "libgitremotehttp.so"),
        ("git-upload-pack", "libgit.so"),
        ("git-receive-pack", "libgit.so"),
        ("git-upload-archive", "libgit.so"),
    };

    private static readonly object InitLock = new();

    // ==================== 路径探测 ====================

    private static string? _nativeLibDir;
    private static bool _nativeLibProbed;
    private static string? _execDir;

    /// <summary>APK 原生库目录（安装后含 libgit.so 等内嵌 git 文件）；非 Android / 探测失败为 null。</summary>
    public static string? NativeLibraryDir
    {
        get
        {
#if ANDROID
            if (!_nativeLibProbed)
            {
                lock (InitLock)
                {
                    if (!_nativeLibProbed)
                    {
                        _nativeLibProbed = true;
                        try
                        {
                            var appInfo = global::Android.App.Application.Context?.ApplicationInfo;
                            _nativeLibDir = string.IsNullOrEmpty(appInfo?.NativeLibraryDir)
                                ? null
                                : appInfo!.NativeLibraryDir;
                        }
                        catch
                        {
                            _nativeLibDir = null;
                        }
                    }
                }
            }
            return _nativeLibDir;
#else
            return null;
#endif
        }
    }

    /// <summary>
    /// 建议的仓库父目录（系统选择器不可用时手输路径的默认值）。
    /// 已授予「所有文件访问」时优先共享存储（其他文件管理器可见），否则退回应用专属外部目录
    /// （/storage/emulated/0/Android/data/&lt;包名&gt;/files，无需特殊权限即可读写）。
    /// 非 Android 返回用户主目录，供桌面端沿用原来的行为。
    /// </summary>
    public static string SuggestRepoParent()
    {
#if ANDROID
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30)
                && global::Android.OS.Environment.IsExternalStorageManager
                && Directory.Exists("/storage/emulated/0"))
                return "/storage/emulated/0/GitRepos";

            string? own = global::Android.App.Application.Context?.GetExternalFilesDir(null)?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(own))
                return Path.Combine(own, "GitRepos");
        }
        catch
        {
            // 平台目录探测失败时走兜底值，不阻断界面
        }
        return "/storage/emulated/0/GitRepos";
#else
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
#endif
    }

    /// <summary>
    /// 是否已拿到读写共享存储所需的权限：Android 11+ 为「所有文件访问」（MANAGE_EXTERNAL_STORAGE），
    /// Android 10 及以下为 WRITE_EXTERNAL_STORAGE 运行时权限。非 Android 恒为 true。
    /// </summary>
    public static bool IsAllFilesAccessGranted
    {
        get
        {
#if ANDROID
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    return global::Android.OS.Environment.IsExternalStorageManager;

                return global::Android.App.Application.Context?
                           .CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage)
                       == global::Android.Content.PM.Permission.Granted;
            }
            catch
            {
                return false;
            }
#else
            return true;
#endif
        }
    }

    /// <summary>打开系统设置页让用户授予存储权限（Android 11+ 为「所有文件访问」，旧版为应用详情页）。</summary>
    public static void OpenStoragePermissionSettings()
    {
#if ANDROID
        try
        {
            var context = global::Android.App.Application.Context;
            string package = "package:" + context?.PackageName;
            var intent = OperatingSystem.IsAndroidVersionAtLeast(30)
                ? new global::Android.Content.Intent(
                    global::Android.Provider.Settings.ActionManageAllFilesAccessPermission,
                    global::Android.Net.Uri.Parse(package))
                : new global::Android.Content.Intent(
                    global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                    global::Android.Net.Uri.Parse(package));
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            context?.StartActivity(intent);
        }
        catch
        {
            // 个别 ROM 没有对应设置页：用户仍可手动到系统设置里授权
        }
#endif
    }

    /// <summary>
    /// 把系统文件夹选择器（SAF ACTION_OPEN_DOCUMENT_TREE）返回的 content:// URI 映射成内嵌 git 能用的真实路径。
    /// 仅支持「内部存储 / SD 卡」这类 ExternalStorageProvider（authority = com.android.externalstorage.documents）：
    /// tree/&lt;volumeId&gt;:&lt;相对路径&gt; → /storage/emulated/0/&lt;相对路径&gt; 或 /storage/&lt;volumeId&gt;/&lt;相对路径&gt;。
    /// 「下载内容」、云端盘等 provider 不给真实文件系统路径，返回 null，由调用方回退到手输路径。
    /// 非 Android 恒为 null。
    /// </summary>
    public static string? ResolveSafTreeUri(Uri? uri)
    {
#if ANDROID
        if (uri == null) return null;
        if (uri.IsFile) return uri.LocalPath;
        if (!string.Equals(uri.Scheme, "content", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Authority, "com.android.externalstorage.documents", StringComparison.OrdinalIgnoreCase))
            return null;

        // 先在「未解码」的串上找 /tree/ 与 /document/ 分界：documentId 内部的 / 是 %2F，
        // 直接整体 UnescapeDataString 后再按 / 截断会把 Download/Sub 这类相对路径切坏。
        string raw = uri.ToString();
        const string treeMarker = "/tree/";
        int tree = raw.IndexOf(treeMarker, StringComparison.OrdinalIgnoreCase);
        if (tree < 0) return null;

        string docPart = raw.Substring(tree + treeMarker.Length);
        int document = docPart.IndexOf("/document/", StringComparison.OrdinalIgnoreCase);
        if (document >= 0) docPart = docPart.Substring(0, document);

        string docId = Uri.UnescapeDataString(docPart);
        int colon = docId.IndexOf(':');
        if (colon <= 0) return null;

        string volume = docId.Substring(0, colon);
        string relative = docId.Substring(colon + 1).Trim('/');

        string? root = null;
        if (string.Equals(volume, "primary", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string candidate in new[] { "/storage/emulated/0", "/sdcard" })
            {
                if (Directory.Exists(candidate)) { root = candidate; break; }
            }
        }
        else
        {
            string candidate = Path.Combine("/storage", volume);
            if (Directory.Exists(candidate)) root = candidate;
        }
        if (root == null) return null;

        return string.IsNullOrEmpty(relative)
            ? root
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
#else
        return null;
#endif
    }

    /// <summary>应用私有 Files 目录（HOME 与符号链接目录的宿主）；非 Android 为 null。</summary>
    private static string? FilesDir
    {
        get
        {
#if ANDROID
            try
            {
                return global::Android.App.Application.Context?.FilesDir?.AbsolutePath;
            }
            catch
            {
                return null;
            }
#else
            return null;
#endif
        }
    }

    /// <summary>
    /// git 的执行路径目录（应用私有目录下 gitbin/，内含指向原生库目录的符号链接与 CA 证书）。
    /// 首次访问时创建；非 Android / 目录不可用时为 null。
    /// </summary>
    private static string? ExecPathDir
    {
        get
        {
            if (_execDir != null) return _execDir;
            lock (InitLock)
            {
                if (_execDir != null) return _execDir;
                _execDir = EnsureExecPath();
                return _execDir;
            }
        }
    }

    // ==================== 运行时准备 ====================

    /// <summary>创建符号链接目录与 CA 证书副本，返回目录路径；失败返回 null。</summary>
    private static string? EnsureExecPath()
    {
#if ANDROID
        if (!OperatingSystem.IsAndroid()) return null;

        string? filesDir = FilesDir;
        string? nativeDir = NativeLibraryDir;
        if (string.IsNullOrEmpty(filesDir) || string.IsNullOrEmpty(nativeDir)) return null;

        string dir = Path.Combine(filesDir, ExecDirName);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            return null;
        }

        foreach (var (helper, lib) in Helpers)
        {
            string target = Path.Combine(nativeDir, lib);
            if (!File.Exists(target)) continue;

            string link = Path.Combine(dir, helper);
            try
            {
                var info = new FileInfo(link);
                if (info.LinkTarget != null) continue;            // 符号链接已就位
                if (info.Exists) info.Delete();                   // 残留的普通文件（异常中断产物）
                File.CreateSymbolicLink(link, target);
            }
            catch
            {
                // 单个链接失败不致命：对应传输方式（https 等）不可用，其余功能不受影响
            }
        }

        CopyCaBundle(dir);
        return dir;
#else
        return null;
#endif
    }

    /// <summary>把打包在 raw 资源里的 TLS 根证书复制到符号链接目录（已存在则跳过）。</summary>
    private static void CopyCaBundle(string dir)
    {
#if ANDROID
        string target = Path.Combine(dir, CaBundleName);
        try
        {
            if (File.Exists(target) && new FileInfo(target).Length > 0) return;

            var context = global::Android.App.Application.Context;
            var resources = context?.Resources;
            if (resources == null || string.IsNullOrEmpty(context?.PackageName)) return;

            int id = resources.GetIdentifier("ca_bundle", "raw", context!.PackageName);
            if (id == 0) return;

            using var input = resources.OpenRawResource(id);
            using var output = File.Create(target);
            input.CopyTo(output);
        }
        catch
        {
            // 证书不可用只影响 https 服务端证书校验（git 会报 ssl 错误），不影响其他功能
        }
#endif
    }

    // ==================== 子进程环境 ====================

    /// <summary>
    /// 为 git 子进程注入安卓运行所需的环境变量：
    /// LD_LIBRARY_PATH（依赖库都在原生库目录）、GIT_EXEC_PATH（辅助程序符号链接）、
    /// HOME（git 全局配置落沙盒）、TMPDIR、CA 证书，并关闭 Termux 前缀相关的系统级配置查找。
    /// 非 Android 平台为空操作。
    /// </summary>
    public static void ApplyEnvironment(ProcessStartInfo psi)
    {
#if ANDROID
        if (!OperatingSystem.IsAndroid()) return;

        string? nativeDir = NativeLibraryDir;
        if (string.IsNullOrEmpty(nativeDir)) return;

        psi.EnvironmentVariables["LD_LIBRARY_PATH"] = nativeDir;

        string? execDir = ExecPathDir;
        if (!string.IsNullOrEmpty(execDir))
        {
            psi.EnvironmentVariables["GIT_EXEC_PATH"] = execDir;

            string caBundle = Path.Combine(execDir, CaBundleName);
            if (File.Exists(caBundle))
                psi.EnvironmentVariables["GIT_SSL_CAINFO"] = caBundle;
        }

        string? filesDir = FilesDir;
        if (!string.IsNullOrEmpty(filesDir))
            psi.EnvironmentVariables["HOME"] = filesDir;

        try
        {
            string? cacheDir = global::Android.App.Application.Context?.CacheDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(cacheDir))
                psi.EnvironmentVariables["TMPDIR"] = cacheDir;
        }
        catch
        {
            // TMPDIR 缺失时 git 回退使用仓库目录创建临时文件，不致命
        }

        // Termux 构建的默认前缀（Termux 私有目录）下没有系统级 gitconfig / gitattributes，显式关掉查找
        psi.EnvironmentVariables["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.EnvironmentVariables["GIT_ATTR_NOSYSTEM"] = "1";

        // 传输辅助程序同时按 PATH 查找（git 的 run_command 走 execvp 语义），把两个目录前置
        string existing = psi.EnvironmentVariables.ContainsKey("PATH")
            ? psi.EnvironmentVariables["PATH"] ?? string.Empty
            : string.Empty;
        string head = string.Join(":",
            new[] { execDir, nativeDir }.Where(s => !string.IsNullOrEmpty(s)));
        psi.EnvironmentVariables["PATH"] =
            existing.Length > 0 ? head + ":" + existing : head;
#endif
    }

    /// <summary>
    /// 「仅加载器需要」的最小环境（LD_LIBRARY_PATH）：供 <see cref="GitLocator.Validate"/> 的
    /// 轻量探测使用 —— 该路径不经过 <see cref="GitCliRunner.BuildStartInfo"/> 的 git 环境注入，
    /// 而动态链接的 libgit.so 离开原生库目录找不到依赖库，直接启动会失败。
    /// 非 Android 平台返回 null（不设置任何变量）。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? LoaderEnvironment()
    {
#if ANDROID
        if (!OperatingSystem.IsAndroid()) return null;

        string? nativeDir = NativeLibraryDir;
        if (string.IsNullOrEmpty(nativeDir)) return null;

        return new Dictionary<string, string>
        {
            ["LD_LIBRARY_PATH"] = nativeDir
        };
#else
        return null;
#endif
    }
}
