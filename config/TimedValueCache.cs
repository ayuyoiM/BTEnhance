#nullable enable
using System;
using System.Threading;

namespace BTitlesLocalizationPatch.Diagnostics
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

        public TimedValueCache(Func<bool> read, TimeSpan cooldown)
        {
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
            }
            catch
            {
                // 配置实例可能在卸载途中不可用，此时按「关闭」处理而不是让渲染路径抛异常
                _cachedValue = false;
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
    }
}
