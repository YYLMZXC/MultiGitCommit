# MultiGitCommit

个人自用的 Git 一键提交推送工具。

- Windows 版（WPF）：本目录的 `src/`，见下方全部说明。
- macOS 版（C# + .NET 10 + Avalonia 11）：[`mac/`](mac/README.md)，功能与配置文件与 Windows 版完全通用。

选择本地 Git 仓库后，自动加载该仓库专属的远端列表，一键完成 **提交 + 推送到多个远端平台**（Gitee、GitHub、cnb.cool 等），提交信息自动携带本地时间戳。

## 功能特性

- **克隆项目**：输入远端 URL 一键 `git clone` 到本地，支持指定分支（`--branch`）、递归子模块（`--recursive`）、浅克隆（`--depth 1`）；目录名自动从 URL 推导，克隆完成后自动切换为该仓库，并可把来源 URL 直接写入推送远端列表，一键完成「克隆 → 提交 → 推送」闭环。
- **多远端推送**：一个 Git 仓库可配置 1 个或多个推送远端地址，推送时逐个执行，单个远端失败不影响其余远端。
- **每个仓库独立配置**：远端列表、复选框状态保存在各自仓库根目录的 `multigitcommit.config.json`，切换仓库自动加载对应项目的配置。
- **固定提交格式**：commit 消息固定为 `yyyy-MM-dd HH:mm:ss 用户备注`；备注为空时仅使用本地时间戳。
- **动态分支**：自动获取当前分支名推送，不硬编码 main/master。
- **空提交防护**：工作区无变更时自动跳过 `git commit`，不生成空提交；勾选【强制提交】可忽略该防护（`--allow-empty`）。
- **强制推送**：勾选【强制推送】后推送命令追加 `--force`，用于非快进被拒、变基/回退后覆盖远端历史（谨慎使用）。
- **手动命令执行**：可在当前仓库目录执行任意 git/cmd 命令，stdout/stderr 全量输出日志。
- **仓库初始化 / 默认分支**：`git init` + 指定默认分支，空仓库直接设置 HEAD 指向；输入仓库子目录时自动定位到真正的仓库根目录。
- **推送凭据（账号）**：在【Git 环境】中按主机保存平台用户名 + 访问令牌，推送 https 远端时自动注入 URL，无需每次输入账号（详见下方【推送凭据】）。
- **非交互执行 + 超时兜底**：不弹窗等待账号/口令，快速命令 20 秒、普通 git 命令 10 分钟、克隆 30 分钟超时即终止，按钮必定恢复（详见下方【非交互执行与超时保护】）。
- **中文不乱码**：进程调用统一 UTF-8 编码。
- **不依赖系统 git**：内置【Git 环境】设置，支持四种 git.exe 来源——自动检测（软件内嵌便携版 Git → 常见安装位置 → 系统 PATH）、仅软件内嵌便携版 Git、仅系统 PATH（环境 git）、指定 git.exe / Git 目录（软件自带、便携版 Git）；配置保存到 `%APPDATA%\MultiGitCommit\settings.json`，切换来源无需重装系统 git。

## 环境要求

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（用于构建）
- Git（满足任一即可，**最终用户无需额外下载 git**）：
  - 开发者构建前运行一次 `download-portable-git.ps1`，内嵌便携版 MinGit（推荐，见下方【内嵌便携版 Git】）；
  - 或系统已安装 Git（自动检测或使用 PATH）；
  - 或将便携版 Git（如 PortableGit / MinGit）解压到软件目录的 `git\` 子目录；
  - 或在程序内点击【Git 环境】按钮指定任意 git.exe / Git 目录。

## 内嵌便携版 Git（免安装分发）

仓库根目录提供 `download-portable-git.ps1`，一键下载官方 **MinGit**（Git for Windows 免安装精简版，约 45MB）并解压到 `src\git\`：

```powershell
# 依次尝试华为云 / npmmirror / GitHub 三个镜像，可指定版本
.\download-portable-git.ps1
.\download-portable-git.ps1 -Version 2.50.1
```

之后 `dotnet build` / `dotnet publish` 会自动把 `src\git\` 复制到输出目录的 `git\` 子目录（与 exe 同级）。程序启动时 `GitLocator` 按 **软件目录便携版 → 常见安装位置 → 系统 PATH** 的优先级自动检测，优先使用内嵌的 MinGit，最终用户机器上无需安装任何 git；ssh 推送所需的附带程序（`ssh.exe` 等）也已随包提供。

> `src\git\` 已在 `.gitignore` 中忽略，不会提交到仓库；分发时将 publish 输出整个目录打包即可。

## 构建与运行

```powershell
cd src
dotnet build
```

运行程序：

```powershell
.\bin\Debug\net10.0-windows\MultiGitCommit.exe
```

或直接双击 exe（在 `src\bin\Debug\net10.0-windows\` 目录下）。

## 使用说明

1. 本地还没有仓库？点击【克隆项目】，填写远端 URL、本地父目录与目录名（目录名默认按 URL 自动推导），可选指定分支 / 递归子模块 / 浅克隆，确认后自动克隆并切换到该仓库。
2. 点击【选择文件夹】，选择任意 Git 仓库根目录（与 `.git` 同级）。
3. 在【远端地址管理】中添加远端 URL（支持 ssh/https 格式，如 `git@gitee.com:user/repo.git`、`https://github.com/user/repo.git`），可添加多个。
4. 填写提交备注（可选），按需勾选 `git add .`、`--tags` 推送标签、【强制提交 `--allow-empty`】与【强制推送 `--force`】。
5. 点击【执行提交并推送全部远端】，日志区实时查看每一步命令与结果（执行期间按钮显示「执行中…」）。

## 非交互执行与超时保护

GUI 环境下 git / ssh 若要「交互式」等待输入（远端账号密码、ssh 口令、主机密钥 yes/no 确认、GPG 签名口令），
进程会永久阻塞——表现为【执行提交并推送全部远端】按钮一直灰着不恢复。为此本工具做两件事：

1. **非交互执行**：子进程 stdin 立即关闭（读到 EOF），并设置 `GIT_TERMINAL_PROMPT=0`、
   `GIT_SSH_COMMAND="ssh.exe -o BatchMode=yes -o StrictHostKeyChecking=accept-new"`（仅在能找到 ssh.exe 时设置，
   否则保留 git 自身的 ssh 查找逻辑）。需要交互时 git 直接**报错失败**并在日志里给出原因，而不是挂起。
   凭据请提前配置好：https 用【推送凭据】里保存的用户名 + 访问令牌（见下节），ssh 用已加载的密钥。
2. **超时兜底**：快速校验类命令 20 秒、普通 git 命令 10 分钟、克隆 30 分钟，
   超时即终止进程并写日志，按钮必定恢复可用。执行期间按钮文案变为「执行中…」。

> 若某次推送日志出现 `Permission denied (publickey)` / `Could not read from remote repository`，
> 说明本机没有可用的远端凭据；https 远端请到【Git 环境】→【推送凭据】配置账号，ssh 远端请先配置好密钥。

## 推送凭据（https 远端）

工具为非交互执行，`GIT_TERMINAL_PROMPT=0` 会禁止 git 弹窗询问账号，
所以 https 远端**必须**预先提供凭据，否则推送必然失败：

```
fatal: could not read Username for 'https://cnb.cool': terminal prompts disabled
```

在【Git 环境】窗口的「推送凭据」区域按主机配置，可配多条（cnb.cool / gitee.com / github.com 各一条）：

| 项目 | 说明 |
|---|---|
| 主机 | 远端主机名，如 `cnb.cool`；也可直接粘贴完整 URL |
| 用户名 | 平台账号名 |
| 令牌 | 平台生成的访问令牌（PAT），比填登录密码更安全（输入时以掩码显示） |

推送时程序自动把凭据注入 URL（`https://user:token@host/...`），**日志中的令牌一律显示为 `***`**。
ssh 远端（`git@host:...`）不走凭据，使用 ssh 密钥。

> **凭据不会被提交到仓库**：它只保存在**仓库之外**的 `%APPDATA%\MultiGitCommit\settings.json`，
> 仓库内的 `multigitcommit.config.json` 不含任何凭据字段，日志里的令牌一律显示为 `***`。
> 因此 `git add .` 与 `git push` 都不会把账号或令牌带到远端；注意不要手工把该文件复制进仓库。

## 配置文件说明

每个 Git 仓库根目录下会生成独立的配置文件 **`multigitcommit.config.json`**（与 `.git` 文件夹同级），字段如下：

| 字段 | 类型 | 说明 |
|---|---|---|
| `RemoteUrls` | string[] | 推送远端 URL 列表 |
| `CheckAddAll` | bool | 是否执行 `git add .` |
| `CheckPushTags` | bool | 是否推送标签 `--tags` |
| `CheckForceCommit` | bool | 是否强制提交（commit 追加 `--allow-empty`，无变更也生成空提交） |
| `CheckForcePush` | bool | 是否强制推送（push 追加 `--force`，覆盖远端历史） |

Git 来源等全局设置（与仓库无关）保存在 **`%APPDATA%\MultiGitCommit\settings.json`**：

| 字段 | 类型 | 说明 |
|---|---|---|
| `GitSource` | int | git.exe 来源：`0` 自动检测 / `1` 系统 PATH / `2` 指定路径 / `3` 软件内嵌便携版 |
| `CustomGitPath` | string | 指定模式下的 git.exe 或 Git 根目录路径 |
| `Credentials` | object[] | https 推送凭据列表：`{ "Host": "cnb.cool", "Username": "...", "Token": "..." }`（按主机匹配，明文保存） |

> **注意**
> - 本工具**不会修改** git 原生的 `.git/config` 文件；远端地址只保存在上述工具独立配置文件中。
> - 推送时直接使用 `git push {url} {branch}`，**不操作 `git remote` 命令**。
> - 每个 Git 仓库拥有独立配置，切换仓库自动加载对应项目的远端列表。

## 技术说明

- WPF 原生控件，事件驱动后台代码，无 MVVM 框架、无第三方 UI/Git NuGet 包。
- JSON 使用内置 `System.Text.Json`。
- 全部 Git 操作通过调用 `git.exe` 命令行完成：由 `GitLocator` 解析 git.exe 来源（自动检测 / 系统 PATH / 指定路径），并将 git 所在目录注入子进程 PATH，便携版 Git 亦可正常工作。
- 文件夹选择使用 `System.Windows.Forms.FolderBrowserDialog`。

## 项目结构

```
src/
├── App.xaml / App.xaml.cs      # 应用入口，全局异常捕获
├── MainWindow.xaml             # 主界面布局
├── MainWindow.xaml.cs          # 全部事件驱动逻辑
├── MultiGitCommit.csproj       # 项目文件（net10.0-windows）
├── ProjectConfig.cs            # 项目配置读写帮助类
├── GitLocator.cs               # git.exe 定位/校验 + 全局设置持久化
├── GitSettingsWindow.xaml(.cs) # Git 环境设置窗口（来源选择/路径浏览/测试）
└── CloneWindow.xaml(.cs)       # 克隆项目窗口（URL/目录/分支/选项 + 命令预览）
```
