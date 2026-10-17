#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using BTitlesEnhance.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesEnhance.Scan
{
    // 配色与图标工具：BackgroundColor 读取、图标加载、主色采样
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
        逐群系配色表：按玩家看得见的标题名精确匹配（区分大小写，不做模糊）
        名字是玩家自己填的，与屏幕上的字不一致时宁可匹配不上，也不要猜错群系
        配置未加载、表为空、或该项颜色还没填（默认全零）时返回 false，调用方沿用策略色
        */
        public static bool TryGetMappedColor(string? displayTitle, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(displayTitle))
                return false;

            var mappings = ModContent.GetInstance<BTitlesConfig>()?.TitleColorMappings;
            if (mappings == null || mappings.Count == 0)
                return false;

            // 表一般是几条到几十条，线性扫描足够，不值得为它建字典与失效逻辑
            foreach (var mapping in mappings)
            {
                if (mapping == null || mapping.Title != displayTitle)
                    continue;

                // 刚加进表还没选颜色时是 (0,0,0,0)：alpha 为 0 会让标题整个看不见，当作没配
                // 用 continue 而不是 return false：同名条目可能不止一条，后面那条也许填了颜色
                if (mapping.Color == default)
                    continue;

                color = mapping.Color;
                return true;
            }

            return false;
        }

        /*
        不用 Color.Transparent/default 作「待补色」哨兵：它是合法颜色值，会让标题真的不可见
        采样由 BiomeNameHook（主线程）执行，后台线程不可调用 Texture2D.GetData
        策略语义：
        - Fixed：一律返回用户挑的固定颜色，不采样
        - IconSampling：一律登记采样（不管有没有背景色），初始白色占位，主线程补 icon 众数
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
                        $"Mods.{nameof(BTitlesEnhance)}.Logs.IconLoadFailed",
                        biome.FullName,
                        ex.Message
                    )
                );
                return null;
            }
        }

        // 采样上限：图标一般远小于此，超大图取这个规模也足够反映主色
        private const int MaxSampleSize = 256;

        /*
        从整个图标采样，返回出现次数最多的颜色（众数）
        像素先量化到 32 级色块（RGB555）再统计，避免抗锯齿噪点把众数打散
        */
        public static Color SampleDominantColor(Texture2D icon)
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
                        $"Mods.{nameof(BTitlesEnhance)}.Logs.SampleColorFailed",
                        icon.Width,
                        icon.Height,
                        ex.Message
                    )
                );
                return Color.Gray;
            }

            var colorFrequency = new Dictionary<int, int>();
            int highestFrequency = 0;
            Color mostCommonColor = Color.Gray;

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

                int colorKey = (pixel.R >> 3) << 10 | (pixel.G >> 3) << 5 | (pixel.B >> 3);
                colorFrequency.TryGetValue(colorKey, out int currentCount);
                currentCount++;
                colorFrequency[colorKey] = currentCount;

                if (currentCount > highestFrequency)
                {
                    highestFrequency = currentCount;
                    // 量化丢掉了低 3 位，还原时每级左移 3 位再 +4 取色块中点
                    mostCommonColor = new Color(
                        (byte)(((colorKey >> 10) & 0x1F) << 3 | 4),
                        (byte)(((colorKey >> 5) & 0x1F) << 3 | 4),
                        (byte)((colorKey & 0x1F) << 3 | 4)
                    );
                }
            }

            // 借来的数组必须归还，否则每次采样都会泄漏一份 256KB
            ArrayPool<Color>.Shared.Return(pixels);
            return highestFrequency > 0 ? mostCommonColor : Color.Gray;
        }
    }
}
