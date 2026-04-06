namespace DryDB;

public enum SortOrder
{
    Ascending,
    Descending,
}

public static class KeyRange
{
    public static byte[] Unbound => [];
}
