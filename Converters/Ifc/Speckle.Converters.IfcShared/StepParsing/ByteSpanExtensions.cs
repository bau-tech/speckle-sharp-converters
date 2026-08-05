using System.Globalization;
using System.Runtime.CompilerServices;
using Ara3D.Buffers;

namespace Speckle.Converters.IfcShared.StepParsing;

public static class ByteSpanExtensions
{
  // FIX (not in the original ported code): STEP/IFC numeric literals are always period-decimal,
  // regardless of the machine's locale. double.Parse/int.Parse without an explicit culture use the
  // current thread culture - on a comma-decimal locale (e.g. de-DE, confirmed present on the machine
  // this was built on), '.' is read as a thousands separator and silently dropped, corrupting every
  // parsed number by roughly 10^(digit count after the decimal point). Caught by a unit test
  // asserting an exact real-file depth value (3500.0000000011578mm parsed as ~3.5x10^16).
  // net8.0's double.Parse/int.Parse(ReadOnlySpan<byte>, IFormatProvider) is the UTF8-span-parsing
  // overload (.NET 7+'s IUtf8SpanParsable) - ByteSpan.ToSpan() returns ReadOnlySpan<byte> (raw
  // ASCII bytes, see Ara3D.Buffers), and this overload parses those bytes directly. net48 has no
  // such overload at all - falls back to a string allocation there.
  //
  // BUG FIX: the net48 fallback originally called `self.ToSpan().ToString()` - but ToSpan() returns
  // ReadOnlySpan<byte>, and ReadOnlySpan<T>.ToString() for a non-char T does NOT decode the bytes as
  // text - it returns the type name ("System.ReadOnlySpan`1[System.Byte]"), which obviously never
  // parses as a number. This made EVERY numeric value in EVERY file fail to parse on net48 (found
  // from a live Tekla receive: enrichment completed without throwing - the TryReadNumber fix already
  // applied caught it - but enriched exactly 0 objects, since literally no number could be read).
  // The fix is ByteSpan's OWN ToString() override (Encoding.ASCII.GetString(Ptr, Length)), not
  // ReadOnlySpan<byte>'s.
#if NET8_0_OR_GREATER
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static double ToDouble(this ByteSpan self) => double.Parse(self.ToSpan(), CultureInfo.InvariantCulture);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int ToInt(this ByteSpan self) => int.Parse(self.ToSpan(), CultureInfo.InvariantCulture);
#else
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static double ToDouble(this ByteSpan self) => double.Parse(self.ToString(), CultureInfo.InvariantCulture);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int ToInt(this ByteSpan self) => int.Parse(self.ToString(), CultureInfo.InvariantCulture);
#endif
}
