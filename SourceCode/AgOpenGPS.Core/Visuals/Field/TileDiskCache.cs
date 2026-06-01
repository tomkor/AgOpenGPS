using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgOpenGPS.Core.Visuals
{
    /// <summary>
    /// Best-effort on-disk cache for map tiles. All operations swallow IO errors:
    /// a broken cache must never break rendering. Files are stored extension-agnostic
    /// (.tile); decoding relies on Image.FromStream sniffing the format.
    /// </summary>
    public sealed class TileDiskCache
    {
        private readonly string _root;

        public TileDiskCache(string rootDirectory)
        {
            _root = string.IsNullOrWhiteSpace(rootDirectory) ? null : rootDirectory;
        }

        public bool IsEnabled => _root != null;

        public string GetFilePath(string source, int z, int x, int y, bool withParcels)
        {
            string name = withParcels ? $"{y}_p.tile" : $"{y}.tile";
            return Path.Combine(_root, source, z.ToString(), x.ToString(), name);
        }

        public byte[] TryRead(string source, int z, int x, int y, bool withParcels)
        {
            if (!IsEnabled) return null;
            try
            {
                string path = GetFilePath(source, z, x, y, withParcels);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch
            {
                return null;
            }
        }

        public void Write(string source, int z, int x, int y, bool withParcels, byte[] bytes)
        {
            if (!IsEnabled || bytes == null || bytes.Length == 0) return;
            try
            {
                string path = GetFilePath(source, z, x, y, withParcels);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
            }
            catch
            {
                // ignore: cache writes are best-effort
            }
        }

        /// <summary>Deletes oldest files (by last write time) until total size is under maxBytes.</summary>
        public void EvictToLimit(long maxBytes)
        {
            if (!IsEnabled) return;
            try
            {
                if (!Directory.Exists(_root)) return;

                List<FileInfo> files = new DirectoryInfo(_root)
                    .GetFiles("*.tile", SearchOption.AllDirectories)
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                long total = files.Sum(f => f.Length);
                foreach (FileInfo file in files)
                {
                    if (total <= maxBytes) break;
                    long len = file.Length;
                    try { file.Delete(); total -= len; } catch { }
                }
            }
            catch
            {
                // ignore: eviction is best-effort
            }
        }
    }
}
