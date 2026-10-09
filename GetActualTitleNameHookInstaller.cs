#nullable enable
using System;
using System.Linq;
using System.Reflection;
using BTitles;
using MonoMod.RuntimeDetour;
using Terraria.ModLoader;

namespace BTitlesLocalizationPatch
{
    /*
    GetActualTitleName Hook 机制：签名指纹验证 + 安装/卸载
    业务逻辑（翻译链、缓存）留在 BiomeNameHook，此处只管 Hook 生命周期
    */
    internal static class GetActualTitleNameHookInstaller
    {
        private static Hook? _getActualTitleNameHook;
        private static readonly Type[] _expectedHookSignature = [typeof(BiomeEntry)];

        // 签名指纹验证：防止 BTitles 更新改变方法签名导致 new Hook() 崩溃
        internal static void Install(Mod mod)
        {
            MethodInfo? getTitleMethod = typeof(BiomeTitlesUI).GetMethod(
                "GetActualTitleName",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            if (getTitleMethod == null)
            {
                BTitlesLocalizationPatch.HookInstalled = false;
                mod.Logger.Error("Failed to find BiomeTitlesUI.GetActualTitleName");
                return;
            }

            if (!ValidateMethodSignature(getTitleMethod, _expectedHookSignature))
            {
                BTitlesLocalizationPatch.HookInstalled = false;
                mod.Logger.Error(
                    $"Hook signature mismatch: actual {FormatMethodSignature(getTitleMethod)}, expected BiomeEntry"
                );
                return;
            }

            _getActualTitleNameHook = new Hook(
                getTitleMethod,
                new Func<
                    Func<BiomeTitlesUI, BiomeEntry, string>,
                    BiomeTitlesUI,
                    BiomeEntry,
                    string
                >(BiomeNameHook.GetActualTitleNamePrefixHook)
            );
            BTitlesLocalizationPatch.HookInstalled = true;
            mod.Logger.Info("GetActualTitleName Detour patch applied successfully");
        }

        // 单独 try-catch 包裹 Dispose，防止它抛异常阻断后续清理
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
            BTitlesLocalizationPatch.HookInstalled = false;
        }

        private static bool ValidateMethodSignature(MethodInfo method, Type[] expectedParamTypes)
        {
            var actualParams = method.GetParameters();
            if (actualParams.Length != expectedParamTypes.Length)
                return false;
            for (int i = 0; i < actualParams.Length; i++)
            {
                if (actualParams[i].ParameterType != expectedParamTypes[i])
                    return false;
            }
            return true;
        }

        private static string FormatMethodSignature(MethodInfo method)
        {
            var paramNames = method
                .GetParameters()
                .Select(p =>
                {
                    string name = p.ParameterType.IsByRef
                        ? p.ParameterType.GetElementType()?.Name ?? "?"
                        : p.ParameterType.Name;
                    string mod =
                        p.IsOut ? "out"
                        : p.ParameterType.IsByRef ? "ref"
                        : "";
                    return mod.Length > 0 ? $"{name} {mod}" : name;
                });
            return $"{method.Name}({string.Join(", ", paramNames)})";
        }
    }
}
