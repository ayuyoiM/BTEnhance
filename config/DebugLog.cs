#nullable enable
using System;
using Terraria.ModLoader;

namespace BTitlesEnhance.Diagnostics
{
    /*
    调试日志工具，按级别分流到 tModLoader 的 Logger
    */
    internal static class DebugLog
    {
        // 缓存配置值，配合冷却时间避免热路径中高频查询 ModContent.GetInstance
        // 修改配置后最多 3 秒生效，无需重载
        private static readonly TimedValueCache ConfigCache = new(
            nameof(BTitlesConfig.EnableDebugLog),
            () => ModContent.GetInstance<BTitlesConfig>()?.EnableDebugLog ?? false,
            TimeSpan.FromSeconds(3)
        );

        internal static void ResetCache()
        {
            ConfigCache.Reset();
        }

        public static bool Enabled => ConfigCache.Get();

        public static Action<string> InfoWriter { get; set; } = s => { };
        public static Action<string> WarnWriter { get; set; } = s => { };
        public static Action<string> ErrorWriter { get; set; } = s => { };

        public static void Info(string message)
        {
            if (Enabled)
                InfoWriter?.Invoke(message);
        }

        // 警告日志：不受调试开关控制，始终输出，用于记录不影响功能的异常
        public static void Warn(string message)
        {
            WarnWriter?.Invoke(message);
        }

        // 错误日志：不受调试开关控制，始终输出，用于记录严重异常
        public static void Error(string message)
        {
            ErrorWriter?.Invoke(message);
        }
    }
}
