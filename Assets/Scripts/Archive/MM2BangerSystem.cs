using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace MM2.Archive
{
    [Serializable]
    public sealed class MM2BangerSpec
    {
        public string PkgName;
        public Vector3 Size = Vector3.one;
        public Vector3 CG = Vector3.zero;
        public float Mass = 50f;
        public float Elasticity = 0.4f;
        public float Friction = 0.5f;
        public float ImpulseLimit2 = 100f;
        public float YRadius = 0.5f;
        public int CollisionPrim = 1;
        public int NumParts = 0;
        public int AudioId = 0;

        public bool IsTreeOrFoliage =>
            !string.IsNullOrEmpty(PkgName) && (
                PkgName.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                PkgName.IndexOf("palm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                PkgName.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0 ||
                PkgName.IndexOf("hedge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                PkgName.IndexOf("plant", StringComparison.OrdinalIgnoreCase) >= 0
            );

        public bool IsImmovableObstacle
        {
            get
            {
                if (string.IsNullOrEmpty(PkgName))
                    return false;

                // Trees, palms, bushes, streetlights, traffic lights, poles, signs, awnings, doors, and windows are always destructible
                if (IsTreeOrFoliage || IsGlassOrDoor ||
                    PkgName.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("traflit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("pole", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("sign", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("awning", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("banr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("banner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("exitfwy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }

                // Heavy structural obstacles (freeway supports, overhead highway exit gantries, arches, monuments, bridges, ships)
                if (PkgName.IndexOf("fwsupport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("exit_f", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("chinagate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("arch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("pillar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("pilaster", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("covent_col", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("stairs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("harrod_endcap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("alcatraz", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("ghirardelli", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("telcom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("eros", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("cenotaph", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("tanker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("carferry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("jumptrailer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("ramp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    PkgName.IndexOf("bridge", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                return NumParts == 0 && (ImpulseLimit2 >= 5e7f || Mass >= 50000f);
            }
        }

        public bool IsGlassOrDoor =>
            AudioId == 19 ||
            PkgName.IndexOf("win", StringComparison.OrdinalIgnoreCase) >= 0 ||
            PkgName.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0 ||
            PkgName.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0;

        public bool AlwaysShatterOnHit =>
            IsGlassOrDoor ||
            IsTreeOrFoliage ||
            PkgName.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0 ||
            PkgName.IndexOf("barel", StringComparison.OrdinalIgnoreCase) >= 0 ||
            PkgName.IndexOf("roadwork", StringComparison.OrdinalIgnoreCase) >= 0 ||
            PkgName.IndexOf("woodbar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            PkgName.IndexOf("busstop", StringComparison.OrdinalIgnoreCase) >= 0 ||
            PkgName.IndexOf("awning", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public sealed class MM2BangerPartBundle
    {
        public string PartName;
        public Mesh SharedMesh;
        public Material[] SharedMaterials;
        public MM2BangerSpec PartSpec;
    }

    public sealed class MM2BangerMeshBundle
    {
        public string PkgName;
        public int Variant;
        public Mesh SharedMesh;
        public Material[] SharedMaterials;
        public Bounds MeshBounds;
        public readonly List<MM2BangerPartBundle> BreakParts = new List<MM2BangerPartBundle>();
    }

    public static class MM2BangerSystem
    {
        private static readonly Dictionary<string, MM2BangerSpec> specCache =
            new Dictionary<string, MM2BangerSpec>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, MM2BangerMeshBundle> bundleCache =
            new Dictionary<string, MM2BangerMeshBundle>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Material> materialCache =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, AudioClip> audioCache =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        private static Mesh cachedGlassShardMesh;

        public static void ClearCaches()
        {
            bundleCache.Clear();
        }

        public static bool TryGetBangerSpec(string pkgName, out MM2BangerSpec spec)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(pkgName)) return false;

            string key = pkgName.Trim().ToLowerInvariant();
            if (specCache.TryGetValue(key, out spec))
            {
                return spec != null;
            }

            string text = MM2ArchiveSystem.ReadFileText($"tune/banger/{key}.dgBangerData");
            if (string.IsNullOrEmpty(text))
            {
                text = MM2ArchiveSystem.ReadFileText($"tune/{key}.mmbangerdata");
            }
            if (string.IsNullOrEmpty(text))
            {
                specCache[key] = null;
                return false;
            }

            spec = ParseBangerDataText(key, text);
            specCache[key] = spec;
            return spec != null;
        }

        private static MM2BangerSpec ParseBangerDataText(string pkgName, string text)
        {
            var spec = new MM2BangerSpec { PkgName = pkgName };
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int braceDepth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                int openBraces = 0;
                int closeBraces = 0;
                for (int c = 0; c < line.Length; c++)
                {
                    if (line[c] == '{') openBraces++;
                    else if (line[c] == '}') closeBraces++;
                }

                // Only parse top-level dgBangerData properties (ignore nested BirthRule { ... } fields)
                if (braceDepth <= 1)
                {
                    string[] tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length >= 2)
                    {
                        string field = tok[0];
                        if (string.Equals(field, "Size", StringComparison.OrdinalIgnoreCase) && tok.Length >= 4)
                        {
                            spec.Size = new Vector3(ParseFloat(tok[1], 1f), ParseFloat(tok[2], 1f), ParseFloat(tok[3], 1f));
                        }
                        else if (string.Equals(field, "CG", StringComparison.OrdinalIgnoreCase) && tok.Length >= 4)
                        {
                            spec.CG = new Vector3(ParseFloat(tok[1], 0f), ParseFloat(tok[2], 0f), ParseFloat(tok[3], 0f));
                        }
                        else if (string.Equals(field, "Mass", StringComparison.OrdinalIgnoreCase))
                        {
                            spec.Mass = Mathf.Max(1f, ParseFloat(tok[1], 50f));
                        }
                        else if (string.Equals(field, "Elasticity", StringComparison.OrdinalIgnoreCase))
                        {
                            spec.Elasticity = Mathf.Clamp01(ParseFloat(tok[1], 0.4f));
                        }
                        else if (string.Equals(field, "Friction", StringComparison.OrdinalIgnoreCase))
                        {
                            spec.Friction = Mathf.Clamp01(ParseFloat(tok[1], 0.5f));
                        }
                        else if (string.Equals(field, "ImpulseLimit2", StringComparison.OrdinalIgnoreCase))
                        {
                            spec.ImpulseLimit2 = ParseFloat(tok[1], 100f);
                        }
                        else if (string.Equals(field, "YRadius", StringComparison.OrdinalIgnoreCase))
                        {
                            spec.YRadius = Mathf.Max(0.15f, ParseFloat(tok[1], 0.5f));
                        }
                        else if (string.Equals(field, "CollisionPrim", StringComparison.OrdinalIgnoreCase))
                        {
                            spec.CollisionPrim = (int)ParseFloat(tok[1], 1f);
                        }
                        else if (string.Equals(field, "NumParts", StringComparison.OrdinalIgnoreCase))
                        {
                            spec.NumParts = Mathf.Max(0, (int)ParseFloat(tok[1], 0f));
                        }
                        else if (string.Equals(field, "AudioId", StringComparison.OrdinalIgnoreCase))
                        {
                            spec.AudioId = (int)ParseFloat(tok[1], 0f);
                        }
                    }
                }

                braceDepth += openBraces - closeBraces;
            }

            return spec;
        }

        private static float ParseFloat(string s, float fallback)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) &&
                   !float.IsNaN(v)
                ? v
                : fallback;
        }

        public static MM2BangerMeshBundle GetOrCreateMeshBundle(string pkgName, int variant, MM2BangerSpec spec)
        {
            string cleanName = pkgName.Trim().ToLowerInvariant();
            MM2RuntimePkgLoader.ParsedPkg tmpl = MM2RuntimePkgLoader.LoadPkg(cleanName);
            if (tmpl == null) return null;

            bool isTrafficLight = cleanName.IndexOf("traflit", StringComparison.OrdinalIgnoreCase) >= 0;
            int numVariants = isTrafficLight ? 3 : Mathf.Max(1, tmpl.NumPaintjobs);
            int variantIdx = Mathf.Abs(variant) % numVariants;
            int pj = tmpl.NumPaintjobs > 0 ? variantIdx % tmpl.NumPaintjobs : 0;

            string cacheKey = $"{cleanName}#{variantIdx}";
            if (bundleCache.TryGetValue(cacheKey, out MM2BangerMeshBundle cached) &&
                cached != null && cached.SharedMesh != null)
            {
                return cached;
            }

            string[] lodOrder = { "H", "M", "L", "VL", "BODY_H" };
            List<MM2RuntimePkgLoader.PkgSection> mainSections = null;
            foreach (string lod in lodOrder)
            {
                if (tmpl.GeometryChunks.TryGetValue(lod, out mainSections) && mainSections != null && mainSections.Count > 0)
                {
                    break;
                }
            }
            if (mainSections == null || mainSections.Count == 0) return null;

            List<MM2RuntimePkgLoader.PkgSection> combinedMainSections = mainSections;
            if (isTrafficLight)
            {
                combinedMainSections = new List<MM2RuntimePkgLoader.PkgSection>(mainSections);
                string[] glowChunks = variantIdx == 0
                    ? new[] { "GREENGLOWDAY_H", "WALK_DAY_H" }
                    : (variantIdx == 1
                        ? new[] { "REDGLOWDAY_H", "NOWALK_DAY_H" }
                        : new[] { "YELLOWGLOWDAY_H", "NOWALK_DAY_H" });

                Vector3 cg = spec != null ? spec.CG : Vector3.zero;
                for (int g = 0; g < glowChunks.Length; g++)
                {
                    if (tmpl.GeometryChunks.TryGetValue(glowChunks[g], out List<MM2RuntimePkgLoader.PkgSection> glowSecs) &&
                        glowSecs != null)
                    {
                        for (int s = 0; s < glowSecs.Count; s++)
                        {
                            MM2RuntimePkgLoader.PkgSection srcSec = glowSecs[s];
                            if (srcSec == null || srcSec.Positions.Count == 0 || srcSec.Triangles.Count < 3) continue;

                            var shiftedSec = new MM2RuntimePkgLoader.PkgSection
                            {
                                ShaderOffset = srcSec.ShaderOffset
                            };
                            for (int v = 0; v < srcSec.Positions.Count; v++)
                            {
                                Vector3 gp = srcSec.Positions[v];
                                shiftedSec.Positions.Add(new Vector3(gp.x - cg.x, gp.y - cg.y, gp.z - cg.z + 0.015f));
                            }
                            shiftedSec.Normals.AddRange(srcSec.Normals);
                            shiftedSec.UVs.AddRange(srcSec.UVs);
                            shiftedSec.Triangles.AddRange(srcSec.Triangles);
                            combinedMainSections.Add(shiftedSec);
                        }
                    }
                }
            }

            bool forceGlassStyle = spec != null && spec.IsGlassOrDoor;
            if (!BuildFlippedMeshAndMaterials(
                    $"{cleanName}_v{variantIdx}",
                    tmpl,
                    combinedMainSections,
                    pj,
                    forceGlassStyle,
                    out Mesh mainMesh,
                    out Material[] mainMats))
            {
                return null;
            }

            var bundle = new MM2BangerMeshBundle
            {
                PkgName = cleanName,
                Variant = variantIdx,
                SharedMesh = mainMesh,
                SharedMaterials = mainMats,
                MeshBounds = mainMesh.bounds
            };

            // Build BREAK01..BREAK08 sub-part meshes if available in this .pkg
            for (int partIdx = 1; partIdx <= 8; partIdx++)
            {
                string prefix = $"BREAK{partIdx:D2}";
                List<MM2RuntimePkgLoader.PkgSection> partSections = null;
                string[] partLodOrder = { $"{prefix}_H", $"{prefix}_M", $"{prefix}_L", prefix };
                foreach (string pLod in partLodOrder)
                {
                    if (tmpl.GeometryChunks.TryGetValue(pLod, out partSections) &&
                        partSections != null && partSections.Count > 0)
                    {
                        break;
                    }
                }

                if (partSections == null || partSections.Count == 0) continue;

                if (BuildFlippedMeshAndMaterials(
                        $"{cleanName}_{prefix}_v{pj}",
                        tmpl,
                        partSections,
                        pj,
                        forceGlassStyle,
                        out Mesh partMesh,
                        out Material[] partMats))
                {
                    string partSpecName = $"{cleanName}_{prefix}";
                    if (!TryGetBangerSpec(partSpecName, out MM2BangerSpec partSpec) || partSpec == null)
                    {
                        partSpec = new MM2BangerSpec
                        {
                            PkgName = partSpecName,
                            Size = partMesh.bounds.size,
                            CG = spec != null ? spec.CG : Vector3.zero,
                            Mass = spec != null ? Mathf.Max(5f, spec.Mass * 0.35f) : 15f,
                            Elasticity = spec != null ? spec.Elasticity : 0.4f,
                            Friction = spec != null ? spec.Friction : 0.5f
                        };
                    }

                    bundle.BreakParts.Add(new MM2BangerPartBundle
                    {
                        PartName = prefix,
                        SharedMesh = partMesh,
                        SharedMaterials = partMats,
                        PartSpec = partSpec
                    });
                }
            }

            bundleCache[cacheKey] = bundle;
            return bundle;
        }

        private static bool BuildFlippedMeshAndMaterials(
            string meshName,
            MM2RuntimePkgLoader.ParsedPkg tmpl,
            List<MM2RuntimePkgLoader.PkgSection> sections,
            int paintjobIndex,
            bool isGlassProp,
            out Mesh mesh,
            out Material[] materials
        )
        {
            mesh = null;
            materials = null;

            bool isFoliageProp = !string.IsNullOrEmpty(tmpl.BaseName) &&
                                 (tmpl.BaseName.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  tmpl.BaseName.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  tmpl.BaseName.IndexOf("palm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  tmpl.BaseName.IndexOf("hedge", StringComparison.OrdinalIgnoreCase) >= 0);

            int pjBase = paintjobIndex * tmpl.ShadersPerPaintjob;
            var allPos = new List<Vector3>();
            var allNorm = new List<Vector3>();
            var allUV = new List<Vector2>();
            var subTris = new List<int[]>();
            var mats = new List<Material>();

            foreach (var sec in sections)
            {
                if (sec.Positions.Count == 0 || sec.Triangles.Count < 3) continue;

                int shIdx = pjBase + sec.ShaderOffset;
                MM2RuntimePkgLoader.PkgShaderEntry shaderEntry =
                    (shIdx >= 0 && shIdx < tmpl.Shaders.Count)
                        ? tmpl.Shaders[shIdx]
                        : new MM2RuntimePkgLoader.PkgShaderEntry
                        {
                            TextureName = string.Empty,
                            DiffuseColor = new Color32(220, 220, 220, 255)
                        };

                int baseV = allPos.Count;
                int vCount = sec.Positions.Count;
                for (int i = 0; i < vCount; i++)
                {
                    Vector3 p = sec.Positions[i];
                    Vector3 n = i < sec.Normals.Count ? sec.Normals[i] : Vector3.up;
                    Vector2 uv = i < sec.UVs.Count ? sec.UVs[i] : Vector2.zero;

                    allPos.Add(new Vector3(-p.x, p.y, p.z));
                    Vector3 flippedNorm = new Vector3(-n.x, n.y, n.z).normalized;
                    if (isFoliageProp)
                    {
                        // Point crossed-billboard tree/bush normals mostly upward so perpendicular planes
                        // receive uniform daylight without a harsh 90-degree dark seam down the center.
                        flippedNorm = new Vector3(flippedNorm.x * 0.15f, 0.98f, flippedNorm.z * 0.15f).normalized;
                    }
                    allNorm.Add(flippedNorm);
                    allUV.Add(uv);
                }

                var tris = new int[sec.Triangles.Count];
                for (int i = 0; i + 2 < sec.Triangles.Count; i += 3)
                {
                    // Reverse winding for left-handed X flip (-p.x)
                    tris[i] = baseV + sec.Triangles[i + 2];
                    tris[i + 1] = baseV + sec.Triangles[i + 1];
                    tris[i + 2] = baseV + sec.Triangles[i];
                }

                subTris.Add(tris);
                mats.Add(GetOrCreatePropMaterial(shaderEntry, isGlassProp, isFoliageProp));
            }

            if (allPos.Count == 0 || subTris.Count == 0) return false;

            mesh = new Mesh { name = meshName };
            mesh.SetVertices(allPos);
            mesh.SetNormals(allNorm);
            mesh.SetUVs(0, allUV);
            mesh.subMeshCount = subTris.Count;
            for (int s = 0; s < subTris.Count; s++)
            {
                mesh.SetTriangles(subTris[s], s, false);
            }
            mesh.RecalculateBounds();
            materials = mats.ToArray();
            return true;
        }

        private static Material GetOrCreatePropMaterial(
            MM2RuntimePkgLoader.PkgShaderEntry sh,
            bool isGlassProp,
            bool isFoliageProp = false
        )
        {
            string texName = CleanTexName(sh.TextureName);
            string key = $"{texName}_{sh.DiffuseColor.r}_{sh.DiffuseColor.g}_{sh.DiffuseColor.b}_{sh.DiffuseColor.a}_{(isGlassProp ? 1 : 0)}_{(isFoliageProp ? 1 : 0)}";
            if (materialCache.TryGetValue(key, out Material mat) && mat != null)
            {
                if (!string.IsNullOrEmpty(texName))
                {
                    MM2EnvironmentManager.RegisterDynamicMaterial(mat, texName);
                }
                return mat;
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            mat = new Material(urpLit)
            {
                name = string.IsNullOrEmpty(texName) ? "Mat_Banger_Color" : $"Mat_Banger_{texName}",
                enableInstancing = true
            };

            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            float smoothness = isGlassProp ? 0.75f : (isFoliageProp ? 0.0f : 0.14f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_SpecularHighlights") && isFoliageProp) mat.SetFloat("_SpecularHighlights", 0f);

            Color baseCol = sh.DiffuseColor;
            if (string.IsNullOrEmpty(texName) && baseCol.r < 0.05f && baseCol.g < 0.05f && baseCol.b < 0.05f)
            {
                baseCol = new Color(0.18f, 0.19f, 0.20f, 1f);
            }

            bool isGlowSprite = !string.IsNullOrEmpty(texName) &&
                                texName.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isGlassTex = isGlassProp ||
                              (!string.IsNullOrEmpty(texName) &&
                               texName.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0);

            if (!string.IsNullOrEmpty(texName))
            {
                var texInfo = MM2RuntimeDecoders.LoadTexture(texName);
                if (texInfo.Texture != null)
                {
                    mat.mainTexture = texInfo.Texture;
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texInfo.Texture);
                    // Always use white tint when a texture map is present (matching city & vehicle loaders)
                    // so MM2's internal shader byte metadata does not darken tree/prop textures.
                    baseCol = Color.white;

                    if (isGlowSprite)
                    {
                        mat.SetFloat("_Surface", 1f);
                        mat.SetFloat("_Blend", 0f);
                        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                        mat.SetFloat("_ZWrite", 0f);
                        mat.SetFloat("_AlphaClip", 0f);
                        mat.DisableKeyword("_ALPHATEST_ON");
                        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        mat.SetOverrideTag("RenderType", "Transparent");
                        mat.SetShaderPassEnabled("DepthOnly", false);
                        mat.renderQueue = (int)RenderQueue.Transparent;
                        mat.EnableKeyword("_EMISSION");
                        if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", texInfo.Texture);
                        if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", new Color(1.35f, 1.35f, 1.35f, 1f));
                    }
                    else if (isGlassTex)
                    {
                        // Breakable glass windows & doors (e.g. s_glass on sp_office_door_f / sp_transbay_win_f / ep_transam_door_f,
                        // or Harrods storefront panes kp_harrod_winbot_*): use true URP transparent alpha blending.
                        // s_glass.tex has built-in alpha ~0.52; multiplying by 0.72 yields crisp ~38% opacity glass,
                        // while opaque-palette storefront textures get 0.48 alpha so you can see inside behind the glass.
                        float glassAlpha = texInfo.HasAlpha ? 0.72f : 0.48f;
                        baseCol = new Color(0.92f, 0.97f, 1.0f, glassAlpha);

                        mat.SetFloat("_Surface", 1f);
                        mat.SetFloat("_Blend", 0f);
                        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                        if (mat.HasProperty("_SrcBlendAlpha")) mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                        if (mat.HasProperty("_DstBlendAlpha")) mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                        mat.SetFloat("_ZWrite", 0f);
                        mat.SetFloat("_AlphaClip", 0f);
                        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.85f);
                        mat.DisableKeyword("_ALPHATEST_ON");
                        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        mat.SetOverrideTag("RenderType", "Transparent");
                        mat.SetShaderPassEnabled("DepthOnly", false);
                        mat.SetShaderPassEnabled("ShadowCaster", false);
                        mat.renderQueue = (int)RenderQueue.Transparent;
                    }
                    else if (texInfo.HasAlpha || isFoliageProp)
                    {
                        mat.SetFloat("_Surface", 0f);
                        mat.SetFloat("_AlphaClip", 1f);
                        mat.SetFloat("_Cutoff", 0.35f);
                        mat.EnableKeyword("_ALPHATEST_ON");
                        mat.SetOverrideTag("RenderType", "TransparentCutout");
                        mat.renderQueue = (int)RenderQueue.AlphaTest;
                    }
                }
            }
            else if (isGlassTex)
            {
                baseCol = new Color(0.80f, 0.90f, 0.98f, 0.42f);
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (mat.HasProperty("_SrcBlendAlpha")) mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                if (mat.HasProperty("_DstBlendAlpha")) mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.SetFloat("_AlphaClip", 0f);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.85f);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetShaderPassEnabled("DepthOnly", false);
                mat.SetShaderPassEnabled("ShadowCaster", false);
                mat.renderQueue = (int)RenderQueue.Transparent;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseCol);
            mat.color = baseCol;

            materialCache[key] = mat;
            if (!string.IsNullOrEmpty(texName))
            {
                MM2EnvironmentManager.RegisterDynamicMaterial(mat, texName);
            }
            return mat;
        }

        private static string CleanTexName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = raw.Trim().Replace('\\', '/');
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            if (dot >= 0) s = s.Substring(0, dot);
            return s.ToLowerInvariant();
        }

        public static GameObject SpawnBangerProp(
            Transform parent,
            string pkgName,
            int variant,
            MM2BangerSpec spec,
            Vector3 xa,
            Vector3 ya,
            Vector3 za,
            Vector3 origAge
        )
        {
            if (spec == null) return null;
            MM2BangerMeshBundle bundle = GetOrCreateMeshBundle(pkgName, variant, spec);
            if (bundle == null || bundle.SharedMesh == null) return null;

            // Compute world position of the CG in AGE space and convert to Unity left-handed space
            Vector3 cg = spec.CG;
            Vector3 centerAge = new Vector3(
                origAge.x + cg.x * xa.x + cg.y * ya.x + cg.z * za.x,
                origAge.y + cg.x * xa.y + cg.y * ya.y + cg.z * za.y,
                origAge.z + cg.x * xa.z + cg.y * ya.z + cg.z * za.z
            );
            Vector3 posUnity = new Vector3(-centerAge.x, centerAge.y, centerAge.z);

            Vector3 fwdUnity = new Vector3(-za.x, za.y, za.z);
            Vector3 upUnity = new Vector3(-ya.x, ya.y, ya.z);
            Quaternion rotUnity = fwdUnity.sqrMagnitude > 0.0001f && upUnity.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(fwdUnity.normalized, upUnity.normalized)
                : Quaternion.identity;

            GameObject go = new GameObject($"Banger_{bundle.PkgName}");
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(posUnity, rotUnity);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = bundle.SharedMesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = bundle.SharedMaterials;

            // Attach trigger BoxCollider sized from pole/mesh bounds so cars never get stopped dead for 1 frame
            var box = go.AddComponent<BoxCollider>();
            Bounds b = bundle.MeshBounds;
            bool isOverhangingPole = spec.Size.y > 2.5f &&
                                     spec.Size.x < 1.2f &&
                                     spec.Size.z < 1.2f &&
                                     b.size.x > spec.Size.x * 2.2f;
            if (isOverhangingPole)
            {
                // For overhanging streetlamps and cantilever traffic lights, keep the trigger collider on the pole
                // so vehicles driving underneath the overhanging arm in the road do not falsely hit the pole.
                box.center = new Vector3(0f, b.center.y, 0f);
                box.size = new Vector3(
                    Mathf.Max(spec.Size.x, 0.55f),
                    Mathf.Max(spec.Size.y, 1.0f),
                    Mathf.Max(spec.Size.z, 0.55f)
                );
            }
            else if (spec.IsTreeOrFoliage && b.size.y > 2.2f)
            {
                // Keep tall tree trigger colliders centered on the trunk rather than the wide overhead leaf canopy
                float trunkWidth = Mathf.Clamp(Mathf.Min(b.size.x, b.size.z) * 0.45f, 0.85f, 1.45f);
                box.center = new Vector3(0f, b.center.y, 0f);
                box.size = new Vector3(trunkWidth, Mathf.Max(b.size.y, 1.5f), trunkWidth);
            }
            else
            {
                Vector3 colSize = b.size;
                colSize.x = Mathf.Max(colSize.x, 0.45f);
                colSize.y = Mathf.Max(colSize.y, 0.55f);
                colSize.z = Mathf.Max(colSize.z, 0.45f);
                box.center = b.center;
                box.size = colSize;
            }
            box.isTrigger = true;

            var prop = go.AddComponent<MM2BangerProp>();
            prop.Initialize(spec, bundle, box, xa, ya, za, origAge);
            return go;
        }

        public static AudioClip GetImpactClip(string pkgName, int audioId)
        {
            string stem = ResolveImpactSoundStem(pkgName, audioId);
            if (string.IsNullOrEmpty(stem)) return null;

            if (audioCache.TryGetValue(stem, out AudioClip clip))
            {
                return clip;
            }

            clip = MM2RuntimeDecoders.LoadAudioClip(stem);
            audioCache[stem] = clip;
            return clip;
        }

        private static string ResolveImpactSoundStem(string pkgName, int audioId)
        {
            string n = (pkgName ?? string.Empty).ToLowerInvariant();

            if (audioId == 19 || n.Contains("win") || n.Contains("door") || n.Contains("glass") || n.Contains("busstop"))
                return "glassbreak";
            if (n.Contains("cone") || n.Contains("barel"))
                return "OrangeCone";
            if (n.Contains("tree") || n.Contains("bush") || n.Contains("palm") || n.Contains("hedge"))
                return "bushimpact";
            if (n.Contains("parkmtr") || n.Contains("bollard"))
                return "parkingmeter6";
            if (n.Contains("mailbox"))
                return "mailimpact1";
            if (n.Contains("phone"))
                return "phoneimpact";
            if (n.Contains("bench") || n.Contains("table"))
                return "parkbenchimpact";
            if (n.Contains("can_") || n.Contains("dumpstr") || n.Contains("trash") || n.Contains("hotdog"))
                return "trashimpact";
            if (n.Contains("roadwork") || n.Contains("woodbar") || n.Contains("crate") || n.Contains("awning"))
                return "roadworksawhorse";
            if (n.Contains("light") || n.Contains("traflit") || n.Contains("telepole"))
                return "trafficlightimpact";
            if (n.Contains("stop") || n.Contains("xing") || n.Contains("sign") || n.Contains("banner"))
                return "signimpact2";

            return "signimpact3";
        }

        public static Mesh GetGlassShardMesh()
        {
            if (cachedGlassShardMesh != null) return cachedGlassShardMesh;

            cachedGlassShardMesh = new Mesh { name = "MM2_GlassShard_Mesh" };
            cachedGlassShardMesh.vertices = new[]
            {
                new Vector3(0f, 0.28f, 0f),
                new Vector3(-0.24f, -0.20f, 0.02f),
                new Vector3(0.22f, -0.16f, -0.02f),
                new Vector3(0.04f, -0.02f, 0.05f)
            };
            cachedGlassShardMesh.normals = new[]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
                Vector3.back
            };
            cachedGlassShardMesh.uv = new[]
            {
                new Vector2(0.5f, 0.9f),
                new Vector2(0.1f, 0.1f),
                new Vector2(0.9f, 0.2f),
                new Vector2(0.5f, 0.5f)
            };
            cachedGlassShardMesh.triangles = new[]
            {
                0, 1, 2,
                0, 2, 1,
                0, 2, 3,
                0, 1, 3
            };
            cachedGlassShardMesh.RecalculateBounds();
            return cachedGlassShardMesh;
        }
    }

    public sealed class MM2BangerProp : MonoBehaviour
    {
        public MM2BangerSpec Spec { get; private set; }
        public MM2BangerMeshBundle Bundle { get; private set; }

        [SerializeField] private string pkgName;
        [SerializeField] private int variantIndex;
        [SerializeField] private MM2BangerSpec serializedSpec;
        [SerializeField] private BoxCollider propCollider;
        [SerializeField] private Vector3 xaAge;
        [SerializeField] private Vector3 yaAge;
        [SerializeField] private Vector3 zaAge;
        [SerializeField] private Vector3 origAge;
        private bool isActivated;

        public void Initialize(
            MM2BangerSpec spec,
            MM2BangerMeshBundle bundle,
            BoxCollider col,
            Vector3 xa,
            Vector3 ya,
            Vector3 za,
            Vector3 orig
        )
        {
            Spec = spec;
            Bundle = bundle;
            serializedSpec = spec;
            pkgName = spec != null ? spec.PkgName : (bundle != null ? bundle.PkgName : string.Empty);
            variantIndex = bundle != null ? bundle.Variant : 0;
            propCollider = col;
            xaAge = xa;
            yaAge = ya;
            zaAge = za;
            origAge = orig;
        }

        private bool EnsureInitialized()
        {
            if (propCollider == null)
            {
                propCollider = GetComponent<BoxCollider>();
            }

            if (Spec == null)
            {
                if (serializedSpec != null && !string.IsNullOrEmpty(serializedSpec.PkgName))
                {
                    Spec = serializedSpec;
                }
                else
                {
                    string lookupName = !string.IsNullOrEmpty(pkgName)
                        ? pkgName
                        : (name.StartsWith("Banger_", StringComparison.OrdinalIgnoreCase) ? name.Substring(7) : name);
                    if (MM2BangerSystem.TryGetBangerSpec(lookupName, out MM2BangerSpec recoveredSpec))
                    {
                        Spec = recoveredSpec;
                        serializedSpec = recoveredSpec;
                        pkgName = lookupName;
                    }
                }
            }

            if (Bundle == null && Spec != null && !string.IsNullOrEmpty(Spec.PkgName))
            {
                Bundle = MM2BangerSystem.GetOrCreateMeshBundle(Spec.PkgName, variantIndex, Spec);
                var mf = GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh == null && Bundle != null)
                {
                    mf.sharedMesh = Bundle.SharedMesh;
                }
                var mr = GetComponent<MeshRenderer>();
                if (mr != null && (mr.sharedMaterials == null || mr.sharedMaterials.Length == 0 || mr.sharedMaterials[0] == null) && Bundle != null)
                {
                    mr.sharedMaterials = Bundle.SharedMaterials;
                }
            }

            if (Spec != null && propCollider != null)
            {
                if (Spec.IsImmovableObstacle)
                {
                    propCollider.isTrigger = false;
                }
                else if (!isActivated)
                {
                    propCollider.isTrigger = true;
                }
            }

            return Spec != null;
        }

        private void OnEnable()
        {
            EnsureInitialized();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (isActivated || other == null) return;
            if (!EnsureInitialized()) return;
            if (Spec.IsImmovableObstacle)
            {
                if (propCollider != null) propCollider.isTrigger = false;
                return;
            }

            Rigidbody hitRb = other.attachedRigidbody;
            if (hitRb == null || hitRb.gameObject == gameObject) return;

            // Only take damage from the player or other vehicles — never from flying bangers or debris
            if (hitRb.GetComponent<MM2BangerProp>() != null) return;
            bool isVehicle = hitRb.GetComponent<MM2.Vehicle.ArcadeVehicleController>() != null ||
                             hitRb.CompareTag("Player") ||
                             hitRb.mass >= 250f;
            if (!isVehicle) return;

            ActivateOnImpact(hitRb, other);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (isActivated || collision == null || collision.collider == null) return;
            OnTriggerEnter(collision.collider);
        }

        private void ActivateOnImpact(Rigidbody hitterRb, Collider hitterCollider)
        {
            if (isActivated) return;
            isActivated = true;

            Vector3 hitterVel = hitterRb != null ? hitterRb.linearVelocity : Vector3.zero;
            float speed = hitterVel.magnitude;

            Vector3 hitDir = transform.position - (hitterRb != null ? hitterRb.worldCenterOfMass : transform.position - Vector3.forward);
            hitDir.y = 0f;
            if (hitterVel.sqrMagnitude > 1.0f)
            {
                Vector3 velFlat = new Vector3(hitterVel.x, 0f, hitterVel.z).normalized;
                hitDir = (velFlat * 0.78f + (hitDir.sqrMagnitude > 0.001f ? hitDir.normalized * 0.22f : velFlat)).normalized;
            }
            else if (hitDir.sqrMagnitude > 0.001f)
            {
                hitDir.Normalize();
            }
            else
            {
                hitDir = Vector3.forward;
            }

            // Apply a proportional arcade speed scrub to the vehicle without brick-walling it
            if (hitterRb != null && hitterRb.mass >= 250f && speed > 1.5f)
            {
                var vehicleDamage = hitterRb.GetComponent<MM2.Vehicle.MM2VehicleDamage>();
                if (vehicleDamage != null)
                {
                    float propMass = (Spec != null && Spec.Mass > 1f) ? Spec.Mass : 35f;
                    vehicleDamage.NotifyPropImpact(
                        propCollider,
                        transform.position,
                        -hitDir,
                        speed,
                        propMass
                    );
                }

                float dragFactor = Spec.IsGlassOrDoor
                    ? 0.01f
                    : (Spec.IsTreeOrFoliage
                        ? 0.05f
                        : Mathf.Clamp(Spec.Mass / 2600f, 0.005f, 0.12f));
                hitterRb.linearVelocity = hitterVel * (1f - dragFactor);
            }

            // Play authentic MM2 impact/shatter 3D sound effect
            PlayImpactAudio(speed);

            bool hasBreakParts = Bundle != null && Bundle.BreakParts != null && Bundle.BreakParts.Count > 0;
            bool shouldShatter = Spec.IsGlassOrDoor ||
                                 (hasBreakParts && (Spec.AlwaysShatterOnHit || speed >= 10.5f));

            if (shouldShatter)
            {
                ShatterIntoParts(hitterRb, hitDir, Mathf.Max(speed, 8f));
            }
            else
            {
                LaunchWholeProp(hitterRb, hitDir, Mathf.Max(speed, 7f));
            }
        }

        private void PlayImpactAudio(float hitSpeed)
        {
            if (Spec == null) return;
            AudioClip clip = MM2BangerSystem.GetImpactClip(Spec.PkgName, Spec.AudioId);
            if (clip == null) return;

            GameObject audioGo = new GameObject($"SFX_Banger_{Spec.PkgName}");
            audioGo.transform.position = transform.position;
            var src = audioGo.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 0.85f;
            src.minDistance = 8f;
            src.maxDistance = 120f;
            src.volume = Mathf.Clamp(0.45f + hitSpeed / 35f, 0.45f, 1.0f);
            src.pitch = UnityEngine.Random.Range(0.92f, 1.08f);
            src.Play();
            Destroy(audioGo, clip.length + 0.25f);
        }

        private void LaunchWholeProp(Rigidbody hitterRb, Vector3 hitDir, float speed)
        {
            Collider[] hitterCols = hitterRb != null
                ? hitterRb.GetComponentsInChildren<Collider>()
                : Array.Empty<Collider>();

            if (propCollider != null)
            {
                // Shrink collider slightly when dynamic so tall streetlamps/trees tumble smoothly
                Vector3 sz = propCollider.size;
                propCollider.size = new Vector3(
                    Mathf.Min(sz.x, 1.8f),
                    Mathf.Max(0.4f, sz.y * 0.85f),
                    Mathf.Min(sz.z, 1.8f)
                );
                propCollider.isTrigger = false;

                for (int i = 0; i < hitterCols.Length; i++)
                {
                    if (hitterCols[i] != null)
                    {
                        Physics.IgnoreCollision(propCollider, hitterCols[i], true);
                    }
                }
            }

            var rb = gameObject.GetComponent<Rigidbody>();
            if (rb == null) rb = gameObject.AddComponent<Rigidbody>();

            rb.mass = Spec != null && Spec.IsTreeOrFoliage
                ? 30f
                : Mathf.Clamp(Spec != null ? Spec.Mass * 0.35f : 25f, 4f, 140f);
            rb.linearDamping = 0.25f;
            rb.angularDamping = 0.2f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            float forwardLaunch = Mathf.Clamp(speed * 1.28f + 4.5f, 8f, 38f);
            float upPop = Mathf.Clamp(speed * 0.34f + 2.8f, 3.0f, 11.5f);
            Vector3 sideScatter = Vector3.Cross(Vector3.up, hitDir) * UnityEngine.Random.Range(-2.5f, 2.5f);

            rb.linearVelocity = hitDir * forwardLaunch + Vector3.up * upPop + sideScatter;

            Vector3 pitchAxis = Vector3.Cross(Vector3.up, hitDir).normalized;
            Vector3 randomSpin = new Vector3(
                UnityEngine.Random.Range(-5f, 5f),
                UnityEngine.Random.Range(-7f, 7f),
                UnityEngine.Random.Range(-5f, 5f)
            );
            rb.angularVelocity = pitchAxis * UnityEngine.Random.Range(6f, 14f) + randomSpin;

            if (hitterCols.Length > 0 && propCollider != null)
            {
                StartCoroutine(RestoreVehicleCollisionsAfterDelay(propCollider, hitterCols, 0.4f));
            }
        }

        private void ShatterIntoParts(Rigidbody hitterRb, Vector3 hitDir, float speed)
        {
            Collider[] hitterCols = hitterRb != null
                ? hitterRb.GetComponentsInChildren<Collider>()
                : Array.Empty<Collider>();

            Transform shardParent = transform.parent;

            // Spawn authored BREAK01..BREAK0N sub-meshes
            if (Bundle != null && Bundle.BreakParts != null && Bundle.BreakParts.Count > 0)
            {
                for (int i = 0; i < Bundle.BreakParts.Count; i++)
                {
                    MM2BangerPartBundle part = Bundle.BreakParts[i];
                    if (part == null || part.SharedMesh == null) continue;

                    Vector3 pCg = part.PartSpec != null ? part.PartSpec.CG : Spec.CG;
                    Vector3 partCenterAge = new Vector3(
                        origAge.x + pCg.x * xaAge.x + pCg.y * yaAge.x + pCg.z * zaAge.x,
                        origAge.y + pCg.x * xaAge.y + pCg.y * yaAge.y + pCg.z * zaAge.y,
                        origAge.z + pCg.x * xaAge.z + pCg.y * yaAge.z + pCg.z * zaAge.z
                    );
                    Vector3 partPosUnity = new Vector3(-partCenterAge.x, partCenterAge.y, partCenterAge.z);

                    GameObject shardGo = new GameObject($"{name}_{part.PartName}");
                    if (shardParent != null) shardGo.transform.SetParent(shardParent, false);
                    shardGo.transform.SetPositionAndRotation(partPosUnity, transform.rotation);

                    shardGo.AddComponent<MeshFilter>().sharedMesh = part.SharedMesh;
                    shardGo.AddComponent<MeshRenderer>().sharedMaterials = part.SharedMaterials;

                    var box = shardGo.AddComponent<BoxCollider>();
                    Bounds pb = part.SharedMesh.bounds;
                    box.center = pb.center;
                    box.size = new Vector3(
                        Mathf.Max(pb.size.x * 0.8f, 0.25f),
                        Mathf.Max(pb.size.y * 0.8f, 0.25f),
                        Mathf.Max(pb.size.z * 0.8f, 0.25f)
                    );

                    for (int c = 0; c < hitterCols.Length; c++)
                    {
                        if (hitterCols[c] != null) Physics.IgnoreCollision(box, hitterCols[c], true);
                    }

                    var rb = shardGo.AddComponent<Rigidbody>();
                    rb.mass = Mathf.Clamp(part.PartSpec != null ? part.PartSpec.Mass * 0.35f : 8f, 2f, 45f);
                    rb.linearDamping = 0.2f;
                    rb.angularDamping = 0.15f;
                    rb.interpolation = RigidbodyInterpolation.Interpolate;

                    Vector3 spread = new Vector3(
                        UnityEngine.Random.Range(-4.5f, 4.5f),
                        UnityEngine.Random.Range(2.0f, 7.5f),
                        UnityEngine.Random.Range(-4.5f, 4.5f)
                    );
                    rb.linearVelocity = hitDir * (speed * UnityEngine.Random.Range(0.95f, 1.35f) + 3.5f) + spread;
                    rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 14f;

                    Destroy(shardGo, 10f);
                }
            }

            // If this is a glass window, door, or bus stop, also burst crisp tumbling glass shards
            if (Spec != null && (Spec.IsGlassOrDoor || Spec.PkgName.Contains("busstop")))
            {
                SpawnGlassShardBurst(shardParent, hitterCols, hitDir, speed);
            }

            gameObject.SetActive(false);
            Destroy(gameObject, 0.1f);
        }

        private void SpawnGlassShardBurst(Transform shardParent, Collider[] hitterCols, Vector3 hitDir, float speed)
        {
            Mesh shardMesh = MM2BangerSystem.GetGlassShardMesh();
            Material shardMat = (Bundle != null && Bundle.SharedMaterials != null && Bundle.SharedMaterials.Length > 0)
                ? Bundle.SharedMaterials[0]
                : null;

            Bounds b = Bundle != null ? Bundle.MeshBounds : new Bounds(Vector3.zero, new Vector3(2f, 2f, 0.3f));
            int shardCount = 10;

            for (int i = 0; i < shardCount; i++)
            {
                Vector3 localOff = new Vector3(
                    UnityEngine.Random.Range(b.min.x * 0.8f, b.max.x * 0.8f),
                    UnityEngine.Random.Range(b.min.y * 0.8f, b.max.y * 0.8f),
                    UnityEngine.Random.Range(-0.15f, 0.15f)
                );
                Vector3 spawnPos = transform.TransformPoint(localOff);

                GameObject g = new GameObject($"{name}_GlassShard_{i}");
                if (shardParent != null) g.transform.SetParent(shardParent, false);
                g.transform.SetPositionAndRotation(spawnPos, UnityEngine.Random.rotation);
                float scale = UnityEngine.Random.Range(0.75f, 1.55f);
                g.transform.localScale = new Vector3(scale, scale, scale);

                g.AddComponent<MeshFilter>().sharedMesh = shardMesh;
                var mr = g.AddComponent<MeshRenderer>();
                if (shardMat != null) mr.sharedMaterial = shardMat;

                var col = g.AddComponent<BoxCollider>();
                col.size = new Vector3(0.32f, 0.32f, 0.08f);
                for (int c = 0; c < hitterCols.Length; c++)
                {
                    if (hitterCols[c] != null) Physics.IgnoreCollision(col, hitterCols[c], true);
                }

                var rb = g.AddComponent<Rigidbody>();
                rb.mass = 1.5f;
                rb.linearVelocity = hitDir * (speed * UnityEngine.Random.Range(0.85f, 1.45f) + 4f) +
                                    new Vector3(
                                        UnityEngine.Random.Range(-5f, 5f),
                                        UnityEngine.Random.Range(1.5f, 6.5f),
                                        UnityEngine.Random.Range(-5f, 5f)
                                    );
                rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 22f;

                Destroy(g, 5.5f);
            }
        }

        private System.Collections.IEnumerator RestoreVehicleCollisionsAfterDelay(Collider propCol, Collider[] vehicleCols, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (propCol == null) yield break;
            for (int i = 0; i < vehicleCols.Length; i++)
            {
                if (vehicleCols[i] != null)
                {
                    Physics.IgnoreCollision(propCol, vehicleCols[i], false);
                }
            }
        }
    }
}
