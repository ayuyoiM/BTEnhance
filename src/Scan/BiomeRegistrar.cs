#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BTitles;
using BTitlesLocalizationPatch;
using BTitlesLocalizationPatch.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch.Scan
{
    internal static class BiomeRegistrar
    {
        // volatile: 扫描线程（PostSetupContent）写入，主线程读取与清除
        internal static volatile Func<Player, string>? RegisteredCheckFunc;

        // 列表在扫描结束后不再修改，可安全跨线程发布
        internal static volatile List<(string DictKey, ModBiome Biome, bool IsNew)>? ScannedBiomes;

        // 关闭扫描时用它把 BTitles 原有条目还原回去
        internal static volatile Dictionary<string, ExistingEntrySnapshot>? PreScanValues;

        // 扫描线程写入，Hook 与配置变更（主线程）读写，故用并发字典
        internal static readonly ConcurrentDictionary<BiomeEntry, byte> PendingColorKeys = new(
            BiomeEntryReferenceComparer.Instance
        );

        // 同一群系 5 秒内最多告警一次，防止常抛异常的群系刷爆日志
        // 时间基准与 TimedValueCache 一致用 TickCount64：系统时钟回拨会让 UtcNow 的差值变负，
        // 冷却永远不满足，异常告警就再也不出现
        private static readonly ConcurrentDictionary<
            (string Mod, string Biome),
            long
        > LastDetectionWarnMs = new();
        private const long DetectionWarnCooldownMs = 5000;

        /*
        扫描令牌：0 = 空闲，非 0 = 当前持有者的代次
        用代次而不是布尔，是为了让 ClearScannedBiomes 能作废「正在跑」的那一次：
        清空时把令牌置 0，在跑的扫描提交前发现自己的代次不再匹配，就丢弃结果。
        布尔令牌说不清「跑的是不是已经被取消的那一次」，只能表示「有人在跑」
        Run 的调用方既有 PostSetupContent（后台线程）也有配置回调（主线程），
        单靠 ScannedBiomes != null 是读-判-写，两个线程可能同时通过
        */
        private static int _scanOwner;
        private static int _scanGeneration;

        // 提交段的互斥：取消检查与三个字段赋值必须原子，否则两个扫描仍可能交错写入
        private static readonly object _commitLock = new();

        /*
        配色开关不从这里传参：Hook 层判定用的是 BiomeNameHook.IsStylingEnabled()，
        若扫描侧另收一份，两者就有两个真相，配置回调里的 UpdateStylingCache 一慢半拍就会各说各话
        统一从缓存读，调用方不必也不该再带一份
        */
        // 抢令牌并领一个代次；返回 0 表示已有扫描在跑
        private static int BeginScan()
        {
            int generation = Interlocked.Increment(ref _scanGeneration);
            return Interlocked.CompareExchange(ref _scanOwner, generation, 0) == 0
                ? generation
                : 0;
        }

        // 只有仍持有令牌时才释放，避免误清后来者的
        private static void ReleaseScan(int generation) =>
            Interlocked.CompareExchange(ref _scanOwner, 0, generation);

        // 令牌已不是自己的：期间被 ClearScannedBiomes 取消，或被后一次扫描接管
        private static bool IsCancelled(int generation) =>
            Volatile.Read(ref _scanOwner) != generation;

        /*
        扫描入口。主线程来的调用（配置回调）甩到线程池，避免卡住 UI；
        PostSetupContent 那条照旧在本线程执行。异常为何分层处理见 docs/PROJECT.md「线程模型」
        */
        public static void Run(Mod mod)
        {
            if (Program.IsMainThread)
            {
                Task.Run(() =>
                {
                    try
                    {
                        RunCore(mod);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Error(SafeFormat(Localize("ScanFailed"), ex.Message));
                    }
                });
                return;
            }

            RunCore(mod);
        }

        private static void RunCore(Mod mod)
        {
            bool enableStyling = BiomeNameHook.IsStylingEnabled();
            bool onlyFillMissing = IsOnlyFillMissing();
            (var colorSource, var fixedColor) = BiomeStyleHelper.GetStylingSettings();
            // 名单是扫描期判定：这里读一次，整轮共用（改名单由 OnChanged 走「先撤再扫」）
            var filter = BiomeFilter.Read();

            // 抢令牌并领一个代次：这一步是原子的，同一时刻只有一个调用能进来
            int generation = BeginScan();
            if (generation == 0)
                return;

            // 已有生效结果就不再扫：重复扫描会把上次自己新增的条目当成「已有条目」快照，
            // 之后关闭扫描只能还原成扫描后的样子，条目和原值都回不来
            if (ScannedBiomes != null)
            {
                ReleaseScan(generation);
                return;
            }

            // RegisteredCheckFunc 马上要被本次覆盖，旧 lambda 不先摘掉就再也拿不回引用
            // 注意：本方法由 PostSetupContent（后台线程）调用，撤字典必须留给主线程
            try
            {
                Cleanup();

                PendingColorKeys.Clear();
                var biomes = new List<(string DictKey, ModBiome Biome, bool IsNew)>();

                var instance = ResolveBTitlesInstance();
                var biomeDict = ResolveBiomeDictionary(instance);
                var checkFuncs = ResolveCheckFunctions(instance);

                // 后台线程只读遍历：不预取键快照，否则会与主线程写入同抢一个集合
                var scanResult = ScanAllModBiomes(
                    mod,
                    biomeDict,
                    enableStyling,
                    onlyFillMissing,
                    colorSource,
                    fixedColor,
                    filter,
                    biomes
                );

                // 期间可能被 ClearScannedBiomes 取消（关扫描 / 改名单）：结果作废，令牌也已不是自己的
                if (IsCancelled(generation))
                    return;

                var checkFunc = BuildDetectionFunction(
                    scanResult.AllBiomes,
                    scanResult.KeyMapping
                );

                // 取消检查与三个字段赋值必须原子，否则后一次扫描可能正好插在中间，
                // 让前一次的结果覆盖掉后一次的
                lock (_commitLock)
                {
                    if (IsCancelled(generation))
                        return;

                    RegisteredCheckFunc = checkFunc;
                    PreScanValues = scanResult.ExistingSnapshots;
                    ScannedBiomes = biomes;
                }

                // BTitles 在主线程每帧遍历这两个集合，写入必须合并成一次主线程操作
                // 捕获已解析的引用而不重新反射：Unload 会清空反射字段
                var writes = scanResult.PendingWrites;
                var funcToInsert = checkFunc;
                Main.QueueMainThreadAction(() =>
                {
                    // 排队期间可能已被取消（Cleanup 清空了引用），提交会插回一个「无主」的函数
                    if (!ReferenceEquals(RegisteredCheckFunc, funcToInsert) || funcToInsert == null)
                        return;

                    // 也可能已「关扫描」，记录置空、字典已撤，此时提交的条目无人可追踪
                    if (!ReferenceEquals(ScannedBiomes, biomes))
                        return;

                    try
                    {
                        ApplyWrites(biomeDict, writes);
                        checkFuncs.Insert(0, funcToInsert);
                        mod.Logger.Info(
                            SafeFormat(
                                Localize("ScanSummary"),
                                scanResult.Added,
                                scanResult.Updated,
                                scanResult.Skipped,
                                biomeDict.Count,
                                checkFuncs.Count,
                                scanResult.PendingColor
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        // 扫描结果提交不上去，注册的条目与检测函数就全是半成品
                        // 此处已在主线程回调里，早已脱离 ModLoader.Load 的 try：
                        // 抛出去不会被标记归属模组、也不会自动禁用，只会变成主循环里的裸异常
                        // 所以只留一条 Error，不吞、不抛
                        mod.Logger.Error(SafeFormat(Localize("ScanApplyFailed"), ex.Message));

                        // ApplyWrites 是逐条写的，抛出时字典已被改了一半。
                        // 就地撤销本次全部写入：此刻还握着 writes 与 PreScanValues，
                        // 而且正好在主线程，是唯一能干净回退的时机
                        UndoWrites(biomeDict, writes, checkFuncs, funcToInsert);
                        RollbackCommitted();
                    }
                });
            }
            catch
            {
                // 解析或遍历阶段抛出时令牌还握在手里，不放回去就是永久锁死：
                // 用户之后怎么开关扫描都会被上面那次抢占挡掉，再也不会重试
                Rollback(generation);
                throw;
            }
            finally
            {
                // 成功路径也要放令牌：能不能重扫由 ScannedBiomes 判断，令牌只表示「有人在跑」
                ReleaseScan(generation);
            }
        }

        /*
        三个解析器取不到目标就抛 BTitlesIncompatibleException
        BTitles 是硬依赖（build.txt 的 modReferences），此刻必然已加载，
        取不到只能是上游改了结构——那种情况下「跳过扫描」等于玩家看不出任何区别
        */
        private static BiomeTitlesMod ResolveBTitlesInstance()
        {
            if (BTitlesLocalizationPatch.InstanceField?.GetValue(null) is BiomeTitlesMod instance)
                return instance;
            throw new BTitlesIncompatibleException("ScanTargetMissing", "Instance");
        }

        private static Dictionary<string, BiomeEntry> ResolveBiomeDictionary(
            BiomeTitlesMod instance
        )
        {
            if (
                BTitlesLocalizationPatch.BiomeDictField?.GetValue(instance)
                is Dictionary<string, BiomeEntry> dict
            )
                return dict;
            throw new BTitlesIncompatibleException("ScanTargetMissing", "BiomeDictionary");
        }

        private static List<Func<Player, string>> ResolveCheckFunctions(BiomeTitlesMod instance)
        {
            if (
                BTitlesLocalizationPatch.CheckFuncsField?.GetValue(instance)
                is List<Func<Player, string>> list
            )
                return list;
            throw new BTitlesIncompatibleException("ScanTargetMissing", "BiomeCheckFunctions");
        }

        private static ScanResult ScanAllModBiomes(
            Mod mod,
            Dictionary<string, BiomeEntry> biomeDict,
            bool enableStyling,
            bool onlyFillMissing,
            TitleColorSource colorSource,
            Color fixedColor,
            BiomeFilter.FilterSnapshot filter,
            List<(string DictKey, ModBiome Biome, bool IsNew)> scannedBiomes
        )
        {
            int added = 0,
                updated = 0,
                skipped = 0,
                pendingColor = 0;
            var allBiomes = new List<(Mod Mod, ModBiome Biome)>();
            var keyMapping = new Dictionary<string, string>();
            // 待应用的字典写入：扫描在后台线程只读遍历，写入统一留到主线程提交
            var pendingWrites = new List<PendingWrite>();
            var existingSnapshots = new Dictionary<string, ExistingEntrySnapshot>();

            try
            {
                foreach (Mod targetMod in ModLoader.Mods)
                {
                    if (targetMod.Name is "BTitles" or "tModLoader" or "ModLoader")
                        continue;

                    var modBiomes = targetMod.GetContent<ModBiome>().ToArray();
                    if (modBiomes.Length == 0)
                        continue;

                    mod.Logger.Info(
                        SafeFormat(Localize("ScanningMod"), targetMod.Name, modBiomes.Length)
                    );

                    foreach (var biome in modBiomes)
                    {
                        // 没重写 IsBiomeActive 的占位群系（如嘉登实验室）注册了也检测不到
                        if (IsPlaceholderBiome(biome))
                        {
                            // 逐条跳过日志走 DebugLog：与 SkippedExistingBiome 同为批量跳过明细，
                            // 受「调试日志」开关控制，不淹没正常日志；总数在 ScanSummary 里已有
                            DebugLog.Info(
                                SafeFormat(
                                    Localize("SkippedPlaceholder"),
                                    biome.Name,
                                    biome.DisplayName?.Value ?? biome.Name
                                )
                            );
                            skipped++;
                            continue;
                        }

                        /*
                        扫描名单过滤放在占位检测之后：占位群系本来就不会注册，
                        先过名单只会让日志里的跳过原因变模糊
                        按 FilterMode 解释成黑名单或白名单，命中即算「跳过」，逐条明细走 DebugLog，
                        总数并进 ScanSummary 的 skipped，不额外加参数
                        */
                        string displayName = biome.DisplayName?.Value ?? biome.Name;
                        var verdict = BiomeFilter.Evaluate(
                            filter,
                            $"{targetMod.Name}.{biome.Name}",
                            biome.Name,
                            displayName
                        );
                        if (verdict != BiomeFilterVerdict.Pass)
                        {
                            string reasonKey =
                                verdict == BiomeFilterVerdict.Blacklisted
                                    ? "SkippedByBlacklist"
                                    : "SkippedByWhitelist";
                            DebugLog.Info(SafeFormat(Localize(reasonKey), biome.Name, displayName));
                            skipped++;
                            continue;
                        }

                        RegisterOrUpdateBiome(
                            biomeDict,
                            targetMod,
                            biome,
                            enableStyling,
                            onlyFillMissing,
                            colorSource,
                            fixedColor,
                            keyMapping,
                            allBiomes,
                            scannedBiomes,
                            pendingWrites,
                            existingSnapshots,
                            mod,
                            ref added,
                            ref updated,
                            ref skipped,
                            ref pendingColor
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                // 扫描中途失败：字典写入尚未提交，丢弃本次的临时登记后照样抛出
                // 令牌由 Run 的 catch 统一放开，这里不碰，避免两处各放一次
                throw new BTitlesIncompatibleException("ScanFailed", ex.Message, ex);
            }

            return new ScanResult(
                added,
                updated,
                skipped,
                pendingColor,
                allBiomes,
                keyMapping,
                pendingWrites,
                existingSnapshots
            );
        }

        /*
        OnlyFillMissingBiomes：扫描只补漏，BTitles 字典里已有的条目完全不碰
        这份配置只需扫描开始时读一次（低频），不进 TimedValueCache
        */
        private static bool IsOnlyFillMissing()
        {
            var config = ModContent.GetInstance<BTitlesConfig>();
            return config?.OnlyFillMissingBiomes ?? false;
        }

        /*
        用 DeclaringType 而非 GetBaseDefinition() 判断：
        fgpt 等模组的 ILHook 会干扰 GetBaseDefinition 的行为
        */
        private static bool IsPlaceholderBiome(ModBiome modBiome)
        {
            var method = modBiome
                .GetType()
                .GetMethod("IsBiomeActive", BindingFlags.Public | BindingFlags.Instance);
            return method != null && method.DeclaringType == typeof(ModBiome);
        }

        /*
        优先匹配命名空间键 "{模组}.{类名}" 避免同名冲突
        再试 BTitles 短键兼容已有条目
        已有条目不动颜色/图标，那是 BTitles 或上一次扫描给的
        这里只登记写入意图，真正改字典在 ApplyWrites（主线程）
        仅补漏跳过的条目不进 allBiomes：检测函数由本模组插在最前，若把 BTitles 已收录、
        且字典里只有命名空间键的群系也放进去，lambda 回落返回短键会在字典里查不到，
        BTitles 原本能显示的群系反而消失。allBiomes 只在确认被本模组追踪的分支登记
        */
        private static void RegisterOrUpdateBiome(
            Dictionary<string, BiomeEntry> biomeDict,
            Mod targetMod,
            ModBiome modBiome,
            bool enableStyling,
            bool onlyFillMissing,
            TitleColorSource colorSource,
            Color fixedColor,
            Dictionary<string, string> keyMapping,
            List<(Mod Mod, ModBiome Biome)> allBiomes,
            List<(string DictKey, ModBiome Biome, bool IsNew)> scannedBiomes,
            List<PendingWrite> pendingWrites,
            Dictionary<string, ExistingEntrySnapshot> existingSnapshots,
            Mod mod,
            ref int added,
            ref int updated,
            ref int skipped,
            ref int pendingColor
        )
        {
            string namespacedKey = $"{targetMod.Name}.{modBiome.Name}";
            string title = modBiome.DisplayName.Value ?? modBiome.Name;

            /*
            OnlyFillMissingBiomes：字典里已经在（命名空间键或短键命中都算已有）

            就完全不动 —— 不快照、不覆写标题/作用域、不排队写入、不进 scannedBiomes
            只计入 Skipped。BTitles 自己的条目保持原样
            逐条记日志，但走 DebugLog（受调试开关控制）：开这个开关时被跳过的条目可能有上百条，
            无脑写进 tModLoader.log 会把正常日志淹掉；「跳过了多少」在 ScanSummary 里已有总数，
            逐条明细属于排查用的调试信息
            */
            if (onlyFillMissing)
            {
                if (biomeDict.ContainsKey(namespacedKey) || biomeDict.ContainsKey(modBiome.Name))
                {
                    DebugLog.Info(
                        SafeFormat(Localize("SkippedExistingBiome"), modBiome.Name, title)
                    );
                    skipped++;
                    return;
                }
            }

            if (biomeDict.TryGetValue(namespacedKey, out var entry))
            {
                SnapshotExisting(namespacedKey, entry, existingSnapshots);
                pendingWrites.Add(PendingWrite.Update(namespacedKey, title, targetMod.Name));
                keyMapping[namespacedKey] = namespacedKey;
                allBiomes.Add((targetMod, modBiome));
                scannedBiomes.Add((namespacedKey, modBiome, false));
                updated++;
                return;
            }

            if (biomeDict.TryGetValue(modBiome.Name, out entry))
            {
                SnapshotExisting(modBiome.Name, entry, existingSnapshots);
                pendingWrites.Add(PendingWrite.Update(modBiome.Name, title, targetMod.Name));
                keyMapping[namespacedKey] = modBiome.Name;
                allBiomes.Add((targetMod, modBiome));
                scannedBiomes.Add((modBiome.Name, modBiome, false));
                updated++;
                return;
            }

            var newEntry = BuildNewEntry(
                modBiome,
                targetMod,
                enableStyling,
                colorSource,
                fixedColor,
                title
            );
            pendingWrites.Add(PendingWrite.Add(namespacedKey, newEntry, modBiome));
            keyMapping[namespacedKey] = namespacedKey;
            allBiomes.Add((targetMod, modBiome));
            scannedBiomes.Add((namespacedKey, modBiome, true));
            added++;

            bool awaitingSampling = PendingColorKeys.ContainsKey(newEntry);
            if (awaitingSampling)
                pendingColor++;
            mod.Logger.Info(
                SafeFormat(
                    Localize("NewBiome"),
                    modBiome.Name,
                    title,
                    awaitingSampling ? Localize("DefaultColorMarker") : ""
                )
            );
        }

        // 记录已有条目的原值；同一个键只记第一次，写两次会把后来的值当成原值
        private static void SnapshotExisting(
            string dictKey,
            BiomeEntry entry,
            Dictionary<string, ExistingEntrySnapshot> existingSnapshots
        )
        {
            if (!existingSnapshots.ContainsKey(dictKey))
                existingSnapshots[dictKey] = new ExistingEntrySnapshot(
                    entry.Title,
                    entry.LocalizationScope,
                    entry.TitleColor,
                    entry.Icon
                );
        }

        private static BiomeEntry BuildNewEntry(
            ModBiome modBiome,
            Mod targetMod,
            bool enableStyling,
            TitleColorSource colorSource,
            Color fixedColor,
            string title
        )
        {
            var entry = new BiomeEntry
            {
                Key = modBiome.Name,
                Title = title,
                SubTitle = targetMod.DisplayNameClean,
                LocalizationScope = targetMod.Name,
                // 不在这里加载图标：扫描跑在后台线程，创建 GPU 纹理是主线程的事，见 ApplyWrites
                Icon = null,
            };

            if (enableStyling)
            {
                entry.TitleColor = BiomeStyleHelper.GetTitleColors(
                    modBiome,
                    colorSource,
                    fixedColor,
                    out bool needsSampling
                );
                // 待补色（策略为 IconSampling 或没有背景色），登记进来等主线程用图标平均色补
                // 配色整档关闭时不登记：那里只落白色
                if (needsSampling)
                    PendingColorKeys[entry] = 0;
            }
            else
            {
                // 未开配色也用白色：留 default（透明）会让标题整个看不见
                entry.TitleColor = Color.White;
            }

            return entry;
        }

        // 图标只能在这里加载：ModContent.Request 会创建 GPU 纹理，属主线程职责
        private static void ApplyWrites(
            Dictionary<string, BiomeEntry> biomeDict,
            List<PendingWrite> writes
        )
        {
            foreach (var write in writes)
            {
                if (write.Entry != null)
                {
                    write.Entry.Icon = BiomeStyleHelper.TryLoadIcon(write.Biome);
                    // 图标加载失败就别占补色队列：留在 PendingColorKeys 里永远不会被补上，
                    // 还会被 ScanSummary 计成「未配色」；真有图标了下次扫描会重新登记
                    if (write.Entry.Icon == null)
                        PendingColorKeys.TryRemove(write.Entry, out _);
                    biomeDict[write.DictKey] = write.Entry;
                }
                else if (biomeDict.TryGetValue(write.DictKey, out var entry))
                {
                    entry.Title = write.Title ?? entry.Title;
                    entry.LocalizationScope = write.LocalizationScope;
                }
            }
        }

        private static Func<Player, string> BuildDetectionFunction(
            List<(Mod Mod, ModBiome Biome)> allBiomes,
            Dictionary<string, string> keyMapping
        )
        {
            return player =>
            {
                foreach (var (sourceMod, biome) in allBiomes)
                {
                    try
                    {
                        if (biome.IsBiomeActive(player))
                            return keyMapping.TryGetValue(
                                $"{sourceMod.Name}.{biome.Name}",
                                out var key
                            )
                                ? key
                                : biome.Name;
                    }
                    catch (Exception ex)
                    {
                        // 限频记录：群系检测异常常在特定区域反复触发，全量记录会刷屏
                        // 种子值取 0 而非 now：GetOrAdd 命中已有项返回的是存的旧值，
                        // 首次调用若用 now 作种子，now - now = 0 永远不够冷却，首见异常会被吞掉
                        string modName = sourceMod.Name;
                        string biomeName = biome.Name;
                        long now = Environment.TickCount64;
                        long lastMs = LastDetectionWarnMs.GetOrAdd((modName, biomeName), 0L);
                        if (now - lastMs >= DetectionWarnCooldownMs)
                        {
                            LastDetectionWarnMs[(modName, biomeName)] = now;
                            DebugLog.Warn(
                                SafeFormat(Localize("DetectionEx"), modName, biomeName, ex.Message)
                            );
                        }
                    }
                }
                return "";
            };
        }

        // 扫描中途失败：字典写入尚未提交，丢弃本次的临时登记
        // 令牌也要放开，否则下次开关扫描会被 Run 的抢占直接挡掉
        private static void Rollback(int generation)
        {
            // 已被取消（令牌不再属于自己）：这些数据可能已经是后一次扫描的，不能清
            if (IsCancelled(generation))
                return;

            PendingColorKeys.Clear();
            ScannedBiomes = null;
            PreScanValues = null;
            ReleaseScan(generation);
        }

        /*
        提交失败的回退，只在主线程回调里调用
        与上面那个的区别：此时扫描已经成功提交过、令牌也已在 finally 里释放，
        所以不能走「已取消就跳过」的判断，必须无条件清掉这份跟踪状态
        */
        private static void RollbackCommitted()
        {
            PendingColorKeys.Clear();
            ScannedBiomes = null;
            PreScanValues = null;
        }

        /*
        提交失败后的就地回退，只在主线程的提交回调里调用
        新增条目直接摘掉；改写过标题的已有条目按快照还原
        检测函数若已插入也一并移除，避免它成为无人认领的孤儿
        */
        private static void UndoWrites(
            Dictionary<string, BiomeEntry> biomeDict,
            List<PendingWrite> writes,
            List<Func<Player, string>> checkFuncs,
            Func<Player, string>? funcToInsert
        )
        {
            foreach (var write in writes)
            {
                if (write.Entry != null)
                {
                    biomeDict.Remove(write.DictKey);
                    continue;
                }

                if (
                    PreScanValues != null
                    && PreScanValues.TryGetValue(write.DictKey, out var snapshot)
                    && biomeDict.TryGetValue(write.DictKey, out var entry)
                )
                {
                    entry.Title = snapshot.Title ?? entry.Title;
                    entry.LocalizationScope = snapshot.LocalizationScope;
                }
            }

            if (funcToInsert != null)
                checkFuncs.Remove(funcToInsert);
        }

        private static string Localize(string key) =>
            Language.GetTextValue($"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.{key}");

        // HJSON 本地化值可能含有未转义 { } 导致 FormatException，兜底返回原模板
        private static string SafeFormat(string format, params object[] args)
        {
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException ex)
            {
                DebugLog.Warn(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.FormatError",
                        format,
                        ex.Message
                    )
                );
                return format;
            }
        }

        /*
        清理注册的检测函数，从 BTitles 的 BiomeCheckFunctions 列表中移除
        防止热重载或关闭扫描后残留 lambda，导致每帧对已卸载模组的群系做无效检测
        */
        public static void Cleanup()
        {
            LastDetectionWarnMs.Clear();

            var funcToRemove = RegisteredCheckFunc;
            if (funcToRemove == null)
                return;

            var checkFuncs = ResolveRegisteredCheckFunctions();
            if (checkFuncs == null)
            {
                // BTitles 已卸载，列表随之消失，无需也无法移除
                RegisteredCheckFunc = null;
                return;
            }

            // 必须排主线程：BTitles 每帧遍历 BiomeCheckFunctions，直接改会与之竞态
            // 捕获列表引用而非在闭包里重新反射：Unload 会清空反射字段
            Main.QueueMainThreadAction(() =>
            {
                try
                {
                    // 成功移除才释放引用，失败保留待下次重试
                    // 还要确认字段仍指向本次的函数，期间可能有新扫描换掉了它
                    if (checkFuncs.Remove(funcToRemove) && RegisteredCheckFunc == funcToRemove)
                        RegisteredCheckFunc = null;
                }
                catch (Exception ex)
                {
                    DebugLog.Warn(SafeFormat(Localize("CleanupRemoveCheckFuncFailed"), ex.Message));
                }
            });
        }

        /*
        Unload 专用：把「撤条目 + 还原快照 + 撤检测函数」合并成一次主线程操作并阻塞等待
        非主线程时用 RunOnMainThread 跳过去（照抄 MusicLoader.CloseModStreams :210-211）；
        Unload 由 ModLoader 在线程池线程调用，而 BTitles 主线程每帧都在遍历这两个集合
        返回 true 表示检测函数移除成功（或已无函数可移除）；失败仅告警，不抛
        */
        internal static bool CleanupBlockingOnMainThread()
        {
            var funcToRemove = RegisteredCheckFunc;
            LastDetectionWarnMs.Clear();

            if (funcToRemove != null && Program.IsMainThread)
            {
                // 已在主线程的可能极小（仅调试时手动调用），按 MusicLoader 语义直接内联执行
                RemoveCheckFuncInline(funcToRemove);
                return RegisteredCheckFunc == null;
            }

            if (funcToRemove == null)
                return true;

            try
            {
                var done = Main.RunOnMainThread(() => RemoveCheckFuncInline(funcToRemove))
                    .GetAwaiter()
                    .GetResult();
                return done;
            }
            catch (Exception ex)
            {
                DebugLog.Warn(SafeFormat(Localize("CleanupRemoveCheckFuncFailed"), ex.Message));
                return false;
            }
        }

        // 与 Cleanup 的主线程回调保持同一套守卫的内联体：CleanupBlockingOnMainThread 用
        private static bool RemoveCheckFuncInline(Func<Player, string> funcToRemove)
        {
            var checkFuncs = ResolveRegisteredCheckFunctions();
            if (checkFuncs == null)
            {
                // BTitles 已卸载，列表随之消失，无需也无法移除
                RegisteredCheckFunc = null;
                return true;
            }

            try
            {
                // 与 Cleanup 的主线程回调同款守卫：只释放仍属于本次的函数
                checkFuncs.Remove(funcToRemove);
                if (RegisteredCheckFunc == funcToRemove)
                    RegisteredCheckFunc = null;
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Warn(SafeFormat(Localize("CleanupRemoveCheckFuncFailed"), ex.Message));
                return false;
            }
        }

        // 清理流程专用：BTitles 实例不在时返回 null，且不打错误日志
        private static List<Func<Player, string>>? ResolveRegisteredCheckFunctions()
        {
            var instance = BTitlesLocalizationPatch.InstanceField?.GetValue(null);
            return instance == null
                ? null
                : BTitlesLocalizationPatch.CheckFuncsField?.GetValue(instance)
                    as List<Func<Player, string>>;
        }

        // 清理本次扫描留下的记录；检测函数引用归 Cleanup 管，这里不动它
        internal static void ClearScannedBiomes()
        {
            ScannedBiomes = null;
            PreScanValues = null;
            PendingColorKeys.Clear();
            // 作废正在跑的那一次：它的代次不再匹配，提交前会自行放弃
            Interlocked.Exchange(ref _scanOwner, 0);
        }

        private sealed record ScanResult(
            int Added,
            int Updated,
            int Skipped,
            int PendingColor,
            List<(Mod Mod, ModBiome Biome)> AllBiomes,
            Dictionary<string, string> KeyMapping,
            List<PendingWrite> PendingWrites,
            Dictionary<string, ExistingEntrySnapshot> ExistingSnapshots
        );

        // 一次待提交的字典写入。新增时 Entry/Biome 有值（图标由 ApplyWrites 在主线程补载），
        // 更新时只覆盖标题与本地化作用域
        private sealed record PendingWrite(
            string DictKey,
            BiomeEntry? Entry,
            ModBiome? Biome,
            string? Title,
            string? LocalizationScope
        )
        {
            public static PendingWrite Add(string dictKey, BiomeEntry entry, ModBiome biome) =>
                new(dictKey, entry, biome, null, null);

            public static PendingWrite Update(
                string dictKey,
                string title,
                string localizationScope
            ) => new(dictKey, null, null, title, localizationScope);
        }

        // 已有条目里会被扫描覆盖的字段，关闭扫描时据此还原 BTitles 原有条目
        internal readonly record struct ExistingEntrySnapshot(
            string? Title,
            string? LocalizationScope,
            Color TitleColor,
            Texture2D? Icon
        );
    }
}
