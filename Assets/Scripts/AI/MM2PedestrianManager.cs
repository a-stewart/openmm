using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using MM2.Archive;
using MM2.UI;

namespace MM2.AI
{
    /// <summary>
    /// Loads, animates, and manages Midtown Madness 1 &amp; 2 sidewalk pedestrians from .ar archives:
    ///   - MM2 models &amp; animations: anim/pedmodel_man(.mod/.skel/.shaders/.csv), pedmodel_woman, pedmodel_manw, pedmodel_womanw, anim/pedanim_*.anim
    ///   - MM1 models &amp; animations: rex/busman_init(.mod/.skel/.var), buswom_init, casman_init, caswom_init, wintrman_init, wintrwom_init, rex/*.anim
    ///   - Voice/scream audio: malescream1..9, femalescream1..8, vscreamm1..3, vscreamf1..3
    ///   - Spawns pedestrians on pavements based on MM2GameSession.PedestrianDensity.
    ///   - Guarantees pedestrians are impossible to hit: they anticipate approaching vehicles, scream, and dive out of the way.
    /// </summary>
    public class MM2PedestrianManager : MonoBehaviour
    {
        public static MM2PedestrianManager Instance { get; private set; }

        private enum PedBehaviorState
        {
            Walking,
            Standing,
            Anticipating,
            Diving,
            OnGround,
            GettingUp
        }

        private class PedBone
        {
            public string Name;
            public int ParentIndex;
            public Vector3 Offset;
        }

        private struct ModMaterialDef
        {
            public string Name;
            public string TextureName;
            public Color BaseColor;
            public int PacketCount;
            public int PrimitiveCount;
        }

        private struct ModAdjunct
        {
            public int VertexIndex;
            public int NormalIndex;
            public int UvIndex;
            public int BoneIndex;
        }

        private class ModSubmeshPacket
        {
            public int MaterialIndex;
            public List<ModAdjunct> Adjuncts = new List<ModAdjunct>();
            public List<int> Triangles = new List<int>(); // triplets of indices into Adjuncts
        }

        private class ParsedModModel
        {
            public bool IsMm1Format;
            public List<PedBone> Bones = new List<PedBone>();
            public List<Vector3> RawVertices = new List<Vector3>();
            public List<Vector3> RawNormals = new List<Vector3>();
            public List<Vector2> RawUVs = new List<Vector2>();
            public int[] VertexBoneIndices;
            public int[] NormalBoneIndices;
            public List<ModMaterialDef> Materials = new List<ModMaterialDef>();
            public List<ModSubmeshPacket> Submeshes = new List<ModSubmeshPacket>();
        }

        private class BakedAnimClip
        {
            public Mesh[] FrameMeshes;
            public float Fps = 18f;
            public bool Loop = true;
        }

        private class PedArchetype
        {
            public string Name;
            public bool IsFemale;
            public Material[][] OutfitMaterials; // [outfitIdx][submeshIdx]
            public BakedAnimClip ClipStand;
            public BakedAnimClip ClipWalk;
            public BakedAnimClip ClipRun;
            public BakedAnimClip ClipAntic;
            public BakedAnimClip ClipDiveLeft;
            public BakedAnimClip ClipDiveRight;
            public BakedAnimClip ClipGroundLeft;
            public BakedAnimClip ClipGroundRight;
            public BakedAnimClip ClipGetUpLeft;
            public BakedAnimClip ClipGetUpRight;
        }

        private class PedestrianAgent
        {
            public GameObject RootObject;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public AudioSource VoiceAudio;

            public PedArchetype Archetype;
            public int OutfitIndex;

            public MM2AiMapData.BaiSidewalkStrip Strip;
            public float DistanceAlongStrip;
            public int WalkDirection; // +1 or -1 along Strip
            public float BaseWalkSpeed;
            public float CurrentLateralNormalized; // -1 = curb, 0 = center, +1 = building wall
            public float TargetLateralNormalized;

            public PedBehaviorState State;
            public BakedAnimClip ActiveClip;
            public float AnimTime;
            public float StateTimer;
            public bool DiveIsLeft;
            public Vector3 DiveWorldDir;
            public float DiveSpeed;
            public float ScreamCooldown;
        }

        private struct VehicleHazard
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3 Forward;
            public float Speed;
            public float SafeRadius;
        }

        private MM2AiMapData aiMap;
        private Transform playerTransform;
        private Rigidbody playerRigidbody;
        private Transform pedContainer;

        private readonly List<PedArchetype> loadedArchetypes = new List<PedArchetype>();
        private readonly List<PedestrianAgent> activePeds = new List<PedestrianAgent>();
        private readonly List<AudioClip> maleScreamClips = new List<AudioClip>();
        private readonly List<AudioClip> femaleScreamClips = new List<AudioClip>();
        private readonly List<VehicleHazard> hazardBuffer = new List<VehicleHazard>(64);
        private readonly List<Vector3> specialPosBuffer = new List<Vector3>(32);
        private readonly List<Vector3> specialVelBuffer = new List<Vector3>(32);
        private readonly System.Random rng = new System.Random(20000921);

        private int targetPedCount;
        private float spawnTimer;

        private const float MinSpawnDist = 18f;
        private const float MaxSpawnDist = 115f;
        private const float DespawnDist = 135f;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            ClearPedestrians();
            if (Instance == this) Instance = null;
        }

        public void Initialize(MM2AiMapData mapData, Transform player)
        {
            ClearPedestrians();

            aiMap = mapData;
            playerTransform = player;
            playerRigidbody = player != null ? player.GetComponent<Rigidbody>() : null;

            // 1. Check race pedestrian density
            if (MM2GameSession.PedestrianDensity <= 0 || aiMap == null || aiMap.SidewalkStrips.Count == 0)
            {
                targetPedCount = 0;
                return;
            }

            // 2. Check if any mounted .ar archive provides pedestrian models & skeletons; if not, gracefully do nothing
            bool hasMm2Peds = MM2ArchiveSystem.FileExists("anim/pedmodel_man.mod") &&
                              MM2ArchiveSystem.FileExists("anim/pedmodel_man.skel");
            bool hasMm1Peds = MM2ArchiveSystem.FileExists("rex/busman_init.mod") &&
                              MM2ArchiveSystem.FileExists("rex/busman_init.skel");

            if (!hasMm2Peds && !hasMm1Peds)
            {
                targetPedCount = 0;
                Debug.Log("[MM2PedestrianManager] No archive provides pedestrian models; skipping pedestrian rendering.");
                return;
            }

            if (pedContainer == null)
            {
                GameObject root = new GameObject("MM2_PedestrianRoot");
                root.transform.SetParent(transform, false);
                pedContainer = root.transform;
            }

            EnsureAudioLoaded();
            EnsureArchetypesLoaded(hasMm2Peds, hasMm1Peds);

            if (loadedArchetypes.Count == 0)
            {
                targetPedCount = 0;
                return;
            }

            float density01 = Mathf.Clamp01(MM2GameSession.PedestrianDensity / 100f);
            targetPedCount = Mathf.RoundToInt(Mathf.Lerp(10f, 56f, density01));

            PopulateInitialPedestrians();
            Debug.Log($"[MM2PedestrianManager] Initialized with PedestrianDensity={MM2GameSession.PedestrianDensity}% -> targetPedCount={targetPedCount} (spawned {activePeds.Count} on {aiMap.SidewalkStrips.Count} sidewalks).");
        }

        public void ClearPedestrians()
        {
            for (int i = 0; i < activePeds.Count; i++)
            {
                if (activePeds[i]?.RootObject != null)
                {
                    Destroy(activePeds[i].RootObject);
                }
            }
            activePeds.Clear();
        }

        private void EnsureAudioLoaded()
        {
            if (maleScreamClips.Count == 0)
            {
                string[] maleCandidates =
                {
                    "malescream1", "malescream2", "malescream3", "malescream4",
                    "malescream5", "malescream7", "malescream8", "malescream9",
                    "vscreamm1", "vscreamm2", "vscreamm3", "scream1", "scream2"
                };
                for (int i = 0; i < maleCandidates.Length; i++)
                {
                    AudioClip clip = MM2RuntimeDecoders.LoadAudioClip(maleCandidates[i]);
                    if (clip != null) maleScreamClips.Add(clip);
                }
            }

            if (femaleScreamClips.Count == 0)
            {
                string[] femaleCandidates =
                {
                    "femalescream1", "femalescream2", "femalescream3", "femalescream4",
                    "femalescream5", "femalescream6", "femalescream8",
                    "vscreamf1", "vscreamf2", "vscreamf3", "scream3", "scream4"
                };
                for (int i = 0; i < femaleCandidates.Length; i++)
                {
                    AudioClip clip = MM2RuntimeDecoders.LoadAudioClip(femaleCandidates[i]);
                    if (clip != null) femaleScreamClips.Add(clip);
                }
            }
        }

        private void EnsureArchetypesLoaded(bool hasMm2Peds, bool hasMm1Peds)
        {
            loadedArchetypes.Clear();

            bool isRainOrFog = MM2GameSession.WeatherIndex >= 2;
            bool isMm1City = aiMap != null &&
                             string.Equals(aiMap.CityName, "chicago", StringComparison.OrdinalIgnoreCase) &&
                             hasMm1Peds;

            if (!isMm1City && hasMm2Peds)
            {
                if (isRainOrFog)
                {
                    TryLoadMm2Archetype("pedmodel_manw", isFemale: false);
                    TryLoadMm2Archetype("pedmodel_womanw", isFemale: true);
                }
                TryLoadMm2Archetype("pedmodel_man", isFemale: false);
                TryLoadMm2Archetype("pedmodel_woman", isFemale: true);
            }

            if (loadedArchetypes.Count == 0 && hasMm1Peds)
            {
                if (isRainOrFog)
                {
                    TryLoadMm1Archetype("wintrman_init", isFemale: false, isWomanAnim: false);
                    TryLoadMm1Archetype("wintrwom_init", isFemale: true, isWomanAnim: true);
                }
                TryLoadMm1Archetype("busman_init", isFemale: false, isWomanAnim: false);
                TryLoadMm1Archetype("buswom_init", isFemale: true, isWomanAnim: true);
                TryLoadMm1Archetype("casman_init", isFemale: false, isWomanAnim: false);
                TryLoadMm1Archetype("caswom_init", isFemale: true, isWomanAnim: false);
            }
        }

        private void TryLoadMm2Archetype(string modelStem, bool isFemale)
        {
            string skelText = MM2ArchiveSystem.ReadFileText($"anim/{modelStem}.skel");
            string modText = MM2ArchiveSystem.ReadFileText($"anim/{modelStem}.mod");
            if (string.IsNullOrEmpty(skelText) || string.IsNullOrEmpty(modText)) return;

            ParsedModModel model = ParseModAndSkel(skelText, modText, isMm1Format: false);
            if (model == null || model.Bones.Count == 0 || model.Submeshes.Count == 0) return;

            byte[] shadersBytes = MM2ArchiveSystem.ReadFileBytes($"anim/{modelStem}.shaders");
            Material[][] outfits = BuildMm2OutfitMaterials(model, shadersBytes, modelStem);

            string pfx = isFemale ? "pedanim_wom" : "pedanim_man";
            string diveLPfx = isFemale ? "anim/pedanim_woman2dl.anim" : "anim/pedanim_manan2dl.anim";
            string diveRPfx = isFemale ? "anim/pedanim_woman2dr.anim" : "anim/pedanim_manan2dr.anim";

            var arch = new PedArchetype
            {
                Name = modelStem,
                IsFemale = isFemale,
                OutfitMaterials = outfits,
                ClipStand = BakeMm2AnimClip(model, $"anim/{pfx}stand.anim", 0, -1, 14f, true),
                ClipWalk = BakeMm2AnimClip(model, $"anim/{pfx}walk.anim", 0, -1, 20f, true),
                ClipRun = BakeMm2AnimClip(model, $"anim/{pfx}run.anim", 0, -1, 22f, true),
                ClipAntic = BakeMm2AnimClip(model, $"anim/{pfx}antic.anim", 0, -1, 18f, true),
                ClipDiveLeft = BakeMm2AnimClip(model, $"anim/{pfx}w2dl.anim", 0, -1, 28f, false) ??
                               BakeMm2AnimClip(model, diveLPfx, 0, -1, 24f, false),
                ClipDiveRight = BakeMm2AnimClip(model, $"anim/{pfx}w2dr.anim", 0, -1, 28f, false) ??
                                BakeMm2AnimClip(model, diveRPfx, 0, -1, 24f, false),
                ClipGroundLeft = BakeMm2AnimClip(model, $"anim/{pfx}dl2gr.anim", 0, -1, 18f, false),
                ClipGroundRight = BakeMm2AnimClip(model, $"anim/{pfx}dr2gr.anim", 0, -1, 18f, false),
                ClipGetUpLeft = BakeMm2AnimClip(model, $"anim/{pfx}gr2st.anim", 0, -1, 22f, false),
                ClipGetUpRight = BakeMm2AnimClip(model, $"anim/{pfx}gr2s3.anim", 0, -1, 22f, false)
            };

            if (arch.ClipWalk == null && arch.ClipStand == null) return;
            FillFallbackClips(arch);
            loadedArchetypes.Add(arch);
        }

        private void TryLoadMm1Archetype(string modelStem, bool isFemale, bool isWomanAnim)
        {
            string skelText = MM2ArchiveSystem.ReadFileText($"rex/{modelStem}.skel");
            string modText = MM2ArchiveSystem.ReadFileText($"rex/{modelStem}.mod");
            if (string.IsNullOrEmpty(skelText) || string.IsNullOrEmpty(modText)) return;

            ParsedModModel model = ParseModAndSkel(skelText, modText, isMm1Format: true);
            if (model == null || model.Bones.Count == 0 || model.Submeshes.Count == 0) return;

            byte[] varBytes = MM2ArchiveSystem.ReadFileBytes($"rex/{modelStem}.var");
            Material[][] outfits = BuildMm1OutfitMaterials(model, varBytes, modelStem);

            string walkAnim = isWomanAnim ? "rex/womwalk.anim" : "rex/cmanwalk.anim";
            string runAnim = isWomanAnim ? "rex/womrun.anim" : "rex/cmanrun.anim";
            string diveLAnim = isWomanAnim ? "rex/womdivel.anim" : "rex/cmandivl.anim";
            string diveRAnim = isWomanAnim ? "rex/womdiver.anim" : "rex/cmandivr.anim";

            var arch = new PedArchetype
            {
                Name = modelStem,
                IsFemale = isFemale,
                OutfitMaterials = outfits,
                ClipStand = BakeMm1AnimClip(model, walkAnim, 0, 1, 12f, true),
                ClipWalk = BakeMm1AnimClip(model, walkAnim, 4, 23, 20f, true),
                ClipRun = BakeMm1AnimClip(model, runAnim, 0, -1, 22f, true),
                ClipAntic = BakeMm1AnimClip(model, diveLAnim, 0, 9, 18f, true),
                ClipDiveLeft = BakeMm1AnimClip(model, diveLAnim, 10, 26, 26f, false),
                ClipDiveRight = BakeMm1AnimClip(model, diveRAnim, 10, 26, 26f, false),
                ClipGroundLeft = BakeMm1AnimClip(model, diveLAnim, 26, 28, 14f, false),
                ClipGroundRight = BakeMm1AnimClip(model, diveRAnim, 26, 28, 14f, false),
                ClipGetUpLeft = BakeMm1AnimClip(model, diveLAnim, 27, -1, 20f, false),
                ClipGetUpRight = BakeMm1AnimClip(model, diveRAnim, 27, -1, 20f, false)
            };

            if (arch.ClipWalk == null && arch.ClipStand == null) return;
            FillFallbackClips(arch);
            loadedArchetypes.Add(arch);
        }

        private static void FillFallbackClips(PedArchetype arch)
        {
            if (arch.ClipStand == null) arch.ClipStand = arch.ClipWalk;
            if (arch.ClipWalk == null) arch.ClipWalk = arch.ClipStand;
            if (arch.ClipRun == null) arch.ClipRun = arch.ClipWalk;
            if (arch.ClipAntic == null) arch.ClipAntic = arch.ClipStand;
            if (arch.ClipDiveLeft == null) arch.ClipDiveLeft = arch.ClipRun;
            if (arch.ClipDiveRight == null) arch.ClipDiveRight = arch.ClipDiveLeft;
            if (arch.ClipGroundLeft == null) arch.ClipGroundLeft = arch.ClipDiveLeft;
            if (arch.ClipGroundRight == null) arch.ClipGroundRight = arch.ClipDiveRight;
            if (arch.ClipGetUpLeft == null) arch.ClipGetUpLeft = arch.ClipStand;
            if (arch.ClipGetUpRight == null) arch.ClipGetUpRight = arch.ClipGetUpLeft;
        }

        // =====================================================================
        // .SKEL, .MOD, .SHADERS, .VAR, AND .ANIM PARSING & POSE BAKING
        // =====================================================================

        private static ParsedModModel ParseModAndSkel(string skelText, string modText, bool isMm1Format)
        {
            var result = new ParsedModModel { IsMm1Format = isMm1Format };

            // 1. Parse hierarchical .skel bones
            var parentStack = new Stack<int>();
            parentStack.Push(-1);
            int currentBoneIdx = -1;

            string[] skelLines = skelText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < skelLines.Length; i++)
            {
                string line = skelLines[i].Trim();
                if (line.StartsWith("bone ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    string bName = parts.Length > 1 ? parts[1] : $"bone_{result.Bones.Count}";
                    currentBoneIdx = result.Bones.Count;
                    result.Bones.Add(new PedBone
                    {
                        Name = bName,
                        ParentIndex = parentStack.Peek(),
                        Offset = Vector3.zero
                    });
                    if (line.IndexOf('{') >= 0)
                    {
                        parentStack.Push(currentBoneIdx);
                    }
                }
                else if (line.StartsWith("offset ", StringComparison.OrdinalIgnoreCase) && currentBoneIdx >= 0)
                {
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 &&
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ox) &&
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float oy) &&
                        float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float oz))
                    {
                        result.Bones[currentBoneIdx].Offset = new Vector3(ox, oy, oz);
                    }
                }
                else if (line == "}")
                {
                    if (parentStack.Count > 1) parentStack.Pop();
                }
            }

            // 2. Parse .mod vertices, normals, UVs, materials, packets/triangles, mtxv, mtxn
            string[] modLines = modText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool inMtl = false;
            bool inPacket = false;
            ModMaterialDef currentMtl = default;
            ModSubmeshPacket currentPacket = null;

            var globalAdjuncts = new List<ModAdjunct>();
            var globalTriangles = new List<(int a, int b, int c)>();
            int packetIndex = 0;
            var mtlIndexPerPacket = new List<int>();

            for (int i = 0; i < modLines.Length; i++)
            {
                string line = modLines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (inMtl)
                {
                    if (line == "}")
                    {
                        int mIdx = result.Materials.Count;
                        result.Materials.Add(currentMtl);
                        int pkts = Mathf.Max(1, currentMtl.PacketCount);
                        for (int k = 0; k < pkts; k++) mtlIndexPerPacket.Add(mIdx);
                        inMtl = false;
                    }
                    else if (line.StartsWith("packets:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 2 && int.TryParse(p[1], out int pk)) currentMtl.PacketCount = pk;
                    }
                    else if (line.StartsWith("primitives:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 2 && int.TryParse(p[1], out int pr)) currentMtl.PrimitiveCount = pr;
                    }
                    else if (line.StartsWith("ambient:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 4 &&
                            float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                        {
                            currentMtl.BaseColor = new Color(
                                Mathf.Max(currentMtl.BaseColor.r, r),
                                Mathf.Max(currentMtl.BaseColor.g, g),
                                Mathf.Max(currentMtl.BaseColor.b, b),
                                1f);
                        }
                    }
                    else if (line.StartsWith("diffuse:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 4 &&
                            float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                        {
                            currentMtl.BaseColor = new Color(
                                Mathf.Clamp01(currentMtl.BaseColor.r * 0.45f + r * 0.75f),
                                Mathf.Clamp01(currentMtl.BaseColor.g * 0.45f + g * 0.75f),
                                Mathf.Clamp01(currentMtl.BaseColor.b * 0.45f + b * 0.75f),
                                1f);
                        }
                    }
                    else if (line.StartsWith("texture1:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 2 && !string.Equals(p[1], "none", StringComparison.OrdinalIgnoreCase))
                        {
                            currentMtl.TextureName = p[1];
                        }
                    }
                    continue;
                }

                if (inPacket)
                {
                    if (line == "}")
                    {
                        if (currentPacket != null && currentPacket.Triangles.Count > 0)
                        {
                            result.Submeshes.Add(currentPacket);
                        }
                        currentPacket = null;
                        inPacket = false;
                        packetIndex++;
                    }
                    else if (line.StartsWith("adj ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("adj\t", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 5 &&
                            int.TryParse(p[1], out int vIdx) &&
                            int.TryParse(p[2], out int nIdx) &&
                            int.TryParse(p[4], out int tIdx))
                        {
                            currentPacket.Adjuncts.Add(new ModAdjunct
                            {
                                VertexIndex = vIdx,
                                NormalIndex = nIdx,
                                UvIndex = tIdx
                            });
                        }
                    }
                    else if (line.StartsWith("tri ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("tri\t", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 4 &&
                            int.TryParse(p[1], out int i0) &&
                            int.TryParse(p[2], out int i1) &&
                            int.TryParse(p[3], out int i2))
                        {
                            currentPacket.Triangles.Add(i0);
                            currentPacket.Triangles.Add(i1);
                            currentPacket.Triangles.Add(i2);
                        }
                    }
                    continue;
                }

                if (line.StartsWith("v ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("v\t", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4 &&
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float vx) &&
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float vy) &&
                        float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float vz))
                    {
                        result.RawVertices.Add(new Vector3(vx, vy, vz));
                    }
                }
                else if (line.StartsWith("n ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("n\t", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4 &&
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float nx) &&
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float ny) &&
                        float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float nz))
                    {
                        result.RawNormals.Add(new Vector3(nx, ny, nz));
                    }
                }
                else if (line.StartsWith("t1 ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("t1\t", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3 &&
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float u) &&
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    {
                        result.RawUVs.Add(new Vector2(u, v));
                    }
                }
                else if (line.StartsWith("mtl ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    currentMtl = new ModMaterialDef
                    {
                        Name = p.Length > 1 ? p[1] : $"mtl_{result.Materials.Count}",
                        BaseColor = new Color(0.6f, 0.6f, 0.6f, 1f),
                        PacketCount = 1,
                        PrimitiveCount = 0
                    };
                    inMtl = true;
                }
                else if (line.StartsWith("packet ", StringComparison.OrdinalIgnoreCase))
                {
                    int mIdx = packetIndex < mtlIndexPerPacket.Count
                        ? mtlIndexPerPacket[packetIndex]
                        : Mathf.Clamp(packetIndex, 0, Mathf.Max(0, result.Materials.Count - 1));
                    currentPacket = new ModSubmeshPacket { MaterialIndex = mIdx };
                    inPacket = true;
                }
                else if (line.StartsWith("adj ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("adj\t", StringComparison.OrdinalIgnoreCase))
                {
                    // MM1 v1.06 top-level adjunct
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 5 &&
                        int.TryParse(p[1], out int vIdx) &&
                        int.TryParse(p[2], out int nIdx) &&
                        int.TryParse(p[4], out int tIdx))
                    {
                        globalAdjuncts.Add(new ModAdjunct
                        {
                            VertexIndex = vIdx,
                            NormalIndex = nIdx,
                            UvIndex = tIdx
                        });
                    }
                }
                else if (line.StartsWith("tri ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("tri\t", StringComparison.OrdinalIgnoreCase))
                {
                    // MM1 v1.06 top-level triangle
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4 &&
                        int.TryParse(p[1], out int i0) &&
                        int.TryParse(p[2], out int i1) &&
                        int.TryParse(p[3], out int i2))
                    {
                        globalTriangles.Add((i0, i1, i2));
                    }
                }
                else if (line.StartsWith("mtxv ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("mtxv\t", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var vMap = new List<int>(result.RawVertices.Count);
                    for (int b = 1; b < p.Length; b++)
                    {
                        if (int.TryParse(p[b], out int cnt))
                        {
                            int boneIdx = b - 1;
                            for (int k = 0; k < cnt; k++) vMap.Add(boneIdx);
                        }
                    }
                    result.VertexBoneIndices = vMap.ToArray();
                }
                else if (line.StartsWith("mtxn ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("mtxn\t", StringComparison.OrdinalIgnoreCase))
                {
                    string[] p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var nMap = new List<int>(result.RawNormals.Count);
                    for (int b = 1; b < p.Length; b++)
                    {
                        if (int.TryParse(p[b], out int cnt))
                        {
                            int boneIdx = b - 1;
                            for (int k = 0; k < cnt; k++) nMap.Add(boneIdx);
                        }
                    }
                    result.NormalBoneIndices = nMap.ToArray();
                }
            }

            // If MM1 v1.06 format (top-level adjuncts & triangles), build submesh packets per material primitive count
            if (result.Submeshes.Count == 0 && globalAdjuncts.Count > 0 && globalTriangles.Count > 0)
            {
                int triCursor = 0;
                for (int m = 0; m < result.Materials.Count && triCursor < globalTriangles.Count; m++)
                {
                    int primCount = result.Materials[m].PrimitiveCount;
                    if (primCount <= 0) primCount = globalTriangles.Count - triCursor;

                    var pkt = new ModSubmeshPacket { MaterialIndex = m };
                    var remapAdj = new Dictionary<int, int>();

                    for (int t = 0; t < primCount && triCursor < globalTriangles.Count; t++, triCursor++)
                    {
                        var (a, b, c) = globalTriangles[triCursor];
                        if (a < 0 || a >= globalAdjuncts.Count ||
                            b < 0 || b >= globalAdjuncts.Count ||
                            c < 0 || c >= globalAdjuncts.Count)
                        {
                            continue;
                        }

                        if (!remapAdj.TryGetValue(a, out int la))
                        {
                            la = pkt.Adjuncts.Count;
                            pkt.Adjuncts.Add(globalAdjuncts[a]);
                            remapAdj[a] = la;
                        }
                        if (!remapAdj.TryGetValue(b, out int lb))
                        {
                            lb = pkt.Adjuncts.Count;
                            pkt.Adjuncts.Add(globalAdjuncts[b]);
                            remapAdj[b] = lb;
                        }
                        if (!remapAdj.TryGetValue(c, out int lc))
                        {
                            lc = pkt.Adjuncts.Count;
                            pkt.Adjuncts.Add(globalAdjuncts[c]);
                            remapAdj[c] = lc;
                        }

                        pkt.Triangles.Add(la);
                        pkt.Triangles.Add(lb);
                        pkt.Triangles.Add(lc);
                    }

                    if (pkt.Triangles.Count > 0)
                    {
                        result.Submeshes.Add(pkt);
                    }
                }
            }

            return result;
        }

        private static Material[][] BuildMm2OutfitMaterials(ParsedModModel model, byte[] shadersBytes, string modelStem)
        {
            int numSubmeshes = model.Submeshes.Count;
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            if (shadersBytes != null && shadersBytes.Length >= 16)
            {
                int numVars = (int)(BitConverter.ToUInt32(shadersBytes, 0) & 0x7Fu);
                int perVar = (int)BitConverter.ToUInt32(shadersBytes, 4);
                int maxOutfits = Mathf.Clamp(numVars, 1, 12); // Cache 12 varied outfits per archetype

                if (numVars > 0 && perVar > 0)
                {
                    var allEntries = new List<(string texName, Color color)>(numVars * perVar);
                    int p = 8;
                    while (p < shadersBytes.Length && allEntries.Count < numVars * perVar)
                    {
                        int sLen = shadersBytes[p];
                        p += 1;
                        if (p + sLen + 68 > shadersBytes.Length) break;

                        string tName = sLen > 0
                            ? Encoding.ASCII.GetString(shadersBytes, p, sLen).TrimEnd('\0').Trim()
                            : string.Empty;
                        p += sLen;

                        float ar = BitConverter.ToSingle(shadersBytes, p);
                        float ag = BitConverter.ToSingle(shadersBytes, p + 4);
                        float ab = BitConverter.ToSingle(shadersBytes, p + 8);
                        float dr = BitConverter.ToSingle(shadersBytes, p + 16);
                        float dg = BitConverter.ToSingle(shadersBytes, p + 20);
                        float db = BitConverter.ToSingle(shadersBytes, p + 24);
                        p += 68;

                        Color col = new Color(
                            Mathf.Clamp01(Mathf.Max(ar, dr) * 0.72f + (ar + dr) * 0.22f),
                            Mathf.Clamp01(Mathf.Max(ag, dg) * 0.72f + (ag + dg) * 0.22f),
                            Mathf.Clamp01(Mathf.Max(ab, db) * 0.72f + (ab + db) * 0.22f),
                            1f);
                        allEntries.Add((tName, col));
                    }

                    if (allEntries.Count >= perVar)
                    {
                        int actualVars = Mathf.Min(maxOutfits, allEntries.Count / perVar);
                        var outfits = new Material[actualVars][];
                        int step = Mathf.Max(1, (allEntries.Count / perVar) / actualVars);

                        for (int v = 0; v < actualVars; v++)
                        {
                            int srcVar = Mathf.Min((allEntries.Count / perVar) - 1, v * step);
                            var subMats = new Material[numSubmeshes];
                            for (int s = 0; s < numSubmeshes; s++)
                            {
                                int mIdx = Mathf.Clamp(model.Submeshes[s].MaterialIndex, 0, perVar - 1);
                                var (texName, col) = allEntries[srcVar * perVar + mIdx];
                                if (string.IsNullOrEmpty(texName) && mIdx < model.Materials.Count)
                                {
                                    texName = model.Materials[mIdx].TextureName;
                                }
                                subMats[s] = CreatePedMaterial(urpLit, $"{modelStem}_v{srcVar}_m{mIdx}", texName, col);
                            }
                            outfits[v] = subMats;
                        }
                        return outfits;
                    }
                }
            }

            // Fallback to .mod material definitions
            var fallback = new Material[numSubmeshes];
            for (int s = 0; s < numSubmeshes; s++)
            {
                int mIdx = Mathf.Clamp(model.Submeshes[s].MaterialIndex, 0, model.Materials.Count - 1);
                ModMaterialDef mDef = model.Materials[mIdx];
                fallback[s] = CreatePedMaterial(urpLit, $"{modelStem}_m{mIdx}", mDef.TextureName, mDef.BaseColor);
            }
            return new[] { fallback };
        }

        private static Material[][] BuildMm1OutfitMaterials(ParsedModModel model, byte[] varBytes, string modelStem)
        {
            int numSubmeshes = model.Submeshes.Count;
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            if (varBytes != null && varBytes.Length >= 12)
            {
                int nVar = BitConverter.ToInt32(varBytes, 0);
                int nMtl = BitConverter.ToInt32(varBytes, 4);
                if (nVar > 0 && nVar <= 32 && nMtl > 0 && 8 + nVar * nMtl * 4 <= varBytes.Length)
                {
                    var outfits = new Material[nVar][];
                    for (int v = 0; v < nVar; v++)
                    {
                        var subMats = new Material[numSubmeshes];
                        for (int s = 0; s < numSubmeshes; s++)
                        {
                            int mIdx = Mathf.Clamp(model.Submeshes[s].MaterialIndex, 0, nMtl - 1);
                            ModMaterialDef mDef = mIdx < model.Materials.Count ? model.Materials[mIdx] : default;

                            int cOff = 8 + (v * nMtl + mIdx) * 4;
                            byte b = varBytes[cOff];
                            byte g = varBytes[cOff + 1];
                            byte r = varBytes[cOff + 2];

                            Color col = (r == 0 && g == 0 && b == 0 && mDef.BaseColor.maxColorComponent > 0.1f)
                                ? mDef.BaseColor
                                : new Color32(r, g, b, 255);

                            // Keep skin/face natural if textured
                            if (!string.IsNullOrEmpty(mDef.TextureName))
                            {
                                col = Color.white;
                            }

                            subMats[s] = CreatePedMaterial(urpLit, $"{modelStem}_v{v}_m{mIdx}", mDef.TextureName, col);
                        }
                        outfits[v] = subMats;
                    }
                    return outfits;
                }
            }

            var fallback = new Material[numSubmeshes];
            for (int s = 0; s < numSubmeshes; s++)
            {
                int mIdx = Mathf.Clamp(model.Submeshes[s].MaterialIndex, 0, model.Materials.Count - 1);
                ModMaterialDef mDef = model.Materials[mIdx];
                fallback[s] = CreatePedMaterial(urpLit, $"{modelStem}_m{mIdx}", mDef.TextureName, mDef.BaseColor);
            }
            return new[] { fallback };
        }

        private static Material CreatePedMaterial(Shader shader, string matName, string texName, Color color)
        {
            Material mat = new Material(shader)
            {
                name = matName,
                color = color
            };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.12f);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);

            if (!string.IsNullOrEmpty(texName) && !string.Equals(texName, "none", StringComparison.OrdinalIgnoreCase))
            {
                var texInfo = MM2RuntimeDecoders.LoadTexture(texName);
                if (texInfo.Texture != null)
                {
                    mat.mainTexture = texInfo.Texture;
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texInfo.Texture);
                    mat.color = Color.white;
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                }
            }

            return mat;
        }

        private static BakedAnimClip BakeMm2AnimClip(
            ParsedModModel model,
            string animVirtualPath,
            int startFrame,
            int endFrame,
            float fps,
            bool loop)
        {
            byte[] data = MM2ArchiveSystem.ReadFileBytes(animVirtualPath);
            if (data == null || data.Length < 17) return null;

            int numFrames = BitConverter.ToInt32(data, 4);
            int numChannels = BitConverter.ToInt32(data, 8);
            int numBones = model.Bones.Count;
            if (numFrames <= 0 || numChannels < (numBones + 1) * 3) return null;
            if (17 + numFrames * numChannels * 4 > data.Length) return null;

            int fStart = Mathf.Clamp(startFrame, 0, numFrames - 1);
            int fEnd = (endFrame < 0 || endFrame >= numFrames) ? (numFrames - 1) : endFrame;
            if (fEnd < fStart) fEnd = fStart;

            int count = fEnd - fStart + 1;
            var meshes = new Mesh[count];
            float[] channels = new float[numChannels];

            for (int f = 0; f < count; f++)
            {
                int frameIdx = fStart + f;
                int byteOff = 17 + frameIdx * numChannels * 4;
                for (int c = 0; c < numChannels; c++)
                {
                    channels[c] = BitConverter.ToSingle(data, byteOff + c * 4);
                }
                meshes[f] = BakeSingleFrameMesh(model, channels, $"{animVirtualPath}_f{frameIdx}");
            }

            return new BakedAnimClip
            {
                FrameMeshes = meshes,
                Fps = fps,
                Loop = loop
            };
        }

        private static BakedAnimClip BakeMm1AnimClip(
            ParsedModModel model,
            string animVirtualPath,
            int startFrame,
            int endFrame,
            float fps,
            bool loop)
        {
            byte[] data = MM2ArchiveSystem.ReadFileBytes(animVirtualPath);
            if (data == null || data.Length < 8) return null;

            int numFrames = BitConverter.ToInt32(data, 0);
            int numBones = BitConverter.ToInt32(data, 4);
            int numChannels = (numBones + 1) * 3;
            if (numFrames <= 0 || numBones != model.Bones.Count) return null;
            if (8 + numFrames * numChannels * 4 > data.Length) return null;

            int fStart = Mathf.Clamp(startFrame, 0, numFrames - 1);
            int fEnd = (endFrame < 0 || endFrame >= numFrames) ? (numFrames - 1) : endFrame;
            if (fEnd < fStart) fEnd = fStart;

            int count = fEnd - fStart + 1;
            var meshes = new Mesh[count];
            float[] channels = new float[numChannels];

            for (int f = 0; f < count; f++)
            {
                int frameIdx = fStart + f;
                int byteOff = 8 + frameIdx * numChannels * 4;
                for (int c = 0; c < numChannels; c++)
                {
                    channels[c] = BitConverter.ToSingle(data, byteOff + c * 4);
                }
                meshes[f] = BakeSingleFrameMesh(model, channels, $"{animVirtualPath}_f{frameIdx}");
            }

            return new BakedAnimClip
            {
                FrameMeshes = meshes,
                Fps = fps,
                Loop = loop
            };
        }

        private static Mesh BakeSingleFrameMesh(ParsedModModel model, float[] channels, string meshName)
        {
            int numBones = model.Bones.Count;
            var boneRot = new Matrix4x4[numBones];
            var bonePos = new Vector3[numBones];

            // Root vertical height is kept from channels[1] while horizontal X/Z root motion is centered at (0,0)
            // so the pedestrian Transform.position is always its exact world position.
            float rootY = Mathf.Max(0.05f, channels[1]);

            for (int b = 0; b < numBones; b++)
            {
                float rx = channels[3 + b * 3];
                float ry = channels[3 + b * 3 + 1];
                float rz = channels[3 + b * 3 + 2];

                Matrix4x4 localR = MatrixFromEulerXyzIntrinsic(rx, ry, rz);
                int parentIdx = model.Bones[b].ParentIndex;
                if (parentIdx < 0)
                {
                    boneRot[b] = localR;
                    bonePos[b] = new Vector3(0f, rootY, 0f);
                }
                else
                {
                    boneRot[b] = boneRot[parentIdx] * localR;
                    bonePos[b] = bonePos[parentIdx] + boneRot[parentIdx].MultiplyVector(model.Bones[b].Offset);
                }
            }

            // Transform vertices and normals from local bone space into posed character space,
            // converting AGE (-Z forward, +X right, +Y up) -> Unity (+Z forward, +X right, +Y up)
            int vCount = model.RawVertices.Count;
            var posedVerts = new Vector3[vCount];
            for (int v = 0; v < vCount; v++)
            {
                int b = (model.VertexBoneIndices != null && v < model.VertexBoneIndices.Length)
                    ? Mathf.Clamp(model.VertexBoneIndices[v], 0, numBones - 1)
                    : 0;
                Vector3 p = bonePos[b] + boneRot[b].MultiplyVector(model.RawVertices[v]);
                posedVerts[v] = new Vector3(p.x, p.y, -p.z);
            }

            int nCount = model.RawNormals.Count;
            var posedNorms = new Vector3[nCount];
            for (int n = 0; n < nCount; n++)
            {
                int b = (model.NormalBoneIndices != null && n < model.NormalBoneIndices.Length)
                    ? Mathf.Clamp(model.NormalBoneIndices[n], 0, numBones - 1)
                    : 0;
                Vector3 nm = boneRot[b].MultiplyVector(model.RawNormals[n]);
                posedNorms[n] = new Vector3(nm.x, nm.y, -nm.z).normalized;
            }

            var outVerts = new List<Vector3>(256);
            var outNorms = new List<Vector3>(256);
            var outUVs = new List<Vector2>(256);
            int subCount = model.Submeshes.Count;
            var subTris = new List<int>[subCount];

            for (int s = 0; s < subCount; s++)
            {
                ModSubmeshPacket pkt = model.Submeshes[s];
                int baseV = outVerts.Count;
                for (int a = 0; a < pkt.Adjuncts.Count; a++)
                {
                    ModAdjunct adj = pkt.Adjuncts[a];
                    Vector3 vp = (adj.VertexIndex >= 0 && adj.VertexIndex < vCount) ? posedVerts[adj.VertexIndex] : Vector3.zero;
                    Vector3 vn = (adj.NormalIndex >= 0 && adj.NormalIndex < nCount) ? posedNorms[adj.NormalIndex] : Vector3.up;
                    Vector2 uv = (adj.UvIndex >= 0 && adj.UvIndex < model.RawUVs.Count) ? model.RawUVs[adj.UvIndex] : Vector2.zero;

                    outVerts.Add(vp);
                    outNorms.Add(vn);
                    outUVs.Add(uv);
                }

                var tris = new List<int>(pkt.Triangles.Count);
                for (int t = 0; t + 2 < pkt.Triangles.Count; t += 3)
                {
                    tris.Add(baseV + pkt.Triangles[t]);
                    tris.Add(baseV + pkt.Triangles[t + 2]);
                    tris.Add(baseV + pkt.Triangles[t + 1]);
                }
                subTris[s] = tris;
            }

            Mesh mesh = new Mesh { name = meshName };
            mesh.SetVertices(outVerts);
            mesh.SetNormals(outNorms);
            mesh.SetUVs(0, outUVs);
            mesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++)
            {
                mesh.SetTriangles(subTris[s], s, false);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Matrix4x4 MatrixFromEulerXyzIntrinsic(float rx, float ry, float rz)
        {
            float cx = Mathf.Cos(rx), sx = Mathf.Sin(rx);
            float cy = Mathf.Cos(ry), sy = Mathf.Sin(ry);
            float cz = Mathf.Cos(rz), sz = Mathf.Sin(rz);

            // R = Rz(rz) * Ry(ry) * Rx(rx)
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = cz * cy;
            m.m01 = cz * sy * sx - sz * cx;
            m.m02 = cz * sy * cx + sz * sx;

            m.m10 = sz * cy;
            m.m11 = sz * sy * sx + cz * cx;
            m.m12 = sz * sy * cx - cz * sx;

            m.m20 = -sy;
            m.m21 = cy * sx;
            m.m22 = cy * cx;
            return m;
        }

        // =====================================================================
        // SPAWNING, SIDEWALK WALKING, THREAT ANTICIPATION, AND DIVING
        // =====================================================================

        private void PopulateInitialPedestrians()
        {
            if (playerTransform == null || aiMap == null || aiMap.SidewalkStrips.Count == 0) return;

            int attempts = targetPedCount * 4;
            for (int i = 0; i < attempts && activePeds.Count < targetPedCount; i++)
            {
                TrySpawnOrPlacePedestrian(null, initialPopulate: true);
            }
        }

        private bool TrySpawnOrPlacePedestrian(PedestrianAgent existing, bool initialPopulate)
        {
            if (playerTransform == null || aiMap == null || aiMap.SidewalkStrips.Count == 0 || loadedArchetypes.Count == 0)
            {
                return false;
            }

            Vector3 playerPos = playerTransform.position;
            Vector3 playerFwd = playerTransform.forward;
            playerFwd.y = 0f;
            if (playerFwd.sqrMagnitude > 0.001f) playerFwd.Normalize();
            else playerFwd = Vector3.forward;

            float minDist = initialPopulate ? 12f : MinSpawnDist;
            float maxDist = MaxSpawnDist;

            int stripCount = aiMap.SidewalkStrips.Count;
            MM2AiMapData.BaiSidewalkStrip chosenStrip = null;
            float chosenDist = 0f;
            float chosenLat = 0f;
            Vector3 spawnPos = Vector3.zero;
            Vector3 spawnFwd = Vector3.forward;

            for (int attempt = 0; attempt < 35; attempt++)
            {
                MM2AiMapData.BaiSidewalkStrip strip = aiMap.SidewalkStrips[rng.Next(stripCount)];
                if (strip == null || strip.TotalLength < 5f) continue;

                float distAlong = (float)rng.NextDouble() * strip.TotalLength;
                float lat = Mathf.Lerp(-0.35f, 0.55f, (float)rng.NextDouble());
                strip.SamplePose(distAlong, lat, out Vector3 candidatePos, out Vector3 candidateFwd, out _);

                Vector3 toCandidate = candidatePos - playerPos;
                float distSq = toCandidate.sqrMagnitude;
                if (distSq < minDist * minDist || distSq > maxDist * maxDist) continue;

                if (!initialPopulate)
                {
                    float dot = Vector3.Dot(toCandidate.normalized, playerFwd);
                    if (dot < -0.35f && rng.NextDouble() < 0.78) continue;
                }

                // Avoid spawning inside or right in front of any vehicle
                if (!IsSafeFromVehicles(candidatePos, 7.5f)) continue;

                chosenStrip = strip;
                chosenDist = distAlong;
                chosenLat = lat;
                spawnPos = candidatePos;
                spawnFwd = candidateFwd;
                break;
            }

            if (chosenStrip == null) return false;

            PedArchetype arch = loadedArchetypes[rng.Next(loadedArchetypes.Count)];
            int outfitIdx = rng.Next(arch.OutfitMaterials.Length);

            if (existing == null)
            {
                GameObject go = new GameObject($"Ped_{arch.Name}_{activePeds.Count:D2}");
                go.transform.SetParent(pedContainer, false);

                MeshFilter mf = go.AddComponent<MeshFilter>();
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;

                AudioSource voice = go.AddComponent<AudioSource>();
                voice.playOnAwake = false;
                voice.loop = false;
                voice.spatialBlend = 1.0f;
                voice.rolloffMode = AudioRolloffMode.Linear;
                voice.minDistance = 4.0f;
                voice.maxDistance = 65.0f;
                voice.volume = 0.85f;

                existing = new PedestrianAgent
                {
                    RootObject = go,
                    Filter = mf,
                    Renderer = mr,
                    VoiceAudio = voice
                };
                activePeds.Add(existing);
            }

            existing.Archetype = arch;
            existing.OutfitIndex = outfitIdx;
            existing.Renderer.sharedMaterials = arch.OutfitMaterials[outfitIdx];

            existing.Strip = chosenStrip;
            existing.DistanceAlongStrip = chosenDist;
            existing.WalkDirection = rng.NextDouble() < 0.5 ? 1 : -1;
            existing.BaseWalkSpeed = Mathf.Lerp(1.25f, 1.72f, (float)rng.NextDouble());
            existing.CurrentLateralNormalized = chosenLat;
            existing.TargetLateralNormalized = chosenLat;

            bool startStanding = rng.NextDouble() < 0.14;
            existing.State = startStanding ? PedBehaviorState.Standing : PedBehaviorState.Walking;
            existing.ActiveClip = startStanding ? arch.ClipStand : arch.ClipWalk;
            existing.AnimTime = (float)rng.NextDouble() * 2f;
            existing.StateTimer = startStanding ? Mathf.Lerp(2f, 5f, (float)rng.NextDouble()) : 0f;

            Vector3 dir = existing.WalkDirection > 0 ? spawnFwd : -spawnFwd;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            existing.RootObject.transform.SetPositionAndRotation(spawnPos, Quaternion.LookRotation(dir.normalized, Vector3.up));
            UpdatePedestrianMeshFrame(existing, 0f);

            return true;
        }

        private void GatherVehicleHazards()
        {
            hazardBuffer.Clear();

            // 1. Player vehicle
            if (playerTransform != null)
            {
                Vector3 vel = playerRigidbody != null ? playerRigidbody.linearVelocity : Vector3.zero;
                float spd = vel.magnitude;
                Vector3 fwd = spd > 1.5f ? (vel / spd) : playerTransform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.0001f) fwd.Normalize();
                else fwd = Vector3.forward;

                hazardBuffer.Add(new VehicleHazard
                {
                    Position = playerTransform.position,
                    Velocity = vel,
                    Forward = fwd,
                    Speed = spd,
                    SafeRadius = 4.6f
                });
            }

            // 2. Opponents
            if (MM2OpponentManager.Instance != null)
            {
                var opps = MM2OpponentManager.Instance.ActiveOpponents;
                for (int i = 0; i < opps.Count; i++)
                {
                    var opp = opps[i];
                    if (opp == null || opp.transform == null) continue;
                    Vector3 vel = opp.Body != null ? opp.Body.linearVelocity : opp.transform.forward * 15f;
                    float spd = vel.magnitude;
                    Vector3 fwd = spd > 1.5f ? (vel / spd) : opp.transform.forward;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 0.0001f) fwd.Normalize();

                    hazardBuffer.Add(new VehicleHazard
                    {
                        Position = opp.transform.position,
                        Velocity = vel,
                        Forward = fwd,
                        Speed = spd,
                        SafeRadius = 4.4f
                    });
                }
            }

            // 3. Police cruisers
            if (MM2PoliceManager.Instance != null)
            {
                var cops = MM2PoliceManager.Instance.ActiveCops;
                for (int i = 0; i < cops.Count; i++)
                {
                    var cop = cops[i];
                    if (cop == null || cop.transform == null) continue;
                    Vector3 vel = cop.Body != null ? cop.Body.linearVelocity : cop.transform.forward * 12f;
                    float spd = vel.magnitude;
                    Vector3 fwd = spd > 1.5f ? (vel / spd) : cop.transform.forward;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 0.0001f) fwd.Normalize();

                    hazardBuffer.Add(new VehicleHazard
                    {
                        Position = cop.transform.position,
                        Velocity = vel,
                        Forward = fwd,
                        Speed = spd,
                        SafeRadius = 4.4f
                    });
                }
            }

            // 4. Ambient traffic (within 75m of player)
            if (MM2TrafficManager.Instance != null && playerTransform != null)
            {
                Vector3 pPos = playerTransform.position;
                var agents = MM2TrafficManager.Instance.ActiveAgents;
                for (int i = 0; i < agents.Count; i++)
                {
                    var ag = agents[i];
                    if (ag == null || ag.transform == null) continue;
                    if ((ag.transform.position - pPos).sqrMagnitude > 75f * 75f) continue;

                    Vector3 vel = ag.Body != null ? ag.Body.linearVelocity : ag.transform.forward * ag.CurrentSpeed;
                    float spd = vel.magnitude;
                    Vector3 fwd = spd > 1.5f ? (vel / spd) : ag.transform.forward;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 0.0001f) fwd.Normalize();

                    hazardBuffer.Add(new VehicleHazard
                    {
                        Position = ag.transform.position,
                        Velocity = vel,
                        Forward = fwd,
                        Speed = spd,
                        SafeRadius = 4.2f
                    });
                }
            }

            // 5. Special traffic (Cable cars & trains)
            if (MM2SpecialTrafficManager.Instance != null)
            {
                MM2SpecialTrafficManager.Instance.GetActiveHazardPositions(specialPosBuffer, specialVelBuffer);
                for (int i = 0; i < specialPosBuffer.Count; i++)
                {
                    Vector3 vel = i < specialVelBuffer.Count ? specialVelBuffer[i] : Vector3.zero;
                    float spd = vel.magnitude;
                    Vector3 fwd = spd > 0.5f ? (vel / spd) : Vector3.forward;
                    hazardBuffer.Add(new VehicleHazard
                    {
                        Position = specialPosBuffer[i],
                        Velocity = vel,
                        Forward = fwd,
                        Speed = spd,
                        SafeRadius = 4.8f
                    });
                }
            }
        }

        private bool IsSafeFromVehicles(Vector3 pos, float minRadius)
        {
            float rSq = minRadius * minRadius;
            for (int i = 0; i < hazardBuffer.Count; i++)
            {
                if ((hazardBuffer[i].Position - pos).sqrMagnitude < rSq) return false;
            }
            return true;
        }

        private bool CheckIncomingVehicleThreat(
            PedestrianAgent ped,
            Vector3 pedPos,
            Vector3 sidewalkOutwardDir,
            out Vector3 escapeDir,
            out Vector3 threatPos)
        {
            escapeDir = sidewalkOutwardDir;
            threatPos = Vector3.zero;

            for (int i = 0; i < hazardBuffer.Count; i++)
            {
                VehicleHazard h = hazardBuffer[i];
                Vector3 toPed = pedPos - h.Position;
                if (Mathf.Abs(toPed.y) > 4.2f) continue;

                toPed.y = 0f;
                float distSq = toPed.sqrMagnitude;

                // Immediate proximity threat (< 7.8m regardless of speed, or < 24m if approaching)
                float lookaheadTime = Mathf.Clamp(1.35f, 0.7f, 1.6f);
                float maxThreatDist = Mathf.Clamp(7.8f + h.Speed * lookaheadTime, 7.8f, 28f);
                if (distSq > maxThreatDist * maxThreatDist) continue;

                float dist = Mathf.Sqrt(distSq);
                if (dist < 7.2f && (h.Speed > 1.2f || dist < 5.2f))
                {
                    threatPos = h.Position;
                    escapeDir = ComputeBestDiveDirection(toPed, h.Forward, sidewalkOutwardDir);
                    return true;
                }

                if (h.Speed > 2.0f)
                {
                    float fwdDot = Vector3.Dot(toPed, h.Forward);
                    if (fwdDot > -2.0f && fwdDot < maxThreatDist)
                    {
                        Vector3 lateralVec = toPed - h.Forward * fwdDot;
                        float corridorHalfWidth = Mathf.Lerp(3.6f, 5.4f, Mathf.Clamp01(h.Speed / 25f));
                        if (lateralVec.sqrMagnitude < corridorHalfWidth * corridorHalfWidth)
                        {
                            threatPos = h.Position;
                            escapeDir = ComputeBestDiveDirection(lateralVec, h.Forward, sidewalkOutwardDir);
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static Vector3 ComputeBestDiveDirection(Vector3 offsetFromVehiclePath, Vector3 vehicleFwd, Vector3 sidewalkOutwardDir)
        {
            Vector3 rightOfVehicle = new Vector3(vehicleFwd.z, 0f, -vehicleFwd.x).normalized;
            float sideSign = Vector3.Dot(offsetFromVehiclePath, rightOfVehicle);

            // Prefer diving toward the building/outer edge of the sidewalk if that moves away from the car's centerline,
            // or laterally away from the car's trajectory line.
            Vector3 lateralAway = sideSign >= 0f ? rightOfVehicle : -rightOfVehicle;
            if (Vector3.Dot(lateralAway, sidewalkOutwardDir) > -0.25f)
            {
                return (lateralAway * 0.65f + sidewalkOutwardDir * 0.35f).normalized;
            }
            return lateralAway;
        }

        private void TriggerPedestrianDive(PedestrianAgent ped, Vector3 escapeDir, Vector3 threatPos)
        {
            ped.State = PedBehaviorState.Diving;
            ped.StateTimer = 0f;
            ped.AnimTime = 0f;
            ped.DiveWorldDir = escapeDir.sqrMagnitude > 0.001f ? escapeDir.normalized : Vector3.right;
            ped.DiveSpeed = 7.2f;
            ped.TargetLateralNormalized = 1.15f; // Dive toward safe outer sidewalk edge

            // Orient pedestrian to face the oncoming threat or perpendicular so the side-dive animation leaps along escapeDir
            Vector3 toThreat = threatPos - ped.RootObject.transform.position;
            toThreat.y = 0f;
            if (toThreat.sqrMagnitude > 0.01f)
            {
                toThreat.Normalize();
                Vector3 right = new Vector3(toThreat.z, 0f, -toThreat.x);
                ped.DiveIsLeft = Vector3.Dot(ped.DiveWorldDir, right) < 0f;
                ped.RootObject.transform.rotation = Quaternion.LookRotation(toThreat, Vector3.up);
            }
            else
            {
                ped.DiveIsLeft = true;
            }

            ped.ActiveClip = ped.DiveIsLeft ? ped.Archetype.ClipDiveLeft : ped.Archetype.ClipDiveRight;
            PlayPedestrianScream(ped);
        }

        private void PlayPedestrianScream(PedestrianAgent ped)
        {
            if (ped.ScreamCooldown > 0f || ped.VoiceAudio == null || playerTransform == null) return;
            if ((ped.RootObject.transform.position - playerTransform.position).sqrMagnitude > 65f * 65f) return;

            var pool = ped.Archetype.IsFemale ? femaleScreamClips : maleScreamClips;
            if (pool.Count == 0) pool = maleScreamClips.Count > 0 ? maleScreamClips : femaleScreamClips;
            if (pool.Count == 0) return;

            AudioClip clip = pool[rng.Next(pool.Count)];
            if (clip != null)
            {
                ped.VoiceAudio.pitch = Mathf.Lerp(0.94f, 1.06f, (float)rng.NextDouble());
                ped.VoiceAudio.PlayOneShot(clip);
                ped.ScreamCooldown = 3.2f;
            }
        }

        private void EnforceHardVehicleExclusion(PedestrianAgent ped, Vector3 sidewalkOutwardDir)
        {
            Transform t = ped.RootObject.transform;
            Vector3 pos = t.position;

            for (int i = 0; i < hazardBuffer.Count; i++)
            {
                VehicleHazard h = hazardBuffer[i];
                Vector3 diff = pos - h.Position;
                if (Mathf.Abs(diff.y) > 3.5f) continue;

                float flatDistSq = diff.x * diff.x + diff.z * diff.z;
                float minSafe = h.SafeRadius;
                if (flatDistSq < minSafe * minSafe)
                {
                    Vector3 pushDir = new Vector3(diff.x, 0f, diff.z);
                    // Ensure push direction moves perpendicular/away from the vehicle's forward vector, never in front of it
                    float fwdProj = Vector3.Dot(pushDir, h.Forward);
                    Vector3 lat = pushDir - h.Forward * fwdProj;
                    if (lat.sqrMagnitude > 0.001f)
                    {
                        pushDir = lat.normalized;
                    }
                    else
                    {
                        pushDir = sidewalkOutwardDir.sqrMagnitude > 0.001f ? sidewalkOutwardDir : Vector3.right;
                    }

                    Vector3 safePos = h.Position + h.Forward * Mathf.Clamp(fwdProj, -minSafe, minSafe) + pushDir * (minSafe + 0.65f);
                    safePos.y = pos.y;
                    pos = safePos;
                    t.position = pos;

                    if (ped.State == PedBehaviorState.Walking || ped.State == PedBehaviorState.Standing || ped.State == PedBehaviorState.Anticipating)
                    {
                        TriggerPedestrianDive(ped, pushDir, h.Position);
                    }
                }
            }
        }

        private void UpdatePedestrianMeshFrame(PedestrianAgent ped, float dt)
        {
            BakedAnimClip clip = ped.ActiveClip;
            if (clip == null || clip.FrameMeshes == null || clip.FrameMeshes.Length == 0) return;

            ped.AnimTime += dt;
            int totalFrames = clip.FrameMeshes.Length;
            int frameIdx;

            if (clip.Loop)
            {
                frameIdx = Mathf.FloorToInt(ped.AnimTime * clip.Fps) % totalFrames;
                if (frameIdx < 0) frameIdx += totalFrames;
            }
            else
            {
                frameIdx = Mathf.Clamp(Mathf.FloorToInt(ped.AnimTime * clip.Fps), 0, totalFrames - 1);
            }

            Mesh m = clip.FrameMeshes[frameIdx];
            if (m != null && ped.Filter.sharedMesh != m)
            {
                ped.Filter.sharedMesh = m;
            }
        }

        private void Update()
        {
            if (MM2MenuController.IsMenuOpen || aiMap == null || playerTransform == null || targetPedCount <= 0)
            {
                return;
            }

            float dt = Time.deltaTime;
            GatherVehicleHazards();

            spawnTimer -= dt;
            if (spawnTimer <= 0f)
            {
                spawnTimer = 0.35f;
                int missing = targetPedCount - activePeds.Count;
                for (int i = 0; i < Mathf.Min(missing, 4); i++)
                {
                    TrySpawnOrPlacePedestrian(null, initialPopulate: false);
                }
            }

            Vector3 playerPos = playerTransform.position;
            float despawnDistSq = DespawnDist * DespawnDist;

            for (int i = 0; i < activePeds.Count; i++)
            {
                PedestrianAgent ped = activePeds[i];
                if (ped?.RootObject == null || ped.Strip == null) continue;

                if (ped.ScreamCooldown > 0f) ped.ScreamCooldown -= dt;

                Vector3 currentPos = ped.RootObject.transform.position;
                if ((currentPos - playerPos).sqrMagnitude > despawnDistSq)
                {
                    TrySpawnOrPlacePedestrian(ped, initialPopulate: false);
                    continue;
                }

                ped.Strip.SamplePose(
                    ped.DistanceAlongStrip,
                    ped.CurrentLateralNormalized,
                    out Vector3 stripPos,
                    out Vector3 stripFwd,
                    out Vector3 outwardDir);

                switch (ped.State)
                {
                    case PedBehaviorState.Walking:
                    case PedBehaviorState.Standing:
                    case PedBehaviorState.Anticipating:
                    {
                        if (CheckIncomingVehicleThreat(ped, currentPos, outwardDir, out Vector3 escapeDir, out Vector3 threatPos))
                        {
                            TriggerPedestrianDive(ped, escapeDir, threatPos);
                            break;
                        }

                        if (ped.State == PedBehaviorState.Standing)
                        {
                            ped.StateTimer -= dt;
                            if (ped.StateTimer <= 0f)
                            {
                                ped.State = PedBehaviorState.Walking;
                                ped.ActiveClip = ped.Archetype.ClipWalk;
                                ped.AnimTime = 0f;
                            }
                            ped.RootObject.transform.position = stripPos;
                        }
                        else
                        {
                            ped.CurrentLateralNormalized = Mathf.MoveTowards(
                                ped.CurrentLateralNormalized,
                                ped.TargetLateralNormalized,
                                0.6f * dt);

                            ped.DistanceAlongStrip += ped.WalkDirection * ped.BaseWalkSpeed * dt;
                            if (ped.DistanceAlongStrip >= ped.Strip.TotalLength)
                            {
                                ped.DistanceAlongStrip = ped.Strip.TotalLength;
                                ped.WalkDirection = -1;
                            }
                            else if (ped.DistanceAlongStrip <= 0f)
                            {
                                ped.DistanceAlongStrip = 0f;
                                ped.WalkDirection = 1;
                            }

                            ped.Strip.SamplePose(
                                ped.DistanceAlongStrip,
                                ped.CurrentLateralNormalized,
                                out Vector3 nextPos,
                                out Vector3 nextFwd,
                                out _);

                            Vector3 walkFwd = ped.WalkDirection > 0 ? nextFwd : -nextFwd;
                            walkFwd.y = 0f;
                            if (walkFwd.sqrMagnitude > 0.0001f)
                            {
                                Quaternion targetRot = Quaternion.LookRotation(walkFwd.normalized, Vector3.up);
                                ped.RootObject.transform.rotation = Quaternion.Slerp(
                                    ped.RootObject.transform.rotation,
                                    targetRot,
                                    dt * 10f);
                            }
                            ped.RootObject.transform.position = nextPos;
                        }
                        break;
                    }

                    case PedBehaviorState.Diving:
                    {
                        ped.StateTimer += dt;
                        float diveDuration = ped.ActiveClip != null && ped.ActiveClip.FrameMeshes != null
                            ? (ped.ActiveClip.FrameMeshes.Length / Mathf.Max(1f, ped.ActiveClip.Fps))
                            : 0.65f;

                        // Translate rapidly out of harm's way along DiveWorldDir
                        Vector3 newPos = currentPos + ped.DiveWorldDir * (ped.DiveSpeed * dt);
                        newPos.y = stripPos.y;
                        ped.RootObject.transform.position = newPos;
                        ped.CurrentLateralNormalized = Mathf.MoveTowards(ped.CurrentLateralNormalized, 0.95f, 2.5f * dt);

                        if (ped.StateTimer >= diveDuration)
                        {
                            ped.State = PedBehaviorState.OnGround;
                            ped.StateTimer = 0f;
                            ped.AnimTime = 0f;
                            ped.ActiveClip = ped.DiveIsLeft ? ped.Archetype.ClipGroundLeft : ped.Archetype.ClipGroundRight;
                        }
                        break;
                    }

                    case PedBehaviorState.OnGround:
                    {
                        ped.StateTimer += dt;
                        // Stay down briefly until vehicles are clear
                        if (ped.StateTimer >= 0.85f && IsSafeFromVehicles(currentPos, 6.5f))
                        {
                            ped.State = PedBehaviorState.GettingUp;
                            ped.StateTimer = 0f;
                            ped.AnimTime = 0f;
                            ped.ActiveClip = ped.DiveIsLeft ? ped.Archetype.ClipGetUpLeft : ped.Archetype.ClipGetUpRight;
                        }
                        break;
                    }

                    case PedBehaviorState.GettingUp:
                    {
                        ped.StateTimer += dt;
                        if (CheckIncomingVehicleThreat(ped, currentPos, outwardDir, out Vector3 escapeDir, out Vector3 threatPos))
                        {
                            TriggerPedestrianDive(ped, escapeDir, threatPos);
                            break;
                        }

                        float getUpDuration = ped.ActiveClip != null && ped.ActiveClip.FrameMeshes != null
                            ? (ped.ActiveClip.FrameMeshes.Length / Mathf.Max(1f, ped.ActiveClip.Fps))
                            : 1.2f;

                        if (ped.StateTimer >= getUpDuration)
                        {
                            ped.State = PedBehaviorState.Walking;
                            ped.StateTimer = 0f;
                            ped.AnimTime = 0f;
                            ped.ActiveClip = ped.Archetype.ClipWalk;
                            ped.TargetLateralNormalized = Mathf.Lerp(-0.2f, 0.45f, (float)rng.NextDouble());
                        }
                        break;
                    }
                }

                EnforceHardVehicleExclusion(ped, outwardDir);
                UpdatePedestrianMeshFrame(ped, dt);
            }
        }
    }
}
