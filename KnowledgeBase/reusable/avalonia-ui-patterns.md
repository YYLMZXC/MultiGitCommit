# 可复用：Avalonia 跨桌面 / 移动 UI 经验

> 适用：下一个同样要「桌面窗口 + Android / iOS 一套代码」的项目。
> 来源与核验：MultiGitCommit（2026-11-12 源码检索）、CharacterSkinsMC（2026-11-06 历史实查）。

## 1. 壳与内容分离

- 把全部 UI 抽成 `UserControl`：桌面 `MainWindow` 只保留标题 / 图标 / 关闭收尾（关闭时调主视图的 `Shutdown()`），移动端由平台入口直接加载主视图。主逻辑只写一份。
- 平台差异（入口、字体、清单、权限、能力开关）集中在各自 TFM 的入口文件 / 条件编译里；业务视图不判断操作系统。

## 2. 对话框统一宿主

- 自建 `DialogViewBase` / `DialogView<TResult>`，用 `Close(result)` 回传结果；统一入口 `UiDialogs.ShowAsync(anchor, view, title)`。
- 桌面：把视图包进 `Window`，`SizeToContent=WidthAndHeight`，依赖视图自带的固定 `Width`，走 `ShowDialog`。
- 移动端（无窗口概念）：推进 `DialogOverlay` 覆盖层并支持嵌套堆叠；MessageBox / Prompt 也走同一宿主，避免两套弹窗实现。
- 覆盖层里把视图 `Width` 重置为 `NaN`（桌面固定宽度不适用），卡片内 `ScrollViewer` 横向 `Auto`；覆盖层放在内容 `ScrollViewer` **之外**，才能盖满视口而非随内容滚动。
- `StorageProvider`、浏览器打开等一律通过 `TopLevel` 获取，不要在视图里假设 `Window` 存在。

## 3. 窄屏响应式套路

- 订阅 `SizeChanged` → `ApplyResponsiveLayout()`，按宽度阈值切换（本项目用了 700 / 420 两档，阈值按内容定）。
- 按钮密集行：宽屏用 `Grid` 列，窄屏搬到 `WrapPanel`；用字段记住原列号，恢复宽屏时还原，避免反复切换后布局错乱。
- 长文案 `RadioButton` 等内容嵌 `TextBlock TextWrapping=Wrap`（桌面外观不变）；长标题同样开 `TextWrapping`。
- 输入表单：预留额外 Grid 行，窄屏把长输入框移到新行并横跨多列，列宽由固定值改 `Star`，恢复时还原。
- 列表列模板用比例列（`40,*,2*`）+ `TextTrimming`，不要写死像素宽度；底部按钮行用 `WrapPanel`。

## 4. 中文字体与编码

- Android 系统没有 XAML 字体链常写的字体（微软雅黑 / PingFang / Source Han），Avalonia 11 的 CJK 回退不可靠，中文会变方块。
- 通用解法：把 Noto Sans CJK SC（SIL OFL）**仅 android TFM** 作为 `AvaloniaResource` 嵌入，在 `MainActivity` 的 `FontManagerOptions.DefaultFamilyName` 设为默认字体；iOS 指定系统自带 `PingFang SC`，不内置字体。
- 外部进程输出统一按 UTF-8 解码，否则 Windows 下中文日志乱码。

## 5. 升级 / API 差异

- Avalonia 12 的 `RadioButton` 没有 `Checked` 事件：订阅 `PropertyChanged` 并判断 `IsCheckedProperty`。跨大版本升级前，先列 Android 入口、拖放、XAML 事件等 API 变化清单再迁移。
- 平台能力不可用时在唯一的服务实现顶部直接返回明确失败结果（例如 iOS 上无法调用外部 git），并让 UI 给出提示，不要让调用方各自判断。
- 改造完成后的最小验证集：逐 TFM `build` 0 错误 → 纯逻辑冒烟断言 → 桌面窗口 + 移动端真机各走一遍「打开弹窗 / 窄屏布局 / 中文显示 / 外部能力失败提示」。
