#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using BTitles;
using BTitlesLocalizationPatch.Scan;
using Microsoft.Xna.Framework.Graphics;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch
{
    internal static class BiomeNameHook
    {
        // 只缓存不受 BTitles 配置影响的翻译链结果（步骤 1-3）
        // 自定义覆盖（步骤 4）每次重新查配置，保证配置变更即时生效
        internal static readonly ConcurrentDictionary<
            string,
            (string Text, string Tag)
        > TranslationCache = new();
        // 群系数量有限（数百级），超限清空重建即可，无需复杂淘汰策略
        private const int MaxCacheEntries = 2048;
        private static volatile string _lastCultureName = "";

        // 样式开关缓存：UI 渲染热路径避免每次查询 ModContent.GetInstance
        // 修改配置后最多 3 秒生效；ApplyConfigChangeStyle 会立即同步缓存
        private static readonly Diagnostics.TimedValueCache StylingCache = new(
            () => ModContent.GetInstance<BTitlesConfig>()?.EnableAutoStyling ?? false,
            TimeSpan.FromSeconds(3)
        );

        private static bool IsStylingEnabled() => StylingCache.Get();

        // 配置变更时立即同步缓存，避免 3 秒冷却期内读到旧值
        internal static void UpdateStylingCache(bool enabled) => StylingCache.Set(enabled);

        // 热重载时使缓存立即失效，下次调用重新读取配置
        internal static void ResetStylingCache() => StylingCache.Reset();

        // 热重载时重置文化名缓存，避免残留旧语言状态
        internal static void ResetCultureCache()
        {
            _lastCultureName = "";
        }

        public static string GetActualTitleNameHook(
            // orig 是 MonoMod 替换式 Hook 传入的原方法引用，翻译链全未命中时作原生回落
            Func<BiomeTitlesUI, BiomeEntry, string> orig,
            BiomeTitlesUI self,
            BiomeEntry biomeEntry
        )
        {
            if (self == null || biomeEntry == null)
                return "";
            if (!BTitlesLocalizationPatch.HookInstalled)
                return biomeEntry.Title ?? "";

            var config = self.Config;
            string biomeKey = biomeEntry.Key ?? "";
            string sanitizedKey = biomeKey.Replace(" ", "_");
            string safeScope = (biomeEntry.LocalizationScope ?? "").Replace(".", "_");
            bool hasValidKey = !string.IsNullOrEmpty(sanitizedKey);

            string cacheKey = $"{safeScope}|{sanitizedKey}";
            string currentCulture = Language.ActiveCulture.Name;

            if (_lastCultureName != currentCulture)
            {
                _lastCultureName = currentCulture;
                TranslationCache.Clear();
            }

            if (hasValidKey && TranslationCache.TryGetValue(cacheKey, out var cachedEntry))
            {
                string displayName =
                    ApplyCustomOverride(cachedEntry.Text, config) ?? cachedEntry.Text;
                string sourceTag = displayName == cachedEntry.Text ? cachedEntry.Tag : "CustomName";
                LogBiomeEntry(biomeEntry.Key ?? "", displayName, biomeEntry, sourceTag);
                return displayName;
            }

            // 只给「群系没提供背景色」的条目补色：登记在 PendingColorKeys 里才算待补色
            // 已按 BackgroundColor 取到颜色的条目，以及关闭配色时留下的白色，都不该被图标众数覆盖
            if (
                IsStylingEnabled()
                && BiomeRegistrar.PendingColorKeys.ContainsKey(biomeEntry)
                && biomeEntry.Icon != null
            )
            {
                // 热重载后 GPU 资源释放，图标引用可能已失效
                if (biomeEntry.Icon.IsDisposed)
                {
                    biomeEntry.Icon = TryReloadIcon(biomeEntry.Key);
                }

                if (biomeEntry.Icon != null && !biomeEntry.Icon.IsDisposed)
                {
                    biomeEntry.TitleColor = BiomeStyleHelper.SampleDominantColor(biomeEntry.Icon);
                    // 补色完成即出队，避免同一个条目每帧重复采样
                    BiomeRegistrar.PendingColorKeys.TryRemove(biomeEntry, out _);
                }
            }

            string? baseText = null;
            string? baseTag = null;

            bool shouldTranslate = hasValidKey && currentCulture != "en-US";

            // 1. 源模组本地化
            if (shouldTranslate)
                (baseText, baseTag) = TryStep1(
                    sanitizedKey,
                    safeScope,
                    biomeEntry.LocalizationScope
                );

            // 2. BTitles 自身翻译
            if (baseText == null && shouldTranslate)
                (baseText, baseTag) = TryLocalizedSteps(
                    "BTitles",
                    $"Mods.BiomeTitles.Title.{safeScope}.{sanitizedKey}"
                );

            // 3. 补丁模组补充翻译
            if (baseText == null && shouldTranslate)
                (baseText, baseTag) = TryLocalizedSteps(
                    "ExtraTitles",
                    $"Mods.{nameof(BTitlesLocalizationPatch)}.ExtraTitles.{safeScope}.{sanitizedKey}"
                );

            // 4. 自定义覆盖
            string? overridden = baseText != null ? ApplyCustomOverride(baseText, config) : null;
            if (overridden != null)
            {
                if (hasValidKey && baseText != null)
                    CacheEntry(cacheKey, baseText, baseTag ?? "Unknown");
                LogBiomeEntry(biomeEntry.Key ?? "", overridden, biomeEntry, "CustomName");
                return overridden;
            }
            if (baseText != null)
            {
                LogAndCache(biomeEntry, cacheKey, hasValidKey, baseText, baseTag ?? "Unknown");
                return baseText;
            }

            // 英文环境 / 链全未命中：用原始标题匹配自定义覆盖
            if (config != null)
            {
                string title = biomeEntry.Title ?? "";
                var match = config.CustomBiomeNames?.FirstOrDefault(n => n.CurrentName == title);
                if (match != null)
                {
                    LogBiomeEntry(biomeEntry.Key ?? "", match.NewName, biomeEntry, "CustomName");
                    return match.NewName;
                }
            }

            // 5. 回退：先尝试 BTitles 原生解析（未来版本新增逻辑自动跟随），异常时回退原始标题
            // 这一层跟着 BTitles 的配置走，结果不进缓存，否则改配置要等切换语言才生效
            try
            {
                string nativeTitle = orig(self, biomeEntry);
                if (!string.IsNullOrEmpty(nativeTitle))
                {
                    LogBiomeEntry(biomeEntry.Key ?? "", nativeTitle, biomeEntry, "Fallback");
                    return nativeTitle;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.DebugLog.Warn(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.NativeTitleFailed",
                        biomeEntry.Key ?? "",
                        ex.Message
                    )
                );
            }

            string fallbackTitle = biomeEntry.Title ?? "";
            LogBiomeEntry(biomeEntry.Key ?? "", fallbackTitle, biomeEntry, "Fallback");
            return fallbackTitle;
        }

        private static (string? Text, string? Tag) TryStep1(
            string sanitizedKey,
            string safeScope,
            string? scope
        )
        {
            string localizationKey =
                scope == "Terraria"
                    ? $"Mods.BiomeTitles.Title.Terraria.{sanitizedKey}"
                    : $"Mods.{safeScope}.Biomes.{sanitizedKey}.DisplayName";

            if (TryLocalized(localizationKey) is { } localized)
            {
                if (scope == "Terraria")
                    return (localized, "BTitles");

                // 非拉丁文字语言下纯 ASCII 值说明模组没提供当前语言的译文（英文回退），
                // 跳过此步，让后续翻译链继续查找
                if (IsNonLatinLanguage() && IsPureAscii(localized))
                    return (null, null);

                return (localized, "ModLocalization");
            }
            return (null, null);
        }

        private static (string? Text, string? Tag) TryLocalizedSteps(string tag, string locKey)
        {
            if (TryLocalized(locKey) is { } localized)
                return (localized, tag);
            return (null, null);
        }

        private static string? ApplyCustomOverride(string currentName, object? configObj)
        {
            if (configObj is not GeneralConfig config || config.CustomBiomeNames == null)
                return null;

            var match = config.CustomBiomeNames.FirstOrDefault(n => n.CurrentName == currentName);
            if (match == null)
                return null;

            if (!Diagnostics.DebugLog.Enabled)
                return match.NewName;

            Diagnostics.DebugLog.Info(
                Language.GetTextValue(
                    $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.CustomOverride",
                    currentName,
                    match.NewName
                )
            );
            return match.NewName;
        }

        private static string LogAndCache(
            BiomeEntry entry,
            string cacheKey,
            bool hasValidKey,
            string text,
            string tag
        )
        {
            if (hasValidKey && !string.IsNullOrEmpty(text))
                CacheEntry(cacheKey, text, tag);
            LogBiomeEntry(entry.Key ?? "", text, entry, tag);
            return text;
        }

        private static void CacheEntry(string cacheKey, string text, string tag)
        {
            if (TranslationCache.Count >= MaxCacheEntries)
                TranslationCache.Clear();
            TranslationCache[cacheKey] = (text, tag);
        }

        private static void LogBiomeEntry(string key, string text, BiomeEntry entry, string tag)
        {
            if (!Diagnostics.DebugLog.Enabled)
                return;

            Diagnostics.DebugLog.Info(
                Language.GetTextValue(
                    $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.BiomeEntry",
                    key,
                    text
                )
            );
            string iconInfo =
                entry.Icon != null
                    ? Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.IconPresent",
                        entry.Icon.Width,
                        entry.Icon.Height
                    )
                    : Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.IconNull"
                    );
            Diagnostics.DebugLog.Info(
                Language.GetTextValue(
                    $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.BiomeDetail",
                    entry.LocalizationScope ?? "",
                    iconInfo,
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.SourceTags.{tag}"
                    )
                )
            );
            Diagnostics.DebugLog.Info(
                Language.GetTextValue(
                    $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.BiomeColor",
                    entry.TitleColor.R,
                    entry.TitleColor.G,
                    entry.TitleColor.B
                )
            );
        }

        private static string? TryLocalized(string locKey)
        {
            if (Language.Exists(locKey))
            {
                string value = Language.GetTextValue(locKey);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
            return null;
        }

        // 非拉丁文字语言：tModLoader 仅中/俄两种，模组可能未提供译文而回退英文
        private static readonly string[] NonLatinLanguagePrefixes = ["zh-", "ru"];

        private static bool IsNonLatinLanguage()
        {
            string name = Language.ActiveCulture.Name;
            foreach (string prefix in NonLatinLanguagePrefixes)
            {
                if (name.StartsWith(prefix))
                    return true;
            }
            return false;
        }

        private static Texture2D? TryReloadIcon(string? biomeKey)
        {
            if (string.IsNullOrEmpty(biomeKey) || BiomeRegistrar.ScannedBiomes == null)
                return null;

            foreach (var (dictKey, biome, _) in BiomeRegistrar.ScannedBiomes)
            {
                if (biome?.Name == biomeKey || dictKey == biomeKey)
                    return BiomeStyleHelper.TryLoadIcon(biome);
            }
            return null;
        }

        private static bool IsPureAscii(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 127)
                    return false;
            }
            return true;
        }
    }
}
