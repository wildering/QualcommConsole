using System;
using System.Threading;
using System.Threading.Tasks;

namespace WackeEdl.Qualcomm.Protocol
{
    public interface IDiagClient : IDisposable
    {
        bool IsConnected { get; }
        Task<bool> ConnectAsync(string portName, int baudRate = 115200);
        void Disconnect();
        Task<bool> SendSpcAsync(string spc = "000000");
        Task<string> ReadImeiAsync(int slot = 1);
        Task<bool> WriteImeiAsync(string imei, int slot = 1);
        Task<ImeiInfo> ReadAllImeiAsync();
        Task<string> ReadMeidAsync();
        Task<bool> ReadQcnAsync(string filePath, IProgress<int> progress = null, CancellationToken cancellationToken = default);
        Task<bool> WriteQcnAsync(string filePath, IProgress<int> progress = null, CancellationToken cancellationToken = default);
        Task<bool> SwitchToDownloadModeAsync();
        Task<bool> RebootAsync();
    }
}
