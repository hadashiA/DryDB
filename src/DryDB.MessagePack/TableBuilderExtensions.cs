using MessagePack;

namespace DryDB.MessagePack;

public static class TableBuilderExtensions
{
    public static MessagePackTableBuilder<TValue> AsMessagePackSerializable<TValue>(
        this TableBuilder builder,
        MessagePackSerializerOptions? options = null)
    {
        return new MessagePackTableBuilder<TValue>(builder, options);
    }
}
