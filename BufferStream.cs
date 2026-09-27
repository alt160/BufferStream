using System;
using System.Collections.Generic;
using System.Buffers;
using System.Linq;
using System.Numerics;
using System.IO;








namespace System.IO
{
    using System;
    using System.Buffers;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;








    /// <summary>
    /// A stream backed by an auto-resizing, pooled byte buffer.
    /// Supports random-access reads, writes, seeks, and can return its buffer to the pool on Dispose.
    /// </summary>
    //====== TYPES ======
    public sealed class BufferStream : Stream
    {




        public static implicit operator BufferStream (Memory<byte> segment) => new BufferStream(segment);
        public static implicit operator BufferStream (ReadOnlyMemory<byte> segment) => new BufferStream(segment);



        // Helper: Calculate size of 7-bit encoded int
        //Shared/Static Members
        private static int Get7BitEncodedIntSize(int value)
        {
            int count = 0;
            uint v = (uint)value;
            do
            {
                v >>= 7;
                count++;
            } while (v != 0);
            return count;
        }
        // Helper: Write 7-bit encoded int to span
        private static int Write7BitEncodedIntToSpan(Span<byte> span, int value)
        {
            int written = 0;
            uint v = (uint)value;
            while (v >= 0x80)
            {
                span[written++] = (byte)(v | 0x80);
                v >>= 7;
            }
            span[written++] = (byte)v;
            return written;
        }








        //======  FIELDS  ======
        private byte[] _buffer;  // must remain as byte[] in order to return it to the array pool
        private Memory<byte> _bufferMemory;
        private bool _disposed;
        private int _length;
        private int _position;
        private BufferStream? _owner;
        private int _baseOffset;
        private bool _isFixedLength;
        private bool _ownsBuffer;
        private bool _isPooledSegment;
        private readonly int _segmentPoolMax = Math.Max(2, Environment.ProcessorCount * 2);
        private BufferStream? _singleSegmentPool;
        private ConcurrentStack<BufferStream>? _segmentPool;
        private int _segmentPoolCount;








        /// <summary>
        /// Initializes a new instance of <see cref="BufferStream"/> with the an initial capacity of 4096 bytes and UTF8 encoding.<br/>
        /// The default string encoding is <see cref="Encoding.UTF8"/>.
        /// </summary>
        //======  CONSTRUCTORS  ======
        public BufferStream() : this((int)4096, System.Text.Encoding.UTF8) { }

        /// <summary>
        /// Initializes a new instance of <see cref="BufferStream"/> with the specified initial capacity.<br/>
        /// The default string encoding is <see cref="Encoding.UTF8"/>.
        /// </summary>
        /// <param name="initialCapacity">The initial size of the underlying buffer in bytes.</param>
        //======  CONSTRUCTORS  ======
        public BufferStream(uint initialCapacity = 4096, Encoding? encoding = null) : this((int)initialCapacity, encoding) { }

        /// <summary>
        /// Initializes a new instance of <see cref="BufferStream"/> with the specified initial capacity.<br/>
        /// The default string encoding is <see cref="Encoding.UTF8"/>.
        /// </summary>
        /// <param name="initialCapacity">The initial size of the underlying buffer in bytes.</param>
        //======  CONSTRUCTORS  ======
        public BufferStream(int initialCapacity = 4096, Encoding? encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;
            if (initialCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity), "Initial capacity must be positive.");

            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _bufferMemory = _buffer.AsMemory();
            _length = 0;
            _position = 0;
            _disposed = false;
            _owner = null;
            _baseOffset = 0;
            _isFixedLength = false;
            _ownsBuffer = true;
            _isPooledSegment = false;
            _segmentPool = null;
            _segmentPoolCount = 0;
            StringEncoding = encoding;
        }

        public BufferStream(Memory<byte> segment, Encoding? encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;
            _buffer = null!;
            _bufferMemory = segment;
            _length = segment.Length;
            _position = 0;
            _disposed = false;
            _owner = null;
            _baseOffset = 0;
            _isFixedLength = false;
            _ownsBuffer = false;
            _isPooledSegment = false;
            _segmentPool = null;
            _segmentPoolCount = 0;
            StringEncoding = encoding;
        }
        public BufferStream(ReadOnlyMemory<byte> segment, Encoding? encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;
            _buffer = null!;
            _bufferMemory = MemoryMarshal.TryGetArray(segment, out ArraySegment<byte> arraySegment)
                ? arraySegment.Array!.AsMemory(arraySegment.Offset, arraySegment.Count)
                : segment.ToArray();
            _length = segment.Length;
            _position = 0;
            _disposed = false;
            _owner = null;
            _baseOffset = 0;
            _isFixedLength = false;
            _ownsBuffer = false;
            _isPooledSegment = false;
            _segmentPool = null;
            _segmentPoolCount = 0;
            StringEncoding = encoding;
        }

        public BufferStream(MemoryStream ms, bool preferZeroCopy = true, bool takeOwnership = false, Encoding? encoding = null)
        {
            if (encoding is null) encoding = Encoding.UTF8;

            if (preferZeroCopy && ms.TryGetBuffer(out var seg))
            {
                // zero-copy wrap of MemoryStream
                _buffer = null!; // mark as non-owning slice
                _bufferMemory = seg.Array!.AsMemory(seg.Offset, seg.Count);
                _length = seg.Count;
                _position = 0;
                _disposed = false;
                _owner = null;
                _baseOffset = 0;
                _isFixedLength = false;
                _ownsBuffer = false;
                _isPooledSegment = false;
                _segmentPool = null;
                _segmentPoolCount = 0;
                StringEncoding = encoding;
                if (takeOwnership) ms.Dispose();
                return;
            }

            // fallback: exact copy of remaining bytes
            long rem = ms.Length - ms.Position;
            if (rem < 0 || rem > int.MaxValue) throw new IOException("Unsupported size.");
            var buf = ArrayPool<byte>.Shared.Rent((int)rem);
            ms.ReadExactly(buf.AsSpan(0, (int)rem));
            _buffer = buf;                // owning
            _bufferMemory = buf;
            _length = (int)rem;
            _position = 0;
            _disposed = false;
            _owner = null;
            _baseOffset = 0;
            _isFixedLength = false;
            _ownsBuffer = true;
            _isPooledSegment = false;
            _segmentPool = null;
            _segmentPoolCount = 0;
            StringEncoding = encoding;
            if (takeOwnership) ms.Dispose();
        }

        public BufferStream(Stream s, bool takeOwnership = false, Encoding? encoding = null)
        {
            if (encoding is null) encoding = Encoding.UTF8;

            // delegate MemoryStream to the specialized ctor
            if (s is MemoryStream ms && ms.TryGetBuffer(out var seg))
            {
                // zero-copy wrap of MemoryStream
                _buffer = null!; // mark as non-owning slice
                _bufferMemory = seg.Array!.AsMemory(seg.Offset, seg.Count);
                _length = seg.Count;
                _position = 0;
                _disposed = false;
                _owner = null;
                _baseOffset = 0;
                _isFixedLength = false;
                _ownsBuffer = false;
                _isPooledSegment = false;
                _segmentPool = null;
                _segmentPoolCount = 0;
                StringEncoding = encoding;
                if (takeOwnership) ms.Dispose();
                return;
            }

            // seekable: exact size, single pass
            if (s.CanSeek)
            {
                long rem = s.Length - s.Position;
                if (rem < 0 || rem > int.MaxValue) throw new IOException("Unsupported size.");

                var buf = ArrayPool<byte>.Shared.Rent((int)rem);
                s.ReadExactly(buf.AsSpan(0, (int)rem));

                _buffer = buf; _bufferMemory = buf; _length = (int)rem; _position = 0;
                _disposed = false;
                _owner = null;
                _baseOffset = 0;
                _isFixedLength = false;
                _ownsBuffer = true;
                _isPooledSegment = false;
                _segmentPool = null;
                _segmentPoolCount = 0;
                StringEncoding = encoding;
                if (takeOwnership) s.Dispose();
                return;
            }

            // non-seekable: pooled growth, then keep the rented buffer
            byte[] tmp = ArrayPool<byte>.Shared.Rent(81920);
            int len = 0;
            while (true)
            {
                if (len == tmp.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(tmp.Length * 2);
                    Buffer.BlockCopy(tmp, 0, bigger, 0, len);
                    ArrayPool<byte>.Shared.Return(tmp, true);
                    tmp = bigger;
                }
                int n = s.Read(tmp, len, tmp.Length - len);
                if (n == 0) break;
                len += n;
            }
            _buffer = tmp; _bufferMemory = tmp; _length = len; _position = 0;
            _disposed = false;
            _owner = null;
            _baseOffset = 0;
            _isFixedLength = false;
            _ownsBuffer = true;
            _isPooledSegment = false;
            _segmentPool = null;
            _segmentPoolCount = 0;
            StringEncoding = encoding;
            if (takeOwnership) s.Dispose();
        }

        public BufferStream(FileStream fs, bool takeOwnership = false, Encoding? encoding = null)
        {
            if (encoding is null) encoding = Encoding.UTF8;
            long rem = fs.Length - fs.Position;
            if (rem < 0 || rem > int.MaxValue) throw new IOException("Unsupported size.");

            var buf = ArrayPool<byte>.Shared.Rent((int)rem);
            long off = fs.Position; int written = 0;
            while (written < rem)
            {
                written += RandomAccess.Read(fs.SafeFileHandle, buf.AsSpan(written, (int)(rem - written)), off + written);
            }
            fs.Position = off + rem;

            _buffer = buf; _bufferMemory = buf; _length = (int)rem; _position = 0;
            _disposed = false;
            _owner = null;
            _baseOffset = 0;
            _isFixedLength = false;
            _ownsBuffer = true;
            _isPooledSegment = false;
            _segmentPool = null;
            _segmentPoolCount = 0;
            StringEncoding = encoding;
            if (takeOwnership) fs.Dispose();
        }







        /// <summary>
        /// Initializes a pooled segment instance bound to an owner stream.<br/>
        /// This constructor does not allocate a buffer and is only used for segment pooling.<br/>
        /// </summary>
        /// <param name="owner">The owning <see cref="BufferStream"/> providing the backing buffer.<br/></param>
        private BufferStream(BufferStream owner)
        {
            _buffer = null!;
            _bufferMemory = default;
            _length = 0;
            _position = 0;
            _disposed = false;
            _owner = owner;
            _baseOffset = 0;
            _isFixedLength = false;
            _ownsBuffer = false;
            _isPooledSegment = true;
            _segmentPool = null;
            _segmentPoolCount = 0;
            StringEncoding = owner.StringEncoding;
        }




        /// <summary>
        /// Gets the written contents as a read-only memory slice.
        /// </summary>
        //======  PROPERTIES  ======
        public ReadOnlyMemory<byte> AsReadOnlyMemory
        {
            get
            {
                EnsureNotDisposed();
                return GetMemorySlice(0, EffectiveLength);
            }
        }

        /// <summary>
        /// Gets the written contents as a read-only span.
        /// </summary>
        public ReadOnlySpan<byte> AsReadOnlySpan
        {
            get
            {
                EnsureNotDisposed();
                return GetReadOnlySpan(0, EffectiveLength);
            }
        }

        /// <summary>
        /// Returns a writable span of the written contents.
        /// </summary>
        public Span<byte> AsWritableSpan
        {
            get
            {
                EnsureNotDisposed();
                return GetWritableSpan(0, EffectiveLength);
            }
        }

        /// <inheritdoc/>
        public override bool CanRead => !_disposed && (_owner == null || !_owner._disposed);

        /// <inheritdoc/>
        public override bool CanSeek => !_disposed && (_owner == null || !_owner._disposed);

        /// <inheritdoc/>
        public override bool CanWrite => !_disposed && (_owner == null || !_owner._disposed);

        /// <inheritdoc/>
        public override long Length => EffectiveLength;

        /// <inheritdoc/>
        public override long Position
        {
            get
            {
                EnsureNotDisposed();
                return _position;
            }
            set
            {
                EnsureNotDisposed();
                if (value < 0 || value > EffectiveLength)
                    throw new ArgumentOutOfRangeException(nameof(value), "Position must be within the length of the stream.");
                _position = (int)value;
            }
        }

        public Encoding StringEncoding { get; }








        /// <summary>
        /// Returns the underlying buffer to the shared pool and releases resources.
        /// </summary>
        /// <param name="disposing">True if called from Dispose; false if from finalizer.</param>
        //======  METHODS  ======
        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (_owner != null)
                {
                    if (_isPooledSegment)
                    {
                        ReturnSegmentToPool();
                        base.Dispose(disposing);
                        return;
                    }

                    _disposed = true;
                    base.Dispose(disposing);
                    return;
                }

                if (_ownsBuffer && _buffer != null) ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                _buffer = null!;
                _disposed = true;
            }
            base.Dispose(disposing);
        }








        /// <summary>
        /// Ensures the backing buffer can accommodate at least <paramref name="min"/> bytes.
        /// Rents a larger buffer if needed, copying existing data and returning the old buffer.
        /// </summary>
        /// <param name="min">The minimum required capacity.</param>
        private void EnsureCapacity(uint min) => EnsureCapacity((int)min);

        /// <summary>
        /// Ensures the backing buffer can accommodate at least <paramref name="min"/> bytes.
        /// Rents a larger buffer if needed, copying existing data and returning the old buffer.
        /// </summary>
        /// <param name="min">The minimum required capacity.</param>
        private void EnsureCapacity(int min)
        {
            if (_owner != null)
            {
                if (_isFixedLength && min > _length)
                    throw new ArgumentOutOfRangeException(nameof(min), "Capacity exceeds fixed segment length.");
                _owner.EnsureCapacity(_baseOffset + min);
                return;
            }

            if (!_ownsBuffer) return;
            if (min <= _buffer.Length) return;
            int newSize = Math.Max(_buffer.Length * 2, min);
            var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
            Array.Copy(_buffer, 0, newBuf, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = newBuf;
            _bufferMemory = newBuf;
        }

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> if this stream has been disposed.
        /// </summary>
        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BufferStream));
            if (_owner != null && _owner._disposed)
                throw new ObjectDisposedException(nameof(BufferStream));
        }

        /// <summary>
        /// Returns the root owner for this instance.<br/>
        /// For non-segments, this instance is the owner.<br/>
        /// </summary>
        private BufferStream RootOwner => _owner == null ? this : _owner.RootOwner;

        /// <summary>
        /// Returns the current logical length for this instance.<br/>
        /// For dynamic segments, this is derived from the owner's length and base offset.<br/>
        /// </summary>
        private int EffectiveLength
        {
            get
            {
                if (_owner == null) return _length;
                if (_isFixedLength) return _length;
                int len = _owner._length - _baseOffset;
                return len < 0 ? 0 : len;
            }
        }

        /// <summary>
        /// Returns the base offset into the owner's buffer for this instance.<br/>
        /// For non-segments, this is always zero.<br/>
        /// </summary>
        private int BaseOffset => _owner == null ? 0 : _baseOffset;

        /// <summary>
        /// Returns the backing memory for this instance.<br/>
        /// For segments, this returns the owner's backing memory.<br/>
        /// </summary>
        private Memory<byte> BufferMemory => _owner == null ? _bufferMemory : _owner._bufferMemory;

        /// <summary>
        /// Returns a writable span slice relative to this instance's base offset.<br/>
        /// </summary>
        private Span<byte> GetWritableSpan(int offset, int count) => BufferMemory.Span.Slice(BaseOffset + offset, count);

        /// <summary>
        /// Returns a read-only span slice relative to this instance's base offset.<br/>
        /// </summary>
        private ReadOnlySpan<byte> GetReadOnlySpan(int offset, int count) => BufferMemory.Span.Slice(BaseOffset + offset, count);

        /// <summary>
        /// Returns a memory slice relative to this instance's base offset.<br/>
        /// </summary>
        private Memory<byte> GetMemorySlice(int offset, int count) => BufferMemory.Slice(BaseOffset + offset, count);

        /// <summary>
        /// Updates the owning length after a write at the specified logical position.<br/>
        /// </summary>
        private void UpdateLengthAfterWrite(int endPosition)
        {
            if (_owner == null)
            {
                if (endPosition > _length)
                    _length = endPosition;
                return;
            }

            if (_isFixedLength)
            {
                if (endPosition > _length)
                    throw new ArgumentOutOfRangeException(nameof(endPosition), "Write exceeds fixed segment length.");
                return;
            }

            int ownerEnd = _baseOffset + endPosition;
            if (ownerEnd > _owner._length)
                _owner._length = ownerEnd;
        }

        /// <summary>
        /// Rents a segment instance for this stream using the owning pool.<br/>
        /// The segment is a view and does not allocate a buffer.<br/>
        /// </summary>
        private BufferStream RentSegment(int offset, int length, bool fixedLength)
        {
            EnsureNotDisposed();
            int effectiveLength = EffectiveLength;

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");

            if (fixedLength)
            {
                if (length < 0 || offset + length > effectiveLength)
                    throw new ArgumentOutOfRangeException(nameof(length), "Segment bounds must lie within written data.");
            }
            else
            {
                if (offset > effectiveLength)
                    throw new ArgumentOutOfRangeException(nameof(offset), "Segment offset must lie within written data.");
            }

            BufferStream owner = RootOwner;
            int baseOffset = BaseOffset + offset;

            BufferStream segment = owner.RentPooledSegment();
            segment.InitializeSegment(owner, baseOffset, fixedLength, length);
            return segment;
        }

        /// <summary>
        /// Initializes a pooled segment to view a region of the owner's buffer.<br/>
        /// This resets position state and sets fixed/dynamic length semantics.<br/>
        /// </summary>
        private void InitializeSegment(BufferStream owner, int baseOffset, bool fixedLength, int length)
        {
            _owner = owner;
            _baseOffset = baseOffset;
            _isFixedLength = fixedLength;
            _length = fixedLength ? length : 0;
            _position = 0;
            _disposed = false;
            _ownsBuffer = false;
            _isPooledSegment = true;
        }

        /// <summary>
        /// Returns a pooled segment to its owner's pool when capacity allows.<br/>
        /// The segment is reset and marked disposed to prevent further use.<br/>
        /// </summary>
        private void ReturnSegmentToPool()
        {
            var owner = _owner;
            _disposed = true;
            _position = 0;
            _baseOffset = 0;
            _isFixedLength = false;
            _length = 0;

            if (owner == null || owner._disposed)
                return;

            owner.CachePooledSegment(this);
        }

        private T[] ReadArrayWithLength<T>() where T : unmanaged
        {
            int byteCount = Read7BitEncodedInt();
            if (byteCount < 0 || _position + byteCount > EffectiveLength)
                throw new EndOfStreamException();

            int elementCount = byteCount / Unsafe.SizeOf<T>();
            T[] result = new T[elementCount];

            ReadOnlySpan<byte> src = GetReadOnlySpan(_position, byteCount);
            var destSpan = MemoryMarshal.AsBytes(result.AsSpan());
            src.CopyTo(destSpan);

            _position += byteCount;
            return result;
        }

        private T ReadPrimitive<T>() where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            if (_position + size > EffectiveLength)
                throw new EndOfStreamException();

            T value = MemoryMarshal.Read<T>(GetReadOnlySpan(_position, size));
            _position += size;
            return value;
        }

        private void WriteAtOffset<T>(uint destinationOffset, T value) where T : unmanaged
        {
            int size = Marshal.SizeOf<T>();
            if (destinationOffset < 0 || destinationOffset + size > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset), "Destination bounds must lie within written data.");

            MemoryMarshal.Write(GetWritableSpan((int)destinationOffset, size), in value);
        }

        /// <summary>
        /// Writes an array of unmanaged values to the internal buffer,
        /// prefixing the serialized bytes with a 7-bit encoded length.
        /// </summary>
        /// <remarks>
        /// Type "T" must be <see langword="unmanaged"/>. Supported framework types include:<br/>
        /// • Integer types: <see cref="byte"/>, <see cref="sbyte"/>, <see cref="short"/>, <see cref="ushort"/>, 
        ///   <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, <see cref="ulong"/><br/>
        /// • Floating-point types: <see cref="float"/>, <see cref="double"/>, <see cref="Half"/><br/>
        /// • Boolean and character: <see cref="bool"/> (1 byte 0/1), <see cref="char"/> (16-bit UTF-16 code unit)<br/>
        /// • Other blittable structs: <see cref="System.Numerics.Vector2"/>, <see cref="System.Numerics.Vector3"/>, 
        ///   <see cref="System.Numerics.Vector4"/>, <see cref="System.Numerics.Matrix4x4"/> and similar
        /// - <see cref="decimal"/> and <see cref="DateTime"/> are not supported because they are not blittable.<br/>
        /// - <see cref="System.Text.Rune"/> is technically blittable today, but its internal layout is not part of 
        ///   the public contract and should not be persisted with this method.<br/>
        /// - The length prefix is the total byte count, not the element count. Readers must divide by 
        ///   <c>Unsafe.SizeOf&lt;T&gt;()</c> to determine the array length.<br/>
        /// <example>
        /// <code>
        /// // Example: writing a char[]
        /// var chars = "Hello".ToCharArray();
        /// WriteArrayWithLength(chars);
        /// </code>
        /// </example>
        /// </remarks>
        private void WriteBlittableArrayWithLength<T>(T[] source) where T : unmanaged
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            int byteCount = source.Length * Unsafe.SizeOf<T>();
            int lenSize = Get7BitEncodedIntSize(byteCount);
            EnsureCapacity(_position + byteCount + lenSize);

            // Write length prefix
            Write7BitEncodedInt(byteCount);

            // Write data as raw bytes
            var srcSpan = MemoryMarshal.AsBytes(source.AsSpan());
            srcSpan.CopyTo(GetWritableSpan(_position, byteCount));
            _position += byteCount;

            UpdateLengthAfterWrite(_position);
        }

        private void WritePrimitive<T>(T value) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            EnsureCapacity(_position + size);
            MemoryMarshal.Write(GetWritableSpan(_position, size), in value);
            _position += size;
            UpdateLengthAfterWrite(_position);
        }








        /// <inheritdoc/>
        //------ Public Methods -----
        public override void Flush()
        {
            EnsureNotDisposed();
            // No-op since data is in-memory
        }

        /// <summary>
        /// Returns a writeable slice of the written contents.
        /// </summary>
        /// <param name="offset">The zero-based byte offset into the written buffer.</param>
        /// <param name="count">The number of bytes to include in the slice.</param>
        /// <returns>A <see cref="ReadOnlyMemory{T}"/> representing the requested slice.</returns>
        public Memory<byte> Memory(int offset)
        {
            EnsureNotDisposed();
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Slice bounds must lie within written data.");
            return GetMemorySlice(offset, EffectiveLength - offset);
        }

        /// <summary>
        /// Returns a writeable slice of the written contents.
        /// </summary>
        /// <param name="offset">The zero-based byte offset into the written buffer.</param>
        /// <param name="count">The number of bytes to include in the slice.</param>
        /// <returns>A <see cref="ReadOnlyMemory{T}"/> representing the requested slice.</returns>
        public Memory<byte> Memory(int offset, int count)
        {
            EnsureNotDisposed();
            if (offset < 0 || count < 0 || offset + count > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(offset), "Slice bounds must lie within written data.");
            return GetMemorySlice(offset, count);
        }

        /// <inheritdoc/>
        public override int Read(byte[] destination, int offset, int count)
        {
            EnsureNotDisposed();
            var destSpan = destination.AsSpan(offset, count);
            var available = Math.Min(count, EffectiveLength - _position);
            if (available <= 0) return 0;

            GetReadOnlySpan(_position, available).CopyTo(destSpan);
            _position += available;
            return available;
        }

        public int Read7BitEncodedInt()
        {
            int count = 0;
            int shift = 0;
            byte b;
            do
            {
                b = ReadByte();
                count |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            // ZigZag decoding
            return (int)((uint)count >> 1) ^ (-(count & 1));
        }

        public Int128 Read7BitEncodedInt128()
        {
            Int128 result = 0;
            int shift = 0;
            byte b;
            do
            {
                b = ReadByte();
                result |= (Int128)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            // ZigZag decoding
            return (result >> 1) ^ (-(result & 1));
        }

        public long Read7BitEncodedLong()
        {
            long result = 0;
            int shift = 0;
            byte b;
            do
            {
                b = ReadByte();
                result |= ((long)(b & 0x7F)) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            // ZigZag decoding
            return (result >> 1) ^ (-(result & 1));
        }

        public uint Read7BitEncodedUInt()
        {
            uint count = 0;
            int shift = 0;
            byte b;
            do
            {
                b = ReadByte();
                count |= (uint)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return count;
        }

        public UInt128 Read7BitEncodedUInt128()
        {
            UInt128 result = 0;
            int shift = 0;
            byte b;
            do
            {
                b = ReadByte();
                result |= (UInt128)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return result;
        }

        public ulong Read7BitEncodedULong()
        {
            ulong result = 0;
            int shift = 0;
            byte b;
            do
            {
                b = ReadByte();
                result |= (ulong)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return result;
        }

        /// <summary>
        /// Returns a segment of the buffer without changing the position.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public byte[] ReadAtOffset(int offset, int count)
        {
            // Validate parameters
            if (offset < 0 || count < 0 || offset + count > EffectiveLength)
                throw new ArgumentOutOfRangeException();

            // Get a read-only span of the desired slice
            var span = ReadOnlySpan(offset, count);

            // Copy to new array
            byte[] result = new byte[count];
            span.CopyTo(result);

            return result;
        }
        /// <summary>
        /// Returns a segment of the buffer without changing the position.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public byte[] ReadAtOffset(uint offset, int count)
        {
            // Validate parameters
            if (offset < 0 || count < 0 || offset + count > EffectiveLength)
                throw new ArgumentOutOfRangeException();

            // Get a read-only span of the desired slice
            var span = ReadOnlySpan((int)offset, count);

            // Copy to new array
            byte[] result = new byte[count];
            span.CopyTo(result);

            return result;
        }

        public BigInteger ReadBigInteger() => new BigInteger(ReadBytesWithLength());

        public BigInteger[] ReadBigIntegersWithLength()
        {
            int length = Read7BitEncodedInt();
            var ret = new BigInteger[length];
            for (int i = 0; i < length; i++)
            {
                ret[i] = ReadBigInteger();
            }
            return ret;
        }

        public BitArray ReadBitArray() => new BitArray(ReadBytesWithLength()) { Length = Read7BitEncodedInt() };

        public bool[] ReadBoolsWithLength() => ReadArrayWithLength<bool>();

        public byte this[int index] => PeekByte(index);

        public byte PeekByte() => BufferMemory.Span[BaseOffset + _position];

        /// <summary>
        /// Reads one byte and advances this live cursor only when a byte is available.<br/>
        /// Intentionally hides Stream.ReadByte: direct BufferStream callers receive a byte or EndOfStreamException, not an int with an EOF sentinel.<br/>
        /// A Stream-typed caller retains the inherited int/minus-one read contract through the guarded array-read override.<br/>
        /// </summary>
        /// <returns>The next byte in this view.<br/></returns>
        /// <exception cref="ObjectDisposedException">This cursor or its owning stream has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The cursor is at the end of this view.<br/></exception>
        public new byte ReadByte()
        {
            EnsureNotDisposed();
            if (_position >= EffectiveLength)
                throw new EndOfStreamException();
            return BufferMemory.Span[BaseOffset + _position++];
        }

        public byte PeekByte(int index)
        {
            if (index < 0 || index >= EffectiveLength)
                throw new EndOfStreamException();
            return BufferMemory.Span[BaseOffset + index];
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            int available = Math.Min(count, EffectiveLength - _position);
            byte[] result = new byte[available];
            GetReadOnlySpan(_position, available).CopyTo(result);
            _position += available;
            return result;
        }

        public byte[] ReadBytesWithLength() => ReadArrayWithLength<byte>();

        public char ReadChar() => ReadPrimitive<char>();

        //chars
        public char[] ReadCharsWithLength() => ReadArrayWithLength<char>();

        public Complex ReadComplex() { return new Complex(ReadPrimitive<double>(), ReadPrimitive<double>()); }

        public DateOnly ReadDateOnly() => ReadPrimitive<DateOnly>();

        public DateTime ReadDateTime() => DateTime.FromBinary(ReadInt64());

        public decimal ReadDecimal()
        {
            int[] bits = new int[4];
            for (int i = 0; i < 4; i++)
                bits[i] = ReadInt32();
            decimal value = new decimal(bits);
            return value;
        }

        public double ReadDouble() => ReadPrimitive<double>();

        //doubles
        public double[] ReadDoubles() => ReadArrayWithLength<double>();

        //floats
        public float[] ReadFloats() => ReadArrayWithLength<float>();

        public Guid ReadGuid() => ReadPrimitive<Guid>();

        // halfs
        public Half[] ReadHalfs() => ReadArrayWithLength<Half>();

        public Int128 ReadInt128() => ReadPrimitive<Int128>();

        //int128s
        public Int128[] ReadInt128s() => ReadArrayWithLength<Int128>();

        public sbyte ReadSByte() => ReadPrimitive<sbyte>();
        public short ReadInt16() => ReadPrimitive<short>();

        // shorts
        public short[] ReadInt16s() => ReadArrayWithLength<short>();

        public int ReadInt32() => ReadPrimitive<int>();

        //ints
        public int[] ReadInt32s() => ReadArrayWithLength<int>();

        public long ReadInt64() => ReadPrimitive<long>();

        //longs
        public long[] ReadInt64s() => ReadArrayWithLength<long>();

        public Matrix3x2 ReadMatrix3x2() { return new Matrix3x2(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        public Matrix4x4 ReadMatrix4x4() { return new Matrix4x4(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        /// <summary>
        /// Returns a slice of the written contents.
        /// </summary>
        /// <param name="offset">The zero-based byte offset into the written buffer.</param>
        /// <param name="count">The number of bytes to include in the slice.</param>
        /// <returns>A <see cref="ReadOnlyMemory{T}"/> representing the requested slice.</returns>
        public ReadOnlyMemory<byte> ReadOnlyMemory(int offset, int count)
        {
            EnsureNotDisposed();
            if (offset < 0 || count < 0 || offset + count > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(offset), "Slice bounds must lie within written data.");
            return GetMemorySlice(offset, count);
        }

        /// <summary>
        /// Returns a span of the written contents.
        /// </summary>
        /// <param name="offset">The zero-based byte offset into the written buffer.</param>
        /// <param name="count">The number of bytes to include in the span.</param>
        /// <returns>A <see cref="ReadOnlySpan{T}"/> representing the requested span.</returns>
        public ReadOnlySpan<byte> ReadOnlySpan(int offset, int count)
        {
            EnsureNotDisposed();
            if (offset < 0 || count < 0 || offset + count > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(offset), "Slice bounds must lie within written data.");
            return GetWritableSpan(offset, count);
        }

        public Plane ReadPlane() { return new Plane(ReadVector3(), ReadPrimitive<float>()); }

        public Quaternion ReadQuaternion() { return new Quaternion(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        public Rune ReadRune() => new Rune(ReadInt32());

        public sbyte[] ReadSBytesWithLength() => ReadArrayWithLength<sbyte>();

        public float ReadSingle() => ReadPrimitive<float>();

        public string? ReadString()
        {
            int length = Read7BitEncodedInt();
            if (length < 0) return null; // negative length indicates null
            var span = GetReadOnlySpan(_position, length);
            string value = StringEncoding.GetString(span);
            _position += length;
            return value;
        }

        //strings
        public string[] ReadStringArray()
        {
            // read 7bit byte count
            int count = Read7BitEncodedInt();

            // read strings
            string[] result = new string[count];
            for (int i = 0; i < count; i++)
                result[i] = ReadString()!;
            return result;
        }

        public TimeOnly ReadTimeOnly()
        {
            long ticks = ReadInt64();
            return new TimeOnly(ticks);
        }

        public TimeSpan ReadTimeSpan()
        {
            long ticks = ReadInt64();
            return new TimeSpan(ticks);
        }

        public DateTimeOffset ReadDateTimeOffset() => new DateTimeOffset(ReadDateTime(), ReadTimeSpan());

        public UInt128 ReadUInt128() => ReadPrimitive<UInt128>();

        //uint128s
        public UInt128[] ReadUInt128s() => ReadArrayWithLength<UInt128>();

        public ushort ReadUInt16() => ReadPrimitive<ushort>();

        //ushorts
        public ushort[] ReadUInt16s() => ReadArrayWithLength<ushort>();

        public uint ReadUInt32() => ReadPrimitive<uint>();

        //uints
        public uint[] ReadUInt32s() => ReadArrayWithLength<uint>();

        public ulong ReadUInt64() => ReadPrimitive<ulong>();

        //ulongs
        public ulong[] ReadUInt64s() => ReadArrayWithLength<ulong>();

        public Vector2 ReadVector2() { return new Vector2(ReadPrimitive<float>(), ReadPrimitive<float>()); }

        public Vector3 ReadVector3() { return new Vector3(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        public Vector4 ReadVector4() { return new Vector4(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        public Version[] ReadVersionsWithLength()
        {
            int count = Read7BitEncodedInt();
            Version[] versions = new Version[count];
            for (int i = 0; i < count; i++)
            {
                versions[i] = new Version(Read7BitEncodedInt(), Read7BitEncodedInt(), Read7BitEncodedInt(), Read7BitEncodedInt());
            }
            return versions;
        }

        /// <summary>
        /// Resets the stream to an empty state. Optionally clears the buffer's contents.
        /// </summary>
        /// <param name="clearBuffer">
        /// If true, zeros out the written buffer contents up to the current length.
        /// </param>
        public void Reset(bool clearBuffer = false)
        {
            EnsureNotDisposed();

            int effectiveLength = EffectiveLength;
            if (clearBuffer && effectiveLength > 0)
                GetWritableSpan(0, effectiveLength).Clear();

            _position = 0;
            if (_owner == null)
                _length = 0;
        }

        /// <summary>
        /// Rebinds a borrowed root stream to a different read-only memory segment without allocating a replacement stream object.<br/>
        /// This operation is intended for tight sequential-reader loops in which every segment borrowed from the prior payload has already been disposed.<br/>
        /// Owned-buffer streams and child segments are rejected because changing their backing storage would violate their ownership or view contracts.<br/>
        /// </summary>
        /// <param name="segment">New borrowed payload memory exposed from position zero.<br/></param>
        public void RebindReadOnlyMemory(ReadOnlyMemory<byte> segment)
        {
            EnsureNotDisposed();
            if (_owner != null || _ownsBuffer)
                throw new InvalidOperationException("Only a borrowed root BufferStream can be rebound to another memory segment.");

            _bufferMemory = MemoryMarshal.TryGetArray(segment,out ArraySegment<byte> arraySegment)
                ? arraySegment.Array!.AsMemory(arraySegment.Offset,arraySegment.Count)
                : segment.ToArray();
            _length = segment.Length;
            _position = 0;
            _baseOffset = 0;
            _isFixedLength = false;
        }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            EnsureNotDisposed();
            int newPos = origin switch
            {
                SeekOrigin.Begin => (int)offset,
                SeekOrigin.Current => _position + (int)offset,
                SeekOrigin.End => EffectiveLength + (int)offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin), "Invalid SeekOrigin.")
            };
            if (newPos < 0 || newPos > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(offset), "Seek position must be within the length of the stream.");
            _position = newPos;
            return _position;
        }

        public BufferStream CloneShallow() => Segment();

        public BufferStream Segment() => RentSegment(_position, 0, fixedLength: false);

        public BufferStream Segment(int offset) => RentSegment(offset, 0, fixedLength: false);

        /// <summary>
        /// Returns a fixed-length segment of the buffer starting at <paramref name="offset"/>.<br/>
        /// Index zero of the returned stream corresponds to the specified offset.<br/>
        /// </summary>
        /// <param name="offset">The zero-based offset into the written data.<br/></param>
        /// <param name="length">The fixed length of the segment.<br/></param>
        public BufferStream Segment(int offset, int length) => RentSegment(offset, length, fixedLength: true);

        public BufferStream Segment(uint offset) => Segment((int)offset);

        /// <inheritdoc/>
        public override void SetLength(long value)
        {
            if (_owner != null || !_ownsBuffer) throw new InvalidOperationException("This BufferStream is a slice of another.  Cannot set length.  Use the parent stream.");
            EnsureNotDisposed();
            if (value < 0 || value > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), "Length must be non-negative and within Int32 range.");

            EnsureCapacity((int)value);
            _length = (int)value;
            if (_position > _length)
                _position = _length;
        }

        /// <summary>
        /// Returns a mutable span slice of the written contents.
        /// </summary>
        /// <param name="offset">The zero-based byte offset into the written buffer.</param>
        /// <param name="count">The number of bytes to include in the slice.</param>
        /// <returns>A <see cref="Span{T}"/> representing the requested slice.</returns>
        public Span<byte> Span(int offset, int count)
        {
            EnsureNotDisposed();
            if (offset < 0 || count < 0 || offset + count > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(offset), "Slice bounds must lie within written data.");
            return GetWritableSpan(offset, count);
        }

        public byte[] ToArray()
        {
            EnsureNotDisposed();
            return GetReadOnlySpan(0, EffectiveLength).ToArray();
        }

        public void Write(bool value) => WritePrimitive(value);

        public void Write(byte value) => WritePrimitive(value);

        public void Write(sbyte value) => WritePrimitive(value);

        public void Write(short value) => WritePrimitive(value);

        public void Write(ushort value) => WritePrimitive(value);

        public void Write(int value) => WritePrimitive(value);

        public void Write(uint value) => WritePrimitive(value);

        public void Write(long value) => WritePrimitive(value);

        public void Write(ulong value) => WritePrimitive(value);

        public void Write(Int128 value)
        {
            Write((ulong)(value & ulong.MaxValue));
            Write((ulong)(value >> 64));
        }

        public void Write(UInt128 value)
        {
            Write((ulong)(value & ulong.MaxValue));
            Write((ulong)(value >> 64));
        }

        public void Write(Half value) => WritePrimitive(value);

        public void Write(float value) => WritePrimitive(value);

        public void Write(double value) => WritePrimitive(value);

        public void Write(decimal value)
        {
            int[] bits = decimal.GetBits((decimal)value);
            foreach (var i in bits)
                WritePrimitive(i); // or your WriteInt32 routine

        }

        public void Write(char value) => WritePrimitive(value);

        public void Write(Rune value) => WritePrimitive((int)value.Value);

        public void Write(DateTime value) => WritePrimitive(value.ToBinary());
        public void Write(DateTimeOffset value) { WritePrimitive(value.DateTime.ToBinary()); WritePrimitive(value.Offset.Ticks); }

        public void Write(Guid value) => WritePrimitive(value);

        public void Write(DateOnly value)
        {
            Write(value.DayNumber);  // Int32
        }

        public void Write(TimeSpan value) => WritePrimitive(value);

        public void Write(TimeOnly value)
        {
            Write(value.Ticks);  // Int64
        }

        public void Write(Vector2 value) { WritePrimitive(value.X); WritePrimitive(value.Y); }

        public void Write(Vector3 value) { WritePrimitive(value.X); WritePrimitive(value.Y); WritePrimitive(value.Z); }

        public void Write(Vector4 value) { WritePrimitive(value.X); WritePrimitive(value.Y); WritePrimitive(value.Z); WritePrimitive(value.W); }

        public void Write(Complex value) { WritePrimitive(value.Real); WritePrimitive(value.Imaginary); }

        public void Write(Quaternion value) { WritePrimitive(value.X); WritePrimitive(value.Y); WritePrimitive(value.Z); WritePrimitive(value.W); }

        public void Write(Plane value) { Write(value.Normal); WritePrimitive(value.D); }

        public void Write(Matrix3x2 value) { WritePrimitive(value.M11); WritePrimitive(value.M12); WritePrimitive(value.M21); WritePrimitive(value.M22); WritePrimitive(value.M31); WritePrimitive(value.M32); }

        public void Write(Matrix4x4 m4x4) { WritePrimitive(m4x4.M11); WritePrimitive(m4x4.M12); WritePrimitive(m4x4.M13); WritePrimitive(m4x4.M14); WritePrimitive(m4x4.M21); WritePrimitive(m4x4.M22); WritePrimitive(m4x4.M23); WritePrimitive(m4x4.M24); WritePrimitive(m4x4.M31); WritePrimitive(m4x4.M32); WritePrimitive(m4x4.M33); WritePrimitive(m4x4.M34); WritePrimitive(m4x4.M41); WritePrimitive(m4x4.M42); WritePrimitive(m4x4.M43); WritePrimitive(m4x4.M44); }

        public void Write(BigInteger value)
        {
            var bytes = value.ToByteArray();
            WriteBytesWithLength(bytes);
        }

        /// <summary>
        /// Writes a string to the stream, encoded as by <see cref="StringEncoding"/>.<br/>
        /// If the string is null, a length of -1 is written.  When read, the negative length indicates null.<br/>
        /// If the string is empty, a length of 0 is written.<br/>
        /// </summary>
        /// <param name="value"></param>
        public void Write(string? value)
        {
            if (value == null) { Write7BitEncodedInt(-1); return; }
            var utf8Bytes = StringEncoding.GetBytes(value);
            Write7BitEncodedInt(utf8Bytes.Length);
            WriteBytes(utf8Bytes);
        }

        public void Write(Memory<byte> value) => WriteBytesWithLength(value.ToArray());

        public void Write(BitArray value)
        {
            int count = (value.Length + 7) / 8;
            byte[] bytes = new byte[count];
            value.CopyTo(bytes, 0);
            WriteBytesWithLength(bytes);
            Write7BitEncodedInt(value.Length);
        }

        public override void Write(byte[] source, int offset, int count)
        {
            WriteBytes(source, offset, count);
        }

        public void Write7BitEncodedInt(int value)
        {
            uint v = (uint)((value << 1) ^ (value >> 31));
            while (v >= 0x80)
            {
                WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            WriteByte((byte)v);
        }

        public void Write7BitEncodedInt128(Int128 value)
        {
            UInt128 v = (UInt128)((value << 1) ^ (value >> 127));
            while (v >= 0x80)
            {
                WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            WriteByte((byte)v);
        }

        public void Write7BitEncodedLong(long value)
        {
            ulong v = (ulong)value; // ensures correct handling of sign
            while (v >= 0x80)
            {
                WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            WriteByte((byte)v);
        }

        public void Write7BitEncodedUInt(uint value)
        {
            uint v = (uint)value;
            while (v >= 0x80)
            {
                WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            WriteByte((byte)v);
        }

        public void Write7BitEncodedUInt128(UInt128 value)
        {
            UInt128 v = value;
            while (v >= 0x80)
            {
                WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            WriteByte((byte)v);
        }

        public void Write7BitEncodedULong(long value)
        {
            ulong v = (ulong)((value << 1) ^ (value >> 63));
            while (v >= 0x80)
            {
                WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            WriteByte((byte)v);
        }

        public void WriteAtOffset(uint destinationOffset, ReadOnlySpan<byte> source)
        {
            if (destinationOffset < 0 || destinationOffset + source.Length > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset), "Destination bounds must lie within written data.");

            source.CopyTo(GetWritableSpan((int)destinationOffset, source.Length));
        }

        public void WriteAtOffset(uint destinationOffset, byte value) => WriteAtOffset<byte>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, sbyte value) => WriteAtOffset<sbyte>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, short value) => WriteAtOffset<short>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, ushort value) => WriteAtOffset<ushort>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, int value) => WriteAtOffset<int>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, uint value) => WriteAtOffset<uint>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, long value) => WriteAtOffset<long>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, ulong value) => WriteAtOffset<ulong>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, float value) => WriteAtOffset<float>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, double value) => WriteAtOffset<double>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, decimal value) => WriteAtOffset<decimal>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, bool value) => WriteAtOffset<bool>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, char value) => WriteAtOffset<char>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, Guid value) => WriteAtOffset<Guid>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, DateTime value) => WriteAtOffset<DateTime>(destinationOffset, value);

        public void WriteAtOffset(uint destinationOffset, TimeSpan value) => WriteAtOffset<TimeSpan>(destinationOffset, value);

        public void WriteAtOffset(int destinationOffset, string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            byte[] bytes = StringEncoding.GetBytes(value);
            int totalSize = Get7BitEncodedIntSize(bytes.Length) + bytes.Length;

            if (destinationOffset < 0 || destinationOffset + totalSize > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset), "Destination bounds must lie within written data.");

            Span<byte> span = GetWritableSpan(destinationOffset, totalSize);

            // Write length as 7-bit encoded
            int written = Write7BitEncodedIntToSpan(span, bytes.Length);

            // Write string bytes
            bytes.AsSpan().CopyTo(span.Slice(written));
        }

        public void WriteAtOffset(int destinationOffset, byte[] source, int sourceOffset, int count)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (destinationOffset < 0 || destinationOffset + count > EffectiveLength)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset), "Destination bounds must lie within written data.");
            if (sourceOffset < 0 || count < 0 || sourceOffset + count > source.Length)
                throw new ArgumentOutOfRangeException(nameof(sourceOffset), "Source bounds are invalid.");

            source.AsSpan(sourceOffset, count).CopyTo(GetWritableSpan(destinationOffset, count));
        }

        public void WriteBigIntegersWithLength(BigInteger[] source)
        {
            //write 7bit count
            Write7BitEncodedInt(source.Length);
            // write binary for each decimal
            for (int i = 0; i < source.Length; i++)
                Write(source[i]);
        }

        public void WriteBooleansWithLength(bool[] source) => WriteBlittableArrayWithLength(source);

        /// <summary>
        /// Writes one byte through the specialized buffer path for both BufferStream and Stream callers.<br/>
        /// Honors owner lifetime and fixed-view bounds; advances position and owning length only after the write succeeds.<br/>
        /// Owned roots may grow, while insufficient borrowed storage is rejected without advancing the cursor.<br/>
        /// </summary>
        /// <param name="value">Byte to store at the current cursor position.<br/></param>
        /// <exception cref="ObjectDisposedException">This cursor or its owning stream has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public override void WriteByte(byte value)
        {
            EnsureNotDisposed();
            EnsureCapacity(_position + 1);
            GetWritableSpan(_position, 1)[0] = value;
            _position++;
            UpdateLengthAfterWrite(_position);
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            EnsureCapacity(_position + bytes.Length);
            bytes.CopyTo(GetWritableSpan(_position, bytes.Length));
            _position += bytes.Length;
            UpdateLengthAfterWrite(_position);
        }

        public void WriteBytes(byte[] source, int offset, int count)
        {
            EnsureNotDisposed();
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (offset < 0 || count < 0 || offset + count > source.Length)
                throw new ArgumentOutOfRangeException();

            EnsureCapacity(_position + count);
            source.AsSpan(offset, count).CopyTo(GetWritableSpan(_position, count));
            _position += count;
            UpdateLengthAfterWrite(_position);
        }

        /// <summary>
        /// Writes a byte array with a 7bit encode length prefix.<br/>
        /// Read with <see cref="ReadBytesWithLength"/>
        /// </summary>
        /// <param name="source"></param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public void WriteBytesWithLength(byte[] source) => WriteBlittableArrayWithLength(source);

        /// <summary>
        /// Writes a byte array with a 7bit encode length prefix.<br/>
        /// Read with <see cref="ReadBytesWithLength"/>
        /// </summary>
        /// <param name="source"></param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public void WriteBytesWithLength(byte[] source, int offset, int count)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (offset < 0 || count < 0 || offset + count > source.Length)
                throw new ArgumentOutOfRangeException();
            var lenSize = Get7BitEncodedIntSize(count);
            EnsureCapacity(_position + count + lenSize);
            Write7BitEncodedIntToSpan(GetWritableSpan(_position, lenSize), count);
            _position += lenSize;
            source.AsSpan(offset, count).CopyTo(GetWritableSpan(_position, count));
            _position += count;
            UpdateLengthAfterWrite(_position);
        }

        public void WriteCharsWithLength(char[] source) => WriteBlittableArrayWithLength(source);

        public void WriteComplexesWithLength(Complex[] source) => WriteBlittableArrayWithLength(source);

        public void WriteDateOnlysWithLength(DateOnly[] source) => WriteBlittableArrayWithLength(source);

        public void WriteDateTimeOffsetsWithLength(DateTimeOffset[] source) => WriteBlittableArrayWithLength(source);

        public void WriteDateTimesWithLength(DateTime[] source) => WriteBlittableArrayWithLength(source);
        //public void WriteDateTimesWithLength(DateTime[] source)
        //{
        //    if (source == null)
        //        throw new ArgumentNullException(nameof(source));
        //    int byteCount = source.Length * 8;
        //    int lenSize = Get7BitEncodedIntSize(byteCount);
        //    EnsureCapacity(_position + byteCount + lenSize);
        //    // Write length prefix
        //    Write7BitEncodedIntToSpan(_bufferMemory.Span.Slice(_position), byteCount);
        //    _position += lenSize;
        //    // Write data as raw bytes
        //    var srcSpan = MemoryMarshal.AsBytes(source.AsSpan());
        //    srcSpan.CopyTo(GetWritableSpan(_position, byteCount));
        //    _position += byteCount;
        //    if (_position > _length)
        //        _length = _position;
        //}

        public void WriteDecimalsWithLength(decimal[] source)
        {
            //write 7bit count
            Write7BitEncodedInt(source.Length);
            // write binary for each decimal
            for (int i = 0; i < source.Length; i++)
                Write(source[i]);
        }

        public void WriteDoublesWithLength(double[] source) => WriteBlittableArrayWithLength(source);

        public void WriteGuidsWithLength(Guid[] source) => WriteBlittableArrayWithLength(source);

        public void WriteHalfsWithLength(Half[] source) => WriteBlittableArrayWithLength(source);

        public void WriteInt128sWithLength(Int128[] source) => WriteBlittableArrayWithLength(source);

        public void WriteIntsWithLength(int[] source) => WriteBlittableArrayWithLength(source);

        public void WriteLongsWithLength(long[] source) => WriteBlittableArrayWithLength(source);

        public void WriteMatrix3x2sWithLength(Matrix3x2[] source) => WriteBlittableArrayWithLength(source);

        public void WriteMatrix4x4sWithLength(Matrix4x4[] source) => WriteBlittableArrayWithLength(source);

        public void WritePlanesWithLength(Plane[] source) => WriteBlittableArrayWithLength(source);

        public void WriteQuaternionsWithLength(Quaternion[] source) => WriteBlittableArrayWithLength(source);

        public void WriteSBytesWithLength(sbyte[] source) => WriteBlittableArrayWithLength(source);

        public void WriteShortsWithLength(short[] source) => WriteBlittableArrayWithLength(source);

        public void WriteSinglesWithLength(float[] source) => WriteBlittableArrayWithLength(source);

        public void WriteTimeOnlysWithLength(TimeOnly[] source) => WriteBlittableArrayWithLength(source);

        public void WriteTimespansWithLength(TimeSpan[] source) => WriteBlittableArrayWithLength(source);

        public void WriteTimeSpansWithLength(TimeSpan[] source) => WriteBlittableArrayWithLength(source);

        public void WriteUInt128sWithLength(UInt128[] source) => WriteBlittableArrayWithLength(source);

        public void WriteUIntsWithLength(uint[] source) => WriteBlittableArrayWithLength(source);

        public void WriteULongsWithLength(ulong[] source) => WriteBlittableArrayWithLength(source);

        public void WriteUShortsWithLength(ushort[] source) => WriteBlittableArrayWithLength(source);

        public void WriteVector2sWithLength(Vector2[] source) => WriteBlittableArrayWithLength(source);

        public void WriteVector3sWithLength(Vector3[] source) => WriteBlittableArrayWithLength(source);

        public void WriteVector4sWithLength(Vector4[] source) => WriteBlittableArrayWithLength(source);

        public void WriteVersionsWithLength(Version[] source)
        {
            //write 7bit count
            Write7BitEncodedInt(source.Length);
            // write binary for each decimal
            foreach (var v in source)
            {
                Write7BitEncodedInt(v.Major);
                Write7BitEncodedInt(v.Minor);
                Write7BitEncodedInt(v.Build);
                Write7BitEncodedInt(v.Revision);
            }
        }


        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(this, obj))
                return true;
            if (obj is not BufferStream other)
                return false;

            int length = EffectiveLength;
            int otherLength = other.EffectiveLength;

            if (length != otherLength)
                return false;

            if (ReferenceEquals(RootOwner, other.RootOwner) && BaseOffset == other.BaseOffset)
                return true;

            // compare actual contents
            return GetReadOnlySpan(0, length)
                .SequenceEqual(other.GetReadOnlySpan(0, otherLength));
        }

        public override int GetHashCode()
        {
            int length = EffectiveLength;
            if (length == 0) return 0;
            var span = BufferMemory.Span;
            int baseOffset = BaseOffset;
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + span[baseOffset];
                hash = (hash * 31) + span[baseOffset + (length / 2)];
                hash = (hash * 31) + span[baseOffset + (length - 1)];
                hash = (hash * 31) + length;
                return hash;
            }
        }

        /// <summary>
        /// Zero out a section of the buffer<br/>
        /// This is a very fast operation using Span.Clear(), which usually is a memset operation by JIT.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        public void Zero(uint offset, uint length)
        {
            EnsureCapacity((int)(offset + length));
            GetWritableSpan((int)offset, (int)length).Clear();
            UpdateLengthAfterWrite((int)(offset + length));
        }

        public static bool operator ==(BufferStream? left, BufferStream? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            return left.Equals(right);
        }

        public static bool operator !=(BufferStream? left, BufferStream? right) => !(left == right);


        public static implicit operator BufferStream(byte[] buffer) => new BufferStream(buffer);
        public static implicit operator BufferStream(Span<byte> buffer) => new BufferStream(buffer.ToArray());
        public static implicit operator BufferStream(MemoryStream buffer) => new BufferStream(buffer);

        /// <summary>
        /// Removes one cached cursor from this root owner's pool, or creates a cursor without a backing-buffer allocation.<br/>
        /// The single-slot fast path precedes the bounded overflow stack; successful overflow removal releases its count reservation.<br/>
        /// Atomics address this instance's fields directly, avoiding marshal-by-reference field access through another instance.<br/>
        /// The caller must initialize the returned cursor before exposing it and must keep the root alive throughout the lease.<br/>
        /// </summary>
        /// <returns>An exclusively rented cursor that has not yet been initialized for its next view.<br/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private BufferStream RentPooledSegment()
        {
            BufferStream? segment = System.Threading.Interlocked.Exchange(ref _singleSegmentPool, null);
            if (segment is null && _segmentPool != null && _segmentPool.TryPop(out segment))
            {
                System.Threading.Interlocked.Decrement(ref _segmentPoolCount);
            }
            return segment ?? new BufferStream(this);
        }

        /// <summary>
        /// Publishes an already reset, disposed cursor into this root owner's single slot or bounded overflow stack.<br/>
        /// The caller must check root lifetime before calling and must not use the cursor after publication.<br/>
        /// Overflow capacity is reserved before pushing and rolled back when full; existing atomic ordering and lazy stack creation are preserved.<br/>
        /// Pool synchronization does not permit concurrent root mutation/disposal or concurrent use/disposal of the same leased cursor.<br/>
        /// </summary>
        /// <param name="segment">Cursor whose view state has been reset before this publication.<br/></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CachePooledSegment(BufferStream segment)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _singleSegmentPool, segment, null) is null)
                return;

            ConcurrentStack<BufferStream>? overflowPool = _segmentPool;
            if (overflowPool is null)
            {
                var createdPool = new ConcurrentStack<BufferStream>();
                overflowPool = System.Threading.Interlocked.CompareExchange(
                    ref _segmentPool, createdPool, null) ?? createdPool;
            }

            int count = System.Threading.Interlocked.Increment(ref _segmentPoolCount);
            if (count <= _segmentPoolMax)
            {
                overflowPool.Push(segment);
            }
            else
            {
                System.Threading.Interlocked.Decrement(ref _segmentPoolCount);
            }
        }



    }
}
