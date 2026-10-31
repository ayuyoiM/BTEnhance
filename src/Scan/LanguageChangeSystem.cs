#nullable enable
using System;
using System.Collections.Generic;
using BTitles;
using BTitlesLocalizationPatch;
using BTitlesLocalizationPatch.Diagnostics;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch.Scan
{
    /*
    语言切换的唯一接收点

    BiomeEntry.Title 是扫描那一刻按当时的语言烤进去的字符串（BiomeRegistrar 里
    modBiome.DisplayName.Value），BTitles 的回退链最后一步（BiomeTitlesUI.cs:511
    `return biomeEntry.Title`）直接吐它。所以切到俄文而源模组没有 ru 译文时，
    漏出来的是上次扫描时的中文——光清翻译缓存没用，得按新语言把 Title 重算一遍。

    用 ModSystem.OnLocalizationsLoaded 而不是散在各处判 Language.ActiveCulture：
    tML 在 LanguageManager.ReloadLanguage 末尾统一分发（LanguageManager.cs.patch:72），
    切语言、热重载语言文件、加载期灌值都会走到，且此刻 RecalculateBoundTextValues
    已经跑过（同一方法内、在分发之前），DisplayName.Value 已是新语言的值
    */
    internal sealed class LanguageChangeSystem : ModSystem
    {
        public override void OnLocalizationsLoaded()
        {
            if (Main.dedServ)
                return;

            // 翻译链结果全部按语言缓存：旧语言的结论一条都不能留
            BiomeNameHook.TranslationCache.Clear();
            BiomeNameHook.ResetCultureCache();

            /*
            热重载语言文件走 LocalizationLoader 的文件监视器，不一定在主线程；
            改 BTitles 字典和反射刷新标题都必须在主线程做
            */
            if (Program.IsMainThread)
                RetranslateScannedTitles();
            else
                Main.RunOnMainThread(RetranslateScannedTitles);
        }

        /*
        按当前语言重算已扫描条目的标题
        只动 Title：颜色、图标、作用域都是语言无关的，重算它们会白白丢掉玩家的配色结果
        */
        private static void RetranslateScannedTitles()
        {
            try
            {
                RetranslateScannedTitlesCore();
            }
            catch (Exception ex)
            {
                /*
                这里吞掉异常是必须的：SystemLoader.OnLocalizationsLoaded 裸 foreach 所有 ModSystem，
                本模组抛出去会中断整条 LanguageManager.ReloadLanguage，连累其他模组的本地化重载
                */
                DebugLog.Warn(
                    Language.GetTextValue(
                        "Mods.BTitlesLocalizationPatch.Logs.RetranslateFailed",
                        ex.Message
                    )
                );
            }
        }

        private static void RetranslateScannedTitlesCore()
        {
            /*
            加载期 ModContent 也调过一次 ReloadLanguage(resetValuesToKeysFirst: false)
            （ModContent.cs:330），那时 LoadCompleted 已是 true 但扫描还没跑（在 PostSetupContent），
            ScannedBiomes 为 null —— 在这里早退，不碰任何反射状态
            */
            var scanned = BiomeRegistrar.ScannedBiomes;
            if (scanned == null || scanned.Count == 0)
                return;

            var instance = BTitlesLocalizationPatch.InstanceField?.GetValue(null);
            var biomeDict =
                instance == null
                    ? null
                    : BTitlesLocalizationPatch.BiomeDictField?.GetValue(instance)
                        as Dictionary<string, BiomeEntry>;

            // 主菜单或 BTitles 未就绪：不是异常状态，下次切语言会再来一遍
            if (biomeDict == null)
                return;

            int retranslated = 0;
            foreach (var (dictKey, modBiome, _) in scanned)
            {
                if (modBiome == null)
                    continue;
                if (!biomeDict.TryGetValue(dictKey, out var entry))
                    continue;

                string title = modBiome.DisplayName.Value ?? modBiome.Name;
                if (title == entry.Title)
                    continue;

                entry.Title = title;
                retranslated++;
            }

            if (retranslated > 0)
                DebugLog.Info(
                    Language.GetTextValue(
                        "Mods.BTitlesLocalizationPatch.Logs.RetranslatedTitles",
                        retranslated
                    )
                );

            /*
            无论 Title 有没有变都要刷：翻译缓存刚被清空，显示名可能因为这次重载
            才有了译文（Title 不变、显示名变），不推这一把就得等走出再走回群系
            */
            BTitlesLocalizationPatch.RefreshCurrentTitle();
        }
    }
}
