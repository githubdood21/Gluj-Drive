namespace GlujDrive.Infrastructure.Semantic;

internal static class HalfVectorCodec
{
    public static byte[] Encode(ReadOnlySpan<float> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(ushort))];

        for (var index = 0; index < values.Length; index++)
        {
            var bits = BitConverter.HalfToUInt16Bits((Half)values[index]);
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(ushort), sizeof(ushort)), bits);
        }

        return bytes;
    }

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(ushort) != 0)
        {
            throw new InvalidDataException("The stored semantic vector has an invalid length.");
        }

        var values = new float[bytes.Length / sizeof(ushort)];

        for (var index = 0; index < values.Length; index++)
        {
            var bits = BitConverter.ToUInt16(bytes.Slice(index * sizeof(ushort), sizeof(ushort)));
            values[index] = (float)BitConverter.UInt16BitsToHalf(bits);
        }

        return values;
    }
}
