# Biome Titles Localization Patch

[![Steam](https://img.shields.io/badge/%E5%88%9B%E6%84%8F%E5%B7%A5%E5%9D%8A-Steam?style=flat-square&logo=Steam&label=Steam&color=%23000000)](https://steamcommunity.com/sharedfiles/filedetails/?id=3758079429)
![Steam Update Date](https://img.shields.io/steam/update-date/3758079429?style=flat-square&label=工坊更新时间)

一个为 [Biome Titles（生物群系名称）](https://steamcommunity.com/sharedfiles/filedetails/?id=2992680615) 提供本地化增强的 tModLoader 模组。

Biome Titles 使用手动维护的方式收录群系名称
本补丁通过挂钩标题解析流程，用自动化取代手动维护——直接从各模组的本地化文件中读取已翻译的名称

## 功能总览

### 群系名称本地化（核心）

按以下优先级依次查找群系名称，命中即进入自定义覆盖判断，否则继续向下查找：

- **源模组本地化** → 优先读取群系所属模组自身的本地化文件（支持汉化模组）
- **Biome Titles 本地化** → 使用 Biome Titles 手动维护的本地化
- **补充翻译** → 本模组提供 Biome Titles 缺失的原版翻译（`Aether → 以太`）
- **用户自定义覆盖** → 上述任一命中后，再检查模组配置中是否有自定义名称（如有则覆盖）
- 若均未命中，回退显示原始名称（即英文）

### 自动扫描与注册

> 默认关闭，需在配置中启用。

扫描所有已加载模组中的模组群系，自动完成：

- **已收录群系** — 更新标题和本地化作用域，不动颜色/图标（以内置为主）
- **未收录群系** — 自动注册到 Biome Titles 字典，并添加检测函数
- **占位群系跳过** — 跳过未重写 `IsBiomeActive` 的占位群系
- **独立检测函数** — 插入到检测列表最前方，确保优先匹配

### 自动配色

> 默认关闭，需同时启用自动扫描。

为新注册的群系自动设置标题颜色：

- **优先使用** 模组定义的背景色
- **无颜色时** 使用白色，保持界面简洁一致
- **有图标无颜色时** 从图标采样主色（众数），采样失败回退灰色
- **描边色** 沿用 Biome Titles 默认的黑色

### 补充翻译

在本地化文件中通过 `ExtraTitles.{作用域}.{键名}` 补充 Biome Titles 缺失的原版翻译

## 配置说明

在游戏内模组配置中找到「Biome Titles Localization Patch」即可调节：

| 配置项 | 默认值 | |
| --- | :---: | --- |
| 扫描新群系 | 关 | 启用后自动扫描未收录群系并注册到字典；关闭时自动移除新注册的群系、还原被改写的已有条目（配置保存后即时生效，重载模组时同样会撤净） |
| 配色 | 关 | 为新增的群系自动设置标题颜色（需同时启用扫描，配置保存后即时生效） |
| 调试日志 | 关 | 输出调试信息到 `client.log`，排查问题时可开启 |

## 兼容性

- 本模组为**纯客户端**模组，不影响联机
- 使用 `MonoMod.RuntimeDetour.Hook` 挂钩 `GetActualTitleName`，与原模组兼容良好
- 自动扫描功能通过 tModLoader 原生反射和 `ModBiome.IsBiomeActive` 检测，不依赖特定模组

## 致谢

- 感谢仍在使用 [Biome Titles](https://steamcommunity.com/sharedfiles/filedetails/?id=2992680615) 和使用本补丁的玩家
- 所有提供反馈和建议的玩家
- AI 辅助工具在本项目开发过程中的支持（模型 DeepSeek v4.1 Flash）

## 许可证

本项目基于 [GPL-3.0](LICENSE) 许可证开源。
