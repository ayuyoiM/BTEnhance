#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using BTitles;
using BTitlesLocalizationPatch.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch
{
    /*
    BTitles 本地化增强补丁
    1. 自动注册未收录群系 — 扫描各模组 ModBiome 并补入 BTitles 字典
    2. GetActualTitleName 增强 — 5 步回退链（源模组本地化→BTitles→补丁补充→自定义→原始标题）
    3. 自动配色 — 扫描时取 BackgroundColor，运行时 Hook 图标采样补色
    */
    public class BTitlesLocalizationPatch : Mod
    {
        private volatile bool _loaded;

        // 配置首次 OnChanged 早于 Mod.Load，那时反射字段还是 null，扫描与卸载都需据此早退
        // 用静态标志而非 ModContent.GetInstance：后者在加载早期可能返回 null
        internal static volatile bool LoadCompleted;

        // volatile: 后台线程（PostSetupContent）读 Load（主线程）写入的最新值
        private static volatile FieldInfo? _instanceField;
        private static volatile FieldInfo? _biomeDictField;
        private static volatile FieldInfo? _checkFuncsField;
        internal static FieldInfo? InstanceField => _instanceField;
        internal static FieldInfo? BiomeDictField => _biomeDictField;
        internal static FieldInfo? CheckFuncsField => _checkFuncsField;

        // volatile: BiomeNameHook（UI 线程）读，Load（主线程）写
        internal static volatile bool HookInstalled;

        public override void Load()
        {
            if (Main.dedServ)
                return;

            if (_loaded)
            {
                Logger.Warn("Mod Load called again, skipped");
                return;
            }
            _loaded = true;

            InstallDebugLogger();
            CacheReflectionFields();
            GetActualTitleNameHookInstaller.Install(this);

            // 放在最后：此刻反射字段与日志器都已就绪，配置变更回调可以安全执行
            LoadCompleted = true;
        }

        // DebugLog InfoWriter/WarnWriter/ErrorWriter 对应 tModLoader Logger 级别
        private void InstallDebugLogger()
        {
            DebugLog.InfoWriter = s => Logger.Info(s!);
            DebugLog.WarnWriter = s => Logger.Warn(s!);
            DebugLog.ErrorWriter = s => Logger.Error(s!);
        }

        /*
        只缓存字段引用，不在这里校验是否存在
        取不到时由扫描入口抛 BTitlesIncompatibleException——
        同一件事只在一处报错，不做「先预检再实测」的两道确认
        */
        private void CacheReflectionFields()
        {
            _instanceField = typeof(BiomeTitlesMod).GetField(
                "Instance",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
            );

            _biomeDictField = typeof(BiomeTitlesMod).GetField(
                "BiomeDictionary",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );

            _checkFuncsField = typeof(BiomeTitlesMod).GetField(
                "BiomeCheckFunctions",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
        }

        public override void PostSetupContent()
        {
            if (Main.dedServ)
                return;

            // 防御：配置类可能为 null（极端情况下 ModContent.GetInstance 返回 null）
            var config = ModContent.GetInstance<BTitlesConfig>();
            if (config == null)
            {
                Logger.Error(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.ConfigNull"
                    )
                );
                return;
            }

            if (config.EnableScan)
            {
                Logger.Info(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.ScanEnabled"
                    )
                );
                Scan.BiomeRegistrar.Run(this, config.EnableAutoStyling);
            }
        }

        // 由 ModConfig.OnChanged 调用；主菜单时 BTitles Instance 为 null，操作会静默跳过
        internal static void ApplyConfigChangeStyle(bool enabled)
        {
            // 立即同步 Hook 的样式开关缓存，避免 3 秒冷却期内采样逻辑读到旧值
            BiomeNameHook.UpdateStylingCache(enabled);

            // 配色不依赖标题 Hook，早退会让已上色的条目撤不掉；Load 之前反射字段还是 null，见 LoadCompleted
            if (!LoadCompleted)
                return;

            if (
                Scan.BiomeRegistrar.ScannedBiomes == null
                || Scan.BiomeRegistrar.ScannedBiomes.Count == 0
            )
                return;

            var instance = InstanceField?.GetValue(null);
            if (instance == null)
                return;

            var biomeDict = BiomeDictField?.GetValue(instance) as Dictionary<string, BiomeEntry>;
            if (biomeDict == null)
                return;

            ApplyStyleToScannedBiomes(enabled, biomeDict);
        }

        private static void ApplyStyleToScannedBiomes(
            bool enabled,
            Dictionary<string, BiomeEntry> biomeDict
        )
        {
            int styledCount = 0;
            foreach (var (dictKey, modBiome, isNew) in Scan.BiomeRegistrar.ScannedBiomes!)
            {
                // 只处理本次扫描新增的条目：BTitles 原有的配色不动
                if (!isNew)
                    continue;

                if (!biomeDict.TryGetValue(dictKey, out var entry))
                    continue;

                if (modBiome == null)
                    continue;

                if (enabled)
                {
                    entry.TitleColor = Scan.BiomeStyleHelper.GetTitleColors(
                        modBiome,
                        out bool needsSampling
                    );
                    // 没有 BackgroundColor 的条目记回待补色队列，等 Hook 用图标众数补上
                    if (needsSampling)
                        Scan.BiomeRegistrar.PendingColorKeys[entry] = 0;
                    else
                        Scan.BiomeRegistrar.PendingColorKeys.TryRemove(entry, out _);
                }
                else
                {
                    // 关配色时还原成白色而不是 default（透明），否则标题会整个看不见
                    entry.TitleColor = Color.White;
                    Scan.BiomeRegistrar.PendingColorKeys.TryRemove(entry, out _);
                }
                styledCount++;
            }

            if (styledCount > 0)
            {
                string restyleKey = enabled ? "RestyledOn" : "RestyledOff";
                Diagnostics.DebugLog.Info(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.{restyleKey}",
                        styledCount
                    )
                );
            }
        }

        internal static void ApplyConfigChangeScan(bool enabled)
        {
            // 扫描与检测函数注册都不依赖标题 Hook，早退会把注册过的群系留在字典里撤不掉
            if (!LoadCompleted)
                return;

            BiomeNameHook.TranslationCache.Clear();

            var mod = ModContent.GetInstance<BTitlesLocalizationPatch>();
            if (mod == null)
                return;

            if (enabled)
            {
                var config = ModContent.GetInstance<BTitlesConfig>();
                if (config == null)
                {
                    mod.Logger.Warn(
                        Language.GetTextValue(
                            $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.ScanConfigNull"
                        )
                    );
                }
                else
                {
                    mod.Logger.Info(
                        Language.GetTextValue(
                            $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.ScanToggleOn"
                        )
                    );
                    Scan.BiomeRegistrar.Run(mod, config.EnableAutoStyling);
                }
            }
            else
            {
                mod.Logger.Info(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.ScanToggleOff"
                    )
                );
                UnregisterScannedBiomes(mod);
            }
        }

        // 卸载本次扫描的成果：移除新增条目、还原被覆盖的已有条目，并清掉检测函数与扫描状态
        // mod 为 null 表示卸载途中实例已不可靠，不走 Mod.Logger
        internal static void UnregisterScannedBiomes(Mod? mod)
        {
            var instance = InstanceField?.GetValue(null);
            var biomeDict =
                instance == null
                    ? null
                    : BiomeDictField?.GetValue(instance) as Dictionary<string, BiomeEntry>;

            int removed = 0;
            int restored = 0;

            var scanned = Scan.BiomeRegistrar.ScannedBiomes;
            if (biomeDict != null && scanned != null)
            {
                // 移除本次扫描新增的字典条目
                foreach (var (dictKey, _, isNew) in scanned)
                {
                    if (isNew && biomeDict.Remove(dictKey))
                        removed++;
                }
            }

            // 还原被扫描改写的已有条目字段，否则关掉扫描仍会显示补丁标题
            var snapshots = Scan.BiomeRegistrar.PreScanValues;
            if (biomeDict != null && snapshots != null)
            {
                foreach (var (dictKey, snapshot) in snapshots)
                {
                    if (!biomeDict.TryGetValue(dictKey, out var entry))
                        continue;
                    entry.Title = snapshot.Title ?? entry.Title;
                    entry.LocalizationScope = snapshot.LocalizationScope;
                    entry.TitleColor = snapshot.TitleColor;
                    entry.Icon = snapshot.Icon;
                    restored++;
                }
            }

            if (removed > 0 && mod != null)
                mod.Logger.Info(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.UnregisteredBiomes",
                        removed
                    )
                );

            if (restored > 0)
                Diagnostics.DebugLog.Info(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.RestoredBiomes",
                        restored
                    )
                );

            // 检测函数与扫描状态无条件清干净，早退路径不能把它们留在原地
            Scan.BiomeRegistrar.Cleanup();
            Scan.BiomeRegistrar.ClearScannedBiomes();
        }

        public override void Unload()
        {
            BiomeNameHook.TranslationCache.Clear();
            BiomeNameHook.ResetStylingCache();
            BiomeNameHook.ResetCultureCache();

            GetActualTitleNameHookInstaller.Uninstall(this);

            // 必须撤字典：BTitles 不随本模组卸载，只清记录会让它的字典留下带不走的孤儿条目
            // 传 null：卸载途中不再走 Mod.Logger
            UnregisterScannedBiomes(null);

            _instanceField = null;
            _biomeDictField = null;
            _checkFuncsField = null;
            _loaded = false;
            // 与 _loaded 同步归位：卸载后若再收到配置回调，不能在字段已清空的状态下执行
            LoadCompleted = false;

            // 清理调试日志写入器，防止卸载后残留引用
            Diagnostics.DebugLog.InfoWriter = s => { };
            Diagnostics.DebugLog.WarnWriter = s => { };
            Diagnostics.DebugLog.ErrorWriter = s => { };

            // 重置调试日志缓存，防止热重载后 3 秒内读到旧缓存值
            Diagnostics.DebugLog.ResetCache();
        }
    }
}
