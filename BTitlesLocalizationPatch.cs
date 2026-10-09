#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BTitles;
using BTitlesLocalizationPatch.Diagnostics;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
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
        private Hook? _getActualTitleNameHook;
        private volatile bool _loaded;

        // volatile: 后台线程（PostSetupContent）读 Load（主线程）写入的最新值
        private static volatile FieldInfo? _instanceField;
        private static volatile FieldInfo? _biomeDictField;
        private static volatile FieldInfo? _checkFuncsField;
        internal static FieldInfo? InstanceField => _instanceField;
        internal static FieldInfo? BiomeDictField => _biomeDictField;
        internal static FieldInfo? CheckFuncsField => _checkFuncsField;

        // volatile: BiomeNameHook（UI 线程）读，Load（主线程）写
        internal static volatile bool HookInstalled;

        private static readonly Type[] _expectedHookSignature = [typeof(BiomeEntry)];

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
            InstallHookWithFingerprint();
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

        // 签名指纹验证：防止 BTitles 更新改变方法签名导致 new Hook() 崩溃
        private void InstallHookWithFingerprint()
        {
            MethodInfo? getTitleMethod = typeof(BiomeTitlesUI).GetMethod(
                "GetActualTitleName",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            if (getTitleMethod == null)
            {
                HookInstalled = false;
                Logger.Error("Failed to find BiomeTitlesUI.GetActualTitleName");
                return;
            }

            if (!ValidateMethodSignature(getTitleMethod, _expectedHookSignature))
            {
                HookInstalled = false;
                Logger.Error(
                    $"Hook signature mismatch: actual {FormatMethodSignature(getTitleMethod)}, expected BiomeEntry"
                );
                return;
            }

            _getActualTitleNameHook = new Hook(
                getTitleMethod,
                new Func<
                    Func<BiomeTitlesUI, BiomeEntry, string>,
                    BiomeTitlesUI,
                    BiomeEntry,
                    string
                >(BiomeNameHook.GetActualTitleNamePrefixHook)
            );
            HookInstalled = true;
            Logger.Info("GetActualTitleName Detour patch applied successfully");
        }

        private static bool ValidateMethodSignature(MethodInfo method, Type[] expectedParamTypes)
        {
            var actualParams = method.GetParameters();
            if (actualParams.Length != expectedParamTypes.Length)
                return false;
            for (int i = 0; i < actualParams.Length; i++)
            {
                if (actualParams[i].ParameterType != expectedParamTypes[i])
                    return false;
            }
            return true;
        }

        private static string FormatMethodSignature(MethodInfo method)
        {
            var paramNames = method
                .GetParameters()
                .Select(p =>
                {
                    string name = p.ParameterType.IsByRef
                        ? p.ParameterType.GetElementType()?.Name ?? "?"
                        : p.ParameterType.Name;
                    string mod =
                        p.IsOut ? "out"
                        : p.ParameterType.IsByRef ? "ref"
                        : "";
                    return mod.Length > 0 ? $"{name} {mod}" : name;
                });
            return $"{method.Name}({string.Join(", ", paramNames)})";
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

            if (!HookInstalled)
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
            foreach (var (dictKey, modBiome) in Scan.BiomeRegistrar.ScannedBiomes!)
            {
                // 跳过预存条目：不覆盖 BTitles 原有的配色
                if (
                    Scan.BiomeRegistrar.PreScanKeys != null
                    && Scan.BiomeRegistrar.PreScanKeys.Contains(dictKey)
                )
                    continue;

                if (!biomeDict.TryGetValue(dictKey, out var entry))
                    continue;

                if (modBiome == null)
                    continue;

                if (enabled)
                {
                    Scan.BiomeStyleHelper.GetTitleColors(modBiome, out Color titleColor);
                    entry.TitleColor = titleColor;
                }
                else
                {
                    entry.TitleColor = default;
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
            if (!HookInstalled)
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

        private static void UnregisterScannedBiomes(Mod mod)
        {
            if (
                Scan.BiomeRegistrar.ScannedBiomes == null
                || Scan.BiomeRegistrar.ScannedBiomes.Count == 0
            )
            {
                Scan.BiomeRegistrar.Cleanup();
                return;
            }

            var instance = InstanceField?.GetValue(null);
            if (instance == null)
                return;

            var biomeDict = BiomeDictField?.GetValue(instance) as Dictionary<string, BiomeEntry>;
            if (biomeDict == null)
                return;

            // 移除本次扫描新增的字典条目（快照中不存在的键）
            if (Scan.BiomeRegistrar.PreScanKeys != null)
            {
                int removed = 0;
                foreach (var (dictKey, _) in Scan.BiomeRegistrar.ScannedBiomes!)
                {
                    if (
                        dictKey != null
                        && !Scan.BiomeRegistrar.PreScanKeys.Contains(dictKey)
                        && biomeDict.Remove(dictKey)
                    )
                        removed++;
                }
                if (removed > 0)
                    mod.Logger.Info(
                        Language.GetTextValue(
                            $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.UnregisteredBiomes",
                            removed
                        )
                    );
            }

            Scan.BiomeRegistrar.Cleanup();
            Scan.BiomeRegistrar.ScannedBiomes = null;
            Scan.BiomeRegistrar.PreScanKeys = null;
        }

        public override void Unload()
        {
            BiomeNameHook.TranslationCache.Clear();
            BiomeNameHook.ResetStylingCache();
            BiomeNameHook.ResetCultureCache();

            // 单独 try-catch 包裹 Dispose，防止它抛异常阻断后续清理
            try
            {
                _getActualTitleNameHook?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Hook Dispose failed: {ex.Message}");
            }
            _getActualTitleNameHook = null;
            HookInstalled = false;

            Scan.BiomeRegistrar.Cleanup();
            // 清理扫描记录，避免热重载后残留旧引用
            Scan.BiomeRegistrar.ClearScannedBiomes();

            _instanceField = null;
            _biomeDictField = null;
            _checkFuncsField = null;
            _loaded = false;

            // 清理调试日志写入器，防止卸载后残留引用
            Diagnostics.DebugLog.InfoWriter = s => { };
            Diagnostics.DebugLog.WarnWriter = s => { };
            Diagnostics.DebugLog.ErrorWriter = s => { };

            // 重置调试日志缓存，防止热重载后 3 秒内读到旧缓存值
            Diagnostics.DebugLog.ResetCache();
        }
    }
}
