using System.IO;
using System.Numerics;

using var buffer = new BufferStream();
buffer.Write(42);
buffer.Write('A');
buffer.Write("package smoke");

buffer.Position = 0;
if (buffer.ReadInt32() != 42 || buffer.ReadChar() != 'A' || buffer.ReadString() != "package smoke")
    throw new InvalidDataException("The packaged BufferStream binary did not preserve the expected primitive, character, and string round trip.");

using var truncatedCharacter = new BufferStream(new byte[1].AsMemory());
try
{
    _ = truncatedCharacter.ReadChar();
    throw new InvalidDataException("ReadChar accepted a truncated character payload.");
}
catch (EndOfStreamException)
{
    if (truncatedCharacter.Position != 0)
        throw new InvalidDataException("ReadChar advanced the cursor after rejecting a truncated character payload.");
}

using var peekBuffer = new BufferStream();
peekBuffer.Write((byte)0x5A);
peekBuffer.Position = 0;
if (peekBuffer.PeekByte() != 0x5A || peekBuffer[0] != 0x5A || peekBuffer.Position != 0)
    throw new InvalidDataException("PeekByte or the byte indexer did not preserve the current cursor position.");

peekBuffer.Position = peekBuffer.Length;
try
{
    _ = peekBuffer.PeekByte();
    throw new InvalidDataException("PeekByte accepted an end-of-stream position.");
}
catch (EndOfStreamException)
{
    if (peekBuffer.Position != peekBuffer.Length)
        throw new InvalidDataException("PeekByte changed the cursor after rejecting an end-of-stream position.");
}

var disposedBuffer = new BufferStream();
disposedBuffer.Dispose();
try
{
    _ = disposedBuffer.PeekByte();
    throw new InvalidDataException("PeekByte accepted a disposed stream.");
}
catch (ObjectDisposedException)
{
}

CollectionRoundTripSmoke.Run();

Console.WriteLine("BufferStream package smoke test passed.");

internal static class CollectionRoundTripSmoke
{
    /// <summary>
    /// Exercises every public collection reader/writer pair through the installed NuGet package.<br/>
    /// Coverage includes empty and populated sequences, nullable strings, version arities, sliced byte frames, and rejected malformed frames.<br/>
    /// </summary>
    public static void Run()
    {
        AssertRoundTrip(new[] { new BigInteger(-123456789), BigInteger.Zero, BigInteger.Pow(2, 100) }, static (stream, values) => stream.WriteBigIntegersWithCount(values), static stream => stream.ReadBigIntegersWithCount());
        AssertRoundTrip(new[] { false, true, true }, static (stream, values) => stream.WriteBooleansWithByteLength(values), static stream => stream.ReadBooleansWithByteLength());
        AssertRoundTrip(new byte[] { 0, 1, 127, 128, 255 }, static (stream, values) => stream.WriteBytesWithByteLength(values), static stream => stream.ReadBytesWithByteLength());
        AssertRoundTrip(new[] { '\0', 'A', '\u03A9' }, static (stream, values) => stream.WriteCharsWithByteLength(values), static stream => stream.ReadCharsWithByteLength());
        AssertRoundTrip(new[] { new Complex(1.25, -2.5), new Complex(0, 10) }, static (stream, values) => stream.WriteComplexesWithByteLength(values), static stream => stream.ReadComplexesWithByteLength());
        AssertRoundTrip(new[] { new DateOnly(2000, 1, 1), new DateOnly(2026, 9, 27) }, static (stream, values) => stream.WriteDateOnlysWithByteLength(values), static stream => stream.ReadDateOnlysWithByteLength());
        AssertRoundTrip(new[] { new DateTimeOffset(2026, 9, 27, 14, 30, 0, TimeSpan.FromHours(-7)), DateTimeOffset.UnixEpoch }, static (stream, values) => stream.WriteDateTimeOffsetsWithByteLength(values), static stream => stream.ReadDateTimeOffsetsWithByteLength());
        AssertRoundTrip(new[] { DateTime.UnixEpoch, new DateTime(2026, 9, 27, 14, 30, 0, DateTimeKind.Local) }, static (stream, values) => stream.WriteDateTimesWithByteLength(values), static stream => stream.ReadDateTimesWithByteLength());
        AssertRoundTrip(new[] { decimal.MinValue, 0m, 12345.6789m }, static (stream, values) => stream.WriteDecimalsWithCount(values), static stream => stream.ReadDecimalsWithCount());
        AssertRoundTrip(new[] { double.MinValue, 0.0, Math.PI }, static (stream, values) => stream.WriteDoublesWithByteLength(values), static stream => stream.ReadDoublesWithByteLength());
        AssertRoundTrip(new[] { Guid.Empty, Guid.Parse("b0fb718e-e71a-4a28-871b-ef1235e687df") }, static (stream, values) => stream.WriteGuidsWithByteLength(values), static stream => stream.ReadGuidsWithByteLength());
        AssertRoundTrip(new[] { Half.MinValue, (Half)0, (Half)1.5 }, static (stream, values) => stream.WriteHalfsWithByteLength(values), static stream => stream.ReadHalfsWithByteLength());
        AssertRoundTrip(new[] { Int128.MinValue, (Int128)0, Int128.MaxValue }, static (stream, values) => stream.WriteInt128sWithByteLength(values), static stream => stream.ReadInt128sWithByteLength());
        AssertRoundTrip(new short[] { short.MinValue, 0, short.MaxValue }, static (stream, values) => stream.WriteInt16sWithByteLength(values), static stream => stream.ReadInt16sWithByteLength());
        AssertRoundTrip(new[] { int.MinValue, 0, int.MaxValue }, static (stream, values) => stream.WriteInt32sWithByteLength(values), static stream => stream.ReadInt32sWithByteLength());
        AssertRoundTrip(new[] { long.MinValue, 0, long.MaxValue }, static (stream, values) => stream.WriteInt64sWithByteLength(values), static stream => stream.ReadInt64sWithByteLength());
        AssertRoundTrip(new[] { Matrix3x2.Identity, Matrix3x2.CreateScale(2.5f) }, static (stream, values) => stream.WriteMatrix3x2sWithByteLength(values), static stream => stream.ReadMatrix3x2sWithByteLength());
        AssertRoundTrip(new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(1, 2, 3) }, static (stream, values) => stream.WriteMatrix4x4sWithByteLength(values), static stream => stream.ReadMatrix4x4sWithByteLength());
        AssertRoundTrip(new[] { new Plane(Vector3.UnitY, 1), new Plane(Vector3.UnitZ, -2) }, static (stream, values) => stream.WritePlanesWithByteLength(values), static stream => stream.ReadPlanesWithByteLength());
        AssertRoundTrip(new[] { Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f) }, static (stream, values) => stream.WriteQuaternionsWithByteLength(values), static stream => stream.ReadQuaternionsWithByteLength());
        AssertRoundTrip(new sbyte[] { sbyte.MinValue, 0, sbyte.MaxValue }, static (stream, values) => stream.WriteSBytesWithByteLength(values), static stream => stream.ReadSBytesWithByteLength());
        AssertRoundTrip(new[] { float.MinValue, 0f, MathF.PI }, static (stream, values) => stream.WriteSinglesWithByteLength(values), static stream => stream.ReadSinglesWithByteLength());
        AssertRoundTrip<string?>(new string?[] { null, string.Empty, "BufferStream", "\u03A9" }, static (stream, values) => stream.WriteStringsWithCount(values), static stream => stream.ReadStringsWithCount());
        AssertRoundTrip(new[] { TimeOnly.MinValue, new TimeOnly(23, 59, 59, 999) }, static (stream, values) => stream.WriteTimeOnlysWithByteLength(values), static stream => stream.ReadTimeOnlysWithByteLength());
        AssertRoundTrip(new[] { TimeSpan.MinValue, TimeSpan.Zero, TimeSpan.MaxValue }, static (stream, values) => stream.WriteTimeSpansWithByteLength(values), static stream => stream.ReadTimeSpansWithByteLength());
        AssertRoundTrip(new[] { UInt128.MinValue, (UInt128)1, UInt128.MaxValue }, static (stream, values) => stream.WriteUInt128sWithByteLength(values), static stream => stream.ReadUInt128sWithByteLength());
        AssertRoundTrip(new ushort[] { ushort.MinValue, 1, ushort.MaxValue }, static (stream, values) => stream.WriteUInt16sWithByteLength(values), static stream => stream.ReadUInt16sWithByteLength());
        AssertRoundTrip(new[] { uint.MinValue, 1U, uint.MaxValue }, static (stream, values) => stream.WriteUInt32sWithByteLength(values), static stream => stream.ReadUInt32sWithByteLength());
        AssertRoundTrip(new[] { ulong.MinValue, 1UL, ulong.MaxValue }, static (stream, values) => stream.WriteUInt64sWithByteLength(values), static stream => stream.ReadUInt64sWithByteLength());
        AssertRoundTrip(new[] { Vector2.Zero, new Vector2(1.25f, -2.5f) }, static (stream, values) => stream.WriteVector2sWithByteLength(values), static stream => stream.ReadVector2sWithByteLength());
        AssertRoundTrip(new[] { Vector3.Zero, new Vector3(1.25f, -2.5f, 3.75f) }, static (stream, values) => stream.WriteVector3sWithByteLength(values), static stream => stream.ReadVector3sWithByteLength());
        AssertRoundTrip(new[] { Vector4.Zero, new Vector4(1.25f, -2.5f, 3.75f, -4.5f) }, static (stream, values) => stream.WriteVector4sWithByteLength(values), static stream => stream.ReadVector4sWithByteLength());
        AssertRoundTrip(new[] { new Version(1, 2), new Version(3, 4, 5), new Version(6, 7, 8, 9) }, static (stream, values) => stream.WriteVersionsWithCount(values), static stream => stream.ReadVersionsWithCount());

        AssertSlicedByteRoundTrip();
        AssertMalformedFramesRejected();
    }

    /// <summary>
    /// Verifies both empty and populated instances of a collection framing pair.<br/>
    /// The reader must reproduce every value, consume exactly the frame, and use the same public package API a consumer compiles against.<br/>
    /// </summary>
    /// <typeparam name="T">The collection element type.<br/></typeparam>
    /// <param name="expected">The populated sequence to round-trip.<br/></param>
    /// <param name="write">The canonical collection writer.<br/></param>
    /// <param name="read">The matching canonical collection reader.<br/></param>
    private static void AssertRoundTrip<T>(T[] expected, Action<BufferStream, T[]> write, Func<BufferStream, T[]> read)
    {
        AssertOneRoundTrip(Array.Empty<T>(), write, read);
        AssertOneRoundTrip(expected, write, read);
    }

    /// <summary>
    /// Writes and reads one sequence and verifies value equality plus exact frame consumption.<br/>
    /// A new stream is used for each case so no prior position or capacity state can mask a framing error.<br/>
    /// </summary>
    /// <typeparam name="T">The collection element type.<br/></typeparam>
    /// <param name="expected">The exact sequence expected after decoding.<br/></param>
    /// <param name="write">The canonical collection writer.<br/></param>
    /// <param name="read">The matching canonical collection reader.<br/></param>
    private static void AssertOneRoundTrip<T>(T[] expected, Action<BufferStream, T[]> write, Func<BufferStream, T[]> read)
    {
        using var stream = new BufferStream();
        write(stream, expected);
        stream.Position = 0;

        T[] actual = read(stream);
        if (!actual.SequenceEqual(expected) || stream.Position != stream.Length)
            throw new InvalidDataException($"{write.Method.Name}/{read.Method.Name} did not preserve values and consume exactly one frame.");
    }

    /// <summary>
    /// Verifies the byte-range overload with a payload large enough to require a multi-byte signed 7-bit prefix.<br/>
    /// This guards the historical mismatch between direct unsigned prefix emission and the reader's signed decoding path.<br/>
    /// </summary>
    private static void AssertSlicedByteRoundTrip()
    {
        byte[] source = Enumerable.Range(0, 100).Select(static value => (byte)value).ToArray();
        using var stream = new BufferStream();
        stream.WriteBytesWithByteLength(source, 10, 80);
        stream.Position = 0;

        byte[] actual = stream.ReadBytesWithByteLength();
        if (!actual.AsSpan().SequenceEqual(source.AsSpan(10, 80)) || stream.Position != stream.Length)
            throw new InvalidDataException("WriteBytesWithByteLength(byte[], int, int) did not preserve a multi-byte length frame.");
    }

    /// <summary>
    /// Verifies deterministic rejection of structurally invalid and incomplete collection frames.<br/>
    /// Byte-length frames reject non-divisible or truncated payloads, while count frames reject negative or incomplete element counts.<br/>
    /// </summary>
    private static void AssertMalformedFramesRejected()
    {
        using (var nonDivisible = new BufferStream())
        {
            nonDivisible.Write7BitEncodedInt(3);
            nonDivisible.WriteBytes(new byte[] { 1, 2, 3 });
            nonDivisible.Position = 0;
            ExpectException<InvalidDataException>(static stream => stream.ReadInt16sWithByteLength(), nonDivisible, 1, "non-divisible byte length");
        }

        using (var truncated = new BufferStream())
        {
            truncated.Write7BitEncodedInt(8);
            truncated.WriteBytes(new byte[] { 1, 2, 3, 4 });
            truncated.Position = 0;
            ExpectException<EndOfStreamException>(static stream => stream.ReadInt32sWithByteLength(), truncated, 1, "truncated byte payload");
        }

        using (var negativeCount = new BufferStream())
        {
            negativeCount.Write7BitEncodedInt(-1);
            negativeCount.Position = 0;
            ExpectException<InvalidDataException>(static stream => stream.ReadStringsWithCount(), negativeCount, 1, "negative logical count");
        }

        using (var truncatedCount = new BufferStream())
        {
            truncatedCount.Write7BitEncodedInt(1);
            truncatedCount.Position = 0;
            ExpectException<EndOfStreamException>(static stream => stream.ReadDecimalsWithCount(), truncatedCount, 1, "truncated count payload");
        }
    }

    /// <summary>
    /// Requires an operation to throw a specific exception without consuming bytes beyond the validated prefix.<br/>
    /// The expected position makes failure atomicity explicit for structural checks performed before payload decoding.<br/>
    /// </summary>
    /// <typeparam name="TException">The exception type required from the malformed frame.<br/></typeparam>
    /// <param name="operation">The collection read operation under test.<br/></param>
    /// <param name="stream">The malformed input stream positioned at its frame prefix.<br/></param>
    /// <param name="expectedPosition">The position expected after rejection.<br/></param>
    /// <param name="scenario">A concise scenario name for test diagnostics.<br/></param>
    private static void ExpectException<TException>(Action<BufferStream> operation, BufferStream stream, long expectedPosition, string scenario)
        where TException : Exception
    {
        try
        {
            operation(stream);
            throw new InvalidDataException($"The {scenario} frame was accepted.");
        }
        catch (TException)
        {
            if (stream.Position != expectedPosition)
                throw new InvalidDataException($"The {scenario} frame advanced to {stream.Position}; expected {expectedPosition}.");
        }
    }
}
