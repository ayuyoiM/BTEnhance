#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using BTitlesLocalizationPatch.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch.Scan
{
    // 配色与图标工具：BackgroundColor 读取、图标加载、代表性颜色采样
    internal static class BiomeStyleHelper
    {
        /*
        读取配色策略与固定颜色（来自 ModConfig）
        这里用 ?. 兜底：配置未加载（Mod.Load 前/卸载中）时退回默认策略 —— 背景色 + 白色
        应用点在扫描（后台线程）与配色刷新（主线程），都不属于每帧热路径，故不做冷却缓存
        */
        public static (TitleColorSource Source, Color FixedColor) GetStylingSettings()
        {
            var config = ModContent.GetInstance<BTitlesConfig>();
            Color fixedColor = config?.FixedTitleColor ?? Color.White;
            // 用户没动过固定颜色时是初始透明/黑，兜成白色，避免 Fixed 策略下标题整个不可见
            if (fixedColor == default)
                fixedColor = Color.White;
            return (config?.PreferredColorSource ?? TitleColorSource.BackgroundColor, fixedColor);
        }

        /*
        配色表的只读快照：条目在读取时就把「不透明 + 跳过未填」算好
        Hook 每次显示标题都会走到 TryGetMappedColor，原实现每次都要 GetInstance 再扫一遍表；
        表本身只在保存配置时变，所以按「保存即失效」缓存一份拍平的数组
        失效由 ApplyConfigChangeStyle / Unload 显式触发（见 InvalidateMappingSnapshot），
        不像 StylingCache 那样按时间冷却：配色表改完要立刻生效，不能有 3 秒的旧值窗口
        */
        private static volatile MappingEntry[] _mappingSnapshot = Array.Empty<MappingEntry>();

        // 空表也要缓存（否则每次命中都要重建一次空数组），所以另用一个标志区分「没建过」与「建过且为空」
        private static volatile bool _mappingSnapshotBuilt;

        private readonly struct MappingEntry
        {
            public readonly string Title;
            public readonly Color Color;

            public MappingEntry(string title, Color color)
            {
                Title = title;
                Color = color;
            }
        }

        private static MappingEntry[] GetMappingSnapshot()
        {
            if (_mappingSnapshotBuilt)
                return _mappingSnapshot;

            var mappings = ModContent.GetInstance<BTitlesConfig>()?.TitleColorMappings;
            if (mappings == null || mappings.Count == 0)
            {
                _mappingSnapshot = Array.Empty<MappingEntry>();
                _mappingSnapshotBuilt = true;
                return _mappingSnapshot;
            }

            var built = new List<MappingEntry>(mappings.Count);
            foreach (var mapping in mappings)
            {
                if (mapping == null || string.IsNullOrEmpty(mapping.Title))
                    continue;

                // alpha 为 0 当作「还没填」：标题色带 alpha 只会让它整个看不见，没有别的语义
                if (mapping.Color.A == 0)
                    continue;

                // UI 不编辑 alpha（[ColorNoAlpha]），字段里的 A 不可信，一律按不透明上色
                built.Add(
                    new MappingEntry(
                        mapping.Title,
                        new Color(mapping.Color.R, mapping.Color.G, mapping.Color.B, (byte)255)
                    )
                );
            }

            // 先填数组再置标志：反过来会短暂暴露「已建好但内容是空数组」的中间态
            _mappingSnapshot = built.Count == 0 ? Array.Empty<MappingEntry>() : built.ToArray();
            _mappingSnapshotBuilt = true;
            return _mappingSnapshot;
        }

        /*
        配色表变更或卸载时清掉快照
        空表也缓存（缓存的空数组同样表示「不用再查」），所以失效只能靠这个显式调用
        */
        internal static void InvalidateMappingSnapshot()
        {
            _mappingSnapshot = Array.Empty<MappingEntry>();
            _mappingSnapshotBuilt = false;
        }

        /*
        逐群系配色表：按玩家看得见的标题名精确匹配（区分大小写，不做模糊）
        名字是玩家自己填的，与屏幕上的字不一致时宁可匹配不上，也不要猜错群系
        配置未加载、表为空、或该项颜色还没填（alpha 仍为 0）时返回 false，调用方沿用策略色
        */
        public static bool TryGetMappedColor(string? displayTitle, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(displayTitle))
                return false;

            // 表一般是几条到几十条，线性扫描足够，不值得为它建字典
            foreach (var entry in GetMappingSnapshot())
            {
                if (entry.Title != displayTitle)
                    continue;

                color = entry.Color;
                return true;
            }

            return false;
        }

        /*
        不用 Color.Transparent/default 作「待补色」哨兵：它是合法颜色值，会让标题真的不可见
        采样由 BiomeNameHook（主线程）执行，后台线程不可调用 Texture2D.GetData
        策略语义：
        - Fixed：一律返回用户挑的固定颜色，不采样
        - IconSampling：一律登记采样（不管有没有背景色），初始白色占位，主线程补图标平均色
        - BackgroundColor（默认 = 旧行为）：有背景色用背景色，否则白色 + 采样
        */
        public static Color GetTitleColors(
            ModBiome? biome,
            TitleColorSource source,
            Color fixedColor,
            out bool needsSampling
        )
        {
            needsSampling = false;

            if (source == TitleColorSource.Fixed)
                return fixedColor == default ? Color.White : fixedColor;

            if (biome == null)
                return Color.White;

            if (source == TitleColorSource.IconSampling)
            {
                needsSampling = true;
                return Color.White;
            }

            if (biome.BackgroundColor.HasValue)
                return biome.BackgroundColor.Value;

            needsSampling = true;
            return Color.White;
        }

        /*
        尝试加载 BestiaryIcon
        加载失败时静默返回 null，不影响群系注册流程
        */
        public static Texture2D? TryLoadIcon(ModBiome? biome)
        {
            if (biome == null)
                return null;

            try
            {
                string iconPath = biome.BestiaryIcon;
                return !string.IsNullOrEmpty(iconPath) && ModContent.HasAsset(iconPath)
                    ? ModContent.Request<Texture2D>(iconPath, AssetRequestMode.ImmediateLoad).Value
                    : null;
            }
            catch (Exception ex)
            {
                DebugLog.Warn(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.IconLoadFailed",
                        biome.FullName,
                        ex.Message
                    )
                );
                return null;
            }
        }

        // 采样上限：图标一般远小于此，超大图取这个规模也足够反映代表性颜色
        private const int MaxSampleSize = 256;

        // 低于这个色度（三通道极差）的像素视为灰调：描边、阴影、单色背景多落在这里
        private const int MinChroma = 24;

        /*
        平均色的饱和度增益
        众数会挑中最纯的那个色块，平均则被大量相近像素稀释；不往饱和方向推一点，
        深色群系的标题会变成一片莫兰迪灰。系数保守，推过头会变成刺眼的荧光色
        */
        private const float SaturationBoost = 1.35f;

        /*
        亮度下限（HSL 的 L，0..1）
        平均色天然比众数暗：众数会挑中某个饱和的亮色块，平均则被同一主体的暗部拉低。
        深色群系的图标尤其明显，直接当标题字色会糊在背景里
        只抬 L、不动 H 与 S，颜色不会偏色相，也不会因为提亮而褪成粉白
        */
        private const float MinLightness = 0.5f;

        /*
        从整个图标采样，返回所有有效像素的平均色（代表性颜色）
        与众数的区别：众数挑「面积最大的那个纯色块」，容易被大块背景与边框主导；
        平均把主体和渐变一起算进去，更接近图标给人的整体印象
        为了不让平均变浑，先剔除三类像素：半透明、接近纯黑纯白、灰调
        */
        public static Color SampleRepresentativeColor(Texture2D icon)
        {
            if (icon == null || icon.IsDisposed || icon.Width <= 1 || icon.Height <= 1)
                return Color.Gray;

            int sampleWidth = Math.Min(icon.Width, MaxSampleSize);
            int sampleHeight = Math.Min(icon.Height, MaxSampleSize);
            int pixelCount = sampleWidth * sampleHeight;

            // 借缓冲而不是每次 new：采样跑在主线程的渲染路径上，
            // 待补色的图标攒到几十个时，每次 256KB 的新分配会攒出一次明显卡顿
            // Rent 返回的数组可能比请求的长，遍历一律以 pixelCount 为界
            Color[] pixels = ArrayPool<Color>.Shared.Rent(pixelCount);
            try
            {
                icon.GetData(
                    0,
                    new Rectangle(0, 0, sampleWidth, sampleHeight),
                    pixels,
                    0,
                    pixelCount
                );
            }
            catch (Exception ex)
            {
                ArrayPool<Color>.Shared.Return(pixels);
                DebugLog.Warn(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.SampleColorFailed",
                        icon.Width,
                        icon.Height,
                        ex.Message
                    )
                );
                return Color.Gray;
            }

            long sumR = 0,
                sumG = 0,
                sumB = 0;
            int validCount = 0;

            // 处理段必须同样以 pixelCount 为界，不能整个 span 走完借来的数组
            foreach (ref Color pixel in pixels.AsSpan(0, pixelCount))
            {
                // 跳过半透明像素：图标阴影与描边多为低 alpha，采进来会让标题发灰
                if (pixel.A < 128)
                    continue;
                // 跳过接近纯黑与纯白：R+G+B 范围为 0..765，这两端作标题色都看不清
                int brightness = pixel.R + pixel.G + pixel.B;
                if (brightness < 30 || brightness > 720)
                    continue;

                // 跳过灰调：三通道几乎相等时它对「这团颜色是什么色」没有信息量，
                // 却常常是图标里面积最大的部分，留着会把平均色整体拉向灰
                int max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                int min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                if (max - min < MinChroma)
                    continue;

                sumR += pixel.R;
                sumG += pixel.G;
                sumB += pixel.B;
                validCount++;
            }

            // 借来的数组必须归还，否则每次采样都会泄漏一份 256KB
            ArrayPool<Color>.Shared.Return(pixels);

            // 全是灰调/半透明/极端亮度：这个图标给不出颜色，交给调用方的灰色兜底
            if (validCount == 0)
                return Color.Gray;

            var average = new Color(
                (byte)(sumR / validCount),
                (byte)(sumG / validCount),
                (byte)(sumB / validCount)
            );
            return BoostSaturation(average);
        }

        /*
        把颜色往饱和、明亮的方向推，色相保持不变
        平均色天生偏灰也偏暗：同一主体的明暗像素互相抵消，得到的饱和度低于任何一个原始像素，
        亮度则被暗部拉向中间。HSL 是现成的，两个量各调各的，互不影响
        */
        private static Color BoostSaturation(Color color)
        {
            Vector3 hsl = Main.rgbToHsl(color);
            hsl.Y = MathHelper.Clamp(hsl.Y * SaturationBoost, 0f, 1f);
            // 只抬下限，不压上限：本来就亮的颜色保持原样
            hsl.Z = Math.Max(hsl.Z, MinLightness);
            Color boosted = Main.hslToRgb(hsl.X, hsl.Y, hsl.Z);
            // hslToRgb 不保留 alpha，统一按不透明返回（标题色带 alpha 只会让它看不见）
            boosted.A = 255;
            return boosted;
        }
    }
}
