#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BTitles;
using BTitlesLocalizationPatch.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch
{
    /*
    BTitles 增强
    1. 自动注册未收录群系 — 扫描各模组 ModBiome 并补入 BTitles 字典
    2. GetActualTitleName 增强 — 5 步回退链（源模组本地化→BTitles→补丁补充→自定义→原始标题）
    3. 自动配色 — 扫描时取 BackgroundColor，运行时 Hook 图标采样补色
    */
    public class BTitlesLocalizationPatch : Mod
    {
        private volatile bool _loaded;

        // 配置首次 OnChanged 早于 Mod.Load，那时反射字段还是 null，扫描与卸载都需据此早退
        // 用静态标志而非 ModContent.GetInstance：后者在加载早期可能返回 null
        internal static volatile bool LoadCompleted;

        // volatile: 后台线程（PostSetupContent）读 Load（主线程）写入的最新值
        private static volatile FieldInfo? _instanceField;
        private static volatile FieldInfo? _biomeDictField;
        private static volatile FieldInfo? _checkFuncsField;
        internal static FieldInfo? InstanceField => _instanceField;
        internal static FieldInfo? BiomeDictField => _biomeDictField;
        internal static FieldInfo? CheckFuncsField => _checkFuncsField;

        // 刷新当前标题用：BTitles 只在群系切换时重算颜色，改完配置得自己推一把
        // 这两个取不到只影响「即时刷新」，不影响功能本身，故不参与 ValidateReflectionTargets
        private static volatile FieldInfo? _uiField;
        private static volatile MethodInfo? _setupVisualsMethod;
        private static int _refreshWarned;

        // volatile: BiomeNameHook（UI 线程）读，Load（主线程）写
        internal static volatile bool HookInstalled;

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
            ValidateReflectionTargets();
            GetActualTitleNameHookInstaller.Install(this);

            // 放在最后：此刻反射字段与日志器都已就绪，配置变更回调可以安全执行
            LoadCompleted = true;
        }

        // DebugLog InfoWriter/WarnWriter/ErrorWriter 对应 tModLoader Logger 级别
        private void InstallDebugLogger()
        {
            DebugLog.InfoWriter = s => Logger.Info(s!);
            DebugLog.WarnWriter = s => Logger.Warn(s!);
            DebugLog.ErrorWriter = s => Logger.Error(s!);
        }

        /*
        只缓存字段引用，是否取得到交给 ValidateReflectionTargets
        分开两步是为了让「取字段」与「判断能不能用」各自只有一份实现
        */
        private void CacheReflectionFields()
        {
            _instanceField = typeof(BiomeTitlesMod).GetField(
                "Instance",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
            );

            _biomeDictField = typeof(BiomeTitlesMod).GetField(
                "BiomeDictionary",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );

            _checkFuncsField = typeof(BiomeTitlesMod).GetField(
                "BiomeCheckFunctions",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );

            _uiField = typeof(BiomeTitlesMod).GetField(
                "_biomeTitlesUi",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );

            _setupVisualsMethod = typeof(BiomeTitlesUI).GetMethod(
                "SetupTitleVisuals",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
        }

        /*
        校验扫描要用的三个反射目标在不在
        放在 Load 里而不是等扫描入口去抛：配置回调触发的扫描跑在主线程 UI 事件里，
        早已脱离 ModLoader.Load 的 try，那时抛异常不会被归属模组、也不会禁用本模组，
        只会变成主循环里一声没人认领的裸异常。加载期抛才是「拒绝加载」该有的样子
        */
        private static void ValidateReflectionTargets()
        {
            if (
                _instanceField == null
                || _biomeDictField == null
                || _checkFuncsField == null
            )
                throw new BTitlesIncompatibleException("ScanTargetMissing");
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
                Scan.BiomeRegistrar.Run(this);
            }
            else if (config.EnableAutoStyling)
            {
                // 扫描关闭但配色开着：进世界仍要对齐一次策略（改了来源/固定颜色后重进世界生效）
                ApplyConfigChangeStyle(true);
            }
        }

        // 由 ModConfig.OnChanged 调用；主菜单时 BTitles Instance 为 null，操作会静默跳过
        // enabled 参数仅控制 hook 层样式开关缓存；条目重配色读取当前完整策略（来源 + 固定颜色）
        internal static void ApplyConfigChangeStyle(bool enabled)
        {
            // 立即同步 Hook 的样式开关缓存，避免 3 秒冷却期内采样逻辑读到旧值
            BiomeNameHook.UpdateStylingCache(enabled);

            /*
            配色表的快照必须在这里失效：玩家刚存完表，下一次 Hook 就要读到新值
            配色开关也走这条路径（表受它门控），一并失效不会多花什么
            */
            Scan.BiomeStyleHelper.InvalidateMappingSnapshot();

            // 配色不依赖标题 Hook，早退会让已上色的条目撤不掉；Load 之前反射字段还是 null，见 LoadCompleted
            if (!LoadCompleted)
                return;

            if (
                Scan.BiomeRegistrar.ScannedBiomes != null
                && Scan.BiomeRegistrar.ScannedBiomes.Count > 0
            )
            {
                var instance = InstanceField?.GetValue(null);
                var biomeDict =
                    instance == null
                        ? null
                        : BiomeDictField?.GetValue(instance) as Dictionary<string, BiomeEntry>;

                if (biomeDict != null)
                    ApplyStyleToScannedBiomes(enabled, biomeDict);
            }

            /*
            必须在重配色之后才刷新
            配色表覆盖的是「当前显示名」，其中也可能有 BTitles 内置条目（如地狱）——
            那些不在扫描结果里，只能靠这次刷新读到新颜色；
            放在重配色之前刷，读到的是旧值，等于白刷
            */
            RefreshCurrentTitle();
        }

        /*
        让 BTitles 重算一次当前标题的显示属性
        BTitles 只在群系切换（CheckNewBiome 里 newBiome != _currentBiome）时才调 SetupTitleVisuals，
        配置变更不会触发 —— 不推这一把，改完配色表要等走出再走回群系才看得到效果
        方法名可能随上游版本变化：取不到就退化成「下次切换生效」，只告警一次，不影响功能
        */
        private static void RefreshCurrentTitle()
        {
            if (!Program.IsMainThread)
                return;

            // 反射字段取不到才算「不可用」：这是上游改了结构，只告警一次
            if (_uiField == null || _setupVisualsMethod == null)
            {
                if (Interlocked.Exchange(ref _refreshWarned, 1) == 0)
                {
                    Diagnostics.DebugLog.Warn(
                        Language.GetTextValue(
                            $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.RefreshUnavailable"
                        )
                    );
                }
                return;
            }

            // 主菜单或 BTitles 尚未建界面：没有东西可刷，这不是异常状态，静默跳过
            var instance = InstanceField?.GetValue(null);
            if (instance == null)
                return;

            var ui = _uiField.GetValue(instance);
            if (ui == null)
                return;

            try
            {
                _setupVisualsMethod.Invoke(ui, null);
            }
            catch (Exception ex)
            {
                // 即时刷新失败不该把配置保存变成一次报错：颜色下次切群系照样会生效
                Diagnostics.DebugLog.Warn(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.RefreshFailed",
                        ex.Message
                    )
                );
            }
        }

        private static void ApplyStyleToScannedBiomes(
            bool enabled,
            Dictionary<string, BiomeEntry> biomeDict
        )
        {
            int styledCount = 0;
            // 取一次策略与固定颜色：本循环内一致，逐项重读配置只会放大竞态
            (var colorSource, Color fixedColor) = Scan.BiomeStyleHelper.GetStylingSettings();
            foreach (var (dictKey, modBiome, isNew) in Scan.BiomeRegistrar.ScannedBiomes!)
            {
                if (!biomeDict.TryGetValue(dictKey, out var entry))
                    continue;

                if (modBiome == null)
                    continue;

                /*
                策略色即将重写这个条目的颜色：配色表记的「原色」随之过时
                丢掉记录，让下一次命中重新按新策略色记基准，否则撤销命中会还原成旧策略的颜色
                */
                BiomeNameHook.ForgetMappedColor(entry);

                if (enabled)
                {
                    entry.TitleColor = Scan.BiomeStyleHelper.GetTitleColors(
                        modBiome,
                        colorSource,
                        fixedColor,
                        out bool needsSampling
                    );
                    // 采样策略下总是登记待补色队列，等 Hook 用图标众数补上
                    if (needsSampling)
                        Scan.BiomeRegistrar.PendingColorKeys[entry] = 0;
                    else
                        Scan.BiomeRegistrar.PendingColorKeys.TryRemove(entry, out _);
                }
                else
                {
                    // 关配色时还原成白色而不是 default（透明），否则标题会整个看不见
                    entry.TitleColor = Color.White;
                    Scan.BiomeRegistrar.PendingColorKeys.TryRemove(entry, out _);
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

        /*
        扫描类配置回调的公共前置：三个入口原先各抄一遍同一段
        「确认已加载 → 丢翻译缓存 → 取 mod 实例 → 取不到就什么都不做」
        抄三遍的坏处不是难看，是将来加一条前置（比如再清一份缓存）必定漏掉其中一处
        配色回调不走这里：它不需要丢翻译缓存，见 ApplyConfigChangeStyle
        */
        private static BTitlesLocalizationPatch? BeginScanConfigChange()
        {
            // 扫描与检测函数注册都不依赖标题 Hook，早退会把注册过的群系留在字典里撤不掉
            if (!LoadCompleted)
                return null;

            BiomeNameHook.TranslationCache.Clear();
            return ModContent.GetInstance<BTitlesLocalizationPatch>();
        }

        internal static void ApplyConfigChangeScan(bool enabled)
        {
            var mod = BeginScanConfigChange();
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
                    Scan.BiomeRegistrar.Run(mod);
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

        /*
        OnlyFillMissingBiomes 改了要重扫：它是扫描期判定，不重扫就会把新旧口径混进同一份结果
        必须先撤再扫 —— BiomeRegistrar.Run 有「已有生效结果就不再扫」的早退，
        不清掉 ScannedBiomes 就永远扫不成第二次；撤净顺带把扫描令牌放回空闲态
        */
        internal static void ApplyConfigChangeOnlyFillMissing()
        {
            var mod = BeginScanConfigChange();
            if (mod == null)
                return;

            // 扫描没开就没有结果可撤，也没必要平白起一次扫描
            var config = ModContent.GetInstance<BTitlesConfig>();
            if (config == null || !config.EnableScan)
                return;

            UnregisterScannedBiomes(mod);

            mod.Logger.Info(
                Language.GetTextValue(
                    $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.RescanOnOnlyFillMissing"
                )
            );

            Scan.BiomeRegistrar.Run(mod);
        }

        // 卸载本次扫描的成果：移除新增条目、还原被覆盖的已有条目，并清掉检测函数与扫描状态
        // mod 为 null 表示卸载途中实例已不可靠，不走 Mod.Logger
        // restoreMappedColors 只有 Unload 传 true：关扫描时配色表仍由 Hook 生效，不该顺手还原
        internal static void UnregisterScannedBiomes(
            Mod? mod,
            bool restoreMappedColors = false
        )
        {
            /*
            BTitles 的字典是本补丁的 detour 读取的共享状态，主线程每帧都可能遍历
            扫描路径的字典变动（PostSetupContent / 配置回调的提交回调）早已排队到主线程，
            而这里一直漏了：Unload 由 ModLoader 在线程池线程调用（Mod.Internals.cs:34），
            BiomeTitlesUI 若此刻正在遍历字典会直接读到撕裂状态
            所以照抄 MusicLoader.CloseModStreams 的模式（MusicLoader.cs:210-211）：
            非主线程时用 RunOnMainThread 阻塞跳到主线程再整批处理；已在本线程就直接走
            */
            if (!Program.IsMainThread)
            {
                try
                {
                    Main.RunOnMainThread(() => UnregisterScannedBiomes(mod, restoreMappedColors))
                        .GetAwaiter()
                        .GetResult();
                    return;
                }
                catch (Exception ex)
                {
                    // Unload 不得抛：吞掉异常只留告警
                    // 扫描记录仍要归位，否则残留的 ScannedBiomes 引用会锁死下次扫描令牌
                    Diagnostics.DebugLog.Warn(
                        Language.GetTextValue(
                            $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.UnloadHopFailed",
                            ex.Message
                        )
                    );
                    Scan.BiomeRegistrar.ClearScannedBiomes();
                    return;
                }
            }

            /*
            配色表改过的颜色也在这里还回去：它写的是 BTitles 字典里条目的 TitleColor，
            与下面的快照还原是同一份共享状态，必须在主线程做
            放在快照还原之前：快照里存的是扫描前的基准色，先还原映射色再覆盖，
            两者都写同一个字段，顺序反了会把刚还原的基准值再抹掉
            */
            if (restoreMappedColors)
                BiomeNameHook.RestoreMappedColors();

            var instance = InstanceField?.GetValue(null);
            var biomeDict =
                instance == null
                    ? null
                    : BiomeDictField?.GetValue(instance) as Dictionary<string, BiomeEntry>;

            int removed = 0;
            int restored = 0;

            var scanned = Scan.BiomeRegistrar.ScannedBiomes;
            if (biomeDict != null && scanned != null)
            {
                // 移除本次扫描新增的字典条目
                foreach (var (dictKey, _, isNew) in scanned)
                {
                    if (isNew && biomeDict.Remove(dictKey))
                        removed++;
                }
            }

            // 还原被扫描改写的已有条目字段，否则关掉扫描仍会显示补丁标题
            var snapshots = Scan.BiomeRegistrar.PreScanValues;
            if (biomeDict != null && snapshots != null)
            {
                foreach (var (dictKey, snapshot) in snapshots)
                {
                    if (!biomeDict.TryGetValue(dictKey, out var entry))
                        continue;
                    entry.Title = snapshot.Title ?? entry.Title;
                    entry.LocalizationScope = snapshot.LocalizationScope;
                    entry.TitleColor = snapshot.TitleColor;
                    entry.Icon = snapshot.Icon;
                    // 刚把颜色还原成扫描前的基准值：配色表记的「原色」已过时，丢掉
                    BiomeNameHook.ForgetMappedColor(entry);
                    restored++;
                }
            }

            if (removed > 0 && mod != null)
                mod.Logger.Info(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.UnregisteredBiomes",
                        removed
                    )
                );

            if (restored > 0)
                Diagnostics.DebugLog.Info(
                    Language.GetTextValue(
                        $"Mods.{nameof(BTitlesLocalizationPatch)}.Logs.RestoredBiomes",
                        restored
                    )
                );

            /*
            检测函数与扫描状态无条件清干净，早退路径不能把它们留在原地
            与本轮条目撤除合并进同一个阻塞主线程跳转：这里的执行上下文由上面的
            RunOnMainThread 保证是主线程；若走的是线程池路径（hop 失败早退）则不在此处清
            */
            Scan.BiomeRegistrar.CleanupBlockingOnMainThread();
            Scan.BiomeRegistrar.ClearScannedBiomes();
        }

        public override void Unload()
        {
            BiomeNameHook.TranslationCache.Clear();
            BiomeNameHook.ResetStylingCache();
            BiomeNameHook.ResetCultureCache();
            BiomeNameHook.ResetEnUsMapCache();
            // 配色表快照指向的是配置对象里的字符串，卸载后配置会被回收，留着就是悬挂引用
            Scan.BiomeStyleHelper.InvalidateMappingSnapshot();

            GetActualTitleNameHookInstaller.Uninstall(this);

            // 必须撤字典：BTitles 不随本模组卸载，只清记录会让它的字典留下带不走的孤儿条目
            // 传 null：卸载途中不再走 Mod.Logger
            // 配色表的还原也一并交给它：那同样是写 BTitles 字典，得在主线程做
            UnregisterScannedBiomes(null, restoreMappedColors: true);

            _instanceField = null;
            _biomeDictField = null;
            _checkFuncsField = null;
            _uiField = null;
            _setupVisualsMethod = null;
            // 告警只报一次的标志跟着归位：重载后上游若还是取不到，应该重新提醒一次
            Interlocked.Exchange(ref _refreshWarned, 0);
            _loaded = false;
            // 与 _loaded 同步归位：卸载后若再收到配置回调，不能在字段已清空的状态下执行
            LoadCompleted = false;

            // 清理调试日志写入器，防止卸载后残留引用
            Diagnostics.DebugLog.InfoWriter = s => { };
            Diagnostics.DebugLog.WarnWriter = s => { };
            Diagnostics.DebugLog.ErrorWriter = s => { };

            // 重置调试日志缓存，防止热重载后 3 秒内读到旧缓存值
            Diagnostics.DebugLog.ResetCache();
        }
    }
}
