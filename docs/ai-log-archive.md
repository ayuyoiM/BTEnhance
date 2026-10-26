# AI 工作流水归档

> `AGENTS.md` 的「流水」只保留最近 3 条，更早的整段移到这里。
> 倒序排列，最新在上。写入格式与 `AGENTS.md` 的「怎么写」一致。
> 日常不用打开，需要考古时再查。

---

### 2026-10-26 · 建立 AGENTS.md

改动

- `AGENTS.md` 新增 —— AI 协作文档，规则 + 当前状态快照 + 倒序流水

为什么

项目已有 `PROJECT.md`（架构真相）、`README.md`、`changelog.txt`，但缺一份「谁在什么时候为什么这么改」的记录。
新增文件的最大风险是与 `PROJECT.md` 互相抄、各说各话，所以划死边界：本文件只写变更与决策，
架构细节一律留给 `PROJECT.md`，同一条信息只写一处。

结构选双段式（快照 + 倒序流水）而非纯追加日志：纯日志读久了像考古，看不出当前状态；
纯快照又会丢历史，且每次重写成本高。

验证

- 快照内容取自 `build.txt`、`git log`、`git status`，非推测
- 行尾按项目约定写成 CRLF

### 2026-10-26 · 本地化收尾：文案改名、名单折叠、英文同步

改动

- `Localization/zh-Hans_*.hjson` —— `FilterMode.Label` 改「群系黑白名单模式」、`ScanFilterList.Label` 改「名单」，
  `Logs` 块按功能重新分组，4 处 `// Tooltip: ""` 注释改成真值（键数 119 → 123）
- `Localization/en-US_*.hjson` —— 按中文顺序与分组重写 `Logs` 块，逐条对齐措辞
- `config/ModConfig.cs` —— `BiomeFilterEntry` 加 `[Expand(false)]`，名单项默认折叠
- `README.md`、`PROJECT.md` 第 6.7 / 8 节、三份 `description*`、`changelog.txt` —— 跟随 UI 改名同步

为什么

- 「扫描名单」作为功能名保留（README 章节、description 功能列表、`PROJECT.md` 正文），
  配置页上的项名才用「群系黑白名单模式」+「名单」。两者不必强求一致
- 折叠机制与逐群系配色表相同：`ObjectElement` 读的是元素类型上的 `Expand` 特性，
  不是列表属性上的。名单往往几十条，全展开会把配置页撑爆

验证

- zh / en 键集完全一致（各 123），花括号 86/86
- `dotnet build` 0 error 0 warning
- 提交 `31a5703`，9 files changed，未 push

### 2026-10-26 · v1.4.2 文档同步并提交

改动

- `PROJECT.md` —— 版本 v1.4.1.1 → v1.4.2，核心能力四项 → 五项，`Logs` 键数 40 → 43，
  第 15 节追加「名单用显示名填写受语言影响」
- `changelog.txt` / `README.md` / 三份 `description*` —— 补扫描名单条目，中英对齐
- `.gitignore` —— 移除 `PROJECT.md` 那行，项目文档纳入版本控制

为什么

`.gitignore` 之前把 `PROJECT.md` 排除了，等于把最完整的技术文档挡在版本控制外。
纳入后改 `PROJECT.md` 会出现在 `git status` 里，提交时别漏。

验证

- 提交 `5ac240a`（后被 amend 为 `4e0c734`），15 files changed
- 未 push

### 2026-10-26 · 尝试改内部名失败并回滚

改动

- 12 个 `.cs` 内容替换 + 4 个文件 `git mv` 已完成，文件夹重命名失败，全部回滚
- 回滚后 `git status` 与动手前完全一致

为什么

目标是改日志前缀 `[BTitlesLocalizationPatch]` → `BiomeTitlesEnhance`。查清后发现日志前缀
= `Mod.Name` = 程序集名 = 文件夹名，`build.txt` 的 `displayName` 管不到，只能动内部名。
代价是玩家配置丢失 + 工坊旧 `.tmod` 残留双 Hook，用户随即叫停。

失败原因：`Device or resource busy`——tModLoader / 编辑器占着目录，`dotnet build-server shutdown` 也解不开。

回滚要点：反向 `sed` + 反向 `git mv` + 把被 `sed` 改成 LF 的 `.cs` 转回 CRLF，
否则 `git status` 会多出纯行尾差异的 `M`。

验证

- `git status` 与动手前逐行一致

### 2026-10-26 · 扫描名单功能落地（三轮收敛）

改动

- `Scan/BiomeFilter.cs` 新增 —— `Read()` 扫描开始读一次配置，`Evaluate()` 返回
  `Pass / Blacklisted / NotWhitelisted`
- `config/ModConfig.cs` —— `ScanFilterMode` 枚举 + `FilterMode` + 单表 `ScanFilterList`，
  `ComputeFilterSignature` + `_lastFilterSignature`
- `Scan/BiomeRegistrar.cs` —— `Run` 读 filter 传入 `ScanAllModBiomes`，过滤落在占位检测之后、
  `RegisterOrUpdateBiome` 之前
- `BTitlesLocalizationPatch.cs` —— `ApplyConfigChangeOnlyFillMissing()` 改名
  `ApplyConfigChangeRescan(string logKey)`，日志措辞由 `logKey` 决定
- 两份 hjson 补键；`PROJECT.md` 第 2 / 4 / 6.1 / 6.7 / 8 / 13 / 14 节同步

为什么

三轮修正，每轮都是同一个问题的不同侧面：

1. 初版用两张表（黑名单 + 白名单）→ 用户指出同时生效会冲突
2. 改成模式 + 各读一张表 → 用户要求合并成一张
3. 收敛为一张表 + 一个模式 —— 模式决定这张表被解释成黑名单还是白名单

最终设计的理由：两张表同时生效就必须回答「冲突时听谁的」，而这个答案无论怎么定都是替玩家猜；
一张表 + 模式让「听谁的」变成玩家的显式选择，切模式还不用搬数据。

配套决策：

- 白名单为空 = 放行 —— 玩家刚切到白名单模式、还没填内容时，不该一键把群系全扫没
- 三种写法都认（`Mod.Biome` / 短键 / 屏幕显示名），`OrdinalIgnoreCase` + `Trim`，空项丢弃
- 名单只管扫描期 —— BTitles 自己收录的群系不经过扫描，名单管不到它们
- `FilterMode == Disabled` 时改表内容不触发重扫 —— 名单不参与判定，重扫纯属白干；
  但签名仍无条件更新，切回启用时不会被当成「刚改过」
- 名单变更走「先撤再扫」（`ApplyConfigChangeRescan`），因为 `Run` 有「已有生效结果就早退」的守卫

上游限制：tML 的 `ModConfig` 没有条件显示特性，两张表在页面上始终可见（现已合并为一张，此问题消失）。

验证

- `dotnet build` 0 error 0 warning
- 修掉 `BiomeRegistrar.cs` 里 `DisplayName.Value` 的 CS8602（改 `?.Value ?? biome.Name`）
- zh / en 键集一致，花括号 86/86 配对
- tML 打包未复验：被 `TML003` 挡住（游戏正在运行），见 `AGENTS.md` 的「下一步 / 待验证」
