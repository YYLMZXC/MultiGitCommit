using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;

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
    private const int StoragePermissionRequestCode = 1001;

    /// <summary>旧版运行时权限只申请一次，避免用户拒绝后每次回到应用都反复弹框。</summary>
    private static bool _legacyPermissionRequested;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            // 安卓系统字体里没有 XAML 字体链声明的那些字体（微软雅黑 / PingFang / Source Han），
            // 且 Avalonia 11 的安卓字体管理器对 CJK 字形回退不可靠，中文会显示成方块。
            // 打包了 Noto Sans CJK SC（SIL OFL 开源许可，见 Fonts/LICENSE）作为默认字体兜底：
            // XAML 字体链全部匹配失败时最终落到这里，中文正常渲染。
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://MultiGitCommit/Fonts/NotoSansCJKsc-Regular.otf#Noto Sans CJK SC"
            })
            .AfterSetup(_ =>
            {
                // 移动端暂无额外平台服务注册；SAF 目录 URI → 本地路径、
                // 存储权限状态与设置页跳转统一放在 AndroidGitPlatform（全 TFM 可调用）。
            });
    }

    protected override void OnResume()
    {
        base.OnResume();
        RequestStoragePermissionsIfNeeded();
    }

    /// <summary>
    /// 启动时申请存储权限（git 需要直接读写仓库目录的本地文件）：
    /// - Android 10 及以下（API ≤ 29）：READ/WRITE_EXTERNAL_STORAGE 是普通运行时权限，这里直接弹系统授权框；
    /// - Android 11+（API ≥ 30）：「所有文件访问」（MANAGE_EXTERNAL_STORAGE）没有运行时弹框，
    ///   只能在应用内说明后引导到系统设置页（见 MainView 的启动引导与
    ///   AndroidGitPlatform.OpenStoragePermissionSettings），避免一启动就把用户踢出应用。
    /// 从设置页返回会再次 OnResume，权限状态由 AndroidGitPlatform.IsAllFilesAccessGranted 实时复查。
    /// </summary>
    private void RequestStoragePermissionsIfNeeded()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) return;
        if (_legacyPermissionRequested) return;

        bool readGranted = CheckSelfPermission(global::Android.Manifest.Permission.ReadExternalStorage)
                           == Permission.Granted;
        bool writeGranted = CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage)
                            == Permission.Granted;
        if (readGranted && writeGranted) return;

        _legacyPermissionRequested = true;
        RequestPermissions(
            new[] { global::Android.Manifest.Permission.ReadExternalStorage, global::Android.Manifest.Permission.WriteExternalStorage },
            StoragePermissionRequestCode);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // 不缓存结果：用户可能在系统设置里改过权限，UI 侧一律以实时检查为准
    }
}
