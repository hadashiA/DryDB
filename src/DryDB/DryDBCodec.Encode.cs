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
using DryDB.BTree;
using DryDB.Internal;
#if NET7_0_OR_GREATER
using static System.Runtime.InteropServices.MemoryMarshal;
#else
using static System.Runtime.CompilerServices.MemoryMarshalEx;
#endif

namespace DryDB;

static partial class DryDBCodec
{
    // TODO: Integrate the builder into this class.

    public static async ValueTask WriteDatabaseHeaderAsync(
        Stream stream,
        Header header,
        FilterOptions? filterOptions,
        CancellationToken cancellationToken = default)
    {
        Span<byte> headerBytes = stackalloc byte[Unsafe.SizeOf<Header>()];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(headerBytes), header);
        stream.Write(headerBytes);

        // write filter ids
        if (filterOptions?.Filters is { Count: > 0 } filters)
        {
            Span<byte> filterIdBuffer = stackalloc byte[byte.MaxValue + 1];
            for (var i = 0; i < filters.Count; i++)
            {
                var filter = filters[i];
                var filterIdBytes = Encoding.UTF8.GetByteCount(filter.Id);
                if (filterIdBytes > byte.MaxValue)
                {
                    throw new InvalidOperationException($"Filter ID length must be less than 255: `{filter.Id}` ");
                }
                var bytesWritten = Encoding.UTF8.GetBytes(filter.Id,  filterIdBuffer[1..]);
                filterIdBuffer[0] = (byte)bytesWritten;
                stream.Write(filterIdBuffer[..(bytesWritten + 1)]);
            }
        }
    }

    /// <summary>
    /// </summary>
    /// <returns>
    /// Returns the trailing offset of the written IndexDescriptor.
    /// </returns>
    public static async ValueTask<long[]> WriteTableDescriptorAsync(
        Stream stream,
        TableOptions tableOptions,
        CancellationToken cancellationToken = default)
    {
        var nameUtf8 = Encoding.UTF8.GetBytes(tableOptions.Name);
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(int) + nameUtf8.Length);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, nameUtf8.Length);
            nameUtf8.CopyTo(buffer.AsSpan(sizeof(int)));
            await stream.WriteAsync(buffer.AsMemory(0, sizeof(int) + nameUtf8.Length), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var indexDescriptorWriteCount = 0;
        var indexDescriptorEndPositions = new long[tableOptions.SecondaryIndexOptionsList.Count + 1];

        // build primary index descriptor
        await WriteIndexDescriptorAsync(stream, tableOptions.PrimaryKeyIndexOptions, cancellationToken);
        indexDescriptorEndPositions[indexDescriptorWriteCount++] = stream.Position;

        // build secondary index length
        Span<byte> indexCountBuffer = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(
            indexCountBuffer,
            (ushort)tableOptions.SecondaryIndexOptionsList.Count);
        stream.Write(indexCountBuffer);

        // build secondary index descriptors
        for (var i = 0; i < tableOptions.SecondaryIndexOptionsList.Count; i++)
        {
            var indexOptions = tableOptions.SecondaryIndexOptionsList[i];
            await WriteIndexDescriptorAsync(stream, indexOptions, cancellationToken);
            indexDescriptorEndPositions[indexDescriptorWriteCount++]  = stream.Position;
        }
        return indexDescriptorEndPositions;
    }

    public static async ValueTask WriteIndexDescriptorAsync(
        Stream stream,
        IndexOptions indexOptions,
        CancellationToken cancellationToken = default)
    {
        var indexNameUtf8 = Encoding.UTF8.GetBytes(indexOptions.Name);
        var keyEncodingIdUtf8 = Encoding.UTF8.GetBytes(indexOptions.KeyEncoding.Id);
        var descriptorLength = sizeof(ushort) * 2 + indexNameUtf8.Length + keyEncodingIdUtf8.Length + 1 + 1 + sizeof(long);

        var buffer = ArrayPool<byte>.Shared.Rent(descriptorLength);

        ref var bufferRef = ref GetArrayDataReference(buffer);

        Unsafe.WriteUnaligned(ref bufferRef, (ushort)indexNameUtf8.Length);
        bufferRef = ref Unsafe.Add(ref bufferRef, sizeof(ushort));

        Unsafe.WriteUnaligned(ref bufferRef, (ushort)keyEncodingIdUtf8.Length);
        bufferRef = ref Unsafe.Add(ref bufferRef, sizeof(ushort));

        Unsafe.CopyBlock(
            ref bufferRef,
            ref GetArrayDataReference(indexNameUtf8),
            (uint)indexNameUtf8.Length);
        bufferRef = ref Unsafe.Add(ref bufferRef, indexNameUtf8.Length);

        Unsafe.CopyBlock(
            ref bufferRef,
            ref GetArrayDataReference(keyEncodingIdUtf8),
            (uint)keyEncodingIdUtf8.Length);
        bufferRef = ref Unsafe.Add(ref bufferRef, keyEncodingIdUtf8.Length);

        bufferRef = (byte)(indexOptions.IsUnique ? 1 : 0);
        bufferRef = ref Unsafe.Add(ref bufferRef, 1);

        bufferRef = (byte)indexOptions.ValueKind;
        bufferRef = ref Unsafe.Add(ref bufferRef, 1);

        var payloadPosition = stream.Position + descriptorLength;
        Unsafe.WriteUnaligned(ref bufferRef, payloadPosition);

        await stream.WriteAsync(buffer.AsMemory(0, descriptorLength), cancellationToken);
    }

    public static async ValueTask BuildTreeAsync(
        Stream stream,
        int pageSize,
        TableOptions tableOptions,
        KeyValueList keyValues,
        IReadOnlyList<IPageFilter>? pageFilters,
        long[] indexDescriptorEndPositions,
        CancellationToken cancellationToken = default)
    {
        // write primary tree
        var primaryKeyResult = await TreeBuilder.BuildToAsync(
            stream,
            pageSize,
            keyValues,
            pageFilters,
            cancellationToken);

        // write primary tree root position
        Span<byte> positionBuffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(positionBuffer, primaryKeyResult.RootPageNumber.Value);
        var currentPosition = stream.Position;
        stream.Seek(indexDescriptorEndPositions[0] - sizeof(long), SeekOrigin.Begin);
        stream.Write(positionBuffer);
        stream.Seek(currentPosition, SeekOrigin.Begin);

        // write secondary tree root positions
        for (var i = 0; i < tableOptions.SecondaryIndexOptionsList.Count; i++)
        {
            var indexOptions = tableOptions.SecondaryIndexOptionsList[i];
            var secondaryKeyValues = KeyValueList.Create(indexOptions.KeyEncoding, indexOptions.IsUnique);
            var primaryKeyIndex = 0;
            foreach (var (primaryKey, value) in keyValues)
            {
                var secondaryKey = indexOptions.IndexFactory.Invoke(primaryKey, value);
                var valuePointer = primaryKeyResult.WroteValueRefs[primaryKeyIndex];
                secondaryKeyValues.Add(secondaryKey, valuePointer.Encode());
                primaryKeyIndex++;
            }

            // write secondary key tree
            var secondaryKeyResult = await TreeBuilder.BuildToAsync(stream, pageSize, secondaryKeyValues, pageFilters, cancellationToken);

            // write secondary tree root position
            Span<byte> positionBuffer2 = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(positionBuffer2, secondaryKeyResult.RootPageNumber.Value);

            var currentPosition2 = stream.Position;
            stream.Seek(indexDescriptorEndPositions[i + 1] - sizeof(long), SeekOrigin.Begin);
            stream.Write(positionBuffer2);
            stream.Seek(currentPosition2, SeekOrigin.Begin);
        }
    }
}
