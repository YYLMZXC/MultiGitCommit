# 边界与适用范围

> 基线：2026-11-13 的 `src/MultiGitCommit.Avalonia`（单项目多目标 `net10.0` / `net10.0-android` / `net10.0-ios`，.NET 10 + Avalonia 11.3.11）。
> 配套文档：[实现路径、测试与可能出现的问题](implementation-and-verification.md)。
> 本文描述的是当前实现的**实际边界**，不是理想目标；与代码不一致时以代码为准并同步修订本文。

## 1. 系统边界

### 1.1 进程与入口边界

一个可执行文件按命令行参数分派为三种模式（桌面 `Program.cs`）：

| 模式 | 触发 | 入口 | 适用 TFM |
| --- | --- | --- | --- |
| GUI | 无参数 | `App` → `MainWindow`（桌面）/ `MainView`（移动） | 全部 |
| CLI | 首参数不以 `-` 开头 | `Cli/CliProgram.cs` | 仅 `net10.0` |
| MCP Server | 含 `--stdio` / `--self-test` 等参数 | `Mcp/McpProgram.cs` | 仅 `net10.0` |

- `Mcp/**`、`Cli/**`、`ConsoleAttach.cs` 由 csproj 在移动端 `Compile Remove`；`Platforms/**` 先移除再按 TFM 显式 include。
- Android 入口 `Platforms/Android/MainActivity.cs`，iOS 入口 `Platforms/iOS/AppDelegate.cs` + `Main.cs`，二者不经过桌面 `Program.Main`。
- GUI、CLI、MCP **不互相调用 UI**：CLI 复用 `McpToolSet.CallAsync`，三者最终都汇入 `AppSession → Services → IGitCliRunner`。

### 1.2 外部系统边界

| 外部系统 | 唯一出入口 | 约束 |
| --- | --- | --- |
| git 子进程 | `Services/GitCliRunner.cs`（唯一 git 进程实现） | UTF-8、非交互 `GIT_TERMINAL_PROMPT=0`、超时杀进程树、注入 PATH；iOS 直接返回失败 |
| 全局设置文件 | `Settings/ISettingsProvider.cs` → `AppSettings.Load/Save` | 整文件覆盖写；凭据加密后落盘 |
| 每仓库配置文件 | `Settings/IProjectConfigStore.cs` → `ProjectConfigStore` | 仓库根目录 `multigitcommit.config.json`，**不改 `.git/config`、不写 git remote** |
| 系统凭据库 / credential.helper | `SystemCredentialProbe` / `GitCredentialHelper` | 通过组合根 `AppServices.Current.Cli` 执行，不自建执行器 |
| 浏览器 / 文件管理器 | `TopLevel.Launcher`、`Services/FileOpener.cs` | 平台差异收敛在 `FileOpener` |
| Android SAF / 内嵌 Git | `AndroidGitPlatform.cs` | 共享存储需「所有文件访问」；https 可用、ssh 不可用 |

### 1.3 安全边界

- MCP：写工具必须 `confirm: true`；`--readonly` / `MULTIGITCOMMIT_MCP_READONLY=1` 隐藏写工具；`run_git` 仅只读白名单并禁 shell 元字符；路径统一经 `RepoLocator` 校验，`files` 只允许仓库内相对路径。
- CLI：写命令在命令行执行即视为人工确认；退出码 `0` 成功 / `1` 失败 / `2` 用法错误。
- 日志与返回值统一脱敏：`GitCredentials.MaskText` / `McpJson.Mask`；`get_git_environment` 只返回已保存凭据数量，不返回令牌。
- **边界差异（需注意）**：GUI 的「手动命令」入口不经过 MCP 的只读白名单；`RemoteUrlPolicy` 的「禁止内嵌凭据」严格规则只在 MCP 写入配置时启用，GUI 人工输入沿用宽松策略。这是显式设计，不是遗漏，见配套文档风险章节。

## 2. 模块与职责边界

依赖方向单一：`View / CLI / MCP → AppSession → Services → IGitCliRunner`，静态默认值只在 `Settings/AppServices.cs` 组合根出现一次。

| 层 | 代表文件 | 职责 | 禁止 |
| --- | --- | --- | --- |
| 视图 | `MainView`、`GitSettingsView`、`RepoHomeView` 等 `*.axaml(.cs)` | 参数收集、确认、控件状态、本地化文案 | 不拼 git 命令、不读写配置文件、不直接改 `AppSettings` 字段、不拼业务规则、不在 UI 线程跑 git 子进程 / 文件 IO |
| UI 基础设施 | `UiDialogs.cs`、`Dialogs.cs` | 桌面 Window / 移动 Overlay 双宿主、提示框 | 不含业务逻辑 |
| 组合根 | `Settings/AppServices.cs`（`AppServices` + `AppSession`） | 唯一对象装配点；`CreateSession(cli, settings, log)` 是测试替身入口 | 不含业务流程 |
| 会话策略服务（新增） | `Settings/AppSettingsService.cs` | 设置字段写回、落盘与失败处理；最近项目去重/置顶/上限、默认启动页、提交身份 | 不引用 Avalonia |
| 会话策略服务（新增） | `Services/StartupRepoResolver.cs` | 启动仓库定位，返回 `StartupRepoDecision(Path, Reason)` | 不拼界面文案、不判断操作系统 |
| 会话策略服务（新增） | `Services/RepoSessionService.cs` | 仓库校验、根目录解析、项目配置加载、记住最近项目，返回 `RepoLoadResult` | 不碰控件、不写日志 |
| 会话策略服务（新增） | `Services/RemoteUrlPolicy.cs` | 远端 URL 清洗、脱敏占位符拒绝、去重、数量上限；GUI 与 MCP 共用 | 不依赖 MCP / Avalonia 类型 |
| 业务服务 | `CommitPushService`、`GitPullService`、`RepositoryCommands`、`RepoLocator`、`GitEnvironmentService` 等 | git 流程、仓库判定、环境快照 | 不引用任何 UI 类型 |
| 抽象 / 模型 | `Abstractions/IGitCliRunner.cs`、`ISettingsProvider`、`IProjectConfigStore`、`Models/*`、`ProjectConfig` | 契约与纯数据 | 模型不自持久化 |
| MCP / CLI | `Mcp/*`、`Cli/*` | 协议、工具元数据、参数解析、confirm 闸 | 不重新实现 git 命令与配置读写 |

`AppSession` 暴露的服务由 `AppServices.CreateSession` 统一装配；注入自定义 `ISettingsProvider` 时 `AppSettingsService` 会创建替身，不复用共享单例。

## 3. 数据边界

| 数据 | 归属 | 落盘位置 | 共享范围 |
| --- | --- | --- | --- |
| `AppSettings` | 全局 | Windows `%APPDATA%\MultiGitCommit\settings.json`；macOS `~/Library/Application Support/MultiGitCommit/`；Linux `$XDG_CONFIG_HOME/MultiGitCommit/`；移动端 `LocalApplicationData` | GUI / CLI / MCP 共用同一文件 |
| `Credentials` | 全局设置内 | 密文（Windows DPAPI；其他平台 AES + `credential.key`） | 换机器 / 换用户后需重新填写 |
| `ProjectConfig` | 单个仓库 | 仓库根 `multigitcommit.config.json`（与 `.git` 同级） | 每仓库独立，切换仓库自动加载 |
| `GitIdentity` | 全局设置 + git config | 设置文件 + 仓库级/全局 `user.name` / `user.email` | 提交前置条件 |

- 写入均为整文件覆盖：GUI 与 MCP 同时写同一设置/配置文件时可能互相覆盖，应错开写操作。
- MCP 请求串行处理，避免进程内静态单例并发冲突。

## 4. 适用范围

| 能力 | Windows / macOS / Linux 桌面 | Android | iOS |
| --- | --- | --- | --- |
| GUI（提交推送、拉取、克隆、设置、仓库首页） | 支持 | 支持（内嵌 Git、https 远端） | 仅界面 / 设置浏览 |
| git 子进程 | 系统 Git / 内嵌便携 Git / 指定路径 | APK 内嵌 `libgit.so` | 沙盒禁止，功能不可用 |
| ssh 远端 | 支持 | 不支持（APK 未内置 ssh 客户端） | 不适用 |
| CLI 子命令 | 支持 | 不编译 | 不编译 |
| MCP Server | 支持 | 不编译 | 不编译 |
| 新增 4 个策略服务 | 支持 | 支持（纯托管，无 UI 依赖） | 支持（纯托管） |
| 高级设置滚动定位优化 | 支持 | 编译通过，窄屏留白同步调整 | 编译通过 |

- 构建与发布：桌面 `dotnet publish -f net10.0`；Android `net10.0-android`（minSdk 23）；iOS `net10.0-ios` 需 macOS。多目标命令必须显式 `-f`。
- 冒烟回归脚本 `scripts/TestSmoke.ps1` 仅适用于桌面 `net10.0`（依赖 CLI / MCP 与本机可用 git），可选 `-IncludeAndroid` 追加 Android 编译检查。

## 5. 明确不在适用范围内

1. `src/old-version/` 中的 WPF / 旧 macOS 实现，已归档且不再更新。
2. 移动端的 MCP / CLI / stdio 协议（csproj 已排除，Android/iOS 也不具备同等的子进程能力）。
3. iOS 上的任何 git 执行、ssh 远端、Android 上的 ssh 远端。
4. 无 DI 容器：组合根是手写的静态单例 `AppServices.Current`，不是通用依赖注入框架。
5. 无自动化测试项目（无 xunit / NUnit / MSTest）；当前回归依赖 `scripts/TestSmoke.ps1` + 手工 UIA 截图。
6. `RemoteUrlPolicy` 的严格模式、MCP 只读白名单**不**约束 GUI 的手动命令与人工输入。
7. 本文与配套文档不覆盖发布签名、分发渠道、多实例并发写入等运维议题。

## 6. 本次改动的影响边界

| 改动 | 影响到的行为 | 不应影响 |
| --- | --- | --- |
| 4 个策略服务 + 组合根装配 | 启动仓库定位、配置加载、最近项目、设置保存、远端 URL 规则、环境快照 | git 命令内容、提交消息格式、远端推送协议、配置文件格式 |
| 静态执行器收口 | 凭据探测 / 凭据助手扫描取的 `IGitCliRunner` 实例 | 执行器行为本身（仍是同一实例） |
| 高级设置 UI 优化 | 对话框滚动位置、卡片间距、入口外观、底部留白 | 设置字段、保存逻辑、git 检测触发时机 |
| 移动端 UI 流畅性优化 | 远端列表滚动层与项模板、日志刷新时机与视口高度、配置加载所在线程 | 远端数据内容、选中 / 编辑 / 删除语义、日志文案与脱敏规则、CLI / MCP 行为 |
| 移动端设置页 / 仓库首页适配 | 数据目录按钮的平台行为、首页列表限高与换行 | 设置字段、最近项目数据、扫描算法、默认启动页取值 |
| `README.md` 分层约定 / 新增文档与脚本 | 文档与回归手段 | 运行时代码 |
