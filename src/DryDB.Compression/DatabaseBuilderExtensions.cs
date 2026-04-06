namespace DryDB.Compression;

public static class DatabaseBuilderExtensions
{
    public static void AddZstandardCompression(this FilterOptions options)
    {
        options.AddFilter(new ZstdCompressionPageFilter());
    }
}