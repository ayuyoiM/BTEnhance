#nullable enable
using System;
using System.Reflection;
using BTitles;
using MonoMod.RuntimeDetour;
using Terraria.ModLoader;

namespace BTitlesEnhance
{
    /*
    GetActualTitleName Hook 机制：安装/卸载
    业务逻辑（翻译链、缓存）留在 BiomeNameHook，此处只管 Hook 生命周期
    */
    internal static class GetActualTitleNameHookInstaller
    {
        private static Hook? _getActualTitleNameHook;

        // 安装 Hook；找不到方法抛 BTitlesIncompatibleException 拒绝加载
        internal static void Install(Mod mod)
        {
            MethodInfo? getTitleMethod = typeof(BiomeTitlesUI).GetMethod(
                "GetActualTitleName",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            // 方法没了说明 BTitles 已改结构：抛出去让模组加载失败，
            // 而不是置 HookInstalled=false 变成「界面照常，翻译悄悄不生效」
            if (getTitleMethod == null)
            {
                BTitlesEnhance.HookInstalled = false;
                throw new BTitlesIncompatibleException("HookMethodMissing");
            }

            // 不做签名预校验：委托签名对不上时 new Hook() 自己就会抛，
            // 那一声异常同样会让 tModLoader 把本模组标成加载失败
            _getActualTitleNameHook = new Hook(
                getTitleMethod,
                new Func<
                    Func<BiomeTitlesUI, BiomeEntry, string>,
                    BiomeTitlesUI,
                    BiomeEntry,
                    string
                >(BiomeNameHook.GetActualTitleNameHook)
            );
            BTitlesEnhance.HookInstalled = true;
            mod.Logger.Info("GetActualTitleName Detour patch applied successfully");
        }

        // 卸载 Hook；单独 try-catch 包裹 Dispose，防止它抛异常阻断后续清理
        internal static void Uninstall(Mod mod)
        {
            try
            {
                _getActualTitleNameHook?.Dispose();
            }
            catch (Exception ex)
            {
                mod.Logger.Warn($"Hook Dispose failed: {ex.Message}");
            }
            _getActualTitleNameHook = null;
            BTitlesEnhance.HookInstalled = false;
        }
    }
}
