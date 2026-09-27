# BufferStream

`BufferStream` is a pooled, auto-resizing `System.IO.Stream` for in-memory binary data. It adds convenient primitive and length-prefixed value helpers while retaining ordinary stream reads, writes, seeks, and shallow segment views.

## Install

```powershell
dotnet add package BufferStream
```

BufferStream targets `net8.0`. Applications targeting newer compatible .NET versions consume the same package asset.

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

## Ownership and views

- An owned stream rents its backing array from `ArrayPool<byte>.Shared` and returns it when disposed.
- Streams constructed from `Memory<byte>` or `ReadOnlyMemory<byte>` borrow the supplied memory; disposing them never returns caller-owned storage to the pool.
- `Segment(...)` creates a shallow, pooled view over the parent stream’s written contents. Dispose the view before disposing its parent.
- The maximum stream length is bounded by `Int32.MaxValue`.

`BufferStream` deliberately lives in the `System.IO` namespace so it can be used naturally with other stream APIs. It is not a framework type.

## What it provides

- Auto-growing pooled byte storage.
- Standard `Stream` operations plus random-access memory/span helpers.
- Binary reads and writes for primitives, numeric arrays, `Guid`, temporal values, vectors, strings, and length-prefixed byte data.
- Zero-copy wrapping where the supplied memory is array-backed.
- Pooled shallow segments for low-allocation parsing workflows.

## Compatibility

The public API follows Semantic Versioning. A future package version may add APIs in a minor release; breaking source or binary API changes require a major version.

## License

Copyright 2026 Eriq VanBibber.

Licensed under [Apache-2.0](LICENSE).
