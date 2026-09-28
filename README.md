# BufferStream

[![CI](https://github.com/alt160/BufferStream/actions/workflows/ci.yml/badge.svg)](https://github.com/alt160/BufferStream/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/BufferStream.svg)](https://www.nuget.org/packages/BufferStream)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/alt160/BufferStream/blob/main/LICENSE)

`BufferStream` is a pooled, reusable `System.IO.Stream` for in-memory binary work. It combines ordinary stream behavior with direct primitive encoding, explicit collection framing, borrowed-memory inputs, shallow cursor segments, and cursor-preserving backpatching.

It is designed for code that repeatedly asks questions such as:

- Can I reuse this buffer for the next record instead of allocating another stream and array?
- Can I read a subsection of this payload with an independent cursor without copying it?
- Can I fill in a header offset or count after writing the payload without moving my current position?
- Can I expose the written bytes as a span or memory view instead of calling `ToArray()`?
- Can I write binary values without placing `BinaryReader` and `BinaryWriter` wrappers in every call chain?

If those are not problems in your application, `MemoryStream` may already be the right answer. BufferStream is intended for the cases where buffer ownership, reuse, framing, and cursor overhead are part of the design rather than incidental details.

## Install

```powershell
dotnet add package BufferStream
```

BufferStream targets `net8.0`. Applications targeting compatible newer .NET versions consume the same package asset.

The type deliberately lives in the `System.IO` namespace, so normal usage needs only:

```csharp
using System.IO;
```

`System.IO.BufferStream` is provided by this package; it is not a .NET framework type.

## Quick start

```csharp
using System.IO;

using var buffer = new BufferStream();

buffer.Write(42);
buffer.Write("hello");

buffer.Position = 0;
int number = buffer.ReadInt32();
string? text = buffer.ReadString();
```

## When BufferStream is a better fit

### 1. Reusing one serialization buffer

A server, serializer, or storage engine may encode thousands or millions of independent values. Creating a new stream and backing array for every value adds avoidable allocation and garbage-collection pressure.

An owned BufferStream rents its storage from `ArrayPool<byte>.Shared`. `Reset()` returns the same instance to an empty logical state while retaining its current capacity.

```csharp
using var buffer = new BufferStream(initialCapacity: 4096);

foreach (Message message in messages)
{
    buffer.Reset();
    WriteMessage(buffer, message);

    Send(buffer.AsReadOnlySpan);
}
```

As long as the retained capacity is sufficient, this avoids allocating a new stream and backing array for each iteration. Allocations performed by `WriteMessage` or `Send` remain their own responsibility.

This is the workload that originally motivated BufferStream and the role it serves beneath the Inheto serializer.

### 2. Backpatching a binary header

Binary formats often place a count, size, or offset before the value is known. With a conventional stream, code usually saves the current position, seeks backward, writes the value, and restores the position.

`WriteAtOffset(...)` overwrites an existing region without changing `Position` or `Length`:

```csharp
using var buffer = new BufferStream();

int countOffset = checked((int)buffer.Position);
buffer.Write(0u); // reserve the count

uint count = 0;
foreach (Item item in items)
{
    WriteItem(buffer, item);
    count++;
}

buffer.WriteAtOffset(countOffset, count);
```

The payload cursor remains at the end, which removes the save/seek/write/restore sequence and the state bugs that can accompany it. Positional writes are bounds-checked and cannot extend the logical stream.

### 3. Parsing nested regions without copying them

`Segment(...)` creates a shallow BufferStream cursor over existing written data. The segment has its own relative `Position` while sharing the root storage.

```csharp
ReadOnlyMemory<byte> packetBytes = ReceivePacket();
using var packet = new BufferStream(packetBytes);

int payloadOffset = packet.ReadInt32();
int payloadLength = packet.ReadInt32();

using BufferStream payload = packet.Segment(payloadOffset, payloadLength);
int recordType = payload.ReadInt32();
string? recordName = payload.ReadString();
```

This is useful for record layouts, nested binary documents, page-oriented storage, and protocol frames where several readers need independent cursors into one payload.

Segment wrappers are pooled. Dispose each segment when finished so the root stream can reuse that wrapper. The root owner must remain alive for the complete segment lifetime.

### 4. Reading caller-owned memory

BufferStream can work over existing `Memory<byte>` or `ReadOnlyMemory<byte>` rather than requiring an owned copy first.

```csharp
Memory<byte> storage = GetWritableStorage();
using var stream = new BufferStream(storage);

int version = stream.ReadInt32();
```

Writable `Memory<byte>` is borrowed directly. Array-backed `ReadOnlyMemory<byte>` is also used directly; non-array-backed read-only memory is copied to an internal managed array. Disposing a borrowed stream never returns or releases caller-owned storage.

Borrowed streams have fixed available storage: they cannot rent a larger replacement when a write exceeds the supplied memory.

### 5. Rebinding a long-lived reader

A parser that consumes many independent payloads can reuse the same borrowed root stream:

```csharp
using var reader = new BufferStream(ReadOnlyMemory<byte>.Empty);

foreach (ReadOnlyMemory<byte> payload in payloads)
{
    reader.RebindReadOnlyMemory(payload);
    ReadRecord(reader);
}
```

Every segment and view derived from the previous payload must be finished before rebinding. Rebinding is supported only for borrowed root streams; owned streams and child segments reject it.

### 6. Building self-describing binary values

BufferStream includes matching readers and writers for primitives, strings, selected .NET numeric and temporal types, and framed collections.

```csharp
using var buffer = new BufferStream();

buffer.Write(Guid.NewGuid());
buffer.Write(DateTime.UtcNow);
buffer.WriteInt32sWithByteLength(new[] { 10, 20, 30 });
buffer.WriteStringsWithCount(new[] { "red", "green", "blue" });

buffer.Position = 0;
Guid id = buffer.ReadGuid();
DateTime created = buffer.ReadDateTime();
int[] values = buffer.ReadInt32sWithByteLength();
string?[] names = buffer.ReadStringsWithCount();
```

This is useful when a project wants direct binary helpers without creating and passing separate reader and writer objects throughout its internal call graph.

## Broader typed binary coverage

`BinaryReader` and `BinaryWriter` intentionally focus on primitive values, strings, and raw byte or character data. BufferStream includes those common operations and adds built-in encodings for type families that binary-heavy applications otherwise have to define repeatedly.

Additional scalar coverage includes:

- 128-bit integers: `Int128` and `UInt128`.
- Identifiers and text elements: `Guid` and `Rune`.
- Dates and durations: `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset`, and `TimeSpan`.
- Arbitrary and complex numbers: `BigInteger` and `Complex`.
- Numerics and geometry: `Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Plane`, `Matrix3x2`, and `Matrix4x4`.
- Packed data: `BitArray`.

BufferStream also supplies matching framed collection methods for most supported value types. This is broader built-in **type encoding and round-trip coverage**, not automatic coercion between unrelated CLR types.

### Public 7-bit codecs and internal framing

BufferStream exposes six matching 7-bit reader/writer pairs as public APIs:

- Signed ZigZag encodings for `Int32`, `Int64`, and `Int128`.
- Unsigned base-128 encodings for `UInt32`, `UInt64`, and `UInt128`.

The signed `Int32` pair is also the framing mechanism used by nullable strings and collection byte-length/count prefixes. A string prefix of `-1` represents `null`, zero represents an empty string, and a positive value is the encoded byte count.

.NET 8 and later also expose [`BinaryReader.Read7BitEncodedInt`](https://learn.microsoft.com/dotnet/api/system.io.binaryreader.read7bitencodedint?view=net-10.0), `Read7BitEncodedInt64`, and the corresponding [`BinaryWriter`](https://learn.microsoft.com/dotnet/api/system.io.binarywriter.write7bitencodedint?view=net-10.0) methods publicly. The distinction is that BufferStream adds unsigned and 128-bit families and uses ZigZag for signed values.

The signed formats are not wire-compatible. For example, BufferStream encodes `-1` as `01`; `BinaryWriter.Write7BitEncodedInt(-1)` encodes it as `FF FF FF FF 0F`. The same difference affects string and collection prefixes, so pair each writer with its documented reader.

### Date and time round-trip semantics

The scalar date and time methods preserve their semantic state rather than reducing every value to an ambiguous timestamp:

- `DateTime` uses `DateTime.ToBinary()` and `DateTime.FromBinary()`, preserving `Kind` and following .NET’s documented local-time adjustment behavior.
- `DateTimeOffset` stores the local clock value together with the exact offset tick count; the smoke suite requires `EqualsExact` after round-trip.
- `DateOnly` stores its day number, without introducing a time or time zone.
- `TimeOnly` stores its full tick count, without introducing a date.
- `TimeSpan` preserves its signed tick count, including negative durations.

The byte-length-framed arrays use the same raw unmanaged-array policy as BufferStream’s other `WithByteLength` methods. Applications defining a long-lived external format should account for that distinction and the portability limits described below.

### Type coverage compared with BinaryReader and BinaryWriter

The comparison below reflects the public .NET 10 [`BinaryReader`](https://learn.microsoft.com/dotnet/api/system.io.binaryreader?view=net-10.0) and [`BinaryWriter`](https://learn.microsoft.com/dotnet/api/system.io.binarywriter?view=net-10.0) surface. “Built in” means the type has a direct public operation; it does not imply that independently implemented encodings are wire-compatible.

| Type or operation | `BinaryReader` | `BinaryWriter` | `BufferStream` |
|---|---|---|---|
| `Boolean` | Scalar | Scalar | Scalar and byte-length-framed array |
| `Byte` / `SByte` | Scalar | Scalar | Scalar; framed `Byte` and `SByte` arrays |
| `Int16` / `UInt16` | Scalar | Scalar | Scalar and byte-length-framed arrays |
| `Int32` / `UInt32` | Scalar | Scalar | Scalar and byte-length-framed arrays |
| `Int64` / `UInt64` | Scalar | Scalar | Scalar and byte-length-framed arrays |
| `Int128` / `UInt128` | — | — | Scalar and byte-length-framed arrays |
| `Half` | Scalar | Scalar | Scalar and byte-length-framed array |
| `Single` / `Double` | Scalar | Scalar | Scalar and byte-length-framed arrays |
| `Decimal` | Scalar | Scalar | Scalar and count-framed array |
| `Char` | Scalar and caller-count read | Scalar and raw array/range/span write | Scalar and byte-length-framed array |
| `Rune` | — | — | Scalar |
| `String` | Scalar | Scalar | Scalar and count-framed nullable array |
| Raw byte buffers | Caller-count read | Raw array, range, and span write | Raw count-based reads/writes plus byte-length-framed payloads |
| `Guid` | — | — | Scalar and byte-length-framed array |
| `DateOnly` | — | — | Scalar and byte-length-framed array |
| `TimeOnly` | — | — | Scalar and byte-length-framed array |
| `DateTime` | — | — | Scalar and byte-length-framed array |
| `DateTimeOffset` | — | — | Scalar and byte-length-framed array |
| `TimeSpan` | — | — | Scalar and byte-length-framed array |
| `BigInteger` | — | — | Scalar and count-framed array |
| `BitArray` | — | — | Scalar packed representation |
| `Complex` | — | — | Scalar and byte-length-framed array |
| `Vector2` / `Vector3` / `Vector4` | — | — | Scalar and byte-length-framed arrays |
| `Quaternion` | — | — | Scalar and byte-length-framed array |
| `Plane` | — | — | Scalar and byte-length-framed array |
| `Matrix3x2` / `Matrix4x4` | — | — | Scalar and byte-length-framed arrays |
| `Version` | — | — | Count-framed array |
| Signed 7-bit `Int32` | Public read using the framework format | Public write; also used by string lengths | Public ZigZag read/write; also used by strings and framed lengths/counts |
| Signed 7-bit `Int64` | Public read using the framework format | Public write using the framework format | Public ZigZag read/write |
| Unsigned 7-bit `UInt32` / `UInt64` | — | — | Public base-128 read/write |
| Signed and unsigned 7-bit 128-bit integers | — | — | Public ZigZag-signed and base-128-unsigned read/write |
| Positional scalar overwrite | — | — | Supported for selected fixed-width types without moving the cursor |

The table describes available operations, not a universal serialization standard. BufferStream’s fixed-width primitives and unmanaged arrays generally use their .NET in-memory representation; applications that require a canonical external wire format should define and test that format explicitly.

## Why not just MemoryStream?

`MemoryStream` is mature, familiar, and usually the best default for ordinary in-memory stream work. BufferStream addresses a more specialized set of requirements.

| Requirement | `MemoryStream` | `BufferStream` |
|---|---|---|
| Ordinary `Stream` reads, writes, and seeks | Yes | Yes |
| Direct span-based `Stream` reads and writes | Yes | Yes |
| Owned growth backed by `ArrayPool<byte>.Shared` | No | Yes |
| Reset and reuse retained pooled capacity | `SetLength(0)` and position management | `Reset()` |
| Direct primitive and typed collection methods | Requires another layer, commonly `BinaryReader`/`BinaryWriter` | Built in |
| Explicit byte-length versus element-count framing | Application-defined | Named `WithByteLength` and `WithCount` pairs |
| Independent shallow cursor over a subsection | Application-defined | Pooled `Segment(...)` views |
| Backpatch without moving the active cursor | Save, seek, write, and restore | `WriteAtOffset(...)` |
| Rebind one reader to successive payloads | Create or reconfigure another abstraction | `RebindReadOnlyMemory(...)` |
| Direct logical-content memory/span views | Possible through buffer APIs and careful length tracking | `AsReadOnlyMemory`, `AsReadOnlySpan`, and `AsWritableSpan` |

BufferStream is therefore not a claim that `MemoryStream` is poorly designed. It is a different ownership and binary-I/O policy packaged as one stream type.

## Framed and unframed bytes

The distinction between raw bytes and a self-describing byte payload is intentional:

```csharp
byte[] bytes = { 1, 2, 3, 4 };

buffer.WriteBytes(bytes);                    // raw bytes, no prefix
buffer.WriteBytesWithByteLength(bytes);      // byte-length prefix + bytes
buffer.Write(bytes.AsMemory());              // byte-length prefix + bytes
buffer.Write(bytes, 0, bytes.Length);         // Stream override: raw bytes
```

Use the corresponding reader for the representation that was written:

- `WriteBytes(...)` pairs with `ReadBytes(count)` when the count comes from the surrounding format.
- `WriteBytesWithByteLength(...)` and `Write(Memory<byte>)` pair with `ReadBytesWithByteLength()`.
- Standard `Stream.Write(...)` overloads remain unframed.

## Collection framing

Collection method names state what their prefix measures:

- `*WithByteLength` prefixes the total payload byte length. It is used for raw unmanaged arrays such as `WriteInt32sWithByteLength` and `ReadInt32sWithByteLength`.
- `*WithCount` prefixes the logical element count. It is used when elements have individual encodings, such as `WriteStringsWithCount` and `ReadStringsWithCount`.

Every public collection writer has a reader with the same type stem and framing suffix.

## Ownership and lifetime rules

BufferStream makes allocation reduction possible by exposing pooled and borrowed storage. That also makes lifetime rules important.

- Dispose owned root streams so their arrays are cleared and returned to `ArrayPool<byte>.Shared`.
- Dispose segments so their cursor wrappers can be returned to the root stream’s bounded segment pool.
- Keep a root stream alive until all of its segments are finished.
- Keep caller-owned memory alive and stable for the lifetime of a borrowed stream.
- Do not retain `AsReadOnlyMemory`, `AsReadOnlySpan`, `AsWritableSpan`, `Memory(...)`, `Span(...)`, or `ReadOnlySpan(...)` views across a capacity-growing write, rebinding, or disposal.
- Use `ToArray()` when the result must be an independent, caller-owned snapshot.
- `Reset(clearBuffer: false)` changes the logical state but deliberately retains prior bytes in the capacity buffer. Use `clearBuffer: true` when those written bytes must be cleared immediately.

## Design and performance boundaries

BufferStream is designed to remove specific allocations and copies; it does not claim to make every stream workload faster.

The package smoke suite includes warmed allocation checks for selected paths, including decimal reads and length-prefixed `Memory<byte>` writes. The broader integration suite also checks pooled segment reuse, direct/base stream dispatch, exact wire output, cursor behavior, and failure atomicity. These are regression constraints, not universal throughput benchmarks.

Profile the complete application. Encoding strings, materializing arrays, calling `ToArray()`, or allocating objects above the stream can dominate a workload even when the stream path itself is allocation-free.

## Important limitations

- Logical length and offsets are managed with `int`; the maximum supported stream length is `Int32.MaxValue`.
- Most fixed-width primitives and unmanaged arrays use their .NET in-memory representation. Do not assume those encodings are a canonical cross-platform network format without defining and testing that contract yourself.
- A single stream cursor is not a general-purpose synchronization primitive. Coordinate concurrent access at the application level.
- Borrowed streams cannot grow beyond their supplied storage.
- Segments share their root storage; they are views, not snapshots.
- Pooled storage rewards deterministic disposal. Leaving owned streams undisposed delays returning their buffers to the pool.

## When MemoryStream is still the better choice

Prefer `MemoryStream` when:

- the operation is infrequent and allocation pressure is irrelevant;
- the rest of the codebase already uses `BinaryReader` and `BinaryWriter` consistently;
- shallow cursor segments, backpatching, borrowing, and rebinding are unnecessary;
- a familiar framework-only dependency is more valuable than BufferStream’s specialized API;
- you have not measured or identified a buffer-lifecycle problem to solve.

The goal is low developer friction, not novelty. Choose BufferStream when its ownership and binary-format tools simplify the whole design—not merely because it is available.

## Compatibility

The public API follows Semantic Versioning. A future package version may add APIs in a minor release; breaking source or binary API changes require a major version.

## Contributing and security

- See [CONTRIBUTING.md](https://github.com/alt160/BufferStream/blob/main/CONTRIBUTING.md) for development guidance.
- See [SECURITY.md](https://github.com/alt160/BufferStream/blob/main/SECURITY.md) for reporting security issues.
- Release history is recorded in [CHANGELOG.md](https://github.com/alt160/BufferStream/blob/main/CHANGELOG.md).

## License

Copyright 2026 Eriq VanBibber.

Licensed under [Apache-2.0](https://github.com/alt160/BufferStream/blob/main/LICENSE).
