using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using NUnit.Framework;
using DryDB.Compression;
using DryDB.Unity;

namespace DryDB.Tests
{
    [TestFixture]
    public class UnityNativeAllocatorFIleStorageTest
    {
        [Test]
        public async Task LoadMultiplePages()
        {
            var filePath = Path.Join(Application.temporaryCachePath, "drydb_test.db");
            var builder = new DatabaseBuilder
            {
                PageSize = 128
            };

            var tableBuilder = builder.CreateTable("items");
            tableBuilder.Append(Encoding.UTF8.GetBytes("key01"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key02"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key03"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key04"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key05"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key06"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key07"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key08"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key09"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key10"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key11"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key12"), Encoding.UTF8.GetBytes("value01"));

            await builder.BuildToFileAsync(filePath);

            var database = await ReadOnlyDatabase.OpenFileAsync(filePath, new DatabaseLoadOptions
            {
                CacheSize = 2,
                StorageFactory = UnityNativeAllocatorPageLoader.Factory,
            });
            var table  = database.GetTable("items");
            using var result = await table.GetRangeAsync(
                Encoding.UTF8.GetBytes("key03"),
                Encoding.UTF8.GetBytes("key10"));
            Assert.That(result.Count, Is.EqualTo(8));
        }

        [Test]
        public async Task LoadCompressedPages()
        {
            var filePath = Path.Join(Application.temporaryCachePath, "drydb_test.db");
            var builder = new DatabaseBuilder
            {
                PageSize = 128,
            };
            builder.AddPageFilter(x =>
            {
                x.AddZstandardCompression();
            });

            var tableBuilder = builder.CreateTable("items");
            tableBuilder.Append(Encoding.UTF8.GetBytes("key01"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key02"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key03"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key04"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key05"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key06"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key07"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key08"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key09"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key10"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key11"), Encoding.UTF8.GetBytes("value01"));
            tableBuilder.Append(Encoding.UTF8.GetBytes("key12"), Encoding.UTF8.GetBytes("value01"));

            await builder.BuildToFileAsync(filePath);

            var database = await ReadOnlyDatabase.OpenFileAsync(filePath, new DatabaseLoadOptions
            {
                CacheSize = 2,
                StorageFactory = UnityNativeAllocatorPageLoader.Factory,
            });
            var table  = database.GetTable("items");
            using var result = await table.GetRangeAsync(
                Encoding.UTF8.GetBytes("key03"),
                Encoding.UTF8.GetBytes("key10"));
            Assert.That(result.Count, Is.EqualTo(8));
        }
    }
}