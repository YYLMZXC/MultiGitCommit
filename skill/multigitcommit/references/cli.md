# MultiGitCommit CLI 完整参考

CLI 与 GUI / MCP 共用同一套业务实现、同一份全局 `settings.json` 和每仓库 `multigitcommit.config.json`；输出全部为 JSON 文本（`ok` 字段表示成败，git 命令日志在 `log` 字段并已脱敏）。退出码：0 成功，1 执行失败，2 用法错误。

## 通用约定

- `[路径]` 可省略：依次回退到软件设置里的「上次使用的仓库」、当前工作目录；传仓库根目录或其任意子目录均可。
- 写命令（push / pull / clone / init / identity set）在命令行执行即视为人工确认（等价 MCP 的 `confirm:true`）。
- 本 exe 不带参数启动 = 图形界面；带 `--stdio` 等参数 = MCP Server；带子命令 = CLI。

## 命令一览

| 命令 | 工具名（MCP 同名） | 类型 |
| --- | --- | --- |
| `env` | get_git_environment | 只读 |
| `repos [父目录] [--depth N] [--limit N]` | find_repos | 只读 |
| `status [路径]` | get_repo_status | 只读 |
| `config [路径]` | get_config | 只读 |
| `history [路径] [--limit N] [--show 哈希]` | get_history | 只读 |
| `identity get [路径]` | get_identity | 只读 |
| `identity set <用户名> <邮箱> [--path 路径] [--global]` | set_identity | 写入 |
| `run [路径] <git 参数...>` | run_git | 只读（白名单） |
| `push [路径] -m <备注> [选项]` | commit_and_push | 写入 |
| `pull [路径] [--url 远端] [--strategy ff_only\|rebase\|merge]` | pull | 写入 |
| `clone <url> <父目录> [目录名] [--branch 分支] [--recursive] [--shallow]` | clone_repo | 写入 |
| `init <路径> [--branch 分支]` | init_repo | 写入 |

## 各命令选项

### push（核心流程：add → commit → 逐个推送全部远端）

| 选项 | 说明 |
| --- | --- |
| `-m <备注>` | 必填。提交消息 = 本地时间戳 + 备注（最长 500 字符，不能含换行） |
| `--add-all` | 覆盖配置：执行 `git add .` |
| `--file <相对路径>` | 可重复；只暂存列出的文件（仓库内相对路径，不能越出仓库） |
| `--tags` | 推送时追加 `--tags` |
| `--empty` | 工作区无改动时仍生成空提交 `--allow-empty` |
| `--remote <url>` | 可重复；本次只推送到这些远端（不写回配置文件） |
| `--apply-identity` | 提交身份缺失时，自动把软件设置里保存的身份写入本仓库 |

未传的选项沿用该仓库 `multigitcommit.config.json` 的默认值。永不使用 `--force`。

### pull

- 默认取配置里的第一个远端；`--url` 可临时指定。
- `--strategy`：`ff_only`（默认）/ `rebase` / `merge`。

### run

- 第一个「已存在的目录」当作仓库路径，其余拼成 git 参数，例如：
  `run d:/sc/foo status --short`、`run log -n 5 --oneline`。
- 只允许只读子命令白名单（status / log / diff / show / branch / rev-parse 等）；
  禁止 shell 元字符、`git -c`、`config --global` 写用法；20 秒超时。

### status 输出要点

`branch`、`changedFileCount`、`files[]`（path / staged / untracked）、`identity`（complete 标志）、`config`（remoteUrls 与四个开关）。

### history

- 默认最近 20 条（最大 200）；`--show <哈希>` 改为列出该提交的改动文件。

## MCP 模式（备选接口）

同一个 exe 带 `--stdio` 参数即为 MCP Server，14 个工具与上表一一对应（名称见「工具名」列），
适合 Claude Desktop / Cursor / Cline / CodeBuddy 等客户端注册使用：

```json
{ "mcpServers": { "multigitcommit": { "command": "<MGEXE 路径>", "args": ["--stdio"] } } }
```

- `--readonly` / 环境变量 `MULTIGITCOMMIT_MCP_READONLY=1`：隐藏全部写工具。
- 自检：`MultiGitCommit.exe --self-test`（握手 / 工具清单 / git 环境）；`--list-tools` 打印全部工具。
- 全局设置与仓库配置和 GUI / CLI 完全共用；GUI 与 MCP 同时写 `settings.json` 可能互相覆盖，建议错开写操作。

## 排障

1. `env`：确认 `git.available`、git 路径、设置文件位置。
2. `status`：确认仓库有效、身份齐全、远端非空。
3. 失败结果中的 `log` 字段是逐条脱敏的 git 命令日志；`error` 字段是人类可读原因。
4. push 报「无远端」时：该仓库的 `multigitcommit.config.json` 没有配置远端，需在 GUI 里添加（CLI 不提供写远端的命令，避免误改）。
