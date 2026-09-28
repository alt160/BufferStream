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
    public sealed class BufferStream : Stream, IEquatable<BufferStream>
    {




        /// <summary>
        /// Creates a non-owning writable stream view over the supplied memory without copying it.<br/>
        /// Mutations are visible through both views, and disposing the stream does not release or clear the caller-owned memory.<br/>
        /// </summary>
        /// <param name="segment">The writable memory to borrow as the complete initial stream contents.<br/></param>
        /// <returns>A writable borrowed stream positioned at zero.<br/></returns>
        public static implicit operator BufferStream(Memory<byte> segment) => new BufferStream(segment);

        /// <summary>
        /// Creates a writable stream from read-only memory, borrowing array-backed storage when available and otherwise copying it.<br/>
        /// The read-only wrapper does not enforce immutability: mutations through the stream are visible when its storage was borrowed.<br/>
        /// </summary>
        /// <param name="segment">The read-only memory supplying the complete initial stream contents.<br/></param>
        /// <returns>A writable stream positioned at zero.<br/></returns>
        public static implicit operator BufferStream(ReadOnlyMemory<byte> segment) => new BufferStream(segment);



        // Helper: Calculate size of 7-bit encoded int
        //Shared/Static Members
        private static int Get7BitEncodedIntSize(int value)
        {
            int count = 0;
            uint v = (uint)((value << 1) ^ (value >> 31));
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
            uint v = (uint)((value << 1) ^ (value >> 31));
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
        /// <param name="encoding">The encoding used by string read and write operations, or <see langword="null"/> to use <see cref="Encoding.UTF8"/>.</param>
        //======  CONSTRUCTORS  ======
        public BufferStream(uint initialCapacity = 4096, Encoding? encoding = null) : this((int)initialCapacity, encoding) { }

        /// <summary>
        /// Initializes a new instance of <see cref="BufferStream"/> with the specified initial capacity.<br/>
        /// The default string encoding is <see cref="Encoding.UTF8"/>.
        /// </summary>
        /// <param name="initialCapacity">The initial size of the underlying buffer in bytes.</param>
        /// <param name="encoding">The encoding used by string read and write operations, or <see langword="null"/> to use <see cref="Encoding.UTF8"/>.</param>
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

        /// <summary>
        /// Initializes a non-owning stream over writable caller-provided memory without copying it.<br/>
        /// The stream starts at position zero with a length equal to <paramref name="segment"/>. Disposing this instance does not return or otherwise release the caller's memory.<br/>
        /// </summary>
        /// <param name="segment">The writable backing memory to expose through the stream.</param>
        /// <param name="encoding">The encoding used by string read and write operations, or <see langword="null"/> to use <see cref="Encoding.UTF8"/>.</param>
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
        /// <summary>
        /// Initializes a non-owning stream from caller-provided read-only memory.<br/>
        /// Array-backed memory is used directly; other memory is copied to an internal array. The stream starts at position zero with a length equal to <paramref name="segment"/>.<br/>
        /// </summary>
        /// <param name="segment">The source memory to expose through the stream.</param>
        /// <param name="encoding">The encoding used by string read and write operations, or <see langword="null"/> to use <see cref="Encoding.UTF8"/>.</param>
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

        /// <summary>
        /// Initializes a stream from the remaining contents of a <see cref="MemoryStream"/>.<br/>
        /// When <paramref name="preferZeroCopy"/> is <see langword="true"/> and the source exposes its buffer, this instance borrows that buffer; otherwise it copies the remaining bytes into a pooled buffer.<br/>
        /// </summary>
        /// <param name="ms">The source memory stream, read from its current position when a copy is required.</param>
        /// <param name="preferZeroCopy"><see langword="true"/> to borrow an accessible source buffer when possible; otherwise <see langword="false"/> to always copy.</param>
        /// <param name="takeOwnership"><see langword="true"/> to dispose <paramref name="ms"/> after initialization; otherwise <see langword="false"/>.</param>
        /// <param name="encoding">The encoding used by string read and write operations, or <see langword="null"/> to use <see cref="Encoding.UTF8"/>.</param>
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

        /// <summary>
        /// Initializes a stream from the remaining contents of another stream.<br/>
        /// An accessible <see cref="MemoryStream"/> buffer is borrowed without copying; all other sources are copied from their current position into a pooled buffer.<br/>
        /// </summary>
        /// <param name="s">The source stream to consume from its current position.</param>
        /// <param name="takeOwnership"><see langword="true"/> to dispose <paramref name="s"/> after initialization; otherwise <see langword="false"/>.</param>
        /// <param name="encoding">The encoding used by string read and write operations, or <see langword="null"/> to use <see cref="Encoding.UTF8"/>.</param>
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

        /// <summary>
        /// Initializes a stream by copying the remaining contents of a <see cref="FileStream"/> into a pooled buffer.<br/>
        /// The source stream advances to its original position plus the copied byte count.<br/>
        /// </summary>
        /// <param name="fs">The source file stream to read from its current position.</param>
        /// <param name="takeOwnership"><see langword="true"/> to dispose <paramref name="fs"/> after initialization; otherwise <see langword="false"/>.</param>
        /// <param name="encoding">The encoding used by string read and write operations, or <see langword="null"/> to use <see cref="Encoding.UTF8"/>.</param>
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

        /// <summary>
        /// Gets the encoding used by this instance's string read and write operations.<br/>
        /// The value is <see cref="Encoding.UTF8"/> when construction did not supply an encoding.
        /// </summary>
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
            EnsureNotDisposed();

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

        /// <summary>
        /// Reads an unmanaged array whose payload is prefixed by its total byte length.<br/>
        /// The byte length must be divisible by the unmanaged element size.<br/>
        /// </summary>
        /// <typeparam name="T">The unmanaged element type.<br/></typeparam>
        /// <returns>A newly allocated array containing the decoded raw element values.<br/></returns>
        private T[] ReadArrayWithByteLength<T>() where T : unmanaged
        {
            int byteCount = Read7BitEncodedInt();
            if (byteCount < 0)
                throw new InvalidDataException("A byte-length-prefixed array cannot have a negative byte length.");

            int elementSize = Unsafe.SizeOf<T>();
            if (byteCount % elementSize != 0)
                throw new InvalidDataException($"The byte length {byteCount} is not divisible by the {elementSize}-byte element size.");

            if (byteCount > EffectiveLength - _position)
                throw new EndOfStreamException();

            int elementCount = byteCount / elementSize;
            T[] result = new T[elementCount];

            ReadOnlySpan<byte> src = GetReadOnlySpan(_position, byteCount);
            var destSpan = MemoryMarshal.AsBytes(result.AsSpan());
            src.CopyTo(destSpan);

            _position += byteCount;
            return result;
        }

        private T ReadPrimitive<T>() where T : unmanaged
        {
            EnsureNotDisposed();
            int size = Unsafe.SizeOf<T>();
            if (_position + size > EffectiveLength)
                throw new EndOfStreamException();

            T value = MemoryMarshal.Read<T>(GetReadOnlySpan(_position, size));
            _position += size;
            return value;
        }

        private Span<byte> GetWritableOffsetSpan(int destinationOffset, int count)
        {
            EnsureNotDisposed();
            int effectiveLength = EffectiveLength;
            if ((uint)destinationOffset > (uint)effectiveLength ||
                (uint)count > (uint)(effectiveLength - destinationOffset))
                throw new ArgumentOutOfRangeException(nameof(destinationOffset), "Destination bounds must lie within written data.");

            return GetWritableSpan(destinationOffset, count);
        }

        private void WriteAtOffset<T>(int destinationOffset, T value) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            MemoryMarshal.Write(GetWritableOffsetSpan(destinationOffset, size), in value);
        }

        /// <summary>
        /// Writes an unmanaged array as raw bytes prefixed by its total byte length.<br/>
        /// The prefix measures payload bytes rather than logical elements.<br/>
        /// </summary>
        /// <typeparam name="T">The unmanaged element type.<br/></typeparam>
        /// <param name="source">The array whose raw element bytes are written.<br/></param>
        /// <remarks>
        /// <typeparamref name="T"/> must be <see langword="unmanaged"/>. Supported framework types include:<br/>
        /// • Integer types: <see cref="byte"/>, <see cref="sbyte"/>, <see cref="short"/>, <see cref="ushort"/>, 
        ///   <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, <see cref="ulong"/><br/>
        /// • Floating-point types: <see cref="float"/>, <see cref="double"/>, <see cref="Half"/><br/>
        /// • Boolean and character: <see cref="bool"/> (1 byte 0/1), <see cref="char"/> (16-bit UTF-16 code unit)<br/>
        /// • Other unmanaged structs used by the public collection API, including dates, times, GUIDs, numerics, and matrices.<br/>
        /// The payload preserves the existing raw in-memory representation; it is distinct from repeatedly invoking semantic scalar writers.<br/>
        /// The length prefix is the total byte count, not the element count. Readers divide by
        ///   <c>Unsafe.SizeOf&lt;T&gt;()</c> to determine the array length.<br/>
        /// <example>
        /// <code>
        /// // Example: writing a char[]
        /// var chars = "Hello".ToCharArray();
        /// WriteArrayWithByteLength(chars);
        /// </code>
        /// </example>
        /// </remarks>
        private void WriteArrayWithByteLength<T>(T[] source) where T : unmanaged
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            int byteCount = checked(source.Length * Unsafe.SizeOf<T>());
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
        /// <returns>A <see cref="Memory{T}"/> representing the requested slice.</returns>
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

        /// <summary>
        /// Reads a signed 32-bit integer encoded with ZigZag transformation and base-128 continuation bytes.<br/>
        /// This is the counterpart of <see cref="Write7BitEncodedInt"/> and consumes between one and five bytes.<br/>
        /// </summary>
        /// <returns>The decoded signed 32-bit integer.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The encoded value is truncated before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The encoded value exceeds 32 bits.<br/></exception>
        public int Read7BitEncodedInt()
        {
            uint encoded = Read7BitEncodedUInt32Core();
            return (int)(encoded >> 1) ^ -((int)encoded & 1);
        }

        /// <summary>
        /// Reads a signed 128-bit integer encoded with ZigZag transformation and base-128 continuation bytes.<br/>
        /// This is the counterpart of <see cref="Write7BitEncodedInt128"/> and consumes between one and nineteen bytes.<br/>
        /// </summary>
        /// <returns>The decoded signed 128-bit integer.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The encoded value is truncated before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The encoded value exceeds 128 bits.<br/></exception>
        public Int128 Read7BitEncodedInt128()
        {
            UInt128 encoded = Read7BitEncodedUInt128Core();
            return (Int128)(encoded >> 1) ^ -((Int128)encoded & 1);
        }

        /// <summary>
        /// Reads a signed 64-bit integer encoded with ZigZag transformation and base-128 continuation bytes.<br/>
        /// This is the counterpart of <see cref="Write7BitEncodedLong"/> and consumes between one and ten bytes.<br/>
        /// </summary>
        /// <returns>The decoded signed 64-bit integer.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The encoded value is truncated before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The encoded value exceeds 64 bits.<br/></exception>
        public long Read7BitEncodedLong()
        {
            ulong encoded = Read7BitEncodedUInt64Core();
            return (long)(encoded >> 1) ^ -((long)encoded & 1);
        }

        /// <summary>
        /// Reads an unsigned 32-bit integer from base-128 continuation bytes without ZigZag transformation.<br/>
        /// This is the counterpart of <see cref="Write7BitEncodedUInt"/> and consumes between one and five bytes.<br/>
        /// </summary>
        /// <returns>The decoded unsigned 32-bit integer.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The encoded value is truncated before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The encoded value exceeds 32 bits.<br/></exception>
        public uint Read7BitEncodedUInt() => Read7BitEncodedUInt32Core();

        /// <summary>
        /// Reads an unsigned 128-bit integer from base-128 continuation bytes without ZigZag transformation.<br/>
        /// This is the counterpart of <see cref="Write7BitEncodedUInt128"/> and consumes between one and nineteen bytes.<br/>
        /// </summary>
        /// <returns>The decoded unsigned 128-bit integer.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The encoded value is truncated before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The encoded value exceeds 128 bits.<br/></exception>
        public UInt128 Read7BitEncodedUInt128() => Read7BitEncodedUInt128Core();

        /// <summary>
        /// Reads an unsigned 64-bit integer from base-128 continuation bytes without ZigZag transformation.<br/>
        /// This is the counterpart of <see cref="Write7BitEncodedULong"/> and consumes between one and ten bytes.<br/>
        /// </summary>
        /// <returns>The decoded unsigned 64-bit integer.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The encoded value is truncated before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The encoded value exceeds 64 bits.<br/></exception>
        public ulong Read7BitEncodedULong() => Read7BitEncodedUInt64Core();

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

        /// <summary>
        /// Reads an arbitrary-precision integer from a byte-length-prefixed little-endian two's-complement payload.<br/>
        /// This is the counterpart of <see cref="Write(BigInteger)"/>; malformed or truncated byte framing is rejected by the shared byte-array reader.<br/>
        /// </summary>
        /// <returns>The decoded arbitrary-precision integer.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The framed payload is truncated.<br/></exception>
        /// <exception cref="InvalidDataException">The byte-length prefix is negative or exceeds its encoded integer width.<br/></exception>
        public BigInteger ReadBigInteger() => new BigInteger(ReadBytesWithByteLength());

        /// <summary>
        /// Reads packed bits from a byte-length-prefixed payload followed by its signed seven-bit logical bit count.<br/>
        /// This is the counterpart of <see cref="Write(BitArray)"/>; the payload byte count must equal the minimum number of bytes required by the logical count.<br/>
        /// </summary>
        /// <returns>A newly allocated bit array with the serialized logical length.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The byte payload or logical-count suffix is truncated.<br/></exception>
        /// <exception cref="InvalidDataException">The payload framing or logical bit count is invalid or inconsistent.<br/></exception>
        public BitArray ReadBitArray()
        {
            byte[] bytes = ReadBytesWithByteLength();
            int bitCount = Read7BitEncodedInt();
            if (bitCount < 0 || (bitCount + 7L) / 8L != bytes.Length)
                throw new InvalidDataException("The logical bit count does not match the packed byte payload.");

            return new BitArray(bytes) { Length = bitCount };
        }

        /// <summary>
        /// Gets the byte at a logical index without changing <see cref="Position"/>.<br/>
        /// The index is relative to this stream or segment, not its root owner's backing buffer.
        /// </summary>
        /// <param name="index">The zero-based logical byte index.</param>
        /// <returns>The byte at <paramref name="index"/>.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException"><paramref name="index"/> is outside the current logical length.</exception>
        public byte this[int index] => PeekByte(index);

        /// <summary>
        /// Gets the byte at the current position without advancing <see cref="Position"/>.<br/>
        /// Use <see cref="ReadByte"/> when the cursor should advance after the byte is read.
        /// </summary>
        /// <returns>The byte at the current position.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">The current position is at the end of this stream or segment.</exception>
        public byte PeekByte()
        {
            EnsureNotDisposed();
            if (_position >= EffectiveLength)
                throw new EndOfStreamException();
            return BufferMemory.Span[BaseOffset + _position];
        }

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

        /// <summary>
        /// Gets the byte at a logical index without changing <see cref="Position"/>.<br/>
        /// The index is relative to this stream or segment, not its root owner's backing buffer.
        /// </summary>
        /// <param name="index">The zero-based logical byte index.</param>
        /// <returns>The byte at <paramref name="index"/>.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException"><paramref name="index"/> is outside the current logical length.</exception>
        public byte PeekByte(int index)
        {
            EnsureNotDisposed();
            if (index < 0 || index >= EffectiveLength)
                throw new EndOfStreamException();
            return BufferMemory.Span[BaseOffset + index];
        }

        /// <summary>
        /// Reads up to <paramref name="count"/> unframed bytes from the current position.<br/>
        /// Fewer bytes are returned at the end of the stream, including an empty array when no bytes remain.<br/>
        /// </summary>
        /// <param name="count">The maximum number of bytes to read.<br/></param>
        /// <returns>A newly allocated array containing the bytes that were available.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.<br/></exception>
        public byte[] ReadBytes(int count)
        {
            EnsureNotDisposed();
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            int available = Math.Min(count, EffectiveLength - _position);
            byte[] result = new byte[available];
            GetReadOnlySpan(_position, available).CopyTo(result);
            _position += available;
            return result;
        }

        /// <summary>
        /// Reads the fixed-width <see cref="char"/> value at the current position and advances the cursor by its in-memory size.<br/>
        /// This is the direct counterpart of <see cref="Write(char)"/>.
        /// </summary>
        /// <returns>The decoded character.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete character does not remain in this stream or segment.</exception>
        public char ReadChar() => ReadPrimitive<char>();

        /// <summary>
        /// Reads a <see cref="Complex"/> value from its real and imaginary <see cref="double"/> components.<br/>
        /// This is the counterpart of <see cref="Write(Complex)"/>; a truncated value can advance the cursor after its real component is read.
        /// </summary>
        /// <returns>The decoded complex value.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete complex value does not remain in this stream or segment.</exception>
        public Complex ReadComplex() { return new Complex(ReadPrimitive<double>(), ReadPrimitive<double>()); }

        /// <summary>
        /// Reads the fixed-width <see cref="DateOnly"/> value at the current position and advances the cursor by its in-memory size.<br/>
        /// This is the direct counterpart of <see cref="Write(DateOnly)"/>.
        /// </summary>
        /// <returns>The decoded date.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete date value does not remain in this stream or segment.</exception>
        public DateOnly ReadDateOnly() => ReadPrimitive<DateOnly>();

        /// <summary>
        /// Reads a <see cref="DateTime"/> from the binary value produced by <see cref="DateTime.ToBinary"/>.<br/>
        /// This is the counterpart of <see cref="Write(DateTime)"/> and advances the cursor by eight bytes.
        /// </summary>
        /// <returns>The decoded date and time.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Eight bytes do not remain in this stream or segment.</exception>
        public DateTime ReadDateTime() => DateTime.FromBinary(ReadInt64());

        /// <summary>
        /// Reads a <see cref="decimal"/> from its four serialized <see cref="int"/> components.<br/>
        /// This is the counterpart of <see cref="Write(decimal)"/>; a truncated value can advance the cursor after one or more components are read.
        /// </summary>
        /// <returns>The decoded decimal value.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete decimal value does not remain in this stream or segment.</exception>
        public decimal ReadDecimal()
        {
            Span<int> bits = stackalloc int[4];
            for (int i = 0; i < 4; i++)
                bits[i] = ReadInt32();
            decimal value = new decimal(bits);
            return value;
        }

        /// <summary>
        /// Reads the fixed-width <see cref="double"/> value at the current position and advances the cursor by eight bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(double)"/>.
        /// </summary>
        /// <returns>The decoded double-precision floating-point value.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Eight bytes do not remain in this stream or segment.</exception>
        public double ReadDouble() => ReadPrimitive<double>();

        /// <summary>
        /// Reads the fixed-width <see cref="Guid"/> value at the current position and advances the cursor by sixteen bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(Guid)"/>.
        /// </summary>
        /// <returns>The decoded globally unique identifier.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Sixteen bytes do not remain in this stream or segment.</exception>
        public Guid ReadGuid() => ReadPrimitive<Guid>();

        /// <summary>
        /// Reads the fixed-width <see cref="Int128"/> value at the current position and advances the cursor by sixteen bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(Int128)"/>.
        /// </summary>
        /// <returns>The decoded signed 128-bit integer.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Sixteen bytes do not remain in this stream or segment.</exception>
        public Int128 ReadInt128() => ReadPrimitive<Int128>();

        /// <summary>
        /// Reads the signed byte at the current position and advances the cursor by one byte.<br/>
        /// This is the direct counterpart of <see cref="Write(sbyte)"/>.
        /// </summary>
        /// <returns>The decoded signed byte.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A byte does not remain in this stream or segment.</exception>
        public sbyte ReadSByte() => ReadPrimitive<sbyte>();

        /// <summary>
        /// Reads the fixed-width <see cref="short"/> value at the current position and advances the cursor by two bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(short)"/>.
        /// </summary>
        /// <returns>The decoded signed 16-bit integer.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Two bytes do not remain in this stream or segment.</exception>
        public short ReadInt16() => ReadPrimitive<short>();

        /// <summary>
        /// Reads the fixed-width <see cref="int"/> value at the current position and advances the cursor by four bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(int)"/>.
        /// </summary>
        /// <returns>The decoded signed 32-bit integer.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Four bytes do not remain in this stream or segment.</exception>
        public int ReadInt32() => ReadPrimitive<int>();

        /// <summary>
        /// Reads the fixed-width <see cref="long"/> value at the current position and advances the cursor by eight bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(long)"/>.
        /// </summary>
        /// <returns>The decoded signed 64-bit integer.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Eight bytes do not remain in this stream or segment.</exception>
        public long ReadInt64() => ReadPrimitive<long>();

        /// <summary>
        /// Reads a <see cref="Matrix3x2"/> from six serialized <see cref="float"/> components.<br/>
        /// This is the counterpart of <see cref="Write(Matrix3x2)"/>; a truncated value can advance the cursor after one or more components are read.
        /// </summary>
        /// <returns>The decoded matrix.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete matrix does not remain in this stream or segment.</exception>
        public Matrix3x2 ReadMatrix3x2() { return new Matrix3x2(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        /// <summary>
        /// Reads a <see cref="Matrix4x4"/> from sixteen serialized <see cref="float"/> components.<br/>
        /// This is the counterpart of <see cref="Write(Matrix4x4)"/>; a truncated value can advance the cursor after one or more components are read.
        /// </summary>
        /// <returns>The decoded matrix.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete matrix does not remain in this stream or segment.</exception>
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

        /// <summary>
        /// Reads a <see cref="Plane"/> from a serialized normal vector and distance component.<br/>
        /// This is the counterpart of <see cref="Write(Plane)"/>; a truncated value can advance the cursor after one or more components are read.
        /// </summary>
        /// <returns>The decoded plane.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete plane does not remain in this stream or segment.</exception>
        public Plane ReadPlane() { return new Plane(ReadVector3(), ReadPrimitive<float>()); }

        /// <summary>
        /// Reads a <see cref="Quaternion"/> from four serialized <see cref="float"/> components.<br/>
        /// This is the counterpart of <see cref="Write(Quaternion)"/>; a truncated value can advance the cursor after one or more components are read.
        /// </summary>
        /// <returns>The decoded quaternion.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete quaternion does not remain in this stream or segment.</exception>
        public Quaternion ReadQuaternion() { return new Quaternion(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        /// <summary>
        /// Reads a Unicode scalar value stored as a signed 32-bit integer and constructs a <see cref="Rune"/>.<br/>
        /// This is the counterpart of <see cref="Write(Rune)"/>.
        /// </summary>
        /// <returns>The decoded Unicode scalar value.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Four bytes do not remain in this stream or segment.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The stored value is not a valid Unicode scalar value.</exception>
        public Rune ReadRune() => new Rune(ReadInt32());

        /// <summary>
        /// Reads the fixed-width <see cref="float"/> value at the current position and advances the cursor by four bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(float)"/>.
        /// </summary>
        /// <returns>The decoded single-precision floating-point value.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Four bytes do not remain in this stream or segment.</exception>
        public float ReadSingle() => ReadPrimitive<float>();

        /// <summary>
        /// Reads a nullable string whose encoded byte count is stored as a signed ZigZag seven-bit prefix.<br/>
        /// A prefix of minus one represents <see langword="null"/>; zero represents an empty string.<br/>
        /// </summary>
        /// <returns>The decoded string, or <see langword="null"/> for the dedicated null marker.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The length prefix or encoded string payload is truncated.<br/></exception>
        /// <exception cref="InvalidDataException">The decoded length is less than minus one or exceeds its encoded integer width.<br/></exception>
        /// <exception cref="DecoderFallbackException">The configured encoding rejects malformed input bytes.<br/></exception>
        public string? ReadString()
        {
            int length = Read7BitEncodedInt();
            if (length == -1)
                return null;
            if (length < -1)
                throw new InvalidDataException("A string length must be non-negative or the dedicated null marker.");
            if (length > EffectiveLength - _position)
                throw new EndOfStreamException();

            var span = GetReadOnlySpan(_position, length);
            string value = StringEncoding.GetString(span);
            _position += length;
            return value;
        }

        /// <summary>
        /// Reads a <see cref="TimeOnly"/> from its serialized tick count.<br/>
        /// This is the counterpart of <see cref="Write(TimeOnly)"/> and advances the cursor by eight bytes.
        /// </summary>
        /// <returns>The decoded time of day.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Eight bytes do not remain in this stream or segment.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The stored tick count is outside the range accepted by <see cref="TimeOnly"/>.</exception>
        public TimeOnly ReadTimeOnly()
        {
            long ticks = ReadInt64();
            return new TimeOnly(ticks);
        }

        /// <summary>
        /// Reads a <see cref="TimeSpan"/> from its serialized tick count.<br/>
        /// This is the counterpart of <see cref="Write(TimeSpan)"/> and advances the cursor by eight bytes.
        /// </summary>
        /// <returns>The decoded time interval.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Eight bytes do not remain in this stream or segment.</exception>
        public TimeSpan ReadTimeSpan()
        {
            long ticks = ReadInt64();
            return new TimeSpan(ticks);
        }

        /// <summary>
        /// Reads a <see cref="DateTimeOffset"/> from a serialized <see cref="DateTime"/> followed by its offset <see cref="TimeSpan"/>.<br/>
        /// This is the counterpart of <see cref="Write(DateTimeOffset)"/>; a truncated value can advance the cursor after its date and time are read.
        /// </summary>
        /// <returns>The decoded date and time with offset.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete date and offset do not remain in this stream or segment.</exception>
        /// <exception cref="ArgumentException">The decoded offset is invalid for the decoded date and time.</exception>
        public DateTimeOffset ReadDateTimeOffset() => new DateTimeOffset(ReadDateTime(), ReadTimeSpan());

        /// <summary>
        /// Reads the fixed-width <see cref="UInt128"/> value at the current position and advances the cursor by sixteen bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(UInt128)"/>.
        /// </summary>
        /// <returns>The decoded unsigned 128-bit integer.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Sixteen bytes do not remain in this stream or segment.</exception>
        public UInt128 ReadUInt128() => ReadPrimitive<UInt128>();

        /// <summary>
        /// Reads the fixed-width <see cref="ushort"/> value at the current position and advances the cursor by two bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(ushort)"/>.
        /// </summary>
        /// <returns>The decoded unsigned 16-bit integer.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Two bytes do not remain in this stream or segment.</exception>
        public ushort ReadUInt16() => ReadPrimitive<ushort>();

        /// <summary>
        /// Reads the fixed-width <see cref="uint"/> value at the current position and advances the cursor by four bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(uint)"/>.
        /// </summary>
        /// <returns>The decoded unsigned 32-bit integer.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Four bytes do not remain in this stream or segment.</exception>
        public uint ReadUInt32() => ReadPrimitive<uint>();

        /// <summary>
        /// Reads the fixed-width <see cref="ulong"/> value at the current position and advances the cursor by eight bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(ulong)"/>.
        /// </summary>
        /// <returns>The decoded unsigned 64-bit integer.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">Eight bytes do not remain in this stream or segment.</exception>
        public ulong ReadUInt64() => ReadPrimitive<ulong>();

        /// <summary>
        /// Reads a <see cref="Vector2"/> from two serialized <see cref="float"/> components.<br/>
        /// This is the counterpart of <see cref="Write(Vector2)"/>; a truncated value can advance the cursor after one component is read.
        /// </summary>
        /// <returns>The decoded vector.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete vector does not remain in this stream or segment.</exception>
        public Vector2 ReadVector2() { return new Vector2(ReadPrimitive<float>(), ReadPrimitive<float>()); }

        /// <summary>
        /// Reads a <see cref="Vector3"/> from three serialized <see cref="float"/> components.<br/>
        /// This is the counterpart of <see cref="Write(Vector3)"/>; a truncated value can advance the cursor after one or more components are read.
        /// </summary>
        /// <returns>The decoded vector.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete vector does not remain in this stream or segment.</exception>
        public Vector3 ReadVector3() { return new Vector3(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        /// <summary>
        /// Reads a <see cref="Vector4"/> from four serialized <see cref="float"/> components.<br/>
        /// This is the counterpart of <see cref="Write(Vector4)"/>; a truncated value can advance the cursor after one or more components are read.
        /// </summary>
        /// <returns>The decoded vector.</returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.</exception>
        /// <exception cref="EndOfStreamException">A complete vector does not remain in this stream or segment.</exception>
        public Vector4 ReadVector4() { return new Vector4(ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>(), ReadPrimitive<float>()); }

        /// <summary>
        /// Reads the fixed-width <see cref="bool"/> value at the current position and advances the cursor by one byte.<br/>
        /// This is the direct counterpart of <see cref="Write(bool)"/>.<br/>
        /// </summary>
        /// <returns>The decoded Boolean value.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">One byte does not remain in this stream or segment.<br/></exception>
        public bool ReadBoolean() => ReadPrimitive<bool>();

        /// <summary>
        /// Reads the fixed-width <see cref="Half"/> value at the current position and advances the cursor by two bytes.<br/>
        /// This is the direct counterpart of <see cref="Write(Half)"/>.<br/>
        /// </summary>
        /// <returns>The decoded half-precision floating-point value.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">Two bytes do not remain in this stream or segment.<br/></exception>
        public Half ReadHalf() => ReadPrimitive<Half>();

        /// <summary>
        /// Reads a sequence of <see cref="BigInteger"/> values prefixed by its element count.<br/>
        /// Each element retains its own byte-length-prefixed representation.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public BigInteger[] ReadBigIntegersWithCount()
        {
            int count = Read7BitEncodedInt();
            if (count < 0)
                throw new InvalidDataException("A count-prefixed array cannot have a negative element count.");
            if (count > EffectiveLength - _position)
                throw new EndOfStreamException();

            BigInteger[] result = new BigInteger[count];
            for (int i = 0; i < count; i++)
                result[i] = ReadBigInteger();
            return result;
        }

        /// <summary>
        /// Reads raw <see cref="bool"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteBooleansWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public bool[] ReadBooleansWithByteLength() => ReadArrayWithByteLength<bool>();

        /// <summary>
        /// Reads bytes prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteBytesWithByteLength(byte[])"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded bytes.<br/></returns>
        public byte[] ReadBytesWithByteLength() => ReadArrayWithByteLength<byte>();

        /// <summary>
        /// Reads raw <see cref="char"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteCharsWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public char[] ReadCharsWithByteLength() => ReadArrayWithByteLength<char>();

        /// <summary>
        /// Reads raw <see cref="Complex"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteComplexesWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Complex[] ReadComplexesWithByteLength() => ReadArrayWithByteLength<Complex>();

        /// <summary>
        /// Reads raw <see cref="DateOnly"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteDateOnlysWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public DateOnly[] ReadDateOnlysWithByteLength() => ReadArrayWithByteLength<DateOnly>();

        /// <summary>
        /// Reads raw <see cref="DateTimeOffset"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteDateTimeOffsetsWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public DateTimeOffset[] ReadDateTimeOffsetsWithByteLength() => ReadArrayWithByteLength<DateTimeOffset>();

        /// <summary>
        /// Reads raw <see cref="DateTime"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteDateTimesWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public DateTime[] ReadDateTimesWithByteLength() => ReadArrayWithByteLength<DateTime>();

        /// <summary>
        /// Reads <see cref="decimal"/> values prefixed by their element count.<br/>
        /// Each element uses the same four-integer representation as <see cref="ReadDecimal"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public decimal[] ReadDecimalsWithCount()
        {
            int count = Read7BitEncodedInt();
            if (count < 0)
                throw new InvalidDataException("A count-prefixed array cannot have a negative element count.");
            if (count > (EffectiveLength - _position) / 16)
                throw new EndOfStreamException();

            decimal[] result = new decimal[count];
            for (int i = 0; i < count; i++)
                result[i] = ReadDecimal();
            return result;
        }

        /// <summary>
        /// Reads raw <see cref="double"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteDoublesWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public double[] ReadDoublesWithByteLength() => ReadArrayWithByteLength<double>();

        /// <summary>
        /// Reads raw <see cref="Guid"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteGuidsWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Guid[] ReadGuidsWithByteLength() => ReadArrayWithByteLength<Guid>();

        /// <summary>
        /// Reads raw <see cref="Half"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteHalfsWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Half[] ReadHalfsWithByteLength() => ReadArrayWithByteLength<Half>();

        /// <summary>
        /// Reads raw <see cref="Int128"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteInt128sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Int128[] ReadInt128sWithByteLength() => ReadArrayWithByteLength<Int128>();

        /// <summary>
        /// Reads raw <see cref="short"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteInt16sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public short[] ReadInt16sWithByteLength() => ReadArrayWithByteLength<short>();

        /// <summary>
        /// Reads raw <see cref="int"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteInt32sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public int[] ReadInt32sWithByteLength() => ReadArrayWithByteLength<int>();

        /// <summary>
        /// Reads raw <see cref="long"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteInt64sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public long[] ReadInt64sWithByteLength() => ReadArrayWithByteLength<long>();

        /// <summary>
        /// Reads raw <see cref="Matrix3x2"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteMatrix3x2sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Matrix3x2[] ReadMatrix3x2sWithByteLength() => ReadArrayWithByteLength<Matrix3x2>();

        /// <summary>
        /// Reads raw <see cref="Matrix4x4"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteMatrix4x4sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Matrix4x4[] ReadMatrix4x4sWithByteLength() => ReadArrayWithByteLength<Matrix4x4>();

        /// <summary>
        /// Reads raw <see cref="Plane"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WritePlanesWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Plane[] ReadPlanesWithByteLength() => ReadArrayWithByteLength<Plane>();

        /// <summary>
        /// Reads raw <see cref="Quaternion"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteQuaternionsWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Quaternion[] ReadQuaternionsWithByteLength() => ReadArrayWithByteLength<Quaternion>();

        /// <summary>
        /// Reads raw <see cref="sbyte"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteSBytesWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public sbyte[] ReadSBytesWithByteLength() => ReadArrayWithByteLength<sbyte>();

        /// <summary>
        /// Reads raw <see cref="float"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteSinglesWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public float[] ReadSinglesWithByteLength() => ReadArrayWithByteLength<float>();

        /// <summary>
        /// Reads strings prefixed by their element count.<br/>
        /// Each string retains its own nullable byte-length-prefixed representation.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded nullable strings.<br/></returns>
        public string?[] ReadStringsWithCount()
        {
            int count = Read7BitEncodedInt();
            if (count < 0)
                throw new InvalidDataException("A count-prefixed array cannot have a negative element count.");
            if (count > EffectiveLength - _position)
                throw new EndOfStreamException();

            string?[] result = new string?[count];
            for (int i = 0; i < count; i++)
                result[i] = ReadString();
            return result;
        }

        /// <summary>
        /// Reads raw <see cref="TimeOnly"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteTimeOnlysWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public TimeOnly[] ReadTimeOnlysWithByteLength() => ReadArrayWithByteLength<TimeOnly>();

        /// <summary>
        /// Reads raw <see cref="TimeSpan"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteTimeSpansWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public TimeSpan[] ReadTimeSpansWithByteLength() => ReadArrayWithByteLength<TimeSpan>();

        /// <summary>
        /// Reads raw <see cref="UInt128"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteUInt128sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public UInt128[] ReadUInt128sWithByteLength() => ReadArrayWithByteLength<UInt128>();

        /// <summary>
        /// Reads raw <see cref="ushort"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteUInt16sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public ushort[] ReadUInt16sWithByteLength() => ReadArrayWithByteLength<ushort>();

        /// <summary>
        /// Reads raw <see cref="uint"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteUInt32sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public uint[] ReadUInt32sWithByteLength() => ReadArrayWithByteLength<uint>();

        /// <summary>
        /// Reads raw <see cref="ulong"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteUInt64sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public ulong[] ReadUInt64sWithByteLength() => ReadArrayWithByteLength<ulong>();

        /// <summary>
        /// Reads raw <see cref="Vector2"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteVector2sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Vector2[] ReadVector2sWithByteLength() => ReadArrayWithByteLength<Vector2>();

        /// <summary>
        /// Reads raw <see cref="Vector3"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteVector3sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Vector3[] ReadVector3sWithByteLength() => ReadArrayWithByteLength<Vector3>();

        /// <summary>
        /// Reads raw <see cref="Vector4"/> values prefixed by their total byte length.<br/>
        /// This is the symmetric counterpart of <see cref="WriteVector4sWithByteLength"/>.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded values.<br/></returns>
        public Vector4[] ReadVector4sWithByteLength() => ReadArrayWithByteLength<Vector4>();

        /// <summary>
        /// Reads <see cref="Version"/> values prefixed by their element count.<br/>
        /// Two-, three-, and four-component versions retain their original component arity.<br/>
        /// </summary>
        /// <returns>A newly allocated array containing the decoded versions.<br/></returns>
        public Version[] ReadVersionsWithCount()
        {
            int count = Read7BitEncodedInt();
            if (count < 0)
                throw new InvalidDataException("A count-prefixed array cannot have a negative element count.");
            if (count > (EffectiveLength - _position) / 4)
                throw new EndOfStreamException();

            Version[] versions = new Version[count];
            for (int i = 0; i < count; i++)
            {
                int major = Read7BitEncodedInt();
                int minor = Read7BitEncodedInt();
                int build = Read7BitEncodedInt();
                int revision = Read7BitEncodedInt();
                versions[i] = revision >= 0
                    ? new Version(major, minor, build, revision)
                    : build >= 0
                        ? new Version(major, minor, build)
                        : new Version(major, minor);
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

        /// <summary>
        /// Creates a dynamic, pooled view of this stream beginning at its current position without copying bytes.<br/>
        /// The returned view shares the root buffer and should be disposed when finished so its wrapper can be reused.
        /// </summary>
        /// <returns>A dynamic view whose position is zero at this stream's current position.</returns>
        public BufferStream CloneShallow() => Segment();

        /// <summary>
        /// Creates a dynamic, pooled view of this stream beginning at its current position without copying bytes.<br/>
        /// The view shares the root buffer and grows with the owning stream's written length after its base offset.
        /// </summary>
        /// <returns>A dynamic view whose position is zero at this stream's current position.</returns>
        public BufferStream Segment() => RentSegment(_position, 0, fixedLength: false);

        /// <summary>
        /// Creates a dynamic, pooled view of this stream beginning at <paramref name="offset"/> without copying bytes.<br/>
        /// The view shares the root buffer and grows with the owning stream's written length after its base offset.
        /// </summary>
        /// <param name="offset">The zero-based offset into this stream's written data.</param>
        /// <returns>A dynamic view whose position is zero at <paramref name="offset"/>.</returns>
        public BufferStream Segment(int offset) => RentSegment(offset, 0, fixedLength: false);

        /// <summary>
        /// Returns a fixed-length segment of the buffer starting at <paramref name="offset"/>.<br/>
        /// Index zero of the returned stream corresponds to the specified offset.<br/>
        /// </summary>
        /// <param name="offset">The zero-based offset into the written data.<br/></param>
        /// <param name="length">The fixed length of the segment.<br/></param>
        public BufferStream Segment(int offset, int length) => RentSegment(offset, length, fixedLength: true);

        /// <summary>
        /// Creates a dynamic, pooled view of this stream beginning at <paramref name="offset"/> without copying bytes.<br/>
        /// The view shares the root buffer and grows with the owning stream's written length after its base offset.
        /// </summary>
        /// <param name="offset">The zero-based offset into this stream's written data.</param>
        /// <returns>A dynamic view whose position is zero at <paramref name="offset"/>.</returns>
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

        /// <summary>
        /// Copies this stream's written contents into a new byte array.<br/>
        /// The returned array is independent of the stream's backing buffer and is not rented from the shared pool.
        /// </summary>
        /// <returns>A new array containing this stream's written contents from offset zero through <see cref="Length"/>.</returns>
        public byte[] ToArray()
        {
            EnsureNotDisposed();
            return GetReadOnlySpan(0, EffectiveLength).ToArray();
        }

        /// <summary>
        /// Writes a fixed-width Boolean value at the current position and advances the cursor by one byte.<br/>
        /// Read the value with <see cref="ReadBoolean"/>.<br/>
        /// </summary>
        /// <param name="value">The Boolean value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(bool value) => WritePrimitive(value);

        /// <summary>
        /// Writes one byte at the current position and advances the cursor by one byte.<br/>
        /// Read the value with <see cref="ReadByte"/>.<br/>
        /// </summary>
        /// <param name="value">The byte to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(byte value) => WritePrimitive(value);

        /// <summary>
        /// Writes a signed byte at the current position and advances the cursor by one byte.<br/>
        /// Read the value with <see cref="ReadSByte"/>.<br/>
        /// </summary>
        /// <param name="value">The signed byte to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(sbyte value) => WritePrimitive(value);

        /// <summary>
        /// Writes a fixed-width signed 16-bit integer at the current position.<br/>
        /// Read the value with <see cref="ReadInt16"/>.<br/>
        /// </summary>
        /// <param name="value">The signed 16-bit integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(short value) => WritePrimitive(value);

        /// <summary>
        /// Writes a fixed-width unsigned 16-bit integer at the current position.<br/>
        /// Read the value with <see cref="ReadUInt16"/>.<br/>
        /// </summary>
        /// <param name="value">The unsigned 16-bit integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(ushort value) => WritePrimitive(value);

        /// <summary>
        /// Writes a fixed-width signed 32-bit integer at the current position.<br/>
        /// Read the value with <see cref="ReadInt32"/>.<br/>
        /// </summary>
        /// <param name="value">The signed 32-bit integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(int value) => WritePrimitive(value);

        /// <summary>
        /// Writes a fixed-width unsigned 32-bit integer at the current position.<br/>
        /// Read the value with <see cref="ReadUInt32"/>.<br/>
        /// </summary>
        /// <param name="value">The unsigned 32-bit integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(uint value) => WritePrimitive(value);

        /// <summary>
        /// Writes a fixed-width signed 64-bit integer at the current position.<br/>
        /// Read the value with <see cref="ReadInt64"/>.<br/>
        /// </summary>
        /// <param name="value">The signed 64-bit integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(long value) => WritePrimitive(value);

        /// <summary>
        /// Writes a fixed-width unsigned 64-bit integer at the current position.<br/>
        /// Read the value with <see cref="ReadUInt64"/>.<br/>
        /// </summary>
        /// <param name="value">The unsigned 64-bit integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(ulong value) => WritePrimitive(value);

        /// <summary>
        /// Writes a signed 128-bit integer as its low 64-bit half followed by its high 64-bit half.<br/>
        /// Read the value with <see cref="ReadInt128"/>; a bounded compound write can advance after its first half before a later failure.<br/>
        /// </summary>
        /// <param name="value">The signed 128-bit integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Int128 value)
        {
            Write((ulong)(value & ulong.MaxValue));
            Write((ulong)(value >> 64));
        }

        /// <summary>
        /// Writes an unsigned 128-bit integer as its low 64-bit half followed by its high 64-bit half.<br/>
        /// Read the value with <see cref="ReadUInt128"/>; a bounded compound write can advance after its first half before a later failure.<br/>
        /// </summary>
        /// <param name="value">The unsigned 128-bit integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(UInt128 value)
        {
            Write((ulong)(value & ulong.MaxValue));
            Write((ulong)(value >> 64));
        }

        /// <summary>
        /// Writes a fixed-width half-precision floating-point value at the current position.<br/>
        /// Read the value with <see cref="ReadHalf"/>.<br/>
        /// </summary>
        /// <param name="value">The half-precision value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Half value) => WritePrimitive(value);

        /// <summary>
        /// Writes a fixed-width single-precision floating-point value at the current position.<br/>
        /// Read the value with <see cref="ReadSingle"/>.<br/>
        /// </summary>
        /// <param name="value">The single-precision value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(float value) => WritePrimitive(value);

        /// <summary>
        /// Writes a fixed-width double-precision floating-point value at the current position.<br/>
        /// Read the value with <see cref="ReadDouble"/>.<br/>
        /// </summary>
        /// <param name="value">The double-precision value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(double value) => WritePrimitive(value);

        /// <summary>
        /// Writes a decimal value as the four signed 32-bit components returned by <see cref="decimal.GetBits(decimal)"/>.<br/>
        /// Read the value with <see cref="ReadDecimal"/>; a bounded compound write can advance after one or more components before a later failure.<br/>
        /// </summary>
        /// <param name="value">The decimal value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(decimal value)
        {
            int[] bits = decimal.GetBits((decimal)value);
            foreach (var i in bits)
                WritePrimitive(i); // or your WriteInt32 routine

        }

        /// <summary>
        /// Writes a fixed-width UTF-16 character at the current position.<br/>
        /// Read the value with <see cref="ReadChar"/>.<br/>
        /// </summary>
        /// <param name="value">The UTF-16 character to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(char value) => WritePrimitive(value);

        /// <summary>
        /// Writes a Unicode scalar value as its signed 32-bit integer representation.<br/>
        /// Read the value with <see cref="ReadRune"/>.<br/>
        /// </summary>
        /// <param name="value">The Unicode scalar value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Rune value) => WritePrimitive((int)value.Value);

        /// <summary>
        /// Writes the signed 64-bit binary representation produced by <see cref="DateTime.ToBinary"/>.<br/>
        /// Read the value with <see cref="ReadDateTime"/>.<br/>
        /// </summary>
        /// <param name="value">The date and time to write, including its <see cref="DateTime.Kind"/> information.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(DateTime value) => WritePrimitive(value.ToBinary());

        /// <summary>
        /// Writes a date-time offset as the local clock value's binary representation followed by the offset tick count.<br/>
        /// Read the value with <see cref="ReadDateTimeOffset"/>; a bounded compound write can advance after the date-time component before a later failure.<br/>
        /// </summary>
        /// <param name="value">The date, time, and UTC offset to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(DateTimeOffset value) { WritePrimitive(value.DateTime.ToBinary()); WritePrimitive(value.Offset.Ticks); }

        /// <summary>
        /// Writes a fixed-width globally unique identifier at the current position.<br/>
        /// Read the value with <see cref="ReadGuid"/>.<br/>
        /// </summary>
        /// <param name="value">The globally unique identifier to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Guid value) => WritePrimitive(value);

        /// <summary>
        /// Writes a date as its signed 32-bit day number.<br/>
        /// Read the value with <see cref="ReadDateOnly"/>.<br/>
        /// </summary>
        /// <param name="value">The date to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(DateOnly value)
        {
            Write(value.DayNumber);  // Int32
        }

        /// <summary>
        /// Writes a time interval as its signed 64-bit tick count.<br/>
        /// Read the value with <see cref="ReadTimeSpan"/>.<br/>
        /// </summary>
        /// <param name="value">The time interval to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(TimeSpan value) => WritePrimitive(value);

        /// <summary>
        /// Writes a time of day as its signed 64-bit tick count.<br/>
        /// Read the value with <see cref="ReadTimeOnly"/>.<br/>
        /// </summary>
        /// <param name="value">The time of day to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(TimeOnly value)
        {
            Write(value.Ticks);  // Int64
        }

        /// <summary>
        /// Writes a two-dimensional vector as its X component followed by its Y component.<br/>
        /// Read the value with <see cref="ReadVector2"/>; a bounded compound write can advance after its first component before a later failure.<br/>
        /// </summary>
        /// <param name="value">The two-dimensional vector to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Vector2 value) { WritePrimitive(value.X); WritePrimitive(value.Y); }

        /// <summary>
        /// Writes a three-dimensional vector in X, Y, Z component order.<br/>
        /// Read the value with <see cref="ReadVector3"/>; a bounded compound write can advance after one or more components before a later failure.<br/>
        /// </summary>
        /// <param name="value">The three-dimensional vector to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Vector3 value) { WritePrimitive(value.X); WritePrimitive(value.Y); WritePrimitive(value.Z); }

        /// <summary>
        /// Writes a four-dimensional vector in X, Y, Z, W component order.<br/>
        /// Read the value with <see cref="ReadVector4"/>; a bounded compound write can advance after one or more components before a later failure.<br/>
        /// </summary>
        /// <param name="value">The four-dimensional vector to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Vector4 value) { WritePrimitive(value.X); WritePrimitive(value.Y); WritePrimitive(value.Z); WritePrimitive(value.W); }

        /// <summary>
        /// Writes a complex number as its real component followed by its imaginary component.<br/>
        /// Read the value with <see cref="ReadComplex"/>; a bounded compound write can advance after its real component before a later failure.<br/>
        /// </summary>
        /// <param name="value">The complex number to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Complex value) { WritePrimitive(value.Real); WritePrimitive(value.Imaginary); }

        /// <summary>
        /// Writes a quaternion in X, Y, Z, W component order.<br/>
        /// Read the value with <see cref="ReadQuaternion"/>; a bounded compound write can advance after one or more components before a later failure.<br/>
        /// </summary>
        /// <param name="value">The quaternion to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Quaternion value) { WritePrimitive(value.X); WritePrimitive(value.Y); WritePrimitive(value.Z); WritePrimitive(value.W); }

        /// <summary>
        /// Writes a plane as its normal vector followed by its distance component.<br/>
        /// Read the value with <see cref="ReadPlane"/>; a bounded compound write can advance after one or more components before a later failure.<br/>
        /// </summary>
        /// <param name="value">The plane to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Plane value) { Write(value.Normal); WritePrimitive(value.D); }

        /// <summary>
        /// Writes a 3x2 matrix in M11, M12, M21, M22, M31, M32 component order.<br/>
        /// Read the value with <see cref="ReadMatrix3x2"/>; a bounded compound write can advance after one or more components before a later failure.<br/>
        /// </summary>
        /// <param name="value">The 3x2 matrix to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Matrix3x2 value) { WritePrimitive(value.M11); WritePrimitive(value.M12); WritePrimitive(value.M21); WritePrimitive(value.M22); WritePrimitive(value.M31); WritePrimitive(value.M32); }

        /// <summary>
        /// Writes a 4x4 matrix in row-major component order from M11 through M44.<br/>
        /// Read the value with <see cref="ReadMatrix4x4"/>; a bounded compound write can advance after one or more components before a later failure.<br/>
        /// </summary>
        /// <param name="m4x4">The 4x4 matrix to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Matrix4x4 m4x4) { WritePrimitive(m4x4.M11); WritePrimitive(m4x4.M12); WritePrimitive(m4x4.M13); WritePrimitive(m4x4.M14); WritePrimitive(m4x4.M21); WritePrimitive(m4x4.M22); WritePrimitive(m4x4.M23); WritePrimitive(m4x4.M24); WritePrimitive(m4x4.M31); WritePrimitive(m4x4.M32); WritePrimitive(m4x4.M33); WritePrimitive(m4x4.M34); WritePrimitive(m4x4.M41); WritePrimitive(m4x4.M42); WritePrimitive(m4x4.M43); WritePrimitive(m4x4.M44); }

        /// <summary>
        /// Writes an arbitrary-precision integer as a byte-length-prefixed little-endian two's-complement payload.<br/>
        /// Read the value with <see cref="ReadBigInteger"/>.<br/>
        /// </summary>
        /// <param name="value">The arbitrary-precision integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The framed write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(BigInteger value)
        {
            var bytes = value.ToByteArray();
            WriteBytesWithByteLength(bytes);
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

        /// <summary>
        /// Writes the memory contents as a byte-length-prefixed payload.<br/>
        /// Read the payload with <see cref="ReadBytesWithByteLength"/> and wrap the returned array in <see cref="Memory{T}"/> when a memory view is required.<br/>
        /// </summary>
        /// <param name="value">The byte memory whose current contents are copied into the stream.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The framed write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(Memory<byte> value)
        {
            int byteCount = value.Length;
            int lengthSize = Get7BitEncodedIntSize(byteCount);
            EnsureCapacity(_position + byteCount + lengthSize);

            Write7BitEncodedInt(byteCount);
            value.Span.CopyTo(GetWritableSpan(_position, byteCount));
            _position += byteCount;
            UpdateLengthAfterWrite(_position);
        }

        /// <summary>
        /// Writes a bit array as a byte-length-prefixed packed payload followed by its logical bit count.<br/>
        /// Read the value with <see cref="ReadBitArray"/>; the logical count preserves unused trailing bits in the final packed byte.<br/>
        /// </summary>
        /// <param name="value">The bit array to pack and write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.<br/></exception>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The framed write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write(BitArray value)
        {
            ArgumentNullException.ThrowIfNull(value);
            int count = (value.Length + 7) / 8;
            byte[] bytes = new byte[count];
            value.CopyTo(bytes, 0);
            WriteBytesWithByteLength(bytes);
            Write7BitEncodedInt(value.Length);
        }

        /// <summary>
        /// Writes an unframed range from a byte array using the standard <see cref="Stream.Write(byte[], int, int)"/> contract.<br/>
        /// The method advances by the number of copied bytes and does not emit a length prefix.<br/>
        /// </summary>
        /// <param name="source">The byte array containing the range to write.<br/></param>
        /// <param name="offset">The zero-based source offset at which copying begins.<br/></param>
        /// <param name="count">The number of bytes to copy.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The source range is invalid, or the write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        public override void Write(byte[] source, int offset, int count)
        {
            WriteBytes(source, offset, count);
        }

        /// <summary>
        /// Writes a signed 32-bit integer using ZigZag transformation followed by base-128 continuation bytes.<br/>
        /// Read the value with <see cref="Read7BitEncodedInt"/>; the complete one-to-five-byte encoding is validated before the stream is modified.<br/>
        /// </summary>
        /// <param name="value">The signed 32-bit integer to encode.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write7BitEncodedInt(int value) =>
            Write7BitEncodedUInt32Core((uint)((value << 1) ^ (value >> 31)));

        /// <summary>
        /// Writes a signed 128-bit integer using ZigZag transformation followed by base-128 continuation bytes.<br/>
        /// Read the value with <see cref="Read7BitEncodedInt128"/>; the complete one-to-nineteen-byte encoding is validated before the stream is modified.<br/>
        /// </summary>
        /// <param name="value">The signed 128-bit integer to encode.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write7BitEncodedInt128(Int128 value) =>
            Write7BitEncodedUInt128Core((UInt128)((value << 1) ^ (value >> 127)));

        /// <summary>
        /// Writes a signed 64-bit integer using ZigZag transformation followed by base-128 continuation bytes.<br/>
        /// Read the value with <see cref="Read7BitEncodedLong"/>; the complete one-to-ten-byte encoding is validated before the stream is modified.<br/>
        /// </summary>
        /// <param name="value">The signed 64-bit integer to encode.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write7BitEncodedLong(long value) =>
            Write7BitEncodedUInt64Core((ulong)((value << 1) ^ (value >> 63)));

        /// <summary>
        /// Writes an unsigned 32-bit integer as base-128 continuation bytes without ZigZag transformation.<br/>
        /// Read the value with <see cref="Read7BitEncodedUInt"/>; the complete one-to-five-byte encoding is validated before the stream is modified.<br/>
        /// </summary>
        /// <param name="value">The unsigned 32-bit integer to encode.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write7BitEncodedUInt(uint value) => Write7BitEncodedUInt32Core(value);

        /// <summary>
        /// Writes an unsigned 128-bit integer as base-128 continuation bytes without ZigZag transformation.<br/>
        /// Read the value with <see cref="Read7BitEncodedUInt128"/>; the complete one-to-nineteen-byte encoding is validated before the stream is modified.<br/>
        /// </summary>
        /// <param name="value">The unsigned 128-bit integer to encode.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write7BitEncodedUInt128(UInt128 value) => Write7BitEncodedUInt128Core(value);

        /// <summary>
        /// Writes an unsigned 64-bit integer as base-128 continuation bytes without ZigZag transformation.<br/>
        /// Read the value with <see cref="Read7BitEncodedULong"/>; the complete one-to-ten-byte encoding is validated before the stream is modified.<br/>
        /// </summary>
        /// <param name="value">The unsigned 64-bit integer to encode.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void Write7BitEncodedULong(ulong value) => Write7BitEncodedUInt64Core(value);

        /// <summary>
        /// Overwrites existing written bytes beginning at <paramref name="destinationOffset"/>.<br/>
        /// The source may overlap the destination, and the operation does not change <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="source">The bytes to copy.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The destination range does not lie entirely within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, ReadOnlySpan<byte> source) =>
            source.CopyTo(GetWritableOffsetSpan(destinationOffset, source.Length));

        /// <summary>
        /// Overwrites one existing byte without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(byte)"/> and can be read with <see cref="ReadByte()"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The byte value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">One byte does not fit entirely within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, byte value) => WriteAtOffset<byte>(destinationOffset, value);

        /// <summary>
        /// Overwrites one existing signed byte without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(sbyte)"/> and can be read with <see cref="ReadSByte"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The signed byte value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">One byte does not fit entirely within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, sbyte value) => WriteAtOffset<sbyte>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing 16-bit signed integer without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(short)"/> and can be read with <see cref="ReadInt16"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The signed integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, short value) => WriteAtOffset<short>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing 16-bit unsigned integer without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(ushort)"/> and can be read with <see cref="ReadUInt16"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The unsigned integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, ushort value) => WriteAtOffset<ushort>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing 32-bit signed integer without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(int)"/> and can be read with <see cref="ReadInt32"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The signed integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, int value) => WriteAtOffset<int>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing 32-bit unsigned integer without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(uint)"/> and can be read with <see cref="ReadUInt32"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The unsigned integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, uint value) => WriteAtOffset<uint>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing 64-bit signed integer without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(long)"/> and can be read with <see cref="ReadInt64"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The signed integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, long value) => WriteAtOffset<long>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing 64-bit unsigned integer without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(ulong)"/> and can be read with <see cref="ReadUInt64"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The unsigned integer to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, ulong value) => WriteAtOffset<ulong>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing single-precision value without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(float)"/> and can be read with <see cref="ReadSingle"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The single-precision value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, float value) => WriteAtOffset<float>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing double-precision value without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(double)"/> and can be read with <see cref="ReadDouble"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The double-precision value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, double value) => WriteAtOffset<double>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing decimal using the four-integer representation produced by <see cref="decimal.GetBits(decimal)"/>.<br/>
        /// The representation is identical to <see cref="Write(decimal)"/> and the operation does not change <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The decimal value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete 16-byte value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, decimal value)
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            MemoryMarshal.AsBytes(bits).CopyTo(GetWritableOffsetSpan(destinationOffset, 16));
        }

        /// <summary>
        /// Overwrites an existing one-byte Boolean without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(bool)"/> and can be read with <see cref="ReadBoolean"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The Boolean value to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">One byte does not fit entirely within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, bool value) => WriteAtOffset<bool>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing fixed-width UTF-16 character without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(char)"/> and can be read with <see cref="ReadChar"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The character to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete character does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, char value) => WriteAtOffset<char>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing fixed-width globally unique identifier without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// The representation is identical to <see cref="Write(Guid)"/> and can be read with <see cref="ReadGuid"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The globally unique identifier to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete identifier does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, Guid value) => WriteAtOffset<Guid>(destinationOffset, value);

        /// <summary>
        /// Overwrites an existing date and time using the signed 64-bit value produced by <see cref="DateTime.ToBinary"/>.<br/>
        /// The representation is identical to <see cref="Write(DateTime)"/> and the operation does not change <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The date and time to write, including its <see cref="DateTime.Kind"/> information.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete eight-byte value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, DateTime value) => WriteAtOffset(destinationOffset, value.ToBinary());

        /// <summary>
        /// Overwrites an existing time interval using its signed 64-bit tick count.<br/>
        /// The representation is identical to <see cref="Write(TimeSpan)"/> and the operation does not change <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="value">The time interval to write.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete eight-byte value does not fit within the written data.<br/></exception>
        public void WriteAtOffset(int destinationOffset, TimeSpan value) => WriteAtOffset(destinationOffset, value.Ticks);

        /// <summary>
        /// Overwrites a reserved region with the same signed-length-prefixed text representation as <see cref="Write(string?)"/>.<br/>
        /// The complete representation must fit within <paramref name="reservedByteCount"/>; unused reserved bytes remain unchanged.<br/>
        /// The operation does not shift following data or change <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based start of the reserved region within the written data.<br/></param>
        /// <param name="value">The string to write, or <see langword="null"/> to write the null-string marker.<br/></param>
        /// <param name="reservedByteCount">The number of existing bytes reserved for the complete encoded representation.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The reserved region is invalid or the encoded value does not fit within it.<br/></exception>
        public void WriteAtOffset(int destinationOffset, string? value, int reservedByteCount)
        {
            EnsureNotDisposed();
            if (reservedByteCount < 0)
                throw new ArgumentOutOfRangeException(nameof(reservedByteCount), "Reserved byte count must be non-negative.");

            Span<byte> destination = GetWritableOffsetSpan(destinationOffset, reservedByteCount);
            int byteCount = value == null ? -1 : StringEncoding.GetByteCount(value);
            int payloadByteCount = Math.Max(byteCount, 0);
            int totalSize = checked(Get7BitEncodedIntSize(byteCount) + payloadByteCount);
            if (totalSize > reservedByteCount)
                throw new ArgumentOutOfRangeException(nameof(reservedByteCount), "The encoded string does not fit within the reserved region.");

            int written = Write7BitEncodedIntToSpan(destination, byteCount);
            if (value != null)
                StringEncoding.GetBytes(value, destination.Slice(written, payloadByteCount));
        }

        /// <summary>
        /// Overwrites existing written bytes from a range of <paramref name="source"/>.<br/>
        /// Overlapping source and destination storage is supported, and the operation does not change <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based destination offset within the written data.<br/></param>
        /// <param name="source">The source array containing the bytes to copy.<br/></param>
        /// <param name="sourceOffset">The zero-based offset of the first source byte.<br/></param>
        /// <param name="count">The number of bytes to copy.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The source or destination range is invalid.<br/></exception>
        public void WriteAtOffset(int destinationOffset, byte[] source, int sourceOffset, int count)
        {
            EnsureNotDisposed();
            ArgumentNullException.ThrowIfNull(source);
            if ((uint)sourceOffset > (uint)source.Length ||
                (uint)count > (uint)(source.Length - sourceOffset))
                throw new ArgumentOutOfRangeException(nameof(sourceOffset), "Source bounds are invalid.");

            source.AsSpan(sourceOffset, count).CopyTo(GetWritableOffsetSpan(destinationOffset, count));
        }

        /// <summary>
        /// Writes a sequence of arbitrary-precision integers prefixed by its logical element count.<br/>
        /// Each element uses the same representation as <see cref="Write(BigInteger)"/> and can be restored with <see cref="ReadBigIntegersWithCount"/>.<br/>
        /// </summary>
        /// <param name="source">The integers to write in sequence order.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteBigIntegersWithCount(BigInteger[] source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Write7BitEncodedInt(source.Length);
            for (int i = 0; i < source.Length; i++)
                Write(source[i]);
        }

        /// <summary>
        /// Writes booleans as their raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadBooleansWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The booleans to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteBooleansWithByteLength(bool[] source) => WriteArrayWithByteLength(source);

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

        /// <summary>
        /// Copies an unframed byte span at the current position and advances by its length.<br/>
        /// Use <see cref="WriteBytesWithByteLength(byte[])"/> when the payload must carry its own byte-length prefix.<br/>
        /// </summary>
        /// <param name="bytes">The byte span to copy.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            EnsureCapacity(_position + bytes.Length);
            bytes.CopyTo(GetWritableSpan(_position, bytes.Length));
            _position += bytes.Length;
            UpdateLengthAfterWrite(_position);
        }

        /// <summary>
        /// Copies an unframed byte-array range at the current position and advances by <paramref name="count"/>.<br/>
        /// Use <see cref="WriteBytesWithByteLength(byte[], int, int)"/> when the selected range must carry its own byte-length prefix.<br/>
        /// </summary>
        /// <param name="source">The byte array containing the range to copy.<br/></param>
        /// <param name="offset">The zero-based source offset at which copying begins.<br/></param>
        /// <param name="count">The number of bytes to copy.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The source range is invalid, or the write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
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
        /// Writes bytes prefixed by their payload byte length.<br/>
        /// Read the value with <see cref="ReadBytesWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The bytes to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteBytesWithByteLength(byte[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes a selected byte range prefixed by the selected payload byte length.<br/>
        /// The prefix uses the same signed 7-bit representation as other BufferStream length prefixes and can be read with <see cref="ReadBytesWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The byte array containing the range to write.<br/></param>
        /// <param name="offset">The zero-based source offset at which copying begins.<br/></param>
        /// <param name="count">The number of bytes to prefix and write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The requested range is outside <paramref name="source"/>.<br/></exception>
        public void WriteBytesWithByteLength(byte[] source, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(source);
            if ((uint)offset > (uint)source.Length || (uint)count > (uint)(source.Length - offset))
                throw new ArgumentOutOfRangeException(nameof(count), "The requested source range is invalid.");

            Write7BitEncodedInt(count);
            WriteBytes(source, offset, count);
        }

        /// <summary>
        /// Writes UTF-16 characters as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadCharsWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The characters to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteCharsWithByteLength(char[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes complex numbers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadComplexesWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The complex numbers to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteComplexesWithByteLength(Complex[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes dates as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadDateOnlysWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The dates to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteDateOnlysWithByteLength(DateOnly[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes date-time offsets as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadDateTimeOffsetsWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The date-time offsets to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteDateTimeOffsetsWithByteLength(DateTimeOffset[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes date-time values as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadDateTimesWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The date-time values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteDateTimesWithByteLength(DateTime[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes decimal values prefixed by their logical element count.<br/>
        /// Each element uses the same representation as <see cref="Write(decimal)"/> and can be restored with <see cref="ReadDecimalsWithCount"/>.<br/>
        /// </summary>
        /// <param name="source">The decimal values to write in sequence order.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteDecimalsWithCount(decimal[] source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Write7BitEncodedInt(source.Length);
            for (int i = 0; i < source.Length; i++)
                Write(source[i]);
        }

        /// <summary>
        /// Writes double-precision values as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadDoublesWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteDoublesWithByteLength(double[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes GUID values as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadGuidsWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The GUID values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteGuidsWithByteLength(Guid[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes half-precision values as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadHalfsWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteHalfsWithByteLength(Half[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes signed 128-bit integers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadInt128sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteInt128sWithByteLength(Int128[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes signed 16-bit integers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadInt16sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteInt16sWithByteLength(short[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes signed 32-bit integers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadInt32sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteInt32sWithByteLength(int[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes signed 64-bit integers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadInt64sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteInt64sWithByteLength(long[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes 3x2 matrices as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadMatrix3x2sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The matrices to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteMatrix3x2sWithByteLength(Matrix3x2[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes 4x4 matrices as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadMatrix4x4sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The matrices to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteMatrix4x4sWithByteLength(Matrix4x4[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes planes as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadPlanesWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The planes to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WritePlanesWithByteLength(Plane[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes quaternions as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadQuaternionsWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The quaternions to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteQuaternionsWithByteLength(Quaternion[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes signed bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadSBytesWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteSBytesWithByteLength(sbyte[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes single-precision values as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadSinglesWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteSinglesWithByteLength(float[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes strings prefixed by their logical element count.<br/>
        /// Each element uses the same nullable representation as <see cref="Write(string?)"/> and can be restored with <see cref="ReadStringsWithCount"/>.<br/>
        /// </summary>
        /// <param name="source">The nullable strings to write in sequence order.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteStringsWithCount(string?[] source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Write7BitEncodedInt(source.Length);
            for (int i = 0; i < source.Length; i++)
                Write(source[i]);
        }

        /// <summary>
        /// Writes times of day as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadTimeOnlysWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The times of day to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteTimeOnlysWithByteLength(TimeOnly[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes time intervals as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadTimeSpansWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The time intervals to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteTimeSpansWithByteLength(TimeSpan[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes unsigned 128-bit integers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadUInt128sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteUInt128sWithByteLength(UInt128[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes unsigned 16-bit integers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadUInt16sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteUInt16sWithByteLength(ushort[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes unsigned 32-bit integers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadUInt32sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteUInt32sWithByteLength(uint[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes unsigned 64-bit integers as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadUInt64sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The values to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteUInt64sWithByteLength(ulong[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes two-dimensional vectors as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadVector2sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The vectors to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteVector2sWithByteLength(Vector2[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes three-dimensional vectors as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadVector3sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The vectors to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteVector3sWithByteLength(Vector3[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes four-dimensional vectors as raw in-memory bytes prefixed by the payload byte length.<br/>
        /// Read the value with <see cref="ReadVector4sWithByteLength"/>.<br/>
        /// </summary>
        /// <param name="source">The vectors to write.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteVector4sWithByteLength(Vector4[] source) => WriteArrayWithByteLength(source);

        /// <summary>
        /// Writes version values prefixed by their logical element count.<br/>
        /// Each version stores its major, minor, build, and revision components and can be restored with <see cref="ReadVersionsWithCount"/>.<br/>
        /// </summary>
        /// <param name="source">The versions to write in sequence order.<br/></param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<br/></exception>
        public void WriteVersionsWithCount(Version[] source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Write7BitEncodedInt(source.Length);
            foreach (Version version in source)
            {
                Write7BitEncodedInt(version.Major);
                Write7BitEncodedInt(version.Minor);
                Write7BitEncodedInt(version.Build);
                Write7BitEncodedInt(version.Revision);
            }
        }


        /// <summary>
        /// Determines whether another stream currently contains the same logical byte sequence.<br/>
        /// Position, capacity, ownership, and string encoding are not compared; two references to the same instance remain equal after disposal.<br/>
        /// Distinct streams must both be usable because their contents are inspected.<br/>
        /// </summary>
        /// <param name="other">The stream to compare with this instance.<br/></param>
        /// <returns><see langword="true"/> when both streams contain equal bytes in the same order; otherwise <see langword="false"/>.<br/></returns>
        /// <exception cref="ObjectDisposedException">Either distinct stream or either stream's root owner has been disposed.<br/></exception>
        public bool Equals(BufferStream? other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other is null)
                return false;

            EnsureNotDisposed();
            other.EnsureNotDisposed();
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

        /// <summary>
        /// Determines whether an object is a <see cref="BufferStream"/> with the same logical byte sequence as this instance.<br/>
        /// The comparison delegates to <see cref="Equals(BufferStream?)"/> and ignores cursor position, capacity, ownership, and encoding.<br/>
        /// </summary>
        /// <param name="obj">The object to compare with this instance.<br/></param>
        /// <returns><see langword="true"/> when <paramref name="obj"/> is a byte-for-byte equal stream; otherwise <see langword="false"/>.<br/></returns>
        /// <exception cref="ObjectDisposedException">Either distinct stream or either stream's root owner has been disposed.<br/></exception>
        public override bool Equals(object? obj) => obj is BufferStream other && Equals(other);

        /// <summary>
        /// Returns a content-derived hash based on the logical length and representative bytes.<br/>
        /// Because stream contents are mutable, an instance must not be mutated while it is being used as a hash-table key.<br/>
        /// </summary>
        /// <returns>A hash code consistent with the current content-based equality contract.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        public override int GetHashCode()
        {
            EnsureNotDisposed();
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

        /// <summary>
        /// Determines whether two streams are the same reference or currently contain the same logical byte sequence.<br/>
        /// Two null references compare equal; one null reference compares unequal.<br/>
        /// </summary>
        /// <param name="left">The left stream operand.<br/></param>
        /// <param name="right">The right stream operand.<br/></param>
        /// <returns><see langword="true"/> when the operands are both null, reference-identical, or byte-for-byte equal.<br/></returns>
        /// <exception cref="ObjectDisposedException">Distinct non-null operands require content from a disposed stream or owner.<br/></exception>
        public static bool operator ==(BufferStream? left, BufferStream? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two streams are neither reference-identical nor byte-for-byte equal.<br/>
        /// This is the logical complement of <see cref="operator ==(BufferStream?, BufferStream?)"/>.<br/>
        /// </summary>
        /// <param name="left">The left stream operand.<br/></param>
        /// <param name="right">The right stream operand.<br/></param>
        /// <returns><see langword="true"/> when the equality operator returns <see langword="false"/>.<br/></returns>
        /// <exception cref="ObjectDisposedException">Distinct non-null operands require content from a disposed stream or owner.<br/></exception>
        public static bool operator !=(BufferStream? left, BufferStream? right) => !(left == right);


        /// <summary>
        /// Creates a non-owning writable stream view over an entire byte array without copying it.<br/>
        /// Mutations are visible through both views, and disposing the stream does not return or clear the caller-owned array.<br/>
        /// </summary>
        /// <param name="buffer">The byte array to borrow as the complete initial stream contents.<br/></param>
        /// <returns>A writable borrowed stream positioned at zero.<br/></returns>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.<br/></exception>
        public static implicit operator BufferStream(byte[] buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return new BufferStream(buffer.AsMemory());
        }

        /// <summary>
        /// Creates an independently owned stream by copying the supplied span.<br/>
        /// Later mutations of the source span do not affect the stream, and stream mutations do not affect the source.<br/>
        /// </summary>
        /// <param name="buffer">The span whose current bytes are copied as the complete initial stream contents.<br/></param>
        /// <returns>An owning stream positioned at zero.<br/></returns>
        public static implicit operator BufferStream(Span<byte> buffer) => new BufferStream(buffer.ToArray());

        /// <summary>
        /// Creates a stream using the <see cref="BufferStream(MemoryStream, bool, bool, Encoding?)"/> constructor defaults.<br/>
        /// An accessible source buffer is borrowed without copying; otherwise the remaining source bytes are copied into pooled storage.<br/>
        /// The source stream is not disposed by this conversion.<br/>
        /// </summary>
        /// <param name="buffer">The memory stream supplying the initial contents.<br/></param>
        /// <returns>A stream positioned at zero.<br/></returns>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.<br/></exception>
        /// <exception cref="IOException">The remaining source length is unsupported.<br/></exception>
        public static implicit operator BufferStream(MemoryStream buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return new BufferStream(buffer);
        }

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

        /// <summary>
        /// Reads an unsigned 32-bit base-128 payload while enforcing its five-byte width limit.<br/>
        /// Bytes consumed before truncation or an over-width terminal byte remain consumed, matching the stream's sequential-read behavior.<br/>
        /// </summary>
        /// <returns>The decoded unsigned payload before any signed ZigZag interpretation.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The payload ends before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The fifth byte contains bits outside the 32-bit payload width.<br/></exception>
        private uint Read7BitEncodedUInt32Core()
        {
            uint result = 0;
            for (int shift = 0; shift < 28; shift += 7)
            {
                byte current = ReadByte();
                result |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return result;
            }

            byte terminal = ReadByte();
            if (terminal > 0x0F)
                throw new InvalidDataException("The 7-bit encoded value exceeds 32 bits.");
            return result | (uint)terminal << 28;
        }

        /// <summary>
        /// Reads an unsigned 64-bit base-128 payload while enforcing its ten-byte width limit.<br/>
        /// Bytes consumed before truncation or an over-width terminal byte remain consumed, matching the stream's sequential-read behavior.<br/>
        /// </summary>
        /// <returns>The decoded unsigned payload before any signed ZigZag interpretation.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The payload ends before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The tenth byte contains bits outside the 64-bit payload width.<br/></exception>
        private ulong Read7BitEncodedUInt64Core()
        {
            ulong result = 0;
            for (int shift = 0; shift < 63; shift += 7)
            {
                byte current = ReadByte();
                result |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return result;
            }

            byte terminal = ReadByte();
            if (terminal > 0x01)
                throw new InvalidDataException("The 7-bit encoded value exceeds 64 bits.");
            return result | (ulong)terminal << 63;
        }

        /// <summary>
        /// Reads an unsigned 128-bit base-128 payload while enforcing its nineteen-byte width limit.<br/>
        /// Bytes consumed before truncation or an over-width terminal byte remain consumed, matching the stream's sequential-read behavior.<br/>
        /// </summary>
        /// <returns>The decoded unsigned payload before any signed ZigZag interpretation.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="EndOfStreamException">The payload ends before a terminating byte.<br/></exception>
        /// <exception cref="InvalidDataException">The nineteenth byte contains bits outside the 128-bit payload width.<br/></exception>
        private UInt128 Read7BitEncodedUInt128Core()
        {
            UInt128 result = 0;
            for (int shift = 0; shift < 126; shift += 7)
            {
                byte current = ReadByte();
                result |= (UInt128)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return result;
            }

            byte terminal = ReadByte();
            if (terminal > 0x03)
                throw new InvalidDataException("The 7-bit encoded value exceeds 128 bits.");
            return result | (UInt128)terminal << 126;
        }

        /// <summary>
        /// Encodes an unsigned 32-bit payload into a temporary five-byte span and commits it with one validated stream write.<br/>
        /// Staging the complete representation prevents partial mutation when a fixed or borrowed destination cannot contain it.<br/>
        /// </summary>
        /// <param name="value">The unsigned payload to encode without ZigZag transformation.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        private void Write7BitEncodedUInt32Core(uint value)
        {
            Span<byte> encoded = stackalloc byte[5];
            int count = 0;
            do
            {
                byte current = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                    current |= 0x80;
                encoded[count++] = current;
            } while (value != 0);

            WriteBytes(encoded.Slice(0, count));
        }

        /// <summary>
        /// Encodes an unsigned 64-bit payload into a temporary ten-byte span and commits it with one validated stream write.<br/>
        /// Staging the complete representation prevents partial mutation when a fixed or borrowed destination cannot contain it.<br/>
        /// </summary>
        /// <param name="value">The unsigned payload to encode without ZigZag transformation.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        private void Write7BitEncodedUInt64Core(ulong value)
        {
            Span<byte> encoded = stackalloc byte[10];
            int count = 0;
            do
            {
                byte current = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                    current |= 0x80;
                encoded[count++] = current;
            } while (value != 0);

            WriteBytes(encoded.Slice(0, count));
        }

        /// <summary>
        /// Encodes an unsigned 128-bit payload into a temporary nineteen-byte span and commits it with one validated stream write.<br/>
        /// Staging the complete representation prevents partial mutation when a fixed or borrowed destination cannot contain it.<br/>
        /// </summary>
        /// <param name="value">The unsigned payload to encode without ZigZag transformation.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The complete encoding exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        private void Write7BitEncodedUInt128Core(UInt128 value)
        {
            Span<byte> encoded = stackalloc byte[19];
            int count = 0;
            do
            {
                byte current = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                    current |= 0x80;
                encoded[count++] = current;
            } while (value != 0);

            WriteBytes(encoded.Slice(0, count));
        }

        /// <summary>
        /// Reads directly into caller-provided span storage without renting an adapter array.<br/>
        /// The operation follows the ordinary <see cref="Stream.Read(Span{byte})"/> contract: it copies at most the remaining logical bytes, advances by the copied count, and returns zero at end of stream.<br/>
        /// Segment-relative addressing, owner lifetime checks, and fixed-view boundaries are identical to the byte-array overload.<br/>
        /// </summary>
        /// <param name="destination">Writable destination that receives the available bytes.<br/></param>
        /// <returns>The number of bytes copied into <paramref name="destination"/>.<br/></returns>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        public override int Read(Span<byte> destination)
        {
            EnsureNotDisposed();
            int available = Math.Min(destination.Length, EffectiveLength - _position);
            if (available <= 0) return 0;

            GetReadOnlySpan(_position, available).CopyTo(destination);
            _position += available;
            return available;
        }

        /// <summary>
        /// Writes caller-provided span data directly into this stream without allocating an adapter array.<br/>
        /// Owned roots grow through the established pooled-capacity path; borrowed roots and fixed segments retain their existing bounds behavior.<br/>
        /// The cursor and owning logical length advance only after the complete span copy succeeds.<br/>
        /// </summary>
        /// <param name="source">Read-only bytes to copy at the current cursor position.<br/></param>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">The write exceeds fixed-view bounds or available borrowed storage.<br/></exception>
        public override void Write(ReadOnlySpan<byte> source)
        {
            EnsureCapacity(_position + source.Length);
            source.CopyTo(GetWritableSpan(_position, source.Length));
            _position += source.Length;
            UpdateLengthAfterWrite(_position);
        }

        /// <summary>
        /// Overwrites one existing byte at an unsigned managed-buffer offset without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// This compatibility boundary preserves callers whose serialized positions are represented as <see cref="uint"/> while retaining the established signed-index implementation.<br/>
        /// Offsets above <see cref="int.MaxValue"/> are rejected before any destination validation or mutation because managed spans cannot address them.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based unsigned destination offset within existing written data.<br/></param>
        /// <param name="value">The byte value to overwrite.<br/></param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="destinationOffset"/> exceeds the managed-buffer range or the byte does not fit entirely within written data.<br/></exception>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        public void WriteAtOffset(uint destinationOffset, byte value)
        {
            if (destinationOffset > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset), "The offset exceeds the managed-buffer range.");

            WriteAtOffset((int)destinationOffset, value);
        }

        /// <summary>
        /// Overwrites one existing 32-bit unsigned integer at an unsigned managed-buffer offset without changing <see cref="Position"/> or <see cref="Length"/>.<br/>
        /// This compatibility boundary preserves callers whose serialized positions are represented as <see cref="uint"/> while retaining the established signed-index implementation.<br/>
        /// Offsets above <see cref="int.MaxValue"/> are rejected before any destination validation or mutation because managed spans cannot address them.<br/>
        /// </summary>
        /// <param name="destinationOffset">The zero-based unsigned destination offset within existing written data.<br/></param>
        /// <param name="value">The unsigned integer to overwrite.<br/></param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="destinationOffset"/> exceeds the managed-buffer range or the complete value does not fit entirely within written data.<br/></exception>
        /// <exception cref="ObjectDisposedException">This stream or its root owner has been disposed.<br/></exception>
        public void WriteAtOffset(uint destinationOffset, uint value)
        {
            if (destinationOffset > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset), "The offset exceeds the managed-buffer range.");

            WriteAtOffset((int)destinationOffset, value);
        }


    }
}
