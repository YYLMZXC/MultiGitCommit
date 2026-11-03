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
- **启动即自动加载**：打开程序自动定位仓库并载入其 `multigitcommit.config.json`——默认**当前目录优先**：从当前目录（含程序自身目录，IDE 调试 / 双击 exe 常见）向上逐级（最多 12 层）查找 `.git` 或 `multigitcommit.config.json` 定位真正的仓库根目录；当前目录不在仓库内时回退到上次使用过的仓库；都找不到才提示手动选择。可在【Git 环境】→「启动时加载的仓库」中改为「上次使用的仓库优先」。
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
- **推送凭据（账号）**：像【Git 环境】选择 git 来源一样，账号也可**选择来源**——自动检测（自定义优先，缺失时取系统）/ 仅系统凭据 / 仅自定义 / 不使用；系统账号可按需**检测并导入**（先选主机再检测，可随时停止），填写后可用一次 `git ls-remote` 实测账号是否可用（详见下方【推送凭据】）。
- **凭据助手自动适配**：自动扫描当前 git 真正可用的 `credential.helper`（Windows：manager / wincred / store；macOS：manager / osxkeychain / store；Linux：manager / libsecret / gnome-keyring / store）；配置里的助手在当前 git 中不存在时（精简便携版常见），临时用 `-c credential.helper=xxx` 覆盖，不改动用户 git 配置。
- **非交互执行 + 超时兜底**：不弹窗等待账号/口令，快速命令 20 秒、普通 git 命令 10 分钟、克隆 30 分钟超时即终止，按钮必定恢复（详见下方【非交互执行与超时保护】）。
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

JetBrains Rider 可直接使用 `src/MultiGitCommit.Avalonia/.run/` 下的共享运行配置：
`MultiGitCommit (Windows Debug)`、`MultiGitCommit (Windows Release win-x64)`、`MultiGitCommit (macOS Debug)`、`MultiGitCommit (Linux Debug)`。
其中 Release 配置需先在 Rider 中把解决方案配置切到 `Release`。

### 发布（单文件，依赖框架）

Release 默认发布为 **单文件可执行程序**（Avalonia、Skia 等依赖全部打进主程序），输出目录只有主程序（Windows：`MultiGitCommit.exe`；macOS / Linux：`MultiGitCommit`）+ `git/` 目录：

```bash
cd src/MultiGitCommit.Avalonia

# Windows（默认 RID 为 win-x64）
dotnet publish -c Release -r win-x64

# macOS Apple 芯片 / Intel
dotnet publish -c Release -r osx-arm64
dotnet publish -c Release -r osx-x64

# Linux x64 / ARM64
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r linux-arm64
```

> 目标机器需已安装 .NET 10 运行时（非自包含）。需要完全独立分发时，追加 `-p:SelfContained=true` 即可（体积会大很多）。
>
> Linux 产物为可执行文件 `MultiGitCommit`（publish 已带执行位，若经压缩包 / U 盘拷贝丢失权限，执行 `chmod +x MultiGitCommit` 即可），
> 与同目录的 `git/` 子目录一起分发；桌面图标可用任意 `.desktop` 文件指向该可执行文件。

## 使用说明

1. 本地还没有仓库？点击【克隆项目】，填写远端 URL、本地父目录与目录名（目录名默认按 URL 自动推导），可选指定分支 / 递归子模块 / 浅克隆，确认后自动克隆并切换到该仓库。
2. 点击【选择文件夹】，选择任意 Git 仓库根目录（与 `.git` 同级）；输入子目录时会自动回填真正的仓库根目录。
3. 在【推送远端地址管理】中添加远端 URL（支持 ssh/https 格式，如 `git@gitee.com:user/repo.git`、`https://github.com/user/repo.git`），可添加多个，也可【在浏览器中打开】查看。
4. 填写提交备注（可选），按需勾选 `git add`（勾选后提交前会弹窗列出工作区变更，自己勾选要添加的文件）、`--tags` 推送标签、【强制提交 `--allow-empty`】与【强制推送 `--force`】。
5. 点击【执行提交并推送全部远端】，日志区实时查看每一步命令与结果（执行期间按钮显示「执行中…」）。

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

> **凭据不会被提交到仓库**：它只保存在**仓库之外**的应用设置目录的 `settings.json`
> （Windows：`%APPDATA%\MultiGitCommit\settings.json`；macOS：`~/Library/Application Support/MultiGitCommit/settings.json`；
> Linux：`$XDG_CONFIG_HOME/MultiGitCommit/settings.json`，未设置 XDG_CONFIG_HOME 时为 `~/.config/MultiGitCommit/settings.json`，权限 600），
> 仓库内的 `multigitcommit.config.json` 不含任何凭据字段，日志里的令牌一律显示为 `***`。
> 因此 `git add .` 与 `git push` 都不会把账号或令牌带到远端；注意不要手工把该文件复制进仓库。

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

| 字段 | 类型 | 说明 |
|---|---|---|
| `GitSource` | int | git 来源：`0` 自动检测 / `1` 系统 PATH / `2` 指定路径 / `3` 软件内嵌 Git |
| `CustomGitPath` | string | 指定模式下的 git 可执行文件或 Git 根目录路径（支持 `~` 与环境变量） |
| `Credentials` | object[] | https 推送凭据列表：`{ "Host": "cnb.cool", "Username": "...", "Token": "..." }`（按主机匹配，明文保存） |
| `CredentialSource` | int | 账号来源：`0` 自动检测（自定义优先）/ `1` 仅系统凭据 / `2` 仅自定义 / `3` 不使用 |
| `CommitIdentity` | object | 提交身份：`{ "Name": "...", "Email": "...", "Scope": 0 }`（`Scope`：`0` 仅当前仓库 / `1` 全局） |
| `LastRepoPath` | string | 上次成功加载的仓库根目录，下次启动自动载入其 `multigitcommit.config.json`（失效时自动清除） |
| `StartupRepo` | int | 启动时加载的仓库：`0` 当前目录优先（默认）/ `1` 上次使用的仓库优先 |
| `StageMode` | int | 执行 `git add` 的文件添加方式：`0` 全提交（`git add .`，默认）/ `1` 可选提交（提交前弹窗勾选要添加的文件） |

> **注意**
> - 本工具**不会修改** git 原生的 `.git/config` 文件；远端地址只保存在上述工具独立配置文件中。
> - 推送时直接使用 `git push {url} {branch}`，**不操作 `git remote` 命令**。
> - 每个 Git 仓库拥有独立配置，切换仓库自动加载对应项目的远端列表。

## 技术说明

- **一套代码三个平台**：`net10.0` + Avalonia 11（`Avalonia.Desktop`、`Avalonia.Themes.Fluent`），不依赖 WPF / WinForms；
  平台差异（git 候选路径、设置目录、命令 shell、凭据库）统一收在 `OperatingSystem.IsWindows()` / `IsMacOS()` 分支里。
- 原生控件 + 事件驱动后台代码，无 MVVM 框架、无第三方 Git NuGet 包；JSON 使用内置 `System.Text.Json`。
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
└── src/
    ├── MultiGitCommit.Avalonia/     # 【当前主项目】一套代码同时支持 Windows / macOS / Linux
    │   ├── Program.cs                       # 入口，Avalonia 应用构建
    │   ├── App.axaml / App.axaml.cs         # 应用定义（Fluent 主题）
    │   ├── MainWindow.axaml(.xaml.cs)       # 主界面与全部事件驱动逻辑
    │   ├── CloneWindow.axaml(.xaml.cs)      # 克隆项目窗口（URL/目录/分支/选项 + 命令预览）
    │   ├── GitSettingsWindow.axaml(.cs)     # Git 环境设置窗口（来源选择/浏览/测试 + 提交身份 + 推送凭据）
    │   ├── IdentityWindow.axaml(.cs)        # 提交身份窗口（user.name / user.email + 仓库级/全局）
    │   ├── CredentialDetectWindow.axaml(.cs)# 系统凭据检测窗口（选主机 → 检测（可停止）→ 勾选导入）
    │   ├── HistoryWindow.axaml(.cs)         # 提交历史窗口（浏览 / 重置提交 + 打开该提交修改的文件）
    │   ├── StageFilesWindow.axaml(.cs)      # 选择性 git add 窗口（工作区变更文件勾选）
    │   ├── AboutWindow.axaml(.cs)           # 关于窗口（版本 + 项目地址）
    │   ├── Dialogs.cs                       # 自建提示 / 确认 / 输入对话框
    │   ├── ProjectConfig.cs                 # 每仓库配置读写（multigitcommit.config.json）
    │   ├── GitLocator.cs                    # git 定位/校验 + 全局设置持久化
    │   ├── GitCredentialHelper.cs           # credential.helper 可用性检测与自动覆盖
    │   ├── SystemCredentialProbe.cs         # 系统凭据探测（凭据管理器 / 钥匙串 / Secret Service / .git-credentials，可取消、5 秒超时）
    │   ├── CredentialTester.cs              # 账号验证（git ls-remote 实测，20 秒超时、可取消、输出脱敏）
    │   ├── Services/FileOpener.cs           # 用系统默认程序打开文件 / 在文件管理器中定位（Windows / macOS / Linux）
    │   ├── MultiGitCommit.csproj            # 项目文件（net10.0，RID：win-x64 / osx-arm64 / osx-x64 / linux-x64 / linux-arm64）
    │   └── res/                             # 应用图标（icon.png / icon.ico）
    └── old-version/                 # 已归档的旧版实现（不再更新）
        ├── MultiGitCommit.NET/      # 旧 Windows WPF 版（net10.0-windows）
        └── MultiGitCommit.MACOS/    # 旧 macOS 版（各自独立代码库）
```
