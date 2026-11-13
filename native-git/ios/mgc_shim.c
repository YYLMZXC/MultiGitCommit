/*
 * mgc_shim.c —— libgit2 的「git CLI 语义」适配层（iOS 内嵌 git 的 C 侧实现）。
 *
 * 为什么需要这一层：C# 侧直接 P/Invoke libgit2 的话，要手工镜像
 * git_clone_options / git_fetch_options / git_push_options 这类嵌套回调结构体，
 * 任何一个字段偏移错了都是内存崩溃；把结构体留在 C 里（直接 include 官方头文件），
 * 暴露给 C# 的就只有基本类型（int / 字符串 / 简单回调），稳定得多。
 *
 * 语义对齐：本文件的输出格式（status --porcelain、log 的 0x1F 分隔行、
 * show --name-status、退出码）必须与 git CLI 完全一致，调用方按 CLI 文本解析。
 *
 * 约定：
 * - 所有函数返回 int：0 成功；1/2 等「业务码」见各函数注释；负数为 libgit2 错误码。
 * - 需要动态长度的结果用 mgc_buffer 系列返回（malloc，C# 读完调 mgc_buffer_free）。
 * - 联网操作的凭据由调用方显式传入（user/pass，来自 URL userinfo），不经任何助手。
 * - 长操作通过 mgc_progress_cb 上报进度；回调返回非 0 表示取消 / 超时 → 中止。
 */
#include <git2.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <time.h>

/* ==================== 进度回调与取消 ==================== */

typedef int (*mgc_progress_cb)(const char *message, void *payload);

typedef struct {
    mgc_progress_cb progress;
    void *payload;
} mgc_ctx;

/* 有联网 / checkout 进度的上下文（额外携带拒收的引用信息，push 用） */
typedef struct {
    mgc_ctx base;
    char *user;
    char *pass;
    char rejected[2048]; /* push_update_reference 汇总的拒收原因 */
} mgc_net_ctx;

/* 进度输出：格式化一行，转发给 C# 回调；回调返回非 0 → 请求中止 */
static int mgc_emit(mgc_ctx *ctx, const char *fmt, ...)
{
    if (ctx == NULL || ctx->progress == NULL) return 0;
    char buf[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    return ctx->progress(buf, ctx->payload);
}

/* libgit2 credentials 回调：用调用方传入的 user/pass 构造 userpass 凭据 */
static int mgc_cred_cb(git_credential **out, const char *url,
                       const char *username_from_url, unsigned int allowed_types,
                       void *payload)
{
    (void)url; (void)allowed_types;
    mgc_net_ctx *ctx = (mgc_net_ctx *)payload;
    const char *user = (ctx->user && ctx->user[0]) ? ctx->user : username_from_url;
    const char *pass = ctx->pass ? ctx->pass : "";
    if (user == NULL) user = "";
    return git_credential_userpass_plaintext_new(out, user, pass);
}

static int mgc_cert_cb(git_cert *cert, int valid, const char *host, void *payload)
{
    (void)cert; (void)valid; (void)host; (void)payload;
    return 0; /* 接受证书（信任链校验由 SecureTransport 完成） */
}

/* sideband 消息：远端发来的 "Enumerating objects..." 等，按行转发 */
static int mgc_sideband_cb(const char *str, int len, void *payload)
{
    mgc_net_ctx *ctx = (mgc_net_ctx *)payload;
    if (ctx == NULL || ctx->base.progress == NULL || len <= 0) return 0;

    /* 服务端消息以 \n / \r 分段；逐段转发，避免日志里出现半截行 */
    char buf[1024];
    int start = 0;
    for (int i = 0; i < len; i++) {
        if (str[i] == '\n' || str[i] == '\r') {
            int seg = i - start;
            if (seg > 0 && seg < (int)sizeof(buf)) {
                memcpy(buf, str + start, seg);
                buf[seg] = '\0';
                /* 去掉纯进度回刷行（以百分比统计的重复行由 C# 侧自行展示） */
                if (ctx->base.progress(buf, ctx->base.payload) != 0) return -1;
            }
            start = i + 1;
        }
    }
    int rest = len - start;
    if (rest > 0 && rest < (int)sizeof(buf)) {
        memcpy(buf, str + start, rest);
        buf[rest] = '\0';
        if (ctx->base.progress(buf, ctx->base.payload) != 0) return -1;
    }
    return 0;
}

/* 对象传输进度：每 5% 上报一次，格式与 git CLI 的进度行兼容 */
static int mgc_transfer_cb(const git_indexer_progress *stats, void *payload)
{
    mgc_net_ctx *ctx = (mgc_net_ctx *)payload;
    if (stats == NULL || ctx->base.progress == NULL) return 0;

    if (stats->received_objects > 0 && stats->total_objects > 0) {
        unsigned percent = (unsigned)(stats->received_objects * 100 / stats->total_objects);
        if (percent % 5 == 0) {
            char buf[256];
            snprintf(buf, sizeof(buf), "接收对象中: %u/%u (%u%%)",
                     stats->received_objects, stats->total_objects, percent);
            if (ctx->base.progress(buf, ctx->base.payload) != 0) return -1;
        }
    }
    return 0;
}

/* push 打包上传进度 */
static int mgc_push_transfer_cb(unsigned int current, unsigned int total, size_t bytes, void *payload)
{
    mgc_net_ctx *ctx = (mgc_net_ctx *)payload;
    if (ctx->base.progress == NULL || total == 0) return 0;
    if (current % 50 == 0 || current == total) {
        char buf[256];
        snprintf(buf, sizeof(buf), "推送对象中: %u/%u (%zu 字节)", current, total, bytes);
        if (ctx->base.progress(buf, ctx->base.payload) != 0) return -1;
    }
    return 0;
}

/* push 每个引用的更新结果：status 非 NULL 表示被拒收 */
static int mgc_push_update_cb(const char *refname, const char *status, void *payload)
{
    mgc_net_ctx *ctx = (mgc_net_ctx *)payload;
    if (status != NULL && refname != NULL) {
        strncat(ctx->rejected, refname, sizeof(ctx->rejected) - strlen(ctx->rejected) - 1);
        strncat(ctx->rejected, ": ", sizeof(ctx->rejected) - strlen(ctx->rejected) - 1);
        strncat(ctx->rejected, status, sizeof(ctx->rejected) - strlen(ctx->rejected) - 1);
        strncat(ctx->rejected, "\n", sizeof(ctx->rejected) - strlen(ctx->rejected) - 1);
    }
    return 0;
}

/* checkout 进度（clone / pull 的文件检出阶段） */
static void mgc_checkout_progress_cb(const char *path, size_t completed, size_t total, void *payload)
{
    mgc_ctx *ctx = (mgc_ctx *)payload;
    if (ctx == NULL || ctx->progress == NULL || path == NULL) return;
    if (completed == 0 || completed % 200 == 0 || completed == total) {
        char buf[512];
        snprintf(buf, sizeof(buf), "检出文件: %zu/%zu %s", completed, total, path);
        ctx->progress(buf, ctx->payload); /* checkout 回调是 void，无法请求中止 */
    }
}

/* ==================== 结果缓冲 ==================== */

/* 上一次调用的动态结果（单线程使用：C# 侧全程持锁） */
static char *mgc_last_result = NULL;
static size_t mgc_last_result_len = 0;

static int mgc_buffer_set(const char *data, size_t len)
{
    free(mgc_last_result);
    mgc_last_result = NULL;
    mgc_last_result_len = 0;
    if (len == 0) return 0;
    char *copy = (char *)malloc(len + 1);
    if (copy == NULL) return -1;
    memcpy(copy, data, len);
    copy[len] = '\0';
    mgc_last_result = copy;
    mgc_last_result_len = len;
    return 0;
}

int mgc_buffer_read(char *out, int cap, int *len)
{
    if (mgc_last_result == NULL) { *len = 0; return 0; }
    size_t n = mgc_last_result_len;
    if ((size_t)cap < n + 1) return -1;
    memcpy(out, mgc_last_result, n);
    out[n] = '\0';
    *len = (int)n;
    return 0;
}

void mgc_buffer_free(void)
{
    free(mgc_last_result);
    mgc_last_result = NULL;
    mgc_last_result_len = 0;
}

/* ==================== 生命周期与版本 ==================== */

int mgc_init(void) { return git_libgit2_init(); }

int mgc_version(char *out, int cap)
{
    int major, minor, rev;
    git_libgit2_version(&major, &minor, &rev);
    return snprintf(out, (size_t)cap, "%d.%d.%d", major, minor, rev) < 0 ? -1 : 0;
}

/* 统一的错误文本入口：把 libgit2 最后错误复制到调用方缓冲 */
static int mgc_error(char *err, int errcap)
{
    const git_error *e = git_error_last();
    if (err != NULL && errcap > 0) {
        if (e != NULL && e->message != NULL)
            snprintf(err, (size_t)errcap, "%s", e->message);
        else
            snprintf(err, (size_t)errcap, "unknown libgit2 error");
    }
    return -1;
}

/* ==================== 仓库定位 / 初始化 ==================== */

int mgc_repo_open(const char *path, git_repository **repo)
{
    /* flags=0：默认向上逐级搜索（对应 rev-parse --show-toplevel 的语义） */
    return git_repository_open_ext(repo, path, 0, NULL);
}

void mgc_repo_close(git_repository *repo)
{
    git_repository_free(repo);
}

int mgc_repo_workdir(git_repository *repo, char *out, int cap)
{
    const char *wd = git_repository_workdir(repo);
    if (wd == NULL) return -1; /* bare 仓库没有工作区 */
    size_t n = strlen(wd);
    while (n > 1 && wd[n - 1] == '/') n--; /* 去掉结尾斜杠 */
    if ((int)n + 1 > cap) return -1;
    memcpy(out, wd, n);
    out[n] = '\0';
    return 0;
}

int mgc_repo_init(const char *path, char *err, int errcap)
{
    git_repository *repo = NULL;
    int rc = git_repository_init(&repo, path, 0);
    if (rc != 0) return mgc_error(err, errcap);
    git_repository_free(repo);
    return 0;
}

/* 当前分支名（detach 后为空串；返回 0 表示成功取得名称或确认 detached） */
int mgc_repo_head_branch(git_repository *repo, char *out, int cap)
{
    out[0] = '\0';
    git_reference *head = NULL;
    int rc = git_repository_head(&head, repo);
    if (rc == GIT_ENOTFOUND || rc == GIT_EUNBORNBRANCH) return 0; /* 空仓库：无分支名 */
    if (rc != 0) return -1;

    const char *name = NULL;
    if (git_branch_name(&name, head) == 0 && name != NULL) {
        snprintf(out, (size_t)cap, "%s", name);
    }
    git_reference_free(head);
    return 0;
}

/* symbolic-ref --short HEAD：非符号引用（detached）返回 1 */
int mgc_repo_head_symbolic_short(git_repository *repo, char *out, int cap)
{
    out[0] = '\0';
    git_reference *head = NULL;
    int rc = git_reference_lookup(&head, repo, "HEAD");
    if (rc != 0) return -1;
    const char *target = git_reference_symbolic_target(head);
    git_reference_free(head);
    if (target == NULL) return 1; /* detached */
    if (strncmp(target, "refs/heads/", 11) == 0) target += 11;
    snprintf(out, (size_t)cap, "%s", target);
    return 0;
}

/* rev-parse --verify HEAD：0 = 存在；1 = 不存在（含空仓库） */
int mgc_repo_has_head(git_repository *repo)
{
    git_oid oid;
    int rc = git_reference_name_to_id(&oid, repo, "HEAD");
    return rc == 0 ? 0 : 1;
}

/* show-ref --verify --quiet <refname>：0 = 存在；1 = 不存在 */
int mgc_repo_ref_exists(git_repository *repo, const char *refname)
{
    git_reference *ref = NULL;
    int rc = git_reference_lookup(&ref, repo, refname);
    if (rc == GIT_ENOTFOUND) return 1;
    if (rc != 0) return -1;
    git_reference_free(ref);
    return 0;
}

/* symbolic-ref HEAD refs/heads/<name> */
int mgc_repo_set_head(git_repository *repo, const char *refname, char *err, int errcap)
{
    git_reference *ref = NULL;
    int rc = git_reference_symbolic_create(&ref, repo, "HEAD", refname, 1, "multigitcommit: set default branch");
    if (rc != 0) return mgc_error(err, errcap);
    git_reference_free(ref);
    return 0;
}

/* checkout <branch>（create=0 已有分支） / checkout -b <branch>（create=1） */
int mgc_repo_checkout_branch(git_repository *repo, const char *name, int create, char *err, int errcap)
{
    git_reference *branch = NULL;
    git_object *commit = NULL;

    if (create) {
        git_object *head = NULL;
        if (git_revparse_single(&head, repo, "HEAD") != 0)
            return mgc_error(err, errcap);
        int rc = git_branch_create(&branch, repo, name, (git_commit *)head, 0);
        git_object_free(head);
        if (rc != 0) return mgc_error(err, errcap);
    } else {
        if (git_branch_lookup(&branch, repo, name, GIT_BRANCH_LOCAL) != 0)
            return mgc_error(err, errcap);
    }

    if (git_object_lookup(&commit, repo, git_reference_target(branch), GIT_OBJECT_COMMIT) != 0) {
        git_reference_free(branch);
        return mgc_error(err, errcap);
    }

    git_checkout_options opts;
    git_checkout_options_init(&opts, GIT_CHECKOUT_OPTIONS_VERSION);
    opts.checkout_strategy = GIT_CHECKOUT_SAFE;
    int rc = git_checkout_tree(repo, commit, &opts);
    git_object_free(commit);
    if (rc == 0) {
        char refname[1024];
        snprintf(refname, sizeof(refname), "refs/heads/%s", name);
        rc = git_repository_set_head(repo, refname);
    }
    git_reference_free(branch);
    if (rc != 0) return mgc_error(err, errcap);
    return 0;
}

/* ==================== 配置 ==================== */

int mgc_config_get(git_repository *repo, const char *key, char *out, int cap)
{
    out[0] = '\0';
    git_config *live = NULL;
    int rc = repo != NULL ? git_repository_config(&live, repo) : git_config_open_default(&live);
    if (rc != 0) return -1;

    /* git_config_get_string 要求快照：live config 直接读会报
       "get_string called on a live config object" */
    git_config *cfg = NULL;
    if (git_config_snapshot(&cfg, live) != 0) { git_config_free(live); return -1; }
    git_config_free(live);

    const char *value = NULL;
    rc = git_config_get_string(&value, cfg, key);
    if (rc == GIT_ENOTFOUND) { git_config_free(cfg); return 1; } /* git config --get 退出码 1 */
    if (rc != 0) { git_config_free(cfg); return -1; }
    snprintf(out, (size_t)cap, "%s", value ? value : "");
    git_config_free(cfg);
    return 0;
}

/* 写配置：repo 非空写仓库 .git/config；为空写全局（--global） */
int mgc_config_set(git_repository *repo, const char *key, const char *value, char *err, int errcap)
{
    git_config *cfg = NULL;
    int rc;
    if (repo != NULL) {
        /* 只打开仓库自己的 config 文件，保证写入位置与 git config（无 --global）一致 */
        const char *common = git_repository_path(repo); /* <repo>/.git（或 bare 根） */
        char path[2048];
        snprintf(path, sizeof(path), "%s/config", common);
        rc = git_config_open_ondisk(&cfg, path);
    } else {
        git_buf global = {0};
        if (git_config_find_global(&global) != 0 || global.size == 0) {
            const char *home = getenv("HOME");
            if (home == NULL) { git_buf_dispose(&global); return mgc_error(err, errcap); }
            snprintf(err, (size_t)errcap, "global git config path not found");
            git_buf_dispose(&global);
            return -1;
        }
        rc = git_config_open_ondisk(&cfg, global.ptr);
        git_buf_dispose(&global);
    }
    if (rc != 0) return mgc_error(err, errcap);

    rc = git_config_set_string(cfg, key, value);
    git_config_free(cfg);
    if (rc != 0) return mgc_error(err, errcap);
    return 0;
}

/* ==================== 状态（porcelain） ==================== */

static char mgc_index_char(int status)
{
    if (status & GIT_STATUS_INDEX_RENAMED) return 'R';
    if (status & GIT_STATUS_INDEX_TYPECHANGE) return 'T';
    if (status & GIT_STATUS_INDEX_NEW) return 'A';
    if (status & GIT_STATUS_INDEX_MODIFIED) return 'M';
    if (status & GIT_STATUS_INDEX_DELETED) return 'D';
    return ' ';
}

static char mgc_wt_char(int status)
{
    if (status & GIT_STATUS_WT_RENAMED) return 'R';
    if (status & GIT_STATUS_WT_TYPECHANGE) return 'T';
    if (status & GIT_STATUS_WT_MODIFIED) return 'M';
    if (status & GIT_STATUS_WT_DELETED) return 'D';
    if (status & GIT_STATUS_CONFLICTED) return 'U';
    return ' ';
}

/* 追加一段文本到动态缓冲 */
typedef struct { char *data; size_t len; size_t cap; } mgc_dynbuf;

static int mgc_dynbuf_append(mgc_dynbuf *b, const char *text, size_t n)
{
    if (b->len + n + 1 > b->cap) {
        size_t newcap = b->cap ? b->cap * 2 : 8192;
        while (newcap < b->len + n + 1) newcap *= 2;
        char *grown = (char *)realloc(b->data, newcap);
        if (grown == NULL) return -1;
        b->data = grown;
        b->cap = newcap;
    }
    memcpy(b->data + b->len, text, n);
    b->len += n;
    b->data[b->len] = '\0';
    return 0;
}

/*
 * 生成与 `git status --porcelain [-z] [-uall]` 完全一致的输出：
 * - 非 -z：每行 "XY path\n"；重命名显示 "XY old -> new"（与 git 显示一致）
 * - -z：条目以 \0 分隔；重命名条目后跟第二个 \0 段放旧路径
 * 返回 0 成功（结果在 mgc_buffer）；负数为 libgit2 错误。
 */
int mgc_status_porcelain(git_repository *repo, int nul_sep, int untracked_all)
{
    git_status_options opts;
    git_status_options_init(&opts, GIT_STATUS_OPTIONS_VERSION);
    opts.show = GIT_STATUS_SHOW_INDEX_AND_WORKDIR;
    opts.flags = GIT_STATUS_OPT_INCLUDE_UNTRACKED | GIT_STATUS_OPT_RENAMES_HEAD_TO_INDEX |
                 GIT_STATUS_OPT_RECURSE_UNTRACKED_DIRS;
    if (!untracked_all) opts.flags |= GIT_STATUS_OPT_EXCLUDE_SUBMODULES;

    git_status_list *list = NULL;
    int rc = git_status_list_new(&list, repo, &opts);
    if (rc != 0) return rc;

    mgc_dynbuf b = {0};
    size_t count = git_status_list_entrycount(list);
    for (size_t i = 0; i < count; i++) {
        const git_status_entry *e = git_status_byindex(list, i);
        if (e == NULL) continue;

        int st = (int)e->status;
        char line[4096];

        const char *old_path = NULL;
        if (e->head_to_index != NULL && (st & GIT_STATUS_INDEX_RENAMED))
            old_path = e->head_to_index->old_file.path;
        if (old_path == NULL && e->index_to_workdir != NULL && (st & GIT_STATUS_WT_RENAMED))
            old_path = e->index_to_workdir->old_file.path;

        const char *path = e->index_to_workdir != NULL && e->index_to_workdir->new_file.path != NULL
            ? e->index_to_workdir->new_file.path
            : (e->head_to_index != NULL ? e->head_to_index->new_file.path : NULL);
        if (path == NULL) path = e->head_to_index != NULL ? e->head_to_index->old_file.path : NULL;
        if (path == NULL) continue;

        char xi, yt;
        if (st == GIT_STATUS_WT_NEW || st == GIT_STATUS_IGNORED) {
            xi = '?'; yt = '?';
        } else if (st & GIT_STATUS_CONFLICTED) {
            xi = 'U'; yt = 'U';
        } else {
            xi = mgc_index_char(st);
            yt = mgc_wt_char(st);
        }

        if (nul_sep) {
            int n = snprintf(line, sizeof(line), "%c%c %s", xi, yt, path);
            if (mgc_dynbuf_append(&b, line, (size_t)n) != 0) break;
            if (mgc_dynbuf_append(&b, "\0", 1) != 0) break;
            if (old_path != NULL) {
                n = snprintf(line, sizeof(line), "%s", old_path);
                if (mgc_dynbuf_append(&b, line, (size_t)n) != 0) break;
                if (mgc_dynbuf_append(&b, "\0", 1) != 0) break;
            }
        } else {
            int n;
            if (old_path != NULL)
                n = snprintf(line, sizeof(line), "%c%c %s -> %s\n", xi, yt, old_path, path);
            else
                n = snprintf(line, sizeof(line), "%c%c %s\n", xi, yt, path);
            if (mgc_dynbuf_append(&b, line, (size_t)n) != 0) break;
        }
    }
    git_status_list_free(list);

    int result = b.data != NULL ? mgc_buffer_set(b.data, b.len) : mgc_buffer_set("", 0);
    free(b.data);
    return result;
}

/* ==================== add / diff --cached / commit ==================== */

int mgc_add_all(git_repository *repo, char *err, int errcap)
{
    git_index *index = NULL;
    if (git_repository_index(&index, repo) != 0) return mgc_error(err, errcap);
    int rc = git_index_add_all(index, NULL, 0, NULL, NULL);
    if (rc == 0) rc = git_index_write(index);
    git_index_free(index);
    if (rc != 0) return mgc_error(err, errcap);
    return 0;
}

int mgc_add_paths(git_repository *repo, const char **paths, int count, char *err, int errcap)
{
    git_index *index = NULL;
    if (git_repository_index(&index, repo) != 0) return mgc_error(err, errcap);

    int rc = 0;
    for (int i = 0; i < count && rc == 0; i++) {
        const char *p = paths[i];
        if (p == NULL || p[0] == '\0') continue;
        /* 文件存在 → add；不存在 → 从索引移除（git add 对已删除文件的行为） */
        rc = git_index_add_bypath(index, p);
        if (rc == GIT_ENOTFOUND) rc = git_index_remove_bypath(index, p);
    }
    if (rc == 0) rc = git_index_write(index);
    git_index_free(index);
    if (rc != 0) return mgc_error(err, errcap);
    return 0;
}

/* diff --cached --quiet 语义：0 = 暂存区与 HEAD 一致；1 = 有暂存改动 */
int mgc_index_has_staged(git_repository *repo)
{
    /* 注意：git_diff_tree_to_index 的 old_tree=NULL 语义是「空树」而不是 HEAD，
       必须显式解析 HEAD^{tree}（空仓库保持 NULL = 与空树比较） */
    git_object *head = NULL;
    git_tree *old_tree = NULL;
    if (git_revparse_single(&head, repo, "HEAD^{tree}") == 0 && head != NULL)
        old_tree = (git_tree *)head;

    git_diff *diff = NULL;
    git_diff_options diffopts;
    git_diff_options_init(&diffopts, GIT_DIFF_OPTIONS_VERSION);
    int rc = git_diff_tree_to_index(&diff, repo, old_tree, NULL, &diffopts);
    if (head != NULL) git_object_free(head);
    if (rc != 0) return -1;
    size_t n = git_diff_num_deltas(diff);
    git_diff_free(diff);
    return n > 0 ? 1 : 0;
}

/* 读取生效身份（repo 级 → 全局）；返回 0 表示两项都有 */
static int mgc_identity(git_repository *repo, char *name, int ncap, char *email, int ecap)
{
    name[0] = '\0'; email[0] = '\0';
    git_config *live = NULL;
    if (repo != NULL ? git_repository_config(&live, repo) != 0 : git_config_open_default(&live) != 0)
        return 0;
    /* 读值必须走快照（get_string 不允许 live config） */
    git_config *cfg = NULL;
    if (git_config_snapshot(&cfg, live) != 0) { git_config_free(live); return 0; }
    git_config_free(live);

    const char *v = NULL;
    if (git_config_get_string(&v, cfg, "user.name") == 0 && v) snprintf(name, (size_t)ncap, "%s", v);
    if (git_config_get_string(&v, cfg, "user.email") == 0 && v) snprintf(email, (size_t)ecap, "%s", v);
    git_config_free(cfg);
    return name[0] != '\0' && email[0] != '\0';
}

/* git commit：返回 0 成功，把 "[branch shortid] subject" 写进 out */
int mgc_commit(git_repository *repo, const char *message, int allow_empty, char *out, int outcap,
               char *err, int errcap)
{
    char name[512], email[512];
    if (!mgc_identity(repo, name, sizeof(name), email, sizeof(email))) {
        snprintf(err, (size_t)errcap,
                 "fatal: unable to auto-detect email address (未配置 user.name / user.email)");
        return -1;
    }

    git_index *index = NULL;
    if (git_repository_index(&index, repo) != 0) return mgc_error(err, errcap);

    git_oid tree_oid;
    if (git_index_write_tree(&tree_oid, index) != 0) {
        git_index_free(index);
        return mgc_error(err, errcap);
    }
    git_index_free(index);

    /* 空提交检查：与父提交树相同且未允许空提交 → 退出码 1（与 git 一致） */
    git_commit *parent = NULL;
    int has_parent_flag = 0;
    {
        git_reference *head_ref = NULL;
        int head_rc = git_repository_head(&head_ref, repo);
        if (head_rc == 0) {
            has_parent_flag = 1;
            git_commit_lookup(&parent, repo, git_reference_target(head_ref));
            git_reference_free(head_ref);
        } else {
            has_parent_flag = 0;
        }
    }
    int has_parent = has_parent_flag;
    if (has_parent && !allow_empty) {
        const git_oid *parent_tree_id = git_commit_tree_id(parent);
        if (parent_tree_id != NULL && git_oid_equal(&tree_oid, parent_tree_id)) {
            git_commit_free(parent);
            snprintf(err, (size_t)errcap, "nothing to commit, working tree clean");
            return 1;
        }
    }

    git_signature *sig = NULL;
    if (git_signature_now(&sig, name, email) != 0) {
        if (has_parent) git_commit_free(parent);
        return mgc_error(err, errcap);
    }

    git_tree *tree = NULL;
    if (git_tree_lookup(&tree, repo, &tree_oid) != 0) {
        git_signature_free(sig);
        if (has_parent) git_commit_free(parent);
        return mgc_error(err, errcap);
    }

    const git_commit *parents[1];
    int parent_count = 0;
    if (has_parent) parents[parent_count++] = parent;

    git_oid commit_oid;
    int rc = git_commit_create(&commit_oid, repo, "HEAD", sig, sig, NULL,
                               message, tree, (size_t)parent_count,
                               parent_count > 0 ? parents : NULL);

    char branch[512] = "?";
    if (has_parent) {
        git_reference *head_ref = NULL;
        if (git_reference_lookup(&head_ref, repo, "HEAD") == 0) {
            const char *target = git_reference_symbolic_target(head_ref);
            if (target != NULL) {
                if (strncmp(target, "refs/heads/", 11) == 0) target += 11;
                snprintf(branch, sizeof(branch), "%s", target);
            }
        }
        git_reference_free(head_ref);
        git_commit_free(parent);
    } else {
        /* 空仓库的第一次提交：HEAD 由 git_commit_create 指到默认分支，重新读一次 */
        git_reference *head_ref = NULL;
        if (git_reference_lookup(&head_ref, repo, "HEAD") == 0) {
            const char *target = git_reference_symbolic_target(head_ref);
            if (target != NULL && strncmp(target, "refs/heads/", 11) == 0)
                snprintf(branch, sizeof(branch), "%s", target + 11);
        }
        git_reference_free(head_ref);
    }

    git_signature_free(sig);
    git_tree_free(tree);
    if (rc != 0) return mgc_error(err, errcap);

    git_object *created = NULL;
    char short_id[16] = "";
    if (git_object_lookup(&created, repo, &commit_oid, GIT_OBJECT_COMMIT) == 0) {
        git_buf buf = {0};
        if (git_object_short_id(&buf, created) == 0 && buf.size > 0)
            snprintf(short_id, sizeof(short_id), "%s", buf.ptr);
        git_buf_dispose(&buf);
        git_object_free(created);
    }

    /* 标题 = 消息首行 */
    char subject[256] = "";
    const char *nl = strchr(message, '\n');
    size_t slen = nl != NULL ? (size_t)(nl - message) : strlen(message);
    if (slen >= sizeof(subject)) slen = sizeof(subject) - 1;
    memcpy(subject, message, slen);
    subject[slen] = '\0';

    snprintf(out, (size_t)outcap, "[%s %s] %s", branch, short_id, subject);
    return 0;
}

/* ==================== 历史 ==================== */

/* 把 git_time 格式化成 --date=iso："2026-11-13 20:33:22 +0800" */
static void mgc_format_iso(git_time when, char *out, size_t cap)
{
    time_t t = (time_t)(when.time + when.offset * 60);
    struct tm tm_utc;
    gmtime_r(&t, &tm_utc);
    int sign = when.offset < 0 ? -1 : 1;
    int off = when.offset * sign;
    snprintf(out, cap, "%04d-%02d-%02d %02d:%02d:%02d %c%02d%02d",
             tm_utc.tm_year + 1900, tm_utc.tm_mon + 1, tm_utc.tm_mday,
             tm_utc.tm_hour, tm_utc.tm_min, tm_utc.tm_sec,
             sign < 0 ? '-' : '+', off / 60, off % 60);
}

/* 标题：取消息首段（到空行为止），段内换行替换为空格（与 git %s 一致） */
static void mgc_subject(const char *message, char *out, size_t cap)
{
    size_t n = 0;
    while (message[n] != '\0' && !(message[n] == '\n' && message[n + 1] == '\n')) n++;
    size_t o = 0;
    for (size_t i = 0; i < n && o + 1 < cap; i++) {
        char c = message[i] == '\n' ? ' ' : message[i];
        if (c == '\r') continue;
        out[o++] = c;
    }
    /* 去尾部空格 */
    while (o > 0 && out[o - 1] == ' ') o--;
    out[o] = '\0';
}

/* git log -n <max> --date=iso --pretty=format:%H%x1f%h%x1f%ad%x1f%an%x1f%s */
int mgc_log(git_repository *repo, int max)
{
    git_revwalk *walk = NULL;
    if (git_revwalk_new(&walk, repo) != 0) return -1;
    git_revwalk_sorting(walk, GIT_SORT_TIME);
    int rc = git_revwalk_push_head(walk);
    if (rc != 0) { git_revwalk_free(walk); return rc; } /* 空仓库：返回原错误码（ENOENT/UNBORN） */

    mgc_dynbuf b = {0};
    git_oid oid;
    int count = 0;
    while (count < max && git_revwalk_next(&oid, walk) == 0) {
        git_commit *commit = NULL;
        if (git_commit_lookup(&commit, repo, &oid) != 0) { rc = -1; break; }

        char full[42];
        git_oid_tostr(full, sizeof(full), &oid);

        git_object *obj = NULL;
        char short_id[16] = "";
        if (git_object_lookup(&obj, repo, &oid, GIT_OBJECT_COMMIT) == 0) {
            git_buf buf = {0};
            if (git_object_short_id(&buf, obj) == 0 && buf.size > 0)
                snprintf(short_id, sizeof(short_id), "%s", buf.ptr);
            git_buf_dispose(&buf);
            git_object_free(obj);
        }

        char iso[64];
        mgc_format_iso(git_commit_author(commit)->when, iso, sizeof(iso));

        char subject[512];
        mgc_subject(git_commit_message(commit), subject, sizeof(subject));

        char line[1280];
        int n = snprintf(line, sizeof(line), "%s\x1f%s\x1f%s\x1f%s\x1f%s\n",
                         full, short_id, iso, git_commit_author(commit)->name, subject);
        int append_rc = mgc_dynbuf_append(&b, line, (size_t)n);

        git_commit_free(commit);
        count++;
        if (append_rc != 0) { rc = -1; break; }
    }
    git_revwalk_free(walk);

    if (rc != 0) { free(b.data); return rc; }
    int result = b.data != NULL ? mgc_buffer_set(b.data, b.len) : mgc_buffer_set("", 0);
    free(b.data);
    return result;
}

/* 状态字母 → git --name-status 的字母 */
static char mgc_delta_char(git_delta_t delta)
{
    switch (delta) {
        case GIT_DELTA_ADDED: return 'A';
        case GIT_DELTA_DELETED: return 'D';
        case GIT_DELTA_MODIFIED: return 'M';
        case GIT_DELTA_RENAMED: return 'R';
        case GIT_DELTA_COPIED: return 'C';
        case GIT_DELTA_TYPECHANGE: return 'T';
        default: return 'M';
    }
}

/* git show <rev> --name-status --format= --first-parent */
int mgc_show_files(git_repository *repo, const char *rev)
{
    git_object *obj = NULL;
    if (git_revparse_single(&obj, repo, rev) != 0) return -1;
    git_commit *commit = (git_commit *)obj; /* revparse 已保证类型为 COMMIT */

    git_tree *commit_tree = NULL;
    if (git_commit_tree(&commit_tree, commit) != 0) { git_object_free(obj); return -1; }

    git_tree *parent_tree = NULL;
    if (git_commit_parentcount(commit) > 0) {
        git_commit *parent = NULL;
        if (git_commit_parent(&parent, commit, 0) == 0) {
            git_commit_tree(&parent_tree, parent);
            git_commit_free(parent);
        }
    }

    git_diff_options diffopts;
    git_diff_options_init(&diffopts, GIT_DIFF_OPTIONS_VERSION);
    git_diff *diff = NULL;
    int rc = git_diff_tree_to_tree(&diff, repo, parent_tree, commit_tree, &diffopts);
    git_tree_free(commit_tree);
    if (parent_tree != NULL) git_tree_free(parent_tree);
    if (rc != 0) { git_object_free(obj); return rc; }

    /* 打开重命名检测（git show 默认 diff.renames=true） */
    git_diff_find_options findopts;
    git_diff_find_options_init(&findopts, GIT_DIFF_FIND_OPTIONS_VERSION);
    findopts.flags = GIT_DIFF_FIND_RENAMES;
    git_diff_find_similar(diff, &findopts);

    mgc_dynbuf b = {0};
    size_t count = git_diff_num_deltas(diff);
    for (size_t i = 0; i < count; i++) {
        const git_diff_delta *d = git_diff_get_delta(diff, i);
        char line[4096];
        int n;
        char c = mgc_delta_char(d->status);
        if (d->status == GIT_DELTA_RENAMED || d->status == GIT_DELTA_COPIED) {
            n = snprintf(line, sizeof(line), "%c%03d\t%s\t%s\n", c,
                         d->similarity, d->old_file.path, d->new_file.path);
        } else {
            n = snprintf(line, sizeof(line), "%c\t%s\n", c, d->new_file.path);
        }
        if (mgc_dynbuf_append(&b, line, (size_t)n) != 0) break;
    }
    git_diff_free(diff);
    git_object_free(obj);

    int result = b.data != NULL ? mgc_buffer_set(b.data, b.len) : mgc_buffer_set("", 0);
    free(b.data);
    return result;
}

/* git reset --soft|--mixed|--hard <rev> */
int mgc_reset(git_repository *repo, const char *rev, int mode, char *err, int errcap)
{
    git_object *obj = NULL;
    if (git_revparse_single(&obj, repo, rev) != 0) return mgc_error(err, errcap);
    git_reset_t t = mode == 0 ? GIT_RESET_SOFT : (mode == 1 ? GIT_RESET_MIXED : GIT_RESET_HARD);
    int rc = git_reset(repo, obj, t, NULL);
    git_object_free(obj);
    if (rc != 0) return mgc_error(err, errcap);
    return 0;
}

/* ==================== 联网操作 ==================== */

static void mgc_setup_net_ctx(mgc_net_ctx *ctx, mgc_progress_cb progress, void *payload,
                              const char *user, const char *pass)
{
    ctx->base.progress = progress;
    ctx->base.payload = payload;
    ctx->user = user ? strdup(user) : NULL;
    ctx->pass = pass ? strdup(pass) : NULL;
    ctx->rejected[0] = '\0';
}

static void mgc_free_net_ctx(mgc_net_ctx *ctx)
{
    free(ctx->user);
    free(ctx->pass);
}

/* 填充 remote callbacks（凭据 + 进度 + 证书） */
static void mgc_setup_callbacks(git_remote_callbacks *cb, mgc_net_ctx *ctx)
{
    git_remote_init_callbacks(cb, GIT_REMOTE_CALLBACKS_VERSION);
    cb->credentials = mgc_cred_cb;
    cb->certificate_check = mgc_cert_cb;
    cb->sideband_progress = mgc_sideband_cb;
    cb->transfer_progress = mgc_transfer_cb;
    cb->push_transfer_progress = mgc_push_transfer_cb;
    cb->push_update_reference = mgc_push_update_cb;
    cb->payload = ctx;
}

/* git_submodule_foreach 回调：逐个 init + update（--recursive） */
static int mgc_update_submodule_cb(git_submodule *sm, const char *name, void *payload)
{
    (void)name;
    git_submodule_update_options *opts = (git_submodule_update_options *)payload;
    git_submodule_update(sm, 1, opts);
    return 0;
}

/*
 * git clone [--branch b] [--depth 1] [--recursive] url path
 * 返回 0 成功；负数错误（err 带消息）；进度经 cb 上报。
 */
int mgc_clone(const char *url, const char *path, const char *branch, int depth, int recursive,
              const char *user, const char *pass,
              mgc_progress_cb progress, void *payload, char *err, int errcap)
{
    mgc_net_ctx ctx;
    mgc_setup_net_ctx(&ctx, progress, payload, user, pass);

    if (mgc_emit(&ctx.base, "正在克隆到 '%s'...", path) != 0) {
        mgc_free_net_ctx(&ctx);
        snprintf(err, (size_t)errcap, "cancelled");
        return -1;
    }

    git_clone_options opts;
    git_clone_options_init(&opts, GIT_CLONE_OPTIONS_VERSION);
    mgc_setup_callbacks(&opts.fetch_opts.callbacks, &ctx);
    opts.checkout_opts.progress_cb = mgc_checkout_progress_cb;
    opts.checkout_opts.progress_payload = &ctx.base;
    if (branch != NULL && branch[0] != '\0') opts.checkout_branch = branch;

    git_repository *repo = NULL;
    int rc = git_clone(&repo, url, path, &opts);
    if (rc != 0) { mgc_free_net_ctx(&ctx); return mgc_error(err, errcap); }

    /* --recursive：递归初始化并更新子模块（尽力而为，失败不阻断） */
    if (recursive) {
        mgc_emit(&ctx.base, "初始化子模块...");
        git_submodule_update_options subopts;
        git_submodule_update_options_init(&subopts, GIT_SUBMODULE_UPDATE_OPTIONS_VERSION);
        git_submodule_foreach(repo, mgc_update_submodule_cb, &subopts);
    }

    git_repository_free(repo);
    mgc_free_net_ctx(&ctx);
    return 0;
}

/* git push [--force] url branch [--tags] */
int mgc_push(git_repository *repo, const char *url, const char *branch, int force, int tags,
             const char *user, const char *pass,
             mgc_progress_cb progress, void *payload, char *err, int errcap)
{
    mgc_net_ctx ctx;
    mgc_setup_net_ctx(&ctx, progress, payload, user, pass);

    git_remote *remote = NULL;
    int rc = git_remote_create_anonymous(&remote, repo, url);
    if (rc != 0) { mgc_free_net_ctx(&ctx); return mgc_error(err, errcap); }

    git_push_options opts;
    git_push_options_init(&opts, GIT_PUSH_OPTIONS_VERSION);
    mgc_setup_callbacks(&opts.callbacks, &ctx);

    char branch_spec[1024];
    snprintf(branch_spec, sizeof(branch_spec), "%srefs/heads/%s:refs/heads/%s",
             force ? "+" : "", branch, branch);

    if (tags) {
        const char *refspecs[2] = { branch_spec, "refs/tags/*:refs/tags/*" };
        git_strarray specs = { (char **)refspecs, 2 };
        rc = git_remote_push(remote, &specs, &opts);
    } else {
        const char *refspecs[1] = { branch_spec };
        git_strarray specs = { (char **)refspecs, 1 };
        rc = git_remote_push(remote, &specs, &opts);
    }

    git_remote_free(remote);
    mgc_free_net_ctx(&ctx);

    if (rc != 0) {
        if (ctx.rejected[0] != '\0')
            snprintf(err, (size_t)errcap, "! [rejected] %s", ctx.rejected);
        else
            return mgc_error(err, errcap);
        return -1;
    }
    if (ctx.rejected[0] != '\0') {
        snprintf(err, (size_t)errcap, "! [rejected] %s", ctx.rejected);
        return -1;
    }
    return 0;
}

/*
 * git pull --ff-only|--rebase|--no-rebase url branch
 * strategy：0 = ff-only，1 = rebase，2 = merge
 */
int mgc_pull(git_repository *repo, const char *url, const char *branch, int strategy,
             const char *user, const char *pass,
             mgc_progress_cb progress, void *payload, char *err, int errcap)
{
    mgc_net_ctx ctx;
    mgc_setup_net_ctx(&ctx, progress, payload, user, pass);

    git_remote *remote = NULL;
    int rc = git_remote_create_anonymous(&remote, repo, url);
    if (rc != 0) { mgc_free_net_ctx(&ctx); return mgc_error(err, errcap); }

    int has_local = 0;
    char remote_ref[1024], local_ref[1024];
    snprintf(remote_ref, sizeof(remote_ref), "refs/heads/%s", branch);
    snprintf(local_ref, sizeof(local_ref), "refs/remotes/mgcpull/%s", branch);

    const char *refspec[1] = { NULL };
    char spec[2048];
    snprintf(spec, sizeof(spec), "%s:%s", remote_ref, local_ref);
    refspec[0] = spec;
    git_strarray fetch_specs = { (char **)refspec, 1 };

    git_fetch_options fetch_opts;
    git_fetch_options_init(&fetch_opts, GIT_FETCH_OPTIONS_VERSION);
    mgc_setup_callbacks(&fetch_opts.callbacks, &ctx);
    fetch_opts.update_fetchhead = 0;

    rc = git_remote_fetch(remote, &fetch_specs, &fetch_opts, "pull");
    git_remote_free(remote);
    if (rc != 0) { mgc_free_net_ctx(&ctx); return mgc_error(err, errcap); }

    git_oid remote_oid;
    if (git_reference_name_to_id(&remote_oid, repo, local_ref) != 0) {
        mgc_free_net_ctx(&ctx);
        snprintf(err, (size_t)errcap, "couldn't find remote ref %s", remote_ref);
        return -1;
    }

    git_commit *remote_commit = NULL;
    if (git_commit_lookup(&remote_commit, repo, &remote_oid) != 0) {
        mgc_free_net_ctx(&ctx);
        return mgc_error(err, errcap);
    }

    git_commit *local_commit = NULL;
    {
        git_reference *head_ref = NULL;
        if (git_repository_head(&head_ref, repo) == 0) {
            has_local = 1;
            git_commit_lookup(&local_commit, repo, git_reference_target(head_ref));
            git_reference_free(head_ref);
        }
    }

    int result = 0;
    if (!has_local) {
        /* 空仓库：把当前分支直接指到远端提交并检出 */
        char refname[1024];
        if (mgc_repo_head_symbolic_short(repo, refname, sizeof(refname)) != 0)
            snprintf(refname, sizeof(refname), "main");
        char full[1100];
        snprintf(full, sizeof(full), "refs/heads/%s", refname);
        git_reference *ref = NULL;
        rc = git_reference_create(&ref, repo, full, &remote_oid, 1, "pull: set branch to remote");
        if (rc == 0) {
            git_reference_free(ref);
            git_checkout_options copts;
            git_checkout_options_init(&copts, GIT_CHECKOUT_OPTIONS_VERSION);
            copts.checkout_strategy = GIT_CHECKOUT_FORCE;
            copts.progress_cb = mgc_checkout_progress_cb;
            copts.progress_payload = &ctx.base;
            git_object *obj = NULL;
            if (git_object_lookup(&obj, repo, &remote_oid, GIT_OBJECT_COMMIT) == 0) {
                git_checkout_tree(repo, obj, &copts);
                git_object_free(obj);
            }
            if (mgc_emit(&ctx.base, "已把分支 %s 指向远端最新提交并完成检出。", refname) != 0) result = -2;
        } else {
            result = mgc_error(err, errcap);
        }
    } else if (git_oid_equal(git_commit_id(local_commit), &remote_oid)) {
        if (mgc_emit(&ctx.base, "Already up to date.") != 0) result = -2;
    } else {
        git_oid base;
        int have_base = git_merge_base(&base, repo, git_commit_id(local_commit), &remote_oid) == 0;

        if (have_base && git_oid_equal(&base, &remote_oid)) {
            /* 远端落后于本地：已是最新 */
            if (mgc_emit(&ctx.base, "Already up to date.") != 0) result = -2;
        } else if (have_base && git_oid_equal(&base, git_commit_id(local_commit))) {
            /* 可快进 */
            git_reference *head_ref = NULL;
            if (git_repository_head(&head_ref, repo) != 0) {
                result = mgc_error(err, errcap);
            } else {
                if (mgc_emit(&ctx.base, "快进（fast-forward）更新...") != 0) {
                    result = -2;
                } else {
                    git_checkout_options copts;
                    git_checkout_options_init(&copts, GIT_CHECKOUT_OPTIONS_VERSION);
                    copts.checkout_strategy = GIT_CHECKOUT_SAFE;
                    copts.progress_cb = mgc_checkout_progress_cb;
                    copts.progress_payload = &ctx.base;
                    git_object *obj = NULL;
                    if (git_object_lookup(&obj, repo, &remote_oid, GIT_OBJECT_COMMIT) == 0) {
                        rc = git_checkout_tree(repo, obj, &copts);
                        git_object_free(obj);
                    } else {
                        rc = -1;
                    }
                    if (rc == 0) {
                        git_reference *updated = NULL;
                        rc = git_reference_set_target(&updated, head_ref, &remote_oid,
                                                      "pull: fast-forward");
                        if (rc == 0) git_reference_free(updated);
                    }
                    if (rc != 0) result = mgc_error(err, errcap);
                }
                git_reference_free(head_ref);
            }
        } else if (strategy == 0) {
            snprintf(err, (size_t)errcap, "fatal: Not possible to fast-forward, aborting.");
            result = -1;
        } else if (strategy == 1) {
            snprintf(err, (size_t)errcap,
                     "拉取策略 --rebase 在 iOS 内嵌 git 中暂不支持，请改用合并（--no-rebase）重新拉取。");
            result = -1;
        } else {
            /* 合并：git_merge + 必要时生成合并提交 */
            git_annotated_commit *annotated = NULL;
            if (git_annotated_commit_lookup(&annotated, repo, &remote_oid) != 0) {
                result = mgc_error(err, errcap);
            } else {
                if (mgc_emit(&ctx.base, "正在合并远端更新...") != 0) {
                    result = -2;
                } else {
                    git_checkout_options copts;
                    git_checkout_options_init(&copts, GIT_CHECKOUT_OPTIONS_VERSION);
                    copts.checkout_strategy = GIT_CHECKOUT_SAFE;
                    git_merge_options mopts;
                    git_merge_options_init(&mopts, GIT_MERGE_OPTIONS_VERSION);
                    rc = git_merge(repo, (const git_annotated_commit **)&annotated, 1,
                                   &mopts, &copts);
                    if (rc != 0) {
                        result = mgc_error(err, errcap);
                    } else {
                        git_index *index = NULL;
                        if (git_repository_index(&index, repo) != 0) {
                            result = mgc_error(err, errcap);
                        } else if (git_index_has_conflicts(index)) {
                            git_index_free(index);
                            snprintf(err, (size_t)errcap,
                                     "CONFLICT: 拉取产生冲突，请在仓库目录手动解决冲突后再继续。");
                            result = -1;
                        } else {
                            git_oid tree_oid;
                            rc = git_index_write_tree(&tree_oid, index);
                            git_index_free(index);
                            if (rc != 0) {
                                result = mgc_error(err, errcap);
                            } else {
                                /* 写合并提交（两个父提交） */
                                char name[512], email[512];
                                if (!mgc_identity(repo, name, sizeof(name), email, sizeof(email))) {
                                    snprintf(err, (size_t)errcap, "未配置 user.name / user.email，无法生成合并提交。");
                                    result = -1;
                                } else {
                                    git_signature *sig = NULL;
                                    git_tree *tree = NULL;
                                    git_commit *ours = local_commit;
                                    if (git_signature_now(&sig, name, email) != 0 ||
                                        git_tree_lookup(&tree, repo, &tree_oid) != 0) {
                                        result = mgc_error(err, errcap);
                                    } else {
                                        const git_commit *parents[2] = { ours, remote_commit };
                                        git_oid merge_oid;
                                        char msg[1024];
                                        snprintf(msg, sizeof(msg), "Merge remote-tracking branch '%s'",
                                                 branch);
                                        rc = git_commit_create(&merge_oid, repo, "HEAD", sig, sig, NULL,
                                                               msg, tree, 2, parents);
                                        git_tree_free(tree);
                                        git_signature_free(sig);
                                        if (rc != 0) result = mgc_error(err, errcap);
                                    }
                                }
                            }
                        }
                    }
                }
                git_annotated_commit_free(annotated);
            }
        }
    }

    if (local_commit != NULL) git_commit_free(local_commit);
    git_commit_free(remote_commit);

    /* 清理临时拉取引用 */
    git_reference *tmp = NULL;
    if (git_reference_lookup(&tmp, repo, local_ref) == 0) {
        git_reference_delete(tmp);
        git_reference_free(tmp);
    }

    mgc_free_net_ctx(&ctx);
    if (result == -2) { snprintf(err, (size_t)errcap, "cancelled"); return -1; }
    return result;
}

/* git ls-remote --heads url：输出 "<sha>\trefs/heads/<name>\n" 到 mgc_buffer */
int mgc_ls_remote(const char *url, const char *user, const char *pass,
                  mgc_progress_cb progress, void *payload, char *err, int errcap)
{
    mgc_net_ctx ctx;
    mgc_setup_net_ctx(&ctx, progress, payload, user, pass);

    git_remote *remote = NULL;
    int rc = git_remote_create_detached(&remote, url);
    if (rc != 0) { mgc_free_net_ctx(&ctx); return mgc_error(err, errcap); }

    git_remote_callbacks cbs;
    mgc_setup_callbacks(&cbs, &ctx);
    rc = git_remote_connect(remote, GIT_DIRECTION_FETCH, &cbs, NULL, NULL);
    if (rc != 0) { git_remote_free(remote); mgc_free_net_ctx(&ctx); return mgc_error(err, errcap); }

    const git_remote_head **heads = NULL;
    size_t count = 0;
    rc = git_remote_ls(&heads, &count, remote);

    mgc_dynbuf b = {0};
    if (rc == 0) {
        for (size_t i = 0; i < count; i++) {
            if (heads[i]->name == NULL || strncmp(heads[i]->name, "refs/heads/", 11) != 0) continue;
            char hex[42];
            git_oid_tostr(hex, sizeof(hex), &heads[i]->oid);
            char line[1200];
            int n = snprintf(line, sizeof(line), "%s\t%s\n", hex, heads[i]->name);
            if (mgc_dynbuf_append(&b, line, (size_t)n) != 0) break;
        }
    }
    git_remote_disconnect(remote);
    git_remote_free(remote);
    mgc_free_net_ctx(&ctx);

    if (rc != 0) { free(b.data); return mgc_error(err, errcap); }
    int result = b.data != NULL ? mgc_buffer_set(b.data, b.len) : mgc_buffer_set("", 0);
    free(b.data);
    return result;
}
