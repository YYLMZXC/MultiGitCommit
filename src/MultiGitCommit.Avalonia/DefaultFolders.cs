using System;
using System.IO;

namespace MultiGitCommit;

/// <summary>
/// 各平台「默认仓库父目录」的统一入口：克隆父目录、选择文件夹的手输默认值、启动水印都取这里。
///
/// 桌面：沿用旧行为（<see cref="AndroidGitPlatform.SuggestRepoParent"/> 对非 Android 返回用户主目录）。
/// Android：共享存储下的 MultiGitCommit 文件夹，未授权时退回应用专属目录（见 AndroidGitPlatform）。
/// iOS：应用沙盒 <c>Documents/MultiGitCommit</c>。iOS 沙盒不允许访问沙盒外的目录，Avalonia 的
///      文件夹选择器也拿不到可用真实路径，所以必须预置一个 App 自己的固定可写目录；
///      配合 Info.plist 的 UIFileSharingEnabled / LSSupportsOpeningDocumentsInPlace，
///      该目录会出现在系统「文件」App →「我的 iPhone」→ MultiGitCommit，可把现成仓库拷进 / 拷出。
/// </summary>
public static class DefaultFolders
{
    /// <summary>移动端独立工作文件夹名（Android / iOS 一致，均为单独的 MultiGitCommit 目录）。</summary>
    private const string FolderName = "MultiGitCommit";

    /// <summary>
    /// 建议的仓库父目录：目录不存在时顺手创建（克隆 / 初始化都作为它的子目录），
    /// 创建失败时退回可写根目录本身，保证返回值总是一个可展示的路径。
    /// </summary>
    public static string RepoParent() => OperatingSystem.IsIOS()
        ? EnsureIosRepoParent()
        : AndroidGitPlatform.SuggestRepoParent();

    /// <summary>
    /// iOS 文档目录（「文件」App 可见）：<c>SpecialFolder.MyDocuments</c> 在 iOS 上就是沙盒
    /// <c>Documents</c>；个别环境返回空串时退回 &lt;沙盒根&gt;/Documents。
    /// </summary>
    public static string IosDocumentsDir
    {
        get
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(docs)) return docs;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home) ? "Documents" : Path.Combine(home, "Documents");
        }
    }

    /// <summary>创建并确认 iOS 默认仓库文件夹；失败时退回 Documents 本身，不抛给 UI。</summary>
    private static string EnsureIosRepoParent()
    {
        string docs = IosDocumentsDir;
        string dir = Path.Combine(docs, FolderName);
        try
        {
            Directory.CreateDirectory(dir);
            if (Directory.Exists(dir)) return dir;
        }
        catch
        {
            // 磁盘异常 / 只读时按不可用处理，继续走下一条兜底
        }

        try
        {
            Directory.CreateDirectory(docs);
        }
        catch
        {
            // Documents 本身由系统创建，正常不会失败；失败也让 UI 拿到路径自行提示
        }
        return docs;
    }
}
