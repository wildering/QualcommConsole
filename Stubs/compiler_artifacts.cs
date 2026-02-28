using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[CompilerGenerated]
internal static class _003CPrivateImplementationDetails_003E
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref TElement InlineArrayElementRef<TBuffer, TElement>(ref TBuffer buffer, int index)
        where TBuffer : struct
    {
        return ref Unsafe.Add(ref Unsafe.As<TBuffer, TElement>(ref buffer), index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<TElement> InlineArrayAsReadOnlySpan<TBuffer, TElement>(in TBuffer buffer, int length)
        where TBuffer : struct
    {
        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<TBuffer, TElement>(ref Unsafe.AsRef(in buffer)), length);
    }
}

[StructLayout(LayoutKind.Auto)]
[InlineArray(2)]
internal struct _003C_003Ey__InlineArray2<T>
{
    private T _element0;
}

[StructLayout(LayoutKind.Auto)]
[InlineArray(4)]
internal struct _003C_003Ey__InlineArray4<T>
{
    private T _element0;
}

[StructLayout(LayoutKind.Auto)]
[InlineArray(5)]
internal struct _003C_003Ey__InlineArray5<T>
{
    private T _element0;
}

[StructLayout(LayoutKind.Auto)]
[InlineArray(6)]
internal struct _003C_003Ey__InlineArray6<T>
{
    private T _element0;
}

[StructLayout(LayoutKind.Auto)]
[InlineArray(7)]
internal struct _003C_003Ey__InlineArray7<T>
{
    private T _element0;
}

[StructLayout(LayoutKind.Auto)]
[InlineArray(8)]
internal struct _003C_003Ey__InlineArray8<T>
{
    private T _element0;
}

[StructLayout(LayoutKind.Auto)]
[InlineArray(9)]
internal struct _003C_003Ey__InlineArray9<T>
{
    private T _element0;
}

[StructLayout(LayoutKind.Auto)]
[InlineArray(10)]
internal struct _003C_003Ey__InlineArray10<T>
{
    private T _element0;
}
