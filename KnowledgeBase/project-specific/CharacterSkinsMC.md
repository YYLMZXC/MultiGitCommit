# CharacterSkinsMC 项目独立经验

> 来源：历史会话整理（2026-11-06），本工作区未重新核验；业务规则换项目即失效。

## 项目结构（当时状态）

- 从 WinForms 迁移到 Avalonia 12.1.5 + .NET 11 双目标：`net11.0` 桌面 `WinExe` + `net11.0-android` 单项目模式。
- 拆分为 `CharacterSkinsMC.Core`（纯托管类库，`StbImageSharp` / `StbImageWriteSharp` 处理 PNG）+ `CharacterSkinsMC.App`（Android 入口在 `Platforms/Android`，`ApplicationId=com.sc.characterskinsmc`）。
- `Directory.Build.props` 定义 `GlobalTargetFramework` 并显式 `Import Directory.Build.targets`（android TFM 复位 `RuntimeIdentifiers` 防 XA0035）。

## 皮肤转换业务规则

- 分辨率分级：16x(64×64)、32x(128×128)、64x(256×256)、128x(512×512)，`SourceScale` 记录倍数；输出仍固定 64×64。
  - 32x：`2×2` 块中心像素降采样。
  - 64x / 128x：alpha 预乘加权区域平均（`DownsampleHd(source, scale)`）。
  - 64×32 旧版固定宽手。
- 宽手 Steve / 细手 Alex 规则分开：`ScSkinAtlas.GetRegions(McArmModel)` 返回 `s_commonRegions` + `s_classicArmRegions` / `s_slimArmRegions`；`MinecraftSkinConverter.Convert` 按 `skin.ArmModel` 取表；细手 3 宽源由采样器按像素中心对称拉伸进 SC 固定 4 宽手臂块。
- UI 用两个 `RadioButton`（`ArmClassicRadio` / `ArmSlimRadio`）替代 `SlimCheck`；Avalonia 12 的 `RadioButton` 无 `Checked` 事件，须订阅 `PropertyChanged` 判断 `IsCheckedProperty`（该 API 变化属可复用经验）。
- 验证方式：`net11.0 build` 0 错误 + `--smoke`（覆盖 64×32 / 64×64 / 128 / 256 / 512 五档与两套规则表断言）。
