// ============================================================================
// WackeEdl - Serial Port Manager | 串口管理器
// ============================================================================
// [ZH] 串口管理 - 线程安全的串口资源管理和错误恢复
// [EN] Serial Port Manager - Thread-safe serial port management and recovery
// [JA] シリアルポート管理 - スレッドセーフなシリアルポート管理とリカバリ
// [KO] 시리얼 포트 관리자 - 스레드 안전 시리얼 포트 관리 및 복구
// [RU] Менеджер COM-порта - Потокобезопасное управление портом и восстановление
// [ES] Gestor de puerto serie - Gestión thread-safe y recuperación de errores
// ============================================================================
// Copyright (c) 2025-2026 WackeEdl | MIT License
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace WackeEdl.Qualcomm.Common
{
    /// <summary>
    /// 串口管理器 - 线程安全的资源管理
    /// </summary>
    public class SerialPortManager : IDisposable
    {
        private SerialPort _port;
        private Stream _portBaseStream;
        private readonly object _lock = new object();
        private bool _disposed;
        private string _currentPortName = "";
        private readonly Action<string> _log;

        // 串口配置 (9008 EDL 模式用 USB CDC 模拟串口)
        public int BaudRate { get; set; } = 9600;
        public int ReadTimeout { get; set; } = 200;
        public int WriteTimeout { get; set; } = 200;
        public int ReadBufferSize { get; set; } = 64 * 1024;    // 64KB 读缓冲
        public int WriteBufferSize { get; set; } = 128 * 1024;  // 128KB 写缓冲

        /// <summary>
        /// 端口断开事件
        /// </summary>
        public event EventHandler PortDisconnected;

        public SerialPortManager(Action<string> log = null)
        {
            _log = log ?? delegate { };
        }

        public bool IsOpen
        {
            get { return _port != null && _port.IsOpen; }
        }
        
        /// <summary>
        /// 验证端口是否真正可用 (不仅检查 IsOpen，还尝试实际访问)
        /// </summary>
        public bool ValidateConnection()
        {
            lock (_lock)
            {
                if (_port == null || !_port.IsOpen)
                    return false;
                
                try
                {
                    // 尝试访问端口属性来验证连接
                    var _ = _port.BytesToRead;
                    return true;
                }
                catch (IOException)
                {
                    // 端口已断开
                    OnPortDisconnected();
                    return false;
                }
                catch (InvalidOperationException)
                {
                    // 端口已关闭
                    OnPortDisconnected();
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    // 端口被其他程序占用
                    OnPortDisconnected();
                    return false;
                }
            }
        }
        
        /// <summary>
        /// 检查端口是否在系统可用端口列表中
        /// </summary>
        public bool IsPortAvailable()
        {
            if (string.IsNullOrEmpty(_currentPortName))
                return false;
            
            var ports = SerialPort.GetPortNames();
            return Array.Exists(ports, p => p.Equals(_currentPortName, StringComparison.OrdinalIgnoreCase));
        }
        
        private void OnPortDisconnected()
        {
            CloseInternal();
            PortDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public string PortName
        {
            get { return _currentPortName; }
        }

        public int BytesToRead
        {
            get { return _port != null ? _port.BytesToRead : 0; }
        }

        public bool Open(string portName, int maxRetries = 3, bool discardBuffer = false)
        {
            lock (_lock)
            {
                if (_port != null && _port.IsOpen && _currentPortName == portName)
                    return true;

                CloseInternal();

                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        _port = new SerialPort
                        {
                            PortName = portName,
                            BaudRate = BaudRate,
                            DataBits = 8,
                            Parity = Parity.None,
                            StopBits = StopBits.One,
                            Handshake = Handshake.None,
                            ReadTimeout = ReadTimeout,
                            WriteTimeout = WriteTimeout,
                            ReadBufferSize = ReadBufferSize,
                            WriteBufferSize = WriteBufferSize
                        };

                        _port.Open();
                        _currentPortName = portName;
                        _portBaseStream = _port.BaseStream;

                        if (discardBuffer)
                        {
                            _port.DiscardInBuffer();
                            _port.DiscardOutBuffer();
                        }

                        return true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Thread.Sleep(500 * (i + 1));
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(300 * (i + 1));
                    }
                    catch (Exception)
                    {
                        if (i == maxRetries - 1)
                            throw;
                        Thread.Sleep(200);
                    }
                }

                return false;
            }
        }

        public Task<bool> OpenAsync(string portName, int maxRetries = 3, bool discardBuffer = false, CancellationToken ct = default(CancellationToken))
        {
            return Task.Run(() => Open(portName, maxRetries, discardBuffer), ct);
        }

        public void Close()
        {
            lock (_lock)
            {
                CloseInternal();
            }
        }

        private void CloseInternal()
        {
            if (_port != null)
            {
                try
                {
                    // 与 EDLLib 一致: 先释放 BaseStream，避免端口句柄残留
                    try
                    {
                        _portBaseStream?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _log(string.Format("[SerialPort] 释放基础流异常: {0}", ex.Message));
                    }

                    if (_port.IsOpen)
                    {
                        // 清空缓冲区 (忽略异常，端口可能已断开)
                        try { _port.DiscardInBuffer(); _port.DiscardOutBuffer(); }
                        catch (Exception ex) { _log(string.Format("[SerialPort] 清空缓冲区异常: {0}", ex.Message)); }
                        
                        // 禁用控制信号 (忽略异常)
                        try { _port.DtrEnable = false; _port.RtsEnable = false; }
                        catch (Exception ex) { _log(string.Format("[SerialPort] 禁用控制信号异常: {0}", ex.Message)); }
                        
                        Thread.Sleep(50);
                        _port.Close();
                    }
                }
                catch (Exception ex)
                {
                    _log(string.Format("[SerialPort] 关闭端口异常: {0}", ex.Message));
                }
                finally
                {
                    try { _port.Dispose(); }
                    catch (Exception ex) { _log(string.Format("[SerialPort] 释放端口异常: {0}", ex.Message)); }
                    _port = null;
                    _portBaseStream = null;
                    _currentPortName = "";
                }
            }
        }

        private void ForceReleasePort(string portName)
        {
            try
            {
                using (var tempPort = new SerialPort(portName))
                {
                    tempPort.Open();
                    tempPort.Close();
                }
            }
            catch (Exception ex)
            {
                // 端口可能被占用或不存在，这是预期的情况
                _log(string.Format("[SerialPort] 强制释放端口 {0} 失败: {1}", portName, ex.Message));
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (_port == null || !_port.IsOpen) throw new InvalidOperationException("串口未打开");
                _port.Write(data, offset, count);
            }
        }

        public void Write(byte[] data)
        {
            Write(data, 0, data.Length);
        }

        public async Task<bool> WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (!IsOpen) return false;
            try
            {
                await _port.BaseStream.WriteAsync(buffer, offset, count, ct);
                return true;
            }
            catch (Exception ex)
            {
                _log(string.Format("[SerialPort] 异步写入异常: {0}", ex.Message));
                return false;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_port == null || !_port.IsOpen) throw new InvalidOperationException("串口未打开");
            return _port.Read(buffer, offset, count);
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_port == null || !_port.IsOpen) return 0;
            return await _port.BaseStream.ReadAsync(buffer, offset, count, ct);
        }

        /// <summary>
        /// 异步普通读取（非 ReadExactly）:
        /// 等待可读数据后执行一次串口 Read，返回实际读取到的数据（可能小于 length）。
        /// </summary>
        public Task<byte[]> TryReadAsync(int length, int timeout = 10000, CancellationToken ct = default(CancellationToken))
        {
            if (_port == null || !_port.IsOpen || length <= 0)
                return Task.FromResult<byte[]>(null);

            return Task.Run(() =>
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    int originalTimeout = _port.ReadTimeout;
                    byte[] readBuffer = new byte[length];

                    try
                    {
                        // 普通 Read：只读取一次，不做“读满 length”的循环拼接
                        while (stopwatch.ElapsedMilliseconds < timeout)
                        {
                            if (ct.IsCancellationRequested)
                                return null;

                            int bytesAvailable = _port.BytesToRead;
                            int toRead = Math.Min(length, bytesAvailable > 0 ? bytesAvailable : length);
                            if (toRead <= 0)
                            {
                                Thread.Sleep(5);
                                continue;
                            }

                            _port.ReadTimeout = Math.Max(50, Math.Min(1000, timeout));
                            int read = 0;
                            try
                            {
                                read = _port.Read(readBuffer, 0, toRead);
                            }
                            catch (TimeoutException)
                            {
                                read = 0;
                            }

                            if (read > 0)
                            {
                                if (read == length)
                                    return readBuffer;

                                byte[] partial = new byte[read];
                                Buffer.BlockCopy(readBuffer, 0, partial, 0, read);
                                return partial;
                            }
                        }

                        return null;
                    }
                    finally
                    {
                        try { _port.ReadTimeout = originalTimeout; }
                        catch (Exception ex) { _log(string.Format("[SerialPort] 恢复超时设置异常: {0}", ex.Message)); }
                    }
                }
                catch (Exception ex)
                {
                    _log(string.Format("[SerialPort] 读取数据异常: {0}", ex.Message));
                    return null;
                }
            }, ct);
        }

        public Stream BaseStream
        {
            get { return _port?.BaseStream; }
        }

        public void DiscardInBuffer()
        {
            if (_port != null) _port.DiscardInBuffer();
        }

        public void DiscardOutBuffer()
        {
            if (_port != null) _port.DiscardOutBuffer();
        }

        public static string[] GetAvailablePorts()
        {
            return SerialPort.GetPortNames();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing) Close();
                _disposed = true;
            }
        }

        ~SerialPortManager()
        {
            Dispose(false);
        }
    }
}
