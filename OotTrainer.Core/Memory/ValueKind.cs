using System.Buffers.Binary;
using System.Globalization;

namespace OotTrainer.Core.Memory;

public enum ValueKind { Byte, Int16, Int32, Int64, Single, Double }

public static class ValueKindInfo
{
    public static int SizeOf(this ValueKind k) => k switch
    {
        ValueKind.Byte => 1,
        ValueKind.Int16 => 2,
        ValueKind.Int32 => 4,
        ValueKind.Int64 => 8,
        ValueKind.Single => 4,
        ValueKind.Double => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static bool IsFloat(this ValueKind k) => k is ValueKind.Single or ValueKind.Double;

    /// <summary>Decode to double for uniform comparison (exact for integers up to 2^53).</summary>
    public static double Read(ReadOnlySpan<byte> s, ValueKind k) => k switch
    {
        ValueKind.Byte => s[0],
        ValueKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(s),
        ValueKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(s),
        ValueKind.Int64 => BinaryPrimitives.ReadInt64LittleEndian(s),
        ValueKind.Single => BinaryPrimitives.ReadSingleLittleEndian(s),
        ValueKind.Double => BinaryPrimitives.ReadDoubleLittleEndian(s),
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static byte[] Encode(double v, ValueKind k)
    {
        var b = new byte[k.SizeOf()];
        switch (k)
        {
            case ValueKind.Byte: b[0] = checked((byte)v); break;
            case ValueKind.Int16: BinaryPrimitives.WriteInt16LittleEndian(b, checked((short)v)); break;
            case ValueKind.Int32: BinaryPrimitives.WriteInt32LittleEndian(b, checked((int)v)); break;
            case ValueKind.Int64: BinaryPrimitives.WriteInt64LittleEndian(b, checked((long)v)); break;
            case ValueKind.Single: BinaryPrimitives.WriteSingleLittleEndian(b, (float)v); break;
            case ValueKind.Double: BinaryPrimitives.WriteDoubleLittleEndian(b, v); break;
        }
        return b;
    }

    public static bool TryParseKind(string s, out ValueKind kind)
    {
        switch (s.ToLowerInvariant())
        {
            case "u8": case "byte": kind = ValueKind.Byte; return true;
            case "i16": case "u16": case "short": kind = ValueKind.Int16; return true;
            case "i32": case "u32": case "int": kind = ValueKind.Int32; return true;
            case "i64": case "u64": case "long": kind = ValueKind.Int64; return true;
            case "f32": case "float": kind = ValueKind.Single; return true;
            case "f64": case "double": kind = ValueKind.Double; return true;
            default: kind = default; return false;
        }
    }

    public static bool TryParseNumber(string s, out double v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h))
        {
            v = h;
            return true;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
