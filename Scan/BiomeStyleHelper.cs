#nullable enable
using System;
using System.Collections.Generic;
using BTitlesLocalizationPatch.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch.Scan
{
    // 配色与图标工具：BackgroundColor 读取、图标加载、主色采样
    internal static class BiomeStyleHelper
    {
        // 取色链：群系返回色（BackgroundColor）→ 无返回色时用白色，并标记等待图标采样
        // 不用 Color.Transparent/default 作「待补色」哨兵：它是合法颜色值，会让标题真的不可见
        // 采样由 BiomeNameHook（主线程）执行，后台线程不可调用 Texture2D.GetData
        public static Color GetTitleColors(ModBiome? biome, out bool needsSampling)
        {
            needsSampling = false;
            if (biome == null)
                return Color.White;

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

        /*
        从整个图标采样，返回出现次数最多的颜色（众数）
        像素先量化到 32 级色块（RGB555）再统计，避免抗锯齿噪点把众数打散
        采样上限 256x256：图标一般远小于此，大图也只需这个规模就能反映主色
        */
        public static Color SampleDominantColor(Texture2D icon)
        {
            if (icon == null || icon.IsDisposed || icon.Width <= 1 || icon.Height <= 1)
                return Color.Gray;

            int sampleWidth = Math.Min(icon.Width, 256);
            int sampleHeight = Math.Min(icon.Height, 256);

            Color[] pixels = new Color[sampleWidth * sampleHeight];
            try
            {
                icon.GetData(
                    0,
                    new Rectangle(0, 0, sampleWidth, sampleHeight),
                    pixels,
                    0,
                    pixels.Length
                );
            }
            catch (Exception ex)
            {
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

            var colorFrequency = new Dictionary<int, int>();
            int highestFrequency = 0;
            Color mostCommonColor = Color.Gray;

            foreach (ref Color pixel in pixels.AsSpan())
            {
                // 跳过半透明像素：图标阴影与描边多为低 alpha，采进来会让标题发灰
                if (pixel.A < 128)
                    continue;
                // 跳过接近纯黑与纯白：R+G+B 范围为 0..765，两端颜色作为标题色看不清
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

            return highestFrequency > 0 ? mostCommonColor : Color.Gray;
        }
    }
}
