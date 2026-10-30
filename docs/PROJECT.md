# Biome Titles Enhance 项目文档

> 显示名：Biome Titles Enhance / 生物群系标题增强；内部名（文件夹、程序集、本地化键前缀）仍为 `BTitlesLocalizationPatch`
> 版本：v1.4.2.2 | 作者：Mio | 工坊 ID：3758079429 | 仓库：https://github.com/ayuyoiM/BTEnhance

---

## 1. 项目定位

BTitles 是一个手动维护群系名称的模组，显示玩家当前所在生物群系的名称。
**本补丁通过挂钩 BTitles 的标题解析流程，用自动化取代手动维护。** 它不做自己的汉化包，只做桥梁——直接从各模组的本地化文件中读取已翻译的名称。

五个核心能力：

1. **自动注册未收录群系** — 扫描所有模组的 ModBiome，缺失的自动补入 BTitles 字典
2. **GetActualTitleName 增强** — 5 步回退链查找翻译
3. **自动配色** — 为自动注册的群系设置标题颜色
4. **逐群系配色表** — 玩家按屏幕上看到的标题名手工指定颜色，命中优先于自动策略
5. **扫描名单** — 按模式（黑名单 / 白名单）决定哪些群系参与扫描，一张表 + 一个开关

---

## 2. 文件结构

```t
BTitlesLocalizationPatch/                  # 文件夹名 = 内部名，不可随意改
├── BTitlesLocalizationPatch.cs           # Mod 主入口：生命周期、Hook 安装、配置变更响应
├── BiomeNameHook.cs                      # GetActualTitleName 的 Hook 替换逻辑（翻译链核心 + 逐群系配色）
├── GetActualTitleNameHookInstaller.cs    # Hook 安装/卸载（MonoMod.RuntimeDetour.Hook 包装）
├── BTitlesIncompatibleException.cs       # BTitles 版本不兼容异常
├── BiomeEntryReferenceComparer.cs        # BiomeEntry 的引用相等比较器（扫描侧与 Hook 侧共用）
├── BTitlesLocalizationPatch.csproj       # 项目引用（BTitles）
├── build.txt                             # 模组元数据（v1.4.2.2、modReferences=BTitles）
├── changelog.txt                         # 更新日志
├── description.txt                       # 工坊简介（中/英）
├── description_workshop.txt
├── description_workshop_en-US.txt
├── icon.png / icon_small.png / icon_workshop.png   # 模组与工坊图标
├── LICENSE                               # GPL-3.0
├── .editorconfig                         # 代码风格：space/4
├── .gitattributes / .gitignore
├── .agents/                              # AI 协作说明目录（.agents/skills/tmodloader-upgrade/SKILL.md）
├── .vscode/                              # 编辑器配置（launch.json、settings.json）
├── Properties/                           # launchSettings.json
├── README.md                             # 工坊介绍页
├── AGENTS.md                             # AI 协作文档：规则 / 当前状态 / 变更流水
├── docs/
│   ├── PROJECT.md                        # 本文件：架构、生命周期、配置与决策记录
│   └── ai-log-archive.md                 # AI 工作流水归档（AGENTS.md 流水溢出的旧条目）
│
├── Scan/
│   ├── BiomeRegistrar.cs                 # 自动扫描注册、检测函数构建、回滚/清理
│   ├── BiomeStyleHelper.cs               # 配色策略、逐群系配色表、图标加载、代表性颜色采样
│   ├── BiomeFilter.cs                    # 扫描名单（模式 + 单表）读取与匹配
│   └── LanguageChangeSystem.cs           # 语言切换接收点（OnLocalizationsLoaded）：清翻译缓存 + 重算已扫描标题
│
├── config/
│   ├── ModConfig.cs                      # BTitlesConfig + BiomeColorMapping：扫描/配色/调试开关
│   ├── TimedValueCache.cs                # 带冷却的配置值缓存（StylingCache 等）
│   └── DebugLog.cs                       # 调试日志工具（带冷却缓存、三级分流）
│
└── Localization/
    ├── en-US_Mods.BTitlesLocalizationPatch.hjson     # 英文本地化（模板，不可删键）
    └── zh-Hans_Mods.BTitlesLocalizationPatch.hjson   # 中文本地化
```

> 未列出构建产物与 IDE 目录：`bin/`、`obj/`、`.vs/`、`.dsh-ops-stage/`、`.git/`。

---

## 3. 核心架构

### 3.1 技术方案

| 技术 | 用途 |
| --- | --- |
| `MonoMod.RuntimeDetour.Hook` | 替换式 Hook（replacement）覆写 `BiomeTitlesUI.GetActualTitleName` |
| 反射（`FieldInfo`） | 访问 BTitles 的 `internal` 字段：`Instance`、`BiomeDictionary`、`BiomeCheckFunctions` |
| `ConcurrentDictionary` | 翻译结果缓存，支持语言切换时清空 |
| `volatile` + `Volatile.Read/Write` | 跨线程状态同步（后台线程 PostSetupContent vs 主线程 Load/Unload/配置） |
| 配置文件化（`ModConfig.OnChanged`） | 事件驱动即时生效，无需重载模组 |

### 3.2 反射字段

主类 `BTitlesLocalizationPatch` 在 `Load` 时预缓存反射目标：

| 字段 | 目标 | 用途 |
| --- | --- | --- |
| `InstanceField` | `BiomeTitlesMod.Instance`（static） | 获取 BTitles 单例 |
| `BiomeDictField` | `BiomeTitlesMod.BiomeDictionary`（instance） | 访问群系字典 `Dictionary<string, BiomeEntry>` |
| `CheckFuncsField` | `BiomeTitlesMod.BiomeCheckFunctions`（instance） | 访问检测函数列表 `List<Func<Player, string>>` |
| `_uiField` | `BiomeTitlesMod._biomeTitlesUi`（instance） | 取 UI 实例，供即时刷新标题 |
| `_setupVisualsMethod` | `BiomeTitlesUI.SetupTitleVisuals()`（private 无参） | 配置保存后立刻重算当前标题颜色 |

前三个字段被多处使用：

- `BiomeRegistrar.Run()` — 扫描注册
- `ApplyConfigChangeStyle()` — 实时配色
- `UnregisterScannedBiomes()` — 热卸载
- `Cleanup()` — 清理检测函数

**前三个缺失即拒绝加载**（`ValidateReflectionTargets` 抛 `ScanTargetMissing`）；后两个只是「即时刷新」的加速器，
取不到就退化成「下次切换群系才生效」，只记一次 `RefreshUnavailable` 告警，不参与校验。
这是刻意的分层：上游改了 UI 内部结构不该让整个模组加载失败。

### 3.3 Hook 安装

```csharp
Hook(
    BiomeTitlesUI.GetActualTitleName,            // 目标方法（非 public）
    new Func<Func<BiomeTitlesUI, BiomeEntry, string>, BiomeTitlesUI, BiomeEntry, string>(
        BiomeNameHook.GetActualTitleNameHook     // 替换方法：接收 orig，完全接管原逻辑
    )
)
```

**签名匹配**：安装前**不做**预校验；若 BTitles 更新改了方法名，`GetMethod` 返回 null → `Install` 抛
`BTitlesIncompatibleException("HookMethodMissing")`；若只是签名对不上，`new Hook()` 会自行抛。两种情况都
未捕获，直接冒到 tModLoader 把本模组标成加载失败（`Load` 里没有 try/catch）。

---

## 4. 生命周期

```t
Load()
├── InstallDebugLogger()            — 将 DebugLog 三级写入器绑定到 Logger
├── CacheReflectionFields()         — 反射缓存 BTitles 的字段与方法（含 UI 刷新用的两个）
├── ValidateReflectionTargets()     — 前三个缺一即抛 BTitlesIncompatibleException("ScanTargetMissing")
└── GetActualTitleNameHookInstaller.Install(this) — 安装 Hook（找不到方法 → 抛 "HookMethodMissing"）

PostSetupContent()
├── Main.dedServ → 直接返回（纯客户端模组）
├── config == null → Logger.Error(键 ConfigNull) + 返回
├── config.EnableScan → BiomeRegistrar.Run(this)
│   └── 扫描各模组 ModBiome，注册到 BTitles 字典（配色开关由 Run 自己从 Hook 缓存读）
└── else config.EnableAutoStyling → ApplyConfigChangeStyle(true)（对齐配色策略）

运行时（群系切换时由 BTitles 调用，非严格每帧）
├── BiomeNameHook.GetActualTitleNameHook
│   ├── ResolveDisplayName — 5 步回退链 + 图标采样补色（PendingColorKeys）
│   └── 逐群系配色表命中覆盖 TitleColor（原色记入 MappedOriginalColors，撤销命中时还原）
└── DebugLog.Enabled（冷却缓存，3 秒刷一次配置）

配置变更（ModConfig.OnChanged，一次 Save 只执行一轮）
├── 首轮基线：OnLoaded 已把 _last* 对齐到实际加载值，见「配置系统」
├── ApplyConfigChangeStyle(bool)           — 重刷已扫描条目配色 + 刷新当前标题
├── ApplyConfigChangeScan(bool)            — 开启/关闭扫描（热注册/热卸载）
└── ApplyConfigChangeRescan(string logKey) — 口径变更（仅补漏 / 黑名单 / 白名单）：先撤再扫
    后两条互斥，同一轮只跑一条：扫描开关变了就走 Scan（Run 现读配置，自带新口径），
    扫描开关没变才走 Rescan（先撤再扫），否则两个开关同开会白扫两遍

Unload()
├── TranslationCache.Clear() / ResetStylingCache() / ResetCultureCache() / ResetEnUsMapCache()
├── BiomeStyleHelper.InvalidateMappingSnapshot() — 丢掉指向配置对象的配色表快照
├── GetActualTitleNameHookInstaller.Uninstall(this)
├── UnregisterScannedBiomes(null, restoreMappedColors: true)
│   ├─ 主线程段先 BiomeNameHook.RestoreMappedColors()：把配色表覆盖过的颜色还回 BTitles 字典
│   │   （Unload 之后 Hook 已摘，不主动还原就会在 BTitles 里留下本模组的颜色；
│   │    写的是同一份共享状态，必须与快照还原一起排进主线程）
│   ├─ 不在主线程 → Main.RunOnMainThread(...).GetAwaiter().GetResult() 阻塞跳转
│   │   （BTitles 不随本模组卸载，其字典与本模组 Hook 都会在主线程被并发访问）
│   ├─ 跳转失败 → 记 Warn（键 UnloadHopFailed）、ClearScannedBiomes、Unload 不抛异常
│   └─ 主线程路径：撤除新增条目、还原已有条目快照、CleanupBlockingOnMainThread、
│       ClearScannedBiomes（撤检测函数与状态清空合并进同一跳转，不留排队回调）
├── 反射字段全部置 null（含 _uiField/_setupVisualsMethod）、_loaded/LoadCompleted 归位
└── DebugLog 写入器复位、ResetCache()
```

---

## 5. 翻译回退链（核心功能）

`BiomeNameHook.GetActualTitleNameHook` 按 5 步查找群系名称，命中即检查自定义覆盖，否则继续。

> **语言门控**：步骤 1–3 只在 `hasValidKey && currentCulture != "en-US"` 时才执行
> （`BiomeNameHook.cs:179` `bool shouldTranslate`）。en-US 客户端下整条链短路，直接用
> `biomeEntry.Title` 去匹配自定义覆盖（`:154-164`）。所以这条链是**依语言而变**的。

```txt
玩家进入群系（有合法 key 且当前语言非 en-US）
    │
    ├─ [1] 源模组本地化
    │   ├─ Terraria 群系 → Mods.BiomeTitles.Title.Terraria.{Key}
    │   └─ 其他模组 → Mods.{Scope}.Biomes.{Key}.DisplayName
    │   └─ 与该模组 en-US 原文相同 → 跳过（模组未提供翻译）
    │
    ├─ [2] BTitles 自身翻译
    │   └─ Mods.BiomeTitles.Title.{Scope}.{Key}
    │
    ├─ [3] 补丁补充翻译（ExtraTitles）
    │   └─ Mods.BTitlesLocalizationPatch.ExtraTitles.{Scope}.{Key}
    │   └─ 例：ExtraTitles.Terraria.Aether: 以太
    │
    ├─ [4] 自定义覆盖（每次重新查配置，不缓存）
    │   └─ config.CustomBiomeNames 匹配当前名称
    │
    └─ [5] BTitles 原生解析回落
        ├─ string nativeTitle = orig(self, biomeEntry) — 非空即返回（标签 Fallback）
        ├─ 抛异常 → 记 Logs.NativeTitleFailed，继续向下
        └─ 为空/异常 → biomeEntry.Title
```

### 关键设计细节

**英文原文对照（替代旧 ASCII 启发式）**：

- 步骤 1 拿到的本地化值会与同键 en-US 原值直接比较（序数区分大小写）
- en-US 对照表按需构建并缓存：遍历 `mod.RootContentSource` 的 `.hjson` 资产，
  识别出 en-US 文件后用 `HjsonValue.Parse` 平铺成 `键 → 值` 字典（含负缓存）
- **三种布局都要认**（实测都存在于真实模组；早先只认第一种，后两种从未生效过）：
  - `Localization/en-US_Mods.X.hjson` —— culture 与 prefix 都在文件名
  - `Localization/en-US.hjson` —— 只有 culture，内容自带 `Mods:{X:{...}}` 外层，prefix 为空
  - `Localization/en-US/Mods.X.hjson` —— culture 在目录名，文件名即 prefix
  判定顺序是「先目录后文件名」：布局三的文件名 `Mods.X.Biomes.hjson` 可能被
  `TryGetCultureAndPrefixFromPath` 误当成 culture，先判目录就不会被挡住
- **只收 `Mods.{mod}.Biomes.{key}.DisplayName`**：`IsEnglishFallbackValue` 只查这一类键。
  实测 StarsAbove 的 en-US 有 6552 个叶子，属于群系的只有 12 个——不限定 `.Biomes.` 等于没过滤。
  这段与 `TryStep1` 拼键的格式强耦合，那边改结构时这里必须同步，否则静默退化成 ASCII 判断
- 平铺时数组节点显式跳过（`FlattenHjson`）：本地化键不会是数组，
  掉进叶子分支只会被 `ToString()` 成 `"[...]"` 整段文本记进对照表
- 模组无 en-US 资产、或键不在 en-US 表里（BTitles 运行时注册的键）→
  保守回退旧的 `IsPureAscii()`（纯 ASCII 判定）
- **例外**：Terraria 原版群系不走对照逻辑（直接返回 "BTitles" 标签）

**缓存策略**：

- 翻译链结果（步骤 1-3）缓存到 `ConcurrentDictionary`，下次直接命中
- 步骤 4 自定义覆盖每次重新查配置，保证配置变更即时生效
- 语言切换时清空全缓存（`Scan/LanguageChangeSystem.cs`，见下）
- **缓存键要求 `LocalizationScope` 非空**：键形如 `{scope}|{key}`，scope 为空时退化成
  `|{key}`，两个模组若都没带作用域、群系短名又恰好相同，就会共用同一条缓存——表现为
  A 群系显示 B 群系的名字。这类条目极少，不缓存、每次走完整解析链的代价可以忽略。
  「能不能翻译」只看 key（`hasValidKey`），「能不能缓存」才额外要求 scope（`canCache`），
  两者必须分开判断：合并会让无作用域的条目直接失去翻译
- **缓存命中路径不写调试日志**：命中说明结果与上次完全一致，而标题每次刷新都会走到这里，
  重复记三行（`BiomeEntry` / `BiomeDetail` / `BiomeColor`）会让 client.log 迅速膨胀。
  完整解析链已经为每个群系记过一次；要看新日志，切一次语言或改一次配置即可（两者都清缓存）

**语言切换的处理**：

`BiomeEntry.Title` 是扫描那一刻按当时语言烤进去的字符串（`Scan/BiomeRegistrar.cs:339`
`modBiome.DisplayName.Value`）。BTitles 回退链的最后一步直接返回它（`BiomeTitlesUI.cs:511`），
所以源模组没有目标语言译文时，漏出来的是上次扫描时的语言。只清翻译缓存不够，
必须把 `Title` 按新语言重算。

接收点只有一个：`Scan/LanguageChangeSystem.cs`（`ModSystem` 子类）覆写
`OnLocalizationsLoaded()`。tML 在 `LanguageManager.ReloadLanguage` 末尾统一分发
（`LanguageManager.cs.patch:72`），切语言、语言文件热重载、加载期灌值都会走到；
此刻 `RecalculateBoundTextValues` 已跑过，`DisplayName.Value` 已是新语言的值。
它做三件事：清 `TranslationCache`、重置 `_lastCultureName`、在主线程按新语言重写
已扫描条目的 `Title` 并调 `RefreshCurrentTitle()` 刷新当前标题。
只动 `Title`：颜色、图标、作用域与语言无关，重算它们会白丢玩家的配色结果。

---

## 6. 自动扫描系统（BiomeRegistrar）

### 6.1 流程

```csharp
BiomeRegistrar.Run(mod)     — 配色开关不传参，Run 自己从 BiomeNameHook.IsStylingEnabled() 读缓存
│
├─ 0. CAS 2,0 — 原子进入，避免并发扫描损坏状态
├─ 1. 读取配置（IsStylingEnabled / IsOnlyFillMissing / GetStylingSettings / BiomeFilter.Read）
├─ 2. Cleanup() — 清理上次扫描残留的检测函数；PendingColorKeys.Clear
├─ 3. 获取 BTitles 实例、字典、检测函数列表
├─ 4. ScanAllModBiomes(...)
│   ├─ 遍历 ModLoader.Mods（跳过 BTitles/tModLoader/ModLoader）
│   ├─ 每个模组的 ModBiome 逐个处理（SnapshotExisting := PreScanValues 快照）
│   ├─ 占位群系 → 跳过；黑/白名单命中 → 跳过（见「扫描名单」）
│   ├─ onlyFillMissing → 字典已存在键 → 计入 Skipped，完全不动（也不进 allBiomes）
│   └─ 异常时 Rollback() 重置扫描状态（不碰字典，见「回滚」）
├─ 5. BuildDetectionFunction(allBiomes, keyMapping) — 只遍历被追踪的条目
├─ 6. 主线程回调：ApplyWrites（加载图标 + 写 Title/Scope）+ `checkFuncs.Insert(0, ...)`
│   └─ 提交失败 → UndoWrites 就地撤销本次写入 + Rollback（见「回滚」）
└─ 7. 输出扫描总结日志（ScanSummary）
```

### 6.2 占位群系跳过

用 `DeclaringType` 检测 `IsBiomeActive` 是否被重写：

- 子类没重写 → `DeclaringType == typeof(ModBiome)` → 认为是占位群系 → 跳过
- 使用 `DeclaringType` 而非 `GetBaseDefinition()`，因为 fgpt 等模组的 ILHook 会干扰后者

典型例子：某些大型模组的基础实验室群系（如嘉登实验室）没重写检测方法，注册了也检测不到。

### 6.3 键匹配策略

优先匹配命名空间键 `{Mod}.{BiomeName}` 避免同名冲突，再试 BTitles 短键兼容已有条目：

- 已收录群系 → 快照原值（`PreScanValues`，供卸载还原）后更新 `Title` 和 `LocalizationScope`，不动颜色/图标
- 未收录群系 → 创建 `BiomeEntry` 并注册到字典；按配色策略上色，需要采样时登记 `PendingColorKeys`
- **只有被追踪的分支才进 `allBiomes`**（检测函数的数据源）：命名空间键更新、短键更新、新增三条分支各调一次
  `allBiomes.Add((targetMod, modBiome))`（`BiomeRegistrar.cs:367/378/394`）。主循环无条件 `Add` 会让
  「仅补漏跳过」的条目也进检测列表——它们没写 `keyMapping`，lambda 回落返回短键 `biome.Name`，
  BTitles 字典只有命名空间键时 `TryGetValue` miss → `_currentBiomeEntry = null` →
  **BTitles 原本能显示的群系反而消失**
- **仅「Biome Titles」未添加的群系**（`OnlyFillMissingBiomes`）开启时：字典已有的键完全跳过——
  不覆写、不快照、不排队写入，仅计入 `ScanResult.Skipped`，并逐条记
  `SkippedExistingBiome`（走 `DebugLog.Info`，受「调试日志」开关控制：
  开这个开关时被跳过的条目可能有上百条，无脑写进 `tModLoader.log` 会把正常日志淹掉，
  总数在 `ScanSummary` 里已有）

### 6.4 检测函数

每个扫描会话构建一个 lambda 检测函数，遍历 `allBiomes`（只含被本模组追踪的条目，
被「仅补漏」跳过的群系不在其中，见「键匹配策略」）：

```csharp
player => {
    foreach allBiomes:
        if biome.IsBiomeActive(player)
            return keyMapping[$"{Mod}.{Biome}"] ?? biome.Name
    return ""
}
```

插到 `BiomeCheckFunctions` 最前面（`Insert(0)`），确保本模组注册的群系优先被检测。

### 6.5 回滚

`ScanAllModBiomes` 抛异常时调用 `Rollback(generation)`，它**只重置扫描状态、不碰字典**：

- 先判 `IsCancelled(generation)`：令牌已不属于自己就整个跳过——此时这些数据可能已经是
  后一次扫描的，清掉就是误伤
- `PendingColorKeys.Clear()`
- `ScannedBiomes = null`、`PreScanValues = null`
- `ReleaseScan(generation)`（只有仍持有时才释放）
- **不动** `RegisteredCheckFunc`，**不**从字典移除任何键

另有一个 `RollbackCommitted()`，只在主线程提交失败的 catch 里调用：那时扫描已成功提交、
令牌也已在 `finally` 里释放，所以不能走「已取消就跳过」的判断，必须无条件清

真正做「移除条目 + 按快照还原 + 摘检测函数」的是另一个方法 `UndoWrites`
（`BiomeRegistrar.cs:545-573`），只在主线程提交失败（`ApplyWrites` 抛异常）的 catch 里调用
（`BiomeRegistrar.cs:145`）。`UndoWrites` 会：
- 摘掉本次新增的条目（`biomeDict.Remove`）
- 按 `PreScanValues` 快照还原已有条目的 `Title` / `LocalizationScope`（`:566-567`）
- 若检测函数已插入也一并移除，避免留下无人认领的孤儿 lambda

### 6.6 热卸载

关闭扫描时（与模组 Unload 走同一 `UnregisterScannedBiomes` 路径，详见「生命周期」）：

1. 不在主线程 → 单次阻塞 `Main.RunOnMainThread` 跳转
2. 从字典移除本次新增的条目，用 `PreScanValues` 快照还原被改写的已有条目
3. 在同一跳转内阻塞移除检测函数（`CleanupBlockingOnMainThread`，不留排队回调）
4. 清空 `ScannedBiomes` 和 `PreScanValues`

### 6.7 扫描名单（黑名单 / 白名单）

`Scan/BiomeFilter.cs`，配置为 `BTitlesConfig.FilterMode`（`ScanFilterMode`）+ **一张**表
`ScanFilterList`（`List<BiomeFilterEntry>`）。

**一张表 + 一个模式**：模式决定这张表这一轮被解释成黑名单还是白名单。
切换模式不需要搬数据，也压根不存在「同一个群系既在黑名单又在白名单」这种自相矛盾的配置。

| `FilterMode` | 表的解释 |
| --- | --- |
| `Disabled`（默认） | 不生效。表里的内容原样留着，不参与判定 |
| `Blacklist` | 黑名单：命中即跳过 |
| `Whitelist` | 白名单：未命中即跳过；表为空时全放行 |

```txt
Run()
├─ filter = BiomeFilter.Read()      — 扫描开始时读一次，整轮共用
│   └─ FilterMode == Disabled → 直接返回空快照，连集合都不建
└─ 每个 ModBiome：
   ├─ 占位群系 → 跳过（原逻辑）
   ├─ BiomeFilter.Evaluate(filter, "Mod.Biome", biome.Name, DisplayName.Value)
   │   ├─ Blacklist 模式命中 → Blacklisted → DebugLog(SkippedByBlacklist) + skipped++
   │   ├─ Whitelist 模式未命中 → NotWhitelisted → DebugLog(SkippedByWhitelist) + skipped++
   │   └─ 否则 Pass → RegisterOrUpdateBiome
   └─ onlyFillMissing 判定（原逻辑）
```

- **三种写法都认**：命名空间键 `Mod.Biome`、短键 `Biome`、屏幕上的标题名。匹配用
  `OrdinalIgnoreCase` 集合并 `Trim`，空项/纯空白项直接丢弃——玩家多点一行空条目不该把名单弄坏
- **不做格式限制**：不要求填某一种键，也不校验名字是否存在（拼错了就是没命中，不会报错）
- **白名单为空 = 放行**：玩家刚把模式切到白名单、还没填内容时，不该一键把群系全扫没
- **只影响扫描**：BTitles 自己收录的群系不经过扫描，因此名单管不到它们
- **过滤位置在占位检测之后**：占位群系本来就不会注册，先过名单只会让日志里的跳过原因变模糊
- **计入 `ScanSummary` 的 skipped**，不新增统计参数；逐条明细走 `DebugLog.Info`，与
  `SkippedPlaceholder` / `SkippedExistingBiome` 同通道
- 模式与表内容都是扫描期判定，改完走「先撤再扫」（见「配置系统」的 `ApplyConfigChangeRescan`）；
  模式为 `Disabled` 时只改表内容不触发重扫——那时候名单不参与判定，重扫纯属白干
- **列表项默认折叠**：`[Expand(false)]` 打在 `BiomeFilterEntry` 类上，机制与逐群系配色表相同
  （见「逐群系配色表」：`ObjectElement` 读的是元素类型上的特性）。单字段条目折叠后要多点一下才能编辑，
  但名单往往几十条，全展开会把整页撑长，这个取舍与配色表保持一致
- **扫描侧只读快照，不碰配置里的 `List`**：`ScanFilterList` 是 `List<BiomeFilterEntry>`，
  配置界面随时可能增删行，后台线程遍历它就是在赌「读的时候没人写」——赌输了轻则
  `InvalidOperationException`，重则读到撕裂的 `Count` 与数组。所以
  `BiomeFilter.RefreshSnapshot()` 由主线程在 `OnLoaded` 与 `OnChanged` 里调用，每次建新的
  `HashSet` 实例、从不原地改；`Read()` 只返回 `(模式, 快照引用)`，后台只读引用就是安全的
- **快照必须在新一轮扫描之前刷新**：`Run` 会把主线程调用甩到线程池（见「线程模型」），
  刷新晚一步，新一轮扫描就会用到旧名单。所以 `OnChanged` 里 `RefreshSnapshot(this)` 写在
  `ApplyConfigChange*` 之前
- `RefreshSnapshot` 的 `config` 由调用方显式传入（`OnLoaded` 里传 `this`），不内部查
  `ModContent.GetInstance`：`OnLoaded` 期间配置未必已注册进 `ModContent`，查回 null
  会把刚加载的名单静默清空

---

## 7. 自动配色系统

### 7.1 扫描阶段（后台线程 PostSetupContent）

```txt
BuildNewEntry()
├─ Icon 不在扫描阶段加载（后台线程不能碰纹理），Entry.Icon = null
├─ enableStyling ? GetTitleColors(modBiome, source, fixedColor, out needsSampling)
│   ├─ source = Fixed                    → 用配置的固定颜色（不走采样）
│   ├─ source = IconSampling             → 白色占位 + needsSampling = true
│   ├─ source = BackgroundColorSampling（默认）
│   │                                  → 背景色存在且非近白 → 用背景色；
│   │                                    否则白色 + needsSampling = true
│   ├─ source = BackgroundColor        → 背景色存在 → 用背景色；
│   │                                    否则纯白（不采样，语义见下）
│   └─ needsSampling → PendingColorKeys[entry] 登记
└─ !enableStyling → Color.White

BackgroundColor 的语义在 1.4.1.1 变过：旧行为是「无背景色 → 白色 + 采样」，
现在「无背景色 → 纯白，不采样」；想要背景色优先 + 回落采样的旧行为，选 BackgroundColorSampling。
默认值是 `BackgroundColorSampling`（`config/ModConfig.cs` 的 `[DefaultValue]`），
README 曾一度写成「背景色」，以代码为准。

**「白色」判定按 RGB 比、留容差（`NearWhiteThreshold = 245`）**：
只认 `Color.White` 会漏掉近白（如 `250,250,250`）与带 alpha 的白——
`Color` 的 `==` 是全分量比较，alpha 不是 255 就不相等。这两种都会被当成「有背景色」，
标题于是被刷成近白色、糊在背景上看不见。所以只看 R/G/B 是否都 ≥ 245。
`Fixed` 策略的 `fixedColor` 归一化（全零 → 白）只在 `GetStylingSettings` 做一次，
`GetTitleColors` 不再重复兜底——同一件事写两遍，改口径时必漏一处。
```

**重要限制** ：后台线程不能调用 `Texture2D.GetData`，因此**不**在扫描阶段做图标采样。

### 7.2 运行时补色（主线程，Hook 调用路径）

提交写入（`ApplyWrites`，主线程回调）时加载图标：`Icon = TryLoadIcon(modBiome)`。
需要采样的条目**在扫描阶段就已登记**（`BuildNewEntry`，`BiomeRegistrar.cs:456-457`），
之后第一次调用 `GetActualTitleNameHook` 时：

```txt
if (IsStylingEnabled() && PendingColorKeys.Contains(entry) && entry.Icon != null)
├─ Icon.IsDisposed → TryReloadIcon(entry.Key) 重加载
├─ 图标仍有效 → SampleRepresentativeColor()
│   ├─ 采样全图标像素（上限 256×256）
│   ├─ 剔除透明（A<128）、过暗/过亮（R+G+B 在 30..720 之外）、灰调（三通道极差 < 24）
│   ├─ 对剩下的有效像素求 RGB 平均
│   ├─ BoostSaturation()：转 HSL，S 放大 1.35 倍、L 抬到不低于 0.4，H 原样，A 固定 255
│   └─ 采样内部失败（GetData 抛异常 / 无有效像素 / 尺寸≤1）→ Color.Gray
└─ 写入 biomeEntry.TitleColor（写回 BTitles 字典，出队，后续不再重试）
```

**注意**：外层守卫要求 `Icon != null`，所以**图标为 null 的条目压根不进这个块**，
只会在待补色队列里等图标到位。两个配套机制防止无限堆积/被缓存短路：

- `ApplyWrites` 里 `TryLoadIcon` 返回 null 时立即 `PendingColorKeys.TryRemove` 出队：
  没有图标资产的群系不该永远占着队列、被 ScanSummary 计成「未配色」；
  真有图标的群系下次扫描会重新登记
- 补色（`TryBackfillColor`）在 `ResolveDisplayName` 的**翻译缓存命中判断之前**调用：
  缓存命中路径不经过后面的解析链，若把补色放在缓存判断之后，
  切换颜色来源时（`TranslationCache` 不清）补色会被缓存早退短路，标题一直白色

**为什么用 `ImmediateLoad` 而不是 `AsyncLoad`**（`BiomeStyleHelper.TryLoadIcon`）：

- 异步加载返回时拿不到纹理（`Asset.Value` 未就绪），调用方只能把条目留在
  `PendingColorKeys` 里反复重试；而重试时机挂在「标题被刷新」上——玩家不切群系就一直白着，
  比卡一下更难排查
- 同步加载的代价是主线程一次性加载全部新增群系的图标（几十张约几十到几百毫秒），
  但只发生在首次扫描 / 改配置重扫，玩家此时本来就在等加载
- 真要消掉这顿挫，正确方向是**分帧加载**（每帧 N 张），而不是换成异步

### 7.3 逐群系配色表（按显示名匹配）

`BTitlesConfig.TitleColorMappings` 是一张 `List<BiomeColorMapping>`（`Title` + `Color`），
按**玩家屏幕上实际看到的标题名**匹配，而不是模组内部的群系键：

```txt
GetActualTitleNameHook
├─ displayName = ResolveDisplayName(...)     — 先跑完 5 步链，拿到最终字符串
├─ IsStylingEnabled() && TryGetMappedColor(displayName, out c)
│   ├─ 命中 → MappedOriginalColors.TryAdd(entry, entry.TitleColor)   — 只在首次记原色
│   │          entry.TitleColor = c
│   └─ 未命中且表里有记录 → TryRemove + entry.TitleColor = 记下的原色
└─ return displayName
```

设计要点：

- **必须在 Hook 层做**：只有这里能看见每一个 `BiomeEntry`。BTitles 内置的原版群系
  （`registerBiome("Hell", ...)`，`LocalizationScope = "Terraria"`）不经过本模组的扫描，
  扫描侧根本碰不到它们
- **命中优先于 `PreferredColorSource`**：策略色是批量兜底，手工指定才是玩家真正的意图
- **受 `EnableAutoStyling` 门控**：总开关关闭时整档失效，命中记录也会被撤销还原
- **列表折叠时显示标题文字**：`BiomeColorMapping` 重写了 `ToString()` 返回 `Title`。
  tML 的 `ObjectElement.OnBind`（`Config/UI/ObjectElement.cs:52-61`）只在元素类型重写了
  `ToString` 时才把值拼到序号后面，没重写就只剩「1: 」这种空标签，玩家看不出这条配的是哪个群系
- **列表项默认折叠**：`[Expand(false)]` 打在 `BiomeColorMapping` 类上。`ObjectElement` 对
  列表元素读的是**元素类型**上的 `Expand` 特性（`Config/UI/ObjectElement.cs:131-136`），
  不是列表属性上的；配色表往往几十条，全展开会把整页撑长
- **原色要留底**：`MappedOriginalColors`（`ConcurrentDictionary<BiomeEntry, Color>`，
  用引用比较，因为 `BiomeEntry` 没重写 `Equals`）。撤销命中、策略色重写、扫描结果撤除、
  Unload 四个时机都会动这个表，见「配置变更时的配色刷新」
- **只有全零 `(0,0,0,0)` 视为「还没填」**：全零意味着玩家从没碰过这一项，沿用策略色。
  alpha 通道已完全放开（不再挂 `[ColorNoAlpha]`，A 滑条可见），填进去的 A 原样上色——
  半透明标题、淡出效果都交给玩家；只把 A 拖到 0 的彩色条目是玩家有意为之，照用。
  字段默认值仍写 `[DefaultValue(typeof(Color), "255,255,255,255")]`，
  未填与已填才有明确分界
- **表按「保存即失效」缓存**：`BiomeStyleHelper` 把表拍平成 `MappingEntry[]`
  （标题 + 原样保留 alpha 的颜色）挂在 `volatile` 字段上，Hook 每次显示标题只做一次线性扫数组，
  不再每帧 `ModContent.GetInstance` 再遍历 `List<T>`。失效点是 `ApplyConfigChangeStyle`
  与 `Unload`——不像 `StylingCache` 那样按时间冷却，改完表要立刻生效，不能有 3 秒旧值窗口。
  空表也缓存，所以另用 `_mappingSnapshotBuilt` 区分「没建过」与「建过且为空」

### 7.4 配置变更时的配色刷新

调整配色设置（开关/来源/固定颜色/配色表）时：

1. `ApplyStyleToScannedBiomes` 对**所有已扫描条目**在主线程整体重新取色：
   - 开 → 按当前策略重新 `GetTitleColors()`；需要采样的重新登记 `PendingColorKeys`
   - 关 → 全设 `Color.White`，移出补色队列
   - 每项重写前先 `ForgetMappedColor(entry)`：策略色变了，之前记的「原色」已过时
2. **最后**调 `RefreshCurrentTitle()` —— 反射 `BiomeTitlesUI.SetupTitleVisuals()` 推一次重算。
   顺序不能反：BTitles 只在群系切换时才重算颜色（`CheckNewBiome` 里
   `if (newBiome != _currentBiome)` 门控），放在重配色之前刷读到的是旧值，等于白刷

不做完整重扫；BTitles 自身注册的条目由 Hook 层懒匹配处理。

**配色表签名的判重**：`Save` 一定触发 `OnChanged`，但拿不到「哪一项变了」，
所以 `ComputeMappingsSignature` 用「条数 + 每条 Title/Color」的 `HashCode` 判断玩家是否动过这张表。
空表签名是 0，记忆字段初值也用 0 —— 用 -1 会让首次 `OnChanged` 平白触发一次全量重配色。

---

## 8. 配置系统

### BTitlesConfig（ClientSide，配置保存后即时生效）

| 属性 | 默认值 | 功能 |
| --- | :---: | --- |
| `EnableScan` | true | 自动扫描注册未收录群系 |
| `OnlyFillMissingBiomes` | true | 仅补漏：扫描跳过字典已有键，完全不动 |
| `ScanFilterList` | 空表 | 名单：按 `FilterMode` 解释成黑名单或白名单（`List<BiomeFilterEntry>`） |
| `FilterMode` | Disabled | 群系黑白名单模式：Disabled / Blacklist / Whitelist，决定上面那张表怎么用 |
| `EnableAutoStyling` | true | 自动设置标题颜色（逐群系配色表也在它之下） |
| `EnableDebugLog` | false | 输出调试日志到 client.log |
| `PreferredColorSource` | BackgroundColorSampling | 颜色来源策略：BackgroundColorSampling / BackgroundColor / IconSampling / Fixed |
| `FixedTitleColor` | 白 | Fixed 策略用的固定颜色（alpha 可调） |
| `TitleColorMappings` | 空表 | 逐群系配色表：`List<BiomeColorMapping>`（Title + Color），按显示名匹配，命中优先于策略色 |

配置项按 `[Header]` 分三组：**Scan**（`EnableScan`、`OnlyFillMissingBiomes`、`FilterMode`、`ScanFilterList`）、
**Styling**（`EnableAutoStyling`、`PreferredColorSource`、`FixedTitleColor`、`TitleColorMappings`）、
**Debug**（`EnableDebugLog`）。

**OnChanged 事件驱动**（一次 Save 只执行一轮，先读全部字段再决定动作）：

- 记录上次值 `_lastAutoStyling` / `_lastScan` / `_lastColorSource` / `_lastFixedColor` /
  `_lastOnlyFillMissing` / `_lastMappingsSignature` / `_lastFilterMode` /
  `_lastFilterSignature`，仅变化时执行
- **首轮基线由 `OnLoaded` 建立**（`config/ModConfig.cs:87-95`）：tML 在 `ConfigManager.Add`
  （注册期，早于 `Mod.Load`）就会调一次 `OnChanged`，那次被 `LoadCompleted` 早退挡住；
  但菜单保存（`OnChangedAll`）与游戏内保存触发时模组已加载，`_last*` 若停在 C# 默认值
  （`false` / 透明黑 / 签名 0），首轮必然把「默认开着」误判成「刚被打开」，平白跑一次
  全量扫描与重配色。`OnLoaded` 在 Load 之后、任何有效 `OnChanged` 之前把六个字段对齐到
  实际加载值——不硬编码 `true`，旧存档里的非默认取值同样兼容
- 开关或策略/配色表任一变化 → 合并成**一次** `ApplyConfigChangeStyle(EnableAutoStyling)`
  （分开写会在两者同时变时连跑两遍重配色 + 两遍刷新）
- 扫描开关变更 → `ApplyConfigChangeScan` 热注册/热卸载
- 扫描开关没变、只改了 `OnlyFillMissingBiomes` 或扫描名单 → `ApplyConfigChangeRescan(logKey)`：
  **先撤再扫**。三者都是扫描期判定，不重扫会把新旧口径混进同一份结果；而
  `BiomeRegistrar.Run` 有「已有生效结果就不再扫」的早退，不清掉 `ScannedBiomes`
  就永远扫不成第二次。`logKey` 只决定日志措辞（`RescanOnOnlyFillMissing` /
  `RescanOnFilterChanged`），别让玩家改了名单却看到「仅补漏已变更」。
  名单的模式变了必扫；`FilterMode == Disabled` 时只改表内容不触发重扫（名单不参与判定，
  重扫纯属白干），但签名仍无条件更新，切回启用状态时不会被当成「刚改过」
- **名单快照在本轮 Save 里、`ApplyConfigChange*` 之前刷新**：`Scan.BiomeFilter.RefreshSnapshot(this)`。
  `Run` 会把主线程调用转线程池（见「线程模型」），刷新晚一步就会让新一轮扫描用到旧名单。
  `OnLoaded` 里也调一次，保证 `PostSetupContent` 之前快照已就位
- **扫描开关与其他扫描口径同一轮都变了只扫一次**：`if (scanChanged) ApplyConfigChangeScan(...)`
  `else if ((onlyFillChanged || filterChanged) && EnableScan) ApplyConfigChangeRescan(...)`。
  反过来逐项 if 会先扫一遍、再撤掉重扫一遍（同一份配置白扫两遍）；
  而 `Run` 里的 `IsOnlyFillMissing()` 是扫描开始时现读配置、`BiomeFilter.Read()` 读的是
  本轮 Save 已经刷新的快照，所以「同时打开」时只走 `ApplyConfigChangeScan` 也能带上新口径
- 主菜单时 BTitles Instance 为 null → 静默跳过
- 进世界后 PostSetupContent 处理初始状态
- 两个扫描相关的回调共用 `BeginScanConfigChange()` 做前置（`LoadCompleted` 判空 + 丢翻译缓存 +
  取 Mod 实例）。抄三遍的坏处不是难看，是将来加一条前置必定漏掉其中一处；
  配色回调不走这里，它不需要丢翻译缓存

`TitleColorMappings` 的 `BiomeColorMapping` 与黑/白名单的 `BiomeFilterEntry` 都必须留在本模组程序集内：
`ConfigManager.RegisterLocalizationKeysForMemberType` 只对
`type.Assembly == owningAssembly` 的 class/enum 递归注册字段键，
放到外部程序集会导致 `Title`/`Color`/`Name` 的 Label/Tooltip 取不到。

---

## 9. 调试日志系统

### DebugLog 设计

三级日志写入器，由 `Mod.Load` 时注入：

- `InfoWriter` → `Logger.Info`（受 `EnableDebugLog` 控制）
- `WarnWriter` → `Logger.Warn`（不受开关控制，始终输出）
- `ErrorWriter` → `Logger.Error`（不受开关控制，始终输出）

**逐条类日志一律走 `DebugLog.Info`**：`SkippedExistingBiome`（仅补漏跳过）与
`SkippedPlaceholder`（占位群系）都可能一次刷出上百条，写进 `Logger.Info` 会把正常日志淹掉；
总数在 `ScanSummary` 里已有，细节留给排查时按需打开「调试日志」。

### 冷却缓存

`Enabled` 属性带 3 秒冷却时间，避免 UI 渲染路径中高频查询 `ModContent.GetInstance`：

- 缓存 `TimedValueCache._cachedValue`（`volatile bool`，保证跨线程可见性）
- `TimedValueCache._lastCheckMs`（`long` 不能 `volatile`，改用 `Volatile.Read` / `Volatile.Write`）
- 修改配置后最多 3 秒生效，无需重载

**时间基准必须是单调时钟**：`_lastCheckMs` 用 `Environment.TickCount64`（自开机起的毫秒数），
**不能用** `DateTime.UtcNow.Ticks`。后者跟着系统时钟走，NTP 校正或用户手动改时间会把它往回拨，
此时 `now - last` 变成负数、恒小于冷却值，缓存就再也不刷新——表现是「改了配置没反应，
重启游戏才好」，而日志里看不出任何异常。`TickCount64` 单调递增，不受系统时间调整影响。
`BiomeRegistrar` 的检测异常告警限频（`LastDetectionWarnMs`）用同一套基准，
那里时钟回拨的后果是反方向：告警再也不会出现。

**逐条日志不重复写**：`LogBiomeEntry` 一次写三行（`BiomeEntry` / `BiomeDetail` / `BiomeColor`），
早期实现连**翻译缓存命中**路径也写——标题每次刷新都会走到那里，开调试开关后一份日志很快到几十 MB。
现在缓存命中路径直接返回、不记日志：命中说明结果与上次一致，完整解析链已经为每个群系记过一次。
想看新日志，切一次语言或改一次配置即可（两者都会清空缓存）。

---

## 10. 线程模型

| 线程 | 运行的代码 | 注意 |
| --- | --- | --- |
| **主线程**（Load/Unload/UI） | `Load()`、`Unload()`、`BiomeNameHook.GetActualTitleNameHook`、`RefreshCurrentTitle()`、`BiomeStyleHelper.TryLoadIcon`（经 `ApplyWrites`）、`DebugLog.Enabled`、`BiomeFilter.RefreshSnapshot` | Hook 补色与配色表匹配、翻译链解析、图标加载、即时刷新、名单快照、Unload 清理 |
| **后台线程**（PostSetupContent / 配置回调转出的线程池） | `BiomeRegistrar.RunCore()`、`ScanAllModBiomes()` | 扫描、构建待写入项（但不能碰纹理 / GetData，`Entry.Icon = null`） |
| **配置变更**（主线程回调） | `OnChanged()` → `ApplyConfigChange*` → `Run()` | 由 tModLoader 在主线程触发；`Run` 检测到主线程会转 `Task.Run`，避免全量扫描卡 UI |
| **语言重载**（`OnLocalizationsLoaded`） | `LanguageChangeSystem` → 清缓存 + 重算 `Title` | 热重载语言文件时可能不在主线程，用 `RunOnMainThread` 兜住字典写入 |

**volatile 使用位置** ：

- `_instanceField` / `_biomeDictField` / `_checkFuncsField` — 后台线程读，主线程写
- `_uiField` / `_setupVisualsMethod` — 仅主线程访问，加 `volatile` 只为与其余反射字段保持一致
- `HookInstalled` — Hook 线程读，主线程写
- `_cachedValue`（TimedValueCache 实例字段）— 多线程读（UI 渲染）/ 主线程写（冷却刷新）
- `RegisteredCheckFunc` / `ScannedBiomes` — 后台线程写，主线程读
- `_snapshotEntries` / `_snapshotMode`（`BiomeFilter`）— 主线程写，后台扫描读
- `_scanOwner` / `_scanGeneration`（`BiomeRegistrar`）— 扫描令牌，主线程（配置变更）与后台线程（扫描）同时读写

**配色表相关状态的线程归属**：`MappedOriginalColors` 与 `PendingColorKeys` 都只在主线程的
Hook / 配置回调路径上读写，用 `ConcurrentDictionary` 是为了与既有结构保持一致并留出余量，
不依赖它的并发语义做同步。

**配置回调为什么转后台**：`Run` 的调用方有两个来源，`PostSetupContent` 在后台线程，
而配置保存的回调在主线程。若不做区分，玩家在游戏里点「保存」就会在主线程上同步遍历所有模组的
`GetContent<ModBiome>()`——模组多的整合包上是一次可感知的顿挫。所以 `Run` 在
`Program.IsMainThread` 时把 `RunCore` 甩到 `Task.Run`；字典写入本来就由
`Main.QueueMainThreadAction` 提交，转线程不影响正确性。

**异常语义按来源分层**：`PostSetupContent` 那条照旧往外抛——tML 会归因到本模组并标记加载失败，
这是「拒绝加载」该有的样子。主线程转出去的那条早已脱离 `ModLoader.Load` 的 try，
抛出去只会变成主循环里的裸异常（没人认领、也不会禁用模组），所以在 `Task.Run` 里
catch 后记一条 `DebugLog.Error(ScanFailed)`。

**扫描令牌用代次而不是布尔**：`_scanOwner` 存的是当前持有者的代次，0 表示空闲。
`ClearScannedBiomes` 把令牌置 0 就等于「作废正在跑的那一次」——在跑的扫描提交前用
`IsCancelled(generation)` 一比就知道自己已被取消，直接丢弃结果。

这条是异步化之后必须补的：`Run` 转 `Task.Run` 后，配置变更可能发生在扫描进行中。
原来的布尔令牌说不清「跑的是不是已经被取消的那一次」，只能表示「有人在跑」，
于是关扫描后那次扫描仍会把条目注册进去、改名单还会让两个扫描并发。
提交段另有 `_commitLock` 兜底：取消检查与三个字段赋值必须原子，否则后一次扫描可能插在中间。

---

## 11. 补充翻译（ExtraTitles）

在本地化文件中通过固定键路径补充翻译，无需修改 BTitles 文件：

这一块大概率只补充原版Terraria没有的

```
# en-US hjson
ExtraTitles.Terraria.Aether: Aether

# zh-Hans hjson
ExtraTitles.Terraria.Aether: 以太
```

查找路径：`Mods.BTitlesLocalizationPatch.ExtraTitles.{Scope}.{Key}`

---

## 12. 依赖关系

```t
BTitlesLocalizationPatch
├── BTitles（ProjectReference + build.txt modReferences）
│   └── 引用类型：BiomeTitlesMod、BiomeTitlesUI、BiomeEntry、GeneralConfig
├── MonoMod.RuntimeDetour（tModLoader 内置）
├── tModLoader（原生 SDK）
└── 运行环境：.NET 8.0
```

---

## 13. 设计决策记录

| 决策 | 选择 | 替代方案（不合适） | 理由 |
| --- | --- | --- | --- |
| Hook 方式 | MonoMod 替换式 Hook（replacement） | 反编译改 dll | 无需分发修改版 BTitles |
| 反射访问字段 | 预缓存 FieldInfo | 每次反射、Harmony | 性能可接受，代码直观 |
| 后台线程图标采样 | 禁止，留主线程补 | 同步 GetData | tModLoader 后台线程不允许调用 GetData |
| 占位群系检测 | DeclaringType | GetBaseDefinition | fgpt 等 ILHook 会干扰后者 |
| 日志开关检测 | 3 秒冷却缓存 | 每次实时查配置 | UI 渲染路径高频调用，性能考虑 |
| 翻译链缓存 | ConcurrentDictionary | 无缓存 | 避免每帧重复查 LanguageManager |
| 未翻译检测 | 与 en-US 原文直接比对 | ASCII/语言前缀启发式 | 任意语言目录都准确，覆盖 ja/ko/th 等（v1.2.3） |
| 卸载主线程跳转 | 单次阻塞 RunOnMainThread | 排队回调（可能不执行） | BTitles 不随本模组卸载，字典访问必须主线程（v1.2.3） |
| 配色策略 | 配置枚举（四选一，v1.4.1.1 起含 `BackgroundColorSampling`） | 固定启发式 | 用户可按喜好选择（v1.2.3） |
| 逐群系配色匹配键 | 屏幕上显示的标题名 | 稳定的群系键（`Mod.Biome`） | 玩家填得出来才叫配置，填不出来的表等于没有（v1.4） |
| 逐群系配色落点 | Hook 层（`GetActualTitleNameHook`） | 扫描层（`BiomeRegistrar`） | 只有 Hook 能看见每个 `BiomeEntry`，BTitles 内置的原版群系（如「地狱」）不经过扫描（v1.4） |
| 原色留底 | `MappedOriginalColors`（引用比较） | 每次重新推导 | `BiomeEntry` 没重写 `Equals`，值相等不代表同一条；不记原色就没法撤销命中（v1.4） |
| 配置变更后即时刷新 | 反射调 `SetupTitleVisuals()` | 等群系切换 | BTitles 只在 `newBiome != _currentBiome` 时重算，不推这一把改完表看不到效果（v1.4） |
| 刷新失败的处理 | 只告警一次，退化为下次切换生效 | 抛异常 / 拒绝加载 | 上游改了 UI 内部结构不该让整个模组加载失败（v1.4） |
| 配色表变更检测 | 廉价 `HashCode` 签名 | 逐项比对 | `OnChanged` 拿不到「哪一项变了」，只能整体判断（v1.4） |
| 扫描/仅补漏同轮变更 | 互斥二选一（`scanChanged` 优先） | 逐项 if 各调一次 | 两个开关都落到「扫描要不要重来」，逐项 if 会白扫两遍；`Run` 现读配置，`scanChanged` 分支自带新口径（v1.4） |
| 配色表读取 | 拍平成数组 + 保存即失效 | 每次 `GetInstance` 遍历 `List<T>` | Hook 每次显示标题都会走这条路；配色表改完要立刻生效，不能用时间冷却（v1.4） |
| 配色表 alpha | 完全放开，填什么用什么；仅全零 `(0,0,0,0)` 视为未填 | 沿用 `[ColorNoAlpha]` 强制不透明 | 半透明标题是合法需求，锁死 A 通道等于替玩家做决定。改成「全零=未填」后，默认值仍是不透明白，未填与已填的分界没有丢（v1.4.1.1 放开） |
| 配色还原的落点 | 并进 `UnregisterScannedBiomes` 的主线程段 | 在 `Unload` 里直接写字典 | 那写的是 BTitles 的共享状态，与快照还原是同一次主线程作业，分两处做就有一条没在主线程上（v1.4） |
| 逐条跳过日志的通道 | `DebugLog.Info`（受开关控制） | `mod.Logger.Info` | 开「仅补漏」时被跳过的可能有上百条，无条件写会把正常日志淹掉；总数 `ScanSummary` 里已有（v1.4） |
| `BiomeEntry` 相等语义 | 独立的 `BiomeEntryReferenceComparer`（根命名空间） | 放在 `Scan/` 里由 Hook 侧引用 | 扫描侧与 Hook 侧用的是同一套语义，谁都不该依赖对方的内部类型（v1.4） |
| 配置即时生效 | OnChanged 事件 | 需重载模组 | 更好的用户体验 |
| OnChanged 首轮误报防护 | `OnLoaded` 快照实际加载值到 `_last*` | 硬编码 `true` / 保持 C# 默认值 | tML 在 `ConfigManager.Add`（注册期，早于 Mod.Load）就调一次 OnChanged，此时 LoadCompleted 早退挡住；但菜单保存（OnChangedAll）与游戏内保存触发时模组已加载，若 `_last*` 停在 C# 默认值（false/透明黑），首轮必然把「默认开着」误判成「刚被打开」，平白跑一次全量扫描与重配色。OnLoaded 快照兼容旧存档非默认取值，不硬编码（v1.4.1.1） |
| onlyFillMissing 不进 allBiomes | `allBiomes.Add` 挪进 `RegisterOrUpdateBiome` 的三个被追踪分支 | 主循环无条件 Add | 检测函数由本模组插在最前；仅补漏跳过的条目不写 keyMapping，lambda 回落返回短键 `biome.Name`，BTitles 字典只有命名空间键时 TryGetValue miss → `_currentBiomeEntry=null` → BTitles 原本能显示的群系消失（v1.4.1.1） |
| Load 阶段日志为何是英文字面量 | `LoadCalledTwice`/`HookApplied` 保留英文硬编码 | 本地化键 | tML 管线（ModContent.Load）：`mod.Autoload()`（内部 `LocalizationLoader.Autoload` 只 `GetOrRegister` 注册键，值为 key 本身）→ `mod.Load()` → MSSetupContent 才 `ReloadLanguage` 灌入 hjson 值（LanguageManager.ReloadLanguage → LoadModTranslations）。Load 阶段 `Language.GetTextValue` 只会原样吐出键名如 `Mods.BTitlesLocalizationPatch.Logs.HookApplied`。Unload 及运行期日志（HookDisposeFailed、CleanupRemoveCheckFuncFailed）不受影响，走本地化（v1.4.1.1） |
| 图标补色的位置 | `TryBackfillColor` 在翻译缓存命中判断**之前**调用 | 放在缓存判断之后（原实现） | 缓存命中路径直接 return，不经过后面的解析链；把补色放在缓存判断之后，切换颜色来源时（`TranslationCache` 不清）补色会被缓存早退短路，标题一直白色。配色回调「不丢翻译缓存」的设计不变，只挪补色位置（v1.4.1.1） |
| 语言切换的接收点 | `ModSystem.OnLocalizationsLoaded()`（`Scan/LanguageChangeSystem.cs`） | 在各处判断 `Language.ActiveCulture` 变化后各自清理 | tML 在 `LanguageManager.ReloadLanguage` 末尾统一分发，切语言、语言文件热重载、加载期灌值都会走到；散在各处判文化名必漏其中一条路径。只清翻译缓存不够——`BiomeEntry.Title` 是扫描时烤进去的字符串，必须按新语言重算（v1.4.1.3） |
| 无图标条目的出队 | `ApplyWrites` 里 `TryLoadIcon` 返回 null 即 `PendingColorKeys.TryRemove` | 留在队列等图标到位 | 没有图标资产的群系（如 InfernumMode/StarsAhead 部分条目）永远等不到图标，只会无限占队列、被 `ScanSummary` 计成「未配色」；真有图标的群系下次扫描会重新登记（v1.4.1.1） |
| 配色策略四选一 | `BackgroundColorSampling`（背景色优先，无背景或背景为白时回落采样） | 只保留三策略 | 「背景色优先 + 回落采样」是很多模组图标的实际形态：有背景色用背景色，没有才值得采图标。三策略表达不了这个组合（v1.4.1.1） |
| `BackgroundColor` 语义收紧 | 无背景色 → 纯白，**不采样** | 沿用旧行为（无背景 → 白色 + 采样） | 「背景色」的名字就该只管背景色；回落采样的需求拆给 `BackgroundColorSampling` 表达，两个策略各管各的语义。旧行为用户迁移：选「背景色优先」（v1.4.1.1） |
| 扫描名单的匹配键 | 三种写法都认（`Mod.Biome` / 短键 / 显示名），忽略大小写 | 只认稳定键 | 玩家填得出来才有用：想精确的人用 `Mod.Biome`，想随手屏蔽的人抄屏幕上的标题。不做格式校验，拼错了就是没命中 |
| 名单结构 | 一张表 `ScanFilterList` + 模式 `FilterMode`（Disabled / Blacklist / Whitelist） | 两张表各管一档 | 两张表同时生效就必须回答「冲突时听谁的」，而这个答案无论怎么定都是替玩家猜；一张表 + 模式让「听谁的」变成玩家的显式选择，切模式还不用搬数据 |
| 白名单为空 | 视为「放行」 | 视为「谁都不许过」 | 空表当黑箱会让玩家刚切到白名单模式就发现群系全没了，代价远大于收益 |
| 名单过滤的落点 | `BiomeRegistrar`（扫描期） | Hook 层（同配色表） | 名单只管「这次扫描要不要碰它」，不需要看见每一个 `BiomeEntry`；放扫描侧才能在注册前就拦住，不产生任何字典写入与快照 |
| 名单变更的处理 | 复用「先撤再扫」路径（`ApplyConfigChangeRescan`） | 只标记下次扫描生效 | 名单是扫描期判定，不重扫就是新旧口径混在一份结果里；`Run` 的早退又要求先清掉 `ScannedBiomes` |
| 冷却缓存的时间基准 | `Environment.TickCount64` | `DateTime.UtcNow.Ticks` | UtcNow 会被 NTP 校正或手动改时间往回拨，差值变负后冷却永不满足，缓存再也不刷新（改配置没反应、重启才好）。TickCount64 单调递增，不受系统时间影响 |
| 扫描名单的跨线程读取 | 主线程维护不可变 `HashSet` 快照（`RefreshSnapshot`） | 后台线程直接遍历 `ScanFilterList` | `List` 不支持并发读+写，配置界面随时在增删行。快照每次建新实例、从不原地改，后台只读引用就安全 |
| 配置回调触发的扫描 | 转 `Task.Run`（`Run` 内判 `Program.IsMainThread`） | 主线程直接 `RunCore` | 全量遍历所有模组的 `ModBiome` 会卡 UI；字典写入本就排队到主线程，转线程不影响正确性。异常按来源分层：后台照抛（tML 归因），主线程转出的记日志 |
| 翻译缓存的键 | 要求 `LocalizationScope` 非空才缓存（`canCache`） | 只判 key 非空（`hasValidKey`） | scope 为空时键退化成 `\|{key}`，不同模组的同名群系会共用一条缓存，表现为 A 群系显示 B 群系的名字。与「能不能翻译」分开判断，合并会让无作用域条目直接失去翻译 |
| 缓存命中的日志 | 不写 | 与完整解析链一样写三行 | 命中说明结果未变；标题每次刷新都走这条路径，开调试开关后 client.log 迅速膨胀 |
| 「白色背景」判定 | 只比 RGB，容差 245（`IsNearWhite`） | `!= Color.White` | `Color` 的 `==` 是全分量比较，alpha 不同就不相等；近白也漏判。两者都会被当成有背景色，标题刷成近白色后看不见 |
| 图标加载方式 | `ImmediateLoad`（主线程同步） | `AsyncLoad` | 异步返回时拿不到纹理，条目只能留在待补色队列反复重试，而重试时机挂在「标题被刷新」上——玩家不切群系就一直白着，比卡一下更难排查。要消顿挫应做分帧加载，不是换异步 |
| 扫描检测告警限频 | `TickCount64` + `const 5000ms` | `DateTime.UtcNow` + `TimeSpan` | 与冷却缓存同一套时间基准；时钟回拨在这里的后果是反方向——异常告警再也不出现 |
| 扫描令牌 | 代次整数（`_scanOwner` / `_scanGeneration`） | 布尔标志 | 异步化之后需要区分「跑的是不是已被取消的那一次」；布尔只能表示「有人在跑」，关扫描后旧扫描仍会提交结果，改名单还会让两次扫描并发 |
| en-US 布局识别 | 先目录后文件名，三种布局都认 | 只把文件名交给 tML 的解析函数 | 实测 `en-US.hjson`（prefix 为空）与 `en-US/Mods.X.hjson`（culture 在目录）两种布局都会被原实现跳过，大模组的对照从未生效 |
| en-US 建表范围 | 只收 `.Biomes.*.DisplayName` | 所有 `Mods.*` 叶子 | 实测 StarsAbove 6552 个叶子里只有 12 个属于群系；不限定等于没过滤 |

---

## 14. 本地化键一览

日志消息通过 `Mods.BTitlesLocalizationPatch.Logs.{Key}` 本地化，支持中/英文
（en-US `Logs:` 块共 43 键）。

> **写 hjson 时注意引号**：值里若出现 `"`，整个值必须写成**完整闭合**的双引号字符串
> （`Key: "say \"hi\""` 这种不行，hjson 只认无转义的成对引号）。像
> `Key: "Only fill missing" changed` 这样引号包一半的写法会让整份文件解析失败，
> 报错只有一行 `Error loading Mods.BTitlesLocalizationPatch.hjson`，不会指到具体键。
> 想强调某个词，用中文引号或干脆不加引号。

> **例外**：仅剩两处硬编码英文字符串未走本地化——`BTitlesLocalizationPatch.cs` 的
> `Mod Load called again, skipped` 与 `GetActualTitleNameHookInstaller.cs` 的
> `GetActualTitleName Detour patch applied successfully`。两者都在 Mod.Load 阶段触发，
> 此时本地化值尚未填充，取键只会原样吐出键名（见「设计决策记录」末行决策）。

| Key | 用途 |
| --- | --- |
| `ScanEnabled` | 开始扫描 |
| `ScanningMod` | 开始处理某个模组的 ModBiome 列表（{0}=模组名，{1}=数量） |
| `ScanSummary` | 扫描总结（新增/更新/跳过/字典数/检测数/未配色数） |
| `ScanToggleOn` / `ScanToggleOff` | 扫描开关变更 |
| `RescanOnFilterChanged` | 扫描黑/白名单变更触发先撤再扫 |
| `RescanOnOnlyFillMissing` | 「仅“Biome Titles”未添加的群系」变更触发先撤再扫 |
| `SkippedByBlacklist` / `SkippedByWhitelist` | 被扫描名单过滤的群系（走 `DebugLog.Info`，与 `SkippedExistingBiome` 同通道） |
| `NewBiome` | 新增群系（带未配色标记） |
| `SkippedExistingBiome` | 仅补漏模式下逐条记录被跳过的已有群系 |
| `BiomeEntry` / `BiomeDetail` | 每个群系的解析日志 |
| `IconPresent` / `IconNull` | 图标是否存在（`BiomeDetail` 的 `{1}` 片段） |
| `BiomeColor` | 标题颜色 RGB |
| `CustomOverride` | 用户自定义覆盖命中 |
| `RestyledOn` / `RestyledOff` | 配色批量刷新 |
| `RefreshUnavailable` | 取不到 BTitles 的 UI 字段，即时刷新退化为「下次切换生效」（只报一次） |
| `RefreshFailed` | 反射调用 `SetupTitleVisuals` 失败（{0}=异常消息） |
| `UnregisteredBiomes` | 热卸载/Unload 撤除计数 |
| `RestoredBiomes` | 热卸载/Unload 还原原标题计数 |
| `UnloadHopFailed` | Unload 主线程跳转失败（{0}=异常消息） |
| `EnUsMapFailed` | 构建模组 en-US 对照表失败（{0}=模组名，{1}=异常消息） |
| `ConfigNull` / `ScanConfigNull` | 配置为 null 的防御日志 |
| `IconLoadFailed` | 图标加载失败 |
| `SampleColorFailed` | 图标采样失败 |
| `NativeTitleFailed` | 回退原始标题时 BTitles 原生解析失败 |
| `FormatError` | `string.Format` 处理**日志模板**时抛异常（模板未转义 `{`/`}` 等），见 `BiomeRegistrar.cs:580-598` |
| `CacheReadFailed` | 配置读取失败，沿用缓存旧值 |
| `HookMethodMissing` | Hook 安装找不到目标方法，拒绝加载 |
| `ScanTargetMissing` | 反射目标缺失，拒绝加载 |
| `ScanFailed` | 扫描群系失败，拒绝加载 |
| `ScanApplyFailed` | 主线程提交扫描结果失败 |
| `DetectionEx` | 单个群系检测异常 |
| `DefaultColorMarker` | 未配色的标记符号 |
| `SkippedPlaceholder` | 跳过占位群系（走 `DebugLog.Info`，与 `SkippedExistingBiome` 同通道） |
| `HookDisposeFailed` | Hook `Dispose` 失败（{0}=异常消息；Unload 阶段触发，本地化可用） |
| `CleanupRemoveCheckFuncFailed` | 清理/卸载时移除检测函数失败（{0}=异常消息） |
| `RetranslatedTitles` | 语言切换后重译的已扫描标题数（{0}=数量，走 `DebugLog.Info`） |
| `RetranslateFailed` | 语言切换重译失败（{0}=异常消息；吞掉异常避免中断其他模组的本地化重载） |

另有 `SourceTags` 块（`CustomName` / `ModLocalization` / `BTitles` / `ExtraTitles` / `Fallback`），
用于 `BiomeDetail` 的 `{2}` 来源标签。

---

## 15. 已知限制

1. **未翻译检测依赖 en-US 资产**：英文原文对照机制要求模组在打包时附带 en-US 本地化
   文件；若模组没有 en-US hjson、或键只在运行时注册，该模组仍走旧的纯 ASCII 回退，
   此时的判读与任何基于启发式的方案一样有边界情况
2. **主线程跳转失败的残留**：Unload 与热卸载都依赖阻塞 `RunOnMainThread`；tModLoader
   保证 Mod.Close/UnloadContent 阶段主线程可用，但若跳转本身抛异常（极端时序），补丁按
   `UnloadHopFailed` 记日志并放弃撤除，BTitles 字典会保留本次会话的少量条目直到 Biome
   Titles 下次重载。此时 `RegisteredCheckFunc` 与 BTitles `BiomeCheckFunctions`
   列表里的检测 lambda 一并残留（catch 只调了 `ClearScannedBiomes`）；重载后新会话会
   重新注册自己的 lambda，残留项引用的静态字段随域重置，不会再产生写入，但列表里的死引用
   要等 BTitles 自身重载才消失
3. **PendingColor 只统计新增**：未配色的群系计数（ScanSummary 第 6 个参数）只统计本次
   `added` 的条目；已注册条目重刷配色后的采样数量不计入该汇总
4. **逐群系配色表按显示名匹配**：玩家填的是当前语言下屏幕上的那行字，切换语言后同一群系
   的显示名会变，原来那一条可能就不再命中。这是「眼见为实、零成本配置」的必然代价——
   换成稳定键虽然跨语言稳定，但玩家根本填不出来
5. **即时刷新依赖反射**：`SetupTitleVisuals` 是 BTitles 的 private 方法，上游改名后本模组
   会退化成「下次切换群系才看到新颜色」，只报一次 `RefreshUnavailable`。功能不失效，
   只是少了那一下即时反馈
6. **扫描名单按显示名匹配同样受语言影响**：名单支持三种写法，其中「屏幕上显示的标题名」
   与配色表是同一个坑——切语言后同一群系的显示名会变，用显示名填的那条可能不再命中。
   用 `Mod.Biome` 命名空间键或短键填写则不受语言影响，需要跨语言稳定时用键
