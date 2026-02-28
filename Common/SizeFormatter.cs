namespace QualcommConsole.Common
{
    /// <summary>
    /// 数据大小和速度的自动单位格式化工具
    /// </summary>
    public static class SizeFormatter
    {
        /// <summary>
        /// 格式化字节数为可读字符串，自动切换 B/KB/MB/GB
        /// </summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes >= 1073741824L)
                return string.Format("{0:F2} GB", (double)bytes / 1073741824.0);
            if (bytes >= 1048576L)
                return string.Format("{0:F2} MB", (double)bytes / 1048576.0);
            if (bytes >= 1024L)
                return string.Format("{0:F0} KB", (double)bytes / 1024.0);
            return string.Format("{0} B", bytes);
        }

        /// <summary>
        /// 格式化字节数 (double)，自动切换 B/KB/MB/GB
        /// </summary>
        public static string FormatSize(double bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes >= 1073741824.0)
                return string.Format("{0:F2} GB", bytes / 1073741824.0);
            if (bytes >= 1048576.0)
                return string.Format("{0:F2} MB", bytes / 1048576.0);
            if (bytes >= 1024.0)
                return string.Format("{0:F0} KB", bytes / 1024.0);
            return string.Format("{0:F0} B", bytes);
        }

        /// <summary>
        /// 格式化传输速度 (bytes/s)，自动切换 B/s, KB/s, MB/s, GB/s
        /// </summary>
        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) bytesPerSecond = 0;
            if (bytesPerSecond >= 1073741824.0)
                return string.Format("{0:F2} GB/s", bytesPerSecond / 1073741824.0);
            if (bytesPerSecond >= 1048576.0)
                return string.Format("{0:F2} MB/s", bytesPerSecond / 1048576.0);
            if (bytesPerSecond >= 1024.0)
                return string.Format("{0:F0} KB/s", bytesPerSecond / 1024.0);
            return string.Format("{0:F0} B/s", bytesPerSecond);
        }
    }
}
