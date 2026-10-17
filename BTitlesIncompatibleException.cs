#nullable enable
using System;
using Terraria.Localization;

namespace BTitlesEnhance
{
    /*
    补丁前提失效时的统一异常
    触发条件：BTitles 改了内部结构（方法没了、签名变了、反射字段没了）、扫描结果无法提交
    抛出后由 tModLoader 捕获并把模组标记为「加载失败」，
    而不是降级成「界面照常，翻译悄悄不生效」这种玩家看不见的失效
    */
    internal sealed class BTitlesIncompatibleException : Exception
    {
        public BTitlesIncompatibleException(string logKey, string? detail = null)
            : base(BuildMessage(logKey, detail)) { }

        public BTitlesIncompatibleException(
            string logKey,
            string? detail,
            Exception innerException
        )
            : base(BuildMessage(logKey, detail), innerException) { }

        // 复用 Logs 下的本地化文案，异常信息对玩家可读
        private static string BuildMessage(string logKey, string? detail)
        {
            string text = Language.GetTextValue(
                $"Mods.{nameof(BTitlesEnhance)}.Logs.{logKey}"
            );
            return detail == null ? text : $"{text} ({detail})";
        }
    }
}
