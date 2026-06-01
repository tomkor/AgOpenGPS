using System;
using System.IO;
using AgOpenGPS.Core.Visuals;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests.Visuals
{
    [TestFixture]
    public class TileDiskCacheTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "AogTileCacheTests_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        [Test]
        public void Disabled_When_Root_Is_Null_Or_Empty()
        {
            var cache = new TileDiskCache(null);
            Assert.That(cache.IsEnabled, Is.False);
            cache.Write("GeoportalOrtho", 17, 1, 2, false, new byte[] { 1, 2, 3 });
            Assert.That(cache.TryRead("GeoportalOrtho", 17, 1, 2, false), Is.Null);
        }

        [Test]
        public void Write_Then_Read_Roundtrips_Bytes()
        {
            var cache = new TileDiskCache(_root);
            byte[] data = { 9, 8, 7, 6 };
            cache.Write("GeoportalOrtho", 17, 73671, 42396, false, data);

            byte[] read = cache.TryRead("GeoportalOrtho", 17, 73671, 42396, false);
            Assert.That(read, Is.EqualTo(data));
        }

        [Test]
        public void Parcels_Flag_Does_Not_Collide_With_Base()
        {
            var cache = new TileDiskCache(_root);
            cache.Write("GeoportalOrtho", 17, 1, 1, false, new byte[] { 1 });
            cache.Write("GeoportalOrtho", 17, 1, 1, true, new byte[] { 2 });

            Assert.That(cache.TryRead("GeoportalOrtho", 17, 1, 1, false), Is.EqualTo(new byte[] { 1 }));
            Assert.That(cache.TryRead("GeoportalOrtho", 17, 1, 1, true), Is.EqualTo(new byte[] { 2 }));
        }

        [Test]
        public void TryRead_Returns_Null_When_Absent()
        {
            var cache = new TileDiskCache(_root);
            Assert.That(cache.TryRead("EsriWorldImagery", 10, 5, 5, false), Is.Null);
        }

        [Test]
        public void EvictToLimit_Removes_Oldest_First()
        {
            var cache = new TileDiskCache(_root);
            // Each blob is 1000 bytes; write three with increasing timestamps.
            cache.Write("S", 1, 0, 0, false, new byte[1000]);
            string oldestPath = cache.GetFilePath("S", 1, 0, 0, false);
            File.SetLastWriteTimeUtc(oldestPath, DateTime.UtcNow.AddMinutes(-30));

            cache.Write("S", 1, 0, 1, false, new byte[1000]);
            File.SetLastWriteTimeUtc(cache.GetFilePath("S", 1, 0, 1, false), DateTime.UtcNow.AddMinutes(-20));

            cache.Write("S", 1, 0, 2, false, new byte[1000]);
            File.SetLastWriteTimeUtc(cache.GetFilePath("S", 1, 0, 2, false), DateTime.UtcNow.AddMinutes(-10));

            // Keep only ~2000 bytes -> oldest (0,0) must be deleted.
            cache.EvictToLimit(2000);

            Assert.That(cache.TryRead("S", 1, 0, 0, false), Is.Null, "oldest should be evicted");
            Assert.That(cache.TryRead("S", 1, 0, 1, false), Is.Not.Null);
            Assert.That(cache.TryRead("S", 1, 0, 2, false), Is.Not.Null);
        }
    }
}
