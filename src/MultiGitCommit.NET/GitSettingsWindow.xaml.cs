using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MultiGitCommit
{
    /// <summary>
    /// Git 环境设置窗口：选择 git.exe 来源（自动检测 / 系统 PATH / 指定路径），
    /// 让工具不依赖系统安装的 git，可使用便携版或软件自带的 Git。
    /// </summary>
    public partial class GitSettingsWindow : Window
    {
        /// <summary>用户点击确定后选定的来源模式。</summary>
        public GitSourceMode SelectedSource { get; private set; } = GitSourceMode.Auto;

        /// <summary>用户点击确定后选定的自定义路径（仅 Custom 模式有意义）。</summary>
        public string SelectedCustomPath { get; private set; } = string.Empty;

        /// <summary>传入的全局设置对象；点确定时把凭据（账号）写回它。</summary>
        private readonly AppSettings _settings;

        /// <summary>当前编辑中的凭据副本（点确定才写回设置，取消不影响）。</summary>
        private readonly List<GitCredential> _credentials;

        /// <summary>InitializeComponent / 初始化过程中抑制事件（避免抢焦点、避免重复检测）。</summary>
        private bool _suppress;

        public GitSettingsWindow(AppSettings current)
        {
            _settings = current;
            _credentials = (current.Credentials ?? new List<GitCredential>())
                .Where(c => c != null)
                .Select(c => new GitCredential
                {
                    Host = c.Host ?? string.Empty,
                    Username = c.Username ?? string.Empty,
                    Token = c.Token ?? string.Empty
                })
                .ToList();

            _suppress = true;
            InitializeComponent();
            _suppress = false;

            RbAuto.IsChecked = current.GitSource == GitSourceMode.Auto;
            RbEmbedded.IsChecked = current.GitSource == GitSourceMode.Embedded;
            RbSystem.IsChecked = current.GitSource == GitSourceMode.System;
            RbCustom.IsChecked = current.GitSource == GitSourceMode.Custom;
            TxtCustomPath.Text = current.CustomGitPath ?? string.Empty;

            RefreshCredList();
            UpdateEnabled();
            Test(); // 打开窗口即显示当前配置的检测结果
        }

        // ==================== 推送凭据（账号） ====================

        /// <summary>刷新凭据列表显示（ListBox 直接显示 GitCredential.ToString()，不含令牌）。</summary>
        private void RefreshCredList()
        {
            ListCred.ItemsSource = null;
            ListCred.ItemsSource = _credentials;
        }

        /// <summary>选中某条凭据时填入输入框，便于修改。</summary>
        private void ListCred_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ListCred.SelectedItem is not GitCredential cred) return;

            TxtCredHost.Text = cred.Host;
            TxtCredUser.Text = cred.Username;
            TxtCredToken.Password = cred.Token;
        }

        /// <summary>添加或更新一条凭据（同一主机只保留一条）。</summary>
        private void BtnCredAdd_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtCredHost.Text.Trim();
            string user = TxtCredUser.Text.Trim();
            string token = TxtCredToken.Password.Trim();

            if (host.Length == 0)
            {
                ShowResult("× 请先填写远端主机（如 cnb.cool）。", false);
                return;
            }

            if (user.Length == 0 || token.Length == 0)
            {
                ShowResult("× 请同时填写用户名与访问令牌。", false);
                return;
            }

            string hostKey = GitCredentials.GetHost(host) ?? host.ToLowerInvariant();
            var existing = _credentials.FirstOrDefault(c =>
                string.Equals(GitCredentials.GetHost(c.Host) ?? c.Host.Trim().ToLowerInvariant(),
                    hostKey, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Host = host;
                existing.Username = user;
                existing.Token = token;
            }
            else
            {
                _credentials.Add(new GitCredential { Host = host, Username = user, Token = token });
            }

            RefreshCredList();
            ShowResult($"√ 已保存 {host} 的推送凭据（点【确定】后生效）。", true);
        }

        /// <summary>删除选中的凭据。</summary>
        private void BtnCredDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ListCred.SelectedItem is not GitCredential cred)
            {
                ShowResult("× 请先在上方列表中选中要删除的凭据。", false);
                return;
            }

            _credentials.Remove(cred);
            RefreshCredList();
            TxtCredHost.Text = string.Empty;
            TxtCredUser.Text = string.Empty;
            TxtCredToken.Password = string.Empty;
            ShowResult($"已删除 {cred.Host} 的凭据（点【确定】后生效）。", null);
        }

        /// <summary>把操作结果写入 TxtResult（true 成功 / false 失败 / null 中性提示）。</summary>
        private void ShowResult(string message, bool? ok)
        {
            TxtResult.Text = message;
            TxtResult.Foreground = ok == true ? Brushes.SeaGreen
                : ok == false ? Brushes.Firebrick
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        }

        private GitSourceMode CurrentMode => RbAuto.IsChecked == true
            ? GitSourceMode.Auto
            : RbEmbedded.IsChecked == true
                ? GitSourceMode.Embedded
                : RbSystem.IsChecked == true
                    ? GitSourceMode.System
                    : GitSourceMode.Custom;

        /// <summary>切换模式时同步自定义路径输入区可用状态，并立即重新检测。</summary>
        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            UpdateEnabled();
            Test();
        }

        private void UpdateEnabled()
        {
            bool custom = RbCustom.IsChecked == true;
            TxtCustomPath.IsEnabled = custom;
            BtnBrowse.IsEnabled = custom;
            if (custom && !_suppress) TxtCustomPath.Focus();
        }

        /// <summary>浏览选择 git.exe 文件。</summary>
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 git.exe（便携版 Git 解压后位于其 cmd\\ 或 bin\\ 目录）",
                Filter = "git.exe|git.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                TxtCustomPath.Text = dlg.FileName;
                Test();
            }
        }

        /// <summary>测试当前所选配置能否找到并执行 git。</summary>
        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            Test();
        }

        /// <summary>按当前窗口选择实时解析并显示结果。</summary>
        private void Test()
        {
            var mode = CurrentMode;
            string custom = TxtCustomPath.Text.Trim();

            if (mode == GitSourceMode.Custom && string.IsNullOrWhiteSpace(custom))
            {
                ShowResult("× 请先填写 git.exe 路径或 Git 根目录。", false);
                return;
            }

            string? exe = GitLocator.ResolvePreview(mode, custom, out string description);
            ShowResult((exe != null ? "√ " : "× ") + description, exe != null);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedSource = CurrentMode;
            SelectedCustomPath = TxtCustomPath.Text.Trim();

            if (SelectedSource == GitSourceMode.Custom)
            {
                if (string.IsNullOrWhiteSpace(SelectedCustomPath))
                {
                    MessageBox.Show(this, "请填写 git.exe 路径或 Git 根目录。", "Git 环境设置",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 路径无效时给出确认，避免静默保存一个用不了的配置
                if (GitLocator.ResolveCustomPath(SelectedCustomPath) == null)
                {
                    var choice = MessageBox.Show(this,
                        "指定路径中未找到可用的 git.exe，仍保存该设置吗？",
                        "Git 环境设置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (choice != MessageBoxResult.Yes) return;
                }
            }

            // 凭据（账号）写回设置对象（调用方在点确定后统一保存；点取消不会落盘）
            _settings.Credentials = _credentials;

            DialogResult = true;
        }
    }
}
