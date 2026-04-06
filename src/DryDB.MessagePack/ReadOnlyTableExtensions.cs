using MessagePack;

namespace DryDB.MessagePack;

public static class ReadOnlyTableExtensions
{
    public static MessagePackReadOnlyTable<TValue> GetTable<TValue>(
        this ReadOnlyDatabase db,
        string tableName,
        MessagePackSerializerOptions? options = null)
    {
        return db.GetTable(tableName).AsMessagePackSerializable<TValue>(options);
    }

    public static MessagePackReadOnlyTable<TValue> AsMessagePackSerializable<TValue>(
        this ReadOnlyTable table,
        MessagePackSerializerOptions? options = null)
    {
        return new MessagePackReadOnlyTable<TValue>(table, options);
    }
}