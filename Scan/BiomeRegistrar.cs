#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
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
        private static readonly ConcurrentDictionary<(string Mod, string Biome), long>
            LastDetectionWarnTicks = new();
        private static readonly TimeSpan DetectionWarnCooldown = TimeSpan.FromSeconds(5);

        // 扫描入场令牌：0=空闲（可扫描） 2=扫描进行中
        // Run 的调用方既有 PostSetupContent（后台线程）也有配置回调（主线程），
        // 单靠 ScannedBiomes != null 是读-判-写，两个线程可能同时通过
        private static int _scanState;

        public static void Run(Mod mod, bool enableStyling)
        {
            enableStyling = BiomeNameHook.IsStylingEnabled();
            bool onlyFillMissing = IsOnlyFillMissing();
            (var colorSource, var fixedColor) = BiomeStyleHelper.GetStylingSettings();

            // 抢占令牌再判状态：这一步是原子的，同一时刻只有一个调用能进来
            if (Interlocked.CompareExchange(ref _scanState, 2, 0) != 0)
                return;

            // 已有生效结果就不再扫：重复扫描会把上次自己新增的条目当成「已有条目」快照，
            // 之后关闭扫描只能还原成扫描后的样子，条目和原值都回不来
            if (ScannedBiomes != null)
            {
                Interlocked.Exchange(ref _scanState, 1);
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
                    biomes
                );

                RegisteredCheckFunc = BuildDetectionFunction(
                    scanResult.AllBiomes,
                    scanResult.KeyMapping
                );
                PreScanValues = scanResult.ExistingSnapshots;
                ScannedBiomes = biomes;

                // 状态归到「已有生效结果」，此后重复开关扫描走上面那条早退
                Interlocked.Exchange(ref _scanState, 1);

                // BTitles 在主线程每帧遍历这两个集合，写入必须合并成一次主线程操作
                // 捕获已解析的引用而不重新反射：Unload 会清空反射字段
                var writes = scanResult.PendingWrites;
                var funcToInsert = RegisteredCheckFunc;
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
                        mod.Logger.Error(
                            SafeFormat(Localize("ScanApplyFailed"), ex.Message)
                        );

                        // ApplyWrites 是逐条写的，抛出时字典已被改了一半。
                        // 就地撤销本次全部写入：此刻还握着 writes 与 PreScanValues，
                        // 而且正好在主线程，是唯一能干净回退的时机
                        UndoWrites(biomeDict, writes, checkFuncs, funcToInsert);
                        Rollback();
                    }
                });
            }
            catch
            {
                // 解析或遍历阶段抛出时令牌还握在手里，不放回去就是永久锁死：
                // 用户之后怎么开关扫描都会被上面那次抢占挡掉，再也不会重试
                Rollback();
                throw;
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

        private static List<Func<Player, string>> ResolveCheckFunctions(
            BiomeTitlesMod instance
        )
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
                            mod.Logger.Info(
                                SafeFormat(
                                    Localize("SkippedPlaceholder"),
                                    biome.Name,
                                    biome.DisplayName.Value
                                )
                            );
                            skipped++;
                            continue;
                        }

                        allBiomes.Add((targetMod, biome));
                        RegisterOrUpdateBiome(
                            biomeDict,
                            targetMod,
                            biome,
                            enableStyling,
                            onlyFillMissing,
                            colorSource,
                            fixedColor,
                            keyMapping,
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
            */
            if (onlyFillMissing)
            {
                if (biomeDict.ContainsKey(namespacedKey) || biomeDict.ContainsKey(modBiome.Name))
                {
                    skipped++;
                    return;
                }
            }

            if (biomeDict.TryGetValue(namespacedKey, out var entry))
            {
                SnapshotExisting(namespacedKey, entry, existingSnapshots);
                pendingWrites.Add(PendingWrite.Update(namespacedKey, title, targetMod.Name));
                keyMapping[namespacedKey] = namespacedKey;
                scannedBiomes.Add((namespacedKey, modBiome, false));
                updated++;
                return;
            }

            if (biomeDict.TryGetValue(modBiome.Name, out entry))
            {
                SnapshotExisting(modBiome.Name, entry, existingSnapshots);
                pendingWrites.Add(PendingWrite.Update(modBiome.Name, title, targetMod.Name));
                keyMapping[namespacedKey] = modBiome.Name;
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
                // 待补色（策略为 IconSampling 或没有背景色），登记进来等主线程用图标众数补
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
                        long now = DateTime.UtcNow.Ticks;
                        long lastTicks = LastDetectionWarnTicks.GetOrAdd(
                            (modName, biomeName),
                            0L
                        );
                        if (now - lastTicks >= DetectionWarnCooldown.Ticks)
                        {
                            LastDetectionWarnTicks[(modName, biomeName)] = now;
                            DebugLog.Warn(
                                SafeFormat(
                                    Localize("DetectionEx"),
                                    modName,
                                    biomeName,
                                    ex.Message
                                )
                            );
                        }
                    }
                }
                return "";
            };
        }

        // 扫描中途失败：字典写入尚未提交，丢弃本次的临时登记
        // 令牌也要放开，否则下次开关扫描会被 Run 的抢占直接挡掉
        private static void Rollback()
        {
            PendingColorKeys.Clear();
            ScannedBiomes = null;
            PreScanValues = null;
            Interlocked.Exchange(ref _scanState, 0);
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
            LastDetectionWarnTicks.Clear();

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
                    DebugLog.Warn($"Cleanup: failed to remove check func - {ex.Message}");
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
            LastDetectionWarnTicks.Clear();

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
                DebugLog.Warn($"Cleanup: failed to remove check func - {ex.Message}");
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
                DebugLog.Warn($"Cleanup: failed to remove check func - {ex.Message}");
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
            Interlocked.Exchange(ref _scanState, 0);
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

        // BiomeEntry 没重写 Equals，这里仍显式按引用比较，
        // 避免将来上游重写后，待补色集合把两个「值相等」的条目当成同一个
        private sealed class BiomeEntryReferenceComparer : IEqualityComparer<BiomeEntry>
        {
            public static readonly BiomeEntryReferenceComparer Instance = new();

            public bool Equals(BiomeEntry? x, BiomeEntry? y) => ReferenceEquals(x, y);

            public int GetHashCode(BiomeEntry obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
