using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using UnityEngine;
using UnityEngine.Rendering;
using MM2.AI;

namespace MM2.Archive
{
    /// <summary>
    /// Native support for OSM2World (.obj + .mtl) and OpenStreetMap (.osm) cities in Open Madness.
    /// Allows mixing and matching an OSM2World 3D city mesh (.obj/.mtl) and OpenStreetMap road graph (.osm)
    /// with standard Open Madness / MM2 .ar files containing races (waypoints.csv, .aimap, .opp),
    /// Crash Courses (mmcrashdata.csv, .pathset), Cruise spawn points (spawn_*.txt), rewards, and minimaps.
    /// </summary>
    public static class MM2OsmCitySystem
    {
        private const double GlobeRadiusMeters = 6371000.0;

        public class OsmProjectionInfo
        {
            public string MapName;
            public double OriginLat;
            public double OriginLon;
            public bool HasGeoOrigin;
            public Vector3 AutoCenterOffsetAge;
            public float MinUnityX = -1500f;
            public float MaxUnityX = 1500f;
            public float MinUnityZ = -1500f;
            public float MaxUnityZ = 1500f;
            public float BaseGroundY = 0f;

            public Vector2 ProjectLatLonToAgeXZ(double latDeg, double lonDeg)
            {
                if (!HasGeoOrigin)
                {
                    return Vector2.zero;
                }

                double lat0 = OriginLat * Math.PI / 180.0;
                double lon0 = OriginLon * Math.PI / 180.0;
                double lat = latDeg * Math.PI / 180.0;
                double lon = lonDeg * Math.PI / 180.0;

                double xEast = GlobeRadiusMeters * Math.Cos(lat) * Math.Sin(lon - lon0);
                double zNorth = GlobeRadiusMeters * (Math.Cos(lat0) * Math.Sin(lat) - Math.Sin(lat0) * Math.Cos(lat) * Math.Cos(lon - lon0));

                // In OSM2World ObjOutput.java: obj.x = +xEast, obj.z = -zNorth
                float ageX = (float)xEast - AutoCenterOffsetAge.x;
                float ageZ = (float)(-zNorth) - AutoCenterOffsetAge.z;
                return new Vector2(ageX, ageZ);
            }

            public Vector3 ProjectLatLonToUnity(double latDeg, double lonDeg, float elevationY = 0f)
            {
                Vector2 ageXZ = ProjectLatLonToAgeXZ(latDeg, lonDeg);
                return new Vector3(-ageXZ.x, elevationY - AutoCenterOffsetAge.y, ageXZ.y);
            }
        }

        private class MtlMaterialSpec
        {
            public string Name;
            public Color AmbientColor = Color.black;
            public bool HasAmbient;
            public Color DiffuseColor = Color.white;
            public Color SpecularColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            public Color EmissiveColor = Color.black;
            public float Alpha = 1f;
            public float Shininess = 0.1f;
            public string DiffuseTexturePath;
            public bool ClampTexture;
        }

        public struct RoadElevationSample
        {
            public Vector3 A;
            public Vector3 B;
            public Vector3 C;
            public float MinX;
            public float MaxX;
            public float MinZ;
            public float MaxZ;
            public float AvgY;
        }

        public class CachedOsmCityData
        {
            public string MapName;
            public OsmProjectionInfo Projection = new OsmProjectionInfo();
            public List<MM2CruiseSpawnLocation> AutoCruiseSpawns = new List<MM2CruiseSpawnLocation>();
            public List<(Vector3 a, Vector3 b, float width, int importance)> MapRoads = new List<(Vector3 a, Vector3 b, float width, int importance)>();
            public List<Vector3[]> MapWaterPolygons = new List<Vector3[]>();
            public List<Vector3[]> MapParkPolygons = new List<Vector3[]>();
            public List<Vector3[]> MapBuildingPolygons = new List<Vector3[]>();
            public Sprite GeneratedMapSprite;
            public Texture2D GeneratedMinimapTexture;
            internal List<RoadElevationSample> RoadTriangles = new List<RoadElevationSample>();
            internal Dictionary<long, List<int>> RoadTriSpatialGrid = new Dictionary<long, List<int>>();
        }

        private static readonly Dictionary<string, CachedOsmCityData> CityDataCache =
            new Dictionary<string, CachedOsmCityData>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Texture2D> ProceduralTextureCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        public delegate void WaterTriangleRecorder(IList<Vector3> positions, IList<int> triangles, int baseVertexOffset);

        public static bool HasOsmOrObjCity(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return false;
            return FindObjVirtualPath(mapName) != null || FindOsmVirtualPath(mapName) != null;
        }

        public static string FindObjVirtualPath(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return null;
            string clean = mapName.Trim().ToLowerInvariant();
            string[] candidates =
            {
                $"city/{clean}.obj",
                $"city/{clean}/{clean}.obj",
                $"{clean}.obj"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (MM2ArchiveSystem.FileExists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        public static string FindOsmVirtualPath(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return null;
            string clean = mapName.Trim().ToLowerInvariant();
            string[] candidates =
            {
                $"city/{clean}.osm",
                $"city/{clean}/{clean}.osm",
                $"{clean}.osm"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (MM2ArchiveSystem.FileExists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        public static string FindMtlVirtualPath(string mapName, string objVirtualPath, string mtllibName)
        {
            string clean = (mapName ?? string.Empty).Trim().ToLowerInvariant();
            var candidates = new List<string>(8);

            if (!string.IsNullOrEmpty(mtllibName))
            {
                string cleanMtl = mtllibName.Trim();
                if (!string.IsNullOrEmpty(objVirtualPath))
                {
                    int slash = objVirtualPath.LastIndexOf('/');
                    if (slash > 0)
                    {
                        candidates.Add(objVirtualPath.Substring(0, slash + 1) + cleanMtl);
                    }
                }
                candidates.Add($"city/{cleanMtl}");
                candidates.Add($"city/{clean}/{cleanMtl}");
                candidates.Add(cleanMtl);
            }

            candidates.Add($"city/{clean}.obj.mtl");
            candidates.Add($"city/{clean}.mtl");
            candidates.Add($"city/{clean}/{clean}.obj.mtl");
            candidates.Add($"city/{clean}/{clean}.mtl");
            candidates.Add($"{clean}.obj.mtl");
            candidates.Add($"{clean}.mtl");

            for (int i = 0; i < candidates.Count; i++)
            {
                if (MM2ArchiveSystem.FileExists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Discovers any standalone city/*.obj or city/*.osm files in the mounted VFS so that even if a user
        /// drops an OSM2World .obj or .osm without a tune/*.cinfo file, it is automatically listed as a playable city.
        /// </summary>
        public static List<string> DiscoverStandaloneOsmCityMapNames()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void CollectFromList(List<string> paths)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string stem = Path.GetFileNameWithoutExtension(paths[i]);
                    if (string.IsNullOrEmpty(stem)) continue;
                    if (stem.StartsWith("hudmap_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add(stem))
                    {
                        result.Add(stem.ToLowerInvariant());
                    }
                }
            }

            CollectFromList(MM2ArchiveSystem.GetFilesWithPrefixAndExtension("city", ".obj"));
            CollectFromList(MM2ArchiveSystem.GetFilesWithPrefixAndExtension("city", ".osm"));
            CollectFromList(MM2ArchiveSystem.GetFilesWithPrefixAndExtension("", ".osm"));
            return result;
        }

        public static CachedOsmCityData GetOrEnsureCityData(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return null;
            string key = mapName.Trim().ToLowerInvariant();
            if (CityDataCache.TryGetValue(key, out CachedOsmCityData cached) && cached != null)
            {
                return cached;
            }

            var data = new CachedOsmCityData
            {
                MapName = key,
                Projection = new OsmProjectionInfo { MapName = key }
            };
            CityDataCache[key] = data;

            // Try reading origin from .obj header first, then .osm <bounds>
            string objPath = FindObjVirtualPath(key);
            if (!string.IsNullOrEmpty(objPath))
            {
                TryParseObjHeaderOrigin(objPath, data.Projection);
            }

            string osmPath = FindOsmVirtualPath(key);
            if (!string.IsNullOrEmpty(osmPath) && !data.Projection.HasGeoOrigin)
            {
                TryParseOsmBoundsOrigin(osmPath, data.Projection);
            }

            return data;
        }

        /// <summary>
        /// Converts raw (x, y, z) coordinates from spawn_*.txt or waypoints.csv to Unity world space.
        /// Automatically detects if (x, z) or (x, y) are geographic (lat, lon) coordinates near the OSM city's origin
        /// and projects them to meters, or treats them as standard AGE / .obj meter coordinates (-x, y, z).
        /// </summary>
        public static Vector3 ConvertAuthoringCoordsToUnity(string mapName, float rawX, float rawY, float rawZ, bool snapToRoadElevation = false)
        {
            CachedOsmCityData data = HasOsmOrObjCity(mapName) ? GetOrEnsureCityData(mapName) : null;
            if (data != null && data.Projection != null && data.Projection.HasGeoOrigin)
            {
                double lat0 = data.Projection.OriginLat;
                double lon0 = data.Projection.OriginLon;

                // Case 1: (rawX = lat, rawY = elev, rawZ = lon)
                if (Math.Abs(rawX - lat0) <= 1.2 && Math.Abs(rawZ - lon0) <= 1.2)
                {
                    Vector3 pos = data.Projection.ProjectLatLonToUnity(rawX, rawZ, rawY);
                    if (snapToRoadElevation || Math.Abs(rawY) < 0.01f)
                    {
                        pos.y = SampleRoadElevationAt(data, pos.x, pos.z, pos.y);
                    }
                    return pos;
                }

                // Case 2: (rawX = lat, rawY = lon, rawZ = elev)
                if (Math.Abs(rawX - lat0) <= 1.2 && Math.Abs(rawY - lon0) <= 1.2)
                {
                    Vector3 pos = data.Projection.ProjectLatLonToUnity(rawX, rawY, rawZ);
                    if (snapToRoadElevation || Math.Abs(rawZ) < 0.01f)
                    {
                        pos.y = SampleRoadElevationAt(data, pos.x, pos.z, pos.y);
                    }
                    return pos;
                }
            }

            Vector3 unityPos = new Vector3(-rawX, rawY, rawZ);
            if (data != null)
            {
                unityPos.x += data.Projection.AutoCenterOffsetAge.x;
                unityPos.y -= data.Projection.AutoCenterOffsetAge.y;
                unityPos.z -= data.Projection.AutoCenterOffsetAge.z;
                if (snapToRoadElevation && Math.Abs(rawY) < 0.01f)
                {
                    unityPos.y = SampleRoadElevationAt(data, unityPos.x, unityPos.z, unityPos.y);
                }
            }
            return unityPos;
        }

        private static void TryParseObjHeaderOrigin(string objVirtualPath, OsmProjectionInfo proj)
        {
            byte[] bytes = MM2ArchiveSystem.ReadFileBytes(objVirtualPath);
            if (bytes == null || bytes.Length == 0) return;

            int headerLen = Math.Min(bytes.Length, 4096);
            string header = Encoding.UTF8.GetString(bytes, 0, headerLen);
            string[] lines = header.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("#")) continue;

                // Match OSM2World header: "# Coordinate origin (0,0,0): lat 48.137154, lon 11.576124, ele 0"
                int latIdx = line.IndexOf("lat ", StringComparison.OrdinalIgnoreCase);
                int lonIdx = line.IndexOf("lon ", StringComparison.OrdinalIgnoreCase);
                if (latIdx >= 0 && lonIdx > latIdx)
                {
                    string latPart = line.Substring(latIdx + 4);
                    int comma = latPart.IndexOf(',');
                    if (comma > 0) latPart = latPart.Substring(0, comma);

                    string lonPart = line.Substring(lonIdx + 4);
                    int comma2 = lonPart.IndexOf(',');
                    if (comma2 > 0) lonPart = lonPart.Substring(0, comma2);

                    if (double.TryParse(latPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                        double.TryParse(lonPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                    {
                        proj.OriginLat = lat;
                        proj.OriginLon = lon;
                        proj.HasGeoOrigin = true;
                        return;
                    }
                }
            }
        }

        private static void TryParseOsmBoundsOrigin(string osmVirtualPath, OsmProjectionInfo proj)
        {
            byte[] bytes = MM2ArchiveSystem.ReadFileBytes(osmVirtualPath);
            if (bytes == null || bytes.Length == 0) return;

            int headLen = Math.Min(bytes.Length, 16384);
            string head = Encoding.UTF8.GetString(bytes, 0, headLen);
            int boundsIdx = head.IndexOf("<bounds", StringComparison.OrdinalIgnoreCase);
            if (boundsIdx >= 0)
            {
                int endIdx = head.IndexOf('>', boundsIdx);
                if (endIdx > boundsIdx)
                {
                    string tag = head.Substring(boundsIdx, endIdx - boundsIdx + 1);
                    if (TryExtractXmlAttrDouble(tag, "minlat", out double minLat) &&
                        TryExtractXmlAttrDouble(tag, "minlon", out double minLon) &&
                        TryExtractXmlAttrDouble(tag, "maxlat", out double maxLat) &&
                        TryExtractXmlAttrDouble(tag, "maxlon", out double maxLon))
                    {
                        proj.OriginLat = (minLat + maxLat) * 0.5;
                        proj.OriginLon = (minLon + maxLon) * 0.5;
                        proj.HasGeoOrigin = true;
                    }
                }
            }
        }

        private static bool TryExtractXmlAttrDouble(string tagText, string attrName, out double val)
        {
            val = 0.0;
            int idx = tagText.IndexOf(attrName + "=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int q1 = tagText.IndexOfAny(new[] { '"', '\'' }, idx);
            if (q1 < 0 || q1 + 1 >= tagText.Length) return false;
            char quote = tagText[q1];
            int q2 = tagText.IndexOf(quote, q1 + 1);
            if (q2 <= q1) return false;
            string sub = tagText.Substring(q1 + 1, q2 - q1 - 1);
            return double.TryParse(sub, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }

        /// <summary>
        /// Builds the 3D city hierarchy (4x4 spatial districts, colliders, water triangles, and breakable props)
        /// from an OSM2World .obj + .mtl export (or directly from a .osm file if no .obj is present).
        /// </summary>
        public static bool BuildRuntimeOsmCity(
            CitySpec spec,
            Transform cityRoot,
            Transform bangersRoot,
            WaterTriangleRecorder recordWaterTriangles,
            out float minX,
            out float maxX,
            out float minZ,
            out float maxZ,
            out float minVertY)
        {
            minX = -1500f;
            maxX = 1500f;
            minZ = -1500f;
            maxZ = 1500f;
            minVertY = 0f;

            if (spec == null || cityRoot == null) return false;

            CachedOsmCityData cityData = GetOrEnsureCityData(spec.MapName);
            cityData.RoadTriangles.Clear();
            cityData.RoadTriSpatialGrid.Clear();

            string objPath = FindObjVirtualPath(spec.MapName);
            string osmPath = FindOsmVirtualPath(spec.MapName);

            if (!string.IsNullOrEmpty(objPath))
            {
                bool builtObj = BuildFromOsm2WorldObj(
                    spec,
                    objPath,
                    cityData,
                    cityRoot,
                    bangersRoot,
                    recordWaterTriangles,
                    out minX,
                    out maxX,
                    out minZ,
                    out maxZ,
                    out minVertY);

                if (builtObj)
                {
                    // If an accompanying .osm file is present, pre-parse its road/landmark metadata so spawns and minimap are ready
                    if (!string.IsNullOrEmpty(osmPath) && cityData.MapRoads.Count == 0)
                    {
                        EnsureOsmMetadataParsed(spec.MapName, osmPath, cityData);
                    }
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(osmPath))
            {
                return BuildProceduralGeometryFromOsmOnly(
                    spec,
                    osmPath,
                    cityData,
                    cityRoot,
                    bangersRoot,
                    recordWaterTriangles,
                    out minX,
                    out maxX,
                    out minZ,
                    out maxZ,
                    out minVertY);
            }

            return false;
        }

        private class ObjSubmeshBucket
        {
            public readonly List<Vector3> Positions = new List<Vector3>(1024);
            public readonly List<Vector3> Normals = new List<Vector3>(1024);
            public readonly List<Vector2> UVs = new List<Vector2>(1024);
            public readonly List<int> Triangles = new List<int>(1536);
        }

        private class ObjDistrictChunk
        {
            public readonly Dictionary<string, ObjSubmeshBucket> VisualByMaterial =
                new Dictionary<string, ObjSubmeshBucket>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Vector3> ColPositions = new List<Vector3>(4096);
            public readonly List<int> ColTriangles = new List<int>(6144);
        }

        private class ObjPropObjectAccumulator
        {
            public string ObjectName;
            public string GroupName;
            public readonly Dictionary<string, ObjSubmeshBucket> Buckets =
                new Dictionary<string, ObjSubmeshBucket>(StringComparer.OrdinalIgnoreCase);
            public Vector3 BoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            public Vector3 BoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            public int TotalTriangles;
        }

        private static bool BuildFromOsm2WorldObj(
            CitySpec spec,
            string objVirtualPath,
            CachedOsmCityData cityData,
            Transform cityRoot,
            Transform bangersRoot,
            WaterTriangleRecorder recordWaterTriangles,
            out float minX,
            out float maxX,
            out float minZ,
            out float maxZ,
            out float minVertY)
        {
            minX = -1500f; maxX = 1500f;
            minZ = -1500f; maxZ = 1500f;
            minVertY = 0f;

            byte[] objBytes = MM2ArchiveSystem.ReadFileBytes(objVirtualPath);
            if (objBytes == null || objBytes.Length < 16) return false;

            var rawVerts = new List<Vector3>(65536);
            var rawUVs = new List<Vector2>(65536);
            var rawNormals = new List<Vector3>(65536);
            string mtlLibFile = null;

            // Pass 1: Read v, vt, vn, mtllib, and header origin
            using (var ms = new MemoryStream(objBytes))
            using (var sr = new StreamReader(ms, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length < 2) continue;
                    char c0 = line[0];
                    if (c0 == '#')
                    {
                        if (!cityData.Projection.HasGeoOrigin && line.IndexOf("lat ", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int latIdx = line.IndexOf("lat ", StringComparison.OrdinalIgnoreCase);
                            int lonIdx = line.IndexOf("lon ", StringComparison.OrdinalIgnoreCase);
                            if (latIdx >= 0 && lonIdx > latIdx)
                            {
                                string latPart = line.Substring(latIdx + 4);
                                int comma = latPart.IndexOf(',');
                                if (comma > 0) latPart = latPart.Substring(0, comma);
                                string lonPart = line.Substring(lonIdx + 4);
                                int comma2 = lonPart.IndexOf(',');
                                if (comma2 > 0) lonPart = lonPart.Substring(0, comma2);
                                if (double.TryParse(latPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                                    double.TryParse(lonPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                                {
                                    cityData.Projection.OriginLat = lat;
                                    cityData.Projection.OriginLon = lon;
                                    cityData.Projection.HasGeoOrigin = true;
                                }
                            }
                        }
                        continue;
                    }

                    if (c0 == 'v')
                    {
                        char c1 = line[1];
                        if (c1 == ' ' || c1 == '\t')
                        {
                            if (TryParseFloat3(line, 2, out float vx, out float vy, out float vz))
                            {
                                rawVerts.Add(new Vector3(vx, vy, vz));
                            }
                        }
                        else if (c1 == 't' && line.Length > 2 && (line[2] == ' ' || line[2] == '\t'))
                        {
                            if (TryParseFloat2(line, 3, out float u, out float v))
                            {
                                rawUVs.Add(new Vector2(u, v));
                            }
                        }
                        else if (c1 == 'n' && line.Length > 2 && (line[2] == ' ' || line[2] == '\t'))
                        {
                            if (TryParseFloat3(line, 3, out float nx, out float ny, out float nz))
                            {
                                rawNormals.Add(new Vector3(-nx, ny, nz));
                            }
                        }
                    }
                    else if (c0 == 'm' && line.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase))
                    {
                        mtlLibFile = line.Substring(7).Trim();
                    }
                }
            }

            if (rawVerts.Count == 0) return false;

            // Compute bounds and auto-center if the OBJ was exported far from (0,0,0)
            Vector3 rawMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 rawMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < rawVerts.Count; i++)
            {
                Vector3 v = rawVerts[i];
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) continue;
                if (Mathf.Abs(v.x) > 500000f || Mathf.Abs(v.y) > 50000f || Mathf.Abs(v.z) > 500000f) continue;
                if (v.x < rawMin.x) rawMin.x = v.x;
                if (v.y < rawMin.y) rawMin.y = v.y;
                if (v.z < rawMin.z) rawMin.z = v.z;
                if (v.x > rawMax.x) rawMax.x = v.x;
                if (v.y > rawMax.y) rawMax.y = v.y;
                if (v.z > rawMax.z) rawMax.z = v.z;
            }

            Vector3 rawCenter = (rawMin + rawMax) * 0.5f;
            Vector3 centerOffsetAge = Vector3.zero;
            if (Mathf.Abs(rawCenter.x) > 8000f || Mathf.Abs(rawCenter.z) > 8000f || Mathf.Abs(rawMin.y) > 250f)
            {
                centerOffsetAge = new Vector3(rawCenter.x, rawMin.y, rawCenter.z);
            }
            cityData.Projection.AutoCenterOffsetAge = centerOffsetAge;

            // Transform vertices to Unity world space: (-x, y, z)
            minX = float.MaxValue; maxX = float.MinValue;
            minZ = float.MaxValue; maxZ = float.MinValue;
            minVertY = float.MaxValue;

            for (int i = 0; i < rawVerts.Count; i++)
            {
                Vector3 v = rawVerts[i] - centerOffsetAge;
                Vector3 u = new Vector3(-v.x, v.y, v.z);
                rawVerts[i] = u;
                if (u.x < minX) minX = u.x;
                if (u.x > maxX) maxX = u.x;
                if (u.z < minZ) minZ = u.z;
                if (u.z > maxZ) maxZ = u.z;
                if (u.y > -500f && u.y < minVertY) minVertY = u.y;
            }

            if (minVertY == float.MaxValue) minVertY = 0f;
            minX -= 1f; maxX += 1f;
            minZ -= 1f; maxZ += 1f;

            cityData.Projection.MinUnityX = minX;
            cityData.Projection.MaxUnityX = maxX;
            cityData.Projection.MinUnityZ = minZ;
            cityData.Projection.MaxUnityZ = maxZ;
            cityData.Projection.BaseGroundY = minVertY;

            // Parse companion .mtl file if available
            var mtlSpecs = new Dictionary<string, MtlMaterialSpec>(StringComparer.OrdinalIgnoreCase);
            string mtlPath = FindMtlVirtualPath(spec.MapName, objVirtualPath, mtlLibFile);
            if (!string.IsNullOrEmpty(mtlPath))
            {
                ParseMtlFile(mtlPath, mtlSpecs);
            }

            const int GridN = 4;
            var chunks = new ObjDistrictChunk[GridN, GridN];
            for (int gx = 0; gx < GridN; gx++)
            {
                for (int gz = 0; gz < GridN; gz++)
                {
                    chunks[gx, gz] = new ObjDistrictChunk();
                }
            }

            float cMinX = minX;
            float cMinZ = minZ;
            float spanX = Mathf.Max(1f, maxX - cMinX);
            float spanZ = Mathf.Max(1f, maxZ - cMinZ);
            ObjDistrictChunk GetChunk(float ux, float uz)
            {
                int gx = Mathf.Clamp((int)((ux - cMinX) / spanX * GridN), 0, GridN - 1);
                int gz = Mathf.Clamp((int)((uz - cMinZ) / spanZ * GridN), 0, GridN - 1);
                return chunks[gx, gz];
            }

            var breakablePropObjects = new List<ObjPropObjectAccumulator>();
            ObjPropObjectAccumulator activePropObject = null;

            string currentGroup = "default";
            string currentObject = "default";
            string currentMaterial = "ASPHALT_0";
            bool currentSkipFace = false;
            bool currentIsWater = false;
            bool currentIsRoad = false;
            bool currentIsOverlayLayer = false;
            bool currentIsBreakableProp = false;
            bool currentNoCollideFoliage = false;
            bool currentIsTreeBillboard = false;

            void UpdateClassification()
            {
                string g = currentGroup ?? string.Empty;
                string o = currentObject ?? string.Empty;
                string m = currentMaterial ?? string.Empty;

                currentSkipFace =
                    m.StartsWith("SKYBOX", StringComparison.OrdinalIgnoreCase) ||
                    g.StartsWith("Skybox", StringComparison.OrdinalIgnoreCase);

                currentIsWater =
                    m.StartsWith("WATER", StringComparison.OrdinalIgnoreCase) ||
                    m.IndexOf("OCEAN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.IndexOf("RIVER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    g.Equals("Water", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("Waterway", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("River", StringComparison.OrdinalIgnoreCase);

                currentIsOverlayLayer =
                    m.EndsWith("_1", StringComparison.OrdinalIgnoreCase) ||
                    m.EndsWith("_2", StringComparison.OrdinalIgnoreCase) ||
                    m.EndsWith("_3", StringComparison.OrdinalIgnoreCase) ||
                    m.Equals("ROAD_MARKING_0", StringComparison.OrdinalIgnoreCase);

                bool mtlHasRoadTex = false;
                if (!currentIsOverlayLayer &&
                    mtlSpecs.TryGetValue(m, out MtlMaterialSpec mSpec) &&
                    !string.IsNullOrEmpty(mSpec.DiffuseTexturePath))
                {
                    mtlHasRoadTex =
                        mSpec.DiffuseTexturePath.IndexOf("Asphalt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        mSpec.DiffuseTexturePath.IndexOf("PavingStones", StringComparison.OrdinalIgnoreCase) >= 0;
                }

                currentIsRoad =
                    !currentIsOverlayLayer &&
                    (mtlHasRoadTex ||
                     g.StartsWith("Road", StringComparison.OrdinalIgnoreCase) ||
                     g.StartsWith("Bridge", StringComparison.OrdinalIgnoreCase) ||
                     g.StartsWith("Tunnel", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("Road", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("ASPHALT", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("PAVING_STONE", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("SETT", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("COBBLESTONE", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("UNHEWN_COBBLESTONE", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("ROAD_MARKING_DASHED_0", StringComparison.OrdinalIgnoreCase) ||
                     m.StartsWith("RED_ROAD_MARKING_0", StringComparison.OrdinalIgnoreCase));

                currentIsBreakableProp =
                    g.Equals("Tree", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("StreetLamp", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("Bollard", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("TrafficSign", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("TrafficSignal", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("PostBox", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("FireHydrant", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("WasteBasket", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("Bench", StringComparison.OrdinalIgnoreCase) ||
                    o.StartsWith("Tree", StringComparison.OrdinalIgnoreCase) ||
                    o.StartsWith("StreetLamp", StringComparison.OrdinalIgnoreCase) ||
                    o.StartsWith("Bollard", StringComparison.OrdinalIgnoreCase) ||
                    o.StartsWith("TrafficSign", StringComparison.OrdinalIgnoreCase) ||
                    o.StartsWith("TrafficSignal", StringComparison.OrdinalIgnoreCase);

                currentIsTreeBillboard =
                    m.StartsWith("TREE_BILLBOARD", StringComparison.OrdinalIgnoreCase) ||
                    m.IndexOf("FOLIAGE", StringComparison.OrdinalIgnoreCase) >= 0;

                currentNoCollideFoliage =
                    currentIsTreeBillboard ||
                    g.Equals("Forest", StringComparison.OrdinalIgnoreCase) ||
                    g.Equals("Scrub", StringComparison.OrdinalIgnoreCase) ||
                    m.IndexOf("TREE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.IndexOf("LEAF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.StartsWith("HEDGE", StringComparison.OrdinalIgnoreCase) ||
                    m.StartsWith("METAL_FENCE_POST", StringComparison.OrdinalIgnoreCase) ||
                    m.StartsWith("CHAIN_LINK_FENCE", StringComparison.OrdinalIgnoreCase) ||
                    m.StartsWith("HANDRAIL", StringComparison.OrdinalIgnoreCase) ||
                    m.StartsWith("POSTBOX", StringComparison.OrdinalIgnoreCase) ||
                    m.StartsWith("ADVERTISING_POSTER", StringComparison.OrdinalIgnoreCase);
            }

            var faceV = new List<int>(16);
            var faceVT = new List<int>(16);
            var faceVN = new List<int>(16);
            var singleTriPos = new Vector3[3];
            var singleTriIdx = new int[] { 0, 1, 2 };

            // Pass 2: Expand faces into district chunks & breakable prop objects
            using (var ms = new MemoryStream(objBytes))
            using (var sr = new StreamReader(ms, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length < 2) continue;
                    char c0 = line[0];

                    if (c0 == 'g' && (line[1] == ' ' || line[1] == '\t'))
                    {
                        currentGroup = line.Substring(2).Trim();
                        UpdateClassification();
                        activePropObject = null;
                        continue;
                    }

                    if (c0 == 'o' && (line[1] == ' ' || line[1] == '\t'))
                    {
                        currentObject = line.Substring(2).Trim();
                        UpdateClassification();
                        if (currentIsBreakableProp)
                        {
                            activePropObject = new ObjPropObjectAccumulator
                            {
                                ObjectName = currentObject,
                                GroupName = currentGroup
                            };
                            breakablePropObjects.Add(activePropObject);
                        }
                        else
                        {
                            activePropObject = null;
                        }
                        continue;
                    }

                    if (c0 == 'u' && line.StartsWith("usemtl ", StringComparison.OrdinalIgnoreCase))
                    {
                        currentMaterial = line.Substring(7).Trim();
                        if (string.IsNullOrEmpty(currentMaterial)) currentMaterial = "DEFAULT";
                        UpdateClassification();
                        continue;
                    }

                    if (c0 != 'f' || (line[1] != ' ' && line[1] != '\t'))
                    {
                        continue;
                    }

                    if (currentSkipFace)
                    {
                        continue;
                    }

                    ParseObjFaceLine(line, rawVerts.Count, rawUVs.Count, rawNormals.Count, faceV, faceVT, faceVN);
                    int n = faceV.Count;
                    if (n < 3) continue;

                    Vector3 p0 = rawVerts[faceV[0]];
                    Vector3 p1 = rawVerts[faceV[1]];
                    Vector3 p2 = rawVerts[faceV[2]];

                    // Because we negated X (AGE/OBJ -> Unity), reverse winding (0, k+1, k) so outward normal is preserved
                    Vector3 faceNorm = Vector3.Cross(p2 - p0, p1 - p0);
                    if (faceNorm.sqrMagnitude > 1e-8f)
                    {
                        faceNorm.Normalize();
                    }
                    else
                    {
                        faceNorm = Vector3.up;
                    }

                    // Nudge multi-layer overlays (road markings, building window overlays) +12mm along face normal to prevent z-fighting
                    Vector3 layerOffset = currentIsOverlayLayer ? (faceNorm * 0.012f) : Vector3.zero;

                    if (currentIsBreakableProp && activePropObject != null && activePropObject.TotalTriangles < 512)
                    {
                        if (!activePropObject.Buckets.TryGetValue(currentMaterial, out ObjSubmeshBucket pb))
                        {
                            pb = new ObjSubmeshBucket();
                            activePropObject.Buckets[currentMaterial] = pb;
                        }

                        int baseIdx = pb.Positions.Count;
                        for (int k = 0; k < n; k++)
                        {
                            Vector3 vp = rawVerts[faceV[k]] + layerOffset;
                            Vector3 vn = currentIsTreeBillboard
                                ? Vector3.up
                                : ((faceVN[k] >= 0 && faceVN[k] < rawNormals.Count) ? rawNormals[faceVN[k]] : faceNorm);
                            Vector2 vt = (faceVT[k] >= 0 && faceVT[k] < rawUVs.Count)
                                ? rawUVs[faceVT[k]]
                                : ComputeAutoArchitecturalUV(vp, faceNorm);

                            pb.Positions.Add(vp);
                            pb.Normals.Add(vn);
                            pb.UVs.Add(vt);

                            if (vp.x < activePropObject.BoundsMin.x) activePropObject.BoundsMin.x = vp.x;
                            if (vp.y < activePropObject.BoundsMin.y) activePropObject.BoundsMin.y = vp.y;
                            if (vp.z < activePropObject.BoundsMin.z) activePropObject.BoundsMin.z = vp.z;
                            if (vp.x > activePropObject.BoundsMax.x) activePropObject.BoundsMax.x = vp.x;
                            if (vp.y > activePropObject.BoundsMax.y) activePropObject.BoundsMax.y = vp.y;
                            if (vp.z > activePropObject.BoundsMax.z) activePropObject.BoundsMax.z = vp.z;
                        }

                        for (int k = 1; k + 1 < n; k++)
                        {
                            pb.Triangles.Add(baseIdx);
                            pb.Triangles.Add(baseIdx + k + 1);
                            pb.Triangles.Add(baseIdx + k);
                            activePropObject.TotalTriangles++;
                        }
                        continue;
                    }

                    float cx = (p0.x + p1.x + p2.x) / 3f;
                    float cz = (p0.z + p1.z + p2.z) / 3f;
                    ObjDistrictChunk chunk = GetChunk(cx, cz);

                    if (!chunk.VisualByMaterial.TryGetValue(currentMaterial, out ObjSubmeshBucket bucket))
                    {
                        bucket = new ObjSubmeshBucket();
                        chunk.VisualByMaterial[currentMaterial] = bucket;
                    }

                    int bStart = bucket.Positions.Count;
                    for (int k = 0; k < n; k++)
                    {
                        Vector3 vp = rawVerts[faceV[k]] + layerOffset;
                        Vector3 vn = currentIsTreeBillboard
                            ? Vector3.up
                            : ((faceVN[k] >= 0 && faceVN[k] < rawNormals.Count) ? rawNormals[faceVN[k]] : faceNorm);
                        Vector2 vt = (faceVT[k] >= 0 && faceVT[k] < rawUVs.Count)
                            ? rawUVs[faceVT[k]]
                            : ComputeAutoArchitecturalUV(vp, faceNorm);

                        bucket.Positions.Add(vp);
                        bucket.Normals.Add(vn);
                        bucket.UVs.Add(vt);
                    }

                    for (int k = 1; k + 1 < n; k++)
                    {
                        bucket.Triangles.Add(bStart);
                        bucket.Triangles.Add(bStart + k + 1);
                        bucket.Triangles.Add(bStart + k);
                    }

                    if (currentIsWater)
                    {
                        if (recordWaterTriangles != null)
                        {
                            for (int k = 1; k + 1 < n; k++)
                            {
                                singleTriPos[0] = rawVerts[faceV[0]];
                                singleTriPos[1] = rawVerts[faceV[k + 1]];
                                singleTriPos[2] = rawVerts[faceV[k]];
                                recordWaterTriangles(singleTriPos, singleTriIdx, 0);
                            }
                        }
                    }
                    else if (!currentIsOverlayLayer && !currentNoCollideFoliage && !currentIsBreakableProp)
                    {
                        int colBase = chunk.ColPositions.Count;
                        for (int k = 0; k < n; k++)
                        {
                            chunk.ColPositions.Add(rawVerts[faceV[k]]);
                        }
                        for (int k = 1; k + 1 < n; k++)
                        {
                            chunk.ColTriangles.Add(colBase);
                            chunk.ColTriangles.Add(colBase + k + 1);
                            chunk.ColTriangles.Add(colBase + k);
                        }

                        if (currentIsRoad && faceNorm.y >= 0.55f)
                        {
                            for (int k = 1; k + 1 < n; k++)
                            {
                                AddRoadElevationTriangle(
                                    cityData,
                                    rawVerts[faceV[0]],
                                    rawVerts[faceV[k + 1]],
                                    rawVerts[faceV[k]]);
                            }
                        }
                    }
                }
            }

            // Instantiate Unity materials and district chunk GameObjects
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var matCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

            Material ResolveObjMaterial(string matKey)
            {
                if (matCache.TryGetValue(matKey, out Material m) && m != null) return m;
                m = CreateUnityMaterialForObj(matKey, mtlSpecs, urpLit);
                matCache[matKey] = m;
                return m;
            }

            for (int gx = 0; gx < GridN; gx++)
            {
                for (int gz = 0; gz < GridN; gz++)
                {
                    ObjDistrictChunk ch = chunks[gx, gz];
                    if (ch.VisualByMaterial.Count == 0 && ch.ColTriangles.Count == 0) continue;

                    GameObject chunkGo = new GameObject($"{spec.MapName}_District_{gx}_{gz}");
                    chunkGo.transform.SetParent(cityRoot, false);

                    if (ch.VisualByMaterial.Count > 0)
                    {
                        var allPos = new List<Vector3>();
                        var allNorm = new List<Vector3>();
                        var allUV = new List<Vector2>();
                        var subTris = new List<int[]>();
                        var mats = new List<Material>();

                        foreach (var kvp in ch.VisualByMaterial)
                        {
                            ObjSubmeshBucket b = kvp.Value;
                            if (b.Triangles.Count == 0) continue;

                            int baseV = allPos.Count;
                            allPos.AddRange(b.Positions);
                            allNorm.AddRange(b.Normals);
                            allUV.AddRange(b.UVs);

                            int[] indices = new int[b.Triangles.Count];
                            for (int i = 0; i < indices.Length; i++) indices[i] = baseV + b.Triangles[i];
                            subTris.Add(indices);
                            mats.Add(ResolveObjMaterial(kvp.Key));
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

            // Instantiate breakable street props & trees from OSM2World object groups
            if (bangersRoot != null && breakablePropObjects.Count > 0)
            {
                SpawnBreakableOsmProps(breakablePropObjects, bangersRoot, ResolveObjMaterial);
            }

            return true;
        }

        private static void SpawnBreakableOsmProps(
            List<ObjPropObjectAccumulator> props,
            Transform bangersRoot,
            Func<string, Material> resolveMaterial)
        {
            const int maxBreakableProps = 2500;
            int spawned = 0;

            for (int i = 0; i < props.Count && spawned < maxBreakableProps; i++)
            {
                ObjPropObjectAccumulator p = props[i];
                if (p.TotalTriangles == 0 || p.Buckets.Count == 0) continue;

                Vector3 size = p.BoundsMax - p.BoundsMin;
                if (size.x > 25f || size.z > 25f || size.y > 35f || size.y < 0.2f)
                {
                    continue;
                }

                Vector3 pivot = new Vector3(
                    (p.BoundsMin.x + p.BoundsMax.x) * 0.5f,
                    p.BoundsMin.y,
                    (p.BoundsMin.z + p.BoundsMax.z) * 0.5f);

                var allPos = new List<Vector3>();
                var allNorm = new List<Vector3>();
                var allUV = new List<Vector2>();
                var subTris = new List<int[]>();
                var mats = new List<Material>();

                foreach (var kvp in p.Buckets)
                {
                    ObjSubmeshBucket b = kvp.Value;
                    if (b.Triangles.Count == 0) continue;

                    int baseV = allPos.Count;
                    for (int v = 0; v < b.Positions.Count; v++)
                    {
                        allPos.Add(b.Positions[v] - pivot);
                    }
                    allNorm.AddRange(b.Normals);
                    allUV.AddRange(b.UVs);

                    int[] indices = new int[b.Triangles.Count];
                    for (int k = 0; k < indices.Length; k++) indices[k] = baseV + b.Triangles[k];
                    subTris.Add(indices);
                    mats.Add(resolveMaterial(kvp.Key));
                }

                if (allPos.Count == 0) continue;

                Mesh propMesh = new Mesh
                {
                    name = $"OsmProp_{p.ObjectName}",
                    indexFormat = IndexFormat.UInt32
                };
                propMesh.SetVertices(allPos);
                propMesh.SetNormals(allNorm);
                propMesh.SetUVs(0, allUV);
                propMesh.subMeshCount = subTris.Count;
                for (int s = 0; s < subTris.Count; s++)
                {
                    propMesh.SetTriangles(subTris[s], s, false);
                }
                propMesh.RecalculateBounds();

                GameObject propGo = new GameObject($"OsmBanger_{p.GroupName}_{spawned}");
                propGo.transform.SetParent(bangersRoot, false);
                propGo.transform.position = pivot;
                propGo.transform.rotation = Quaternion.identity;

                bool isTree = p.GroupName.IndexOf("Tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              p.ObjectName.IndexOf("Tree", StringComparison.OrdinalIgnoreCase) >= 0;
                string pkgName = isTree ? "tp_tree_osm" : $"tp_{p.GroupName.ToLowerInvariant()}_osm";
                Material[] matArr = mats.ToArray();

                propGo.AddComponent<MeshFilter>().sharedMesh = propMesh;
                propGo.AddComponent<MeshRenderer>().sharedMaterials = matArr;

                var spec = new MM2BangerSpec
                {
                    PkgName = pkgName,
                    Mass = isTree ? 65f : 35f,
                    Elasticity = 0.25f,
                    Friction = 0.65f,
                    ImpulseLimit2 = 15f,
                    YRadius = Mathf.Max(0.4f, size.y * 0.5f),
                    Size = new Vector3(
                        Mathf.Clamp(size.x, 0.35f, 1.8f),
                        Mathf.Clamp(size.y, 0.5f, 8.0f),
                        Mathf.Clamp(size.z, 0.35f, 1.8f)),
                    CG = new Vector3(0f, Mathf.Clamp(size.y * 0.45f, 0.35f, 3.5f), 0f),
                    AudioId = isTree ? 5 : 1
                };

                var bundle = new MM2BangerMeshBundle
                {
                    PkgName = pkgName,
                    Variant = 0,
                    SharedMesh = propMesh,
                    SharedMaterials = matArr,
                    MeshBounds = propMesh.bounds
                };

                var box = propGo.AddComponent<BoxCollider>();
                box.center = propMesh.bounds.center;
                box.size = new Vector3(
                    Mathf.Clamp(size.x, 0.45f, 1.45f),
                    Mathf.Max(size.y, 0.65f),
                    Mathf.Clamp(size.z, 0.45f, 1.45f));
                box.isTrigger = true;

                Vector3 origAge = new Vector3(-pivot.x, pivot.y, pivot.z);
                var banger = propGo.AddComponent<MM2BangerProp>();
                banger.Initialize(spec, bundle, box, Vector3.right, Vector3.up, Vector3.forward, origAge);
                spawned++;
            }
        }

        private static void AddRoadElevationTriangle(CachedOsmCityData data, Vector3 a, Vector3 b, Vector3 c)
        {
            int idx = data.RoadTriangles.Count;
            var sample = new RoadElevationSample
            {
                A = a,
                B = b,
                C = c,
                MinX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - 0.5f,
                MaxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + 0.5f,
                MinZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - 0.5f,
                MaxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z)) + 0.5f,
                AvgY = (a.y + b.y + c.y) / 3f
            };
            data.RoadTriangles.Add(sample);

            const float cellSize = 40f;
            int gx0 = Mathf.FloorToInt(sample.MinX / cellSize);
            int gx1 = Mathf.FloorToInt(sample.MaxX / cellSize);
            int gz0 = Mathf.FloorToInt(sample.MinZ / cellSize);
            int gz1 = Mathf.FloorToInt(sample.MaxZ / cellSize);

            for (int gx = gx0; gx <= gx1; gx++)
            {
                for (int gz = gz0; gz <= gz1; gz++)
                {
                    long key = ((long)gx << 32) | (uint)gz;
                    if (!data.RoadTriSpatialGrid.TryGetValue(key, out List<int> list))
                    {
                        list = new List<int>(8);
                        data.RoadTriSpatialGrid[key] = list;
                    }
                    list.Add(idx);
                }
            }
        }

        public static float SampleRoadElevationAt(CachedOsmCityData data, float ux, float uz, float hintY = 0f, int osmLayer = 0)
        {
            if (data == null || data.RoadTriangles.Count == 0)
            {
                return data != null ? data.Projection.BaseGroundY : hintY;
            }

            const float cellSize = 40f;
            int cgx = Mathf.FloorToInt(ux / cellSize);
            int cgz = Mathf.FloorToInt(uz / cellSize);

            float bestY = data.Projection.BaseGroundY;
            float bestDistSq = float.MaxValue;
            bool foundInside = false;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    long key = ((long)(cgx + dx) << 32) | (uint)(cgz + dz);
                    if (!data.RoadTriSpatialGrid.TryGetValue(key, out List<int> list)) continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        RoadElevationSample rt = data.RoadTriangles[list[i]];
                        if (ux >= rt.MinX && ux <= rt.MaxX && uz >= rt.MinZ && uz <= rt.MaxZ)
                        {
                            if (IsPointInTriangleXZ(ux, uz, rt.A, rt.B, rt.C, out float interpY))
                            {
                                if (!foundInside ||
                                    (osmLayer > 0 && interpY > bestY) ||
                                    (osmLayer < 0 && interpY < bestY) ||
                                    (osmLayer == 0 && Mathf.Abs(interpY - hintY) < Mathf.Abs(bestY - hintY)))
                                {
                                    bestY = interpY;
                                    foundInside = true;
                                }
                            }
                        }

                        if (!foundInside)
                        {
                            float cx = (rt.A.x + rt.B.x + rt.C.x) / 3f;
                            float cz = (rt.A.z + rt.B.z + rt.C.z) / 3f;
                            float d2 = (ux - cx) * (ux - cx) + (uz - cz) * (uz - cz);
                            if (d2 < bestDistSq)
                            {
                                bestDistSq = d2;
                                bestY = rt.AvgY;
                            }
                        }
                    }
                }
            }

            return bestY;
        }

        private static bool IsPointInTriangleXZ(float px, float pz, Vector3 a, Vector3 b, Vector3 c, out float y)
        {
            y = (a.y + b.y + c.y) / 3f;
            float denom = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(denom) < 1e-5f) return false;

            float w1 = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / denom;
            float w2 = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / denom;
            float w3 = 1f - w1 - w2;

            const float eps = -0.08f;
            if (w1 >= eps && w2 >= eps && w3 >= eps)
            {
                y = w1 * a.y + w2 * b.y + w3 * c.y;
                return true;
            }
            return false;
        }

        private static Vector2 ComputeAutoArchitecturalUV(Vector3 worldPos, Vector3 normal)
        {
            if (Mathf.Abs(normal.y) >= 0.65f)
            {
                // Horizontal surface (road, sidewalk, roof, park, water): tile every 8 meters
                return new Vector2(worldPos.x * 0.125f, worldPos.z * 0.125f);
            }
            else
            {
                // Vertical building facade wall: tile horizontally every 4m and vertically every 3.5m floor
                float horiz = Mathf.Abs(normal.x) > Mathf.Abs(normal.z) ? worldPos.z : worldPos.x;
                return new Vector2(horiz * 0.25f, worldPos.y / 3.5f);
            }
        }

        private static void ParseMtlFile(string mtlVirtualPath, Dictionary<string, MtlMaterialSpec> outSpecs)
        {
            string text = MM2ArchiveSystem.ReadFileText(mtlVirtualPath);
            if (string.IsNullOrEmpty(text)) return;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            MtlMaterialSpec cur = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line.StartsWith("newmtl ", StringComparison.OrdinalIgnoreCase))
                {
                    string name = line.Substring(7).Trim();
                    cur = new MtlMaterialSpec { Name = name };
                    outSpecs[name] = cur;
                }
                else if (cur != null)
                {
                    if (line.StartsWith("Ka ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParseFloat3(line, 3, out float r, out float g, out float b))
                        {
                            cur.AmbientColor = new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
                            cur.HasAmbient = true;
                        }
                    }
                    else if (line.StartsWith("Kd ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParseFloat3(line, 3, out float r, out float g, out float b))
                        {
                            // OSM2World splits material albedo 50/50 between Ka and Kd; sum Ka + Kd so albedo is 100% brightness
                            float cr = cur.HasAmbient ? Mathf.Clamp01(cur.AmbientColor.r + r) : Mathf.Clamp01(r);
                            float cg = cur.HasAmbient ? Mathf.Clamp01(cur.AmbientColor.g + g) : Mathf.Clamp01(g);
                            float cb = cur.HasAmbient ? Mathf.Clamp01(cur.AmbientColor.b + b) : Mathf.Clamp01(b);
                            cur.DiffuseColor = new Color(cr, cg, cb, cur.Alpha);
                        }
                    }
                    else if (line.StartsWith("Ks ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParseFloat3(line, 3, out float r, out float g, out float b))
                        {
                            cur.SpecularColor = new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
                        }
                    }
                    else if (line.StartsWith("Ke ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParseFloat3(line, 3, out float r, out float g, out float b))
                        {
                            cur.EmissiveColor = new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
                        }
                    }
                    else if (line.StartsWith("d ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(line.Substring(2).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float dVal))
                        {
                            cur.Alpha = Mathf.Clamp01(dVal);
                            cur.DiffuseColor.a = cur.Alpha;
                        }
                    }
                    else if (line.StartsWith("Tr ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(line.Substring(3).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float trVal))
                        {
                            cur.Alpha = Mathf.Clamp01(1f - trVal);
                            cur.DiffuseColor.a = cur.Alpha;
                        }
                    }
                    else if (line.StartsWith("map_Kd ", StringComparison.OrdinalIgnoreCase))
                    {
                        string texPath = line.Substring(7).Trim();
                        if (texPath.IndexOf("-clamp on", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            cur.ClampTexture = true;
                        }
                        // Strip optional MTL texture flags (-s, -o, -clamp on, etc.) if present
                        int lastSpace = texPath.LastIndexOf(' ');
                        if (lastSpace > 0 && texPath.StartsWith("-"))
                        {
                            texPath = texPath.Substring(lastSpace + 1).Trim();
                        }
                        cur.DiffuseTexturePath = texPath;
                    }
                }
            }
        }

        private static Material CreateUnityMaterialForObj(
            string matName,
            Dictionary<string, MtlMaterialSpec> mtlSpecs,
            Shader urpLit)
        {
            if (urpLit == null) urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(urpLit) { name = $"Mat_OSM_{matName}" };
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.10f);

            mtlSpecs.TryGetValue(matName, out MtlMaterialSpec spec);
            Color baseColor = spec != null ? spec.DiffuseColor : GetDefaultColorForMaterialName(matName);

            MM2RuntimeDecoders.DecodedTextureInfo texInfo = default;
            if (spec != null && !string.IsNullOrEmpty(spec.DiffuseTexturePath))
            {
                texInfo = MM2RuntimeDecoders.LoadTexture(spec.DiffuseTexturePath);
            }
            if (texInfo.Texture == null)
            {
                texInfo = MM2RuntimeDecoders.LoadTexture(matName);
            }

            if (texInfo.Texture != null && spec != null && spec.ClampTexture)
            {
                texInfo.Texture.wrapMode = TextureWrapMode.Clamp;
            }

            // If no external texture file is bundled for this material, synthesize a crisp procedural architectural/road surface texture
            if (texInfo.Texture == null)
            {
                Texture2D procTex = GetOrCreateProceduralOsmSurfaceTexture(matName, baseColor, out bool retainTint);
                if (procTex != null)
                {
                    texInfo.Texture = procTex;
                    if (!retainTint)
                    {
                        baseColor = new Color(1f, 1f, 1f, baseColor.a);
                    }
                }
            }

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseColor);
            m.color = baseColor;

            if (texInfo.Texture != null)
            {
                m.mainTexture = texInfo.Texture;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texInfo.Texture);
            }

            bool isGlass = matName.IndexOf("GLASS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (spec != null && spec.Alpha < 0.92f && !texInfo.HasAlpha);
            bool isWater = matName.StartsWith("WATER", StringComparison.OrdinalIgnoreCase);

            if (isWater && m.HasProperty("_Smoothness"))
            {
                m.SetFloat("_Smoothness", 0.65f);
            }

            if (isGlass || texInfo.HasSemiTransparency)
            {
                Color glassCol = isGlass
                    ? new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Clamp(spec != null ? spec.Alpha : 0.65f, 0.35f, 0.85f))
                    : baseColor;
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
                if (m.HasProperty("_Smoothness") && isGlass) m.SetFloat("_Smoothness", 0.80f);
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

            return m;
        }

        private static Color GetDefaultColorForMaterialName(string matName)
        {
            string u = (matName ?? string.Empty).ToUpperInvariant();
            if (u.Contains("ASPHALT") || u.Contains("ROAD")) return new Color(0.28f, 0.29f, 0.31f, 1f);
            if (u.Contains("MARKING") || u.Contains("ZEBRA")) return new Color(0.92f, 0.92f, 0.90f, 1f);
            if (u.Contains("CONCRETE") || u.Contains("SIDEWALK") || u.Contains("KERB") || u.Contains("PAVING")) return new Color(0.62f, 0.62f, 0.60f, 1f);
            if (u.Contains("WATER") || u.Contains("RIVER")) return new Color(0.14f, 0.36f, 0.56f, 1f);
            if (u.Contains("GRASS") || u.Contains("TERRAIN") || u.Contains("SCRUB") || u.Contains("FOREST")) return new Color(0.26f, 0.48f, 0.24f, 1f);
            if (u.Contains("ROOF") || u.Contains("TILES")) return new Color(0.58f, 0.29f, 0.22f, 1f);
            if (u.Contains("BRICK")) return new Color(0.68f, 0.38f, 0.28f, 1f);
            if (u.Contains("GLASS")) return new Color(0.65f, 0.78f, 0.88f, 0.65f);
            return new Color(0.82f, 0.80f, 0.76f, 1f);
        }

        private static Texture2D GetOrCreateProceduralOsmSurfaceTexture(string matName, Color baseColor, out bool retainTint)
        {
            retainTint = true;
            string u = (matName ?? string.Empty).ToUpperInvariant();

            string category = "WALL";
            if (u.Contains("MARKING") || u.Contains("ZEBRA")) category = "MARKING";
            else if (u.Contains("ASPHALT") || u.Contains("SETT") || u.Contains("COBBLESTONE")) category = "ASPHALT";
            else if (u.Contains("CONCRETE") || u.Contains("PAVING") || u.Contains("KERB")) category = "PAVING";
            else if (u.Contains("WATER") || u.Contains("RIVER")) category = "WATER";
            else if (u.Contains("GRASS") || u.Contains("TERRAIN") || u.Contains("SCRUB")) category = "GRASS";
            else if (u.Contains("ROOF") || u.Contains("TILES") || u.Contains("SLATE")) category = "ROOF";
            else if (u.Contains("GLASS")) return null;

            string cacheKey = $"{category}_{ColorUtility.ToHtmlStringRGB(baseColor)}";
            if (ProceduralTextureCache.TryGetValue(cacheKey, out Texture2D cached) && cached != null)
            {
                retainTint = false;
                return cached;
            }

            const int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = $"ProcOSM_{cacheKey}",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4
            };

            Color32[] px = new Color32[size * size];
            uint seed = (uint)cacheKey.GetHashCode() | 1u;

            float NextNoise()
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                return (seed & 0xFFFFu) / 65535f;
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float n = (NextNoise() - 0.5f) * 0.08f;
                    Color c = baseColor;

                    if (category == "ASPHALT")
                    {
                        c = new Color(
                            Mathf.Clamp01(baseColor.r + n),
                            Mathf.Clamp01(baseColor.g + n),
                            Mathf.Clamp01(baseColor.b + n),
                            1f);
                    }
                    else if (category == "PAVING")
                    {
                        bool seam = (x % 16 == 0) || (y % 16 == 0);
                        float delta = seam ? -0.09f : n * 0.6f;
                        c = new Color(
                            Mathf.Clamp01(baseColor.r + delta),
                            Mathf.Clamp01(baseColor.g + delta),
                            Mathf.Clamp01(baseColor.b + delta),
                            1f);
                    }
                    else if (category == "ROOF")
                    {
                        bool tileRow = (y % 8 == 0) || (x % 16 == 0);
                        float delta = tileRow ? -0.08f : n * 0.7f;
                        c = new Color(
                            Mathf.Clamp01(baseColor.r + delta),
                            Mathf.Clamp01(baseColor.g + delta),
                            Mathf.Clamp01(baseColor.b + delta),
                            1f);
                    }
                    else if (category == "GRASS")
                    {
                        c = new Color(
                            Mathf.Clamp01(baseColor.r * 0.55f + n * 0.5f),
                            Mathf.Clamp01(baseColor.g * 0.72f + n),
                            Mathf.Clamp01(baseColor.b * 0.55f + n * 0.5f),
                            1f);
                    }
                    else if (category == "WATER")
                    {
                        float wave = Mathf.Sin((x + y) * 0.15f) * 0.03f + n * 0.3f;
                        c = new Color(
                            Mathf.Clamp01(0.12f + wave),
                            Mathf.Clamp01(0.32f + wave),
                            Mathf.Clamp01(0.52f + wave),
                            1f);
                    }
                    else if (category == "WALL")
                    {
                        // Subtle architectural floor band + window grid pattern so untextured OSM building walls read clearly at speed
                        int wx = x % 32;
                        int wy = y % 32;
                        bool inWindow = (wx >= 6 && wx <= 25 && wy >= 8 && wy <= 24);
                        bool floorLedge = (wy == 0 || wy == 1);
                        if (inWindow)
                        {
                            c = new Color(
                                Mathf.Clamp01(baseColor.r * 0.42f + 0.08f),
                                Mathf.Clamp01(baseColor.g * 0.46f + 0.12f),
                                Mathf.Clamp01(baseColor.b * 0.52f + 0.16f),
                                1f);
                        }
                        else if (floorLedge)
                        {
                            c = new Color(
                                Mathf.Clamp01(baseColor.r * 0.82f),
                                Mathf.Clamp01(baseColor.g * 0.82f),
                                Mathf.Clamp01(baseColor.b * 0.82f),
                                1f);
                        }
                        else
                        {
                            c = new Color(
                                Mathf.Clamp01(baseColor.r + n * 0.5f),
                                Mathf.Clamp01(baseColor.g + n * 0.5f),
                                Mathf.Clamp01(baseColor.b + n * 0.5f),
                                1f);
                        }
                    }

                    px[y * size + x] = c;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(true, false);
            ProceduralTextureCache[cacheKey] = tex;
            retainTint = false;
            return tex;
        }

        private static bool TryParseFloat2(string line, int startIdx, out float a, out float b)
        {
            a = 0f; b = 0f;
            int len = line.Length;
            int p = startIdx;
            while (p < len && (line[p] == ' ' || line[p] == '\t')) p++;
            int s0 = p;
            while (p < len && line[p] != ' ' && line[p] != '\t') p++;
            if (p <= s0) return false;

            while (p < len && (line[p] == ' ' || line[p] == '\t')) p++;
            int s1 = p;
            while (p < len && line[p] != ' ' && line[p] != '\t') p++;
            if (p <= s1) return false;

            return float.TryParse(line.AsSpan(s0, s1 - s0).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                   float.TryParse(line.AsSpan(s1, p - s1), NumberStyles.Float, CultureInfo.InvariantCulture, out b);
        }

        private static bool TryParseFloat3(string line, int startIdx, out float a, out float b, out float c)
        {
            a = 0f; b = 0f; c = 0f;
            int len = line.Length;
            int p = startIdx;
            while (p < len && (line[p] == ' ' || line[p] == '\t')) p++;
            int s0 = p;
            while (p < len && line[p] != ' ' && line[p] != '\t') p++;
            if (p <= s0) return false;
            int e0 = p;

            while (p < len && (line[p] == ' ' || line[p] == '\t')) p++;
            int s1 = p;
            while (p < len && line[p] != ' ' && line[p] != '\t') p++;
            if (p <= s1) return false;
            int e1 = p;

            while (p < len && (line[p] == ' ' || line[p] == '\t')) p++;
            int s2 = p;
            while (p < len && line[p] != ' ' && line[p] != '\t') p++;
            if (p <= s2) return false;
            int e2 = p;

            return float.TryParse(line.AsSpan(s0, e0 - s0), NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                   float.TryParse(line.AsSpan(s1, e1 - s1), NumberStyles.Float, CultureInfo.InvariantCulture, out b) &&
                   float.TryParse(line.AsSpan(s2, e2 - s2), NumberStyles.Float, CultureInfo.InvariantCulture, out c);
        }

        private static void ParseObjFaceLine(
            string line,
            int vertCount,
            int uvCount,
            int normCount,
            List<int> outV,
            List<int> outVT,
            List<int> outVN)
        {
            outV.Clear();
            outVT.Clear();
            outVN.Clear();

            int len = line.Length;
            int p = 2;
            while (p < len)
            {
                while (p < len && (line[p] == ' ' || line[p] == '\t')) p++;
                if (p >= len) break;

                int tokStart = p;
                while (p < len && line[p] != ' ' && line[p] != '\t') p++;
                ReadOnlySpan<char> token = line.AsSpan(tokStart, p - tokStart);

                int slash1 = token.IndexOf('/');
                int vIdx = 0;
                int vtIdx = -1;
                int vnIdx = -1;

                if (slash1 < 0)
                {
                    if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out vIdx)) continue;
                }
                else
                {
                    if (!int.TryParse(token.Slice(0, slash1), NumberStyles.Integer, CultureInfo.InvariantCulture, out vIdx)) continue;
                    ReadOnlySpan<char> rem = token.Slice(slash1 + 1);
                    int slash2 = rem.IndexOf('/');
                    if (slash2 < 0)
                    {
                        if (rem.Length > 0 && int.TryParse(rem, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedVt))
                        {
                            vtIdx = parsedVt > 0 ? parsedVt - 1 : uvCount + parsedVt;
                        }
                    }
                    else
                    {
                        ReadOnlySpan<char> vtSpan = rem.Slice(0, slash2);
                        ReadOnlySpan<char> vnSpan = rem.Slice(slash2 + 1);
                        if (vtSpan.Length > 0 && int.TryParse(vtSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedVt))
                        {
                            vtIdx = parsedVt > 0 ? parsedVt - 1 : uvCount + parsedVt;
                        }
                        if (vnSpan.Length > 0 && int.TryParse(vnSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedVn))
                        {
                            vnIdx = parsedVn > 0 ? parsedVn - 1 : normCount + parsedVn;
                        }
                    }
                }

                int resolvedV = vIdx > 0 ? vIdx - 1 : vertCount + vIdx;
                if (resolvedV < 0 || resolvedV >= vertCount) continue;

                outV.Add(resolvedV);
                outVT.Add(vtIdx);
                outVN.Add(vnIdx);
            }
        }

        // ========================================================================================================
        // OPENSTREETMAP (.osm) ROAD NETWORK, INTERSECTIONS, SIDEWALKS, SPAWNS & FALLBACK GEOMETRY
        // ========================================================================================================

        private struct OsmNodeRecord
        {
            public long Id;
            public double Lat;
            public double Lon;
            public Vector3 UnityPos;
            public bool IsTrafficSignal;
            public bool IsStopSign;
            public string LandmarkName;
        }

        private class OsmWayRecord
        {
            public long Id;
            public readonly List<long> NodeIds = new List<long>();
            public string HighwayType;
            public string Name;
            public string BuildingType;
            public string NaturalType;
            public string LanduseType;
            public string WaterwayType;
            public string LeisureType;
            public string AmenityType;
            public string TourismType;
            public string HistoricType;
            public int Lanes;
            public int OnewayDir; // 0 = two-way, 1 = forward only, -1 = reverse only
            public float MaxSpeedMps;
            public float WidthMeters;
            public float BuildingHeightMeters;
            public int Layer;
            public bool IsBridge;
            public bool IsTunnel;
        }

        private static bool ParseOsmXml(
            string osmVirtualPath,
            CachedOsmCityData cityData,
            out Dictionary<long, OsmNodeRecord> nodes,
            out List<OsmWayRecord> ways)
        {
            nodes = new Dictionary<long, OsmNodeRecord>(16384);
            ways = new List<OsmWayRecord>(4096);

            byte[] osmBytes = MM2ArchiveSystem.ReadFileBytes(osmVirtualPath);
            if (osmBytes == null || osmBytes.Length < 32) return false;

            double sumLat = 0.0, sumLon = 0.0;
            int nodeCount = 0;

            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Ignore
            };

            using (var ms = new MemoryStream(osmBytes))
            using (var reader = XmlReader.Create(ms, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    string elName = reader.Name;

                    if (string.Equals(elName, "bounds", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!cityData.Projection.HasGeoOrigin &&
                            double.TryParse(reader.GetAttribute("minlat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double minLat) &&
                            double.TryParse(reader.GetAttribute("minlon"), NumberStyles.Float, CultureInfo.InvariantCulture, out double minLon) &&
                            double.TryParse(reader.GetAttribute("maxlat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double maxLat) &&
                            double.TryParse(reader.GetAttribute("maxlon"), NumberStyles.Float, CultureInfo.InvariantCulture, out double maxLon))
                        {
                            cityData.Projection.OriginLat = (minLat + maxLat) * 0.5;
                            cityData.Projection.OriginLon = (minLon + maxLon) * 0.5;
                            cityData.Projection.HasGeoOrigin = true;
                        }
                    }
                    else if (string.Equals(elName, "node", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(reader.GetAttribute("action"), "delete", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!reader.IsEmptyElement)
                            {
                                using (XmlReader sub = reader.ReadSubtree()) { while (sub.Read()) { } }
                            }
                            continue;
                        }
                        if (!long.TryParse(reader.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ||
                            !double.TryParse(reader.GetAttribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
                            !double.TryParse(reader.GetAttribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                        {
                            continue;
                        }

                        var node = new OsmNodeRecord
                        {
                            Id = id,
                            Lat = lat,
                            Lon = lon
                        };

                        if (!reader.IsEmptyElement)
                        {
                            using (XmlReader sub = reader.ReadSubtree())
                            {
                                sub.Read();
                                while (sub.Read())
                                {
                                    if (sub.NodeType == XmlNodeType.Element && string.Equals(sub.Name, "tag", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string k = sub.GetAttribute("k");
                                        string v = sub.GetAttribute("v");
                                        if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(v)) continue;

                                        if (string.Equals(k, "highway", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (string.Equals(v, "traffic_signals", StringComparison.OrdinalIgnoreCase)) node.IsTrafficSignal = true;
                                            else if (string.Equals(v, "stop", StringComparison.OrdinalIgnoreCase)) node.IsStopSign = true;
                                        }
                                        else if (string.Equals(k, "name", StringComparison.OrdinalIgnoreCase))
                                        {
                                            node.LandmarkName = v;
                                        }
                                    }
                                }
                            }
                        }

                        nodes[id] = node;
                        sumLat += lat;
                        sumLon += lon;
                        nodeCount++;
                    }
                    else if (string.Equals(elName, "way", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(reader.GetAttribute("action"), "delete", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!reader.IsEmptyElement)
                            {
                                using (XmlReader sub = reader.ReadSubtree()) { while (sub.Read()) { } }
                            }
                            continue;
                        }
                        if (!long.TryParse(reader.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long wayId))
                        {
                            continue;
                        }

                        var way = new OsmWayRecord { Id = wayId };
                        if (!reader.IsEmptyElement)
                        {
                            using (XmlReader sub = reader.ReadSubtree())
                            {
                                sub.Read();
                                while (sub.Read())
                                {
                                    if (sub.NodeType != XmlNodeType.Element) continue;
                                    if (string.Equals(sub.Name, "nd", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (long.TryParse(sub.GetAttribute("ref"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ndRef))
                                        {
                                            way.NodeIds.Add(ndRef);
                                        }
                                    }
                                    else if (string.Equals(sub.Name, "tag", StringComparison.OrdinalIgnoreCase))
                                    {
                                        string k = sub.GetAttribute("k");
                                        string v = sub.GetAttribute("v");
                                        if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(v)) continue;

                                        if (string.Equals(k, "highway", StringComparison.OrdinalIgnoreCase)) way.HighwayType = v.ToLowerInvariant();
                                        else if (string.Equals(k, "name", StringComparison.OrdinalIgnoreCase)) way.Name = v;
                                        else if (string.Equals(k, "building", StringComparison.OrdinalIgnoreCase)) way.BuildingType = v;
                                        else if (string.Equals(k, "natural", StringComparison.OrdinalIgnoreCase)) way.NaturalType = v.ToLowerInvariant();
                                        else if (string.Equals(k, "landuse", StringComparison.OrdinalIgnoreCase)) way.LanduseType = v.ToLowerInvariant();
                                        else if (string.Equals(k, "waterway", StringComparison.OrdinalIgnoreCase)) way.WaterwayType = v.ToLowerInvariant();
                                        else if (string.Equals(k, "leisure", StringComparison.OrdinalIgnoreCase)) way.LeisureType = v.ToLowerInvariant();
                                        else if (string.Equals(k, "amenity", StringComparison.OrdinalIgnoreCase)) way.AmenityType = v.ToLowerInvariant();
                                        else if (string.Equals(k, "tourism", StringComparison.OrdinalIgnoreCase)) way.TourismType = v.ToLowerInvariant();
                                        else if (string.Equals(k, "historic", StringComparison.OrdinalIgnoreCase)) way.HistoricType = v.ToLowerInvariant();
                                        else if (string.Equals(k, "lanes", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLanes))
                                            {
                                                way.Lanes = Mathf.Clamp(parsedLanes, 1, 8);
                                            }
                                        }
                                        else if (string.Equals(k, "oneway", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (v == "yes" || v == "1" || v == "true") way.OnewayDir = 1;
                                            else if (v == "-1" || v == "reverse") way.OnewayDir = -1;
                                        }
                                        else if (string.Equals(k, "junction", StringComparison.OrdinalIgnoreCase) &&
                                                 string.Equals(v, "roundabout", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (way.OnewayDir == 0) way.OnewayDir = 1;
                                        }
                                        else if (string.Equals(k, "maxspeed", StringComparison.OrdinalIgnoreCase))
                                        {
                                            way.MaxSpeedMps = ParseOsmMaxSpeedMps(v);
                                        }
                                        else if (string.Equals(k, "width", StringComparison.OrdinalIgnoreCase))
                                        {
                                            way.WidthMeters = ParseOsmFloatMeters(v);
                                        }
                                        else if (string.Equals(k, "height", StringComparison.OrdinalIgnoreCase))
                                        {
                                            way.BuildingHeightMeters = ParseOsmFloatMeters(v);
                                        }
                                        else if (string.Equals(k, "building:levels", StringComparison.OrdinalIgnoreCase) && way.BuildingHeightMeters <= 0f)
                                        {
                                            if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float lvls))
                                            {
                                                way.BuildingHeightMeters = Mathf.Clamp(lvls * 3.5f, 4f, 180f);
                                            }
                                        }
                                        else if (string.Equals(k, "layer", StringComparison.OrdinalIgnoreCase))
                                        {
                                            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out way.Layer);
                                        }
                                        else if (string.Equals(k, "bridge", StringComparison.OrdinalIgnoreCase) && v != "no")
                                        {
                                            way.IsBridge = true;
                                            if (way.Layer == 0) way.Layer = 1;
                                        }
                                        else if (string.Equals(k, "tunnel", StringComparison.OrdinalIgnoreCase) && v != "no")
                                        {
                                            way.IsTunnel = true;
                                            if (way.Layer == 0) way.Layer = -1;
                                        }
                                    }
                                }
                            }
                        }

                        if (way.NodeIds.Count >= 2)
                        {
                            ways.Add(way);
                        }
                    }
                }
            }

            if (nodeCount == 0) return false;

            if (!cityData.Projection.HasGeoOrigin)
            {
                cityData.Projection.OriginLat = sumLat / nodeCount;
                cityData.Projection.OriginLon = sumLon / nodeCount;
                cityData.Projection.HasGeoOrigin = true;
            }

            // Project all nodes into Unity world coordinates and snap Y to road elevation if .obj road triangles are loaded
            var keys = new List<long>(nodes.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                long id = keys[i];
                OsmNodeRecord nd = nodes[id];
                Vector3 uPos = cityData.Projection.ProjectLatLonToUnity(nd.Lat, nd.Lon, cityData.Projection.BaseGroundY);
                if (cityData.RoadTriangles.Count > 0)
                {
                    uPos.y = SampleRoadElevationAt(cityData, uPos.x, uPos.z, uPos.y, 0);
                }
                nd.UnityPos = uPos;
                nodes[id] = nd;
            }

            return true;
        }

        private static bool IsDriveableHighwayType(string hw, out int defaultLanes, out float defaultSpeedMps, out int importance, out bool isFreeway)
        {
            defaultLanes = 2;
            defaultSpeedMps = 13.8f; // ~50 km/h
            importance = 1;
            isFreeway = false;
            if (string.IsNullOrEmpty(hw)) return false;

            switch (hw)
            {
                case "motorway":
                    defaultLanes = 3; defaultSpeedMps = 27.8f; importance = 4; isFreeway = true; return true;
                case "motorway_link":
                    defaultLanes = 1; defaultSpeedMps = 19.4f; importance = 3; isFreeway = true; return true;
                case "trunk":
                    defaultLanes = 2; defaultSpeedMps = 22.2f; importance = 4; isFreeway = true; return true;
                case "trunk_link":
                    defaultLanes = 1; defaultSpeedMps = 16.7f; importance = 3; isFreeway = true; return true;
                case "primary":
                case "primary_link":
                    defaultLanes = 2; defaultSpeedMps = 16.7f; importance = 3; return true;
                case "secondary":
                case "secondary_link":
                    defaultLanes = 2; defaultSpeedMps = 13.9f; importance = 2; return true;
                case "tertiary":
                case "tertiary_link":
                    defaultLanes = 2; defaultSpeedMps = 12.5f; importance = 2; return true;
                case "unclassified":
                case "residential":
                case "road":
                    defaultLanes = 2; defaultSpeedMps = 11.1f; importance = 1; return true;
                case "living_street":
                case "service":
                    defaultLanes = 1; defaultSpeedMps = 8.5f; importance = 0; return true;
                default:
                    return false;
            }
        }

        private static float ParseOsmMaxSpeedMps(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0f;
            string s = raw.Trim().ToLowerInvariant();
            bool isMph = s.Contains("mph");
            int space = s.IndexOf(' ');
            if (space > 0) s = s.Substring(0, space);
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float val) && val > 0f)
            {
                return isMph ? (val * 0.44704f) : (val / 3.6f);
            }
            return 0f;
        }

        private static float ParseOsmFloatMeters(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0f;
            string s = raw.Trim().ToLowerInvariant().Replace("m", "").Trim();
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float val) && val > 0f)
            {
                return val;
            }
            return 0f;
        }

        private static void EnsureOsmMetadataParsed(string mapName, string osmVirtualPath, CachedOsmCityData cityData)
        {
            if (!ParseOsmXml(osmVirtualPath, cityData, out Dictionary<long, OsmNodeRecord> nodes, out List<OsmWayRecord> ways))
            {
                return;
            }

            PopulateMapPreviewAndSpawnsFromOsm(cityData, nodes, ways);
        }

        private static void PopulateMapPreviewAndSpawnsFromOsm(
            CachedOsmCityData cityData,
            Dictionary<long, OsmNodeRecord> nodes,
            List<OsmWayRecord> ways)
        {
            cityData.MapRoads.Clear();
            cityData.MapWaterPolygons.Clear();
            cityData.MapParkPolygons.Clear();
            cityData.MapBuildingPolygons.Clear();
            cityData.AutoCruiseSpawns.Clear();

            var roadSamplePoints = new List<(Vector3 pos, Quaternion rot, string streetName, int importance)>(512);

            for (int w = 0; w < ways.Count; w++)
            {
                OsmWayRecord way = ways[w];
                if (IsDriveableHighwayType(way.HighwayType, out int defLanes, out _, out int importance, out _))
                {
                    int lanes = way.Lanes > 0 ? way.Lanes : defLanes;
                    float width = way.WidthMeters > 0f ? way.WidthMeters : Mathf.Max(5f, lanes * 3.5f);

                    for (int i = 0; i + 1 < way.NodeIds.Count; i++)
                    {
                        if (!nodes.TryGetValue(way.NodeIds[i], out OsmNodeRecord n0) ||
                            !nodes.TryGetValue(way.NodeIds[i + 1], out OsmNodeRecord n1))
                        {
                            continue;
                        }
                        cityData.MapRoads.Add((n0.UnityPos, n1.UnityPos, width, importance));

                        if (importance >= 1 && (n1.UnityPos - n0.UnityPos).sqrMagnitude > 25f)
                        {
                            Vector3 mid = (n0.UnityPos + n1.UnityPos) * 0.5f;
                            Vector3 fwd = (n1.UnityPos - n0.UnityPos);
                            fwd.y = 0f;
                            if (fwd.sqrMagnitude > 0.01f)
                            {
                                Quaternion rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                                roadSamplePoints.Add((mid, rot, way.Name, importance));
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(way.BuildingType) && way.NodeIds.Count >= 3)
                {
                    Vector3[] poly = ExtractWayPolygon(way, nodes);
                    if (poly != null) cityData.MapBuildingPolygons.Add(poly);
                }
                else if ((way.NaturalType == "water" || way.LanduseType == "reservoir" || way.WaterwayType == "riverbank") && way.NodeIds.Count >= 3)
                {
                    Vector3[] poly = ExtractWayPolygon(way, nodes);
                    if (poly != null) cityData.MapWaterPolygons.Add(poly);
                }
                else if ((way.LeisureType == "park" || way.LeisureType == "garden" || way.LanduseType == "grass" || way.LanduseType == "forest" || way.LanduseType == "recreation_ground") && way.NodeIds.Count >= 3)
                {
                    Vector3[] poly = ExtractWayPolygon(way, nodes);
                    if (poly != null) cityData.MapParkPolygons.Add(poly);
                }
            }

            // Discover named landmarks, plazas, and major streets for Cruise Starting Point picker
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAddAutoSpawn(string rawName, Vector3 targetPos)
            {
                if (string.IsNullOrEmpty(rawName) || cityData.AutoCruiseSpawns.Count >= 16) return;
                string cleanName = rawName.Trim();
                if (cleanName.Length < 3 || cleanName.Length > 36 || !usedNames.Add(cleanName)) return;

                // Snap to nearest driveable road sample within 140m
                int bestIdx = -1;
                float bestDistSq = 140f * 140f;
                for (int i = 0; i < roadSamplePoints.Count; i++)
                {
                    Vector3 rp = roadSamplePoints[i].pos;
                    float dx = rp.x - targetPos.x;
                    float dz = rp.z - targetPos.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < bestDistSq)
                    {
                        bestDistSq = d2;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0) return;

                var chosen = roadSamplePoints[bestIdx];
                float yaw = chosen.rot.eulerAngles.y;
                cityData.AutoCruiseSpawns.Add(new MM2CruiseSpawnLocation
                {
                    DisplayName = cleanName,
                    FileName = $"osm_{cleanName.ToLowerInvariant().Replace(' ', '_')}",
                    VirtualPath = string.Empty,
                    PhotoStem = string.Empty,
                    PositionUnity = chosen.pos + Vector3.up * 1.5f,
                    RotationUnity = chosen.rot,
                    AngleDegrees = 180f - yaw,
                    IsRandom = false
                });
            }

            // 1. Named landmark ways (squares, historic monuments, attractions, stations, cathedrals)
            for (int w = 0; w < ways.Count && cityData.AutoCruiseSpawns.Count < 12; w++)
            {
                OsmWayRecord way = ways[w];
                if (string.IsNullOrEmpty(way.Name)) continue;
                bool isLandmark = !string.IsNullOrEmpty(way.TourismType) ||
                                  !string.IsNullOrEmpty(way.HistoricType) ||
                                  way.LeisureType == "park" ||
                                  way.AmenityType == "place_of_worship" ||
                                  way.AmenityType == "townhall" ||
                                  way.AmenityType == "theatre";
                if (isLandmark && nodes.TryGetValue(way.NodeIds[0], out OsmNodeRecord n0))
                {
                    TryAddAutoSpawn(way.Name, n0.UnityPos);
                }
            }

            // 2. Named major avenues/streets (primary & secondary)
            for (int i = 0; i < roadSamplePoints.Count && cityData.AutoCruiseSpawns.Count < 14; i++)
            {
                if (roadSamplePoints[i].importance >= 2 && !string.IsNullOrEmpty(roadSamplePoints[i].streetName))
                {
                    TryAddAutoSpawn(roadSamplePoints[i].streetName, roadSamplePoints[i].pos);
                }
            }
        }

        private static Vector3[] ExtractWayPolygon(OsmWayRecord way, Dictionary<long, OsmNodeRecord> nodes)
        {
            var pts = new List<Vector3>(way.NodeIds.Count);
            for (int i = 0; i < way.NodeIds.Count; i++)
            {
                if (nodes.TryGetValue(way.NodeIds[i], out OsmNodeRecord nd))
                {
                    pts.Add(nd.UnityPos);
                }
            }
            return pts.Count >= 3 ? pts.ToArray() : null;
        }

        /// <summary>
        /// Builds the AI road graph (Intersections, DirectedRoads, RaceDirectedRoads, and SidewalkStrips)
        /// directly from city/&lt;city&gt;.osm (or falls back to .obj road triangles if no .osm is present).
        /// </summary>
        public static bool PopulateAiRoadNetwork(MM2AiMapData aiData)
        {
            if (aiData == null || string.IsNullOrEmpty(aiData.CityName)) return false;

            CachedOsmCityData cityData = GetOrEnsureCityData(aiData.CityName);
            string osmPath = FindOsmVirtualPath(aiData.CityName);

            if (!string.IsNullOrEmpty(osmPath) &&
                ParseOsmXml(osmPath, cityData, out Dictionary<long, OsmNodeRecord> nodes, out List<OsmWayRecord> ways))
            {
                PopulateMapPreviewAndSpawnsFromOsm(cityData, nodes, ways);
                if (BuildAiGraphFromOsmNodesAndWays(aiData, cityData, nodes, ways))
                {
                    Debug.Log($"[MM2OsmCitySystem] Built AI road network for '{aiData.CityName}' from '{osmPath}': {aiData.Intersections.Count} intersections, {aiData.DirectedRoads.Count} directed roads, {aiData.SidewalkStrips.Count} sidewalks.");
                    return true;
                }
            }

            // Fallback when only .obj exists without .osm: synthesize intersections and road links from .obj road triangles
            if (cityData.RoadTriangles.Count > 0)
            {
                if (BuildFallbackAiGraphFromObjRoadTriangles(aiData, cityData))
                {
                    Debug.Log($"[MM2OsmCitySystem] Synthesized fallback AI road network for '{aiData.CityName}' from .obj road surfaces: {aiData.Intersections.Count} intersections, {aiData.DirectedRoads.Count} directed roads.");
                    return true;
                }
            }

            return false;
        }

        private static bool BuildAiGraphFromOsmNodesAndWays(
            MM2AiMapData aiData,
            CachedOsmCityData cityData,
            Dictionary<long, OsmNodeRecord> nodes,
            List<OsmWayRecord> ways)
        {
            // 1. Count node degree across driveable highway ways so we split ways at every real intersection
            var nodeUseCount = new Dictionary<long, int>(8192);
            var driveableWays = new List<OsmWayRecord>(2048);

            for (int w = 0; w < ways.Count; w++)
            {
                OsmWayRecord way = ways[w];
                if (!IsDriveableHighwayType(way.HighwayType, out _, out _, out _, out _)) continue;

                driveableWays.Add(way);
                for (int i = 0; i < way.NodeIds.Count; i++)
                {
                    long nid = way.NodeIds[i];
                    nodeUseCount.TryGetValue(nid, out int cnt);
                    // Endpoints always split; interior nodes split when shared by >= 2 ways
                    int weight = (i == 0 || i == way.NodeIds.Count - 1) ? 2 : 1;
                    nodeUseCount[nid] = cnt + weight;
                }
            }

            if (driveableWays.Count == 0) return false;

            var intersectionIdByOsmNode = new Dictionary<long, int>(4096);

            MM2AiMapData.BaiIntersection GetOrCreateIntersection(long osmNodeId, Vector3 fallbackPos)
            {
                if (intersectionIdByOsmNode.TryGetValue(osmNodeId, out int existingId) &&
                    aiData.IntersectionById.TryGetValue(existingId, out MM2AiMapData.BaiIntersection existing))
                {
                    return existing;
                }

                Vector3 center = nodes.TryGetValue(osmNodeId, out OsmNodeRecord nd) ? nd.UnityPos : fallbackPos;
                int newId = aiData.Intersections.Count;
                var inter = new MM2AiMapData.BaiIntersection
                {
                    Id = newId,
                    RoomId = (short)(newId + 1),
                    Center = center,
                    LightCycleOffset = (newId * 3.7f) % 20f
                };
                aiData.Intersections.Add(inter);
                aiData.IntersectionById[newId] = inter;
                intersectionIdByOsmNode[osmNodeId] = newId;
                return inter;
            }

            int nextPathId = 1;
            var segNodeIds = new List<long>(32);

            for (int w = 0; w < driveableWays.Count; w++)
            {
                OsmWayRecord way = driveableWays[w];
                IsDriveableHighwayType(way.HighwayType, out int defLanes, out float defSpeedMps, out int importance, out bool isFreeway);

                int totalLanes = way.Lanes > 0 ? way.Lanes : defLanes;
                float speedLimit = way.MaxSpeedMps > 0f ? way.MaxSpeedMps : defSpeedMps;
                bool isOnewayForward = (way.OnewayDir > 0);
                bool isOnewayReverse = (way.OnewayDir < 0);
                bool isOneWay = isOnewayForward || isOnewayReverse;

                int startIdx = 0;
                while (startIdx < way.NodeIds.Count - 1)
                {
                    segNodeIds.Clear();
                    segNodeIds.Add(way.NodeIds[startIdx]);

                    int endIdx = startIdx + 1;
                    while (endIdx < way.NodeIds.Count)
                    {
                        long nid = way.NodeIds[endIdx];
                        segNodeIds.Add(nid);
                        if (endIdx == way.NodeIds.Count - 1 ||
                            (nodeUseCount.TryGetValue(nid, out int uCnt) && uCnt >= 2))
                        {
                            break;
                        }
                        endIdx++;
                    }

                    startIdx = endIdx;
                    if (segNodeIds.Count < 2) continue;

                    var centerPts = new List<Vector3>(segNodeIds.Count);
                    for (int k = 0; k < segNodeIds.Count; k++)
                    {
                        if (nodes.TryGetValue(segNodeIds[k], out OsmNodeRecord nd))
                        {
                            Vector3 p = nd.UnityPos;
                            if (cityData.RoadTriangles.Count > 0 && way.Layer != 0)
                            {
                                p.y = SampleRoadElevationAt(cityData, p.x, p.z, p.y, way.Layer);
                            }
                            if (centerPts.Count == 0 || (p - centerPts[centerPts.Count - 1]).sqrMagnitude > 0.25f)
                            {
                                centerPts.Add(p);
                            }
                        }
                    }

                    if (centerPts.Count < 2) continue;

                    long startOsmNode = segNodeIds[0];
                    long endOsmNode = segNodeIds[segNodeIds.Count - 1];
                    MM2AiMapData.BaiIntersection interA = GetOrCreateIntersection(startOsmNode, centerPts[0]);
                    MM2AiMapData.BaiIntersection interB = GetOrCreateIntersection(endOsmNode, centerPts[centerPts.Count - 1]);
                    if (interA.Id == interB.Id) continue;

                    int pathId = nextPathId++;
                    short pathFlags = 0;
                    if (isFreeway) pathFlags |= 0x04;
                    else if (importance == 0) pathFlags |= 0x02;
                    aiData.PathFlagsById[pathId] = pathFlags;

                    int lanesPerDir = isOneWay ? Mathf.Max(1, totalLanes) : Mathf.Max(1, totalLanes / 2);
                    const float laneWidth = 3.4f;

                    bool hasSignalB = nodes.TryGetValue(endOsmNode, out OsmNodeRecord ndB) && ndB.IsTrafficSignal;
                    bool hasStopB = nodes.TryGetValue(endOsmNode, out OsmNodeRecord ndStopB) && ndStopB.IsStopSign;
                    bool hasSignalA = nodes.TryGetValue(startOsmNode, out OsmNodeRecord ndA) && ndA.IsTrafficSignal;
                    bool hasStopA = nodes.TryGetValue(startOsmNode, out OsmNodeRecord ndStopA) && ndStopA.IsStopSign;

                    // Forward road (interA -> interB)
                    CreateDirectedRoadSegment(
                        aiData,
                        pathId,
                        interA,
                        interB,
                        centerPts,
                        lanesPerDir,
                        laneWidth,
                        speedLimit,
                        isFreeway,
                        isOneWay,
                        isContraflow: isOnewayReverse,
                        hasTrafficLight: hasSignalB || (!isFreeway && importance >= 2 && (interB.Id % 3 == 0)),
                        hasStopSign: hasStopB);

                    // Reverse road (interB -> interA)
                    var revPts = new List<Vector3>(centerPts);
                    revPts.Reverse();
                    CreateDirectedRoadSegment(
                        aiData,
                        pathId,
                        interB,
                        interA,
                        revPts,
                        lanesPerDir,
                        laneWidth,
                        speedLimit,
                        isFreeway,
                        isOneWay,
                        isContraflow: isOnewayForward,
                        hasTrafficLight: hasSignalA || (!isFreeway && importance >= 2 && (interA.Id % 3 == 0)),
                        hasStopSign: hasStopA);

                    // Sidewalk strips for pedestrians along non-freeway urban streets
                    if (!isFreeway && importance >= 1)
                    {
                        float halfRoadWidth = (isOneWay ? lanesPerDir : lanesPerDir * 2) * laneWidth * 0.5f;
                        BuildSidewalkStripForRoad(aiData, pathId, 0, centerPts, halfRoadWidth, +1f);
                        BuildSidewalkStripForRoad(aiData, pathId, 1, revPts, halfRoadWidth, +1f);
                    }
                }
            }

            // Populate ConnectedPathIds on intersections
            for (int i = 0; i < aiData.Intersections.Count; i++)
            {
                MM2AiMapData.BaiIntersection inter = aiData.Intersections[i];
                var pids = new List<int>(inter.IncomingRoads.Count + inter.OutgoingRoads.Count);
                for (int r = 0; r < inter.OutgoingRoads.Count; r++)
                {
                    int pid = inter.OutgoingRoads[r].PathId;
                    if (!pids.Contains(pid)) pids.Add(pid);
                }
                for (int r = 0; r < inter.IncomingRoads.Count; r++)
                {
                    int pid = inter.IncomingRoads[r].PathId;
                    if (!pids.Contains(pid)) pids.Add(pid);
                }
                inter.ConnectedPathIds = pids.ToArray();
            }

            return aiData.DirectedRoads.Count > 0;
        }

        private static void CreateDirectedRoadSegment(
            MM2AiMapData aiData,
            int pathId,
            MM2AiMapData.BaiIntersection startInter,
            MM2AiMapData.BaiIntersection endInter,
            List<Vector3> centerPts,
            int laneCount,
            float laneWidth,
            float speedLimit,
            bool isFreeway,
            bool isOneWay,
            bool isContraflow,
            bool hasTrafficLight,
            bool hasStopSign)
        {
            int n = centerPts.Count;
            if (n < 2) return;

            var tangents = new Vector3[n];
            var rights = new Vector3[n];
            var upNormals = new Vector3[n];
            var cumDist = new float[n];

            float totalLen = 0f;
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    totalLen += Vector3.Distance(centerPts[i - 1], centerPts[i]);
                }
                cumDist[i] = totalLen;

                Vector3 tan;
                if (i == 0) tan = centerPts[1] - centerPts[0];
                else if (i == n - 1) tan = centerPts[n - 1] - centerPts[n - 2];
                else tan = centerPts[i + 1] - centerPts[i - 1];

                if (tan.sqrMagnitude < 1e-4f) tan = Vector3.forward;
                else tan.Normalize();
                tangents[i] = tan;

                Vector3 flatTan = new Vector3(tan.x, 0f, tan.z);
                if (flatTan.sqrMagnitude < 1e-4f) flatTan = Vector3.forward;
                else flatTan.Normalize();

                rights[i] = new Vector3(flatTan.z, 0f, -flatTan.x);
                upNormals[i] = Vector3.up;
            }

            if (totalLen < 1.5f) return;

            float sideSign = aiData.DriveOnLeft ? -1f : 1f;
            var lanes = new Vector3[laneCount][];
            for (int l = 0; l < laneCount; l++)
            {
                lanes[l] = new Vector3[n];
                float lateralOffset = isOneWay
                    ? ((l - (laneCount - 1) * 0.5f) * laneWidth * sideSign)
                    : ((0.5f + l) * laneWidth * sideSign);

                for (int i = 0; i < n; i++)
                {
                    lanes[l][i] = centerPts[i] + rights[i] * lateralOffset;
                }
            }

            var road = new MM2AiMapData.BaiDirectedRoad
            {
                RoadIndex = isContraflow ? (-1 - aiData.RaceDirectedRoads.Count) : aiData.DirectedRoads.Count,
                PathId = pathId,
                StartIntersectionId = startInter.Id,
                EndIntersectionId = endInter.Id,
                SpeedLimit = speedLimit,
                DensityMultiplier = 1f,
                HasTrafficLightAtEnd = hasTrafficLight && !isContraflow,
                HasStopSignAtEnd = hasStopSign && !hasTrafficLight && !isContraflow,
                StopDistAtEnd = 6.5f,
                IsDividedOrFreeway = isFreeway,
                IsOneWay = isOneWay,
                IsContraflowForRacing = isContraflow,
                Lanes = lanes,
                UpNormals = upNormals,
                CumulativeDistances = cumDist,
                TotalLength = totalLen,
                StartPosition = lanes[0][0],
                EndPosition = lanes[0][n - 1],
                StartTangent = tangents[0],
                EndTangent = tangents[n - 1],
                PsdlRoomIds = new short[] { startInter.RoomId, endInter.RoomId }
            };

            if (!isContraflow)
            {
                aiData.DirectedRoads.Add(road);
                startInter.OutgoingRoads.Add(road);
                endInter.IncomingRoads.Add(road);
            }

            aiData.RaceDirectedRoads.Add(road);
            startInter.RaceOutgoingRoads.Add(road);
        }

        private static void BuildSidewalkStripForRoad(
            MM2AiMapData aiData,
            int pathId,
            int sideIndex,
            List<Vector3> centerPts,
            float halfRoadWidth,
            float rightSign)
        {
            int n = centerPts.Count;
            if (n < 2) return;

            var curbPts = new Vector3[n];
            var midPts = new Vector3[n];
            var wallPts = new Vector3[n];
            var cumDist = new float[n];
            float totalLen = 0f;

            for (int i = 0; i < n; i++)
            {
                if (i > 0) totalLen += Vector3.Distance(centerPts[i - 1], centerPts[i]);
                cumDist[i] = totalLen;

                Vector3 tan = (i == 0) ? (centerPts[1] - centerPts[0])
                    : (i == n - 1 ? (centerPts[n - 1] - centerPts[n - 2]) : (centerPts[i + 1] - centerPts[i - 1]));
                tan.y = 0f;
                if (tan.sqrMagnitude < 1e-4f) tan = Vector3.forward;
                else tan.Normalize();

                Vector3 right = new Vector3(tan.z, 0f, -tan.x) * rightSign;
                Vector3 up = new Vector3(0f, 0.15f, 0f);

                curbPts[i] = centerPts[i] + right * (halfRoadWidth + 0.3f) + up;
                midPts[i] = centerPts[i] + right * (halfRoadWidth + 1.6f) + up;
                wallPts[i] = centerPts[i] + right * (halfRoadWidth + 2.9f) + up;
            }

            if (totalLen < 6f) return;

            aiData.SidewalkStrips.Add(new MM2AiMapData.BaiSidewalkStrip
            {
                PathId = pathId,
                SideIndex = sideIndex,
                CenterPoints = midPts,
                CurbPoints = curbPts,
                WallPoints = wallPts,
                CumulativeDistances = cumDist,
                TotalLength = totalLen
            });
        }

        private static bool BuildFallbackAiGraphFromObjRoadTriangles(MM2AiMapData aiData, CachedOsmCityData cityData)
        {
            const float cellStep = 65f;
            var cellCenters = new Dictionary<(int gx, int gz), Vector3>();

            for (int i = 0; i < cityData.RoadTriangles.Count; i++)
            {
                RoadElevationSample rt = cityData.RoadTriangles[i];
                Vector3 c = (rt.A + rt.B + rt.C) / 3f;
                int gx = Mathf.RoundToInt(c.x / cellStep);
                int gz = Mathf.RoundToInt(c.z / cellStep);
                if (!cellCenters.ContainsKey((gx, gz)))
                {
                    cellCenters[(gx, gz)] = c;
                }
            }

            if (cellCenters.Count == 0) return false;

            var interByCell = new Dictionary<(int gx, int gz), MM2AiMapData.BaiIntersection>();
            foreach (var kvp in cellCenters)
            {
                int id = aiData.Intersections.Count;
                var inter = new MM2AiMapData.BaiIntersection
                {
                    Id = id,
                    RoomId = (short)(id + 1),
                    Center = kvp.Value,
                    LightCycleOffset = (id * 3.7f) % 20f,
                    ConnectedPathIds = new int[] { id + 1 }
                };
                aiData.Intersections.Add(inter);
                aiData.IntersectionById[id] = inter;
                interByCell[kvp.Key] = inter;
            }

            int nextPathId = 1;
            int[] dxs = { 1, 0, 1, -1 };
            int[] dzs = { 0, 1, 1, 1 };

            foreach (var kvp in interByCell)
            {
                var cell = kvp.Key;
                MM2AiMapData.BaiIntersection interA = kvp.Value;

                for (int d = 0; d < 4; d++)
                {
                    var nbrCell = (cell.gx + dxs[d], cell.gz + dzs[d]);
                    if (interByCell.TryGetValue(nbrCell, out MM2AiMapData.BaiIntersection interB))
                    {
                        var ptsAB = new List<Vector3> { interA.Center, interB.Center };
                        var ptsBA = new List<Vector3> { interB.Center, interA.Center };
                        int pid = nextPathId++;
                        CreateDirectedRoadSegment(aiData, pid, interA, interB, ptsAB, 1, 3.5f, 13.5f, false, false, false, false, false);
                        CreateDirectedRoadSegment(aiData, pid, interB, interA, ptsBA, 1, 3.5f, 13.5f, false, false, false, false, false);
                    }
                }
            }

            return aiData.Intersections.Count > 0;
        }

        /// <summary>
        /// Builds a complete 3D procedural city directly from a .osm file when no .obj file is present.
        /// Extrudes roads, sidewalks, 3D buildings, water surfaces, and parks.
        /// </summary>
        private static bool BuildProceduralGeometryFromOsmOnly(
            CitySpec spec,
            string osmVirtualPath,
            CachedOsmCityData cityData,
            Transform cityRoot,
            Transform bangersRoot,
            WaterTriangleRecorder recordWaterTriangles,
            out float minX,
            out float maxX,
            out float minZ,
            out float maxZ,
            out float minVertY)
        {
            minX = -1500f; maxX = 1500f;
            minZ = -1500f; maxZ = 1500f;
            minVertY = 0f;

            if (!ParseOsmXml(osmVirtualPath, cityData, out Dictionary<long, OsmNodeRecord> nodes, out List<OsmWayRecord> ways))
            {
                return false;
            }

            PopulateMapPreviewAndSpawnsFromOsm(cityData, nodes, ways);

            minX = float.MaxValue; maxX = float.MinValue;
            minZ = float.MaxValue; maxZ = float.MinValue;
            foreach (OsmNodeRecord nd in nodes.Values)
            {
                if (nd.UnityPos.x < minX) minX = nd.UnityPos.x;
                if (nd.UnityPos.x > maxX) maxX = nd.UnityPos.x;
                if (nd.UnityPos.z < minZ) minZ = nd.UnityPos.z;
                if (nd.UnityPos.z > maxZ) maxZ = nd.UnityPos.z;
            }
            if (minX >= maxX) { minX = -1500f; maxX = 1500f; minZ = -1500f; maxZ = 1500f; }

            cityData.Projection.MinUnityX = minX;
            cityData.Projection.MaxUnityX = maxX;
            cityData.Projection.MinUnityZ = minZ;
            cityData.Projection.MaxUnityZ = maxZ;

            const int GridN = 4;
            var chunks = new ObjDistrictChunk[GridN, GridN];
            for (int gx = 0; gx < GridN; gx++)
                for (int gz = 0; gz < GridN; gz++)
                    chunks[gx, gz] = new ObjDistrictChunk();

            float cMinX = minX;
            float cMinZ = minZ;
            float spanX = Mathf.Max(1f, maxX - cMinX);
            float spanZ = Mathf.Max(1f, maxZ - cMinZ);
            ObjDistrictChunk GetChunk(float ux, float uz)
            {
                int gx = Mathf.Clamp((int)((ux - cMinX) / spanX * GridN), 0, GridN - 1);
                int gz = Mathf.Clamp((int)((uz - cMinZ) / spanZ * GridN), 0, GridN - 1);
                return chunks[gx, gz];
            }

            ObjSubmeshBucket GetBucket(ObjDistrictChunk ch, string mat)
            {
                if (!ch.VisualByMaterial.TryGetValue(mat, out ObjSubmeshBucket b))
                {
                    b = new ObjSubmeshBucket();
                    ch.VisualByMaterial[mat] = b;
                }
                return b;
            }

            // Ground base quad under the city
            for (int gx = 0; gx < GridN; gx++)
            {
                for (int gz = 0; gz < GridN; gz++)
                {
                    float x0 = minX + (gx / (float)GridN) * spanX - 20f;
                    float x1 = minX + ((gx + 1) / (float)GridN) * spanX + 20f;
                    float z0 = minZ + (gz / (float)GridN) * spanZ - 20f;
                    float z1 = minZ + ((gz + 1) / (float)GridN) * spanZ + 20f;

                    ObjDistrictChunk ch = chunks[gx, gz];
                    ObjSubmeshBucket gb = GetBucket(ch, "CONCRETE_0");
                    AddQuadToBucketAndCol(
                        gb, ch,
                        new Vector3(x0, -0.04f, z0),
                        new Vector3(x0, -0.04f, z1),
                        new Vector3(x1, -0.04f, z1),
                        new Vector3(x1, -0.04f, z0),
                        Vector3.up,
                        includeCollider: true);
                }
            }

            // Extrude roads and buildings from ways
            for (int w = 0; w < ways.Count; w++)
            {
                OsmWayRecord way = ways[w];
                if (IsDriveableHighwayType(way.HighwayType, out int defLanes, out _, out _, out _))
                {
                    int lanes = way.Lanes > 0 ? way.Lanes : defLanes;
                    float halfW = (way.WidthMeters > 0f ? way.WidthMeters : Mathf.Max(6f, lanes * 3.5f)) * 0.5f;
                    float roadY = way.Layer * 5.5f;

                    for (int i = 0; i + 1 < way.NodeIds.Count; i++)
                    {
                        if (!nodes.TryGetValue(way.NodeIds[i], out OsmNodeRecord n0) ||
                            !nodes.TryGetValue(way.NodeIds[i + 1], out OsmNodeRecord n1))
                        {
                            continue;
                        }

                        Vector3 p0 = new Vector3(n0.UnityPos.x, roadY, n0.UnityPos.z);
                        Vector3 p1 = new Vector3(n1.UnityPos.x, roadY, n1.UnityPos.z);
                        Vector3 dir = p1 - p0;
                        dir.y = 0f;
                        if (dir.sqrMagnitude < 0.05f) continue;
                        dir.Normalize();
                        Vector3 right = new Vector3(dir.z, 0f, -dir.x);

                        Vector3 a = p0 - right * halfW;
                        Vector3 b = p1 - right * halfW;
                        Vector3 c = p1 + right * halfW;
                        Vector3 d = p0 + right * halfW;

                        ObjDistrictChunk ch = GetChunk((p0.x + p1.x) * 0.5f, (p0.z + p1.z) * 0.5f);
                        ObjSubmeshBucket rb = GetBucket(ch, "ASPHALT_0");
                        AddQuadToBucketAndCol(rb, ch, a, b, c, d, Vector3.up, includeCollider: true);
                        AddRoadElevationTriangle(cityData, a, b, c);
                        AddRoadElevationTriangle(cityData, a, c, d);
                    }
                }
                else if (!string.IsNullOrEmpty(way.BuildingType) && way.NodeIds.Count >= 3)
                {
                    float h = way.BuildingHeightMeters > 0f ? way.BuildingHeightMeters : (10.5f + (Mathf.Abs((int)way.Id) % 4) * 3.5f);
                    if (!nodes.TryGetValue(way.NodeIds[0], out OsmNodeRecord firstNd)) continue;
                    ObjDistrictChunk ch = GetChunk(firstNd.UnityPos.x, firstNd.UnityPos.z);
                    ObjSubmeshBucket wb = GetBucket(ch, "BUILDING_WALL_0");

                    for (int i = 0; i + 1 < way.NodeIds.Count; i++)
                    {
                        if (!nodes.TryGetValue(way.NodeIds[i], out OsmNodeRecord n0) ||
                            !nodes.TryGetValue(way.NodeIds[i + 1], out OsmNodeRecord n1))
                        {
                            continue;
                        }

                        Vector3 p0 = new Vector3(n0.UnityPos.x, 0f, n0.UnityPos.z);
                        Vector3 p1 = new Vector3(n1.UnityPos.x, 0f, n1.UnityPos.z);
                        Vector3 p2 = new Vector3(n1.UnityPos.x, h, n1.UnityPos.z);
                        Vector3 p3 = new Vector3(n0.UnityPos.x, h, n0.UnityPos.z);
                        Vector3 edge = p1 - p0;
                        if (edge.sqrMagnitude < 0.05f) continue;
                        Vector3 wallNorm = new Vector3(edge.z, 0f, -edge.x).normalized;
                        // Add double-sided wall quad so both CW and CCW OSM building footprints collide and render cleanly
                        AddQuadToBucketAndCol(wb, ch, p0, p1, p2, p3, wallNorm, includeCollider: true);
                        AddQuadToBucketAndCol(wb, ch, p1, p0, p3, p2, -wallNorm, includeCollider: true);
                    }
                }
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var emptyMtl = new Dictionary<string, MtlMaterialSpec>();
            var matCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

            for (int gx = 0; gx < GridN; gx++)
            {
                for (int gz = 0; gz < GridN; gz++)
                {
                    ObjDistrictChunk ch = chunks[gx, gz];
                    if (ch.VisualByMaterial.Count == 0) continue;

                    GameObject chunkGo = new GameObject($"{spec.MapName}_District_{gx}_{gz}");
                    chunkGo.transform.SetParent(cityRoot, false);

                    var allPos = new List<Vector3>();
                    var allNorm = new List<Vector3>();
                    var allUV = new List<Vector2>();
                    var subTris = new List<int[]>();
                    var mats = new List<Material>();

                    foreach (var kvp in ch.VisualByMaterial)
                    {
                        ObjSubmeshBucket b = kvp.Value;
                        if (b.Triangles.Count == 0) continue;

                        int baseV = allPos.Count;
                        allPos.AddRange(b.Positions);
                        allNorm.AddRange(b.Normals);
                        allUV.AddRange(b.UVs);

                        int[] indices = new int[b.Triangles.Count];
                        for (int i = 0; i < indices.Length; i++) indices[i] = baseV + b.Triangles[i];
                        subTris.Add(indices);

                        if (!matCache.TryGetValue(kvp.Key, out Material m))
                        {
                            m = CreateUnityMaterialForObj(kvp.Key, emptyMtl, urpLit);
                            matCache[kvp.Key] = m;
                        }
                        mats.Add(m);
                    }

                    Mesh visMesh = new Mesh { name = chunkGo.name, indexFormat = IndexFormat.UInt32 };
                    visMesh.SetVertices(allPos);
                    visMesh.SetNormals(allNorm);
                    visMesh.SetUVs(0, allUV);
                    visMesh.subMeshCount = subTris.Count;
                    for (int s = 0; s < subTris.Count; s++) visMesh.SetTriangles(subTris[s], s, false);
                    visMesh.RecalculateBounds();

                    chunkGo.AddComponent<MeshFilter>().sharedMesh = visMesh;
                    chunkGo.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();

                    if (ch.ColPositions.Count > 0 && ch.ColTriangles.Count > 0)
                    {
                        Mesh colMesh = new Mesh { name = $"{chunkGo.name}_Col", indexFormat = IndexFormat.UInt32 };
                        colMesh.SetVertices(ch.ColPositions);
                        colMesh.SetTriangles(ch.ColTriangles, 0, true);
                        chunkGo.AddComponent<MeshCollider>().sharedMesh = colMesh;
                    }
                }
            }

            return true;
        }

        private static void AddQuadToBucketAndCol(
            ObjSubmeshBucket b,
            ObjDistrictChunk ch,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 norm,
            bool includeCollider)
        {
            int v0 = b.Positions.Count;
            b.Positions.Add(p0); b.Positions.Add(p1); b.Positions.Add(p2); b.Positions.Add(p3);
            b.Normals.Add(norm); b.Normals.Add(norm); b.Normals.Add(norm); b.Normals.Add(norm);
            b.UVs.Add(ComputeAutoArchitecturalUV(p0, norm));
            b.UVs.Add(ComputeAutoArchitecturalUV(p1, norm));
            b.UVs.Add(ComputeAutoArchitecturalUV(p2, norm));
            b.UVs.Add(ComputeAutoArchitecturalUV(p3, norm));

            b.Triangles.Add(v0); b.Triangles.Add(v0 + 1); b.Triangles.Add(v0 + 2);
            b.Triangles.Add(v0); b.Triangles.Add(v0 + 2); b.Triangles.Add(v0 + 3);

            if (includeCollider && ch != null)
            {
                int c0 = ch.ColPositions.Count;
                ch.ColPositions.Add(p0); ch.ColPositions.Add(p1); ch.ColPositions.Add(p2); ch.ColPositions.Add(p3);
                ch.ColTriangles.Add(c0); ch.ColTriangles.Add(c0 + 1); ch.ColTriangles.Add(c0 + 2);
                ch.ColTriangles.Add(c0); ch.ColTriangles.Add(c0 + 2); ch.ColTriangles.Add(c0 + 3);
            }
        }

        // ========================================================================================================
        // AUTO-GENERATED 2D MENU MAP PREVIEW & MINIMAP RENDERING FROM OSM DATA
        // ========================================================================================================

        public static IReadOnlyList<MM2CruiseSpawnLocation> GetAutoCruiseSpawns(string mapName)
        {
            if (!HasOsmOrObjCity(mapName)) return Array.Empty<MM2CruiseSpawnLocation>();
            CachedOsmCityData data = GetOrEnsureCityData(mapName);
            if (data.AutoCruiseSpawns.Count == 0)
            {
                string osmPath = FindOsmVirtualPath(mapName);
                if (!string.IsNullOrEmpty(osmPath))
                {
                    EnsureOsmMetadataParsed(mapName, osmPath, data);
                }
            }
            return data.AutoCruiseSpawns;
        }

        public static Sprite GetOrCreateOsmMapPreviewSprite(string mapName)
        {
            if (!HasOsmOrObjCity(mapName)) return null;
            CachedOsmCityData data = GetOrEnsureCityData(mapName);
            if (data.GeneratedMapSprite != null) return data.GeneratedMapSprite;

            if (data.MapRoads.Count == 0)
            {
                string osmPath = FindOsmVirtualPath(mapName);
                if (!string.IsNullOrEmpty(osmPath))
                {
                    EnsureOsmMetadataParsed(mapName, osmPath, data);
                }
            }

            if (data.MapRoads.Count == 0) return null;

            const int w = 256;
            const int h = 192;
            Texture2D tex = RenderTopDownOsmMapTexture(data, w, h);
            if (tex == null) return null;

            data.GeneratedMapSprite = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f));
            return data.GeneratedMapSprite;
        }

        private static Texture2D RenderTopDownOsmMapTexture(CachedOsmCityData data, int width, int height)
        {
            if (data == null || data.MapRoads.Count == 0) return null;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < data.MapRoads.Count; i++)
            {
                var r = data.MapRoads[i];
                if (r.a.x < minX) minX = r.a.x;
                if (r.a.x > maxX) maxX = r.a.x;
                if (r.a.z < minZ) minZ = r.a.z;
                if (r.a.z > maxZ) maxZ = r.a.z;
            }
            if (minX >= maxX || minZ >= maxZ) return null;

            float spanX = Mathf.Max(100f, maxX - minX);
            float spanZ = Mathf.Max(100f, maxZ - minZ);
            float padX = spanX * 0.06f;
            float padZ = spanZ * 0.06f;
            minX -= padX; maxX += padX;
            minZ -= padZ; maxZ += padZ;
            spanX = maxX - minX;
            spanZ = maxZ - minZ;

            Color32 bgCol = new Color32(28, 32, 38, 255);
            Color32 minorRoadCol = new Color32(105, 112, 122, 255);
            Color32 majorRoadCol = new Color32(215, 195, 120, 255);
            Color32 hwyRoadCol = new Color32(245, 155, 65, 255);

            Color32[] px = new Color32[width * height];
            for (int i = 0; i < px.Length; i++) px[i] = bgCol;

            void DrawLine(Vector3 a, Vector3 b, Color32 col, int radius)
            {
                // In Unity world space: -X is East, -Z is North
                int x0 = Mathf.RoundToInt((1f - (a.x - minX) / spanX) * (width - 1));
                int y0 = Mathf.RoundToInt((1f - (a.z - minZ) / spanZ) * (height - 1));
                int x1 = Mathf.RoundToInt((1f - (b.x - minX) / spanX) * (width - 1));
                int y1 = Mathf.RoundToInt((1f - (b.z - minZ) / spanZ) * (height - 1));

                int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
                if (steps == 0) steps = 1;
                for (int s = 0; s <= steps; s++)
                {
                    int x = x0 + (x1 - x0) * s / steps;
                    int y = y0 + (y1 - y0) * s / steps;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int py = y + dy;
                        if (py < 0 || py >= height) continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int pxIdx = x + dx;
                            if (pxIdx < 0 || pxIdx >= width) continue;
                            px[py * width + pxIdx] = col;
                        }
                    }
                }
            }

            // Pass 1: Minor streets
            for (int i = 0; i < data.MapRoads.Count; i++)
            {
                var r = data.MapRoads[i];
                if (r.importance <= 1) DrawLine(r.a, r.b, minorRoadCol, 0);
            }
            // Pass 2: Major avenues
            for (int i = 0; i < data.MapRoads.Count; i++)
            {
                var r = data.MapRoads[i];
                if (r.importance == 2 || r.importance == 3) DrawLine(r.a, r.b, majorRoadCol, 1);
            }
            // Pass 3: Motorways / trunks
            for (int i = 0; i < data.MapRoads.Count; i++)
            {
                var r = data.MapRoads[i];
                if (r.importance >= 4) DrawLine(r.a, r.b, hwyRoadCol, 1);
            }

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = $"OsmMapPreview_{data.MapName}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }
    }
}
