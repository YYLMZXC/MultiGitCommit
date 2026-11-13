using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MultiGitCommit.IosLibgit2;

/// <summary>
/// <c>native-git/ios/mgc_shim.c</c>（libgit2 的 git CLI 语义适配层）的 P/Invoke 声明。
///
/// 静态库已链接进 App 主可执行文件，库名固定为 <c>__Internal</c>；
/// 返回值约定与各函数语义见 shim 头部注释，此处只做声明与字符串编组。
/// 动态长度的结果（status / log / show / ls-remote）不直接跨边界返回，
/// 而是写入 shim 内部缓冲，再经 <see cref="ReadBuffer"/> 拷出（调用全程持锁，无并发竞争）。
/// </summary>
internal static class LibGit2Shim
{
    private const string Lib = "__Internal";

    private const CallingConvention CC = CallingConvention.Cdecl;

    // ==================== 生命周期 / 版本 ====================

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_init();

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_version(byte[] outBuf, int cap);

    // ==================== 仓库 ====================

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_open(string path, out IntPtr repo);

    [DllImport(Lib, CallingConvention = CC)] internal static extern void mgc_repo_close(IntPtr repo);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_workdir(IntPtr repo, byte[] outBuf, int cap);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_init(string path, byte[] err, int errCap);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_head_branch(IntPtr repo, byte[] outBuf, int cap);

    /// <summary>symbolic-ref --short HEAD；返回 1 表示 detached HEAD。</summary>
    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_head_symbolic_short(IntPtr repo, byte[] outBuf, int cap);

    /// <summary>rev-parse --verify HEAD：0 = 有提交，1 = 没有。</summary>
    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_has_head(IntPtr repo);

    /// <summary>show-ref --verify --quiet：0 = 存在，1 = 不存在。</summary>
    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_ref_exists(IntPtr repo, string refName);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_set_head(IntPtr repo, string refName, byte[] err, int errCap);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_repo_checkout_branch(IntPtr repo, string name, int create, byte[] err, int errCap);

    // ==================== 配置 ====================

    /// <summary>config --get；返回 1 = 键不存在（git 退出码 1）。</summary>
    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_config_get(IntPtr repo, string key, byte[] outBuf, int cap);

    /// <summary>config 写入；repo 为 IntPtr.Zero 时写全局（--global）。</summary>
    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_config_set(IntPtr repo, string key, string value, byte[] err, int errCap);

    // ==================== 状态 / 暂存 / 提交 ====================

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_status_porcelain(IntPtr repo, int nulSep, int untrackedAll);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_add_all(IntPtr repo, byte[] err, int errCap);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_add_paths(IntPtr repo, string[] paths, int count, byte[] err, int errCap);

    /// <summary>diff --cached --quiet：0 = 无暂存改动，1 = 有。</summary>
    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_index_has_staged(IntPtr repo);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_commit(IntPtr repo, string message, int allowEmpty, byte[] outBuf, int outCap, byte[] err, int errCap);

    // ==================== 历史 ====================

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_log(IntPtr repo, int max);

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_show_files(IntPtr repo, string rev);

    /// <summary>git reset；mode：0 soft / 1 mixed / 2 hard。</summary>
    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_reset(IntPtr repo, string rev, int mode, byte[] err, int errCap);

    // ==================== 联网操作 ====================

    /// <summary>进度回调原型（函数指针，iOS AOT 安全）：<c>int cb(const char* message, void* payload)</c>；返回非 0 请求中止。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static int OnProgressNative(IntPtr message, IntPtr payload)
    {
        try
        {
            if (GCHandle.FromIntPtr(payload).Target is not ProgressContext ctx) return 0;
            return ctx.Invoke(message, payload);
        }
        catch
        {
            // 回调里绝不抛异常跨边界：异常时只当作一次进度输出失败
            return 0;
        }
    }

    /// <summary>shim 进度参数的函数指针（指向 <see cref="OnProgressNative"/>）。</summary>
    internal static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> Progress =>
        (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&OnProgressNative;

    /// <summary>clone；depth 为 0 表示完整克隆（libgit2 浅克隆支持有限）。</summary>
    [DllImport(Lib, CallingConvention = CC)]
    internal static unsafe extern int mgc_clone(string url, string path, string branch, int depth, int recursive,
        string user, string pass, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> progress, IntPtr payload, byte[] err, int errCap);

    /// <summary>push；tags 非 0 时附带推送全部标签。</summary>
    [DllImport(Lib, CallingConvention = CC)]
    internal static unsafe extern int mgc_push(IntPtr repo, string url, string branch, int force, int tags,
        string user, string pass, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> progress, IntPtr payload, byte[] err, int errCap);

    /// <summary>pull；strategy：0 ff-only / 1 rebase / 2 merge。</summary>
    [DllImport(Lib, CallingConvention = CC)]
    internal static unsafe extern int mgc_pull(IntPtr repo, string url, string branch, int strategy,
        string user, string pass, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> progress, IntPtr payload, byte[] err, int errCap);

    /// <summary>ls-remote --heads；结果写入 shim 缓冲。</summary>
    [DllImport(Lib, CallingConvention = CC)]
    internal static unsafe extern int mgc_ls_remote(string url, string user, string pass, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> progress, IntPtr payload, byte[] err, int errCap);

    // ==================== 结果缓冲 ====================

    [DllImport(Lib, CallingConvention = CC)] internal static extern int mgc_buffer_read(byte[] outBuf, int cap, out int len);

    [DllImport(Lib, CallingConvention = CC)] internal static extern void mgc_buffer_free();

    /// <summary>读取 shim 缓冲并按 UTF-8 解码；缓冲不足时自动扩容重试。</summary>
    internal static string ReadBuffer()
    {
        int len;
        for (int cap = 64 * 1024; cap <= 64 * 1024 * 1024; cap *= 8)
        {
            var buf = new byte[cap];
            if (mgc_buffer_read(buf, cap, out len) == 0)
            {
                return System.Text.Encoding.UTF8.GetString(buf, 0, len);
            }
        }
        return string.Empty;
    }

    /// <summary>读出固定大小的 C 字符串缓冲（err / out 类参数）。</summary>
    internal static string ReadCString(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end);
    }

    /// <summary>统一尺寸的 err / out 缓冲。</summary>
    internal const int BufferSize = 4096;

    /// <summary>进度回调的托管上下文（经 GCHandle 传给原生回调）。</summary>
    internal sealed class ProgressContext
    {
        public Action<string>? Emit;
        public Func<bool>? ShouldAbort;

        public int Invoke(IntPtr messagePtr, IntPtr payload)
        {
            if (ShouldAbort?.Invoke() == true) return -1;
            if (messagePtr == IntPtr.Zero) return 0;
            string? message = Marshal.PtrToStringUTF8(messagePtr);
            if (!string.IsNullOrEmpty(message)) Emit?.Invoke(message);
            return 0;
        }
    }
}
