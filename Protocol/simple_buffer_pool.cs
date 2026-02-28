using System.Collections.Concurrent;

namespace WackeEdl.Qualcomm.Protocol;

internal static class SimpleBufferPool
{
	private static readonly ConcurrentBag<byte[]> _pool16MB = new ConcurrentBag<byte[]>();

	private static readonly ConcurrentBag<byte[]> _pool4MB = new ConcurrentBag<byte[]>();

	private const int SIZE_16MB = 16777216;

	private const int SIZE_4MB = 4194304;

	public static byte[] Rent(int minSize)
	{
		if (minSize <= 4194304)
		{
			if (_pool4MB.TryTake(out var result))
			{
				return result;
			}
			return new byte[4194304];
		}
		if (minSize <= 16777216)
		{
			if (_pool16MB.TryTake(out var result2))
			{
				return result2;
			}
			return new byte[16777216];
		}
		return new byte[minSize];
	}

	public static void Return(byte[] buffer)
	{
		if (buffer != null)
		{
			if (buffer.Length == 4194304 && _pool4MB.Count < 4)
			{
				_pool4MB.Add(buffer);
			}
			else if (buffer.Length == 16777216 && _pool16MB.Count < 2)
			{
				_pool16MB.Add(buffer);
			}
		}
	}
}
