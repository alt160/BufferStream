using System.IO;

using var buffer = new BufferStream();
buffer.Write(42);
buffer.Write("package smoke");

buffer.Position = 0;
if (buffer.ReadInt32() != 42 || buffer.ReadString() != "package smoke")
    throw new InvalidDataException("The packaged BufferStream binary did not preserve the expected primitive/string round trip.");

Console.WriteLine("BufferStream package smoke test passed.");
