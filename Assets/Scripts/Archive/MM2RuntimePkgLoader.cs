using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace MM2.Archive
{
    public class MM2RuntimePaintjobBinder : MonoBehaviour
    {
        public const int DamageZoneNone = 0;
        public const int DamageZoneFront = 1 << 0;
        public const int DamageZoneRear = 1 << 1;
        public const int DamageZoneSide = 1 << 2;
        public const int DamageZoneTopOrGeneral = 1 << 3;
        public const int DamageZoneAll = DamageZoneFront | DamageZoneRear | DamageZoneSide | DamageZoneTopOrGeneral;

        public MM2RuntimePkgLoader.ParsedPkg PkgData;
        public int[] SubmeshShaderOffsets;
        public bool RemapDamageTextures;
        public bool IsLightChunk;
        public int CurrentPaintjobIndex;
        public int DamageZoneMask;
    }

    /// <summary>
    /// Runtime parser and Unity Mesh/Material builder for Angel Studios PKG3 (.pkg) and (.mtx) files.
    /// Supports both base game vehicles/props and high-polygon community mod vehicles.
    /// </summary>
    public static class MM2RuntimePkgLoader
    {
        public struct PkgShaderEntry
        {
            public string TextureName;
            public Color32 DiffuseColor;
            public Color32 SpecularColor;
            public Color32 EmissiveColor;
            public float Shininess;
        }

        public class PkgSection
        {
            public int ShaderOffset;
            public List<Vector3> Positions = new List<Vector3>();
            public List<Vector3> Normals = new List<Vector3>();
            public List<Vector2> UVs = new List<Vector2>();
            public List<int> Triangles = new List<int>();
        }

        public struct PkgXref
        {
            public Vector3 XAxis;
            public Vector3 YAxis;
            public Vector3 ZAxis;
            public Vector3 Origin;
            public string TargetPkgName;
        }

        public struct MtxData
        {
            public Vector3 Min;
            public Vector3 Max;
            public Vector3 Size;
            public Vector3 Position;
            public bool IsValid;
        }

        public class ParsedPkg
        {
            public string BaseName;
            public int NumPaintjobs = 1;
            public int ShadersPerPaintjob = 0;
            public List<PkgShaderEntry> Shaders = new List<PkgShaderEntry>();
            public Dictionary<string, List<PkgSection>> GeometryChunks =
                new Dictionary<string, List<PkgSection>>(StringComparer.OrdinalIgnoreCase);
            public List<PkgXref> Xrefs = new List<PkgXref>();
        }

        private static readonly Dictionary<string, ParsedPkg> PkgCache =
            new Dictionary<string, ParsedPkg>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Material> MaterialCache =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] ExtraVehicleBodyChunks =
        {
            "BREAK0_H", "BREAK1_H", "BREAK2_H", "BREAK3_H",
            "BREAK01_H", "BREAK03_H", "BREAK12_H", "BREAK23_H",
            "FNDR0_H", "FNDR1_H",
            "EXHAUST0_H", "EXHAUST1_H"
        };

        public static bool HasMm2PkgModel(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return false;
            if (!string.IsNullOrEmpty(MM2ArchiveSystem.FindPkgVirtualPath(baseName))) return true;
            if (MM2ArchiveSystem.FileExists($"geometry/{baseName}.pkg")) return true;
            if (MM2ArchiveSystem.FileExists($"geometry/{baseName}_body_h.pkg")) return true;
            return false;
        }

        public static bool HasMm1BmsModel(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return false;
            return MM2ArchiveSystem.HasBmsDirectory(baseName);
        }

        public static MtxData ReadMtx(string baseName, string partSuffix)
        {
            // Model-type precedence: if an MM2 .pkg model exists for baseName, use .mtx;
            // otherwise fall back to reading part bounds/offsets from MM1 .bms.
            if (HasMm2PkgModel(baseName) || MM2ArchiveSystem.FileExists($"geometry/{baseName}_{partSuffix}.mtx"))
            {
                string vPath = $"geometry/{baseName}_{partSuffix}.mtx";
                byte[] data = MM2ArchiveSystem.ReadFileBytes(vPath);
                if (data != null && data.Length >= 48)
                {
                    return new MtxData
                    {
                        Min = new Vector3(
                            BitConverter.ToSingle(data, 0),
                            BitConverter.ToSingle(data, 4),
                            BitConverter.ToSingle(data, 8)),
                        Max = new Vector3(
                            BitConverter.ToSingle(data, 12),
                            BitConverter.ToSingle(data, 16),
                            BitConverter.ToSingle(data, 20)),
                        Size = new Vector3(
                            BitConverter.ToSingle(data, 24),
                            BitConverter.ToSingle(data, 28),
                            BitConverter.ToSingle(data, 32)),
                        Position = new Vector3(
                            BitConverter.ToSingle(data, 36),
                            BitConverter.ToSingle(data, 40),
                            BitConverter.ToSingle(data, 44)),
                        IsValid = true
                    };
                }
            }

            if (HasMm1BmsModel(baseName))
            {
                return ReadMtxFromBms(baseName, partSuffix);
            }

            return default;
        }

        private static MtxData ReadMtxFromBms(string baseName, string partSuffix)
        {
            string lookupPart = partSuffix;
            // In MM1 .bms vehicles, whl2 is Rear-Right (+X) and whl3 is Rear-Left (-X),
            // whereas MM2 convention uses whl2 for Rear-Left (-X) and whl3 for Rear-Right (+X).
            if (string.Equals(partSuffix, "whl2", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(partSuffix, "whl3", StringComparison.OrdinalIgnoreCase))
            {
                string w2Path = MM2ArchiveSystem.FindBmsVirtualPath(baseName, "whl2_h") ??
                                MM2ArchiveSystem.FindBmsVirtualPath(baseName, "whl2");
                string w3Path = MM2ArchiveSystem.FindBmsVirtualPath(baseName, "whl3_h") ??
                                MM2ArchiveSystem.FindBmsVirtualPath(baseName, "whl3");
                if (!string.IsNullOrEmpty(w2Path) && !string.IsNullOrEmpty(w3Path))
                {
                    byte[] w2Bytes = MM2ArchiveSystem.ReadFileBytes(w2Path);
                    byte[] w3Bytes = MM2ArchiveSystem.ReadFileBytes(w3Path);
                    if (w2Bytes != null && w2Bytes.Length >= 16 && w3Bytes != null && w3Bytes.Length >= 16)
                    {
                        float x2 = BitConverter.ToSingle(w2Bytes, 4);
                        float x3 = BitConverter.ToSingle(w3Bytes, 4);
                        if (x2 > 0f && x3 < 0f)
                        {
                            lookupPart = string.Equals(partSuffix, "whl2", StringComparison.OrdinalIgnoreCase)
                                ? "whl3"
                                : "whl2";
                        }
                    }
                }
            }

            string bmsPath = MM2ArchiveSystem.FindBmsVirtualPath(baseName, $"{lookupPart}_h") ??
                             MM2ArchiveSystem.FindBmsVirtualPath(baseName, lookupPart);
            if (string.IsNullOrEmpty(bmsPath)) return default;

            byte[] data = MM2ArchiveSystem.ReadFileBytes(bmsPath);
            if (data == null || data.Length < 0x34) return default;
            if (data[0] != '3' || data[1] != 'H' || data[2] != 'S' || data[3] != 'M') return default;

            Vector3 offset = new Vector3(
                BitConverter.ToSingle(data, 4),
                BitConverter.ToSingle(data, 8),
                BitConverter.ToSingle(data, 12));
            uint nVerts = BitConverter.ToUInt32(data, 0x10);
            byte nMaterials = data[0x2C];
            int vertPos = 0x34 + nMaterials * 0x30;

            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            if (nVerts > 0 && vertPos + nVerts * 12 <= data.Length)
            {
                min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < nVerts; i++)
                {
                    int p = vertPos + i * 12;
                    float vx = BitConverter.ToSingle(data, p);
                    float vy = BitConverter.ToSingle(data, p + 4);
                    float vz = BitConverter.ToSingle(data, p + 8);
                    if (vx < min.x) min.x = vx;
                    if (vy < min.y) min.y = vy;
                    if (vz < min.z) min.z = vz;
                    if (vx > max.x) max.x = vx;
                    if (vy > max.y) max.y = vy;
                    if (vz > max.z) max.z = vz;
                }
            }

            return new MtxData
            {
                Min = min,
                Max = max,
                Size = max - min,
                Position = offset,
                IsValid = true
            };
        }

        public static ParsedPkg LoadPkg(string pkgNameOrBaseName)
        {
            if (string.IsNullOrEmpty(pkgNameOrBaseName)) return null;

            if (PkgCache.TryGetValue(pkgNameOrBaseName, out ParsedPkg cached) && cached != null)
            {
                return cached;
            }

            // 1. Model-type precedence: always check for an MM2 .pkg (PKG3) model first.
            string vPath = MM2ArchiveSystem.FindPkgVirtualPath(pkgNameOrBaseName);
            if (string.IsNullOrEmpty(vPath))
            {
                string candidate = $"geometry/{pkgNameOrBaseName}.pkg";
                if (MM2ArchiveSystem.FileExists(candidate)) vPath = candidate;
            }

            if (!string.IsNullOrEmpty(vPath))
            {
                if (PkgCache.TryGetValue(vPath, out cached) && cached != null)
                {
                    PkgCache[pkgNameOrBaseName] = cached;
                    return cached;
                }

                byte[] data = MM2ArchiveSystem.ReadFileBytes(vPath);
                if (data != null && data.Length >= 12)
                {
                    ParsedPkg parsed = ParsePkgBytes(data, pkgNameOrBaseName);
                    if (parsed != null)
                    {
                        PkgCache[vPath] = parsed;
                        PkgCache[pkgNameOrBaseName] = parsed;
                        return parsed;
                    }
                }
            }

            // 2. Fallback to MM1 .bms (3HSM) directory if no .pkg model exists for pkgNameOrBaseName.
            if (HasMm1BmsModel(pkgNameOrBaseName))
            {
                ParsedPkg bmsPkg = LoadBmsDirectoryAsPkg(pkgNameOrBaseName);
                if (bmsPkg != null)
                {
                    PkgCache[pkgNameOrBaseName] = bmsPkg;
                    return bmsPkg;
                }
            }

            return null;
        }

        public static ParsedPkg ParsePkgBytes(byte[] data, string baseName)
        {
            if (data == null || data.Length < 8) return null;
            if (data[0] != 'P' || data[1] != 'K' || data[2] != 'G' || data[3] != '3') return null;

            var pkg = new ParsedPkg { BaseName = baseName };
            int pos = 4;

            while (pos + 9 <= data.Length)
            {
                if (data[pos] != 'F' || data[pos + 1] != 'I' || data[pos + 2] != 'L' || data[pos + 3] != 'E')
                {
                    break;
                }

                int nameLen = data[pos + 4];
                if (pos + 5 + nameLen + 4 > data.Length) break;

                string chunkName = Encoding.ASCII.GetString(data, pos + 5, Math.Max(0, nameLen)).TrimEnd('\0');
                int chunkSize = (int)BitConverter.ToUInt32(data, pos + 5 + nameLen);
                int chunkStart = pos + 9 + nameLen;
                if (chunkSize < 0 || chunkStart + chunkSize > data.Length) break;

                if (string.Equals(chunkName, "shaders", StringComparison.OrdinalIgnoreCase))
                {
                    ParseShadersChunk(data, chunkStart, chunkSize, pkg);
                }
                else if (string.Equals(chunkName, "xrefs", StringComparison.OrdinalIgnoreCase))
                {
                    ParseXrefsChunk(data, chunkStart, chunkSize, pkg);
                }
                else if (chunkName.EndsWith("_H", StringComparison.OrdinalIgnoreCase) ||
                         chunkName.EndsWith("LIGHT_L", StringComparison.OrdinalIgnoreCase) ||
                         chunkName.StartsWith("SIREN", StringComparison.OrdinalIgnoreCase) ||
                         chunkName.StartsWith("BREAK", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(chunkName, "H", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(chunkName, "M", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(chunkName, "L", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(chunkName, "VL", StringComparison.OrdinalIgnoreCase))
                {
                    List<PkgSection> sections = ParseGeometryChunk(data, chunkStart, chunkSize);
                    if (sections != null && sections.Count > 0)
                    {
                        pkg.GeometryChunks[chunkName] = sections;
                    }
                }

                pos = chunkStart + chunkSize;
            }

            return pkg;
        }

        private static readonly string[] Mm1ColorKeywords =
        {
            "white", "blue", "red", "yellow", "orange", "green", "black", "grey", "gold", "purple"
        };

        private static readonly Color32[] Mm1AmbientTrafficColors =
        {
            new Color32(215, 215, 220, 255), // Silver / White
            new Color32(165, 38, 38, 255),   // Burgundy / Red
            new Color32(45, 82, 148, 255),   // Navy / Blue
            new Color32(42, 98, 62, 255),    // Forest Green
            new Color32(198, 178, 132, 255), // Champagne / Beige
            new Color32(72, 76, 84, 255)     // Charcoal Grey
        };

        public static bool TryParseBmsMesh(
            byte[] data,
            out Vector3 offset,
            out List<PkgShaderEntry> materials,
            out List<PkgSection> sections)
        {
            offset = Vector3.zero;
            materials = null;
            sections = null;

            if (data == null || data.Length < 0x34) return false;
            if (data[0] != '3' || data[1] != 'H' || data[2] != 'S' || data[3] != 'M') return false;

            offset = new Vector3(
                BitConverter.ToSingle(data, 4),
                BitConverter.ToSingle(data, 8),
                BitConverter.ToSingle(data, 12));

            uint nVerts = BitConverter.ToUInt32(data, 0x10);
            uint nAdj = BitConverter.ToUInt32(data, 0x14);
            uint nFaces = BitConverter.ToUInt32(data, 0x18);
            uint nIdx = BitConverter.ToUInt32(data, 0x1C);
            byte nMaterials = data[0x2C];
            byte flags = data[0x2D];

            int pos = 0x34;
            if (pos + nMaterials * 0x30 > data.Length) return false;

            materials = new List<PkgShaderEntry>(nMaterials);
            for (int m = 0; m < nMaterials; m++)
            {
                int mOff = pos + m * 0x30;
                int nameEnd = mOff;
                while (nameEnd < mOff + 32 && data[nameEnd] != 0) nameEnd++;
                string texName = Encoding.ASCII.GetString(data, mOff, nameEnd - mOff).Trim();

                // Strip _DMG suffix for default undamaged shader entry so damage zone remapping works cleanly
                if (texName.EndsWith("_DMG", StringComparison.OrdinalIgnoreCase))
                {
                    string cleanCandidate = texName.Substring(0, texName.Length - 4);
                    if (MM2ArchiveSystem.FindTextureVirtualPath(cleanCandidate) != null)
                    {
                        texName = cleanCandidate;
                    }
                }

                materials.Add(new PkgShaderEntry
                {
                    TextureName = texName,
                    DiffuseColor = new Color32(255, 255, 255, 255),
                    SpecularColor = new Color32(255, 255, 255, 255),
                    EmissiveColor = new Color32(0, 0, 0, 255),
                    Shininess = 0.2f
                });
            }
            pos += nMaterials * 0x30;

            if (nVerts == 0 || nAdj == 0 || nFaces == 0 || nIdx < nFaces * 4) return false;

            long expected = (long)nVerts * 12L
                + (((flags & 2) != 0) ? nAdj : 0L)
                + (((flags & 1) != 0) ? (long)nAdj * 8L : 0L)
                + (((flags & 4) != 0) ? (long)nAdj * 4L : 0L)
                + (long)nAdj * 2L
                + (((flags & 16) != 0) ? (long)nFaces * 16L : 0L)
                + nFaces
                + (long)nIdx * 2L;

            long rem = data.Length - pos;
            if (rem < expected) return false;
            bool hasBbox = (rem - expected) >= 96L;

            Vector3[] verts = new Vector3[nVerts];
            for (int i = 0; i < nVerts; i++)
            {
                verts[i] = new Vector3(
                    BitConverter.ToSingle(data, pos),
                    BitConverter.ToSingle(data, pos + 4),
                    BitConverter.ToSingle(data, pos + 8));
                pos += 12;
            }

            if (hasBbox) pos += 96;
            if ((flags & 2) != 0) pos += (int)nAdj; // skip packed normal indices

            Vector2[] uvs = new Vector2[nAdj];
            if ((flags & 1) != 0)
            {
                for (int i = 0; i < nAdj; i++)
                {
                    uvs[i] = new Vector2(
                        BitConverter.ToSingle(data, pos),
                        BitConverter.ToSingle(data, pos + 4));
                    pos += 8;
                }
            }

            Color32[] adjColors = null;
            if ((flags & 4) != 0)
            {
                adjColors = new Color32[nAdj];
                for (int i = 0; i < nAdj; i++)
                {
                    byte b = data[pos];
                    byte g = data[pos + 1];
                    byte r = data[pos + 2];
                    byte a = data[pos + 3];
                    adjColors[i] = new Color32(r, g, b, a == 0 ? (byte)255 : a);
                    pos += 4;
                }
            }

            ushort[] vertIndices = new ushort[nAdj];
            for (int i = 0; i < nAdj; i++)
            {
                vertIndices[i] = BitConverter.ToUInt16(data, pos);
                pos += 2;
            }

            Vector3[] faceNormals = null;
            if ((flags & 16) != 0)
            {
                faceNormals = new Vector3[nFaces];
                for (int i = 0; i < nFaces; i++)
                {
                    faceNormals[i] = new Vector3(
                        BitConverter.ToSingle(data, pos),
                        BitConverter.ToSingle(data, pos + 4),
                        BitConverter.ToSingle(data, pos + 8));
                    pos += 16;
                }
            }

            byte[] faceMats = new byte[nFaces];
            Buffer.BlockCopy(data, pos, faceMats, 0, (int)nFaces);
            pos += (int)nFaces;

            ushort[] adjIndices = new ushort[nIdx];
            for (int i = 0; i < nIdx; i++)
            {
                adjIndices[i] = BitConverter.ToUInt16(data, pos);
                pos += 2;
            }

            var sectionByShader = new Dictionary<int, PkgSection>();
            var untexturedShaderByColor = new Dictionary<int, int>();
            sections = new List<PkgSection>();

            for (int f = 0; f < nFaces; f++)
            {
                int a0 = adjIndices[f * 4];
                int a1 = adjIndices[f * 4 + 1];
                int a2 = adjIndices[f * 4 + 2];
                int a3 = adjIndices[f * 4 + 3];

                if (a0 >= nAdj || a1 >= nAdj || a2 >= nAdj) continue;
                int v0Idx = vertIndices[a0];
                int v1Idx = vertIndices[a1];
                int v2Idx = vertIndices[a2];
                if (v0Idx >= nVerts || v1Idx >= nVerts || v2Idx >= nVerts) continue;
                if (v0Idx == v1Idx || v1Idx == v2Idx || v0Idx == v2Idx) continue;

                byte mIdx = faceMats[f];
                int shaderIdx;
                if (mIdx >= 1 && mIdx <= nMaterials)
                {
                    shaderIdx = mIdx - 1;
                }
                else
                {
                    Color32 vc = (adjColors != null && a0 < adjColors.Length)
                        ? adjColors[a0]
                        : new Color32(180, 180, 180, 255);
                    int colorKey = (vc.r << 16) | (vc.g << 8) | vc.b;
                    if (!untexturedShaderByColor.TryGetValue(colorKey, out shaderIdx))
                    {
                        shaderIdx = materials.Count;
                        materials.Add(new PkgShaderEntry
                        {
                            TextureName = string.Empty,
                            DiffuseColor = vc,
                            SpecularColor = new Color32(255, 255, 255, 255),
                            EmissiveColor = new Color32(0, 0, 0, 255),
                            Shininess = 0.25f
                        });
                        untexturedShaderByColor[colorKey] = shaderIdx;
                    }
                }

                if (!sectionByShader.TryGetValue(shaderIdx, out PkgSection sec))
                {
                    sec = new PkgSection { ShaderOffset = shaderIdx };
                    sectionByShader[shaderIdx] = sec;
                    sections.Add(sec);
                }

                Vector3 p0 = verts[v0Idx];
                Vector3 p1 = verts[v1Idx];
                Vector3 p2 = verts[v2Idx];
                Vector3 fn;
                if (faceNormals != null &&
                    !float.IsNaN(faceNormals[f].x) &&
                    !float.IsNaN(faceNormals[f].y) &&
                    !float.IsNaN(faceNormals[f].z) &&
                    faceNormals[f].sqrMagnitude > 1e-6f)
                {
                    fn = faceNormals[f];
                }
                else
                {
                    Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                    fn = cross.sqrMagnitude > 1e-8f ? cross.normalized : Vector3.up;
                }

                bool isQuad = (a3 != 0 && a3 != a0 && a3 != a2 && a3 < nAdj && vertIndices[a3] < nVerts);
                int baseV = sec.Positions.Count;

                sec.Positions.Add(p0);
                sec.Normals.Add(fn);
                sec.UVs.Add(uvs[a0]);

                sec.Positions.Add(p1);
                sec.Normals.Add(fn);
                sec.UVs.Add(uvs[a1]);

                sec.Positions.Add(p2);
                sec.Normals.Add(fn);
                sec.UVs.Add(uvs[a2]);

                sec.Triangles.Add(baseV);
                sec.Triangles.Add(baseV + 1);
                sec.Triangles.Add(baseV + 2);

                if (isQuad)
                {
                    Vector3 p3 = verts[vertIndices[a3]];
                    sec.Positions.Add(p3);
                    sec.Normals.Add(fn);
                    sec.UVs.Add(uvs[a3]);

                    sec.Triangles.Add(baseV);
                    sec.Triangles.Add(baseV + 2);
                    sec.Triangles.Add(baseV + 3);
                }
            }

            return sections.Count > 0;
        }

        private static ParsedPkg LoadBmsDirectoryAsPkg(string dirName)
        {
            IReadOnlyList<string> bmsFiles = MM2ArchiveSystem.GetBmsFilesInDirectory(dirName);
            if (bmsFiles == null || bmsFiles.Count == 0) return null;

            var pkg = new ParsedPkg { BaseName = dirName };
            var shaderLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Check if whl2/whl3 need swapping (in MM1 .bms, whl2 is RR (+X) and whl3 is RL (-X))
            bool swapRearWheels = false;
            string w2Path = MM2ArchiveSystem.FindBmsVirtualPath(dirName, "whl2_h") ??
                            MM2ArchiveSystem.FindBmsVirtualPath(dirName, "whl2");
            string w3Path = MM2ArchiveSystem.FindBmsVirtualPath(dirName, "whl3_h") ??
                            MM2ArchiveSystem.FindBmsVirtualPath(dirName, "whl3");
            if (!string.IsNullOrEmpty(w2Path) && !string.IsNullOrEmpty(w3Path))
            {
                byte[] w2Bytes = MM2ArchiveSystem.ReadFileBytes(w2Path);
                byte[] w3Bytes = MM2ArchiveSystem.ReadFileBytes(w3Path);
                if (w2Bytes != null && w2Bytes.Length >= 16 && w3Bytes != null && w3Bytes.Length >= 16)
                {
                    float x2 = BitConverter.ToSingle(w2Bytes, 4);
                    float x3 = BitConverter.ToSingle(w3Bytes, 4);
                    if (x2 > 0f && x3 < 0f) swapRearWheels = true;
                }
            }

            for (int i = 0; i < bmsFiles.Count; i++)
            {
                string vPath = bmsFiles[i];
                string fileName = System.IO.Path.GetFileNameWithoutExtension(vPath);
                if (string.IsNullOrEmpty(fileName)) continue;

                string chunkName = fileName.ToUpperInvariant();
                if (swapRearWheels)
                {
                    if (chunkName.StartsWith("WHL2", StringComparison.OrdinalIgnoreCase))
                        chunkName = "WHL3" + chunkName.Substring(4);
                    else if (chunkName.StartsWith("WHL3", StringComparison.OrdinalIgnoreCase))
                        chunkName = "WHL2" + chunkName.Substring(4);
                }

                byte[] bytes = MM2ArchiveSystem.ReadFileBytes(vPath);
                if (!TryParseBmsMesh(bytes, out _, out List<PkgShaderEntry> localMats, out List<PkgSection> sections))
                {
                    continue;
                }

                for (int s = 0; s < sections.Count; s++)
                {
                    PkgSection sec = sections[s];
                    PkgShaderEntry mat = localMats[sec.ShaderOffset];
                    string key = $"{mat.TextureName}|{mat.DiffuseColor.r},{mat.DiffuseColor.g},{mat.DiffuseColor.b}";
                    if (!shaderLookup.TryGetValue(key, out int globalShaderIdx))
                    {
                        globalShaderIdx = pkg.Shaders.Count;
                        pkg.Shaders.Add(mat);
                        shaderLookup[key] = globalShaderIdx;
                    }
                    sec.ShaderOffset = globalShaderIdx;
                }

                pkg.GeometryChunks[chunkName] = sections;

                // Map MM1 light chunk names without _H suffix to MM2 chunk names
                if (chunkName == "HLIGHT" && !pkg.GeometryChunks.ContainsKey("HLIGHT_H"))
                    pkg.GeometryChunks["HLIGHT_H"] = sections;
                else if (chunkName == "TLIGHT" && !pkg.GeometryChunks.ContainsKey("TLIGHT_H"))
                    pkg.GeometryChunks["TLIGHT_H"] = sections;
                else if (chunkName == "BLIGHT" && !pkg.GeometryChunks.ContainsKey("BLIGHT_H"))
                    pkg.GeometryChunks["BLIGHT_H"] = sections;
                else if (chunkName == "RLIGHT" && !pkg.GeometryChunks.ContainsKey("RLIGHT_H"))
                    pkg.GeometryChunks["RLIGHT_H"] = sections;
                else if (chunkName == "SLIGHT0" && !pkg.GeometryChunks.ContainsKey("SLIGHT0_H"))
                    pkg.GeometryChunks["SLIGHT0_H"] = sections;
                else if (chunkName == "SLIGHT1" && !pkg.GeometryChunks.ContainsKey("SLIGHT1_H"))
                    pkg.GeometryChunks["SLIGHT1_H"] = sections;
                else if (chunkName == "REDLIGHT" && !pkg.GeometryChunks.ContainsKey("SIREN0_H"))
                    pkg.GeometryChunks["SIREN0_H"] = sections;
                else if (chunkName == "BLUELIGHT" && !pkg.GeometryChunks.ContainsKey("SIREN1_H"))
                    pkg.GeometryChunks["SIREN1_H"] = sections;
            }

            if (pkg.GeometryChunks.Count == 0) return null;

            int baseShaderCount = pkg.Shaders.Count;
            pkg.ShadersPerPaintjob = baseShaderCount;
            pkg.NumPaintjobs = 1;

            if (baseShaderCount > 0)
            {
                BuildMm1BmsPaintjobs(pkg, dirName, baseShaderCount);
            }

            return pkg;
        }

        private static void BuildMm1BmsPaintjobs(ParsedPkg pkg, string dirName, int baseShaderCount)
        {
            string lowerDir = dirName.ToLowerInvariant();

            // 1. Check if this is a textured MM1 player vehicle (vp*) with color-variant textures
            if (lowerDir.StartsWith("vp", StringComparison.Ordinal))
            {
                string defaultPrefix = null;
                string sampleSuffix = null;

                for (int i = 0; i < baseShaderCount; i++)
                {
                    string tName = pkg.Shaders[i].TextureName;
                    if (string.IsNullOrEmpty(tName)) continue;
                    int us = tName.IndexOf('_');
                    if (us <= 2) continue;

                    string suffix = tName.Substring(us + 1).ToLowerInvariant();
                    if (suffix == "bk" || suffix == "ft" || suffix == "sd" || suffix == "bklft" || suffix == "sdlft" || suffix == "sdlfta")
                    {
                        defaultPrefix = tName.Substring(0, us).ToLowerInvariant();
                        sampleSuffix = suffix;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(defaultPrefix) && !string.IsNullOrEmpty(sampleSuffix))
                {
                    string stemPrefix = defaultPrefix;
                    for (int c = 0; c < Mm1ColorKeywords.Length; c++)
                    {
                        string kw = Mm1ColorKeywords[c];
                        if (stemPrefix.Length > kw.Length + 2 && stemPrefix.EndsWith(kw, StringComparison.OrdinalIgnoreCase))
                        {
                            stemPrefix = stemPrefix.Substring(0, stemPrefix.Length - kw.Length);
                            break;
                        }
                    }

                    var validPrefixes = new List<string> { defaultPrefix };
                    for (int c = 0; c < Mm1ColorKeywords.Length; c++)
                    {
                        string candPrefix = stemPrefix + Mm1ColorKeywords[c];
                        if (string.Equals(candPrefix, defaultPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (MM2ArchiveSystem.FindTextureVirtualPath($"{candPrefix}_{sampleSuffix}") != null)
                        {
                            validPrefixes.Add(candPrefix);
                        }
                    }

                    if (validPrefixes.Count > 1)
                    {
                        for (int p = 1; p < validPrefixes.Count; p++)
                        {
                            string targetPrefix = validPrefixes[p];
                            for (int s = 0; s < baseShaderCount; s++)
                            {
                                PkgShaderEntry entry = pkg.Shaders[s];
                                if (!string.IsNullOrEmpty(entry.TextureName) &&
                                    entry.TextureName.StartsWith(defaultPrefix + "_", StringComparison.OrdinalIgnoreCase))
                                {
                                    string remapped = targetPrefix + entry.TextureName.Substring(defaultPrefix.Length);
                                    if (MM2ArchiveSystem.FindTextureVirtualPath(remapped) != null)
                                    {
                                        entry.TextureName = remapped;
                                    }
                                }
                                pkg.Shaders.Add(entry);
                            }
                        }
                        pkg.NumPaintjobs = validPrefixes.Count;
                        return;
                    }
                }
            }

            // 2. Check if this is an MM1 ambient traffic car (va*) with untextured painted body faces
            if (lowerDir.StartsWith("va", StringComparison.Ordinal) &&
                !lowerDir.StartsWith("vataxi", StringComparison.Ordinal) &&
                !lowerDir.Equals("valimo", StringComparison.Ordinal) &&
                !lowerDir.Equals("vadelivery", StringComparison.Ordinal))
            {
                int bodyShaderIdx = -1;
                int maxLum = 115;
                for (int s = 0; s < baseShaderCount; s++)
                {
                    PkgShaderEntry entry = pkg.Shaders[s];
                    if (!string.IsNullOrEmpty(entry.TextureName)) continue;
                    int lum = Mathf.Max(entry.DiffuseColor.r, Mathf.Max(entry.DiffuseColor.g, entry.DiffuseColor.b));
                    if (lum > maxLum)
                    {
                        maxLum = lum;
                        bodyShaderIdx = s;
                    }
                }

                if (bodyShaderIdx >= 0)
                {
                    for (int p = 0; p < Mm1AmbientTrafficColors.Length; p++)
                    {
                        if (p == 0)
                        {
                            PkgShaderEntry e0 = pkg.Shaders[bodyShaderIdx];
                            e0.DiffuseColor = Mm1AmbientTrafficColors[0];
                            pkg.Shaders[bodyShaderIdx] = e0;
                        }
                        else
                        {
                            for (int s = 0; s < baseShaderCount; s++)
                            {
                                PkgShaderEntry entry = pkg.Shaders[s];
                                if (s == bodyShaderIdx)
                                {
                                    entry.DiffuseColor = Mm1AmbientTrafficColors[p];
                                }
                                pkg.Shaders.Add(entry);
                            }
                        }
                    }
                    pkg.NumPaintjobs = Mm1AmbientTrafficColors.Length;
                }
            }
        }

        private static void ParseShadersChunk(byte[] data, int start, int length, ParsedPkg pkg)
        {
            if (length < 8) return;
            uint nPjRaw = BitConverter.ToUInt32(data, start);
            uint perPj = BitConverter.ToUInt32(data, start + 4);

            int extraLen = ((nPjRaw & 0x80u) != 0) ? 16 : 68;
            int p = start + 8;
            int end = start + length;

            while (p < end)
            {
                int sLen = data[p];
                p += 1;
                if (p + sLen + extraLen > end) break;

                string tName = sLen > 0
                    ? Encoding.ASCII.GetString(data, p, sLen).TrimEnd('\0').Trim()
                    : string.Empty;
                p += sLen;

                Color32 diff = new Color32(255, 255, 255, 255);
                Color32 spec = new Color32(255, 255, 255, 255);
                Color32 emis = new Color32(0, 0, 0, 255);
                float shininess = 0.2f;

                if (extraLen >= 16)
                {
                    diff = new Color32(data[p], data[p + 1], data[p + 2], data[p + 3]);
                    spec = new Color32(data[p + 4], data[p + 5], data[p + 6], data[p + 7]);
                    emis = new Color32(data[p + 8], data[p + 9], data[p + 10], data[p + 11]);
                    float rawShine = BitConverter.ToSingle(data, p + 12);
                    if (!float.IsNaN(rawShine) && !float.IsInfinity(rawShine))
                    {
                        shininess = Mathf.Clamp01(rawShine);
                    }
                }

                p += extraLen;

                pkg.Shaders.Add(new PkgShaderEntry
                {
                    TextureName = tName,
                    DiffuseColor = diff,
                    SpecularColor = spec,
                    EmissiveColor = emis,
                    Shininess = shininess
                });
            }

            pkg.ShadersPerPaintjob = (int)perPj;
            pkg.NumPaintjobs = (perPj > 0 && pkg.Shaders.Count >= perPj)
                ? (pkg.Shaders.Count / (int)perPj)
                : Math.Max(1, (int)(nPjRaw & 0x7Fu));
        }

        private static void ParseXrefsChunk(byte[] data, int start, int length, ParsedPkg pkg)
        {
            if (length < 4) return;
            uint count = BitConverter.ToUInt32(data, start);
            int p = start + 4;
            int end = start + length;

            for (uint i = 0; i < count && p + 80 <= end; i++)
            {
                Vector3 xa = new Vector3(BitConverter.ToSingle(data, p), BitConverter.ToSingle(data, p + 4), BitConverter.ToSingle(data, p + 8));
                Vector3 ya = new Vector3(BitConverter.ToSingle(data, p + 12), BitConverter.ToSingle(data, p + 16), BitConverter.ToSingle(data, p + 20));
                Vector3 za = new Vector3(BitConverter.ToSingle(data, p + 24), BitConverter.ToSingle(data, p + 28), BitConverter.ToSingle(data, p + 32));
                Vector3 orig = new Vector3(BitConverter.ToSingle(data, p + 36), BitConverter.ToSingle(data, p + 40), BitConverter.ToSingle(data, p + 44));

                int nameEnd = p + 48;
                while (nameEnd < p + 80 && data[nameEnd] != 0) nameEnd++;
                string targetName = Encoding.ASCII.GetString(data, p + 48, nameEnd - (p + 48)).Trim();
                p += 80;

                if (!string.IsNullOrEmpty(targetName))
                {
                    pkg.Xrefs.Add(new PkgXref
                    {
                        XAxis = xa,
                        YAxis = ya,
                        ZAxis = za,
                        Origin = orig,
                        TargetPkgName = targetName
                    });
                }
            }
        }

        private static List<PkgSection> ParseGeometryChunk(byte[] data, int start, int length)
        {
            if (length < 20) return null;

            uint nSec = BitConverter.ToUInt32(data, start);
            uint fvf = BitConverter.ToUInt32(data, start + 16);

            bool hasRhw = (fvf & 0x04u) != 0;
            bool hasNorm = (fvf & 0x10u) != 0;
            bool hasDiff = (fvf & 0x40u) != 0;
            bool hasSpec = (fvf & 0x80u) != 0;
            int texCnt = (int)((fvf >> 8) & 0x0Fu);

            int p = start + 20;
            int end = start + length;
            var sections = new List<PkgSection>((int)nSec);

            for (uint s = 0; s < nSec && p + 8 <= end; s++)
            {
                ushort nStrips = BitConverter.ToUInt16(data, p);
                int shaderOff = BitConverter.ToInt32(data, p + 4);
                p += 8;

                var section = new PkgSection { ShaderOffset = shaderOff };

                for (int st = 0; st < nStrips && p + 8 <= end; st++)
                {
                    uint nVerts = BitConverter.ToUInt32(data, p + 4);
                    p += 8;

                    int baseVert = section.Positions.Count;
                    for (uint v = 0; v < nVerts && p + 12 <= end; v++)
                    {
                        float vx = BitConverter.ToSingle(data, p);
                        float vy = BitConverter.ToSingle(data, p + 4);
                        float vz = BitConverter.ToSingle(data, p + 8);
                        p += 12;

                        if (hasRhw) p += 4;

                        float nx = 0f, ny = 1f, nz = 0f;
                        if (hasNorm && p + 12 <= end)
                        {
                            nx = BitConverter.ToSingle(data, p);
                            ny = BitConverter.ToSingle(data, p + 4);
                            nz = BitConverter.ToSingle(data, p + 8);
                            p += 12;
                        }

                        if (hasDiff) p += 4;
                        if (hasSpec) p += 4;

                        float u = 0f, vt = 0f;
                        for (int t = 0; t < texCnt && p + 8 <= end; t++)
                        {
                            float tu = BitConverter.ToSingle(data, p);
                            float tv = BitConverter.ToSingle(data, p + 4);
                            p += 8;
                            if (t == 0)
                            {
                                u = tu;
                                vt = tv;
                            }
                        }

                        section.Positions.Add(new Vector3(vx, vy, vz));
                        section.Normals.Add(new Vector3(nx, ny, nz));
                        section.UVs.Add(new Vector2(u, vt));
                    }

                    if (p + 4 > end) break;
                    uint nIdx = BitConverter.ToUInt32(data, p);
                    p += 4;
                    if (p + nIdx * 2 > end) break;

                    for (uint i = 0; i + 2 < nIdx; i += 3)
                    {
                        int i0 = baseVert + BitConverter.ToUInt16(data, p + (int)i * 2);
                        int i1 = baseVert + BitConverter.ToUInt16(data, p + (int)(i + 1) * 2);
                        int i2 = baseVert + BitConverter.ToUInt16(data, p + (int)(i + 2) * 2);
                        section.Triangles.Add(i0);
                        section.Triangles.Add(i1);
                        section.Triangles.Add(i2);
                    }
                    p += (int)nIdx * 2;
                }

                if (section.Triangles.Count > 0)
                {
                    sections.Add(section);
                }
            }

            return sections;
        }

        /// <summary>
        /// Builds runtime visual meshes and materials for rail/prop models that use either BODY_H or H/M/L geometry chunks
        /// (such as va_cablecar_f, va_ug_l, and MM1 bms/r_l_train).
        /// </summary>
        public static bool BuildRuntimePropOrTrainVisuals(
            string baseName,
            int paintjobIndex,
            Transform targetParent,
            out GameObject headlightsGlow)
        {
            headlightsGlow = null;
            ParsedPkg pkg = LoadPkg(baseName);
            if (pkg == null || pkg.GeometryChunks.Count == 0)
            {
                return false;
            }

            List<PkgSection> mainSections = null;
            string[] candidates = { "BODY_H", "H", "BODY_M", "M", "BODY_L", "L", "BODY_VL", "VL" };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (pkg.GeometryChunks.TryGetValue(candidates[i], out mainSections) &&
                    mainSections != null && mainSections.Count > 0)
                {
                    break;
                }
            }

            if (mainSections == null || mainSections.Count == 0)
            {
                return false;
            }

            var chunksWithOffsets = new List<(List<PkgSection> sections, Vector3 ageOffset)>
            {
                (mainSections, Vector3.zero)
            };

            CreateChunkMeshObject(
                "Body_Mesh",
                targetParent,
                pkg,
                chunksWithOffsets,
                paintjobIndex,
                remapDamageTextures: false
            );

            GameObject lightsRoot = new GameObject("VehicleLights");
            lightsRoot.transform.SetParent(targetParent, false);
            lightsRoot.transform.localPosition = Vector3.zero;
            lightsRoot.transform.localRotation = Quaternion.identity;

            headlightsGlow = CreateLightGroupObject(
                "Headlights_Glow",
                lightsRoot.transform,
                pkg,
                new[] { "HLIGHT_H", "HLIGHT_L", "TLIGHT_H", "TLIGHT_L" },
                paintjobIndex
            );

            return true;
        }

        /// <summary>
        /// Builds the complete runtime visual hierarchy (Body_Mesh + VisualWheels) for a vehicle from its .pkg and .mtx files.
        /// </summary>
        public static bool BuildRuntimeVehicleVisuals(
            string baseName,
            int paintjobIndex,
            Transform targetParent,
            out Transform bodyMeshRoot,
            out Transform wheelFL,
            out Transform wheelFR,
            out Transform wheelRL,
            out Transform wheelRR,
            out Vector3 posFL,
            out Vector3 posFR,
            out Vector3 posRL,
            out Vector3 posRR,
            out float wheelRadius
        )
        {
            bodyMeshRoot = null;
            wheelFL = null;
            wheelFR = null;
            wheelRL = null;
            wheelRR = null;
            posFL = new Vector3(-0.74f, 0.33f, 1.40f);
            posFR = new Vector3(0.74f, 0.33f, 1.40f);
            posRL = new Vector3(-0.74f, 0.33f, -1.40f);
            posRR = new Vector3(0.74f, 0.33f, -1.40f);
            wheelRadius = 0.34f;

            ParsedPkg pkg = LoadPkg(baseName);
            if (pkg == null || !pkg.GeometryChunks.ContainsKey("BODY_H"))
            {
                return false;
            }

            // 1. Collect BODY_H + attached vehicle sub-parts (mirrors, fenders, bumpers, lights, etc.)
            var bodyChunksWithOffsets = new List<(List<PkgSection> sections, Vector3 ageOffset)>();
            bodyChunksWithOffsets.Add((pkg.GeometryChunks["BODY_H"], Vector3.zero));

            foreach (string extraChunk in ExtraVehicleBodyChunks)
            {
                if (pkg.GeometryChunks.TryGetValue(extraChunk, out List<PkgSection> extraSections))
                {
                    string mtxSuffix = extraChunk.Substring(0, extraChunk.Length - 2); // strip "_H"
                    MtxData mtx = ReadMtx(baseName, mtxSuffix);
                    Vector3 offset = mtx.IsValid ? mtx.Position : Vector3.zero;
                    bodyChunksWithOffsets.Add((extraSections, offset));
                }
            }

            GameObject bodyGo = CreateChunkMeshObject(
                "Body_Mesh",
                targetParent,
                pkg,
                bodyChunksWithOffsets,
                paintjobIndex,
                remapDamageTextures: true
            );
            bodyMeshRoot = bodyGo.transform;

            // 2. Build 4 Primary Wheels (plus extra axles WHL4/WHL5 if 6-wheeler)
            GameObject wheelsRoot = new GameObject("VisualWheels");
            wheelsRoot.transform.SetParent(targetParent, false);
            wheelsRoot.transform.localPosition = Vector3.zero;
            wheelsRoot.transform.localRotation = Quaternion.identity;

            MtxData mtx0 = ReadMtx(baseName, "whl0");
            MtxData mtx1 = ReadMtx(baseName, "whl1");
            MtxData mtx2 = ReadMtx(baseName, "whl2");
            MtxData mtx3 = ReadMtx(baseName, "whl3");

            // Direct AGE right-handed (-Z forward, -X left, +Y up) -> Unity left-handed (+Z forward, -X left, +Y up):
            // (x_unity, y_unity, z_unity) = (x_age, y_age, -z_age).
            // Note: In MM2_Porting_Guide.md, (-x, y, -z) was written to .obj only because Unity's .obj importer
            // negates X a second time upon import (-(-x) = +x). When building UnityEngine.Mesh directly in memory,
            // we keep +x and negate z, and reverse triangle winding (i0, i2, i1).
            Vector3 uPos0 = mtx0.IsValid ? new Vector3(mtx0.Position.x, mtx0.Position.y, -mtx0.Position.z) : new Vector3(-0.74f, 0.33f, 1.40f);
            Vector3 uPos1 = mtx1.IsValid ? new Vector3(mtx1.Position.x, mtx1.Position.y, -mtx1.Position.z) : new Vector3(0.74f, 0.33f, 1.40f);
            Vector3 uPos2 = mtx2.IsValid ? new Vector3(mtx2.Position.x, mtx2.Position.y, -mtx2.Position.z) : new Vector3(-0.74f, 0.33f, -1.40f);
            Vector3 uPos3 = mtx3.IsValid ? new Vector3(mtx3.Position.x, mtx3.Position.y, -mtx3.Position.z) : new Vector3(0.74f, 0.33f, -1.40f);

            if (mtx0.IsValid && mtx0.Max.y > 0.1f)
            {
                wheelRadius = mtx0.Max.y;
            }

            // Assign WHL0..WHL3 to FL/FR/RL/RR
            posFL = uPos0;
            posFR = uPos1;
            posRL = uPos2;
            posRR = uPos3;

            wheelFL = CreateSingleWheelVisual("FL_Mesh", wheelsRoot.transform, pkg, "WHL0_H", posFL, paintjobIndex);
            wheelFR = CreateSingleWheelVisual("FR_Mesh", wheelsRoot.transform, pkg, "WHL1_H", posFR, paintjobIndex);
            wheelRL = CreateSingleWheelVisual("RL_Mesh", wheelsRoot.transform, pkg, "WHL2_H", posRL, paintjobIndex);
            wheelRR = CreateSingleWheelVisual("RR_Mesh", wheelsRoot.transform, pkg, "WHL3_H", posRR, paintjobIndex);

            // Support 6-wheel semi trucks (vpcentury, vpsemi)
            if (pkg.GeometryChunks.ContainsKey("WHL4_H"))
            {
                MtxData mtx4 = ReadMtx(baseName, "whl4");
                if (mtx4.IsValid)
                {
                    Vector3 uPos4 = new Vector3(mtx4.Position.x, mtx4.Position.y, -mtx4.Position.z);
                    CreateSingleWheelVisual("RL2_Mesh", wheelsRoot.transform, pkg, "WHL4_H", uPos4, paintjobIndex);
                }
            }
            if (pkg.GeometryChunks.ContainsKey("WHL5_H"))
            {
                MtxData mtx5 = ReadMtx(baseName, "whl5");
                if (mtx5.IsValid)
                {
                    Vector3 uPos5 = new Vector3(mtx5.Position.x, mtx5.Position.y, -mtx5.Position.z);
                    CreateSingleWheelVisual("RR2_Mesh", wheelsRoot.transform, pkg, "WHL5_H", uPos5, paintjobIndex);
                }
            }

            // 3. Build dynamic vehicle light meshes (Headlights/Taillights, Brakelights, Reverselights)
            // In MM2, these chunks are hidden by default (e.g. in the Select Vehicle showcase and daytime driving)
            // and only enabled when headlights, brakes, or reverse gear are active.
            GameObject lightsRoot = new GameObject("VehicleLights");
            lightsRoot.transform.SetParent(targetParent, false);
            lightsRoot.transform.localPosition = Vector3.zero;
            lightsRoot.transform.localRotation = Quaternion.identity;

            CreateLightGroupObject(
                "Headlights_Glow",
                lightsRoot.transform,
                pkg,
                new[] { "HLIGHT_H", "HLIGHT_L", "TLIGHT_H", "TLIGHT_L" },
                paintjobIndex
            );
            CreateLightGroupObject(
                "Brakelights_Glow",
                lightsRoot.transform,
                pkg,
                new[] { "BLIGHT_H", "BLIGHT_L" },
                paintjobIndex
            );
            CreateLightGroupObject(
                "Reverselights_Glow",
                lightsRoot.transform,
                pkg,
                new[] { "RLIGHT_H", "RLIGHT_L" },
                paintjobIndex
            );
            CreateLightGroupObject(
                "Siren0_Glow",
                lightsRoot.transform,
                pkg,
                new[] { "SIREN0_H", "SIREN0_L" },
                paintjobIndex
            );
            CreateLightGroupObject(
                "Siren1_Glow",
                lightsRoot.transform,
                pkg,
                new[] { "SIREN1_H", "SIREN1_L" },
                paintjobIndex
            );
            CreateLightGroupObject(
                "SignalLeft_Glow",
                lightsRoot.transform,
                pkg,
                new[] { "SLIGHT0_H", "SLIGHT0_L" },
                paintjobIndex
            );
            CreateLightGroupObject(
                "SignalRight_Glow",
                lightsRoot.transform,
                pkg,
                new[] { "SLIGHT1_H", "SLIGHT1_L" },
                paintjobIndex
            );

            return true;
        }

        private static GameObject CreateLightGroupObject(
            string name,
            Transform parent,
            ParsedPkg pkg,
            string[] candidateChunks,
            int paintjobIndex
        )
        {
            var chunkList = new List<(List<PkgSection> sections, Vector3 ageOffset)>();
            bool hasHeadlight = false;
            bool hasTaillight = false;

            foreach (string cName in candidateChunks)
            {
                if (cName.StartsWith("HLIGHT", StringComparison.OrdinalIgnoreCase) && hasHeadlight) continue;
                if (cName.StartsWith("TLIGHT", StringComparison.OrdinalIgnoreCase) && hasTaillight) continue;

                if (pkg.GeometryChunks.TryGetValue(cName, out List<PkgSection> sections) && sections != null && sections.Count > 0)
                {
                    chunkList.Add((sections, Vector3.zero));
                    if (cName.StartsWith("HLIGHT", StringComparison.OrdinalIgnoreCase)) hasHeadlight = true;
                    if (cName.StartsWith("TLIGHT", StringComparison.OrdinalIgnoreCase)) hasTaillight = true;
                }
            }

            if (chunkList.Count == 0) return null;

            GameObject lightGo = CreateChunkMeshObject(
                name,
                parent,
                pkg,
                chunkList,
                paintjobIndex,
                remapDamageTextures: false,
                isLightChunk: true
            );
            lightGo.SetActive(false);
            return lightGo;
        }

        private static Transform CreateSingleWheelVisual(
            string name,
            Transform parent,
            ParsedPkg pkg,
            string wheelChunkName,
            Vector3 localPos,
            int paintjobIndex
        )
        {
            GameObject wheelPivot = new GameObject(name);
            wheelPivot.transform.SetParent(parent, false);
            wheelPivot.transform.localPosition = localPos;
            wheelPivot.transform.localRotation = Quaternion.identity;

            if (!pkg.GeometryChunks.TryGetValue(wheelChunkName, out List<PkgSection> sections))
            {
                pkg.GeometryChunks.TryGetValue("WHL0_H", out sections);
            }

            if (sections != null)
            {
                var list = new List<(List<PkgSection>, Vector3)> { (sections, Vector3.zero) };
                CreateChunkMeshObject("Model", wheelPivot.transform, pkg, list, paintjobIndex, remapDamageTextures: false);
            }

            return wheelPivot.transform;
        }

        private static GameObject CreateChunkMeshObject(
            string name,
            Transform parent,
            ParsedPkg pkg,
            List<(List<PkgSection> sections, Vector3 ageOffset)> chunkList,
            int paintjobIndex,
            bool remapDamageTextures,
            bool isLightChunk = false
        )
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var allPositions = new List<Vector3>();
            var allNormals = new List<Vector3>();
            var allUVs = new List<Vector2>();

            // Group triangles by shaderOffset
            var submeshBuckets = new Dictionary<int, List<int>>();
            var shaderOffsetOrder = new List<int>();

            foreach (var (sections, ageOffset) in chunkList)
            {
                foreach (PkgSection sec in sections)
                {
                    int sOff = sec.ShaderOffset;
                    if (!submeshBuckets.TryGetValue(sOff, out List<int> triBucket))
                    {
                        triBucket = new List<int>();
                        submeshBuckets[sOff] = triBucket;
                        shaderOffsetOrder.Add(sOff);
                    }

                    int baseVert = allPositions.Count;
                    int vCount = sec.Positions.Count;

                    for (int i = 0; i < vCount; i++)
                    {
                        Vector3 p = sec.Positions[i] + ageOffset;
                        Vector3 n = sec.Normals[i];
                        // Direct AGE right-handed (-Z forward, -X left) -> Unity left-handed (+Z forward, -X left):
                        // negate Z only and reverse triangle winding below so the mesh is never mirrored left-to-right.
                        allPositions.Add(new Vector3(p.x, p.y, -p.z));
                        allNormals.Add(new Vector3(n.x, n.y, -n.z));
                        allUVs.Add(sec.UVs[i]);
                    }

                    int tCount = sec.Triangles.Count;
                    for (int i = 0; i + 2 < tCount; i += 3)
                    {
                        triBucket.Add(baseVert + sec.Triangles[i]);
                        triBucket.Add(baseVert + sec.Triangles[i + 2]);
                        triBucket.Add(baseVert + sec.Triangles[i + 1]);
                    }
                }
            }

            Mesh mesh = new Mesh
            {
                name = $"{pkg.BaseName}_{name}",
                indexFormat = IndexFormat.UInt32
            };
            mesh.SetVertices(allPositions);
            mesh.SetNormals(allNormals);
            mesh.SetUVs(0, allUVs);
            mesh.subMeshCount = shaderOffsetOrder.Count;

            for (int s = 0; s < shaderOffsetOrder.Count; s++)
            {
                mesh.SetTriangles(submeshBuckets[shaderOffsetOrder[s]], s, false);
            }
            mesh.RecalculateBounds();

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            if (isLightChunk)
            {
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }

            var binder = go.AddComponent<MM2RuntimePaintjobBinder>();
            binder.PkgData = pkg;
            binder.SubmeshShaderOffsets = shaderOffsetOrder.ToArray();
            binder.RemapDamageTextures = remapDamageTextures;
            binder.IsLightChunk = isLightChunk;

            ApplyPaintjobToBinder(binder, mr, paintjobIndex);
            return go;
        }

        public static void ApplyPaintjobToHierarchy(Transform root, int paintjobIndex)
        {
            if (root == null) return;
            foreach (var binder in root.GetComponentsInChildren<MM2RuntimePaintjobBinder>(true))
            {
                var mr = binder.GetComponent<MeshRenderer>();
                if (mr != null && binder.PkgData != null)
                {
                    ApplyPaintjobToBinder(binder, mr, paintjobIndex);
                }
            }
        }

        public static void ApplyDamageZonesToHierarchy(Transform root, int damageZoneMask)
        {
            if (root == null) return;
            foreach (var binder in root.GetComponentsInChildren<MM2RuntimePaintjobBinder>(true))
            {
                if (binder.IsLightChunk) continue;
                if (binder.DamageZoneMask == damageZoneMask) continue;
                binder.DamageZoneMask = damageZoneMask;

                var mr = binder.GetComponent<MeshRenderer>();
                if (mr != null && binder.PkgData != null)
                {
                    ApplyPaintjobToBinder(binder, mr, binder.CurrentPaintjobIndex);
                }
            }
        }

        private static void ApplyPaintjobToBinder(MM2RuntimePaintjobBinder binder, MeshRenderer mr, int paintjobIndex)
        {
            ParsedPkg pkg = binder.PkgData;
            int pj = (pkg.NumPaintjobs > 0) ? Mathf.Clamp(paintjobIndex, 0, pkg.NumPaintjobs - 1) : 0;
            binder.CurrentPaintjobIndex = pj;
            int pjBase = pj * pkg.ShadersPerPaintjob;

            var mats = new Material[binder.SubmeshShaderOffsets.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                int sOff = binder.SubmeshShaderOffsets[i];
                int shaderIdx = pjBase + sOff;
                PkgShaderEntry entry = (shaderIdx >= 0 && shaderIdx < pkg.Shaders.Count)
                    ? pkg.Shaders[shaderIdx]
                    : new PkgShaderEntry { TextureName = string.Empty, DiffuseColor = new Color32(180, 180, 180, 255) };

                mats[i] = GetOrCreateShaderMaterial(pkg, entry, pj, binder.RemapDamageTextures, binder.IsLightChunk, binder.DamageZoneMask);
            }

            mr.sharedMaterials = mats;
        }

        private static int ClassifyTextureDamageZone(string cleanTexName)
        {
            if (string.IsNullOrEmpty(cleanTexName)) return MM2RuntimePaintjobBinder.DamageZoneTopOrGeneral;
            string lower = cleanTexName.ToLowerInvariant();
            if (lower.EndsWith("_ft") || lower.EndsWith("_tpft") || lower.Contains("_front"))
                return MM2RuntimePaintjobBinder.DamageZoneFront;
            if (lower.EndsWith("_bk") || lower.EndsWith("_tpbk") || lower.EndsWith("_bklft") || lower.EndsWith("_bkrgt") || lower.Contains("_rear") || lower.Contains("_back"))
                return MM2RuntimePaintjobBinder.DamageZoneRear;
            if (lower.EndsWith("_sd") || lower.EndsWith("_sdlft") || lower.EndsWith("_sdrgt") || lower.Contains("_side"))
                return MM2RuntimePaintjobBinder.DamageZoneSide;
            if (lower.EndsWith("_tp") || lower.Contains("_top"))
                return MM2RuntimePaintjobBinder.DamageZoneTopOrGeneral;
            return MM2RuntimePaintjobBinder.DamageZoneTopOrGeneral;
        }

        public static Material GetOrCreateShaderMaterial(
            ParsedPkg pkg,
            PkgShaderEntry entry,
            int paintjobIndex,
            bool remapDamageTextures,
            bool isLightChunk = false,
            int damageZoneMask = 0
        )
        {
            string texName = entry.TextureName ?? string.Empty;

            // Resolve clean vs. *_dmg body panel textures based on active damageZoneMask
            if (remapDamageTextures && !isLightChunk && !string.IsNullOrEmpty(texName))
            {
                string cleanTex = texName;
                if (cleanTex.EndsWith("_dmg", StringComparison.OrdinalIgnoreCase))
                {
                    string candidate = cleanTex.Substring(0, cleanTex.Length - 4);
                    if (MM2ArchiveSystem.FindTextureVirtualPath(candidate) != null)
                    {
                        cleanTex = candidate;
                    }
                    else if (candidate.EndsWith("_bkrgt", StringComparison.OrdinalIgnoreCase))
                    {
                        string leftRear = candidate.Substring(0, candidate.Length - 6) + "_bklft";
                        if (MM2ArchiveSystem.FindTextureVirtualPath(leftRear) != null)
                        {
                            cleanTex = leftRear;
                        }
                    }
                }

                int texZone = ClassifyTextureDamageZone(cleanTex);
                bool wantsDamagedTexture = (damageZoneMask & texZone) != 0;

                if (wantsDamagedTexture)
                {
                    // Prefer the direct *_dmg variant, or original entry texture if it already ended in _dmg
                    string dmgCandidate = cleanTex.EndsWith("_dmg", StringComparison.OrdinalIgnoreCase)
                        ? cleanTex
                        : cleanTex + "_dmg";
                    if (MM2ArchiveSystem.FindTextureVirtualPath(dmgCandidate) != null)
                    {
                        texName = dmgCandidate;
                    }
                    else if (entry.TextureName != null &&
                             entry.TextureName.EndsWith("_dmg", StringComparison.OrdinalIgnoreCase) &&
                             MM2ArchiveSystem.FindTextureVirtualPath(entry.TextureName) != null)
                    {
                        texName = entry.TextureName;
                    }
                    else
                    {
                        // Modded car or part without a _dmg texture: keep clean texture gracefully
                        texName = cleanTex;
                    }
                }
                else
                {
                    texName = cleanTex;
                }
            }

            string cacheKey = $"{pkg.BaseName}|{texName}|{entry.DiffuseColor.r}_{entry.DiffuseColor.g}_{entry.DiffuseColor.b}_{entry.DiffuseColor.a}|{entry.Shininess:F2}|{(isLightChunk ? 1 : 0)}";
            if (MaterialCache.TryGetValue(cacheKey, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit == null) urpLit = Shader.Find("Standard");

            Material mat = new Material(urpLit)
            {
                name = string.IsNullOrEmpty(texName) ? $"Mat_{pkg.BaseName}_Color" : $"Mat_{texName}"
            };

            // Double-sided rendering for open vehicle interiors/fenders, but cull backfaces on closed train cars
            bool cullBackfaces = (pkg != null &&
                                  (string.Equals(pkg.BaseName, "r_l_train", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(pkg.BaseName, "va_ug_l", StringComparison.OrdinalIgnoreCase))) ||
                                 (!string.IsNullOrEmpty(texName) &&
                                  (texName.StartsWith("r_l_", StringComparison.OrdinalIgnoreCase) ||
                                   texName.Equals("l_side", StringComparison.OrdinalIgnoreCase) ||
                                   texName.Equals("l_side_n", StringComparison.OrdinalIgnoreCase)));
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", cullBackfaces ? 2f : 0f);

            MM2RuntimeDecoders.DecodedTextureInfo texInfo = default;
            if (!string.IsNullOrEmpty(texName))
            {
                texInfo = MM2RuntimeDecoders.LoadTexture(texName);
            }

            if (texInfo.Texture != null)
            {
                mat.mainTexture = texInfo.Texture;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texInfo.Texture);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                mat.color = Color.white;

                string lowerTex = texName.ToLowerInvariant();
                bool isGlowSprite = lowerTex.Contains("glow") ||
                                    lowerTex.Contains("toplight") ||
                                    lowerTex.Contains("sirlight") ||
                                    lowerTex.Contains("fxltcone");
                bool isGlassSurface = lowerTex.Contains("glass") || lowerTex.Contains("window") || lowerTex.Contains("vitre");
                bool isWheelOrGrille = lowerTex.Contains("whl") || lowerTex.Contains("wheel") ||
                                       lowerTex.Contains("jante") || lowerTex.Contains("grille") ||
                                       lowerTex.Contains("wiper") || lowerTex.Contains("nodamage") ||
                                       lowerTex.Contains("shdw") || lowerTex.Contains("shadow");

                if (isGlowSprite)
                {
                    // Soft additive alpha-faded blending (SrcAlpha One) for MM2 light flare sprites
                    // (e.g. stc_enzo_glow00, fxltglow, fxltglowred) whose radial falloff is in the alpha channel.
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_Blend", 2f);
                    mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    mat.SetFloat("_DstBlend", (float)BlendMode.One);
                    mat.SetFloat("_ZWrite", 0f);
                    mat.SetFloat("_AlphaClip", 0f);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.SetShaderPassEnabled("DepthOnly", false);
                    mat.renderQueue = (int)RenderQueue.Transparent;

                    mat.EnableKeyword("_EMISSION");
                    if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", texInfo.Texture);
                    if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.white * 1.15f);
                }
                else if (isGlassSurface && texInfo.HasAlpha)
                {
                    Color glassCol = new Color(1f, 1f, 1f, 0.78f);
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", glassCol);
                    mat.color = glassCol;
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_Blend", 0f);
                    mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    if (mat.HasProperty("_SrcBlendAlpha")) mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    if (mat.HasProperty("_DstBlendAlpha")) mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    mat.SetFloat("_ZWrite", 0f);
                    mat.SetFloat("_AlphaClip", 0f);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.SetShaderPassEnabled("DepthOnly", false);
                    mat.SetShaderPassEnabled("ShadowCaster", false);
                    mat.renderQueue = (int)RenderQueue.Transparent;
                }
                else if (texInfo.HasAlpha || isWheelOrGrille)
                {
                    mat.SetFloat("_Surface", 0f);
                    mat.SetFloat("_AlphaClip", 1f);
                    mat.SetFloat("_Cutoff", 0.35f);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.SetOverrideTag("RenderType", "TransparentCutout");
                    mat.renderQueue = (int)RenderQueue.AlphaTest;
                }

                if (isLightChunk && !isGlowSprite)
                {
                    // Self-illuminated brake/tail/reverse lens overlays (e.g. stc_enzo_lumieres00, clk55amg_rearlightson)
                    mat.EnableKeyword("_EMISSION");
                    if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", texInfo.Texture);
                    if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.white * 1.25f);
                }

                float smoothness = (lowerTex.Contains("tire") || lowerTex.Contains("pneu") || lowerTex.Contains("bottom") || lowerTex.Contains("undercarriage"))
                    ? 0.1f
                    : Mathf.Clamp(entry.Shininess * 0.85f, 0.25f, 0.72f);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            }
            else
            {
                // Untextured material (e.g. tire rubber, dark interior trim, chrome, or tinted window glass)
                Color baseCol = entry.DiffuseColor;
                if (baseCol.r == 0f && baseCol.g == 0f && baseCol.b == 0f && baseCol.a == 0f)
                {
                    baseCol = new Color(0.12f, 0.12f, 0.12f, 1f);
                }

                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseCol);
                mat.color = baseCol;
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", Mathf.Clamp(entry.Shininess, 0.15f, 0.75f));
            }

            MaterialCache[cacheKey] = mat;
            return mat;
        }
    }
}
