using System;
using System.Diagnostics;
using System.Globalization;
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
///    remote helper 以 git_cmd 方式启动（git remote-https &lt;name&gt; &lt;url&gt;），git 需要
///    先从 PATH 找到名为 git 的可执行文件，再由它去 git_exec_path() 找 git-remote-https，
///    所以 gitbin/ 里必须同时有 git 这个链接，否则统一报 "unable to find remote helper for 'https'"。
///    每次访问都会校验链接目标：应用更新 / 重装后 nativeLibraryDir 的随机路径会变，
///    旧链接会悬空，必须重建，不能只判断「链接已存在」。
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
        // git_cmd 启动 helper 时先 exec PATH 里的 git（见类注释），原生库目录只有 libgit.so
        ("git", "libgit.so"),
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

    /// <summary>共享存储根目录。</summary>
    private const string SharedStorageRoot = "/storage/emulated/0";

    /// <summary>安卓版独立工作文件夹名：单独占一个目录，不与其他仓库 / 应用混放。</summary>
    private const string DefaultRepoFolderName = "MultiGitCommit";

    /// <summary>
    /// 默认仓库父目录：安卓版在共享存储下使用一个独立的 MultiGitCommit 文件夹，
    /// 文件管理器可见、方便传文件；首次访问时自动创建，但它本身 **不是** Git 仓库，
    /// 克隆 / 初始化的仓库都作为它的子目录存在。
    /// 未授予「所有文件访问」时退回应用专属外部目录（无需权限即可读写）。
    /// 非 Android 返回用户主目录，供桌面端沿用原来的行为。
    /// </summary>
    public static string EnsureDefaultRepoParent()
    {
#if ANDROID
        if (IsAllFilesAccessGranted && Directory.Exists(SharedStorageRoot))
        {
            string shared = Path.Combine(SharedStorageRoot, DefaultRepoFolderName);
            if (TryCreateDirectory(shared)) return shared;
        }

        try
        {
            string? own = global::Android.App.Application.Context?.GetExternalFilesDir(null)?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(own))
            {
                string fallback = Path.Combine(own, DefaultRepoFolderName);
                if (TryCreateDirectory(fallback)) return fallback;
            }
        }
        catch
        {
            // 平台目录探测失败时继续走共享存储兜底值
        }
        return Path.Combine(SharedStorageRoot, DefaultRepoFolderName);
#else
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
#endif
    }

    /// <summary>创建目录并确认存在；FUSE 未授权 / 只读时按不可用处理，不抛给 UI。</summary>
    private static bool TryCreateDirectory(string path)
    {
#if ANDROID
        try
        {
            Directory.CreateDirectory(path);
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>
    /// 建议的仓库父目录（克隆 / 选择文件夹等手输路径的默认值）。
    /// 每次都重新解析并顺便确保目录存在：用户中途去系统设置授权「所有文件访问」后，
    /// 返回应用即可自动切回共享存储的独立文件夹，无需重启。
    /// </summary>
    public static string SuggestRepoParent() => EnsureDefaultRepoParent();

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

    /// <summary>
    /// 打开系统设置页让用户授予存储权限，返回是否成功拉起；失败时 error 为最后一次异常信息，
    /// 调用方必须给出「手动进入设置」的提示，不能像以前那样静默吞掉。
    ///
    /// Android 11+ 按优先级尝试三级候选（部分 ROM 只实现其中一种）：
    /// ① ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION：直达本应用的开关页，必须带 package: data；
    /// ② ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION：全局「所有文件访问」应用列表，不接收 data；
    /// ③ ACTION_APPLICATION_DETAILS_SETTINGS：应用详情页，旧版系统的主路径，也是兜底手动入口。
    /// 旧实现把 ② 的 action 和 ① 的 package: data 混在一起，AOSP Settings 的 intent-filter
    /// 不匹配 data，StartActivity 抛 ActivityNotFoundException 后被空 catch 吞掉，
    /// 表现就是点「前往设置」毫无反应。
    /// </summary>
    public static bool OpenStoragePermissionSettings(out string? error)
    {
        error = null;
#if ANDROID
        // 优先用前台 Activity 作 Context：部分 ROM 不接受从 Application Context 启动设置页；
        // 退回 Application Context 时必须带 NEW_TASK 才能启动 Activity。
        global::Android.Content.Context? context = MainActivity.Current
                                                   ?? global::Android.App.Application.Context;
        string? packageName = context?.PackageName;
        if (context == null || string.IsNullOrEmpty(packageName))
        {
            error = "无法获取应用上下文或包名";
            return false;
        }

        var packageUri = global::Android.Net.Uri.Parse("package:" + packageName);
        var candidates = new List<global::Android.Content.Intent>();

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var appPage = new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, packageUri);
            appPage.AddCategory(global::Android.Content.Intent.CategoryDefault);
            candidates.Add(appPage);
            candidates.Add(new global::Android.Content.Intent(
                global::Android.Provider.Settings.ActionManageAllFilesAccessPermission));
        }
        candidates.Add(new global::Android.Content.Intent(
            global::Android.Provider.Settings.ActionApplicationDetailsSettings, packageUri));

        foreach (var intent in candidates)
        {
            try
            {
                if (context is not global::Android.App.Activity)
                    intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                context.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                // 该 ROM 没有这一级设置页：换下一个候选，全部失败才把最后的异常交给 UI 提示
                error = ex.Message;
            }
        }
#endif
        return false;
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
            string link = Path.Combine(dir, helper);
            try
            {
                // 目标库缺失（旧版 APK 未打包）时，也要清掉可能指向旧安装路径的残留链接
                if (!File.Exists(target))
                {
                    File.Delete(link);
                    continue;
                }

                var info = new FileInfo(link);
                // 应用更新 / 重装后 nativeLibraryDir 的随机目录名会变，旧链接仍在但已悬空；
                // File.Exists(link) 会解析符号链接，可同时识别出这种「链接存在却不可用」的情况。
                if (string.Equals(info.LinkTarget, target, StringComparison.Ordinal) && File.Exists(link))
                    continue;

                // File.Delete 走 unlink，对悬空符号链接同样有效（不解析目标）
                File.Delete(link);
                File.CreateSymbolicLink(link, target);
            }
            catch
            {
                // 单个链接失败不致命：对应传输方式（https 等）不可用，其余功能不受影响；
                // 启动日志会通过 GetBrokenHelpers 把缺失的辅助程序报出来
            }
        }

        CopyCaBundle(dir);
        return dir;
#else
        return null;
#endif
    }

    /// <summary>
    /// 校验 gitbin/ 里的辅助程序链接是否正常（顺便完成首次创建 / 更新后的重建），
    /// 返回不可用的名字列表；为空表示齐备。供启动日志诊断，非 Android 恒为空。
    /// </summary>
    public static IReadOnlyList<string> GetBrokenHelpers()
    {
#if ANDROID
        string? dir = ExecPathDir;
        if (string.IsNullOrEmpty(dir))
            return Helpers.Select(h => h.Helper).ToArray();

        var broken = new List<string>();
        foreach (var (helper, _) in Helpers)
        {
            // File.Exists 解析符号链接：能同时查出「链接不存在」与「链接悬空」
            if (!File.Exists(Path.Combine(dir, helper))) broken.Add(helper);
        }
        return broken;
#else
        return Array.Empty<string>();
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

        ApplySafeDirectories(psi);

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
    /// Android 共享存储（FUSE / sdcardfs）里文件的 stat uid 由系统映射，通常不等于应用 uid，
    /// git 2.35.2+ 会把 /storage/emulated/0 下的仓库判为 "detected dubious ownership"：
    /// 克隆本身能完成，但随后的 rev-parse / status / branch 全部被拒绝，仓库无法加载。
    ///
    /// 用 GIT_CONFIG_COUNT 注入进程级 safe.directory（等价于命令行 -c，不改写用户的 ~/.gitconfig）：
    /// 只放开共享存储根（结尾 /* 是前缀匹配，覆盖新建的克隆目标、SD 卡与任意层级子目录），
    /// 再补一条当前工作目录的精确值兜底用户手输的其他位置，而不是无条件信任所有路径。
    /// </summary>
    private static void ApplySafeDirectories(ProcessStartInfo psi)
    {
#if ANDROID
        var values = new List<string> { "/storage/*", "/sdcard/*" };

        string? workDir = psi.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workDir))
        {
            try
            {
                string full = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar);
                if (full.Length > 0
                    && !full.StartsWith("/storage/", StringComparison.Ordinal)
                    && !full.StartsWith("/sdcard/", StringComparison.Ordinal))
                    values.Add(full);
            }
            catch
            {
                // 路径不规范时仅依赖共享存储通配项，不阻断命令
            }
        }

        for (int i = 0; i < values.Count; i++)
        {
            psi.EnvironmentVariables["GIT_CONFIG_KEY_" + i] = "safe.directory";
            psi.EnvironmentVariables["GIT_CONFIG_VALUE_" + i] = values[i];
        }
        psi.EnvironmentVariables["GIT_CONFIG_COUNT"] = values.Count.ToString(CultureInfo.InvariantCulture);
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
