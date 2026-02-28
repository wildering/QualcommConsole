using System;

namespace WackeEdl.Qualcomm.Common
{
    /// <summary>
    /// 敏感数据显示策略（默认脱敏，显式 debug 开启明文）。
    /// </summary>
    public static class SensitiveDataPolicy
    {
        private static volatile bool _allowSensitiveData;

        public static bool AllowSensitiveData
        {
            get => _allowSensitiveData;
            set => _allowSensitiveData = value;
        }

        public static string DisplayImei(string imei)
        {
            if (string.IsNullOrWhiteSpace(imei))
                return imei;

            if (AllowSensitiveData)
                return imei;

            return MaskImei(imei);
        }

        public static string MaskImei(string imei)
        {
            if (string.IsNullOrWhiteSpace(imei))
                return imei;

            string trimmed = imei.Trim();
            if (trimmed.Length <= 6)
                return new string('*', trimmed.Length);

            string head = trimmed.Substring(0, 2);
            string tail = trimmed.Substring(trimmed.Length - 4, 4);
            return head + new string('*', trimmed.Length - 6) + tail;
        }
    }
}
