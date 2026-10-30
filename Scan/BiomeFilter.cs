#nullable enable
using System;
using System.Collections.Generic;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch.Scan
{
    internal enum BiomeFilterVerdict
    {
        Pass,
        Blacklisted,
        NotWhitelisted,
    }

    /*
    扫描名单过滤

    只有一张表（ScanFilterList），由 ScanFilterMode 决定它这一轮被解释成黑名单还是白名单：
    - Disabled   不生效，表里的内容原样留着
    - Blacklist  命中即跳过
    - Whitelist  未命中即跳过（表为空时全放行）
    一张表 + 一个模式，切模式不用搬数据，「两张表同时生效时听谁的」这种问题也不存在

    与逐群系配色表的分工不同：配色表挂在 Hook 层，因为它要看见每一个 BiomeEntry；
    名单只管「这次扫描要不要碰这个群系」，是纯粹的扫描期判定，所以落在 BiomeRegistrar 一侧

    匹配不做格式限制，三种写法都认：
    - Mod.Biome   命名空间键，最精确，跨语言稳定
    - Biome       短键，BTitles 自己的条目就用这个
    - 显示名       屏幕上看到的那行字，与配色表同一套直觉
    比较忽略大小写与首尾空白；空条目、纯空白条目直接忽略

    必要时的限制：白名单模式下空表 = 放行。
    玩家刚把模式切到白名单、还没填内容时，不该一键把群系全扫没
    */
    internal static class BiomeFilter
    {
        // 主线程维护的不可变快照：ScanFilterList 是 List，后台线程遍历它不安全。
        // 刷新时机与线程归属见 docs/PROJECT.md「扫描名单」与「线程模型」
        private static volatile HashSet<string> _snapshotEntries = new(
            StringComparer.OrdinalIgnoreCase
        );
        private static volatile ScanFilterMode _snapshotMode = ScanFilterMode.Disabled;

        /*
        刷新快照，只能在主线程调用（OnLoaded / OnChanged）
        config 显式传入而非内部 GetInstance：OnLoaded 期间配置未必已注册进 ModContent
        */
        internal static void RefreshSnapshot(BTitlesConfig? config = null)
        {
            config ??= ModContent.GetInstance<BTitlesConfig>();
            if (config == null || config.FilterMode == ScanFilterMode.Disabled)
            {
                _snapshotEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _snapshotMode = ScanFilterMode.Disabled;
                return;
            }

            // BuildSet 每次返回新实例，发布后不再修改：读方看到的就是一份固定内容
            _snapshotEntries = BuildSet(config.ScanFilterList);
            _snapshotMode = config.FilterMode;
        }

        /*
        扫描开始时读一次，整轮共用
        只读快照，不碰配置里的 List（原因见 _snapshotEntries）
        */
        internal static FilterSnapshot Read() => new(_snapshotMode, _snapshotEntries);

        internal static BiomeFilterVerdict Evaluate(
            FilterSnapshot snapshot,
            string namespacedKey,
            string shortName,
            string? displayName
        )
        {
            switch (snapshot.Mode)
            {
                case ScanFilterMode.Blacklist:
                    return Matches(snapshot.Entries, namespacedKey, shortName, displayName)
                        ? BiomeFilterVerdict.Blacklisted
                        : BiomeFilterVerdict.Pass;

                case ScanFilterMode.Whitelist:
                    // 空表 = 放行，见类注释
                    if (snapshot.Entries.Count == 0)
                        return BiomeFilterVerdict.Pass;

                    return Matches(snapshot.Entries, namespacedKey, shortName, displayName)
                        ? BiomeFilterVerdict.Pass
                        : BiomeFilterVerdict.NotWhitelisted;

                default:
                    return BiomeFilterVerdict.Pass;
            }
        }

        // 玩家在列表里多点一行空条目不该把整张名单弄坏
        private static HashSet<string> BuildSet(List<BiomeFilterEntry>? entries)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entries == null)
                return set;

            foreach (var entry in entries)
            {
                string? name = entry?.Name?.Trim();
                if (!string.IsNullOrEmpty(name))
                    set.Add(name);
            }

            return set;
        }

        private static bool Matches(
            HashSet<string> set,
            string namespacedKey,
            string shortName,
            string? displayName
        )
        {
            if (set.Count == 0)
                return false;

            if (set.Contains(namespacedKey) || set.Contains(shortName))
                return true;

            return !string.IsNullOrWhiteSpace(displayName) && set.Contains(displayName.Trim());
        }

        internal sealed class FilterSnapshot
        {
            internal FilterSnapshot(ScanFilterMode mode, HashSet<string> entries)
            {
                Mode = mode;
                Entries = entries;
            }

            internal ScanFilterMode Mode { get; }
            internal HashSet<string> Entries { get; }
        }
    }
}
