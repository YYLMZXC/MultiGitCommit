# MultiGitCommit

个人自用的 Git 一键提交推送工具（WPF，仅 Windows 平台）。

选择本地 Git 仓库后，自动加载该仓库专属的远端列表，一键完成 **提交 + 推送到多个远端平台**（Gitee、GitHub、cnb.cool 等），提交信息自动携带本地时间戳。

## 功能特性

- **多远端推送**：一个 Git 仓库可配置 1 个或多个推送远端地址，推送时逐个执行，单个远端失败不影响其余远端。
- **每个仓库独立配置**：远端列表、复选框状态保存在各自仓库根目录的 `multigitcommit.config.json`，切换仓库自动加载对应项目的配置。
- **固定提交格式**：commit 消息固定为 `yyyy-MM-dd HH:mm:ss 用户备注`；备注为空时仅使用本地时间戳。
- **动态分支**：自动获取当前分支名推送，不硬编码 main/master。
- **空提交防护**：工作区无变更时自动跳过 `git commit`，不生成空提交。
- **手动命令执行**：可在当前仓库目录执行任意 git/cmd 命令，stdout/stderr 全量输出日志。
- **中文不乱码**：进程调用统一 UTF-8 编码。
- **不依赖系统 git**：内置【Git 环境】设置，支持三种 git.exe 来源——自动检测（软件目录便携版 Git → 常见安装位置 → 系统 PATH）、仅系统 PATH（环境 git）、指定 git.exe / Git 目录（软件自带、便携版 Git）；配置保存到 `%APPDATA%\MultiGitCommit\settings.json`，切换来源无需重装系统 git。

## 环境要求

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（用于构建）
- Git（满足任一即可，无需加入系统 PATH）：
  - 系统已安装 Git（自动检测或使用 PATH）；
  - 或将便携版 Git（如 PortableGit）解压到软件目录的 `git\` 子目录；
  - 或在程序内点击【Git 环境】按钮指定任意 git.exe / Git 目录。

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

1. 点击【选择文件夹】，选择任意 Git 仓库根目录（与 `.git` 同级）。
2. 在【远端地址管理】中添加远端 URL（支持 ssh/https 格式，如 `git@gitee.com:user/repo.git`、`https://github.com/user/repo.git`），可添加多个。
3. 填写提交备注（可选），按需勾选 `git add .` 与 `--tags` 推送标签。
4. 点击【执行提交并推送全部远端】，日志区实时查看每一步命令与结果。

## 配置文件说明

每个 Git 仓库根目录下会生成独立的配置文件 **`multigitcommit.config.json`**（与 `.git` 文件夹同级），字段如下：

| 字段 | 类型 | 说明 |
|---|---|---|
| `RemoteUrls` | string[] | 推送远端 URL 列表 |
| `CheckAddAll` | bool | 是否执行 `git add .` |
| `CheckPushTags` | bool | 是否推送标签 `--tags` |

Git 来源等全局设置（与仓库无关）保存在 **`%APPDATA%\MultiGitCommit\settings.json`**：

| 字段 | 类型 | 说明 |
|---|---|---|
| `GitSource` | int | git.exe 来源：`0` 自动检测 / `1` 系统 PATH / `2` 指定路径 |
| `CustomGitPath` | string | 指定模式下的 git.exe 或 Git 根目录路径 |

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
└── GitSettingsWindow.xaml(.cs) # Git 环境设置窗口（来源选择/路径浏览/测试）
```
