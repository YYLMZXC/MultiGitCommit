# MultiGitCommit（本仓库主项目）项目独立经验

> 核验日期：2026-11-13。依据 `README.md`、`src/MultiGitCommit.Avalonia/MultiGitCommit.csproj` 及源码内命名检索。
> 这些内容依赖本项目自身的结构与业务，换项目后不直接适用；通用机制见 [`../reusable/`](../reusable/)。

## 1. 项目现状（会随升级变化）

- 主项目 `src/MultiGitCommit.Avalonia`：单 csproj 多目标 `net10.0;net10.0-android;net10.0-ios`，Avalonia 11.3.11，`ApplicationId=com.yylmzxc.multigitcommit`；桌面 `WinExe`，移动端 `Exe`。
- 桌面后端包仅 `net10.0`，Android/iOS 后端包仅对应 TFM；`Platforms/**` 先 `Compile Remove`，再按 TFM `Include`。
- `RuntimeIdentifiers` 按 TFM 分别声明；Android `SupportedOSPlatformVersion=23`（.NET 10 要求 ≥23，见 reusable 文档）。`Project` 上带 `TreatAsLocalProperty="RuntimeIdentifier"`，并有条件 PropertyGroup 把泄漏到 android/ios TFM 的桌面 RID 清零（否则 VS 发布报 `NU1102` 找 `Microsoft.NETCore.App.Runtime.Mono.win-x64`）。发布配置在 `Properties/PublishProfiles/`（`FolderProfile.pubxml` = win-x64、`FolderProfile-osx-arm64.pubxml`），均固定 `TargetFramework=net10.0`；命令行加载 profile 仍须带 `-f net10.0`（多目标否则 `NETSDK1129`）。
- 版本号：`Version=$(BuildDate)`（`yy.MM.dd`，纯数字）、`InformationalVersion=$(BuildDate)-$(BuildTime)`（`dHHmm`）、`ApplicationVersion=$(BuildTime)`（Android versionCode / iOS CFBundleVersion）、`CFBundleShortVersionString=$(BuildDate)`；`ApplicationDisplayVersion` 跟随 `$(Version)`——移动端 SDK 会用 `ApplicationDisplayVersion` 覆盖 `Version`，两者分叉会让各 TFM 的 `PackageVersion` 不一致（NU1105，Visual Studio 还原必失败）。`IncludeSourceRevisionInInformationalVersion=false`；`NoWarn` 含 `AVLN3001`（窗口均带参构造，设计器加载器用不到）。
- Release 桌面才启用 `PublishSingleFile` + `SelfContained=false`，默认 RID 随构建主机；内嵌 git 目录用 `ExcludeFromSingleFile=true` 保持为 exe 同级 `git/` 文件夹。
- 安卓中文字体 `Fonts/NotoSansCJKsc-Regular.otf`（SIL OFL，许可文件 `Fonts/LICENSE`）**仅 android TFM** 作为 `AvaloniaResource` 嵌入；iOS 用系统 PingFang SC，不内置字体以免 ipa 增大。
- 归档实现：`src/old-version/`（WPF 版 `MultiGitCommit.NET`、旧 macOS 版 `MultiGitCommit.MACOS`），不再更新。

## 2. 架构与分层（本项目约定）

- 依赖方向单一：`View → AppSession → Services → IGitCliRunner`；静态默认值只在组合根 `Settings/AppServices.cs` 出现一次。
- 视图只做参数收集、确认与展示；不拼 git 命令、不判断操作系统、不读写配置、不直接访问 `GitLocator` 等全局状态。
- 唯一进程执行实现 `Services/GitCliRunner.cs`：UTF-8、非交互、超时杀进程树、把 git 所在目录及 `mingw64/bin`、`usr/bin`（Windows）或 `bin`、`libexec/git-core`（macOS/Linux）注入子进程 PATH。日志呈现由 `GitCommandReporter` 负责。
- 命令与规则各一份实现：`GitCloneArguments` 同供预览与执行、`GitRefName` 同供主界面与克隆对话框、`GitStatusText` 同供拉取与历史。
- 模型只表示数据：`ProjectConfig` 的读写交给 `IProjectConfigStore`，`AppSettings` 的访问交给 `ISettingsProvider`。
- 生命周期收口：视图离开可视树 / 窗口关闭时取消后台 git 任务并退订事件；git 环境切换时由环境服务让凭据助手缓存失效。
- 桌面 `MainWindow` 只是薄壳（标题 / 图标 / 关闭时调 `MainView.Shutdown()`），主逻辑全在 `MainView`；9 个对话框均为 `XxxView`（About / Clone / CredentialDetect / GitSettings / History / Identity / Main / Pull / StageFiles），`IdentityView` 的结果属性是 `SelectedIdentity`。
- 对话框统一基础设施：`UiDialogs.cs` 的 `DialogViewBase` / `DialogView<TResult>`（`Close(result)` 回传）+ `UiDialogs.ShowAsync(anchor, view, title)`；`Dialogs.cs` 的 MessageBox / Prompt 也走同一宿主。`StorageProvider`、浏览器打开统一走 `TopLevel`。

## 3. 业务规则（换项目即失效）

- 提交消息固定 `yyyy-MM-dd HH:mm:ss 用户备注`；备注为空时仅时间戳；工作区无变更跳过 `git commit`，【强制提交】用 `--allow-empty`。
- 每仓库远端列表 / 复选框状态存仓库根目录 `multigitcommit.config.json`；全局设置 `settings.json` 字段三平台一致，可直接互相拷贝。
- 启动定位仓库：默认只读取当前目录（向上最多 12 层找 `.git` 或 `multigitcommit.config.json`），找不到不自动打开上次项目、提示手选；【Git 环境】可改为「上次使用的仓库优先」或「当前目录优先，找不到时回退上次项目」（`StartupRepo`：`0`/`1`/`2`）。
- git 来源四选一：自动检测（内嵌 → 常见安装位置 → PATH）/ 仅内嵌 / 仅系统 PATH / 指定路径；切换来源时凭据助手缓存失效。
- 凭据来源四选一：自动检测（自定义优先）/ 仅系统 / 仅自定义 / 不使用；账号实测使用一次 `git ls-remote`；`credential.helper` 不可用时临时 `-c credential.helper=xxx` 覆盖，不改用户配置。
- 超时档位：快速命令 20 秒、普通 git 命令 10 分钟、克隆 30 分钟；手动命令 Windows 走 `cmd.exe /c chcp 65001`，macOS/Linux 走登录 shell `/bin/bash -lc`。
- 打开文件：Windows `ShellExecute` + `explorer /select`，macOS `open` / `open -R`，Linux `xdg-open` / `gio open`；已删除文件打开所在目录。

## 4. 移动端适配与已知限制

- 主视图：`MainView` 根布局用 `ScrollViewer` 包内容，`DialogOverlay` 在 `ScrollViewer` 外盖满视口；宽度 < 700（`NarrowLayoutThreshold`）时 `ApplyResponsiveLayout` 把仓库目录行 7 个按钮搬到 `RepoHeaderNarrow` 的 `WrapPanel`（`_repoHeaderButtonColumns` 记录原 Grid 列号），远端 / 主按钮 / 日志底部行改 `WrapPanel`，长标题 `TextWrapping=Wrap`。
- 对话框：`DialogOverlay.PushAsync` 把视图 `Width` 重置为 `NaN`（桌面固定宽度不适用），卡片 `ScrollViewer` 横向 `Auto`。
- `GitSettingsView`：长文案 `RadioButton` 内容嵌 `TextBlock TextWrapping=Wrap`；凭据输入 Grid 加第 2 行，宽度 < 420 时把 `TxtCredToken` 移到第 2 行横跨 3 列（列宽 150/110 → Star/Star），恢复时还原；按钮行 `WrapPanel`；`CmbPlatform MinWidth 150→100`。
- `CredentialDetectView`：按钮行 `WrapPanel`，两个 `ListBox` 列模板改比例列（`40,*,2*` / `40,*,*,1.2*`）+ `TextTrimming`。
- `AppSettings` 移动端目录用 `LocalApplicationData`；Android 清单配 `MANAGE_EXTERNAL_STORAGE`。
- iOS 必须显式入口 `Platforms/iOS/Main.cs`；`GitCliRunner.RunAsync` 在 iOS 顶部直接返回失败结果。
- **已知限制**：便携 Git 无 Android/iOS 版本。Android 需在【Git 环境】手动指定可用的 git 路径；iOS git 功能不可用。
