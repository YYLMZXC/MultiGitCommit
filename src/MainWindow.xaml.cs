using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

// 文件夹选择对话框使用 System.Windows.Forms.FolderBrowserDialog，
// 需要引用程序集：项目 csproj 中已设置 <UseWindowsForms>true</UseWindowsForms> 并引用 System.Windows.Forms。
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;

namespace MultiGitCommit
{
    /// <summary>
    /// 主窗口：自用 Git 一键提交推送工具。
    ///
    /// ⚠ 本工具【不会修改 git 原生 .git/config】；远端地址保存在每个项目目录的
    ///   <c>multigitcommit.config.json</c>，为工具独立配置文件。
    /// ⚠ 推送时直接使用 url 推送（git push {url} {branch}），不操作 git remote 命令。
    /// ⚠ 每个 git 仓库拥有独立配置，切换仓库自动加载对应项目的远端列表。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>当前项目独立配置（与仓库根目录绑定，切换仓库时重新加载）。</summary>
        private ProjectConfig? _config;

        /// <summary>当前有效 git 仓库根目录；为 null 表示未选择/校验失败。</summary>
        private string? _repoRoot;

        /// <summary>加载配置的标志位，防止加载过程中触发复选框事件导致重复保存。</summary>
        private bool _loading;

        /// <summary>仓库路径输入防抖计时器：停止输入 400ms 后再加载，避免每敲一个字符都执行 git 校验。</summary>
        private readonly DispatcherTimer _pathDebounce;

        /// <summary>单条命令执行结果。</summary>
        private sealed class CommandResult
        {
            public int ExitCode;
            public string Stdout = string.Empty;
            public string Stderr = string.Empty;
            public bool Success => ExitCode == 0;
        }

        public MainWindow()
        {
            InitializeComponent();
            _pathDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _pathDebounce.Tick += (s, e) =>
            {
                _pathDebounce.Stop();
                LoadConfigFromPath();
            };

            Log("MultiGitCommit 已启动。");
            Log("提示：本工具不会修改 git 原生 .git/config；远端地址保存在每个项目目录下的 multigitcommit.config.json。");
            Log("提示：每个 git 仓库拥有独立配置，切换仓库会自动加载对应项目的远端列表。");

            // 解析 git.exe 来源（环境 git / 自动检测 / 指定便携版），不依赖系统安装 git
            RefreshGitEnvironment();

            // 启动时默认使用当前工作目录，若为 git 仓库则自动加载其配置
            TxtRepoPath.Text = Environment.CurrentDirectory;
        }

        // ==================== 日志输出 ====================

        /// <summary>向日志区追加一行（带时间戳），并自动滚动到底部。</summary>
        private void Log(string message)
        {
            if (TxtLog == null) return; // InitializeComponent 尚未完成时保护

            // 保证始终在 UI 线程操作文本框
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Log(message));
                return;
            }

            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            if (ChkAutoScroll.IsChecked == true)
            {
                TxtLog.ScrollToEnd();
            }
        }

        // ==================== 进程调用（统一 UTF-8 编码） ====================

        /// <summary>创建 git/cmd 进程配置。指定 UTF-8 编码，解决 Windows 控制台中文输出乱码。</summary>
        private static Process CreateProcess(string workingDir, string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // 指定 UTF-8 编码，避免中文乱码
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // 将解析到的 git 相关目录注入子进程 PATH：
            // 1. 便携版 / 指定路径 / 内嵌的 git 也能找到自带的 ssh、git-lfs 等程序；
            // 2. 手动命令区通过 cmd.exe 执行 "git xxx" 时同样可用。
            var pathEntries = GitLocator.GetPathEntries();
            if (pathEntries.Count > 0)
            {
                string oldPath = psi.EnvironmentVariables["PATH"] ?? string.Empty;
                psi.EnvironmentVariables["PATH"] =
                    string.Join(Path.PathSeparator.ToString(), pathEntries) + Path.PathSeparator + oldPath;
            }

            return new Process { StartInfo = psi };
        }

        /// <summary>同步执行命令（用于快速校验，输出量小），返回结果对象。</summary>
        private static CommandResult RunCommand(string workingDir, string fileName, string arguments)
        {
            try
            {
                using var p = CreateProcess(workingDir, fileName, arguments);
                p.Start();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                Task.WaitAll(outTask, errTask);
                p.WaitForExit();
                return new CommandResult { ExitCode = p.ExitCode, Stdout = outTask.Result, Stderr = errTask.Result };
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -1, Stderr = ex.ToString() };
            }
        }

        /// <summary>异步执行命令，stdout/stderr 逐行实时输出到日志区。</summary>
        private async Task<CommandResult> RunCommandAsync(string workingDir, string fileName, string arguments, string? logLabel = null)
        {
            using var p = CreateProcess(workingDir, fileName, arguments);
            if (logLabel != null) Log("> " + logLabel);
            p.Start();

            var outTask = DrainStreamAsync(p.StandardOutput);
            var errTask = DrainStreamAsync(p.StandardError);
            string stdout = await outTask;
            string stderr = await errTask;
            await p.WaitForExitAsync();

            return new CommandResult { ExitCode = p.ExitCode, Stdout = stdout, Stderr = stderr };
        }

        /// <summary>逐行读取输出流并实时写入日志，同时累积完整文本用于返回。</summary>
        private async Task<string> DrainStreamAsync(StreamReader reader)
        {
            var sb = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length > 0) Log("      " + line);
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        // ==================== 配置加载与保存 ====================

        /// <summary>
        /// 根据当前仓库路径文本框内容加载项目配置。
        /// 规则：目录存在且为合法 git 仓库 → 读取 multigitcommit.config.json（不存在则新建空白配置）；
        /// 切换不同仓库自动加载对应项目自己的远端列表。
        /// </summary>
        private void LoadConfigFromPath()
        {
            string path = TxtRepoPath.Text.Trim();
            _repoRoot = null;
            _config = null;

            _loading = true;
            try
            {
                ListRemotes.Items.Clear();
                ChkAddAll.IsChecked = false;
                ChkPushTags.IsChecked = false;
                TxtBranchInfo.Text = string.Empty;
                BtnEditRemote.IsEnabled = false;
                BtnDeleteRemote.IsEnabled = false;
                BtnOpenRemote.IsEnabled = false;

                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (!Directory.Exists(path))
                {
                    Log($"目录不存在：{path}");
                    return;
                }

                if (!EnsureGitAvailable()) return;

                // 校验是否为合法 git 仓库（git rev-parse --is-inside-work-tree）
                var check = RunCommand(path, GitLocator.GitExe, "rev-parse --is-inside-work-tree");
                if (!check.Success || !check.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"不是合法的 git 仓库目录：{path}（未发现有效的 .git 仓库）");
                    return;
                }

                _repoRoot = path;
                // 读取本项目独立配置；不存在则返回空白配置（首次保存时才写入文件）
                _config = ProjectConfig.Load(path);
                if (_config == null)
                {
                    Log($"读取项目配置失败：{path}");
                    return;
                }

                foreach (var url in _config.RemoteUrls)
                {
                    if (!string.IsNullOrWhiteSpace(url))
                        ListRemotes.Items.Add(url);
                }
                ChkAddAll.IsChecked = _config.CheckAddAll;
                ChkPushTags.IsChecked = _config.CheckPushTags;

                string urlSummary = _config.RemoteUrls.Count == 0
                    ? "（空）"
                    : string.Join("、", _config.RemoteUrls);
                Log($"已加载项目配置：{ProjectConfig.ConfigFileName}（目录：{path}）");
                Log($"当前项目远端共 {_config.RemoteUrls.Count} 个：{urlSummary}");
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>保存当前配置到项目根目录 multigitcommit.config.json（仅工具自己的配置文件，不动 .git/config）。</summary>
        private void SaveConfig()
        {
            if (_config == null) return;
            try
            {
                _config.Save();
                Log($"已保存配置：{_config.ConfigFilePath}");
            }
            catch (Exception ex)
            {
                Log($"保存配置失败：{ex.Message}");
            }
        }

        /// <summary>刷新远端列表 UI（不触发保存）。</summary>
        private void RefreshRemoteList()
        {
            _loading = true;
            try
            {
                ListRemotes.Items.Clear();
                if (_config != null)
                {
                    foreach (var url in _config.RemoteUrls)
                    {
                        if (!string.IsNullOrWhiteSpace(url))
                            ListRemotes.Items.Add(url);
                    }
                }
            }
            finally
            {
                _loading = false;
            }
        }

        // ==================== 仓库目录区域事件 ====================

        /// <summary>路径输入变化：防抖后重新加载项目配置，实现切换仓库自动加载对应远端列表。</summary>
        private void TxtRepoPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            _pathDebounce.Stop();
            _pathDebounce.Start();
        }

        /// <summary>选择文件夹：浏览选择 git 仓库根目录，选择后自动读取该项目 multigitcommit.config.json。</summary>
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            // 文件夹选择对话框使用 System.Windows.Forms.FolderBrowserDialog（需引用 System.Windows.Forms 程序集）
            string initial = Directory.Exists(TxtRepoPath.Text.Trim())
                ? TxtRepoPath.Text.Trim()
                : Environment.CurrentDirectory;

            using var dlg = new FolderBrowserDialog
            {
                Description = "请选择 Git 仓库根目录（与 .git 文件夹同级）",
                SelectedPath = initial,
                ShowNewFolderButton = false
            };

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _pathDebounce.Stop(); // 取消防抖，立即加载，避免重复加载
                TxtRepoPath.Text = dlg.SelectedPath.TrimEnd('\\');
                LoadConfigFromPath();
            }
        }

        /// <summary>手动重新读取当前目录下的 multigitcommit.config.json。</summary>
        private void BtnLoadConfig_Click(object sender, RoutedEventArgs e)
        {
            _pathDebounce.Stop();
            LoadConfigFromPath();
        }

        /// <summary>打开“关于”窗口，展示软件信息与项目地址。</summary>
        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

        // ==================== Git 环境管理（不依赖系统安装 git） ====================

        /// <summary>打开 Git 环境设置窗口：选择 自动检测 / 系统 PATH / 指定便携版或软件自带 git。</summary>
        private void BtnGitEnv_Click(object sender, RoutedEventArgs e)
        {
            var win = new GitSettingsWindow(GitLocator.Settings) { Owner = this };
            if (win.ShowDialog() == true)
            {
                GitLocator.Settings.GitSource = win.SelectedSource;
                GitLocator.Settings.CustomGitPath = win.SelectedCustomPath;
                try
                {
                    GitLocator.Settings.Save();
                    Log($"已保存 Git 环境设置：{GitLocator.Settings.SettingsFilePath}");
                }
                catch (Exception ex)
                {
                    Log($"保存 Git 环境设置失败：{ex.Message}");
                }
                RefreshGitEnvironment();
            }
        }

        /// <summary>按当前设置重新解析 git.exe，更新状态栏显示并输出日志。</summary>
        private void RefreshGitEnvironment()
        {
            bool ok = GitLocator.Resolve();
            TxtGitStatus.Text = ok
                ? $"Git：{GitLocator.GitExePath}"
                : "Git：未找到（点击【Git 环境】配置）";
            TxtGitStatus.ToolTip = ok
                ? $"当前生效：{GitLocator.Description}"
                : "未找到可用的 git.exe，请点击【Git 环境】按钮配置来源。";

            if (ok)
            {
                Log($"Git 环境：{GitLocator.Description}");
            }
            else
            {
                Log("未找到可用的 git.exe：请点击【Git 环境】按钮选择环境 git（系统 PATH）或指定便携版 / 软件自带 git。");
            }
        }

        /// <summary>确保 git.exe 可用（已解析成功）；不可用时输出日志并返回 false。</summary>
        private bool EnsureGitAvailable()
        {
            if (GitLocator.GitExePath != null) return true;

            Log("流程终止：未找到可用的 git.exe，请点击【Git 环境】按钮配置（自动检测 / 系统 PATH / 指定便携版 git）。");
            return false;
        }

        // ==================== 仓库初始化 / 默认分支 ====================

        /// <summary>默认分支名（初始化仓库 / 创建默认分支时的预填值，可在弹窗中修改）。</summary>
        private const string DefaultBranchName = "main";

        /// <summary>
        /// 初始化仓库：对当前目录执行 git init，并创建默认分支。
        /// 目录不存在时询问是否自动新建；已是 git 仓库则跳过初始化，避免误覆盖。
        /// </summary>
        private async void BtnInitRepo_Click(object sender, RoutedEventArgs e)
        {
            BtnInitRepo.IsEnabled = false;
            try
            {
                string path = TxtRepoPath.Text.Trim();
                if (string.IsNullOrWhiteSpace(path))
                {
                    Log("请先选择或输入要初始化的仓库目录。");
                    return;
                }

                if (!EnsureGitAvailable()) return;

                // 目录不存在时询问是否自动创建，方便“从零新建项目”
                if (!Directory.Exists(path))
                {
                    var choice = MessageBox.Show(
                        $"目录不存在，是否新建该目录？{Environment.NewLine}{path}",
                        "初始化仓库", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                    if (choice != MessageBoxResult.OK) return;

                    try
                    {
                        Directory.CreateDirectory(path);
                        Log($"已新建目录：{path}");
                    }
                    catch (Exception ex)
                    {
                        Log($"新建目录失败：{ex.Message}");
                        return;
                    }
                }

                // 已是 git 仓库则不再重复初始化，防止覆盖既有仓库信息
                if (IsGitRepository(path))
                {
                    Log($"该目录已经是 git 仓库，无需重复初始化：{path}");
                    return;
                }

                string? branch = PromptBranchName("初始化仓库", "请输入默认分支名（例如 main / master）：");
                if (branch == null) return;

                Log($"========== 初始化 git 仓库：{path} ==========");
                var init = await RunCommandAsync(path, GitLocator.GitExe, "init", "git init");
                if (!init.Success)
                {
                    Log("git init 执行失败，初始化终止。");
                    return;
                }

                // git init 默认分支可能是 master，统一改为用户指定的默认分支
                await SetDefaultBranchAsync(path, branch);

                // 初始化完成后按正常流程加载项目配置（此时已是合法仓库）
                _pathDebounce.Stop();
                LoadConfigFromPath();
                Log("========== 仓库初始化完成 ==========");
            }
            catch (Exception ex)
            {
                Log("初始化仓库异常：" + ex);
            }
            finally
            {
                BtnInitRepo.IsEnabled = true;
            }
        }

        /// <summary>
        /// 创建默认分支：为当前仓库创建/切换默认分支。
        /// 空仓库直接设置 HEAD 指向该分支；已有提交则新建（或切换）分支。
        /// </summary>
        private async void BtnCreateBranch_Click(object sender, RoutedEventArgs e)
        {
            BtnCreateBranch.IsEnabled = false;
            try
            {
                string path = TxtRepoPath.Text.Trim();
                if (!Directory.Exists(path))
                {
                    Log($"目录不存在：{path}");
                    return;
                }
                if (!IsGitRepository(path))
                {
                    Log("该目录不是 git 仓库，请先点击【初始化仓库】。");
                    return;
                }

                string? branch = PromptBranchName("创建默认分支", "请输入要创建/切换的默认分支名：");
                if (branch == null) return;

                Log($"========== 创建默认分支：{branch} ==========");
                await SetDefaultBranchAsync(path, branch);

                // 刷新配置加载与分支信息显示
                _pathDebounce.Stop();
                LoadConfigFromPath();
                _repoRoot ??= path;
                await GetCurrentBranchAsync(_repoRoot);
                Log("========== 默认分支处理完成 ==========");
            }
            catch (Exception ex)
            {
                Log("创建默认分支异常：" + ex);
            }
            finally
            {
                BtnCreateBranch.IsEnabled = true;
            }
        }

        /// <summary>判断目录是否已经是合法 git 仓库（git rev-parse --is-inside-work-tree）。</summary>
        private static bool IsGitRepository(string dir)
        {
            var r = RunCommand(dir, GitLocator.GitExe, "rev-parse --is-inside-work-tree");
            return r.Success && r.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 将仓库默认分支设置为指定名称：
        /// 1. 空仓库（尚无任何提交）：git symbolic-ref HEAD refs/heads/{branch}，不会报错，等价于设置默认分支；
        /// 2. 已有提交：分支已存在则切换，不存在则新建并切换。
        /// </summary>
        private async Task SetDefaultBranchAsync(string repoDir, string branch)
        {
            // 用 HEAD 能否解析判断仓库是否已有提交（unborn 状态无法解析 HEAD）
            var head = RunCommand(repoDir, GitLocator.GitExe, "rev-parse --verify HEAD");
            if (!head.Success)
            {
                // 空仓库：直接令 HEAD 指向目标分支，不会产生 master 残留引用
                string args = $"symbolic-ref HEAD refs/heads/{branch}";
                var r = await RunCommandAsync(repoDir, GitLocator.GitExe, args, $"git {args}");
                Log(r.Success ? $"已将默认分支设置为：{branch}" : $"设置默认分支失败：{branch}");
                return;
            }

            // 已有提交：检查目标分支是否已存在，决定切换还是新建
            var exists = RunCommand(repoDir, GitLocator.GitExe, $"show-ref --verify --quiet refs/heads/{branch}");
            string checkoutArgs = exists.Success ? $"checkout {branch}" : $"checkout -b {branch}";
            var res = await RunCommandAsync(repoDir, GitLocator.GitExe, checkoutArgs, $"git {checkoutArgs}");
            if (res.Success)
            {
                Log(exists.Success ? $"已切换到默认分支：{branch}" : $"已创建并切换到默认分支：{branch}");
            }
            else
            {
                Log($"创建默认分支失败：{branch}");
            }
        }

        /// <summary>
        /// 弹出分支名输入框并做基本合法性校验（复用 PromptText）。
        /// 返回 null 表示用户取消；空串或非法名称会提示并要求重新输入。
        /// </summary>
        private static string? PromptBranchName(string title, string message)
        {
            while (true)
            {
                string? text = PromptText(title, message, DefaultBranchName);
                if (text == null) return null; // 用户取消

                text = text.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    MessageBox.Show("分支名不能为空。", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }
                if (!IsValidBranchName(text))
                {
                    MessageBox.Show(
                        "分支名不合法：不能包含空格或 ~ ^ : ? * [ \\ .. // 等字符，且不能以 - / . 开头或结尾。",
                        title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }
                return text;
            }
        }

        /// <summary>校验分支名是否符合 git 引用命名基本规则。</summary>
        private static bool IsValidBranchName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Any(char.IsWhiteSpace)) return false;
            if (name.StartsWith("-") || name.StartsWith("/") || name.EndsWith("/") || name.EndsWith(".")) return false;
            if (name.Contains("..") || name.Contains("//") || name.Contains("@{")) return false;
            if (name.IndexOfAny(new[] { '~', '^', ':', '?', '*', '[', '\\' }) >= 0) return false;
            return true;
        }

        // ==================== 远端地址管理事件 ====================

        private void ListRemotes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = ListRemotes.SelectedIndex >= 0;
            BtnEditRemote.IsEnabled = hasSelection;
            BtnDeleteRemote.IsEnabled = hasSelection;
            BtnOpenRemote.IsEnabled = hasSelection;
        }

        /// <summary>添加远端 URL，并自动保存到项目目录 multigitcommit.config.json。</summary>
        private void BtnAddRemote_Click(object sender, RoutedEventArgs e)
        {
            if (_config == null) { Log("请先选择有效的 git 仓库目录。"); return; }

            var text = PromptText("添加远端", "请输入推送远端 URL（支持 ssh/https 格式，例如 https://gitee.com/user/repo.git）：", string.Empty);
            if (text == null) return; // 用户取消
            if (string.IsNullOrWhiteSpace(text)) { Log("远端 URL 不能为空。"); return; }
            if (_config.RemoteUrls.Contains(text)) { Log($"远端 URL 已存在：{text}"); return; }

            _config.RemoteUrls.Add(text);
            RefreshRemoteList();
            Log($"已添加远端：{text}");
            SaveConfig();
        }

        /// <summary>编辑选中的远端 URL，并自动保存。</summary>
        private void BtnEditRemote_Click(object sender, RoutedEventArgs e)
        {
            if (_config == null) { Log("请先选择有效的 git 仓库目录。"); return; }
            if (ListRemotes.SelectedIndex < 0 || ListRemotes.SelectedIndex >= _config.RemoteUrls.Count)
            {
                Log("请先选中要编辑的远端。");
                return;
            }

            int index = ListRemotes.SelectedIndex;
            string oldUrl = _config.RemoteUrls[index];
            var text = PromptText("编辑远端", "修改远端 URL：", oldUrl);
            if (text == null) return;
            if (string.IsNullOrWhiteSpace(text)) { Log("远端 URL 不能为空。"); return; }

            _config.RemoteUrls[index] = text;
            RefreshRemoteList();
            Log($"已修改远端：{oldUrl} → {text}");
            SaveConfig();
        }

        /// <summary>在系统默认浏览器中打开选中的远端 URL。</summary>
        private void BtnOpenRemote_Click(object sender, RoutedEventArgs e)
        {
            if (ListRemotes.SelectedItem is not string selected)
            {
                Log("请先选中要打开的远端。");
                return;
            }

            if (string.IsNullOrWhiteSpace(selected))
            {
                Log("远端 URL 为空，无法打开。");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = selected,
                    UseShellExecute = true
                });
                Log($"已在浏览器中打开：{selected}");
            }
            catch (Exception ex)
            {
                Log($"打开浏览器失败：{ex.Message}");
            }
        }

        /// <summary>删除选中的远端 URL，并自动保存。</summary>
        private void BtnDeleteRemote_Click(object sender, RoutedEventArgs e)
        {
            if (_config == null) { Log("请先选择有效的 git 仓库目录。"); return; }
            if (ListRemotes.SelectedItem is not string selected)
            {
                Log("请先选中要删除的远端。");
                return;
            }

            _config.RemoteUrls.Remove(selected);
            RefreshRemoteList();
            Log($"已删除远端：{selected}");
            SaveConfig();
        }

        /// <summary>复选【git add .】状态自动写入项目配置。</summary>
        private void ChkAddAll_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _config == null) return;
            _config.CheckAddAll = ChkAddAll.IsChecked == true;
            SaveConfig();
        }

        /// <summary>复选【推送 --tags】状态自动写入项目配置。</summary>
        private void ChkPushTags_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _config == null) return;
            _config.CheckPushTags = ChkPushTags.IsChecked == true;
            SaveConfig();
        }

        // ==================== 主流程：执行提交并推送全部远端 ====================

        private async void BtnCommitPush_Click(object sender, RoutedEventArgs e)
        {
            BtnCommitPush.IsEnabled = false;
            try
            {
                // 若路径文本框与当前加载的仓库不一致，先重新加载配置
                if (_repoRoot == null ||
                    !string.Equals(TxtRepoPath.Text.Trim(), _repoRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _pathDebounce.Stop();
                    LoadConfigFromPath();
                }
                if (_repoRoot == null || _config == null)
                {
                    Log("流程终止：请先选择有效的 git 仓库目录。");
                    return;
                }

                string repoDir = _repoRoot;
                if (!EnsureGitAvailable()) return;

                Log("========== 开始提交并推送流程 ==========");

                // 1) 自动获取当前 git 分支名称（禁止硬编码 main/master）
                string? branch = await GetCurrentBranchAsync(repoDir);
                if (branch == null)
                {
                    Log("流程终止：无法获取当前分支。");
                    return;
                }

                // 2) 读取本项目配置的远端 url 列表
                var urls = _config.RemoteUrls
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct()
                    .ToList();
                if (urls.Count == 0)
                {
                    Log("尚未配置推送远端地址，请添加远端URL");
                    Log("流程终止。");
                    return;
                }

                // 3) 勾选【git add .】才执行；未勾选则跳过，由用户提前手动 add
                if (_config.CheckAddAll)
                {
                    var add = await RunCommandAsync(repoDir, GitLocator.GitExe, "add .", "git add .");
                    if (!add.Success)
                    {
                        Log("git add . 执行失败，继续后续流程。");
                    }
                }

                // 4) 使用 git status 判断工作区状态：无变更则跳过 commit，不生成空提交
                var status = await RunCommandAsync(repoDir, GitLocator.GitExe, "status --porcelain", "git status --porcelain");
                bool hasChanges = !string.IsNullOrWhiteSpace(status.Stdout);
                if (!hasChanges)
                {
                    Log("工作区无任何变更（nothing to commit），跳过 git commit，不生成空提交。");
                }
                else
                {
                    // 5) 组装 commit 消息：固定格式 yyyy-MM-dd HH:mm:ss 用户备注
                    string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string remark = TxtCommitMessage.Text.Trim();
                    string commitMessage = string.IsNullOrEmpty(remark) ? time : $"{time} {remark}";

                    // 对消息内双引号做转义，防止命令行语法报错
                    string escapedMessage = commitMessage.Replace("\"", "\\\"");
                    Log($"提交消息：{commitMessage}");

                    var commit = await RunCommandAsync(repoDir, GitLocator.GitExe, $"commit -m \"{escapedMessage}\"", "git commit -m \"...\"");
                    if (!commit.Success)
                    {
                        Log("git commit 执行失败，继续尝试推送已提交的内容。");
                    }
                }

                // 6) 循环遍历每一个远端 URL 逐个推送；一个远端失败不阻断后续
                bool allSuccess = true;
                foreach (var url in urls)
                {
                    bool withTags = _config.CheckPushTags;
                    string pushArgs = $"push {url} {branch}" + (withTags ? " --tags" : string.Empty);
                    string pushLabel = $"git push {url} {branch}" + (withTags ? " --tags" : string.Empty);
                    Log($"--- 推送到：{url}（分支：{branch}）---");
                    var push = await RunCommandAsync(repoDir, GitLocator.GitExe, pushArgs, pushLabel);
                    if (push.Success)
                    {
                        Log($"[成功] 推送到：{url}");
                    }
                    else
                    {
                        allSuccess = false;
                        Log($"[失败] 推送到：{url}（继续推送其余远端）");
                    }
                }

                Log(allSuccess
                    ? "========== 全部远端推送成功 =========="
                    : "========== 推送流程结束（存在失败项，请查看上方日志） ==========");
            }
            catch (Exception ex)
            {
                Log("发生异常：" + ex);
            }
            finally
            {
                BtnCommitPush.IsEnabled = true;
            }
        }

        /// <summary>获取当前 git 分支名称（git rev-parse --abbrev-ref HEAD），不硬编码 main/master。</summary>
        private async Task<string?> GetCurrentBranchAsync(string repoDir)
        {
            var r = await RunCommandAsync(repoDir, GitLocator.GitExe, "rev-parse --abbrev-ref HEAD", "git rev-parse --abbrev-ref HEAD");
            string branch = r.Stdout.Trim();
            if (!r.Success || string.IsNullOrEmpty(branch) || branch == "HEAD")
            {
                Log("获取当前分支失败（可能处于 detached HEAD 状态）。");
                return null;
            }
            TxtBranchInfo.Text = $"当前分支：{branch}";
            return branch;
        }

        // ==================== 手动命令执行区 ====================

        private async void BtnRunCommand_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteManualCommandAsync();
        }

        /// <summary>命令输入框回车直接执行。</summary>
        private void TxtCommand_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = ExecuteManualCommandAsync();
            }
        }

        /// <summary>在当前选定的仓库目录执行任意 git/cmd 命令，stdout/stderr 输出到日志框。</summary>
        private async Task ExecuteManualCommandAsync()
        {
            string command = TxtCommand.Text.Trim();
            if (string.IsNullOrEmpty(command))
            {
                Log("请输入要执行的命令。");
                return;
            }

            try
            {
                // 工作目录优先使用当前选定的 git 仓库目录
                string workDir = _repoRoot ?? TxtRepoPath.Text.Trim();
                if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                    workDir = Environment.CurrentDirectory;

                Log($"执行命令：{command}（目录：{workDir}）");
                CommandResult r;
                if (command.StartsWith("git ", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(command, "git", StringComparison.OrdinalIgnoreCase))
                {
                    // 以 git 开头的命令直接调用解析到的 git.exe（支持环境 git / 便携版 git）
                    string gitArgs = command.Length > 4 ? command.Substring(4) : string.Empty;
                    if (GitLocator.GitExePath == null)
                    {
                        Log("未找到可用的 git.exe：请点击【Git 环境】按钮配置（自动检测 / 系统 PATH / 指定便携版 git）。");
                        return;
                    }
                    r = await RunCommandAsync(workDir, GitLocator.GitExe, gitArgs, command);
                }
                else
                {
                    // 其他命令通过 cmd.exe 执行；chcp 65001 切换 UTF-8 代码页，解决中文乱码
                    r = await RunCommandAsync(workDir, "cmd.exe", "/c chcp 65001 >nul & " + command, command);
                }
                Log($"命令退出码：{r.ExitCode}");
            }
            catch (Exception ex)
            {
                Log("命令执行异常：" + ex);
            }
        }

        // ==================== 其他 ====================

        /// <summary>清空日志区。</summary>
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }

        /// <summary>
        /// 弹出简易输入对话框（纯代码构建，不引入第三方 UI 库），返回输入内容；用户取消返回 null。
        /// </summary>
        private static string? PromptText(string title, string message, string initialValue)
        {
            var win = new Window
            {
                Title = title,
                Width = 500,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                FontSize = 13
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            var tb = new TextBox
            {
                Text = initialValue,
                MinHeight = 26,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var btnOk = new Button { Content = "确定", Width = 80, IsDefault = true, Padding = new Thickness(8, 2, 8, 2) };
            var btnCancel = new Button { Content = "取消", Width = 80, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            panel.Children.Add(btnOk);
            panel.Children.Add(btnCancel);

            Grid.SetRow(lbl, 0);
            Grid.SetRow(tb, 1);
            Grid.SetRow(panel, 2);
            grid.Children.Add(lbl);
            grid.Children.Add(tb);
            grid.Children.Add(panel);
            win.Content = grid;

            string? result = null;
            btnOk.Click += (s, e) =>
            {
                result = tb.Text.Trim();
                win.DialogResult = true;
            };
            win.Loaded += (s, e) =>
            {
                tb.Focus();
                tb.SelectAll();
            };
            win.ShowDialog();
            return result;
        }
    }
}
