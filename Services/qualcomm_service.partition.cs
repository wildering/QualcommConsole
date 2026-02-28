// ============================================================================
// QualcommService - 分区操作/设备控制/批量刷写 (partial class)
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WackeEdl.Common;
using WackeEdl.Qualcomm.Common;
using WackeEdl.Qualcomm.Database;
using WackeEdl.Qualcomm.Models;
using WackeEdl.Qualcomm.Protocol;
using WackeEdl.Qualcomm.Authentication;

namespace WackeEdl.Qualcomm.Services
{
    public partial class QualcommService
    {
        #region 分区操作

        /// <summary>
        /// 读取所有 LUN 的 GPT 分区表
        /// </summary>
        public async Task<List<PartitionInfo>> ReadAllGptAsync(int maxLuns = 6, CancellationToken ct = default(CancellationToken))
        {
            return await ReadAllGptAsync(maxLuns, null, null, ct);
        }

        /// <summary>
        /// 读取所有 LUN 的 GPT 分区表（带进度回调）
        /// </summary>
        /// <param name="maxLuns">最大 LUN 数量</param>
        /// <param name="totalProgress">总进度回调 (当前LUN, 总LUN)</param>
        /// <param name="subProgress">子进度回调 (0-100)</param>
        /// <param name="ct">取消令牌</param>
        public async Task<List<PartitionInfo>> ReadAllGptAsync(
            int maxLuns, 
            IProgress<Tuple<int, int>> totalProgress,
            IProgress<double> subProgress,
            CancellationToken ct = default(CancellationToken))
        {
            var allPartitions = new List<PartitionInfo>();

            if (_firehose == null)
                return allPartitions;

            _logDetail("正在读取 GUID 分区表...");

            // 报告开始
            if (totalProgress != null) totalProgress.Report(Tuple.Create(0, maxLuns));
            if (subProgress != null) subProgress.Report(0);

            // LUN 进度回调 - 实时更新进度
            var lunProgress = new Progress<int>(lun => {
                if (totalProgress != null) totalProgress.Report(Tuple.Create(lun, maxLuns));
                if (subProgress != null) subProgress.Report(100.0 * lun / maxLuns);
            });

            var partitions = await _firehose.ReadGptPartitionsAsync(IsVipDevice, ct, lunProgress);
            
            // 报告中间进度
            if (subProgress != null) subProgress.Report(80);
            
            if (partitions != null && partitions.Count > 0)
            {
                allPartitions.AddRange(partitions);
                _log(string.Format("读取 GUID 分区表 : 成功 [{0}]", partitions.Count));

                // 缓存分区
                _partitionCache.Clear();
                foreach (var p in partitions)
                {
                    if (!_partitionCache.ContainsKey(p.Lun))
                        _partitionCache[p.Lun] = new List<PartitionInfo>();
                    _partitionCache[p.Lun].Add(p);
                }

                // 输出完整分区汇总表
                _logDetail("[高通] ────────────────────────────────────────────────────────────────");
                _logDetail(string.Format("[高通] {0,-4} {1,-28} {2,12} {3,12} {4,10}",
                    "LUN", "分区名", "起始扇区", "结束扇区", "大小"));
                _logDetail("[高通] ────────────────────────────────────────────────────────────────");
                foreach (var p in allPartitions)
                {
                    _logDetail(string.Format("[高通] {0,-4} {1,-28} {2,12} {3,12} {4,10}",
                        p.Lun, p.Name, p.StartSector, p.StartSector + p.NumSectors - 1, p.FormattedSize));
                }
                _logDetail("[高通] ────────────────────────────────────────────────────────────────");

                // 按 LUN 输出统计
                foreach (var kv in _partitionCache)
                {
                    long totalSectors = 0;
                    foreach (var p in kv.Value) totalSectors += p.NumSectors;
                    _logDetail(string.Format("[高通] LUN{0}: {1} 个分区, 总扇区数: {2}",
                        kv.Key, kv.Value.Count, totalSectors));
                }
            }

            // 报告完成
            if (subProgress != null) subProgress.Report(100);
            if (totalProgress != null) totalProgress.Report(Tuple.Create(maxLuns, maxLuns));

            _log(string.Format("[高通] 共发现 {0} 个分区", allPartitions.Count));
            return allPartitions;
        }

        /// <summary>
        /// 获取指定 LUN 的分区列表
        /// </summary>
        public List<PartitionInfo> GetCachedPartitions(int lun = -1)
        {
            var result = new List<PartitionInfo>();

            if (lun == -1)
            {
                foreach (var kv in _partitionCache)
                    result.AddRange(kv.Value);
            }
            else
            {
                List<PartitionInfo> list;
                if (_partitionCache.TryGetValue(lun, out list))
                    result.AddRange(list);
            }

            return result;
        }

        /// <summary>
        /// 查找分区
        /// </summary>
        public PartitionInfo FindPartition(string name)
        {
            foreach (var kv in _partitionCache)
            {
                foreach (var p in kv.Value)
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
            }
            return null;
        }

        /// <summary>
        /// 读取分区到文件
        /// </summary>
        public async Task<bool> ReadPartitionAsync(string partitionName, string outputPath, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            var partition = FindPartition(partitionName);
            if (partition == null)
            {
                _log("[高通] 未找到分区 " + partitionName);
                return false;
            }

            _log(string.Format("[高通] 读取分区 {0} ({1})", partitionName, partition.FormattedSize));

            try
            {
                int sectorSize = partition.SectorSize > 0 ? partition.SectorSize : (_firehose.SectorSize > 0 ? _firehose.SectorSize : 4096);
                int sectorsPerChunk = _firehose.EffectiveChunkSize / sectorSize;
                if (sectorsPerChunk <= 0)
                    sectorsPerChunk = 1;

                long totalSectors = partition.NumSectors;
                long readSectors = 0;
                long totalBytes = partition.Size;
                long readBytes = 0;

                int chunkBytes = sectorsPerChunk * sectorSize;
                byte[] ioBuffer = SimpleBufferPool.Rent(chunkBytes);
                try
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        if (totalBytes > 0)
                        {
                            fs.SetLength(totalBytes);
                            fs.Position = 0;
                        }

                        while (readSectors < totalSectors && !ct.IsCancellationRequested)
                        {
                            int toRead = (int)Math.Min(sectorsPerChunk, totalSectors - readSectors);
                            int expectedBytes = toRead * sectorSize;
                            int receivedBytes = await _firehose.ReadSectorsIntoBufferAsync(
                                partition.Lun,
                                partition.StartSector + readSectors,
                                toRead,
                                ioBuffer,
                                ct,
                                IsVipDevice,
                                partitionName).ConfigureAwait(false);

                            if (receivedBytes < expectedBytes)
                            {
                                _log("[高通] 读取失败");
                                return false;
                            }

                            await fs.WriteAsync(ioBuffer, 0, expectedBytes, ct).ConfigureAwait(false);
                            readSectors += toRead;
                            readBytes += expectedBytes;

                            // 调用字节级进度回调 (用于速度计算)
                            _firehose.ReportProgress(readBytes, totalBytes);

                            // 百分比进度 (使用 double)
                            if (progress != null && totalBytes > 0)
                                progress.Report(100.0 * readBytes / totalBytes);
                        }
                    }
                }
                finally
                {
                    SimpleBufferPool.Return(ioBuffer);
                }

                _log(string.Format("[高通] 分区 {0} 已保存到 {1}", partitionName, outputPath));
                return true;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 读取错误 - {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 写入分区
        /// </summary>
        public async Task<bool> WritePartitionAsync(string partitionName, string filePath, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            var partition = FindPartition(partitionName);
            if (partition == null)
            {
                _log("[高通] 未找到分区 " + partitionName);
                return false;
            }

            // OPLUS 某些分区需要 SHA256 校验环绕
            bool useSha256 = IsOplusDevice && (partitionName.ToLower() == "xbl" || partitionName.ToLower() == "abl" || partitionName.ToLower() == "imagefv");
            if (useSha256) await _firehose.Sha256InitAsync(ct).ConfigureAwait(false);

            // O+认证设备使用伪装模式写入
            // ConfigureAwait(false) 避免回到 UI 线程，提高 IO 性能
            bool success = await _firehose.FlashPartitionFromFileAsync(
                partitionName, filePath, partition.Lun, partition.StartSector, progress, ct, IsVipDevice).ConfigureAwait(false);

            if (useSha256) await _firehose.Sha256FinalAsync(ct).ConfigureAwait(false);

            return success;
        }

        private bool IsOplusDevice 
        { 
            get { 
                if (IsVipDevice) return true;
                if (ChipInfo != null && (ChipInfo.Vendor == "OPPO" || ChipInfo.Vendor == "Realme" || ChipInfo.Vendor == "OnePlus")) return true;
                return false;
            } 
        }

        /// <summary>
        /// 直接写入指定 LUN 和 StartSector (用于 PrimaryGPT/BackupGPT 等特殊分区)
        /// 支持官方 NUM_DISK_SECTORS-N 负扇区格式
        /// </summary>
        public async Task<bool> WriteDirectAsync(string label, string filePath, int lun, long startSector, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            // 负扇区使用官方格式直接发送给设备 (不依赖客户端 GPT 缓存)
            if (startSector < 0)
            {
                _logDetail(string.Format("[高通] 写入: {0} -> LUN{1} @ NUM_DISK_SECTORS{2}", label, lun, startSector));
                
                // 使用官方 NUM_DISK_SECTORS-N 格式，让设备计算绝对地址
                // ConfigureAwait(false) 避免回到 UI 线程
                return await _firehose.FlashPartitionWithNegativeSectorAsync(
                    label, filePath, lun, startSector, progress, ct).ConfigureAwait(false);
            }
            else
            {
                _logDetail(string.Format("[高通] 写入: {0} -> LUN{1} @ sector {2}", label, lun, startSector));

                // 正数扇区正常写入
                // ConfigureAwait(false) 避免回到 UI 线程
                return await _firehose.FlashPartitionFromFileAsync(
                    label, filePath, lun, startSector, progress, ct, IsVipDevice).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 擦除分区
        /// </summary>
        public async Task<bool> ErasePartitionAsync(string partitionName, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            var partition = FindPartition(partitionName);
            if (partition == null)
            {
                _log("[高通] 未找到分区 " + partitionName);
                return false;
            }

            // O+认证设备使用伪装模式擦除
            // ConfigureAwait(false) 避免回到 UI 线程
            return await _firehose.ErasePartitionAsync(partition, ct, IsVipDevice).ConfigureAwait(false);
        }

        /// <summary>
        /// 读取分区指定偏移处的数据
        /// </summary>
        /// <param name="partitionName">分区名称</param>
        /// <param name="offset">偏移 (字节)</param>
        /// <param name="size">大小 (字节)</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>读取的数据</returns>
        public async Task<byte[]> ReadPartitionDataAsync(string partitionName, long offset, int size, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return null;

            var partition = FindPartition(partitionName);
            if (partition == null)
            {
                _log("[高通] 未找到分区 " + partitionName);
                return null;
            }

            // 计算扇区位置 (优先使用分区自身的 SectorSize, 与 ReadPartitionAsync 一致)
            int sectorSize = partition.SectorSize > 0 ? partition.SectorSize : (SectorSize > 0 ? SectorSize : 4096);
            long startSector = partition.StartSector + (offset / sectorSize);
            int numSectors = (size + sectorSize - 1) / sectorSize;

            // 只有 O+认证成功后才使用 O+认证模式读取
            // IsVipDevice = true 表示 O+认证已成功
            // IsOplusDevice 只用于判断是否需要 SHA256 校验，不用于读取模式
            bool useVipMode = IsVipDevice;

            // 读取数据
            byte[] data = await _firehose.ReadSectorsAsync(partition.Lun, startSector, numSectors, ct, useVipMode, partitionName);
            if (data == null) return null;

            // 如果有偏移对齐问题，截取正确的数据
            int offsetInSector = (int)(offset % sectorSize);
            if (offsetInSector > 0 || data.Length > size)
            {
                int actualSize = Math.Min(size, data.Length - offsetInSector);
                if (actualSize <= 0) return null;
                
                byte[] result = new byte[actualSize];
                Array.Copy(data, offsetInSector, result, 0, actualSize);
                return result;
            }

            return data;
        }

        /// <summary>
        /// 直接读取分区到文件 (不依赖缓存, 由调用方提供 LUN/扇区/大小)
        /// </summary>
        public async Task<bool> ReadPartitionDirectAsync(
            string label, int lun, long startSector, long numSectors,
            string outputPath, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;

            int sectorSize = _firehose.SectorSize > 0 ? _firehose.SectorSize : 4096;
            long totalBytes = numSectors * sectorSize;
            _log(string.Format("[高通] 读取分区 {0} (LUN{1}, {2})", label, lun,
                QualcommConsole.Common.SizeFormatter.FormatSize(totalBytes)));

            try
            {
                int sectorsPerChunk = _firehose.EffectiveChunkSize / sectorSize;
                if (sectorsPerChunk <= 0) sectorsPerChunk = 1;

                long readSectors = 0;
                long readBytes = 0;
                int chunkBytes = sectorsPerChunk * sectorSize;
                byte[] ioBuffer = SimpleBufferPool.Rent(chunkBytes);
                try
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        if (totalBytes > 0)
                        {
                            fs.SetLength(totalBytes);
                            fs.Position = 0;
                        }

                        while (readSectors < numSectors && !ct.IsCancellationRequested)
                        {
                            int toRead = (int)Math.Min(sectorsPerChunk, numSectors - readSectors);
                            int expectedBytes = toRead * sectorSize;
                            int receivedBytes = await _firehose.ReadSectorsIntoBufferAsync(
                                lun, startSector + readSectors, toRead, ioBuffer, ct,
                                IsVipDevice, label).ConfigureAwait(false);

                            if (receivedBytes < expectedBytes)
                            {
                                _log("[高通] 读取失败");
                                return false;
                            }

                            await fs.WriteAsync(ioBuffer, 0, expectedBytes, ct).ConfigureAwait(false);
                            readSectors += toRead;
                            readBytes += expectedBytes;

                            _firehose.ReportProgress(readBytes, totalBytes);
                            if (progress != null && totalBytes > 0)
                                progress.Report(100.0 * readBytes / totalBytes);
                        }
                    }
                }
                finally
                {
                    SimpleBufferPool.Return(ioBuffer);
                }

                _log(string.Format("[高通] 分区 {0} 已保存到 {1}", label, outputPath));
                return true;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 读取错误 - {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 直接写入分区 (不依赖缓存, 由调用方提供 LUN/扇区)
        /// </summary>
        public async Task<bool> WritePartitionDirectAsync(
            string label, int lun, long startSector, string filePath,
            IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;

            bool useSha256 = IsOplusDevice && (label.ToLower() == "xbl" || label.ToLower() == "abl" || label.ToLower() == "imagefv");
            if (useSha256) await _firehose.Sha256InitAsync(ct).ConfigureAwait(false);

            bool success;
            if (startSector < 0)
            {
                _logDetail(string.Format("[高通] 写入: {0} -> LUN{1} @ NUM_DISK_SECTORS{2}", label, lun, startSector));
                success = await _firehose.FlashPartitionWithNegativeSectorAsync(
                    label, filePath, lun, startSector, progress, ct).ConfigureAwait(false);
            }
            else
            {
                success = await _firehose.FlashPartitionFromFileAsync(
                    label, filePath, lun, startSector, progress, ct, IsVipDevice).ConfigureAwait(false);
            }

            if (useSha256) await _firehose.Sha256FinalAsync(ct).ConfigureAwait(false);
            return success;
        }

        /// <summary>
        /// 直接擦除分区 (不依赖缓存, 由调用方提供 LUN/扇区/大小)
        /// </summary>
        public async Task<bool> ErasePartitionDirectAsync(
            string label, int lun, long startSector, long numSectors,
            CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.ErasePartitionAsync(label, lun, startSector, numSectors, ct, IsVipDevice).ConfigureAwait(false);
        }

        /// <summary>
        /// 直接读取分区指定偏移处的数据 (不依赖缓存)
        /// </summary>
        public async Task<byte[]> ReadPartitionDataDirectAsync(
            string label, int lun, long startSector, long offset, int size,
            CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return null;

            int sectorSize = _firehose.SectorSize > 0 ? _firehose.SectorSize : 4096;
            long sectorOffset = startSector + (offset / sectorSize);
            int numSectors = (size + sectorSize - 1) / sectorSize;

            byte[] data = await _firehose.ReadSectorsAsync(lun, sectorOffset, numSectors, ct, IsVipDevice, label);
            if (data == null) return null;

            int offsetInSector = (int)(offset % sectorSize);
            if (offsetInSector > 0 || data.Length > size)
            {
                int actualSize = Math.Min(size, data.Length - offsetInSector);
                if (actualSize <= 0) return null;
                byte[] result = new byte[actualSize];
                Array.Copy(data, offsetInSector, result, 0, actualSize);
                return result;
            }
            return data;
        }

        /// <summary>
        /// 获取 Firehose 客户端 (供内部使用)
        /// </summary>
        internal Protocol.FirehoseClient GetFirehoseClient()
        {
            return _firehose;
        }

        #endregion

        #region 设备控制

        /// <summary>
        /// 重启设备
        /// </summary>
        public Task<bool> RebootAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return Task.FromResult(false);

            bool result = false;
            try
            {
                // 对齐 EDLLib: 发送重启命令后立即断开，不等待 ACK。
                result = _firehose.ResetNoAck("reset");
                return Task.FromResult(result);
            }
            finally
            {
                Disconnect();
            }
        }

        /// <summary>
        /// 关机
        /// </summary>
        public async Task<bool> PowerOffAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            bool result = await _firehose.PowerOffAsync(ct);
            if (result)
                Disconnect();

            return result;
        }

        /// <summary>
        /// 重启到 EDL 模式
        /// </summary>
        public Task<bool> RebootToEdlAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return Task.FromResult(false);

            bool result = false;
            try
            {
                // 对齐 EDLLib: 发送 reset_to_edl 后立即断开，不等待 ACK。
                result = _firehose.RebootToEdlNoAck();
                return Task.FromResult(result);
            }
            finally
            {
                Disconnect();
            }
        }

        /// <summary>
        /// 设置活动 Slot
        /// </summary>
        public async Task<bool> SetActiveSlotAsync(string slot, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            return await _firehose.SetActiveSlotAsync(slot, ct);
        }

        /// <summary>
        /// 修复 GPT
        /// </summary>
        public async Task<bool> FixGptAsync(int lun = -1, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            return await _firehose.FixGptAsync(lun, true, ct);
        }

        /// <summary>
        /// 设置启动 LUN
        /// </summary>
        public async Task<bool> SetBootLunAsync(int lun, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            return await _firehose.SetBootLunAsync(lun, ct);
        }

        /// <summary>
        /// 读取 Firehose 存储信息 (getstorageinfo)。
        /// </summary>
        public async Task<FirehoseStorageInfo> GetFirehoseStorageInfoAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return null;

            return await _firehose.GetStorageInfoDetailedAsync(ct);
        }

        /// <summary>
        /// Ping 测试连接
        /// </summary>
        public async Task<bool> PingAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            return await _firehose.PingAsync(ct);
        }

        /// <summary>
        /// 应用 Patch XML 文件
        /// </summary>
        public async Task<int> ApplyPatchXmlAsync(string patchXmlPath, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return 0;

            return await _firehose.ApplyPatchXmlAsync(patchXmlPath, ct);
        }

        /// <summary>
        /// 应用多个 Patch XML 文件
        /// </summary>
        public async Task<int> ApplyPatchFilesAsync(IEnumerable<string> patchFiles, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return 0;

            int totalPatches = 0;
            foreach (var patchFile in patchFiles)
            {
                if (ct.IsCancellationRequested) break;
                totalPatches += await _firehose.ApplyPatchXmlAsync(patchFile, ct);
            }
            return totalPatches;
        }

        #endregion

        #region 批量刷写

        /// <summary>
        /// 批量刷写分区
        /// </summary>
        public async Task<bool> FlashMultipleAsync(IEnumerable<FlashPartitionInfo> partitions, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
                return false;

            var list = new List<FlashPartitionInfo>(partitions);
            int total = list.Count;
            int current = 0;
            bool allSuccess = true;

            foreach (var p in list)
            {
                if (ct.IsCancellationRequested)
                    break;

                _log(string.Format("[高通] 刷写 [{0}/{1}] {2}", current + 1, total, p.Name));

                bool ok = await WritePartitionAsync(p.Name, p.Filename, null, ct);
                if (!ok)
                {
                    allSuccess = false;
                    _log("[高通] 刷写失败 - " + p.Name);
                }

                current++;
                if (progress != null)
                    progress.Report(100.0 * current / total);
            }

            return allSuccess;
        }

        #endregion
    }
}
