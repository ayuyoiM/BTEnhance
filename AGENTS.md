# AGENTS.md

> 面向：接手本项目的 AI（以及愿意旁观的人类）。
>
> 本文件只回答「最近发生了什么、为什么」，架构真相看 `docs/PROJECT.md`。
>
> 最后更新：2026-10-26 | 当前版本：v1.4.2

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

- 版本：v1.4.2（`build.txt`）
- 显示名：Biome Titles Enhance / 生物群系标题增强
- 内部名：`BTitlesLocalizationPatch`（文件夹 / 程序集 / 本地化键前缀，不可改，见「活跃的坑」）
- 分支：`main`，ahead 2（未 push），工作区干净
- 最近提交：`31a5703` 本地化 / 英文同步 / 名单折叠 / 文档同步
- 依赖：BTitles（`modReferences`）、tModLoader、.NET 8.0
- 文档：`AGENTS.md`（本文件，根目录）、`docs/PROJECT.md`（架构地图）、`docs/ai-log-archive.md`（流水归档）、`README.md`（工坊页）

### 1.2 进行中

（无）

### 1.3 下一步 / 待验证

- v1.4.2 的 tML 打包未复验：上次 `dotnet build` 被 `TML003` 挡住（游戏正在运行），
  hjson 的运行时校验没跑到。游戏关闭后需 `dotnet build` 复验一次
- 两个本地提交未 push：`4e0c734`、`31a5703` 都还在本地

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

---

## 2. 流水

> 最新在上。格式见「怎么写」，四段：标题 / 改动 / 为什么 / 验证。

### 2026-10-26 · 流水改为滚动窗口，修复编辑器造成的格式损坏

改动

- `AGENTS.md` 的流水 —— 改为滚动窗口，只保留最近 3 条
- `docs/ai-log-archive.md` 新增 —— 承接更早的流水，倒序排列
- `AGENTS.md`「怎么写」—— 补「滚动窗口」机制说明
- `AGENTS.md`「项目坐标」—— 文档行加入归档文件
- `AGENTS.md` 全文 —— 行尾 LF 恢复 CRLF；13 处「空行 + 两空格」连回原段落

为什么

流水占全文 51%（201 / 394 行），每轮对话都要整份读入，而且会随时间无限增长。
滚动窗口让 AGENTS.md 的体量恒定，历史仍可在归档文件里查到，两头都不亏。

格式损坏来自外部 Markdown 格式化工具：行尾被统一成 LF、加粗标记被剥除、
表格转义竖线被改写、软换行变成「空行 + 两空格」。按用户要求只修行尾与空行，不加回加粗。

验证

- 流水保留 3 条，归档 5 条
- 行尾 CRLF 校验通过
- 未加回 `**` 加粗（用户明确不要）

### 2026-10-26 · 补充构建方式

改动

- `AGENTS.md` 新增「构建」一节 —— tML 内置构建与外部 `dotnet build` 两条路径、
  前置条件表、换机器的硬编码路径、已知阻塞、产物目录约定

为什么

原文档只记录了「`dotnet build` 0 error 0 warning」这类验证结果，没写怎么构建。
AI 接手时会踩三个坑：不知道 `..\BTitles\BTitles.csproj` 是 `ProjectReference`
（要源码，光有 `.tmod` 不够）、不知道上级还缺不了 `tModLoader.targets`、
不知道游戏运行时构建会撞 `TML003`。

按「与 docs/PROJECT.md 的边界」，构建属于操作流程，只写在本文件，`docs/PROJECT.md` 不重复。

验证

- 前置条件取自 `BTitlesLocalizationPatch.csproj`、`../tModLoader.targets` 与上级目录实际结构，非推测
- 已确认 `../BTitles/BTitles.csproj`、`../tModLoader.targets` 存在

### 2026-10-26 · 文档归入 docs/，AGENTS.md 留守根目录

改动

- `docs/PROJECT.md` —— 从根目录移入 `docs/`（用户手动移动）
- `AGENTS.md` —— 保持在根目录（曾移入 `docs/`，本次移回）
- `AGENTS.md` 全文 —— `PROJECT.md` 引用统一改为 `docs/PROJECT.md`（7 处）
- `docs/PROJECT.md` 的文件结构树 —— 补 `AGENTS.md` 与 `docs/` 两层
- `README.md` —— 新增「开发文档」小节，链接两份文档

为什么

`docs/PROJECT.md` 是给人读的架构文档，进 `docs/` 不影响任何工具链，纯整理。

`AGENTS.md` 不能进 `docs/`：Claude Code、Codex、Cursor、Aider 等只自动加载仓库根目录的
`AGENTS.md`。放进子目录，工具不会发现它，这份文档就退化成「每次对话都要手动贴路径」的东西——
等于白写。

流水里更早的条目保留原有的 `PROJECT.md` 写法，那是当时文件的真实位置，不改写历史。

验证

- 根目录：`AGENTS.md` 存在，`PROJECT.md` 已不在
- `git status`：`D PROJECT.md`、`?? AGENTS.md`、`?? docs/`
- 全文检索 `PROJECT.md`：非历史条目均带 `docs/` 前缀
