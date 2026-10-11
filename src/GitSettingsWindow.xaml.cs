using System;
using System.Windows;
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

        public GitSettingsWindow(AppSettings current)
        {
            InitializeComponent();

            RbAuto.IsChecked = current.GitSource == GitSourceMode.Auto;
            RbSystem.IsChecked = current.GitSource == GitSourceMode.System;
            RbCustom.IsChecked = current.GitSource == GitSourceMode.Custom;
            TxtCustomPath.Text = current.CustomGitPath ?? string.Empty;

            UpdateEnabled();
            Test(); // 打开窗口即显示当前配置的检测结果
        }

        private GitSourceMode CurrentMode => RbAuto.IsChecked == true
            ? GitSourceMode.Auto
            : RbSystem.IsChecked == true ? GitSourceMode.System : GitSourceMode.Custom;

        /// <summary>切换模式时同步自定义路径输入区可用状态。</summary>
        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            UpdateEnabled();
        }

        private void UpdateEnabled()
        {
            bool custom = RbCustom.IsChecked == true;
            TxtCustomPath.IsEnabled = custom;
            BtnBrowse.IsEnabled = custom;
            if (custom) TxtCustomPath.Focus();
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
                TxtResult.Text = "× 请先填写 git.exe 路径或 Git 根目录。";
                TxtResult.Foreground = System.Windows.Media.Brushes.Firebrick;
                return;
            }

            string? exe = GitLocator.ResolvePreview(mode, custom, out string description);
            TxtResult.Text = (exe != null ? "√ " : "× ") + description;
            TxtResult.Foreground = exe != null
                ? System.Windows.Media.Brushes.SeaGreen
                : System.Windows.Media.Brushes.Firebrick;
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

            DialogResult = true;
        }
    }
}
