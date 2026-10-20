using System.Runtime.InteropServices;

namespace Fat32Fmt.Core.IO;

/// <summary>
/// アドレスが整列したネイティブメモリ。FILE_FLAG_NO_BUFFERING での I/O にはセクタ境界に整列したバッファが必要。
/// </summary>
public sealed unsafe class AlignedBuffer : IDisposable
{
    public const int DefaultAlignment = 4096;

    private void* _ptr;

    public AlignedBuffer(int size, int alignment = DefaultAlignment)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        _ptr = NativeMemory.AlignedAlloc((nuint)size, (nuint)alignment);
        NativeMemory.Clear(_ptr, (nuint)size);
        Size = size;
    }

    public int Size { get; }

    public void* Pointer => _ptr != null ? _ptr : throw new ObjectDisposedException(nameof(AlignedBuffer));

    public Span<byte> Span => new(Pointer, Size);

    public Span<byte> Slice(int length) => new(Pointer, length);

    public void Dispose()
    {
        if (_ptr != null)
        {
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
    }
}
