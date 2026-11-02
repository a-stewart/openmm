using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using MM2.Archive;

namespace MM2.AI
{
    /// <summary>
    /// Parses and stores AI map data from .ar archives:
    ///   - Binary road network: city/&lt;city&gt;.bai ("CAI1" format)
    ///   - City AI configuration: city/&lt;city&gt;.aimap (speed limit, left-hand driving, ambient vehicle types &amp; density probabilities)
    ///   - Per-race AI configuration: race/&lt;city&gt;/&lt;race&gt;.aimap (road density/speed exceptions, police spawns, opponent definitions)
    ///   - Per-opponent route tables: race/&lt;city&gt;/&lt;opp&gt;.opp (starting grid pose + target route intersections)
    /// </summary>
    public class MM2AiMapData
    {
        public class AmbientTypeEntry
        {
            public string PkgName;
            public float CumulativeProbability;
            public int Flag;
        }

        public class RoadExceptionEntry
        {
            public int PathId;
            public float DensityMultiplier;
            public float SpeedLimit;
        }

        public class PoliceSpawnDefinition
        {
            public string PkgName;
            public Vector3 Position;
            public Quaternion Rotation;
            public int StartBehavior;
            public float MaxSpeed;
            public float AccelFactor;
            public float DetectRadius;
        }

        public class OpponentWaypoint
        {
            public Vector3 Position;
            public float TargetSpeed;
        }

        public class OpponentDefinition
        {
            public string PkgName;
            public string OppFileName;
            public int BehaviorType;
            public float MaxThrottle = 1f;
            public float TargetSpeed = 40f;
            public float CornerBraking = 0.75f;
            public float TopSpeedMultiplier = 1f;
            public Vector3 StartPosition;
            public Quaternion StartRotation = Quaternion.identity;
            public bool HasValidStartPose;
            public List<OpponentWaypoint> Waypoints = new List<OpponentWaypoint>();
        }

        public class BaiDirectedRoad
        {
            public int RoadIndex;
            public int PathId;
            public int StartIntersectionId;
            public int EndIntersectionId;
            public float SpeedLimit;
            public float DensityMultiplier = 1f;
            public bool HasTrafficLightAtEnd;
            public bool HasStopSignAtEnd;
            public float StopDistAtEnd;
            public bool IsDividedOrFreeway;
            public bool IsOneWay;
            public bool IsContraflowForRacing;
            public Vector3[][] Lanes; // Lanes[laneIdx][sectionIdx], ordered Start -> End
            public Vector3[] UpNormals; // Ordered Start -> End
            public float[] CumulativeDistances; // Along Lanes[0], ordered Start -> End
            public float TotalLength;
            public Vector3 StartPosition;
            public Vector3 EndPosition;
            public Vector3 StartTangent;
            public Vector3 EndTangent;
            public short[] PsdlRoomIds;

            public int LaneCount => Lanes != null ? Lanes.Length : 0;

            public void SampleLanePose(int laneIdx, float distanceAlongRoad, out Vector3 pos, out Vector3 forward, out Vector3 up)
            {
                if (Lanes == null || Lanes.Length == 0)
                {
                    pos = StartPosition;
                    forward = StartTangent.sqrMagnitude > 0.001f ? StartTangent : Vector3.forward;
                    up = Vector3.up;
                    return;
                }

                int clampedLane = Mathf.Clamp(laneIdx, 0, Lanes.Length - 1);
                Vector3[] pts = Lanes[clampedLane];
                int n = pts.Length;
                if (n == 1)
                {
                    pos = pts[0];
                    forward = StartTangent.sqrMagnitude > 0.001f ? StartTangent : Vector3.forward;
                    up = UpNormals != null && UpNormals.Length > 0 ? UpNormals[0] : Vector3.up;
                    return;
                }

                float d = Mathf.Clamp(distanceAlongRoad, 0f, TotalLength);
                int seg = 0;
                for (int i = 0; i < n - 1; i++)
                {
                    if (d <= CumulativeDistances[i + 1] || i == n - 2)
                    {
                        seg = i;
                        break;
                    }
                }

                float d0 = CumulativeDistances[seg];
                float d1 = CumulativeDistances[seg + 1];
                float segLen = Mathf.Max(0.001f, d1 - d0);
                float t = Mathf.Clamp01((d - d0) / segLen);

                pos = Vector3.Lerp(pts[seg], pts[seg + 1], t);
                Vector3 diff = pts[seg + 1] - pts[seg];
                if (diff.sqrMagnitude > 0.0001f)
                {
                    forward = diff.normalized;
                }
                else
                {
                    forward = StartTangent.sqrMagnitude > 0.001f ? StartTangent : Vector3.forward;
                }

                // Compute zero-roll surface normal from the 3D lane tangent and horizontal right axis
                // so vehicles pitch naturally with hills/ramps while never rolling onto their sides.
                Vector3 flatFwd = new Vector3(forward.x, 0f, forward.z);
                if (flatFwd.sqrMagnitude > 0.0001f)
                {
                    flatFwd.Normalize();
                    Vector3 levelRight = new Vector3(flatFwd.z, 0f, -flatFwd.x);
                    up = Vector3.Cross(forward, levelRight).normalized;
                    if (up.y < 0.5f) up = Vector3.up;
                }
                else
                {
                    up = Vector3.up;
                }
            }
        }

        public enum TrafficLightPhase
        {
            Green,
            Yellow,
            Red
        }

        public class BaiSidewalkStrip
        {
            public int PathId;
            public int SideIndex;
            public Vector3[] CenterPoints;
            public Vector3[] CurbPoints;
            public Vector3[] WallPoints;
            public float[] CumulativeDistances;
            public float TotalLength;
            public short[] PsdlRoomIds;

            public void SamplePose(
                float distanceAlong,
                float lateralNormalized,
                out Vector3 pos,
                out Vector3 forward,
                out Vector3 outwardDir)
            {
                if (CenterPoints == null || CenterPoints.Length == 0)
                {
                    pos = Vector3.zero;
                    forward = Vector3.forward;
                    outwardDir = Vector3.right;
                    return;
                }

                int n = CenterPoints.Length;
                if (n == 1)
                {
                    pos = CenterPoints[0];
                    forward = Vector3.forward;
                    outwardDir = WallPoints != null && CurbPoints != null && WallPoints.Length > 0
                        ? (WallPoints[0] - CurbPoints[0]).normalized
                        : Vector3.right;
                    return;
                }

                float d = Mathf.Clamp(distanceAlong, 0f, TotalLength);
                int seg = 0;
                for (int i = 0; i < n - 1; i++)
                {
                    if (d <= CumulativeDistances[i + 1] || i == n - 2)
                    {
                        seg = i;
                        break;
                    }
                }

                float d0 = CumulativeDistances[seg];
                float d1 = CumulativeDistances[seg + 1];
                float segLen = Mathf.Max(0.001f, d1 - d0);
                float t = Mathf.Clamp01((d - d0) / segLen);

                Vector3 cPos = Vector3.Lerp(CenterPoints[seg], CenterPoints[seg + 1], t);
                Vector3 curbPos = (CurbPoints != null && CurbPoints.Length == n)
                    ? Vector3.Lerp(CurbPoints[seg], CurbPoints[seg + 1], t)
                    : cPos;
                Vector3 wallPos = (WallPoints != null && WallPoints.Length == n)
                    ? Vector3.Lerp(WallPoints[seg], WallPoints[seg + 1], t)
                    : cPos;

                Vector3 diff = CenterPoints[seg + 1] - CenterPoints[seg];
                forward = diff.sqrMagnitude > 0.0001f ? diff.normalized : Vector3.forward;

                Vector3 outVec = wallPos - curbPos;
                outVec.y = 0f;
                if (outVec.sqrMagnitude > 0.0001f)
                {
                    outwardDir = outVec.normalized;
                }
                else
                {
                    outwardDir = new Vector3(forward.z, 0f, -forward.x).normalized;
                }

                // lateralNormalized: -1 = curb edge, 0 = sidewalk center, +1 = building/wall edge
                if (lateralNormalized >= 0f)
                {
                    pos = Vector3.Lerp(cPos, wallPos, Mathf.Clamp01(lateralNormalized));
                }
                else
                {
                    pos = Vector3.Lerp(cPos, curbPos, Mathf.Clamp01(-lateralNormalized));
                }
            }
        }

        public class BaiCableCarTrack
        {
            public int TrackIndex;
            public int PathId;
            public int SideIndex; // 0 = Sink->Source, 1 = Source->Sink
            public int StartIntersectionId;
            public int EndIntersectionId;
            public Vector3[] RailPoints; // Ordered Start -> End
            public float[] CumulativeDistances;
            public float TotalLength;
            public Vector3 StartTangent;
            public Vector3 EndTangent;

            public void SamplePose(float distanceAlong, out Vector3 pos, out Vector3 forward, out Vector3 up)
            {
                if (RailPoints == null || RailPoints.Length == 0)
                {
                    pos = Vector3.zero;
                    forward = Vector3.forward;
                    up = Vector3.up;
                    return;
                }

                int n = RailPoints.Length;
                if (n == 1)
                {
                    pos = RailPoints[0];
                    forward = StartTangent.sqrMagnitude > 0.001f ? StartTangent : Vector3.forward;
                    up = Vector3.up;
                    return;
                }

                float d = Mathf.Clamp(distanceAlong, 0f, TotalLength);
                int seg = 0;
                for (int i = 0; i < n - 1; i++)
                {
                    if (d <= CumulativeDistances[i + 1] || i == n - 2)
                    {
                        seg = i;
                        break;
                    }
                }

                float d0 = CumulativeDistances[seg];
                float d1 = CumulativeDistances[seg + 1];
                float segLen = Mathf.Max(0.001f, d1 - d0);
                float t = Mathf.Clamp01((d - d0) / segLen);

                pos = Vector3.Lerp(RailPoints[seg], RailPoints[seg + 1], t);
                Vector3 diff = RailPoints[seg + 1] - RailPoints[seg];
                forward = diff.sqrMagnitude > 0.0001f ? diff.normalized : StartTangent;

                Vector3 flatFwd = new Vector3(forward.x, 0f, forward.z);
                if (flatFwd.sqrMagnitude > 0.0001f)
                {
                    flatFwd.Normalize();
                    Vector3 levelRight = new Vector3(flatFwd.z, 0f, -flatFwd.x);
                    up = Vector3.Cross(forward, levelRight).normalized;
                    if (up.y < 0.5f) up = Vector3.up;
                }
                else
                {
                    up = Vector3.up;
                }
            }
        }

        public class BaiIntersection
        {
            public int Id;
            public short RoomId;
            public Vector3 Center;
            public int[] ConnectedPathIds;
            public List<BaiDirectedRoad> IncomingRoads = new List<BaiDirectedRoad>();
            public List<BaiDirectedRoad> OutgoingRoads = new List<BaiDirectedRoad>();
            public List<BaiDirectedRoad> RaceOutgoingRoads = new List<BaiDirectedRoad>();
            public float LightCycleOffset;

            /// <summary>
            /// Evaluates the traffic light state for an incoming road at this intersection.
            /// Perpendicular road axes alternate on a 20-second cycle (8s green, 2s yellow, 10s red).
            /// </summary>
            public TrafficLightPhase GetLightPhase(BaiDirectedRoad incomingRoad, float worldTime)
            {
                if (incomingRoad == null || !incomingRoad.HasTrafficLightAtEnd)
                {
                    return TrafficLightPhase.Green;
                }

                // Group incoming roads into roughly North-South vs East-West axes using their end tangent
                Vector3 dir = incomingRoad.EndTangent;
                bool isNorthSouthGroup = Mathf.Abs(dir.z) >= Mathf.Abs(dir.x);

                const float cycleDuration = 20f;
                const float halfCycle = 10f;
                const float greenDuration = 7.8f;

                float phaseTime = (worldTime + LightCycleOffset + (isNorthSouthGroup ? 0f : halfCycle)) % cycleDuration;
                if (phaseTime < 0f) phaseTime += cycleDuration;

                if (phaseTime < greenDuration)
                {
                    return TrafficLightPhase.Green;
                }
                if (phaseTime < halfCycle)
                {
                    return TrafficLightPhase.Yellow;
                }
                return TrafficLightPhase.Red;
            }
        }

        public string CityName { get; private set; }
        public float DefaultSpeedLimit { get; private set; } = 15f;
        public bool DriveOnLeft { get; private set; }
        public List<AmbientTypeEntry> AmbientTypes { get; } = new List<AmbientTypeEntry>();
        public Dictionary<int, RoadExceptionEntry> RoadExceptions { get; } = new Dictionary<int, RoadExceptionEntry>();
        public List<PoliceSpawnDefinition> PoliceSpawns { get; } = new List<PoliceSpawnDefinition>();
        public List<OpponentDefinition> Opponents { get; } = new List<OpponentDefinition>();

        public List<BaiDirectedRoad> DirectedRoads { get; } = new List<BaiDirectedRoad>();
        public List<BaiDirectedRoad> RaceDirectedRoads { get; } = new List<BaiDirectedRoad>();
        public List<BaiSidewalkStrip> SidewalkStrips { get; } = new List<BaiSidewalkStrip>();
        public List<BaiCableCarTrack> CableCarTracks { get; } = new List<BaiCableCarTrack>();
        public List<BaiIntersection> Intersections { get; } = new List<BaiIntersection>();
        public Dictionary<int, BaiIntersection> IntersectionById { get; } = new Dictionary<int, BaiIntersection>();
        public Dictionary<int, short> PathFlagsById { get; } = new Dictionary<int, short>();

        public bool IsValidSpawnIntersection(BaiIntersection intersection)
        {
            if (intersection == null) return false;
            if (intersection.ConnectedPathIds == null || intersection.ConnectedPathIds.Length == 0) return false;
            if (intersection.OutgoingRoads.Count == 0 && intersection.IncomingRoads.Count == 0) return false;

            // AIPATH_ALLEYWAY = 0x02, AIPATH_FREEWAY = 0x04
            const short invalidFlags = 0x02 | 0x04;
            for (int i = 0; i < intersection.ConnectedPathIds.Length; i++)
            {
                int pid = intersection.ConnectedPathIds[i];
                if (PathFlagsById.TryGetValue(pid, out short flags) && (flags & invalidFlags) != 0)
                {
                    return false;
                }
            }
            return true;
        }

        public bool TryGetRandomSpawnIntersectionPose(out Vector3 spawnPos, out Quaternion spawnRot)
        {
            spawnPos = Vector3.zero;
            spawnRot = Quaternion.identity;
            if (Intersections.Count == 0) return false;

            var valid = new List<BaiIntersection>(Intersections.Count);
            for (int i = 0; i < Intersections.Count; i++)
            {
                if (IsValidSpawnIntersection(Intersections[i]))
                {
                    valid.Add(Intersections[i]);
                }
            }

            BaiIntersection chosen = valid.Count > 0
                ? valid[UnityEngine.Random.Range(0, valid.Count)]
                : Intersections[UnityEngine.Random.Range(0, Intersections.Count)];

            spawnPos = chosen.Center + Vector3.up * 1.5f;
            spawnRot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            return true;
        }

        public static MM2AiMapData Load(string cityName, string raceDir, string raceName)
        {
            if (string.IsNullOrEmpty(cityName)) return null;

            var aiData = new MM2AiMapData
            {
                CityName = cityName.ToLowerInvariant()
            };

            // 1. Parse city/<city>.aimap or city/<city>/<city>.aimap
            string cityAimapPath = $"city/{aiData.CityName}.aimap";
            string cityAimapText = MM2ArchiveSystem.ReadFileText(cityAimapPath);
            if (string.IsNullOrEmpty(cityAimapText))
            {
                cityAimapText = MM2ArchiveSystem.ReadFileText($"city/{aiData.CityName}/{aiData.CityName}.aimap");
            }
            if (!string.IsNullOrEmpty(cityAimapText))
            {
                aiData.ParseCityAimap(cityAimapText);
            }
            else
            {
                aiData.PopulateDefaultAmbients();
            }

            // 2. Parse race/<raceDir>/<raceName>.aimap if available
            if (!string.IsNullOrEmpty(raceDir) && !string.IsNullOrEmpty(raceName))
            {
                string raceAimapPath = $"race/{raceDir}/{raceName}.aimap";
                string raceAimapText = MM2ArchiveSystem.ReadFileText(raceAimapPath);
                if (!string.IsNullOrEmpty(raceAimapText))
                {
                    aiData.ParseRaceAimap(raceAimapText, raceDir);
                }
            }

            // 3. Parse binary road graph city/<city>.bai or city/<city>/<city>.bai
            string baiPath = $"city/{aiData.CityName}.bai";
            byte[] baiBytes = MM2ArchiveSystem.ReadFileBytes(baiPath);
            if (baiBytes == null || baiBytes.Length < 8)
            {
                baiPath = $"city/{aiData.CityName}/{aiData.CityName}.bai";
                baiBytes = MM2ArchiveSystem.ReadFileBytes(baiPath);
            }
            if (baiBytes != null && baiBytes.Length >= 8)
            {
                aiData.ParseBai(baiBytes);
            }
            else if (MM2OsmCitySystem.HasOsmOrObjCity(aiData.CityName))
            {
                if (!MM2OsmCitySystem.PopulateAiRoadNetwork(aiData))
                {
                    Debug.LogWarning($"[MM2AiMapData] Could not build AI road graph from OSM/OBJ for '{aiData.CityName}'.");
                }
            }
            else
            {
                Debug.LogWarning($"[MM2AiMapData] Could not load binary AI map '{baiPath}'.");
            }

            return aiData;
        }

        private void PopulateDefaultAmbients()
        {
            bool isLondon = string.Equals(CityName, "london", StringComparison.OrdinalIgnoreCase);
            bool isChicago = string.Equals(CityName, "chicago", StringComparison.OrdinalIgnoreCase);
            DriveOnLeft = isLondon;

            bool useMm1Ambients = (isChicago || !MM2RuntimePkgLoader.HasMm2PkgModel("va_compact_s")) &&
                                  MM2RuntimePkgLoader.HasMm1BmsModel("vacompact");

            string[] models = useMm1Ambients
                ? new[] { "vacompact", "vasedans", "vasedanl", "vataxi", "vataxicheck", "vavan", "vapickup", "vabus", "vadelivery", "valimo", "vadiesels" }
                : (isLondon
                    ? new[] { "va_compact_s", "va_sedans_s", "va_cab_l", "va_cooper_l", "va_euro_l", "va_ddbus_l", "va_2sitersport_l" }
                    : new[] { "va_compact_s", "va_sedans_s", "va_taxi_f", "va_smallsuv_s", "va_largesuv_s", "va_eurovan_f", "va_bus_f" });

            for (int i = 0; i < models.Length; i++)
            {
                AmbientTypes.Add(new AmbientTypeEntry
                {
                    PkgName = models[i],
                    CumulativeProbability = (i + 1) / (float)models.Length,
                    Flag = 0
                });
            }
        }

        public string SampleAmbientVehiclePkg(float random01)
        {
            if (AmbientTypes.Count == 0)
            {
                return DriveOnLeft ? "va_cab_l" : "va_compact_s";
            }

            for (int i = 0; i < AmbientTypes.Count; i++)
            {
                if (random01 <= AmbientTypes[i].CumulativeProbability)
                {
                    return AmbientTypes[i].PkgName;
                }
            }

            return AmbientTypes[AmbientTypes.Count - 1].PkgName;
        }

        private void ParseCityAimap(string text)
        {
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < lines.Length)
            {
                string line = StripComment(lines[i]).Trim();
                i++;
                if (string.IsNullOrEmpty(line)) continue;

                if (line.Equals("[Speed Limit]", StringComparison.OrdinalIgnoreCase))
                {
                    while (i < lines.Length)
                    {
                        string valLine = StripComment(lines[i]).Trim();
                        i++;
                        if (string.IsNullOrEmpty(valLine)) continue;
                        if (float.TryParse(valLine, NumberStyles.Float, CultureInfo.InvariantCulture, out float spd) && spd > 0f)
                        {
                            DefaultSpeedLimit = spd;
                        }
                        break;
                    }
                }
                else if (line.Equals("[Ambients Drive On The Left]", StringComparison.OrdinalIgnoreCase))
                {
                    while (i < lines.Length)
                    {
                        string valLine = StripComment(lines[i]).Trim();
                        i++;
                        if (string.IsNullOrEmpty(valLine)) continue;
                        if (int.TryParse(valLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dol))
                        {
                            DriveOnLeft = dol != 0;
                        }
                        break;
                    }
                }
                else if (line.Equals("[Ambient Types/Density]", StringComparison.OrdinalIgnoreCase))
                {
                    int count = ReadNextInt(lines, ref i);
                    for (int k = 0; k < count && i < lines.Length;)
                    {
                        string row = StripComment(lines[i]).Trim();
                        i++;
                        if (string.IsNullOrEmpty(row)) continue;
                        if (row.StartsWith("[")) { i--; break; }

                        string[] parts = row.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 &&
                            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float prob))
                        {
                            int flag = 0;
                            if (parts.Length >= 3)
                            {
                                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out flag);
                            }
                            AmbientTypes.Add(new AmbientTypeEntry
                            {
                                PkgName = parts[0].Trim(),
                                CumulativeProbability = prob,
                                Flag = flag
                            });
                        }
                        k++;
                    }
                }
            }

            if (AmbientTypes.Count == 0)
            {
                PopulateDefaultAmbients();
            }
        }

        private void ParseRaceAimap(string text, string raceDir)
        {
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < lines.Length)
            {
                string line = StripComment(lines[i]).Trim();
                i++;
                if (string.IsNullOrEmpty(line)) continue;

                if (line.Equals("[Speed Limit]", StringComparison.OrdinalIgnoreCase))
                {
                    while (i < lines.Length)
                    {
                        string valLine = StripComment(lines[i]).Trim();
                        i++;
                        if (string.IsNullOrEmpty(valLine)) continue;
                        if (float.TryParse(valLine, NumberStyles.Float, CultureInfo.InvariantCulture, out float spd) && spd > 0f)
                        {
                            DefaultSpeedLimit = spd;
                        }
                        break;
                    }
                }
                else if (line.Equals("[Exceptions]", StringComparison.OrdinalIgnoreCase))
                {
                    int count = ReadNextInt(lines, ref i);
                    for (int k = 0; k < count && i < lines.Length;)
                    {
                        string row = StripComment(lines[i]).Trim();
                        i++;
                        if (string.IsNullOrEmpty(row)) continue;
                        if (row.StartsWith("[")) { i--; break; }

                        string[] parts = row.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3 &&
                            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pathId) &&
                            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float densityMult) &&
                            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float spdLimit))
                        {
                            RoadExceptions[pathId] = new RoadExceptionEntry
                            {
                                PathId = pathId,
                                DensityMultiplier = densityMult,
                                SpeedLimit = spdLimit
                            };
                        }
                        k++;
                    }
                }
                else if (line.Equals("[Police]", StringComparison.OrdinalIgnoreCase))
                {
                    int count = ReadNextInt(lines, ref i);
                    for (int k = 0; k < count && i < lines.Length;)
                    {
                        string row = StripComment(lines[i]).Trim();
                        i++;
                        if (string.IsNullOrEmpty(row)) continue;
                        if (row.StartsWith("[")) { i--; break; }

                        string[] parts = row.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 5 &&
                            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) &&
                            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float ay) &&
                            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float az) &&
                            float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float yawDeg))
                        {
                            int behavior = 0;
                            float maxSpeed = 50f;
                            float accelFactor = 0.8f;
                            float detectRadius = 75f;

                            if (parts.Length >= 6) int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out behavior);
                            if (parts.Length >= 7) float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out maxSpeed);
                            if (parts.Length >= 8) float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out accelFactor);
                            if (parts.Length >= 9) float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out detectRadius);

                            Vector3 copPos = MM2OsmCitySystem.HasOsmOrObjCity(CityName)
                                ? MM2OsmCitySystem.ConvertAuthoringCoordsToUnity(CityName, ax, ay, az, snapToRoadElevation: true)
                                : new Vector3(-ax, ay, az);

                            PoliceSpawns.Add(new PoliceSpawnDefinition
                            {
                                PkgName = NormalizePolicePkgName(parts[0].Trim()),
                                Position = copPos,
                                Rotation = Quaternion.Euler(0f, 180f - yawDeg, 0f),
                                StartBehavior = behavior,
                                MaxSpeed = Mathf.Max(25f, maxSpeed),
                                AccelFactor = Mathf.Clamp(accelFactor, 0.3f, 1.5f),
                                DetectRadius = Mathf.Max(35f, detectRadius)
                            });
                        }
                        k++;
                    }
                }
                else if (line.Equals("[Opponent]", StringComparison.OrdinalIgnoreCase))
                {
                    int count = ReadNextInt(lines, ref i);
                    for (int k = 0; k < count && i < lines.Length;)
                    {
                        string row = StripComment(lines[i]).Trim();
                        i++;
                        if (string.IsNullOrEmpty(row)) continue;
                        if (row.StartsWith("[")) { i--; break; }

                        string[] parts = row.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var opp = new OpponentDefinition
                            {
                                PkgName = parts[0].Trim(),
                                OppFileName = parts[1].Trim()
                            };

                            if (parts.Length >= 3 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float throttle))
                            {
                                opp.MaxThrottle = Mathf.Clamp(throttle, 0.4f, 1.2f);
                            }
                            if (parts.Length >= 4 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int behType))
                            {
                                opp.BehaviorType = behType;
                            }
                            if (parts.Length >= 5 && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float spd))
                            {
                                opp.TargetSpeed = Mathf.Max(15f, spd);
                            }
                            if (parts.Length >= 6 && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float brake))
                            {
                                opp.CornerBraking = Mathf.Clamp(brake, 0.2f, 1.2f);
                            }
                            if (parts.Length >= 12 && float.TryParse(parts[parts.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float topMult))
                            {
                                opp.TopSpeedMultiplier = Mathf.Clamp(topMult, 0.7f, 1.2f);
                            }

                            ParseOpponentOppFile(opp, raceDir, CityName);
                            Opponents.Add(opp);
                        }
                        k++;
                    }
                }
            }
        }

        private static string NormalizePolicePkgName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "vpcop";
            if (MM2RuntimePkgLoader.HasMm2PkgModel(rawName) || MM2RuntimePkgLoader.HasMm1BmsModel(rawName))
            {
                return rawName;
            }
            return "vpcop";
        }

        private static void ParseOpponentOppFile(OpponentDefinition opp, string raceDir, string cityName = null)
        {
            if (opp == null || string.IsNullOrEmpty(opp.OppFileName)) return;

            string oppFile = opp.OppFileName;
            if (string.IsNullOrEmpty(System.IO.Path.GetExtension(oppFile)))
            {
                oppFile += ".opp";
            }

            string vPath = $"race/{raceDir}/{oppFile}";
            string csvText = MM2ArchiveSystem.ReadFileText(vPath);
            if (string.IsNullOrEmpty(csvText)) return;

            bool isOsmCity = !string.IsNullOrEmpty(cityName) && MM2OsmCitySystem.HasOsmOrObjCity(cityName);
            string[] lines = csvText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool parsedStartRow = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("x,", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] cols = line.Split(',');
                if (cols.Length < 4) continue;

                if (!float.TryParse(cols[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) ||
                    !float.TryParse(cols[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ay) ||
                    !float.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float az) ||
                    !float.TryParse(cols[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float col3))
                {
                    continue;
                }

                Vector3 unityPos = isOsmCity
                    ? MM2OsmCitySystem.ConvertAuthoringCoordsToUnity(cityName, ax, ay, az, snapToRoadElevation: true)
                    : new Vector3(-ax, ay, az);

                if (!parsedStartRow)
                {
                    // Row 0 in .opp files stores the opponent's starting grid position (x, y, z)
                    // and starting yaw angle in degrees in column 3 ("brake" column).
                    // Note: .opp column 3 has the opposite yaw sign to waypoints.csv column 3,
                    // so Unity yaw is (180f - col3).
                    opp.StartPosition = unityPos;
                    opp.StartRotation = Quaternion.Euler(0f, 180f - col3, 0f);
                    opp.HasValidStartPose = true;
                    parsedStartRow = true;
                }
                else
                {
                    float wpSpeed = opp.TargetSpeed;
                    if (cols.Length >= 7 &&
                        float.TryParse(cols[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float csvTargetSpeed) &&
                        csvTargetSpeed > 1f && csvTargetSpeed < 120f)
                    {
                        wpSpeed = csvTargetSpeed;
                    }

                    // Skip consecutive duplicate waypoints
                    if (opp.Waypoints.Count > 0 &&
                        (opp.Waypoints[opp.Waypoints.Count - 1].Position - unityPos).sqrMagnitude < 1f)
                    {
                        continue;
                    }

                    opp.Waypoints.Add(new OpponentWaypoint
                    {
                        Position = unityPos,
                        TargetSpeed = wpSpeed
                    });
                }
            }
        }

        private static string StripComment(string line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            int hashIdx = line.IndexOf('#');
            return hashIdx >= 0 ? line.Substring(0, hashIdx) : line;
        }

        private static int ReadNextInt(string[] lines, ref int index)
        {
            while (index < lines.Length)
            {
                string clean = StripComment(lines[index]).Trim();
                index++;
                if (string.IsNullOrEmpty(clean)) continue;
                if (int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                {
                    return val;
                }
                return 0;
            }
            return 0;
        }

        /// <summary>
        /// Parses the binary CAI1 (.bai) road network,
        /// or falls back to ParseMm1Bai for MM1 binary road networks (e.g. city/chicago/chicago.bai).
        /// </summary>
        private void ParseBai(byte[] data)
        {
            if (data == null || data.Length < 8) return;
            if (data[0] != 'C' || data[1] != 'A' || data[2] != 'I' || data[3] != '1')
            {
                ParseMm1Bai(data);
                return;
            }

            int numIntersections = BitConverter.ToInt16(data, 4);
            int numPaths = BitConverter.ToInt16(data, 6);
            int pos = 8;

            DirectedRoads.Clear();
            RaceDirectedRoads.Clear();
            SidewalkStrips.Clear();
            CableCarTracks.Clear();
            Intersections.Clear();
            IntersectionById.Clear();
            PathFlagsById.Clear();

            for (int pIdx = 0; pIdx < numPaths; pIdx++)
            {
                if (pos + 16 > data.Length) break;

                short pathId = BitConverter.ToInt16(data, pos);
                short numSections = BitConverter.ToInt16(data, pos + 2);
                short flags = BitConverter.ToInt16(data, pos + 4);
                short numPsdls = BitConverter.ToInt16(data, pos + 6);
                pos += 8;

                PathFlagsById[pathId] = flags;

                int nSec = Mathf.Max(0, (int)numSections);
                int nPsdl = Mathf.Max(0, (int)numPsdls);
                if (pos + 2 * nPsdl + 8 > data.Length) break;

                short[] psdlIds = new short[nPsdl];
                for (int m = 0; m < nPsdl; m++)
                {
                    psdlIds[m] = BitConverter.ToInt16(data, pos + m * 2);
                }
                pos += 2 * nPsdl;

                float speedLimit = BitConverter.ToSingle(data, pos);
                int subRdIdx = BitConverter.ToInt32(data, pos + 4);
                pos += 8;

                // --- Side 0 (travel direction: Sink -> Source) ---
                if (pos + 10 > data.Length) break;
                short numLanes0 = BitConverter.ToInt16(data, pos);
                short hasCableCar0 = BitConverter.ToInt16(data, pos + 2);
                short hasSubway0 = BitConverter.ToInt16(data, pos + 4);
                short numShoulders0 = BitConverter.ToInt16(data, pos + 6);
                short sidewalkFlag0 = BitConverter.ToInt16(data, pos + 8);
                pos += 10;

                int totalLanes0 = Mathf.Max(0, numLanes0 + numShoulders0);
                // Skip laneDistances0 (4 * totalLanes0 * nSec), laneOffsets0 (4 * totalLanes0), and 40-byte config
                pos += 4 * totalLanes0 * nSec + 4 * totalLanes0 + 40;
                if (pos + 12 * totalLanes0 * nSec > data.Length) break;

                Vector3[][] side0Lanes = ReadDrivingLaneVertices(data, pos, numLanes0, totalLanes0, nSec);
                pos += 12 * totalLanes0 * nSec;
                Vector3[] cableCarRail0 = null;
                if (hasCableCar0 != 0)
                {
                    if (pos + 12 * nSec <= data.Length) cableCarRail0 = ReadVector3Array(data, pos, nSec);
                    pos += 12 * nSec;
                }
                if (hasSubway0 != 0) pos += 12 * nSec;
                Vector3[] sw0Curb = (pos + 24 * nSec <= data.Length) ? ReadVector3Array(data, pos, nSec) : null;
                Vector3[] sw0Wall = (pos + 24 * nSec <= data.Length) ? ReadVector3Array(data, pos + 12 * nSec, nSec) : null;
                pos += 24 * nSec; // boundary / sidewalk rails

                // --- Side 1 (travel direction: Source -> Sink) ---
                if (pos + 10 > data.Length) break;
                short numLanes1 = BitConverter.ToInt16(data, pos);
                short hasCableCar1 = BitConverter.ToInt16(data, pos + 2);
                short hasSubway1 = BitConverter.ToInt16(data, pos + 4);
                short numShoulders1 = BitConverter.ToInt16(data, pos + 6);
                short sidewalkFlag1 = BitConverter.ToInt16(data, pos + 8);
                pos += 10;

                int totalLanes1 = Mathf.Max(0, numLanes1 + numShoulders1);
                pos += 4 * totalLanes1 * nSec + 4 * totalLanes1 + 40;
                if (pos + 12 * totalLanes1 * nSec > data.Length) break;

                Vector3[][] side1Lanes = ReadDrivingLaneVertices(data, pos, numLanes1, totalLanes1, nSec);
                pos += 12 * totalLanes1 * nSec;
                Vector3[] cableCarRail1 = null;
                if (hasCableCar1 != 0)
                {
                    if (pos + 12 * nSec <= data.Length) cableCarRail1 = ReadVector3Array(data, pos, nSec);
                    pos += 12 * nSec;
                }
                if (hasSubway1 != 0) pos += 12 * nSec;
                Vector3[] sw1Curb = (pos + 24 * nSec <= data.Length) ? ReadVector3Array(data, pos, nSec) : null;
                Vector3[] sw1Wall = (pos + 24 * nSec <= data.Length) ? ReadVector3Array(data, pos + 12 * nSec, nSec) : null;
                pos += 24 * nSec; // boundary / sidewalk rails

                // --- Center & Orientation Arrays ---
                // sectionLengths (4 * nSec), centerVertices (12 * nSec), rightDirs (12 * nSec),
                // upNormals (12 * nSec), forwardDirs (12 * nSec), boundaryDirs (12 * nSec)
                if (pos + (4 + 12 * 5) * nSec + 76 > data.Length) break;

                pos += 4 * nSec; // sectionLengths
                Vector3[] centerVertices = ReadVector3Array(data, pos, nSec);
                pos += 12 * nSec; // skip centerVertices
                pos += 12 * nSec; // skip rightDirs (horizontal cross-road vectors where y ~ 0)
                Vector3[] upNormals = ReadVector3Array(data, pos, nSec);
                pos += 12 * nSec; // skip upNormals
                pos += 12 * nSec * 2; // skip forwardDirs, boundaryDirs

                // --- End 0: Sink Intersection (reached at end of Side 1 / start of Side 0) ---
                int sinkIntersectionId = BitConverter.ToInt32(data, pos);
                short sinkWaitType = BitConverter.ToInt16(data, pos + 4);
                short sinkLightType = BitConverter.ToInt16(data, pos + 6);
                short sinkBlocked = BitConverter.ToInt16(data, pos + 8);
                float sinkStopDist = BitConverter.ToSingle(data, pos + 10);
                pos += 38; // 14 bytes + 2 * 12-byte stopLine vectors

                // --- End 1: Source Intersection (reached at end of Side 0 / start of Side 1) ---
                int sourceIntersectionId = BitConverter.ToInt32(data, pos);
                short sourceWaitType = BitConverter.ToInt16(data, pos + 4);
                short sourceLightType = BitConverter.ToInt16(data, pos + 6);
                short sourceBlocked = BitConverter.ToInt16(data, pos + 8);
                float sourceStopDist = BitConverter.ToSingle(data, pos + 10);
                pos += 38;

                if (nSec < 2) continue;

                // Record Sidewalk Strips when sidewalkFlag == 0 and not a freeway/alleyway ((flags & 0x06) == 0)
                if ((flags & 0x06) == 0)
                {
                    if (sidewalkFlag0 == 0 && sw0Curb != null && sw0Wall != null)
                    {
                        BaiSidewalkStrip strip0 = CreateSidewalkStrip(pathId, 0, sw0Curb, sw0Wall, psdlIds);
                        if (strip0 != null && strip0.TotalLength >= 4f) SidewalkStrips.Add(strip0);
                    }
                    if (sidewalkFlag1 == 0 && sw1Curb != null && sw1Wall != null)
                    {
                        BaiSidewalkStrip strip1 = CreateSidewalkStrip(pathId, 1, sw1Curb, sw1Wall, psdlIds);
                        if (strip1 != null && strip1.TotalLength >= 4f) SidewalkStrips.Add(strip1);
                    }
                }

                // Record Cable Car Tracks (Side 0: Sink -> Source, Side 1: Source -> Sink)
                if (hasCableCar0 != 0 && cableCarRail0 != null && cableCarRail0.Length >= 2)
                {
                    BaiCableCarTrack ccTrack0 = CreateCableCarTrack(
                        CableCarTracks.Count, pathId, 0, sinkIntersectionId, sourceIntersectionId, cableCarRail0);
                    if (ccTrack0.TotalLength > 0.5f) CableCarTracks.Add(ccTrack0);
                }
                if (hasCableCar1 != 0 && cableCarRail1 != null && cableCarRail1.Length >= 2)
                {
                    BaiCableCarTrack ccTrack1 = CreateCableCarTrack(
                        CableCarTracks.Count, pathId, 1, sourceIntersectionId, sinkIntersectionId, cableCarRail1);
                    if (ccTrack1.TotalLength > 0.5f) CableCarTracks.Add(ccTrack1);
                }

                // Resolve speed limit and density multiplier with race exceptions
                float effectiveSpeed = speedLimit > 1f ? speedLimit : DefaultSpeedLimit;
                float densityMult = 1f;
                if (RoadExceptions.TryGetValue(pathId, out RoadExceptionEntry ex))
                {
                    densityMult = Mathf.Max(0f, ex.DensityMultiplier);
                    if (ex.SpeedLimit > 1f) effectiveSpeed = ex.SpeedLimit;
                }

                bool isTwoWay = numLanes0 > 0 && numLanes1 > 0;
                bool isDivided = (flags & 0x08) != 0;

                // Build Directed Road for Source -> Sink (Side 1 travel direction)
                if (numLanes1 > 0 && sinkBlocked == 0)
                {
                    // In London (DriveOnLeft = true), on two-way undivided streets, traffic going Source -> Sink
                    // drives on the left side of the road (which is Side 0's lane geometry reversed from N-1..0).
                    Vector3[][] lanesForSourceToSink = (DriveOnLeft && isTwoWay && !isDivided && side0Lanes != null && side0Lanes.Length > 0)
                        ? ReverseLanePolylines(side0Lanes)
                        : side1Lanes;

                    if (lanesForSourceToSink != null && lanesForSourceToSink.Length > 0)
                    {
                        BaiDirectedRoad road1 = CreateDirectedRoad(
                            DirectedRoads.Count,
                            pathId,
                            sourceIntersectionId,
                            sinkIntersectionId,
                            effectiveSpeed,
                            sidewalkFlag1 == 3 ? 0f : densityMult,
                            sinkLightType == 1,
                            sinkWaitType != 0 && sinkLightType == 0,
                            sinkStopDist,
                            isDivided,
                            !isTwoWay,
                            lanesForSourceToSink,
                            upNormals,
                            psdlIds
                        );
                        if (road1.TotalLength > 0.5f)
                        {
                            DirectedRoads.Add(road1);
                            RaceDirectedRoads.Add(road1);
                        }
                    }
                }
                else
                {
                    // Synthesize contraflow race-only road link for Source -> Sink so race courses
                    // that traverse one-way or blocked streets in reverse can still follow road geometry.
                    Vector3[][] contraflowLanes1 = (side1Lanes != null && side1Lanes.Length > 0)
                        ? side1Lanes
                        : ((side0Lanes != null && side0Lanes.Length > 0)
                            ? ReverseLanePolylines(side0Lanes)
                            : (centerVertices != null && centerVertices.Length >= 2 ? new[] { centerVertices } : null));

                    if (contraflowLanes1 != null && contraflowLanes1.Length > 0)
                    {
                        BaiDirectedRoad cfRoad1 = CreateDirectedRoad(
                            10000 + RaceDirectedRoads.Count,
                            pathId,
                            sourceIntersectionId,
                            sinkIntersectionId,
                            effectiveSpeed,
                            0f,
                            false,
                            false,
                            0f,
                            isDivided,
                            true,
                            contraflowLanes1,
                            upNormals,
                            psdlIds
                        );
                        cfRoad1.IsContraflowForRacing = true;
                        if (cfRoad1.TotalLength > 0.5f)
                        {
                            RaceDirectedRoads.Add(cfRoad1);
                        }
                    }
                }

                // Build Directed Road for Sink -> Source (Side 0 travel direction)
                if (numLanes0 > 0 && sourceBlocked == 0)
                {
                    Vector3[][] lanesForSinkToSource = (DriveOnLeft && isTwoWay && !isDivided && side1Lanes != null && side1Lanes.Length > 0)
                        ? ReverseLanePolylines(side1Lanes)
                        : side0Lanes;

                    if (lanesForSinkToSource != null && lanesForSinkToSource.Length > 0)
                    {
                        Vector3[] reversedNormals = new Vector3[nSec];
                        for (int s = 0; s < nSec; s++)
                        {
                            reversedNormals[s] = upNormals[nSec - 1 - s];
                        }

                        BaiDirectedRoad road0 = CreateDirectedRoad(
                            DirectedRoads.Count,
                            pathId,
                            sinkIntersectionId,
                            sourceIntersectionId,
                            effectiveSpeed,
                            sidewalkFlag0 == 3 ? 0f : densityMult,
                            sourceLightType == 1,
                            sourceWaitType != 0 && sourceLightType == 0,
                            sourceStopDist,
                            isDivided,
                            !isTwoWay,
                            lanesForSinkToSource,
                            reversedNormals,
                            psdlIds
                        );
                        if (road0.TotalLength > 0.5f)
                        {
                            DirectedRoads.Add(road0);
                            RaceDirectedRoads.Add(road0);
                        }
                    }
                }
                else
                {
                    // Synthesize contraflow race-only road link for Sink -> Source
                    Vector3[][] contraflowLanes0 = (side0Lanes != null && side0Lanes.Length > 0)
                        ? side0Lanes
                        : ((side1Lanes != null && side1Lanes.Length > 0)
                            ? ReverseLanePolylines(side1Lanes)
                            : null);

                    if ((contraflowLanes0 == null || contraflowLanes0.Length == 0) && centerVertices != null && centerVertices.Length >= 2)
                    {
                        Vector3[] revCenter = new Vector3[nSec];
                        for (int s = 0; s < nSec; s++)
                        {
                            revCenter[s] = centerVertices[nSec - 1 - s];
                        }
                        contraflowLanes0 = new[] { revCenter };
                    }

                    if (contraflowLanes0 != null && contraflowLanes0.Length > 0)
                    {
                        Vector3[] reversedNormals = new Vector3[nSec];
                        for (int s = 0; s < nSec; s++)
                        {
                            reversedNormals[s] = upNormals[nSec - 1 - s];
                        }

                        BaiDirectedRoad cfRoad0 = CreateDirectedRoad(
                            10000 + RaceDirectedRoads.Count,
                            pathId,
                            sinkIntersectionId,
                            sourceIntersectionId,
                            effectiveSpeed,
                            0f,
                            false,
                            false,
                            0f,
                            isDivided,
                            true,
                            contraflowLanes0,
                            reversedNormals,
                            psdlIds
                        );
                        cfRoad0.IsContraflowForRacing = true;
                        if (cfRoad0.TotalLength > 0.5f)
                        {
                            RaceDirectedRoads.Add(cfRoad0);
                        }
                    }
                }
            }

            // Read Intersections
            for (int iIdx = 0; iIdx < numIntersections; iIdx++)
            {
                if (pos + 18 > data.Length) break;

                short intId = BitConverter.ToInt16(data, pos);
                short roomId = BitConverter.ToInt16(data, pos + 2);
                Vector3 center = ReadUnityVector3(data, pos + 4);
                short numConn = BitConverter.ToInt16(data, pos + 16);
                pos += 18;

                int nConn = Mathf.Max(0, (int)numConn);
                if (pos + 4 * nConn > data.Length) break;

                int[] connPaths = new int[nConn];
                for (int c = 0; c < nConn; c++)
                {
                    connPaths[c] = BitConverter.ToInt32(data, pos + c * 4);
                }
                pos += 4 * nConn;

                var intersection = new BaiIntersection
                {
                    Id = intId,
                    RoomId = roomId,
                    Center = center,
                    ConnectedPathIds = connPaths,
                    LightCycleOffset = (intId * 3.7f) % 20f
                };

                Intersections.Add(intersection);
                IntersectionById[intId] = intersection;
            }

            // Link directed roads to their start/end intersections
            for (int r = 0; r < DirectedRoads.Count; r++)
            {
                BaiDirectedRoad road = DirectedRoads[r];
                if (IntersectionById.TryGetValue(road.StartIntersectionId, out BaiIntersection startInt))
                {
                    startInt.OutgoingRoads.Add(road);
                }
                if (IntersectionById.TryGetValue(road.EndIntersectionId, out BaiIntersection endInt))
                {
                    endInt.IncomingRoads.Add(road);
                }
            }

            for (int r = 0; r < RaceDirectedRoads.Count; r++)
            {
                BaiDirectedRoad road = RaceDirectedRoads[r];
                if (IntersectionById.TryGetValue(road.StartIntersectionId, out BaiIntersection startInt))
                {
                    startInt.RaceOutgoingRoads.Add(road);
                }
            }

            Debug.Log($"[MM2AiMapData] Loaded '{CityName}.bai': {DirectedRoads.Count} directed road links, {Intersections.Count} intersections, {AmbientTypes.Count} ambient vehicle types, {PoliceSpawns.Count} police spawns, {Opponents.Count} opponents.");
        }

        private void ParseMm1Bai(byte[] data)
        {
            if (data == null || data.Length < 8) return;

            int numIntersections = BitConverter.ToUInt16(data, 0);
            int numPaths = BitConverter.ToUInt16(data, 2);
            if (numIntersections <= 0 || numIntersections > 4096 || numPaths <= 0 || numPaths > 16384)
            {
                Debug.LogWarning("[MM2AiMapData] Invalid MM1 .bai header counts.");
                return;
            }

            DirectedRoads.Clear();
            RaceDirectedRoads.Clear();
            SidewalkStrips.Clear();
            CableCarTracks.Clear();
            Intersections.Clear();
            IntersectionById.Clear();
            PathFlagsById.Clear();

            int pos = 4;
            for (int pIdx = 0; pIdx < numPaths; pIdx++)
            {
                if (pos + 168 > data.Length) break;

                ushort pathId = BitConverter.ToUInt16(data, pos);
                int nSec = BitConverter.ToUInt16(data, pos + 2);
                int nLanes = BitConverter.ToUInt16(data, pos + 4);
                int nSw = BitConverter.ToUInt16(data, pos + 6);

                int recordSize = 168 + nSec * (100 + 16 * (nLanes + nSw));
                if (recordSize <= 0 || pos + recordSize > data.Length) break;

                float speedLimit = BitConverter.ToSingle(data, pos + 28);
                int twinPathId = BitConverter.ToInt32(data, pos + 64);
                int stopLightType = BitConverter.ToInt32(data, pos + 68);
                int blockedFlag = BitConverter.ToInt32(data, pos + 72);

                int arrOff = pos + 76 + 4 * (nLanes + nSw + 1) * nSec;
                int endIntersectionId = BitConverter.ToInt32(data, arrOff);
                int startIntersectionId = BitConverter.ToInt32(data, arrOff + 4);

                int vOff = arrOff + 8;
                Vector3[][] drivingLanes = ReadDrivingLaneVertices(data, vOff, nLanes, nLanes, nSec);
                int swOff = vOff + 12 * nLanes * nSec;
                Vector3[] mm1SwPts = (nSw > 0 && swOff + 12 * nSec <= pos + recordSize)
                    ? ReadVector3Array(data, swOff, nSec)
                    : null;
                int centerOff = vOff + 12 * (nLanes + nSw) * nSec;
                Vector3[] mm1CenterPts = (centerOff + 12 * nSec <= pos + recordSize)
                    ? ReadVector3Array(data, centerOff, nSec)
                    : null;
                int upOff = vOff + 12 * (nLanes + nSw + 1) * nSec;
                Vector3[] upNormals = (upOff + 12 * nSec <= pos + recordSize)
                    ? ReadVector3Array(data, upOff, nSec)
                    : new Vector3[nSec];

                pos += recordSize;

                PathFlagsById[pathId] = 0;
                if (nSec < 2 || drivingLanes == null || drivingLanes.Length == 0) continue;

                // Extract MM1 sidewalk strip using interior sections (1 .. nSec-2 when nSec >= 3)
                if (mm1SwPts != null && mm1CenterPts != null && nSec >= 3 && blockedFlag == 0)
                {
                    int swLen = nSec - 2;
                    Vector3[] swCenter = new Vector3[swLen];
                    Vector3[] swCurb = new Vector3[swLen];
                    Vector3[] swWall = new Vector3[swLen];
                    Vector3[] outerLane = drivingLanes[drivingLanes.Length - 1];
                    for (int s = 0; s < swLen; s++)
                    {
                        int srcIdx = s + 1;
                        Vector3 swPt = mm1SwPts[srcIdx] + Vector3.up * 0.08f;
                        Vector3 rdPt = outerLane[srcIdx] + Vector3.up * 0.08f;
                        Vector3 outDir = swPt - mm1CenterPts[srcIdx];
                        outDir.y = 0f;
                        outDir = outDir.sqrMagnitude > 0.0001f ? outDir.normalized : Vector3.right;
                        swCenter[s] = swPt;
                        swCurb[s] = Vector3.Lerp(swPt, rdPt, 0.35f);
                        swWall[s] = swPt + outDir * 1.8f;
                    }
                    BaiSidewalkStrip mm1Strip = CreateSidewalkStripFromArrays(pathId, 0, swCenter, swCurb, swWall, Array.Empty<short>());
                    if (mm1Strip != null && mm1Strip.TotalLength >= 4f)
                    {
                        SidewalkStrips.Add(mm1Strip);
                    }
                }

                float effectiveSpeed = speedLimit > 1f ? speedLimit : DefaultSpeedLimit;
                float densityMult = 1f;
                if (RoadExceptions.TryGetValue(pathId, out RoadExceptionEntry ex))
                {
                    densityMult = Mathf.Max(0f, ex.DensityMultiplier);
                    if (ex.SpeedLimit > 1f) effectiveSpeed = ex.SpeedLimit;
                }

                bool isOneWay = twinPathId < 0;
                bool hasTrafficLight = stopLightType == 1;
                bool hasStopSign = stopLightType == 5;

                BaiDirectedRoad road = CreateDirectedRoad(
                    blockedFlag == 0 ? DirectedRoads.Count : (10000 + RaceDirectedRoads.Count),
                    pathId,
                    startIntersectionId,
                    endIntersectionId,
                    effectiveSpeed,
                    blockedFlag == 0 ? densityMult : 0f,
                    hasTrafficLight,
                    hasStopSign,
                    0f,
                    false,
                    isOneWay,
                    drivingLanes,
                    upNormals,
                    Array.Empty<short>()
                );

                if (road.TotalLength > 0.5f)
                {
                    if (blockedFlag == 0)
                    {
                        DirectedRoads.Add(road);
                        RaceDirectedRoads.Add(road);
                    }
                    else
                    {
                        road.IsContraflowForRacing = true;
                        RaceDirectedRoads.Add(road);
                    }

                    if (isOneWay)
                    {
                        Vector3[][] revLanes = ReverseLanePolylines(drivingLanes);
                        Vector3[] revNormals = new Vector3[nSec];
                        for (int s = 0; s < nSec; s++)
                        {
                            revNormals[s] = upNormals[nSec - 1 - s];
                        }

                        BaiDirectedRoad cfRoad = CreateDirectedRoad(
                            10000 + RaceDirectedRoads.Count,
                            pathId,
                            endIntersectionId,
                            startIntersectionId,
                            effectiveSpeed,
                            0f,
                            false,
                            false,
                            0f,
                            false,
                            true,
                            revLanes,
                            revNormals,
                            Array.Empty<short>()
                        );
                        cfRoad.IsContraflowForRacing = true;
                        RaceDirectedRoads.Add(cfRoad);
                    }
                }
            }

            var sumPosByInt = new Vector3[numIntersections];
            var countByInt = new int[numIntersections];
            var connPathsByInt = new List<int>[numIntersections];
            for (int i = 0; i < numIntersections; i++)
            {
                connPathsByInt[i] = new List<int>(4);
            }

            for (int r = 0; r < RaceDirectedRoads.Count; r++)
            {
                BaiDirectedRoad road = RaceDirectedRoads[r];
                if (road.IsContraflowForRacing && DirectedRoads.Count > 0) continue;

                int sId = road.StartIntersectionId;
                if (sId >= 0 && sId < numIntersections)
                {
                    sumPosByInt[sId] += road.StartPosition;
                    countByInt[sId]++;
                    if (!connPathsByInt[sId].Contains(road.PathId))
                    {
                        connPathsByInt[sId].Add(road.PathId);
                    }
                }

                int eId = road.EndIntersectionId;
                if (eId >= 0 && eId < numIntersections)
                {
                    sumPosByInt[eId] += road.EndPosition;
                    countByInt[eId]++;
                    if (!connPathsByInt[eId].Contains(road.PathId))
                    {
                        connPathsByInt[eId].Add(road.PathId);
                    }
                }
            }

            for (int intId = 0; intId < numIntersections; intId++)
            {
                if (countByInt[intId] == 0) continue;

                var intersection = new BaiIntersection
                {
                    Id = intId,
                    RoomId = 0,
                    Center = sumPosByInt[intId] / countByInt[intId],
                    ConnectedPathIds = connPathsByInt[intId].ToArray(),
                    LightCycleOffset = (intId * 3.7f) % 20f
                };

                Intersections.Add(intersection);
                IntersectionById[intId] = intersection;
            }

            for (int r = 0; r < DirectedRoads.Count; r++)
            {
                BaiDirectedRoad road = DirectedRoads[r];
                if (IntersectionById.TryGetValue(road.StartIntersectionId, out BaiIntersection startInt))
                {
                    startInt.OutgoingRoads.Add(road);
                }
                if (IntersectionById.TryGetValue(road.EndIntersectionId, out BaiIntersection endInt))
                {
                    endInt.IncomingRoads.Add(road);
                }
            }

            for (int r = 0; r < RaceDirectedRoads.Count; r++)
            {
                BaiDirectedRoad road = RaceDirectedRoads[r];
                if (IntersectionById.TryGetValue(road.StartIntersectionId, out BaiIntersection startInt))
                {
                    startInt.RaceOutgoingRoads.Add(road);
                }
            }

            Debug.Log($"[MM2AiMapData] Loaded MM1 '{CityName}.bai': {DirectedRoads.Count} directed road links, {Intersections.Count} intersections, {AmbientTypes.Count} ambient vehicle types, {PoliceSpawns.Count} police spawns, {Opponents.Count} opponents.");
        }

        private static BaiDirectedRoad CreateDirectedRoad(
            int roadIndex,
            int pathId,
            int startIntId,
            int endIntId,
            float speedLimit,
            float densityMult,
            bool hasTrafficLight,
            bool hasStopSign,
            float stopDist,
            bool isDivided,
            bool isOneWay,
            Vector3[][] lanes,
            Vector3[] upNormals,
            short[] psdlIds)
        {
            Vector3[] primaryLane = lanes[0];
            int n = primaryLane.Length;
            float[] cumDist = new float[n];
            float totalLen = 0f;
            cumDist[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                totalLen += Vector3.Distance(primaryLane[i - 1], primaryLane[i]);
                cumDist[i] = totalLen;
            }

            Vector3 startPos = primaryLane[0];
            Vector3 endPos = primaryLane[n - 1];
            Vector3 startTan = (primaryLane[Mathf.Min(1, n - 1)] - startPos);
            startTan.y = 0f;
            startTan = startTan.sqrMagnitude > 0.0001f ? startTan.normalized : Vector3.forward;

            Vector3 endTan = (endPos - primaryLane[Mathf.Max(0, n - 2)]);
            endTan.y = 0f;
            endTan = endTan.sqrMagnitude > 0.0001f ? endTan.normalized : startTan;

            return new BaiDirectedRoad
            {
                RoadIndex = roadIndex,
                PathId = pathId,
                StartIntersectionId = startIntId,
                EndIntersectionId = endIntId,
                SpeedLimit = speedLimit,
                DensityMultiplier = densityMult,
                HasTrafficLightAtEnd = hasTrafficLight,
                HasStopSignAtEnd = hasStopSign,
                StopDistAtEnd = Mathf.Clamp(stopDist, 0f, Mathf.Max(0f, totalLen * 0.4f)),
                IsDividedOrFreeway = isDivided,
                IsOneWay = isOneWay,
                Lanes = lanes,
                UpNormals = upNormals,
                CumulativeDistances = cumDist,
                TotalLength = totalLen,
                StartPosition = startPos,
                EndPosition = endPos,
                StartTangent = startTan,
                EndTangent = endTan,
                PsdlRoomIds = psdlIds
            };
        }

        private static BaiSidewalkStrip CreateSidewalkStrip(
            int pathId,
            int sideIdx,
            Vector3[] curbPts,
            Vector3[] wallPts,
            short[] psdlIds)
        {
            if (curbPts == null || wallPts == null || curbPts.Length < 2 || wallPts.Length != curbPts.Length)
            {
                return null;
            }

            int n = curbPts.Length;
            Vector3[] centerPts = new Vector3[n];
            Vector3[] safeCurb = new Vector3[n];
            Vector3[] safeWall = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                centerPts[i] = Vector3.Lerp(curbPts[i], wallPts[i], 0.52f);
                safeCurb[i] = Vector3.Lerp(curbPts[i], wallPts[i], 0.18f);
                safeWall[i] = Vector3.Lerp(curbPts[i], wallPts[i], 0.88f);
            }

            return CreateSidewalkStripFromArrays(pathId, sideIdx, centerPts, safeCurb, safeWall, psdlIds);
        }

        private static BaiSidewalkStrip CreateSidewalkStripFromArrays(
            int pathId,
            int sideIdx,
            Vector3[] centerPts,
            Vector3[] curbPts,
            Vector3[] wallPts,
            short[] psdlIds)
        {
            if (centerPts == null || centerPts.Length < 2) return null;

            int n = centerPts.Length;
            float[] cumDist = new float[n];
            float totalLen = 0f;
            cumDist[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                totalLen += Vector3.Distance(centerPts[i - 1], centerPts[i]);
                cumDist[i] = totalLen;
            }

            return new BaiSidewalkStrip
            {
                PathId = pathId,
                SideIndex = sideIdx,
                CenterPoints = centerPts,
                CurbPoints = curbPts,
                WallPoints = wallPts,
                CumulativeDistances = cumDist,
                TotalLength = totalLen,
                PsdlRoomIds = psdlIds
            };
        }

        private static BaiCableCarTrack CreateCableCarTrack(
            int trackIndex,
            int pathId,
            int sideIdx,
            int startIntId,
            int endIntId,
            Vector3[] railPoints)
        {
            int n = railPoints.Length;
            float[] cumDist = new float[n];
            float totalLen = 0f;
            cumDist[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                totalLen += Vector3.Distance(railPoints[i - 1], railPoints[i]);
                cumDist[i] = totalLen;
            }

            Vector3 startTan = railPoints[Mathf.Min(1, n - 1)] - railPoints[0];
            startTan.y = 0f;
            startTan = startTan.sqrMagnitude > 0.0001f ? startTan.normalized : Vector3.forward;

            Vector3 endTan = railPoints[n - 1] - railPoints[Mathf.Max(0, n - 2)];
            endTan.y = 0f;
            endTan = endTan.sqrMagnitude > 0.0001f ? endTan.normalized : startTan;

            return new BaiCableCarTrack
            {
                TrackIndex = trackIndex,
                PathId = pathId,
                SideIndex = sideIdx,
                StartIntersectionId = startIntId,
                EndIntersectionId = endIntId,
                RailPoints = railPoints,
                CumulativeDistances = cumDist,
                TotalLength = totalLen,
                StartTangent = startTan,
                EndTangent = endTan
            };
        }

        private static Vector3[][] ReadDrivingLaneVertices(byte[] data, int offset, int numDrivingLanes, int totalLanes, int nSec)
        {
            if (numDrivingLanes <= 0 || totalLanes <= 0 || nSec <= 0) return null;

            int count = Mathf.Min(numDrivingLanes, totalLanes);
            Vector3[][] lanes = new Vector3[count][];
            for (int l = 0; l < count; l++)
            {
                Vector3[] pts = new Vector3[nSec];
                int laneBase = offset + l * nSec * 12;
                for (int s = 0; s < nSec; s++)
                {
                    pts[s] = ReadUnityVector3(data, laneBase + s * 12);
                }
                lanes[l] = pts;
            }
            return lanes;
        }

        private static Vector3[][] ReverseLanePolylines(Vector3[][] sourceLanes)
        {
            if (sourceLanes == null) return null;
            Vector3[][] result = new Vector3[sourceLanes.Length][];
            for (int l = 0; l < sourceLanes.Length; l++)
            {
                Vector3[] src = sourceLanes[l];
                int n = src.Length;
                Vector3[] rev = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    rev[i] = src[n - 1 - i];
                }
                result[l] = rev;
            }
            return result;
        }

        private static Vector3[] ReadVector3Array(byte[] data, int offset, int count)
        {
            Vector3[] arr = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                arr[i] = ReadUnityVector3(data, offset + i * 12);
            }
            return arr;
        }

        private static Vector3 ReadUnityVector3(byte[] data, int offset)
        {
            float x = BitConverter.ToSingle(data, offset);
            float y = BitConverter.ToSingle(data, offset + 4);
            float z = BitConverter.ToSingle(data, offset + 8);
            return new Vector3(-x, y, z);
        }

        /// <summary>
        /// Chooses a valid outgoing road at an intersection for ambient traffic or patrol cars,
        /// avoiding immediate U-turns back onto the same road unless it is a dead end.
        /// </summary>
        public BaiDirectedRoad ChooseNextOutgoingRoad(BaiDirectedRoad currentRoad, System.Random rng)
        {
            if (currentRoad == null) return null;
            if (!IntersectionById.TryGetValue(currentRoad.EndIntersectionId, out BaiIntersection inter))
            {
                return null;
            }

            if (inter.OutgoingRoads.Count == 0) return null;
            if (inter.OutgoingRoads.Count == 1) return inter.OutgoingRoads[0];

            // Collect non-U-turn roads with positive density
            var candidates = new List<BaiDirectedRoad>(inter.OutgoingRoads.Count);
            float totalWeight = 0f;

            for (int i = 0; i < inter.OutgoingRoads.Count; i++)
            {
                BaiDirectedRoad next = inter.OutgoingRoads[i];
                if (next.PathId == currentRoad.PathId && next.EndIntersectionId == currentRoad.StartIntersectionId)
                {
                    continue; // Avoid U-turn
                }
                if (next.DensityMultiplier <= 0.001f) continue;

                candidates.Add(next);
                // Favor continuing straight or gentle turns slightly more than sharp turns
                float align = Mathf.Clamp01(Vector3.Dot(currentRoad.EndTangent, next.StartTangent) * 0.5f + 0.6f);
                totalWeight += Mathf.Max(0.15f, align * next.DensityMultiplier * next.LaneCount);
            }

            if (candidates.Count == 0)
            {
                // Fallback to any non-U-turn road even if density is 0, or U-turn if dead-end
                for (int i = 0; i < inter.OutgoingRoads.Count; i++)
                {
                    BaiDirectedRoad next = inter.OutgoingRoads[i];
                    if (next.PathId != currentRoad.PathId)
                    {
                        candidates.Add(next);
                    }
                }
                if (candidates.Count == 0) return inter.OutgoingRoads[0];
                return candidates[rng.Next(candidates.Count)];
            }

            double roll = rng.NextDouble() * totalWeight;
            float accum = 0f;
            for (int i = 0; i < candidates.Count; i++)
            {
                BaiDirectedRoad next = candidates[i];
                float align = Mathf.Clamp01(Vector3.Dot(currentRoad.EndTangent, next.StartTangent) * 0.5f + 0.6f);
                accum += Mathf.Max(0.15f, align * next.DensityMultiplier * next.LaneCount);
                if (roll <= accum)
                {
                    return next;
                }
            }

            return candidates[candidates.Count - 1];
        }

        public BaiIntersection FindNearestIntersection(Vector3 worldPos)
        {
            BaiIntersection best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < Intersections.Count; i++)
            {
                BaiIntersection inter = Intersections[i];
                if (inter.OutgoingRoads.Count == 0 && inter.IncomingRoads.Count == 0) continue;

                float dx = inter.Center.x - worldPos.x;
                float dz = inter.Center.z - worldPos.z;
                float dy = Mathf.Abs(inter.Center.y - worldPos.y);
                // Strongly weight vertical separation so an overpass never snaps to a street underneath it (or vice versa)
                float vertPenalty = (dy <= 3.5f) ? (dy * dy * 16f) : (dy * dy * 100f + 2000f);
                float score = dx * dx + dz * dz + vertPenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = inter;
                }
            }
            return best;
        }

        public BaiDirectedRoad FindNearestRoad(Vector3 worldPos, Vector3 preferredDir, out int bestLane, out float bestDistAlongRoad)
        {
            return FindNearestRoadInternal(worldPos, preferredDir, maxHorizontalDist: 1000f, maxVerticalDiff: 1000f, out bestLane, out bestDistAlongRoad);
        }

        /// <summary>
        /// Finds the closest directed road segment on the same vertical road deck (rejecting roads
        /// that are on an overpass above or surface street below beyond <paramref name="maxVerticalDiff"/>).
        /// </summary>
        public BaiDirectedRoad FindNearestRoadOnSameDeck(
            Vector3 worldPos,
            Vector3 preferredDir,
            float maxHorizontalDist,
            float maxVerticalDiff,
            out int bestLane,
            out float bestDistAlongRoad)
        {
            return FindNearestRoadInternal(worldPos, preferredDir, maxHorizontalDist, maxVerticalDiff, out bestLane, out bestDistAlongRoad);
        }

        private BaiDirectedRoad FindNearestRoadInternal(
            Vector3 worldPos,
            Vector3 preferredDir,
            float maxHorizontalDist,
            float maxVerticalDiff,
            out int bestLane,
            out float bestDistAlongRoad)
        {
            bestLane = 0;
            bestDistAlongRoad = 0f;
            BaiDirectedRoad bestRoad = null;
            float bestScore = float.MaxValue;
            float maxHorizSq = maxHorizontalDist * maxHorizontalDist;
            bool hasPrefDir = preferredDir.sqrMagnitude > 0.01f;
            if (hasPrefDir)
            {
                preferredDir.y = 0f;
                if (preferredDir.sqrMagnitude > 0.0001f) preferredDir.Normalize();
                else hasPrefDir = false;
            }

            for (int r = 0; r < DirectedRoads.Count; r++)
            {
                BaiDirectedRoad road = DirectedRoads[r];
                if (road.Lanes == null || road.Lanes.Length == 0) continue;

                // Quick bounding check using road endpoints
                Vector3 mid = (road.StartPosition + road.EndPosition) * 0.5f;
                float halfSpan = road.TotalLength * 0.6f + 60f;
                if ((worldPos - mid).sqrMagnitude > halfSpan * halfSpan) continue;

                Vector3[] pts = road.Lanes[0];
                for (int s = 0; s < pts.Length - 1; s++)
                {
                    Vector3 a = pts[s];
                    Vector3 b = pts[s + 1];
                    Vector3 ab = b - a;
                    float lenSq = ab.sqrMagnitude;
                    float t = lenSq > 0.0001f ? Mathf.Clamp01(Vector3.Dot(worldPos - a, ab) / lenSq) : 0f;
                    Vector3 proj = a + ab * t;

                    float dx = worldPos.x - proj.x;
                    float dz = worldPos.z - proj.z;
                    float horizSq = dx * dx + dz * dz;
                    float dy = Mathf.Abs(worldPos.y - proj.y);

                    if (horizSq > maxHorizSq || dy > maxVerticalDiff)
                    {
                        continue;
                    }

                    // Penalize vertical differences so roads on the same deck/elevation are always chosen
                    float vertPenalty = (dy <= 3.5f) ? (dy * dy * 16f) : (dy * dy * 120f + 2500f);
                    float score = horizSq + vertPenalty;

                    if (hasPrefDir && lenSq > 0.0001f)
                    {
                        Vector3 segHoriz = new Vector3(ab.x, 0f, ab.z);
                        if (segHoriz.sqrMagnitude > 0.0001f)
                        {
                            segHoriz.Normalize();
                            float dot = Vector3.Dot(segHoriz, preferredDir);
                            if (dot < -0.15f) score += 3600f; // Strongly penalize wrong-way roads
                        }
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestRoad = road;
                        bestDistAlongRoad = Mathf.Lerp(road.CumulativeDistances[s], road.CumulativeDistances[s + 1], t);
                        bestLane = 0;
                    }
                }
            }

            return bestRoad;
        }

        /// <summary>
        /// Routes between two 3D positions along elevation-matched .bai directed roads so vehicles
        /// stay on the proper overpass or underpass road deck and never float vertically between decks.
        /// Returns true if a valid road-graph route was appended to <paramref name="outPoints"/>.
        /// </summary>
        public bool AppendRoadRouteBetweenPositions(
            Vector3 startPos,
            Vector3 startForward,
            Vector3 endPos,
            Vector3 endForward,
            bool requireStrictForwardStart,
            List<Vector3> outPoints)
        {
            float directDist = Vector3.Distance(startPos, endPos);
            if (DirectedRoads.Count == 0 || directDist < 18f)
            {
                return false;
            }

            Vector3 segDir = endPos - startPos;
            segDir.y = 0f;
            if (segDir.sqrMagnitude > 0.01f) segDir.Normalize();

            Vector3 effStartFwd = startForward.sqrMagnitude > 0.01f ? startForward : segDir;
            Vector3 effEndFwd = endForward.sqrMagnitude > 0.01f ? endForward : segDir;

            BaiDirectedRoad startRoad = FindNearestRoadOnSameDeck(
                startPos,
                effStartFwd,
                maxHorizontalDist: 42f,
                maxVerticalDiff: 4.5f,
                out _,
                out float startDistAlong
            );

            BaiDirectedRoad endRoad = FindNearestRoadOnSameDeck(
                endPos,
                effEndFwd,
                maxHorizontalDist: 42f,
                maxVerticalDiff: 4.5f,
                out _,
                out float endDistAlong
            );

            if (startRoad == null || endRoad == null)
            {
                return false;
            }

            Vector3 flatStartFwd = effStartFwd;
            flatStartFwd.y = 0f;
            if (flatStartFwd.sqrMagnitude > 0.0001f) flatStartFwd.Normalize();

            // Case 1: Both points lie along the same directed road
            if (startRoad.RoadIndex == endRoad.RoadIndex && endDistAlong >= startDistAlong - 2f)
            {
                Vector3[] pts = startRoad.Lanes[0];
                for (int s = 0; s < pts.Length; s++)
                {
                    float d = startRoad.CumulativeDistances[s];
                    if (d <= startDistAlong + 3.0f || d >= endDistAlong - 2.0f) continue;
                    if (requireStrictForwardStart && flatStartFwd.sqrMagnitude > 0.01f)
                    {
                        Vector3 delta = pts[s] - startPos;
                        delta.y = 0f;
                        if (Vector3.Dot(delta, flatStartFwd) < 8.0f) continue;
                    }
                    AppendDistinctPoint(outPoints, pts[s], 2.0f);
                }
                return true;
            }

            // Case 2: Route from startRoad.EndIntersectionId to endRoad.StartIntersectionId
            List<BaiDirectedRoad> midRoute = FindShortestRoadRoute(startRoad.EndIntersectionId, endRoad.StartIntersectionId);
            if (startRoad.EndIntersectionId != endRoad.StartIntersectionId && (midRoute == null || midRoute.Count == 0))
            {
                return false;
            }

            float routeLen = Mathf.Max(0f, startRoad.TotalLength - startDistAlong) + endDistAlong;
            if (midRoute != null)
            {
                for (int r = 0; r < midRoute.Count; r++) routeLen += midRoute[r].TotalLength;
            }

            // Reject if the road graph route is an extreme detour
            if (routeLen > directDist * 2.45f + 75f)
            {
                return false;
            }

            // 1. Append remaining forward vertices along startRoad
            if (startRoad.Lanes != null && startRoad.Lanes.Length > 0)
            {
                Vector3[] startPts = startRoad.Lanes[0];
                for (int s = 0; s < startPts.Length; s++)
                {
                    if (startRoad.CumulativeDistances[s] <= startDistAlong + 3.0f) continue;
                    if (requireStrictForwardStart && flatStartFwd.sqrMagnitude > 0.01f)
                    {
                        Vector3 delta = startPts[s] - startPos;
                        delta.y = 0f;
                        if (Vector3.Dot(delta, flatStartFwd) < 8.0f) continue;
                    }
                    AppendDistinctPoint(outPoints, startPts[s], 2.0f);
                }
            }

            // 2. Append intermediate directed roads
            if (midRoute != null)
            {
                for (int r = 0; r < midRoute.Count; r++)
                {
                    BaiDirectedRoad road = midRoute[r];
                    if (road.Lanes == null || road.Lanes.Length == 0) continue;
                    Vector3[] pts = road.Lanes[0];
                    for (int s = 0; s < pts.Length; s++)
                    {
                        if (requireStrictForwardStart && r == 0 && flatStartFwd.sqrMagnitude > 0.01f)
                        {
                            Vector3 delta = pts[s] - startPos;
                            delta.y = 0f;
                            if (Vector3.Dot(delta, flatStartFwd) < 8.0f) continue;
                        }
                        AppendDistinctPoint(outPoints, pts[s], 2.0f);
                    }
                }
            }

            // 3. Append vertices along endRoad up to endDistAlong
            if (endRoad.Lanes != null && endRoad.Lanes.Length > 0)
            {
                Vector3[] endPts = endRoad.Lanes[0];
                for (int s = 0; s < endPts.Length; s++)
                {
                    if (endRoad.CumulativeDistances[s] >= endDistAlong - 2.0f) break;
                    if (requireStrictForwardStart && flatStartFwd.sqrMagnitude > 0.01f)
                    {
                        Vector3 delta = endPts[s] - startPos;
                        delta.y = 0f;
                        if (Vector3.Dot(delta, flatStartFwd) < 8.0f) continue;
                    }
                    AppendDistinctPoint(outPoints, endPts[s], 2.0f);
                }
            }

            return true;
        }

        private static void AppendDistinctPoint(List<Vector3> list, Vector3 pt, float minDist)
        {
            if (list.Count == 0 || (list[list.Count - 1] - pt).sqrMagnitude >= minDist * minDist)
            {
                list.Add(pt);
            }
        }

        /// <summary>
        /// Computes the shortest directed road path between two intersections using Dijkstra's algorithm.
        /// </summary>
        public List<BaiDirectedRoad> FindShortestRoadRoute(int startIntersectionId, int goalIntersectionId)
        {
            var result = new List<BaiDirectedRoad>();
            if (startIntersectionId == goalIntersectionId) return result;
            if (!IntersectionById.ContainsKey(startIntersectionId) || !IntersectionById.ContainsKey(goalIntersectionId))
            {
                return result;
            }

            var dist = new Dictionary<int, float>(Intersections.Count);
            var prevRoad = new Dictionary<int, BaiDirectedRoad>(Intersections.Count);
            var visited = new HashSet<int>();
            var openList = new List<int> { startIntersectionId };
            dist[startIntersectionId] = 0f;

            while (openList.Count > 0)
            {
                int bestIdx = 0;
                int u = openList[0];
                float bestCost = dist[u];
                for (int i = 1; i < openList.Count; i++)
                {
                    int candidate = openList[i];
                    float c = dist[candidate];
                    if (c < bestCost)
                    {
                        bestCost = c;
                        u = candidate;
                        bestIdx = i;
                    }
                }

                openList.RemoveAt(bestIdx);
                if (u == goalIntersectionId) break;
                if (!visited.Add(u)) continue;

                if (!IntersectionById.TryGetValue(u, out BaiIntersection uInt)) continue;
                for (int i = 0; i < uInt.OutgoingRoads.Count; i++)
                {
                    BaiDirectedRoad edge = uInt.OutgoingRoads[i];
                    int v = edge.EndIntersectionId;
                    if (visited.Contains(v)) continue;

                    float edgeCost = edge.TotalLength;
                    float alt = bestCost + edgeCost;
                    if (!dist.TryGetValue(v, out float oldCost) || alt < oldCost)
                    {
                        dist[v] = alt;
                        prevRoad[v] = edge;
                        if (!openList.Contains(v))
                        {
                            openList.Add(v);
                        }
                    }
                }
            }

            // Also check if an undirected/reverse fallback is needed when one-way constraints block direct connection
            if (!prevRoad.ContainsKey(goalIntersectionId))
            {
                return result;
            }

            int curr = goalIntersectionId;
            int safety = 0;
            while (curr != startIntersectionId && prevRoad.TryGetValue(curr, out BaiDirectedRoad road) && safety++ < 512)
            {
                result.Add(road);
                curr = road.StartIntersectionId;
            }
            result.Reverse();
            return result;
        }

        private struct RaceRoadCandidate
        {
            public BaiDirectedRoad Road;
            public float DistAlong;
            public float LocalPenalty;
        }

        /// <summary>
        /// Builds a globally consistent, non-U-turning race route through an ordered sequence of anchors
        /// (start position + checkpoint gates or Crash Course waypoints) using dynamic programming over
        /// candidate road projections at each anchor.
        /// Accounts for MM2's symmetric checkpoint gate yaw angles (±180° around the street axis) and
        /// allows contraflow links when a race course travels against one-way streets.
        /// </summary>
        public void BuildRaceRouteThroughAnchors(
            IReadOnlyList<Vector3> anchors,
            IReadOnlyList<Vector3> anchorAxes,
            IReadOnlyList<bool> strictAxisSigns,
            List<Vector3> outPoints)
        {
            if (anchors == null || anchors.Count == 0 || outPoints == null) return;

            int m = anchors.Count;
            if (m == 1 || RaceDirectedRoads.Count == 0)
            {
                for (int i = 0; i < m; i++)
                {
                    AppendDistinctPoint(outPoints, anchors[i], 1.2f);
                }
                return;
            }

            // 1. Gather candidate road projections for each anchor
            var candLists = new List<RaceRoadCandidate>[m];
            for (int a = 0; a < m; a++)
            {
                Vector3 axis = (anchorAxes != null && a < anchorAxes.Count) ? anchorAxes[a] : Vector3.zero;
                bool strictSign = (strictAxisSigns != null && a < strictAxisSigns.Count) && strictAxisSigns[a];
                List<RaceRoadCandidate> cands = FindCandidateRaceRoads(
                    anchors[a],
                    axis,
                    strictSign,
                    maxCandidates: 6,
                    maxHorizontalDist: 24f,
                    maxVerticalDiff: 4.5f
                );
                if (cands.Count == 0)
                {
                    cands.Add(new RaceRoadCandidate { Road = null, DistAlong = 0f, LocalPenalty = 0f });
                }
                candLists[a] = cands;
            }

            // 2. Dynamic programming (Viterbi) across anchors to pick the globally consistent road sequence
            var dpCost = new float[m][];
            var dpParent = new int[m][];
            dpCost[0] = new float[candLists[0].Count];
            dpParent[0] = new int[candLists[0].Count];
            for (int c = 0; c < candLists[0].Count; c++)
            {
                dpCost[0][c] = candLists[0][c].LocalPenalty * 0.1f;
                dpParent[0][c] = -1;
            }

            for (int a = 0; a < m - 1; a++)
            {
                List<RaceRoadCandidate> prevCands = candLists[a];
                List<RaceRoadCandidate> nextCands = candLists[a + 1];
                dpCost[a + 1] = new float[nextCands.Count];
                dpParent[a + 1] = new int[nextCands.Count];
                for (int j = 0; j < nextCands.Count; j++)
                {
                    dpCost[a + 1][j] = float.MaxValue * 0.25f;
                    dpParent[a + 1][j] = 0;
                }

                float directDist = Vector3.Distance(anchors[a], anchors[a + 1]);

                for (int i = 0; i < prevCands.Count; i++)
                {
                    float baseCost = dpCost[a][i];
                    if (baseCost >= float.MaxValue * 0.2f) continue;

                    BaiDirectedRoad sr = prevCands[i].Road;
                    float sd = prevCands[i].DistAlong;

                    for (int j = 0; j < nextCands.Count; j++)
                    {
                        BaiDirectedRoad er = nextCands[j].Road;
                        float ed = nextCands[j].DistAlong;
                        float locJ = nextCands[j].LocalPenalty;

                        float transCost;
                        if (sr == null || er == null)
                        {
                            transCost = directDist * 2.2f + 150f;
                        }
                        else if (sr.RoadIndex == er.RoadIndex)
                        {
                            if (ed >= sd - 3.0f)
                            {
                                transCost = Mathf.Max(0f, ed - sd) * (sr.IsContraflowForRacing ? 1.15f : 1.0f);
                            }
                            else
                            {
                                // Going backwards on the same directed road requires a full loop or U-turn
                                transCost = directDist * 4.0f + 5000f;
                            }
                        }
                        else if (sr.PathId == er.PathId && sr.StartIntersectionId == er.EndIntersectionId)
                        {
                            // Immediate reversal onto the opposite direction of the exact same street
                            transCost = directDist * 4.0f + 5000f;
                        }
                        else
                        {
                            List<BaiDirectedRoad> mid = FindShortestRaceRoadRouteNoUturn(
                                sr.EndIntersectionId,
                                er.StartIntersectionId,
                                sr.StartIntersectionId,
                                out float midCost
                            );
                            if (mid == null)
                            {
                                transCost = directDist * 3.0f + 1500f;
                            }
                            else
                            {
                                float remStart = Mathf.Max(0f, sr.TotalLength - sd) * (sr.IsContraflowForRacing ? 1.15f : 1.0f);
                                float remEnd = ed * (er.IsContraflowForRacing ? 1.15f : 1.0f);
                                float totalRouteCost = remStart + midCost + remEnd;
                                if (mid.Count > 0)
                                {
                                    BaiDirectedRoad lastMid = mid[mid.Count - 1];
                                    if (er.EndIntersectionId == lastMid.StartIntersectionId)
                                    {
                                        totalRouteCost += 5000f;
                                    }
                                }
                                if (totalRouteCost > directDist * 2.8f + 110f)
                                {
                                    transCost = totalRouteCost + 800f;
                                }
                                else
                                {
                                    transCost = totalRouteCost;
                                }
                            }
                        }

                        float total = baseCost + transCost + locJ * 0.1f;
                        if (total < dpCost[a + 1][j])
                        {
                            dpCost[a + 1][j] = total;
                            dpParent[a + 1][j] = i;
                        }
                    }
                }
            }

            // Backtrack best candidate at each anchor
            var chosen = new RaceRoadCandidate[m];
            int bestLastIdx = 0;
            float bestLastCost = dpCost[m - 1][0];
            for (int c = 1; c < candLists[m - 1].Count; c++)
            {
                if (dpCost[m - 1][c] < bestLastCost)
                {
                    bestLastCost = dpCost[m - 1][c];
                    bestLastIdx = c;
                }
            }

            int currIdx = bestLastIdx;
            for (int a = m - 1; a >= 0; a--)
            {
                chosen[a] = candLists[a][currIdx];
                currIdx = dpParent[a][currIdx];
                if (currIdx < 0) currIdx = 0;
            }

            // 3. Stitch road geometry along the chosen candidate chain
            AppendDistinctPoint(outPoints, anchors[0], 1.2f);
            Vector3 startFwd = (anchorAxes != null && anchorAxes.Count > 0) ? anchorAxes[0] : Vector3.zero;
            startFwd.y = 0f;
            if (startFwd.sqrMagnitude > 0.0001f) startFwd.Normalize();

            for (int a = 0; a < m - 1; a++)
            {
                Vector3 sPos = anchors[a];
                Vector3 ePos = anchors[a + 1];
                float directDist = Vector3.Distance(sPos, ePos);
                BaiDirectedRoad sr = chosen[a].Road;
                float sd = chosen[a].DistAlong;
                BaiDirectedRoad er = chosen[a + 1].Road;
                float ed = chosen[a + 1].DistAlong;

                if (sr != null && er != null && directDist >= 12f)
                {
                    if (sr.RoadIndex == er.RoadIndex && ed >= sd - 3.0f)
                    {
                        Vector3[] pts = sr.Lanes[0];
                        for (int k = 0; k < pts.Length; k++)
                        {
                            float d = sr.CumulativeDistances[k];
                            if (d <= sd + 3.0f || d >= ed - 2.0f) continue;
                            if (a == 0 && startFwd.sqrMagnitude > 0.01f)
                            {
                                Vector3 delta = pts[k] - sPos;
                                delta.y = 0f;
                                if (Vector3.Dot(delta, startFwd) < 6.0f) continue;
                            }
                            AppendDistinctPoint(outPoints, pts[k], 2.0f);
                        }
                    }
                    else if (!(sr.PathId == er.PathId && sr.StartIntersectionId == er.EndIntersectionId))
                    {
                        List<BaiDirectedRoad> mid = FindShortestRaceRoadRouteNoUturn(
                            sr.EndIntersectionId,
                            er.StartIntersectionId,
                            sr.StartIntersectionId,
                            out _
                        );
                        if (mid != null)
                        {
                            float routeLen = Mathf.Max(0f, sr.TotalLength - sd) + ed;
                            for (int r = 0; r < mid.Count; r++) routeLen += mid[r].TotalLength;

                            if (routeLen <= directDist * 2.8f + 110f)
                            {
                                Vector3[] startPts = sr.Lanes[0];
                                for (int k = 0; k < startPts.Length; k++)
                                {
                                    if (sr.CumulativeDistances[k] <= sd + 3.0f) continue;
                                    if (a == 0 && startFwd.sqrMagnitude > 0.01f)
                                    {
                                        Vector3 delta = startPts[k] - sPos;
                                        delta.y = 0f;
                                        if (Vector3.Dot(delta, startFwd) < 6.0f) continue;
                                    }
                                    AppendDistinctPoint(outPoints, startPts[k], 2.0f);
                                }

                                for (int r = 0; r < mid.Count; r++)
                                {
                                    BaiDirectedRoad road = mid[r];
                                    if (road.Lanes == null || road.Lanes.Length == 0) continue;
                                    Vector3[] pts = road.Lanes[0];
                                    for (int k = 0; k < pts.Length; k++)
                                    {
                                        if (a == 0 && r == 0 && startFwd.sqrMagnitude > 0.01f)
                                        {
                                            Vector3 delta = pts[k] - sPos;
                                            delta.y = 0f;
                                            if (Vector3.Dot(delta, startFwd) < 6.0f) continue;
                                        }
                                        AppendDistinctPoint(outPoints, pts[k], 2.0f);
                                    }
                                }

                                Vector3[] endPts = er.Lanes[0];
                                for (int k = 0; k < endPts.Length; k++)
                                {
                                    if (er.CumulativeDistances[k] >= ed - 2.0f) break;
                                    if (a == 0 && startFwd.sqrMagnitude > 0.01f)
                                    {
                                        Vector3 delta = endPts[k] - sPos;
                                        delta.y = 0f;
                                        if (Vector3.Dot(delta, startFwd) < 6.0f) continue;
                                    }
                                    AppendDistinctPoint(outPoints, endPts[k], 2.0f);
                                }
                            }
                        }
                    }
                }

                AppendDistinctPoint(outPoints, ePos, 1.2f);
            }
        }

        private List<RaceRoadCandidate> FindCandidateRaceRoads(
            Vector3 worldPos,
            Vector3 axisDir,
            bool strictSign,
            int maxCandidates,
            float maxHorizontalDist,
            float maxVerticalDiff)
        {
            var results = new List<RaceRoadCandidate>(maxCandidates);
            float maxHorizSq = maxHorizontalDist * maxHorizontalDist;

            bool hasAxis = axisDir.sqrMagnitude > 0.01f;
            if (hasAxis)
            {
                axisDir.y = 0f;
                if (axisDir.sqrMagnitude > 0.0001f) axisDir.Normalize();
                else hasAxis = false;
            }

            for (int r = 0; r < RaceDirectedRoads.Count; r++)
            {
                BaiDirectedRoad road = RaceDirectedRoads[r];
                if (road.Lanes == null || road.Lanes.Length == 0) continue;

                Vector3 mid = (road.StartPosition + road.EndPosition) * 0.5f;
                float halfSpan = road.TotalLength * 0.6f + 60f;
                if ((worldPos - mid).sqrMagnitude > halfSpan * halfSpan) continue;

                Vector3[] pts = road.Lanes[0];
                float bestRoadScore = float.MaxValue;
                float bestDistAlong = 0f;

                for (int s = 0; s < pts.Length - 1; s++)
                {
                    Vector3 a = pts[s];
                    Vector3 b = pts[s + 1];
                    Vector3 ab = b - a;
                    float lenSq = ab.sqrMagnitude;
                    if (lenSq < 0.0001f) continue;

                    float t = Mathf.Clamp01(Vector3.Dot(worldPos - a, ab) / lenSq);
                    Vector3 proj = a + ab * t;

                    float dx = worldPos.x - proj.x;
                    float dz = worldPos.z - proj.z;
                    float horizSq = dx * dx + dz * dz;
                    float dy = Mathf.Abs(worldPos.y - proj.y);

                    if (horizSq > maxHorizSq || dy > maxVerticalDiff) continue;

                    Vector3 segHoriz = new Vector3(ab.x, 0f, ab.z);
                    if (segHoriz.sqrMagnitude < 0.0001f) continue;
                    segHoriz.Normalize();

                    float alignPenalty = 0f;
                    if (hasAxis)
                    {
                        float dot = Vector3.Dot(segHoriz, axisDir);
                        if (strictSign)
                        {
                            if (dot < 0.2f) continue;
                            alignPenalty = (1f - dot) * 120f;
                        }
                        else
                        {
                            // Checkpoint gate normal is collinear with the street axis up to ±180° symmetry;
                            // reject perpendicular cross-streets (|dot| < 0.35).
                            float absDot = Mathf.Abs(dot);
                            if (absDot < 0.35f) continue;
                            alignPenalty = (1f - absDot) * 180f;
                        }
                    }

                    float vertPenalty = (dy <= 3.5f) ? (dy * dy * 16f) : (dy * dy * 120f + 2500f);
                    float contraflowPenalty = road.IsContraflowForRacing ? 25f : 0f;
                    float score = horizSq + vertPenalty + alignPenalty + contraflowPenalty;

                    if (score < bestRoadScore)
                    {
                        bestRoadScore = score;
                        bestDistAlong = Mathf.Lerp(road.CumulativeDistances[s], road.CumulativeDistances[s + 1], t);
                    }
                }

                if (bestRoadScore < float.MaxValue)
                {
                    results.Add(new RaceRoadCandidate
                    {
                        Road = road,
                        DistAlong = bestDistAlong,
                        LocalPenalty = bestRoadScore
                    });
                }
            }

            results.Sort((x, y) => x.LocalPenalty.CompareTo(y.LocalPenalty));
            if (results.Count > maxCandidates)
            {
                results.RemoveRange(maxCandidates, results.Count - maxCandidates);
            }
            return results;
        }

        private List<BaiDirectedRoad> FindShortestRaceRoadRouteNoUturn(
            int startIntersectionId,
            int goalIntersectionId,
            int prevStartIntersectionId,
            out float totalCost)
        {
            totalCost = 0f;
            var result = new List<BaiDirectedRoad>();
            if (startIntersectionId == goalIntersectionId) return result;
            if (!IntersectionById.ContainsKey(startIntersectionId) || !IntersectionById.ContainsKey(goalIntersectionId))
            {
                totalCost = float.MaxValue;
                return null;
            }

            var dist = new Dictionary<long, float>(Intersections.Count * 2);
            var prevEdge = new Dictionary<long, BaiDirectedRoad>(Intersections.Count * 2);
            var prevState = new Dictionary<long, long>(Intersections.Count * 2);
            var visited = new HashSet<long>();
            var openStates = new List<long>(64);

            long initKey = ((long)startIntersectionId << 32) | (uint)(prevStartIntersectionId & 0xFFFFFFFF);
            dist[initKey] = 0f;
            openStates.Add(initKey);

            long goalStateKey = -1;

            while (openStates.Count > 0)
            {
                int bestIdx = 0;
                long uKey = openStates[0];
                float bestCost = dist[uKey];
                for (int i = 1; i < openStates.Count; i++)
                {
                    long candidate = openStates[i];
                    float c = dist[candidate];
                    if (c < bestCost)
                    {
                        bestCost = c;
                        uKey = candidate;
                        bestIdx = i;
                    }
                }

                openStates.RemoveAt(bestIdx);
                int u = (int)(uKey >> 32);
                int pInt = (int)(uKey & 0xFFFFFFFF);

                if (u == goalIntersectionId)
                {
                    goalStateKey = uKey;
                    totalCost = bestCost;
                    break;
                }

                if (!visited.Add(uKey)) continue;
                if (!IntersectionById.TryGetValue(u, out BaiIntersection uInt)) continue;

                for (int i = 0; i < uInt.RaceOutgoingRoads.Count; i++)
                {
                    BaiDirectedRoad edge = uInt.RaceOutgoingRoads[i];
                    int v = edge.EndIntersectionId;
                    long vKey = ((long)v << 32) | (uint)(u & 0xFFFFFFFF);
                    if (visited.Contains(vKey)) continue;

                    float stepCost = edge.TotalLength * (edge.IsContraflowForRacing ? 1.15f : 1.0f);
                    if (v == pInt)
                    {
                        // Strongly penalize immediate 180-degree U-turns at intersections
                        stepCost += 5000f;
                    }

                    float alt = bestCost + stepCost;
                    if (!dist.TryGetValue(vKey, out float oldCost) || alt < oldCost)
                    {
                        dist[vKey] = alt;
                        prevEdge[vKey] = edge;
                        prevState[vKey] = uKey;
                        if (!openStates.Contains(vKey))
                        {
                            openStates.Add(vKey);
                        }
                    }
                }
            }

            if (goalStateKey == -1)
            {
                totalCost = float.MaxValue;
                return null;
            }

            long currKey = goalStateKey;
            int safety = 0;
            while (currKey != initKey && prevEdge.TryGetValue(currKey, out BaiDirectedRoad road) && safety++ < 512)
            {
                result.Add(road);
                currKey = prevState[currKey];
            }
            result.Reverse();
            return result;
        }
    }
}
