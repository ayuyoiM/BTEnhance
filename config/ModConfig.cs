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
    - BackgroundColor：默认。只用模组声明的 BackgroundColor，没有则白色，不采样
    - BackgroundColorSampling：背景色优先；没有背景色或背景色是白色时使用图标采样
    - IconSampling：总是从图标采代表性颜色（主线程），初始白色占位
    - Fixed：始终用用户指定的 FixedTitleColor
    枚举成员的中文标签走 tML 自动注册键：Mods.BTitlesLocalizationPatch.Configs.TitleColorSource.{成员}.Label
    */
    public enum TitleColorSource
    {
        BackgroundColor,
        BackgroundColorSampling,
        IconSampling,
        Fixed,
    }

    /*
    逐群系标题配色
    按「玩家看得见的标题名」匹配，而不是模组内部的群系键：
    玩家填的是自己屏幕上看到的那行字，眼见为实，零成本配置
    命中后覆盖 PreferredColorSource 给出的策略色
    */
    // 列表项默认折叠：配色表往往几十条，全展开会把整页撑长，找一条得滚半天
    // tML 的 ObjectElement 对列表元素读的是「元素类型」上的 Expand 特性（ObjectElement.cs:131-136）
    [Expand(false)]
    public class BiomeColorMapping
    {
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeColorMapping.Title.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeColorMapping.Title.Tooltip")]
        public string Title { get; set; } = "";

        /*
        A 通道完全放开：滑条可见，填什么就用什么（半透明标题、淡出效果都交给玩家）
        默认值仍是不透明白，未填与已填靠 (0,0,0,0) 区分——全零才算「还没填」，
        只把 alpha 拖到 0 的彩色条目是玩家有意为之，照用
        */
        [DefaultValue(typeof(Color), "255,255,255,255")]
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeColorMapping.Color.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeColorMapping.Color.Tooltip")]
        public Color Color { get; set; }

        /*
        列表折叠时显示「序号: 标题文字」，而不是光秃秃的「序号:」
        tML 的 ObjectElement.OnBind 只在元素类型重写了 ToString 时才把值拼上去
        （ObjectElement.cs:52-61），没重写就只剩序号，看不出这条配的是哪个群系
        未填标题时留空，让序号后面干干净净
        */
        public override string ToString() => Title ?? "";
    }

    /*
    扫描名单模式
    - Disabled：名单不生效（表里的内容原样留着，不影响扫描）
    - Blacklist：表按黑名单解释，命中即跳过
    - Whitelist：表按白名单解释，未命中即跳过；表为空时全放行
    只有一张表，模式决定它怎么被读——「既在黑名单又在白名单」这种自相矛盾的配置不存在
    枚举成员的中文标签走 tML 自动注册键：Mods.BTitlesLocalizationPatch.Configs.ScanFilterMode.{成员}.Label
    */
    public enum ScanFilterMode
    {
        Disabled,
        Blacklist,
        Whitelist,
    }

    /*
    扫描名单条目
    只填一个名字，折叠时直接显示它（同 BiomeColorMapping 的理由：不重写 ToString 就只剩「1: 」）
    [Expand(false)] 打在类上，列表项默认折叠：名单往往几十条，全展开会把整页撑长。
    单字段条目折叠后要多点一下才能编辑，这个取舍与配色表保持一致
    */
    [Expand(false)]
    public class BiomeFilterEntry
    {
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeFilterEntry.Name.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BiomeFilterEntry.Name.Tooltip")]
        public string Name { get; set; } = "";

        public override string ToString() => Name ?? "";
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

        /*
        名单模式：决定下面这张表按什么方式用
        - Disabled：表不生效（留着内容也不影响扫描）
        - Blacklist：表里是黑名单，命中即跳过
        - Whitelist：表里是白名单，未命中即跳过；表为空时全放行
        一张表 + 一个模式，切换模式不需要搬数据；两张表同时生效的「冲突时听谁的」问题也就不存在
        属于扫描期判定，改完必须重扫，见 OnChanged
        */
        [DefaultValue(ScanFilterMode.Disabled)]
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.FilterMode.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.FilterMode.Tooltip")]
        public ScanFilterMode FilterMode { get; set; }

        // 扫描名单：按 FilterMode 解释成黑名单或白名单
        [LabelKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.ScanFilterList.Label")]
        [TooltipKey("$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.ScanFilterList.Tooltip")]
        public List<BiomeFilterEntry> ScanFilterList { get; set; } = new List<BiomeFilterEntry>();

        private bool _lastAutoStyling;
        private bool _lastScan;
        private TitleColorSource _lastColorSource;
        private Color _lastFixedColor;
        private bool _lastOnlyFillMissing;
        private ScanFilterMode _lastFilterMode;

        // 两张表的签名初值都用 0：空表签名也是 0，没动过表时不该平白触发一次重扫 / 全量重配色
        private int _lastFilterSignature;
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
            _lastFilterMode = FilterMode;
            _lastFilterSignature = ComputeFilterSignature(ScanFilterList);
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
        [DefaultValue(TitleColorSource.BackgroundColorSampling)]
        [LabelKey(
            "$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.PreferredColorSource.Label"
        )]
        [TooltipKey(
            "$Mods.BTitlesLocalizationPatch.Configs.BTitlesConfig.PreferredColorSource.Tooltip"
        )]
        public TitleColorSource PreferredColorSource { get; set; }

        // Fixed 策略用的固定颜色；alpha 同样放开，由玩家决定不透明度
        [DefaultValue(typeof(Color), "255,255,255,255")]
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

        /*
        扫描名单的签名，口径同 ComputeMappingsSignature：
        Save 一定触发 OnChanged，但拿不到「哪一项变了」，用「条数 + 每项名字」的哈希判断
        只用于决定「要不要重扫」，不需要密码学强度
        */
        private static int ComputeFilterSignature(List<BiomeFilterEntry>? entries)
        {
            if (entries == null || entries.Count == 0)
                return 0;

            var hash = new HashCode();
            hash.Add(entries.Count);
            foreach (var entry in entries)
            {
                if (entry == null)
                    continue;
                hash.Add(entry.Name);
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
            ScanFilterMode filterModeCurrent = FilterMode;
            int mappingsSignature = ComputeMappingsSignature(TitleColorMappings);
            int filterSignature = ComputeFilterSignature(ScanFilterList);

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
            模式变了必然要重扫；只改表内容时，模式关着就没必要白扫一遍——
            名单在 Disabled 下压根不参与判定
            */
            bool filterChanged =
                filterModeCurrent != _lastFilterMode
                || (
                    filterModeCurrent != ScanFilterMode.Disabled
                    && filterSignature != _lastFilterSignature
                );

            /*
            同一轮 Save 只能扫一次
            扫描开关、仅补漏、扫描名单最终都落到「扫描要不要重来」上，逐项 if 会连扫多遍：
            先 ApplyConfigChangeScan 扫一遍，再撤掉重扫一遍
            - 扫描开关本身变了：交给 ApplyConfigChangeScan。它会重新扫，而
              BiomeRegistrar.Run 里的 IsOnlyFillMissing() / BiomeFilter.Read() 都是扫描开始时现读配置，
              所以同一轮里改的「仅补漏」和名单天然被这次扫描带上，不需要再扫一遍
            - 扫描开关没变、只改了「仅补漏」或名单：它们是扫描期判定，必须「先撤再扫」，
              不清掉 ScannedBiomes 就永远扫不成第二次
            */
            if (scanChanged)
                BTitlesLocalizationPatch.ApplyConfigChangeScan(scanCurrent);
            else if ((onlyFillChanged || filterChanged) && scanCurrent)
                BTitlesLocalizationPatch.ApplyConfigChangeRescan(
                    onlyFillChanged ? "RescanOnOnlyFillMissing" : "RescanOnFilterChanged"
                );

            _lastScan = scanCurrent;
            _lastOnlyFillMissing = onlyFillCurrent;
            _lastFilterMode = filterModeCurrent;
            _lastFilterSignature = filterSignature;
        }
    }
}
