# MultiGitCommit（macOS 版）

Windows WPF 版（仓库根目录 `src/`）的 macOS 对应实现：**C# + .NET 10 + Avalonia 11**。
功能、界面布局、配置文件与 Windows 版一一对应，**每个仓库的 `multigitcommit.config.json` 两平台完全通用**。

选择本地 Git 仓库后，自动加载该仓库专属的远端列表，一键完成 **提交 + 推送到多个远端平台**（Gitee、GitHub、cnb.cool 等），提交信息自动携带本地时间戳。

## 功能特性

- **克隆项目**：输入远端 URL 一键 `git clone`，支持指定分支（`--branch`）、递归子模块（`--recursive`）、浅克隆（`--depth 1`）；目录名自动从 URL 推导，克隆完成后自动切换为该仓库，并可把来源 URL 直接写入推送远端列表。
- **多远端推送**：一个仓库可配置 1 个或多个推送远端，逐个执行，单个远端失败不影响其余远端。
- **每个仓库独立配置**：远端列表、复选框状态保存在各自仓库根目录的 `multigitcommit.config.json`，切换仓库自动加载。
- **固定提交格式**：commit 消息固定为 `yyyy-MM-dd HH:mm:ss 用户备注`；备注为空时仅使用本地时间戳。
- **动态分支**：自动获取当前分支名推送，不硬编码 main/master。
- **空提交防护**：工作区无变更时自动跳过 `git commit`；勾选【强制提交】可忽略（`--allow-empty`）。
- **强制推送**：勾选【强制推送】后推送命令追加 `--force`（谨慎使用）。
- **手动命令执行**：在当前仓库目录执行任意 git/shell 命令，stdout/stderr 全量输出到日志。
- **仓库初始化 / 默认分支**：`git init` + 指定默认分支，空仓库直接设置 HEAD 指向。
- **中文不乱码**：进程调用统一 UTF-8 编码。
- **不依赖系统 git**：内置【Git 环境】设置，支持四种来源——自动检测（软件内嵌 Git → 常见安装位置 → 系统 PATH）、系统 PATH、指定路径、软件内嵌 Git。

## 环境要求

- macOS（Apple Silicon / Intel 均可）
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（用于构建）
- Git（满足任一即可）：
  - 系统已安装 Git（Xcode 命令行工具 `xcode-select --install`，或 `brew install git`）——推荐；
  - 或在【Git 环境】中指定任意 git 可执行文件 / Git 根目录；
  - 或把 Git 发行包解压到 `mac/src/git/`（结构：`git/bin/git`、`git/libexec/git-core/...`），构建时自动复制到产物目录，程序优先使用。

## 构建与运行

```bash
cd mac/src
dotnet build
dotnet run                 # 直接运行
```

产物：`mac/src/bin/Debug/net10.0/MultiGitCommit`（可执行入口为 `MultiGitCommit.dll`，用 `dotnet MultiGitCommit.dll` 启动）。

## 打包为 .app 应用包

```bash
cd mac/src
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishTrimmed=false -o ../publish
# Intel Mac 将 -r 改为 osx-x64
open ../publish            # 得到 MultiGitCommit.app
```

> 若 `mac/src/git/` 中放有 Git，会被一同复制到 `.app/Contents/Resources/git`，
> 程序启动时 `GitLocator` 会优先使用该内嵌 Git（候选路径含 `<app>/Contents/Resources/git/bin/git`）。

## 使用说明

1. 本地还没有仓库？点击【克隆项目】，填写远端 URL、本地父目录与目录名（目录名默认按 URL 自动推导），可选指定分支 / 递归子模块 / 浅克隆，确认后自动克隆并切换到该仓库。
2. 点击【选择文件夹】，选择任意 Git 仓库根目录（与 `.git` 同级）。
3. 在【远端地址管理】中添加远端 URL（ssh/https 均可），可添加多个。
4. 填写提交备注（可选），按需勾选 `git add .`、`--tags` 推送标签、【强制提交】与【强制推送】。
5. 点击【执行提交并推送全部远端】，日志区实时查看每一步命令与结果。

## 非交互执行与超时保护

GUI 环境下 git / ssh 若要「交互式」等待输入（远端账号密码、ssh 口令、主机密钥 yes/no 确认、GPG 签名口令），
进程会永久阻塞——表现为【执行提交并推送全部远端】按钮一直灰着不恢复。为此本工具做两件事：

1. **非交互执行**：子进程 stdin 立即关闭（读到 EOF），并设置 `GIT_TERMINAL_PROMPT=0`、
   `GIT_SSH_COMMAND="ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new"`。
   需要交互时 git 直接**报错失败**并在日志里给出原因，而不是挂起。
   凭据请提前配置好：https 用 macOS 钥匙串（`git credential-osxkeychain`），ssh 用已加载的密钥（`ssh-add`）。
2. **超时兜底**：快速校验类命令 20 秒、普通 git 命令 10 分钟、克隆 30 分钟，
   超时即终止进程并写日志，按钮必定恢复可用。执行期间按钮文案变为「执行中…」。

> 若某次推送日志出现 `Permission denied (publickey)` / `Could not read from remote repository`，
> 说明本机没有可用的远端凭据，先在终端里用一次 `ssh -T git@github.com` 之类的命令验证密钥即可。

## 配置文件说明

每个 Git 仓库根目录下的 **`multigitcommit.config.json`**（与 Windows 版字段完全一致，可跨平台共用）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `RemoteUrls` | string[] | 推送远端 URL 列表 |
| `CheckAddAll` | bool | 是否执行 `git add .` |
| `CheckPushTags` | bool | 是否推送标签 `--tags` |
| `CheckForceCommit` | bool | 是否强制提交（追加 `--allow-empty`） |
| `CheckForcePush` | bool | 是否强制推送（追加 `--force`） |

Git 来源等全局设置保存在 **`~/Library/Application Support/MultiGitCommit/settings.json`**（Windows 版为 `%APPDATA%\MultiGitCommit\settings.json`）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `GitSource` | int | git 来源：`0` 自动检测 / `1` 系统 PATH / `2` 指定路径 / `3` 软件内嵌 |
| `CustomGitPath` | string | 指定模式下的 git 路径或 Git 根目录 |

> **注意**
> - 本工具**不会修改** git 原生的 `.git/config`；远端地址只保存在上述工具独立配置文件中。
> - 推送时直接使用 `git push {url} {branch}`，**不操作 `git remote` 命令**。
> - 每个 Git 仓库拥有独立配置，切换仓库自动加载对应项目的远端列表。

## 与 Windows 版的差异（实现层面）

| 项 | Windows（WPF） | macOS（Avalonia） |
|---|---|---|
| UI 框架 | WPF + WinForms 对话框 | Avalonia 11（`Avalonia.Desktop`），`StorageProvider` 文件/文件夹选择 |
| 分组容器 | `GroupBox` | Avalonia 无 `GroupBox`，改用 `Border` + 标题 `TextBlock` |
| 字体 | Microsoft YaHei UI / Consolas | PingFang SC / Menlo |
| 弹窗 | `MessageBox` | 自建 `Dialogs`（提示 / 确认 / 输入） |
| 手动命令 | `cmd.exe /c chcp 65001 & ...` | `/bin/bash -lc "..."`（登录 shell，可拿到 Homebrew PATH） |
| 打开浏览器 | `Process.Start(url)` | `open <url>` |
| git 候选位置 | Program Files / scoop / winget / 软件目录 `git\cmd\git.exe` | `/opt/homebrew`、`/usr/local`、`/usr/local/git`、`/usr/bin`（Xcode CLT）、MacPorts、Nix、`.app/Contents/Resources/git` |
| 全局设置路径 | `%APPDATA%\MultiGitCommit` | `~/Library/Application Support/MultiGitCommit` |

## 技术说明

- Avalonia 原生控件 + 事件驱动后台代码，无 MVVM 框架、无第三方 Git NuGet 包。
- JSON 使用内置 `System.Text.Json`。
- 全部 Git 操作通过调用 git 命令行完成：`GitLocator` 解析 git 来源，并把 git 所在目录与 macOS 常见命令目录注入子进程 PATH
  （GUI 应用从 Finder/Dock 启动时 PATH 通常只有 `/usr/bin:/bin:/usr/sbin:/sbin`，需显式补充 `/opt/homebrew/bin`、`/usr/local/bin` 等）。

## 项目结构

```
mac/src/
├── Program.cs                     # 入口，Avalonia 应用构建
├── App.axaml / App.axaml.cs       # 应用定义（Fluent 主题）
├── MainWindow.axaml(.cs)          # 主界面与全部事件驱动逻辑
├── CloneWindow.axaml(.cs)         # 克隆项目窗口
├── GitSettingsWindow.axaml(.cs)   # Git 环境设置窗口（来源选择/浏览/测试）
├── AboutWindow.axaml(.cs)         # 关于窗口
├── Dialogs.cs                     # 自建提示 / 确认 / 输入对话框
├── ProjectConfig.cs               # 项目配置读写（multigitcommit.config.json）
├── GitLocator.cs                  # git 定位/校验 + 全局设置持久化
└── res/icon.png                   # 应用图标
```


t