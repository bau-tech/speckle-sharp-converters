using System.Diagnostics;
using Ara3D.Buffers;
#if !NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace Speckle.Converters.IfcShared.StepParsing;

public static class AlignedMemoryReader
{
  public static unsafe AlignedMemory ReadAllBytes(string path, int bufferSize = 1024 * 1024)
  {
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, false);
    var fileLength = fs.Length;
    if (fileLength > int.MaxValue)
      throw new IOException("File too big: > 2GB");

    var count = (int)fileLength;
    var r = new AlignedMemory(count);
    var pBytes = r.BytePtr;
#if NET8_0_OR_GREATER
    while (count > 0)
    {
      var span = new Span<byte>(pBytes, count);
      var n = fs.Read(span);
      if (n == 0)
        break;
      pBytes += n;
      count -= n;
    }
#else
    // net48's FileStream has no Span<byte>-accepting Read overload at all - reads into a plain
    // byte[] buffer and copies into the unmanaged pointer instead.
    var buffer = new byte[Math.Min(bufferSize, count)];
    while (count > 0)
    {
      var toRead = Math.Min(buffer.Length, count);
      var n = fs.Read(buffer, 0, toRead);
      if (n == 0)
        break;
      Marshal.Copy(buffer, 0, (IntPtr)pBytes, n);
      pBytes += n;
      count -= n;
    }
#endif

    Debug.Assert(count == 0);
    return r;
  }
}
