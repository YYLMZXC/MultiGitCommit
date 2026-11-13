# 可复用：.NET 构建、多目标与工具链经验

> 适用：从一个项目迁移到下一个项目仍成立的机制 / 报错 / 命令。
> 来源与核验：MultiGitCommit（2026-11-12 实查 csproj）、sc_mod_unpak 与 CharacterSkinsMC（2026-11-06 历史实查）。版本号属于项目现状，不写进结论。

## 1. 改 csproj 之前先看这一条

- **IDE 会回写 / 清空 csproj**：编辑器里直接改项目文件，内容可能被覆盖成空或旧值。多项目仓库（Uno / 单项目多 TFM）都遇到过。
- 可靠做法：在 `execute_command` 里先写到 `.txt` 中转文件，再 `Copy-Item` 覆盖目标 csproj，随后立刻读回确认，并单独执行一次构建。
- 配套习惯：改完 csproj 用 `git diff` / 读文件确认最终内容，不要以「工具返回成功」为准。

## 2. TargetFramework / RID / 发布

- **Android minSdk 随 .NET 版本变**：.NET 10 下 `SupportedOSPlatformVersion` 必须 ≥ 23；.NET 11 下 ≥ 24，否则 `XA4216`。
- **桌面 RID 会经 NuGet dgspec 泄漏到移动 TFM**，触发 `XA0035` / `NETSDK1206`。两种解法：
  - 在 `Directory.Build.targets` 里按 TFM 复位 `RuntimeIdentifiers`（Uno 多项目结构适用）；
  - 或在 csproj 里给每个 TFM 单独加 `Condition="'$(TargetFramework)' == '...'"` 声明，避免全局 `RuntimeIdentifiers`。
- **多目标项目 `dotnet publish` 必须显式 `-f <TFM>`**；`dotnet build -f <TFM>` 逐目标验证，比一次全量构建更容易定位报错归属。
- 移动端发布用 `-f` 指定 TFM 并只带一个 `-r`；构建可声明多个 RID 变体，发布命令里混合多个移动 RID 易出打包冲突。
- 桌面 `PublishSingleFile` 会把依赖打进主程序；**数据目录（如内嵌 git、字体资源、配置模板）必须 `ExcludeFromSingleFile="true"`**，否则运行时找不到同级文件夹。
- 平台专属源码目录（如 `Platforms/**`）SDK 不自动过滤：先 `<Compile Remove="Platforms/**" />`，再按 TFM `<Compile Include="Platforms\Android\**\*.cs" />`；iOS 需要显式入口文件（如 `Platforms/iOS/Main.cs`）。
- 移动端应用元数据：`ApplicationId` 同时作 Android 包名 / iOS BundleId；macOS `CFBundleShortVersionString` 只接受纯数字三段，构建时间戳这类字符串放到 `CFBundleVersion`。

## 3. 平台编译冲突

- Android TFM 下 `Android.App` / `Android.Widget` 的隐式 `using` 会与 Avalonia 同名类型冲突：`App` 基类写全限定 `Avalonia.Application`，控件用别名（如 `AvaloniaButton` / `AvaloniaOrientation`）。
- 桌面才有的属性（`BuiltInComInteropSupport`、`.ico` 图标、单文件发布）一律加 TFM / OS 条件，不要让移动端 TFM 读到。
- 移动端设置目录用 `LocalApplicationData`，不要沿用桌面的用户目录；Android 访问外部存储需在清单里声明权限（如 `MANAGE_EXTERNAL_STORAGE`）。
- 嵌入第三方字体 / 资源时连许可文件一起分发（如 Noto Sans CJK SC 的 SIL OFL），并只在该资源真正需要的 TFM 里 `AvaloniaResource`，避免无谓增大安装包。

## 4. 工具链与验证习惯

- **PowerShell 5.1 读取含中文注释的 `.ps1` 必须是 UTF-8 BOM**，否则注释按 ANSI 解析导致脚本异常；无 BOM 的 UTF-8 只在 PowerShell 7+ 可靠。
- 纯逻辑改造加一个可直接运行的冒烟入口（例如 `--smoke`），把关键分支写成断言，验证时先 build 再跑冒烟，比只靠 UI 手点可靠。
- 生成日志里的第三方警告（如 Avalonia 的 `AVLN3001`：窗口无 public 无参构造）若确认是设计使然，用 `NoWarn` 显式静默并写明原因，不要留着淹没真实错误。
- 版本号可由构建时间自动生成并支持 `-p:Version=...` 覆盖；需要可复现构建时显式传版本。**同时有 `netX.0-android` / `netX.0-ios` 目标时，`Version` 必须纯数字且各 TFM 一致**——移动端 SDK 有一条「`ApplicationDisplayVersion` 能解析成 `System.Version` 就覆盖 `Version`」的规则，带后缀（如 `26.11.13-1311`）会使 `PackageVersion` 各 TFM 不同，NuGet 报 NU1105、资产文件被写成空壳（`"targets": {}`）进而报 NETSDK1005；构建时刻应放 `InformationalVersion`。这种不一致只有 Visual Studio 的 NuGet 会拒绝，`dotnet restore` / Rider 常放行，不能以「命令行能过」判定还原健康。
- 本机常驻 `MultiGitCommit.exe` 会自动提交中间态：改动过程中间文件可能被自动提交，重要中间结论先落到文档，验证通过后再做一次整理性提交。
