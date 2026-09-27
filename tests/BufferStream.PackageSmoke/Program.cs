using System.Collections;
using System.IO;
using System.Numerics;
using System.Text;

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
ScalarWriteRoundTripSmoke.Run();

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

internal static class ScalarWriteRoundTripSmoke
{
    /// <summary>
    /// Exercises every scalar and direct-byte writer represented by the 37-member documentation-warning group.<br/>
    /// The checks cover matching scalar readers, framed value shapes, unframed byte paths, disposal guards, and explicit null rejection.<br/>
    /// </summary>
    public static void Run()
    {
        AssertRoundTrip(true, static (stream, value) => stream.Write(value), static stream => stream.ReadBoolean());
        AssertRoundTrip((byte)0xA5, static (stream, value) => stream.Write(value), static stream => stream.ReadByte());
        AssertRoundTrip((sbyte)-100, static (stream, value) => stream.Write(value), static stream => stream.ReadSByte());
        AssertRoundTrip((short)-12345, static (stream, value) => stream.Write(value), static stream => stream.ReadInt16());
        AssertRoundTrip((ushort)54321, static (stream, value) => stream.Write(value), static stream => stream.ReadUInt16());
        AssertRoundTrip(-123456789, static (stream, value) => stream.Write(value), static stream => stream.ReadInt32());
        AssertRoundTrip(3_456_789_012U, static (stream, value) => stream.Write(value), static stream => stream.ReadUInt32());
        AssertRoundTrip(-8_765_432_109_876_543_210L, static (stream, value) => stream.Write(value), static stream => stream.ReadInt64());
        AssertRoundTrip(17_654_321_098_765_432_109UL, static (stream, value) => stream.Write(value), static stream => stream.ReadUInt64());
        AssertRoundTrip(Int128.MinValue + 123, static (stream, value) => stream.Write(value), static stream => stream.ReadInt128());
        AssertRoundTrip(UInt128.MaxValue - 123, static (stream, value) => stream.Write(value), static stream => stream.ReadUInt128());
        AssertRoundTrip((Half)1.5, static (stream, value) => stream.Write(value), static stream => stream.ReadHalf());
        AssertRoundTrip(MathF.PI, static (stream, value) => stream.Write(value), static stream => stream.ReadSingle());
        AssertRoundTrip(Math.PI, static (stream, value) => stream.Write(value), static stream => stream.ReadDouble());
        AssertRoundTrip(-123456789.0123456789m, static (stream, value) => stream.Write(value), static stream => stream.ReadDecimal());
        AssertRoundTrip('\u03A9', static (stream, value) => stream.Write(value), static stream => stream.ReadChar());
        AssertRoundTrip(new Rune(0x1F680), static (stream, value) => stream.Write(value), static stream => stream.ReadRune());

        DateTime dateTime = new(2026, 9, 27, 15, 4, 5, DateTimeKind.Utc);
        AssertRoundTrip(dateTime, static (stream, value) => stream.Write(value), static stream => stream.ReadDateTime(), static (expected, actual) => expected.Ticks == actual.Ticks && expected.Kind == actual.Kind);

        DateTimeOffset dateTimeOffset = new(2026, 9, 27, 15, 4, 5, TimeSpan.FromHours(-7));
        AssertRoundTrip(dateTimeOffset, static (stream, value) => stream.Write(value), static stream => stream.ReadDateTimeOffset(), static (expected, actual) => expected.EqualsExact(actual));

        AssertRoundTrip(Guid.Parse("23d6f309-bb92-4e2c-b4b9-8cd6a0c49cbd"), static (stream, value) => stream.Write(value), static stream => stream.ReadGuid());
        AssertRoundTrip(new DateOnly(2026, 9, 27), static (stream, value) => stream.Write(value), static stream => stream.ReadDateOnly());
        AssertRoundTrip(TimeSpan.FromTicks(-123456789), static (stream, value) => stream.Write(value), static stream => stream.ReadTimeSpan());
        AssertRoundTrip(new TimeOnly(23, 59, 58, 999), static (stream, value) => stream.Write(value), static stream => stream.ReadTimeOnly());
        AssertRoundTrip(new Vector2(1.25f, -2.5f), static (stream, value) => stream.Write(value), static stream => stream.ReadVector2());
        AssertRoundTrip(new Vector3(1.25f, -2.5f, 3.75f), static (stream, value) => stream.Write(value), static stream => stream.ReadVector3());
        AssertRoundTrip(new Vector4(1.25f, -2.5f, 3.75f, -4.5f), static (stream, value) => stream.Write(value), static stream => stream.ReadVector4());
        AssertRoundTrip(new Complex(1.25, -2.5), static (stream, value) => stream.Write(value), static stream => stream.ReadComplex());
        AssertRoundTrip(Quaternion.CreateFromYawPitchRoll(0.25f, -0.5f, 0.75f), static (stream, value) => stream.Write(value), static stream => stream.ReadQuaternion());
        AssertRoundTrip(new Plane(new Vector3(1, 2, 3), -4), static (stream, value) => stream.Write(value), static stream => stream.ReadPlane());
        AssertRoundTrip(Matrix3x2.CreateRotation(0.25f) * Matrix3x2.CreateTranslation(2, -3), static (stream, value) => stream.Write(value), static stream => stream.ReadMatrix3x2());
        AssertRoundTrip(Matrix4x4.CreateRotationX(0.25f) * Matrix4x4.CreateTranslation(2, -3, 4), static (stream, value) => stream.Write(value), static stream => stream.ReadMatrix4x4());
        AssertRoundTrip(BigInteger.Pow(2, 200) - 12345, static (stream, value) => stream.Write(value), static stream => stream.ReadBigInteger());

        AssertMemoryRoundTrip();
        AssertBitArrayRoundTrip();
        AssertDirectByteWrites();
        AssertDisposedWritesRejected();
        AssertNullBitArrayRejected();
    }

    /// <summary>
    /// Writes one scalar through its public overload and reads it through the documented counterpart.<br/>
    /// The check requires value equivalence and exact consumption of the written representation.<br/>
    /// </summary>
    /// <typeparam name="T">The scalar value type.<br/></typeparam>
    /// <param name="expected">The value expected after decoding.<br/></param>
    /// <param name="write">The public scalar writer under test.<br/></param>
    /// <param name="read">The documented matching scalar reader.<br/></param>
    /// <param name="equals">An optional equality function for values whose default equality omits serialized state.<br/></param>
    private static void AssertRoundTrip<T>(T expected, Action<BufferStream, T> write, Func<BufferStream, T> read, Func<T, T, bool>? equals = null)
    {
        using var stream = new BufferStream();
        write(stream, expected);
        stream.Position = 0;

        T actual = read(stream);
        bool equivalent = equals?.Invoke(expected, actual) ?? EqualityComparer<T>.Default.Equals(expected, actual);
        if (!equivalent || stream.Position != stream.Length)
            throw new InvalidDataException($"{write.Method.Name}/{read.Method.Name} did not preserve the scalar value and consume exactly its representation.");
    }

    /// <summary>
    /// Verifies that the memory overload emits the documented byte-length-prefixed payload.<br/>
    /// The source is copied, decoded through the byte reader, and consumed as one complete frame.<br/>
    /// </summary>
    private static void AssertMemoryRoundTrip()
    {
        byte[] expected = Enumerable.Range(0, 80).Select(static value => (byte)(value * 3)).ToArray();
        using var stream = new BufferStream();
        stream.Write(expected.AsMemory());
        stream.Position = 0;

        byte[] actual = stream.ReadBytesWithByteLength();
        if (!actual.AsSpan().SequenceEqual(expected) || stream.Position != stream.Length)
            throw new InvalidDataException("Write(Memory<byte>) did not preserve its framed byte payload.");
    }

    /// <summary>
    /// Verifies packed-bit bytes and the separate logical bit count for a non-byte-aligned value.<br/>
    /// Every logical bit must survive and the reader must consume the complete representation.<br/>
    /// </summary>
    private static void AssertBitArrayRoundTrip()
    {
        var expected = new BitArray(new[] { true, false, true, true, false, false, true, false, true, true, false });
        using var stream = new BufferStream();
        stream.Write(expected);
        stream.Position = 0;

        BitArray actual = stream.ReadBitArray();
        if (actual.Length != expected.Length || stream.Position != stream.Length)
            throw new InvalidDataException("Write(BitArray) did not preserve the logical bit count.");
        for (int i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
                throw new InvalidDataException($"Write(BitArray) changed bit {i}.");
        }
    }

    /// <summary>
    /// Verifies the three unframed byte-range entry points in one deterministic byte sequence.<br/>
    /// The standard override, span overload, and array-range overload must concatenate without adding prefixes.<br/>
    /// </summary>
    private static void AssertDirectByteWrites()
    {
        byte[] source = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();
        using var stream = new BufferStream();
        stream.Write(source, 1, 4);
        stream.WriteBytes(source.AsSpan(5, 3));
        stream.WriteBytes(source, 8, 2);
        stream.Position = 0;

        byte[] actual = stream.ReadBytes(9);
        byte[] expected = source.AsSpan(1, 9).ToArray();
        if (!actual.AsSpan().SequenceEqual(expected) || stream.Position != stream.Length)
            throw new InvalidDataException("The unframed direct-byte writers changed data or emitted a prefix.");
    }

    /// <summary>
    /// Verifies that shared capacity-based write paths reject disposed streams before touching pooled storage.<br/>
    /// Both a scalar path and the span path are exercised because they previously reached the same unguarded helper.<br/>
    /// </summary>
    private static void AssertDisposedWritesRejected()
    {
        var scalar = new BufferStream();
        scalar.Dispose();
        ExpectException<ObjectDisposedException>(static stream => stream.Write(42), scalar, "disposed scalar write");

        var span = new BufferStream();
        span.Dispose();
        try
        {
            span.WriteBytes(new byte[] { 1, 2, 3 }.AsSpan());
            throw new InvalidDataException("The disposed span write was accepted.");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Verifies that a null bit array produces the documented argument exception.<br/>
    /// This prevents an implementation-detail null-reference exception from escaping the public API.<br/>
    /// </summary>
    private static void AssertNullBitArrayRejected()
    {
        using var stream = new BufferStream();
        ExpectException<ArgumentNullException>(static candidate => candidate.Write((BitArray)null!), stream, "null bit-array write");
    }

    /// <summary>
    /// Requires an operation to throw the specified exception type.<br/>
    /// A successful operation is converted into an explicit smoke-test failure with the supplied scenario name.<br/>
    /// </summary>
    /// <typeparam name="TException">The required exception type.<br/></typeparam>
    /// <param name="operation">The operation expected to fail.<br/></param>
    /// <param name="stream">The stream supplied to the operation.<br/></param>
    /// <param name="scenario">A concise scenario name for diagnostics.<br/></param>
    private static void ExpectException<TException>(Action<BufferStream> operation, BufferStream stream, string scenario)
        where TException : Exception
    {
        try
        {
            operation(stream);
            throw new InvalidDataException($"The {scenario} was accepted.");
        }
        catch (TException)
        {
        }
    }
}
