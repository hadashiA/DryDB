using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DryDB.BTree;
using DryDB.Internal;

namespace DryDB.Storages;

public class InMemoryPageLoader(Memory<byte> memory) : IPageLoader
{
    public void Dispose()
    {
    }

    public ValueTask<IMemoryOwner<byte>> ReadPageAsync(
        PageNumber pageNumber,
        IPageFilter[]? filters,
        CancellationToken cancellationToken = default)
    {
        var pageLength = Unsafe.ReadUnaligned<int>(
            ref Unsafe.Add(
                ref MemoryMarshal.GetReference(memory.Span),
                (int)pageNumber.Value));

        var destination = MemoryPool<byte>.Shared.Rent(pageLength);
        memory.Slice((int)pageNumber.Value, pageLength).CopyTo(destination.Memory);

        if (filters is { Length: > 0 })
        {
            try
            {
                return new ValueTask<IMemoryOwner<byte>>(
                    ApplyFilter(
                        destination.Memory.Span[..pageLength],
                        filters));
            }
            finally
            {
                destination.Dispose();
            }
        }
        return new ValueTask<IMemoryOwner<byte>>(destination);
    }

    public IMemoryOwner<byte> ReadPage(PageNumber pageNumber, IPageFilter[]? filters)
    {
        var pageLength = Unsafe.ReadUnaligned<int>(
            ref Unsafe.Add(
                ref MemoryMarshal.GetReference(memory.Span),
                (int)pageNumber.Value));

        var destination = MemoryPool<byte>.Shared.Rent(pageLength);

        memory.Span.Slice((int)pageNumber.Value, pageLength)
            .CopyTo(destination.Memory.Span);

        if (filters is { Length: > 0 })
        {
            try
            {
                return ApplyFilter(
                    destination.Memory.Span[..pageLength],
                    filters);
            }
            finally
            {
                destination.Dispose();
            }
        }
        return destination;
    }

    static IMemoryOwner<byte> ApplyFilter(ReadOnlySpan<byte> source, IPageFilter[] filters)
    {
        var headerSize = Unsafe.SizeOf<PageHeader>() + Unsafe.SizeOf<NodeHeader>();
        var output = BufferWriterPool.Rent(source.Length);

        // copy header
        output.Write(source[..headerSize]);

        filters[0].Decode(source[headerSize..], output);

        // update page header
        Unsafe.WriteUnaligned(
            ref MemoryMarshal.GetReference(output.WrittenSpan),
            new PageHeader { PageSize = output.WrittenCount });

        if (filters.Length <= 1)
        {
            return output.ToPoolableMemory();
        }

        // double buffer
        var input = output;
        output = BufferWriterPool.Rent(output.WrittenCount);

        for (var i = 1; i < filters.Length; i++)
        {
            // copy header
            output.Write(source[..headerSize]);

            // copy node header
            filters[i].Encode(input.WrittenSpan[headerSize..], output);

            // update page header
            Unsafe.WriteUnaligned(
                ref MemoryMarshal.GetReference(output.WrittenSpan),
                new PageHeader { PageSize = output.WrittenCount });

            // last
            if (i >= filters.Length - 1)
            {
                BufferWriterPool.Return(input);
                return output.ToPoolableMemory();
            }
            (output, input) = (input, output);
        }
        throw new InvalidOperationException("unreached");
    }
}
