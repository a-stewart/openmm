using System;
using System.Collections.Generic;
using MM2.AI;
using MM2.Archive;
using MM2.Vehicle;
using UnityEngine;
using UnityEngine.Rendering;

namespace MM2.UI
{
    /// <summary>
    /// Runtime race manager for Midtown Madness 2 Blitz, Checkpoint, and Circuit races.
    /// Spawns 3D checkpoint gates (geometry/pt_check.pkg & geometry/pt_finish.pkg) from .ar archives,
    /// runs the "Ready..." -> "Set..." -> "Go" start sequence, tracks timers/laps/checkpoints,
    /// and provides as-the-crow-flies target direction for the HUD 3D green arrow.
    /// </summary>
    public class MM2RaceManager : MonoBehaviour
    {
        private static MM2RaceManager instance;
        public static MM2RaceManager Instance => instance;

        private class CheckpointNode
        {
            public int Index;
            public MM2RaceWaypoint Waypoint;
            public GameObject RootObject;
            public MeshFilter ArchMeshFilter;
            public Transform BeaconTransform;
            public bool IsCollected;
            public bool IsFinishGate;
        }

        private readonly List<CheckpointNode> checkpoints = new List<CheckpointNode>();
        private GameObject checkpointsWorldRoot;

        private Mesh meshCheckpointArch;
        private Mesh meshFinishArch;
        private Material matCheckpointStand;
        private Mesh meshCyanBeacon;
        private Material matCyanBeacon;

        private AudioClip sfxCountdownLow;
        private AudioClip sfxCountdownHigh;
        private AudioClip sfxWaypoint;
        private AudioClip sfxLastWaypoint;
        private AudioClip sfxRaceWin;
        private AudioClip sfxRaceLose;
        private AudioClip sfxTimerWarning;
        private AudioClip sfxWaterSplash;
        private AudioSource raceAudioSource;

        // Active race state
        public bool IsRaceActive { get; private set; }
        public string ActiveRaceType { get; private set; } = "Cruise";
        public int CollectedCheckpoints { get; private set; }
        public int TotalCheckpoints { get; private set; }
        public int CurrentLap { get; private set; } = 1;
        public int TotalLaps { get; private set; } = 3;
        public float DisplayTimeSeconds { get; private set; }
        public float ElapsedTime => DisplayTimeSeconds;
        public string CenterBannerText { get; private set; } = string.Empty;
        public bool IsRaceFinished { get; private set; }
        public bool IsCountdownLockActive { get; private set; }
        public bool IsCountdownFrozen => IsCountdownLockActive;
        public int PlayerPosition { get; private set; } = 1;
        public int TotalRacers { get; private set; } = 1;
        public bool IsSinkingInWater { get; private set; }
        public bool IsPlayerTotaled { get; private set; }
        public bool IsCruiseRepairZoomingIn { get; private set; }
        public float CruiseRepairZoomInProgress01 { get; private set; }
        public float WaterSurfaceY { get; private set; } = -2.0f;
        private bool isImmersedInWaterPhysics;
        private float cruiseRepairZoomTimer;
        public IReadOnlyList<float> CompletedLapTimes => completedLapTimes;

        private readonly List<float> completedLapTimes = new List<float>();
        private float currentLapStartTime;
        private bool hasTriggeredEventResults;
        private bool hasRecordedRaceResult;
        private float blitzElapsedSeconds;

        private MM2AiMapData currentAiMap;
        private MM2TrafficManager trafficManager;
        private MM2SpecialTrafficManager specialTrafficManager;
        private MM2PedestrianManager pedestrianManager;
        private MM2PoliceManager policeManager;
        private MM2OpponentManager opponentManager;
        private float playerFinishTime;

        private float countdownElapsed;
        private int countdownStage; // 0 = Ready, 1 = Set, 2 = Go, 3 = Racing
        private float finishBannerTimer;
        private int lastWarningSecond = -1;

        private Vector3 startHoldPosition;
        private Quaternion startHoldRotation;
        private Vector3 prevCarPosition;
        private bool hasPrevCarPosition;

        // Crash Course & Cruise announcer speech state
        private AudioSource speechAudioSource;
        private string lastPlayedCruiseSpeechStem;
        private MM2CrashLessonSpec activeCrashLesson;
        private int activeCrashStageIndex;
        private MM2CrashLessonStage activeCrashStage;
        private bool crashHasCountdownTimer;
        private bool crashHasReachedMinSpeed;
        private float crashBelowMinSpeedTimer;
        private float crashInitialDamage01;
        private float crashTransientBannerTimer;
        private bool crashLessonPassed;
        private string crashEndStatusText;

        public int ActiveCrashEventType =>
            string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase) && activeCrashLesson != null
                ? activeCrashStage.EventType
                : -1;
        public float ActiveCrashFollowDistNear => activeCrashStage.FollowDistNear;
        public float ActiveCrashFollowDistFar => activeCrashStage.FollowDistFar;

        public bool TryGetCrashFollowDistanceDebugInfo(
            Vector3 playerPos,
            out float currentDist,
            out float failNear,
            out float warnNear,
            out float warnFar,
            out float failFar
        )
        {
            currentDist = 0f;
            failNear = 8.0f;
            warnNear = 25.0f;
            warnFar = 97.5f;
            failFar = 161.25f;

            if (!IsRaceActive ||
                !string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase) ||
                activeCrashLesson == null ||
                activeCrashStage.EventType != 2 ||
                opponentManager == null ||
                opponentManager.ActiveOpponents.Count == 0)
            {
                return false;
            }

            MM2OpponentAgent targetOpp = opponentManager.ActiveOpponents[0];
            if (targetOpp == null) return false;

            currentDist = Vector3.Distance(playerPos, targetOpp.transform.position);
            failNear = 8.0f;
            warnNear = Mathf.Max(failNear + 2f, activeCrashStage.FollowDistNear);
            warnFar = Mathf.Max(warnNear + 5f, activeCrashStage.FollowDistFar * 1.3f);
            failFar = Mathf.Max(warnFar + 5f, activeCrashStage.FollowDistFar * 2.15f);
            return true;
        }

        public bool TryGetCrashDestroyCarDebugInfo(
            ArcadeVehicleController playerCar,
            out float playerDamagePercent,
            out float opponentDamagePercent
        )
        {
            playerDamagePercent = 0f;
            opponentDamagePercent = 0f;

            if (!IsRaceActive ||
                !string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase) ||
                activeCrashLesson == null ||
                activeCrashStage.EventType != 8 ||
                opponentManager == null ||
                opponentManager.ActiveOpponents.Count == 0)
            {
                return false;
            }

            MM2OpponentAgent targetOpp = opponentManager.ActiveOpponents[0];
            if (targetOpp == null) return false;

            float playerDmg01 = (playerCar != null && playerCar.VehicleDamage != null)
                ? playerCar.VehicleDamage.Damage01
                : 0f;
            float oppDmg01 = (targetOpp.Damage != null)
                ? (targetOpp.Damage.IsTotaled ? 1.0f : Mathf.Clamp01(targetOpp.Damage.Damage01 / 0.98f))
                : 0f;

            playerDamagePercent = Mathf.Clamp(playerDmg01 * 100f, 0f, 100f);
            opponentDamagePercent = Mathf.Clamp(oppDmg01 * 100f, 0f, 100f);
            return true;
        }

        public static MM2RaceManager EnsureExists()
        {
            if (instance != null) return instance;

            MM2RaceManager existing = FindFirstObjectByType<MM2RaceManager>();
            if (existing != null)
            {
                instance = existing;
                return instance;
            }

            GameObject go = new GameObject("MM2_RaceManager");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MM2RaceManager>();
            return instance;
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;

            raceAudioSource = gameObject.AddComponent<AudioSource>();
            raceAudioSource.spatialBlend = 0f;
            raceAudioSource.volume = 0.85f;
            raceAudioSource.playOnAwake = false;

            speechAudioSource = gameObject.AddComponent<AudioSource>();
            speechAudioSource.spatialBlend = 0f;
            speechAudioSource.volume = 1.0f;
            speechAudioSource.playOnAwake = false;
        }

        private void OnDestroy()
        {
            ClearSpawnedCheckpoints();
            if (instance == this)
            {
                instance = null;
            }
        }

        private void EnsureAssetsLoaded()
        {
            MM2ArchiveSystem.EnsureInitialized();

            if (meshCheckpointArch == null)
            {
                meshCheckpointArch = BuildArchMeshFromPkg("pt_check");
            }
            if (meshFinishArch == null)
            {
                meshFinishArch = BuildArchMeshFromPkg("pt_finish");
                if (meshFinishArch == null) meshFinishArch = meshCheckpointArch;
            }
            if (matCheckpointStand == null)
            {
                matCheckpointStand = BuildCheckpointStandMaterial();
            }
            if (meshCyanBeacon == null)
            {
                meshCyanBeacon = BuildCyanDiamondMesh();
            }
            if (matCyanBeacon == null)
            {
                Shader unlit =
                    Shader.Find("Universal Render Pipeline/Unlit") ??
                    Shader.Find("Unlit/Color") ??
                    Shader.Find("Sprites/Default") ??
                    Shader.Find("Standard");
                matCyanBeacon = new Material(unlit)
                {
                    name = "Mat_MM2_CheckpointCyanBeacon",
                    color = new Color32(20, 250, 255, 255)
                };
                if (matCyanBeacon.HasProperty("_BaseColor"))
                {
                    matCyanBeacon.SetColor("_BaseColor", new Color32(20, 250, 255, 255));
                }
            }

            if (sfxCountdownLow == null) sfxCountdownLow = MM2RuntimeDecoders.LoadAudioClip("startracelow");
            if (sfxCountdownHigh == null) sfxCountdownHigh = MM2RuntimeDecoders.LoadAudioClip("startracehigh");
            if (sfxWaypoint == null) sfxWaypoint = MM2RuntimeDecoders.LoadAudioClip("waypoint");
            if (sfxLastWaypoint == null) sfxLastWaypoint = MM2RuntimeDecoders.LoadAudioClip("lastwaypoint");
            if (sfxRaceWin == null) sfxRaceWin = MM2RuntimeDecoders.LoadAudioClip("endofracetag");
            if (sfxRaceLose == null) sfxRaceLose = MM2RuntimeDecoders.LoadAudioClip("youlose");
            if (sfxTimerWarning == null) sfxTimerWarning = MM2RuntimeDecoders.LoadAudioClip("timerwarning");
            if (sfxWaterSplash == null) sfxWaterSplash = MM2RuntimeDecoders.LoadAudioClip("fountainsplash");
        }

        private static Mesh BuildArchMeshFromPkg(string pkgName)
        {
            MM2RuntimePkgLoader.ParsedPkg pkg = MM2RuntimePkgLoader.LoadPkg(pkgName);
            if (pkg == null || pkg.GeometryChunks.Count == 0) return null;

            List<MM2RuntimePkgLoader.PkgSection> sections = null;
            string[] lodOrder = { "H", "M", "L", "VL" };
            foreach (string lod in lodOrder)
            {
                if (pkg.GeometryChunks.TryGetValue(lod, out sections) && sections != null && sections.Count > 0)
                {
                    break;
                }
            }
            if (sections == null || sections.Count == 0) return null;

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            foreach (MM2RuntimePkgLoader.PkgSection sec in sections)
            {
                int baseV = positions.Count;
                for (int i = 0; i < sec.Positions.Count; i++)
                {
                    Vector3 p = sec.Positions[i];
                    Vector3 n = i < sec.Normals.Count ? sec.Normals[i] : Vector3.forward;
                    Vector2 uv = i < sec.UVs.Count ? sec.UVs[i] : Vector2.zero;

                    // Convert AGE local space (-Z forward, -X left, +Y up) -> Unity local space (+Z forward, -X left, +Y up)
                    positions.Add(new Vector3(p.x, p.y, -p.z));
                    normals.Add(new Vector3(n.x, n.y, -n.z).normalized);
                    uvs.Add(uv);
                }

                for (int i = 0; i + 2 < sec.Triangles.Count; i += 3)
                {
                    triangles.Add(baseV + sec.Triangles[i]);
                    triangles.Add(baseV + sec.Triangles[i + 2]);
                    triangles.Add(baseV + sec.Triangles[i + 1]);
                }
            }

            if (positions.Count == 0 || triangles.Count == 0) return null;

            Mesh mesh = new Mesh
            {
                name = $"MM2_{pkgName}_Mesh"
            };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material BuildCheckpointStandMaterial()
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material mat = new Material(urpLit)
            {
                name = "Mat_MM2_CheckpointStand"
            };

            // Keep backface culling enabled (_Cull = 2) because pt_check.pkg and pt_finish.pkg
            // have explicit front and back banner quads at z=0.085 with opposite winding so
            // "CHECKPOINT" and "FINISH" read left-to-right from both sides without Z-fighting.
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 2f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.15f);

            var texInfo = MM2RuntimeDecoders.LoadTexture("checkpointstand");
            if (texInfo.Texture != null)
            {
                texInfo.Texture.wrapMode = TextureWrapMode.Clamp;
                mat.mainTexture = texInfo.Texture;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texInfo.Texture);

                // Enable alpha cutout for the yellow X-truss scaffolding cutouts
                mat.SetFloat("_Surface", 0f);
                mat.SetFloat("_AlphaClip", 1f);
                mat.SetFloat("_Cutoff", 0.35f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.renderQueue = (int)RenderQueue.AlphaTest;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            mat.color = Color.white;
            return mat;
        }

        private static Mesh BuildCyanDiamondMesh()
        {
            // Authentic elongated vertical cyan diamond/crystal beacon floating above checkpoints (as seen in Screenshot 2)
            Mesh m = new Mesh { name = "MM2_CyanBeaconDiamond" };
            Vector3 top = new Vector3(0f, 1.45f, 0f);
            Vector3 bot = new Vector3(0f, -1.45f, 0f);
            Vector3 n = new Vector3(0f, 0.25f, 0.48f);
            Vector3 s = new Vector3(0f, 0.25f, -0.48f);
            Vector3 e = new Vector3(0.48f, 0.25f, 0f);
            Vector3 w = new Vector3(-0.48f, 0.25f, 0f);

            m.vertices = new[] { top, bot, n, e, s, w };
            m.triangles = new[]
            {
                0, 2, 3,
                0, 3, 4,
                0, 4, 5,
                0, 5, 2,
                1, 3, 2,
                1, 4, 3,
                1, 5, 4,
                1, 2, 5
            };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Configures the current session (Cruise vs Blitz / Checkpoint / Circuit) and returns the player start pose.
        /// </summary>
        public void SetupSessionAndGetSpawnPose(
            out Vector3 spawnPos,
            out Quaternion spawnRot
        )
        {
            ClearSpawnedCheckpoints();
            EnsureAssetsLoaded();
            if (speechAudioSource != null)
            {
                speechAudioSource.Stop();
            }

            IsSinkingInWater = false;
            isImmersedInWaterPhysics = false;
            IsPlayerTotaled = false;
            IsCruiseRepairZoomingIn = false;
            CruiseRepairZoomInProgress01 = 0f;
            cruiseRepairZoomTimer = 0f;
            IsRaceFinished = false;
            hasTriggeredEventResults = false;
            hasRecordedRaceResult = false;
            finishBannerTimer = 0f;
            completedLapTimes.Clear();
            currentLapStartTime = 0f;
            blitzElapsedSeconds = 0f;
            WaterSurfaceY = MM2RuntimeCityLoader.CityWaterLevel;

            ArcadeVehicleController existingCar = FindActivePlayerCar();
            if (existingCar != null)
            {
                if (existingCar.VehicleRigidbody != null)
                {
                    existingCar.VehicleRigidbody.useGravity = true;
                }
                if (existingCar.VehicleDamage != null)
                {
                    existingCar.VehicleDamage.ResetDamage();
                }
            }

            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.Location);
            MM2RuntimeCityLoader.GetCitySpawnPose(spec, out spawnPos, out spawnRot);

            string raceType = MM2GameSession.RaceType ?? "Cruise";
            if (string.Equals(raceType, "Cruise", StringComparison.OrdinalIgnoreCase))
            {
                MM2RuntimeCityLoader.RefreshRaceSpecificPathsets(MM2GameSession.Location, string.Empty);
                MM2RuntimeCityLoader.GetCruiseSpawnPose(
                    spec,
                    MM2GameSession.CruiseSpawnFileName,
                    out spawnPos,
                    out spawnRot
                );
                activeCrashLesson = null;
                IsRaceActive = false;
                ActiveRaceType = "Cruise";
                CenterBannerText = string.Empty;
                IsCountdownLockActive = false;
                PlayCruiseIntroSpeech(spec);
                return;
            }

            if (string.Equals(raceType, "CrashCourse", StringComparison.OrdinalIgnoreCase))
            {
                if (spec != null && spec.HasCrashCourse && spec.CrashLessons != null && spec.CrashLessons.Length > 0)
                {
                    int lIdx = Mathf.Clamp(MM2GameSession.CrashLessonIndex, 0, spec.CrashLessons.Length - 1);
                    activeCrashLesson = spec.CrashLessons[lIdx];
                    activeCrashStageIndex = 0;
                    crashLessonPassed = false;
                    crashEndStatusText = string.Empty;

                    if (SetupCrashCourseStage(0, out spawnPos, out spawnRot))
                    {
                        PlayRandomCrashSpeechClip(activeCrashLesson.PreRaceClips);
                        return;
                    }
                }

                Debug.LogWarning($"[MM2RaceManager] No valid Crash Course stage found for {MM2GameSession.Location} #{MM2GameSession.CrashLessonIndex}; falling back to Cruise.");
                activeCrashLesson = null;
                IsRaceActive = false;
                ActiveRaceType = "Cruise";
                CenterBannerText = string.Empty;
                IsCountdownLockActive = false;
                PlayCruiseIntroSpeech(spec);
                return;
            }

            activeCrashLesson = null;
            string raceStemPrefix = string.Equals(raceType, "Blitz", StringComparison.OrdinalIgnoreCase) ? "blitz"
                : string.Equals(raceType, "Circuit", StringComparison.OrdinalIgnoreCase) ? "circuit"
                : "race";
            MM2RuntimeCityLoader.RefreshRaceSpecificPathsets(
                MM2GameSession.Location,
                $"{raceStemPrefix}{MM2GameSession.RaceIndex}"
            );

            List<MM2RaceWaypoint> waypoints = MM2RuntimeCityLoader.LoadRaceWaypoints(
                MM2GameSession.Location,
                raceType,
                MM2GameSession.RaceIndex
            );

            if (waypoints == null || waypoints.Count < 2)
            {
                Debug.LogWarning($"[MM2RaceManager] No valid waypoints found for {MM2GameSession.Location} {raceType} #{MM2GameSession.RaceIndex}; falling back to Cruise.");
                IsRaceActive = false;
                ActiveRaceType = "Cruise";
                CenterBannerText = string.Empty;
                IsCountdownLockActive = false;
                PlayCruiseIntroSpeech(spec);
                return;
            }

            ActiveRaceType = raceType;
            IsRaceActive = true;
            IsRaceFinished = false;

            // Row 0 is the player start pose
            MM2RaceWaypoint startWp = waypoints[0];
            spawnPos = SnapToRoadSurface(startWp.Position, 0.55f);
            spawnRot = startWp.Rotation;

            startHoldPosition = spawnPos;
            startHoldRotation = spawnRot;
            prevCarPosition = spawnPos;
            hasPrevCarPosition = true;

            // Rows 1..N-1 are the race checkpoints
            checkpointsWorldRoot = new GameObject($"MM2_RaceCheckpoints_{raceType}_{MM2GameSession.RaceIndex}");

            int cpCount = waypoints.Count - 1;
            TotalCheckpoints = cpCount;
            CollectedCheckpoints = 0;
            CurrentLap = 1;
            TotalLaps = string.Equals(raceType, "Circuit", StringComparison.OrdinalIgnoreCase)
                ? Mathf.Max(1, MM2GameSession.NumLaps)
                : 1;

            bool isBlitz = string.Equals(raceType, "Blitz", StringComparison.OrdinalIgnoreCase);
            if (isBlitz)
            {
                MM2RaceMetadata meta = MM2RuntimeCityLoader.GetRaceMetadata(
                    MM2GameSession.Location,
                    raceType,
                    MM2GameSession.RaceIndex
                );
                float timeLimit = MM2GameSession.RaceTimeLimit > 1f ? MM2GameSession.RaceTimeLimit : meta.TimeLimit;
                DisplayTimeSeconds = Mathf.Max(5f, timeLimit);
            }
            else
            {
                DisplayTimeSeconds = 0f;
            }

            for (int i = 1; i < waypoints.Count; i++)
            {
                int cpIdx = i - 1;
                MM2RaceWaypoint wp = waypoints[i];
                wp.Position = SnapToRoadSurface(wp.Position, 0f);

                bool startsAsFinish = (cpCount == 1) ||
                    (string.Equals(raceType, "Circuit", StringComparison.OrdinalIgnoreCase) && TotalLaps == 1 && cpIdx == cpCount - 1);

                CheckpointNode node = SpawnCheckpointObject(cpIdx, wp, startsAsFinish);
                checkpoints.Add(node);
            }

            // Start "Ready..." -> "Set..." -> "Go" sequence
            countdownElapsed = 0f;
            countdownStage = 0;
            IsCountdownLockActive = true;
            CenterBannerText = "Ready...";
            finishBannerTimer = 0f;
            lastWarningSecond = -1;
            playerFinishTime = 0f;
            PlayerPosition = 1;
            TotalRacers = 1;
            PlayClip(sfxCountdownLow);
        }

        private bool SetupCrashCourseStage(int stageIdx, out Vector3 spawnPos, out Quaternion spawnRot)
        {
            spawnPos = Vector3.zero;
            spawnRot = Quaternion.identity;

            if (activeCrashLesson == null || activeCrashLesson.Stages == null || stageIdx < 0 || stageIdx >= activeCrashLesson.Stages.Length)
            {
                return false;
            }

            ClearSpawnedCheckpoints();
            activeCrashStageIndex = stageIdx;
            activeCrashStage = activeCrashLesson.Stages[stageIdx];

            MM2RuntimeCityLoader.RefreshRaceSpecificPathsets(
                MM2GameSession.Location,
                $"crash{activeCrashLesson.LessonIndex}",
                activeCrashStage.WaypointFileStem
            );

            List<MM2RaceWaypoint> waypoints = MM2RuntimeCityLoader.LoadCrashStageWaypoints(
                MM2GameSession.Location,
                activeCrashStage.WaypointFileStem
            );
            if (waypoints == null || waypoints.Count < 2)
            {
                return false;
            }

            ActiveRaceType = "CrashCourse";
            IsRaceActive = true;
            IsRaceFinished = false;
            crashHasReachedMinSpeed = false;
            crashBelowMinSpeedTimer = 0f;
            crashTransientBannerTimer = 0f;

            ArcadeVehicleController car = FindActivePlayerCar();
            crashInitialDamage01 = (car != null && car.VehicleDamage != null) ? car.VehicleDamage.Damage01 : 0f;

            MM2RaceWaypoint startWp = waypoints[0];
            spawnPos = SnapToRoadSurface(startWp.Position, 0.55f);
            spawnRot = startWp.Rotation;

            startHoldPosition = spawnPos;
            startHoldRotation = spawnRot;
            prevCarPosition = spawnPos;
            hasPrevCarPosition = true;

            checkpointsWorldRoot = new GameObject($"MM2_CrashCheckpoints_L{activeCrashLesson.LessonIndex}_S{stageIdx}");

            int cpCount = waypoints.Count - 1;
            TotalCheckpoints = cpCount;
            CollectedCheckpoints = 0;
            CurrentLap = 1;
            TotalLaps = 1;

            // EventType 2 ("Follow That Car!") stages store 36 in the TimeLimit column for follow distance/config
            // and do not impose a 36-second countdown timer across a 2.5-3.5 km city tailing route.
            crashHasCountdownTimer = activeCrashStage.TimeLimit > 0.5f &&
                                     !(activeCrashStage.EventType == 2 && activeCrashStage.TimeLimit <= 60f);
            DisplayTimeSeconds = crashHasCountdownTimer ? activeCrashStage.TimeLimit : 0f;

            for (int i = 1; i < waypoints.Count; i++)
            {
                int cpIdx = i - 1;
                MM2RaceWaypoint wp = waypoints[i];
                wp.Position = SnapToRoadSurface(wp.Position, 0f);

                bool isFinish = (cpIdx == cpCount - 1);
                CheckpointNode node = SpawnCheckpointObject(cpIdx, wp, isFinish);
                checkpoints.Add(node);
            }

            UpdateCrashCourseCheckpointVisibility();

            MM2GameSession.NumOpponents = activeCrashStage.NumOpponents;
            MM2GameSession.RaceTimeLimit = activeCrashStage.TimeLimit;
            MM2GameSession.TrafficDensity = activeCrashStage.AmbientDensity > 0
                ? activeCrashStage.AmbientDensity
                : activeCrashLesson.TrafficDensity;

            countdownElapsed = 0f;
            countdownStage = 0;
            IsCountdownLockActive = true;
            CenterBannerText = GetCrashCountdownBanner(0);
            finishBannerTimer = 0f;
            lastWarningSecond = -1;
            playerFinishTime = 0f;
            PlayerPosition = 1;
            TotalRacers = 1;
            PlayClip(sfxCountdownLow);
            return true;
        }

        private string GetCrashCountdownBanner(int stage)
        {
            int ev = activeCrashStage.EventType;
            if (stage == 0)
            {
                switch (ev)
                {
                    case 0: return "Take the jump to hit all checkpoints";
                    case 3: return "Practice evading your pursuers!";
                    case 4: return $"Maintain {Mathf.RoundToInt(activeCrashStage.CornerSpeedMph)} MPH through the checkpoints";
                    case 5: return "You are late, but you can't scrape the paint!";
                    default: return "Ready...";
                }
            }
            if (stage == 1)
            {
                switch (ev)
                {
                    case 0:
                    case 3:
                    case 4:
                        return "Ready...";
                    case 5:
                        return "Your warranty just ran out!";
                    default:
                        return "Set...";
                }
            }
            switch (ev)
            {
                case 0:
                case 4:
                    return "Go!";
                case 2:
                    return "Follow that car!";
                case 3:
                    return "Get going! Lose the tail\nbefore you finish";
                case 5:
                    return "Don't even touch another car!";
                case 8:
                    return "Destroy that car!";
                default:
                    return "Hit all checkpoints!";
            }
        }

        private void UpdateCrashCourseCheckpointVisibility()
        {
            if (!string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase)) return;

            int ev = activeCrashStage.EventType;
            for (int i = 0; i < checkpoints.Count; i++)
            {
                CheckpointNode node = checkpoints[i];
                if (node == null || node.RootObject == null) continue;

                if (node.IsCollected || ev == 8)
                {
                    node.RootObject.SetActive(false);
                    continue;
                }

                if (activeCrashStage.ChkFlags != 0)
                {
                    // Only show the active target checkpoint when ChkFlags == 1 (e.g. Forward 180 back-and-forth or Landmark Map)
                    node.RootObject.SetActive(i == CollectedCheckpoints);
                }
                else
                {
                    // Hide any future checkpoint that overlaps spatially with an earlier uncollected checkpoint
                    bool overlapsEarlier = false;
                    for (int j = CollectedCheckpoints; j < i; j++)
                    {
                        if (!checkpoints[j].IsCollected &&
                            (checkpoints[j].Waypoint.Position - node.Waypoint.Position).sqrMagnitude < 64f)
                        {
                            overlapsEarlier = true;
                            break;
                        }
                    }
                    node.RootObject.SetActive(!overlapsEarlier);
                }
            }
        }

        private void PlayRandomCrashSpeechClip(string[] clips)
        {
            if (clips == null || clips.Length == 0) return;
            string clipName = clips[UnityEngine.Random.Range(0, clips.Length)];
            PlayCrashSpeechClip(clipName);
        }

        private void PlayCrashSpeechClip(string clipName)
        {
            if (string.IsNullOrEmpty(clipName) || speechAudioSource == null) return;
            AudioClip clip = MM2RuntimeDecoders.LoadAudioClip(clipName);
            if (clip == null) return;
            speechAudioSource.Stop();
            speechAudioSource.clip = clip;
            speechAudioSource.Play();
        }

        /// <summary>
        /// Plays a random pre-session announcer voice clip when starting in Cruise mode:
        /// - Reads MM2 city announcer configs (`aud/spchdata/&lt;city&gt;.csv`) and collects clips from
        ///   `cruise.csv`, weather (`weaclr_prerace.csv`, `weacldy_prerace.csv`, `weafog_prerace.csv`, `wearain_prerace.csv`),
        ///   time of day (`timemorn_prerace.csv`, `timenoon_prerace.csv`, `timeeve_prerace.csv`, `timenight_prerace.csv`),
        ///   and vehicle (`&lt;vehicle&gt;_prerace.csv`) — including `AS1WeaClr02` ("The sun is out, the streets are dry,
        ///   and there's an eerie calm throughout the city... until now.").
        /// - Supports MM1 Chicago announcer clips (`ACRUISEMODE*`, `AWEACLEAR*`, `AWEACLOUDY*`, `AWEARAIN*`, `AWEASNOW*`,
        ///   `ATIMEMORN*`, `ATIMENOON*`, `ATIMESUN*`, `ATIMENIGHT*`) when `audio.ar` is mounted.
        /// - Gracefully degrades on custom mod cities or partial archive installs.
        /// </summary>
        private void PlayCruiseIntroSpeech(CitySpec spec)
        {
            if (speechAudioSource == null) return;

            string mapName = (spec != null && !string.IsNullOrEmpty(spec.MapName))
                ? spec.MapName.Trim().ToLowerInvariant()
                : (MM2GameSession.SelectedCityCode ?? "sf").Trim().ToLowerInvariant();
            if (mapName == "sanfrancisco") mapName = "sf";

            int weatherIdx = MM2EnvironmentManager.ParseWeatherIndex(MM2GameSession.Weather);
            int todIdx = MM2EnvironmentManager.ParseTimeOfDayIndex(MM2GameSession.TimeOfDay);

            string weatherCsv = weatherIdx switch
            {
                1 => "weacldy_prerace.csv",
                2 => "weafog_prerace.csv",
                3 => "wearain_prerace.csv",
                _ => "weaclr_prerace.csv"
            };

            string todCsv = todIdx switch
            {
                0 => "timemorn_prerace.csv",
                2 => "timeeve_prerace.csv",
                3 => "timenight_prerace.csv",
                _ => "timenoon_prerace.csv"
            };

            VehicleSpec vehSpec = VehicleCatalog.GetSpec(MM2GameSession.VehicleName);
            string vehSpeechBase = MapVehicleBaseToPreraceCsvBase(vehSpec != null ? vehSpec.BaseName : null);
            string vehCsv = !string.IsNullOrEmpty(vehSpeechBase) ? $"{vehSpeechBase}_prerace.csv" : null;

            var candidates = new List<string>(64);

            // 1. Check MM2 aud/spchdata/<city>.csv for the active city
            if (TryGetCityAnnouncerConfig(mapName, out string annPrefix, out int numAnnouncers))
            {
                CollectMm2AnnouncerCruiseClips(
                    annPrefix,
                    numAnnouncers,
                    includeCityCruiseCsv: true,
                    weatherCsv,
                    todCsv,
                    vehCsv,
                    candidates
                );
            }
            else if ((!MM2ArchiveSystem.FileExists($"city/{mapName}.psdl") && MM2ArchiveSystem.FileExists($"city/{mapName}.ptl")) || mapName == "chicago")
            {
                // 2. MM1 city (e.g. Chicago in audio.ar)
                CollectMm1CruiseClips(includeCityCruiseClips: true, weatherIdx, todIdx, candidates);
            }

            // 3. Fallback for custom community mod cities (or if the city's own announcer clips aren't installed):
            //    use city-neutral weather, time-of-day, and vehicle pre-race clips from installed MM2/MM1 archives.
            if (candidates.Count == 0)
            {
                if (TryGetCityAnnouncerConfig("sf", out string sfPrefix, out int sfCount))
                {
                    CollectMm2AnnouncerCruiseClips(sfPrefix, sfCount, includeCityCruiseCsv: false, weatherCsv, todCsv, vehCsv, candidates);
                }
                if (TryGetCityAnnouncerConfig("london", out string lonPrefix, out int lonCount))
                {
                    CollectMm2AnnouncerCruiseClips(lonPrefix, lonCount, includeCityCruiseCsv: false, weatherCsv, todCsv, vehCsv, candidates);
                }
                if (candidates.Count == 0)
                {
                    CollectMm1CruiseClips(includeCityCruiseClips: false, weatherIdx, todIdx, candidates);
                }
            }

            if (candidates.Count == 0) return;

            string chosen = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            if (candidates.Count > 1 && string.Equals(chosen, lastPlayedCruiseSpeechStem, StringComparison.OrdinalIgnoreCase))
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    string alt = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                    if (!string.Equals(alt, lastPlayedCruiseSpeechStem, StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = alt;
                        break;
                    }
                }
            }

            lastPlayedCruiseSpeechStem = chosen;
            PlayCrashSpeechClip(chosen);
        }

        private static bool TryGetCityAnnouncerConfig(string cityCode, out string prefix, out int numAnnouncers)
        {
            prefix = string.Empty;
            numAnnouncers = 0;
            if (string.IsNullOrEmpty(cityCode)) return false;

            string csv = MM2ArchiveSystem.ReadFileText($"aud/spchdata/{cityCode}.csv");
            if (string.IsNullOrEmpty(csv)) return false;

            string[] lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("Num announcers", StringComparison.OrdinalIgnoreCase) && i + 1 < lines.Length)
                {
                    int.TryParse(
                        lines[i + 1].Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out numAnnouncers
                    );
                }
                else if (line.StartsWith("prefix", StringComparison.OrdinalIgnoreCase) && i + 1 < lines.Length)
                {
                    prefix = lines[i + 1].Trim();
                }
            }

            return !string.IsNullOrEmpty(prefix) && numAnnouncers > 0;
        }

        private static void CollectMm2AnnouncerCruiseClips(
            string prefix,
            int numAnnouncers,
            bool includeCityCruiseCsv,
            string weatherCsv,
            string todCsv,
            string vehCsv,
            List<string> outCandidates
        )
        {
            for (int a = 1; a <= numAnnouncers; a++)
            {
                string annTag = $"{prefix}{a}";
                string annDir = annTag.ToLowerInvariant();

                if (includeCityCruiseCsv)
                {
                    AppendSpchDataCsvClips(annTag, $"aud/spchdata/{annDir}/cruise.csv", outCandidates);
                }
                if (!string.IsNullOrEmpty(weatherCsv))
                {
                    AppendSpchDataCsvClips(annTag, $"aud/spchdata/{annDir}/{weatherCsv}", outCandidates);
                }
                if (!string.IsNullOrEmpty(todCsv))
                {
                    AppendSpchDataCsvClips(annTag, $"aud/spchdata/{annDir}/{todCsv}", outCandidates);
                }
                if (!string.IsNullOrEmpty(vehCsv))
                {
                    AppendSpchDataCsvClips(annTag, $"aud/spchdata/{annDir}/{vehCsv}", outCandidates);
                }
            }
        }

        private static void AppendSpchDataCsvClips(string annTag, string virtualCsvPath, List<string> outCandidates)
        {
            string csv = MM2ArchiveSystem.ReadFileText(virtualCsvPath);
            if (string.IsNullOrEmpty(csv)) return;

            string[] lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] cols = lines[i].Split(',');
                if (cols.Length < 2) continue;

                string key = cols[0].Trim();
                if (string.IsNullOrEmpty(key) ||
                    key.StartsWith("Name prefix", StringComparison.OrdinalIgnoreCase) ||
                    key.EndsWith("header", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!int.TryParse(
                        cols[1].Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int endSuffix) ||
                    endSuffix <= 0)
                {
                    continue;
                }

                int suffixAdd = 0;
                if (cols.Length >= 3)
                {
                    int.TryParse(
                        cols[2].Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out suffixAdd
                    );
                }

                for (int n = 1; n <= endSuffix; n++)
                {
                    string stem = $"{annTag}{key}{n + suffixAdd:D2}";
                    if (MM2ArchiveSystem.FindWavVirtualPath(stem) != null)
                    {
                        outCandidates.Add(stem);
                    }
                    else if (key.Equals("WEAFOG", StringComparison.OrdinalIgnoreCase))
                    {
                        // Handle AL5Weasfog01/02 filename typo in retail mm2audex.ar
                        string typoStem = $"{annTag}Weasfog{n + suffixAdd:D2}";
                        if (MM2ArchiveSystem.FindWavVirtualPath(typoStem) != null)
                        {
                            outCandidates.Add(typoStem);
                        }
                    }
                }
            }
        }

        private static void CollectMm1CruiseClips(
            bool includeCityCruiseClips,
            int weatherIdx,
            int todIdx,
            List<string> outCandidates
        )
        {
            if (includeCityCruiseClips)
            {
                AppendExistingNumberedStems("ACRUISEMODE", 1, 5, outCandidates);
            }

            switch (weatherIdx)
            {
                case 1:
                    AppendExistingNumberedStems("AWEACLOUDY", 1, 3, outCandidates);
                    AppendExistingNumberedStems("AWEACLOUD", 4, 5, outCandidates);
                    break;
                case 2:
                    AppendExistingNumberedStems("AWEARAIN", 1, 5, outCandidates);
                    break;
                case 3:
                    AppendExistingNumberedStems("AWEARAIN", 1, 5, outCandidates);
                    AppendExistingNumberedStems("AWEASNOW", 1, 5, outCandidates);
                    break;
                default:
                    AppendExistingNumberedStems("AWEACLEAR", 1, 5, outCandidates);
                    break;
            }

            switch (todIdx)
            {
                case 0:
                    AppendExistingNumberedStems("ATIMEMORN", 1, 4, outCandidates);
                    break;
                case 2:
                    AppendExistingNumberedStems("ATIMESUN", 1, 2, outCandidates);
                    break;
                case 3:
                    AppendExistingNumberedStems("ATIMENIGHT", 1, 3, outCandidates);
                    break;
                default:
                    AppendExistingNumberedStems("ATIMENOON", 1, 3, outCandidates);
                    break;
            }
        }

        private static void AppendExistingNumberedStems(string prefix, int startIdx, int endIdx, List<string> outCandidates)
        {
            for (int n = startIdx; n <= endIdx; n++)
            {
                string stem = $"{prefix}{n:D2}";
                if (MM2ArchiveSystem.FindWavVirtualPath(stem) != null)
                {
                    outCandidates.Add(stem);
                }
            }
        }

        private static string MapVehicleBaseToPreraceCsvBase(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return null;
            string lower = baseName.Trim().ToLowerInvariant();
            return lower switch
            {
                "vpdblbus" => "vpddbus",
                "vphumvee" => "vp4x4",
                "vpfire" => "vpsemi",
                "vpvwcup" => "vpbug",
                _ => lower
            };
        }

        /// <summary>
        /// Loads the city .bai / .aimap / .opp AI data for the active session and initializes
        /// MM2TrafficManager, MM2PoliceManager, and MM2OpponentManager around the player vehicle.
        /// </summary>
        public void InitializeAiSystemsForSession(Transform playerCarTransform)
        {
            if (playerCarTransform == null)
            {
                ArcadeVehicleController car = FindActivePlayerCar();
                if (car != null) playerCarTransform = car.transform;
            }
            if (playerCarTransform == null) return;

            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.Location);
            string mapName = spec != null && !string.IsNullOrEmpty(spec.MapName) ? spec.MapName : "sf";
            string raceDir = spec != null && !string.IsNullOrEmpty(spec.RaceDir) ? spec.RaceDir : mapName;

            string raceType = MM2GameSession.RaceType ?? "Cruise";
            string raceStem = "roam";
            if (string.Equals(raceType, "CrashCourse", StringComparison.OrdinalIgnoreCase))
            {
                int lIdx = activeCrashLesson != null ? activeCrashLesson.LessonIndex : Mathf.Max(0, MM2GameSession.CrashLessonIndex);
                raceStem = $"crash{lIdx}";
            }
            else if (string.Equals(raceType, "Blitz", StringComparison.OrdinalIgnoreCase))
            {
                raceStem = $"blitz{Mathf.Max(0, MM2GameSession.RaceIndex)}";
            }
            else if (string.Equals(raceType, "Checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                raceStem = $"race{Mathf.Max(0, MM2GameSession.RaceIndex)}";
            }
            else if (string.Equals(raceType, "Circuit", StringComparison.OrdinalIgnoreCase))
            {
                raceStem = $"circuit{Mathf.Max(0, MM2GameSession.RaceIndex)}";
            }

            currentAiMap = MM2AiMapData.Load(mapName, raceDir, raceStem);

            if (string.Equals(raceType, "CrashCourse", StringComparison.OrdinalIgnoreCase) && activeCrashLesson != null)
            {
                // Enable police spawns only on stages that use pursuers (EventType == 3 or no target opponent)
                bool stageUsesPolice =
                    activeCrashStage.EventType == 3 ||
                    (currentAiMap != null && currentAiMap.PoliceSpawns.Count > 0 && activeCrashStage.NumOpponents == 0);
                MM2GameSession.CopDensity = stageUsesPolice ? Mathf.Max(100, activeCrashLesson.CopDensity) : activeCrashLesson.CopDensity;
                MM2GameSession.TrafficDensity = activeCrashStage.AmbientDensity > 0
                    ? activeCrashStage.AmbientDensity
                    : activeCrashLesson.TrafficDensity;
                MM2GameSession.NumOpponents = activeCrashStage.NumOpponents;
            }

            if (trafficManager == null)
            {
                trafficManager = GetComponent<MM2TrafficManager>();
                if (trafficManager == null) trafficManager = gameObject.AddComponent<MM2TrafficManager>();
            }
            if (specialTrafficManager == null)
            {
                specialTrafficManager = GetComponent<MM2SpecialTrafficManager>();
                if (specialTrafficManager == null) specialTrafficManager = gameObject.AddComponent<MM2SpecialTrafficManager>();
            }
            if (pedestrianManager == null)
            {
                pedestrianManager = GetComponent<MM2PedestrianManager>();
                if (pedestrianManager == null) pedestrianManager = gameObject.AddComponent<MM2PedestrianManager>();
            }
            if (policeManager == null)
            {
                policeManager = GetComponent<MM2PoliceManager>();
                if (policeManager == null) policeManager = gameObject.AddComponent<MM2PoliceManager>();
            }
            if (opponentManager == null)
            {
                opponentManager = GetComponent<MM2OpponentManager>();
                if (opponentManager == null) opponentManager = gameObject.AddComponent<MM2OpponentManager>();
            }

            // Collect checkpoint positions, rotations & radii for opponents
            var cpPositions = new List<Vector3>(checkpoints.Count);
            var cpRotations = new List<Quaternion>(checkpoints.Count);
            var cpRadii = new List<float>(checkpoints.Count);
            for (int i = 0; i < checkpoints.Count; i++)
            {
                cpPositions.Add(checkpoints[i].Waypoint.Position);
                cpRotations.Add(checkpoints[i].Waypoint.Rotation);
                cpRadii.Add(checkpoints[i].Waypoint.Radius);
            }

            opponentManager.Initialize(currentAiMap, playerCarTransform, ActiveRaceType, cpPositions, cpRadii, TotalLaps, cpRotations);
            trafficManager.Initialize(currentAiMap, playerCarTransform);
            specialTrafficManager.Initialize(currentAiMap, playerCarTransform, mapName, raceDir);
            pedestrianManager.Initialize(currentAiMap, playerCarTransform);
            policeManager.Initialize(currentAiMap, playerCarTransform);

            TotalRacers = 1 + opponentManager.ActiveOpponents.Count;
            PlayerPosition = opponentManager.ComputePlayerRacePosition(
                CurrentLap,
                CollectedCheckpoints,
                playerCarTransform.position,
                IsRaceFinished,
                playerFinishTime,
                out _
            );
        }

        private CheckpointNode SpawnCheckpointObject(int index, MM2RaceWaypoint wp, bool isFinishGate)
        {
            GameObject root = new GameObject($"Checkpoint_{index:D2}");
            if (checkpointsWorldRoot != null)
            {
                root.transform.SetParent(checkpointsWorldRoot.transform, false);
            }
            root.transform.SetPositionAndRotation(wp.Position, wp.Rotation);

            // 1. 3D Checkpoint / Finish Arch Model (from geometry/pt_check.pkg or geometry/pt_finish.pkg)
            // In pt_check.pkg, X is [-1, 1] (inner opening [-0.8, 0.8]) and Y is [-0.5, 0.5].
            // Scaling X by wp.Radius spans [-Radius, +Radius] across the road;
            // scaling Y by 7.5m and placing at local Y = 3.75m puts the base at Y = 0 on the street.
            const float archHeight = 7.5f;
            GameObject archGo = new GameObject("Arch_Model");
            archGo.transform.SetParent(root.transform, false);
            archGo.transform.localPosition = new Vector3(0f, archHeight * 0.5f, 0f);
            archGo.transform.localRotation = Quaternion.identity;
            archGo.transform.localScale = new Vector3(wp.Radius, archHeight, 8.0f);

            MeshFilter mf = archGo.AddComponent<MeshFilter>();
            mf.sharedMesh = isFinishGate ? meshFinishArch : meshCheckpointArch;

            MeshRenderer mr = archGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = matCheckpointStand;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // 2. Floating Cyan Diamond Beacon above the checkpoint (matching Screenshot 2)
            GameObject beaconGo = new GameObject("Cyan_Beacon");
            beaconGo.transform.SetParent(root.transform, false);
            beaconGo.transform.localPosition = new Vector3(0f, archHeight + 1.85f, 0f);
            beaconGo.transform.localRotation = Quaternion.identity;

            MeshFilter bMf = beaconGo.AddComponent<MeshFilter>();
            bMf.sharedMesh = meshCyanBeacon;

            MeshRenderer bMr = beaconGo.AddComponent<MeshRenderer>();
            bMr.sharedMaterial = matCyanBeacon;
            bMr.shadowCastingMode = ShadowCastingMode.Off;
            bMr.receiveShadows = false;

            return new CheckpointNode
            {
                Index = index,
                Waypoint = wp,
                RootObject = root,
                ArchMeshFilter = mf,
                BeaconTransform = beaconGo.transform,
                IsCollected = false,
                IsFinishGate = isFinishGate
            };
        }

        private void SetNodeFinishVisual(CheckpointNode node, bool isFinish)
        {
            if (node == null || node.IsFinishGate == isFinish) return;
            node.IsFinishGate = isFinish;
            if (node.ArchMeshFilter != null)
            {
                node.ArchMeshFilter.sharedMesh = isFinish ? meshFinishArch : meshCheckpointArch;
            }
        }

        private static Vector3 SnapToRoadSurface(Vector3 pos, float heightOffset)
        {
            // Start only 3.0m above the authored waypoint elevation (NOT 12m above!) and pick the
            // surface hit closest in Y to pos.y so a checkpoint on a road underneath an overpass
            // never hits the overpass deck overhead and teleports up onto the overpass.
            Vector3 origin = pos + Vector3.up * 3.0f;
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 18f, ~0, QueryTriggerInteraction.Ignore);
            float bestVerticalDiff = float.MaxValue;
            float bestY = pos.y;
            bool found = false;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || col.attachedRigidbody != null) continue;
                if (hits[i].normal.y < 0.45f) continue;

                float hitY = hits[i].point.y;
                float diff = Mathf.Abs(hitY - pos.y);
                // Reject surfaces more than 4.2m away vertically (e.g., an underpass below an overpass)
                if (diff <= 4.2f && diff < bestVerticalDiff)
                {
                    bestVerticalDiff = diff;
                    bestY = hitY;
                    found = true;
                }
            }

            if (found)
            {
                pos.y = bestY + heightOffset;
            }
            else
            {
                pos.y += heightOffset;
            }
            return pos;
        }

        public void ClearSpawnedCheckpoints()
        {
            checkpoints.Clear();
            if (checkpointsWorldRoot != null)
            {
                Destroy(checkpointsWorldRoot);
                checkpointsWorldRoot = null;
            }
        }

        private void Update()
        {
            // Pause while menu is open
            if (FindFirstObjectByType<MM2MenuController>() != null) return;

            float dt = Time.deltaTime;
            ArcadeVehicleController car = FindActivePlayerCar();

            // Check water immersion ("Sleep with the fishes!") in both Cruise and Race modes,
            // as well as post-event water entry (e.g. landing in water after "Time's Up!") so the car never falls out the bottom of the map
            if (!isImmersedInWaterPhysics && !IsCountdownLockActive && car != null)
            {
                Collider carCol = car.GetComponent<Collider>();
                if (MM2RuntimeCityLoader.IsPointInWater(car.transform.position, carCol, out float detectedWaterY))
                {
                    if (!IsSinkingInWater && !IsPlayerTotaled && !IsRaceFinished)
                    {
                        TriggerSleepWithTheFishes(car, detectedWaterY);
                    }
                    else
                    {
                        ActivateWaterSinkingPhysics(car, detectedWaterY);
                    }
                }
            }

            // Handle Cruise mode post-totaled camera zoom-back-in after full repair
            if (IsCruiseRepairZoomingIn)
            {
                const float zoomInDuration = 1.15f;
                cruiseRepairZoomTimer += dt;
                CruiseRepairZoomInProgress01 = Mathf.Clamp01(cruiseRepairZoomTimer / zoomInDuration);
                if (cruiseRepairZoomTimer >= zoomInDuration)
                {
                    IsCruiseRepairZoomingIn = false;
                    CruiseRepairZoomInProgress01 = 0f;
                    cruiseRepairZoomTimer = 0f;
                }
                return;
            }

            // Handle post-event timer & transition to Event Results screen (or Cruise mode auto-repair at full zoom-out)
            if (IsSinkingInWater || IsPlayerTotaled || IsRaceFinished)
            {
                finishBannerTimer += dt;

                bool isCruiseTotaled = IsPlayerTotaled &&
                                       !IsSinkingInWater &&
                                       !IsRaceActive &&
                                       string.Equals(ActiveRaceType, "Cruise", StringComparison.OrdinalIgnoreCase);

                if (isCruiseTotaled)
                {
                    // In Cruise mode: wait until the camera has fully zoomed out (~2.8s), then repair the car
                    // back to 0 damage, zoom the camera back to normal, and let the player keep driving!
                    if (finishBannerTimer >= 2.8f)
                    {
                        if (car != null)
                        {
                            if (car.VehicleDamage != null)
                            {
                                car.VehicleDamage.ResetDamage();
                            }
                            if (car.VehicleRigidbody != null)
                            {
                                Rigidbody rb = car.VehicleRigidbody;
                                rb.linearVelocity = Vector3.zero;
                                rb.angularVelocity = Vector3.zero;
                                Quaternion uprightRot = Quaternion.Euler(0f, car.transform.eulerAngles.y, 0f);
                                rb.MoveRotation(uprightRot);
                            }
                        }

                        IsPlayerTotaled = false;
                        CenterBannerText = string.Empty;
                        finishBannerTimer = 0f;
                        IsCruiseRepairZoomingIn = true;
                        cruiseRepairZoomTimer = 0f;
                        CruiseRepairZoomInProgress01 = 0f;
                    }
                    return;
                }

                if (!hasTriggeredEventResults && finishBannerTimer >= 3.8f)
                {
                    hasTriggeredEventResults = true;
                    TransitionToEventResultsScreen();
                }
                return;
            }

            if (!IsRaceActive) return;

            // Animate floating cyan beacons above active checkpoints
            float spinAngle = Time.time * 140f;
            float bobOffset = Mathf.Sin(Time.time * 3.5f) * 0.35f;
            for (int i = 0; i < checkpoints.Count; i++)
            {
                CheckpointNode node = checkpoints[i];
                if (node.BeaconTransform != null && node.RootObject != null && node.RootObject.activeSelf)
                {
                    node.BeaconTransform.localPosition = new Vector3(0f, 9.35f + bobOffset, 0f);
                    node.BeaconTransform.localRotation = Quaternion.Euler(0f, spinAngle, 0f);
                }
            }

            // Handle "Ready..." -> "Set..." -> "Go" sequence
            bool isCrashCourse = string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase);
            if (countdownStage < 3)
            {
                countdownElapsed += dt;

                if (countdownStage == 0 && countdownElapsed >= 1.15f)
                {
                    countdownStage = 1;
                    CenterBannerText = isCrashCourse ? GetCrashCountdownBanner(1) : "Set...";
                    PlayClip(sfxCountdownLow);
                }
                else if (countdownStage == 1 && countdownElapsed >= 2.30f)
                {
                    countdownStage = 2;
                    IsCountdownLockActive = false;
                    CenterBannerText = isCrashCourse ? GetCrashCountdownBanner(2) : "Go";
                    PlayClip(sfxCountdownHigh);
                }
                else if (countdownStage == 2 && countdownElapsed >= 3.55f)
                {
                    countdownStage = 3;
                    if (!IsRaceFinished && !IsSinkingInWater)
                    {
                        CenterBannerText = string.Empty;
                    }
                }

                // Hold vehicle at starting line during "Ready..." and "Set..."
                if (IsCountdownLockActive && car != null)
                {
                    Rigidbody rb = car.VehicleRigidbody;
                    if (rb != null)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.position = startHoldPosition;
                        rb.rotation = startHoldRotation;
                    }
                    car.transform.SetPositionAndRotation(startHoldPosition, startHoldRotation);
                    prevCarPosition = startHoldPosition;
                    return;
                }
            }

            if (crashTransientBannerTimer > 0f)
            {
                crashTransientBannerTimer -= dt;
                if (crashTransientBannerTimer <= 0f && countdownStage >= 3 && !IsRaceFinished && !IsSinkingInWater && !IsPlayerTotaled)
                {
                    CenterBannerText = string.Empty;
                }
            }

            // Update Race Timer (starts ticking once "Go" begins at countdownStage >= 2)
            if (countdownStage >= 2)
            {
                bool isBlitz = string.Equals(ActiveRaceType, "Blitz", StringComparison.OrdinalIgnoreCase);
                bool countsDown = isBlitz || (isCrashCourse && crashHasCountdownTimer);
                if (countsDown)
                {
                    blitzElapsedSeconds += dt;
                    DisplayTimeSeconds = Mathf.Max(0f, DisplayTimeSeconds - dt);

                    // Play low-time beep during final 5 seconds
                    int wholeSec = Mathf.CeilToInt(DisplayTimeSeconds);
                    if (wholeSec >= 1 && wholeSec <= 5 && wholeSec != lastWarningSecond)
                    {
                        lastWarningSecond = wholeSec;
                        PlayClip(sfxTimerWarning);
                    }

                    if (DisplayTimeSeconds <= 0f)
                    {
                        DisplayTimeSeconds = 0f;
                        if (isCrashCourse)
                        {
                            TriggerCrashCourseFail("Time's Up!");
                        }
                        else
                        {
                            IsRaceFinished = true;
                            CenterBannerText = "Time's Up!";
                            finishBannerTimer = 0f;
                            PlayClip(sfxRaceLose);
                        }
                        return;
                    }
                }
                else
                {
                    blitzElapsedSeconds += dt;
                    DisplayTimeSeconds += dt;
                }
            }

            if (car != null)
            {
                Vector3 carPos = car.transform.position;
                if (!hasPrevCarPosition)
                {
                    prevCarPosition = carPos;
                    hasPrevCarPosition = true;
                }

                if (isCrashCourse && countdownStage >= 2)
                {
                    StepCrashCourseGameplay(car, prevCarPosition, carPos);
                }
                else
                {
                    CheckPlayerCheckpointPasses(prevCarPosition, carPos);
                }
                prevCarPosition = carPos;

                if (!isCrashCourse && opponentManager != null && opponentManager.ActiveOpponents.Count > 0)
                {
                    PlayerPosition = opponentManager.ComputePlayerRacePosition(
                        CurrentLap,
                        CollectedCheckpoints,
                        carPos,
                        IsRaceFinished,
                        playerFinishTime,
                        out int totalRacers
                    );
                    TotalRacers = totalRacers;
                }
                else
                {
                    PlayerPosition = 1;
                    TotalRacers = 1;
                }
            }
        }

        private void StepCrashCourseGameplay(ArcadeVehicleController car, Vector3 prevPos, Vector3 currPos)
        {
            if (IsRaceFinished || IsSinkingInWater || IsPlayerTotaled) return;

            int ev = activeCrashStage.EventType;
            bool isProProfile = MM2PlayerManager.IsActiveProfilePro();
            float damageTolerance01 = isProProfile ? (0.0004f * 0.25f) : 0.0004f;
            float speedDeltaToleranceMph = isProProfile ? 1.0f : 3.0f;
            float speedTimeToleranceSec = isProProfile ? 0.5f : 1.5f;

            // Event 5: Don't Scrape the Paint (Zero Collision)
            if (ev == 5 && car.VehicleDamage != null)
            {
                if (car.VehicleDamage.Damage01 > crashInitialDamage01 + damageTolerance01)
                {
                    TriggerCrashCourseFail("You scraped the paint!");
                    return;
                }
            }

            // Event 4: Minimum Speed Cornering
            if (ev == 4)
            {
                float minSpeed = activeCrashStage.CornerSpeedMph;
                float curSpeed = Mathf.Max(0f, car.CurrentSpeedMph);
                if (!crashHasReachedMinSpeed)
                {
                    if (curSpeed >= minSpeed)
                    {
                        crashHasReachedMinSpeed = true;
                        crashBelowMinSpeedTimer = 0f;
                        ShowCrashTransientBanner("You have reached the minimum speed!", 2.0f);
                    }
                }
                else if (curSpeed < minSpeed - speedDeltaToleranceMph)
                {
                    crashBelowMinSpeedTimer += Time.deltaTime;
                    if (crashBelowMinSpeedTimer >= speedTimeToleranceSec)
                    {
                        TriggerCrashCourseFail("You have not maintained\nthe minimum speed!", "Min speed not met");
                        return;
                    }
                }
                else
                {
                    crashBelowMinSpeedTimer = 0f;
                }
            }

            // Event 2: Follow That Car!
            if (ev == 2)
            {
                if (opponentManager != null && opponentManager.ActiveOpponents.Count > 0)
                {
                    MM2OpponentAgent targetOpp = opponentManager.ActiveOpponents[0];
                    if (targetOpp != null)
                    {
                        float dist = Vector3.Distance(currPos, targetOpp.transform.position);
                        bool hitByPlayer = targetOpp.Damage != null && targetOpp.Damage.WasHitByPlayer && dist < 14.0f;

                        if (blitzElapsedSeconds > 4.0f)
                        {
                            if (dist < 8.0f || hitByPlayer)
                            {
                                TriggerCrashCourseFail("You were spotted!");
                                return;
                            }
                            if (dist > activeCrashStage.FollowDistFar * 2.15f)
                            {
                                TriggerCrashCourseFail("Car escaped!");
                                return;
                            }
                            if (dist < activeCrashStage.FollowDistNear)
                            {
                                ShowCrashTransientBanner("Don't get too close!", 0.5f);
                            }
                            else if (dist > activeCrashStage.FollowDistFar * 1.3f)
                            {
                                ShowCrashTransientBanner("Get Closer!", 0.5f);
                            }
                        }

                        if (targetOpp.HasFinished ||
                            (targetOpp.TotalRouteLength > 10f && targetOpp.DistanceAlongLap >= targetOpp.TotalRouteLength - 6f))
                        {
                            OnCrashStageCompleted("You Won!");
                            return;
                        }
                    }
                }
                return;
            }

            // Event 8: Smash / Stop Target Car ("Bonnie & Collide")
            if (ev == 8)
            {
                if (opponentManager != null && opponentManager.ActiveOpponents.Count > 0)
                {
                    MM2OpponentAgent targetOpp = opponentManager.ActiveOpponents[0];
                    if (targetOpp != null)
                    {
                        if (targetOpp.Damage != null && (targetOpp.Damage.IsTotaled || targetOpp.Damage.Damage01 >= 0.98f))
                        {
                            OnCrashStageCompleted("You did it!");
                            return;
                        }
                        if (targetOpp.HasFinished)
                        {
                            TriggerCrashCourseFail("Car reached destination");
                            return;
                        }
                    }
                }
                return;
            }

            // Sequential Checkpoint Progression (Events 0, 3, 4, 5, 7, 9)
            if (CollectedCheckpoints >= 0 && CollectedCheckpoints < checkpoints.Count)
            {
                CheckpointNode targetNode = checkpoints[CollectedCheckpoints];
                if (!targetNode.IsCollected && DidCarPassThroughNode(targetNode, prevPos, currPos))
                {
                    if (ev == 4 && !crashHasReachedMinSpeed)
                    {
                        if (Mathf.Max(0f, car.CurrentSpeedMph) >= activeCrashStage.CornerSpeedMph - speedDeltaToleranceMph)
                        {
                            crashHasReachedMinSpeed = true;
                            crashBelowMinSpeedTimer = 0f;
                        }
                        else
                        {
                            TriggerCrashCourseFail("You need to get up to speed", "Min speed not met");
                            return;
                        }
                    }

                    bool isFinalCheckpoint = (CollectedCheckpoints == TotalCheckpoints - 1);
                    if (ev == 3 && isFinalCheckpoint && policeManager != null)
                    {
                        float checkDist = Mathf.Min(activeCrashStage.CopChaseDistance, 65f);
                        if (policeManager.AreAnyCopsPursuingWithinDistance(checkDist))
                        {
                            ShowCrashTransientBanner("Lose your pursuers before you finish!", 1.8f);
                            return;
                        }
                    }

                    OnCrashCourseCheckpointPassed(targetNode);
                }
            }
        }

        private void OnCrashCourseCheckpointPassed(CheckpointNode passedNode)
        {
            passedNode.IsCollected = true;
            if (passedNode.RootObject != null)
            {
                passedNode.RootObject.SetActive(false);
            }

            CollectedCheckpoints++;

            if (CollectedCheckpoints >= TotalCheckpoints)
            {
                string winMsg = activeCrashStage.EventType == 3
                    ? "You survived the gauntlet!"
                    : "You Won!";
                OnCrashStageCompleted(winMsg);
                return;
            }

            UpdateCrashCourseCheckpointVisibility();

            int remaining = TotalCheckpoints - CollectedCheckpoints;
            PlayClip(remaining == 1 && sfxLastWaypoint != null ? sfxLastWaypoint : sfxWaypoint);

            // Event 9 ("4. The Knowledge" in London): play next landmark voice callout from cc_cpoint_indexinfo.csv
            if (activeCrashStage.EventType == 9)
            {
                CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.Location);
                if (spec != null &&
                    spec.CrashCheckpointSpeechIndices != null &&
                    CollectedCheckpoints < spec.CrashCheckpointSpeechIndices.Length &&
                    !string.IsNullOrEmpty(spec.CrashCheckpointSpeechPrefix))
                {
                    int clipIdx = spec.CrashCheckpointSpeechIndices[CollectedCheckpoints];
                    if (clipIdx > 0)
                    {
                        PlayCrashSpeechClip($"{spec.CrashCheckpointSpeechPrefix}{clipIdx:D2}");
                    }
                }
            }
        }

        private void OnCrashStageCompleted(string winBanner)
        {
            // Check if this lesson has another stage (e.g. 2-stage Midterm 2 or Final Exam)
            if (activeCrashLesson != null &&
                activeCrashLesson.Stages != null &&
                activeCrashStageIndex + 1 < activeCrashLesson.Stages.Length)
            {
                int nextStage = activeCrashStageIndex + 1;
                if (SetupCrashCourseStage(nextStage, out Vector3 nextSpawnPos, out Quaternion nextSpawnRot))
                {
                    ArcadeVehicleController car = FindActivePlayerCar();
                    if (car != null)
                    {
                        if (car.VehicleDamage != null) car.VehicleDamage.ResetDamage();
                        if (car.VehicleRigidbody != null)
                        {
                            car.VehicleRigidbody.linearVelocity = Vector3.zero;
                            car.VehicleRigidbody.angularVelocity = Vector3.zero;
                            car.VehicleRigidbody.position = nextSpawnPos;
                            car.VehicleRigidbody.rotation = nextSpawnRot;
                        }
                        car.transform.SetPositionAndRotation(nextSpawnPos, nextSpawnRot);
                        InitializeAiSystemsForSession(car.transform);
                    }
                    return;
                }
            }

            IsRaceFinished = true;
            crashLessonPassed = true;
            playerFinishTime = blitzElapsedSeconds;
            crashEndStatusText = FormatRaceTime(playerFinishTime, padMinutes: false);
            CenterBannerText = winBanner;
            finishBannerTimer = 0f;
            hasTriggeredEventResults = false;

            if (activeCrashLesson != null)
            {
                MM2GameSession.SetCrashLessonStatus(MM2GameSession.SelectedCityCode, activeCrashLesson.LessonIndex, 1);
                RecordCurrentRaceToPlayerProfile(
                    playerFinishedCleanly: true,
                    finishRank: 1,
                    finishTime: playerFinishTime > 0f ? playerFinishTime : blitzElapsedSeconds
                );
                PlayRandomCrashSpeechClip(activeCrashLesson.WinClips);
            }
            PlayClip(sfxRaceWin);
        }

        private void TriggerCrashCourseFail(string reasonBanner, string resultsStatusText = null)
        {
            if (IsRaceFinished || IsSinkingInWater || IsPlayerTotaled) return;

            IsRaceFinished = true;
            crashLessonPassed = false;
            crashEndStatusText = !string.IsNullOrEmpty(resultsStatusText)
                ? resultsStatusText
                : reasonBanner.Replace('\n', ' ');
            CenterBannerText = reasonBanner;
            finishBannerTimer = 0f;
            hasTriggeredEventResults = false;

            if (activeCrashLesson != null)
            {
                MM2GameSession.SetCrashLessonStatus(MM2GameSession.SelectedCityCode, activeCrashLesson.LessonIndex, 2);
                PlayRandomCrashSpeechClip(activeCrashLesson.LoseClips);
            }
            PlayClip(sfxRaceLose);
        }

        private void ShowCrashTransientBanner(string message, float duration)
        {
            if (countdownStage < 3 || IsRaceFinished || IsSinkingInWater || IsPlayerTotaled) return;
            CenterBannerText = message;
            crashTransientBannerTimer = duration;
        }

        private void FixedUpdate()
        {
            if (FindFirstObjectByType<MM2MenuController>() != null) return;

            if (IsSinkingInWater || isImmersedInWaterPhysics)
            {
                ArcadeVehicleController sinkingCar = FindActivePlayerCar();
                if (sinkingCar != null && sinkingCar.VehicleRigidbody != null)
                {
                    Rigidbody rb = sinkingCar.VehicleRigidbody;
                    rb.useGravity = false;

                    // Damp horizontal motion and sink gradually into the water with a slight nose-down pitch
                    Vector3 vel = rb.linearVelocity;
                    vel.x *= 0.90f;
                    vel.z *= 0.90f;

                    float minSinkY = WaterSurfaceY - 2.6f;
                    if (rb.position.y > minSinkY)
                    {
                        vel.y = Mathf.Lerp(vel.y, -0.68f, Time.fixedDeltaTime * 6f);
                    }
                    else
                    {
                        vel.y = 0f;
                        Vector3 clampedPos = rb.position;
                        clampedPos.y = minSinkY;
                        rb.position = clampedPos;
                    }
                    rb.linearVelocity = vel;
                    rb.angularVelocity *= 0.85f;

                    Quaternion targetSinkRot = Quaternion.Euler(16f, sinkingCar.transform.eulerAngles.y, 0f);
                    rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetSinkRot, Time.fixedDeltaTime * 1.8f));
                }
                return;
            }

            if (!IsRaceActive || !IsCountdownLockActive) return;

            ArcadeVehicleController car = FindActivePlayerCar();
            if (car != null && car.VehicleRigidbody != null)
            {
                Rigidbody rb = car.VehicleRigidbody;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.MovePosition(startHoldPosition);
                rb.MoveRotation(startHoldRotation);
            }
        }

        private void ActivateWaterSinkingPhysics(ArcadeVehicleController car, float waterY)
        {
            EnsureAssetsLoaded();
            isImmersedInWaterPhysics = true;
            WaterSurfaceY = waterY;

            if (car != null && car.VehicleRigidbody != null)
            {
                Rigidbody rb = car.VehicleRigidbody;
                rb.useGravity = false;
                Vector3 v = rb.linearVelocity;
                rb.linearVelocity = new Vector3(v.x * 0.35f, Mathf.Max(-1.1f, v.y * 0.25f), v.z * 0.35f);
                rb.angularVelocity *= 0.25f;
                if (rb.position.y < waterY - 2.6f)
                {
                    rb.position = new Vector3(rb.position.x, waterY - 2.6f, rb.position.z);
                }
            }

            PlayClip(sfxWaterSplash);
        }

        private void TriggerSleepWithTheFishes(ArcadeVehicleController car, float waterY)
        {
            ActivateWaterSinkingPhysics(car, waterY);
            IsSinkingInWater = true;
            CenterBannerText = "Sleep with the fishes!";
            finishBannerTimer = 0f;
            hasTriggeredEventResults = false;

            PlayClip(sfxRaceLose);

            if (string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase) && activeCrashLesson != null)
            {
                crashLessonPassed = false;
                crashEndStatusText = "Sleep with the fishes!";
                MM2GameSession.SetCrashLessonStatus(MM2GameSession.SelectedCityCode, activeCrashLesson.LessonIndex, 2);
                PlayRandomCrashSpeechClip(activeCrashLesson.LoseClips);
            }
        }

        /// <summary>
        /// Triggered when the player's car reaches 100% damage (`MaxDamage` in `.vehCarDamage`) and its wheels detach.
        /// Displays the `"Game over!"` banner and transitions to the Event Results screen.
        /// </summary>
        public void TriggerPlayerTotaled(ArcadeVehicleController car)
        {
            if (IsPlayerTotaled || IsSinkingInWater || IsRaceFinished) return;
            EnsureAssetsLoaded();

            IsPlayerTotaled = true;
            CenterBannerText = "Game over!";
            finishBannerTimer = 0f;
            hasTriggeredEventResults = false;

            if (string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase) && activeCrashLesson != null)
            {
                crashLessonPassed = false;
                crashEndStatusText = "Game over! (Totaled)";
                MM2GameSession.SetCrashLessonStatus(MM2GameSession.SelectedCityCode, activeCrashLesson.LessonIndex, 2);
                PlayRandomCrashSpeechClip(activeCrashLesson.LoseClips);
            }
        }

        private void CheckPlayerCheckpointPasses(Vector3 prevPos, Vector3 currPos)
        {
            if (checkpoints.Count == 0 || IsRaceFinished) return;

            bool isCircuit = string.Equals(ActiveRaceType, "Circuit", StringComparison.OrdinalIgnoreCase);

            if (isCircuit)
            {
                // In Circuit mode, checkpoints must be taken in strict sequential order (0 .. TotalCheckpoints - 1)
                if (CollectedCheckpoints >= 0 && CollectedCheckpoints < checkpoints.Count)
                {
                    CheckpointNode targetNode = checkpoints[CollectedCheckpoints];
                    if (!targetNode.IsCollected && DidCarPassThroughNode(targetNode, prevPos, currPos))
                    {
                        OnCircuitCheckpointPassed(targetNode);
                    }
                }
            }
            else
            {
                // In Blitz and Checkpoint modes, checkpoints can be taken in any order
                for (int i = 0; i < checkpoints.Count; i++)
                {
                    CheckpointNode node = checkpoints[i];
                    if (node.IsCollected) continue;

                    if (DidCarPassThroughNode(node, prevPos, currPos))
                    {
                        OnAnyOrderCheckpointPassed(node);
                        break;
                    }
                }
            }
        }

        private static bool DidCarPassThroughNode(CheckpointNode node, Vector3 prevPos, Vector3 currPos)
        {
            if (node == null || node.RootObject == null) return false;

            Transform t = node.RootObject.transform;
            float halfWidth = node.Waypoint.Radius + 2.2f;
            const float minHeight = -8.5f;
            const float maxHeight = 65.0f;
            const float gateDepthTolerance = 4.2f;

            Vector3 localCurr = t.InverseTransformPoint(currPos);
            if (localCurr.y >= minHeight && localCurr.y <= maxHeight)
            {
                // 1. Direct gate box check
                if (Mathf.Abs(localCurr.x) <= halfWidth && Mathf.Abs(localCurr.z) <= gateDepthTolerance)
                {
                    return true;
                }

                // 2. Radial proximity check (handles angled intersection entries cleanly)
                float dx = currPos.x - node.Waypoint.Position.x;
                float dz = currPos.z - node.Waypoint.Position.z;
                float radialLimit = Mathf.Max(6.5f, node.Waypoint.Radius * 0.72f);
                if (dx * dx + dz * dz <= radialLimit * radialLimit)
                {
                    return true;
                }
            }

            // 3. High-speed segment plane crossing check (using interpolated height at gate crossing)
            Vector3 localPrev = t.InverseTransformPoint(prevPos);
            if ((localPrev.z <= 0f && localCurr.z >= 0f) || (localPrev.z >= 0f && localCurr.z <= 0f))
            {
                float denom = localCurr.z - localPrev.z;
                float frac = Mathf.Abs(denom) > 0.0001f ? Mathf.Clamp01(-localPrev.z / denom) : 0.5f;
                float crossX = Mathf.Lerp(localPrev.x, localCurr.x, frac);
                float crossY = Mathf.Lerp(localPrev.y, localCurr.y, frac);
                if (Mathf.Abs(crossX) <= halfWidth && crossY >= minHeight && crossY <= maxHeight)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnAnyOrderCheckpointPassed(CheckpointNode passedNode)
        {
            passedNode.IsCollected = true;
            if (passedNode.RootObject != null)
            {
                passedNode.RootObject.SetActive(false);
            }

            CollectedCheckpoints++;

            if (CollectedCheckpoints >= TotalCheckpoints)
            {
                // Race Finished!
                CompletePlayerRaceFinish();
                return;
            }

            // If only 1 checkpoint remains, swap its model to pt_finish.pkg ("FINISH")
            int remaining = TotalCheckpoints - CollectedCheckpoints;
            if (remaining == 1)
            {
                for (int i = 0; i < checkpoints.Count; i++)
                {
                    if (!checkpoints[i].IsCollected)
                    {
                        SetNodeFinishVisual(checkpoints[i], true);
                    }
                }
                PlayClip(sfxLastWaypoint != null ? sfxLastWaypoint : sfxWaypoint);
            }
            else
            {
                PlayClip(sfxWaypoint);
            }
        }

        private void OnCircuitCheckpointPassed(CheckpointNode passedNode)
        {
            passedNode.IsCollected = true;
            if (passedNode.RootObject != null)
            {
                passedNode.RootObject.SetActive(false);
            }

            CollectedCheckpoints++;

            if (CollectedCheckpoints >= TotalCheckpoints)
            {
                // Record completed lap split time
                float lapSplit = Mathf.Max(0.01f, DisplayTimeSeconds - currentLapStartTime);
                completedLapTimes.Add(lapSplit);
                currentLapStartTime = DisplayTimeSeconds;

                // Completed a full lap!
                if (CurrentLap >= TotalLaps)
                {
                    // Completed final lap -> Race Finished!
                    CompletePlayerRaceFinish();
                    return;
                }

                // Advance to next lap and reactivate all checkpoints in the circuit
                CurrentLap++;
                CollectedCheckpoints = 0;
                bool isFinalLap = (CurrentLap == TotalLaps);

                for (int i = 0; i < checkpoints.Count; i++)
                {
                    CheckpointNode cp = checkpoints[i];
                    cp.IsCollected = false;
                    if (cp.RootObject != null) cp.RootObject.SetActive(true);
                    bool shouldBeFinish = isFinalLap && (i == checkpoints.Count - 1);
                    SetNodeFinishVisual(cp, shouldBeFinish);
                }

                PlayClip(isFinalLap && sfxLastWaypoint != null ? sfxLastWaypoint : sfxWaypoint);
                return;
            }

            bool isSecondToLastOnFinalLap = (CurrentLap == TotalLaps) && (CollectedCheckpoints == TotalCheckpoints - 1);
            if (isSecondToLastOnFinalLap)
            {
                SetNodeFinishVisual(checkpoints[checkpoints.Count - 1], true);
                PlayClip(sfxLastWaypoint != null ? sfxLastWaypoint : sfxWaypoint);
            }
            else
            {
                PlayClip(sfxWaypoint);
            }
        }

        public bool IsQuestTargetVehicleStage =>
            IsRaceActive &&
            string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase) &&
            (activeCrashStage.EventType == 2 || activeCrashStage.EventType == 8);

        public void GetActiveMinimapCheckpoints(List<Vector3> outPositions)
        {
            if (outPositions == null) return;
            outPositions.Clear();

            if (!IsRaceActive || IsRaceFinished || checkpoints.Count == 0)
            {
                return;
            }

            if (string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase))
            {
                if (activeCrashStage.EventType == 2 || activeCrashStage.EventType == 8)
                {
                    return;
                }

                if (activeCrashStage.EventType == 9 || activeCrashStage.ChkFlags != 0)
                {
                    if (CollectedCheckpoints >= 0 && CollectedCheckpoints < checkpoints.Count)
                    {
                        CheckpointNode nextCp = checkpoints[CollectedCheckpoints];
                        if (nextCp != null && !nextCp.IsCollected)
                        {
                            outPositions.Add(nextCp.Waypoint.Position);
                        }
                    }
                    return;
                }
            }

            for (int i = 0; i < checkpoints.Count; i++)
            {
                CheckpointNode cp = checkpoints[i];
                if (cp == null || cp.IsCollected) continue;
                if (cp.RootObject != null && !cp.RootObject.activeSelf) continue;
                outPositions.Add(cp.Waypoint.Position);
            }
        }

        /// <summary>
        /// Returns the world position of the target checkpoint for the HUD 3D green arrow:
        /// - In Circuit mode: the next sequential checkpoint in the current lap.
        /// - In CrashCourse mode: the target car (Event 2 &amp; 8), hidden (Event 9), or the next sequential checkpoint.
        /// - In Blitz / Checkpoint mode: the nearest uncollected checkpoint as the crow flies.
        /// </summary>
        public bool TryGetTargetCheckpointPosition(Vector3 fromPosition, out Vector3 targetPosition)
        {
            targetPosition = Vector3.zero;
            if (!IsRaceActive || IsRaceFinished || checkpoints.Count == 0)
            {
                return false;
            }

            if (string.Equals(ActiveRaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase))
            {
                int ev = activeCrashStage.EventType;
                if (ev == 9)
                {
                    // Event 9 ("4. The Knowledge" in London) hides the HUD arrow to test landmark knowledge
                    return false;
                }
                if ((ev == 2 || ev == 8) && opponentManager != null && opponentManager.ActiveOpponents.Count > 0)
                {
                    MM2OpponentAgent targetOpp = opponentManager.ActiveOpponents[0];
                    if (targetOpp != null)
                    {
                        targetPosition = targetOpp.transform.position;
                        return true;
                    }
                }
                int cpIdx = Mathf.Clamp(CollectedCheckpoints, 0, checkpoints.Count - 1);
                if (!checkpoints[cpIdx].IsCollected)
                {
                    targetPosition = checkpoints[cpIdx].Waypoint.Position;
                    return true;
                }
                return false;
            }

            bool isCircuit = string.Equals(ActiveRaceType, "Circuit", StringComparison.OrdinalIgnoreCase);
            if (isCircuit)
            {
                int idx = Mathf.Clamp(CollectedCheckpoints, 0, checkpoints.Count - 1);
                if (!checkpoints[idx].IsCollected)
                {
                    targetPosition = checkpoints[idx].Waypoint.Position;
                    return true;
                }
            }

            float bestDistSq = float.MaxValue;
            bool found = false;

            for (int i = 0; i < checkpoints.Count; i++)
            {
                CheckpointNode cp = checkpoints[i];
                if (cp.IsCollected) continue;

                float dx = cp.Waypoint.Position.x - fromPosition.x;
                float dz = cp.Waypoint.Position.z - fromPosition.z;
                float dSq = dx * dx + dz * dz;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    targetPosition = cp.Waypoint.Position;
                    found = true;
                }
            }

            return found;
        }

        private void CompletePlayerRaceFinish()
        {
            IsRaceFinished = true;
            bool isBlitz = string.Equals(ActiveRaceType, "Blitz", StringComparison.OrdinalIgnoreCase);
            playerFinishTime = isBlitz ? blitzElapsedSeconds : DisplayTimeSeconds;
            finishBannerTimer = 0f;
            hasTriggeredEventResults = false;

            ArcadeVehicleController car = FindActivePlayerCar();
            Vector3 carPos = car != null ? car.transform.position : Vector3.zero;

            if (opponentManager != null && opponentManager.ActiveOpponents.Count > 0)
            {
                PlayerPosition = opponentManager.ComputePlayerRacePosition(
                    CurrentLap,
                    CollectedCheckpoints,
                    carPos,
                    true,
                    playerFinishTime,
                    out int totalRacers
                );
                TotalRacers = totalRacers;

                CenterBannerText = $"You finished {FormatOrdinal(PlayerPosition)}!";
                PlayClip(PlayerPosition <= 3 ? sfxRaceWin : sfxRaceLose);
            }
            else
            {
                PlayerPosition = 1;
                TotalRacers = 1;
                CenterBannerText = isBlitz ? "You passed the Blitz!" : "You finished 1st!";
                PlayClip(sfxRaceWin);
            }

            RecordCurrentRaceToPlayerProfile(
                playerFinishedCleanly: true,
                finishRank: PlayerPosition,
                finishTime: playerFinishTime
            );
        }

        public static string FormatRaceTime(float seconds, bool padMinutes = false)
        {
            float t = Mathf.Max(0f, seconds);
            int totalCentis = Mathf.FloorToInt(t * 100f + 0.001f);
            int centis = totalCentis % 100;
            int totalSecs = totalCentis / 100;
            int secs = totalSecs % 60;
            int mins = totalSecs / 60;
            return padMinutes
                ? $"{mins:00}:{secs:00}:{centis:00}"
                : $"{mins}:{secs:00}:{centis:00}";
        }

        private void TransitionToEventResultsScreen()
        {
            BuildAndStoreEventResults();

            if (FindFirstObjectByType<MM2MenuController>() == null)
            {
                GameObject menuGo = new GameObject("MM2_MenuSystem");
                var ctrl = menuGo.AddComponent<MM2MenuController>();
                ctrl.ShowScreen(MM2MenuController.MenuScreen.EventResults);
            }
        }

        private void BuildAndStoreEventResults()
        {
            string mode = string.IsNullOrEmpty(MM2GameSession.RaceType) ? "Cruise" : MM2GameSession.RaceType;
            bool isCrashCourse = string.Equals(mode, "CrashCourse", StringComparison.OrdinalIgnoreCase);
            string modeLabel = isCrashCourse
                ? "Crash Course"
                : (string.Equals(mode, "Cruise", StringComparison.OrdinalIgnoreCase)
                    ? "Cruise"
                    : $"{mode} Race");

            string rName = MM2GameSession.RaceName;
            if (string.IsNullOrEmpty(rName))
            {
                string[] names = MM2RuntimeCityLoader.GetRaceNames(MM2GameSession.Location, mode);
                if (names != null && names.Length > 0)
                {
                    rName = names[Mathf.Clamp(MM2GameSession.RaceIndex, 0, names.Length - 1)];
                }
                else
                {
                    rName = MM2GameSession.Location;
                }
            }

            var data = new MM2EventResultsData
            {
                RaceModeLabel = modeLabel,
                RaceName = rName
            };

            string driverName = string.IsNullOrEmpty(MM2GameSession.DriverName) ? "DriverX" : MM2GameSession.DriverName;
            bool isBlitz = string.Equals(mode, "Blitz", StringComparison.OrdinalIgnoreCase);
            bool isCruise = string.Equals(mode, "Cruise", StringComparison.OrdinalIgnoreCase);

            if (isCrashCourse || isCruise || isBlitz || opponentManager == null || opponentManager.ActiveOpponents.Count == 0)
            {
                string statusText;
                if (isCrashCourse && !string.IsNullOrEmpty(crashEndStatusText))
                {
                    statusText = crashLessonPassed ? $"Passed ({crashEndStatusText})" : crashEndStatusText;
                }
                else if (IsSinkingInWater)
                {
                    statusText = "Sleep with the fishes!";
                }
                else if (IsPlayerTotaled)
                {
                    statusText = "Game over! (Totaled)";
                }
                else if (isBlitz && DisplayTimeSeconds <= 0f && CollectedCheckpoints < TotalCheckpoints)
                {
                    statusText = "Time's Up!";
                }
                else
                {
                    float t = (isBlitz || isCrashCourse) ? blitzElapsedSeconds : (playerFinishTime > 0f ? playerFinishTime : DisplayTimeSeconds);
                    statusText = FormatRaceTime(t, padMinutes: false);
                }

                data.Entries.Add(new MM2EventResultEntry
                {
                    Rank = 1,
                    RacerName = driverName,
                    TimeOrStatus = statusText,
                    IsPlayer = true
                });
            }
            else
            {
                ArcadeVehicleController car = FindActivePlayerCar();
                Vector3 carPos = car != null ? car.transform.position : Vector3.zero;
                int totalCps = Mathf.Max(1, TotalCheckpoints);

                var standings = new List<(string name, bool isPlayer, bool finished, float finishTime, float score, string statusOverride)>();

                bool playerFinishedCleanly = IsRaceFinished && !IsSinkingInWater && !IsPlayerTotaled;
                float pScore = ((CurrentLap - 1) * totalCps + CollectedCheckpoints) * 10000f;
                if (IsSinkingInWater || IsPlayerTotaled) pScore -= 100000f;
                string pStatusOverride = IsSinkingInWater
                    ? "Sleep with the fishes!"
                    : (IsPlayerTotaled ? "DNF (Totaled)" : null);

                standings.Add((
                    driverName,
                    true,
                    playerFinishedCleanly,
                    playerFinishTime > 0f ? playerFinishTime : DisplayTimeSeconds,
                    pScore,
                    pStatusOverride
                ));

                var opps = opponentManager.ActiveOpponents;
                for (int i = 0; i < opps.Count; i++)
                {
                    MM2OpponentAgent opp = opps[i];
                    if (opp == null) continue;
                    bool oppTotaled = opp.Damage != null && opp.Damage.IsTotaled;
                    float oScore = ((opp.CurrentLap - 1) * totalCps + opp.CurrentCheckpointIndex) * 10000f + opp.DistanceAlongLap;
                    if (oppTotaled) oScore -= 90000f;
                    standings.Add((
                        opp.RacerName,
                        false,
                        opp.HasFinished && !oppTotaled,
                        opp.FinishTime,
                        oScore,
                        oppTotaled ? "DNF (Totaled)" : null
                    ));
                }

                standings.Sort((a, b) =>
                {
                    if (a.finished && b.finished)
                        return a.finishTime.CompareTo(b.finishTime);
                    if (a.finished != b.finished)
                        return a.finished ? -1 : 1;
                    return b.score.CompareTo(a.score);
                });

                float referenceTime = playerFinishTime > 0.5f ? playerFinishTime : Mathf.Max(25f, DisplayTimeSeconds);
                for (int i = 0; i < standings.Count; i++)
                {
                    var item = standings[i];
                    string timeStr;
                    if (!string.IsNullOrEmpty(item.statusOverride))
                    {
                        timeStr = item.statusOverride;
                    }
                    else if (item.finished)
                    {
                        timeStr = FormatRaceTime(item.finishTime, padMinutes: false);
                        referenceTime = Mathf.Max(referenceTime, item.finishTime);
                    }
                    else
                    {
                        // Estimate finish time for AI opponents that were still on the course when the race ended
                        float estTime = referenceTime + 3.4f + i * 2.75f + ((i * 37) % 100) * 0.01f;
                        timeStr = FormatRaceTime(estTime, padMinutes: false);
                    }

                    data.Entries.Add(new MM2EventResultEntry
                    {
                        Rank = i + 1,
                        RacerName = item.name,
                        TimeOrStatus = timeStr,
                        IsPlayer = item.isPlayer
                    });
                }
            }

            MM2GameSession.LastEventResults = data;

            bool finishedCleanly = false;
            if (!isCruise)
            {
                int finalRank = 1;
                float finalTime = playerFinishTime > 0f ? playerFinishTime : DisplayTimeSeconds;

                if (isCrashCourse)
                {
                    finishedCleanly = crashLessonPassed && !IsSinkingInWater && !IsPlayerTotaled;
                    finalTime = playerFinishTime > 0f ? playerFinishTime : blitzElapsedSeconds;
                }
                else if (isBlitz)
                {
                    finishedCleanly = IsRaceFinished && !IsSinkingInWater && !IsPlayerTotaled &&
                        !(DisplayTimeSeconds <= 0f && CollectedCheckpoints < TotalCheckpoints);
                    finalTime = blitzElapsedSeconds;
                }
                else
                {
                    finishedCleanly = IsRaceFinished && !IsSinkingInWater && !IsPlayerTotaled;
                    for (int i = 0; i < data.Entries.Count; i++)
                    {
                        if (data.Entries[i].IsPlayer)
                        {
                            finalRank = data.Entries[i].Rank;
                            break;
                        }
                    }
                }

                if (!hasRecordedRaceResult)
                {
                    RecordCurrentRaceToPlayerProfile(finishedCleanly, finalRank, finalTime);
                }
            }

            if (isCrashCourse)
            {
                CitySpec cSpec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.Location);
                int totalLessons = (cSpec != null && cSpec.CrashLessons != null) ? cSpec.CrashLessons.Length : 0;
                int curLessonIdx = activeCrashLesson != null ? activeCrashLesson.LessonIndex : MM2GameSession.CrashLessonIndex;
                int nextLessonIdx = MM2GameSession.GetNextUnlockedCrashLessonIndex(
                    MM2GameSession.SelectedCityCode,
                    curLessonIdx,
                    totalLessons
                );
                data.CanAdvanceToNext = finishedCleanly && nextLessonIdx >= 0;
            }
            else
            {
                data.CanAdvanceToNext = isCruise || finishedCleanly;
            }
        }

        private void RecordCurrentRaceToPlayerProfile(bool playerFinishedCleanly, int finishRank, float finishTime)
        {
            if (hasRecordedRaceResult) return;

            string mode = string.IsNullOrEmpty(MM2GameSession.RaceType) ? ActiveRaceType : MM2GameSession.RaceType;
            if (string.IsNullOrEmpty(mode) || string.Equals(mode, "Cruise", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            hasRecordedRaceResult = true;

            bool isCrashMode = string.Equals(mode, "CrashCourse", StringComparison.OrdinalIgnoreCase);
            int raceId = isCrashMode
                ? (activeCrashLesson != null ? activeCrashLesson.LessonIndex : MM2GameSession.CrashLessonIndex)
                : MM2GameSession.RaceIndex;
            string targetCity = isCrashMode && !string.IsNullOrEmpty(MM2GameSession.CrashCourseCity)
                ? MM2GameSession.CrashCourseCity
                : MM2GameSession.SelectedCityCode;

            MM2PlayerManager.RecordRaceResult(
                cityCode: targetCity,
                modeName: mode,
                raceId: raceId,
                playerFinishedCleanly: playerFinishedCleanly,
                finishRank: finishRank,
                finishTimeSeconds: finishTime,
                vehicleDisplayName: MM2GameSession.VehicleName
            );
        }

        private static string FormatOrdinal(int number)
        {
            if (number <= 0) return number.ToString();
            int rem100 = number % 100;
            if (rem100 >= 11 && rem100 <= 13) return $"{number}th";
            switch (number % 10)
            {
                case 1: return $"{number}st";
                case 2: return $"{number}nd";
                case 3: return $"{number}rd";
                default: return $"{number}th";
            }
        }

        public void RefreshAiVehicleLights(bool headlightsOn)
        {
            if (trafficManager != null)
            {
                var agents = trafficManager.ActiveAgents;
                for (int i = 0; i < agents.Count; i++)
                {
                    if (agents[i] != null && agents[i].HeadlightsGlow != null)
                    {
                        agents[i].HeadlightsGlow.SetActive(headlightsOn);
                    }
                }
            }
            if (opponentManager != null)
            {
                var opps = opponentManager.ActiveOpponents;
                for (int i = 0; i < opps.Count; i++)
                {
                    if (opps[i] != null && opps[i].HeadlightsGlow != null)
                    {
                        opps[i].HeadlightsGlow.SetActive(headlightsOn);
                    }
                }
            }
            if (policeManager != null)
            {
                var cops = policeManager.ActiveCops;
                for (int i = 0; i < cops.Count; i++)
                {
                    if (cops[i] != null && cops[i].HeadlightsGlow != null)
                    {
                        cops[i].HeadlightsGlow.SetActive(headlightsOn || cops[i].State == MM2PoliceState.Pursuit);
                    }
                }
            }
            if (specialTrafficManager != null)
            {
                specialTrafficManager.RefreshHeadlights(headlightsOn);
            }
        }

        private void PlayClip(AudioClip clip)
        {
            if (clip == null || raceAudioSource == null) return;
            raceAudioSource.PlayOneShot(clip);
        }

        private static ArcadeVehicleController FindActivePlayerCar()
        {
            foreach (var car in FindObjectsByType<ArcadeVehicleController>(FindObjectsSortMode.None))
            {
                if (car != null && car.gameObject.activeInHierarchy)
                {
                    return car;
                }
            }
            return null;
        }
    }
}
