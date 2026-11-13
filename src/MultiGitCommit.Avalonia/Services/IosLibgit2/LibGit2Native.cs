using System;
using System.Runtime.InteropServices;

namespace MultiGitCommit.IosLibgit2;

/// <summary>
/// iOS 上 libgit2 的最小 P/Invoke 声明（静态库已链接进 App 主可执行文件，故用 <c>__Internal</c>）。
///
/// 为什么不用 LibGit2Sharp：它按 <c>DllImport("git2-&lt;hash&gt;")</c> 动态加载共享库，
/// iOS 只有静态链接、没有可 dlopen 的 .dylib，库名解析在 AOT 下不可控；直连 C API 行为确定。
/// </summary>
internal static class LibGit2Native
{
    /// <summary>静态链接到主可执行文件的符号（iOS 专用）。</summary>
    private const string Lib = "__Internal";

    /// <summary>libgit2 的通用返回码：0 成功，负数失败（-3 = GIT_ENOTFOUND）。</summary>
    internal const int Ok = 0;

    /// <summary>GIT_ENOTFOUND：对象 / 引用 / 路径不存在。</summary>
    internal const int NotFound = -3;

    /// <summary>GIT_EEXISTS：引用已存在。</summary>
    internal const int Exists = -4;

    /// <summary>GIT_EUNBORNBRANCH：仓库没有提交（空仓库）。</summary>
    internal const int UnbornBranch = -9;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_libgit2_init();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_libgit2_shutdown();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void git_libgit2_version(out int major, out int minor, out int rev);

    /// <summary>取最近一次失败的详细信息（无错误时返回 null）。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr git_error_last();

    /// <summary>把 <see cref="git_error_last"/> 转成可读文本（不抛异常）。</summary>
    internal static string LastError()
    {
        IntPtr ptr = git_error_last();
        if (ptr == IntPtr.Zero) return "unknown error";

        // git_error { char *message; int klass; }
        IntPtr message = Marshal.ReadIntPtr(ptr);
        return message == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(message) ?? "unknown error";
    }

    /// <summary>libgit2 版本号，如 <c>1.9.1</c>。</summary>
    internal static string Version
    {
        get
        {
            git_libgit2_version(out int major, out int minor, out int rev);
            return $"{major}.{minor}.{rev}";
        }
    }
}
