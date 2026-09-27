using System.IO;

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

Console.WriteLine("BufferStream package smoke test passed.");
