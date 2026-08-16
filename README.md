# DryDB

> [!NOTE]
> This project was formerly known as **VKV** and has been renamed to **DryDB**.

DryDB is a read-only embedded B+Tree based key/value database, implemented pure C#.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/point_lookup_dark.svg">
  <img alt="Point lookup benchmark: DryDB 17 ns, RocksDB 383 ns, SQLite (prepared + immutable) 552 ns, SQLite (default) 4,287 ns per query" src="docs/benchmarks/point_lookup_light.svg">
</picture>

See [Performance](#performance) for details.

## Features

- B+Tree based query
  - Read a value by primary key 
  - Read values by key range
  - Read values by key prefix 
  - Count by key range
  - Secondary index
    - unique
    - non-unique
- Multiple Tables
- Support for both async and sync
- Sort by asc/desc
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
- Support for large BLOBs. (Values exceeding 65,536 bytes are stored on a separated page.)

## Performance

All benchmarks read a table of 10,000 rows (int64 primary key, 13-byte value) with 4 KB pages, on Apple M-series / .NET 10, measured with BenchmarkDotNet. Lower is better.

Two SQLite configurations are shown to keep the comparison fair:

- **default** — a command is created and parsed for every query (typical naive usage)
- **prepared + immutable** — the statement is prepared once and reused, and the file is opened with [`immutable=1`](https://www.sqlite.org/uri.html#uriimmutable), the fastest read-only setup SQLite offers

### Point lookup

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/point_lookup_dark.svg">
  <img alt="Point lookup benchmark: DryDB 17 ns, RocksDB 383 ns, SQLite (prepared + immutable) 552 ns, SQLite (default) 4,287 ns per query" src="docs/benchmarks/point_lookup_light.svg">
</picture>

### Range scan

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/range_scan_dark.svg">
  <img alt="Range scan benchmark (100 rows): DryDB 0.4 µs, SQLite (prepared + immutable) 5.7 µs, SQLite (default) 10 µs, RocksDB 10.1 µs per query" src="docs/benchmarks/range_scan_light.svg">
</picture>

### Count by key range

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/count_range_dark.svg">
  <img alt="Count benchmark (8,000 rows): DryDB 1.0 µs, SQLite (prepared + immutable) 102 µs, SQLite (default) 110 µs, RocksDB 713 µs per query" src="docs/benchmarks/count_range_light.svg">
</picture>

<details>
<summary>Raw BenchmarkDotNet results</summary>

Each benchmark class is measured in its own run. 1 op = 1000 queries for point lookup, 100 queries for range scan and count. The `Parallel` variants run 8 threads of 1000 queries each: `ParallelSpread` reads spread-out keys (the realistic shape), `Parallel` hammers a single key (worst case for page refcount contention). `RandomKeys` reads a pseudo-random key sequence, though the same 1,000-key sequence repeats every op, so a large branch predictor gradually learns it; `RandomKeys_NoRepeat` carries the seed across ops so the sequence never repeats — the closest model of real random access. `OpenAndFirstRead` opens the database file and runs one query. The `1M` variants read a 1,000,000-row (~45 MB) table where the working set no longer fits in the CPU cache.

| Type             | Method                              | Mean         | Error        | StdDev       | Allocated  |
|----------------- |------------------------------------ |-------------:|-------------:|-------------:|-----------:|
| ReadBenchmark    | DryDB_FindByKey                     |     17.06 us |     0.329 us |     0.196 us |          - |
| ReadBenchmark    | DryDB_FindByKeyAsync                |     20.18 us |     0.132 us |     0.078 us |          - |
| ReadBenchmark    | DryDB_FindByKey_RandomKeys          |     30.85 us |     0.285 us |     0.169 us |          - |
| ReadBenchmark    | DryDB_FindByKey_RandomKeys_NoRepeat |     65.87 us |     0.348 us |     0.182 us |          - |
| ReadBenchmark    | DryDB_FindByKey_ParallelSpread      |    129.88 us |     1.874 us |     1.115 us |     1551 B |
| ReadBenchmark    | DryDB_FindByKey_Parallel            |    613.26 us |     4.271 us |     2.541 us |     1296 B |
| ReadBenchmark    | RocksDB_FindByKey                   |    383.24 us |     4.271 us |     2.825 us |    40032 B |
| ReadBenchmark    | CsSqlite_FindByKey_Fair             |    552.49 us |     5.611 us |     3.712 us |    48000 B |
| ReadBenchmark    | CsSqlite_FindByKey                  |  4,286.52 us |    70.881 us |    42.180 us |    48000 B |
| RangeBenchmark   | DryDB_GetRange                      |     44.72 us |     0.725 us |     0.479 us |          - |
| RangeBenchmark   | CsSqlite_GetRange_Fair              |    574.20 us |    21.034 us |    12.517 us |   480000 B |
| RangeBenchmark   | CsSqlite_GetRange                   |  1,001.08 us |     9.494 us |     6.279 us |   480000 B |
| RangeBenchmark   | RocksDB_GetRange                    |  1,012.93 us |    44.708 us |    26.605 us |   726432 B |
| CountBenchmark   | DryDB_CountRange                    |    104.80 us |     3.180 us |     1.890 us |          - |
| CountBenchmark   | CsSqlite_CountRange_Fair            | 10,216.20 us |   252.840 us |   167.240 us |          - |
| CountBenchmark   | CsSqlite_CountRange                 | 11,014.20 us |   461.740 us |   305.410 us |          - |
| CountBenchmark   | RocksDB_CountRange                  | 71,326.60 us | 1,521.980 us |   905.710 us | 25606432 B |
| OpenBenchmark    | DryDB_OpenAndFirstRead              |    175.50 us |    15.630 us |    10.340 us |   652820 B |
| BigReadBenchmark | DryDB_FindByKey_1M                  |     56.27 us |     5.436 us |     3.235 us |          - |
| BigReadBenchmark | DryDB_FindByKey_1M_RandomKeys       |     98.11 us |     3.163 us |     1.654 us |          - |
| BigReadBenchmark | DryDB_GetRange_1M                   |     61.41 us |     5.439 us |     3.597 us |          - |

</details>

> [!NOTE]
> DryDB returns values as zero-copy slices of cached pages, so read paths allocate no managed memory.
> The RocksDB numbers go through the C# binding ([rocksdb-sharp](https://github.com/curiosity-ai/rocksdb-sharp)), whose iterator allocates a `byte[]` per key/value access — the count benchmark in particular is dominated by that binding overhead rather than the storage engine itself.

The benchmark source is in [sandbox/DryDB.Benchmark](sandbox/DryDB.Benchmark).

## Why read-only ?

## Installation

### NuGet

| Package         | Description                                            | Latest version                                                                                             |
|:----------------|:-------------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| DryDB             | Main package. Embedded key/value store implementation. | [![NuGet](https://img.shields.io/nuget/v/DryDB)](https://www.nuget.org/packages/DryDB)                         |
| DryDB.MessagePack | Plugin that handles value as MessagePack-Csharp.       | [![NuGet](https://img.shields.io/nuget/v/DryDB.MessagePack)](https://www.nuget.org/packages/DryDB.MessagePack) |
| DryDB.Compression | Plugin  for compressing binary data.                    | [![NuGet](https://img.shields.io/nuget/v/DryDB.Compression)](https://www.nuget.org/packages/DryDB.Compression) | 
| DryDB.UlidKey     | Plugin enabling the use of ulid as a key               | [![NuGet](https://img.shields.io/nuget/v/DryDB.UlidKey)](https://www.nuget.org/packages/DryDB.UlidKey) | 

### Unity

> [!NOTE]
> Requirements: Unity 2022.2 or later.

1. Install [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity).
2. Install the DryDB package and the optional plugins listed above using NuGetForUnity.
3. Open the Package Manager window by selecting Window > Package Manager, then click on [+] > Add package from git URL and enter the following URL:
    - ```
      https://github.com/hadashiA/DryDB.git?path=src/DryDB.Unity/Assets/DryDB#1.0.2
      ```

### Cli tool (optional)

We distribute the CLI tool as a dotnet tool.

```bash
$ dotnet tool install drydb.cli
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
await builder.BuildToFileAsync("/path/to/bin.drydb");
```

```cs
// Open DB
var database = await ReadOnlyDatabase.OpenAsync("/pth/to/bin.drydb", new DatabaseLoadOptions
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
await builder.BuildToFileAsync("/path/to/bin.drydb");
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


DryDB currently provides built-in serialization by the following libraries:

- [MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp)
- System.Text.Json (in progress)

#### DryDB.MessagePack

Installing the `DryDB.MessagePack` package enables the following features:

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
using DryDB;
using DryDB.MessagePack;

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

await builder.BuildToFileAsync("/path/to/db.drydb");
```

``` cs
// Load from messagepack values
using DryDB;
using DryDB.MessagePack;

using var database = await ReadOnlyDatabase.OpenAync("/path/to/db.drydb");
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
$ dotnet tool install drydb.cli --prerelease
```

After install, specify the DB file and start an interactive session.

```bash
$ dotnet drydb --file ./sample.drydb
```

<img src="./demo_cli.gif" alt="CLI Demo" width="50%" />


During an interactive session, the following commands are available.

| Command                 | Description |
|-------------------------|-------------|
| get <key>               | Get value by key |
| scan [offset] [limit]   | Scan key-value entries (default: offset=0, limit=20) |
| keys [offset] [limit]   | Scan keys only |
| values [offset] [limit] | Scan values only |
| prefix \<key\> [limit]  | Search by key prefix (default: limit=10) |
| count                   | Count all entries |
| tables                  | List all tables |
| use [table]             | Switch to another table |
| info                    | Show database info |
| help                    | Show this help |
| quit                    | Exit the session |

## Binary Format

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                             .drydb File Format                              │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                        Header (14 bytes)                              │  │
│  ├───────────┬───────────┬───────────┬───────────────┬───────────────────┤  │
│  │ MagicBytes│  Version  │FilterCount│   PageSize    │    TableCount     │  │
│  │  "DRY\0"  │Major|Minor│  ushort   │     int       │      ushort       │  │
│  │  4 bytes  │ 1b  | 1b  │  2 bytes  │    4 bytes    │      2 bytes      │  │
│  └───────────┴───────────┴───────────┴───────────────┴───────────────────┘  │
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
├────────────┬─────────────┬──────────┬────────────┬──────────┬──────────┬────────────┤
│ NameLength │ EncodingLen │   Name   │ EncodingId │ IsUnique │ ValueKnd │ RootPosion │
│   ushort   │   ushort    │  UTF-8   │   UTF-8    │   bool   │   enum   │    long    │
│  2 bytes   │  2 bytes    │ variable │  variable  │  1 byte  │  1 byte  │  8 bytes   │
└────────────┴─────────────┴──────────┴────────────┴──────────┴──────────┴────────────┘

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

