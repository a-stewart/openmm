using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MM2.Archive
{
    /// <summary>
    /// Runtime decoders for Midtown Madness 2 .tex, .tga, .jpg/.png, and .wav audio files
    /// read directly from .ar archives.
    /// </summary>
    public static class MM2RuntimeDecoders
    {
        public struct DecodedTextureInfo
        {
            public Texture2D Texture;
            public bool HasAlpha;
            public bool HasSemiTransparency;
            public bool ClampU;
            public bool ClampV;
        }

        private static readonly Dictionary<string, DecodedTextureInfo> TextureCache =
            new Dictionary<string, DecodedTextureInfo>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Texture2D> UiTextureCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, AudioClip> AudioCache =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Loads a texture oriented for Unity 2D UI (bottom-up row order, TextureWrapMode.Clamp, no mipmap bleeding).
        /// Used for HUD elements such as DIGITAC_*, SPEED_TICKS, DAMAGE, DAMAGE_LABLE, etc.
        /// </summary>
        public static Texture2D LoadUiTexture(string texName)
        {
            if (string.IsNullOrEmpty(texName)) return null;

            if (UiTextureCache.TryGetValue(texName, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            string vPath = MM2ArchiveSystem.FindTextureVirtualPath(texName);
            if (string.IsNullOrEmpty(vPath))
            {
                return null;
            }

            if (UiTextureCache.TryGetValue(vPath, out cached) && cached != null)
            {
                UiTextureCache[texName] = cached;
                return cached;
            }

            byte[] bytes = MM2ArchiveSystem.ReadFileBytes(vPath);
            if (bytes == null || bytes.Length < 14)
            {
                return null;
            }

            Texture2D tex = null;
            if (vPath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            {
                DecodedTextureInfo info = DecodeTga(bytes, texName, forUi: true);
                tex = info.Texture;
            }
            else if (vPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                DecodedTextureInfo info = DecodeTex(bytes, texName);
                tex = info.Texture;
            }
            else if (vPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                DecodedTextureInfo info = DecodeDds(bytes, texName, forUi: true);
                tex = info.Texture;
            }
            else
            {
                Texture2D loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (loaded.LoadImage(bytes))
                {
                    loaded.name = texName;
                    loaded.wrapMode = TextureWrapMode.Clamp;
                    loaded.filterMode = FilterMode.Bilinear;
                    tex = loaded;
                }
                else
                {
                    UnityEngine.Object.Destroy(loaded);
                }
            }

            if (tex != null)
            {
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.wrapModeU = TextureWrapMode.Clamp;
                tex.wrapModeV = TextureWrapMode.Clamp;
                UiTextureCache[vPath] = tex;
                UiTextureCache[texName] = tex;
            }

            return tex;
        }

        public static DecodedTextureInfo LoadTexture(string texName)
        {
            if (string.IsNullOrEmpty(texName)) return default;

            if (TextureCache.TryGetValue(texName, out DecodedTextureInfo cached) && cached.Texture != null)
            {
                return cached;
            }

            string vPath = MM2ArchiveSystem.FindTextureVirtualPath(texName);
            if (string.IsNullOrEmpty(vPath))
            {
                return default;
            }

            if (TextureCache.TryGetValue(vPath, out cached) && cached.Texture != null)
            {
                TextureCache[texName] = cached;
                return cached;
            }

            byte[] bytes = MM2ArchiveSystem.ReadFileBytes(vPath);
            if (bytes == null || bytes.Length < 14)
            {
                return default;
            }

            DecodedTextureInfo info = default;
            if (vPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                info = DecodeTex(bytes, texName);
            }
            else if (vPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                info = DecodeDds(bytes, texName, forUi: false);
            }
            else if (vPath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            {
                info = DecodeTga(bytes, texName);
            }
            else
            {
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (tex.LoadImage(bytes))
                {
                    tex.name = texName;
                    tex.wrapMode = TextureWrapMode.Repeat;
                    tex.filterMode = FilterMode.Bilinear;
                    tex.anisoLevel = 4;

                    bool hasAlpha = false;
                    bool hasSemiTrans = false;
                    if (vPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        Color32[] px = tex.GetPixels32();
                        for (int i = 0; i < px.Length; i++)
                        {
                            byte a = px[i].a;
                            if (a < 250)
                            {
                                hasAlpha = true;
                                if (a > 15 && a < 240)
                                {
                                    hasSemiTrans = true;
                                    break;
                                }
                            }
                        }
                    }

                    info = new DecodedTextureInfo
                    {
                        Texture = tex,
                        HasAlpha = hasAlpha,
                        HasSemiTransparency = hasSemiTrans
                    };
                }
                else
                {
                    UnityEngine.Object.Destroy(tex);
                }
            }

            if (info.Texture != null)
            {
                TextureCache[vPath] = info;
                TextureCache[texName] = info;
            }

            return info;
        }

        public static DecodedTextureInfo DecodeTex(byte[] data, string name)
        {
            if (data == null || data.Length < 14) return default;

            ushort width = BitConverter.ToUInt16(data, 0);
            ushort height = BitConverter.ToUInt16(data, 2);
            ushort typeCode = BitConverter.ToUInt16(data, 4);
            uint flags = BitConverter.ToUInt32(data, 10);

            if (width == 0 || height == 0 || width > 4096 || height > 4096) return default;

            bool clampU = (flags & 1) != 0;
            bool clampV = (flags & 2) != 0;
            bool hasAlpha = false;
            bool hasSemiTrans = false;

            Color32[] pixels = new Color32[width * height];
            int pos = 14;

            if (typeCode == 1 || typeCode == 2 || typeCode == 14)
            {
                bool useAlpha = (typeCode == 2 || typeCode == 14);
                if (pos + 1024 + width * height > data.Length) return default;

                Color32[] pal = new Color32[256];
                for (int i = 0; i < 256; i++)
                {
                    byte b = data[pos + i * 4];
                    byte g = data[pos + i * 4 + 1];
                    byte r = data[pos + i * 4 + 2];
                    byte a = useAlpha ? data[pos + i * 4 + 3] : (byte)255;
                    if (a < 250) hasAlpha = true;
                    if (a > 15 && a < 240) hasSemiTrans = true;
                    pal[i] = new Color32(r, g, b, a);
                }
                pos += 1024;

                for (int y = 0; y < height; y++)
                {
                    int srcRow = pos + y * width;
                    int dstRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        pixels[dstRow + x] = pal[data[srcRow + x]];
                    }
                }
            }
            else if (typeCode == 15 || typeCode == 16)
            {
                bool useAlpha = (typeCode == 16);
                int rowBytes = (width + 1) >> 1;
                if (pos + 64 + rowBytes * height > data.Length) return default;

                Color32[] pal = new Color32[16];
                for (int i = 0; i < 16; i++)
                {
                    byte b = data[pos + i * 4];
                    byte g = data[pos + i * 4 + 1];
                    byte r = data[pos + i * 4 + 2];
                    byte a = useAlpha ? data[pos + i * 4 + 3] : (byte)255;
                    if (a < 250) hasAlpha = true;
                    if (a > 15 && a < 240) hasSemiTrans = true;
                    pal[i] = new Color32(r, g, b, a);
                }
                pos += 64;

                for (int y = 0; y < height; y++)
                {
                    int srcRow = pos + y * rowBytes;
                    int dstRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        byte bv = data[srcRow + (x >> 1)];
                        int nib = ((x & 1) == 0) ? (bv & 0x0F) : (bv >> 4);
                        pixels[dstRow + x] = pal[nib];
                    }
                }
            }
            else if (typeCode == 17) // RGB888
            {
                int rowBytes = width * 3;
                if (pos + rowBytes * height > data.Length) return default;

                for (int y = 0; y < height; y++)
                {
                    int srcRow = pos + y * rowBytes;
                    int dstRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int px = srcRow + x * 3;
                        pixels[dstRow + x] = new Color32(data[px], data[px + 1], data[px + 2], 255);
                    }
                }
            }
            else if (typeCode == 18) // RGBA8888
            {
                int rowBytes = width * 4;
                if (pos + rowBytes * height > data.Length) return default;

                for (int y = 0; y < height; y++)
                {
                    int srcRow = pos + y * rowBytes;
                    int dstRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int px = srcRow + x * 4;
                        byte a = data[px + 3];
                        if (a < 250) hasAlpha = true;
                        if (a > 15 && a < 240) hasSemiTrans = true;
                        pixels[dstRow + x] = new Color32(data[px], data[px + 1], data[px + 2], a);
                    }
                }
            }
            else
            {
                return default;
            }

            if (hasAlpha)
            {
                long sumR = 0, sumG = 0, sumB = 0, opaqueCount = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a >= 90)
                    {
                        sumR += pixels[i].r;
                        sumG += pixels[i].g;
                        sumB += pixels[i].b;
                        opaqueCount++;
                    }
                }

                if (opaqueCount > 0)
                {
                    byte avgR = (byte)(sumR / opaqueCount);
                    byte avgG = (byte)(sumG / opaqueCount);
                    byte avgB = (byte)(sumB / opaqueCount);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        if (pixels[i].a < 16)
                        {
                            pixels[i] = new Color32(avgR, avgG, avgB, 0);
                        }
                    }
                }
            }

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, true);
            tex.name = name;

            string lowerName = (name ?? string.Empty).ToLowerInvariant();
            bool isClampedFoliage = lowerName.Contains("treesmall") ||
                                    lowerName.Contains("treesamll") ||
                                    lowerName.Contains("palm") ||
                                    lowerName.Contains("glow") ||
                                    lowerName.StartsWith("beta_") ||
                                    lowerName.StartsWith("l-lod");

            tex.wrapMode = isClampedFoliage ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            tex.wrapModeU = (isClampedFoliage && clampU) ? TextureWrapMode.Clamp : tex.wrapMode;
            tex.wrapModeV = (isClampedFoliage && clampV) ? TextureWrapMode.Clamp : tex.wrapMode;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = 4;
            tex.SetPixels32(pixels);
            tex.Apply(true, false);

            return new DecodedTextureInfo
            {
                Texture = tex,
                HasAlpha = hasAlpha,
                HasSemiTransparency = hasSemiTrans,
                ClampU = clampU,
                ClampV = clampV
            };
        }

        public static DecodedTextureInfo DecodeDds(byte[] data, string name, bool forUi = false)
        {
            if (data == null || data.Length < 128) return default;
            if (data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ') return default;

            int height = BitConverter.ToInt32(data, 12);
            int width = BitConverter.ToInt32(data, 16);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return default;

            uint bitCount = BitConverter.ToUInt32(data, 88);
            uint rMask = BitConverter.ToUInt32(data, 92);
            uint gMask = BitConverter.ToUInt32(data, 96);
            uint bMask = BitConverter.ToUInt32(data, 100);
            uint aMask = BitConverter.ToUInt32(data, 104);

            int totalPixels = width * height;
            Color32[] pixels = new Color32[totalPixels];
            int pos = 128;
            bool hasAlpha = false;
            bool hasSemiTrans = false;

            if (bitCount == 16)
            {
                if (pos + totalPixels * 2 > data.Length) return default;

                bool isArgb4444 = (aMask == 0xF000u && rMask == 0x0F00u && gMask == 0x00F0u && bMask == 0x000Fu);
                bool isArgb1555 = (aMask == 0x8000u);

                for (int y = 0; y < height; y++)
                {
                    int srcRow = pos + y * width * 2;
                    int dstRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        ushort px = BitConverter.ToUInt16(data, srcRow + x * 2);
                        byte r, g, b, a;
                        if (isArgb4444)
                        {
                            a = (byte)(((px >> 12) & 0x0F) * 17);
                            r = (byte)(((px >> 8) & 0x0F) * 17);
                            g = (byte)(((px >> 4) & 0x0F) * 17);
                            b = (byte)((px & 0x0F) * 17);
                            if (a < 250) hasAlpha = true;
                            if (a > 15 && a < 240) hasSemiTrans = true;
                        }
                        else if (isArgb1555)
                        {
                            a = (px & 0x8000) != 0 ? (byte)255 : (byte)0;
                            r = (byte)((((px >> 10) & 0x1F) * 255 + 15) / 31);
                            g = (byte)((((px >> 5) & 0x1F) * 255 + 15) / 31);
                            b = (byte)(((px & 0x1F) * 255 + 15) / 31);
                            if (a < 250) hasAlpha = true;
                        }
                        else // RGB565 (0xF800, 0x07E0, 0x001F)
                        {
                            r = (byte)((((px >> 11) & 0x1F) * 255 + 15) / 31);
                            g = (byte)((((px >> 5) & 0x3F) * 255 + 31) / 63);
                            b = (byte)(((px & 0x1F) * 255 + 15) / 31);
                            a = 255;
                        }
                        pixels[dstRow + x] = new Color32(r, g, b, a);
                    }
                }
            }
            else if (bitCount == 32)
            {
                if (pos + totalPixels * 4 > data.Length) return default;
                for (int y = 0; y < height; y++)
                {
                    int srcRow = pos + y * width * 4;
                    int dstRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int off = srcRow + x * 4;
                        byte b = data[off];
                        byte g = data[off + 1];
                        byte r = data[off + 2];
                        byte a = aMask != 0 ? data[off + 3] : (byte)255;
                        if (a < 250) hasAlpha = true;
                        if (a > 15 && a < 240) hasSemiTrans = true;
                        pixels[dstRow + x] = new Color32(r, g, b, a);
                    }
                }
            }
            else if (bitCount == 24)
            {
                if (pos + totalPixels * 3 > data.Length) return default;
                for (int y = 0; y < height; y++)
                {
                    int srcRow = pos + y * width * 3;
                    int dstRow = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int off = srcRow + x * 3;
                        pixels[dstRow + x] = new Color32(data[off + 2], data[off + 1], data[off], 255);
                    }
                }
            }
            else
            {
                return default;
            }

            if (hasAlpha)
            {
                long sumR = 0, sumG = 0, sumB = 0, opaqueCount = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a >= 90)
                    {
                        sumR += pixels[i].r;
                        sumG += pixels[i].g;
                        sumB += pixels[i].b;
                        opaqueCount++;
                    }
                }

                if (opaqueCount > 0)
                {
                    byte avgR = (byte)(sumR / opaqueCount);
                    byte avgG = (byte)(sumG / opaqueCount);
                    byte avgB = (byte)(sumB / opaqueCount);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        if (pixels[i].a < 16)
                        {
                            pixels[i] = new Color32(avgR, avgG, avgB, 0);
                        }
                    }
                }
            }

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, !forUi);
            tex.name = name;

            string lowerName = (name ?? string.Empty).ToLowerInvariant();
            bool isClampedFoliage = forUi ||
                                    lowerName.Contains("treesmall") ||
                                    lowerName.Contains("treesamll") ||
                                    lowerName.Contains("t_tree") ||
                                    lowerName.Contains("palm") ||
                                    lowerName.Contains("glow") ||
                                    lowerName.StartsWith("beta_") ||
                                    lowerName.StartsWith("l-lod");

            tex.wrapMode = isClampedFoliage ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = forUi ? 1 : 4;
            tex.SetPixels32(pixels);
            tex.Apply(!forUi, false);

            return new DecodedTextureInfo
            {
                Texture = tex,
                HasAlpha = hasAlpha,
                HasSemiTransparency = hasSemiTrans,
                ClampU = isClampedFoliage,
                ClampV = isClampedFoliage
            };
        }

        public static DecodedTextureInfo DecodeTga(byte[] data, string name, bool forUi = false)
        {
            if (data == null || data.Length < 18) return default;

            byte idLen = data[0];
            byte cmapType = data[1];
            byte imgType = data[2];
            ushort cmapStart = BitConverter.ToUInt16(data, 3);
            ushort cmapLen = BitConverter.ToUInt16(data, 5);
            byte cmapDepth = data[7];
            ushort width = BitConverter.ToUInt16(data, 12);
            ushort height = BitConverter.ToUInt16(data, 14);
            byte bpp = data[16];
            byte desc = data[17];

            if (width == 0 || height == 0 || width > 4096 || height > 4096) return default;

            // In MM2 DirectX surface convention, top of the visual image maps to v_pkg = 0 (Color32 row 0).
            // Standard TGAs have topOrigin == false (file row 0 is bottom of image), so file row y maps to height - 1 - y.
            // For Unity 2D UI (forUi == true), row 0 is the bottom of the image, so standard TGAs map y -> y.
            bool topOrigin = (desc & 0x20) != 0;
            int pos = 18 + idLen;

            Color32[] palette = null;
            if (cmapType == 1 && cmapLen > 0)
            {
                palette = new Color32[cmapStart + cmapLen];
                int palBytesPerEntry = Math.Max(1, cmapDepth / 8);
                for (int i = 0; i < cmapLen && pos + palBytesPerEntry <= data.Length; i++)
                {
                    palette[cmapStart + i] = ReadTgaPixel(data, ref pos, cmapDepth);
                }
            }

            int totalPixels = width * height;
            Color32[] rawScanlines = new Color32[totalPixels];
            bool isRle = (imgType == 9 || imgType == 10 || imgType == 11);

            if (!isRle)
            {
                for (int i = 0; i < totalPixels && pos < data.Length; i++)
                {
                    if (imgType == 1 && palette != null)
                    {
                        byte idx = data[pos++];
                        rawScanlines[i] = idx < palette.Length ? palette[idx] : new Color32(0, 0, 0, 255);
                    }
                    else if (imgType == 3)
                    {
                        byte g = data[pos++];
                        rawScanlines[i] = new Color32(g, g, g, 255);
                    }
                    else
                    {
                        rawScanlines[i] = ReadTgaPixel(data, ref pos, bpp);
                    }
                }
            }
            else
            {
                int pixIdx = 0;
                while (pixIdx < totalPixels && pos < data.Length)
                {
                    byte header = data[pos++];
                    int count = (header & 0x7F) + 1;
                    if ((header & 0x80) != 0)
                    {
                        Color32 c = ReadTgaElement(data, ref pos, imgType, bpp, palette);
                        for (int k = 0; k < count && pixIdx < totalPixels; k++)
                        {
                            rawScanlines[pixIdx++] = c;
                        }
                    }
                    else
                    {
                        for (int k = 0; k < count && pixIdx < totalPixels && pos < data.Length; k++)
                        {
                            rawScanlines[pixIdx++] = ReadTgaElement(data, ref pos, imgType, bpp, palette);
                        }
                    }
                }
            }

            Color32[] finalPixels = new Color32[totalPixels];
            int minAlpha = 255;
            int maxAlpha = 0;
            bool hasSemi = false;

            for (int y = 0; y < height; y++)
            {
                int dstY;
                if (forUi)
                {
                    dstY = topOrigin ? (height - 1 - y) : y;
                }
                else
                {
                    dstY = topOrigin ? y : (height - 1 - y);
                }
                int srcRow = y * width;
                int dstRow = dstY * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = rawScanlines[srcRow + x];
                    if (c.a < minAlpha) minAlpha = c.a;
                    if (c.a > maxAlpha) maxAlpha = c.a;
                    if (c.a > 15 && c.a < 240) hasSemi = true;
                    finalPixels[dstRow + x] = c;
                }
            }

            // Some 32-bit TGAs exported by older tools have alpha=0 across the entire image when meant to be opaque
            bool hasAlpha = false;
            if (bpp == 32)
            {
                if (maxAlpha == 0)
                {
                    for (int i = 0; i < totalPixels; i++) finalPixels[i].a = 255;
                    hasSemi = false;
                }
                else if (minAlpha < 250)
                {
                    hasAlpha = true;
                }
            }

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, !forUi);
            tex.name = name;
            tex.wrapMode = forUi ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = forUi ? 1 : 4;
            tex.SetPixels32(finalPixels);
            tex.Apply(!forUi, false);

            return new DecodedTextureInfo
            {
                Texture = tex,
                HasAlpha = hasAlpha,
                HasSemiTransparency = hasSemi
            };
        }

        private static Color32 ReadTgaElement(byte[] data, ref int pos, byte imgType, byte bpp, Color32[] palette)
        {
            if (imgType == 9 && palette != null && pos < data.Length)
            {
                byte idx = data[pos++];
                return idx < palette.Length ? palette[idx] : new Color32(0, 0, 0, 255);
            }
            if (imgType == 11 && pos < data.Length)
            {
                byte g = data[pos++];
                return new Color32(g, g, g, 255);
            }
            return ReadTgaPixel(data, ref pos, bpp);
        }

        private static Color32 ReadTgaPixel(byte[] data, ref int pos, byte bpp)
        {
            if (bpp == 32 && pos + 4 <= data.Length)
            {
                byte b = data[pos];
                byte g = data[pos + 1];
                byte r = data[pos + 2];
                byte a = data[pos + 3];
                pos += 4;
                return new Color32(r, g, b, a);
            }
            if (bpp == 24 && pos + 3 <= data.Length)
            {
                byte b = data[pos];
                byte g = data[pos + 1];
                byte r = data[pos + 2];
                pos += 3;
                return new Color32(r, g, b, 255);
            }
            if ((bpp == 16 || bpp == 15) && pos + 2 <= data.Length)
            {
                ushort v = BitConverter.ToUInt16(data, pos);
                pos += 2;
                byte r = (byte)(((v >> 10) & 0x1F) * 255 / 31);
                byte g = (byte)(((v >> 5) & 0x1F) * 255 / 31);
                byte b = (byte)((v & 0x1F) * 255 / 31);
                return new Color32(r, g, b, 255);
            }
            if (pos < data.Length)
            {
                byte g = data[pos++];
                return new Color32(g, g, g, 255);
            }
            return new Color32(255, 255, 255, 255);
        }

        public static AudioClip LoadAudioClip(string waveName)
        {
            if (string.IsNullOrEmpty(waveName)) return null;

            if (AudioCache.TryGetValue(waveName, out AudioClip cached) && cached != null)
            {
                return cached;
            }

            string vPath = MM2ArchiveSystem.FindWavVirtualPath(waveName);
            if (string.IsNullOrEmpty(vPath)) return null;

            if (AudioCache.TryGetValue(vPath, out cached) && cached != null)
            {
                AudioCache[waveName] = cached;
                return cached;
            }

            byte[] bytes = MM2ArchiveSystem.ReadFileBytes(vPath);
            if (bytes == null || bytes.Length < 44) return null;

            AudioClip clip = DecodeWavToAudioClip(bytes, waveName);
            if (clip != null)
            {
                AudioCache[vPath] = clip;
                AudioCache[waveName] = clip;
            }
            return clip;
        }

        public static AudioClip DecodeWavToAudioClip(byte[] data, string clipName)
        {
            if (data == null || data.Length < 44) return null;
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return null;
            if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return null;

            ushort audioFormat = 1;
            ushort channels = 1;
            int sampleRate = 22050;
            ushort bitsPerSample = 16;
            ushort blockAlign = 2;
            int dataOffset = -1;
            int dataLength = 0;

            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                string chunkId = Encoding.ASCII.GetString(data, pos, 4);
                int chunkSize = BitConverter.ToInt32(data, pos + 4);
                pos += 8;
                if (chunkSize < 0 || pos + chunkSize > data.Length)
                {
                    chunkSize = Math.Max(0, data.Length - pos);
                }

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    audioFormat = BitConverter.ToUInt16(data, pos);
                    channels = BitConverter.ToUInt16(data, pos + 2);
                    sampleRate = BitConverter.ToInt32(data, pos + 4);
                    blockAlign = BitConverter.ToUInt16(data, pos + 12);
                    bitsPerSample = BitConverter.ToUInt16(data, pos + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = pos;
                    dataLength = chunkSize;
                    break;
                }

                pos += chunkSize + (chunkSize & 1);
            }

            if (dataOffset < 0 || dataLength <= 0 || channels == 0 || sampleRate <= 0) return null;

            float[] samples = null;

            if (audioFormat == 1) // PCM
            {
                if (bitsPerSample == 16)
                {
                    int sampleCount = dataLength / 2;
                    samples = new float[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                    {
                        short s = BitConverter.ToInt16(data, dataOffset + i * 2);
                        samples[i] = s / 32768f;
                    }
                }
                else if (bitsPerSample == 8)
                {
                    samples = new float[dataLength];
                    for (int i = 0; i < dataLength; i++)
                    {
                        samples[i] = (data[dataOffset + i] - 128) / 128f;
                    }
                }
            }
            else if (audioFormat == 17 && blockAlign > 0) // IMA ADPCM (4-bit)
            {
                samples = DecodeImaAdpcm(data, dataOffset, dataLength, channels, blockAlign);
            }

            if (samples == null || samples.Length == 0) return null;

            int lengthSamples = samples.Length / channels;
            if (lengthSamples <= 0) return null;

            AudioClip clip = AudioClip.Create(clipName, lengthSamples, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static readonly int[] ImaIndexTable =
        {
            -1, -1, -1, -1, 2, 4, 6, 8,
            -1, -1, -1, -1, 2, 4, 6, 8
        };

        private static readonly int[] ImaStepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
            19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
            337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
            2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
            5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        };

        private static float[] DecodeImaAdpcm(byte[] data, int offset, int length, int channels, int blockAlign)
        {
            var outList = new List<float>(length * 2);
            int end = offset + length;
            int pos = offset;

            while (pos + 4 * channels <= end)
            {
                int blockEnd = Math.Min(end, pos + blockAlign);
                short predictor = BitConverter.ToInt16(data, pos);
                int stepIdx = Mathf.Clamp((int)data[pos + 2], 0, 88);
                pos += 4 * channels;

                outList.Add(predictor / 32768f);

                while (pos < blockEnd)
                {
                    byte b = data[pos++];
                    for (int nibIdx = 0; nibIdx < 2; nibIdx++)
                    {
                        int nibble = (nibIdx == 0) ? (b & 0x0F) : (b >> 4);
                        int step = ImaStepTable[stepIdx];
                        int diff = step >> 3;
                        if ((nibble & 1) != 0) diff += step >> 2;
                        if ((nibble & 2) != 0) diff += step >> 1;
                        if ((nibble & 4) != 0) diff += step;
                        if ((nibble & 8) != 0) diff = -diff;

                        predictor = (short)Mathf.Clamp(predictor + diff, -32768, 32767);
                        stepIdx = Mathf.Clamp(stepIdx + ImaIndexTable[nibble], 0, 88);
                        outList.Add(predictor / 32768f);
                    }
                }
            }

            return outList.ToArray();
        }
    }
}
