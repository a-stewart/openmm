using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace MM2.Archive
{
    public struct MM2RaceWaypoint
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float AngleDegrees;
        public float Radius;
    }

    public class MM2CruiseSpawnLocation
    {
        public string DisplayName;
        public string FileName;
        public string VirtualPath;
        public string PhotoStem;
        public Vector3 PositionUnity;
        public Quaternion RotationUnity;
        public float AngleDegrees;
        public bool IsRandom;
    }

    public struct MM2RaceMetadata
    {
        public int TimeOfDayIndex;
        public int WeatherIndex;
        public int Opponents;
        public int CopDensity;
        public int TrafficDensity;
        public int PedestrianDensity;
        public int NumLaps;
        public float TimeLimit;
        public bool IsValid;
    }

    public struct MM2CrashLessonStage
    {
        public string WaypointFileStem;
        public int NumCheckpoints;
        public int NumOpponents;
        public float TimeLimit;
        public int AmbientDensity;
        public int ChkFlags;
        public int EventType;
        public float CornerSpeedMph;
        public float FollowDistNear;
        public float FollowDistFar;
        public string OpponentCarId;
        public int OpponentColor;
        public string OpponentFileStem;
        public float CopChaseDistance;
    }

    public enum MM2CrashLessonType
    {
        Lesson,
        Midterm,
        Final,
        Other
    }

    public class MM2CrashLessonSpec
    {
        public int LessonIndex;
        public string CsvName;
        public MM2CrashLessonType LessonType;
        public string CurriculumLabel;
        public string DropdownLabel;
        public string Description;
        public bool IsExam;
        public int TimeOfDayIndex;
        public int WeatherIndex;
        public int CopDensity;
        public int TrafficDensity;
        public int PedestrianDensity;
        public MM2CrashLessonStage[] Stages;
        public string[] PreRaceClips;
        public string[] WinClips;
        public string[] LoseClips;
    }

    public class MM2CityRewardSpec
    {
        public string CityMapName;
        public string CityDisplayName;
        public string RaceType;          // "blitz", "circuit", "race" (Checkpoint), or "crash"
        public bool IsHalf;              // RaceNum == "half"
        public bool IsAll;               // RaceNum == "all"
        public int SpecificRaceIndex;    // >= 0 when RaceNum is an integer index
        public string CarBaseName;       // e.g. "vpcoop2k", "vpauditt", "vpvwcup"
        public int VariantNum;           // 0 = unlocks vehicle itself; > 0 = unlocks paintjob index VariantNum
        public string UnlockMessage;     // Unlock notification message parsed from .cinfo
    }

    public class CitySpec
    {
        public string DisplayName;
        public string MapName;
        public string RaceDir;
        public int MustPlace = 3;
        public int UnlockGroup = 3;
        public string[] BlitzNames;
        public string[] CircuitNames;
        public string[] CheckpointNames;
        public Sprite CustomMapPreviewSprite;
        public MM2CityRewardSpec[] Rewards = Array.Empty<MM2CityRewardSpec>();

        public bool HasCrashCourse;
        public string CrashButtonLabel;
        public string CrashSchoolTitle;
        public string CrashIntroBlurb;
        public string CrashSchoolSubtitle;
        public string CrashDefaultVehicle;
        public int CrashDefaultColor;
        public string CrashSpeechDir;
        public MM2CrashLessonSpec[] CrashLessons;
        public int[] CrashCheckpointSpeechIndices;
        public string CrashCheckpointSpeechPrefix;
    }

    /// <summary>
    /// Discovers cities from tune/*.cinfo inside .ar archives and builds 3D city geometry
    /// (.psdl road/building/bridge networks + .inst/.pkg landmarks + sky_dome) at runtime.
    /// </summary>
    public static class MM2RuntimeCityLoader
    {
        private const float SidewalkOffsetHeight = 0.15f;

        private static bool isCatalogLoaded;
        private static readonly List<CitySpec> CityList = new List<CitySpec>();
        private static readonly Dictionary<string, CitySpec> CitiesByName =
            new Dictionary<string, CitySpec>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<MM2CityRewardSpec> AllCityRewards = new List<MM2CityRewardSpec>();

        private static string currentlyLoadedMapName = "sf";

        private struct WaterTriangle
        {
            public Vector3 A;
            public Vector3 B;
            public Vector3 C;
            public float MinX;
            public float MaxX;
            public float MinZ;
            public float MaxZ;
        }

        private static readonly List<WaterTriangle> CityWaterTriangles = new List<WaterTriangle>(256);
        private static bool hasRecordedWaterLevel;
        private static bool hasExplicitWaterFileLevel;
        public static float CityWaterLevel { get; private set; } = -2.0f;

        private static bool IsWaterSurfaceTexture(string tName)
        {
            if (string.IsNullOrEmpty(tName)) return false;
            // Only match deepwater textures from city/materials.csv (s_ocean, s_thames, l3water).
            // Shallow water textures (s_pond, s_water) used on lakes, lagoons, and fountains keep their solid MeshCollider
            // so the player can drive across lakes and ponds without triggering "Sleep with the fishes!".
            return tName.StartsWith("s_ocean", StringComparison.OrdinalIgnoreCase) ||
                   tName.StartsWith("s_thames", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tName, "l3water", StringComparison.OrdinalIgnoreCase);
        }

        private static void RecordWaterTriangles(IList<Vector3> positions, IList<int> tris, int baseVertexOffset = 0)
        {
            if (positions == null || tris == null) return;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                int i0 = tris[i] - baseVertexOffset;
                int i1 = tris[i + 1] - baseVertexOffset;
                int i2 = tris[i + 2] - baseVertexOffset;
                if (i0 < 0 || i0 >= positions.Count ||
                    i1 < 0 || i1 >= positions.Count ||
                    i2 < 0 || i2 >= positions.Count)
                {
                    continue;
                }

                Vector3 a = positions[i0];
                Vector3 b = positions[i1];
                Vector3 c = positions[i2];

                float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
                float maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                // Ignore steep/sloped boundary triangles so coastal wall artifacts never register as water surfaces
                if (maxY - minY > 1.2f)
                {
                    continue;
                }

                float avgY = (a.y + b.y + c.y) / 3f;
                if (!hasExplicitWaterFileLevel)
                {
                    if (!hasRecordedWaterLevel || avgY < CityWaterLevel)
                    {
                        CityWaterLevel = avgY;
                        hasRecordedWaterLevel = true;
                    }
                }
                else if (!hasRecordedWaterLevel && Mathf.Abs(avgY - CityWaterLevel) <= 1.5f)
                {
                    // Snap to exact PSDL ocean/river mesh height (e.g. -2.0 in SF vs -1.9 in sf.water, -4.0 in London vs -3.8 in london.water)
                    CityWaterLevel = avgY;
                    hasRecordedWaterLevel = true;
                }

                CityWaterTriangles.Add(new WaterTriangle
                {
                    A = a,
                    B = b,
                    C = c,
                    MinX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - 0.35f,
                    MaxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + 0.35f,
                    MinZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - 0.35f,
                    MaxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z)) + 0.35f
                });
            }
        }

        private static bool IsPointInTriangleXZ(float px, float pz, Vector3 a, Vector3 b, Vector3 c)
        {
            float d1 = (px - b.x) * (a.z - b.z) - (a.x - b.x) * (pz - b.z);
            float d2 = (px - c.x) * (b.z - c.z) - (b.x - c.x) * (pz - c.z);
            float d3 = (px - a.x) * (c.z - a.z) - (c.x - a.x) * (pz - a.z);

            const float eps = 0.05f;
            bool hasNeg = (d1 < -eps) || (d2 < -eps) || (d3 < -eps);
            bool hasPos = (d1 > eps) || (d2 > eps) || (d3 > eps);
            return !(hasNeg && hasPos);
        }

        private static bool HasSolidGroundBelow(Vector3 worldPos, Collider ignoreCollider, float minGroundY, float maxProbeDist)
        {
            Vector3 origin = worldPos + Vector3.up * 1.1f;
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, maxProbeDist, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || (col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic)) continue;
                if (ignoreCollider != null && (col == ignoreCollider || col.transform.IsChildOf(ignoreCollider.transform.root))) continue;
                if (hits[i].normal.y < 0.35f) continue;
                if (hits[i].point.y >= minGroundY)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasSolidCeilingAbove(Vector3 worldPos, Collider ignoreCollider, float maxProbeDist)
        {
            Vector3 origin = worldPos + Vector3.up * 0.4f;
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.up, maxProbeDist, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || (col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic)) continue;
                if (ignoreCollider != null && (col == ignoreCollider || col.transform.IsChildOf(ignoreCollider.transform.root))) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks whether the given vehicle position has entered city water (bay, river, ocean, pond, or horizon water)
        /// while not supported by a solid bridge/pier/tunnel floor.
        /// </summary>
        public static bool IsPointInWater(Vector3 worldPos, Collider ignoreCollider, out float waterSurfaceY)
        {
            waterSurfaceY = CityWaterLevel;

            // 1. Check authored PSDL / PKG water surface triangles (s_ocean, s_thames, s_water, s_pond, l3water)
            for (int i = 0; i < CityWaterTriangles.Count; i++)
            {
                WaterTriangle wt = CityWaterTriangles[i];
                if (worldPos.x < wt.MinX || worldPos.x > wt.MaxX ||
                    worldPos.z < wt.MinZ || worldPos.z > wt.MaxZ)
                {
                    continue;
                }

                float triY = (wt.A.y + wt.B.y + wt.C.y) / 3f;
                if (worldPos.y <= triY + 0.55f && worldPos.y >= triY - 25.0f)
                {
                    if (IsPointInTriangleXZ(worldPos.x, worldPos.z, wt.A, wt.B, wt.C))
                    {
                        if (!HasSolidGroundBelow(worldPos, ignoreCollider, triY + 0.25f, 3.2f) &&
                            !HasSolidCeilingAbove(worldPos, ignoreCollider, 14f))
                        {
                            waterSurfaceY = triY;
                            return true;
                        }
                    }
                }
            }

            // 2. Fallback for driving off coastal piers/quays onto Runtime_HorizonWater outside PSDL rooms
            //    (excluding underground subway/road tunnels which have solid floor/ceiling colliders)
            if (worldPos.y <= CityWaterLevel + 0.45f && worldPos.y >= CityWaterLevel - 25.0f)
            {
                if (!HasSolidGroundBelow(worldPos, ignoreCollider, worldPos.y - 2.5f, 3.8f) &&
                    !HasSolidCeilingAbove(worldPos, ignoreCollider, 14f))
                {
                    waterSurfaceY = CityWaterLevel;
                    return true;
                }
            }

            return false;
        }

        public static IReadOnlyList<CitySpec> GetAllCities()
        {
            EnsureCatalogLoaded();
            return CityList;
        }

        public static IReadOnlyList<MM2CityRewardSpec> GetAllCityRewards()
        {
            EnsureCatalogLoaded();
            return AllCityRewards;
        }

        public static string[] GetCityNames()
        {
            EnsureCatalogLoaded();
            var names = new string[CityList.Count];
            for (int i = 0; i < CityList.Count; i++)
            {
                names[i] = CityList[i].DisplayName;
            }
            return names;
        }

        public static CitySpec GetCitySpec(string displayNameOrMapName)
        {
            EnsureCatalogLoaded();
            if (!string.IsNullOrEmpty(displayNameOrMapName) &&
                CitiesByName.TryGetValue(displayNameOrMapName, out CitySpec spec))
            {
                return spec;
            }
            return CityList.Count > 0 ? CityList[0] : new CitySpec { DisplayName = "Default City", MapName = "default_city", RaceDir = "default_city" };
        }

        public static void EnsureCatalogLoaded()
        {
            if (isCatalogLoaded && CityList.Count > 0) return;
            CityList.Clear();
            CitiesByName.Clear();
            AllCityRewards.Clear();

            MM2ArchiveSystem.EnsureInitialized();
            List<string> cinfoFiles = MM2ArchiveSystem.GetFilesWithPrefixAndExtension("tune", ".cinfo");
            var loadedRewardPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string vPath in cinfoFiles)
            {
                string text = MM2ArchiveSystem.ReadFileText(vPath);
                if (string.IsNullOrEmpty(text)) continue;

                var fields = ParseKeyValueText(text);
                string mapName = fields.TryGetValue("MapName", out string mn) ? mn : System.IO.Path.GetFileNameWithoutExtension(vPath);
                string locName = fields.TryGetValue("LocalizedName", out string ln) && !string.IsNullOrEmpty(ln) ? ln : mapName;
                string raceDir = fields.TryGetValue("RaceDir", out string rd) && !string.IsNullOrEmpty(rd) ? rd : mapName;

                // Ensure the corresponding MM2 .psdl or MM1 .ptl / city .bms directory exists in the archives
                bool hasMm2City = MM2ArchiveSystem.FileExists($"city/{mapName}.psdl");
                bool hasMm1City = MM2ArchiveSystem.FileExists($"city/{mapName}.ptl") ||
                                  MM2ArchiveSystem.HasBmsDirectory($"{mapName}city");
                if (!hasMm2City && !hasMm1City)
                {
                    continue;
                }

                int mustPlace = 3;
                if (fields.TryGetValue("MustPlace", out string mpStr) &&
                    int.TryParse(mpStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMp) &&
                    parsedMp > 0)
                {
                    mustPlace = parsedMp;
                }

                int unlockGroup = 3;
                if (fields.TryGetValue("UnlockGroup", out string ugStr) &&
                    int.TryParse(ugStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedUg) &&
                    parsedUg > 0)
                {
                    unlockGroup = parsedUg;
                }

                var spec = new CitySpec
                {
                    DisplayName = locName,
                    MapName = mapName.ToLowerInvariant(),
                    RaceDir = raceDir.ToLowerInvariant(),
                    MustPlace = mustPlace,
                    UnlockGroup = unlockGroup,
                    BlitzNames = ResolveRaceNameList(raceDir, mapName, "blitz", fields.TryGetValue("BlitzNames", out string bn) ? bn : null, fields.TryGetValue("BlitzCount", out string bc) ? bc : null),
                    CircuitNames = ResolveRaceNameList(raceDir, mapName, "circuit", fields.TryGetValue("CircuitNames", out string cn) ? cn : null, fields.TryGetValue("CircuitCount", out string cc) ? cc : null),
                    CheckpointNames = ResolveRaceNameList(raceDir, mapName, "race", fields.TryGetValue("CheckpointNames", out string cpn) ? cpn : null, fields.TryGetValue("CheckpointCount", out string cpc) ? cpc : null),
                    CustomMapPreviewSprite = TryLoadCityMapSprite(mapName)
                };

                PopulateCrashCourseData(spec, fields);
                PopulateCityRewardsData(spec, loadedRewardPaths);

                CitiesByName[spec.DisplayName] = spec;
                CitiesByName[spec.MapName] = spec;
                CityList.Add(spec);
            }

            // Also scan any standalone race/*/*_rewards.csv files from mod archives
            List<string> allRewardCsvs = MM2ArchiveSystem.GetFilesWithPrefixAndExtension("race", "_rewards.csv");
            foreach (string rPath in allRewardCsvs)
            {
                if (loadedRewardPaths.Contains(rPath)) continue;
                loadedRewardPaths.Add(rPath);

                // Infer city code from path "race/<city>/<file>_rewards.csv"
                string norm = rPath.Replace('\\', '/');
                string[] parts = norm.Split('/');
                string cityKey = parts.Length >= 3 ? parts[1] : "sf";
                CitySpec targetCity = CitiesByName.TryGetValue(cityKey, out CitySpec foundCity) ? foundCity : (CityList.Count > 0 ? CityList[0] : null);
                string cMap = targetCity != null ? targetCity.MapName : cityKey.ToLowerInvariant();
                string cDisp = targetCity != null ? targetCity.DisplayName : cityKey;

                List<MM2CityRewardSpec> parsed = ParseRewardsCsvText(MM2ArchiveSystem.ReadFileText(rPath), cMap, cDisp);
                if (parsed.Count > 0)
                {
                    AllCityRewards.AddRange(parsed);
                    if (targetCity != null)
                    {
                        var combined = new List<MM2CityRewardSpec>(targetCity.Rewards ?? Array.Empty<MM2CityRewardSpec>());
                        combined.AddRange(parsed);
                        targetCity.Rewards = combined.ToArray();
                    }
                }
            }

            // Sort so San Francisco, London, and Chicago appear first, followed by custom mod cities alphabetically
            CityList.Sort((a, b) =>
            {
                int Rank(CitySpec c)
                {
                    if (c.MapName == "sf") return 0;
                    if (c.MapName == "london") return 1;
                    if (c.MapName == "chicago") return 2;
                    return 3;
                }
                int rA = Rank(a);
                int rB = Rank(b);
                return rA != rB ? rA.CompareTo(rB) : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            if (CityList.Count == 0)
            {
                var defaultFallback = new CitySpec { DisplayName = "Default City", MapName = "default_city", RaceDir = "default_city" };
                CityList.Add(defaultFallback);
                CitiesByName["Default City"] = defaultFallback;
                CitiesByName["default_city"] = defaultFallback;
            }

            isCatalogLoaded = true;
            Debug.Log($"[MM2RuntimeCityLoader] Discovered {CityList.Count} cities and {AllCityRewards.Count} reward rules from .ar archives: {string.Join(", ", GetCityNames())}");
        }

        private static void PopulateCityRewardsData(CitySpec spec, HashSet<string> loadedRewardPaths)
        {
            if (spec == null) return;

            var candidatePaths = new List<string>
            {
                $"race/{spec.RaceDir}/{spec.MapName}_rewards.csv",
                $"race/{spec.RaceDir}/{spec.RaceDir}_rewards.csv",
                $"race/{spec.MapName}/{spec.MapName}_rewards.csv"
            };

            foreach (string extraPath in MM2ArchiveSystem.GetFilesWithPrefixAndExtension($"race/{spec.RaceDir}", "_rewards.csv"))
            {
                if (!candidatePaths.Contains(extraPath)) candidatePaths.Add(extraPath);
            }
            if (!string.Equals(spec.RaceDir, spec.MapName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string extraPath in MM2ArchiveSystem.GetFilesWithPrefixAndExtension($"race/{spec.MapName}", "_rewards.csv"))
                {
                    if (!candidatePaths.Contains(extraPath)) candidatePaths.Add(extraPath);
                }
            }

            var cityRewards = new List<MM2CityRewardSpec>();
            foreach (string vPath in candidatePaths)
            {
                string normPath = MM2ArchiveSystem.NormalizeVirtualPath(vPath);
                if (loadedRewardPaths != null && loadedRewardPaths.Contains(normPath)) continue;
                if (!MM2ArchiveSystem.FileExists(normPath)) continue;

                if (loadedRewardPaths != null) loadedRewardPaths.Add(normPath);
                string csvText = MM2ArchiveSystem.ReadFileText(normPath);
                List<MM2CityRewardSpec> parsed = ParseRewardsCsvText(csvText, spec.MapName, spec.DisplayName);
                if (parsed.Count > 0)
                {
                    cityRewards.AddRange(parsed);
                    AllCityRewards.AddRange(parsed);
                }
            }

            spec.Rewards = cityRewards.ToArray();
        }

        private static List<MM2CityRewardSpec> ParseRewardsCsvText(string csvText, string cityMapName, string cityDisplayName)
        {
            var list = new List<MM2CityRewardSpec>();
            if (string.IsNullOrEmpty(csvText)) return list;

            string[] lines = csvText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                string[] cols = line.Split(',');
                if (cols.Length < 4) continue;

                string raceType = cols[0].Trim();
                if (string.IsNullOrEmpty(raceType) ||
                    string.Equals(raceType, "RaceType", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string raceNumRaw = cols[1].Trim();
                string carName = cols[2].Trim();
                string variantRaw = cols[3].Trim();
                if (string.IsNullOrEmpty(carName)) continue;

                string unlockMsg = string.Empty;
                if (cols.Length == 5)
                {
                    unlockMsg = cols[4].Trim();
                }
                else if (cols.Length > 5)
                {
                    unlockMsg = string.Join(",", cols, 4, cols.Length - 4).Trim();
                }

                bool isHalf = string.Equals(raceNumRaw, "half", StringComparison.OrdinalIgnoreCase);
                bool isAll = string.Equals(raceNumRaw, "all", StringComparison.OrdinalIgnoreCase);
                int specificRaceIdx = -1;
                if (!isHalf && !isAll)
                {
                    if (!int.TryParse(raceNumRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out specificRaceIdx))
                    {
                        continue;
                    }
                }

                int.TryParse(variantRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int variantNum);

                list.Add(new MM2CityRewardSpec
                {
                    CityMapName = (cityMapName ?? "sf").ToLowerInvariant(),
                    CityDisplayName = string.IsNullOrEmpty(cityDisplayName) ? cityMapName : cityDisplayName,
                    RaceType = raceType.ToLowerInvariant(),
                    IsHalf = isHalf,
                    IsAll = isAll,
                    SpecificRaceIndex = specificRaceIdx,
                    CarBaseName = carName,
                    VariantNum = Mathf.Max(0, variantNum),
                    UnlockMessage = unlockMsg
                });
            }

            return list;
        }

        private static string[] ResolveRaceNameList(
            string raceDir,
            string mapName,
            string filePrefix,
            string rawNames,
            string rawCount
        )
        {
            string[] names = SplitPipeList(rawNames);
            int declaredCount = 0;
            if (!string.IsNullOrEmpty(rawCount))
            {
                int.TryParse(rawCount.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out declaredCount);
            }

            // Verify which waypoint CSVs actually exist in the mounted .ar archives
            var validNames = new List<string>();
            int maxCheck = Math.Max(names.Length, Math.Max(declaredCount, 16));
            for (int i = 0; i < maxCheck; i++)
            {
                bool exists =
                    MM2ArchiveSystem.FileExists($"race/{raceDir}/{filePrefix}{i}waypoints.csv") ||
                    MM2ArchiveSystem.FileExists($"race/{mapName}/{filePrefix}{i}waypoints.csv");

                if (i < names.Length && (exists || i < declaredCount || names.Length > 0))
                {
                    if (exists)
                    {
                        validNames.Add(names[i]);
                    }
                }
                else if (exists && (declaredCount == 0 || i < declaredCount))
                {
                    string modeLabel = filePrefix == "race" ? "Checkpoint" :
                        char.ToUpperInvariant(filePrefix[0]) + filePrefix.Substring(1);
                    validNames.Add($"{modeLabel} {i + 1}");
                }
            }

            return validNames.ToArray();
        }

        public static string[] GetRaceNames(string locationName, string raceType)
        {
            CitySpec spec = GetCitySpec(locationName);
            if (spec == null || string.IsNullOrEmpty(raceType)) return Array.Empty<string>();

            if (string.Equals(raceType, "Blitz", StringComparison.OrdinalIgnoreCase))
            {
                return spec.BlitzNames ?? Array.Empty<string>();
            }
            if (string.Equals(raceType, "Checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                return spec.CheckpointNames ?? Array.Empty<string>();
            }
            if (string.Equals(raceType, "Circuit", StringComparison.OrdinalIgnoreCase))
            {
                return spec.CircuitNames ?? Array.Empty<string>();
            }
            return Array.Empty<string>();
        }

        public static MM2RaceMetadata GetRaceMetadata(string locationName, string raceType, int raceIndex)
        {
            var fallback = new MM2RaceMetadata
            {
                TimeOfDayIndex = 1,
                WeatherIndex = 0,
                Opponents = string.Equals(raceType, "Blitz", StringComparison.OrdinalIgnoreCase) ? 0 : 7,
                CopDensity = 0,
                TrafficDensity = 30,
                PedestrianDensity = 10,
                NumLaps = 3,
                TimeLimit = 45f,
                IsValid = false
            };

            CitySpec spec = GetCitySpec(locationName);
            if (spec == null || string.IsNullOrEmpty(raceType) ||
                string.Equals(raceType, "Cruise", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            string dataFileName = "mmblitzdata.csv";
            if (string.Equals(raceType, "Checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                dataFileName = "mmracedata.csv";
            }
            else if (string.Equals(raceType, "Circuit", StringComparison.OrdinalIgnoreCase))
            {
                dataFileName = "mmcircuitdata.csv";
            }

            string csv = MM2ArchiveSystem.ReadFileText($"race/{spec.RaceDir}/{dataFileName}");
            if (string.IsNullOrEmpty(csv))
            {
                csv = MM2ArchiveSystem.ReadFileText($"race/{spec.MapName}/{dataFileName}");
            }
            if (string.IsNullOrEmpty(csv)) return fallback;

            string[] lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int targetLine = 1 + Mathf.Max(0, raceIndex);
            if (targetLine >= lines.Length) return fallback;

            string[] cols = lines[targetLine].Split(',');
            if (cols.Length < 10) return fallback;

            // Format: Description(0), CarType(1), TimeofDay(2), Weather(3), Opponents(4), Cops(5), Ambient(6), Peds(7), NumLaps(8), TimeLimit(9), Difficulty(10)
            int.TryParse(cols[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tod);
            int.TryParse(cols[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int weath);
            int.TryParse(cols[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int opps);
            float.TryParse(cols[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float cops);
            float.TryParse(cols[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ambient);
            float.TryParse(cols[7].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float peds);
            int.TryParse(cols[8].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int laps);
            float.TryParse(cols[9].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float timeLimit);

            return new MM2RaceMetadata
            {
                TimeOfDayIndex = Mathf.Clamp(tod, 0, 3),
                WeatherIndex = Mathf.Clamp(weath, 0, 3),
                Opponents = Mathf.Max(0, opps),
                CopDensity = Mathf.Clamp(cops > 1.0f ? Mathf.RoundToInt(cops * 12.5f) : Mathf.RoundToInt(cops * 100f), 0, 100),
                TrafficDensity = Mathf.Clamp(Mathf.RoundToInt(ambient * 100f), 0, 100),
                PedestrianDensity = Mathf.Clamp(Mathf.RoundToInt(peds * 100f), 0, 100),
                NumLaps = Mathf.Clamp(laps > 0 ? laps : 3, 1, 20),
                TimeLimit = timeLimit > 1f ? timeLimit : 45f,
                IsValid = true
            };
        }

        public static List<CitySpec> GetCrashCourseCities()
        {
            EnsureCatalogLoaded();
            var result = new List<CitySpec>();
            // In MM2 Crash Course Intro, London Cabbie is listed first, then SF Stunt Driver, followed by any mod cities
            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < CityList.Count; i++)
                {
                    CitySpec c = CityList[i];
                    if (c == null || !c.HasCrashCourse) continue;
                    bool isLondon = string.Equals(c.MapName, "london", StringComparison.OrdinalIgnoreCase);
                    bool isSf = string.Equals(c.MapName, "sf", StringComparison.OrdinalIgnoreCase);
                    if ((pass == 0 && isLondon) ||
                        (pass == 1 && isSf) ||
                        (pass == 2 && !isLondon && !isSf))
                    {
                        result.Add(c);
                    }
                }
            }
            return result;
        }

        private static readonly string[] DefaultSfLessonTitles =
        {
            "1. Frequent Flyer",
            "2. Hit Or Miss",
            "3. Turn It Up!",
            "Midterm 1.",
            "4. Brake Dancing",
            "5. The Heat Is On",
            "6. Bonnie & Collide",
            "Midterm 2.",
            "7. Keep It Clean",
            "8. About Face",
            "9. Straight, With Chaser",
            "Midterm 3.",
            "Final Exam."
        };

        private static readonly string[] DefaultSfLessonDescriptions =
        {
            "Catch the departing ferry by making the jump across the dock before time runs out!",
            "Slalom cleanly through the park course before time expires!",
            "Maintain high speed around each corner to pass this stunt driving drill.",
            "First stunt driving midterm exam: combine high-speed cornering and precision control.",
            "Perform a clean Forward 180 maneuver and race back through the checkpoints!",
            "Evade the pursuing police cruisers and reach the final checkpoint!",
            "Ram the target vehicle until it is disabled before time runs out!",
            "Second stunt driving midterm exam: disable the target car and escape the pursuit!",
            "Navigate the busy intersection course without damaging the vehicle!",
            "Master the Reverse 180 spin maneuver and sprint to the finish!",
            "Stay within range of the lead car throughout the high-speed city chase!",
            "Third stunt driving midterm exam: shadow the lead vehicle through traffic!",
            "Final stunt driving exam: complete the full multi-stage course before time runs out!"
        };

        private static readonly string[] DefaultLondonLessonTitles =
        {
            "1. London Leap",
            "2. Cutting Corners",
            "3. The Weaver's Tale",
            "Midterm 1.",
            "4. The Knowledge",
            "5. Round You'll Go",
            "6. Follow That Car!",
            "Midterm 2.",
            "7. Give Me A Brake!",
            "8. River Dancing",
            "9. Artful Dodging",
            "Midterm 3.",
            "Final Exam."
        };

        private static readonly string[] DefaultLondonLessonDescriptions =
        {
            "Clear the raising drawbridge jump before time runs out!",
            "Hold your speed through the tight city corners to pass the drill!",
            "Weave cleanly through the slalom obstacles before the timer expires!",
            "First cab driving midterm exam: high-speed cornering through city traffic!",
            "Navigate from landmark to landmark across the city using your map skills!",
            "Cross the roundabouts quickly without damaging the vehicle!",
            "Tail the target vehicle closely through the streets without losing sight or getting too close!",
            "Second cab driving midterm exam: precision slalom followed by a city tailing mission!",
            "Execute a clean Reverse 180 turn and race to the checkpoint!",
            "Launch across the river jump and land cleanly on the far bank!",
            "Outrun the pursuing vehicles and reach the safe destination!",
            "Third cab driving midterm exam: complete the full obstacle course under time pressure!",
            "Final cab driving exam: pass both the city tailing stage and obstacle finale!"
        };

        private static MM2CrashLessonType ClassifyCrashLessonType(string csvDescription)
        {
            if (string.IsNullOrEmpty(csvDescription))
            {
                return MM2CrashLessonType.Other;
            }
            string trimmed = csvDescription.Trim();
            if (trimmed.StartsWith("lesson", StringComparison.OrdinalIgnoreCase))
            {
                return MM2CrashLessonType.Lesson;
            }
            if (trimmed.StartsWith("midtrm", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("midterm", StringComparison.OrdinalIgnoreCase))
            {
                return MM2CrashLessonType.Midterm;
            }
            if (trimmed.StartsWith("final", StringComparison.OrdinalIgnoreCase))
            {
                return MM2CrashLessonType.Final;
            }
            return MM2CrashLessonType.Other;
        }

        private static bool TryGetHardcodedCityCrashLessonText(
            string cityDisplayName,
            int lessonNumber1Based,
            MM2CrashLessonType lessonType,
            int typeOccurrence1Based,
            int totalOfType,
            int rawIndex,
            out string curriculumLabel,
            out string description)
        {
            curriculumLabel = null;
            description = null;

            bool isSanFrancisco = string.Equals(cityDisplayName, "San Francisco", StringComparison.OrdinalIgnoreCase);
            bool isLondon = string.Equals(cityDisplayName, "London", StringComparison.OrdinalIgnoreCase);
            if (!isSanFrancisco && !isLondon)
            {
                return false;
            }

            string[] titles = isSanFrancisco ? DefaultSfLessonTitles : DefaultLondonLessonTitles;
            string[] descs = isSanFrancisco ? DefaultSfLessonDescriptions : DefaultLondonLessonDescriptions;

            // Map by lesson/exam index if within the standard 13-entry curriculum
            if (rawIndex >= 0 && rawIndex < titles.Length)
            {
                curriculumLabel = titles[rawIndex];
                description = rawIndex < descs.Length ? descs[rawIndex] : null;
                return true;
            }

            return false;
        }

        private static void PopulateCrashCourseData(CitySpec spec, Dictionary<string, string> cinfoFields)
        {
            if (spec == null) return;

            string crashCsv = MM2ArchiveSystem.ReadFileText($"race/{spec.RaceDir}/mmcrashdata.csv");
            if (string.IsNullOrEmpty(crashCsv))
            {
                crashCsv = MM2ArchiveSystem.ReadFileText($"race/{spec.MapName}/mmcrashdata.csv");
            }

            bool hasAnyStageCsv =
                MM2ArchiveSystem.FileExists($"race/{spec.RaceDir}/crash0data.csv") ||
                MM2ArchiveSystem.FileExists($"race/{spec.MapName}/crash0data.csv");

            if (string.IsNullOrEmpty(crashCsv) && !hasAnyStageCsv)
            {
                spec.HasCrashCourse = false;
                return;
            }

            bool isSf = string.Equals(spec.DisplayName, "San Francisco", StringComparison.OrdinalIgnoreCase);
            bool isLondon = string.Equals(spec.DisplayName, "London", StringComparison.OrdinalIgnoreCase);

            spec.CrashButtonLabel = cinfoFields != null && cinfoFields.TryGetValue("CrashButtonLabel", out string cbl) && !string.IsNullOrEmpty(cbl)
                ? cbl
                : $"{spec.DisplayName.ToUpperInvariant()} CRASH COURSE";

            spec.CrashSchoolTitle = cinfoFields != null && cinfoFields.TryGetValue("CrashSchoolTitle", out string cst) && !string.IsNullOrEmpty(cst)
                ? cst
                : $"{spec.DisplayName.ToUpperInvariant()} DRIVING SCHOOL:";

            spec.CrashIntroBlurb = cinfoFields != null && cinfoFields.TryGetValue("CrashIntroBlurb", out string cib) && !string.IsNullOrEmpty(cib)
                ? cib
                : $"MASTER THE STREETS OF {spec.DisplayName.ToUpperInvariant()}\nTHROUGH RIGOROUS DRIVING CHALLENGES!";

            spec.CrashSchoolSubtitle = cinfoFields != null && cinfoFields.TryGetValue("CrashSchoolSubtitle", out string css) && !string.IsNullOrEmpty(css)
                ? css
                : $"LEARN ADVANCED DRIVING IN {spec.DisplayName.ToUpperInvariant()}\nTHROUGH RIGOROUS SKILLS DEVELOPMENT";

            spec.CrashDefaultVehicle = cinfoFields != null && cinfoFields.TryGetValue("CrashVehicle", out string cv) && !string.IsNullOrEmpty(cv)
                ? cv
                : (isLondon ? "vpcab" : "vpbullet");

            spec.CrashDefaultColor = 0;
            if (cinfoFields != null && cinfoFields.TryGetValue("CrashVehicleColor", out string cvc))
            {
                int.TryParse(cvc.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out spec.CrashDefaultColor);
            }

            spec.CrashSpeechDir = cinfoFields != null && cinfoFields.TryGetValue("CrashSpeechDir", out string csd) && !string.IsNullOrEmpty(csd)
                ? csd
                : (isLondon ? "ccl" : (isSf ? "ccs" : spec.MapName));

            string[] customTitles = cinfoFields != null && cinfoFields.TryGetValue("CrashLessonNames", out string cln)
                ? SplitPipeList(cln)
                : Array.Empty<string>();
            string[] customDescs = cinfoFields != null && cinfoFields.TryGetValue("CrashLessonDescriptions", out string cld)
                ? SplitPipeList(cld)
                : Array.Empty<string>();

            // Parse optional checkpoint speech index info (e.g. London's The Knowledge landmark callouts)
            string cpIdxText = MM2ArchiveSystem.ReadFileText($"aud/spchdata/{spec.CrashSpeechDir}/cc_cpoint_indexinfo.csv");
            if (!string.IsNullOrEmpty(cpIdxText))
            {
                string[] parts = cpIdxText.Split(new[] { ',', '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var idxList = new List<int>(parts.Length);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    {
                        idxList.Add(v);
                    }
                }
                spec.CrashCheckpointSpeechIndices = idxList.ToArray();
            }

            string cpWaveText = MM2ArchiveSystem.ReadFileText($"aud/spchdata/{spec.CrashSpeechDir}/cc_cpoint_waveinfo.csv");
            if (!string.IsNullOrEmpty(cpWaveText))
            {
                string[] lines = cpWaveText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] cols = lines[i].Split(',');
                    if (cols.Length == 0) continue;
                    string col0 = cols[0].Trim();
                    if (string.IsNullOrEmpty(col0) ||
                        col0.StartsWith("Name prefix", StringComparison.OrdinalIgnoreCase) ||
                        col0.EndsWith("header", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (cols.Length >= 2 && col0.Equals("CPWAVE", StringComparison.OrdinalIgnoreCase))
                    {
                        spec.CrashCheckpointSpeechPrefix = cols[1].Trim();
                        break;
                    }
                    spec.CrashCheckpointSpeechPrefix = col0;
                    break;
                }
            }

            string[] metaLines = !string.IsNullOrEmpty(crashCsv)
                ? crashCsv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();

            // Check if the CSV header defines an optional "Name" column
            int nameColIndex = -1;
            if (metaLines.Length > 0)
            {
                string[] headerCols = metaLines[0].Split(',');
                for (int c = 0; c < headerCols.Length; c++)
                {
                    if (string.Equals(headerCols[c].Trim(), "Name", StringComparison.OrdinalIgnoreCase))
                    {
                        nameColIndex = c;
                        break;
                    }
                }
            }

            int maxLessons = metaLines.Length > 1 ? metaLines.Length - 1 : 16;

            // First pass: collect valid lessons and count how many of each prefix type exist
            var rawEntries = new List<(
                int rawIdx,
                string csvName,
                string csvExplicitName,
                MM2CrashLessonType lessonType,
                int tod,
                int weather,
                int copDensity,
                int trafficDensity,
                int pedDensity,
                MM2CrashLessonStage[] stages
            )>(maxLessons);

            var typeTotalCounts = new Dictionary<MM2CrashLessonType, int>
            {
                { MM2CrashLessonType.Lesson, 0 },
                { MM2CrashLessonType.Midterm, 0 },
                { MM2CrashLessonType.Final, 0 },
                { MM2CrashLessonType.Other, 0 }
            };

            for (int idx = 0; idx < maxLessons; idx++)
            {
                string stageCsv = MM2ArchiveSystem.ReadFileText($"race/{spec.RaceDir}/crash{idx}data.csv");
                if (string.IsNullOrEmpty(stageCsv))
                {
                    stageCsv = MM2ArchiveSystem.ReadFileText($"race/{spec.MapName}/crash{idx}data.csv");
                }
                if (string.IsNullOrEmpty(stageCsv))
                {
                    continue;
                }

                MM2CrashLessonStage[] stages = ParseCrashStageCsv(stageCsv);
                if (stages.Length == 0)
                {
                    continue;
                }

                string csvName = $"lesson{idx + 1}";
                string csvExplicitName = null;
                int tod = 1;
                int weather = 0;
                int copDensity = 0;
                int trafficDensity = 0;
                int pedDensity = 0;

                if (idx + 1 < metaLines.Length)
                {
                    string[] mCols = metaLines[idx + 1].Split(',');
                    if (mCols.Length >= 1 && !string.IsNullOrEmpty(mCols[0].Trim()))
                    {
                        csvName = mCols[0].Trim();
                    }
                    if (nameColIndex >= 0 && nameColIndex < mCols.Length)
                    {
                        string parsedName = mCols[nameColIndex].Trim();
                        if (!string.IsNullOrEmpty(parsedName))
                        {
                            csvExplicitName = parsedName;
                        }
                    }
                    if (mCols.Length >= 8)
                    {
                        int.TryParse(mCols[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tod);
                        int.TryParse(mCols[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out weather);
                        float.TryParse(mCols[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float cops);
                        float.TryParse(mCols[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ambient);
                        float.TryParse(mCols[7].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float peds);

                        copDensity = Mathf.Clamp(cops > 1.0f ? Mathf.RoundToInt(cops * 12.5f) : Mathf.RoundToInt(cops * 100f), 0, 100);
                        trafficDensity = Mathf.Clamp(Mathf.RoundToInt(ambient * 100f), 0, 100);
                        pedDensity = Mathf.Clamp(Mathf.RoundToInt(peds * 100f), 0, 100);
                    }
                }

                if (trafficDensity == 0 && stages.Length > 0 && stages[0].AmbientDensity > 0)
                {
                    trafficDensity = stages[0].AmbientDensity;
                }

                MM2CrashLessonType lType = ClassifyCrashLessonType(csvName);
                typeTotalCounts[lType] = typeTotalCounts[lType] + 1;

                rawEntries.Add((idx, csvName, csvExplicitName, lType, tod, weather, copDensity, trafficDensity, pedDensity, stages));
            }

            // Second pass: build MM2CrashLessonSpec with incremental numbering per prefix type
            var lessons = new List<MM2CrashLessonSpec>(rawEntries.Count);
            var typeSeenCounts = new Dictionary<MM2CrashLessonType, int>
            {
                { MM2CrashLessonType.Lesson, 0 },
                { MM2CrashLessonType.Midterm, 0 },
                { MM2CrashLessonType.Final, 0 },
                { MM2CrashLessonType.Other, 0 }
            };

            for (int i = 0; i < rawEntries.Count; i++)
            {
                var entry = rawEntries[i];
                typeSeenCounts[entry.lessonType] = typeSeenCounts[entry.lessonType] + 1;
                int occurrence = typeSeenCounts[entry.lessonType];
                int totalOfType = typeTotalCounts[entry.lessonType];
                int lessonNumber1Based = typeSeenCounts[MM2CrashLessonType.Lesson];

                bool isExam = entry.lessonType == MM2CrashLessonType.Midterm ||
                              entry.lessonType == MM2CrashLessonType.Final;

                string curriculumLabel = null;
                string hardcodedDesc = null;

                // 1. Explicit "Name" column in mmcrashdata.csv takes highest priority
                if (!string.IsNullOrEmpty(entry.csvExplicitName))
                {
                    if (entry.lessonType == MM2CrashLessonType.Lesson &&
                        totalOfType > 1 &&
                        !char.IsDigit(entry.csvExplicitName[0]))
                    {
                        curriculumLabel = $"{occurrence}. {entry.csvExplicitName}";
                    }
                    else
                    {
                        curriculumLabel = entry.csvExplicitName;
                    }
                }
                // 2. Next, CrashLessonNames from .cinfo if provided
                else if (entry.rawIdx < customTitles.Length && !string.IsNullOrEmpty(customTitles[entry.rawIdx]))
                {
                    curriculumLabel = customTitles[entry.rawIdx];
                }
                // 3. Special case for cities called "London" and "San Francisco" when missing from CSV
                else if (TryGetHardcodedCityCrashLessonText(
                    spec.DisplayName,
                    lessonNumber1Based,
                    entry.lessonType,
                    occurrence,
                    totalOfType,
                    entry.rawIdx,
                    out string hcLabel,
                    out hardcodedDesc))
                {
                    curriculumLabel = hcLabel;
                }
                // 4. Prefix-based incremental fallback ("Lesson 1", "Midterm 1", "Final Exam", etc.)
                else
                {
                    switch (entry.lessonType)
                    {
                        case MM2CrashLessonType.Lesson:
                            curriculumLabel = totalOfType == 1
                                ? "Lesson"
                                : $"{occurrence}. Lesson {occurrence}";
                            break;
                        case MM2CrashLessonType.Midterm:
                            curriculumLabel = totalOfType == 1
                                ? "Midterm."
                                : $"Midterm {occurrence}.";
                            break;
                        case MM2CrashLessonType.Final:
                            curriculumLabel = totalOfType == 1
                                ? "Final Exam."
                                : $"Final Exam {occurrence}.";
                            break;
                        default:
                            curriculumLabel = !string.IsNullOrEmpty(entry.csvName)
                                ? entry.csvName
                                : (totalOfType == 1 ? "Challenge" : $"Challenge {occurrence}");
                            break;
                    }
                }

                string dropdownLabel = curriculumLabel;
                int dotSpace = curriculumLabel.IndexOf(". ", StringComparison.Ordinal);
                if (dotSpace > 0 && dotSpace <= 3 && char.IsDigit(curriculumLabel[0]))
                {
                    dropdownLabel = curriculumLabel.Substring(dotSpace + 2).Trim();
                }
                else if (curriculumLabel.EndsWith(".", StringComparison.Ordinal))
                {
                    dropdownLabel = curriculumLabel.TrimEnd('.');
                }

                string description;
                if (entry.rawIdx < customDescs.Length && !string.IsNullOrEmpty(customDescs[entry.rawIdx]))
                {
                    description = customDescs[entry.rawIdx];
                }
                else if (!string.IsNullOrEmpty(hardcodedDesc))
                {
                    description = hardcodedDesc;
                }
                else
                {
                    description = $"Complete {dropdownLabel} to advance in the curriculum.";
                }

                ParseCrashSpeechCsv(
                    spec.CrashSpeechDir,
                    entry.rawIdx,
                    out string[] preRaceClips,
                    out string[] winClips,
                    out string[] loseClips);

                lessons.Add(new MM2CrashLessonSpec
                {
                    LessonIndex = lessons.Count,
                    CsvName = entry.csvName,
                    LessonType = entry.lessonType,
                    CurriculumLabel = curriculumLabel,
                    DropdownLabel = dropdownLabel,
                    Description = description,
                    IsExam = isExam,
                    TimeOfDayIndex = Mathf.Clamp(entry.tod, 0, 3),
                    WeatherIndex = Mathf.Clamp(entry.weather, 0, 3),
                    CopDensity = entry.copDensity,
                    TrafficDensity = entry.trafficDensity,
                    PedestrianDensity = entry.pedDensity,
                    Stages = entry.stages,
                    PreRaceClips = preRaceClips,
                    WinClips = winClips,
                    LoseClips = loseClips
                });
            }

            spec.CrashLessons = lessons.ToArray();
            spec.HasCrashCourse = spec.CrashLessons.Length > 0;
        }

        private static MM2CrashLessonStage[] ParseCrashStageCsv(string csv)
        {
            var stages = new List<MM2CrashLessonStage>(2);
            string[] lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] cols = line.Split(',');
                if (cols.Length < 6) continue;

                string wpStem = cols[0].Trim();
                int numCp = 1;
                int numOpp = 0;
                float timeLim = 0f;
                int ambDensity = 0;
                int chkFlags = 0;
                int eventType = 0;
                float cornerMph = 50f;
                float followNear = 25f;
                float followFar = 75f;
                string oppCarId = "vpbullet";
                int oppColor = 0;
                string oppFileStem = null;
                float copChaseDist = 120f;

                if (cols.Length >= 8)
                {
                    // Standard MM2 crashXdata.csv layout:
                    // Filename,Event,Checkpoints,TimeLimit,AmbDensity,cornerspeed,chkflags,numopp,extra,extra
                    int.TryParse(cols[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out eventType);
                    int.TryParse(cols[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numCp);
                    float.TryParse(cols[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out timeLim);
                    if (float.TryParse(cols[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedAmb) && parsedAmb > 0f)
                    {
                        ambDensity = Mathf.Clamp(
                            parsedAmb > 1.0f ? Mathf.RoundToInt(parsedAmb) : Mathf.RoundToInt(parsedAmb * 100f),
                            0,
                            100);
                    }
                    if (float.TryParse(cols[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedCorner) && parsedCorner > 1f)
                    {
                        cornerMph = parsedCorner;
                    }
                    int.TryParse(cols[6].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out chkFlags);
                    int.TryParse(cols[7].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numOpp);
                }
                else
                {
                    // 7-column custom mod layout:
                    // name,numcheckpoints,numopp,timelimit,chkflags,eventtype,misc
                    int.TryParse(cols[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numCp);
                    int.TryParse(cols[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numOpp);
                    float.TryParse(cols[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out timeLim);
                    int.TryParse(cols[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out chkFlags);
                    int.TryParse(cols[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out eventType);

                    string misc = cols.Length > 6 ? cols[6].Trim() : string.Empty;
                    if (!string.IsNullOrEmpty(misc))
                    {
                        string[] tokens = misc.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (eventType == 4 && tokens.Length >= 1)
                        {
                            float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out cornerMph);
                        }
                        else if (eventType == 2 && tokens.Length >= 5)
                        {
                            float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out followNear);
                            float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out followFar);
                            oppCarId = tokens[2];
                            int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out oppColor);
                            oppFileStem = System.IO.Path.GetFileNameWithoutExtension(tokens[4]);
                        }
                        else if (eventType == 8 && tokens.Length >= 3)
                        {
                            oppCarId = tokens[0];
                            int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out oppColor);
                            oppFileStem = System.IO.Path.GetFileNameWithoutExtension(tokens[2]);
                        }
                        else if (eventType == 3 && tokens.Length >= 1)
                        {
                            float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out copChaseDist);
                        }
                    }
                }

                // Only EventType 2 ("Follow That Car!") and EventType 8 ("Stop That Car!") spawn an AI opponent vehicle
                if (eventType != 2 && eventType != 8)
                {
                    numOpp = 0;
                }

                stages.Add(new MM2CrashLessonStage
                {
                    WaypointFileStem = wpStem,
                    NumCheckpoints = Mathf.Max(1, numCp),
                    NumOpponents = Mathf.Max(0, numOpp),
                    TimeLimit = Mathf.Max(0f, timeLim),
                    AmbientDensity = ambDensity,
                    ChkFlags = chkFlags,
                    EventType = eventType,
                    CornerSpeedMph = cornerMph,
                    FollowDistNear = followNear,
                    FollowDistFar = followFar,
                    OpponentCarId = oppCarId,
                    OpponentColor = oppColor,
                    OpponentFileStem = oppFileStem,
                    CopChaseDistance = copChaseDist
                });
            }
            return stages.ToArray();
        }

        private static void ParseCrashSpeechCsv(
            string speechDir,
            int lessonIdx,
            out string[] preRaceClips,
            out string[] winClips,
            out string[] loseClips)
        {
            var pre = new List<string>();
            var win = new List<string>();
            var lose = new List<string>();

            if (!string.IsNullOrEmpty(speechDir))
            {
                string csv = MM2ArchiveSystem.ReadFileText($"aud/spchdata/{speechDir}/{speechDir}{lessonIdx}.csv");
                if (!string.IsNullOrEmpty(csv))
                {
                    List<string> activeSection = null;
                    string[] lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string[] cols = lines[i].Split(',');
                        if (cols.Length == 0) continue;
                        string key = cols[0].Trim();
                        if (string.IsNullOrEmpty(key) || key.StartsWith("Name prefix", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (key.StartsWith("PRERACE", StringComparison.OrdinalIgnoreCase))
                        {
                            activeSection = pre;
                            if (key.EndsWith("header", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        else if (key.StartsWith("RESULTSWIN", StringComparison.OrdinalIgnoreCase))
                        {
                            activeSection = win;
                            if (key.EndsWith("header", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        else if (key.StartsWith("RESULTSPOOR", StringComparison.OrdinalIgnoreCase) ||
                                 key.StartsWith("RESULTSMID", StringComparison.OrdinalIgnoreCase))
                        {
                            activeSection = lose;
                            if (key.EndsWith("header", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        else if (key.EndsWith("header", StringComparison.OrdinalIgnoreCase))
                        {
                            activeSection = null;
                            continue;
                        }
                        else if (activeSection != null && cols.Length >= 2 &&
                                 int.TryParse(cols[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int endSuffix) &&
                                 endSuffix > 0)
                        {
                            int suffixAdd = 0;
                            if (cols.Length >= 3)
                            {
                                int.TryParse(cols[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out suffixAdd);
                            }

                            for (int n = 1; n <= endSuffix; n++)
                            {
                                string candidate = $"{key}{n + suffixAdd:D2}";
                                if (MM2ArchiveSystem.FindWavVirtualPath(candidate) != null)
                                {
                                    activeSection.Add(candidate);
                                }
                                else if (n == 1 && MM2ArchiveSystem.FindWavVirtualPath(key) != null)
                                {
                                    activeSection.Add(key);
                                }
                            }
                            continue;
                        }

                        if (activeSection != null)
                        {
                            for (int c = 1; c < cols.Length; c++)
                            {
                                string clip = cols[c].Trim();
                                if (!string.IsNullOrEmpty(clip) && !clip.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                                {
                                    activeSection.Add(clip);
                                }
                            }
                        }
                    }
                }
            }

            preRaceClips = pre.ToArray();
            winClips = win.ToArray();
            loseClips = lose.ToArray();
        }

        public static List<MM2RaceWaypoint> LoadCrashStageWaypoints(string locationName, string waypointFileStem)
        {
            CitySpec spec = GetCitySpec(locationName);
            if (spec == null || string.IsNullOrEmpty(waypointFileStem))
            {
                return new List<MM2RaceWaypoint>();
            }

            string stem = waypointFileStem.Trim();
            if (stem.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(0, stem.Length - 4);
            }

            string csv = MM2ArchiveSystem.ReadFileText($"race/{spec.RaceDir}/{stem}.csv");
            if (string.IsNullOrEmpty(csv))
            {
                csv = MM2ArchiveSystem.ReadFileText($"race/{spec.MapName}/{stem}.csv");
            }
            return ParseWaypointsCsv(csv);
        }

        public static List<MM2RaceWaypoint> LoadRaceWaypoints(string locationName, string raceType, int raceIndex)
        {
            CitySpec spec = GetCitySpec(locationName);
            if (spec == null || string.IsNullOrEmpty(raceType)) return new List<MM2RaceWaypoint>();

            string prefix = "blitz";
            if (string.Equals(raceType, "Checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "race";
            }
            else if (string.Equals(raceType, "Circuit", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "circuit";
            }

            int idx = Mathf.Max(0, raceIndex);
            string csv = MM2ArchiveSystem.ReadFileText($"race/{spec.RaceDir}/{prefix}{idx}waypoints.csv");
            if (string.IsNullOrEmpty(csv))
            {
                csv = MM2ArchiveSystem.ReadFileText($"race/{spec.MapName}/{prefix}{idx}waypoints.csv");
            }
            return ParseWaypointsCsv(csv);
        }

        private static List<MM2RaceWaypoint> ParseWaypointsCsv(string csv)
        {
            var list = new List<MM2RaceWaypoint>();
            if (string.IsNullOrEmpty(csv)) return list;

            string[] lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split(',');
                if (cols.Length < 4) continue;

                if (!float.TryParse(cols[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) ||
                    !float.TryParse(cols[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ay) ||
                    !float.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float az) ||
                    !float.TryParse(cols[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ang))
                {
                    continue;
                }

                float radius = 12f;
                if (cols.Length >= 5 &&
                    float.TryParse(cols[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedR) &&
                    parsedR > 1f)
                {
                    radius = Mathf.Clamp(parsedR, 5f, 36f);
                }

                // In AGE space: forward = (sin(ang), 0, -cos(ang)).
                // In Unity left-handed space (-x, y, z): forward = (-sin(ang), 0, -cos(ang)) = Quaternion.Euler(0, ang + 180, 0).
                Vector3 posUnity = new Vector3(-ax, ay, az);
                Quaternion rotUnity = Quaternion.Euler(0f, ang + 180f, 0f);

                list.Add(new MM2RaceWaypoint
                {
                    Position = posUnity,
                    Rotation = rotUnity,
                    AngleDegrees = ang,
                    Radius = radius
                });
            }

            // If any intermediate waypoint had a=0 placeholder (e.g. sf/circuit10), orient it along the track tangent
            for (int i = 0; i < list.Count; i++)
            {
                MM2RaceWaypoint wp = list[i];
                if (Mathf.Abs(wp.AngleDegrees) < 0.001f && list.Count > 2)
                {
                    int nextI = (i + 1 < list.Count) ? (i + 1) : 1;
                    Vector3 dir = list[nextI].Position - wp.Position;
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 1f)
                    {
                        wp.Rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                        list[i] = wp;
                    }
                }
            }

            return list;
        }

        private static readonly Dictionary<string, Sprite> RaceMapSpriteCache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        public static Sprite LoadRaceMapPreviewSprite(string locationName, string raceType, int raceIndex)
        {
            CitySpec spec = GetCitySpec(locationName);
            if (spec == null) return null;

            string modeTag = "roam";
            if (string.Equals(raceType, "Blitz", StringComparison.OrdinalIgnoreCase))
            {
                modeTag = $"mapblitz{Mathf.Max(0, raceIndex)}";
            }
            else if (string.Equals(raceType, "Checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                modeTag = $"maprace{Mathf.Max(0, raceIndex)}";
            }
            else if (string.Equals(raceType, "Circuit", StringComparison.OrdinalIgnoreCase))
            {
                modeTag = $"mapcircuit{Mathf.Max(0, raceIndex)}";
            }

            string cacheKey = $"{spec.MapName}|{modeTag}";
            if (RaceMapSpriteCache.TryGetValue(cacheKey, out Sprite cached) && cached != null)
            {
                return cached;
            }

            string[] candidates = string.Equals(modeTag, "roam", StringComparison.OrdinalIgnoreCase)
                ? new[]
                {
                    $"jpg/{spec.MapName}_maproam.jpg",
                    $"jpg/{spec.MapName}_roam.jpg",
                    $"jpg/{spec.RaceDir}_maproam.jpg",
                    $"jpg/mapr_{spec.MapName}.jpg",
                    spec.MapName == "london" ? "jpg/mapr_lon.jpg" : null
                }
                : new[]
                {
                    $"jpg/{spec.MapName}_{modeTag}.jpg",
                    $"jpg/{spec.RaceDir}_{modeTag}.jpg",
                    $"jpg/{spec.MapName}_maproam.jpg",
                    $"jpg/{spec.MapName}_roam.jpg",
                    $"jpg/mapr_{spec.MapName}.jpg",
                    spec.MapName == "london" ? "jpg/mapr_lon.jpg" : null
                };

            foreach (string cand in candidates)
            {
                if (string.IsNullOrEmpty(cand)) continue;
                byte[] bytes = MM2ArchiveSystem.ReadFileBytes(cand);
                if (bytes != null && bytes.Length > 64)
                {
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(bytes))
                    {
                        tex.name = $"Map_{spec.MapName}_{modeTag}";
                        tex.wrapMode = TextureWrapMode.Clamp;
                        // Standard MM2 242x184 map previews include a 9px left / 4px top-bottom menu bezel
                        Rect rect = (tex.width == 242 && tex.height == 184)
                            ? new Rect(9f, 4f, 228f, 176f)
                            : new Rect(0f, 0f, tex.width, tex.height);
                        Sprite sp = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f));
                        RaceMapSpriteCache[cacheKey] = sp;
                        return sp;
                    }
                    UnityEngine.Object.Destroy(tex);
                }
            }

            return spec.CustomMapPreviewSprite ?? GetOrCreatePlaceholderMapSprite(spec.MapName);
        }

        private static readonly Dictionary<string, List<MM2CruiseSpawnLocation>> CruiseSpawnsByCity =
            new Dictionary<string, List<MM2CruiseSpawnLocation>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Sprite> CruiseSpawnSpriteCache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Discovers Cruise mode spawn locations from city/spawn_&lt;city&gt;_*.txt files in mounted .ar archives
        /// (such as !SpawnLocationSelector.ar). Always begins with index 0 = "Random".
        /// </summary>
        public static IReadOnlyList<MM2CruiseSpawnLocation> GetCruiseSpawnLocations(string locationName)
        {
            CitySpec spec = GetCitySpec(locationName);
            string mapName = (spec != null && !string.IsNullOrEmpty(spec.MapName))
                ? spec.MapName.ToLowerInvariant()
                : "sf";

            if (CruiseSpawnsByCity.TryGetValue(mapName, out List<MM2CruiseSpawnLocation> cached) && cached != null)
            {
                return cached;
            }

            var discovered = new List<MM2CruiseSpawnLocation>();

            string prefixPrimary = $"spawn_{mapName}";
            string prefixAlt = (spec != null && !string.IsNullOrEmpty(spec.RaceDir))
                ? $"spawn_{spec.RaceDir.ToLowerInvariant()}"
                : prefixPrimary;

            List<string> cityTxtFiles = MM2ArchiveSystem.GetFilesWithPrefixAndExtension("city", ".txt");
            for (int i = 0; i < cityTxtFiles.Count; i++)
            {
                string vPath = cityTxtFiles[i];
                string fn = System.IO.Path.GetFileName(vPath);
                if (string.IsNullOrEmpty(fn)) continue;

                if (fn.StartsWith(prefixPrimary, StringComparison.OrdinalIgnoreCase) ||
                    fn.StartsWith(prefixAlt, StringComparison.OrdinalIgnoreCase))
                {
                    MM2CruiseSpawnLocation parsed = ParseCruiseSpawnFile(vPath);
                    if (parsed != null)
                    {
                        discovered.Add(parsed);
                    }
                }
            }

            // Only expose the Cruise Starting Point selector when the city actually has spawn_*.txt files;
            // otherwise return an empty list so the UI and Cruise spawner fall back to classic behavior.
            var list = new List<MM2CruiseSpawnLocation>();
            if (discovered.Count > 0)
            {
                list.Add(new MM2CruiseSpawnLocation
                {
                    DisplayName = "Random",
                    FileName = string.Empty,
                    VirtualPath = string.Empty,
                    PhotoStem = string.Empty,
                    PositionUnity = Vector3.zero,
                    RotationUnity = Quaternion.identity,
                    AngleDegrees = 0f,
                    IsRandom = true
                });
                list.AddRange(discovered);
            }

            CruiseSpawnsByCity[mapName] = list;
            return list;
        }

        public static string[] GetCruiseSpawnDisplayNames(string locationName)
        {
            IReadOnlyList<MM2CruiseSpawnLocation> locs = GetCruiseSpawnLocations(locationName);
            string[] names = new string[locs.Count];
            for (int i = 0; i < locs.Count; i++)
            {
                names[i] = locs[i].DisplayName;
            }
            return names;
        }

        private static MM2CruiseSpawnLocation ParseCruiseSpawnFile(string virtualPath)
        {
            string content = MM2ArchiveSystem.ReadFileText(virtualPath);
            if (string.IsNullOrEmpty(content)) return null;

            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;

            string displayName = lines[0].Trim();
            if (string.IsNullOrEmpty(displayName)) return null;

            int coordsIdx = content.IndexOf("Coordinates:", StringComparison.OrdinalIgnoreCase);
            if (coordsIdx < 0) return null;

            string coordsPart = content.Substring(coordsIdx + "Coordinates:".Length);
            int nl = coordsPart.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) coordsPart = coordsPart.Substring(0, nl);

            string[] parts = coordsPart.Replace(" ", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return null;

            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ay) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float az) ||
                !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float angle))
            {
                return null;
            }

            string fileName = System.IO.Path.GetFileName(virtualPath);
            string photoStem = System.IO.Path.GetFileNameWithoutExtension(virtualPath);

            // In MM2 CarSim.ResetRotation = math.rad(angle), RotateY(angle) rotates AGE forward (0,0,-1)
            // to (-sin(angle), 0, -cos(angle)) in AGE -> (+sin(angle), 0, -cos(angle)) in Unity = Euler(0, 180 - angle, 0).
            return new MM2CruiseSpawnLocation
            {
                DisplayName = displayName,
                FileName = fileName,
                VirtualPath = virtualPath,
                PhotoStem = photoStem,
                PositionUnity = new Vector3(-ax, ay + 1.5f, az),
                RotationUnity = Quaternion.Euler(0f, 180f - angle, 0f),
                AngleDegrees = angle,
                IsRandom = false
            };
        }

        public static Sprite LoadCruiseSpawnPreviewSprite(string photoStem)
        {
            if (string.IsNullOrEmpty(photoStem)) return null;

            if (CruiseSpawnSpriteCache.TryGetValue(photoStem, out Sprite cached) && cached != null)
            {
                return cached;
            }

            string[] candidates =
            {
                $"jpg/{photoStem}.jpg",
                $"bmp/{photoStem}.bmp"
            };

            foreach (string cand in candidates)
            {
                byte[] bytes = MM2ArchiveSystem.ReadFileBytes(cand);
                if (bytes != null && bytes.Length > 64)
                {
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(bytes))
                    {
                        tex.name = $"SpawnPreview_{photoStem}";
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.filterMode = FilterMode.Bilinear;
                        Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                        CruiseSpawnSpriteCache[photoStem] = sp;
                        return sp;
                    }
                    UnityEngine.Object.Destroy(tex);
                }
            }

            Texture2D uiTex = MM2RuntimeDecoders.LoadUiTexture(photoStem);
            if (uiTex != null)
            {
                Sprite sp = Sprite.Create(uiTex, new Rect(0f, 0f, uiTex.width, uiTex.height), new Vector2(0.5f, 0.5f));
                CruiseSpawnSpriteCache[photoStem] = sp;
                return sp;
            }

            return null;
        }

        private static Sprite TryLoadCityMapSprite(string mapName)
        {
            string[] candidates =
            {
                $"jpg/{mapName}_maproam.jpg",
                $"jpg/{mapName}_roam.jpg",
                $"jpg/mapr_{mapName}.jpg",
                string.Equals(mapName, "london", StringComparison.OrdinalIgnoreCase) ? "jpg/mapr_lon.jpg" : null,
                $"jpg/{mapName}_mapblitz0.jpg",
                $"jpg/{mapName}_maprace0.jpg"
            };

            foreach (string cand in candidates)
            {
                if (string.IsNullOrEmpty(cand)) continue;
                byte[] bytes = MM2ArchiveSystem.ReadFileBytes(cand);
                if (bytes != null && bytes.Length > 64)
                {
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(bytes))
                    {
                        tex.name = $"Map_{mapName}";
                        tex.wrapMode = TextureWrapMode.Clamp;
                        Rect rect = (tex.width == 242 && tex.height == 184)
                            ? new Rect(9f, 4f, 228f, 176f)
                            : new Rect(0f, 0f, tex.width, tex.height);
                        return Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f));
                    }
                    UnityEngine.Object.Destroy(tex);
                }
            }
            return GetOrCreatePlaceholderMapSprite(mapName);
        }

        private static readonly Dictionary<string, Sprite> PlaceholderMapSpriteCache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private static Sprite GetOrCreatePlaceholderMapSprite(string mapName)
        {
            string key = string.IsNullOrEmpty(mapName) ? "unknown" : mapName.ToLowerInvariant();
            if (PlaceholderMapSpriteCache.TryGetValue(key, out Sprite cached) && cached != null)
            {
                return cached;
            }

            bool isMm1 = !MM2ArchiveSystem.FileExists($"city/{key}.psdl") &&
                (MM2ArchiveSystem.FileExists($"city/{key}.ptl") ||
                 MM2ArchiveSystem.HasBmsDirectory($"{key}city") ||
                 string.Equals(key, "chicago", StringComparison.OrdinalIgnoreCase));

            string titleText = isMm1 ? "MM1 CITY" : "NO PREVIEW";

            const int width = 456;
            const int height = 352;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = $"MapPlaceholder_{key}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color32[] pixels = new Color32[width * height];
            Color32 bgTop = new Color32(28, 38, 54, 255);
            Color32 bgBottom = new Color32(16, 22, 32, 255);
            Color32 gridLine = new Color32(36, 48, 68, 255);
            Color32 borderOuter = new Color32(58, 76, 102, 255);
            Color32 borderInner = new Color32(90, 114, 148, 255);

            // Background vertical gradient + subtle map-style grid (y=0 is bottom in Unity Texture2D)
            for (int y = 0; y < height; y++)
            {
                float t = (float)y / (height - 1);
                byte r = (byte)Mathf.RoundToInt(Mathf.Lerp(bgBottom.r, bgTop.r, t));
                byte g = (byte)Mathf.RoundToInt(Mathf.Lerp(bgBottom.g, bgTop.g, t));
                byte b = (byte)Mathf.RoundToInt(Mathf.Lerp(bgBottom.b, bgTop.b, t));
                Color32 rowColor = new Color32(r, g, b, 255);

                bool isGridY = (y % 32) == 0;
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    bool isGridX = (x % 32) == 0;
                    pixels[rowOffset + x] = (isGridX || isGridY) ? gridLine : rowColor;
                }
            }

            // Outer & inner bevel border
            DrawRectOutline(pixels, width, height, 4, 4, width - 8, height - 8, 2, borderOuter);
            DrawRectOutline(pixels, width, height, 10, 10, width - 20, height - 20, 1, borderInner);

            Color32 iconColor = new Color32(168, 188, 214, 255);
            Color32 iconDim = new Color32(118, 138, 166, 255);
            Color32 shadowColor = new Color32(8, 12, 18, 220);
            Color32 textColor = new Color32(228, 238, 250, 255);
            Color32 slashColor = new Color32(220, 96, 88, 255);

            // Title text ("MM1 CITY") in upper region (centered horizontally, y around 254..289)
            int titleScale = 5;
            DrawCenteredText5x7(pixels, width, height, titleText, (width / 2) + 2, 252, titleScale, shadowColor);
            DrawCenteredText5x7(pixels, width, height, titleText, width / 2, 254, titleScale, textColor);

            // "No Image" icon centered below the title text (y around 76..196)
            int iconW = 148;
            int iconH = 112;
            int iconX = (width - iconW) / 2;
            int iconY = 82; // bottom of the icon frame

            // Drop shadow for picture frame
            DrawRectOutline(pixels, width, height, iconX + 3, iconY - 3, iconW, iconH, 5, shadowColor);

            // Picture frame outline
            DrawRectOutline(pixels, width, height, iconX, iconY, iconW, iconH, 5, iconColor);

            // Mountain silhouette inside the picture frame (clipped to interior)
            int innerLeft = iconX + 10;
            int innerRight = iconX + iconW - 10;
            int innerBottom = iconY + 10;
            int innerTop = iconY + iconH - 10;

            int peak1X = iconX + 48;
            int peak1Y = iconY + 68;
            int peak2X = iconX + 98;
            int peak2Y = iconY + 54;

            for (int x = innerLeft; x <= innerRight; x++)
            {
                int h1 = peak1Y - Mathf.Abs(x - peak1X) * 9 / 10;
                int h2 = peak2Y - Mathf.Abs(x - peak2X) * 8 / 10;
                int mountainTop = Mathf.Max(h1, h2);
                if (mountainTop > innerBottom)
                {
                    int yMax = Mathf.Min(innerTop, mountainTop);
                    for (int y = innerBottom; y <= yMax; y++)
                    {
                        pixels[y * width + x] = (h1 >= h2) ? iconColor : iconDim;
                    }
                }
            }

            // Sun circle in upper-right of the frame
            int sunCenterX = iconX + iconW - 36;
            int sunCenterY = iconY + iconH - 30;
            DrawFilledCircle(pixels, width, height, sunCenterX, sunCenterY, 11, iconColor);

            // Diagonal "No Image" slash across the frame (with dark outline for contrast)
            int slashX0 = iconX - 10;
            int slashY0 = iconY + iconH + 8;
            int slashX1 = iconX + iconW + 10;
            int slashY1 = iconY - 8;
            DrawThickLine(pixels, width, height, slashX0, slashY0, slashX1, slashY1, 6f, bgBottom);
            DrawThickLine(pixels, width, height, slashX0, slashY0, slashX1, slashY1, 3.2f, slashColor);

            // Subtle subtitle below the icon
            int subScale = 2;
            DrawCenteredText5x7(pixels, width, height, "NO MAP IMAGE", (width / 2) + 1, 43, subScale, shadowColor);
            DrawCenteredText5x7(pixels, width, height, "NO MAP IMAGE", width / 2, 44, subScale, iconDim);

            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
            PlaceholderMapSpriteCache[key] = sp;
            return sp;
        }

        private static void DrawRectOutline(Color32[] pixels, int width, int height, int rx, int ry, int rw, int rh, int thickness, Color32 color)
        {
            for (int t = 0; t < thickness; t++)
            {
                int x0 = Mathf.Clamp(rx + t, 0, width - 1);
                int x1 = Mathf.Clamp(rx + rw - 1 - t, 0, width - 1);
                int y0 = Mathf.Clamp(ry + t, 0, height - 1);
                int y1 = Mathf.Clamp(ry + rh - 1 - t, 0, height - 1);

                for (int x = x0; x <= x1; x++)
                {
                    pixels[y0 * width + x] = color;
                    pixels[y1 * width + x] = color;
                }
                for (int y = y0; y <= y1; y++)
                {
                    pixels[y * width + x0] = color;
                    pixels[y * width + x1] = color;
                }
            }
        }

        private static void DrawFilledCircle(Color32[] pixels, int width, int height, int cx, int cy, int radius, Color32 color)
        {
            int r2 = radius * radius;
            int yMin = Mathf.Max(0, cy - radius);
            int yMax = Mathf.Min(height - 1, cy + radius);
            int xMin = Mathf.Max(0, cx - radius);
            int xMax = Mathf.Min(width - 1, cx + radius);

            for (int y = yMin; y <= yMax; y++)
            {
                int dy = y - cy;
                for (int x = xMin; x <= xMax; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dy * dy <= r2)
                    {
                        pixels[y * width + x] = color;
                    }
                }
            }
        }

        private static void DrawThickLine(Color32[] pixels, int width, int height, int x0, int y0, int x1, int y1, float radius, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.Min(x0, x1) - Mathf.CeilToInt(radius) - 1);
            int maxX = Mathf.Min(width - 1, Mathf.Max(x0, x1) + Mathf.CeilToInt(radius) + 1);
            int minY = Mathf.Max(0, Mathf.Min(y0, y1) - Mathf.CeilToInt(radius) - 1);
            int maxY = Mathf.Min(height - 1, Mathf.Max(y0, y1) + Mathf.CeilToInt(radius) + 1);

            float dx = x1 - x0;
            float dy = y1 - y0;
            float lenSq = dx * dx + dy * dy;
            float rSq = radius * radius;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float t = lenSq > 0.0001f ? Mathf.Clamp01(((x - x0) * dx + (y - y0) * dy) / lenSq) : 0f;
                    float projX = x0 + t * dx;
                    float projY = y0 + t * dy;
                    float distSq = (x - projX) * (x - projX) + (y - projY) * (y - projY);
                    if (distSq <= rSq)
                    {
                        pixels[y * width + x] = color;
                    }
                }
            }
        }

        private static void DrawCenteredText5x7(Color32[] pixels, int width, int height, string text, int centerX, int bottomY, int scale, Color32 color)
        {
            if (string.IsNullOrEmpty(text)) return;
            int charAdvance = 6 * scale;
            int totalWidth = text.Length * charAdvance - scale;
            int startX = centerX - totalWidth / 2;

            for (int i = 0; i < text.Length; i++)
            {
                string[] rows = GetGlyph5x7(char.ToUpperInvariant(text[i]));
                if (rows == null) continue;

                int charX = startX + i * charAdvance;
                for (int r = 0; r < 7 && r < rows.Length; r++)
                {
                    string row = rows[r];
                    int glyphRowY = bottomY + (6 - r) * scale;
                    for (int c = 0; c < 5 && c < row.Length; c++)
                    {
                        if (row[c] != '#') continue;
                        int px0 = charX + c * scale;
                        for (int sy = 0; sy < scale; sy++)
                        {
                            int py = glyphRowY + sy;
                            if (py < 0 || py >= height) continue;
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int px = px0 + sx;
                                if (px >= 0 && px < width)
                                {
                                    pixels[py * width + px] = color;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static string[] GetGlyph5x7(char ch)
        {
            switch (ch)
            {
                case 'A': return new[] { " ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" };
                case 'C': return new[] { " ####", "#    ", "#    ", "#    ", "#    ", "#    ", " ####" };
                case 'E': return new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####" };
                case 'G': return new[] { " ####", "#    ", "#    ", "# ###", "#   #", "#   #", " ####" };
                case 'I': return new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "#####" };
                case 'M': return new[] { "#   #", "## ##", "# # #", "# # #", "#   #", "#   #", "#   #" };
                case 'N': return new[] { "#   #", "##  #", "# # #", "#  ##", "#   #", "#   #", "#   #" };
                case 'O': return new[] { " ### ", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " };
                case 'P': return new[] { "#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    " };
                case 'R': return new[] { "#### ", "#   #", "#   #", "#### ", "# #  ", "#  # ", "#   #" };
                case 'T': return new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  " };
                case 'V': return new[] { "#   #", "#   #", "#   #", "#   #", "#   #", " # # ", "  #  " };
                case 'W': return new[] { "#   #", "#   #", "#   #", "# # #", "# # #", "## ##", "#   #" };
                case 'Y': return new[] { "#   #", "#   #", " # # ", "  #  ", "  #  ", "  #  ", "  #  " };
                case '1': return new[] { "  #  ", " ##  ", "  #  ", "  #  ", "  #  ", "  #  ", "#####" };
                case ' ': return new[] { "     ", "     ", "     ", "     ", "     ", "     ", "     " };
                default: return null;
            }
        }

        internal static Dictionary<string, string> ParseKeyValueText(string text)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return dict;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("//")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                dict[key] = val;
            }
            return dict;
        }

        internal static string[] SplitPipeList(string raw)
        {
            if (string.IsNullOrEmpty(raw) || string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }
            string[] parts = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        private static GameObject FindSceneRootObject(string name)
        {
            GameObject found = GameObject.Find(name);
            if (found != null) return found;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root != null && string.Equals(root.name, name, StringComparison.Ordinal))
                    {
                        return root;
                    }
                }
            }
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureRuntimeCityOnDirectSceneLoad()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.IsValid() || !string.Equals(scene.name, "GoDrive", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            GameObject playerCar = FindSceneRootObject("PlayerVehicle");
            if (playerCar != null && playerCar.activeInHierarchy)
            {
                EnsureCityLoadedInScene("San Francisco", out _, out _);
            }
        }

        /// <summary>
        /// Ensures the selected city's 3D geometry is loaded in the active scene and returns the player spawn pose.
        /// </summary>
        public static void EnsureCityLoadedInScene(
            string locationName,
            out Vector3 spawnPosition,
            out Quaternion spawnRotation
        )
        {
            CitySpec spec = GetCitySpec(locationName);
            GameObject prebakedSF = FindSceneRootObject("SanFrancisco_City");
            GameObject runtimeCityRoot = FindSceneRootObject("MM2_Runtime_City");

            if (prebakedSF != null)
            {
                prebakedSF.SetActive(false);
            }

            bool needsRebuild = runtimeCityRoot == null ||
                                !string.Equals(currentlyLoadedMapName, spec.MapName, StringComparison.OrdinalIgnoreCase);
            if (!needsRebuild && runtimeCityRoot != null)
            {
                MeshCollider sampleCol = runtimeCityRoot.GetComponentInChildren<MeshCollider>();
                if (sampleCol == null || sampleCol.sharedMesh == null)
                {
                    needsRebuild = true;
                }
            }

            if (needsRebuild)
            {
                if (runtimeCityRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(runtimeCityRoot);
                }

                MM2BangerSystem.ClearCaches();
                MM2EnvironmentManager.ClearRegisteredMaterials();
                BuildRuntimeCityFromArchives(spec);
                currentlyLoadedMapName = spec.MapName;
                Physics.SyncTransforms();
            }

            // Always apply the active session's Time of Day and Weather to the city
            MM2EnvironmentManager.EnsureAndApply(
                spec.MapName,
                MM2.UI.MM2GameSession.TimeOfDay,
                MM2.UI.MM2GameSession.Weather
            );

            GetCitySpawnPose(spec, out spawnPosition, out spawnRotation);
        }

        public static void GetCitySpawnPose(CitySpec spec, out Vector3 spawnPos, out Quaternion spawnRot)
        {
            if (spec.MapName == "sf")
            {
                spawnPos = new Vector3(1152.0f, 65.25f, -402.12f);
                spawnRot = Quaternion.Euler(0f, 90f, 0f);
                return;
            }

            string[] waypointFiles =
            {
                $"race/{spec.RaceDir}/race0waypoints.csv",
                $"race/{spec.RaceDir}/blitz0waypoints.csv",
                $"race/{spec.RaceDir}/circuit0waypoints.csv",
                $"race/{spec.MapName}/race0waypoints.csv",
                $"race/{spec.MapName}/blitz0waypoints.csv"
            };

            foreach (string wpPath in waypointFiles)
            {
                string csv = MM2ArchiveSystem.ReadFileText(wpPath);
                if (string.IsNullOrEmpty(csv)) continue;

                var pts = new List<(Vector3 posUnity, float angle)>();
                string[] lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] cols = lines[i].Split(',');
                    if (cols.Length >= 4 &&
                        float.TryParse(cols[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) &&
                        float.TryParse(cols[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ay) &&
                        float.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float az) &&
                        float.TryParse(cols[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ang))
                    {
                        pts.Add((new Vector3(-ax, ay + 0.6f, az), ang));
                        if (pts.Count >= 2) break;
                    }
                }

                if (pts.Count >= 1)
                {
                    spawnPos = pts[0].posUnity;
                    if (pts.Count >= 2)
                    {
                        Vector3 dir = pts[1].posUnity - pts[0].posUnity;
                        dir.y = 0f;
                        spawnRot = dir.sqrMagnitude > 0.01f
                            ? Quaternion.LookRotation(dir.normalized, Vector3.up)
                            : Quaternion.Euler(0f, -pts[0].angle, 0f);
                    }
                    else
                    {
                        spawnRot = Quaternion.Euler(0f, -pts[0].angle, 0f);
                    }

                    // Snap vertically to road collider if present
                    if (Physics.Raycast(spawnPos + Vector3.up * 15f, Vector3.down, out RaycastHit hit, 50f))
                    {
                        spawnPos.y = hit.point.y + 0.55f;
                    }
                    return;
                }
            }

            spawnPos = new Vector3(0f, 5f, 0f);
            spawnRot = Quaternion.identity;
        }

        public static void GetCruiseSpawnPose(
            CitySpec spec,
            string chosenSpotFileName,
            out Vector3 spawnPos,
            out Quaternion spawnRot
        )
        {
            if (spec == null)
            {
                spawnPos = new Vector3(0f, 5f, 0f);
                spawnRot = Quaternion.identity;
                return;
            }

            // If no city/spawn_<city>_*.txt files exist for this city (e.g. base .ar archives only or a mod city
            // without spawn files), fall back to the original default city spawn pose.
            IReadOnlyList<MM2CruiseSpawnLocation> citySpawns = GetCruiseSpawnLocations(spec.DisplayName ?? spec.MapName);
            if (citySpawns == null || citySpawns.Count == 0)
            {
                GetCitySpawnPose(spec, out spawnPos, out spawnRot);
                return;
            }

            // 1. Specific Cruise starting point selected from city/spawn_<city>_*.txt
            if (!string.IsNullOrEmpty(chosenSpotFileName))
            {
                string vPath = chosenSpotFileName.StartsWith("city/", StringComparison.OrdinalIgnoreCase)
                    ? chosenSpotFileName
                    : $"city/{chosenSpotFileName}";
                MM2CruiseSpawnLocation loc = ParseCruiseSpawnFile(vPath);
                if (loc != null)
                {
                    spawnPos = loc.PositionUnity;
                    spawnRot = loc.RotationUnity;

                    // Snap vertically to road collider directly below (starting from y+1.5 so tunnels/overpasses are safe)
                    if (Physics.Raycast(spawnPos, Vector3.down, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore) &&
                        hit.normal.y >= 0.35f)
                    {
                        spawnPos.y = hit.point.y + 0.55f;
                    }
                    return;
                }
            }

            // 2. "Random" selected: pick a valid non-freeway, non-alleyway intersection from city/<city>.bai
            MM2.AI.MM2AiMapData aiMap = MM2.AI.MM2AiMapData.Load(spec.MapName, spec.RaceDir, null);
            if (aiMap != null && aiMap.TryGetRandomSpawnIntersectionPose(out spawnPos, out spawnRot))
            {
                if (Physics.Raycast(spawnPos + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 12f, ~0, QueryTriggerInteraction.Ignore) &&
                    hit.normal.y >= 0.35f)
                {
                    spawnPos.y = hit.point.y + 0.55f;
                }
                return;
            }

            // 3. Fallback to default city spawn pose if the city has no .bai AI map
            GetCitySpawnPose(spec, out spawnPos, out spawnRot);
        }

        // =====================================================================
        // RUNTIME PSDL + INST + SKY_DOME BUILDER
        // =====================================================================
        private class RuntimeSubmeshBucket
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> UVs = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();
        }

        private class RuntimeDistrictChunk
        {
            public readonly Dictionary<string, RuntimeSubmeshBucket> VisualByTexture =
                new Dictionary<string, RuntimeSubmeshBucket>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Vector3> ColPositions = new List<Vector3>();
            public readonly List<int> ColTriangles = new List<int>();
        }

        private struct PsdlElement
        {
            public int Type;
            public int Subtype;
            public int TexId;
            public Vector3[] Verts;
            public int RowBreadth;
            public int RowCount;
            public bool IsStartCap;
            public bool IsEndCap;
            public float Height;
            public float Scale;
            public float Bottom;
            public float Top;
            public float UTiling;
            public float VTiling;
            public Vector3 Left;
            public Vector3 Right;
            public int DivFlags;
            public int DivType;
            public int DivTex;
            public ushort Value;
            public bool IsJunction;
            public ushort TunnelFlags;
            public ushort CeilOrig;
            public uint WallBits;
            public uint Inv1;
            public uint Inv2;
        }

        private static void BuildRuntimeCityFromArchives(CitySpec spec)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] psdlBytes = MM2ArchiveSystem.ReadFileBytes($"city/{spec.MapName}.psdl");
            if (psdlBytes == null || psdlBytes.Length < 24)
            {
                if (MM2ArchiveSystem.HasBmsDirectory($"{spec.MapName}city") ||
                    MM2ArchiveSystem.FileExists($"city/{spec.MapName}.fcd"))
                {
                    BuildRuntimeMm1CityFromArchives(spec);
                    return;
                }

                Debug.LogError($"[MM2RuntimeCityLoader] Missing or invalid city/{spec.MapName}.psdl!");
                return;
            }

            GameObject cityRoot = new GameObject("MM2_Runtime_City");
            cityRoot.transform.position = Vector3.zero;
            cityRoot.transform.rotation = Quaternion.identity;

            CityWaterTriangles.Clear();
            hasRecordedWaterLevel = false;
            hasExplicitWaterFileLevel = false;
            CityWaterLevel = string.Equals(spec.MapName, "london", StringComparison.OrdinalIgnoreCase) ? -4.0f : -2.0f;
            string waterFileText = MM2ArchiveSystem.ReadFileText($"city/{spec.MapName}.water");
            if (!string.IsNullOrEmpty(waterFileText))
            {
                string[] waterTokens = waterFileText.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (waterTokens.Length > 0 &&
                    float.TryParse(waterTokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedWaterY))
                {
                    CityWaterLevel = parsedWaterY;
                    hasExplicitWaterFileLevel = true;
                }
            }

            // Parse PSDL header, vertices, floats, textures, and rooms
            int nVerts = (int)BitConverter.ToUInt32(psdlBytes, 8);
            var verts = new Vector3[nVerts];
            int off = 12;
            for (int i = 0; i < nVerts; i++)
            {
                verts[i] = new Vector3(
                    BitConverter.ToSingle(psdlBytes, off),
                    BitConverter.ToSingle(psdlBytes, off + 4),
                    BitConverter.ToSingle(psdlBytes, off + 8)
                );
                off += 12;
            }

            int nFloats = (int)BitConverter.ToUInt32(psdlBytes, off);
            off += 4;
            var floats = new float[nFloats];
            for (int i = 0; i < nFloats; i++)
            {
                floats[i] = BitConverter.ToSingle(psdlBytes, off);
                off += 4;
            }

            int nTex = (int)BitConverter.ToUInt32(psdlBytes, off) - 1;
            off += 4;
            var textures = new string[Math.Max(0, nTex)];
            for (int i = 0; i < nTex; i++)
            {
                int tLen = psdlBytes[off++];
                if (tLen > 0)
                {
                    string rawT = Encoding.ASCII.GetString(psdlBytes, off, tLen - 1);
                    off += tLen;
                    textures[i] = CleanTexName(rawT);
                }
                else
                {
                    textures[i] = string.Empty;
                }
            }

            string GetTex(int idx) => (idx >= 0 && idx < textures.Length) ? textures[idx] : string.Empty;

            int nRooms = (int)BitConverter.ToUInt32(psdlBytes, off) - 1;
            off += 8;

            const int GridN = 4;
            var chunks = new RuntimeDistrictChunk[GridN, GridN];
            for (int gx = 0; gx < GridN; gx++)
            {
                for (int gz = 0; gz < GridN; gz++)
                {
                    chunks[gx, gz] = new RuntimeDistrictChunk();
                }
            }

            float minX = -2000f, maxX = 2000f, minZ = -2000f, maxZ = 2000f;
            float minVertY = 0f;
            if (verts.Length > 0)
            {
                minX = float.MaxValue; maxX = float.MinValue;
                minZ = float.MaxValue; maxZ = float.MinValue;
                minVertY = float.MaxValue;
                for (int i = 0; i < verts.Length; i++)
                {
                    float ux = -verts[i].x;
                    float uy = verts[i].y;
                    float uz = verts[i].z;
                    if (ux < minX) minX = ux;
                    if (ux > maxX) maxX = ux;
                    if (uz < minZ) minZ = uz;
                    if (uz > maxZ) maxZ = uz;
                    if (uy > -500f && uy < minVertY) minVertY = uy;
                }
                if (minVertY == float.MaxValue) minVertY = 0f;
                minX -= 1f; maxX += 1f;
                minZ -= 1f; maxZ += 1f;
            }

            RuntimeDistrictChunk GetChunk(float ux, float uz)
            {
                int gx = Mathf.Clamp((int)((ux - minX) / Math.Max(1f, maxX - minX) * GridN), 0, GridN - 1);
                int gz = Mathf.Clamp((int)((uz - minZ) / Math.Max(1f, maxZ - minZ) * GridN), 0, GridN - 1);
                return chunks[gx, gz];
            }

            var roomRoadElements = new List<PsdlElement>[Math.Max(0, nRooms)];
            var crosswalkElements = new List<PsdlElement>(768);

            // Expand PSDL rooms
            for (int rIdx = 0; rIdx < nRooms && off + 8 <= psdlBytes.Length; rIdx++)
            {
                uint nPerim = BitConverter.ToUInt32(psdlBytes, off);
                uint attrWords = BitConverter.ToUInt32(psdlBytes, off + 4);
                off += 8;

                float sumX = 0f, sumZ = 0f;
                var perim = new List<Vector3>((int)nPerim);
                for (uint p = 0; p < nPerim && off + 4 <= psdlBytes.Length; p++)
                {
                    ushort vIdx = BitConverter.ToUInt16(psdlBytes, off);
                    off += 4;
                    if (vIdx < verts.Length)
                    {
                        perim.Add(verts[vIdx]);
                        sumX += -verts[vIdx].x;
                        sumZ += verts[vIdx].z;
                    }
                }

                float roomCx = perim.Count > 0 ? sumX / perim.Count : 0f;
                float roomCz = perim.Count > 0 ? sumZ / perim.Count : 0f;
                RuntimeDistrictChunk roomChunk = GetChunk(roomCx, roomCz);

                int attrEnd = Math.Min(psdlBytes.Length, off + (int)attrWords * 2);
                int curTexId = 0;
                var roomElements = new List<PsdlElement>();

                while (off + 2 <= attrEnd)
                {
                    ushort att = BitConverter.ToUInt16(psdlBytes, off);
                    off += 2;
                    int etype = (att >> 3) & 0x0F;
                    int subtype = att & 0x07;

                    if (etype == 10)
                    {
                        ushort val = BitConverter.ToUInt16(psdlBytes, off);
                        off += 2;
                        curTexId = (val + (256 * subtype) - 1) & 0xFFFF;
                        continue;
                    }

                    var el = new PsdlElement { Type = etype, Subtype = subtype, TexId = curTexId };

                    if (etype == 0) // Road
                    {
                        int nSec = subtype > 0 ? subtype : BitConverter.ToUInt16(psdlBytes, off);
                        if (subtype == 0) off += 2;
                        var ev = new Vector3[nSec * 4];
                        for (int i = 0; i < ev.Length && off + 2 <= psdlBytes.Length; i++)
                        {
                            ev[i] = verts[BitConverter.ToUInt16(psdlBytes, off)];
                            off += 2;
                        }
                        el.Verts = ev;
                        el.RowBreadth = 4;
                        el.RowCount = nSec;
                    }
                    else if (etype == 1) // SidewalkStrip
                    {
                        int nSec = subtype > 0 ? subtype : BitConverter.ToUInt16(psdlBytes, off);
                        if (subtype == 0) off += 2;
                        var vrefs = new ushort[nSec * 2];
                        for (int i = 0; i < vrefs.Length && off + 2 <= psdlBytes.Length; i++)
                        {
                            vrefs[i] = BitConverter.ToUInt16(psdlBytes, off);
                            off += 2;
                        }
                        if (vrefs.Length >= 2)
                        {
                            bool isStart = (vrefs[0] == 0 && vrefs[1] == 0);
                            bool isEnd = (vrefs[0] == 1 && vrefs[1] == 1);
                            int startI = (isStart || isEnd) ? 2 : 0;
                            var ev = new Vector3[Math.Max(0, vrefs.Length - startI)];
                            for (int i = 0; i < ev.Length; i++) ev[i] = verts[vrefs[startI + i]];
                            el.Verts = ev;
                            el.IsStartCap = isStart;
                            el.IsEndCap = isEnd;
                            el.RowBreadth = 2;
                            el.RowCount = ev.Length / 2;
                        }
                    }
                    else if (etype == 2) // Walkway
                    {
                        int nSec = subtype > 0 ? subtype : BitConverter.ToUInt16(psdlBytes, off);
                        if (subtype == 0) off += 2;
                        var ev = new Vector3[nSec * 2];
                        for (int i = 0; i < ev.Length && off + 2 <= psdlBytes.Length; i++)
                        {
                            ev[i] = verts[BitConverter.ToUInt16(psdlBytes, off)];
                            off += 2;
                        }
                        el.Verts = ev;
                        el.RowBreadth = 2;
                        el.RowCount = nSec;
                    }
                    else if (etype == 3) // Sliver
                    {
                        ushort topI = BitConverter.ToUInt16(psdlBytes, off);
                        ushort scaleI = BitConverter.ToUInt16(psdlBytes, off + 2);
                        ushort leftI = BitConverter.ToUInt16(psdlBytes, off + 4);
                        ushort rightI = BitConverter.ToUInt16(psdlBytes, off + 6);
                        off += 8;
                        el.Height = floats[topI];
                        el.Scale = floats[scaleI];
                        el.Left = verts[leftI];
                        el.Right = verts[rightI];
                    }
                    else if (etype == 4) // Crosswalk
                    {
                        var ev = new Vector3[4];
                        for (int i = 0; i < 4; i++)
                        {
                            ev[i] = verts[BitConverter.ToUInt16(psdlBytes, off)];
                            off += 2;
                        }
                        el.Verts = ev;
                    }
                    else if (etype == 5 || etype == 6) // TriangleFan / CulledTriangleFan
                    {
                        int nTris = subtype > 0 ? subtype : BitConverter.ToUInt16(psdlBytes, off);
                        if (subtype == 0) off += 2;
                        var ev = new Vector3[nTris + 2];
                        for (int i = 0; i < ev.Length && off + 2 <= psdlBytes.Length; i++)
                        {
                            ev[i] = verts[BitConverter.ToUInt16(psdlBytes, off)];
                            off += 2;
                        }
                        el.Verts = ev;
                    }
                    else if (etype == 7) // FacadeBound
                    {
                        ushort topI = BitConverter.ToUInt16(psdlBytes, off + 2);
                        ushort leftI = BitConverter.ToUInt16(psdlBytes, off + 4);
                        ushort rightI = BitConverter.ToUInt16(psdlBytes, off + 6);
                        off += 8;
                        el.Height = floats[topI];
                        el.Left = verts[leftI];
                        el.Right = verts[rightI];
                    }
                    else if (etype == 8) // DividedRoad
                    {
                        int nSec = subtype > 0 ? subtype : BitConverter.ToUInt16(psdlBytes, off);
                        if (subtype == 0) off += 2;
                        byte flagType = psdlBytes[off];
                        byte divTex = psdlBytes[off + 1];
                        ushort val = BitConverter.ToUInt16(psdlBytes, off + 2);
                        off += 4;
                        var ev = new Vector3[nSec * 6];
                        for (int i = 0; i < ev.Length && off + 2 <= psdlBytes.Length; i++)
                        {
                            ev[i] = verts[BitConverter.ToUInt16(psdlBytes, off)];
                            off += 2;
                        }
                        el.Verts = ev;
                        el.RowBreadth = 6;
                        el.RowCount = nSec;
                        el.DivFlags = flagType >> 2;
                        el.DivType = flagType & 0x03;
                        el.DivTex = divTex;
                        el.Value = val;
                    }
                    else if (etype == 9) // Tunnel
                    {
                        int st = subtype > 0 ? subtype : BitConverter.ToUInt16(psdlBytes, off);
                        if (subtype == 0) off += 2;
                        bool isJunc = (st == 0x9 || st == 0xA);
                        bool hasUnk = (st == 0x3 || st == 0xA);
                        ushort tFlags = BitConverter.ToUInt16(psdlBytes, off);
                        ushort rawH = BitConverter.ToUInt16(psdlBytes, off + 2);
                        off += 4;
                        if (hasUnk) off += 2;
                        el.IsJunction = isJunc;
                        el.TunnelFlags = tFlags;
                        el.Height = rawH / 256f;
                        if (isJunc && off + 14 <= psdlBytes.Length)
                        {
                            el.CeilOrig = BitConverter.ToUInt16(psdlBytes, off);
                            el.WallBits = BitConverter.ToUInt32(psdlBytes, off + 2);
                            el.Inv1 = BitConverter.ToUInt32(psdlBytes, off + 6);
                            el.Inv2 = BitConverter.ToUInt32(psdlBytes, off + 10);
                            off += 14;
                        }
                    }
                    else if (etype == 11) // Facade
                    {
                        ushort botI = BitConverter.ToUInt16(psdlBytes, off);
                        ushort topI = BitConverter.ToUInt16(psdlBytes, off + 2);
                        ushort uRep = BitConverter.ToUInt16(psdlBytes, off + 4);
                        ushort vRep = BitConverter.ToUInt16(psdlBytes, off + 6);
                        ushort leftI = BitConverter.ToUInt16(psdlBytes, off + 8);
                        ushort rightI = BitConverter.ToUInt16(psdlBytes, off + 10);
                        off += 12;
                        el.Bottom = floats[botI];
                        el.Top = floats[topI];
                        el.UTiling = uRep;
                        el.VTiling = vRep;
                        el.Left = verts[leftI];
                        el.Right = verts[rightI];
                    }
                    else if (etype == 12) // RoofTriangleFan
                    {
                        int nV = subtype > 0 ? subtype : BitConverter.ToUInt16(psdlBytes, off);
                        if (subtype == 0) off += 2;
                        ushort hRef = BitConverter.ToUInt16(psdlBytes, off);
                        off += 2;
                        var ev = new Vector3[nV + 1];
                        for (int i = 0; i < ev.Length && off + 2 <= psdlBytes.Length; i++)
                        {
                            ev[i] = verts[BitConverter.ToUInt16(psdlBytes, off)];
                            off += 2;
                        }
                        el.Verts = ev;
                        el.Height = floats[hRef];
                    }

                    roomElements.Add(el);
                }

                for (int eIdx = 0; eIdx < roomElements.Count; eIdx++)
                {
                    PsdlElement el = roomElements[eIdx];
                    if (el.Type == 7)
                    {
                        BuildPsdlFacadeBound(roomChunk, el.Left, el.Right, el.Height);
                        continue;
                    }
                    if (el.TexId == 65535 || string.IsNullOrEmpty(GetTex(el.TexId)))
                    {
                        continue;
                    }

                    switch (el.Type)
                    {
                        case 0:
                            BuildPsdlRoad(roomChunk, GetTex, el.TexId, el.Verts);
                            break;
                        case 1:
                            BuildPsdlSidewalk(roomChunk, GetTex, el.TexId, el.Verts, el.IsStartCap, el.IsEndCap);
                            break;
                        case 2:
                            BuildPsdlWalkway(roomChunk, GetTex, el.TexId, el.Verts);
                            break;
                        case 3:
                            BuildPsdlSliver(roomChunk, GetTex, el.TexId, el.Left, el.Right, el.Height, el.Scale);
                            break;
                        case 4:
                            BuildPsdlCrosswalk(roomChunk, GetTex, el.TexId, el.Verts);
                            break;
                        case 5:
                        case 6:
                            BuildPsdlTriangleFan(roomChunk, GetTex, el.TexId, el.Verts, false, 0f);
                            break;
                        case 8:
                            BuildPsdlDividedRoad(roomChunk, GetTex, el.TexId, el.Verts, el.DivFlags, el.DivType, el.DivTex, el.Value);
                            break;
                        case 9:
                            BuildPsdlTunnel(roomChunk, GetTex, el, perim, roomElements, eIdx);
                            break;
                        case 11:
                            BuildPsdlFacade(roomChunk, GetTex, el.TexId, el.Left, el.Right, el.Bottom, el.Top, el.UTiling, el.VTiling);
                            break;
                        case 12:
                            BuildPsdlTriangleFan(roomChunk, GetTex, el.TexId, el.Verts, true, el.Height);
                            break;
                    }

                    if ((el.Type == 0 || el.Type == 8) && el.Verts != null && el.Verts.Length >= el.RowBreadth * 2)
                    {
                        if (roomRoadElements[rIdx] == null) roomRoadElements[rIdx] = new List<PsdlElement>();
                        roomRoadElements[rIdx].Add(el);
                    }
                    else if (el.Type == 4 && el.Verts != null && el.Verts.Length >= 4)
                    {
                        crosswalkElements.Add(el);
                    }
                }

                off = attrEnd;
            }

            // Read trailing PSDL roomFlags (nRooms + 1 bytes) and propRules (nRooms + 1 bytes)
            var roomPropRules = new byte[Math.Max(0, nRooms)];
            int propRuleStart = off + (nRooms + 1);
            for (int rIdx = 0; rIdx < nRooms; rIdx++)
            {
                int pOff = propRuleStart + rIdx + 1;
                if (pOff >= 0 && pOff < psdlBytes.Length)
                {
                    roomPropRules[rIdx] = psdlBytes[pOff];
                }
            }

            GameObject bangersRoot = new GameObject($"{spec.MapName}_Bangers");
            bangersRoot.transform.SetParent(cityRoot.transform, false);

            // Expand .inst files and props.pathset (landmarks, bridges, buildings, props, and child xrefs)
            ExpandInstFile($"city/{spec.MapName}.inst", GetChunk, bangersRoot.transform);
            ExpandInstFile($"city/{spec.MapName}_ai.inst", GetChunk, bangersRoot.transform);
            ExpandPropsPathset($"city/{spec.MapName}/props.pathset", GetChunk, bangersRoot.transform);

            // Spawn procedural sidewalk Bangers (streetlamps, parking meters, mailboxes, trash cans, phone booths, trees, traffic lights)
            SpawnProceduralSidewalkProps(spec.MapName, roomRoadElements, crosswalkElements, roomPropRules, bangersRoot.transform);

            // Instantiate Unity MeshRenderers and MeshColliders per district chunk
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var cityMatCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

            Material GetCityMaterial(string tName) => GetOrCreateCityMaterial(tName, urpLit, cityMatCache);

            // Expand movable drawbridges (e.g. race/london/london_bridge.pathset: Tower Bridge, Waterloo Bridge, Lambeth Bridge, Tower of London Moat)
            GameObject bridgesRoot = new GameObject($"{spec.MapName}_Drawbridges");
            bridgesRoot.transform.SetParent(cityRoot.transform, false);
            ExpandBridgePathset($"race/{spec.RaceDir}/{spec.MapName}_bridge.pathset", bridgesRoot.transform, GetCityMaterial, false);

            for (int gx = 0; gx < GridN; gx++)
            {
                for (int gz = 0; gz < GridN; gz++)
                {
                    RuntimeDistrictChunk ch = chunks[gx, gz];
                    if (ch.VisualByTexture.Count == 0 && ch.ColTriangles.Count == 0) continue;

                    GameObject chunkGo = new GameObject($"{spec.MapName}_District_{gx}_{gz}");
                    chunkGo.transform.SetParent(cityRoot.transform, false);

                    if (ch.VisualByTexture.Count > 0)
                    {
                        var allPos = new List<Vector3>();
                        var allNorm = new List<Vector3>();
                        var allUV = new List<Vector2>();
                        var subTris = new List<int[]>();
                        var mats = new List<Material>();

                        foreach (var kvp in ch.VisualByTexture)
                        {
                            RuntimeSubmeshBucket b = kvp.Value;
                            if (b.Triangles.Count == 0) continue;

                            int baseV = allPos.Count;
                            allPos.AddRange(b.Positions);
                            allNorm.AddRange(b.Normals);
                            allUV.AddRange(b.UVs);

                            int[] indices = new int[b.Triangles.Count];
                            for (int i = 0; i < indices.Length; i++) indices[i] = baseV + b.Triangles[i];
                            subTris.Add(indices);
                            mats.Add(GetCityMaterial(kvp.Key));
                        }

                        Mesh visMesh = new Mesh
                        {
                            name = chunkGo.name,
                            indexFormat = IndexFormat.UInt32
                        };
                        visMesh.SetVertices(allPos);
                        visMesh.SetNormals(allNorm);
                        visMesh.SetUVs(0, allUV);
                        visMesh.subMeshCount = subTris.Count;
                        for (int s = 0; s < subTris.Count; s++)
                        {
                            visMesh.SetTriangles(subTris[s], s, false);
                        }
                        visMesh.RecalculateBounds();

                        chunkGo.AddComponent<MeshFilter>().sharedMesh = visMesh;
                        chunkGo.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
                    }

                    if (ch.ColPositions.Count > 0 && ch.ColTriangles.Count > 0)
                    {
                        Mesh colMesh = new Mesh
                        {
                            name = $"{chunkGo.name}_Col",
                            indexFormat = IndexFormat.UInt32
                        };
                        colMesh.SetVertices(ch.ColPositions);
                        colMesh.SetTriangles(ch.ColTriangles, 0, true);
                        var mc = chunkGo.AddComponent<MeshCollider>();
                        mc.sharedMesh = colMesh;
                    }
                }
            }

            // Build SkyDome and Horizon Water Plane
            BuildRuntimeSkyDome(cityRoot.transform, spec.MapName);
            BuildRuntimeHorizonWater(cityRoot.transform, CityWaterLevel, minX, maxX, minZ, maxZ, minVertY);

            sw.Stop();
            Debug.Log($"[MM2RuntimeCityLoader] Built runtime city '{spec.DisplayName}' ({spec.MapName}) from .ar archives in {sw.ElapsedMilliseconds} ms.");
        }

        private static void BuildRuntimeMm1CityFromArchives(CitySpec spec)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            GameObject cityRoot = new GameObject("MM2_Runtime_City");
            cityRoot.transform.position = Vector3.zero;
            cityRoot.transform.rotation = Quaternion.identity;

            CityWaterTriangles.Clear();
            hasRecordedWaterLevel = false;
            hasExplicitWaterFileLevel = false;
            CityWaterLevel = -1.5f;

            string waterFileText = MM2ArchiveSystem.ReadFileText($"city/{spec.MapName}.water");
            if (!string.IsNullOrEmpty(waterFileText))
            {
                string[] waterTokens = waterFileText.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (waterTokens.Length > 0 &&
                    float.TryParse(waterTokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedWaterY))
                {
                    CityWaterLevel = parsedWaterY;
                    hasExplicitWaterFileLevel = true;
                }
            }

            var cityBmsPaths = new List<string>(1536);
            void CollectMm1CullMeshes(IReadOnlyList<string> dirFiles)
            {
                if (dirFiles == null) return;
                for (int i = 0; i < dirFiles.Count; i++)
                {
                    string p = dirFiles[i];
                    if (p.EndsWith("_h.bms", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith("_h2.bms", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith("_a.bms", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith("_a2.bms", StringComparison.OrdinalIgnoreCase))
                    {
                        cityBmsPaths.Add(p);
                    }
                }
            }

            CollectMm1CullMeshes(MM2ArchiveSystem.GetBmsFilesInDirectory($"{spec.MapName}city"));
            CollectMm1CullMeshes(MM2ArchiveSystem.GetBmsFilesInDirectory($"{spec.MapName}lm"));

            const int GridN = 4;
            var chunks = new RuntimeDistrictChunk[GridN, GridN];
            for (int gx = 0; gx < GridN; gx++)
            {
                for (int gz = 0; gz < GridN; gz++)
                {
                    chunks[gx, gz] = new RuntimeDistrictChunk();
                }
            }

            float minX = -1600f, maxX = 1600f, minZ = -1600f, maxZ = 1600f;
            float minVertY = 0f;

            var parsedCells = new List<(Vector3 center, List<MM2RuntimePkgLoader.PkgShaderEntry> mats, List<MM2RuntimePkgLoader.PkgSection> secs)>(cityBmsPaths.Count);
            if (cityBmsPaths.Count > 0)
            {
                minX = float.MaxValue; maxX = float.MinValue;
                minZ = float.MaxValue; maxZ = float.MinValue;
                minVertY = float.MaxValue;

                for (int f = 0; f < cityBmsPaths.Count; f++)
                {
                    byte[] bytes = MM2ArchiveSystem.ReadFileBytes(cityBmsPaths[f]);
                    if (!MM2RuntimePkgLoader.TryParseBmsMesh(
                            bytes,
                            out Vector3 hdrOff,
                            out List<MM2RuntimePkgLoader.PkgShaderEntry> mats,
                            out List<MM2RuntimePkgLoader.PkgSection> secs))
                    {
                        continue;
                    }

                    float sumX = 0f, sumZ = 0f;
                    int vTotal = 0;
                    for (int s = 0; s < secs.Count; s++)
                    {
                        var posList = secs[s].Positions;
                        for (int i = 0; i < posList.Count; i++)
                        {
                            Vector3 v = posList[i] + hdrOff;
                            posList[i] = v;
                            float ux = -v.x;
                            float uy = v.y;
                            float uz = v.z;
                            if (ux < minX) minX = ux;
                            if (ux > maxX) maxX = ux;
                            if (uz < minZ) minZ = uz;
                            if (uz > maxZ) maxZ = uz;
                            if (uy > -500f && uy < minVertY) minVertY = uy;
                            sumX += ux;
                            sumZ += uz;
                            vTotal++;
                        }
                    }

                    if (vTotal > 0)
                    {
                        parsedCells.Add((new Vector3(sumX / vTotal, 0f, sumZ / vTotal), mats, secs));
                    }
                }

                if (minX >= maxX) { minX = -1600f; maxX = 1600f; }
                if (minZ >= maxZ) { minZ = -1600f; maxZ = 1600f; }
                if (minVertY == float.MaxValue) minVertY = 0f;
                minX -= 1f; maxX += 1f;
                minZ -= 1f; maxZ += 1f;
            }

            RuntimeDistrictChunk GetChunk(float ux, float uz)
            {
                int gx = Mathf.Clamp((int)((ux - minX) / Math.Max(1f, maxX - minX) * GridN), 0, GridN - 1);
                int gz = Mathf.Clamp((int)((uz - minZ) / Math.Max(1f, maxZ - minZ) * GridN), 0, GridN - 1);
                return chunks[gx, gz];
            }

            // 1. Append all cell and landmark .bms meshes to district chunks
            for (int c = 0; c < parsedCells.Count; c++)
            {
                var cell = parsedCells[c];
                RuntimeDistrictChunk chunk = GetChunk(cell.center.x, cell.center.z);
                AppendBmsSectionsToChunk(
                    chunk,
                    cell.mats,
                    cell.secs,
                    addCollider: true,
                    xa: Vector3.right,
                    ya: Vector3.up,
                    za: Vector3.forward,
                    orig: Vector3.zero);
            }

            // 2. Expand city/<mapName>.fcd (3D building facades, roofs, side walls, tunnels, freeway walls)
            ExpandMm1FcdFile($"city/{spec.MapName}.fcd", GetChunk);

            // 3. Expand city/<mapName>.bng (breakable props, traffic lights, streetlamps, trees, storefront windows)
            GameObject bangersRoot = new GameObject($"{spec.MapName}_Bangers");
            bangersRoot.transform.SetParent(cityRoot.transform, false);
            ExpandMm1BngFile($"city/{spec.MapName}.bng", GetChunk, bangersRoot.transform);

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var cityMatCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            Material GetCityMaterial(string tName) => GetOrCreateCityMaterial(tName, urpLit, cityMatCache);

            // 4. Expand movable drawbridges from city/<mapName>.gizmo
            GameObject bridgesRoot = new GameObject($"{spec.MapName}_Drawbridges");
            bridgesRoot.transform.SetParent(cityRoot.transform, false);
            ExpandMm1GizmoDrawbridges($"city/{spec.MapName}.gizmo", bridgesRoot.transform, GetCityMaterial);

            // 5. Instantiate Unity MeshRenderers and MeshColliders per district chunk
            for (int gx = 0; gx < GridN; gx++)
            {
                for (int gz = 0; gz < GridN; gz++)
                {
                    RuntimeDistrictChunk ch = chunks[gx, gz];
                    if (ch.VisualByTexture.Count == 0 && ch.ColTriangles.Count == 0) continue;

                    GameObject chunkGo = new GameObject($"{spec.MapName}_District_{gx}_{gz}");
                    chunkGo.transform.SetParent(cityRoot.transform, false);

                    if (ch.VisualByTexture.Count > 0)
                    {
                        var allPos = new List<Vector3>();
                        var allNorm = new List<Vector3>();
                        var allUV = new List<Vector2>();
                        var subTris = new List<int[]>();
                        var mats = new List<Material>();

                        foreach (var kvp in ch.VisualByTexture)
                        {
                            RuntimeSubmeshBucket b = kvp.Value;
                            if (b.Triangles.Count == 0) continue;

                            int baseV = allPos.Count;
                            allPos.AddRange(b.Positions);
                            allNorm.AddRange(b.Normals);
                            allUV.AddRange(b.UVs);

                            int[] indices = new int[b.Triangles.Count];
                            for (int i = 0; i < indices.Length; i++) indices[i] = baseV + b.Triangles[i];
                            subTris.Add(indices);
                            mats.Add(GetCityMaterial(kvp.Key));
                        }

                        Mesh visMesh = new Mesh
                        {
                            name = chunkGo.name,
                            indexFormat = IndexFormat.UInt32
                        };
                        visMesh.SetVertices(allPos);
                        visMesh.SetNormals(allNorm);
                        visMesh.SetUVs(0, allUV);
                        visMesh.subMeshCount = subTris.Count;
                        for (int s = 0; s < subTris.Count; s++)
                        {
                            visMesh.SetTriangles(subTris[s], s, false);
                        }
                        visMesh.RecalculateBounds();

                        chunkGo.AddComponent<MeshFilter>().sharedMesh = visMesh;
                        chunkGo.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
                    }

                    if (ch.ColPositions.Count > 0 && ch.ColTriangles.Count > 0)
                    {
                        Mesh colMesh = new Mesh
                        {
                            name = $"{chunkGo.name}_Col",
                            indexFormat = IndexFormat.UInt32
                        };
                        colMesh.SetVertices(ch.ColPositions);
                        colMesh.SetTriangles(ch.ColTriangles, 0, true);
                        var mc = chunkGo.AddComponent<MeshCollider>();
                        mc.sharedMesh = colMesh;
                    }
                }
            }

            // 6. Build SkyDome and Horizon Water Plane
            BuildRuntimeSkyDome(cityRoot.transform, spec.MapName);
            BuildRuntimeHorizonWater(cityRoot.transform, CityWaterLevel, minX, maxX, minZ, maxZ, minVertY);

            sw.Stop();
            Debug.Log($"[MM2RuntimeCityLoader] Built runtime MM1 city '{spec.DisplayName}' ({spec.MapName}) from .ar archives in {sw.ElapsedMilliseconds} ms.");
        }

        private static void AppendBmsSectionsToChunk(
            RuntimeDistrictChunk chunk,
            List<MM2RuntimePkgLoader.PkgShaderEntry> shaders,
            List<MM2RuntimePkgLoader.PkgSection> sections,
            bool addCollider,
            Vector3 xa,
            Vector3 ya,
            Vector3 za,
            Vector3 orig
        )
        {
            if (chunk == null || sections == null) return;

            for (int s = 0; s < sections.Count; s++)
            {
                var sec = sections[s];
                int shIdx = sec.ShaderOffset;
                string tName = (shaders != null && shIdx >= 0 && shIdx < shaders.Count)
                    ? CleanTexName(shaders[shIdx].TextureName)
                    : string.Empty;
                if (string.IsNullOrEmpty(tName))
                {
                    if (shaders != null && shIdx >= 0 && shIdx < shaders.Count)
                    {
                        Color32 dc = shaders[shIdx].DiffuseColor;
                        tName = $"__color_{dc.r:x2}{dc.g:x2}{dc.b:x2}";
                    }
                    else
                    {
                        continue;
                    }
                }

                bool isFoliageTex = tName.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tName.IndexOf("palm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tName.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isWaterTex = IsWaterSurfaceTexture(tName);
                bool addSecCollider = addCollider && !isWaterTex;

                if (!chunk.VisualByTexture.TryGetValue(tName, out RuntimeSubmeshBucket bucket))
                {
                    bucket = new RuntimeSubmeshBucket();
                    chunk.VisualByTexture[tName] = bucket;
                }

                int baseV = bucket.Positions.Count;
                int baseColV = chunk.ColPositions.Count;
                int vCount = sec.Positions.Count;

                for (int i = 0; i < vCount; i++)
                {
                    Vector3 pt = sec.Positions[i];
                    Vector3 wx = new Vector3(
                        orig.x + pt.x * xa.x + pt.y * ya.x + pt.z * za.x,
                        orig.y + pt.x * xa.y + pt.y * ya.y + pt.z * za.y,
                        orig.z + pt.x * xa.z + pt.y * ya.z + pt.z * za.z
                    );
                    Vector3 uPos = new Vector3(-wx.x, wx.y, wx.z);

                    Vector3 d = sec.Normals[i];
                    Vector3 wn = new Vector3(
                        d.x * xa.x + d.y * ya.x + d.z * za.x,
                        d.x * xa.y + d.y * ya.y + d.z * za.y,
                        d.x * xa.z + d.y * ya.z + d.z * za.z
                    );
                    Vector3 uNorm = new Vector3(-wn.x, wn.y, wn.z).normalized;
                    if (isFoliageTex)
                    {
                        uNorm = new Vector3(uNorm.x * 0.15f, 0.98f, uNorm.z * 0.15f).normalized;
                    }

                    bucket.Positions.Add(uPos);
                    bucket.Normals.Add(uNorm);
                    bucket.UVs.Add(sec.UVs[i]);

                    if (addSecCollider) chunk.ColPositions.Add(uPos);
                }

                int baseTriStart = bucket.Triangles.Count;
                for (int i = 0; i + 2 < sec.Triangles.Count; i += 3)
                {
                    int i0 = sec.Triangles[i + 2];
                    int i1 = sec.Triangles[i + 1];
                    int i2 = sec.Triangles[i];
                    bucket.Triangles.Add(baseV + i0);
                    bucket.Triangles.Add(baseV + i1);
                    bucket.Triangles.Add(baseV + i2);

                    if (addSecCollider)
                    {
                        chunk.ColTriangles.Add(baseColV + i0);
                        chunk.ColTriangles.Add(baseColV + i1);
                        chunk.ColTriangles.Add(baseColV + i2);
                    }
                }

                if (isWaterTex)
                {
                    var secWaterTris = new List<int>(sec.Triangles.Count);
                    for (int i = baseTriStart; i < bucket.Triangles.Count; i++)
                    {
                        secWaterTris.Add(bucket.Triangles[i]);
                    }
                    RecordWaterTriangles(bucket.Positions, secWaterTris);
                }
            }
        }

        private static void ExpandMm1FcdFile(
            string virtualPath,
            Func<float, float, RuntimeDistrictChunk> getChunk
        )
        {
            byte[] data = MM2ArchiveSystem.ReadFileBytes(virtualPath);
            if (data == null || data.Length < 48) return;

            uint count = BitConverter.ToUInt32(data, 0);
            int pos = 4;

            for (uint i = 0; i < count && pos + 45 <= data.Length; i++)
            {
                ushort room = BitConverter.ToUInt16(data, pos);
                ushort flags = BitConverter.ToUInt16(data, pos + 2);
                float x1 = BitConverter.ToSingle(data, pos + 4);
                float y1 = BitConverter.ToSingle(data, pos + 8);
                float z1 = BitConverter.ToSingle(data, pos + 12);
                float x2 = BitConverter.ToSingle(data, pos + 16);
                float y2 = BitConverter.ToSingle(data, pos + 20);
                float z2 = BitConverter.ToSingle(data, pos + 24);
                float f0 = BitConverter.ToSingle(data, pos + 28);
                float f1 = BitConverter.ToSingle(data, pos + 32);
                float f2 = BitConverter.ToSingle(data, pos + 36);
                float nw = BitConverter.ToSingle(data, pos + 40);
                pos += 44;

                int end = Array.IndexOf(data, (byte)0, pos);
                if (end < 0) break;
                string name = Encoding.ASCII.GetString(data, pos, end - pos).Trim().ToLowerInvariant();
                pos = end + 1;

                if (room == 0 || string.IsNullOrEmpty(name)) continue;

                MM2RuntimePkgLoader.ParsedPkg tmpl = MM2RuntimePkgLoader.LoadPkg(name);
                if (tmpl == null) continue;

                Vector3 orig = new Vector3(x1, y1, z1);
                Vector3 p2 = new Vector3(x2, y2, z2);
                Vector3 xa;
                Vector3 ya = Vector3.up;
                Vector3 za;

                if ((flags & 0x04) != 0)
                {
                    xa = p2 - orig;
                    za = new Vector3(f0 - orig.x, f1 - orig.y, f2 - orig.z);
                }
                else
                {
                    Vector3 delta = p2 - orig;
                    float flatDist = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
                    if (flatDist < 0.01f) continue;

                    Vector3 flatDir = new Vector3(delta.x / flatDist, 0f, delta.z / flatDist);
                    float invNw = nw > 0.05f ? (1f / nw) : (1f / flatDist);
                    xa = delta * invNw;
                    za = new Vector3(-flatDir.z, 0f, flatDir.x);
                }

                RuntimeDistrictChunk chunk = getChunk(-orig.x, orig.z);

                if ((flags & 0x01) != 0)
                {
                    if (tmpl.GeometryChunks.TryGetValue("FACADE_H", out var fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("BLDG_H", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("H", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("FACADE_M", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("BLDG_M", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("M", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("FACADE_L", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("BLDG_L", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("L", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("FACADE_VL", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("BLDG_VL", out fSecs) ||
                        tmpl.GeometryChunks.TryGetValue("VL", out fSecs))
                    {
                        AppendBmsSectionsToChunk(chunk, tmpl.Shaders, fSecs, true, xa, ya, za, orig);
                    }

                    if (tmpl.GeometryChunks.TryGetValue("GRND_H", out var gSecs) ||
                        tmpl.GeometryChunks.TryGetValue("GRND_M", out gSecs) ||
                        tmpl.GeometryChunks.TryGetValue("GRND_L", out gSecs))
                    {
                        AppendBmsSectionsToChunk(chunk, tmpl.Shaders, gSecs, true, xa, ya, za, orig);
                    }
                }
                if ((flags & 0x08) != 0 && tmpl.GeometryChunks.TryGetValue("LEFT", out var lSecs))
                {
                    AppendBmsSectionsToChunk(chunk, tmpl.Shaders, lSecs, true, xa, ya, za, orig);
                }
                if ((flags & 0x10) != 0 && tmpl.GeometryChunks.TryGetValue("RIGHT", out var rSecs))
                {
                    AppendBmsSectionsToChunk(chunk, tmpl.Shaders, rSecs, true, xa, ya, za, orig);
                }
                if ((flags & 0x20) != 0 &&
                    (tmpl.GeometryChunks.TryGetValue("TOP_H", out var tSecs) ||
                     tmpl.GeometryChunks.TryGetValue("TOP_M", out tSecs) ||
                     tmpl.GeometryChunks.TryGetValue("TOP_L", out tSecs) ||
                     tmpl.GeometryChunks.TryGetValue("TOP_VL", out tSecs)))
                {
                    AppendBmsSectionsToChunk(chunk, tmpl.Shaders, tSecs, true, xa, ya, za, orig);
                }
                if (((flags & 0x40) != 0 || (flags & 0x400) != 0) &&
                    tmpl.GeometryChunks.TryGetValue("BACK", out var bSecs))
                {
                    AppendBmsSectionsToChunk(chunk, tmpl.Shaders, bSecs, true, xa, ya, za, orig);
                }
                if ((flags & 0x200) != 0 &&
                    (tmpl.GeometryChunks.TryGetValue("UPPER_H", out var uSecs) ||
                     tmpl.GeometryChunks.TryGetValue("UPPER_M", out uSecs) ||
                     tmpl.GeometryChunks.TryGetValue("UPPER_L", out uSecs)))
                {
                    AppendBmsSectionsToChunk(chunk, tmpl.Shaders, uSecs, true, xa, ya, za, orig);
                }
            }
        }

        private static void ExpandMm1BngFile(
            string virtualPath,
            Func<float, float, RuntimeDistrictChunk> getChunk,
            Transform bangersRoot
        )
        {
            byte[] data = MM2ArchiveSystem.ReadFileBytes(virtualPath);
            if (data == null || data.Length < 32) return;

            uint count = BitConverter.ToUInt32(data, 0);
            int pos = 4;

            for (uint i = 0; i < count && pos + 29 <= data.Length; i++)
            {
                ushort room = BitConverter.ToUInt16(data, pos);
                ushort flags = BitConverter.ToUInt16(data, pos + 2);
                float x1 = BitConverter.ToSingle(data, pos + 4);
                float y1 = BitConverter.ToSingle(data, pos + 8);
                float z1 = BitConverter.ToSingle(data, pos + 12);
                float x2 = BitConverter.ToSingle(data, pos + 16);
                float y2 = BitConverter.ToSingle(data, pos + 20);
                float z2 = BitConverter.ToSingle(data, pos + 24);
                pos += 28;

                int end = Array.IndexOf(data, (byte)0, pos);
                if (end < 0) break;
                string name = Encoding.ASCII.GetString(data, pos, end - pos).Trim().ToLowerInvariant();
                pos = end + 1;

                if (room == 0 || string.IsNullOrEmpty(name)) continue;

                Vector3 orig = new Vector3(x1, y1, z1);
                Vector3 dp = new Vector3(x2 - x1, 0f, z2 - z1);
                Vector3 xa = dp.sqrMagnitude > 0.0001f ? dp.normalized : Vector3.right;
                Vector3 ya = Vector3.up;
                Vector3 za = new Vector3(-xa.z, 0f, xa.x);

                ExpandSinglePkgWithXrefs(name, (int)i, true, xa, ya, za, orig, getChunk, bangersRoot);
            }
        }

        private struct Mm1BridgeMgrTune
        {
            public float BridgeDelta;
            public float BridgeOffGoal;
            public float BridgeOnGoal;
            public float GateDelta;
            public float GateOffGoal;
            public float GateOnGoal;
            public float BridgeOnDelay;
            public float GateOffDelay;
            public float BridgeOffDelay;
            public float GateOnDelay;
            public List<Mm1BridgeSetTune> Bridges;
        }

        private struct Mm1BridgeSetTune
        {
            public int InitialPos;
            public float DownInterval;
            public float UpInterval;
            public bool Triggered;
            public float TriggerTimeout;
            public Vector3 TriggerPosAge;
            public float TriggerDist2;
        }

        private static Mm1BridgeSetTune ParseMm1BridgeSetBlock(string blockText)
        {
            var tune = new Mm1BridgeSetTune
            {
                InitialPos = 0,
                DownInterval = 10f,
                UpInterval = 10f,
                Triggered = false,
                TriggerTimeout = 1000000f,
                TriggerPosAge = Vector3.zero,
                TriggerDist2 = 100f
            };
            if (string.IsNullOrEmpty(blockText)) return tune;

            string[] lines = blockText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] tok = lines[i].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 2) continue;
                string key = tok[0];
                if (string.Equals(key, "InitialPos", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(tok[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int initPos))
                {
                    tune.InitialPos = initPos;
                }
                else if (string.Equals(key, "DownInterval", StringComparison.OrdinalIgnoreCase) &&
                         float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float downInt))
                {
                    tune.DownInterval = Mathf.Max(0.1f, downInt);
                }
                else if (string.Equals(key, "UpInterval", StringComparison.OrdinalIgnoreCase) &&
                         float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float upInt))
                {
                    tune.UpInterval = Mathf.Max(0.1f, upInt);
                }
                else if (string.Equals(key, "Triggered", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(tok[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int trig))
                {
                    tune.Triggered = trig != 0;
                }
                else if (string.Equals(key, "TriggerTimeout", StringComparison.OrdinalIgnoreCase))
                {
                    if (tok[1].IndexOf("INF", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        tune.TriggerTimeout = float.PositiveInfinity;
                    }
                    else if (float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float tOut))
                    {
                        tune.TriggerTimeout = tOut;
                    }
                }
                else if (string.Equals(key, "TriggerPos", StringComparison.OrdinalIgnoreCase) && tok.Length >= 4 &&
                         float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float tx) &&
                         float.TryParse(tok[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float ty) &&
                         float.TryParse(tok[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float tz))
                {
                    tune.TriggerPosAge = new Vector3(tx, ty, tz);
                }
                else if (string.Equals(key, "TriggerDist2", StringComparison.OrdinalIgnoreCase) &&
                         float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float td2))
                {
                    tune.TriggerDist2 = Mathf.Max(0.01f, td2);
                }
            }
            return tune;
        }

        private static Mm1BridgeMgrTune LoadMm1BridgeMgrTune(string raceStem)
        {
            var mgr = new Mm1BridgeMgrTune
            {
                BridgeDelta = 0.2f,
                BridgeOffGoal = 0f,
                BridgeOnGoal = 0.471239f,
                GateDelta = 0.4f,
                GateOffGoal = -1.5708f,
                GateOnGoal = 0f,
                BridgeOnDelay = 1.5f,
                GateOffDelay = 5.3f,
                BridgeOffDelay = 0f,
                GateOnDelay = 0f,
                Bridges = new List<Mm1BridgeSetTune>(9)
            };

            string mgrPath;
            if (string.IsNullOrEmpty(raceStem) || string.Equals(raceStem, "roam", StringComparison.OrdinalIgnoreCase))
            {
                mgrPath = "tune/roam.mmbridgemgr";
            }
            else if (string.Equals(raceStem, "multi", StringComparison.OrdinalIgnoreCase))
            {
                mgrPath = "tune/multi.mmbridgemgr";
            }
            else
            {
                string candidate = $"tune/{raceStem.ToLowerInvariant()}.mmbridgemgr";
                mgrPath = MM2ArchiveSystem.FileExists(candidate) ? candidate : "tune/default.mmbridgemgr";
            }

            string text = MM2ArchiveSystem.ReadFileText(mgrPath);
            if (string.IsNullOrEmpty(text)) return mgr;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder currentSetBlock = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].Trim();
                if (string.IsNullOrEmpty(raw)) continue;

                if (raw.StartsWith("mmBridgeSet", StringComparison.OrdinalIgnoreCase))
                {
                    currentSetBlock = new StringBuilder();
                    continue;
                }

                if (currentSetBlock != null)
                {
                    if (raw == "}")
                    {
                        mgr.Bridges.Add(ParseMm1BridgeSetBlock(currentSetBlock.ToString()));
                        currentSetBlock = null;
                    }
                    else
                    {
                        currentSetBlock.AppendLine(raw);
                    }
                    continue;
                }

                string[] tok = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 2) continue;
                string key = tok[0];
                if (!float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float val)) continue;

                if (string.Equals(key, "BridgeDelta", StringComparison.OrdinalIgnoreCase)) mgr.BridgeDelta = Mathf.Max(0.01f, val);
                else if (string.Equals(key, "BridgeOffGoal", StringComparison.OrdinalIgnoreCase)) mgr.BridgeOffGoal = val;
                else if (string.Equals(key, "BridgeOnGoal", StringComparison.OrdinalIgnoreCase)) mgr.BridgeOnGoal = Mathf.Max(0.05f, val);
                else if (string.Equals(key, "GateDelta", StringComparison.OrdinalIgnoreCase)) mgr.GateDelta = Mathf.Max(0.01f, val);
                else if (string.Equals(key, "GateOffGoal", StringComparison.OrdinalIgnoreCase)) mgr.GateOffGoal = val;
                else if (string.Equals(key, "GateOnGoal", StringComparison.OrdinalIgnoreCase)) mgr.GateOnGoal = val;
                else if (string.Equals(key, "BridgeOnDelay", StringComparison.OrdinalIgnoreCase)) mgr.BridgeOnDelay = Mathf.Max(0f, val);
                else if (string.Equals(key, "GateOffDelay", StringComparison.OrdinalIgnoreCase)) mgr.GateOffDelay = Mathf.Max(0f, val);
                else if (string.Equals(key, "BridgeOffDelay", StringComparison.OrdinalIgnoreCase)) mgr.BridgeOffDelay = Mathf.Max(0f, val);
                else if (string.Equals(key, "GateOnDelay", StringComparison.OrdinalIgnoreCase)) mgr.GateOnDelay = Mathf.Max(0f, val);
            }

            return mgr;
        }

        private static void ExpandMm1GizmoDrawbridges(
            string virtualPath,
            Transform bridgesRoot,
            Func<string, Material> getMaterial,
            string raceStem = null
        )
        {
            string text = MM2ArchiveSystem.ReadFileText(virtualPath);
            if (string.IsNullOrEmpty(text) || bridgesRoot == null) return;

            Mm1BridgeMgrTune mgrTune = LoadMm1BridgeMgrTune(raceStem);

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var bridgeLeaves = new List<(string pkgName, Vector3 hingeAge)>(2);
            var bridgeGates = new List<(string pkgName, Vector3 p1Age, Vector3 p2Age)>(4);
            string currentBlockHeader = string.Empty;
            int bridgeIndex = 0;

            void FlushCurrentDrawbridge()
            {
                if (bridgeLeaves.Count < 2)
                {
                    bridgeLeaves.Clear();
                    bridgeGates.Clear();
                    return;
                }

                string pkgName = bridgeLeaves[0].pkgName;
                MM2RuntimePkgLoader.ParsedPkg tmpl = MM2RuntimePkgLoader.LoadPkg(pkgName);
                if (tmpl != null)
                {
                    Mesh leafMesh = BuildDrawbridgeLeafMesh(
                        pkgName,
                        tmpl,
                        getMaterial,
                        out Material[] leafMats,
                        out float leafLength,
                        out float totalHalfWidth,
                        out float roadHalfWidth,
                        out float roadSurfaceY,
                        out float sidewalkSurfaceY
                    );

                    if (leafMesh != null && leafMats != null && leafMats.Length > 0)
                    {
                        Vector3 hingeA = new Vector3(-bridgeLeaves[0].hingeAge.x, bridgeLeaves[0].hingeAge.y, bridgeLeaves[0].hingeAge.z);
                        Vector3 hingeB = new Vector3(-bridgeLeaves[1].hingeAge.x, bridgeLeaves[1].hingeAge.y, bridgeLeaves[1].hingeAge.z);
                        Vector3 center = (hingeA + hingeB) * 0.5f;

                        Vector3 dirA = new Vector3(center.x - hingeA.x, 0f, center.z - hingeA.z);
                        Vector3 dirB = new Vector3(center.x - hingeB.x, 0f, center.z - hingeB.z);
                        if (dirA.sqrMagnitude > 0.01f && dirB.sqrMagnitude > 0.01f)
                        {
                            string blockLabel = string.IsNullOrEmpty(currentBlockHeader) ? $"Drawbridge_{bridgeIndex}" : currentBlockHeader;
                            GameObject groupGo = new GameObject($"{blockLabel}_{pkgName}");
                            groupGo.transform.SetParent(bridgesRoot, false);

                            Quaternion baseRotA = Quaternion.LookRotation(dirA.normalized, Vector3.up);
                            Quaternion baseRotB = Quaternion.LookRotation(dirB.normalized, Vector3.up);

                            Rigidbody rbA = SpawnDrawbridgeLeaf(
                                groupGo.transform,
                                $"{pkgName}_LeafA",
                                leafMesh,
                                leafMats,
                                hingeA,
                                baseRotA,
                                leafLength,
                                totalHalfWidth,
                                roadHalfWidth,
                                roadSurfaceY,
                                sidewalkSurfaceY
                            );

                            Rigidbody rbB = SpawnDrawbridgeLeaf(
                                groupGo.transform,
                                $"{pkgName}_LeafB",
                                leafMesh,
                                leafMats,
                                hingeB,
                                baseRotB,
                                leafLength,
                                totalHalfWidth,
                                roadHalfWidth,
                                roadSurfaceY,
                                sidewalkSurfaceY
                            );

                            // Resolve per-bridge mmBridgeSet tune (either embedded in roam/race6/multi.mmbridgemgr or in tune/drawbridgeN.mmbridgeset)
                            Mm1BridgeSetTune setTune;
                            if (bridgeIndex < mgrTune.Bridges.Count)
                            {
                                setTune = mgrTune.Bridges[bridgeIndex];
                            }
                            else
                            {
                                string setPath = $"tune/{blockLabel.ToLowerInvariant()}.mmbridgeset";
                                string setText = MM2ArchiveSystem.ReadFileText(setPath);
                                setTune = ParseMm1BridgeSetBlock(setText);
                            }

                            // Convert BridgeOnGoal directly from radians to degrees (0.471239 rad -> 27.0 deg; 0.314159 rad in race6 -> 18.0 deg)
                            float maxPitchDeg = mgrTune.BridgeOnGoal * Mathf.Rad2Deg;
                            float moveDur = Mathf.Max(0.5f, mgrTune.BridgeOnGoal / Mathf.Max(0.02f, mgrTune.BridgeDelta));

                            Vector3 triggerPosUnity = setTune.TriggerPosAge.sqrMagnitude > 0.01f
                                ? new Vector3(-setTune.TriggerPosAge.x, setTune.TriggerPosAge.y, setTune.TriggerPosAge.z)
                                : center;

                            bool startsOpen = setTune.InitialPos != 0;

                            var schedule = new MM2DrawbridgeSchedule
                            {
                                Mode = setTune.Triggered ? MM2DrawbridgeMode.Proximity : MM2DrawbridgeMode.Timed,
                                StartsOpen = startsOpen,
                                InitialTimerSeconds = 0f,
                                DownIntervalSeconds = setTune.DownInterval,
                                BridgeOnDelaySeconds = mgrTune.BridgeOnDelay,
                                OpenDurationSeconds = moveDur,
                                UpIntervalSeconds = setTune.UpInterval,
                                BridgeOffDelaySeconds = mgrTune.BridgeOffDelay,
                                CloseDurationSeconds = moveDur,
                                TriggerPosition = triggerPosUnity,
                                TriggerDistSq = setTune.TriggerDist2,
                                TriggerTimeoutSeconds = setTune.TriggerTimeout
                            };

                            var ctrl = groupGo.AddComponent<MM2DrawbridgeGroup>();
                            ctrl.InitializeWithSchedule(
                                rbA,
                                baseRotA,
                                rbB,
                                baseRotB,
                                center,
                                maxPitchDeg,
                                schedule
                            );
                            bridgeIndex++;
                        }
                    }
                }

                bridgeLeaves.Clear();
                bridgeGates.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].Trim();
                if (string.IsNullOrEmpty(raw)) continue;

                if (raw.StartsWith("DrawBridge", StringComparison.OrdinalIgnoreCase) && raw.IndexOf(',') < 0)
                {
                    if (bridgeLeaves.Count >= 2)
                    {
                        FlushCurrentDrawbridge();
                    }
                    currentBlockHeader = raw;
                    continue;
                }

                string[] cols = raw.Split(',');
                if (cols.Length >= 8)
                {
                    string itemName = cols[0].Trim().ToLowerInvariant();
                    if (float.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x1) &&
                        float.TryParse(cols[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y1) &&
                        float.TryParse(cols[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z1) &&
                        float.TryParse(cols[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x2) &&
                        float.TryParse(cols[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y2) &&
                        float.TryParse(cols[7].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z2))
                    {
                        if (itemName.StartsWith("tpdrawbridge", StringComparison.OrdinalIgnoreCase))
                        {
                            bridgeLeaves.Add((itemName, new Vector3(x1, y1, z1)));
                        }
                        else if (itemName.StartsWith("tpcrossgate", StringComparison.OrdinalIgnoreCase))
                        {
                            bridgeGates.Add((itemName, new Vector3(x1, y1, z1), new Vector3(x2, y2, z2)));
                        }
                    }
                }
            }

            if (bridgeLeaves.Count >= 2)
            {
                FlushCurrentDrawbridge();
            }
        }

        private static void ExpandInstFile(
            string virtualPath,
            Func<float, float, RuntimeDistrictChunk> getChunk,
            Transform bangersRoot
        )
        {
            byte[] data = MM2ArchiveSystem.ReadFileBytes(virtualPath);
            if (data == null || data.Length < 10) return;

            int off = 0;
            while (off + 5 <= data.Length)
            {
                ushort room = BitConverter.ToUInt16(data, off);
                ushort mods = BitConverter.ToUInt16(data, off + 2);
                byte nameAndType = data[off + 4];
                off += 5;

                int nameLen = nameAndType & 0x7F;
                bool isSimple = (nameAndType & 0x80) != 0;
                if (off + nameLen > data.Length) break;

                string pkgName = nameLen > 1
                    ? Encoding.ASCII.GetString(data, off, nameLen - 1).Trim().ToLowerInvariant()
                    : string.Empty;
                off += nameLen;

                int variant = mods & 0xFF;
                int flags = mods >> 8;

                Vector3 xa, ya, za, orig;
                if (isSimple)
                {
                    if (off + 20 > data.Length) break;
                    float xd = BitConverter.ToSingle(data, off);
                    float zd = BitConverter.ToSingle(data, off + 4);
                    orig = new Vector3(
                        BitConverter.ToSingle(data, off + 8),
                        BitConverter.ToSingle(data, off + 12),
                        BitConverter.ToSingle(data, off + 16));
                    off += 20;
                    xa = new Vector3(xd, 0f, zd);
                    ya = new Vector3(0f, 1f, 0f);
                    za = new Vector3(-zd, 0f, xd);
                }
                else
                {
                    if (off + 48 > data.Length) break;
                    xa = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), BitConverter.ToSingle(data, off + 8));
                    ya = new Vector3(BitConverter.ToSingle(data, off + 12), BitConverter.ToSingle(data, off + 16), BitConverter.ToSingle(data, off + 20));
                    za = new Vector3(BitConverter.ToSingle(data, off + 24), BitConverter.ToSingle(data, off + 28), BitConverter.ToSingle(data, off + 32));
                    orig = new Vector3(BitConverter.ToSingle(data, off + 36), BitConverter.ToSingle(data, off + 40), BitConverter.ToSingle(data, off + 44));
                    off += 48;
                }

                if (room == 0 || string.IsNullOrEmpty(pkgName)) continue;

                ExpandSinglePkgWithXrefs(pkgName, variant, (flags & 2) == 0, xa, ya, za, orig, getChunk, bangersRoot);
            }
        }

        private static Material GetOrCreateCityMaterial(
            string tName,
            Shader urpLit,
            Dictionary<string, Material> cityMatCache
        )
        {
            if (cityMatCache.TryGetValue(tName, out Material m) && m != null) return m;
            if (urpLit == null) urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            m = new Material(urpLit) { name = $"Mat_City_{tName}" };
            bool isBridgeUnderside = tName.IndexOf("_bot_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     tName.IndexOf("underside", StringComparison.OrdinalIgnoreCase) >= 0;
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", isBridgeUnderside ? 2f : 0f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.08f);

            if (tName.StartsWith("__color_", StringComparison.OrdinalIgnoreCase) && tName.Length >= 14)
            {
                string hex = tName.Substring(8, 6);
                if (int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) &&
                    int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) &&
                    int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
                {
                    Color c = new Color32((byte)r, (byte)g, (byte)b, 255);
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                    m.color = c;
                }
                cityMatCache[tName] = m;
                MM2EnvironmentManager.RegisterDynamicMaterial(m, tName);
                return m;
            }

            var texInfo = MM2RuntimeDecoders.LoadTexture(tName);
            if (texInfo.Texture != null)
            {
                m.mainTexture = texInfo.Texture;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texInfo.Texture);

                bool isGlass = tName.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isGlass)
                {
                    Color glassCol = new Color(0.92f, 0.97f, 1.0f, texInfo.HasAlpha ? 0.72f : 0.48f);
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", glassCol);
                    m.color = glassCol;
                    m.SetFloat("_Surface", 1f);
                    m.SetFloat("_Blend", 0f);
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                    if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    m.SetFloat("_ZWrite", 0f);
                    m.SetFloat("_AlphaClip", 0f);
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.82f);
                    m.DisableKeyword("_ALPHATEST_ON");
                    m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    m.SetOverrideTag("RenderType", "Transparent");
                    m.SetShaderPassEnabled("DepthOnly", false);
                    m.SetShaderPassEnabled("ShadowCaster", false);
                    m.renderQueue = (int)RenderQueue.Transparent;
                }
                else if (texInfo.HasAlpha)
                {
                    m.SetFloat("_Surface", 0f);
                    m.SetFloat("_AlphaClip", 1f);
                    m.SetFloat("_Cutoff", 0.35f);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.SetOverrideTag("RenderType", "TransparentCutout");
                    m.renderQueue = (int)RenderQueue.AlphaTest;
                }
            }
            cityMatCache[tName] = m;
            MM2EnvironmentManager.RegisterDynamicMaterial(m, tName);
            return m;
        }

        /// <summary>
        /// Refreshes race- and lesson-specific .pathset files (ramps, ferries, Thames barges, barricades, cones, parked cars, and drawbridge overrides).
        /// </summary>
        public static void RefreshRaceSpecificPathsets(string locationName, string raceStem, string stageWaypointStem = null)
        {
            CitySpec spec = GetCitySpec(locationName);
            if (spec == null) return;

            GameObject cityRoot = GameObject.Find("MM2_Runtime_City");
            if (cityRoot == null) return;

            Transform oldRaceRoot = cityRoot.transform.Find("MM2_Race_Pathsets");
            if (oldRaceRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(oldRaceRoot.gameObject);
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var matCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            Material GetMat(string tName) => GetOrCreateCityMaterial(tName, urpLit, matCache);

            // Refresh race-specific drawbridges if a race override exists or if we need to restore default drawbridges
            Transform oldBridges = cityRoot.transform.Find($"{spec.MapName}_Drawbridges");
            if (oldBridges != null)
            {
                UnityEngine.Object.DestroyImmediate(oldBridges.gameObject);
            }

            GameObject bridgesRoot = new GameObject($"{spec.MapName}_Drawbridges");
            bridgesRoot.transform.SetParent(cityRoot.transform, false);

            string defaultBridgePathset = $"race/{spec.RaceDir}/{spec.MapName}_bridge.pathset";
            string bridgePathset = defaultBridgePathset;
            if (!string.IsNullOrEmpty(raceStem))
            {
                string raceBridgeCandidate = $"race/{spec.RaceDir}/{spec.MapName}_bridge_{raceStem}.pathset";
                if (MM2ArchiveSystem.FileExists(raceBridgeCandidate))
                {
                    bridgePathset = raceBridgeCandidate;
                }
            }

            if (MM2ArchiveSystem.FileExists(bridgePathset))
            {
                ExpandBridgePathset(bridgePathset, bridgesRoot.transform, GetMat, false, defaultBridgePathset);
            }
            else if (MM2ArchiveSystem.FileExists($"city/{spec.MapName}.gizmo"))
            {
                ExpandMm1GizmoDrawbridges($"city/{spec.MapName}.gizmo", bridgesRoot.transform, GetMat, raceStem);
            }

            GameObject raceRoot = new GameObject("MM2_Race_Pathsets");
            raceRoot.transform.SetParent(cityRoot.transform, false);

            GameObject raceBangers = new GameObject("Race_Bangers");
            raceBangers.transform.SetParent(raceRoot.transform, false);

            var raceChunk = new RuntimeDistrictChunk();
            RuntimeDistrictChunk GetRaceChunk(float wx, float wz) => raceChunk;

            bool loadedFerryFromRacePathset = false;
            if (!string.IsNullOrEmpty(raceStem))
            {
                string primaryPathset = $"race/{spec.RaceDir}/{raceStem}.pathset";
                if (MM2ArchiveSystem.FileExists(primaryPathset))
                {
                    ExpandPropsPathset(primaryPathset, GetRaceChunk, raceBangers.transform);
                    if (string.Equals(raceStem, "crash0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(raceStem, "crash3", StringComparison.OrdinalIgnoreCase))
                    {
                        loadedFerryFromRacePathset = true;
                    }
                }
                else if (!string.IsNullOrEmpty(stageWaypointStem))
                {
                    string stagePathset = $"race/{spec.RaceDir}/{stageWaypointStem}.pathset";
                    if (MM2ArchiveSystem.FileExists(stagePathset))
                    {
                        ExpandPropsPathset(stagePathset, GetRaceChunk, raceBangers.transform);
                    }
                }

                // Support MM1 circuit barricade banger files (city/<mapName>_c0.bng .. _c9.bng)
                if (raceStem.StartsWith("circuit", StringComparison.OrdinalIgnoreCase) && raceStem.Length > 7)
                {
                    string idxPart = raceStem.Substring(7);
                    string mm1CircuitBng = $"city/{spec.MapName}_c{idxPart}.bng";
                    if (MM2ArchiveSystem.FileExists(mm1CircuitBng))
                    {
                        ExpandMm1BngFile(mm1CircuitBng, GetRaceChunk, raceBangers.transform);
                    }
                }

                string parkedCarPathset = $"race/{spec.RaceDir}/{spec.MapName}_parkedcar_{raceStem}.pathset";
                if (MM2ArchiveSystem.FileExists(parkedCarPathset))
                {
                    ExpandPropsPathset(parkedCarPathset, GetRaceChunk, raceBangers.transform);
                }

                string ferryRacePathset = $"race/{spec.RaceDir}/{spec.MapName}_ferry_{raceStem}.pathset";
                if (MM2ArchiveSystem.FileExists(ferryRacePathset))
                {
                    ExpandPropsPathset(ferryRacePathset, GetRaceChunk, raceBangers.transform);
                    loadedFerryFromRacePathset = true;
                }
            }

            if (!loadedFerryFromRacePathset)
            {
                string defaultFerryPathset = $"race/{spec.RaceDir}/{spec.MapName}_ferry.pathset";
                if (MM2ArchiveSystem.FileExists(defaultFerryPathset))
                {
                    ExpandPropsPathset(defaultFerryPathset, GetRaceChunk, raceBangers.transform);
                }
            }

            if (raceChunk.VisualByTexture.Count > 0 || raceChunk.ColTriangles.Count > 0)
            {
                GameObject staticGo = new GameObject("MM2_Race_Pathsets_StaticMesh");
                staticGo.transform.SetParent(raceRoot.transform, false);

                if (raceChunk.VisualByTexture.Count > 0)
                {
                    var allPos = new List<Vector3>();
                    var allNorm = new List<Vector3>();
                    var allUV = new List<Vector2>();
                    var subTris = new List<int[]>();
                    var mats = new List<Material>();

                    foreach (var kvp in raceChunk.VisualByTexture)
                    {
                        RuntimeSubmeshBucket b = kvp.Value;
                        if (b.Triangles.Count == 0) continue;

                        int baseV = allPos.Count;
                        allPos.AddRange(b.Positions);
                        allNorm.AddRange(b.Normals);
                        allUV.AddRange(b.UVs);

                        int[] indices = new int[b.Triangles.Count];
                        for (int i = 0; i < indices.Length; i++) indices[i] = baseV + b.Triangles[i];
                        subTris.Add(indices);
                        mats.Add(GetMat(kvp.Key));
                    }

                    Mesh visMesh = new Mesh
                    {
                        name = "MM2_Race_Pathsets_VisMesh",
                        indexFormat = IndexFormat.UInt32
                    };
                    visMesh.SetVertices(allPos);
                    visMesh.SetNormals(allNorm);
                    visMesh.SetUVs(0, allUV);
                    visMesh.subMeshCount = subTris.Count;
                    for (int s = 0; s < subTris.Count; s++)
                    {
                        visMesh.SetTriangles(subTris[s], s, false);
                    }
                    visMesh.RecalculateBounds();

                    staticGo.AddComponent<MeshFilter>().sharedMesh = visMesh;
                    staticGo.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
                }

                if (raceChunk.ColPositions.Count > 0 && raceChunk.ColTriangles.Count > 0)
                {
                    Mesh colMesh = new Mesh
                    {
                        name = "MM2_Race_Pathsets_ColMesh",
                        indexFormat = IndexFormat.UInt32
                    };
                    colMesh.SetVertices(raceChunk.ColPositions);
                    colMesh.SetTriangles(raceChunk.ColTriangles, 0, true);
                    var mc = staticGo.AddComponent<MeshCollider>();
                    mc.sharedMesh = colMesh;
                }
            }

            Physics.SyncTransforms();
        }

        private static void ExpandPropsPathset(
            string virtualPath,
            Func<float, float, RuntimeDistrictChunk> getChunk,
            Transform bangersRoot
        )
        {
            byte[] data = MM2ArchiveSystem.ReadFileBytes(virtualPath);
            if (data == null || data.Length < 12) return;
            if (data[0] != 'P' || data[1] != 'T' || data[2] != 'H' || data[3] != '1') return;

            uint nPaths = BitConverter.ToUInt32(data, 4);
            int off = 12;

            for (uint p = 0; p < nPaths && off + 44 <= data.Length; p++)
            {
                int nul = 0;
                while (nul < 32 && data[off + nul] != 0) nul++;
                string pkgName = nul > 0
                    ? Encoding.ASCII.GetString(data, off, nul).Trim().ToLowerInvariant()
                    : string.Empty;
                uint nPts = BitConverter.ToUInt32(data, off + 32);
                off += 44;

                if (nPts > 256u) break;

                var pts = new List<(Vector3 pos, ushort ang)>((int)nPts);
                for (uint i = 0; i < nPts && off + 16 <= data.Length; i++)
                {
                    float x = BitConverter.ToSingle(data, off);
                    float y = BitConverter.ToSingle(data, off + 4);
                    float z = BitConverter.ToSingle(data, off + 8);
                    ushort ang = BitConverter.ToUInt16(data, off + 12);
                    off += 16;

                    if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                        float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z) ||
                        Mathf.Abs(x) > 10000f || Mathf.Abs(y) > 1000f || Mathf.Abs(z) > 10000f)
                    {
                        continue;
                    }
                    pts.Add((new Vector3(x, y, z), ang));
                }

                if (string.IsNullOrEmpty(pkgName) || pts.Count == 0) continue;
                if (pkgName.StartsWith("path", StringComparison.OrdinalIgnoreCase)) continue;

                // Ferry / Thames barge placement (sp_carferry01_f in SF crash0.pathset, giz_carferry01_l in London/SF ferry pathsets):
                // Each path record represents a single ferry/barge at pts[0], oriented toward pts[1].
                if (pkgName.IndexOf("carferry", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Vector3 p0 = pts[0].pos;
                    Vector3 flatDir = Vector3.forward;
                    if (pts.Count >= 2)
                    {
                        Vector3 delta = pts[1].pos - p0;
                        flatDir = new Vector3(delta.x, 0f, delta.z);
                        if (flatDir.sqrMagnitude > 0.001f) flatDir.Normalize();
                        else flatDir = Vector3.forward;
                    }
                    else
                    {
                        float rad = (pts[0].ang / 65536f) * 2f * Mathf.PI;
                        flatDir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                    }

                    Vector3 ya = Vector3.up;
                    Vector3 xa, za;
                    if (pkgName.IndexOf("giz_carferry", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // giz_carferry01_l has its 91.5m barge length along Z (entry ramp at +Z, launch ramp at -Z)
                        za = flatDir;
                        xa = new Vector3(za.z, 0f, -za.x);
                    }
                    else
                    {
                        // sp_carferry01_f has its 121.6m car deck length along X (open bow at +X, stern wheelhouse at -X)
                        xa = flatDir;
                        za = new Vector3(-xa.z, 0f, xa.x);
                    }

                    ExpandSinglePkgWithXrefs(pkgName, 0, true, xa, ya, za, p0, getChunk, bangersRoot);
                    continue;
                }

                // Stunt jump ramp trailer (sp_jumptrailer_f in SF crash0.pathset, London crash9.pathset, race7.pathset):
                // pts[0] is the low entry end and pts[1] is the launch direction.
                if (pkgName.IndexOf("jumptrailer", StringComparison.OrdinalIgnoreCase) >= 0 && pts.Count >= 2)
                {
                    for (int i = 0; i + 1 < pts.Count; i += 2)
                    {
                        Vector3 p0 = pts[i].pos;
                        Vector3 p1 = pts[i + 1].pos;
                        Vector3 delta = p1 - p0;
                        Vector3 flatDir = new Vector3(delta.x, 0f, delta.z);
                        if (flatDir.sqrMagnitude > 0.001f) flatDir.Normalize();
                        else flatDir = Vector3.forward;

                        Vector3 ya = Vector3.up;
                        Vector3 xa = flatDir;
                        Vector3 za = new Vector3(-xa.z, 0f, xa.x);
                        // Lower by 0.22m so the entry ramp lip sits flush with the road/pier surface for smooth WheelCollider transition
                        Vector3 orig = new Vector3(p0.x, p0.y - 0.22f, p0.z);
                        ExpandSinglePkgWithXrefs(pkgName, 0, true, xa, ya, za, orig, getChunk, bangersRoot);
                    }
                    continue;
                }

                int pathMode = pts[pts.Count - 1].ang & 0xFF;
                int spacingRaw = pts[pts.Count - 1].ang >> 8;

                // Mode 1: Oriented pairs (pts[2*k] = origin, pts[2*k + 1] = direction target)
                if (pathMode == 1 && pts.Count >= 2)
                {
                    MM2BangerSystem.TryGetBangerSpec(pkgName, out MM2BangerSpec spec);
                    bool alignAlongX = (spec == null || spec.Size.x >= spec.Size.z);

                    for (int i = 0; i + 1 < pts.Count; i += 2)
                    {
                        Vector3 p0 = pts[i].pos;
                        Vector3 p1 = pts[i + 1].pos;
                        Vector3 delta = p1 - p0;
                        Vector3 flatDir = new Vector3(delta.x, 0f, delta.z);
                        if (flatDir.sqrMagnitude > 0.001f) flatDir.Normalize();
                        else flatDir = Vector3.forward;

                        Vector3 ya = Vector3.up;
                        Vector3 xa, za;
                        if (alignAlongX)
                        {
                            xa = flatDir;
                            za = new Vector3(-xa.z, 0f, xa.x);
                        }
                        else
                        {
                            za = flatDir;
                            xa = new Vector3(za.z, 0f, -za.x);
                        }

                        ExpandSinglePkgWithXrefs(pkgName, 0, true, xa, ya, za, p0, getChunk, bangersRoot);
                    }
                    continue;
                }

                // Mode 2: Spaced polyline across all segments (pts[seg] -> pts[seg + 1])
                if (pathMode == 2 && pts.Count >= 2)
                {
                    float defaultSpacing = pkgName.Contains("bollard") || pkgName.Contains("cone") || pkgName.Contains("woodbar") || pkgName.Contains("barricade")
                        ? 5.0f
                        : 12.0f;
                    float spacing = spacingRaw > 0 ? Mathf.Max(2.5f, spacingRaw * 0.25f) : defaultSpacing;
                    bool isBarricadeWall = pkgName.Contains("barricade");

                    for (int seg = 0; seg + 1 < pts.Count; seg++)
                    {
                        Vector3 p0 = pts[seg].pos;
                        Vector3 p1 = pts[seg + 1].pos;
                        Vector3 delta = p1 - p0;
                        float segLen = delta.magnitude;
                        if (segLen < 0.05f) continue;

                        Vector3 dir = delta / segLen;
                        Vector3 flatDir = new Vector3(dir.x, 0f, dir.z);
                        if (flatDir.sqrMagnitude < 0.001f) flatDir = Vector3.forward;
                        else flatDir.Normalize();

                        Vector3 ya = Vector3.up;
                        if (isBarricadeWall)
                        {
                            // sp_barricadeconcl_f / sp_barricadeconcr_f span X = [0, 5.0] with CG.x = +2.5
                            Vector3 xa = flatDir;
                            Vector3 za = new Vector3(-xa.z, 0f, xa.x);
                            int count = Mathf.Clamp(Mathf.RoundToInt(segLen / spacing), 1, 64);
                            for (int k = 0; k < count; k++)
                            {
                                float t = (float)k / count;
                                Vector3 orig = Vector3.Lerp(p0, p1, t);
                                ExpandSinglePkgWithXrefs(pkgName, 0, true, xa, ya, za, orig, getChunk, bangersRoot);
                            }
                        }
                        else
                        {
                            bool isPerpendicularChicaneCar =
                                pkgName.StartsWith("giz_pcar", StringComparison.OrdinalIgnoreCase) ||
                                (pkgName.IndexOf("pcar", StringComparison.OrdinalIgnoreCase) >= 0 && spacing >= 15.0f);
                            Vector3 xa, za;
                            if (isPerpendicularChicaneCar)
                            {
                                // Slalom / chicane obstacle cars on Lambeth Bridge (crash2 "Weaver's Tale", Midterm 2/3, Final Exam)
                                // sit perpendicular across the bridge lanes with their front hood (+Z) pointing right across the road.
                                xa = -flatDir;
                                za = new Vector3(flatDir.z, 0f, -flatDir.x);
                            }
                            else
                            {
                                za = flatDir;
                                xa = new Vector3(za.z, 0f, -za.x);
                            }

                            int count = Mathf.Clamp(Mathf.FloorToInt(segLen / spacing) + 1, 2, 64);
                            for (int k = 0; k < count; k++)
                            {
                                float t = count > 1 ? (float)k / (count - 1) : 0f;
                                Vector3 orig = Vector3.Lerp(p0, p1, t);
                                ExpandSinglePkgWithXrefs(pkgName, 0, true, xa, ya, za, orig, getChunk, bangersRoot);
                            }
                        }
                    }
                    continue;
                }

                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3 orig = pts[i].pos;
                    ushort ang = pts[i].ang;
                    float rad = (ang / 65536f) * 2f * Mathf.PI;
                    float c = Mathf.Cos(rad);
                    float s = Mathf.Sin(rad);
                    Vector3 xa = new Vector3(c, 0f, s);
                    Vector3 ya = new Vector3(0f, 1f, 0f);
                    Vector3 za = new Vector3(-s, 0f, c);

                    ExpandSinglePkgWithXrefs(pkgName, 0, true, xa, ya, za, orig, getChunk, bangersRoot);
                }
            }
        }

        private static List<(string rawName, uint expectedPts, List<(Vector3 pos, ushort ang)> pts)> ParseBridgePathsetEntries(string virtualPath)
        {
            var result = new List<(string rawName, uint expectedPts, List<(Vector3 pos, ushort ang)> pts)>(4);
            if (string.IsNullOrEmpty(virtualPath)) return result;

            byte[] data = MM2ArchiveSystem.ReadFileBytes(virtualPath);
            if (data == null || data.Length < 12) return result;
            if (data[0] != 'P' || data[1] != 'T' || data[2] != 'H' || data[3] != '1') return result;

            uint nPaths = BitConverter.ToUInt32(data, 4);
            int off = 12;

            for (uint p = 0; p < nPaths && off + 44 <= data.Length; p++)
            {
                int nul = 0;
                while (nul < 32 && data[off + nul] != 0) nul++;
                string rawEntryName = nul > 0
                    ? Encoding.ASCII.GetString(data, off, nul).Trim()
                    : string.Empty;
                uint nPts = BitConverter.ToUInt32(data, off + 32);
                off += 44;

                var pts = new List<(Vector3 pos, ushort ang)>((int)Math.Min(nPts, 32u));
                for (uint i = 0; i < nPts && off + 16 <= data.Length; i++)
                {
                    float x = BitConverter.ToSingle(data, off);
                    float y = BitConverter.ToSingle(data, off + 4);
                    float z = BitConverter.ToSingle(data, off + 8);
                    ushort ang = BitConverter.ToUInt16(data, off + 12);
                    off += 16;
                    pts.Add((new Vector3(x, y, z), ang));
                }

                result.Add((rawEntryName, nPts, pts));
            }

            return result;
        }

        private static void ExpandBridgePathset(
            string virtualPath,
            Transform bridgesRoot,
            Func<string, Material> getMaterial,
            bool forceBridge01AlwaysOpenJump = false,
            string fallbackBasePathset = null
        )
        {
            if (bridgesRoot == null) return;

            var entries = ParseBridgePathsetEntries(virtualPath);
            if (entries.Count == 0) return;

            if (!string.IsNullOrEmpty(fallbackBasePathset) &&
                !string.Equals(virtualPath, fallbackBasePathset, StringComparison.OrdinalIgnoreCase))
            {
                var baseEntries = ParseBridgePathsetEntries(fallbackBasePathset);
                for (int i = 0; i < entries.Count && i < baseEntries.Count; i++)
                {
                    // Recover truncated point coordinates (e.g. Path 3 Lambeth Bridge in london_bridge_blitz10.pathset)
                    if (entries[i].pts.Count < entries[i].expectedPts && baseEntries[i].pts.Count >= baseEntries[i].expectedPts)
                    {
                        entries[i] = (entries[i].rawName, baseEntries[i].expectedPts, baseEntries[i].pts);
                    }
                }

                // Supplement omitted bridges (e.g. Waterloo & Lambeth Bridges in london_bridge_circuit0.pathset) as inactive closed decks
                for (int i = entries.Count; i < baseEntries.Count; i++)
                {
                    string baseName = baseEntries[i].rawName ?? string.Empty;
                    int c = baseName.IndexOfAny(new[] { ':', '@' });
                    string barePkg = c >= 0 ? baseName.Substring(c + 1).Trim() : baseName.Trim();
                    entries.Add(($"inactive:{barePkg}", baseEntries[i].expectedPts, baseEntries[i].pts));
                }
            }

            // If a .mmbridgemgr tune exists in the mounted .ar archives, read GoalAngle/Speed/Intervals from it;
            // otherwise use standard bridge rotation defaults.
            Mm1BridgeMgrTune mm2Tune = LoadMm1BridgeMgrTune(null);
            float mm2GoalRad = mm2Tune.BridgeOnGoal > 0.01f ? mm2Tune.BridgeOnGoal : 0.47123894f;
            float mm2GoalPitchDeg = mm2GoalRad * Mathf.Rad2Deg;
            const float Mm2MoveDurationSeconds = 0.47123894f / 0.05f;
            const float Mm2DownIntervalSeconds = 10.0f;
            const float Mm2UpIntervalSeconds = 10.0f;
            const float Mm2ProxDistSq = 10000.0f;

            for (int p = 0; p < entries.Count; p++)
            {
                string rawEntryName = entries[p].rawName;
                var pts = entries[p].pts;
                if (string.IsNullOrEmpty(rawEntryName) || pts.Count < 2) continue;

                string pkgName = rawEntryName.ToLowerInvariant();
                MM2DrawbridgeMode mode = MM2DrawbridgeMode.Timed;

                int delim = pkgName.IndexOfAny(new[] { ':', '@' });
                if (delim >= 0)
                {
                    string prefix = pkgName.Substring(0, delim).Trim();
                    pkgName = pkgName.Substring(delim + 1).Trim();
                    if (prefix.StartsWith("open", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = MM2DrawbridgeMode.AlwaysOpen;
                    }
                    else if (prefix.StartsWith("inac", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = MM2DrawbridgeMode.Inactive;
                    }
                    else if (prefix.StartsWith("prox", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = MM2DrawbridgeMode.Proximity;
                    }
                    else if (prefix.StartsWith("time", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = MM2DrawbridgeMode.Timed;
                    }
                }

                if (forceBridge01AlwaysOpenJump && pkgName.IndexOf("bridge01", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    mode = MM2DrawbridgeMode.AlwaysOpen;
                }

                MM2RuntimePkgLoader.ParsedPkg tmpl = MM2RuntimePkgLoader.LoadPkg(pkgName);
                if (tmpl == null) continue;

                Mesh leafMesh = BuildDrawbridgeLeafMesh(
                    pkgName,
                    tmpl,
                    getMaterial,
                    out Material[] leafMats,
                    out float leafLength,
                    out float totalHalfWidth,
                    out float roadHalfWidth,
                    out float roadSurfaceY,
                    out float sidewalkSurfaceY
                );
                if (leafMesh == null || leafMats == null || leafMats.Length == 0) continue;

                GameObject groupGo = new GameObject($"Drawbridge_{p}_{pkgName}");
                groupGo.transform.SetParent(bridgesRoot, false);

                if (pts.Count >= 3)
                {
                    // Two-leaf bascule bridge (e.g. Tower Bridge giz_bridge01_l, Waterloo Bridge & Lambeth Bridge giz_waterloo_l):
                    // pt[0] = Shore Hinge A, pt[1] = Center Gap, pt[2] = Shore Hinge B
                    Vector3 hingeA = new Vector3(-pts[0].pos.x, pts[0].pos.y, pts[0].pos.z);
                    Vector3 center = new Vector3(-pts[1].pos.x, pts[0].pos.y, pts[1].pos.z);
                    Vector3 hingeB = new Vector3(-pts[2].pos.x, pts[2].pos.y, pts[2].pos.z);

                    Vector3 dirA = new Vector3(center.x - hingeA.x, 0f, center.z - hingeA.z);
                    Vector3 dirB = new Vector3(center.x - hingeB.x, 0f, center.z - hingeB.z);
                    if (dirA.sqrMagnitude < 0.01f || dirB.sqrMagnitude < 0.01f) continue;

                    Quaternion baseRotA = Quaternion.LookRotation(dirA.normalized, Vector3.up);
                    Quaternion baseRotB = Quaternion.LookRotation(dirB.normalized, Vector3.up);

                    Rigidbody rbA = SpawnDrawbridgeLeaf(
                        groupGo.transform,
                        $"{pkgName}_LeafA",
                        leafMesh,
                        leafMats,
                        hingeA,
                        baseRotA,
                        leafLength,
                        totalHalfWidth,
                        roadHalfWidth,
                        roadSurfaceY,
                        sidewalkSurfaceY
                    );

                    Rigidbody rbB = SpawnDrawbridgeLeaf(
                        groupGo.transform,
                        $"{pkgName}_LeafB",
                        leafMesh,
                        leafMats,
                        hingeB,
                        baseRotB,
                        leafLength,
                        totalHalfWidth,
                        roadHalfWidth,
                        roadSurfaceY,
                        sidewalkSurfaceY
                    );

                    var schedule = new MM2DrawbridgeSchedule
                    {
                        Mode = mode,
                        StartsOpen = (mode == MM2DrawbridgeMode.AlwaysOpen),
                        InitialTimerSeconds = 0f,
                        DownIntervalSeconds = Mm2DownIntervalSeconds,
                        BridgeOnDelaySeconds = 0f,
                        OpenDurationSeconds = Mm2MoveDurationSeconds,
                        UpIntervalSeconds = Mm2UpIntervalSeconds,
                        BridgeOffDelaySeconds = 0f,
                        CloseDurationSeconds = Mm2MoveDurationSeconds,
                        TriggerPosition = center,
                        TriggerDistSq = Mm2ProxDistSq,
                        TriggerTimeoutSeconds = float.PositiveInfinity
                    };

                    var ctrl = groupGo.AddComponent<MM2DrawbridgeGroup>();
                    ctrl.InitializeWithSchedule(
                        rbA,
                        baseRotA,
                        rbB,
                        baseRotB,
                        center,
                        maxPitchDeg: mm2GoalPitchDeg,
                        schedule: schedule
                    );
                }
                else
                {
                    // Single-leaf drawbridge (e.g. Tower of London Moat open:giz_bridge02_l):
                    // pt[0] = Gate Hinge, pt[1] = Outer Moat Target
                    Vector3 hingeA = new Vector3(-pts[0].pos.x, pts[0].pos.y, pts[0].pos.z);
                    Vector3 target = new Vector3(-pts[1].pos.x, pts[0].pos.y, pts[1].pos.z);
                    Vector3 dirA = new Vector3(target.x - hingeA.x, 0f, target.z - hingeA.z);
                    if (dirA.sqrMagnitude < 0.01f) continue;

                    Quaternion baseRotA = Quaternion.LookRotation(dirA.normalized, Vector3.up);
                    Rigidbody rbA = SpawnDrawbridgeLeaf(
                        groupGo.transform,
                        $"{pkgName}_Leaf",
                        leafMesh,
                        leafMats,
                        hingeA,
                        baseRotA,
                        leafLength,
                        totalHalfWidth,
                        roadHalfWidth,
                        roadSurfaceY,
                        sidewalkSurfaceY
                    );

                    Vector3 center = hingeA + dirA.normalized * (leafLength * 0.5f);
                    var schedule = new MM2DrawbridgeSchedule
                    {
                        Mode = mode,
                        StartsOpen = (mode == MM2DrawbridgeMode.AlwaysOpen),
                        InitialTimerSeconds = 0f,
                        DownIntervalSeconds = Mm2DownIntervalSeconds,
                        BridgeOnDelaySeconds = 0f,
                        OpenDurationSeconds = Mm2MoveDurationSeconds,
                        UpIntervalSeconds = Mm2UpIntervalSeconds,
                        BridgeOffDelaySeconds = 0f,
                        CloseDurationSeconds = Mm2MoveDurationSeconds,
                        TriggerPosition = center,
                        TriggerDistSq = Mm2ProxDistSq,
                        TriggerTimeoutSeconds = float.PositiveInfinity
                    };

                    var ctrl = groupGo.AddComponent<MM2DrawbridgeGroup>();
                    ctrl.InitializeWithSchedule(
                        rbA,
                        baseRotA,
                        null,
                        Quaternion.identity,
                        center,
                        maxPitchDeg: mm2GoalPitchDeg,
                        schedule: schedule
                    );
                }
            }
        }

        private static Rigidbody SpawnDrawbridgeLeaf(
            Transform parent,
            string leafName,
            Mesh leafMesh,
            Material[] leafMats,
            Vector3 hingeWorldPos,
            Quaternion baseWorldRot,
            float leafLength,
            float totalHalfWidth,
            float roadHalfWidth,
            float roadSurfaceY,
            float sidewalkSurfaceY
        )
        {
            GameObject leafGo = new GameObject(leafName);
            leafGo.transform.SetParent(parent, false);
            leafGo.transform.SetPositionAndRotation(hingeWorldPos, baseWorldRot);

            var mf = leafGo.AddComponent<MeshFilter>();
            mf.sharedMesh = leafMesh;
            var mr = leafGo.AddComponent<MeshRenderer>();
            mr.sharedMaterials = leafMats;

            var rb = leafGo.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // Smooth road deck BoxCollider so WheelColliders roll cleanly up the raising/raised bridge leaf
            const float deckThickness = 1.2f;
            var roadBox = leafGo.AddComponent<BoxCollider>();
            roadBox.size = new Vector3(roadHalfWidth * 2f, deckThickness, leafLength);
            roadBox.center = new Vector3(0f, roadSurfaceY - deckThickness * 0.5f, leafLength * 0.5f);

            // Sidewalk & side railing BoxColliders if the bridge has sidewalks/parapets outside the road lanes
            float swWidth = totalHalfWidth - roadHalfWidth;
            if (swWidth > 0.35f)
            {
                const float curbThickness = 1.35f;
                const float railingHeight = 1.35f;

                var leftSwBox = leafGo.AddComponent<BoxCollider>();
                leftSwBox.size = new Vector3(swWidth, curbThickness, leafLength);
                leftSwBox.center = new Vector3(-(roadHalfWidth + swWidth * 0.5f), sidewalkSurfaceY - curbThickness * 0.5f, leafLength * 0.5f);

                var rightSwBox = leafGo.AddComponent<BoxCollider>();
                rightSwBox.size = new Vector3(swWidth, curbThickness, leafLength);
                rightSwBox.center = new Vector3(roadHalfWidth + swWidth * 0.5f, sidewalkSurfaceY - curbThickness * 0.5f, leafLength * 0.5f);

                // Outer parapet wall colliders so cars stay on the bridge deck during high-speed jumps
                var leftRailBox = leafGo.AddComponent<BoxCollider>();
                leftRailBox.size = new Vector3(0.45f, railingHeight, leafLength);
                leftRailBox.center = new Vector3(-(totalHalfWidth - 0.22f), sidewalkSurfaceY + railingHeight * 0.5f, leafLength * 0.5f);

                var rightRailBox = leafGo.AddComponent<BoxCollider>();
                rightRailBox.size = new Vector3(0.45f, railingHeight, leafLength);
                rightRailBox.center = new Vector3(totalHalfWidth - 0.22f, sidewalkSurfaceY + railingHeight * 0.5f, leafLength * 0.5f);
            }

            return rb;
        }

        private static Mesh BuildDrawbridgeLeafMesh(
            string pkgName,
            MM2RuntimePkgLoader.ParsedPkg tmpl,
            Func<string, Material> getMaterial,
            out Material[] materials,
            out float leafLength,
            out float totalHalfWidth,
            out float roadHalfWidth,
            out float roadSurfaceY,
            out float sidewalkSurfaceY
        )
        {
            materials = Array.Empty<Material>();
            leafLength = 20f;
            totalHalfWidth = 9f;
            roadHalfWidth = 6f;
            roadSurfaceY = -0.15f;
            sidewalkSurfaceY = 0f;

            List<MM2RuntimePkgLoader.PkgSection> baseSections = null;
            string[] lodOrder = { "H", "M", "L", "VL", "BODY_H" };
            foreach (string lod in lodOrder)
            {
                if (tmpl.GeometryChunks.TryGetValue(lod, out baseSections)) break;
            }
            if (baseSections == null || baseSections.Count == 0) return null;

            var sections = new List<MM2RuntimePkgLoader.PkgSection>(baseSections);
            if (tmpl.GeometryChunks.TryGetValue("RAIL_H", out var railSections) && railSections != null)
            {
                sections.AddRange(railSections);
            }

            // Compute raw bounds in PKG coordinates
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var sec in baseSections)
            {
                for (int i = 0; i < sec.Positions.Count; i++)
                {
                    Vector3 p = sec.Positions[i];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
            }

            float halfLen = Mathf.Max(1f, (maxZ - minZ) * 0.5f);
            leafLength = halfLen * 2f;
            totalHalfWidth = Mathf.Max(1f, (maxX - minX) * 0.5f);

            // In giz_bridge02_l (Tower of London moat drawbridge), the shore hinge support struts are at +Z (+10)
            // and the outer tip is at -Z (-10), with deck surface at Y = 0.00.
            // In tpdrawbridge04 / tpdrawbridge06 (MM1 Chicago River bascule bridges), the shore hinge is at Z = 0
            // and the center tip is at Z = -32.5, with road surface at Y = 0.00 and sidewalk at Y = +0.15.
            // In giz_bridge01_l (Tower Bridge) and giz_waterloo_l (Waterloo/Lambeth Bridge), the leaf is symmetric in Z,
            // with road surface at Y_pkg = +0.15 and sidewalk at Y_pkg = +0.30, while PSDL pathset hinge points are at sidewalk Y.
            bool isMoatDrawbridge = pkgName.IndexOf("bridge02", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isMm1Drawbridge = pkgName.StartsWith("tpdrawbridge", StringComparison.OrdinalIgnoreCase);
            float yShift = (isMoatDrawbridge || isMm1Drawbridge) ? 0.0f : -0.30f;
            roadSurfaceY = (isMoatDrawbridge || isMm1Drawbridge) ? 0.0f : -0.15f;
            sidewalkSurfaceY = isMm1Drawbridge ? 0.15f : 0.0f;
            roadHalfWidth = isMoatDrawbridge ? totalHalfWidth : Mathf.Max(3.0f, totalHalfWidth - 3.0f);

            var submeshesByTex = new Dictionary<string, RuntimeSubmeshBucket>(StringComparer.OrdinalIgnoreCase);

            foreach (var sec in sections)
            {
                int shIdx = sec.ShaderOffset;
                string tName = (shIdx >= 0 && shIdx < tmpl.Shaders.Count)
                    ? CleanTexName(tmpl.Shaders[shIdx].TextureName)
                    : string.Empty;
                if (string.IsNullOrEmpty(tName)) continue;

                bool isBridgeUnderside = tName.IndexOf("_bot_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         tName.IndexOf("underside", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!submeshesByTex.TryGetValue(tName, out RuntimeSubmeshBucket bucket))
                {
                    bucket = new RuntimeSubmeshBucket();
                    submeshesByTex[tName] = bucket;
                }

                int baseV = bucket.Positions.Count;
                float secYOffset = yShift + (isBridgeUnderside ? -0.45f : 0f);

                for (int i = 0; i < sec.Positions.Count; i++)
                {
                    Vector3 pt = sec.Positions[i];
                    Vector3 n = sec.Normals[i];

                    Vector3 uPos;
                    Vector3 uNorm;
                    if (isMm1Drawbridge)
                    {
                        // Flip both X and Z so Z_bms = 0 hinge becomes Z = 0 and Z_bms = -32.5 tip becomes Z = +32.5
                        uPos = new Vector3(-pt.x, pt.y + secYOffset, -pt.z);
                        uNorm = new Vector3(-n.x, n.y, -n.z).normalized;
                    }
                    else if (isMoatDrawbridge)
                    {
                        // Flip both X and Z (180° around Y) so Z_pkg = +10 hinge becomes Z = 0 and Z_pkg = -10 tip becomes Z = +20
                        // (two axis flips preserve triangle winding order!)
                        uPos = new Vector3(-pt.x, pt.y + secYOffset, halfLen - pt.z);
                        uNorm = new Vector3(-n.x, n.y, -n.z).normalized;
                    }
                    else
                    {
                        // Flip X for Unity left-handed coords and shift Z by +halfLen so Z = 0 is the hinge and Z = +2*halfLen is the center tip
                        uPos = new Vector3(-pt.x, pt.y + secYOffset, pt.z + halfLen);
                        uNorm = new Vector3(-n.x, n.y, n.z).normalized;
                    }

                    bucket.Positions.Add(uPos);
                    bucket.Normals.Add(uNorm);
                    bucket.UVs.Add(sec.UVs[i]);
                }

                for (int i = 0; i + 2 < sec.Triangles.Count; i += 3)
                {
                    if (isMoatDrawbridge || isMm1Drawbridge)
                    {
                        bucket.Triangles.Add(baseV + sec.Triangles[i]);
                        bucket.Triangles.Add(baseV + sec.Triangles[i + 1]);
                        bucket.Triangles.Add(baseV + sec.Triangles[i + 2]);
                    }
                    else
                    {
                        bucket.Triangles.Add(baseV + sec.Triangles[i + 2]);
                        bucket.Triangles.Add(baseV + sec.Triangles[i + 1]);
                        bucket.Triangles.Add(baseV + sec.Triangles[i]);
                    }
                }
            }

            if (submeshesByTex.Count == 0) return null;

            var allPos = new List<Vector3>();
            var allNorm = new List<Vector3>();
            var allUV = new List<Vector2>();
            var subTris = new List<int[]>();
            var mats = new List<Material>();

            foreach (var kvp in submeshesByTex)
            {
                RuntimeSubmeshBucket b = kvp.Value;
                if (b.Triangles.Count == 0) continue;

                int baseV = allPos.Count;
                allPos.AddRange(b.Positions);
                allNorm.AddRange(b.Normals);
                allUV.AddRange(b.UVs);

                int[] indices = new int[b.Triangles.Count];
                for (int i = 0; i < indices.Length; i++) indices[i] = baseV + b.Triangles[i];
                subTris.Add(indices);
                mats.Add(getMaterial(kvp.Key));
            }

            Mesh mesh = new Mesh { name = $"Drawbridge_{pkgName}_Mesh" };
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
            return mesh;
        }

        private static void ExpandSinglePkgWithXrefs(
            string pkgName,
            int variant,
            bool addCollider,
            Vector3 xa,
            Vector3 ya,
            Vector3 za,
            Vector3 orig,
            Func<float, float, RuntimeDistrictChunk> getChunk,
            Transform bangersRoot
        )
        {
            if (MM2BangerSystem.TryGetBangerSpec(pkgName, out MM2BangerSpec bangerSpec) && bangerSpec != null)
            {
                if (!bangerSpec.IsImmovableObstacle && bangersRoot != null)
                {
                    MM2BangerSystem.SpawnBangerProp(bangersRoot, pkgName, variant, bangerSpec, xa, ya, za, orig);
                    return;
                }

                // Immovable obstacles (e.g. freeway pillars sp_fwsupport_f, arches, monuments, ramps, ferries) are baked with CG offset + collider
                Vector3 cg = pkgName.IndexOf("giz_carferry", StringComparison.OrdinalIgnoreCase) >= 0
                    ? new Vector3(0f, bangerSpec.CG.y, 0f)
                    : bangerSpec.CG;
                orig = new Vector3(
                    orig.x + cg.x * xa.x + cg.y * ya.x + cg.z * za.x,
                    orig.y + cg.x * xa.y + cg.y * ya.y + cg.z * za.y,
                    orig.z + cg.x * xa.z + cg.y * ya.z + cg.z * za.z
                );
                addCollider = true;
            }

            MM2RuntimePkgLoader.ParsedPkg tmpl = MM2RuntimePkgLoader.LoadPkg(pkgName);
            if (tmpl == null) return;

            RuntimeDistrictChunk chunk = getChunk(-orig.x, orig.z);
            AppendPkgInstanceToChunk(chunk, tmpl, variant, addCollider, xa, ya, za, orig);

            if (tmpl.Xrefs != null && tmpl.Xrefs.Count > 0)
            {
                Vector3 MulAxis(Vector3 ax) => new Vector3(
                    ax.x * xa.x + ax.y * ya.x + ax.z * za.x,
                    ax.x * xa.y + ax.y * ya.y + ax.z * za.y,
                    ax.x * xa.z + ax.y * ya.z + ax.z * za.z
                );

                foreach (var xr in tmpl.Xrefs)
                {
                    if (string.IsNullOrEmpty(xr.TargetPkgName)) continue;

                    Vector3 cXa = MulAxis(xr.XAxis);
                    Vector3 cYa = MulAxis(xr.YAxis);
                    Vector3 cZa = MulAxis(xr.ZAxis);
                    Vector3 cOrig = new Vector3(
                        orig.x + xr.Origin.x * xa.x + xr.Origin.y * ya.x + xr.Origin.z * za.x,
                        orig.y + xr.Origin.x * xa.y + xr.Origin.y * ya.y + xr.Origin.z * za.y,
                        orig.z + xr.Origin.x * xa.z + xr.Origin.y * ya.z + xr.Origin.z * za.z
                    );

                    string childName = xr.TargetPkgName.Trim().ToLowerInvariant();
                    if (MM2BangerSystem.TryGetBangerSpec(childName, out MM2BangerSpec xBangerSpec) && xBangerSpec != null)
                    {
                        if (!xBangerSpec.IsImmovableObstacle && bangersRoot != null)
                        {
                            MM2BangerSystem.SpawnBangerProp(bangersRoot, childName, 0, xBangerSpec, cXa, cYa, cZa, cOrig);
                            continue;
                        }

                        Vector3 xCg = xBangerSpec.CG;
                        cOrig = new Vector3(
                            cOrig.x + xCg.x * cXa.x + xCg.y * cYa.x + xCg.z * cZa.x,
                            cOrig.y + xCg.x * cXa.y + xCg.y * cYa.y + xCg.z * cZa.y,
                            cOrig.z + xCg.x * cXa.z + xCg.y * cYa.z + xCg.z * cZa.z
                        );
                    }

                    MM2RuntimePkgLoader.ParsedPkg xTmpl = MM2RuntimePkgLoader.LoadPkg(childName);
                    if (xTmpl == null) continue;

                    bool childCollider = addCollider || (xBangerSpec != null && xBangerSpec.IsImmovableObstacle);
                    AppendPkgInstanceToChunk(chunk, xTmpl, 0, childCollider, cXa, cYa, cZa, cOrig);
                }
            }
        }

        // =====================================================================
        // PROCEDURAL SIDEWALK PROP PLACEMENT (propdefs.csv + proprules.csv)
        // =====================================================================
        private sealed class PropDefEntry
        {
            public string Name;
            public float Start;
            public float Distance;
            public int MaxUse;
            public float MinLerp;
            public float MaxLerp;
            public readonly List<string>PkgFiles = new List<string>();
        }

        private static void SpawnProceduralSidewalkProps(
            string mapName,
            List<PsdlElement>[] roomRoadElements,
            List<PsdlElement> crosswalkElements,
            byte[] roomPropRules,
            Transform bangersRoot
        )
        {
            if (roomRoadElements == null || roomPropRules == null || bangersRoot == null) return;

            Dictionary<string, PropDefEntry> propDefs = ParsePropDefsCsv($"city/{mapName}/propdefs.csv");
            Dictionary<string, List<string>> propRules = ParsePropRulesCsv($"city/{mapName}/proprules.csv");

            ResolveCityTrafficLightModels(mapName, propDefs, out string singleTrafficLightPkg, out string dualTrafficLightPkg, out PropDefEntry trafficLightDef);

            var placedTrafficLights = new List<Vector3>(768);
            SpawnCrosswalkTrafficLights(
                mapName,
                crosswalkElements,
                singleTrafficLightPkg,
                dualTrafficLightPkg,
                placedTrafficLights,
                bangersRoot
            );

            if (propDefs.Count == 0 || propRules.Count == 0) return;

            int nRooms = Math.Min(roomRoadElements.Length, roomPropRules.Length);
            for (int rIdx = 0; rIdx < nRooms; rIdx++)
            {
                List<PsdlElement> roads = roomRoadElements[rIdx];
                if (roads == null || roads.Count == 0) continue;

                byte ruleId = roomPropRules[rIdx];
                if (ruleId < 1 || ruleId > 20) continue;

                string leftKey = $"n{ruleId:D2}left";
                string rightKey = $"n{ruleId:D2}right";

                propRules.TryGetValue(leftKey, out List<string> leftTypes);
                propRules.TryGetValue(rightKey, out List<string> rightTypes);

                for (int e = 0; e < roads.Count; e++)
                {
                    PsdlElement road = roads[e];
                    int stride = road.RowBreadth;
                    int rows = road.RowCount;
                    if (road.Verts == null || rows < 2 || road.Verts.Length < rows * stride) continue;

                    // Left sidewalk: outer = row*stride + 0, curb = row*stride + 1
                    if (leftTypes != null && leftTypes.Count > 0)
                    {
                        float swWidthL = FlatDist(road.Verts[0], road.Verts[1]);
                        if (swWidthL > 0.45f)
                        {
                            SpawnSidewalkRulePropsAlongChain(
                                road.Verts,
                                rows,
                                stride,
                                curbOffset: 1,
                                outerOffset: 0,
                                ruleTypes: leftTypes,
                                propDefs: propDefs,
                                trafficLightDef: trafficLightDef,
                                placedTrafficLights: placedTrafficLights,
                                roomSeed: rIdx * 31 + e * 7 + 1,
                                bangersRoot: bangersRoot
                            );
                        }
                    }

                    // Right sidewalk: curb = row*stride + (stride - 2), outer = row*stride + (stride - 1)
                    if (rightTypes != null && rightTypes.Count > 0)
                    {
                        float swWidthR = FlatDist(road.Verts[stride - 1], road.Verts[stride - 2]);
                        if (swWidthR > 0.45f)
                        {
                            SpawnSidewalkRulePropsAlongChain(
                                road.Verts,
                                rows,
                                stride,
                                curbOffset: stride - 2,
                                outerOffset: stride - 1,
                                ruleTypes: rightTypes,
                                propDefs: propDefs,
                                trafficLightDef: trafficLightDef,
                                placedTrafficLights: placedTrafficLights,
                                roomSeed: rIdx * 31 + e * 7 + 2,
                                bangersRoot: bangersRoot
                            );
                        }
                    }
                }
            }
        }

        private static void ResolveCityTrafficLightModels(
            string mapName,
            Dictionary<string, PropDefEntry> propDefs,
            out string singlePkg,
            out string dualPkg,
            out PropDefEntry combinedDef
        )
        {
            singlePkg = null;
            dualPkg = null;
            combinedDef = new PropDefEntry { Name = "trafficlight" };

            if (propDefs != null)
            {
                if (propDefs.TryGetValue("trafficlight1", out PropDefEntry tl1) && tl1 != null && tl1.PkgFiles.Count > 0)
                {
                    singlePkg = tl1.PkgFiles[0];
                    combinedDef.PkgFiles.AddRange(tl1.PkgFiles);
                }
                if (propDefs.TryGetValue("trafficlight2", out PropDefEntry tl2) && tl2 != null && tl2.PkgFiles.Count > 0)
                {
                    dualPkg = tl2.PkgFiles[0];
                    combinedDef.PkgFiles.AddRange(tl2.PkgFiles);
                }
                if (propDefs.TryGetValue("trafficlight", out PropDefEntry tl) && tl != null && tl.PkgFiles.Count > 0)
                {
                    if (string.IsNullOrEmpty(singlePkg)) singlePkg = tl.PkgFiles[0];
                    if (string.IsNullOrEmpty(dualPkg)) dualPkg = tl.PkgFiles[tl.PkgFiles.Count > 1 ? 1 : 0];
                    combinedDef.PkgFiles.AddRange(tl.PkgFiles);
                }
            }

            if (string.IsNullOrEmpty(singlePkg))
            {
                bool isLondon = string.Equals(mapName, "london", StringComparison.OrdinalIgnoreCase);
                singlePkg = isLondon ? "sp_traflitsingle_ped_l" : "sp_traflitsingle_f";
                dualPkg = isLondon ? "sp_traflitsingle_corner_l" : "sp_traflitdual_f";
                combinedDef.PkgFiles.Add(singlePkg);
                combinedDef.PkgFiles.Add(dualPkg);
            }
            else if (string.IsNullOrEmpty(dualPkg))
            {
                dualPkg = singlePkg;
            }
        }

        private static void SpawnCrosswalkTrafficLights(
            string mapName,
            List<PsdlElement> crosswalks,
            string singlePkg,
            string dualPkg,
            List<Vector3> placedTrafficLights,
            Transform bangersRoot
        )
        {
            if (crosswalks == null || crosswalks.Count == 0 || bangersRoot == null) return;

            bool driveOnLeft = string.Equals(mapName, "london", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < crosswalks.Count; i++)
            {
                Vector3[] ev = crosswalks[i].Verts;
                if (ev == null || ev.Length < 4) continue;

                // In MM2 PSDL Crosswalk (etype == 4):
                // v0 -> v2 is the inner edge across the street (curb to curb),
                // v1 -> v3 is the outer edge 3m into the approaching road,
                // v0/v1 is the right-hand curb for approaching traffic, and v2/v3 is the left-hand curb.
                Vector3 v0 = ev[0];
                Vector3 v1 = ev[1];
                Vector3 v2 = ev[2];
                Vector3 v3 = ev[3];

                Vector3 acrossVec = new Vector3(v2.x - v0.x, 0f, v2.z - v0.z);
                float streetWidth = acrossVec.magnitude;
                if (streetWidth < 5.0f) continue;
                Vector3 acrossDir = acrossVec / streetWidth;

                Vector3 outVec = new Vector3(v1.x - v0.x, 0f, v1.z - v0.z);
                if (outVec.sqrMagnitude < 0.1f) continue;
                Vector3 outDir = outVec.normalized;

                Vector3 origAge;
                Vector3 xaAge;
                Vector3 yaAge = Vector3.up;
                Vector3 zaAge = outDir; // +Z faces oncoming traffic approaching the crosswalk

                if (driveOnLeft)
                {
                    // Left-hand traffic (London): upright signal on left-hand sidewalk corner (near v2/v3)
                    origAge = v2 + acrossDir * 0.55f + outDir * 0.35f;
                    origAge.y = v2.y + SidewalkOffsetHeight;
                    xaAge = new Vector3(-zaAge.z, 0f, zaAge.x);
                }
                else
                {
                    // Right-hand traffic (San Francisco): cantilever signal on right-hand sidewalk corner (near v0/v1)
                    // with xaAge pointing onto the sidewalk (-acrossDir) so the -X cantilever arm extends over the street.
                    origAge = v0 - acrossDir * 0.55f + outDir * 0.35f;
                    origAge.y = v0.y + SidewalkOffsetHeight;
                    xaAge = -acrossDir;
                }

                bool tooClose = false;
                for (int p = 0; p < placedTrafficLights.Count; p++)
                {
                    float dx = placedTrafficLights[p].x - origAge.x;
                    float dz = placedTrafficLights[p].z - origAge.z;
                    if (dx * dx + dz * dz < 9.0f)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                string pkgName = streetWidth >= 18.5f ? dualPkg : singlePkg;
                if (MM2BangerSystem.TryGetBangerSpec(pkgName, out MM2BangerSpec spec) &&
                    spec != null && !spec.IsImmovableObstacle)
                {
                    MM2BangerSystem.SpawnBangerProp(bangersRoot, pkgName, i, spec, xaAge, yaAge, zaAge, origAge);
                    placedTrafficLights.Add(origAge);
                }
            }
        }

        private static void SpawnSidewalkRulePropsAlongChain(
            Vector3[] verts,
            int rows,
            int stride,
            int curbOffset,
            int outerOffset,
            List<string> ruleTypes,
            Dictionary<string, PropDefEntry> propDefs,
            PropDefEntry trafficLightDef,
            List<Vector3> placedTrafficLights,
            int roomSeed,
            Transform bangersRoot
        )
        {
            // Compute cumulative arc length along the curb
            var cumDist = new float[rows];
            cumDist[0] = 0f;
            for (int r = 1; r < rows; r++)
            {
                Vector3 c0 = verts[(r - 1) * stride + curbOffset];
                Vector3 c1 = verts[r * stride + curbOffset];
                cumDist[r] = cumDist[r - 1] + Vector3.Distance(c0, c1);
            }

            float totalLen = cumDist[rows - 1];
            if (totalLen < 5.0f) return;

            var placedPositions = new List<Vector3>(16);
            bool hasStreetLightInRule = false;

            for (int tIdx = 0; tIdx < ruleTypes.Count; tIdx++)
            {
                string typeName = ruleTypes[tIdx];
                if (typeName.StartsWith("streetlight", StringComparison.OrdinalIgnoreCase))
                {
                    hasStreetLightInRule = true;
                }

                if (!propDefs.TryGetValue(typeName, out PropDefEntry def) || def == null || def.PkgFiles.Count == 0)
                {
                    continue;
                }

                float startD = Mathf.Max(2.0f, def.Start);
                float stepD = Mathf.Max(4.0f, def.Distance);
                int maxCount = Mathf.Max(1, def.MaxUse);

                for (int k = 0; k < maxCount; k++)
                {
                    float d = startD + k * stepD;
                    if (d > totalLen - 2.0f) break;

                    float lerpT = Mathf.Lerp(
                        def.MinLerp,
                        def.MaxLerp,
                        ((roomSeed + tIdx * 13 + k * 29) & 0xFF) / 255f
                    );
                    lerpT = Mathf.Clamp(lerpT, 0.12f, 0.88f);

                    if (!SampleSidewalkPointAtDistance(
                            verts,
                            cumDist,
                            rows,
                            stride,
                            curbOffset,
                            outerOffset,
                            d,
                            lerpT,
                            out Vector3 origAge,
                            out Vector3 xaAge,
                            out Vector3 yaAge,
                            out Vector3 zaAge))
                    {
                        continue;
                    }

                    // Avoid overlapping props placed by earlier rules on the same sidewalk
                    bool tooClose = false;
                    for (int p = 0; p < placedPositions.Count; p++)
                    {
                        if ((placedPositions[p] - origAge).sqrMagnitude < 3.2f)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (!tooClose && placedTrafficLights != null)
                    {
                        for (int p = 0; p < placedTrafficLights.Count; p++)
                        {
                            float dx = placedTrafficLights[p].x - origAge.x;
                            float dz = placedTrafficLights[p].z - origAge.z;
                            if (dx * dx + dz * dz < 4.0f)
                            {
                                tooClose = true;
                                break;
                            }
                        }
                    }
                    if (tooClose) continue;

                    string pkgName = def.PkgFiles[(roomSeed + tIdx + k) % def.PkgFiles.Count];
                    if (MM2BangerSystem.TryGetBangerSpec(pkgName, out MM2BangerSpec spec) &&
                        spec != null && !spec.IsImmovableObstacle)
                    {
                        MM2BangerSystem.SpawnBangerProp(bangersRoot, pkgName, roomSeed + k, spec, xaAge, yaAge, zaAge, origAge);
                        placedPositions.Add(origAge);
                    }
                }
            }

            // Fallback corner traffic light at the far end of urban streets with streetlamps that lack a PSDL crosswalk
            if (trafficLightDef != null && trafficLightDef.PkgFiles.Count > 0 && hasStreetLightInRule && totalLen >= 22f)
            {
                float cornerD = totalLen - 1.6f;
                if (SampleSidewalkPointAtDistance(
                        verts,
                        cumDist,
                        rows,
                        stride,
                        curbOffset,
                        outerOffset,
                        cornerD,
                        0.22f,
                        out Vector3 tlOrig,
                        out Vector3 tlXa,
                        out Vector3 tlYa,
                        out Vector3 tlZa))
                {
                    bool nearExistingLight = false;
                    if (placedTrafficLights != null)
                    {
                        for (int p = 0; p < placedTrafficLights.Count; p++)
                        {
                            float dx = placedTrafficLights[p].x - tlOrig.x;
                            float dz = placedTrafficLights[p].z - tlOrig.z;
                            if (dx * dx + dz * dz < 144.0f)
                            {
                                nearExistingLight = true;
                                break;
                            }
                        }
                    }

                    if (!nearExistingLight)
                    {
                        string tlPkg = trafficLightDef.PkgFiles[roomSeed % trafficLightDef.PkgFiles.Count];
                        if (MM2BangerSystem.TryGetBangerSpec(tlPkg, out MM2BangerSpec tlSpec) &&
                            tlSpec != null && !tlSpec.IsImmovableObstacle)
                        {
                            MM2BangerSystem.SpawnBangerProp(bangersRoot, tlPkg, roomSeed, tlSpec, tlXa, tlYa, tlZa, tlOrig);
                            placedTrafficLights?.Add(tlOrig);
                        }
                    }
                }
            }
        }

        private static bool SampleSidewalkPointAtDistance(
            Vector3[] verts,
            float[] cumDist,
            int rows,
            int stride,
            int curbOffset,
            int outerOffset,
            float targetDist,
            float sidewalkLerp,
            out Vector3 origAge,
            out Vector3 xaAge,
            out Vector3 yaAge,
            out Vector3 zaAge
        )
        {
            origAge = Vector3.zero;
            xaAge = Vector3.right;
            yaAge = Vector3.up;
            zaAge = Vector3.forward;

            int seg = 0;
            while (seg + 1 < rows - 1 && cumDist[seg + 1] < targetDist)
            {
                seg++;
            }

            float segStart = cumDist[seg];
            float segLen = cumDist[seg + 1] - segStart;
            if (segLen < 0.05f) return false;

            float u = Mathf.Clamp01((targetDist - segStart) / segLen);

            Vector3 c0 = verts[seg * stride + curbOffset];
            Vector3 c1 = verts[(seg + 1) * stride + curbOffset];
            Vector3 o0 = verts[seg * stride + outerOffset];
            Vector3 o1 = verts[(seg + 1) * stride + outerOffset];

            Vector3 curbPt = Vector3.Lerp(c0, c1, u);
            Vector3 outerPt = Vector3.Lerp(o0, o1, u);

            Vector3 outward = new Vector3(outerPt.x - curbPt.x, 0f, outerPt.z - curbPt.z);
            if (outward.sqrMagnitude < 0.15f) return false;
            outward.Normalize();

            origAge = Vector3.Lerp(curbPt, outerPt, sidewalkLerp);
            origAge.y = curbPt.y + SidewalkOffsetHeight;

            // Point +X (xaAge) from the curb toward the outer sidewalk so -X (streetlamp overhanging arm) extends over the street
            xaAge = outward;
            yaAge = Vector3.up;
            zaAge = new Vector3(-xaAge.z, 0f, xaAge.x);
            return true;
        }

        private static Dictionary<string, PropDefEntry> ParsePropDefsCsv(string virtualPath)
        {
            var dict = new Dictionary<string, PropDefEntry>(StringComparer.OrdinalIgnoreCase);
            string text = MM2ArchiveSystem.ReadFileText(virtualPath);
            if (string.IsNullOrEmpty(text)) return dict;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("type,", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] cols = line.Split(',');
                if (cols.Length < 7) continue;

                string typeName = cols[0].Trim();
                if (string.IsNullOrEmpty(typeName)) continue;

                float.TryParse(cols[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float start);
                float.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float dist);
                int.TryParse(cols[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxUse);
                float.TryParse(cols[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float minL);
                float.TryParse(cols[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float maxL);

                var entry = new PropDefEntry
                {
                    Name = typeName,
                    Start = start,
                    Distance = dist > 0f ? dist : 25f,
                    MaxUse = maxUse > 0 ? maxUse : 1,
                    MinLerp = minL,
                    MaxLerp = maxL
                };

                for (int c = 6; c < cols.Length; c++)
                {
                    string f = cols[c].Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(f))
                    {
                        entry.PkgFiles.Add(f);
                    }
                }

                if (entry.PkgFiles.Count > 0)
                {
                    dict[typeName] = entry;
                }
            }

            return dict;
        }

        private static Dictionary<string, List<string>> ParsePropRulesCsv(string virtualPath)
        {
            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string text = MM2ArchiveSystem.ReadFileText(virtualPath);
            if (string.IsNullOrEmpty(text)) return dict;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("rulename", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] cols = line.Split(',');
                if (cols.Length < 2) continue;

                string ruleName = cols[0].Trim();
                if (string.IsNullOrEmpty(ruleName)) continue;

                var list = new List<string>();
                for (int c = 1; c < cols.Length; c++)
                {
                    string p = cols[c].Trim();
                    if (!string.IsNullOrEmpty(p))
                    {
                        list.Add(p);
                    }
                }

                if (list.Count > 0)
                {
                    dict[ruleName] = list;
                }
            }

            return dict;
        }

        private static void AppendPkgInstanceToChunk(
            RuntimeDistrictChunk chunk,
            MM2RuntimePkgLoader.ParsedPkg tmpl,
            int variant,
            bool addCollider,
            Vector3 xa,
            Vector3 ya,
            Vector3 za,
            Vector3 orig
        )
        {
            List<MM2RuntimePkgLoader.PkgSection> sections = null;
            string[] lodOrder = { "H", "M", "L", "VL", "BODY_H", "FACADE_H" };
            foreach (string lod in lodOrder)
            {
                if (tmpl.GeometryChunks.TryGetValue(lod, out sections)) break;
            }
            if (sections == null) return;

            int pj = tmpl.NumPaintjobs > 0 ? (variant % tmpl.NumPaintjobs) : 0;
            int pjBase = pj * tmpl.ShadersPerPaintjob;

            foreach (var sec in sections)
            {
                int shIdx = pjBase + sec.ShaderOffset;
                string tName = (shIdx >= 0 && shIdx < tmpl.Shaders.Count)
                    ? CleanTexName(tmpl.Shaders[shIdx].TextureName)
                    : string.Empty;
                if (string.IsNullOrEmpty(tName))
                {
                    if (shIdx >= 0 && shIdx < tmpl.Shaders.Count)
                    {
                        Color32 dc = tmpl.Shaders[shIdx].DiffuseColor;
                        tName = $"__color_{dc.r:x2}{dc.g:x2}{dc.b:x2}";
                    }
                    else
                    {
                        continue;
                    }
                }

                bool isBridgeUnderside = tName.IndexOf("_bot_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         tName.IndexOf("underside", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isFoliageTex = tName.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tName.IndexOf("palm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tName.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isWaterTex = IsWaterSurfaceTexture(tName);
                bool addSecCollider = addCollider && !isBridgeUnderside && !isWaterTex;

                if (!chunk.VisualByTexture.TryGetValue(tName, out RuntimeSubmeshBucket bucket))
                {
                    bucket = new RuntimeSubmeshBucket();
                    chunk.VisualByTexture[tName] = bucket;
                }

                int baseV = bucket.Positions.Count;
                int baseColV = chunk.ColPositions.Count;
                int vCount = sec.Positions.Count;

                for (int i = 0; i < vCount; i++)
                {
                    Vector3 pt = sec.Positions[i];
                    Vector3 wx = new Vector3(
                        orig.x + pt.x * xa.x + pt.y * ya.x + pt.z * za.x,
                        orig.y + pt.x * xa.y + pt.y * ya.y + pt.z * za.y,
                        orig.z + pt.x * xa.z + pt.y * ya.z + pt.z * za.z
                    );
                    Vector3 uPos = new Vector3(-wx.x, wx.y, wx.z);
                    if (isBridgeUnderside)
                    {
                        // In MM2 bridge .pkg files (e.g. pl_ggbridge_sanfran_mid_f), the downward-facing underside truss
                        // is authored slightly above the PSDL road deck Y; offset it below the road deck so it never
                        // pokes through or renders on top of the road surface.
                        uPos.y -= 0.85f;
                    }

                    Vector3 d = sec.Normals[i];
                    Vector3 wn = new Vector3(
                        d.x * xa.x + d.y * ya.x + d.z * za.x,
                        d.x * xa.y + d.y * ya.y + d.z * za.y,
                        d.x * xa.z + d.y * ya.z + d.z * za.z
                    );
                    Vector3 uNorm = new Vector3(-wn.x, wn.y, wn.z).normalized;
                    if (isFoliageTex)
                    {
                        uNorm = new Vector3(uNorm.x * 0.15f, 0.98f, uNorm.z * 0.15f).normalized;
                    }

                    bucket.Positions.Add(uPos);
                    bucket.Normals.Add(uNorm);
                    bucket.UVs.Add(sec.UVs[i]);

                    if (addSecCollider) chunk.ColPositions.Add(uPos);
                }

                int baseTriStart = bucket.Triangles.Count;
                for (int i = 0; i + 2 < sec.Triangles.Count; i += 3)
                {
                    // Reverse winding for left-handed X flip (-wx.x)
                    int i0 = sec.Triangles[i + 2];
                    int i1 = sec.Triangles[i + 1];
                    int i2 = sec.Triangles[i];
                    bucket.Triangles.Add(baseV + i0);
                    bucket.Triangles.Add(baseV + i1);
                    bucket.Triangles.Add(baseV + i2);

                    if (addSecCollider)
                    {
                        chunk.ColTriangles.Add(baseColV + i0);
                        chunk.ColTriangles.Add(baseColV + i1);
                        chunk.ColTriangles.Add(baseColV + i2);
                    }
                }

                if (isWaterTex)
                {
                    var secWaterTris = new List<int>(sec.Triangles.Count);
                    for (int i = baseTriStart; i < bucket.Triangles.Count; i++)
                    {
                        secWaterTris.Add(bucket.Triangles[i]);
                    }
                    RecordWaterTriangles(bucket.Positions, secWaterTris);
                }
            }
        }

        private static void BuildRuntimeSkyDome(Transform cityRoot, string mapName = "sf")
        {
            string preferredSkyPkg = string.Equals(mapName, "london", StringComparison.OrdinalIgnoreCase) ? "sky_dome_l" : "sky_dome";
            MM2RuntimePkgLoader.ParsedPkg skyPkg = MM2RuntimePkgLoader.LoadPkg(preferredSkyPkg) ?? MM2RuntimePkgLoader.LoadPkg("sky_dome");
            List<MM2RuntimePkgLoader.PkgSection> sections = null;
            List<MM2RuntimePkgLoader.PkgShaderEntry> shaders = null;
            bool isMm1SkyMesh = false;

            if (skyPkg != null && skyPkg.GeometryChunks.TryGetValue("H", out sections))
            {
                shaders = skyPkg.Shaders;
            }
            else
            {
                byte[] mmSkyBytes = MM2ArchiveSystem.ReadFileBytes("bms/mmsky.bms");
                if (MM2RuntimePkgLoader.TryParseBmsMesh(mmSkyBytes, out _, out shaders, out sections))
                {
                    isMm1SkyMesh = true;
                }
            }

            if (sections == null || sections.Count == 0) return;

            GameObject skyGo = new GameObject("Runtime_SkyDome");
            skyGo.transform.SetParent(cityRoot, false);
            skyGo.transform.position = new Vector3(0f, -8f, 0f);
            // Keep radius (~730m) smaller than fogStartDistance so linear fog never washes out the sky texture,
            // while rendering at RenderQueue.Background (1000) with ZWrite Off so all distant city geometry draws in front.
            skyGo.transform.localScale = new Vector3(17f, 10f, 17f);

            var pos = new List<Vector3>();
            var norm = new List<Vector3>();
            var uv = new List<Vector2>();
            var tris = new List<int>();
            string skyTexName = string.Equals(mapName, "chicago", StringComparison.OrdinalIgnoreCase) ? "sky_mc" : "sky_ca_f";
            float coordScale = isMm1SkyMesh ? 4.3f : 1.0f;

            foreach (var sec in sections)
            {
                if (!string.Equals(mapName, "chicago", StringComparison.OrdinalIgnoreCase) &&
                    shaders != null && sec.ShaderOffset >= 0 && sec.ShaderOffset < shaders.Count &&
                    !string.IsNullOrEmpty(shaders[sec.ShaderOffset].TextureName))
                {
                    skyTexName = CleanTexName(shaders[sec.ShaderOffset].TextureName);
                }
                int bV = pos.Count;
                for (int i = 0; i < sec.Positions.Count; i++)
                {
                    Vector3 p = sec.Positions[i] * coordScale;
                    pos.Add(new Vector3(-p.x, p.y, p.z));
                    norm.Add(Vector3.up);
                    uv.Add(sec.UVs[i]);
                }
                for (int i = 0; i + 2 < sec.Triangles.Count; i += 3)
                {
                    tris.Add(bV + sec.Triangles[i + 2]);
                    tris.Add(bV + sec.Triangles[i + 1]);
                    tris.Add(bV + sec.Triangles[i]);
                }
            }

            Mesh m = new Mesh { name = "Runtime_SkyDome" };
            m.SetVertices(pos);
            m.SetNormals(norm);
            m.SetUVs(0, uv);
            m.SetTriangles(tris, 0);
            m.bounds = new Bounds(Vector3.zero, new Vector3(1000f, 1000f, 1000f));

            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture") ?? Shader.Find("Standard");
            Material mat = new Material(unlit) { name = "Mat_Runtime_SkyDome" };
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = (int)RenderQueue.Background;

            var texInfo = MM2RuntimeDecoders.LoadTexture(skyTexName);
            if (texInfo.Texture != null)
            {
                mat.mainTexture = texInfo.Texture;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texInfo.Texture);
            }

            skyGo.AddComponent<MeshFilter>().sharedMesh = m;
            var mr = skyGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // Keep SkyDome centered on the active camera and configure long-distance fog/clip planes
            skyGo.AddComponent<MM2SkyDomeFollower>();
        }

        private static void BuildRuntimeHorizonWater(
            Transform cityRoot,
            float waterLevelY = -2.0f,
            float cityMinX = -2000f,
            float cityMaxX = 2000f,
            float cityMinZ = -2000f,
            float cityMaxZ = 2000f,
            float minCityVertY = -20f
        )
        {
            GameObject waterGo = new GameObject("Runtime_HorizonWater");
            waterGo.transform.SetParent(cityRoot, false);
            waterGo.transform.position = Vector3.zero;

            const float S = 25000f;
            float outerY = waterLevelY - 0.05f;
            // Keep the inner city fallback plane below the lowest tunnel/underpass so underground tunnels below sea level never flood visually
            float innerY = Mathf.Min(outerY, minCityVertY - 0.5f);

            Mesh wm = new Mesh { name = "Runtime_HorizonWater_Mesh" };
            var vList = new List<Vector3>(20);
            var nList = new List<Vector3>(20);
            var uvList = new List<Vector2>(20);
            var tList = new List<int>(30);

            void AddWaterQuad(float x0, float z0, float x1, float z1, float y)
            {
                if (x1 <= x0 || z1 <= z0) return;
                int b = vList.Count;
                vList.Add(new Vector3(x0, y, z0));
                vList.Add(new Vector3(x0, y, z1));
                vList.Add(new Vector3(x1, y, z1));
                vList.Add(new Vector3(x1, y, z0));
                for (int i = 0; i < 4; i++) nList.Add(Vector3.up);
                const float uvScale = 800f / (2f * S);
                uvList.Add(new Vector2((x0 + S) * uvScale, (z0 + S) * uvScale));
                uvList.Add(new Vector2((x0 + S) * uvScale, (z1 + S) * uvScale));
                uvList.Add(new Vector2((x1 + S) * uvScale, (z1 + S) * uvScale));
                uvList.Add(new Vector2((x1 + S) * uvScale, (z0 + S) * uvScale));
                tList.Add(b); tList.Add(b + 1); tList.Add(b + 2);
                tList.Add(b); tList.Add(b + 2); tList.Add(b + 3);
            }

            if (innerY < outerY - 0.1f && cityMinX > -S && cityMaxX < S && cityMinZ > -S && cityMaxZ < S)
            {
                // 4 outer horizon quads around the city perimeter at sea level, plus inner quad below tunnels
                AddWaterQuad(-S, -S, S, cityMinZ, outerY);
                AddWaterQuad(-S, cityMaxZ, S, S, outerY);
                AddWaterQuad(-S, cityMinZ, cityMinX, cityMaxZ, outerY);
                AddWaterQuad(cityMaxX, cityMinZ, S, cityMaxZ, outerY);
                AddWaterQuad(cityMinX, cityMinZ, cityMaxX, cityMaxZ, innerY);
            }
            else
            {
                AddWaterQuad(-S, -S, S, S, outerY);
            }

            wm.SetVertices(vList);
            wm.SetNormals(nList);
            wm.SetUVs(0, uvList);
            wm.SetTriangles(tList, 0);
            wm.RecalculateBounds();

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material wMat = new Material(urpLit) { name = "Mat_Runtime_HorizonWater" };
            if (wMat.HasProperty("_Cull")) wMat.SetFloat("_Cull", 2f);
            if (wMat.HasProperty("_Smoothness")) wMat.SetFloat("_Smoothness", 0.2f);
            var waterTex = MM2RuntimeDecoders.LoadTexture("s_water");
            if (waterTex.Texture != null)
            {
                wMat.mainTexture = waterTex.Texture;
                if (wMat.HasProperty("_BaseMap")) wMat.SetTexture("_BaseMap", waterTex.Texture);
            }
            else if (wMat.HasProperty("_BaseColor"))
            {
                wMat.SetColor("_BaseColor", new Color(0.16f, 0.36f, 0.45f, 1f));
            }

            waterGo.AddComponent<MeshFilter>().sharedMesh = wm;
            var wmr = waterGo.AddComponent<MeshRenderer>();
            wmr.sharedMaterial = wMat;
            wmr.shadowCastingMode = ShadowCastingMode.Off;
        }

        // =====================================================================
        // PSDL ELEMENT GEOMETRY HELPERS
        // =====================================================================
        private static string CleanTexName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string s = name.Trim().ToLowerInvariant();
            int dash = s.IndexOf('-');
            return dash >= 0 ? s.Substring(0, dash) : s;
        }

        private static Vector3 FlipV(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        private static float FlatDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static Vector2 WorldspaceUV(Vector3 v, float ds, float dt)
        {
            return new Vector2(v.x * 0.125f - ds, v.z * 0.125f - dt);
        }

        private static Vector2[] StripMap(Vector3[] verts, int stride, int leftOff, int rightOff, float lenScale = 1f, float widScale = 1f)
        {
            int rows = verts.Length / stride;
            var buf = new Vector2[rows * 2];
            float lastV = 0f;
            for (int r = 0; r < rows - 1; r++)
            {
                int i = r * stride;
                Vector3 lv = verts[i + leftOff];
                Vector3 rv = verts[i + rightOff];
                Vector3 lvn = verts[i + leftOff + stride];
                float len = Vector3.Distance(lv, lvn);
                float wid = FlatDist(lv, rv);
                buf[r * 2] = new Vector2(lastV, 0f);
                buf[r * 2 + 1] = new Vector2(lastV, widScale);
                if (wid > 1e-5f) lastV += (len / wid) * lenScale;
            }
            buf[(rows - 1) * 2] = new Vector2(lastV, 0f);
            buf[(rows - 1) * 2 + 1] = new Vector2(lastV, widScale);
            return buf;
        }

        private static void WriteStripQuad(List<int> tris, int b, int n)
        {
            tris.Add(b); tris.Add(n); tris.Add(n + 1);
            tris.Add(b); tris.Add(n + 1); tris.Add(b + 1);
        }

        private static void AddSubmeshToChunk(
            RuntimeDistrictChunk chunk,
            Func<int, string> getTex,
            int texIdx,
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] sdlUVs,
            List<int> tris,
            bool addCollider
        )
        {
            if (tris == null || tris.Count == 0) return;

            string tName = getTex(texIdx);
            if (string.IsNullOrEmpty(tName)) return;

            bool isWater = IsWaterSurfaceTexture(tName);
            if (isWater)
            {
                RecordWaterTriangles(positions, tris);
            }
            else if (addCollider)
            {
                int baseC = chunk.ColPositions.Count;
                chunk.ColPositions.AddRange(positions);
                for (int i = 0; i < tris.Count; i++) chunk.ColTriangles.Add(baseC + tris[i]);
            }

            if (!chunk.VisualByTexture.TryGetValue(tName, out RuntimeSubmeshBucket bucket))
            {
                bucket = new RuntimeSubmeshBucket();
                chunk.VisualByTexture[tName] = bucket;
            }

            int baseV = bucket.Positions.Count;
            bucket.Positions.AddRange(positions);
            bucket.Normals.AddRange(normals);
            for (int i = 0; i < sdlUVs.Length; i++)
            {
                bucket.UVs.Add(new Vector2(sdlUVs[i].x, 1f - sdlUVs[i].y));
            }
            for (int i = 0; i < tris.Count; i++)
            {
                bucket.Triangles.Add(baseV + tris[i]);
            }
        }

        private static void BuildPsdlFacadeBound(RuntimeDistrictChunk chunk, Vector3 lv, Vector3 rv, float h)
        {
            if (h <= lv.y || h <= rv.y) return;
            int b = chunk.ColPositions.Count;
            chunk.ColPositions.Add(new Vector3(-lv.x, lv.y, lv.z));
            chunk.ColPositions.Add(new Vector3(-rv.x, rv.y, rv.z));
            chunk.ColPositions.Add(new Vector3(-lv.x, h, lv.z));
            chunk.ColPositions.Add(new Vector3(-rv.x, h, rv.z));
            // Emit both windings (double-sided collision wall) so outward-facing and inward-facing bounds both block vehicles
            WriteStripQuad(chunk.ColTriangles, b, b + 2);
            chunk.ColTriangles.Add(b);
            chunk.ColTriangles.Add(b + 1);
            chunk.ColTriangles.Add(b + 2);
            chunk.ColTriangles.Add(b + 1);
            chunk.ColTriangles.Add(b + 3);
            chunk.ColTriangles.Add(b + 2);
        }

        private static void BuildPsdlTriangleFan(
            RuntimeDistrictChunk chunk,
            Func<int, string> getTex,
            int tid,
            Vector3[] ev,
            bool overrideHeight,
            float h
        )
        {
            if (ev.Length < 3 || tid == 65535) return;

            if (!overrideHeight && IsWaterSurfaceTexture(getTex(tid)))
            {
                float minFanY = float.MaxValue;
                float maxFanY = float.MinValue;
                for (int i = 0; i < ev.Length; i++)
                {
                    if (ev[i].y < minFanY) minFanY = ev[i].y;
                    if (ev[i].y > maxFanY) maxFanY = ev[i].y;
                }
                if (maxFanY - minFanY > 0.5f)
                {
                    // Flatten coastal water fans that share elevated highway/seawall perimeter vertices down to sea level
                    overrideHeight = true;
                    h = minFanY;
                }
            }

            float ds = Mathf.Floor(-ev[0].x * 0.125f);
            float dt = Mathf.Floor(ev[0].z * 0.125f);

            var pos = new Vector3[ev.Length];
            var norms = new Vector3[ev.Length];
            var uvs = new Vector2[ev.Length];
            for (int i = 0; i < ev.Length; i++)
            {
                pos[i] = new Vector3(-ev[i].x, overrideHeight ? h : ev[i].y, ev[i].z);
                norms[i] = Vector3.up;
                uvs[i] = WorldspaceUV(pos[i], ds, dt);
            }
            var tris = new List<int>((ev.Length - 2) * 3);
            for (int i = 1; i < ev.Length - 1; i++)
            {
                tris.Add(0); tris.Add(i + 1); tris.Add(i);
            }
            AddSubmeshToChunk(chunk, getTex, tid, pos, norms, uvs, tris, true);
        }

        private static void BuildPsdlFacade(
            RuntimeDistrictChunk chunk,
            Func<int, string> getTex,
            int tid,
            Vector3 lv,
            Vector3 rv,
            float bh,
            float th,
            float ut,
            float vt
        )
        {
            if (ut > 128f || vt > 128f || bh >= th || tid == 65535) return;
            Vector3 br = new Vector3(-rv.x, bh, rv.z);
            Vector3 bl = new Vector3(-lv.x, bh, lv.z);
            Vector3 tl = new Vector3(-lv.x, th, lv.z);
            Vector3 tr = new Vector3(-rv.x, th, rv.z);
            Vector3 n = Vector3.Cross(bl - br, tl - br).normalized;

            var pos = new[] { br, bl, tl, tr };
            var norms = new[] { n, n, n, n };
            var uvs = new[] { new Vector2(ut, vt), new Vector2(0f, vt), new Vector2(0f, 0f), new Vector2(ut, 0f) };
            var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
            AddSubmeshToChunk(chunk, getTex, tid, pos, norms, uvs, tris, true);
        }

        private static void BuildPsdlSliver(
            RuntimeDistrictChunk chunk,
            Func<int, string> getTex,
            int tid,
            Vector3 lv,
            Vector3 rv,
            float h,
            float scale
        )
        {
            if ((lv.y > h && rv.y > h) || tid == 65535) return;
            Vector3 left = FlipV(lv);
            Vector3 right = FlipV(rv);
            Vector3 tl = new Vector3(left.x, h, left.z);
            Vector3 tr = new Vector3(right.x, h, right.z);
            bool lClip = left.y >= h;
            bool rClip = right.y >= h;
            float u = Mathf.Floor(FlatDist(left, right) * scale + 0.5f);
            float v1 = (tl.y - left.y) * scale;
            float v2 = (tr.y - right.y) * scale;
            Vector3 n = Vector3.Cross(Vector3.up, right - left).normalized;

            Vector3[] pos;
            Vector3[] norms;
            Vector2[] uvs;
            List<int> tris;
            if (rClip)
            {
                pos = new[] { left, tl, tr };
                norms = new[] { n, n, n };
                uvs = new[] { new Vector2(0f, v1), new Vector2(0f, 0f), new Vector2(u, 0f) };
                tris = new List<int> { 0, 1, 2 };
            }
            else if (lClip)
            {
                pos = new[] { tl, tr, right };
                norms = new[] { n, n, n };
                uvs = new[] { new Vector2(0f, 0f), new Vector2(u, 0f), new Vector2(u, v2) };
                tris = new List<int> { 0, 1, 2 };
            }
            else
            {
                pos = new[] { right, left, tl, tr };
                norms = new[] { n, n, n, n };
                uvs = new[] { new Vector2(u, v2), new Vector2(0f, v1), new Vector2(0f, 0f), new Vector2(u, 0f) };
                tris = new List<int> { 0, 1, 2, 0, 2, 3 };
            }
            AddSubmeshToChunk(chunk, getTex, tid, pos, norms, uvs, tris, true);
        }

        private static void BuildPsdlWalkway(RuntimeDistrictChunk chunk, Func<int, string> getTex, int tid, Vector3[] ev)
        {
            if (ev.Length < 4 || tid == 65535) return;
            int rowCount = ev.Length / 2;
            Vector2[] surfUVs = StripMap(ev, 2, 0, 1);
            var pos = new Vector3[rowCount * 2];
            var norms = new Vector3[rowCount * 2];
            for (int i = 0; i < pos.Length; i++)
            {
                pos[i] = FlipV(ev[i]);
                norms[i] = Vector3.up;
            }
            var tris = new List<int>((rowCount - 1) * 6);
            for (int i = 0; i < rowCount - 1; i++) WriteStripQuad(tris, i * 2, (i + 1) * 2);
            AddSubmeshToChunk(chunk, getTex, tid, pos, norms, surfUVs, tris, true);
        }

        private static void BuildPsdlCrosswalk(RuntimeDistrictChunk chunk, Func<int, string> getTex, int tid, Vector3[] ev)
        {
            if (ev.Length < 4 || tid == 65535) return;
            float width = FlatDist(ev[0], ev[1]);
            float length = Vector3.Distance(ev[0], ev[2]) + Vector3.Distance(ev[1], ev[3]);
            float uvV = width > 1e-5f ? (length / width * 0.5f) : 1f;
            var pos = new[] { FlipV(ev[0]), FlipV(ev[1]), FlipV(ev[3]), FlipV(ev[2]) };
            var norms = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            var uvs = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, uvV), new Vector2(0f, uvV) };
            var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
            AddSubmeshToChunk(chunk, getTex, tid + 2, pos, norms, uvs, tris, true);
        }

        private static void BuildPsdlSidewalk(
            RuntimeDistrictChunk chunk,
            Func<int, string> getTex,
            int tid,
            Vector3[] ev,
            bool isStart,
            bool isEnd
        )
        {
            if (ev == null || ev.Length == 0 || tid == 65535) return;
            float ds = Mathf.Floor(-ev[0].x * 0.125f);
            float dt = Mathf.Floor(ev[0].z * 0.125f);

            if (isStart || isEnd)
            {
                if (ev.Length < 2) return;
                Vector3 c0 = FlipV(ev[0]);
                Vector3 c1 = FlipV(ev[1]);
                Vector3 c2 = new Vector3(c0.x, c0.y + SidewalkOffsetHeight, c0.z);
                var capPos = new[] { c0, c1, c2 };
                var capNorms = new[] { Vector3.up, Vector3.up, Vector3.up };
                var capUVs = new[] { WorldspaceUV(c0, ds, dt), WorldspaceUV(c1, ds, dt), WorldspaceUV(c2, ds, dt) };
                var capTris = isEnd ? new List<int> { 2, 1, 0 } : new List<int> { 0, 1, 2 };
                AddSubmeshToChunk(chunk, getTex, tid + 1, capPos, capNorms, capUVs, capTris, true);
                return;
            }

            if (ev.Length < 4) return;
            int rowCount = ev.Length / 2;
            int surfVc = rowCount * 2;
            int totalVc = surfVc + rowCount * 2;
            var pos = new Vector3[totalVc];
            var norms = new Vector3[totalVc];
            var uvs = new Vector2[totalVc];

            for (int i = 0; i < surfVc; i++)
            {
                Vector3 p = FlipV(ev[i]);
                if ((i & 1) == 0) p.y += SidewalkOffsetHeight;
                pos[i] = p;
                norms[i] = Vector3.up;
                uvs[i] = WorldspaceUV(p, ds, dt);
            }

            int curbStart = surfVc;
            for (int r = 0; r < rowCount; r++)
            {
                Vector3 bot = FlipV(ev[r * 2]);
                Vector3 top = new Vector3(bot.x, bot.y + SidewalkOffsetHeight, bot.z);
                Vector2 uv = WorldspaceUV(bot, ds, dt);
                int idx = curbStart + r * 2;
                pos[idx] = bot;
                pos[idx + 1] = top;
                norms[idx] = Vector3.up;
                norms[idx + 1] = Vector3.up;
                uvs[idx] = uv;
                uvs[idx + 1] = uv;
            }

            var tris = new List<int>((surfVc - 2) * 3 + (rowCount - 1) * 6);
            bool flip = true;
            for (int i = 2; i < surfVc; i++)
            {
                if (flip) { tris.Add(i); tris.Add(i - 1); tris.Add(i - 2); }
                else { tris.Add(i - 2); tris.Add(i - 1); tris.Add(i); }
                flip = !flip;
            }
            for (int i = 0; i < rowCount - 1; i++)
            {
                int b = curbStart + i * 2;
                int n = b + 2;
                tris.Add(b); tris.Add(n + 1); tris.Add(b + 1);
                tris.Add(n); tris.Add(n + 1); tris.Add(b);
            }
            AddSubmeshToChunk(chunk, getTex, tid + 1, pos, norms, uvs, tris, true);
        }

        private static void BuildPsdlRoad(RuntimeDistrictChunk chunk, Func<int, string> getTex, int tid, Vector3[] ev)
        {
            if (ev.Length < 8 || tid == 65535) return;
            int rowCount = ev.Length / 4;
            Vector2[] surfUVs = StripMap(ev, 4, 1, 2);
            Vector2[] swLUVs = StripMap(ev, 4, 0, 1);
            Vector2[] swRUVs = StripMap(ev, 4, 2, 3);

            var pos = new Vector3[rowCount * 11];
            var norms = new Vector3[rowCount * 11];
            var uvs = new Vector2[rowCount * 11];

            for (int i = 0; i < rowCount; i++)
            {
                int ib = i * 4;
                int sb = i * 2;
                int ob = i * 11;
                Vector3 sll = FlipV(ev[ib]);
                Vector3 slr = FlipV(ev[ib + 1]);
                if (Vector3.SqrMagnitude(ev[ib] - ev[ib + 1]) > 1e-6f) slr.y += SidewalkOffsetHeight;
                Vector3 rl = FlipV(ev[ib + 1]);
                Vector3 rr = FlipV(ev[ib + 2]);
                Vector3 rc = Vector3.Lerp(rl, rr, 0.5f);
                Vector3 srl = FlipV(ev[ib + 2]);
                Vector3 srr = FlipV(ev[ib + 3]);
                if (Vector3.SqrMagnitude(ev[ib + 2] - ev[ib + 3]) > 1e-6f) srl.y += SidewalkOffsetHeight;

                pos[ob] = sll; pos[ob + 1] = slr; pos[ob + 2] = slr; pos[ob + 3] = rl;
                pos[ob + 4] = rl; pos[ob + 5] = rc; pos[ob + 6] = rr;
                pos[ob + 7] = rr; pos[ob + 8] = srl; pos[ob + 9] = srl; pos[ob + 10] = srr;

                uvs[ob] = swLUVs[sb]; uvs[ob + 1] = swLUVs[sb + 1]; uvs[ob + 2] = swLUVs[sb + 1]; uvs[ob + 3] = swLUVs[sb + 1];
                uvs[ob + 4] = surfUVs[sb]; uvs[ob + 5] = surfUVs[sb + 1]; uvs[ob + 6] = surfUVs[sb];
                uvs[ob + 7] = swRUVs[sb + 1]; uvs[ob + 8] = swRUVs[sb + 1]; uvs[ob + 9] = swRUVs[sb + 1]; uvs[ob + 10] = swRUVs[sb];

                for (int k = 0; k < 11; k++) norms[ob + k] = Vector3.up;
            }

            var swTris = new List<int>((rowCount - 1) * 24);
            var surfTris = new List<int>((rowCount - 1) * 12);
            for (int i = 0; i < rowCount - 1; i++)
            {
                int b = i * 11;
                int n = b + 11;
                WriteStripQuad(swTris, b, n);
                WriteStripQuad(swTris, b + 2, n + 2);
                WriteStripQuad(swTris, b + 7, n + 7);
                WriteStripQuad(swTris, b + 9, n + 9);
                WriteStripQuad(surfTris, b + 4, n + 4);
                WriteStripQuad(surfTris, b + 5, n + 5);
            }

            AddSubmeshToChunk(chunk, getTex, tid, pos, norms, uvs, surfTris, true);
            AddSubmeshToChunk(chunk, getTex, tid + 1, pos, norms, uvs, swTris, true);
        }

        private static void BuildPsdlDividedRoad(
            RuntimeDistrictChunk chunk,
            Func<int, string> getTex,
            int tid,
            Vector3[] ev,
            int divFlags,
            int divType,
            int divTex,
            ushort divVal
        )
        {
            if (ev == null || ev.Length < 12 || tid == 65535) return;
            int rowCount = ev.Length / 6;
            int spanCount = rowCount - 1;
            bool closedStart = (divFlags & 16) != 0;
            bool closedEnd = (divFlags & 32) != 0;
            bool capsSupp = (divType == 2 || divType == 3);
            bool haveStartCap = capsSupp && closedStart;
            bool haveEndCap = capsSupp && closedEnd;

            Vector2[] surfUVs = StripMap(ev, 6, 1, 4);
            Vector2[] swLUVs = StripMap(ev, 6, 0, 1);
            Vector2[] swRUVs = StripMap(ev, 6, 4, 5);

            var pos = new List<Vector3>(rowCount * 22);
            var uvs = new List<Vector2>(rowCount * 22);

            for (int i = 0; i < rowCount; i++)
            {
                int s = i * 6;
                int u = i * 2;
                Vector3 sll = FlipV(ev[s]);
                Vector3 slr = FlipV(ev[s + 1]);
                if (Vector3.SqrMagnitude(ev[s] - ev[s + 1]) > 1e-6f) slr.y += SidewalkOffsetHeight;
                Vector3 rll = FlipV(ev[s + 1]);
                Vector3 rlr = FlipV(ev[s + 2]);
                Vector3 rrl = FlipV(ev[s + 3]);
                Vector3 rrr = FlipV(ev[s + 4]);
                Vector3 srl = FlipV(ev[s + 4]);
                Vector3 srr = FlipV(ev[s + 5]);
                if (Vector3.SqrMagnitude(ev[s + 4] - ev[s + 5]) > 1e-6f) srl.y += SidewalkOffsetHeight;

                pos.Add(sll); pos.Add(slr); pos.Add(slr); pos.Add(rll);
                pos.Add(rll); pos.Add(rlr); pos.Add(rrl); pos.Add(rrr);
                pos.Add(rrr); pos.Add(srl); pos.Add(srl); pos.Add(srr);

                uvs.Add(swLUVs[u]); uvs.Add(swLUVs[u + 1]); uvs.Add(swLUVs[u + 1]); uvs.Add(swLUVs[u + 1]);
                uvs.Add(surfUVs[u]); uvs.Add(surfUVs[u + 1]); uvs.Add(surfUVs[u + 1]); uvs.Add(surfUVs[u]);
                uvs.Add(swRUVs[u + 1]); uvs.Add(swRUVs[u + 1]); uvs.Add(swRUVs[u + 1]); uvs.Add(swRUVs[u]);
            }

            int divVertOff = pos.Count;
            float centerH = divVal / 256f;
            if (divType == 1) // Flat median
            {
                float uvV = ((divVal & 0xFF) << 8) | ((divVal >> 8) & 0xFF);
                Vector2[] divUVs = StripMap(ev, 6, 2, 3, 1f, uvV);
                for (int i = 0; i < rowCount; i++)
                {
                    int s = i * 6;
                    int u = i * 2;
                    pos.Add(FlipV(ev[s + 2]));
                    pos.Add(FlipV(ev[s + 3]));
                    uvs.Add(divUVs[u]);
                    uvs.Add(divUVs[u + 1]);
                }
            }
            else if (divType == 3) // Wedged median
            {
                Vector2[] divUVs = StripMap(ev, 6, 2, 3);
                for (int i = 0; i < rowCount; i++)
                {
                    int s = i * 6;
                    int u = i * 2;
                    Vector3 l = FlipV(ev[s + 2]);
                    Vector3 r = FlipV(ev[s + 3]);
                    Vector3 il = Vector3.Lerp(l, r, 0.4f); il.y += centerH;
                    Vector3 ir = Vector3.Lerp(r, l, 0.4f); ir.y += centerH;
                    pos.Add(l); pos.Add(il); pos.Add(il); pos.Add(ir); pos.Add(ir); pos.Add(r);
                    uvs.Add(divUVs[u + 1]); uvs.Add(divUVs[u]); uvs.Add(divUVs[u + 1]);
                    uvs.Add(divUVs[u]); uvs.Add(divUVs[u]); uvs.Add(divUVs[u + 1]);
                }
            }
            else if (divType == 2) // Elevated median
            {
                Vector2[] divUVs = StripMap(ev, 6, 2, 3);
                for (int i = 0; i < rowCount; i++)
                {
                    int s = i * 6;
                    int u = i * 2;
                    Vector3 l = FlipV(ev[s + 2]);
                    Vector3 r = FlipV(ev[s + 3]);
                    Vector3 ml = new Vector3(l.x, l.y + centerH, l.z);
                    Vector3 mr = new Vector3(r.x, r.y + centerH, r.z);
                    Vector3 mil = Vector3.Lerp(ml, mr, 0.05f);
                    Vector3 mir = Vector3.Lerp(mr, ml, 0.05f);
                    pos.Add(l); pos.Add(ml); pos.Add(ml); pos.Add(mil); pos.Add(mil);
                    pos.Add(mir); pos.Add(mir); pos.Add(mr); pos.Add(mr); pos.Add(r);
                    uvs.Add(divUVs[u + 1]); uvs.Add(divUVs[u]); uvs.Add(divUVs[u + 1]); uvs.Add(divUVs[u]);
                    uvs.Add(divUVs[u + 1]); uvs.Add(divUVs[u]); uvs.Add(divUVs[u + 1]); uvs.Add(divUVs[u]);
                    uvs.Add(divUVs[u]); uvs.Add(divUVs[u + 1]);
                }
            }

            int capVertOff = pos.Count;
            void AddDivCap(Vector3 lSrc, Vector3 rSrc)
            {
                Vector3 l = FlipV(lSrc);
                Vector3 r = FlipV(rSrc);
                Vector3 ml = (divType == 3) ? Vector3.Lerp(l, r, 0.4f) : l;
                Vector3 mr = (divType == 3) ? Vector3.Lerp(r, l, 0.4f) : r;
                ml.y += centerH;
                mr.y += centerH;
                pos.Add(l); pos.Add(r); pos.Add(mr); pos.Add(ml);
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 1f));
                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
            }

            if (haveStartCap) AddDivCap(ev[2], ev[3]);
            if (haveEndCap)
            {
                int er = (rowCount - 1) * 6;
                AddDivCap(ev[er + 3], ev[er + 2]);
            }

            var surfTris = new List<int>(spanCount * 12);
            var swTris = new List<int>(spanCount * 24);
            for (int i = 0; i < spanCount; i++)
            {
                int b = i * 12;
                int n = b + 12;
                WriteStripQuad(swTris, b, n);
                WriteStripQuad(swTris, b + 2, n + 2);
                WriteStripQuad(swTris, b + 8, n + 8);
                WriteStripQuad(swTris, b + 10, n + 10);
                WriteStripQuad(surfTris, b + 4, n + 4);
                WriteStripQuad(surfTris, b + 6, n + 6);
            }

            var sidesTris = new List<int>();
            var stripsTris = new List<int>();
            var centerTris = new List<int>();
            if (divType == 1)
            {
                for (int i = 0; i < spanCount; i++)
                {
                    int b = divVertOff + i * 2;
                    WriteStripQuad(centerTris, b, b + 2);
                }
            }
            else if (divType == 3)
            {
                for (int i = 0; i < spanCount; i++)
                {
                    int b = divVertOff + i * 6;
                    int n = b + 6;
                    WriteStripQuad(sidesTris, b, n);
                    WriteStripQuad(sidesTris, b + 4, n + 4);
                    WriteStripQuad(centerTris, b + 2, n + 2);
                }
            }
            else if (divType == 2)
            {
                for (int i = 0; i < spanCount; i++)
                {
                    int b = divVertOff + i * 10;
                    int n = b + 10;
                    WriteStripQuad(sidesTris, b, n);
                    WriteStripQuad(sidesTris, b + 8, n + 8);
                    WriteStripQuad(stripsTris, b + 2, n + 2);
                    WriteStripQuad(stripsTris, b + 6, n + 6);
                    WriteStripQuad(centerTris, b + 4, n + 4);
                }
            }

            var capTris = new List<int>();
            int capCount = (haveStartCap ? 1 : 0) + (haveEndCap ? 1 : 0);
            for (int i = 0; i < capCount; i++)
            {
                int c = capVertOff + i * 4;
                capTris.Add(c); capTris.Add(c + 2); capTris.Add(c + 1);
                capTris.Add(c); capTris.Add(c + 3); capTris.Add(c + 2);
            }

            Vector3[] posArr = pos.ToArray();
            Vector2[] uvArr = uvs.ToArray();
            var norms = new Vector3[posArr.Length];
            for (int i = 0; i < norms.Length; i++) norms[i] = Vector3.up;

            AddSubmeshToChunk(chunk, getTex, tid, posArr, norms, uvArr, surfTris, true);
            AddSubmeshToChunk(chunk, getTex, tid + 1, posArr, norms, uvArr, swTris, true);
            if (divType == 1)
            {
                AddSubmeshToChunk(chunk, getTex, divTex, posArr, norms, uvArr, centerTris, true);
            }
            else if (divType == 3)
            {
                AddSubmeshToChunk(chunk, getTex, divTex, posArr, norms, uvArr, sidesTris, true);
                AddSubmeshToChunk(chunk, getTex, divTex + 1, posArr, norms, uvArr, centerTris, true);
            }
            else if (divType == 2)
            {
                AddSubmeshToChunk(chunk, getTex, divTex - 1, posArr, norms, uvArr, sidesTris, true);
                AddSubmeshToChunk(chunk, getTex, divTex, posArr, norms, uvArr, stripsTris, true);
                AddSubmeshToChunk(chunk, getTex, divTex + 1, posArr, norms, uvArr, centerTris, true);
            }
            if (capTris.Count > 0)
            {
                AddSubmeshToChunk(chunk, getTex, divTex + 2, posArr, norms, uvArr, capTris, true);
            }
        }

        private static Vector2[] VerticalStripMap(Vector3[] verts, int stride, int offset, float height)
        {
            int rows = verts.Length / stride;
            var buf = new Vector2[rows * 2];
            float u = 0f;
            float invH = height > 1e-6f ? 1f / height : 0f;
            for (int r = 0; r < rows - 1; r++)
            {
                int i = r * stride + offset;
                float len = FlatDist(verts[i], verts[i + stride]);
                buf[r * 2] = new Vector2(u, 1f);
                buf[r * 2 + 1] = new Vector2(u, 0f);
                u += len * invH;
            }
            buf[(rows - 1) * 2] = new Vector2(u, 1f);
            buf[(rows - 1) * 2 + 1] = new Vector2(u, 0f);
            return buf;
        }

        private static Vector3 FlatDirBetween(Vector3 a, Vector3 b)
        {
            Vector3 d = new Vector3(b.x - a.x, 0f, b.z - a.z);
            return d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.zero;
        }

        private static void BuildPsdlTunnel(
            RuntimeDistrictChunk chunk,
            Func<int, string> getTex,
            PsdlElement el,
            List<Vector3> perim,
            List<PsdlElement> roomElements,
            int eIdx
        )
        {
            ushort tFlags = el.TunnelFlags;
            float th = el.Height;
            int tid = el.TexId;
            bool isWall = (tFlags & 4) != 0;
            bool flatCeil = (tFlags & 8) != 0;
            bool curvedCeil = (tFlags & 256) != 0;
            bool curvedSides = (tFlags & 8192) != 0;
            bool doubleSided = (tFlags & 16384) != 0 && !isWall;
            float wallThick = th / 3f;
            Vector3 whVec = new Vector3(0f, th, 0f);
            Vector3 w3Vec = new Vector3(0f, wallThick, 0f);

            int TTex(int slot)
            {
                int cand = tid + slot;
                return !string.IsNullOrEmpty(getTex(cand)) ? cand : tid;
            }

            if (el.IsJunction)
            {
                int pc = perim.Count;
                if (pc < 3 || el.WallBits == 0) return;
                uint wBits = el.WallBits;
                uint inv1 = el.Inv1;
                uint inv2 = el.Inv2;
                int ceilOrig = el.CeilOrig;
                float maxPy = float.MinValue;
                for (int i = 0; i < pc; i++) if (perim[i].y > maxPy) maxPy = perim[i].y;

                var pos = new List<Vector3>();
                var uvs = new List<Vector2>();
                var innerTris = new List<int>();
                var outerTris = new List<int>();
                var underTris = new List<int>();
                var ceilTris = new List<int>();

                bool HasWBit(uint bits, int edgeI) => (bits & (1u << (edgeI + 1))) != 0;

                for (int i = 0; i < pc; i++)
                {
                    if (!HasWBit(wBits, i)) continue;
                    Vector3 vl = FlipV(perim[i]);
                    Vector3 vr = FlipV(perim[(i + 1) % pc]);
                    Vector3 vlt = vl + whVec;
                    Vector3 vrt = vr + whVec;
                    float uvEnd = th > 1e-6f ? Mathf.Max(1f, FlatDist(vl, vr) / th) : 1f;
                    int vc = pos.Count;
                    pos.Add(vl); pos.Add(vr); pos.Add(vrt); pos.Add(vlt);
                    uvs.Add(new Vector2(0f, 1f)); uvs.Add(new Vector2(uvEnd, 1f));
                    uvs.Add(new Vector2(uvEnd, 0f)); uvs.Add(new Vector2(0f, 0f));
                    innerTris.Add(vc); innerTris.Add(vc + 1); innerTris.Add(vc + 2);
                    innerTris.Add(vc); innerTris.Add(vc + 2); innerTris.Add(vc + 3);
                    if (doubleSided)
                    {
                        vc = pos.Count;
                        pos.Add(vl); pos.Add(vr); pos.Add(vrt); pos.Add(vlt);
                        uvs.Add(new Vector2(0f, 1f)); uvs.Add(new Vector2(uvEnd, 1f));
                        uvs.Add(new Vector2(uvEnd, 0f)); uvs.Add(new Vector2(0f, 0f));
                        innerTris.Add(vc + 2); innerTris.Add(vc + 1); innerTris.Add(vc);
                        innerTris.Add(vc + 3); innerTris.Add(vc + 2); innerTris.Add(vc);
                    }
                }

                if (isWall)
                {
                    for (int i = 0; i < pc; i++)
                    {
                        if (!HasWBit(wBits, i)) continue;
                        Vector3 vl = FlipV(perim[i]);
                        Vector3 vr = FlipV(perim[(i + 1) % pc]);
                        Vector3 odir = Vector3.Cross(vl - vr, Vector3.up).normalized;
                        Vector3 odirA = HasWBit(inv1, i) ? -odir : odir;
                        Vector3 odirB = HasWBit(inv2, i) ? -odir : odir;
                        Vector3 vol = vl + odirA * wallThick - w3Vec;
                        Vector3 vor = vr + odirB * wallThick - w3Vec;
                        Vector3 votl = vl + odirA * wallThick + whVec;
                        Vector3 votr = vr + odirB * wallThick + whVec;
                        Vector3 vtl = vl + whVec;
                        Vector3 vtr = vr + whVec;
                        float uvEnd = th > 1e-6f ? Mathf.Max(1f, FlatDist(vl, vr) / th) : 1f;
                        int oc = pos.Count;
                        pos.Add(vor); pos.Add(vol); pos.Add(votl); pos.Add(votr);
                        pos.Add(votr); pos.Add(votl); pos.Add(vtl); pos.Add(vtr);
                        uvs.Add(new Vector2(uvEnd, 1f)); uvs.Add(new Vector2(0f, 1f));
                        uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(uvEnd, 0f));
                        uvs.Add(new Vector2(uvEnd, 0f)); uvs.Add(new Vector2(0f, 0f));
                        uvs.Add(new Vector2(0f, 1f)); uvs.Add(new Vector2(uvEnd, 1f));
                        outerTris.Add(oc); outerTris.Add(oc + 1); outerTris.Add(oc + 2);
                        outerTris.Add(oc); outerTris.Add(oc + 2); outerTris.Add(oc + 3);
                        outerTris.Add(oc + 4); outerTris.Add(oc + 5); outerTris.Add(oc + 6);
                        outerTris.Add(oc + 4); outerTris.Add(oc + 6); outerTris.Add(oc + 7);
                    }
                    Vector3 uOrig = FlipV(perim[0]);
                    float ds = uOrig.x * 0.125f, dt = uOrig.z * 0.125f;
                    int uOff = pos.Count;
                    for (int i = 0; i < pc; i++)
                    {
                        int p = (i + ceilOrig) % pc;
                        int pn = (i + ceilOrig + 1) % pc;
                        Vector3 pv = FlipV(perim[p]); pv.y = maxPy;
                        Vector3 npv = FlipV(perim[pn]); npv.y = maxPy;
                        Vector2 uv = WorldspaceUV(pv, ds, dt);
                        Vector3 upos;
                        if (HasWBit(wBits, p) || HasWBit(wBits, pn))
                        {
                            Vector3 odir = Vector3.Cross(pv - npv, Vector3.up).normalized;
                            upos = pv + odir * wallThick - w3Vec;
                        }
                        else
                        {
                            upos = pv - w3Vec;
                        }
                        pos.Add(upos);
                        uvs.Add(uv);
                        if (i >= 2)
                        {
                            underTris.Add(uOff); underTris.Add(uOff + i - 1); underTris.Add(uOff + i);
                        }
                    }
                }

                if (flatCeil)
                {
                    Vector3 cOrig = FlipV(perim[0]);
                    float ds = cOrig.x * 0.125f, dt = cOrig.z * 0.125f;
                    int cOff = pos.Count;
                    for (int i = 0; i < pc; i++)
                    {
                        int p = (i + ceilOrig) % pc;
                        Vector3 pv = FlipV(perim[p]);
                        Vector3 cpos = new Vector3(pv.x, maxPy + th, pv.z);
                        pos.Add(cpos);
                        uvs.Add(WorldspaceUV(pv, ds, dt));
                        if (i >= 2)
                        {
                            ceilTris.Add(cOff + i - 1); ceilTris.Add(cOff + i); ceilTris.Add(cOff);
                        }
                    }
                }

                Vector3[] posArr = pos.ToArray();
                Vector2[] uvArr = uvs.ToArray();
                var norms = new Vector3[posArr.Length];
                for (int i = 0; i < norms.Length; i++) norms[i] = Vector3.up;

                AddSubmeshToChunk(chunk, getTex, TTex(0), posArr, norms, uvArr, innerTris, true);
                if (isWall)
                {
                    AddSubmeshToChunk(chunk, getTex, TTex(4), posArr, norms, uvArr, outerTris, true);
                    AddSubmeshToChunk(chunk, getTex, TTex(5), posArr, norms, uvArr, underTris, true);
                }
                if (flatCeil)
                {
                    AddSubmeshToChunk(chunk, getTex, TTex(2), posArr, norms, uvArr, ceilTris, true);
                }
            }
            else
            {
                bool leftSide = (tFlags & 1) != 0;
                bool rightSide = (tFlags & 2) != 0;
                if (!leftSide && !rightSide) return;

                PsdlElement roadEl = default;
                bool foundRoad = false;
                for (int candI = eIdx + 1; candI < Math.Min(roomElements.Count, eIdx + 3); candI++)
                {
                    int ct = roomElements[candI].Type;
                    if (ct == 0 || ct == 2 || ct == 8)
                    {
                        roadEl = roomElements[candI];
                        foundRoad = true;
                        break;
                    }
                }
                if (!foundRoad || roadEl.Verts == null || roadEl.RowCount < 2) return;

                Vector3[] rv = roadEl.Verts;
                int rb = roadEl.RowBreadth;
                int rc = roadEl.RowCount;
                int sc = rc - 1;
                bool haveCeil = flatCeil || curvedCeil;
                bool leftOuter = isWall && leftSide;
                bool rightOuter = isWall && rightSide;
                bool closedSL = (tFlags & 16) != 0;
                bool closedEL = (tFlags & 32) != 0;
                bool closedSR = (tFlags & 64) != 0;
                bool closedER = (tFlags & 128) != 0;
                int lrVc = curvedSides ? 4 : 2;
                int ceilVc = curvedCeil ? 5 : 2;

                Vector2[] uvsL = VerticalStripMap(rv, rb, 0, th);
                Vector2[] uvsR = VerticalStripMap(rv, rb, rb - 1, th);
                Vector2[] wUvsL = leftOuter ? VerticalStripMap(rv, rb, 0, wallThick) : null;
                Vector2[] wUvsR = rightOuter ? VerticalStripMap(rv, rb, rb - 1, wallThick) : null;
                Vector2[] uUvs = isWall ? StripMap(rv, rb, 0, rb - 1) : null;
                Vector2[] cUvs = haveCeil ? StripMap(rv, rb, 0, rb - 1) : null;

                var pos = new List<Vector3>();
                var uvs = new List<Vector2>();
                int lwBase = -1, rwBase = -1, loBase = -1, roBase = -1, unBase = -1, ceBase = -1;
                int vPerRow = 0;
                if (leftSide) { lwBase = vPerRow; vPerRow += lrVc * (doubleSided ? 2 : 1); }
                if (rightSide) { rwBase = vPerRow; vPerRow += lrVc * (doubleSided ? 2 : 1); }
                if (isWall)
                {
                    if (leftOuter) { loBase = vPerRow; vPerRow += 4; }
                    if (rightOuter) { roBase = vPerRow; vPerRow += 4; }
                    unBase = vPerRow; vPerRow += 2;
                }
                if (haveCeil) { ceBase = vPerRow; vPerRow += ceilVc; }

                Vector3 MitreOff(Vector3 sdir, bool isStart, bool isLeft)
                {
                    float sign = (isStart ? 1f : -1f) * (isLeft ? 1f : -1f);
                    return Vector3.Cross(Vector3.up, sdir) * (wallThick * 1.414f * sign);
                }

                for (int i = 0; i < rc; i++)
                {
                    int vb = i * rb;
                    int uvb = i * 2;
                    Vector3 lv = FlipV(rv[vb]);
                    Vector3 rrv = FlipV(rv[vb + rb - 1]);
                    Vector3 ltv = lv + whVec;
                    Vector3 rtv = rrv + whVec;
                    Vector2 uvBl = uvsL[uvb], uvTl = uvsL[uvb + 1];
                    Vector2 uvBr = uvsR[uvb], uvTr = uvsR[uvb + 1];
                    Vector3 lSdir = -FlatDirBetween(lv, FlipV(rv[vb + 1]));
                    Vector3 rSdir = -FlatDirBetween(rrv, FlipV(rv[vb + rb - 2]));
                    bool doCurv = !((i == 0 && closedSL) || (i == rc - 1 && closedEL));

                    if (leftSide)
                    {
                        if (curvedSides)
                        {
                            Vector3 opos = lv + lSdir * wallThick;
                            Vector2 uv25 = Vector2.Lerp(uvBl, uvTl, 0.25f);
                            Vector2 uv75 = Vector2.Lerp(uvBl, uvTl, 0.75f);
                            Vector3 bpos = doCurv ? opos : lv;
                            Vector3 p1 = bpos + whVec * 0.25f;
                            Vector3 p2 = bpos + whVec * 0.75f;
                            pos.Add(lv); pos.Add(p1); pos.Add(p2); pos.Add(ltv);
                            uvs.Add(uvBl); uvs.Add(uv25); uvs.Add(uv75); uvs.Add(uvTl);
                            if (doubleSided)
                            {
                                pos.Add(ltv); pos.Add(p2); pos.Add(p1); pos.Add(lv);
                                uvs.Add(uvTl); uvs.Add(uv75); uvs.Add(uv25); uvs.Add(uvBl);
                            }
                        }
                        else
                        {
                            pos.Add(lv); pos.Add(ltv);
                            uvs.Add(uvBl); uvs.Add(uvTl);
                            if (doubleSided)
                            {
                                pos.Add(ltv); pos.Add(lv);
                                uvs.Add(uvTl); uvs.Add(uvBl);
                            }
                        }
                    }

                    if (rightSide)
                    {
                        if (curvedSides)
                        {
                            Vector3 opos = rrv + rSdir * wallThick;
                            Vector2 uv25 = Vector2.Lerp(uvBr, uvTr, 0.25f);
                            Vector2 uv75 = Vector2.Lerp(uvBr, uvTr, 0.75f);
                            Vector3 bpos = doCurv ? opos : rrv;
                            Vector3 p1 = bpos + whVec * 0.25f;
                            Vector3 p2 = bpos + whVec * 0.75f;
                            pos.Add(rrv); pos.Add(p1); pos.Add(p2); pos.Add(rtv);
                            uvs.Add(uvBr); uvs.Add(uv25); uvs.Add(uv75); uvs.Add(uvTr);
                            if (doubleSided)
                            {
                                pos.Add(rtv); pos.Add(p2); pos.Add(p1); pos.Add(rrv);
                                uvs.Add(uvTr); uvs.Add(uv75); uvs.Add(uv25); uvs.Add(uvBr);
                            }
                        }
                        else
                        {
                            pos.Add(rrv); pos.Add(rtv);
                            uvs.Add(uvBr); uvs.Add(uvTr);
                            if (doubleSided)
                            {
                                pos.Add(rtv); pos.Add(rrv);
                                uvs.Add(uvTr); uvs.Add(uvBr);
                            }
                        }
                    }

                    if (isWall)
                    {
                        Vector3 loOff = Vector3.zero;
                        if (i == 0 && (tFlags & 512) != 0) loOff = MitreOff(lSdir, true, true);
                        else if (i == rc - 1 && (tFlags & 1024) != 0) loOff = MitreOff(lSdir, false, true);
                        Vector3 roOff = Vector3.zero;
                        if (i == 0 && (tFlags & 2048) != 0) roOff = MitreOff(rSdir, true, false);
                        else if (i == rc - 1 && (tFlags & 4096) != 0) roOff = MitreOff(rSdir, false, false);

                        Vector3 lBotOut = leftOuter ? (lv + lSdir * wallThick + loOff - w3Vec) : (lv - w3Vec);
                        Vector3 rBotOut = rightOuter ? (rrv + rSdir * wallThick + roOff - w3Vec) : (rrv - w3Vec);

                        if (leftOuter)
                        {
                            Vector3 lTopOut = ltv + lSdir * wallThick + loOff;
                            pos.Add(lBotOut); pos.Add(lTopOut); pos.Add(lTopOut); pos.Add(ltv);
                            uvs.Add(uvBl); uvs.Add(uvTl); uvs.Add(wUvsL[uvb]); uvs.Add(wUvsL[uvb + 1]);
                        }
                        if (rightOuter)
                        {
                            Vector3 rTopOut = rtv + rSdir * wallThick + roOff;
                            pos.Add(rBotOut); pos.Add(rTopOut); pos.Add(rTopOut); pos.Add(rtv);
                            uvs.Add(uvBr); uvs.Add(uvTr); uvs.Add(wUvsR[uvb]); uvs.Add(wUvsR[uvb + 1]);
                        }
                        pos.Add(rBotOut); pos.Add(lBotOut);
                        uvs.Add(uUvs[uvb + 1]); uvs.Add(uUvs[uvb]);
                    }

                    if (flatCeil)
                    {
                        pos.Add(ltv); pos.Add(rtv);
                        uvs.Add(cUvs[uvb]); uvs.Add(cUvs[uvb + 1]);
                    }
                    else if (curvedCeil)
                    {
                        Vector2 uvCl = cUvs[uvb], uvCr = cUvs[uvb + 1];
                        Vector3 ccV = Vector3.Lerp(ltv, rtv, 0.5f);
                        Vector3 lcV = Vector3.Lerp(ltv, ccV, 0.5f);
                        Vector3 rcV = Vector3.Lerp(rtv, ccV, 0.5f);
                        Vector2 ccUv = Vector2.Lerp(uvCl, uvCr, 0.5f);
                        Vector2 lcUv = Vector2.Lerp(uvCl, ccUv, 0.5f);
                        Vector2 rcUv = Vector2.Lerp(uvCr, ccUv, 0.5f);
                        if (doCurv)
                        {
                            lcV.y += 1.5f;
                            rcV.y += 1.5f;
                            ccV.y += 2.0f;
                        }
                        pos.Add(ltv); pos.Add(lcV); pos.Add(ccV); pos.Add(rcV); pos.Add(rtv);
                        uvs.Add(uvCl); uvs.Add(lcUv); uvs.Add(ccUv); uvs.Add(rcUv); uvs.Add(uvCr);
                    }
                }

                var lwTris = new List<int>();
                var rwTris = new List<int>();
                var loTris = new List<int>();
                var roTris = new List<int>();
                var unTris = new List<int>();
                var ceTris = new List<int>();

                for (int i = 0; i < sc; i++)
                {
                    int rbI = i * vPerRow;
                    int rbN = (i + 1) * vPerRow;
                    if (leftSide)
                    {
                        for (int j = 0; j < lrVc - 1; j++)
                        {
                            int a = rbI + lwBase + j, b = a + 1, c = rbN + lwBase + j, d = c + 1;
                            lwTris.Add(b); lwTris.Add(c); lwTris.Add(a);
                            lwTris.Add(d); lwTris.Add(c); lwTris.Add(b);
                            if (doubleSided)
                            {
                                int o = lrVc;
                                lwTris.Add(b + o); lwTris.Add(c + o); lwTris.Add(a + o);
                                lwTris.Add(d + o); lwTris.Add(c + o); lwTris.Add(b + o);
                            }
                        }
                    }
                    if (rightSide)
                    {
                        for (int j = 0; j < lrVc - 1; j++)
                        {
                            int a = rbI + rwBase + j, b = a + 1, c = rbN + rwBase + j, d = c + 1;
                            rwTris.Add(a); rwTris.Add(c); rwTris.Add(b);
                            rwTris.Add(b); rwTris.Add(c); rwTris.Add(d);
                            if (doubleSided)
                            {
                                int o = lrVc;
                                rwTris.Add(a + o); rwTris.Add(c + o); rwTris.Add(b + o);
                                rwTris.Add(b + o); rwTris.Add(c + o); rwTris.Add(d + o);
                            }
                        }
                    }
                    if (leftOuter)
                    {
                        int alo = rbI + loBase, clo = rbN + loBase;
                        loTris.Add(alo); loTris.Add(clo); loTris.Add(alo + 1);
                        loTris.Add(alo + 1); loTris.Add(clo); loTris.Add(clo + 1);
                        loTris.Add(alo + 2); loTris.Add(clo + 2); loTris.Add(alo + 3);
                        loTris.Add(alo + 3); loTris.Add(clo + 2); loTris.Add(clo + 3);
                    }
                    if (rightOuter)
                    {
                        int aro = rbI + roBase, cro = rbN + roBase;
                        roTris.Add(aro + 1); roTris.Add(cro); roTris.Add(aro);
                        roTris.Add(cro + 1); roTris.Add(cro); roTris.Add(aro + 1);
                        roTris.Add(aro + 3); roTris.Add(cro + 2); roTris.Add(aro + 2);
                        roTris.Add(cro + 3); roTris.Add(cro + 2); roTris.Add(aro + 3);
                    }
                    if (isWall)
                    {
                        int au = rbI + unBase, cu = rbN + unBase;
                        unTris.Add(au); unTris.Add(cu); unTris.Add(au + 1);
                        unTris.Add(au + 1); unTris.Add(cu); unTris.Add(cu + 1);
                    }
                    if (haveCeil)
                    {
                        for (int j = 0; j < ceilVc - 1; j++)
                        {
                            int a = rbI + ceBase + j, b = a + 1, c = rbN + ceBase + j, d = c + 1;
                            ceTris.Add(a); ceTris.Add(b); ceTris.Add(c);
                            ceTris.Add(b); ceTris.Add(d); ceTris.Add(c);
                        }
                    }
                }

                void AddTCap(Vector3 innerS, Vector3 refS, bool isStart, bool isLeft, bool mitre, List<int> targetTris)
                {
                    Vector3 iw = FlipV(innerS);
                    Vector3 iwt = iw + whVec;
                    Vector3 sdir = -FlatDirBetween(iw, FlipV(refS));
                    Vector3 offV = mitre ? MitreOff(sdir, isStart, isLeft) : Vector3.zero;
                    Vector3 owt = iwt + sdir * wallThick + offV;
                    Vector3 ow = iw + sdir * wallThick + offV - w3Vec;
                    int c = pos.Count;
                    if (isStart != isLeft)
                    {
                        pos.Add(ow); pos.Add(iw); pos.Add(iwt); pos.Add(owt);
                        uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f));
                        uvs.Add(new Vector2(1f, 1f)); uvs.Add(new Vector2(0f, 1f));
                    }
                    else
                    {
                        pos.Add(iw); pos.Add(ow); pos.Add(owt); pos.Add(iwt);
                        uvs.Add(new Vector2(1f, 0f)); uvs.Add(new Vector2(0f, 0f));
                        uvs.Add(new Vector2(0f, 1f)); uvs.Add(new Vector2(1f, 1f));
                    }
                    targetTris.Add(c); targetTris.Add(c + 2); targetTris.Add(c + 1);
                    targetTris.Add(c); targetTris.Add(c + 3); targetTris.Add(c + 2);
                }

                if (leftOuter && closedSL) AddTCap(rv[0], rv[1], true, true, (tFlags & 512) != 0, loTris);
                if (leftOuter && closedEL)
                {
                    int b = (rc - 1) * rb;
                    AddTCap(rv[b], rv[b + 1], false, true, (tFlags & 1024) != 0, loTris);
                }
                if (rightOuter && closedSR)
                {
                    int b = rb - 2;
                    AddTCap(rv[b + 1], rv[b], true, false, (tFlags & 2048) != 0, roTris);
                }
                if (rightOuter && closedER)
                {
                    int b = (rc - 1) * rb + rb - 2;
                    AddTCap(rv[b + 1], rv[b], false, false, (tFlags & 4096) != 0, roTris);
                }

                Vector3[] posArr = pos.ToArray();
                Vector2[] uvArr = uvs.ToArray();
                var norms = new Vector3[posArr.Length];
                for (int i = 0; i < norms.Length; i++) norms[i] = Vector3.up;

                if (leftSide) AddSubmeshToChunk(chunk, getTex, TTex(0), posArr, norms, uvArr, lwTris, true);
                if (rightSide) AddSubmeshToChunk(chunk, getTex, TTex(1), posArr, norms, uvArr, rwTris, true);
                if (isWall)
                {
                    if (leftOuter) AddSubmeshToChunk(chunk, getTex, TTex(4), posArr, norms, uvArr, loTris, true);
                    if (rightOuter) AddSubmeshToChunk(chunk, getTex, TTex(3), posArr, norms, uvArr, roTris, true);
                    AddSubmeshToChunk(chunk, getTex, TTex(5), posArr, norms, uvArr, unTris, true);
                }
                if (haveCeil) AddSubmeshToChunk(chunk, getTex, TTex(2), posArr, norms, uvArr, ceTris, true);
            }
        }
    }

    public sealed class MM2SkyDomeFollower : MonoBehaviour
    {
        private float yOffset = -12f;
        private float rotSpeedDegPerSec = 0.005f * Mathf.Rad2Deg;

        public void ConfigureSkyParameters(float skyYOff, float rotSpeedRad, float fogStart, float fogEnd)
        {
            yOffset = -12f + skyYOff;
            rotSpeedDegPerSec = rotSpeedRad * Mathf.Rad2Deg;

            // Keep SkyDome radius inside the active fog range so dense Foggy/Rainy conditions
            // blend the horizon naturally while keeping overhead clouds/stars/rain-sky visible.
            // Because Runtime_SkyDome renders at RenderQueue.Background (1000) with ZWrite Off,
            // all opaque city geometry at any distance renders in front of it without clipping.
            float targetRadius = Mathf.Clamp(fogEnd * 0.42f, 28f, 680f);
            float xzScale = Mathf.Clamp(targetRadius / 42f, 0.7f, 17f);
            float yScale = xzScale * (10f / 17f);
            transform.localScale = new Vector3(xzScale, yScale, xzScale);
        }

        private void Start()
        {
            Camera cam = Camera.main;
            if (cam != null && cam.farClipPlane < 12000f)
            {
                cam.farClipPlane = 12000f;
            }
        }

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            if (cam.farClipPlane < 12000f)
            {
                cam.farClipPlane = 12000f;
            }

            Vector3 cPos = cam.transform.position;
            transform.position = new Vector3(cPos.x, cPos.y + yOffset, cPos.z);

            if (Mathf.Abs(rotSpeedDegPerSec) > 0.0001f)
            {
                transform.Rotate(0f, rotSpeedDegPerSec * Time.deltaTime, 0f, Space.World);
            }
        }
    }

    public enum MM2DrawbridgeMode
    {
        Inactive = 0,
        Proximity = 1,
        Timed = 2,
        AlwaysOpen = 3
    }

    public struct MM2DrawbridgeSchedule
    {
        public MM2DrawbridgeMode Mode;
        public bool StartsOpen;
        public float InitialTimerSeconds;
        public float DownIntervalSeconds;
        public float BridgeOnDelaySeconds;
        public float OpenDurationSeconds;
        public float UpIntervalSeconds;
        public float BridgeOffDelaySeconds;
        public float CloseDurationSeconds;
        public Vector3 TriggerPosition;
        public float TriggerDistSq;
        public float TriggerTimeoutSeconds;
    }

    public sealed class MM2DrawbridgeGroup : MonoBehaviour
    {
        private enum BridgeCycleState
        {
            Closed = 0,
            OpeningDelay = 1,
            Opening = 2,
            OpenHold = 3,
            ClosingDelay = 4,
            Closing = 5
        }

        private static readonly List<MM2DrawbridgeGroup> ActiveBridges = new List<MM2DrawbridgeGroup>(16);
        private static bool audioLoaded;
        private static AudioClip sfxBridgeBell;
        private static AudioClip sfxBridgeMove;

        private Rigidbody leafA;
        private Quaternion baseRotA;
        private Rigidbody leafB;
        private Quaternion baseRotB;
        private Vector3 bridgeCenter;
        private float maxPitchDeg = 13.0f;
        private MM2DrawbridgeSchedule schedule;

        private BridgeCycleState state;
        private float stateTimer;
        private float elapsedSinceReset;
        private float currentPitchDeg;

        private AudioSource bellAudioSource;
        private AudioSource moveAudioSource;

        private void OnEnable()
        {
            if (!ActiveBridges.Contains(this))
            {
                ActiveBridges.Add(this);
            }
        }

        private void OnDisable()
        {
            ActiveBridges.Remove(this);
            StopBridgeAudio();
        }

        public static void ResetAllBridges()
        {
            for (int i = 0; i < ActiveBridges.Count; i++)
            {
                if (ActiveBridges[i] != null)
                {
                    ActiveBridges[i].ResetBridgeState();
                }
            }
        }

        public void Initialize(
            Rigidbody rbA,
            Quaternion rotA,
            Rigidbody rbB,
            Quaternion rotB,
            Vector3 centerWorldPos,
            float maxPitchDeg,
            bool alwaysOpenJump,
            bool isInactive,
            float phaseOffsetSeconds
        )
        {
            var sched = new MM2DrawbridgeSchedule
            {
                Mode = isInactive ? MM2DrawbridgeMode.Inactive : (alwaysOpenJump ? MM2DrawbridgeMode.AlwaysOpen : MM2DrawbridgeMode.Timed),
                StartsOpen = alwaysOpenJump,
                InitialTimerSeconds = phaseOffsetSeconds,
                DownIntervalSeconds = 10.0f,
                BridgeOnDelaySeconds = 0f,
                OpenDurationSeconds = 0.47123894f / 0.05f,
                UpIntervalSeconds = 10.0f,
                BridgeOffDelaySeconds = 0f,
                CloseDurationSeconds = 0.47123894f / 0.05f,
                TriggerPosition = centerWorldPos,
                TriggerDistSq = 10000.0f,
                TriggerTimeoutSeconds = float.PositiveInfinity
            };
            InitializeWithSchedule(rbA, rotA, rbB, rotB, centerWorldPos, maxPitchDeg, sched);
        }

        public void InitializeWithSchedule(
            Rigidbody rbA,
            Quaternion rotA,
            Rigidbody rbB,
            Quaternion rotB,
            Vector3 centerWorldPos,
            float maxPitchDeg,
            MM2DrawbridgeSchedule schedule
        )
        {
            leafA = rbA;
            baseRotA = rotA;
            leafB = rbB;
            baseRotB = rotB;
            bridgeCenter = centerWorldPos;
            this.maxPitchDeg = Mathf.Clamp(maxPitchDeg, 5f, 45f);
            this.schedule = schedule;

            EnsureAudioSources();
            ResetBridgeState();
        }

        public void ResetBridgeState()
        {
            elapsedSinceReset = 0f;
            stateTimer = Mathf.Max(0f, schedule.InitialTimerSeconds);

            if (schedule.Mode == MM2DrawbridgeMode.AlwaysOpen)
            {
                state = BridgeCycleState.OpenHold;
                currentPitchDeg = maxPitchDeg;
            }
            else if (schedule.Mode == MM2DrawbridgeMode.Inactive)
            {
                state = BridgeCycleState.Closed;
                currentPitchDeg = 0f;
            }
            else if (schedule.StartsOpen)
            {
                state = BridgeCycleState.OpenHold;
                currentPitchDeg = maxPitchDeg;
            }
            else
            {
                state = BridgeCycleState.Closed;
                currentPitchDeg = 0f;
            }

            StopBridgeAudio();
            ApplyLeafRotations(currentPitchDeg);
        }

        private void EnsureAudioSources()
        {
            if (!audioLoaded)
            {
                audioLoaded = true;
                sfxBridgeBell = MM2RuntimeDecoders.LoadAudioClip("bridgebell");
                sfxBridgeMove = MM2RuntimeDecoders.LoadAudioClip("bridgemove");
            }

            if (sfxBridgeBell != null && bellAudioSource == null)
            {
                GameObject audioGo = new GameObject("BridgeAudio");
                audioGo.transform.SetParent(transform, false);
                audioGo.transform.position = bridgeCenter;

                bellAudioSource = audioGo.AddComponent<AudioSource>();
                bellAudioSource.clip = sfxBridgeBell;
                bellAudioSource.loop = true;
                bellAudioSource.playOnAwake = false;
                bellAudioSource.spatialBlend = 1.0f;
                bellAudioSource.rolloffMode = AudioRolloffMode.Linear;
                bellAudioSource.minDistance = 18f;
                bellAudioSource.maxDistance = 220f;
                bellAudioSource.volume = 0.65f;
                bellAudioSource.pitch = 0.95f;

                if (sfxBridgeMove != null)
                {
                    moveAudioSource = audioGo.AddComponent<AudioSource>();
                    moveAudioSource.clip = sfxBridgeMove;
                    moveAudioSource.loop = true;
                    moveAudioSource.playOnAwake = false;
                    moveAudioSource.spatialBlend = 1.0f;
                    moveAudioSource.rolloffMode = AudioRolloffMode.Linear;
                    moveAudioSource.minDistance = 18f;
                    moveAudioSource.maxDistance = 200f;
                    moveAudioSource.volume = 0.75f;
                }
            }
        }

        private void UpdateBridgeAudio(bool playBell, bool playMove)
        {
            if (bellAudioSource == null) return;

            bool inHearingRange = false;
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 c = cam.transform.position;
                float dx = c.x - bridgeCenter.x;
                float dz = c.z - bridgeCenter.z;
                inHearingRange = (dx * dx + dz * dz) <= 250f * 250f;
            }

            bool wantBell = playBell && inHearingRange;
            if (wantBell && !bellAudioSource.isPlaying) bellAudioSource.Play();
            else if (!wantBell && bellAudioSource.isPlaying) bellAudioSource.Stop();

            if (moveAudioSource != null)
            {
                bool wantMove = playMove && inHearingRange;
                if (wantMove && !moveAudioSource.isPlaying) moveAudioSource.Play();
                else if (!wantMove && moveAudioSource.isPlaying) moveAudioSource.Stop();
            }
        }

        private void StopBridgeAudio()
        {
            if (bellAudioSource != null && bellAudioSource.isPlaying) bellAudioSource.Stop();
            if (moveAudioSource != null && moveAudioSource.isPlaying) moveAudioSource.Stop();
        }

        private bool CheckProximityTrigger()
        {
            if (elapsedSinceReset > schedule.TriggerTimeoutSeconds) return false;

            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 p = cam.transform.position;
                float dx = p.x - schedule.TriggerPosition.x;
                float dy = p.y - schedule.TriggerPosition.y;
                float dz = p.z - schedule.TriggerPosition.z;
                if (dx * dx + dy * dy + dz * dz <= schedule.TriggerDistSq)
                {
                    return true;
                }
            }

            return false;
        }

        private void BeginOpeningSequence()
        {
            stateTimer = 0f;
            if (schedule.BridgeOnDelaySeconds > 0.01f)
            {
                state = BridgeCycleState.OpeningDelay;
            }
            else
            {
                state = BridgeCycleState.Opening;
            }
        }

        private void BeginClosingSequence()
        {
            stateTimer = 0f;
            if (schedule.BridgeOffDelaySeconds > 0.01f)
            {
                state = BridgeCycleState.ClosingDelay;
            }
            else
            {
                state = BridgeCycleState.Closing;
            }
        }

        private void FixedUpdate()
        {
            if (leafA == null) return;

            if (schedule.Mode == MM2DrawbridgeMode.Inactive)
            {
                if (currentPitchDeg != 0f)
                {
                    currentPitchDeg = 0f;
                    ApplyLeafRotations(0f);
                }
                return;
            }

            if (schedule.Mode == MM2DrawbridgeMode.AlwaysOpen)
            {
                if (Mathf.Abs(currentPitchDeg - maxPitchDeg) > 0.001f)
                {
                    currentPitchDeg = maxPitchDeg;
                    ApplyLeafRotations(currentPitchDeg);
                }
                return;
            }

            float dt = Time.fixedDeltaTime;
            elapsedSinceReset += dt;

            switch (state)
            {
                case BridgeCycleState.Closed:
                {
                    currentPitchDeg = 0f;
                    UpdateBridgeAudio(playBell: false, playMove: false);

                    if (schedule.Mode == MM2DrawbridgeMode.Proximity)
                    {
                        if (CheckProximityTrigger())
                        {
                            BeginOpeningSequence();
                        }
                    }
                    else if (schedule.Mode == MM2DrawbridgeMode.Timed)
                    {
                        stateTimer += dt;
                        if (stateTimer >= schedule.DownIntervalSeconds)
                        {
                            BeginOpeningSequence();
                        }
                    }
                    break;
                }

                case BridgeCycleState.OpeningDelay:
                {
                    currentPitchDeg = 0f;
                    UpdateBridgeAudio(playBell: true, playMove: false);
                    stateTimer += dt;
                    if (stateTimer >= schedule.BridgeOnDelaySeconds)
                    {
                        stateTimer = 0f;
                        state = BridgeCycleState.Opening;
                    }
                    break;
                }

                case BridgeCycleState.Opening:
                {
                    UpdateBridgeAudio(playBell: true, playMove: true);
                    float openSpeedDegPerSec = maxPitchDeg / Mathf.Max(0.25f, schedule.OpenDurationSeconds);
                    currentPitchDeg = Mathf.MoveTowards(currentPitchDeg, maxPitchDeg, openSpeedDegPerSec * dt);
                    ApplyLeafRotations(currentPitchDeg);

                    if (currentPitchDeg >= maxPitchDeg - 0.001f)
                    {
                        currentPitchDeg = maxPitchDeg;
                        stateTimer = 0f;
                        state = BridgeCycleState.OpenHold;
                        UpdateBridgeAudio(playBell: false, playMove: false);
                    }
                    break;
                }

                case BridgeCycleState.OpenHold:
                {
                    currentPitchDeg = maxPitchDeg;
                    UpdateBridgeAudio(playBell: false, playMove: false);
                    stateTimer += dt;
                    if (stateTimer >= schedule.UpIntervalSeconds)
                    {
                        BeginClosingSequence();
                    }
                    break;
                }

                case BridgeCycleState.ClosingDelay:
                {
                    currentPitchDeg = maxPitchDeg;
                    UpdateBridgeAudio(playBell: true, playMove: false);
                    stateTimer += dt;
                    if (stateTimer >= schedule.BridgeOffDelaySeconds)
                    {
                        stateTimer = 0f;
                        state = BridgeCycleState.Closing;
                    }
                    break;
                }

                case BridgeCycleState.Closing:
                {
                    UpdateBridgeAudio(playBell: true, playMove: true);
                    float closeSpeedDegPerSec = maxPitchDeg / Mathf.Max(0.25f, schedule.CloseDurationSeconds);
                    currentPitchDeg = Mathf.MoveTowards(currentPitchDeg, 0f, closeSpeedDegPerSec * dt);
                    ApplyLeafRotations(currentPitchDeg);

                    if (currentPitchDeg <= 0.001f)
                    {
                        currentPitchDeg = 0f;
                        stateTimer = 0f;
                        state = BridgeCycleState.Closed;
                        UpdateBridgeAudio(playBell: false, playMove: false);
                    }
                    break;
                }
            }
        }

        private void ApplyLeafRotations(float pitchDeg)
        {
            Quaternion localTilt = Quaternion.Euler(-pitchDeg, 0f, 0f);
            if (leafA != null)
            {
                leafA.MoveRotation(baseRotA * localTilt);
            }
            if (leafB != null)
            {
                leafB.MoveRotation(baseRotB * localTilt);
            }
        }
    }
}
