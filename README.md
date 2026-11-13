# MultiGitCommit

个人自用的 Git 一键提交推送工具。

**当前主项目：`src/MultiGitCommit.Avalonia`** —— 一套代码同时支持 Windows、macOS 与 Linux（C# + .NET 10 + Avalonia 11），
功能与配置文件三个平台**完全通用**（每仓库的 `multigitcommit.config.json`、全局 `settings.json` 字段一致，可直接互相拷贝）。

选择本地 Git 仓库后，自动加载该仓库专属的远端列表，一键完成 **提交 + 推送到多个远端平台**（Gitee、GitHub、cnb.cool 等），提交信息自动携带本地时间戳。

> 早期平台各自独立的实现已归档到 `src/old-version/`（`MultiGitCommit.NET` WPF 版、`MultiGitCommit.MACOS` 旧 macOS 版），
> 仅供对照参考，不再更新。

## 功能特性

- **克隆项目**：输入远端 URL 一键 `git clone` 到本地，支持指定分支（`--branch`）、递归子模块（`--recursive`）、浅克隆（`--depth 1`）；目录名自动从 URL 推导（手动修改后不再被覆盖），克隆完成后自动切换为该仓库，并可把来源 URL 直接写入推送远端列表，一键完成「克隆 → 提交 → 推送」闭环。
- **多远端推送**：一个 Git 仓库可配置 1 个或多个推送远端地址，推送时逐个执行，单个远端失败不影响其余远端。
- **每个仓库独立配置**：远端列表、复选框状态保存在各自仓库根目录的 `multigitcommit.config.json`，切换仓库自动加载对应项目的配置。
- **启动即自动加载**：打开程序自动定位仓库并载入其 `multigitcommit.config.json`——默认**只读取当前目录**：从当前目录（含程序自身目录，IDE 调试 / 双击 exe 常见）向上逐级（最多 12 层）查找 `.git` 或 `multigitcommit.config.json` 定位真正的仓库根目录；找不到时**不会**自动打开上次项目，而是留空等待手动选择。可在【Git 环境】→「启动时加载的仓库」中改为「上次使用的仓库优先」或「当前目录优先，找不到时回退上次项目」。
- **固定提交格式**：commit 消息固定为 `yyyy-MM-dd HH:mm:ss 用户备注`；备注为空时仅使用本地时间戳。
- **选择性 git add**：勾选【执行 git add】后点击提交，添加方式由【Git 环境】→「执行 git add 时的文件添加方式」决定——**全提交（默认）**直接 `git add .`；**可选提交**会先列出工作区的全部变更（状态 / 路径 / 暂存区状态），由你勾选要添加的文件（默认全选，支持全选 / 全不选 / 只选未暂存），只对勾选项执行 `git add --`，避免把垃圾文件带进提交。
- **动态分支**：自动获取当前分支名推送，不硬编码 main/master。
- **空提交防护**：工作区无变更时自动跳过 `git commit`，不生成空提交；勾选【强制提交】可忽略该防护（`--allow-empty`）。
- **强制推送**：勾选【强制推送】后推送命令追加 `--force`，用于非快进被拒、变基/回退后覆盖远端历史（谨慎使用）。
- **手动命令执行**：可在当前仓库目录执行任意 git / 系统命令，stdout/stderr 全量输出日志（Windows 走 `cmd.exe /c chcp 65001`，macOS / Linux 走登录 shell `/bin/bash -lc`，可拿到 Homebrew / Linuxbrew 等 PATH）。
- **仓库初始化 / 默认分支**：`git init` + 指定默认分支，空仓库直接设置 HEAD 指向；输入仓库子目录时自动定位到真正的仓库根目录。
- **远端快速操作**：远端列表支持添加 / 编辑 / 删除，并可【在浏览器中打开】选中的远端 URL。
- **提交历史与打开文件**：查看当前分支最近 200 条提交，可把本地分支重置到任意提交（soft / mixed / hard，执行前二次确认）；选中提交后列出该提交改动的文件，双击列表项（或按回车 / 点【打开选中文件】）即用系统默认关联程序打开对应文件，【打开所在文件夹】在资源管理器 / Finder / 文件管理器中定位——已删除的文件也能打开其所在目录（Windows 走 ShellExecute + `explorer /select`，macOS 走 `open` / `open -R`，Linux 走 `xdg-open` / `gio open`）。
- **提交身份（user.name / user.email）**：提交的前置条件，界面实时显示当前生效身份；提交前自动校验，缺失时套用已保存身份或弹窗引导填写并写入 git config（可选仓库级 / 全局），未设置则终止提交而不是静默失败（详见下方【提交身份】）。
- **推送凭据（账号）**：像【Git 环境】选择 git 来源一样，账号也可**选择来源**——自动检测（自定义优先，缺失时取系统）/ 仅系统凭据 / 仅自定义 / 不使用；系统账号可按需**检测并导入**（先选主机再检测，可随时停止），填写后可用一次 `git ls-remote` 实测账号是否可用（详见下方【推送凭据】）；账号与令牌在 `settings.json` 中加密保存。
- **凭据助手自动适配**：自动扫描当前 git 真正可用的 `credential.helper`（Windows：manager / wincred / store；macOS：manager / osxkeychain / store；Linux：manager / libsecret / gnome-keyring / store）；配置里的助手在当前 git 中不存在时（精简便携版常见），临时用 `-c credential.helper=xxx` 覆盖，不改动用户 git 配置。
- **非交互执行 + 超时兜底**：不弹窗等待账号/口令，快速命令 20 秒、普通 git 命令 10 分钟、克隆 30 分钟超时即终止，按钮必定恢复（详见下方【非交互执行与超时保护】）。
- **多语言界面**：内置简体中文（默认）与 English，【Git 环境】→「界面语言」切换后立即生效；界面、弹窗提示与运行日志同步翻译，语言选择保存在 `settings.json` 的 `Language` 字段（详见下方【多语言（i18n）】）。
- **数据目录直达**：【Git 环境】设置窗口底部显示数据目录完整路径，点【打开数据目录】即可用系统文件管理器打开（移动端不支持系统文件管理器，仅显示路径供手动查找）。
- **中文不乱码**：进程调用统一 UTF-8 编码。
- **不依赖系统 git**：内置【Git 环境】设置，支持四种来源——自动检测（软件内嵌便携版 Git → 常见安装位置 → 系统 PATH）、仅软件内嵌 Git、仅系统 PATH（环境 git）、指定 git 可执行文件 / Git 目录；配置保存到各自平台的应用设置目录，切换来源无需重装系统 git。

## 环境要求

| 项 | 说明 |
|---|---|
| 系统 | Windows 10 / 11、macOS（Apple Silicon / Intel 均可）或 Linux（x64 / ARM64，需图形桌面环境） |
| 构建 | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |
| 运行时 | 发布为「依赖框架」单文件，目标机器需已安装 .NET 10 运行时 |
| Git | 满足任一即可，**最终用户无需额外安装 git**（见下节） |

> Linux 桌面依赖：Avalonia 11 通过 X11 渲染（Wayland 会话由 XWayland 转发），需要 `libx11`、`libice`、`libsm`、
> `libfontconfig` 等基础库，各发行版一般已随桌面环境安装；无图形桌面 / 纯 SSH 环境无法运行界面。
> 不同发行版请用对应包管理器安装 git（程序默认从 `/usr/bin/git` 等标准位置自动检测）。

Git 的四种取得方式：

- Windows：仓库根目录运行一次 `download-portable-git.ps1`，内嵌便携版 MinGit（推荐，见下节）；
- 系统已安装 Git（Windows 官方安装包 / Homebrew / Xcode 命令行工具 / Linux 发行版包管理器等，自动检测）；
- 把便携版 Git 解压到软件目录的 `git/` 子目录（Windows：`git\cmd\git.exe`；macOS / Linux：`git/bin/git`，macOS 也支持 `.app` 内 `Resources/git`）；
- 在程序内点击【Git 环境】按钮指定任意 git 可执行文件 / Git 目录。

Linux 上安装 git（任选其一）：

```bash
sudo apt install git        # Debian / Ubuntu
sudo dnf install git        # Fedora / RHEL
sudo pacman -S git          # Arch
brew install git            # Linuxbrew
```

## 内嵌便携版 Git（免安装分发）

仓库根目录提供 `download-portable-git.ps1`，一键下载官方 **MinGit**（Git for Windows 免安装精简版，约 45MB）并解压到
`src\MultiGitCommit.Avalonia\git\`：

```powershell
# 依次尝试华为云 / npmmirror / GitHub 三个镜像，可指定版本，也可自定义目标目录
.\download-portable-git.ps1
.\download-portable-git.ps1 -Version 2.50.1
.\download-portable-git.ps1 -Destination "D:\some\other\git"
```

之后 `dotnet build` / `dotnet publish` 会自动把该目录复制到输出目录的 `git/` 子目录（与 exe / .app / Linux 可执行文件同级）。
程序启动时 `GitLocator` 按 **软件内嵌 Git → 常见安装位置（Windows：Program Files / scoop / winget；macOS：Homebrew / Xcode / MacPorts / Nix；Linux：~/.local / Linuxbrew / snap / Nix / 发行版标准目录）→ 系统 PATH** 的优先级自动检测，
最终用户机器上无需安装任何 git；ssh 推送所需的附带程序（`ssh` / `ssh.exe` 等）也已随包提供。

> 该目录由脚本生成、体积较大，已在 `.gitignore` 中忽略（早期误入库的文件不影响构建）；
> 分发时将 publish 输出整个目录打包即可。

> **macOS / Linux 的内嵌 Git**：该脚本只下载 Windows 版 MinGit。macOS / Linux 若要做成免安装分发，
> 可把对应平台的可重定位 git 目录放到 `git/bin/git`（Linux 需要与目标发行版 glibc 兼容的构建；
> 多数发行版直接使用系统包管理器安装的 git 更省事，程序会自动检测）。

## 构建与运行

```powershell
cd src\MultiGitCommit.Avalonia
dotnet build
dotnet run            # 直接运行当前平台
```

Debug 产物：

- Windows：`src\MultiGitCommit.Avalonia\bin\Debug\net10.0\MultiGitCommit.exe`
- macOS / Linux：`src\MultiGitCommit.Avalonia/bin/Debug/net10.0/MultiGitCommit`（或 `dotnet MultiGitCommit.dll`）

> 默认构建（含 Rider「构建解决方案」）只编桌面 `net10.0`。Android / iOS 目标仍会被 IDE 识别，
> 但只有显式用 `-f net10.0-android` / `-f net10.0-ios`（或 `-p:IncludeMobile=true`）时才参与构建，
> 详见下文「移动端构建」。

JetBrains Rider 可直接使用 `src/MultiGitCommit.Avalonia/.run/` 下的共享运行配置：
`MultiGitCommit (Windows Debug)`、`MultiGitCommit (Windows Release win-x64)`、`MultiGitCommit (macOS Debug)`、`MultiGitCommit (Linux Debug)`。
其中 Release 配置需先在 Rider 中把解决方案配置切到 `Release`。

### 版本号与还原

版本号按构建时间自动生成，无需手改任何文件：

| 属性 | 取值示例 | 用途 |
|---|---|---|
| `Version` | `26.11.13`（日期 `yy.MM.dd`） | 程序集版本 / NuGet 包版本，**必须纯数字且三个 TFM 完全一致** |
| `InformationalVersion` | `26.11.13-1311`（日期 + 构建时刻 `dHHmm`） | 文件属性「产品版本」；界面标题只取日期部分（`AppVersion`） |
| `ApplicationVersion` | `131105`（`dHHmm`：日 + 时 + 分） | Android `versionCode` / iOS `CFBundleVersion` |

`Version` 必须保持纯数字日期：Android / iOS SDK 有一条「`ApplicationDisplayVersion` 能解析成 `System.Version` 时就用它覆盖 `Version`」的规则，
桌面侧若带 `-HHmm` 后缀，三个 TFM 的 `PackageVersion` 就不一致，NuGet 报 `NU1105`「属性 PackageVersion 应在所有目标框架中具有单个值」并直接拒绝还原。
需要固定版本时传纯数字版本：

```powershell
dotnet build -p:Version=26.11.03
```

> **Visual Studio 还原失败**（报 `NETSDK1005`「资产文件 … 没有 net10.0 的目标」）：说明还原被 NuGet 拒绝，
> 失败结果连同日志缓存在 `obj\project.assets.json` / `obj\project.nuget.cache` 里。
> 删除 `src\MultiGitCommit.Avalonia\obj` 后重新【还原 NuGet 包】即可；命令行 `dotnet restore` 与 Rider 使用较宽容的 NuGet 实现，通常不会暴露这类不一致。

### 发布（单文件，依赖框架）

Release 默认发布为 **单文件可执行程序**（Avalonia、Skia 等依赖全部打进主程序），输出目录只有主程序（Windows：`MultiGitCommit.exe`；macOS / Linux：`MultiGitCommit`）+ `git/` 目录：

```bash
cd src/MultiGitCommit.Avalonia

# Windows（默认 RID 为 win-x64）
dotnet publish -c Release -f net10.0 -r win-x64

# macOS Apple 芯片 / Intel
dotnet publish -c Release -f net10.0 -r osx-arm64
dotnet publish -c Release -f net10.0 -r osx-x64

# Linux x64 / ARM64
dotnet publish -c Release -f net10.0 -r linux-x64
dotnet publish -c Release -f net10.0 -r linux-arm64

# 或直接用仓库自带的发布配置（等价于第一条，输出到 bin\Release\net10.0\publish\win-x64\）
dotnet publish -c Release -f net10.0 -p:PublishProfile=FolderProfile
```

> **必须带 `-f net10.0`**：项目是多目标（桌面 + Android + iOS），不指定框架时 `Publish` 目标直接报 `NETSDK1129`。
>
> **Visual Studio 发布**：在【发布】里选择仓库自带的 `FolderProfile`（win-x64）或 `FolderProfile-osx-arm64` 配置文件，
> 它们已固定 `TargetFramework=net10.0`，不会把桌面目标/框架混进来。若自行新建配置文件，务必把「目标框架」选成 `net10.0`——
> VS 若在未固定框架的情况下带上 `RuntimeIdentifier=win-x64`，该 RID 会泄漏到 Android / iOS 的还原图，
> NuGet 会去找并不存在的 `Microsoft.NETCore.App.Runtime.Mono.win-x64` 并报 `NU1102`。
> csproj 已用 `TreatAsLocalProperty="RuntimeIdentifier"` + 条件复位把泄漏到移动 TFM 的桌面 RID 归位到 `android-arm64` / 清空，但不要依赖这层兜底。

> 目标机器需已安装 .NET 10 运行时（非自包含）。需要完全独立分发时，追加 `-p:SelfContained=true` 即可（体积会大很多）。
>
> Linux 产物为可执行文件 `MultiGitCommit`（publish 已带执行位，若经压缩包 / U 盘拷贝丢失权限，执行 `chmod +x MultiGitCommit` 即可），
> 与同目录的 `git/` 子目录一起分发；桌面图标可用任意 `.desktop` 文件指向该可执行文件。

### 移动端构建

移动端默认不参与「构建解决方案」，用 `-f` 单独构建即可；需要一次构建全部三个目标时加 `-p:IncludeMobile=true`：

```powershell
# Android APK：默认只出 arm64（真机），产物 bin\Release\net10.0-android\android-arm64\*.apk
dotnet build MultiGitCommit.csproj -f net10.0-android -c Release

# x64 模拟器包：显式指定 RID
dotnet build MultiGitCommit.csproj -f net10.0-android -c Release -r android-x64

# iOS（需在 macOS 上构建）
dotnet build MultiGitCommit.csproj -f net10.0-ios -c Release

# 一次构建桌面 + Android + iOS（移动端环境需齐备）
dotnet build MultiGitCommit.csproj -c Release -p:IncludeMobile=true
```

> IDE 里构建移动端：TargetFrameworks 完整声明了三个目标，Rider / VS 能识别并单独选择对应 TFM；
> 默认的「构建解决方案」只派发桌面内层构建，不会因本机缺 Android SDK / iOS 预配而失败。
>
> **iOS**：Debug 默认面向模拟器（`iossimulator-arm64`），本机无需证书与预配文件即可编译 / 调试；
> Release 才打包真机 `ios-arm64`。调试真机时用 `-p:RuntimeIdentifiers=ios-arm64` 覆盖
> （必须是复数 `RuntimeIdentifiers`：iOS SDK 在单数/复数同时设置时会清空单数，导致回落到真机并要求预配）。
>
> **Rider 报「部署失败 / Backend did not return application bundle path」**：Rider 的 iOS 运行配置是通过命令行
> 把单数 `RuntimeIdentifier=iossimulator-arm64` 传进来的；iOS SDK 在单数、复数同时非空时会清空单数
> （`Xamarin.Shared.Sdk.DefaultItems.targets` 的 Architecture 段），而本项目 `TreatAsLocalProperty` 让该清空生效，
> 于是构建产物记录里 rid 变成空串，`XamarinIOSDeploymentProvider` 按 (tfm, rid) 取 `AppBundleDir` 只能拿到 null。
> 因此 csproj 里 iOS 的 `RuntimeIdentifiers` 默认值都带 `'$(RuntimeIdentifier)' == ''` 条件（命令行带 RID 时以命令行为准），**勿删**。
>
> **iOS 真机签名（Release 失败的常见原因）**：真机包必须签名，缺少描述文件时报
> `在 iOS 上找不到 <应用名> 的任何可用预配配置文件`（`Xamarin.Shared.targets`）。
> 本机需先在 Xcode → Settings → Accounts 登录 Apple ID，为 `com.yylmzxc.multigitcommit` 生成并安装
> Development 描述文件（双击安装到 `~/Library/MobileDevice/Provisioning Profiles`），再显式指定证书与描述文件：
>
> ```bash
> dotnet build MultiGitCommit.csproj -f net10.0-ios -c Release \
>   -p:CodesignKey="Apple Development: <账号> (<ID>)" \
>   -p:CodesignProvision="<描述文件名>"
> ```
>
> 不想签名只想验证 Release 逻辑时，把 RID 换成模拟器：
> `dotnet build -f net10.0-ios -c Release -p:RuntimeIdentifiers=iossimulator-arm64`。
>
> **iOS 清单**：`Platforms/iOS/Info.plist` 必须以 `None` + `LogicalName="Info.plist"` 声明（SDK 不识别 `<InfoPlist>` 属性，
> 文件不在项目根时也探测不到），否则方向、`UIDeviceFamily`、`MinimumOSVersion` 会被静默丢弃；
> 最低系统版本由 csproj 的 `SupportedOSPlatformVersion` 决定，版本号不要再写进 plist（会覆盖自动生成的日期版本）。
> 另外 plist 必须声明 `UILaunchScreen`（空 dict 即可）：缺失时 iOS 把 App 当旧应用跑兼容模式，上下留大黑边无法全屏。
>
> **Android**：.NET 10 要求 JDK 17。若系统只装了旧 JDK（如 11），SDK / IDE 会误选并报 `AMM0000`；
> csproj 一旦发现 `~/Library/Developer/Xamarin/jdk` 存在就强制使用它（`JavaSdkDirectory` 已设为项目内可覆盖，IDE 传入的旧路径不会生效）。
> 缺 Android SDK 平台时可执行：
> `dotnet build -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True -p:AndroidSdkDirectory=<SDK 路径> -p:JavaSdkDirectory=~/Library/Developer/Xamarin/jdk`。

> 命令里显式写 `MultiGitCommit.csproj`：该目录下有 `MultiGitCommit.slnx`，`dotnet build` 会优先按解决方案解析，
> 带 `-r` 时直接报 `NETSDK1134`（解决方案不支持指定 RID）。

> Android 的 `RuntimeIdentifiers` **只保留单个 RID**（`android-arm64`），要 x64 时用 `-r android-x64`。
> 不要同时声明多个 Android RID：Android SDK 36.1.118 走「每 RID 内层构建」时，资源设计器程序集
> `_Microsoft.Android.Resource.Designer.dll` 缺少 `RuntimeIdentifier` 元数据，`_ResolveAssemblies` 会抛
> `XAPRAS7009: Assembly ... doesn't have the required RuntimeIdentifier metadata`。
> Avalonia.Android 强制要求 `AndroidUseDesignerAssembly=true`（关掉会报 `XA1034`），所以只能靠单 RID 规避。

## 使用说明

1. 本地还没有仓库？点击【克隆项目】，填写远端 URL、本地父目录与目录名（目录名默认按 URL 自动推导），可选指定分支 / 递归子模块 / 浅克隆，确认后自动克隆并切换到该仓库。
2. 点击【选择文件夹】，选择任意 Git 仓库根目录（与 `.git` 同级）；输入子目录时会自动回填真正的仓库根目录。
3. 在【推送远端地址管理】中添加远端 URL（支持 ssh/https 格式，如 `git@gitee.com:user/repo.git`、`https://github.com/user/repo.git`），可添加多个，也可【在浏览器中打开】查看。
4. 填写提交备注（可选），按需勾选 `git add`（勾选后提交前会弹窗列出工作区变更，自己勾选要添加的文件）、`--tags` 推送标签、【强制提交 `--allow-empty`】与【强制推送 `--force`】。
5. 点击【执行提交并推送全部远端】，日志区实时查看每一步命令与结果（执行期间按钮显示「执行中…」）。
6. 想用英文界面：点击【Git 环境】→「界面语言」选择 `English` →【确定】，界面文案立即切换；已输出的日志属于历史记录，保留其原语言。

> **Android 权限与文件夹选择**：启动时申请存储权限——Android 10 及以下弹系统运行时权限框（READ/WRITE_EXTERNAL_STORAGE）；Android 11+ 的「所有文件访问」（MANAGE_EXTERNAL_STORAGE）没有运行时弹框，应用内会先说明用途再引导到系统设置页授权，可跳过（跳过时只能用应用专属外部目录）。跳转依次尝试「本应用的开关页 / 全局列表页 / 应用详情页」，若设备都不支持会明确提示手动路径（设置 → 应用 → MultiGitCommit → 权限 → 所有文件访问）；之后遇到「目录不可写」时仍可再次前往设置。
> 【选择文件夹】/【浏览...】在安卓走系统 SAF 文档选择器，应用会把「内部存储 / SD 卡」返回的 `content://` 树 URI 映射成 git 可用的真实路径（如 `/storage/emulated/0/MultiGitCommit`），并做可写校验；未授权共享存储或选到云端盘 / 下载内容等无真实路径的位置时，会提示并回退到手输绝对路径（可自动新建目录）。远端请用 https。
>
> **安卓默认仓库文件夹**：启动时在共享存储创建一个独立目录 `/storage/emulated/0/MultiGitCommit`（文件管理器可见、方便传文件，需「所有文件访问」权限；未授权时自动回退到应用专属外部目录 `Android/data/<包名>/files/MultiGitCommit`）。该目录只作**父目录**，本身不是 Git 仓库、不会自动 `git init`；【克隆项目】【选择文件夹】【初始化仓库】默认都以它为起点，克隆 / 初始化的仓库是它的子目录，仓库管理首页也会自动扫描到其中的仓库。主界面仓库输入框为空时以水印显示该路径，不会把它误当成仓库去校验。
>
> **iOS 默认仓库文件夹**：iOS 沙盒不允许访问外部目录，系统文件选择器也拿不到可用真实路径，所以启动时在应用沙盒内创建 `Documents/MultiGitCommit` 作为唯一默认仓库父目录（行为与安卓一致：只作父目录、克隆 / 手输路径 / 仓库管理首页扫描都以它为起点，输入框空时以水印显示）。plist 声明了 `UIFileSharingEnabled` + `LSSupportsOpeningDocumentsInPlace`，可在系统「文件」App →「我的 iPhone」→ MultiGitCommit 里查看该目录并拷入 / 拷出现成仓库。相关实现统一收在 `DefaultFolders.cs`（安卓仍走 `AndroidGitPlatform`，iOS 走沙盒 Documents）。
>
> **iOS 内嵌 git（libgit2）**：iOS 沙盒禁止 fork/exec 子进程，安卓那套「打包 git 可执行文件」在 iOS 不可行。
> git 能力改为**进程内实现**：libgit2 1.9.1 编成静态库链接进 App（`native-git/ios/`，复现脚本 `scripts/BuildIosLibgit2.sh`），
> C# 侧 `Services/IosLibgit2/IosGitRunner.cs` 实现 `IGitCliRunner`，把 git 命令行字符串逐条翻译成
> libgit2 调用（经 `native-git/ios/mgc_shim.c` 适配层，输出格式与退出码严格对齐 git CLI，上层解析逻辑零改动）。
> 限制与约定：
> - 只支持 **https** 远端（SecureTransport 走系统信任链；未编译 libssh2，ssh 不可用），凭据沿用「注入 URL」方案；
> - iOS 无系统 git 凭据助手，`credential fill` 恒失败，请直接在【Git 环境】配置账号；
> - `pull --rebase` 暂不支持（会明确报错），请用合并策略；`--depth` 浅克隆被忽略（libgit2 不支持）；
> - 设置页的 git 路径显示伪路径 `__libgit2__`（`GitLocator` 在 iOS 固定返回），Git 来源模式选择对 iOS 无效。

## 非交互执行与超时保护

GUI 环境下 git / ssh 若要「交互式」等待输入（远端账号密码、ssh 口令、主机密钥 yes/no 确认、GPG 签名口令），
进程会永久阻塞——表现为【执行提交并推送全部远端】按钮一直灰着不恢复。为此本工具做两件事：

1. **非交互执行**：子进程 stdin 立即关闭（读到 EOF），并设置 `GIT_TERMINAL_PROMPT=0`、
   `GIT_SSH_COMMAND="<ssh> -o BatchMode=yes -o StrictHostKeyChecking=accept-new"`（仅在能找到 ssh 时设置，
   否则保留 git 自身的 ssh 查找逻辑）。需要交互时 git 直接**报错失败**并在日志里给出原因，而不是挂起。
   凭据请提前配置好：https 用【推送凭据】里保存的用户名 + 访问令牌（见下节），ssh 用已加载的密钥（`ssh-add`）。
2. **超时兜底**：快速校验类命令 20 秒、普通 git 命令 10 分钟、克隆 30 分钟，
   超时即终止进程树并写日志，按钮必定恢复可用。执行期间按钮文案变为「执行中…」。

> 若某次推送日志出现 `Permission denied (publickey)` / `Could not read from remote repository`，
> 说明本机没有可用的远端凭据；https 远端请到【Git 环境】→【推送凭据】配置账号，ssh 远端请先配置好密钥。

## 提交身份（user.name / user.email）——提交的前置条件

git 生成 commit **必须**有 `user.name` 与 `user.email`，二者缺一时 git 直接报错、根本生成不了提交：

```
*** Please tell me who you are.

fatal: unable to auto-detect email address
```

本工具的处理方式：

1. **状态可见**：主界面底部实时显示当前生效的提交身份（`git config --get` 的生效值：仓库级 → 全局），
   未设置时显示「未设置」提示。
2. **提交前强制校验**：点【执行提交并推送全部远端】时，若确实要生成 commit 而身份缺失，
   会先自动套用已保存的身份；仍未设置则**弹窗引导填写**并写入 git config；取消则终止流程（不会静默失败）。
3. **随时设置**：主界面【提交身份…】按钮，或【Git 环境】→【提交身份】→【设置提交身份…】。

| 项目 | 说明 |
|---|---|
| 用户名 | `git config user.name`，提交记录里的作者名 |
| 邮箱 | `git config user.email`，建议与代码平台注册邮箱一致，便于平台关联贡献者 |
| 写入位置 | 仅当前仓库（`git config`，写入 `.git/config`）或全局（`git config --global`，对所有仓库生效） |

身份同时保存在应用设置目录的 `settings.json`，换仓库时可直接复用。

## 推送凭据（https 远端）

工具为非交互执行，`GIT_TERMINAL_PROMPT=0` 会禁止 git 弹窗询问账号，
所以 https 远端**必须**预先提供凭据，否则推送必然失败：

```
fatal: could not read Username for 'https://cnb.cool': terminal prompts disabled
```

### 账号来源（像 git 环境一样可选择）

在【Git 环境】→【推送凭据 / 账号】顶部选择来源，配置保存在应用设置目录的 `settings.json`：

| 来源 | 说明 |
|---|---|
| 自动检测（推荐） | 优先使用已保存的自定义账号，该主机没配时自动从系统凭据获取 |
| 仅系统凭据 | 每次推送实时向系统凭据库（凭据管理器 / 钥匙串 / Secret Service）/ helper 索取，**令牌不写入配置文件** |
| 仅自定义账号 | 只使用下方保存的账号，完全不访问系统 |
| 不使用凭据 | ssh 远端，或已自行配置（ssh-agent / URL 内已带账号） |

### 自定义填写与验证

| 项目 | 说明 |
|---|---|
| 主机 | 远端主机名，如 `cnb.cool`；也可直接粘贴完整 URL |
| 用户名 | 平台账号名 |
| 令牌 | 平台生成的访问令牌（PAT），比填登录密码更安全（输入时以掩码显示） |

点【验证账号】会用一次 `git ls-remote` 实测该账号能否通过服务端认证（默认 20 秒超时、可随时取消、
非交互且不改动系统凭据）；【添加/更新】前同样会先实测一次，未通过时可自行决定是否仍保存。

### 检测并导入系统账号

点击【检测系统凭据…】打开检测窗口，分三步，**不会一上来就全量扫描**：

1. **选择主机**：默认只勾选有明确线索的主机（当前项目远端 → Windows 凭据管理器中的 git 目标 → 凭据文件），
   常用平台（github / gitee / cnb.cool …）默认不勾选，也可手动添加主机、全选 / 全不选；
2. **开始检测**：逐个探测并显示进度（`1/3：cnb.cool`），随时可点【停止】；
3. **勾选导入**：列出 主机 / 用户名 / 来源，勾选后导入，导入后与手工填写的完全一样，可继续编辑或删除。

| 检测来源 | 说明 |
|---|---|
| Windows 凭据管理器 | Git Credential Manager / wincred 保存的账号（`cmdkey` 枚举目标 + `git credential fill` 取账号与令牌） |
| macOS 钥匙串 | 不逐个枚举钥匙串（太慢），由 `git credential fill` 经 `osxkeychain` 按需索取 |
| Linux Secret Service | GNOME Keyring / KWallet 中的账号，由 `git credential fill` 经 `libsecret` 按需索取（未安装 libsecret 时退回 `~/.git-credentials`） |
| `~/.git-credentials` | `credential.helper=store` 生成的明文凭据文件，直接解析 `https://user:token@host` |
| 其他 credential helper | 通过 `git credential fill` 统一索取，支持任意自定义 helper |

> **防卡死**：每个主机单次探测 5 秒超时即杀掉进程树，整体可【停止】，
> 且全程非交互（`GIT_TERMINAL_PROMPT=0`、`GCM_INTERACTIVE=never`），不会弹出凭据管理器窗口。

### 凭据助手（credential.helper）

【Git 环境】窗口会显示当前 git 的助手状态。若配置的助手在当前 git 中不存在（精简便携版常见），
推送时报 `'credential-manager' is not a git command`，本工具会自动改用实际可用的助手
（`-c credential.helper=xxx`，仅本次命令生效，不修改用户 git 配置）。

程序自动把凭据注入 URL（`https://user:token@host/...`），**日志中的令牌一律显示为 `***`**，
并会注明该凭据来自「已保存」还是「系统凭据管理器」。
ssh 远端（`git@host:...`）不走凭据，使用 ssh 密钥。

> **凭据不会被提交到仓库**：它只保存在**仓库之外**的应用设置目录的 `settings.json`（路径见下方【配置文件说明】），
> 仓库内的 `multigitcommit.config.json` 不含任何凭据字段，日志里的令牌一律显示为 `***`。
> 因此 `git add .` 与 `git push` 都不会把账号或令牌带到远端。
>
> **账号与令牌加密保存**：`Credentials[].Username` / `Token` 落盘为密文（旧版明文配置在首次保存时自动升级，无需迁移）：
> Windows 用 DPAPI 绑定当前 Windows 用户，密钥由系统保管；macOS / Linux / 移动端用 AES-256-CBC + HMAC-SHA256，
> 密钥是数据目录里的 `credential.key`（Unix 权限 600，移动端依赖应用沙盒隔离）。`Host` 保持明文以便按主机匹配。
> 密文与当前用户 / 本机绑定，配置文件换机器、换用户或跨平台复制后凭据会解不开（设置页会提示重新填写），其余字段不受影响。
> 请勿把整个数据目录（`settings.json` + `credential.key`）复制给别人，也不要手工把配置文件放进仓库。

## 配置文件说明

每个 Git 仓库根目录下会生成独立的配置文件 **`multigitcommit.config.json`**（与 `.git` 文件夹同级），各平台字段完全一致：

| 字段 | 类型 | 说明 |
|---|---|---|
| `RemoteUrls` | string[] | 推送远端 URL 列表 |
| `CheckAddAll` | bool | 是否执行 `git add`（勾选后提交前弹窗勾选要添加的文件） |
| `CheckPushTags` | bool | 是否推送标签 `--tags` |
| `CheckForceCommit` | bool | 是否强制提交（commit 追加 `--allow-empty`，无变更也生成空提交） |
| `CheckForcePush` | bool | 是否强制推送（push 追加 `--force`，覆盖远端历史） |

Git 来源等全局设置（与仓库无关）保存在 **`settings.json`**，各平台字段也完全一致：

| 平台 | 路径 |
|---|---|
| Windows | `%APPDATA%\MultiGitCommit\settings.json` |
| macOS | `~/Library/Application Support/MultiGitCommit/settings.json`（权限 600） |
| Linux | `$XDG_CONFIG_HOME/MultiGitCommit/settings.json`（默认 `~/.config/MultiGitCommit/settings.json`，权限 600） |

【Git 环境】设置窗口底部显示数据目录（即上表 `settings.json` 所在目录），点【打开数据目录】可直接用系统文件管理器打开；
非 Windows 平台的凭据密钥 `credential.key` 也在该目录（权限 600），Windows 由 DPAPI 保管密钥、不生成此文件。

| 字段 | 类型 | 说明 |
|---|---|---|
| `GitSource` | int | git 来源：`0` 自动检测 / `1` 系统 PATH / `2` 指定路径 / `3` 软件内嵌 Git |
| `CustomGitPath` | string | 指定模式下的 git 可执行文件或 Git 根目录路径（支持 `~` 与环境变量） |
| `Credentials` | object[] | https 推送凭据列表：`{ "Host": "cnb.cool", "Username": "enc:...", "Token": "enc:..." }`（按主机匹配；`Host` 明文，账号与令牌加密保存，详见上方【推送凭据】） |
| `CredentialSource` | int | 账号来源：`0` 自动检测（自定义优先）/ `1` 仅系统凭据 / `2` 仅自定义 / `3` 不使用 |
| `CommitIdentity` | object | 提交身份：`{ "Name": "...", "Email": "...", "Scope": 0 }`（`Scope`：`0` 仅当前仓库 / `1` 全局） |
| `LastRepoPath` | string | 上次成功加载的仓库根目录；仅在启动模式选择「上次使用的仓库优先」或「回退上次项目」时用于自动载入 |
| `StartupRepo` | int | 启动时加载的仓库：`0` 只读取当前目录（默认，找不到时不自动打开上次项目）/ `1` 上次使用的仓库优先 / `2` 当前目录优先，找不到时回退上次项目 |
| `StageMode` | int | 执行 `git add` 的文件添加方式：`0` 全提交（`git add .`，默认）/ `1` 可选提交（提交前弹窗勾选要添加的文件） |
| `Language` | int | 界面语言：`0` 简体中文（默认）/ `1` English；切换后立即生效 |

> **注意**
> - 本工具**不会修改** git 原生的 `.git/config` 文件；远端地址只保存在上述工具独立配置文件中。
> - 推送时直接使用 `git push {url} {branch}`，**不操作 `git remote` 命令**。
> - 每个 Git 仓库拥有独立配置，切换仓库自动加载对应项目的远端列表。

## 多语言（i18n）

- **内置语言**：简体中文（默认）与 English。【Git 环境】→「界面语言」选择后点【确定】，设置立即保存、界面文案即时刷新，无需重启；已输出的日志是历史记录，保持原语言不回译。
- **覆盖范围**：全部 XAML 界面文案、按钮 / 提示 / 对话框、运行状态与 git 流程日志。CLI / MCP 的工具描述与错误文本属于面向 Agent 的协议输出，暂不参与界面本地化。
- **实现方式**：中文文案直接作为资源键——C# 侧用 `Loc.T("中文")` / `Loc.F($"中文 {x}")`，XAML 侧用 `{DynamicResource L.xxx}`。App 启动时把当前语言字典写入 `Application.Resources`，切换语言时原地改写资源值即可让 `DynamicResource` 整页刷新；字典里查不到的键自动回退中文，新增文案不会导致界面空白。字典内置于代码（无 .resx / 卫星程序集），桌面单文件发布与 Android / iOS 打包行为一致。
- **翻译源与生成**：`I18n/strings.zh-CN.tsv`、`I18n/strings.en-US.tsv`（每行 `key<TAB>文本`，`@N@` 表示换行）；运行 `python scripts/BuildI18n.py` 生成 `I18n/Strings.*.g.cs`（请勿手改生成文件）。脚本会校验英文包覆盖全部界面键。
- **扩展新语言**：在 `I18n/AppLanguage.cs` 追加枚举值 → 复制一份 `strings.<区域>.tsv` 并翻译 → 在 `Loc.T` / `Loc.GetResourceEntries()` 接入新字典 → 设置页 `CmbLanguage` 加一项 → 重新生成字典。

## MCP 支持（外部 AI 调用软件）

MCP Server（**MCP（Model Context Protocol）over stdio**）已合并进主项目 `src/MultiGitCommit.Avalonia/Mcp/`，
与图形界面是同一个 exe：**不带参数启动 = 图形界面；带 MCP 参数 = MCP Server**。
外部 AI 客户端（Claude Desktop、Cursor、Cline、CodeBuddy 等）拉起它之后，可以直接调用本软件的能力：
查仓库状态 / 历史 / 配置、执行只读 git 命令、提交并推送到全部远端、拉取、克隆、初始化仓库、设置提交身份。

- **同一套逻辑**：MCP 代码与 GUI 同在一个项目、同一份二进制，共用同一份全局 `settings.json` 和每个仓库的
  `multigitcommit.config.json`——GUI 里配置的 git 来源、凭据来源、推送远端对 MCP 直接生效。
- **零第三方依赖**：只用 BCL 实现 JSON-RPC 2.0 与 MCP stdio（`initialize`、`ping`、`tools/list`、`tools/call`、
  工具注解、日志 / 资源 / 提示词空清单），离线即可构建，不把 Avalonia / ASP.NET Core 拖进无界面进程。
- **stdout 只承载协议**：git 子进程的 stdout / stderr 由 `GitCliRunner` 全部重定向；命令日志经
  `GitCredentials.MaskText` 脱敏后作为工具结果的 `log` 字段返回，诊断信息只写 stderr。
- **仅桌面编译**：`Mcp/` 目录在移动 TFM（android / ios）中被 csproj 排除（移动端无法执行 git 子进程）。

### 构建

MCP 随主项目一起构建，无需单独项目：

```powershell
dotnet publish src\MultiGitCommit.Avalonia\MultiGitCommit.csproj -c Release -r win-x64
```

- Release 产物（单文件，依赖框架）：`src\MultiGitCommit.Avalonia\bin\Release\net10.0\win-x64\publish\MultiGitCommit.exe`，
  内含 GUI + MCP，同目录自带 `git\` 便携 Git。
- macOS / Linux 把 RID 换成 `osx-arm64`、`osx-x64`、`linux-x64`、`linux-arm64` 等，产物为无扩展名可执行文件。

### MCP 客户端配置

以常见的 `mcpServers` 格式为例（`command` 用绝对路径）：

```json
{
  "mcpServers": {
    "multigitcommit": {
      "command": "D:/path/to/publish/MultiGitCommit.exe",
      "args": ["--stdio"]
    }
  }
}
```

MCP 进程与 GUI 是同一个 exe，会自动找到 exe 同级的 `git\` 便携 Git（`GitLocator` 的 Embedded / Auto 模式），
一般无需额外环境变量；机器已装系统 Git、或在【Git 环境】里已指定 git 路径时也一样直接生效。
也可用 `--git <路径>` / `MULTIGITCOMMIT_MCP_GIT` 显式指定 git，或 `--app-dir` / `MULTIGITCOMMIT_MCP_APP_DIR`
指向另一个含 `git\` 子目录的安装目录。
开发调试可把客户端的 `command` / `args` 换成 `dotnet` / `run --project src/MultiGitCommit.Avalonia -- --stdio`
（会同时拉起 GUI 项目编译，仅限桌面 TFM）。

不接客户端时的自检（从终端运行时程序会自动附加父控制台，输出可见；被重定向捕获时同样正常）：

```powershell
MultiGitCommit.exe --self-test     # 协议握手 / 工具清单 / git 环境探测
MultiGitCommit.exe --list-tools    # 打印全部工具
MultiGitCommit.exe --help          # 全部参数与环境变量
```

### 工具清单

| 工具 | 类型 | 说明 |
| --- | --- | --- |
| `get_git_environment` | 只读 | git 路径与版本、全局设置文件、设置摘要（不返回令牌） |
| `resolve_repo` | 只读 | 把目录解析为仓库根目录，报告配置文件与远端数量 |
| `find_repos` | 只读 | 在父目录下扫描本地 Git 仓库（深度 / 数量有上限） |
| `get_repo_status` | 只读 | 当前分支、工作区变更文件、暂存状态、提交身份、配置摘要 |
| `get_config` | 只读 | 读取 `multigitcommit.config.json` 的远端列表与四个开关 |
| `get_history` | 只读 | 提交历史；传 `commit_hash` 时列出该提交改动的文件 |
| `get_identity` | 只读 | 仓库生效的 `user.name` / `user.email` 与软件保存的身份 |
| `run_git` | 只读 | 执行一条白名单内的只读 git 命令（`status` / `log` / `diff` / `show` 等） |
| `set_config` | 写入 | 修改仓库的远端列表与开关（只写工具自己的配置文件） |
| `commit_and_push` | 写入 | 核心流程：`git add` → `commit`（时间戳 + 备注）→ 逐个推送所有远端 |
| `pull` | 写入 | 从远端 URL 拉取当前分支，支持 `ff_only` / `rebase` / `merge` |
| `clone_repo` | 写入 | 克隆仓库，支持指定分支 / 递归子模块 / 浅克隆 |
| `init_repo` | 写入 | `git init` 并可选设置默认分支 |
| `set_identity` | 写入 | 写入仓库级或全局提交身份 |

### 安全约束与限制

- 所有写工具必须显式传 `confirm: true`（在 MCP 客户端自身的工具审批之外再加一道闸）；
  `--readonly` 或 `MULTIGITCOMMIT_MCP_READONLY=1` 会隐藏全部写工具。
- 不暴露 `--force` 强制推送、`reset --hard`、任意系统命令；`run_git` 只允许只读 git 子命令，
  并禁止 shell 元字符、`git -c`、`config --global` 写用法等，20 秒超时。
- 返回值与日志统一脱敏；没有读取凭据的工具，`get_git_environment` 只报告已保存凭据的数量；
  `set_config` 拒绝把内嵌令牌的 URL 写入配置。
- 仓库路径统一经 `RepoLocator`（`git rev-parse --show-toplevel`）校验，`files` 只能传仓库内相对路径。
- GUI 与 MCP 共用设置文件，二者同时写可能互相覆盖（`settings.json` / 配置文件都是整文件覆盖），建议错开写操作。
- 请求串行处理，避免进程内静态单例的并发冲突；clone 上限 30 分钟，部分 MCP 客户端可能提前超时。
- 仅桌面 `net10.0`；Android 走 APK 内嵌 git 可执行文件、iOS 走进程内 libgit2（无独立 git 程序），MCP 场景不适用移动端。

## CLI 命令行（人机直接调用）

与 MCP 同一套工具处理器，命令行直接调用软件能力，输出 JSON 文本（`ok` 字段表示成败，git 日志在 `log` 字段并已脱敏）：

```powershell
MultiGitCommit.exe status D:\path\to\repo                  # 分支 / 变更文件 / 身份 / 配置摘要
MultiGitCommit.exe push  D:\path\to\repo -m "修复登录问题"   # 提交并推送全部远端（核心流程）
MultiGitCommit.exe history D:\path\to\repo --limit 10      # 提交历史
MultiGitCommit.exe run   D:\path\to\repo log -n 5 --oneline # 只读白名单 git 命令
MultiGitCommit.exe help                                    # 全部命令
```

- 可用命令：`env` / `repos` / `status` / `config` / `history` / `identity get|set` / `run` / `push` / `pull` / `clone` / `init` / `help`。
- 写命令在命令行执行即视为人工确认（等价 MCP 的 `confirm:true`）；退出码 0=成功 / 1=失败 / 2=用法错误。
- 一个 exe 三种模式：**无参数 = GUI；子命令 = CLI；`--` 参数 = MCP Server**。

## Agent Skill（AI 技能包）

`skill/multigitcommit/` 内置 Agent 技能（SKILL.md + references/cli.md），让 CodeBuddy 等 AI 客户端
自动学会定位 CLI 并按规范调用提交推送 / 拉取 / 查询等流程：

- 仓库内分发：`skill/multigitcommit.zip`（已按技能规范校验打包）；
- 安装为用户技能：把 `skill/multigitcommit/` 复制到 `~/.codebuddy/skills/multigitcommit/`，
  或设置环境变量 `MULTIGITCOMMIT_CLI` 指向 `MultiGitCommit.exe`。

## 技术说明

- **一套代码三个平台**：`net10.0` + Avalonia 11（`Avalonia.Desktop`、`Avalonia.Themes.Fluent`），不依赖 WPF / WinForms；
  平台差异（git 候选路径、设置目录、命令 shell、凭据库）统一收在 `OperatingSystem.IsWindows()` / `IsMacOS()` 分支里。
- 原生控件 + 事件驱动后台代码，无 MVVM 框架、无第三方 Git NuGet 包；JSON 使用内置 `System.Text.Json`。
- 界面文案统一走 `I18n/Loc` + `Application.Resources` 的 `DynamicResource`（无第三方 i18n 框架、无 .resx / 卫星程序集），详见【多语言（i18n）】。
- 文件夹 / 文件选择使用 Avalonia 的 `StorageProvider`；弹窗为自建 `Dialogs`（提示 / 确认 / 输入），不用系统 MessageBox。
- 全部 Git 操作通过调用 git 命令行完成：由 `GitLocator` 解析来源，并把 git 所在目录及其 `mingw64\bin`、`usr\bin`（Windows）
  或 `bin`、`libexec/git-core`（macOS / Linux）注入子进程 PATH，便携版 / 内嵌 git 执行 ssh 远端推送时也无需系统组件；
  macOS 还会补充 `/opt/homebrew/bin`、`/usr/local/bin` 等目录（GUI 应用从 Finder / Dock 启动时 PATH 通常只有系统默认几项），
  Linux 会补充 `~/.local/bin`、`/home/linuxbrew/.linuxbrew/bin`、`/snap/bin`、`/usr/local/bin` 等（从 `.desktop` 图标启动时同样拿不到用户 shell 的 PATH）。

## 项目结构

```
MultiGitCommit/
├── download-portable-git.ps1        # 下载 MinGit 到 src\MultiGitCommit.Avalonia\git\
├── multigitcommit.config.json       # 本项目自身的工具配置（远端列表）
├── res/                             # 图标等素材
├── scripts/
│   ├── BuildI18n.py                 # 由 I18n/strings.*.tsv 生成 Strings.*.g.cs
│   └── FetchAndroidGit.ps1          # 安卓内嵌 Git 获取 / 重打包脚本
└── src/
    ├── MultiGitCommit.Avalonia/     # 【当前主项目】net10.0 / net10.0-android / net10.0-ios 一套代码
    │   ├── Program.cs / App.axaml(.cs)      # 入口与应用定义；App 从组合根装配桌面 / 移动两条启动路径
    │   ├── MainWindow.axaml(.xaml.cs)       # 桌面窗口薄壳（标题 / 图标 / 关闭收尾）
    │   ├── MainView.axaml(.cs)              # 主界面（全平台共用）：UI 事件、日志、响应式布局
    │   ├── *View.axaml(.cs)                 # 对话框视图：GitSettings / Clone / History / StageFiles /
    │   │                                   # Identity / Pull / CredentialDetect / About
    │   ├── Dialogs.cs / UiDialogs.cs        # 提示 / 确认 / 输入 + 桌面 Window、移动 DialogOverlay 统一宿主
    │   ├── Abstractions/IGitCliRunner.cs    # 外部命令执行契约 + Request / Result DTO + 超时档位
    │   ├── Services/
    │   │   ├── GitCliRunner.cs              # 唯一进程执行实现（UTF-8 / 非交互 / 超时杀进程树 / PATH 注入）
    │   │   ├── GitCommandReporter.cs        # 命令执行 + 日志呈现（ILoggedGitRunner）
    │   │   ├── CommitPushService.cs         # 提交并推送全部远端
    │   │   ├── GitPullService.cs            # 拉取更新
    │   │   ├── RepositoryCommands.cs        # init / 默认分支 / clone / 工作区状态 / 手动命令 + clone 参数规则
    │   │   ├── GitHistoryReader.cs          # log / show / status / reset 命令与解析
    │   │   ├── GitEnvironmentService.cs     # git 来源解析、可用性判断、凭据助手缓存失效
    │   │   ├── RepoLocator.cs / GitIdentityService.cs / GitCredentialResolver.cs
    │   │   └── FileOpener.cs / GitCredentials.cs / GitArgument.cs
    │   ├── I18n/
    │   │   ├── AppLanguage.cs               # 界面语言枚举（写入 settings.json 的 Language）
    │   │   ├── Loc.cs                      # 本地化服务：T / F、语言切换事件、资源字典输出
    │   │   ├── strings.zh-CN.tsv / strings.en-US.tsv   # 翻译源（人工维护）
    │   │   └── Strings.*.g.cs              # 由 scripts/BuildI18n.py 生成，勿手工编辑
    │   ├── Settings/
    │   │   ├── AppServices.cs               # 组合根（唯一装配点）+ AppSession（一次界面生命周期的服务集合）
    │   │   ├── ISettingsProvider.cs         # 全局设置访问契约
    │   │   ├── AppSettings.cs               # 全局设置模型 + 跨平台持久化
    │   │   └── ProjectConfigStore.cs        # 每仓库配置仓储（IProjectConfigStore）
    │   ├── Models/                          # 纯数据模型与领域规则（GitRefName / GitCommit / GitIdentity …）
    │   ├── ProjectConfig.cs                 # 每仓库配置模型（纯数据，不自持久化）
    │   ├── GitLocator.cs                    # git 可执行文件定位（仅由 GitEnvironmentService 调用）
    │   ├── GitCredentialHelper.cs           # credential.helper 可用性检测与自动覆盖
    │   ├── SystemCredentialProbe.cs / CredentialTester.cs  # 系统凭据探测 / 账号实测
    │   ├── Platforms/Android|iOS/           # 移动入口与清单（按 TFM 显式编译）
    │   ├── Mcp/                             # 【MCP Server】与 GUI 同一 exe，按命令行参数分派（仅桌面 TFM 编译）
    │   │   ├── McpProgram.cs                # MCP 入口：参数解析、模式分派、git 环境初始化、自检
    │   │   ├── McpProtocol.cs               # JSON-RPC 2.0 + MCP 方法分派（stdout 单帧协议）
    │   │   ├── McpToolSet.cs                # 工具清单 / JSON Schema / confirm 与只读闸
    │   │   ├── McpToolHandlers.cs           # 14 个工具实现（统一调用 AppServices 组合根）
    │   │   └── McpSupport.cs                # 参数解析、日志脱敏缓冲、工具上下文
    │   ├── Cli/CliProgram.cs                # 【CLI】子命令分派（复用 McpToolSet / Handlers），仅桌面 TFM 编译
    │   ├── ConsoleAttach.cs                 # WinExe 附加父控制台（CLI / MCP 人类可读输出共用）
    │   ├── Fonts/ / res/ / git/             # 安卓中文字体 / 图标 / 便携 Git
    │   └── MultiGitCommit.csproj            # 多 TFM / RID / 发布配置
    ├── skill/
    │   ├── multigitcommit/                  # 【Agent Skill】SKILL.md + references/cli.md（供 AI 客户端学习使用 CLI）
    │   └── multigitcommit.zip               # 已校验打包的可分发副本
    └── old-version/                 # 已归档的旧版实现（不再更新）
        ├── MultiGitCommit.NET/      # 旧 Windows WPF 版
        └── MultiGitCommit.MACOS/    # 旧 macOS 版
```

分层约定（高内聚 / 低耦合）：

- 依赖方向单一：`View → AppSession → Services → IGitCliRunner`；静态默认值只在 `AppServices` 组合根出现一次，凭据探测等辅助类也只从组合根取执行器。
- 视图只做参数收集、确认与展示；不拼 git 命令、不读写配置文件、不直接摸 `GitLocator` 等全局状态，操作系统判断只用于生成提示文案。
- 会话级策略服务由组合根统一装配，视图只消费结构化结果：
  - `AppSettingsService`：设置字段写回、落盘与失败处理；最近项目去重 / 置顶 / 上限、默认启动页、提交身份保存；
  - `StartupRepoResolver`：启动仓库定位，返回 `StartupRepoDecision`，视图只把 `Reason` 翻成文案；
  - `RepoSessionService`：仓库校验、根目录解析、项目配置加载与最近项目记忆，返回 `RepoLoadResult`；
  - `RemoteUrlPolicy`：远端 URL 清洗、脱敏占位符拒绝、去重与数量上限；GUI 添加 / 编辑 / 克隆与 MCP `set_config`、`clone_repo` 共用同一规则源（MCP 额外启用「禁止内嵌凭据」）；
  - `IGitEnvironmentService.Describe()`：状态栏与 MCP `get_git_environment` 共用同一份环境快照。
- 命令与规则各有一份实现：clone 参数由 `GitCloneArguments` 同时供预览与执行使用，分支名规则由 `GitRefName` 供主界面与克隆对话框共用，工作区状态解析由 `GitStatusText` 供拉取与历史共用。
- 模型只表示数据：`ProjectConfig` 的读写交给 `IProjectConfigStore`，`AppSettings` 的访问交给 `ISettingsProvider`。
- 生命周期收口：视图离开可视树 / 窗口关闭时统一取消后台 git 任务并退订事件；git 环境切换时由环境服务自动让凭据助手缓存失效。
- MCP Server / CLI 与 View 平级：同样只经 `AppSession → Services → IGitCliRunner` 访问业务，与 GUI 共用同一项目、
  同一份逻辑层，不重复实现 git 命令与配置读写；桌面 `Program.Main` 按命令行分派
  （子命令 → CLI，`--` 参数 → MCP，无参数 → GUI），CLI 直接复用 `McpToolSet.CallAsync`，
  移动 TFM 由 csproj 排除 `Mcp/`、`Cli/` 与 `ConsoleAttach.cs`。

边界与适用范围见 [`docs/boundaries-and-scope.md`](docs/boundaries-and-scope.md)；实现路径、测试与可能出现的问题见
[`docs/implementation-and-verification.md`](docs/implementation-and-verification.md)。提交前回归：`pwsh scripts/TestSmoke.ps1 -IncludeAndroid`。
