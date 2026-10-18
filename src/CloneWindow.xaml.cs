using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

// 文件夹选择对话框使用 System.Windows.Forms.FolderBrowserDialog（与 MainWindow 一致，按需别名引入）。
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;

namespace MultiGitCommit
{
    /// <summary>
    /// 克隆项目窗口：填写远端 URL 与本地目录，执行 git clone。
    ///
    /// 说明：
    /// 1. 仅负责收集参数并做前置校验，真正的 git 调用由 <see cref="MainWindow"/> 复用统一的进程调用逻辑完成；
    /// 2. 目标目录名默认根据 URL 自动推导，用户手动修改后不再被自动覆盖；
    /// 3. 可选项：指定分支、递归子模块、浅克隆、克隆后写入推送远端列表。
    /// </summary>
    public partial class CloneWindow : Window
    {
        /// <summary>用户确认后的远端仓库 URL。</summary>
        public string RepoUrl { get; private set; } = string.Empty;

        /// <summary>用户确认后的本地父目录（仓库克隆到该目录下）。</summary>
        public string ParentDir { get; private set; } = string.Empty;

        /// <summary>用户确认后的完整目标目录（父目录 + 目录名），即仓库根目录。</summary>
        public string TargetDir { get; private set; } = string.Empty;

        /// <summary>用户确认后要检出的分支；空表示使用远端默认分支。</summary>
        public string BranchName { get; private set; } = string.Empty;

        /// <summary>是否递归克隆子模块（--recursive）。</summary>
        public bool Recursive { get; private set; }

        /// <summary>是否浅克隆（--depth 1）。</summary>
        public bool Shallow { get; private set; }

        /// <summary>克隆成功后是否把该 URL 写入本项目的推送远端列表。</summary>
        public bool AddToRemotes { get; private set; }

        /// <summary>目录名是否被用户手动编辑过（编辑后不再根据 URL 自动覆盖）。</summary>
        private bool _folderNameEdited;

        /// <summary>程序内部设置目录名时抑制 TextChanged 的标志位。</summary>
        private bool _suppress;

        /// <param name="initialDir">父目录初始值（一般传入当前仓库目录或程序工作目录）。</param>
        public CloneWindow(string? initialDir = null)
        {
            InitializeComponent();

            ChkAddRemote.IsChecked = true;
            TxtParentDir.Text = !string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir)
                ? initialDir
                : Environment.CurrentDirectory;

            UpdatePreview();
        }

        // ==================== 输入事件 ====================

        /// <summary>URL 变化：未手动编辑过目录名时，自动推导目录名。</summary>
        private void TxtUrl_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_folderNameEdited)
            {
                SetFolderName(DeriveRepoName(TxtUrl.Text));
            }
            UpdatePreview();
        }

        /// <summary>目录名被用户手动编辑后，标记不再自动覆盖。</summary>
        private void TxtFolderName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_suppress) return;
            _folderNameEdited = true;
            UpdatePreview();
        }

        /// <summary>其余输入项变化时刷新预览。</summary>
        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            UpdatePreview();
        }

        /// <summary>浏览选择本地父目录。</summary>
        private void BtnBrowseParent_Click(object sender, RoutedEventArgs e)
        {
            string initial = Directory.Exists(TxtParentDir.Text.Trim())
                ? TxtParentDir.Text.Trim()
                : Environment.CurrentDirectory;

            using var dlg = new FolderBrowserDialog
            {
                Description = "请选择要把仓库克隆到的父目录",
                SelectedPath = initial,
                ShowNewFolderButton = true
            };

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TxtParentDir.Text = dlg.SelectedPath.TrimEnd('\\');
                UpdatePreview();
            }
        }

        // ==================== 预览与参数组装 ====================

        /// <summary>刷新目标路径与将要执行的 git clone 命令预览。</summary>
        private void UpdatePreview()
        {
            string url = TxtUrl.Text.Trim();
            string parent = TxtParentDir.Text.Trim();
            string folder = TxtFolderName.Text.Trim();

            string target = string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folder)
                ? "（请先填写远端 URL 与本地目录）"
                : Path.Combine(parent, folder);

            TxtTargetPath.Text = target;
            TxtCommandPreview.Text = string.IsNullOrEmpty(url)
                ? "（请先填写远端 URL）"
                : $"git {BuildCloneArgs(TxtBranch.Text.Trim(), ChkRecursive.IsChecked == true, ChkShallow.IsChecked == true)}\"{url}\" \"{target}\"";
        }

        /// <summary>
        /// 组装 git clone 参数（不含 url 与目标目录，二者由调用方分别加引号后追加）。
        /// 追加 --progress：输出被重定向到管道时强制 git 输出克隆进度，便于写入日志。
        /// </summary>
        internal static string BuildCloneArgs(string branch, bool recursive, bool shallow)
        {
            var sb = new StringBuilder("clone");
            if (!string.IsNullOrWhiteSpace(branch))
                sb.Append(" --branch ").Append(branch.Trim());
            if (recursive)
                sb.Append(" --recursive");
            if (shallow)
                sb.Append(" --depth 1");
            sb.Append(" --progress ");
            return sb.ToString();
        }

        /// <summary>根据远端 URL 推导默认目录名（去掉 .git 后缀，取最后一段路径）。</summary>
        private static string DeriveRepoName(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            string s = url.Trim().TrimEnd('/', '\\');
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);

            // 兼容 ssh（git@host:user/repo.git）、https 与本地路径三种形式
            int idx = s.LastIndexOfAny(new[] { '/', '\\', ':' });
            string name = idx >= 0 ? s.Substring(idx + 1) : s;

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), string.Empty);

            return name.Trim();
        }

        /// <summary>程序内部设置目录名（不触发“已手动编辑”标记）。</summary>
        private void SetFolderName(string name)
        {
            _suppress = true;
            try
            {
                TxtFolderName.Text = name;
            }
            finally
            {
                _suppress = false;
            }
        }

        // ==================== 确定 / 校验 ====================

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                Warn("请填写远端仓库 URL。");
                TxtUrl.Focus();
                return;
            }
            if (url.Contains('"'))
            {
                Warn("远端 URL 不能包含双引号。");
                TxtUrl.Focus();
                return;
            }

            string parent = TxtParentDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(parent))
            {
                Warn("请填写要克隆到的本地目录。");
                TxtParentDir.Focus();
                return;
            }
            if (!Directory.Exists(parent))
            {
                var choice = MessageBox.Show(this,
                    $"父目录不存在，是否新建？{Environment.NewLine}{parent}",
                    "克隆项目", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (choice != MessageBoxResult.OK) return;

                try
                {
                    Directory.CreateDirectory(parent);
                }
                catch (Exception ex)
                {
                    Warn($"新建目录失败：{ex.Message}");
                    return;
                }
            }

            string folder = TxtFolderName.Text.Trim();
            if (string.IsNullOrEmpty(folder))
            {
                folder = DeriveRepoName(url);
                SetFolderName(folder);
            }
            if (string.IsNullOrEmpty(folder))
            {
                Warn("无法确定目录名，请手动填写【目录名】。");
                TxtFolderName.Focus();
                return;
            }
            if (folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Warn("目录名包含非法字符（\\ / : * ? \" < > | 等）。");
                TxtFolderName.Focus();
                return;
            }

            string target = Path.Combine(parent, folder);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                Warn($"目标目录已存在且不为空：{target}{Environment.NewLine}请修改【目录名】或选择其他本地目录。");
                TxtFolderName.Focus();
                return;
            }

            string branch = TxtBranch.Text.Trim();
            if (branch.Length > 0 && !MainWindow.IsValidBranchName(branch))
            {
                Warn("分支名不合法：不能包含空格或 ~ ^ : ? * [ \\ .. // 等字符，且不能以 - / . 开头或结尾。");
                TxtBranch.Focus();
                return;
            }

            RepoUrl = url;
            ParentDir = parent;
            TargetDir = target;
            BranchName = branch;
            Recursive = ChkRecursive.IsChecked == true;
            Shallow = ChkShallow.IsChecked == true;
            AddToRemotes = ChkAddRemote.IsChecked == true;

            DialogResult = true;
        }

        private static void Warn(string message) =>
            MessageBox.Show(message, "克隆项目", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
