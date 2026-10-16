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

        // 配置在 Mod.Load 之前就会触发一次 OnChanged（OnLoaded 紧接其后），
        // 那时反射字段还都是 null，扫描与卸载都没法正常执行，需要按此状态早退
        // 用静态标志而不是 ModContent.GetInstance：后者在加载早期可能返回 null
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

        private void CacheReflectionFields()
        {
            _instanceField = typeof(BiomeTitlesMod).GetField(
                "Instance",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
            );
            if (_instanceField == null)
                Logger.Warn("Failed to reflect Instance field");

            _biomeDictField = typeof(BiomeTitlesMod).GetField(
                "BiomeDictionary",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
            if (_biomeDictField == null)
                Logger.Warn("Failed to reflect BiomeDictionary field");

            _checkFuncsField = typeof(BiomeTitlesMod).GetField(
                "BiomeCheckFunctions",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
            if (_checkFuncsField == null)
                Logger.Warn("Failed to reflect BiomeCheckFunctions field");
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

        // tModLoader 通知配置变更后直接执行，由 ModConfig.OnChanged 调用
        // 主菜单时 BTitles Instance 为 null，操作会静默跳过
        internal static void ApplyConfigChangeStyle(bool enabled)
        {
            // 立即同步 Hook 的样式开关缓存，避免 3 秒冷却期内采样逻辑读到旧值
            BiomeNameHook.UpdateStylingCache(enabled);

            // 这里不检查 HookInstalled：套用与还原标题配色都不依赖标题 Hook
            // 若因为签名不符没装上 Hook 就早退，关闭配色时已上色的条目就撤不掉了
            // 但必须等 Load 完成：配置首次 OnChanged 发生在 Mod.Load 之前，反射字段尚为 null
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
            // 这里不检查 HookInstalled：扫描与检测函数注册都不依赖标题 Hook
            // 若因为签名不符没装上 Hook 就早退，关闭扫描时反而会把注册过的群系留在字典里

            // 但必须等 Load 完成：配置首次 OnChanged 发生在 Mod.Load 之前，
            // 反射字段尚为 null，此时执行只会在日志里刷一条「未获取到 BTitles 主模组实例」
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
        // 无论字典是否可用都要清干净，否则注册过的检测函数会留在 BTitles 的检测列表里每帧空转
        // mod 为 null 表示不能在日志里走 Mod.Logger（卸载途中实例已不可靠）
        // 调用点：关闭扫描开关、模组 Unload（含热重载）
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

            // 还原本次扫描覆盖过的已有条目（标题、本地化作用域、颜色、图标）
            // 扫描对已有条目只改字段，不还原会留下「关掉扫描仍显示补丁标题」的残留
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

            // 热重载也要把扫描写进 BTitles 的东西撤掉：BTitles 自身不会被一起卸载，
            // 它的字典会带着本文加入的条目活下来，而记录在卸载后随静态字段一起消失，
            // 只清记录不撤字典就会留下永久孤儿条目（下次扫描还会把它们当「已有」）
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
