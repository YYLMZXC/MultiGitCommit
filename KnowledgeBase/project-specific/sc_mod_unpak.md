# sc_mod_unpak 项目独立经验

> 来源：历史会话整理（2026-11-06 实查结论），本工作区未重新核验；换项目仅作对照。

## 结构与结论修正

- 早期记录「唯一项目 `src/ScModUnpak/ScModUnpak.csproj`，Avalonia 12.1.5 + net10.0 多目标」**已过时**。
- 2026-11-06 实查：仓库已重构为 `src/sc_mod_unpak.net` 的 **Uno 多项目结构**（Uno.Sdk、net9.0）：
  `ProjectAppUi` / `ProjectCore` / `ProjectDesktop` / `ProjectMobile` / `ProjectWasm` + `Adapters/*`。
- 但当年在该仓库验证出的构建经验仍跨项目适用，已抽到 [`../reusable/dotnet-build-and-tooling.md`](../reusable/dotnet-build-and-tooling.md)：
  IDE 回写 / 清空 csproj、Android minSdk、桌面 RID 泄漏触发 XA0035、多目标 publish 需 `-f`、PowerShell 5.1 的 UTF-8 BOM。

## 维护提示

- 遇到与该结构冲突的旧记录，先到仓库实查目录与 TFM，再更新本文件；不要直接复用旧结论。
