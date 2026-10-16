#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BTitles;
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
        // volatile: 扫描（PostSetupContent 后台线程）写入，配置变更 / Unload（主线程）读取与清除
        internal static volatile Func<Player, string>? RegisteredCheckFunc;

        // 本次扫描处理的条目，以及它是否为本次新增（IsNew）
        // volatile: 扫描线程写入，主线程读取；列表在扫描结束后不再修改，可安全跨线程发布
        internal static volatile List<(string DictKey, ModBiome Biome, bool IsNew)>? ScannedBiomes;

        // 扫描覆盖过的已有条目的原值快照，关闭扫描时用它把 BTitles 原有条目还原回原样
        internal static volatile Dictionary<string, ExistingEntrySnapshot>? PreScanValues;

        // 等待运行时用图标众数补色的条目（按引用比较）
        // 扫描线程写入，Hook 与配置变更（主线程）读写，故用并发字典
        internal static readonly ConcurrentDictionary<BiomeEntry, byte> PendingColorKeys = new(
            BiomeEntryReferenceComparer.Instance
        );

        // 检测异常告警冷却：同一群系 5 秒内最多记录一次，防止常抛异常的群系刷爆日志
        private static readonly ConcurrentDictionary<(string Mod, string Biome), long>
            LastDetectionWarnTicks = new();
        private static readonly TimeSpan DetectionWarnCooldown = TimeSpan.FromSeconds(5);

        public static void Run(Mod mod, bool enableStyling)
        {
            // 同一份扫描结果不重复产出。正常加载时本方法只由 PostSetupContent 调用一次，
            // 但配置回调（OnChanged）与 PostSetupContent 的先后由 tModLoader 的加载顺序决定，
            // 热重载时也可能两者都走到这里。重复扫描会把上一次自己新增的条目当成「已有条目」
            // 快照下来，之后关闭扫描就成了「还原成扫描后的样子」——条目不移除、原值也回不来
            // 关闭扫描与卸载都会把这份结果撤净并置空，所以正常重扫（关→开）不受影响
            if (ScannedBiomes != null)
                return;

            // 摘掉上一次注册的检测函数：RegisteredCheckFunc 马上要被本次覆盖，
            // 不先移除，旧 lambda 就会永远留在 BTitles 的检测列表里每帧空转（引用已丢失，再也删不掉）
            // 不在此处撤字典：本方法由 PostSetupContent（后台线程）调用，字典只能改在主线程
            Cleanup();

            // 只清待补色登记，避免旧条目继续参与补色
            PendingColorKeys.Clear();
            var biomes = new List<(string DictKey, ModBiome Biome, bool IsNew)>();

            var instance = ResolveBTitlesInstance(mod);
            if (instance == null)
                return;
            var biomeDict = ResolveBiomeDictionary(instance, mod);
            if (biomeDict == null)
                return;
            var checkFuncs = ResolveCheckFunctions(instance, mod);
            if (checkFuncs == null)
                return;

            // 扫描阶段只做只读遍历与判定：新增/已有一律靠 biomeDict.TryGetValue 当场判断，
            // 不预取整份键快照——那会在后台线程遍历 BTitles 的字典，与主线程写入同抢一个集合
            var scanResult = ScanAllModBiomes(mod, biomeDict, enableStyling, biomes);
            if (scanResult == null)
                return;

            RegisteredCheckFunc = BuildDetectionFunction(
                scanResult.AllBiomes,
                scanResult.KeyMapping
            );
            PreScanValues = scanResult.ExistingSnapshots;
            ScannedBiomes = biomes;

            // 字典与检测函数列表都由 BTitles 在主线程每帧遍历，
            // 因此「写入字典 + 加载图标 + 插入检测函数」合并成一次主线程操作
            // 捕获已解析的对象引用而不重新反射：Unload 会清空反射字段，重新反射会拿不到对象
            var writes = scanResult.PendingWrites;
            var funcToInsert = RegisteredCheckFunc;
            Main.QueueMainThreadAction(() =>
            {
                // 这次扫描可能已被取消（Cleanup 在主线程清空了引用），此时不能提交，
                // 否则会往 BTitles 的检测列表里插回一个已被判为「无主」的函数，永久残留
                if (!ReferenceEquals(RegisteredCheckFunc, funcToInsert) || funcToInsert == null)
                    return;

                // 还要确认扫描记录仍指向本次扫描：从排队到执行之间可能发生「关扫描」，
                // 那会把 ScannedBiomes 置空并撤掉字典条目；此时若仍提交，
                // 写进去的条目再没有任何记录能追踪，成了永久孤儿
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
                    DebugLog.Warn($"Run: failed to apply scan result - {ex.Message}");
                }
            });
        }

        private static BiomeTitlesMod? ResolveBTitlesInstance(Mod mod)
        {
            var instance = BTitlesLocalizationPatch.InstanceField?.GetValue(null) as BiomeTitlesMod;
            if (instance == null)
                mod.Logger.Error(Localize("InstanceNull"));
            return instance;
        }

        private static Dictionary<string, BiomeEntry>? ResolveBiomeDictionary(
            BiomeTitlesMod instance,
            Mod mod
        )
        {
            var dict =
                BTitlesLocalizationPatch.BiomeDictField?.GetValue(instance)
                as Dictionary<string, BiomeEntry>;
            if (dict == null)
                mod.Logger.Error(Localize("DictNull"));
            return dict;
        }

        private static List<Func<Player, string>>? ResolveCheckFunctions(
            BiomeTitlesMod instance,
            Mod mod
        )
        {
            var list =
                BTitlesLocalizationPatch.CheckFuncsField?.GetValue(instance)
                as List<Func<Player, string>>;
            if (list == null)
                mod.Logger.Error(Localize("CheckerNull"));
            return list;
        }

        private static ScanResult? ScanAllModBiomes(
            Mod mod,
            Dictionary<string, BiomeEntry> biomeDict,
            bool enableStyling,
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
                            keyMapping,
                            scannedBiomes,
                            pendingWrites,
                            existingSnapshots,
                            mod,
                            ref added,
                            ref updated,
                            ref pendingColor
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                mod.Logger.Error($"Biome scan failed, rolling back: {ex.Message}");
                Rollback();
                return null;
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
        已有条目不动颜色/图标——BTitles 或之前扫描已有配色
        这里只登记写入意图，真正改字典在 ApplyWrites（主线程）
        */
        private static void RegisterOrUpdateBiome(
            Dictionary<string, BiomeEntry> biomeDict,
            Mod targetMod,
            ModBiome modBiome,
            bool enableStyling,
            Dictionary<string, string> keyMapping,
            List<(string DictKey, ModBiome Biome, bool IsNew)> scannedBiomes,
            List<PendingWrite> pendingWrites,
            Dictionary<string, ExistingEntrySnapshot> existingSnapshots,
            Mod mod,
            ref int added,
            ref int updated,
            ref int pendingColor
        )
        {
            string namespacedKey = $"{targetMod.Name}.{modBiome.Name}";
            string title = modBiome.DisplayName.Value ?? modBiome.Name;

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

            var newEntry = BuildNewEntry(modBiome, targetMod, enableStyling, title);
            pendingWrites.Add(PendingWrite.Add(namespacedKey, newEntry, modBiome));
            keyMapping[namespacedKey] = namespacedKey;
            scannedBiomes.Add((namespacedKey, modBiome, true));
            added++;

            // 等图标采样补色的条目在日志里标出来，方便排查「为什么这个群系是白的」
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

        // 记录已有条目的原值；同一个键只记第一次，别让后续写入覆盖掉真正的原值
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
                    out bool needsSampling
                );
                // 群系没给 BackgroundColor，登记进来等主线程用图标众数补色
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

        // 主线程提交扫描结果：写入新增条目、覆盖已有条目字段、补载图标
        // 图标必须在这里加载——ModContent.Request 会创建 GPU 纹理，属主线程职责
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
                        string modName = sourceMod.Name;
                        string biomeName = biome.Name;
                        long now = DateTime.UtcNow.Ticks;
                        long lastTicks = LastDetectionWarnTicks.GetOrAdd(
                            (modName, biomeName),
                            now
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

        // 扫描中途失败：本次尚未提交任何字典写入，只丢弃本次扫描产出的临时登记
        // 不必回滚 ScannedBiomes / PreScanValues：Run 开头的守卫保证进入时它们都是 null，
        // 而这两个字段要到扫描成功返回后才赋值，此刻仍为 null，字典里没有本文写入的条目
        // RegisteredCheckFunc 也不动：Run 开头已把上一次的函数排给 Cleanup 移除，本次还从未赋值
        // 配置仍为开时，关掉再打开扫描即可重试
        private static void Rollback()
        {
            PendingColorKeys.Clear();
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
            // 告警限频表无条件清理，避免群系在重启扫描后残留旧状态
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

            // 列表修改必须排到主线程执行，避免与 BTitles 每帧遍历 BiomeCheckFunctions 竞态
            // 捕获列表引用而不是在闭包里重新反射：Unload 会清空反射字段，重新反射会拿不到列表
            Main.QueueMainThreadAction(() =>
            {
                try
                {
                    // 成功移除才释放引用；失败时保留，下次清理可以重试
                    // 还要确认字段仍指向本次移除的函数：期间可能有新的扫描把字段换成新函数
                    if (checkFuncs.Remove(funcToRemove) && RegisteredCheckFunc == funcToRemove)
                        RegisteredCheckFunc = null;
                }
                catch (Exception ex)
                {
                    DebugLog.Warn($"Cleanup: failed to remove check func - {ex.Message}");
                }
            });
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

        // 一次待提交的字典写入
        // 新增：Entry 与 Biome 都有值，图标由 ApplyWrites 在主线程补载
        // 更新：只覆盖标题与本地化作用域，颜色与图标保持原样
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
