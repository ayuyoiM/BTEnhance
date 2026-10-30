# AGENTS.md

> 面向：接手本项目的 AI（以及愿意旁观的人类）。
>
> 本文件只回答「最近发生了什么、为什么」，架构真相看 `docs/PROJECT.md`。
>
> 最后更新：2026-10-30 | 当前版本：v1.4.2.1

---

## 0. 规则

先读完这一节，再动任何东西。规则不是建议。

### 0.1 阅读顺序

1. 本文件「当前状态」—— 有没有进行中的事、有没有已知的坑
2. `docs/PROJECT.md` —— 架构、生命周期、翻译回退链、配置系统、决策记录
3. 再读要改的源码

不要跳过第 2 步直接读代码。BTitles 的反射、Hook、主线程跳转有大量「为什么非得这么写」只记在 `docs/PROJECT.md` 里，
光看代码会得出错误结论，然后把一个刻意的设计当成 bug 改掉。

### 0.2 什么时候写

写（满足任意一条）：

- 改了代码 / 配置 / 本地化 hjson / 构建元数据（`build.txt`、`changelog.txt`）
- 改了面向人类的文档（`README.md`、三份 `description*`、`docs/PROJECT.md`）
- 做出了技术选型，或否决了某个方案（包括「试了但回滚」——这类最值钱）
- 踩到坑并定位到原因，尤其是下次还会再踩的
- 版本号变化、提交 / push 状态变化

不写：

- 纯问答、查资料、只是读代码看逻辑
- 无结论的探索（例外：结论是「还没查清」的，要写进「下一步 / 待验证」）
- 工具报错原文、临时文件路径、一次性命令

判断标准一句话：三个月后还有人需要知道这件事吗？ 不需要就不写。

### 0.3 怎么写

一次任务 = 一条流水，追加到「流水」顶部（最新在上，不往下堆）。

**滚动窗口**：「流水」只保留最近 3 条。写第 4 条时，把最旧的一条整段移进
`docs/ai-log-archive.md`（插到该文件最上方，保持倒序）。这样 AGENTS.md 体量恒定，历史也不丢。

每条固定四段，缺一不可：

1. 标题 —— 动词开头，一句话说清干了什么
2. 改动 —— 文件级清单：`路径` + 一句话作用
3. 为什么 —— 选这个方案的理由，以及否决了什么
4. 验证 —— `dotnet build` 结果、键数校验、是否提交

硬性要求：

- 叙述用中文；标识符、类型名、API、路径、报错原文一律保持英文原样
- 不写「已完成」「成功」这类空话，写具体结果（`0 error 0 warning` / `123 键对齐` / 提交 hash）
- 没验证的就写「未验证」并说明原因，不要含糊过去

### 0.4 写完必须同步当前状态

流水写完，回头改「当前状态」：

| 发生了什么            | 改哪                           |
|-----------------------|--------------------------------|
| 进行中的事做完了      | 从「进行中」删掉，流水里已记录 |
| 出现新的坑            | 加进「活跃的坑」               |
| 版本号 / 提交状态变了 | 改「项目坐标」                 |
| 下一步变了            | 重写「下一步 / 待验证」        |

「当前状态」是快照，不是历史。 过期的待办直接删，不要留「曾经想做」的僵尸条目。

### 0.5 与 docs/PROJECT.md 的边界

|          | AGENTS.md（本文件，根目录） | docs/PROJECT.md                              |
|----------|-----------------------------|----------------------------------------------|
| 面向     | AI 为主                     | 人类读者                                     |
| 内容     | 变更、决策、坑、当前状态    | 架构真相、文件结构、生命周期、回退链、配置表 |
| 时间性   | 流水，带日期                | 无时间，永远描述「现在」                     |
| 更新时机 | 每次任务结束                | 结构或行为变了才改                           |

同一条信息只写一处。 架构细节进了 `docs/PROJECT.md`，这里只写「改了 docs/PROJECT.md 的哪一节」，不复述内容。

反过来，`docs/PROJECT.md` 里不写「某天改了什么」，那是本文件的职责。

### 0.6 硬性禁止

- 不修改「流水」里已有的历史条目。写错了就在新条目里更正，并注明「更正 XX」
- 不把 `bin/`、`obj/`、`.vs/`、`.dsh-ops/`、`.workbuddy-ai/` 里的产物当成果写进来
- 不记录密钥、token、玩家个人数据
- 不声称做了没做的事。没跑 build 就说没跑，没进游戏测试就说没测

### 0.7 构建

改完代码必须构建。没构建就说「已完成」，等于没说。

两条路径，产物一样（都由 `tMLMod.targets` 处理打包），选一条即可。

#### 方式一：tModLoader 内置（推荐）

游戏内 Workshop → Develop Mods → Build（或 Build + Reload）。tML 自己调 Roslyn，（文本以游戏内文本为准）

产物直接落到 `Mods/`，接着 `Reload Mods` 就能进游戏验证。

- 优点：不用管依赖路径，tML 知道自己装在哪
- 缺点：必须开着游戏；构建日志混在 `client.log` 里，比终端难读

#### 方式二：外部 `dotnet build`

在本目录执行：

```sh
dotnet build
```

前置条件（缺一个就还原失败）：

| 需要                                                                  | 位置                        | 说明                                                                          |
|-----------------------------------------------------------------------|-----------------------------|-------------------------------------------------------------------------------|
| [BTitles 源码](https://github.com/n-Name336/BTitles-1.4.3/tree/1.4.4) | `../BTitles/BTitles.csproj` | `csproj` 里是 `ProjectReference` 而非 DLL 引用，光有 `.tmod` 不行，必须有源码 |
| `tModLoader.targets`                                                  | `../tModLoader.targets`     | 转发到 tML 安装目录的 `tMLMod.targets`                                        |

即 `ModSources/` 下要同时存在 `BTitles/` 和 `BTitlesLocalizationPatch/`。

缺 BTitles 源码时，报错出现在还原阶段：找不到 `..\BTitles\BTitles.csproj`。

换机器要改的硬编码路径（两处，分属不同文件，容易只改一处）：

- `../tModLoader.targets` → `D:\Program Files\Steam\...\tMLMod.targets`
- `../BTitles/BTitles.csproj` → `C:\Program Files (x86)\Steam\...\tModLoader.dll` 与 `TerrariaHooks.dll`

#### 已知阻塞

- 游戏运行时构建会失败：报 `TML003`。先关游戏再构建；
  `dotnet build-server shutdown` 不一定解得开（实测无效的情况遇到过）
- `dotnet build` 通过 ≠ 模组能加载：`0 error 0 warning` 只证明 C# 编过了。
  hjson 的运行时校验要等 tML 真正加载模组才跑，动过本地化就必须进游戏验一次

#### 产物

`bin/`、`obj/` 是构建产物，`.gitignore` 已排除。不提交，也不要在「流水」里当成改动写。

---

## 1. 当前状态

### 1.1 项目坐标

- 版本：v1.4.2.1（`build.txt`）
- 显示名：Biome Titles Enhance / 生物群系标题增强
- 内部名：`BTitlesLocalizationPatch`（文件夹 / 程序集 / 本地化键前缀，不可改，见「活跃的坑」）
- 分支：`main`，与 `origin/main` 同步；工作区有未提交改动（本轮跨线程与时钟修复 + 文档，见「流水」）
- 最近提交：`1d22e59` tmodloader更新本地化
- 依赖：BTitles（`modReferences`）、tModLoader、.NET 8.0
- 文档：`AGENTS.md`（本文件，根目录）、`docs/PROJECT.md`（架构地图）、`docs/ai-log-archive.md`（流水归档）、`README.md`（工坊页）
- CI：`.github/workflows/build.yml`（`build` = ubuntu-latest + bash，`localization` = ubuntu-latest）

### 1.2 进行中

（无）

### 1.3 下一步 / 待验证

- CI 未在 runner 上实跑过。首次 push 后要确认 `build` job 能过，重点是 Linux 上
  `dotnet tModLoader.dll -server -build` 能否启动、`-tmlsavedirectory` 是否真把产物钉到
  `$RUNNER_TEMP/tmlsave/Mods`；失败时先看 `tml-logs` artifact
- 本轮修复未进游戏实测：配置保存后重扫（主线程转线程池）、名单快照刷新时机、
  图标 `ImmediateLoad` 与配色表现，都需要进游戏改一次名单与配色表确认

### 1.4 活跃的坑

- 内部名不能改。日志前缀 `[BTitlesLocalizationPatch]` = `Mod.Name` = 程序集名 = 文件夹名，
  `build.txt` 的 `displayName` 只影响显示名，管不到日志 tag。改名代价：配置丢失
  （`ModConfigs/*.json` 按内部名读取）+ 工坊旧 `.tmod` 残留导致双 Hook。
  2026-10-26 试过，卡在文件夹重命名，已回滚。别再试。
- 游戏运行时改不了文件夹名：`Device or resource busy`，`dotnet build-server shutdown` 解不开。
- hjson 引号：值里出现 `"` 必须整段闭合。`Key: "half quoted" tail` 这种会让整份文件解析失败，
  报错只有一行 `Error loading Mods.BTitlesLocalizationPatch.hjson`，不指到具体键。
- 文档行尾是 CRLF（`core.autocrlf=true`）。批量 `sed`、以及 python 脚本里用 `\n` 拼的多行字符串，
  写进去都是 LF，记得整体转回 CRLF，否则 `git status` 冒出一堆纯行尾差异的 `M`。
- 提交可能被 amend：动手前先 `git log --oneline` 看基线，别按记忆里的 hash 操作。
- 章节引用写名称，不写编号：用「当前状态」「流水」「活跃的坑」这类章节名，不要写 1.2、2.3 这种带章节符号的编号。编号会随章节调整失效，而且用户明确嫌密。引用 `docs/PROJECT.md` 时写「第 8 节」这类中文表述。
- 编辑器会重排这份文件：保存后可能变成 LF 行尾、`**` 加粗被剥除、表格 `\|` 转义被改写、
  软换行变成「空行 + 两空格」。用户明确不要加回加粗，只修行尾与空行。
  编辑过本文件后先查这四项再继续。
- 中文 hjson 是手改的：`Logs` 块按功能分组（调试 / 错误 / 图标 / 功能 / 扫描 / 跳过 / 卸载），
  改英文侧时对齐这个顺序，不要重新排序。
- 后台线程遍历可变集合不安全：扫描跑在后台、配置界面在主线程，两边同时动同一份 `List` / `Dictionary`
  会抛 `InvalidOperationException` 或读到撕裂状态。名单已改为主线程快照（见「流水」2026-10-30），
  但 `BiomeRegistrar.ScanAllModBiomes` 仍在后台 `TryGetValue` / `ContainsKey` BTitles 的
  `BiomeDictionary` —— 那条没修。窗口窄（`PostSetupContent` 期间 UI 尚未遍历），但逻辑上仍不一致

---

## 2. 流水

> 最新在上。格式见「怎么写」，四段：标题 / 改动 / 为什么 / 验证。

### 2026-10-30 · en-US 布局识别、建表过滤与扫描取消

改动

- `BiomeNameHook.BuildEnUsMap` —— 新增 `TryResolveEnUsPrefix`：先判目录名再交给 tML 的解析函数，
  同时支持 `en-US_Mods.X.hjson`、`en-US.hjson`、`en-US/Mods.X.hjson` 三种布局；
  `FlattenHjson` 只收 `.Biomes.*.DisplayName`，新增 `CombineKey` 处理 prefix 为空的情形
- `BiomeRegistrar` —— 令牌由布尔 `_scanState` 换成代次 `_scanOwner` / `_scanGeneration`，
  新增 `BeginScan` / `ReleaseScan` / `IsCancelled`；提交段加 `_commitLock`；
  `Rollback` 改带 `generation` 并先判取消，另拆出 `RollbackCommitted` 供主线程提交失败用
- `docs/PROJECT.md` —— 「翻译回退链」补三种布局与建表范围，「回滚」改签名说明，
  「线程模型」补令牌机制，「设计决策记录」新增三行

为什么

上一轮把 `Run` 改成 `Task.Run` 之后，扫描变得可以「跑到一半」：配置变更时 `ClearScannedBiomes`
会把令牌置 0，但后台扫描仍会完成并提交结果——关扫描后条目照旧注册；改名单还会让两次扫描并发，
孤儿检测函数留在 BTitles 的列表里。布尔令牌说不出「跑的是不是已被取消的那一次」，换成代次就能在提交前判定。

布局与过滤是在 `ModReader` 里实测出来的：CalamityMod 只有 dll（本地化由代码注册），
StarsAbove 用 `en-US.hjson`（prefix 为空），NoxusBoss 用 `en-US/Mods.X.hjson`（culture 在目录）。
原实现只把 `Path.GetFileName` 交给解析函数，后两种布局整份被跳过——大模组的 en-US 对照从未生效过。
过滤范围也据实收窄：StarsAbove 的 en-US 有 6552 个叶子，`Biomes.DisplayName` 只有 12 个。

否决「只判 `.DisplayName` 结尾」：那样 6552 个叶子一个都砍不掉，等于没过滤。
否决「只修布局不做过滤」：大模组进表会先吃一遍全表内存，顺序反了。

验证

- `dotnet build` → 0 个错误
- Python 模拟平铺 StarsAbove：6552 个叶子 → 12 个 `Biomes.DisplayName`，键形如
  `Mods.StarsAbove.Biomes.BleachedWorldBiome.DisplayName`
- 未进游戏验证：三种布局的实际命中、扫描取消的表现、提交段加锁后的时序，均未实测

### 2026-10-30 · 修复跨线程与时钟类缺陷，注释迁入 PROJECT.md

改动

- `config/TimedValueCache.cs` —— 冷却时间基准由 `DateTime.UtcNow.Ticks` 改为 `Environment.TickCount64`；
  字段改名 `_lastCheckMs` / `_cooldownMs`
- `Scan/BiomeRegistrar.cs` —— 检测异常告警限频同步改用 `TickCount64`（`LastDetectionWarnMs` +
  `const DetectionWarnCooldownMs = 5000`）；`Run` 拆成 `Run`（主线程转 `Task.Run`）+ `RunCore`
- `Scan/BiomeFilter.cs` —— 扫描侧不再遍历 `ScanFilterList`：主线程 `RefreshSnapshot` 维护不可变
  `HashSet` 快照，`Read()` 只读快照；删掉不再使用的 `FilterSnapshot.Empty`
- `config/ModConfig.cs` —— `OnLoaded` 与 `OnChanged` 调 `RefreshSnapshot(this)`，后者放在
  `ApplyConfigChange*` 之前
- `Scan/BiomeStyleHelper.cs` —— 「白色」判定改 `IsNearWhite`（只比 RGB，容差 245）；`ArrayPool.Return`
  移入 `finally`；采样魔法数字抽成 `MinOpaqueAlpha` / `MinBrightnessSum` / `MaxBrightnessSum`；
  删掉 `GetTitleColors` 里与 `GetStylingSettings` 重复的 `default` 兜底；配置缺失时的回退策略由
  `BackgroundColor` 改为 `BackgroundColorSampling`
- `BiomeNameHook.cs` —— 新增 `canCache`（scope 非空才缓存）；`TranslationCache` 值类型由
  `(Text, Tag)` 元组简化为 `string`；缓存命中路径不再写三行调试日志
- `README.md` —— 颜色来源默认值改为「背景色优先」；`AGENTS.md` 链接补 `docs/` 前缀
- `description.txt` / `description_workshop.txt` / `description_workshop_en-US.txt` —— 默认值说明同步
- `docs/PROJECT.md` —— 理由迁入：「翻译回退链」缓存策略、「扫描名单」快照、「自动配色系统」近白判定、
  「运行时补色」ImmediateLoad 取舍、「配置系统」默认值与快照刷新、「调试日志系统」时钟基准与日志去重、
  「线程模型」配置回调转后台与异常分层、「设计决策记录」新增 8 行；全文 17 处 `§` 改为章节名

为什么

补齐「同一件事在 A 处想到了、B 处忘了」的几处，同时把长篇设计理由从代码注释搬进 `docs/PROJECT.md`，
代码里只留一行结论加指向（用户明确要求）。

跨线程是重点：`ScanFilterList` 与 BTitles 的 `BiomeDictionary` 都是可变集合，后台扫描遍历时主线程
可能在写。名单这条更危险——配置界面是活的。快照方案让后台只读一份发布后不再修改的 `HashSet`，
不依赖并发集合语义。

时钟基准换单调时钟，是因为 `UtcNow` 被 NTP 校正往回拨时 `now - last` 变负、冷却条件恒成立，
缓存再也不刷新（改配置没反应、重启才好）。同一份代码里两处限频统一用这套基准。

否决把图标加载改成 `AsyncLoad`：异步返回时拿不到纹理，条目只能留在待补色队列反复重试，而重试时机
挂在「标题被刷新」上，玩家不切群系就一直白着，比偶发一次顿挫更难排查。真要消顿挫应做分帧加载。

否决改代码默认值去迎合文档：`v1.4.2` 已上线，默认值是既成事实，改文档才对。

验证

- `dotnet build` → 0 个错误，4 个警告（全是 `BTitles.csproj` 的 MSB3245 / MSB3243，与改动前基线一致）
- 首次构建抓到两处漏改（缓存值改 `string` 后残留 `.Text` 与三参 `CacheEntry`），已修
- 未进游戏验证：快照刷新时机、配置保存后重扫、图标加载与配色均未在游戏内实测。
  动过配置路径，需进游戏改一次名单与配色表确认
- 已提交 `c16ddba`（13 文件，+1327/−121）；版本号 1.4.2 → 1.4.2.1，附注 tag `1.4.2.1`
  打在发布提交上（本地，未 push）

### 2026-10-29 · 构建 job 从 Windows 迁到 Linux

改动

- `.github/workflows/build.yml` —— `build` job 改 `ubuntu-latest` + `shell: bash`，全部步骤用 bash 重写；
  产物路径用 `-tmlsavedirectory $RUNNER_TEMP/tmlsave` 钉死；warning 过滤正则改 `[^(:]*` 以覆盖子目录
- `AGENTS.md` —— 新增本条流水，「补充构建方式」移入 `docs/ai-log-archive.md`

为什么

`tModLoader.zip` 是全平台合并包：本机 `Libraries/Native/` 下同时有 `Windows/`、`Linux/`、
`Linux-arm64/`、`OSX/` 四套原生库，根目录还有 `start-tModLoader.sh`。所以 Linux runner 解压即用，
不需要 SteamCMD。之前误判成 Windows 专用包，是漏看了那些 `.sh`。

`-build` 在图形初始化之前就退出：`Program.cs.patch` 里 `Main.dedServ = isServer` 后立即
`ModCompile.BuildModCommandLine(build)` → `Environment.Exit(0)`，SDL / FAudio / HighDPI 全在其后
且被 `isServer` 挡掉。所以无显示器的 runner 能跑，也不需要 `LD_LIBRARY_PATH`。

否决 SteamCMD（`+login anonymous +app_update 1281930` 可行但多下 400MB，没必要）；
否决 `-p:AllowUnsafeBlocks=false`（`Utils.ParseArguements` 遇 `-` 开头即切换 key，
`-unsafe -tmlsavedirectory /path` 解析正确，官方 CI 同样写法且跑得通）；
否决双 job 并存（回滚靠 git 就够）。

验证

- `pyyaml` 解析通过；6 个 bash step 全部 `bash -n` 通过
- python 替换在真实上游 `BTitles.csproj` 上实测：`<Reference>` 2 → 0，空 `<ItemGroup>` 一并清掉
- warning 正则三组实测：命中 `BTitlesLocalizationPatch/Scan/BiomeFilter.cs` 与 analyzer 警告；
  不误伤 `BTitles/GeneralConfig.cs` 与 `.csproj` 级；真实构建日志 0 命中
- 更正 2026-10-28 条目的「warning 过滤正则实测」：当时的 `[^\\/]+` 不允许路径含分隔符，
  子目录（`Scan/`、`config/`）里的 warning 会被漏判。本次改 `[^(:]*` 修掉
- 未在 runner 上实跑：Linux 上 `dotnet tModLoader.dll -server -build` 是否真跑通，只能等首次 push
