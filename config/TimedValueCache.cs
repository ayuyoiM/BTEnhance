#nullable enable
using System;
using System.Threading;
using Terraria.Localization;

namespace BTitlesEnhance.Diagnostics
{
    /*
    带冷却的布尔配置值缓存
    标题渲染与群系检测都跑在每帧路径上，直接查 ModContent.GetInstance 开销明显
    冷却期内复用缓存值，配置变更时由调用方 Set 立即同步，无需重载
    读写可能跨线程（UI 线程 / PostSetupContent），故用 Volatile 保证可见性
    */
    internal sealed class TimedValueCache
    {
        // volatile: 允许不同线程（UI 渲染 / PostSetupContent）读取同一个缓存值
        private volatile bool _cachedValue;

        // long 不能 volatile（CS0677），用 Volatile.Read/Write 保证原子可见性
        private long _lastCheckTicks;
        private readonly long _cooldownTicks;

        // 日志里用它指出是哪个配置项读不到，多个缓存实例共用同一条 Warn
        private readonly string _name;

        // 读取失败的日志只留给第一次：实例不可用时每次 Get 都会抛，
        // 不限频的话每过一个冷却周期就是一条，能把日志刷满
        private volatile bool _readFailureReported;

        public TimedValueCache(string name, Func<bool> read, TimeSpan cooldown)
        {
            _name = name;
            Read = read;
            _cooldownTicks = cooldown.Ticks;
        }

        private Func<bool> Read { get; }

        public bool Get()
        {
            long now = DateTime.UtcNow.Ticks;
            long lastTicks = Volatile.Read(ref _lastCheckTicks);
            if (now - lastTicks < _cooldownTicks)
                return _cachedValue;

            Volatile.Write(ref _lastCheckTicks, now);
            try
            {
                _cachedValue = Read();
                // 恢复后再失败还要能报一次，所以成功时要把「已报过」清掉
                _readFailureReported = false;
            }
            catch (Exception ex)
            {
                // 配置实例可能在卸载途中不可用。此时保留上一次的缓存值，
                // 而不是当成「配置为 false」：后者会让一个本来开着的功能静默关闭，
                // 而且日志里与「用户真的关了这个开关」无从分辨
                if (!_readFailureReported)
                {
                    _readFailureReported = true;
                    ReportReadFailure(ex);
                }
            }
            return _cachedValue;
        }

        // 配置变更时立即同步，避免冷却期内读到旧值
        public void Set(bool value)
        {
            _cachedValue = value;
            Volatile.Write(ref _lastCheckTicks, DateTime.UtcNow.Ticks);
        }

        // 热重载时使缓存立即失效，下次 Get 重新读取
        public void Reset()
        {
            _cachedValue = false;
            Volatile.Write(ref _lastCheckTicks, 0L);
        }

        /*
        读失败上报一次
        异常本身不往外抛：本类服务于每帧渲染路径，抛出去会直接打断绘制，
        而调用方要的只是一份可用的布尔值
        */
        private void ReportReadFailure(Exception ex)
        {
            string message;
            try
            {
                message = Language.GetTextValue(
                    $"Mods.{nameof(BTitlesEnhance)}.Logs.CacheReadFailed",
                    _name,
                    ex.Message
                );
            }
            catch (FormatException)
            {
                // 本地化模板含未转义 { } 时 GetTextValue 会抛 FormatException；
                // 退化成标识符 + 异常原文，不让日志问题盖掉真正要报的读取失败
                message = $"[{_name}] {ex.Message}";
            }
            DebugLog.Warn(message);
        }
    }
}
