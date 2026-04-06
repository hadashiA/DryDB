using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DryDB;

// Header
//   magic_bytes(4): "DRY\0"
//   major_version(1): byte
//   minor_version(1): byte
//   page_filter_count(2): ushort
//   page_size(4): int
//   table_count(2): int
//
//   PageFilter[0]
//     name_length(1): byte
//     name(name_length): utf8
//
//   Table[0]:
//     name_length(4): int
//     name(name_length): utf8
//     Index(Primary Key):
//       name_length(2): ushort
//       key_encoding_id_length(2): ushort
//       name(name_length): utf8
//       key_encoding_id(name_length): utf8
//       is_unique(1): bool
//       value_kind(1): enum
//       root_position(8): long
//     index_count(2): ushort
//     Index[1](Secondary Key):
//       ...
//    Table[1]:
//      ...
//
// Page[0]
//   page_length(4): int
//   payload...

// NOTE: little endian only
[StructLayout(LayoutKind.Explicit)]
unsafe struct Header
{
    public static ReadOnlySpan<byte> MagicBytesValue => "DRY\0"u8;

    [FieldOffset(0)]
    public fixed byte MagicBytes[4];

    [FieldOffset(4)]
    public byte MajorVersion;

    [FieldOffset(5)]
    public byte MinorVersion;

    [FieldOffset(6)]
    public ushort PageFilterCount;

    [FieldOffset(8)]
    public int PageSize;

    [FieldOffset(12)]
    public ushort TableCount;

    public bool ValidateMagicBytes()
    {
        fixed (byte* ptr = MagicBytes)
        {
            var span = new ReadOnlySpan<byte>(ptr, MagicBytesValue.Length);
            return MagicBytesValue.SequenceEqual(span);
        }
    }
}

[StructLayout(LayoutKind.Explicit, Size = 4)]
public struct PageHeader
{
    [FieldOffset(0)]
    public int PageSize;
}

public class StorageFormatException(string message) : Exception(message);

static partial class DryDBCodec
{
    public static async ValueTask<Catalog> ParseCatalogAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Header header;
        var buffer = ArrayPool<byte>.Shared.Rent(Unsafe.SizeOf<Header>());
        try
        {
            await stream.ReadAtLeastAsync(buffer, Unsafe.SizeOf<Header>(), cancellationToken: cancellationToken);
            header = Unsafe.ReadUnaligned<Header>(ref MemoryMarshal.GetReference(buffer.AsSpan()));
            if (!header.ValidateMagicBytes())
            {
                throw new StorageFormatException("Invalid magic bytes");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        stream.Seek(Unsafe.SizeOf<Header>(), SeekOrigin.Begin);

        // parse filters
        List<IPageFilter>? filters = null;
        for (var i = 0; i < header.PageFilterCount; i++)
        {
            var pageIdLength = stream.ReadByte();
            buffer = ArrayPool<byte>.Shared.Rent(pageIdLength);
            try
            {
                var bytesRead = await stream.ReadAtLeastAsync(buffer, pageIdLength, cancellationToken: cancellationToken);
                stream.Seek(-(bytesRead - pageIdLength), SeekOrigin.Current);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            var pageId = Encoding.UTF8.GetString(buffer[..pageIdLength]);
            var filter = PageFilterRegistry.Resolve(pageId);
            (filters ??= []).Add(filter);
        }

        // parse tables
        var tableCount = header.TableCount;
        var tableDescriptors = new Dictionary<string, TableDescriptor>(tableCount);

        for (var i = 0; i < tableCount; i++)
        {
            var tableDescriptor = await ParseTableDescriptorAsync(stream, cancellationToken);
            if (!tableDescriptors.TryAdd(tableDescriptor.Name, tableDescriptor))
            {
                throw new StorageFormatException($"Duplicate table name: {tableDescriptor.Name}");
            }
        }

        return new Catalog(header.PageSize, tableDescriptors, filters);
    }

    static async ValueTask<TableDescriptor> ParseTableDescriptorAsync(Stream stream, CancellationToken cancellationToken)
    {
        int tableNameLength, bytesRead;
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(int));
        try
        {
            bytesRead = await stream.ReadAtLeastAsync(buffer, sizeof(int), cancellationToken: cancellationToken);
            tableNameLength = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        stream.Seek(-(bytesRead - sizeof(int)), SeekOrigin.Current);

        string tableName;
        buffer = ArrayPool<byte>.Shared.Rent(tableNameLength);
        try
        {
            bytesRead = await stream.ReadAtLeastAsync(buffer, tableNameLength, cancellationToken: cancellationToken);
            tableName = Encoding.UTF8.GetString(buffer.AsSpan(0, tableNameLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        stream.Seek(-(bytesRead - tableNameLength), SeekOrigin.Current);

        var primaryKeyDescriptor = await ParseIndexDescriptorAsync(stream, cancellationToken);

        ushort indexCount;
        buffer = ArrayPool<byte>.Shared.Rent(sizeof(ushort));
        try
        {
            bytesRead = await stream.ReadAtLeastAsync(buffer, sizeof(ushort), cancellationToken: cancellationToken);
            indexCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(0, sizeof(ushort)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        stream.Seek(-(bytesRead - sizeof(ushort)), SeekOrigin.Current);

        var indexDescriptors = new IndexDescriptor[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            indexDescriptors[i] = await ParseIndexDescriptorAsync(stream, cancellationToken);
        }
        return new TableDescriptor(tableName, primaryKeyDescriptor, indexDescriptors);
    }

    static async ValueTask<IndexDescriptor> ParseIndexDescriptorAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(ushort));

        var bytesRead = await stream.ReadAtLeastAsync(buffer, sizeof(ushort) * 2, cancellationToken: cancellationToken);
        var indexNameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        var keyEncodingIdLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[sizeof(ushort)..]);
        stream.Seek(-(bytesRead - sizeof(ushort) * 2), SeekOrigin.Current);

        ArrayPool<byte>.Shared.Return(buffer);

        string indexName;
        string keyEncodingId;
        var remaining = indexNameLength + keyEncodingIdLength + 1 + 1 + sizeof(ulong);
        buffer = ArrayPool<byte>.Shared.Rent(remaining);
        try
        {
            bytesRead = await stream.ReadAtLeastAsync(buffer, remaining, cancellationToken: cancellationToken);
            indexName = Encoding.UTF8.GetString(buffer[..indexNameLength]);
            keyEncodingId = Encoding.UTF8.GetString(buffer[indexNameLength..(indexNameLength + keyEncodingIdLength)]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        stream.Seek(-(bytesRead - remaining), SeekOrigin.Current);

        var keyEncoding = KeyEncoding.FromId(keyEncodingId);

        var offset = indexNameLength + keyEncodingIdLength ;
        var isUnique = buffer[offset] == 1;
        var valueKind = (ValueKind)buffer[offset + 1];
        var rootPosition = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset + 2));

        return new IndexDescriptor
        {
            Name = indexName,
            IsUnique = isUnique,
            KeyEncoding = keyEncoding,
            ValueKind = valueKind,
            RootPageNumber = new PageNumber(rootPosition),
        };
    }
}
