#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BTitles;
using BTitlesLocalizationPatch.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch.Scan
{
    internal static class BiomeRegistrar
    {
        // volatile: 后台线程（PostSetupContent）写入，主线程（Unload/配置变更）读取
        internal static volatile Func<Player, string>? RegisteredCheckFunc;
        internal static volatile List<(string DictKey, ModBiome Biome)>? ScannedBiomes;

        // 扫描前 BiomeDictionary 的快照，用于区分新增和已有条目（回滚、热卸载）
        internal static HashSet<string>? PreScanKeys;

        // 检测异常告警冷却：同一群系 5 秒内最多记录一次，防止常抛异常的群系刷爆日志
        private static readonly ConcurrentDictionary<(string Mod, string Biome), long>
            LastDetectionWarnTicks = new();
        private static readonly TimeSpan DetectionWarnCooldown = TimeSpan.FromSeconds(5);

        public static void Run(Mod mod, bool enableStyling)
        {
            Cleanup();
            ScannedBiomes = null;
            var biomes = new List<(string DictKey, ModBiome Biome)>();

            var instance = ResolveBTitlesInstance(mod);
            if (instance == null)
                return;
            var biomeDict = ResolveBiomeDictionary(instance, mod);
            if (biomeDict == null)
                return;
            var checkFuncs = ResolveCheckFunctions(instance, mod);
            if (checkFuncs == null)
                return;

            // 复制到新 HashSet 避免扫描期间外部修改导致快照失真
            PreScanKeys = new HashSet<string>(biomeDict.Keys, biomeDict.Comparer);

            var scanResult = ScanAllModBiomes(mod, biomeDict, enableStyling, biomes);
            if (scanResult == null)
                return;

            RegisteredCheckFunc = BuildDetectionFunction(
                scanResult.AllBiomes,
                scanResult.KeyMapping
            );
            // 列表修改必须排到主线程执行，避免与 BTitles 的 CheckNewBiome 遍历竞态
            var funcToInsert = RegisteredCheckFunc;
            Main.QueueMainThreadAction(() =>
            {
                try
                {
                    var instance = BTitlesLocalizationPatch.InstanceField?.GetValue(null);
                    if (instance == null)
                        return;

                    var checkFuncs =
                        BTitlesLocalizationPatch.CheckFuncsField?.GetValue(instance)
                        as List<Func<Player, string>>;
                    if (checkFuncs == null)
                        return;

                    checkFuncs.Insert(0, funcToInsert);
                }
                catch (Exception ex)
                {
                    DebugLog.Warn($"Run: failed to insert check func - {ex.Message}");
                }
            });
            ScannedBiomes = biomes;

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
            List<(string DictKey, ModBiome Biome)> scannedBiomes
        )
        {
            int added = 0,
                updated = 0,
                skipped = 0,
                pendingColor = 0;
            var allBiomes = new List<(Mod Mod, ModBiome Biome)>();
            var keyMapping = new Dictionary<string, string>();

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
                Rollback(biomeDict);
                return null;
            }

            return new ScanResult(added, updated, skipped, pendingColor, allBiomes, keyMapping);
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
        */
        private static void RegisterOrUpdateBiome(
            Dictionary<string, BiomeEntry> biomeDict,
            Mod targetMod,
            ModBiome modBiome,
            bool enableStyling,
            Dictionary<string, string> keyMapping,
            List<(string DictKey, ModBiome Biome)> scannedBiomes,
            Mod mod,
            ref int added,
            ref int updated,
            ref int pendingColor
        )
        {
            string namespacedKey = $"{targetMod.Name}.{modBiome.Name}";

            if (biomeDict.TryGetValue(namespacedKey, out var entry))
            {
                entry.Title = modBiome.DisplayName.Value ?? modBiome.Name;
                entry.LocalizationScope = targetMod.Name;
                keyMapping[namespacedKey] = namespacedKey;
                scannedBiomes.Add((namespacedKey, modBiome));
                updated++;
            }
            else if (biomeDict.TryGetValue(modBiome.Name, out entry))
            {
                entry.Title = modBiome.DisplayName.Value ?? modBiome.Name;
                entry.LocalizationScope = targetMod.Name;
                keyMapping[namespacedKey] = modBiome.Name;
                scannedBiomes.Add((modBiome.Name, modBiome));
                updated++;
            }
            else
            {
                var newEntry = BuildNewEntry(modBiome, targetMod, enableStyling);
                keyMapping[namespacedKey] = namespacedKey;
                biomeDict[namespacedKey] = newEntry;
                scannedBiomes.Add((namespacedKey, modBiome));
                added++;
                if (newEntry.TitleColor == default)
                    pendingColor++;
                string pendingColorSuffix = newEntry.TitleColor == default
                    ? Localize("DefaultColorMarker")
                    : "";
                mod.Logger.Info(
                    SafeFormat(Localize("NewBiome"), modBiome.Name, modBiome.DisplayName.Value, pendingColorSuffix)
                );
            }
        }

        private static BiomeEntry BuildNewEntry(
            ModBiome modBiome,
            Mod targetMod,
            bool enableStyling
        )
        {
            var entry = new BiomeEntry
            {
                Key = modBiome.Name,
                Title = modBiome.DisplayName.Value ?? modBiome.Name,
                SubTitle = targetMod.DisplayNameClean,
                LocalizationScope = targetMod.Name,
                Icon = BiomeStyleHelper.TryLoadIcon(modBiome),
            };

            if (enableStyling)
            {
                BiomeStyleHelper.GetTitleColors(modBiome, out Color titleColor);
                entry.TitleColor = titleColor;
            }
            else
            {
                entry.TitleColor = default;
            }

            return entry;
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

        private static void Rollback(Dictionary<string, BiomeEntry> biomeDict)
        {
            var newlyAddedKeys =
                PreScanKeys != null
                    ? biomeDict.Keys.Where(key => !PreScanKeys.Contains(key)).ToList()
                    : new List<string>();
            foreach (var key in newlyAddedKeys)
                biomeDict.Remove(key);

            RegisteredCheckFunc = null;
            ScannedBiomes = null;
            PreScanKeys = null;
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
        清理注册的检测函数，在 Unload 时从 BTitles 的 BiomeCheckFunctions 列表中移除
        防止热重载时重复累积 lambda，导致无效的额外检测调用
        */
        public static void Cleanup()
        {
            // 告警限频表无条件清理，避免上报群系在重启扫描后残留旧状态
            LastDetectionWarnTicks.Clear();

            if (RegisteredCheckFunc == null)
                return;

            // 列表修改必须排到主线程执行，避免与 BTitles 的 CheckNewBiome 遍历竞态
            var funcToRemove = RegisteredCheckFunc;
            Main.QueueMainThreadAction(() =>
            {
                try
                {
                    var instance = BTitlesLocalizationPatch.InstanceField?.GetValue(null);
                    if (instance == null)
                        return;

                    var checkFuncs =
                        BTitlesLocalizationPatch.CheckFuncsField?.GetValue(instance)
                        as List<Func<Player, string>>;
                    if (checkFuncs == null)
                        return;

                    checkFuncs.Remove(funcToRemove);
                }
                catch (Exception ex)
                {
                    DebugLog.Warn($"Cleanup: failed to remove check func - {ex.Message}");
                }
            });
            // 只在成功移除后才释放引用；移除失败时保留引用，下次清理可重试
            RegisteredCheckFunc = null;
        }

        internal static void ClearScannedBiomes()
        {
            ScannedBiomes = null;
            PreScanKeys = null;
            RegisteredCheckFunc = null;
        }

        private sealed record ScanResult(
            int Added,
            int Updated,
            int Skipped,
            int PendingColor,
            List<(Mod Mod, ModBiome Biome)> AllBiomes,
            Dictionary<string, string> KeyMapping
        );
    }
}
