using System;
using System.Collections.Generic;

namespace WackeEdl.Qualcomm.Models
{
    /// <summary>
    /// Firehose getstorageinfo 结果。
    /// </summary>
    public sealed class FirehoseStorageInfo
    {
        public bool Success { get; set; }

        public string ResultValue { get; set; } = "";

        public string ErrorMessage { get; set; } = "";

        public Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<string> Logs { get; } = new List<string>();
    }
}
