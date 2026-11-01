using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace MM2.Archive
{
    /// <summary>
    /// Runtime Virtual File System that mounts Midtown Madness 2 .ar archives
    /// from the game's "resources" folder.
    /// Supports:
    ///   1. Angel Studios DAVE archives (mm2core.ar, mm2tex.ar, mm2aud.ar, mm2audex.ar)
    ///   2. Standard PKZIP .ar mod archives (!2003 Ferrari Enzo.ar, !!!!city_as_manhattan-051220.ar, copny.ar)
    ///   3. AR-Locked / Stripped-Local-Header PKZIP .ar mod archives (!clk55amg.ar)
    /// </summary>
    public static class MM2ArchiveSystem
    {
        public struct ArchiveEntry
        {
            public string ArchiveFilePath;
            public string VirtualPath;
            public long DataOffset;
            public int CompressedSize;
            public int UncompressedSize;
            public bool IsDeflated;
            public int ArchiveType; // 0 = DAVE (MM2), 1 = ARES (MM1), 2 = PKZIP (Mod)
        }

        private static bool isInitialized;
        private static string resourcesFolderPath;
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        // Case-insensitive virtual path -> ArchiveEntry (later archives override earlier ones)
        private static readonly Dictionary<string, ArchiveEntry> Entries =
            new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        // Fast lookup indices by filename/stem
        private static readonly Dictionary<string, string> TextureByStem =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> PkgByStem =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> WavByStem =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<string>> BmsFilesByDir =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public static string ResourcesFolderPath
        {
            get
            {
                EnsureInitialized();
                return resourcesFolderPath;
            }
        }

        public static int TotalMountedFiles
        {
            get
            {
                EnsureInitialized();
                return Entries.Count;
            }
        }

        public static void EnsureInitialized()
        {
            if (isInitialized) return;
            Initialize();
        }

        public static void Initialize()
        {
            isInitialized = true;
            Entries.Clear();
            TextureByStem.Clear();
            PkgByStem.Clear();
            WavByStem.Clear();
            BmsFilesByDir.Clear();

            resourcesFolderPath = ResolveResourcesDirectory();
            if (string.IsNullOrEmpty(resourcesFolderPath) || !Directory.Exists(resourcesFolderPath))
            {
                Debug.LogWarning($"[MM2ArchiveSystem] 'resources' directory not found at '{resourcesFolderPath}'.");
                return;
            }

            string[] allArFiles = Directory.GetFiles(resourcesFolderPath, "*.ar", SearchOption.TopDirectoryOnly);
            if (allArFiles.Length == 0)
            {
                Debug.LogWarning($"[MM2ArchiveSystem] No .ar files found in '{resourcesFolderPath}'.");
                return;
            }

            // Classify archives by binary container header (ARES, DAVE, or PKZIP mod) rather than archive filename.
            // ARES (MM1) mounts first, DAVE (MM2) second, and PKZIP mod archives last.
            var aresArchives = new List<string>();
            var daveArchives = new List<string>();
            var modArchives = new List<string>();

            foreach (string path in allArFiles)
            {
                string magic = PeekArchiveMagic(path);
                if (magic == "ARES")
                {
                    aresArchives.Add(path);
                }
                else if (magic == "DAVE")
                {
                    daveArchives.Add(path);
                }
                else
                {
                    modArchives.Add(path);
                }
            }

            aresArchives.Sort(StringComparer.OrdinalIgnoreCase);
            daveArchives.Sort(StringComparer.OrdinalIgnoreCase);
            modArchives.Sort(StringComparer.OrdinalIgnoreCase);

            var orderedArchives = new List<string>(aresArchives.Count + daveArchives.Count + modArchives.Count);
            orderedArchives.AddRange(aresArchives);
            orderedArchives.AddRange(daveArchives);
            orderedArchives.AddRange(modArchives);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int mountedArchives = 0;

            foreach (string arPath in orderedArchives)
            {
                try
                {
                    if (MountArchive(arPath))
                    {
                        mountedArchives++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[MM2ArchiveSystem] Failed to mount '{Path.GetFileName(arPath)}': {ex.Message}");
                }
            }

            sw.Stop();
            Debug.Log($"[MM2ArchiveSystem] Mounted {mountedArchives} .ar archives ({Entries.Count:N0} virtual files) from '{resourcesFolderPath}' in {sw.ElapsedMilliseconds} ms.");
        }

        private static string ResolveResourcesDirectory()
        {
            // 1. Check <ProjectRoot>/resources (Editor) or <BuildFolder>/resources (Standalone Player)
            DirectoryInfo parentDir = Directory.GetParent(Application.dataPath);
            if (parentDir != null)
            {
                string candidate = Path.Combine(parentDir.FullName, "resources");
                if (Directory.Exists(candidate)) return candidate;
            }

            // 2. Check <Application.dataPath>/resources
            string dataCandidate = Path.Combine(Application.dataPath, "resources");
            if (Directory.Exists(dataCandidate)) return dataCandidate;

            // 3. Check StreamingAssets/resources
            string streamingCandidate = Path.Combine(Application.streamingAssetsPath, "resources");
            if (Directory.Exists(streamingCandidate)) return streamingCandidate;

            // Default fallback
            return parentDir != null ? Path.Combine(parentDir.FullName, "resources") : dataCandidate;
        }

        private static string PeekArchiveMagic(string arPath)
        {
            try
            {
                using var fs = new FileStream(arPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < 16) return string.Empty;
                byte[] magic = new byte[4];
                if (fs.Read(magic, 0, 4) != 4) return string.Empty;
                return Latin1.GetString(magic, 0, 4);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool MountArchive(string arPath)
        {
            using var fs = new FileStream(arPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 16) return false;

            byte[] magic = new byte[4];
            if (fs.Read(magic, 0, 4) != 4) return false;

            if (magic[0] == 'D' && magic[1] == 'A' && magic[2] == 'V' && magic[3] == 'E')
            {
                return MountDaveArchive(fs, arPath);
            }

            if (magic[0] == 'A' && magic[1] == 'R' && magic[2] == 'E' && magic[3] == 'S')
            {
                return MountAresArchive(fs, arPath);
            }

            if (magic[0] == 'P' && magic[1] == 'K')
            {
                return MountZipArchive(fs, arPath);
            }

            // Check if it has a ZIP End of Central Directory at the end even if magic was modified
            return MountZipArchive(fs, arPath);
        }

        private static bool MountAresArchive(FileStream fs, string arPath)
        {
            using var br = new BinaryReader(fs, Latin1, leaveOpen: true);
            fs.Seek(4, SeekOrigin.Begin);
            uint numEntries = br.ReadUInt32();
            uint numRootDirs = br.ReadUInt32();
            uint strTableSize = br.ReadUInt32();

            if (numEntries == 0 || numEntries > 500000 || numRootDirs > numEntries) return false;

            long entryTableBytes = (long)numEntries * 12L;
            if (16L + entryTableBytes + strTableSize > fs.Length) return false;

            byte[] entryBuf = br.ReadBytes((int)entryTableBytes);
            byte[] strTable = br.ReadBytes((int)strTableSize);

            string ReadAresString(uint offset, uint subValue)
            {
                if (offset >= strTable.Length) return string.Empty;
                int end = Array.IndexOf(strTable, (byte)0, (int)offset);
                if (end < 0) end = strTable.Length;
                int len = end - (int)offset;
                if (len <= 0) return string.Empty;

                string s = Latin1.GetString(strTable, (int)offset, len);
                if (s.IndexOf('\x01') >= 0)
                {
                    s = s.Replace("\x01", subValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                return s;
            }

            void WalkAresEntry(uint idx, string prefix)
            {
                if (idx >= numEntries) return;
                int baseOff = (int)(idx * 12);
                uint a = BitConverter.ToUInt32(entryBuf, baseOff);
                uint b = BitConverter.ToUInt32(entryBuf, baseOff + 4);
                uint c = BitConverter.ToUInt32(entryBuf, baseOff + 8);

                bool isDir = (c & 1u) != 0;
                uint numSub = (c >> 1) & 0x1FFFu;
                uint nameOff = c >> 14;
                string name = ReadAresString(nameOff, numSub);

                if (isDir)
                {
                    string nextPrefix = string.IsNullOrEmpty(name) ? prefix : $"{prefix}{name}/";
                    for (uint i = 0; i < b; i++)
                    {
                        WalkAresEntry(a + i, nextPrefix);
                    }
                }
                else
                {
                    uint fileSize = b & 0x7FFFFFu;
                    uint extOff = b >> 23;
                    string ext = ReadAresString(extOff, 0);
                    string rawPath = string.IsNullOrEmpty(ext) ? $"{prefix}{name}" : $"{prefix}{name}.{ext}";
                    string vPath = NormalizeVirtualPath(rawPath);
                    if (string.IsNullOrEmpty(vPath) || vPath.EndsWith("/")) return;

                    RegisterEntry(new ArchiveEntry
                    {
                        ArchiveFilePath = arPath,
                        VirtualPath = vPath,
                        DataOffset = a,
                        CompressedSize = (int)fileSize,
                        UncompressedSize = (int)fileSize,
                        IsDeflated = false,
                        ArchiveType = 1 // ARES (MM1)
                    });
                }
            }

            for (uint r = 0; r < numRootDirs; r++)
            {
                WalkAresEntry(r, string.Empty);
            }

            return true;
        }

        private static bool MountDaveArchive(FileStream fs, string arPath)
        {
            using var br = new BinaryReader(fs, Latin1, leaveOpen: true);
            fs.Seek(4, SeekOrigin.Begin);
            uint numFiles = br.ReadUInt32();
            if (numFiles == 0 || numFiles > 500000) return false;

            const long mftPos = 0x800;
            long mftSize = ((long)numFiles * 16 + 0x7FF) & ~0x7FFL;
            long fnTablePos = mftPos + mftSize;

            if (fs.Length < fnTablePos) return false;

            fs.Seek(mftPos, SeekOrigin.Begin);
            byte[] mftBytes = br.ReadBytes((int)(numFiles * 16));

            uint firstFileOffset = BitConverter.ToUInt32(mftBytes, 4);
            int fnTableSize = (int)Math.Max(0, Math.Min(fs.Length - fnTablePos, firstFileOffset - fnTablePos));

            fs.Seek(fnTablePos, SeekOrigin.Begin);
            byte[] fnBytes = br.ReadBytes(fnTableSize);

            for (int i = 0; i < numFiles; i++)
            {
                int baseOff = i * 16;
                uint fnOff = BitConverter.ToUInt32(mftBytes, baseOff);
                uint fileOff = BitConverter.ToUInt32(mftBytes, baseOff + 4);
                uint realSize = BitConverter.ToUInt32(mftBytes, baseOff + 8);
                uint arSize = BitConverter.ToUInt32(mftBytes, baseOff + 12);

                if (fnOff >= fnBytes.Length) continue;
                int endIdx = Array.IndexOf(fnBytes, (byte)0, (int)fnOff);
                if (endIdx < 0) endIdx = fnBytes.Length;

                string rawName = Latin1.GetString(fnBytes, (int)fnOff, endIdx - (int)fnOff);
                string vPath = NormalizeVirtualPath(rawName);
                if (string.IsNullOrEmpty(vPath) || vPath.EndsWith("/")) continue;

                RegisterEntry(new ArchiveEntry
                {
                    ArchiveFilePath = arPath,
                    VirtualPath = vPath,
                    DataOffset = fileOff,
                    CompressedSize = (int)arSize,
                    UncompressedSize = (int)realSize,
                    IsDeflated = (realSize != arSize),
                    ArchiveType = 0 // DAVE (MM2)
                });
            }

            return true;
        }

        private static bool MountZipArchive(FileStream fs, string arPath)
        {
            long fileLen = fs.Length;
            int tailSearchSize = (int)Math.Min(fileLen, 65557L);
            fs.Seek(fileLen - tailSearchSize, SeekOrigin.Begin);
            byte[] tail = new byte[tailSearchSize];
            int read = fs.Read(tail, 0, tailSearchSize);

            int eocdPos = -1;
            for (int i = read - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                {
                    eocdPos = i;
                    break;
                }
            }

            if (eocdPos < 0) return false;

            uint cdSize = BitConverter.ToUInt32(tail, eocdPos + 12);
            uint cdOffset = BitConverter.ToUInt32(tail, eocdPos + 16);

            if (cdOffset + cdSize > fileLen) return false;

            fs.Seek(cdOffset, SeekOrigin.Begin);
            byte[] cdBytes = new byte[cdSize];
            if (fs.Read(cdBytes, 0, (int)cdSize) != (int)cdSize) return false;

            byte[] localSigBuf = new byte[30];
            int pos = 0;

            while (pos + 46 <= cdBytes.Length)
            {
                if (cdBytes[pos] != 0x50 || cdBytes[pos + 1] != 0x4B || cdBytes[pos + 2] != 0x01 || cdBytes[pos + 3] != 0x02)
                {
                    break;
                }

                ushort method = BitConverter.ToUInt16(cdBytes, pos + 10);
                uint compSize = BitConverter.ToUInt32(cdBytes, pos + 20);
                uint uncompSize = BitConverter.ToUInt32(cdBytes, pos + 24);
                ushort fnLen = BitConverter.ToUInt16(cdBytes, pos + 28);
                ushort extraLen = BitConverter.ToUInt16(cdBytes, pos + 30);
                ushort commentLen = BitConverter.ToUInt16(cdBytes, pos + 32);
                uint localHdrOff = BitConverter.ToUInt32(cdBytes, pos + 42);

                string rawName = Latin1.GetString(cdBytes, pos + 46, fnLen);
                pos += 46 + fnLen + extraLen + commentLen;

                if (uncompSize == 0) continue;

                string vPath = NormalizeVirtualPath(rawName);
                if (string.IsNullOrEmpty(vPath) || vPath.EndsWith("/")) continue;

                // Determine payload offset:
                // Standard ZIP has PK\x03\x04 at localHdrOff; AR-locked ZIPs strip local headers so
                // data starts directly at localHdrOff + 30 + fnLen.
                long dataOffset = localHdrOff + 30L + fnLen;
                if (localHdrOff + 30L <= fileLen)
                {
                    fs.Seek(localHdrOff, SeekOrigin.Begin);
                    if (fs.Read(localSigBuf, 0, 30) == 30 &&
                        localSigBuf[0] == 0x50 && localSigBuf[1] == 0x4B &&
                        localSigBuf[2] == 0x03 && localSigBuf[3] == 0x04)
                    {
                        ushort localFnLen = BitConverter.ToUInt16(localSigBuf, 26);
                        ushort localExtraLen = BitConverter.ToUInt16(localSigBuf, 28);
                        dataOffset = localHdrOff + 30L + localFnLen + localExtraLen;
                    }
                }

                RegisterEntry(new ArchiveEntry
                {
                    ArchiveFilePath = arPath,
                    VirtualPath = vPath,
                    DataOffset = dataOffset,
                    CompressedSize = (int)compSize,
                    UncompressedSize = (int)uncompSize,
                    IsDeflated = (method == 8),
                    ArchiveType = 2 // PKZIP (Mod)
                });
            }

            return true;
        }

        private static void RegisterEntry(ArchiveEntry entry)
        {
            string vPath = entry.VirtualPath;
            Entries[vPath] = entry;

            string ext = Path.GetExtension(vPath).ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(vPath);
            if (string.IsNullOrEmpty(stem)) return;

            if (ext == ".tex" || ext == ".tga" || ext == ".bmp")
            {
                if (vPath.StartsWith("texture/", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer .tga over .tex if a mod supplies higher-res TGA textures
                    if (!TextureByStem.TryGetValue(stem, out string existing) ||
                        ext == ".tga" ||
                        !existing.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    {
                        TextureByStem[stem] = vPath;
                    }
                }
            }
            else if (ext == ".dds")
            {
                // MM1 textures live in tex16a/, tex16o/, bmp16/, and texp/.
                // Never overwrite an MM2 .tex/.tga texture, and prefer 16-bit tex16a/tex16o over 8-bit palettized texp/.
                bool isPalettized = vPath.StartsWith("texp/", StringComparison.OrdinalIgnoreCase);
                if (!TextureByStem.TryGetValue(stem, out string existing))
                {
                    TextureByStem[stem] = vPath;
                }
                else if (!isPalettized && existing.StartsWith("texp/", StringComparison.OrdinalIgnoreCase))
                {
                    TextureByStem[stem] = vPath;
                }
            }
            else if (ext == ".pkg")
            {
                if (vPath.StartsWith("geometry/", StringComparison.OrdinalIgnoreCase))
                {
                    PkgByStem[stem] = vPath;
                }
            }
            else if (ext == ".bms")
            {
                if (vPath.StartsWith("bms/", StringComparison.OrdinalIgnoreCase))
                {
                    int secondSlash = vPath.IndexOf('/', 4);
                    if (secondSlash > 4)
                    {
                        string dirName = vPath.Substring(4, secondSlash - 4);
                        if (!BmsFilesByDir.TryGetValue(dirName, out List<string> list))
                        {
                            list = new List<string>();
                            BmsFilesByDir[dirName] = list;
                        }
                        if (!list.Contains(vPath))
                        {
                            list.Add(vPath);
                        }
                    }
                }
            }
            else if (ext == ".wav")
            {
                WavByStem[stem] = vPath;
                // Also index without .11k / .22k suffix (e.g. "mustangidle.11k" -> "mustangidle")
                int dotIdx = stem.IndexOf('.');
                if (dotIdx > 0)
                {
                    string baseWaveName = stem.Substring(0, dotIdx);
                    if (!WavByStem.ContainsKey(baseWaveName) || stem.EndsWith(".22k", StringComparison.OrdinalIgnoreCase))
                    {
                        WavByStem[baseWaveName] = vPath;
                    }
                }
            }
        }

        public static string NormalizeVirtualPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string s = path.Replace('\\', '/').Trim();
            while (s.StartsWith("/")) s = s.Substring(1);
            return s.ToLowerInvariant();
        }

        public static bool FileExists(string virtualPath)
        {
            EnsureInitialized();
            return Entries.ContainsKey(NormalizeVirtualPath(virtualPath));
        }

        public static int GetFileArchiveType(string virtualPath)
        {
            EnsureInitialized();
            string key = NormalizeVirtualPath(virtualPath);
            return Entries.TryGetValue(key, out ArchiveEntry entry) ? entry.ArchiveType : -1;
        }

        public static byte[] ReadFileBytes(string virtualPath)
        {
            EnsureInitialized();
            string key = NormalizeVirtualPath(virtualPath);
            if (!Entries.TryGetValue(key, out ArchiveEntry entry))
            {
                return null;
            }

            try
            {
                using var fs = new FileStream(entry.ArchiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(entry.DataOffset, SeekOrigin.Begin);
                byte[] compBuf = new byte[entry.CompressedSize];
                int totalRead = 0;
                while (totalRead < entry.CompressedSize)
                {
                    int r = fs.Read(compBuf, totalRead, entry.CompressedSize - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                }

                if (!entry.IsDeflated)
                {
                    return compBuf;
                }

                byte[] outBuf = new byte[entry.UncompressedSize];
                using var ms = new MemoryStream(compBuf);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                int decompRead = 0;
                while (decompRead < entry.UncompressedSize)
                {
                    int r = deflate.Read(outBuf, decompRead, entry.UncompressedSize - decompRead);
                    if (r <= 0) break;
                    decompRead += r;
                }
                return outBuf;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2ArchiveSystem] Error reading '{virtualPath}' from '{Path.GetFileName(entry.ArchiveFilePath)}': {ex.Message}");
                return null;
            }
        }

        public static string ReadFileText(string virtualPath)
        {
            byte[] bytes = ReadFileBytes(virtualPath);
            return bytes != null ? Latin1.GetString(bytes) : null;
        }

        public static List<string> GetFilesWithPrefixAndExtension(string dirPrefix, string extension)
        {
            EnsureInitialized();
            string normPrefix = NormalizeVirtualPath(dirPrefix);
            if (!string.IsNullOrEmpty(normPrefix) && !normPrefix.EndsWith("/"))
            {
                normPrefix += "/";
            }
            string normExt = extension.ToLowerInvariant();

            var results = new List<string>();
            foreach (string key in Entries.Keys)
            {
                if (key.StartsWith(normPrefix, StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(normExt, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(key);
                }
            }
            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        public static string FindTextureVirtualPath(string texName)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(texName)) return null;

            string clean = texName.Trim();
            int extDot = clean.LastIndexOf('.');
            if (extDot > 0) clean = clean.Substring(0, extDot);

            if (TextureByStem.TryGetValue(clean, out string path)) return path;

            // Try stripping frame suffix after '-' (e.g. "road-0001") or adding "-0001"
            int dashIdx = clean.IndexOf('-');
            if (dashIdx > 0)
            {
                string beforeDash = clean.Substring(0, dashIdx);
                if (TextureByStem.TryGetValue(beforeDash, out path)) return path;
            }
            if (TextureByStem.TryGetValue(clean + "-0001", out path)) return path;

            // Fallback suffix variants (_tp vs _tp1)
            if (clean.EndsWith("_tp", StringComparison.OrdinalIgnoreCase) &&
                TextureByStem.TryGetValue(clean + "1", out path))
            {
                return path;
            }

            return null;
        }

        public static string FindPkgVirtualPath(string pkgName)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(pkgName)) return null;
            string clean = Path.GetFileNameWithoutExtension(pkgName.Trim());
            return PkgByStem.TryGetValue(clean, out string path) ? path : null;
        }

        public static bool HasBmsDirectory(string dirName)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(dirName)) return false;
            string clean = Path.GetFileNameWithoutExtension(dirName.Trim());
            return BmsFilesByDir.TryGetValue(clean, out List<string> list) && list != null && list.Count > 0;
        }

        public static IReadOnlyList<string> GetBmsFilesInDirectory(string dirName)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(dirName)) return Array.Empty<string>();
            string clean = Path.GetFileNameWithoutExtension(dirName.Trim());
            return BmsFilesByDir.TryGetValue(clean, out List<string> list) && list != null
                ? list
                : (IReadOnlyList<string>)Array.Empty<string>();
        }

        public static string FindBmsVirtualPath(string dirName, string partName)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(dirName) || string.IsNullOrEmpty(partName)) return null;
            string cleanDir = Path.GetFileNameWithoutExtension(dirName.Trim());
            string cleanPart = Path.GetFileNameWithoutExtension(partName.Trim());
            string candidate = NormalizeVirtualPath($"bms/{cleanDir}/{cleanPart}.bms");
            return Entries.ContainsKey(candidate) ? candidate : null;
        }

        public static string FindWavVirtualPath(string waveName)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(waveName)) return null;
            string clean = waveName.Trim();
            if (clean.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(0, clean.Length - 4);
            }
            if (WavByStem.TryGetValue(clean, out string path)) return path;

            // Handle double-dot typos in base game CSVs (e.g. "AustinHigh..11k")
            if (WavByStem.TryGetValue(clean + ".", out path)) return path;

            return null;
        }
    }
}
