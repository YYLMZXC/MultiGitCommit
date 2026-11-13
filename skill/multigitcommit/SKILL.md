---
name: multigitcommit
description: 通过 MultiGitCommit 的 CLI 调用本机多远端 Git 一键提交推送工具：一次提交推送到全部远端、拉取、克隆、初始化仓库、查询状态/历史/配置/身份。This skill should be used when the user asks to commit and push (especially to multiple configured remotes), pull, clone, or init a Git repo via MultiGitCommit, inspect repo status / history / commit identity, or mentions multigitcommit.config.json / MultiGitCommit.exe.
---

# MultiGitCommit

多远端 Git 一键提交推送工具的命令行入口。核心能力：一次 `push` 同时提交并推送到 `multigitcommit.config.json` 里配置的**全部远端**；所有输出为 JSON 文本，适合 AI 解析。

完整命令参考：[references/cli.md](references/cli.md)（各命令选项、JSON 输出字段、MCP 模式说明）。

## 定位 CLI

按顺序确定可执行文件路径 `MGEXE`：

1. 环境变量 `MULTIGITCOMMIT_CLI` 指向的 `MultiGitCommit.exe`；
2. 默认发布路径：`D:/sc/MultiGitCommit/src/MultiGitCommit.Avalonia/bin/Release/net10.0/win-x64/publish/MultiGitCommit.exe`；
3. 都不存在时告知用户先发布：`dotnet publish src/MultiGitCommit.Avalonia/MultiGitCommit.csproj -f net10.0 -c Release`（项目根目录下执行）。

调用方式：`& MGEXE <命令> [参数]`。退出码 0=成功、1=执行失败、2=用法错误。`[路径]` 省略时依次回退：软件设置里的上次仓库 → 当前工作目录。

## 命令速查

| 命令 | 说明 |
| --- | --- |
| `status [路径]` | 分支 / 变更文件 / 提交身份 / 配置摘要（写操作前必看） |
| `push [路径] -m <备注>` | **核心**：add → commit（本地时间戳 + 备注）→ 推送全部远端 |
| `pull [路径]` | 从第一个远端拉取（默认 ff_only） |
| `history [路径]` / `--show 哈希` | 提交历史 / 某提交的改动文件 |
| `config [路径]` | 读取该仓库的远端列表与四个开关 |
| `repos [父目录]` | 扫描本地 Git 仓库 |
| `run [路径] <git 参数...>` | 只读白名单 git 命令（status/log/diff/branch 等） |
| `identity get / set` | 查看 / 写入提交身份 |
| `clone <url> <父目录> [目录名]` | 克隆（可 --branch / --recursive / --shallow） |
| `init <路径>` | 初始化新仓库（可 --branch） |
| `env` | git 环境 / 设置文件位置（排障第一步） |

## 典型工作流

### 提交并推送（写操作，先向用户复述将要执行的动作）

1. `status` 确认变更文件与身份（`identity.complete` 为 false 时先 `identity set`）；
2. `config` 确认远端列表非空（无远端时 push 会失败，需先在 GUI 里配置）；
3. `push <路径> -m "<一句话备注>"`；需要选择性提交时用 `--file <相对路径>`，全量暂存用 `--add-all`；
4. 失败时读取结果里的 `log` 字段（已脱敏的 git 命令日志）定位原因。

### 拉取 / 排查

- `pull` 失败于分叉时改用 `pull --strategy rebase` 或 `merge`，不建议随意改本地。
- 排障顺序：`env` → `status` → `run <路径> log -n 5 --oneline`。

## 安全约束

- CLI 写命令（push / pull / clone / init / identity set）**执行即生效**，执行前必须向用户确认。
- `run` 只允许只读 git 子命令白名单，禁止一切写操作、shell 元字符、`git -c`，20 秒超时。
- 提交消息固定为「本地时间戳 + 备注」，不能自定义完整消息；永不强制推送。
- 不读取、不返回任何保存的凭据；输出中的 URL 令牌已自动脱敏。
