# VKV

> [!WARNING]
> This project is work in progress 

VKV is a read-only embedded B+Tree based key/value database, implemented pure C#.

```
| Method             | Mean        | Error     | StdDev    |
|------------------- |------------:|----------:|----------:|
| VKV_FindByKey      |    37.57 us |  0.230 us |  0.120 us |
| CsSqlite_FindByKey | 4,322.48 us | 44.492 us | 26.476 us |
```

## Features

- B+Tree based query
  - Read a value by primary key 
  - Read values by key range
  - Count by key range
  - Secondary index
    - unique
    - non-unique
- Multiple Tables
- Support for both async and sync
- C# Serialization
  - MessagePack
  - (Other formats are under planning.  
- Unity Integration
  - `AsyncReadManager` + `NativeArray<byte>` based optimized custom loader.
- Custom key encoding
  - Simple ascii/u8 byte sequence string (default)
  - Int64
  - UUIDv7 (only for .NET 9 or later. Needs `Guid.CreateVersion7()`)
  - Ulid
- Page filter
  - Built-in filters
      - Cysharp/NativeCompression based page compression.
  - We can write custom filters in C#.
- Iterator API
  - By manipulating the cursor, large areas can be accessed sequentially.
- CLI tool
- TODO
  - Read values by key prefix
  - Support more large blob (currentry 65536B is limit)
  - Sort order

## Why read-only ?

## Installation

### NuGet

| Package         | Description                                            | Latest version                                                                                             |
|:----------------|:-------------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| VKV             | Main package. Embedded key/value store implementation. | [![NuGet](https://img.shields.io/nuget/v/VKV)](https://www.nuget.org/packages/VKV)                         |
| VKV.MessagePack | Plugin that handles value as MessagePack-Csharp.       | [![NuGet](https://img.shields.io/nuget/v/VKV.MessagePack)](https://www.nuget.org/packages/VKV.MessagePack) |
| VKV.Compression | Plugin  for compressing binary data.                    | [![NuGet](https://img.shields.io/nuget/v/VKV.Compression)](https://www.nuget.org/packages/VKV.Compression) | 
| VKV.UlidKey     | Plugin enabling the use of ulid as a key               | [![NuGet](https://img.shields.io/nuget/v/VKV.UlidKey)](https://www.nuget.org/packages/VKV.UlidKey) | 

### Unity

> [!NOTE]
> Requirements: Unity 2022.2 or later.

1. Install [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity).
2. Install the VKV package and the optional plugins listed above using NuGetForUnity.
3. Open the Package Manager window by selecting Window > Package Manager, then click on [+] > Add package from git URL and enter the following URL:
    - ```
      https://github.com/hadashiA/VKV.git?path=src/VKV.Unity/Assets/VKV#0.1.0-preview
      ```

### Cli tool (optional)

We distribute the CLI tool as a dotnet tool.

```bash
$ dotnet tool install vkv.cli --prerelease
```

See [CLI tool](#cli-tool) section for the usage.


## Usage

```cs
// Create DB

var builder = new DatabaseBuilder
{
     // The smallest unit of data loaded into memory
    PageSize = 4096,
};

// Create table (string key - ascii comparer)
var table1 = builder.CreateTable("items", KeyEncoding.Ascii);
table1.Append("key1", "value1"u8.ToArray()); // value is any `Memory<byte>` 
table1.Append("key2", "value2"u8.ToArray());
table1.Append("key3", "value3"u8.ToArray());
table1.Append("key4", "value4"u8.ToArray());


// Create table (Int64 key)
var table2 = builder.CreateTable("quests", KeyEncoding.Int64LittleEndian);
table2.Append(1, "hoge"u8.ToArray());

// Build
await builder.BuildToFileAsync("/path/to/bin.vkv");
```

```cs
// Open DB
var database = await ReadOnlyDatabase.OpenAsync("/pth/to/bin.vkv", new DatabaseLoadOptions
{
    // Maximum number of pages to keep in memory
    // Basically, page cache x capacity serves as a rough estimate of memory usage.
    PageCacheCapacity = 32, 
});

var table = database.GetTable("items");

// find by key (string key)
using var result = table.Get("key1");
result.IsExists //=> true
result.Span //=> "value1"u8

// byte sequence key (fatest)
using var result = table.Get("key1"u8);

// find key range. ("key1" between "key3")
using var range = table.GetRange(
    startKey: "key1"u8, 
    endKey: "key3"u8,
    startKeyExclusive: false,
    endKeyExclusive: false,
    sortOrder: SortOrder.Ascending);
    
range.Count //=> 3

// "key1" <=
using var range = table.GetRange("key1"u8, KeyRange.Unbound);

// "key1" <
using var range = table.GetRange("key1"u8, KeyRange.Unbound, startKeyExclusive: true);

// "key999" >= 
using var range = table.GetRange(KeyRange.UnBound, "key999");

// "key999" >
using var range = table.GetRange(KeyRange.UnBound, "key999", endKeyExclusive: true);

// count
var count = table.CountRange("key1", "key3");
    
// async
using var value1 = await table.GetAsync("key1");
using var range1 = await table.GetRangeAsync("key1", "key3");
var count = await table.CountRangeAsync();
```

### Secondary Index

```cs
var table1 = builder.CreateTable("items", KeyEncoding.Ascii);
table1.Append("key1", "value1"u8.ToArray()); // value is any `Memory<byte>` 
table1.Append("key2", "value2"u8.ToArray());
table1.Append("key3", "value3"u8.ToArray());
table1.Append("key4", "value4"u8.ToArray());

// Buiild secondary index (non-unique)
table1.AddSecondaryIndex("category", isUnique: false, KeyEncoding.Ascii, (key, value) =>
{
    // This lambda expression defines a factory that generates an index from any value.

    if (key.Span.SequenceEqual("key1") ||
        key.Span.SequenceEqual("key3"))
    {
        return "category1";
    }
    else
    {
        return "category2";
    }
});

// Build
await builder.BuildToFileAsync("/path/to/bin.vkv");
```

```cs
var table = database.GetTable("items");

// get "category1" values
table.Index("category").GetAll("category1"u8); //=> "value1", "value3"

// get range 
table.Index("category").GetRange("category1"u8, "category2"u8);

// async
await table.Index("category").GetAllAsync("category1"u8.ToArray());
await table.Index("category").GetRangeAsync(...);
```


### Range Iterator

Fetching all values beforehand consumes a lot of memory.


If you want to process each row sequentially in a table, you can further suppress memory consumption by using RangeIterator.

```cs
using var iterator = table.CreateIterator();

// Get current value..
iterator.CurrentKey //=> "key01"u8
iterator.CurrentValue //=> "value01"u8

// Seach and seek to the specified key position
iterator.TrySeek("key03"u8);

iterator.CurrentKey //=> "key03"u8;
iterator.CurrentValue //=> "value03"u8;

// Seek with async
await iterator.TrySeekAsync("key03");
```

RangeIterator also provides the IEnumerable and IAnycEnumerable interfaces.

``` cs
iterator.Current //=> "value03"u8
iterator.MoveNext();

iterator.Current //=> "value04"u8

// async
await iterator.MoveNextASync();
iterator.Current //=> "value05"u8
```

We can also use `foreach` and `await foreach` with iterators.
It loops from the current seek position to the end.


### C# Serialization

We can store arbitrary byte sequences in value, but it would be convenient if you could store arbitrary C# types.


VKV currently provides built-in serialization by the following libraries:

- [MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp)
- System.Text.Json (in progress)

#### VKV.MessagePack

Installing the `VKV.MessagePack` package enables the following features:

```cs
[MessagePackObject]
public class Person
{
    [Key(0)]
    public string Name { get; set; } = "";

    [Key(1)]
    public int Age { get; set; }
}
```

``` cs
// Create MessagePack value table...
using VKV;
using VKV.MessagePack;

var databaseBuilder = new DatabaseBuilder();

var tableBuilder = builder.CreateTable("items", KeyEncoding.Ascii)
    .AsMessagePackSerializable<Person>();

// Add MessagePack serialized values...
var tableBuilder.Append("key01", new Person { Name = "Bob", Age = 22 });
var tableBuilder.Append("key02", new Person { Name = "Tom", Age = 34 });

// Secondary index example
tableBuilder.AddSecondaryIndex("age", false, KeyEncoding.Int64LittleEndian, (key, person) =>
{
    return person.Age;
});

await builder.BuildToFileAsync("/path/to/db.vkv");
```

``` cs
// Load from messagepack values
using VKV;
using VKV.MessagePack;

using var database = await ReadOnlyDatabase.OpenAync("/path/to/db.vkv");
var table = database.GetTable("items")
    .AsMessagePackSerializable<Person>();
    
Person value = tabel.Get("key01"); //=> Person("Bob", 22)
```


### Unity


```cs
// The page cache will use the unity native allocator.
var database = await ReadOnlyDatabase.OpenFromFileAsync(filePath, new DatabaseLoadOptions
{
    StorageFactory = UnityNativeAllocatorFileStorage.Factory,
});
```

### Cli tool

```bash
$ dotnet tool install vkv.cli --prerelease
```

After install, specify the DB file and start an interactive session.

```bash
$ dotnet vkv --file ./sample.vkv
```

<img src="./demo_cli.gif" alt="CLI Demo" width="50%" />


During an interactive session, the following commands are available.

| Command | Description |
|---------|-------------|
| get <key> | Get value by key |
| scan [offset] [limit] | Scan key-value entries (default: offset=0, limit=20) |
| keys [offset] [limit] | Scan keys only |
| values [offset] [limit] | Scan values only |
| count | Count all entries |
| tables | List all tables |
| use <table> | Switch to another table |
| info | Show database info |
| help | Show this help |
| quit | Exit the session |




## Binary Format

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              .vkv File Format                               │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                        Header (14 bytes)                              │  │
│  ├───────────┬───────────┬───────────┬───────────────┬──────────────────┤  │
│  │ MagicBytes│  Version  │FilterCount│   PageSize    │   TableCount     │  │
│  │  "VKV\0"  │Major|Minor│  ushort   │     int       │     ushort       │  │
│  │  4 bytes  │ 1b  | 1b  │  2 bytes  │    4 bytes    │     2 bytes      │  │
│  └───────────┴───────────┴───────────┴───────────────┴──────────────────┘  │
│                                    │                                        │
│                                    ▼                                        │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                   PageFilter[FilterCount]                             │  │
│  ├───────────────────────────────────────────────────────────────────────┤  │
│  │  ┌─────────────┬─────────────────────────┐                            │  │
│  │  │ NameLength  │        Name (UTF-8)     │  × FilterCount             │  │
│  │  │   1 byte    │      variable bytes     │                            │  │
│  │  └─────────────┴─────────────────────────┘                            │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                    │                                        │
│                                    ▼                                        │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                      Table[TableCount]                                │  │
│  ├───────────────────────────────────────────────────────────────────────┤  │
│  │  ┌─────────────┬─────────────────┬─────────────────┬────────────────┐ │  │
│  │  │ NameLength  │  Name (UTF-8)   │  PrimaryIndex   │ SecondaryIndex │ │  │
│  │  │   4 bytes   │ variable bytes  │   Descriptor    │  Descriptors   │ │  │
│  │  └─────────────┴─────────────────┴─────────────────┴────────────────┘ │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                    │                                        │
│                                    ▼                                        │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                           B+Tree Pages                                │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                          Index Descriptor                                   │
├───────────┬───────────┬──────────┬────────────┬────────┬────────┬──────────┤
│NameLength│EncodingLen│   Name   │ EncodingId │IsUnique│ValueKnd│RootPosion│
│  ushort  │  ushort   │  UTF-8   │   UTF-8    │  bool  │  enum  │   long   │
│  2 bytes │  2 bytes  │ variable │  variable  │ 1 byte │ 1 byte │  8 bytes │
└───────────┴───────────┴──────────┴────────────┴────────┴────────┴──────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                             Page Structure                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                       Page Header (28 bytes)                        │    │
│  ├───────────┬───────────┬────────────┬──────────────┬────────────────┤    │
│  │ PageSize  │   Kind    │ EntryCount │ LeftSibling  │  RightSibling  │    │
│  │    int    │   enum    │    int     │    long      │     long       │    │
│  │  4 bytes  │  4 bytes  │  4 bytes   │   8 bytes    │    8 bytes     │    │
│  └───────────┴───────────┴────────────┴──────────────┴────────────────┘    │
│                                    │                                        │
│       Kind = 0 (Leaf)              │              Kind = 1 (Internal)       │
│              │                     │                     │                  │
│              ▼                     │                     ▼                  │
│  ┌───────────────────────┐         │        ┌───────────────────────┐       │
│  │ EntryMeta[EntryCount] │         │        │ EntryMeta[EntryCount] │       │
│  ├───────────────────────┤         │        ├───────────────────────┤       │
│  │ PageOffset │  4 bytes │         │        │ PageOffset │  4 bytes │       │
│  │ KeyLength  │  2 bytes │         │        │ KeyLength  │  2 bytes │       │
│  │ ValueLength│  2 bytes │         │        └───────────────────────┘       │
│  └───────────────────────┘         │                     │                  │
│              │                     │                     ▼                  │
│              ▼                     │        ┌───────────────────────┐       │
│  ┌───────────────────────┐         │        │  Entry[EntryCount]    │       │
│  │  Entry[EntryCount]    │         │        ├───────────────────────┤       │
│  ├───────────────────────┤         │        │    Key   │  variable  │       │
│  │    Key   │  variable  │         │        │ ChildPtr │   8 bytes  │       │
│  │   Value  │  variable  │         │        └───────────────────────┘       │
│  └───────────────────────┘         │                                        │
│                                    │                                        │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                            B+Tree Structure                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│                           ┌─────────────┐                                   │
│                           │  Internal   │                                   │
│                           │   (Root)    │                                   │
│                           └──────┬──────┘                                   │
│                     ┌────────────┼────────────┐                             │
│                     ▼            ▼            ▼                             │
│              ┌──────────┐ ┌──────────┐ ┌──────────┐                         │
│              │ Internal │ │ Internal │ │ Internal │                         │
│              └────┬─────┘ └────┬─────┘ └────┬─────┘                         │
│                   │            │            │                               │
│          ┌────────┴────────┐   │   ┌────────┴────────┐                      │
│          ▼                 ▼   ▼   ▼                 ▼                      │
│     ┌────────┐        ┌────────┬────────┐       ┌────────┐                  │
│     │  Leaf  │◄──────►│  Leaf  │  Leaf  │◄─────►│  Leaf  │                  │
│     │ k1:v1  │        │ k2:v2  │ k3:v3  │       │ k4:v4  │                  │
│     │  ...   │        │  ...   │  ...   │       │  ...   │                  │
│     └────────┘        └────────┴────────┘       └────────┘                  │
│         ▲                                            ▲                      │
│         │         Left/Right Sibling Links           │                      │
│         └────────────────────────────────────────────┘                      │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

## LICENSE

MIT

## Author

[@hadashiA](https://x.com/hadashiA)

