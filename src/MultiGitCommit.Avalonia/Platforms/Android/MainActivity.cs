using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace MultiGitCommit;

/// <summary>
/// Android 入口 Activity：Avalonia 单项目模式（Platforms/Android 目录约定）。
/// 移动端没有桌面窗口，App 以 MainView 为单视图根（见 App.axaml.cs）。
/// </summary>
[Activity(
    Label = "MultiGitCommit",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    /// <summary>是否已尝试引导「所有文件访问」授权（每个进程只提示一次，避免反复跳出设置页）。</summary>
    private static bool _storageAccessRequested;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .AfterSetup(_ =>
            {
                // 移动端暂时不需要额外的平台服务注册；
                // 后续需要 Android 特有能力（如 SAF 封装）时在这里挂接。
            });
    }

    protected override void OnResume()
    {
        base.OnResume();
        TryRequestAllFilesAccess();
    }

    /// <summary>
    /// Android 11+ 读写共享存储（/storage/emulated/0 下的仓库目录）需要
    /// 「所有文件访问」特殊权限（MANAGE_EXTERNAL_STORAGE），普通权限申请无效，
    /// 只能跳转系统设置页由用户手动授权。未授权时打开仓库会得到权限错误。
    /// </summary>
    private void TryRequestAllFilesAccess()
    {
        if (_storageAccessRequested) return;
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return;
        if (Android.OS.Environment.IsExternalStorageManager) return;

        _storageAccessRequested = true;
        try
        {
            var intent = new Intent(
                Android.Provider.Settings.ActionManageAllFilesAccessPermission,
                Android.Net.Uri.Parse("package:" + PackageName));
            StartActivity(intent);
        }
        catch (Exception)
        {
            // 部分定制 ROM 没有该设置页，忽略（用户也可手动到设置里授权）
        }
    }
}
