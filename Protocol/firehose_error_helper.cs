namespace WackeEdl.Qualcomm.Protocol;

public static class FirehoseErrorHelper
{
	public static void ParseNakError(string errorText, out string message, out string suggestion, out bool isFatal, out bool canRetry)
	{
		message = "未知错误";
		suggestion = "请重试操作";
		isFatal = false;
		canRetry = true;
		if (!string.IsNullOrEmpty(errorText))
		{
			string text = errorText.ToLowerInvariant();
			if (text.Contains("authentication") || text.Contains("auth failed"))
			{
				message = "认证失败";
				suggestion = "设备需要特殊认证";
				isFatal = true;
				canRetry = false;
			}
			else if (text.Contains("signature") || text.Contains("sign"))
			{
				message = "签名验证失败";
				suggestion = "镜像签名不正确";
				isFatal = true;
				canRetry = false;
			}
			else if (text.Contains("hash") && (text.Contains("mismatch") || text.Contains("fail")))
			{
				message = "Hash 校验失败";
				suggestion = "数据完整性验证失败";
				isFatal = true;
				canRetry = false;
			}
			else if (text.Contains("partition not found"))
			{
				message = "分区未找到";
				suggestion = "设备上不存在此分区";
				isFatal = true;
				canRetry = false;
			}
			else if (text.Contains("invalid lun"))
			{
				message = "无效的 LUN";
				suggestion = "指定的 LUN 不存在";
				isFatal = true;
				canRetry = false;
			}
			else if (text.Contains("write protect"))
			{
				message = "写保护";
				suggestion = "存储设备处于写保护状态";
				isFatal = true;
				canRetry = false;
			}
			else if (text.Contains("timeout"))
			{
				message = "超时";
				suggestion = "操作超时，建议重试";
				isFatal = false;
				canRetry = true;
			}
			else if (text.Contains("busy"))
			{
				message = "设备忙";
				suggestion = "设备正在处理其他操作";
				isFatal = false;
				canRetry = true;
			}
			else
			{
				message = "设备错误: " + errorText;
				suggestion = "请查看完整错误信息";
			}
		}
	}
}
