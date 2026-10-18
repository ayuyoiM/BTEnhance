#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.Xna.Framework;
using Terraria.ModLoader.Config;

namespace BTitlesLocalizationPatch
{
    /*
    标题颜色策略
    - BackgroundColor：默认。优先用模组声明的 BackgroundColor，没有则白色并登记图标采样（旧行为）
    - IconSampling：总是从图标采代表性颜色（主线程），初始白色占位
    - Fixed：始终用用户指定的 FixedTitleColor
    枚举成员的中文标签走 tML 自动注册键：Mods.BTitlesLocalizationPatch.Configs.TitleColorSource.{成员}.Label
    */
    public enum TitleColorSource
    {
        BackgroundColor,
        IconSampling,
        Fixed,
    }

    /*
    逐群系标题配色
    按「玩家看得见的标题名」匹配，而不是模组内部的群系键：
    玩家填的是自己屏幕上看到的那行字，眼见为实，零成本配置
    命中后覆盖 PreferredColorSource 给出的策略色
    */
    public class BiomeColorMapping
    {
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeColorMapping.Title.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeColorMapping.Title.Tooltip")]
        public string Title { get; set; } = "";

        /*
        默认不透明白而不是 (0,0,0,0)：ColorNoAlpha 只是把 A 滑条藏起来，
        tML 不会替我们写 255（ColorElement.cs:136 只做 skip.Add(A)），
        于是玩家拖完 RGB 得到的仍是 alpha=0，标题会整个看不见
        给一个不透明的默认值，未填与已填才有明确分界
        */
        [DefaultValue(typeof(Color), "255,255,255,255")]
        [ColorNoAlpha]
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeColorMapping.Color.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeColorMapping.Color.Tooltip")]
        public Color Color { get; set; }
    }

    public class BTitlesConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ClientSide;

        [Header("Scan")]
        [DefaultValue(true)]
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.EnableScan.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.EnableScan.Tooltip")]
        public bool EnableScan { get; set; }

        // 开启后扫描只补漏：BTitles 字典里已存在的条目完全不动（不覆写标题/作用域、不配色、不做快照、不排队写入），
        // 只统计进 ScanResult.Skipped
        // 属于扫描期判定，改口径后必须重扫，见 OnChanged
        [DefaultValue(true)]
        [LabelKey(
            "$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.OnlyFillMissingBiomes.Label"
        )]
        [TooltipKey(
            "$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.OnlyFillMissingBiomes.Tooltip"
        )]
        public bool OnlyFillMissingBiomes { get; set; }

        private bool _lastAutoStyling;
        private bool _lastScan;
        private TitleColorSource _lastColorSource;
        private Color _lastFixedColor;
        private bool _lastOnlyFillMissing;

        // 空表的签名是 0，初值也用 0：没动过表时不该平白触发一次全量重配色
        private int _lastMappingsSignature;

        /*
        tML 在注册期就会调一次 OnChanged（ConfigManager.Add，早于 Mod.Load，LoadCompleted 早退挡住），
        但菜单保存（OnChangedAll）与游戏内保存触发时模组已加载，若 _last* 还停在 C# 默认值
        （false / 透明黑），首轮必然把「默认开着」误判成「刚被打开」，平白跑一次扫描与全量重配色。
        在 OnLoaded（Load 之后、任何有效 OnChanged 之前）把字段对齐到实际加载值，
        兼容旧存档里非默认的取值，不硬编码 true
        */
        public override void OnLoaded()
        {
            _lastAutoStyling = EnableAutoStyling;
            _lastScan = EnableScan;
            _lastColorSource = PreferredColorSource;
            _lastFixedColor = FixedTitleColor;
            _lastOnlyFillMissing = OnlyFillMissingBiomes;
            _lastMappingsSignature = ComputeMappingsSignature(TitleColorMappings);
        }

        // 自动配色总开关：控制策略色的批量上色与图标采样；
        // 逐群系手工配色表也在它之下，关掉后整档不生效
        [Header("Styling")]
        [DefaultValue(true)]
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.EnableAutoStyling.Label")]
        [TooltipKey(
            "$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.EnableAutoStyling.Tooltip"
        )]
        public bool EnableAutoStyling { get; set; }

        // 标题配色策略，见 TitleColorSource 注释
        [DefaultValue(TitleColorSource.BackgroundColor)]
        [LabelKey(
            "$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.PreferredColorSource.Label"
        )]
        [TooltipKey(
            "$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.PreferredColorSource.Tooltip"
        )]
        public TitleColorSource PreferredColorSource { get; set; }

        // Fixed 策略用的固定颜色；不参与 alpha（无边框/背景纯色）
        [DefaultValue(typeof(Color), "255,255,255,255")]
        [ColorNoAlpha]
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.FixedTitleColor.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.FixedTitleColor.Tooltip")]
        public Color FixedTitleColor { get; set; }

        /*
        逐群系手工配色表：按最终显示名匹配，命中优先于 PreferredColorSource
        这里不设默认值：空表就是「谁都不用」，与玩家手动清空表格的行为一致
        */
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.TitleColorMappings.Label")]
        [TooltipKey(
            "$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.TitleColorMappings.Tooltip"
        )]
        public List<BiomeColorMapping> TitleColorMappings { get; set; } =
            new List<BiomeColorMapping>();

        [Header("Debug")]
        [DefaultValue(false)]
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.EnableDebugLog.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.EnableDebugLog.Tooltip")]
        public bool EnableDebugLog { get; set; }

        /*
        给配色表算一个廉价签名
        Save 一定触发 OnChanged，但拿不到「哪一项变了」，没法逐项比对；
        用「条数 + 每条 Title/Color」的哈希就能看出玩家是否动过这张表
        签名只用于判断「要不要重刷颜色」，不需要密码学强度
        */
        private static int ComputeMappingsSignature(List<BiomeColorMapping>? mappings)
        {
            if (mappings == null || mappings.Count == 0)
                return 0;

            var hash = new HashCode();
            hash.Add(mappings.Count);
            foreach (var mapping in mappings)
            {
                if (mapping == null)
                    continue;
                hash.Add(mapping.Title);
                hash.Add(mapping.Color);
            }
            return hash.ToHashCode();
        }

        // tModLoader 确认保存后通知，直接执行配置变更
        // 主菜单时 BTitles 未加载，操作会静默跳过；进世界后 PostSetupContent 处理初始状态
        public override void OnChanged()
        {
            /*
            先全部读出来再决定动作
            逐项 if 直接调用的写法在「开关与策略同时变」时会连跑两遍重配色与刷新，
            第二遍纯属白做；合成一次调用既省事也避免两遍之间读到中间状态
            */
            bool styleCurrent = EnableAutoStyling;
            bool scanCurrent = EnableScan;
            TitleColorSource colorSourceCurrent = PreferredColorSource;
            Color fixedColorCurrent = FixedTitleColor;
            bool onlyFillCurrent = OnlyFillMissingBiomes;
            int mappingsSignature = ComputeMappingsSignature(TitleColorMappings);

            bool styleToggleChanged = styleCurrent != _lastAutoStyling;
            bool styleChanged =
                colorSourceCurrent != _lastColorSource
                || fixedColorCurrent != _lastFixedColor
                || mappingsSignature != _lastMappingsSignature;

            if (styleToggleChanged || styleChanged)
                BTitlesLocalizationPatch.ApplyConfigChangeStyle(styleCurrent);

            _lastAutoStyling = styleCurrent;
            _lastColorSource = colorSourceCurrent;
            _lastFixedColor = fixedColorCurrent;
            _lastMappingsSignature = mappingsSignature;

            bool scanChanged = scanCurrent != _lastScan;
            bool onlyFillChanged = onlyFillCurrent != _lastOnlyFillMissing;

            /*
            同一轮 Save 只能扫一次
            两个开关最终都落到「扫描要不要重来」上，逐项 if 会连扫两遍：
            先 ApplyConfigChangeScan 扫一遍，再 ApplyConfigChangeOnlyFillMissing 撤掉重扫一遍
            - 扫描开关本身变了：交给 ApplyConfigChangeScan。它会重新扫，而
              BiomeRegistrar.Run 里的 IsOnlyFillMissing() 是扫描开始时现读配置，
              所以同一轮里改的「仅补漏」新值天然被这次扫描带上，不需要再扫一遍
            - 扫描开关没变、只改了「仅补漏」：它是扫描期判定，必须「先撤再扫」，
              不清掉 ScannedBiomes 就永远扫不成第二次
            */
            if (scanChanged)
                BTitlesLocalizationPatch.ApplyConfigChangeScan(scanCurrent);
            else if (onlyFillChanged && scanCurrent)
                BTitlesLocalizationPatch.ApplyConfigChangeOnlyFillMissing();

            _lastScan = scanCurrent;
            _lastOnlyFillMissing = onlyFillCurrent;
        }
    }
}
