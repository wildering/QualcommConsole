using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WackeEdl.Qualcomm.Common
{
    /// <summary>
    /// Stream 安全读取扩展：
    /// - TryReadExactly: 循环读取直到满足长度，失败返回 false
    /// - ReadExactlyOrThrow: 读取不足时抛 EndOfStreamException
    /// </summary>
    public static class StreamReadExtensions
    {
        public static bool TryReadExactly(this Stream stream, byte[] buffer, int offset, int count)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                    return false;

                totalRead += read;
            }

            return true;
        }

        public static async Task<bool> TryReadExactlyAsync(
            this Stream stream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(
                    new Memory<byte>(buffer, offset + totalRead, count - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    return false;

                totalRead += read;
            }

            return true;
        }

        public static void ReadExactlyOrThrow(this Stream stream, byte[] buffer, int offset, int count, string context = null)
        {
            if (!stream.TryReadExactly(buffer, offset, count))
                throw new EndOfStreamException(string.IsNullOrWhiteSpace(context)
                    ? "读取流数据不完整"
                    : $"读取流数据不完整: {context}");
        }

        public static async Task ReadExactlyOrThrowAsync(
            this Stream stream,
            byte[] buffer,
            int offset,
            int count,
            string context = null,
            CancellationToken cancellationToken = default)
        {
            if (!await stream.TryReadExactlyAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false))
                throw new EndOfStreamException(string.IsNullOrWhiteSpace(context)
                    ? "读取流数据不完整"
                    : $"读取流数据不完整: {context}");
        }
    }
}
