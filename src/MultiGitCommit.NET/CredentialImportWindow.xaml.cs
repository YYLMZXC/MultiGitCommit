using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace MultiGitCommit
{
    /// <summary>
    /// 系统凭据导入窗口：列出自动检测到的账号，用户勾选后导入到【推送凭据】列表。
    /// 令牌不在列表中展示，导入后可在设置窗口里选中该条查看/修改。
    /// </summary>
    public partial class CredentialImportWindow : Window
    {
        /// <summary>列表数据（含勾选状态）。</summary>
        private readonly List<CredentialImportItem> _items;

        /// <summary>用户勾选并确认导入的凭据。</summary>
        public IReadOnlyList<DetectedCredential> SelectedCredentials =>
            _items.Where(i => i.Selected).Select(i => i.Credential).ToList();

        public CredentialImportWindow(IEnumerable<DetectedCredential> detected)
        {
            _items = detected
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Host))
                .Select(c => new CredentialImportItem { Credential = c, Selected = true })
                .ToList();

            InitializeComponent();
            ListCred.ItemsSource = _items;
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

        private void SetAll(bool selected)
        {
            foreach (var item in _items) item.Selected = selected;
            // 无通知机制，直接刷新绑定
            ListCred.ItemsSource = null;
            ListCred.ItemsSource = _items;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCredentials.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一条凭据。", "导入系统凭据",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }
    }
}
