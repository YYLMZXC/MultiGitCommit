# 实现路径、测试与可能出现的问题

> 基线：2026-11-13 的两轮改动（分层重构 + 高级设置 UI 优化）。
> 配套文档：[边界与适用范围](boundaries-and-scope.md)。
> 所有「本次验证记录」均为实际执行结果，未执行的检查明确标为「未覆盖」。

## 1. 实现路径

### 1.1 组合根装配（唯一入口）

```
Program.Main / MainActivity / AppDelegate
  └─ App.OnFrameworkInitializationCompleted
       └─ MainWindow / MainView(AppServices.Current.Cli, AppServices.Current.Settings)
            └─ AppServices.Current.CreateSession(cli, settings, log)
                 ├─ GitCommandReporter(cli, log)            命令日志
                 ├─ shared ? Identity/History/RepoLocator : new(cli)
                 ├─ sharedSettings ? SettingsEditor : new AppSettingsService(settings)
                 ├─ RepoSessionService(repoLocator, ProjectConfigs, GitEnvironment, settingsEditor)
                 ├─ StartupRepoResolver(settings)
                 ├─ RemoteUrlPolicy()
                 └─ AppSession(...)  ← 视图 / MCP / CLI 只从这里取服务
```

`shared` 依据 `ReferenceEquals(cli, Cli)`，`sharedSettings` 依据 `ReferenceEquals(settings, Settings)`：注入测试替身时整套依赖一起替换，不会被共享单例绕过。

### 1.2 六条关键调用链

| # | 场景 | 调用链 | 结构化结果 |
| --- | --- | --- | --- |
| 1 | 启动仓库定位 | `MainView` 构造 → `AppSession.StartupRepo.Resolve()` → `RepoLocator.FindRepoAncestor` / `AppSettings.StartupRepo` | `StartupRepoDecision(Path, Reason)`；视图用 `FormatStartupMessage` 把 `Reason` 翻成本地化文案 |
| 2 | 加载仓库 | `MainView.LoadConfigFromPathAsync` → `Task.Run(AppSession.RepoSession.Load)` → `IGitEnvironmentService.IsAvailable` → `RepoLocator.TryResolve` → `AppSettingsService.RememberRepo` → `IProjectConfigStore.Load` → 回 UI 线程 | `RepoLoadResult(RepoRoot, Config, Failure, GitError, InputPath)`；`_loadSeq` 丢弃过期结果，视图按 `Failure` 输出提示并回填控件 |
| 3 | 设置变更 | `MainView` / `RepoHomeView` → `AppSettingsService.TryApplyEnvironmentSettings` / `TrySetCommitIdentity` / `TryRemoveRecent` / `TryClearRecent` / `TrySetDefaultStartPage` → `ISettingsProvider.Save()` | `bool` + `out error`；视图只展示结果 |
| 4 | 远端 URL | GUI「添加/编辑/克隆」→ `RemoteUrlPolicy.Add/Replace/Normalize`；MCP `set_config` / `commit_and_push` / `clone_repo` → `RemoteUrlPolicy.NormalizeAll/Normalize` | `RemoteUrlChange(Changed, Url, PreviousUrl, Message)` 或清洗后的列表 + `error`；MCP 把 `error` 包装成 `ToolUsageException` |
| 5 | 环境快照 | `GitEnvironmentService.Refresh()` / `Describe()` → `GitEnvironmentSnapshot` | 状态栏、主界面日志、MCP `get_git_environment` 共用一份数据，MCP 不再直读 `GitLocator.Current` |
| 6 | CLI 复用 | `CliProgram.DispatchAsync` → `McpToolSet.CallAsync` → `ToolContext(AppSession)` → `McpToolHandlers` → 服务层 | CLI 不重写 git 逻辑；写命令自动补 `confirm` |

### 1.3 关键设计决策

1. **服务返回结构化结果，视图负责文案**：`RepoLoadFailure` / `StartupRepoReason` 把「为什么」与「怎么显示」分开，服务层不出现 `Loc.T` 拼界面句子（错误消息除外）。
2. **规则单一源、策略差异显式参数化**：`RemoteUrlPolicy` 的 `rejectEmbeddedCredentials` 由调用场景决定（MCP `true` / GUI `false`），避免同一规则维护两份实现。
3. **执行器来源唯一**：`CredentialTester` / `SystemCredentialProbe` / `GitCredentialHelper` 从 `AppServices.Current.Cli` 取执行器，消除与 `GitCliRunner.Default` 并存的两套来源。
4. **UI 展开定位不依赖 `BringIntoView`**：`GitSettingsView.ScrollAdvancedIntoViewAsync()` 等展开动画稳定后，用 `TransformToVisual(ContentScroll)` 的矩阵平移量 `M32` 计算内容纵坐标，把「高级设置」标题对齐到滚动区顶部并留 16px；定位失败时保持原滚动位置。
5. **留白与视觉分区**：`ScrollViewer` 底部留白 28（窄屏 24）、Expander 底部 20、入口改为带底色/边框的整行卡片；首屏卡片内边距与间距压缩约 20px，使收起状态的入口完整露出。
6. **移动端远端列表不嵌套滚动**：窄屏（单视图 / 宽度 < 700）时 `ListRemotes` 禁用内部纵向滚动并自适应高度，由 `RootScroll` 统一滚动；项模板把长 ssh/https URL 单行省略（`TextTrimming`），避免逐项测量撑宽整页；宽屏恢复固定高度 + 内部滚动。
7. **日志缓冲 + 固定视口 + 后台加载**：`Log` 写入 `StringBuilder`，60ms 内的输出合并成一次 `TxtLog.Text` 赋值并限制 64KB；移动端 `LogFrame.Height = 240`，避免 `*` 行随日志增长撑高整页；仓库校验 / 配置读取 / 设置落盘放进 `Task.Run`，`_loadSeq` 保证只有最后一次结果写 UI。

### 1.4 文件清单

**新增**
- `Settings/AppSettingsService.cs`（含 `EnvironmentSettingsUpdate`）
- `Services/StartupRepoResolver.cs`（含 `StartupRepoDecision` / `StartupRepoReason`）
- `Services/RepoSessionService.cs`（含 `RepoLoadResult` / `RepoLoadFailure`）
- `Services/RemoteUrlPolicy.cs`（含 `RemoteUrlChange`）
- `scripts/TestSmoke.ps1`、`docs/boundaries-and-scope.md`、`docs/implementation-and-verification.md`

**修改**
- `Settings/AppServices.cs`（组合根 + `AppSession` 新增 4 个服务属性）
- `Services/GitEnvironmentService.cs`（`IGitEnvironmentService.Describe()`、`GitEnvironmentSnapshot.SshExe`）
- `MainView.axaml.cs`（启动定位、异步配置加载、远端 CRUD、环境设置 / 身份保存、首页 `Attach(this, AppSession)`、日志缓冲、移动端列表与日志视口）
- `MainView.axaml`（远端列表项模板：长 URL 单行省略 + 禁用横向滚动）
- `RepoHomeView.axaml.cs`（注入 `AppSession`，最近项目与启动页走设置服务）
- `Mcp/McpToolHandlers.cs`（复用 `RemoteUrlPolicy` 与 `Describe()`，删除本地 URL 规则副本）
- `CredentialTester.cs` / `SystemCredentialProbe.cs` / `GitCredentialHelper.cs`（执行器来源）
- `GitSettingsView.axaml(.cs)`（高级设置入口、留白、滚动定位、响应式 padding）
- `README.md`（分层约定与文档链接）

## 2. 测试

### 2.1 测试策略与前置条件

当前仓库**没有单元测试项目**（无 xunit / NUnit / MSTest 引用，`MultiGitCommit.slnx` 只含主项目）。因此采用三层回归：

1. **构建门禁**：逐 TFM `dotnet build`，0 错误。
2. **无 UI 冒烟**：`scripts/TestSmoke.ps1` 通过 CLI / MCP stdio 断言命令、退出码、脱敏与安全规则。
3. **UI 实跑**：Windows UI Automation 打开「Git 环境设置」，收起 / 展开「高级设置」各截一张图，比对入口与首块内容是否完整可见。

前置条件：.NET 10 SDK；桌面 exe 存在（脚本会先构建）；本机 git 可用（`status` 会执行 `git branch` / `git status`）；UI 实跑需 Windows + 图形会话。

### 2.2 用例矩阵

| ID | 用例 | 命令 / 方式 | 期望 | 本次结果 |
| --- | --- | --- | --- | --- |
| B1/B2 | 桌面 / Android 构建 | `dotnet build -f net10.0` / `-f net10.0-android` | 0 错误 | 通过（仅既有 `CS0169` / `XA0141` 警告） |
| B3 | 桌面 exe 生成 | 文件存在性 | 存在 | 通过 |
| C1 | MCP 自检 | `--self-test` | 退出码 0 | 通过 |
| C2 | 工具清单数量 | `--self-test` 输出 | 14 个工具 | 通过 |
| C3/C4/C5 | 环境摘要 | `env` | 退出码 0、`ok=true`、含 `git` 快照 | 通过 |
| C6/C7 | 仓库状态 | `status <repo>` | 退出码 0、含 `repoRoot` | 通过 |
| C8 | 帮助 | `help` | 退出码 0、含用法文本 | 通过 |
| C9 | 未知命令 | `definitely-not-a-command` | 退出码 2 | 通过 |
| C10 | 只读白名单 | `run <repo> reset --hard` | 被拒绝（非 0 或 `ok=false`） | 通过 |
| C11 | URL 规则 | MCP `tools/call set_config` 带 `https://user:***@...` | `"isError":true`，且不写配置文件 | 通过 |
| U1 | 收起状态入口 | UIA 截图 | 入口卡片完整、下缘不被按钮栏裁切 | 通过 |
| U2 | 展开状态 | UIA 截图 | 标题完整置顶、「Git 程序来源」完整可见 | 通过 |

脚本另有第 2 条 URL 规则断言的人工版本（内嵌凭据 URL 被拒绝），已单独验证通过；脚本中保留更安全的脱敏占位符用例（不会写盘）。

### 2.3 执行方式

```powershell
# 一键回归（含 Android 编译检查）
pwsh scripts/TestSmoke.ps1 -IncludeAndroid

# 只跑桌面；或指定 Release
pwsh scripts/TestSmoke.ps1
pwsh scripts/TestSmoke.ps1 -Configuration Release

# 手工 UI 实跑片段（UIA 可用时）：启动 → Invoke「Git 环境」→ Toggle 高级设置 → CopyFromScreen 截图
```

脚本输出 `N passed, M failed`，存在失败时退出码 1，便于接入 CI / 提交前检查。

### 2.4 本次验证记录（2026-11-13）

- `pwsh scripts/TestSmoke.ps1 -IncludeAndroid` → **14 passed, 0 failed**。
- 桌面 `net10.0` / Android `net10.0-android` 构建均 0 错误；IDE 诊断 0 问题。
- CLI 实测：`env`、`status d:/sc/MultiGitCommit` 返回完整 JSON；MCP `get_git_environment` 的 `exePath` / `description` / `sshExe` 均来自新 `Describe()` 快照。
- MCP 实测：脱敏占位符 URL 与内嵌凭据 URL 均被 `set_config` 拒绝，`multigitcommit.config.json` 未被改写。
- UI 实跑：收起时高级设置入口矩形由 `948,1094,664,57` 上移到 `948,1074,664,57`，底部不再越过固定按钮栏；展开后标题完整置顶，前两块高级内容可见，第三块需正常滚动。
- GUI 启动冒烟：进程存活、stderr 无未处理异常输出。
- 移动端卡顿优化复验（2026-11-13）：UIA 实跑 GUI 5 秒后远端列表 3 项全部加载、日志 673 字符可见，截图确认「远端共 3 个」分行日志与列表显示正常；`pwsh scripts/TestSmoke.ps1 -IncludeAndroid` → **14 passed, 0 failed**；桌面窗口 `Resize` 到 900（受 `MinWidth` 限制，未达 700 阈值）无异常。
- 移动端设置 / 首页适配复验（2026-11-13）：桌面设置页 UIA 仅见「打开数据目录」（复制按钮按平台隐藏）；仓库首页截图确认标题、2 条最近项目、扫描区与底部操作条完整可见；窗口缩到 900×640 后 UIA 坐标确认「项目页 / 仓库管理页」单选与「选择文件夹打开…」全部落在窗口内。

### 2.5 未覆盖 / 待补

| 项目 | 现状 | 建议 |
| --- | --- | --- |
| 单元 / 集成测试项目 | 无 | 用 `AppServices.CreateSession(cli, settings, log)` 注入假执行器，断言生成的 git 参数与服务返回值 |
| Android / iOS 真机 UI | Android 仅编译通过；iOS 在 Windows 无法构建 | macOS 构建 iOS；真机验证窄屏 420 阈值、SAF 目录、内嵌 git |
| 并发写 | 未测 | 补 GUI + MCP 同时写 settings.json / 配置文件的竞态用例 |
| 异常路径 | 未系统覆盖 | git 缺失、目录不可写、凭据解密失败、超长路径 / 中文路径 |
| MCP 长任务 | 未测 | clone 30 分钟上限与客户端超时的交互 |

## 3. 可能出现的问题

### 3.1 已在本轮缓解

| 问题 | 原因 | 处理 |
| --- | --- | --- |
| 展开「高级设置」后标题上沿被裁、首块内容看不见 | `BringIntoView()` 对远高于视口的 Expander 只滚到视口边缘 | 改为等动画稳定后精确设置 `ScrollViewer.Offset` |
| 收起时入口贴按钮栏、下缘被切 | 首屏内容自然高度略大于视口 | 压缩卡片间距约 20px + 增加底部留白 + 入口卡片化 |
| 同一 URL 规则在 GUI / MCP 各写一份 | 规则散落在 `MainView` 与 `McpToolHandlers` | 收敛到 `RemoteUrlPolicy`，差异用显式参数表达 |
| 设置写回与最近项目规则散在视图 | 视图直接改 `AppSettings` 并各自 `try/catch` | 收敛到 `AppSettingsService.TryXxx` |
| 测试替身被共享单例绕过 | `shared` 判断只看 `cli` | `SettingsEditor` 按 `ReferenceEquals(settings, Settings)` 单独判断 |
| 凭据探测类自建第二套执行器 | 直接取 `GitCliRunner.Default` | 统一从 `AppServices.Current.Cli` 取 |
| 移动端远端列表滑动掉帧 | `ListBox` 嵌在整页 `ScrollViewer` 中两层抢手势；长 URL 项参与整页测量 | 窄屏禁用列表内部滚动、自适应高度由外层统一滚动；项模板单行省略 + 禁用横向滚动 |
| 日志越多越卡 | 每次 `TxtLog.Text +=` 全量重设文本，窄屏折行后整页重排；`*` 行还会随日志撑高整页 | `StringBuilder` + 60ms 合并刷新 + 64KB 上限；移动端 `LogFrame.Height = 240` 固定视口 |
| 打开 / 切换仓库时卡住 | `RepoLocator.TryResolve` 同步启动 git 子进程（Android 内嵌 git 更慢）阻塞 UI 线程 | `LoadConfigFromPathAsync` 把服务调用放进 `Task.Run`，`_loadSeq` 防重入 |
| 移动端设置页没有数据目录按钮 | `BtnOpenDataDir.IsVisible = !IsAndroid && !IsIOS`，而应用私有目录又无法用系统文件管理器打开 | 移动端显示「复制数据目录」按钮，点击复制完整路径到剪贴板；桌面仍打开文件夹；4 个 i18n 文件同步词条 |
| 仓库管理首页显示不全 | 最近项目 `ItemsControl` 在 `Auto` 行内无限增高，挤掉自动发现列表与底部操作条；底部条单行横排在窄屏被裁 | 两个列表按视口高度动态限高并各自内部滚动；列表项按钮与底部操作条改 `WrapPanel` 自动换行 |

### 3.2 残留风险（按优先级）

1. **静态可变全局状态仍在**：`AppServices.Current`（组合根）、`SettingsProvider.Instance`、`GitLocator._current`、`UiDialogs.OverlayHost`（可写全局 UI 宿主）、`Loc.LanguageChanged`（未退订）、`CredentialProtector.UnprotectFailureCount`（`AppSettings.DecryptCredentials` 靠前后差值判断解密失败，隐式耦合）。MCP 已明确串行处理请求以规避并发冲突；多实例同时运行仍需错开写操作。
2. **规则分裂**：GUI 手动命令不经过 MCP 的只读白名单；`set_config` 拒绝内嵌凭据 URL 而 GUI 允许。若后续要求一致，需把 `GitCommandPolicy` 下沉到服务层并统一 GUI 策略（会改变现有 GUI 能力，需单独评审）。
3. **`GitSettingsView` 仍直接编辑传入的 `AppSettings` DTO**（点「取消」不会回滚内存中的改动）：当前靠调用方在点确定后统一落盘。建议改为输出 `SettingsEditResult`，由调用方提交。
4. **平台判断仍分散在约 18 个文件**（`OperatingSystem.Is*` / `#if ANDROID`）：README 已声明「操作系统判断只用于生成提示文案」，但 `GitLocator` / `FileOpener` / 平台适配类仍含大量平台分支。下一步可引入 `IPlatformServices`。
5. **窗口尺寸策略**：桌面对话框首帧锁定尺寸（`FitsOverlayViewport`），高级设置展开后的内容全部依赖内部 `ScrollViewer`。若用户把窗口拉得很小，仍会觉得「内容看不全」；这是设计取舍（避免收起时出现大片空白），不是遮挡 bug。
6. **滚动定位依赖 300ms 延时与 `Matrix.M32`**：极慢机器或 Avalonia 升级改变展开动画 / 变换实现时，可能在 Extent 稳定前设置 Offset，导致标题仍停在中段。排查：调大 `ScrollAdvancedIntoViewAsync` 的延时，或改为监听 `ScrollViewer.Extent` 稳定后再定位；`catch` 会静默失败，需在日志中留意。
7. **无自动化测试项目**：重构后回归依赖脚本与人工截图，容易漏掉服务层边界条件。
8. **多目标构建与 csproj 回写**：IDE 会回写 / 清空 csproj，改动 csproj 必须走命令行中转文件；多目标命令必须显式 `-f`，`dotnet publish` 不能同时带 `-r` 与多 TFM；桌面 RID 泄漏到移动 TFM 会触发 `XA0035`；Android minSdk 23（.NET 10）；iOS 构建需 macOS；Android 只保留单个 RID `android-arm64`。
9. **中文与编码**：PowerShell 5.1 读取含中文注释的 `.ps1` 需要 UTF-8 BOM（因此 `TestSmoke.ps1` 注释与输出全用英文）；git 子进程输出按 UTF-8 解码，否则 Windows 中文日志乱码。
10. **`ConsoleAttach` 与输出捕获**：CLI / MCP 人类可读模式在 stdout 未重定向时会附加父控制台，脚本中直接用 `& $exe ...` 赋给变量可能拿不到输出。必须用 `Start-Process -RedirectStandardOutput` 或真实 stdio 管道（`TestSmoke.ps1` 已处理）。
11. **文档与代码漂移**：本文的边界与用例基于 2026-11-13 的版本；新增服务、改动工具清单数量或平台策略时，需同步更新两份文档与脚本中的断言（如「14 个工具」）。
12. **窄屏分支未实机验证**：桌面 `MainWindow` 最小宽度 900，无法缩到 700 阈值触发窄屏布局；Android 仅编译通过。列表自适应高度、外层统一滚动与 240px 日志视口需在真机 / 模拟器复验。
13. **日志 60ms 合并窗口**：刚写日志立即读 `TxtLog.Text` 可能少几行，复制与关闭已显式 `FlushLog()`；超过 64KB 会裁掉旧日志，复制全文不包含更早内容。
14. **后台加载的跨线程访问**：`Task.Run` 内服务读取 git 环境快照并写设置文件，与 UI 线程的设置写回理论上可能并发；多实例同时运行仍应错开写操作。

### 3.3 排查顺序建议

1. 先跑 `pwsh scripts/TestSmoke.ps1 -IncludeAndroid`，区分是构建问题还是运行时问题。
2. CLI/MCP 异常 → 看 `env` 的 git 快照与 `settingsFile` 路径，确认 git 来源与设置文件位置。
3. UI 异常 → 用 UIA 截图对比「收起 / 展开」两态，检查 `ScrollViewer.Offset` 与入口矩形是否越过按钮栏。
4. 涉及配置写入 → 先备份 `settings.json` 与 `multigitcommit.config.json`，确认没有并发写入。

## 4. 维护约定

| 改动 | 需同步 |
| --- | --- |
| 新增 / 删除 MCP 工具 | `docs/boundaries-and-scope.md`（安全边界）、`scripts/TestSmoke.ps1`（工具数量断言）、`README.md` 工具表、`skill/multigitcommit/references/cli.md` |
| 修改启动仓库 / 最近项目规则 | `StartupRepoResolver` / `AppSettingsService`、`docs` 两份文档、UI 截图验证 |
| 修改远端 URL 规则 | `RemoteUrlPolicy`、MCP 与 GUI 两侧用例、`scripts/TestSmoke.ps1` 的 URL 断言 |
| 修改对话框尺寸 / 响应式阈值 | `GitSettingsView`、`UiDialogs.ConfigureDesktopWindowSize`、`DialogOverlay.PushAsync`、UI 实跑截图 |
| 改日志输出 / 仓库加载 / 远端列表布局 | `MainView.axaml(.cs)`、冒烟脚本、UIA 实跑截图（远端项数 + 日志长度）、本文 1.2 / 3.1 章节 |
| 升级 .NET / Avalonia | 逐 TFM 构建 + 冒烟脚本 + 移动端真机；核对 `Matrix` / `Offset` / `Expander` 动画行为 |
