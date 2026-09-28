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
PositionalWriteSmoke.Run();
SevenBitEncodingSmoke.Run();
DirectReaderConversionEqualitySmoke.Run();

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
        AssertAllocationSensitivePaths();
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

    /// <summary>
    /// Verifies the allocation-sensitive decimal, memory-frame, and span-based Stream paths through the packaged public surface.<br/>
    /// Warm scalar reads and framed-memory writes must allocate no managed bytes, while Stream-typed span calls must preserve exact bytes, cursor movement, end-of-stream behavior, and fixed-segment failure atomicity.<br/>
    /// </summary>
    private static void AssertAllocationSensitivePaths()
    {
        const decimal expectedDecimal = -123456789.0123456789m;
        using var decimalStream = new BufferStream();
        decimalStream.Write(expectedDecimal);
        for (int i = 0; i < 20_000; i++)
        {
            decimalStream.Position = 0;
            _ = decimalStream.ReadDecimal();
        }

        long decimalBefore = GC.GetAllocatedBytesForCurrentThread();
        decimal checksum = 0;
        for (int i = 0; i < 10_000; i++)
        {
            decimalStream.Position = 0;
            checksum += decimalStream.ReadDecimal();
        }
        long decimalAllocated = GC.GetAllocatedBytesForCurrentThread() - decimalBefore;
        if (decimalAllocated != 0 || checksum != expectedDecimal * 10_000)
            throw new InvalidDataException($"ReadDecimal allocated {decimalAllocated} managed bytes or changed its value during the warmed loop.");

        byte[] source = Enumerable.Range(0, 80).Select(static value => (byte)(value * 3)).ToArray();
        using var memoryStream = new BufferStream(128);
        for (int i = 0; i < 20_000; i++)
        {
            memoryStream.Reset();
            memoryStream.Write(source.AsMemory());
        }

        long memoryBefore = GC.GetAllocatedBytesForCurrentThread();
        long lengthChecksum = 0;
        for (int i = 0; i < 10_000; i++)
        {
            memoryStream.Reset();
            memoryStream.Write(source.AsMemory());
            lengthChecksum += memoryStream.Length;
        }
        long memoryAllocated = GC.GetAllocatedBytesForCurrentThread() - memoryBefore;
        if (memoryAllocated != 0 || lengthChecksum != 820_000)
            throw new InvalidDataException($"Write(Memory<byte>) allocated {memoryAllocated} managed bytes or changed its framed length during the warmed loop.");

        using var spanStream = new BufferStream();
        Stream standard = spanStream;
        standard.Write(source.AsSpan(5, 7));
        standard.Position = 0;
        Span<byte> destination = stackalloc byte[9];
        int copied = standard.Read(destination);
        if (copied != 7 || !destination[..copied].SequenceEqual(source.AsSpan(5, 7)) || standard.Read(destination) != 0)
            throw new InvalidDataException("Stream-typed span overrides changed bytes, cursor movement, or end-of-stream behavior.");

        using var owner = new BufferStream();
        owner.Write((byte)0xCC);
        using BufferStream fixedSegment = owner.Segment(0, 1);
        ExpectException<ArgumentOutOfRangeException>(static stream => stream.Write(new byte[] { 1, 2 }.AsMemory()), fixedSegment, "oversized fixed-segment memory write");
        if (fixedSegment.Position != 0 || owner.PeekByte(0) != 0xCC)
            throw new InvalidDataException("The rejected fixed-segment memory write partially changed the destination.");
    }
}

internal static class PositionalWriteSmoke
{
    /// <summary>
    /// Exercises every positional-write overload through the installed package.<br/>
    /// The checks require sequential wire compatibility, unchanged stream state, overflow-safe bounds, reserved-string enforcement, overlap safety, and disposal rejection.<br/>
    /// </summary>
    public static void Run()
    {
        AssertScalarRoundTrip((byte)0xA5, 1, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadByte());
        AssertScalarRoundTrip((sbyte)-100, 1, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadSByte());
        AssertScalarRoundTrip((short)-12345, 2, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadInt16());
        AssertScalarRoundTrip((ushort)54321, 2, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadUInt16());
        AssertScalarRoundTrip(-123456789, 4, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadInt32());
        AssertScalarRoundTrip(3_456_789_012U, 4, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadUInt32());
        AssertScalarRoundTrip(-8_765_432_109_876_543_210L, 8, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadInt64());
        AssertScalarRoundTrip(17_654_321_098_765_432_109UL, 8, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadUInt64());
        AssertScalarRoundTrip(MathF.PI, 4, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadSingle());
        AssertScalarRoundTrip(Math.PI, 8, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadDouble());
        AssertScalarRoundTrip(-123456789.0123456789m, 16, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadDecimal());
        AssertScalarRoundTrip(true, 1, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadBoolean());
        AssertScalarRoundTrip('\u03A9', 2, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadChar());
        AssertScalarRoundTrip(Guid.Parse("23d6f309-bb92-4e2c-b4b9-8cd6a0c49cbd"), 16, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadGuid());

        DateTime dateTime = new(2026, 9, 27, 15, 4, 5, DateTimeKind.Utc);
        AssertScalarRoundTrip(dateTime, 8, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadDateTime(), static (expected, actual) => expected.Ticks == actual.Ticks && expected.Kind == actual.Kind);
        AssertScalarRoundTrip(TimeSpan.FromTicks(-123456789), 8, static (stream, offset, value) => stream.WriteAtOffset(offset, value), static stream => stream.ReadTimeSpan());

        AssertSpanAndArrayCopies();
        AssertStringRoundTrips();
        AssertBoundaries();
        AssertDisposedWritesRejected();
        AssertUnsignedOffsetCompatibility();
    }

    /// <summary>
    /// Patches one fixed-width scalar inside existing data and reads it with the corresponding sequential reader.<br/>
    /// Position and length must remain unchanged by the patch, including when the value ends exactly at the stream boundary.<br/>
    /// </summary>
    /// <typeparam name="T">The scalar value type.<br/></typeparam>
    /// <param name="expected">The value expected after decoding.<br/></param>
    /// <param name="size">The serialized fixed width in bytes.<br/></param>
    /// <param name="write">The positional writer under test.<br/></param>
    /// <param name="read">The matching sequential reader.<br/></param>
    /// <param name="equals">An optional equality function for values with additional serialized state.<br/></param>
    private static void AssertScalarRoundTrip<T>(T expected, int size, Action<BufferStream, int, T> write, Func<BufferStream, T> read, Func<T, T, bool>? equals = null)
    {
        using var stream = new BufferStream();
        stream.SetLength(size + 2);
        stream.Position = 1;
        long originalPosition = stream.Position;
        long originalLength = stream.Length;

        write(stream, 2, expected);
        if (stream.Position != originalPosition || stream.Length != originalLength)
            throw new InvalidDataException($"{write.Method.Name} changed position or length.");

        stream.Position = 2;
        T actual = read(stream);
        bool equivalent = equals?.Invoke(expected, actual) ?? EqualityComparer<T>.Default.Equals(expected, actual);
        if (!equivalent || stream.Position != originalLength)
            throw new InvalidDataException($"{write.Method.Name}/{read.Method.Name} did not preserve the scalar representation at the exact end boundary.");
    }

    /// <summary>
    /// Verifies raw span and array-range overwrites, including overlapping source and destination storage.<br/>
    /// Both overloads must provide memmove-style overlap behavior while preserving position and length.<br/>
    /// </summary>
    private static void AssertSpanAndArrayCopies()
    {
        using (var stream = new BufferStream())
        {
            stream.WriteBytes(new byte[] { 0, 1, 2, 3, 4, 5 });
            stream.Position = 1;
            long originalLength = stream.Length;
            stream.WriteAtOffset(2, stream.AsReadOnlySpan.Slice(0, 4));
            if (!stream.AsReadOnlySpan.SequenceEqual(new byte[] { 0, 1, 0, 1, 2, 3 }) || stream.Position != 1 || stream.Length != originalLength)
                throw new InvalidDataException("WriteAtOffset(int, ReadOnlySpan<byte>) did not preserve overlapping-copy or stream-state semantics.");
        }

        byte[] storage = { 0, 1, 2, 3, 4, 5 };
        using (var stream = new BufferStream(storage.AsMemory()))
        {
            stream.Position = 1;
            long originalLength = stream.Length;
            stream.WriteAtOffset(2, storage, 0, 4);
            if (!storage.AsSpan().SequenceEqual(new byte[] { 0, 1, 0, 1, 2, 3 }) || stream.Position != 1 || stream.Length != originalLength)
                throw new InvalidDataException("WriteAtOffset(int, byte[], int, int) did not preserve overlapping-copy or stream-state semantics.");
        }
    }

    /// <summary>
    /// Verifies null, empty, short, threshold, and multibyte strings using reserved regions.<br/>
    /// A rejected oversized replacement must leave the complete destination unchanged.<br/>
    /// </summary>
    private static void AssertStringRoundTrips()
    {
        AssertStringRoundTrip(null, 4);
        AssertStringRoundTrip(string.Empty, 4);
        AssertStringRoundTrip("test", 8);
        AssertStringRoundTrip("tests", 8);
        AssertStringRoundTrip(new string('x', 64), 70);
        AssertStringRoundTrip("\u03A9\U0001F680", 12);

        using var stream = new BufferStream();
        byte[] initial = Enumerable.Repeat((byte)0xCC, 12).ToArray();
        stream.WriteBytes(initial);
        stream.Position = 3;
        try
        {
            stream.WriteAtOffset(2, "too large", 4);
            throw new InvalidDataException("The reserved-string writer accepted an oversized representation.");
        }
        catch (ArgumentOutOfRangeException)
        {
            if (!stream.AsReadOnlySpan.SequenceEqual(initial) || stream.Position != 3 || stream.Length != initial.Length)
                throw new InvalidDataException("The rejected reserved-string write changed data, position, or length.");
        }
    }

    /// <summary>
    /// Writes one nullable string into a larger reserved slot and reads it through <see cref="BufferStream.ReadString"/>.<br/>
    /// Bytes beyond the encoded representation must remain untouched.<br/>
    /// </summary>
    /// <param name="expected">The nullable string value to round-trip.<br/></param>
    /// <param name="reservedByteCount">The size of the existing destination reservation.<br/></param>
    private static void AssertStringRoundTrip(string? expected, int reservedByteCount)
    {
        const int offset = 2;
        using var stream = new BufferStream();
        byte[] initial = Enumerable.Repeat((byte)0xCC, offset + reservedByteCount).ToArray();
        stream.WriteBytes(initial);
        stream.Position = 1;
        long originalLength = stream.Length;

        stream.WriteAtOffset(offset, expected, reservedByteCount);
        if (stream.Position != 1 || stream.Length != originalLength)
            throw new InvalidDataException("The reserved-string writer changed position or length.");

        stream.Position = offset;
        string? actual = stream.ReadString();
        if (actual != expected)
            throw new InvalidDataException("The reserved-string writer did not match the sequential string reader.");

        int used = checked((int)stream.Position - offset);
        if (!stream.AsReadOnlySpan.Slice(offset + used, reservedByteCount - used).SequenceEqual(initial.AsSpan(offset + used, reservedByteCount - used)))
            throw new InvalidDataException("The reserved-string writer changed unused reservation bytes.");
    }

    /// <summary>
    /// Verifies negative, oversized, and extreme offsets plus invalid source ranges.<br/>
    /// The checks specifically exercise subtraction-based validation without overflowing signed arithmetic.<br/>
    /// </summary>
    private static void AssertBoundaries()
    {
        using var stream = new BufferStream();
        stream.SetLength(4);

        ExpectException<ArgumentOutOfRangeException>(static candidate => candidate.WriteAtOffset(-1, (byte)1), stream, "negative positional offset");
        ExpectException<ArgumentOutOfRangeException>(static candidate => candidate.WriteAtOffset(int.MaxValue, (byte)1), stream, "extreme positional offset");
        ExpectException<ArgumentOutOfRangeException>(static candidate => candidate.WriteAtOffset(4, (short)1), stream, "past-end scalar write");
        ExpectException<ArgumentOutOfRangeException>(static candidate => candidate.WriteAtOffset(0, new byte[1], 0, -1), stream, "negative source count");
        ExpectException<ArgumentOutOfRangeException>(static candidate => candidate.WriteAtOffset(0, new byte[1], int.MaxValue, 0), stream, "extreme source offset");
    }

    /// <summary>
    /// Verifies that root and owner-disposed positional writes fail before touching pooled memory.<br/>
    /// Both fixed-width and raw-byte paths are included because they converge on the shared destination validator.<br/>
    /// </summary>
    private static void AssertDisposedWritesRejected()
    {
        var stream = new BufferStream();
        stream.SetLength(4);
        stream.Dispose();
        ExpectException<ObjectDisposedException>(static candidate => candidate.WriteAtOffset(0, 42), stream, "disposed positional scalar write");

        var raw = new BufferStream();
        raw.SetLength(4);
        raw.Dispose();
        ExpectException<ObjectDisposedException>(static candidate => candidate.WriteAtOffset(0, new byte[] { 1 }, 0, 1), raw, "disposed positional array write");

        var owner = new BufferStream();
        owner.SetLength(4);
        BufferStream segment = owner.Segment(0, 4);
        owner.Dispose();
        try
        {
            ExpectException<ObjectDisposedException>(static candidate => candidate.WriteAtOffset(0, (byte)1), segment, "owner-disposed positional segment write");
        }
        finally
        {
            segment.Dispose();
        }
    }

    /// <summary>
    /// Requires a positional operation to throw the specified exception type.<br/>
    /// A successful operation is converted into an explicit smoke-test failure with the supplied scenario name.<br/>
    /// </summary>
    /// <typeparam name="TException">The required exception type.<br/></typeparam>
    /// <param name="operation">The positional operation expected to fail.<br/></param>
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

    /// <summary>
    /// Verifies the unsigned-offset compatibility boundaries used by serializers that store wire positions as <see cref="uint"/>.<br/>
    /// Both supported value widths must preserve stream state, and an offset above the managed-buffer range must fail before changing existing bytes.<br/>
    /// </summary>
    private static void AssertUnsignedOffsetCompatibility()
    {
        using var stream = new BufferStream();
        stream.SetLength(8);
        stream.Position = 1;
        long originalPosition = stream.Position;
        long originalLength = stream.Length;

        stream.WriteAtOffset(2u, (byte)0xA5);
        stream.WriteAtOffset(4u, 3_456_789_012u);
        if (stream.Position != originalPosition || stream.Length != originalLength)
            throw new InvalidDataException("Unsigned-offset positional writes changed position or length.");

        stream.Position = 2;
        if (stream.ReadByte() != 0xA5)
            throw new InvalidDataException("WriteAtOffset(uint, byte) changed the scalar representation.");
        stream.Position = 4;
        if (stream.ReadUInt32() != 3_456_789_012u)
            throw new InvalidDataException("WriteAtOffset(uint, uint) changed the scalar representation.");

        byte[] beforeFailure = stream.ToArray();
        ExpectException<ArgumentOutOfRangeException>(static candidate => candidate.WriteAtOffset(uint.MaxValue, (byte)1), stream, "out-of-range unsigned positional offset");
        if (!stream.AsReadOnlySpan.SequenceEqual(beforeFailure))
            throw new InvalidDataException("A rejected unsigned-offset positional write changed existing bytes.");
    }
}

internal static class SevenBitEncodingSmoke
{
    /// <summary>
    /// Exercises all six signed and unsigned seven-bit reader/writer pairs through the installed package.<br/>
    /// Coverage includes zero, sign boundaries, maximum widths, malformed terminal bytes, truncation, disposal, and atomic fixed-segment failure.<br/>
    /// </summary>
    public static void Run()
    {
        AssertRoundTrip(int.MinValue, 5, static (stream, value) => stream.Write7BitEncodedInt(value), static stream => stream.Read7BitEncodedInt());
        AssertRoundTrip(-1, 1, static (stream, value) => stream.Write7BitEncodedInt(value), static stream => stream.Read7BitEncodedInt());
        AssertRoundTrip(0, 1, static (stream, value) => stream.Write7BitEncodedInt(value), static stream => stream.Read7BitEncodedInt());
        AssertRoundTrip(int.MaxValue, 5, static (stream, value) => stream.Write7BitEncodedInt(value), static stream => stream.Read7BitEncodedInt());

        AssertRoundTrip(long.MinValue, 10, static (stream, value) => stream.Write7BitEncodedLong(value), static stream => stream.Read7BitEncodedLong());
        AssertRoundTrip(-1L, 1, static (stream, value) => stream.Write7BitEncodedLong(value), static stream => stream.Read7BitEncodedLong());
        AssertRoundTrip(0L, 1, static (stream, value) => stream.Write7BitEncodedLong(value), static stream => stream.Read7BitEncodedLong());
        AssertRoundTrip(long.MaxValue, 10, static (stream, value) => stream.Write7BitEncodedLong(value), static stream => stream.Read7BitEncodedLong());

        AssertRoundTrip(Int128.MinValue, 19, static (stream, value) => stream.Write7BitEncodedInt128(value), static stream => stream.Read7BitEncodedInt128());
        AssertRoundTrip((Int128)(-1), 1, static (stream, value) => stream.Write7BitEncodedInt128(value), static stream => stream.Read7BitEncodedInt128());
        AssertRoundTrip(Int128.Zero, 1, static (stream, value) => stream.Write7BitEncodedInt128(value), static stream => stream.Read7BitEncodedInt128());
        AssertRoundTrip(Int128.MaxValue, 19, static (stream, value) => stream.Write7BitEncodedInt128(value), static stream => stream.Read7BitEncodedInt128());

        AssertRoundTrip(0U, 1, static (stream, value) => stream.Write7BitEncodedUInt(value), static stream => stream.Read7BitEncodedUInt());
        AssertRoundTrip(127U, 1, static (stream, value) => stream.Write7BitEncodedUInt(value), static stream => stream.Read7BitEncodedUInt());
        AssertRoundTrip(128U, 2, static (stream, value) => stream.Write7BitEncodedUInt(value), static stream => stream.Read7BitEncodedUInt());
        AssertRoundTrip(uint.MaxValue, 5, static (stream, value) => stream.Write7BitEncodedUInt(value), static stream => stream.Read7BitEncodedUInt());

        AssertRoundTrip(0UL, 1, static (stream, value) => stream.Write7BitEncodedULong(value), static stream => stream.Read7BitEncodedULong());
        AssertRoundTrip(127UL, 1, static (stream, value) => stream.Write7BitEncodedULong(value), static stream => stream.Read7BitEncodedULong());
        AssertRoundTrip(128UL, 2, static (stream, value) => stream.Write7BitEncodedULong(value), static stream => stream.Read7BitEncodedULong());
        AssertRoundTrip(ulong.MaxValue, 10, static (stream, value) => stream.Write7BitEncodedULong(value), static stream => stream.Read7BitEncodedULong());

        AssertRoundTrip(UInt128.Zero, 1, static (stream, value) => stream.Write7BitEncodedUInt128(value), static stream => stream.Read7BitEncodedUInt128());
        AssertRoundTrip((UInt128)127, 1, static (stream, value) => stream.Write7BitEncodedUInt128(value), static stream => stream.Read7BitEncodedUInt128());
        AssertRoundTrip((UInt128)128, 2, static (stream, value) => stream.Write7BitEncodedUInt128(value), static stream => stream.Read7BitEncodedUInt128());
        AssertRoundTrip(UInt128.MaxValue, 19, static (stream, value) => stream.Write7BitEncodedUInt128(value), static stream => stream.Read7BitEncodedUInt128());

        AssertMalformedValuesRejected();
        AssertDisposedOperationsRejected();
        AssertFixedSegmentFailureIsAtomic();
    }

    /// <summary>
    /// Writes one seven-bit value, verifies its canonical byte count, and reads it back through the matching public reader.<br/>
    /// The reader must consume exactly the encoded representation and reproduce the original value.<br/>
    /// </summary>
    /// <typeparam name="T">The signed or unsigned integer type under test.<br/></typeparam>
    /// <param name="expected">The value expected after decoding.<br/></param>
    /// <param name="expectedByteCount">The canonical encoded width in bytes.<br/></param>
    /// <param name="write">The public seven-bit writer under test.<br/></param>
    /// <param name="read">The matching public seven-bit reader.<br/></param>
    private static void AssertRoundTrip<T>(T expected, int expectedByteCount, Action<BufferStream, T> write, Func<BufferStream, T> read)
    {
        using var stream = new BufferStream();
        write(stream, expected);
        if (stream.Length != expectedByteCount || stream.Position != expectedByteCount)
            throw new InvalidDataException($"{write.Method.Name} produced {stream.Length} bytes; expected {expectedByteCount}.");

        stream.Position = 0;
        T actual = read(stream);
        if (!EqualityComparer<T>.Default.Equals(expected, actual) || stream.Position != stream.Length)
            throw new InvalidDataException($"{write.Method.Name}/{read.Method.Name} did not preserve the value and consume exactly its encoding.");
    }

    /// <summary>
    /// Verifies deterministic rejection of terminal bytes that exceed the 32-, 64-, and 128-bit payload widths.<br/>
    /// A truncated continuation sequence must instead report end-of-stream after consuming the available byte.<br/>
    /// </summary>
    private static void AssertMalformedValuesRejected()
    {
        AssertReadFailure<InvalidDataException>(
            new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 },
            static stream => _ = stream.Read7BitEncodedUInt(),
            5,
            "over-width UInt32");

        AssertReadFailure<InvalidDataException>(
            Enumerable.Repeat((byte)0x80, 9).Append((byte)0x02).ToArray(),
            static stream => _ = stream.Read7BitEncodedULong(),
            10,
            "over-width UInt64");

        AssertReadFailure<InvalidDataException>(
            Enumerable.Repeat((byte)0x80, 18).Append((byte)0x04).ToArray(),
            static stream => _ = stream.Read7BitEncodedUInt128(),
            19,
            "over-width UInt128");

        AssertReadFailure<EndOfStreamException>(
            new byte[] { 0x80 },
            static stream => _ = stream.Read7BitEncodedInt(),
            1,
            "truncated signed Int32");
    }

    /// <summary>
    /// Verifies that both reader and writer entry points reject disposed streams before accessing returned pooled memory.<br/>
    /// </summary>
    private static void AssertDisposedOperationsRejected()
    {
        var writer = new BufferStream();
        writer.Dispose();
        ExpectException<ObjectDisposedException>(static stream => stream.Write7BitEncodedUInt(1), writer, "disposed seven-bit writer");

        var reader = new BufferStream();
        reader.Dispose();
        ExpectException<ObjectDisposedException>(static stream => _ = stream.Read7BitEncodedUInt(), reader, "disposed seven-bit reader");
    }

    /// <summary>
    /// Verifies that a complete encoded value is validated before a fixed segment is mutated.<br/>
    /// A two-byte value written into a one-byte segment must preserve the sentinel byte and cursor.<br/>
    /// </summary>
    private static void AssertFixedSegmentFailureIsAtomic()
    {
        using var owner = new BufferStream();
        owner.Write((byte)0xCC);
        using BufferStream segment = owner.Segment(0, 1);

        ExpectException<ArgumentOutOfRangeException>(static stream => stream.Write7BitEncodedULong(128), segment, "oversized fixed-segment seven-bit write");
        if (segment.Position != 0 || owner.AsReadOnlySpan[0] != 0xCC)
            throw new InvalidDataException("The rejected fixed-segment seven-bit write partially changed the destination.");
    }

    /// <summary>
    /// Requires a malformed seven-bit payload to throw the specified exception after consuming the expected bytes.<br/>
    /// </summary>
    /// <typeparam name="TException">The required exception type.<br/></typeparam>
    /// <param name="payload">The complete malformed or truncated byte sequence.<br/></param>
    /// <param name="read">The public reader expected to reject the payload.<br/></param>
    /// <param name="expectedPosition">The cursor position expected after rejection.<br/></param>
    /// <param name="scenario">A concise scenario name for diagnostics.<br/></param>
    private static void AssertReadFailure<TException>(byte[] payload, Action<BufferStream> read, long expectedPosition, string scenario)
        where TException : Exception
    {
        using var stream = new BufferStream(payload.AsMemory());
        try
        {
            read(stream);
            throw new InvalidDataException($"The {scenario} payload was accepted.");
        }
        catch (TException)
        {
            if (stream.Position != expectedPosition)
                throw new InvalidDataException($"The {scenario} payload advanced to {stream.Position}; expected {expectedPosition}.");
        }
    }

    /// <summary>
    /// Requires a seven-bit operation to throw the specified exception type.<br/>
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

internal static class DirectReaderConversionEqualitySmoke
{
    /// <summary>
    /// Exercises the final direct-reader, conversion, and equality documentation-warning group through the installed package.<br/>
    /// The checks cover malformed frames, logical-length boundaries, storage borrowing versus copying, null conversions, content equality, and disposal safety.<br/>
    /// </summary>
    public static void Run()
    {
        AssertDirectReaders();
        AssertConversionOwnership();
        AssertEqualityContract();
    }

    /// <summary>
    /// Verifies arbitrary integers, partial byte reads, strict nullable-string framing, and exact packed-bit logical lengths.<br/>
    /// Malformed inputs must report domain-specific exceptions without reading beyond logical stream length.<br/>
    /// </summary>
    private static void AssertDirectReaders()
    {
        BigInteger expectedInteger = -BigInteger.Pow(2, 300) + 12345;
        using (var integer = new BufferStream())
        {
            integer.Write(expectedInteger);
            integer.Position = 0;
            if (integer.ReadBigInteger() != expectedInteger || integer.Position != integer.Length)
                throw new InvalidDataException("ReadBigInteger did not preserve the framed arbitrary-precision value.");
        }

        using (var bytes = new BufferStream())
        {
            bytes.WriteBytes(new byte[] { 1, 2, 3 });
            bytes.Position = 1;
            if (!bytes.ReadBytes(10).AsSpan().SequenceEqual(new byte[] { 2, 3 }) || bytes.Position != bytes.Length)
                throw new InvalidDataException("ReadBytes did not return the available suffix at end-of-stream.");
            if (bytes.ReadBytes(10).Length != 0 || bytes.Position != bytes.Length)
                throw new InvalidDataException("ReadBytes did not return an empty array at end-of-stream.");
            ExpectException<ArgumentOutOfRangeException>(static stream => _ = stream.ReadBytes(-1), bytes, "negative direct-byte count");
        }

        var disposedBytes = new BufferStream();
        disposedBytes.WriteBytes(new byte[] { 1, 2, 3 });
        disposedBytes.Position = 0;
        disposedBytes.Dispose();
        ExpectException<ObjectDisposedException>(static stream => _ = stream.ReadBytes(3), disposedBytes, "disposed direct-byte reader");

        using (var invalidNullMarker = new BufferStream())
        {
            invalidNullMarker.Write7BitEncodedInt(-2);
            invalidNullMarker.Position = 0;
            ExpectException<InvalidDataException>(static stream => _ = stream.ReadString(), invalidNullMarker, "invalid negative string marker");
            if (invalidNullMarker.Position != invalidNullMarker.Length)
                throw new InvalidDataException("The invalid string marker did not consume exactly its prefix.");
        }

        using (var truncatedString = new BufferStream())
        {
            truncatedString.Write7BitEncodedInt(5);
            truncatedString.WriteBytes(new byte[] { (byte)'A', (byte)'B' });
            truncatedString.Position = 0;
            ExpectException<EndOfStreamException>(static stream => _ = stream.ReadString(), truncatedString, "truncated string payload");
            if (truncatedString.Position != 1)
                throw new InvalidDataException("The truncated string reader advanced into the incomplete payload.");
        }

        AssertMalformedBitArray(new byte[] { 1 }, 9, "logical bit count exceeding payload");
        AssertMalformedBitArray(new byte[] { 1, 2 }, 1, "oversized packed-bit payload");
    }

    /// <summary>
    /// Verifies the documented borrowed-storage contracts for memory, read-only memory, arrays, and accessible memory streams.<br/>
    /// Span conversion must instead create an independent copy, and nullable reference conversions must reject null explicitly.<br/>
    /// </summary>
    private static void AssertConversionOwnership()
    {
        byte[] memoryStorage = { 1, 2, 3 };
        using (BufferStream stream = memoryStorage.AsMemory())
        {
            stream.WriteAtOffset(0, (byte)9);
            if (memoryStorage[0] != 9)
                throw new InvalidDataException("Memory<byte> conversion did not preserve borrowed writable storage.");
        }

        byte[] readOnlyStorage = { 1, 2, 3 };
        ReadOnlyMemory<byte> readOnlyMemory = readOnlyStorage.AsMemory();
        using (BufferStream stream = readOnlyMemory)
        {
            stream.WriteAtOffset(0, (byte)9);
            if (readOnlyStorage[0] != 9)
                throw new InvalidDataException("Array-backed ReadOnlyMemory<byte> conversion did not preserve its documented borrowed storage.");
        }

        byte[] arrayStorage = { 1, 2, 3 };
        using (BufferStream stream = arrayStorage)
        {
            stream.WriteAtOffset(1, (byte)9);
            if (arrayStorage[1] != 9)
                throw new InvalidDataException("Byte-array conversion did not preserve borrowed writable storage.");
        }

        byte[] spanStorage = { 1, 2, 3 };
        Span<byte> sourceSpan = spanStorage;
        using (BufferStream stream = sourceSpan)
        {
            spanStorage[0] = 9;
            stream.WriteAtOffset(1, (byte)8);
            if (stream.PeekByte(0) != 1 || spanStorage[1] != 2)
                throw new InvalidDataException("Span<byte> conversion did not isolate its copied storage.");
        }

        byte[] memoryStreamStorage = { 1, 2, 3 };
        using (var source = new MemoryStream(memoryStreamStorage, 0, memoryStreamStorage.Length, writable: true, publiclyVisible: true))
        using (BufferStream stream = source)
        {
            stream.WriteAtOffset(2, (byte)9);
            if (memoryStreamStorage[2] != 9 || source.Position != 0)
                throw new InvalidDataException("MemoryStream conversion did not preserve zero-copy storage and source position.");
        }

        byte[]? nullArray = null;
        ExpectException<ArgumentNullException>(
            _ =>
            {
                BufferStream converted = nullArray!;
                converted.Dispose();
            },
            new BufferStream(),
            "null byte-array conversion");

        MemoryStream? nullMemoryStream = null;
        ExpectException<ArgumentNullException>(
            _ =>
            {
                BufferStream converted = nullMemoryStream!;
                converted.Dispose();
            },
            new BufferStream(),
            "null memory-stream conversion");
    }

    /// <summary>
    /// Verifies byte-content equality independent of cursor position, matching hashes for equal content, null operators, and disposal guards.<br/>
    /// Reference identity remains reflexive after disposal, while distinct disposed instances reject content inspection.<br/>
    /// </summary>
    private static void AssertEqualityContract()
    {
        using var left = new BufferStream();
        using var right = new BufferStream();
        using var different = new BufferStream();
        left.WriteBytes(new byte[] { 1, 2, 3, 4 });
        right.WriteBytes(new byte[] { 1, 2, 3, 4 });
        different.WriteBytes(new byte[] { 1, 2, 9, 4 });
        left.Position = 1;
        right.Position = 3;

        if (!left.Equals(right) || !left.Equals((object)right) || left != right || !(left == right))
            throw new InvalidDataException("Equal BufferStream contents did not satisfy the equality contract.");
        if (left.GetHashCode() != right.GetHashCode())
            throw new InvalidDataException("Equal BufferStream contents produced different hash codes.");
        if (left.Equals(different) || left == different || !(left != different))
            throw new InvalidDataException("Different BufferStream contents compared equal.");

        BufferStream? nullLeft = null;
        BufferStream? nullRight = null;
        if (!(nullLeft == nullRight) || nullLeft != nullRight || left == nullLeft || !(left != nullLeft))
            throw new InvalidDataException("BufferStream null operators violated their documented contract.");

        var disposedIdentity = new BufferStream();
        BufferStream disposedAlias = disposedIdentity;
        disposedIdentity.Dispose();
        if (!disposedIdentity.Equals(disposedAlias) || !(disposedIdentity == disposedAlias))
            throw new InvalidDataException("Reference identity was not reflexive after disposal.");
        ExpectException<ObjectDisposedException>(static stream => _ = stream.GetHashCode(), disposedIdentity, "disposed hash-code operation");

        var disposedOther = new BufferStream();
        disposedOther.Dispose();
        ExpectException<ObjectDisposedException>(stream => _ = stream.Equals(disposedOther), disposedIdentity, "distinct disposed equality operation");
    }

    /// <summary>
    /// Builds one inconsistent packed-bit frame and requires <see cref="BufferStream.ReadBitArray"/> to reject it after consuming the frame.<br/>
    /// </summary>
    /// <param name="payload">The framed packed-byte payload.<br/></param>
    /// <param name="bitCount">The inconsistent logical bit count suffix.<br/></param>
    /// <param name="scenario">A concise scenario name for diagnostics.<br/></param>
    private static void AssertMalformedBitArray(byte[] payload, int bitCount, string scenario)
    {
        using var stream = new BufferStream();
        stream.WriteBytesWithByteLength(payload);
        stream.Write7BitEncodedInt(bitCount);
        stream.Position = 0;
        ExpectException<InvalidDataException>(static candidate => _ = candidate.ReadBitArray(), stream, scenario);
        if (stream.Position != stream.Length)
            throw new InvalidDataException($"The {scenario} frame was not consumed consistently before rejection.");
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
        finally
        {
            if (scenario.StartsWith("null ", StringComparison.Ordinal))
                stream.Dispose();
        }
    }
}
