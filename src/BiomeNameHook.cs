#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BTitles;
using BTitlesLocalizationPatch.Scan;
using Hjson;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch
{
    internal static class BiomeNameHook
    {
        // 只缓存不受 BTitles 配置影响的翻译链结果（步骤 1-3）
        // 自定义覆盖（步骤 4）每次重新查配置，保证配置变更即时生效
        internal static readonly ConcurrentDictionary<string, string> TranslationCache = new();

        // 群系数量有限（数百级），超限清空重建即可，无需复杂淘汰策略
        private const int MaxCacheEntries = 2048;
        private static volatile string _lastCultureName = "";

        // 样式开关缓存：UI 渲染热路径避免每次查询 ModContent.GetInstance
        // 修改配置后最多 3 秒生效；ApplyConfigChangeStyle 会立即同步缓存
        private static readonly Diagnostics.TimedValueCache StylingCache = new(
            nameof(BTitlesConfig.EnableAutoStyling),
            () => ModContent.GetInstance<BTitlesConfig>()?.EnableAutoStyling ?? false,
            TimeSpan.FromSeconds(3)
        );

        internal static bool IsStylingEnabled() => StylingCache.Get();

        /*
        被配色表覆盖前的原色
        配色表是玩家随时会改的：命中时记下这一条目原本的颜色，撤销命中时还回去，
        否则手工配色一旦覆盖过 BTitles 内置条目（如「地狱」），改回表就再也恢复不了
        按引用比较：BiomeEntry 没重写 Equals，两个「值相等」的条目不是同一条
        */
        private static readonly ConcurrentDictionary<BiomeEntry, Color> MappedOriginalColors = new(
            BiomeEntryReferenceComparer.Instance
        );

        /*
        卸载前把配色表改过的颜色还回去
        平时靠 Hook 在下次显示时懒还原，但 Unload 之后 Hook 已摘，
        不主动还原的话 BTitles 字典里会留下本模组的颜色（它不随本模组卸载）
        */
        internal static void RestoreMappedColors()
        {
            foreach (var pair in MappedOriginalColors)
                pair.Key.TitleColor = pair.Value;
            MappedOriginalColors.Clear();
        }

        /*
        条目颜色被外部还原（扫描结果撤除）时丢掉对应记录
        否则留下的「原色」是扫描后的颜色，下次撤销命中会把它盖回去，抹掉刚还原的基准值
        */
        internal static void ForgetMappedColor(BiomeEntry entry) =>
            MappedOriginalColors.TryRemove(entry, out _);

        // 配置变更时立即同步缓存，避免 3 秒冷却期内读到旧值
        internal static void UpdateStylingCache(bool enabled) => StylingCache.Set(enabled);

        internal static void ResetStylingCache() => StylingCache.Reset();

        internal static void ResetCultureCache()
        {
            _lastCultureName = "";
        }

        public static string GetActualTitleNameHook(
            // MonoMod 替换式 Hook 传入的原方法引用，翻译链全未命中时作原生回落
            Func<BiomeTitlesUI, BiomeEntry, string> orig,
            BiomeTitlesUI self,
            BiomeEntry biomeEntry
        )
        {
            if (self == null || biomeEntry == null)
                return "";
            if (!BTitlesLocalizationPatch.HookInstalled)
                return biomeEntry.Title ?? "";

            string displayName = ResolveDisplayName(orig, self, biomeEntry);

            /*
            逐群系配色表按最终显示名匹配，且优先于策略色：
            策略色只是批量兜底，玩家手工指定的那个名字此刻才真正可见
            关配色时整档不生效，手工表也不越过这个开关
            */
            if (
                IsStylingEnabled()
                && BiomeStyleHelper.TryGetMappedColor(displayName, out var mappedColor)
            )
            {
                // 首次命中先记原色；记过就不再覆盖，否则第二次命中会把「已覆盖的值」当原色存下
                MappedOriginalColors.TryAdd(biomeEntry, biomeEntry.TitleColor);
                biomeEntry.TitleColor = mappedColor;
            }
            else if (MappedOriginalColors.TryRemove(biomeEntry, out var originalColor))
            {
                // 命中过但现在不命中了（改了表 / 换了语言 / 关了配色）：还原本来的颜色
                biomeEntry.TitleColor = originalColor;
            }

            return displayName;
        }

        /*
        图标补色：只有登记在 PendingColorKeys 的条目才补——已按策略直接取到颜色的，
        以及关配色时留下的白色，都不该被图标平均色覆盖
        采样成功即出队，避免每帧重复采样；图标缺失/释放则留在队列，等图标可用再补
        必须在翻译缓存命中判断之前调用：缓存命中路径不经过后面的解析链，
        漏调会导致切颜色来源后补色被缓存早退短路，标题一直白色
        */
        private static void TryBackfillColor(BiomeEntry biomeEntry)
        {
            if (
                !IsStylingEnabled()
                || !BiomeRegistrar.PendingColorKeys.ContainsKey(biomeEntry)
                || biomeEntry.Icon == null
            )
            {
                return;
            }

            // 热重载后图标可能已释放
            if (biomeEntry.Icon.IsDisposed)
            {
                biomeEntry.Icon = TryReloadIcon(biomeEntry.Key);
            }

            if (biomeEntry.Icon != null && !biomeEntry.Icon.IsDisposed)
            {
                biomeEntry.TitleColor = BiomeStyleHelper.SampleRepresentativeColor(biomeEntry.Icon);
                BiomeRegistrar.PendingColorKeys.TryRemove(biomeEntry, out _);
            }
        }

        /*
        原来的整条解析链原样搬到这里：5 步查找、缓存、图标补色都在这一层
        抽出来是为了让 Hook 能在拿到最终字符串之后再动颜色 ——
        配色表按「看得见的名字」匹配，链上任何一个中间 return 都还不是最终值
        */
        private static string ResolveDisplayName(
            // MonoMod 替换式 Hook 传入的原方法引用，翻译链全未命中时作原生回落
            Func<BiomeTitlesUI, BiomeEntry, string> orig,
            BiomeTitlesUI self,
            BiomeEntry biomeEntry
        )
        {
            var config = self.Config;
            string biomeKey = biomeEntry.Key ?? "";
            string sanitizedKey = biomeKey.Replace(" ", "_");
            string safeScope = (biomeEntry.LocalizationScope ?? "").Replace(".", "_");
            bool hasValidKey = !string.IsNullOrEmpty(sanitizedKey);

            // scope 为空时缓存键退化成 "|{key}"，不同模组的同名群系会串味，故不缓存。
            // 与 hasValidKey 分开：能不能翻译只看 key。详见 docs/PROJECT.md「翻译回退链」的缓存策略
            bool canCache = hasValidKey && !string.IsNullOrEmpty(safeScope);

            string cacheKey = $"{safeScope}|{sanitizedKey}";
            string currentCulture = Language.ActiveCulture.Name;

            if (_lastCultureName != currentCulture)
            {
                _lastCultureName = currentCulture;
                TranslationCache.Clear();
            }

            // 补色放在缓存判断之前：缓存命中路径也要补色，
            // 否则切颜色来源后 TranslationCache 未清，补色块被缓存早退短路，标题一直白色
            TryBackfillColor(biomeEntry);

            if (canCache && TranslationCache.TryGetValue(cacheKey, out var cachedEntry))
            {
                // 命中缓存说明结果与上次一致，重复写三行只会刷满 client.log
                // 详见 docs/PROJECT.md「调试日志系统」
                return ApplyCustomOverride(cachedEntry, config) ?? cachedEntry;
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
                if (canCache && baseText != null)
                    CacheEntry(cacheKey, baseText);
                LogBiomeEntry(biomeEntry.Key ?? "", overridden, biomeEntry, "CustomName");
                return overridden;
            }
            if (baseText != null)
            {
                LogAndCache(biomeEntry, cacheKey, canCache, baseText, baseTag ?? "Unknown");
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

                // 直接对照：源模组 en-US hjson 中同键的原文与当前取值一致 ⇒ 源模组未提供当前
                // 语言译文（tML 英文回退），跳过步骤 1 走后续链。
                if (IsEnglishFallbackValue(localizationKey, safeScope, localized))
                    return (null, null);

                return (localized, "ModLocalization");
            }
            return (null, null);
        }

        /*
        判断 localized 是否等于源模组 en-US 原文（未译）。
        localizationKey 形如 Mods.{modName}.Biomes.{key}.DisplayName —— 对真实模组
        （tML 强制内部名是合法 C# 标识符）modName 就是不带点的 safeScope 首段。
        - 模组没带 en-US 资产 / 键缺失 ⇒ 无法比对，退回旧的 ASCII 启发式行为
        （这是显式保留的兼容分支：BTitles 的翻译也在，但不能凭空捏造「没译」结论，宁可保守）
        - 仅在非 en-US 语言下被调用（调用点已由 shouldTranslate 保证）
        */
        private static bool IsEnglishFallbackValue(
            string localizationKey,
            string safeScope,
            string localized
        )
        {
            // 提取模组名：Mods.{modName}.Biomes.{key}.DisplayName 的第 2 段
            string modName = GetScopeModName(safeScope);
            if (string.IsNullOrEmpty(modName))
                return IsPureAscii(localized); // 结构异常时的保守回退（旧启发式）

            Mod? mod = ModLoader.Mods.FirstOrDefault(m => m.Name == modName);
            if (mod == null)
                return IsPureAscii(localized);

            IReadOnlyDictionary<string, string> map = GetEnUsMap(mod);
            if (map.Count == 0)
                return IsPureAscii(localized); // 无 en-US 资产：无法比对，走旧启发式

            if (!map.TryGetValue(localizationKey, out string? englishValue))
                return IsPureAscii(localized); // 键不在 en-US 里（BTitles 运行时注册的）：保守回退

            return string.Equals(localized, englishValue, StringComparison.Ordinal);
        }

        // safeScope 形如 {modName}.{其他段}；模组名是合法标识符，不含「.」，
        // 取第一个点之前的部分即 mod 名
        private static string GetScopeModName(string safeScope)
        {
            int dot = safeScope.IndexOf('.');
            return dot > 0 ? safeScope[..dot] : safeScope;
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
            bool canCache,
            string text,
            string tag
        )
        {
            if (canCache && !string.IsNullOrEmpty(text))
                CacheEntry(cacheKey, text);
            LogBiomeEntry(entry.Key ?? "", text, entry, tag);
            return text;
        }

        private static void CacheEntry(string cacheKey, string text)
        {
            if (TranslationCache.Count >= MaxCacheEntries)
                TranslationCache.Clear();
            TranslationCache[cacheKey] = text;
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

        // ---------------------------------------------------------------
        // 步骤 1 英文原文对照（替代旧的 NonLatinLanguagePrefixes/IsPureAscii 启发式）
        // 机制：直接比对「源模组 en-US hjson」与「当前语言取值」。
        // 两者一致说明源模组根本没提供当前语言译文（tML 英文回退），跳过步骤 1 走后续链
        // ---------------------------------------------------------------

        // mod 名 -> 该模组 en-US 本地化键值表（只含 DisplayName 会用到的键前缀）
        // 负结果也缓存（模组没带 en-US 或解析失败时记空表，避免反复重试）
        private static readonly ConcurrentDictionary<
            string,
            IReadOnlyDictionary<string, string>
        > EnUsTranslationMaps = new();

        /*
        构建单个模组的 en-US 键值表

        只收 DisplayName 叶子：IsEnglishFallbackValue 只查
        Mods.{mod}.Biomes.{key}.DisplayName 这一类键，物品名 / NPC 名 / buff 描述存进来纯属浪费。
        实测 StarsAbove 的 en-US 里 2330 个 DisplayName 只有 24 个属于群系

        三种布局都要认（实测都存在于真实模组中）：
        - Localization/en-US_Mods.X.hjson    → culture 与 prefix 都在文件名
        - Localization/en-US.hjson           → 只有 culture，内容自带 Mods:{X:{...}} 外层
        - Localization/en-US/Mods.X.hjson    → culture 在目录名，文件名即 prefix
        早先只把 Path.GetFileName 交给 tML 的解析函数，后两种布局因此全部被跳过，
        大模组的 en-US 对照从未生效过
        */
        private static IReadOnlyDictionary<string, string> BuildEnUsMap(Mod mod)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var source = mod.RootContentSource;
                if (source == null)
                    return result;

                foreach (string assetPath in source.EnumerateAssets())
                {
                    if (!assetPath.EndsWith(".hjson", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!TryResolveEnUsPrefix(assetPath, out string prefix))
                        continue;

                    using var stream = source.OpenStream(assetPath);
                    using var reader = new StreamReader(stream);
                    string text = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    // Hjson 是 tML 引用库（tMLMod.targets 会带上 Libraries 下的 Hjson.dll）
                    JsonValue parsed = HjsonValue.Parse(text);
                    FlattenHjson(parsed, prefix, result);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.DebugLog.Warn(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.EnUsMapFailed",
                        mod.Name,
                        ex.Message
                    )
                );
            }
            return result;
        }

        /*
        判定一条资产路径是不是 en-US 本地化，并给出建表用的前缀
        返回 true 时 prefix 可能为空串：那是「内容自带完整路径」的布局，平铺时直接以叶节点路径为键
        先看目录再看文件名：布局三里文件名（Mods.X.Biomes.hjson）有可能被 tML 的解析函数误当成
        culture，先判目录就不会被这种误判挡住
        */
        private static bool TryResolveEnUsPrefix(string assetPath, out string prefix)
        {
            prefix = "";
            string fileName = Path.GetFileName(assetPath);

            // 布局三：culture 是最后一层目录名，文件名即 prefix
            string directory = (Path.GetDirectoryName(assetPath) ?? "").Replace('\\', '/');
            int slash = directory.LastIndexOf('/');
            string lastDir = slash >= 0 ? directory[(slash + 1)..] : directory;
            if (string.Equals(lastDir, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                prefix = Path.GetFileNameWithoutExtension(fileName);
                return !string.IsNullOrEmpty(prefix);
            }

            // 布局一 / 二：culture 写在文件名里，交给 tML 的解析函数
            if (
                LocalizationLoader.TryGetCultureAndPrefixFromPath(
                    fileName,
                    out GameCulture? culture,
                    out string? filePrefix
                )
                && culture != null
                && culture.Name == "en-US"
            )
            {
                prefix = filePrefix ?? "";
                return true;
            }

            return false;
        }

        // 展开解析后的 Hjson：叶子（字符串等标量）记为 key=value；对象递归；数组一律跳过（本地化键不会是数组）
        private static void FlattenHjson(
            JsonValue value,
            string prefix,
            Dictionary<string, string> into
        )
        {
            if (value is JsonObject obj)
            {
                foreach (string key in obj.Keys)
                    FlattenHjson(obj[key], CombineKey(prefix, key), into);
                return;
            }
            // 数组显式跳过：掉进下面的叶子分支会被 ToString() 成 "[...]" 整段文本记进对照表
            if (value is JsonArray)
                return;

            // 只收群系显示名：IsEnglishFallbackValue 只查 Mods.{mod}.Biomes.{key}.DisplayName 这一类键。
            // 只判 .DisplayName 结尾是不够的 —— StarsAbove 的 en-US 里有 2330 个 DisplayName，
            // 属于群系的只有 24 个，不限定 .Biomes. 等于没过滤。
            // 这段与 TryStep1 里拼键的格式强耦合：那边改了键结构，这里必须同步，否则会静默退化成 ASCII 判断
            if (
                !prefix.EndsWith(".DisplayName", StringComparison.Ordinal)
                || !prefix.Contains(".Biomes.", StringComparison.Ordinal)
            )
                return;

            string? text = HjsonValueCheck(value);
            if (text != null && !into.ContainsKey(prefix))
                into[prefix] = text;
        }

        // 布局二的 prefix 为空：内容自带 Mods:{X:{...}} 外层，叶节点路径本身就是完整键
        private static string CombineKey(string prefix, string key) =>
            prefix.Length == 0 ? key : $"{prefix}.{key}";

        private static string? HjsonValueCheck(JsonValue value)
        {
            try
            {
                // Hjson 3.x 的 Stringify 枚举没有 Json 成员；Json 格式即默认 ToString()
                string text = value.ToString();
                if (text != null && text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                    return text[1..^1];
                return text;
            }
            catch
            {
                return null;
            }
        }

        /*
        取模组的 en-US 映射（懒构建 + 负缓存）。
        空表 = 该模组没带 en-US 资产或构建失败，后续一律退回旧的 ASCII 启发式
        直接收 Mod 实例：调用方（IsEnglishFallbackValue）已持有实例，
        闭包里再按 Name 线性回查 ModLoader.Mods 是白扫一遍
        */
        private static IReadOnlyDictionary<string, string> GetEnUsMap(Mod mod) =>
            EnUsTranslationMaps.GetOrAdd(mod.Name, _ => BuildEnUsMap(mod));

        internal static void ResetEnUsMapCache() => EnUsTranslationMaps.Clear();

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
