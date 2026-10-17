#nullable enable
using System.ComponentModel;
using Microsoft.Xna.Framework;
using Terraria.ModLoader.Config;

namespace BTitlesEnhance
{
    /*
    标题颜色策略
    - BackgroundColor：默认。优先用模组声明的 BackgroundColor，没有则白色并登记图标采样（旧行为）
    - IconSampling：总是从图标采主色（主线程），初始白色占位
    - Fixed：始终用用户指定的 FixedTitleColor
    枚举成员的中文标签走 tML 自动注册键：Mods.BTitlesEnhance.Configs.TitleColorSource.{成员}.Label
    */
    public enum TitleColorSource
    {
        BackgroundColor,
        IconSampling,
        Fixed,
    }

    public class BTitlesConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ClientSide;

        [Header("Scan")]
        [DefaultValue(false)]
        [LabelKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.EnableScan.Label")]
        [TooltipKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.EnableScan.Tooltip")]
        public bool EnableScan { get; set; }

        [DefaultValue(false)]
        [LabelKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.EnableAutoStyling.Label")]
        [TooltipKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.EnableAutoStyling.Tooltip")]
        public bool EnableAutoStyling { get; set; }

        [Header("Debug")]
        [DefaultValue(false)]
        [LabelKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.EnableDebugLog.Label")]
        [TooltipKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.EnableDebugLog.Tooltip")]
        public bool EnableDebugLog { get; set; }

        private bool _lastAutoStyling;
        private bool _lastScan;
        private TitleColorSource _lastColorSource;
        private Color _lastFixedColor;
        private bool _lastOnlyFillMissing;

        // 标题配色策略，见 TitleColorSource 注释
        [Header("Styling")]
        [DefaultValue(TitleColorSource.BackgroundColor)]
        [LabelKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.PreferredColorSource.Label")]
        [TooltipKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.PreferredColorSource.Tooltip")]
        public TitleColorSource PreferredColorSource { get; set; }

        // Fixed 策略用的固定颜色；不参与 alpha（无边框/背景纯色）
        [DefaultValue(typeof(Color), "255,255,255,255")]
        [ColorNoAlpha]
        [LabelKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.FixedTitleColor.Label")]
        [TooltipKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.FixedTitleColor.Tooltip")]
        public Color FixedTitleColor { get; set; }

        // 开启后扫描只补漏：BTitles 字典里已存在的条目完全不动（不覆写标题/作用域、不配色、不做快照、不排队写入），
        // 只统计进 ScanResult.Skipped
        [DefaultValue(false)]
        [LabelKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.OnlyFillMissingBiomes.Label")]
        [TooltipKey("$Mods.BTitlesEnhance.Configs.BTitlesConfig.OnlyFillMissingBiomes.Tooltip")]
        public bool OnlyFillMissingBiomes { get; set; }

        // tModLoader 确认保存后通知，直接执行配置变更
        // 主菜单时 BTitles 未加载，操作会静默跳过；进世界后 PostSetupContent 处理初始状态
        public override void OnChanged()
        {
            bool styleCurrent = EnableAutoStyling;
            if (styleCurrent != _lastAutoStyling)
                BTitlesEnhance.ApplyConfigChangeStyle(styleCurrent);
            _lastAutoStyling = styleCurrent;

            bool scanCurrent = EnableScan;
            if (scanCurrent != _lastScan)
                BTitlesEnhance.ApplyConfigChangeScan(scanCurrent);
            _lastScan = scanCurrent;

            TitleColorSource colorSourceCurrent = PreferredColorSource;
            Color fixedColorCurrent = FixedTitleColor;
            bool onlyFillCurrent = OnlyFillMissingBiomes;
            bool styleChanged = colorSourceCurrent != _lastColorSource || fixedColorCurrent != _lastFixedColor;
            if (styleChanged)
                BTitlesEnhance.ApplyConfigChangeStyle(EnableAutoStyling);
            _lastColorSource = colorSourceCurrent;
            _lastFixedColor = fixedColorCurrent;

            // OnlyFillMissingBiomes 只在下一次扫描时读取生效，无需即时动作
            _lastOnlyFillMissing = onlyFillCurrent;
        }
    }
}
