// ============================================================================
// WackeEdl Console - Watchdog Stubs | 操作监视器桩代码
// ============================================================================
// 替代 WackeEdl.Common 中的 Watchdog 相关类型
// ============================================================================

using System;
using System.Threading;

namespace WackeEdl.Common
{
    /// <summary>
    /// 操作监视器超时事件参数
    /// </summary>
    public class WatchdogTimeoutEventArgs : EventArgs
    {
        public string OperationName { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public int TimeoutCount { get; set; }
        public bool ShouldReset { get; set; } = true;
    }

    /// <summary>
    /// 操作监视器 - 检测操作卡死并触发超时事件
    /// </summary>
    public class Watchdog : IDisposable
    {
        private readonly string _name;
        private readonly TimeSpan _timeout;
        private readonly Action<string> _logDetail;
        private Timer _timer;
        private DateTime _lastFeed;
        private string _currentOperation;
        private int _timeoutCount;
        private bool _running;
        private bool _disposed;

        public event EventHandler<WatchdogTimeoutEventArgs> OnTimeout;

        public Watchdog(string name, TimeSpan timeout, Action<string> logDetail = null)
        {
            _name = name;
            _timeout = timeout;
            _logDetail = logDetail ?? delegate { };
        }

        public void Start(string operation)
        {
            _currentOperation = operation;
            _lastFeed = DateTime.Now;
            _timeoutCount = 0;
            _running = true;

            _timer?.Dispose();
            _timer = new Timer(CheckTimeout, null, _timeout, _timeout);
            _logDetail($"[Monitor:{_name}] 启动: {operation} (超时={_timeout.TotalSeconds}s)");
        }

        public void Stop()
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
            _logDetail($"[Monitor:{_name}] 停止");
        }

        public void Feed()
        {
            _lastFeed = DateTime.Now;
            _timeoutCount = 0;
        }

        private void CheckTimeout(object state)
        {
            if (!_running || _disposed) return;

            var elapsed = DateTime.Now - _lastFeed;
            if (elapsed >= _timeout)
            {
                _timeoutCount++;
                var args = new WatchdogTimeoutEventArgs
                {
                    OperationName = _currentOperation,
                    ElapsedTime = elapsed,
                    TimeoutCount = _timeoutCount,
                    ShouldReset = true
                };

                OnTimeout?.Invoke(this, args);

                if (!args.ShouldReset)
                {
                    Stop();
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _running = false;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    /// <summary>
    /// 操作监视器管理器 - 提供默认超时配置
    /// </summary>
    public static class WatchdogManager
    {
        public static class DefaultTimeouts
        {
            public static readonly TimeSpan Qualcomm = TimeSpan.FromSeconds(60);
            public static readonly TimeSpan Sahara = TimeSpan.FromSeconds(45);
            public static readonly TimeSpan Firehose = TimeSpan.FromSeconds(120);
        }
    }
}
