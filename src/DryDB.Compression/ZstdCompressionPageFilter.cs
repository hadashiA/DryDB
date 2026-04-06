using System;
using System.Buffers;
using NativeCompressions;

namespace DryDB.Compression;

public class ZstdCompressionPageFilter : IPageFilter
{
    static ZstdCompressionPageFilter()
    {
        PageFilterRegistry.Register(new ZstdCompressionPageFilter());
    }

    public string Id => "DryDB.ZstdCompression";

    public void Encode(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        using var encoder = new ZstandardEncoder();
        var maxDestinationLength = Zstandard.GetMaxCompressedLength(input.Length);
        OperationStatus status;
        do
        {
            var destination = output.GetSpan(maxDestinationLength);

            status = encoder.Compress(
                input,
                destination,
                out var bytesConsumed,
                out var bytesWritten,
                isFinalBlock: true);

            if (status == OperationStatus.InvalidData)
            {
                throw new Exception("zstd compress failed. invalid data");
            }

            input = input[bytesConsumed..];
            output.Advance(bytesWritten);
        } while (status == OperationStatus.DestinationTooSmall);
    }

    public void Decode(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        using var decoder = new ZstandardDecoder();
        OperationStatus status;
        do
        {
            var destination = output.GetSpan(input.Length);

            status = decoder.Decompress(
                input,
                destination,
                out var bytesConsumed,
                out var bytesWritten);

            if (status == OperationStatus.InvalidData)
            {
                throw new Exception("zstd decompress failed. invalid data");
            }

            input = input[bytesConsumed..];
            output.Advance(bytesWritten);
        } while (status == OperationStatus.DestinationTooSmall);
    }
}
