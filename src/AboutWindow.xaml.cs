using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace MultiGitCommit
{
    /// <summary>
    /// “关于”窗口：展示软件名称、版本及项目开源地址。
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
        }

        /// <summary>点击项目地址超链接时，使用系统默认浏览器打开。</summary>
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Uri?.OriginalString))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.OriginalString,
                    UseShellExecute = true
                });
            }
            e.Handled = true;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
