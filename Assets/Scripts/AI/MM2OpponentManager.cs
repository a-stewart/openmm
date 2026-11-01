using System;
using System.Collections.Generic;
using UnityEngine;
using MM2.Archive;
using MM2.UI;
using MM2.Vehicle;

namespace MM2.AI
{
    public struct OpponentRouteNode
    {
        public Vector3 Position;
        public Vector3 Forward;
        public Vector3 Right;
        public Vector3 Up;
        public float CumulativeDistance;
        public float SpeedLimit;
    }

    public class MM2OpponentAgent : MonoBehaviour
    {
        public int OpponentIndex;
        public string RacerName;
        public string PkgName;
        public Rigidbody Body;
        public BoxCollider MainCollider;
        public MM2VehicleDamage Damage;

        public Transform WheelFL;
        public Transform WheelFR;
        public Transform WheelRL;
        public Transform WheelRR;
        public float WheelRadius = 0.34f;
        public float HalfLength = 2.15f;
        public float HalfWidth = 0.90f;
        public float GroundClearanceOffset = 0.02f;

        public GameObject HeadlightsGlow;
        public GameObject BrakelightsGlow;

        // Route & racing state
        public List<OpponentRouteNode> RouteNodes = new List<OpponentRouteNode>();
        public float TotalRouteLength;
        public float[] CheckpointRouteDistances; // Route distance of each checkpoint (0..N-1)
        public bool IsCircuitLoop;

        public Vector3 StartPosition;
        public Quaternion StartRotation;

        public float DistanceAlongLap;
        public float CurrentSpeed;
        public float BaseTopSpeed = 38f;
        public float MaxThrottle = 0.9f;
        public float CornerBraking = 0.75f;
        public float PersonalSkillFactor = 1f;

        public float PreferredLateralOffset;
        public float CurrentLateralOffset;
        public Vector3 ImpactPerturbationVelocity;
        public float ImpactRecoveryTimer;

        public float WheelSpinAngle;
        public float SteerVisualAngle;
        public bool IsBraking;

        // Race standings & progress
        public int CurrentLap = 1;
        public int CurrentCheckpointIndex = 0;
        public bool HasFinished = false;
        public float FinishTime = 0f;

        private void OnCollisionEnter(Collision collision)
        {
            if (collision == null || collision.rigidbody == null) return;
            Vector3 relVel = collision.relativeVelocity;
            float impactMag = relVel.magnitude;
            if (impactMag < 2.0f) return;

            // Absorb lateral/longitudinal impact impulse so player/cop collisions knock opponents realistically
            Vector3 horizImpulse = new Vector3(-relVel.x, 0f, -relVel.z) * 0.45f;
            ImpactPerturbationVelocity = Vector3.ClampMagnitude(ImpactPerturbationVelocity + horizImpulse, 14f);
            ImpactRecoveryTimer = Mathf.Clamp(impactMag * 0.08f, 0.25f, 1.2f);
            CurrentSpeed = Mathf.Max(4f, CurrentSpeed - impactMag * 0.22f);
        }
    }

    /// <summary>
    /// Spawns, routes, and simulates AI Race Opponents in Checkpoint and Circuit races,
    /// reading opponent vehicles and .opp routes from race/&lt;city&gt;/&lt;race&gt;.aimap and
    /// stitching them through the .bai city road network and race checkpoints.
    /// </summary>
    public class MM2OpponentManager : MonoBehaviour
    {
        public static MM2OpponentManager Instance { get; private set; }

        private static readonly string[] FallbackOpponentPackages =
        {
            "vpbug", "vpcoop", "vpmustang99", "vppt", "vpford", "vpcab", "vpdb7", "vppanoz"
        };

        private MM2AiMapData aiMap;
        private Transform playerTransform;
        private Transform opponentsContainer;
        private PhysicsMaterial opponentPhysMat;

        private readonly List<MM2OpponentAgent> activeOpponents = new List<MM2OpponentAgent>();
        private readonly List<Vector3> raceCheckpointPositions = new List<Vector3>();
        private readonly List<Quaternion> raceCheckpointRotations = new List<Quaternion>();
        private readonly List<float> raceCheckpointRadii = new List<float>();

        private bool isCircuitRace;
        private bool isCrashCourse;
        private int totalLaps = 1;
        private bool nightOrBadWeatherLights;

        public IReadOnlyList<MM2OpponentAgent> ActiveOpponents => activeOpponents;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Initialize(
            MM2AiMapData mapData,
            Transform player,
            string gameMode,
            IReadOnlyList<Vector3> checkpointPositions,
            IReadOnlyList<float> checkpointRadii,
            int numLaps,
            IReadOnlyList<Quaternion> checkpointRotations = null)
        {
            ClearOpponents();

            aiMap = mapData;
            playerTransform = player;
            isCircuitRace = string.Equals(gameMode, "Circuit", StringComparison.OrdinalIgnoreCase);
            bool isCheckpointRace = string.Equals(gameMode, "Checkpoint", StringComparison.OrdinalIgnoreCase);
            isCrashCourse = string.Equals(gameMode, "CrashCourse", StringComparison.OrdinalIgnoreCase);
            totalLaps = isCircuitRace ? Mathf.Max(1, numLaps) : 1;

            // Opponents participate in Checkpoint, Circuit, and CrashCourse races when NumOpponents > 0
            int desiredOpponents = Mathf.Clamp(MM2GameSession.NumOpponents, 0, 8);
            if ((!isCircuitRace && !isCheckpointRace && !isCrashCourse) || desiredOpponents <= 0 || checkpointPositions == null || checkpointPositions.Count == 0)
            {
                return;
            }

            raceCheckpointPositions.Clear();
            raceCheckpointRotations.Clear();
            raceCheckpointRadii.Clear();
            for (int i = 0; i < checkpointPositions.Count; i++)
            {
                raceCheckpointPositions.Add(checkpointPositions[i]);
                Quaternion rot = (checkpointRotations != null && i < checkpointRotations.Count)
                    ? checkpointRotations[i]
                    : Quaternion.identity;
                raceCheckpointRotations.Add(rot);
                float r = (checkpointRadii != null && i < checkpointRadii.Count) ? checkpointRadii[i] : 14f;
                raceCheckpointRadii.Add(Mathf.Max(8f, r));
            }

            nightOrBadWeatherLights = MM2GameSession.TimeOfDayIndex >= 2 || MM2GameSession.WeatherIndex >= 2;

            if (opponentPhysMat == null)
            {
                opponentPhysMat = new PhysicsMaterial("MM2_Opponent_PhysMat")
                {
                    dynamicFriction = 0.2f,
                    staticFriction = 0.2f,
                    bounciness = 0.08f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounceCombine = PhysicsMaterialCombine.Average
                };
            }

            if (opponentsContainer == null)
            {
                GameObject root = new GameObject("MM2_RaceOpponents");
                root.transform.SetParent(transform, false);
                opponentsContainer = root.transform;
            }

            SpawnOpponents(desiredOpponents);

            Debug.Log($"[MM2OpponentManager] Initialized {activeOpponents.Count}/{desiredOpponents} opponents for {gameMode} race ({raceCheckpointPositions.Count} checkpoints, {totalLaps} laps).");
        }

        public void ClearOpponents()
        {
            for (int i = 0; i < activeOpponents.Count; i++)
            {
                if (activeOpponents[i] != null && activeOpponents[i].gameObject != null)
                {
                    Destroy(activeOpponents[i].gameObject);
                }
            }
            activeOpponents.Clear();
        }

        public void ResetOpponentsToStartingGrid()
        {
            for (int i = 0; i < activeOpponents.Count; i++)
            {
                MM2OpponentAgent opp = activeOpponents[i];
                if (opp == null) continue;

                opp.DistanceAlongLap = 0f;
                opp.CurrentSpeed = 0f;
                opp.CurrentLateralOffset = 0f;
                opp.ImpactPerturbationVelocity = Vector3.zero;
                opp.ImpactRecoveryTimer = 0f;
                opp.SteerVisualAngle = 0f;
                opp.IsBraking = true;
                opp.CurrentLap = 1;
                opp.CurrentCheckpointIndex = 0;
                opp.HasFinished = false;
                opp.FinishTime = 0f;

                if (opp.Damage != null)
                {
                    opp.Damage.ResetDamage();
                }

                Vector3 pos = opp.StartPosition + Vector3.up * opp.GroundClearanceOffset;
                opp.transform.SetPositionAndRotation(pos, opp.StartRotation);
                if (opp.Body != null)
                {
                    opp.Body.position = pos;
                    opp.Body.rotation = opp.StartRotation;
                    opp.Body.linearVelocity = Vector3.zero;
                    opp.Body.angularVelocity = Vector3.zero;
                }
            }
        }

        private void SpawnOpponents(int count)
        {
            Vector3 playerStartPos = playerTransform != null ? playerTransform.position : Vector3.zero;
            Quaternion playerStartRot = playerTransform != null ? playerTransform.rotation : Quaternion.identity;
            Vector3 playerFwd = playerStartRot * Vector3.forward;
            Vector3 playerRight = playerStartRot * Vector3.right;

            for (int i = 0; i < count; i++)
            {
                MM2AiMapData.OpponentDefinition def = (aiMap != null && i < aiMap.Opponents.Count)
                    ? aiMap.Opponents[i]
                    : null;

                if (isCrashCourse && (def == null || !def.HasValidStartPose))
                {
                    continue;
                }

                string pkgName = (def != null && !string.IsNullOrEmpty(def.PkgName))
                    ? def.PkgName
                    : FallbackOpponentPackages[i % FallbackOpponentPackages.Length];

                Vector3 startPos;
                Quaternion startRot;
                if (def != null && def.HasValidStartPose)
                {
                    startPos = def.StartPosition;
                    startRot = def.StartRotation;
                    if (isCrashCourse && def.Waypoints != null && def.Waypoints.Count >= 2)
                    {
                        // In Crash Course .opp files, Waypoints[0] is the source intersection behind StartPosition
                        // and Waypoints[1] is the sink intersection ahead of StartPosition.
                        int firstAhead = 0;
                        Vector3 d0 = def.Waypoints[0].Position - startPos;
                        Vector3 d1 = def.Waypoints[1].Position - startPos;
                        d0.y = 0f;
                        d1.y = 0f;
                        if (Vector3.Dot(d0, d1) < 0f || d0.sqrMagnitude < 4f)
                        {
                            firstAhead = 1;
                        }
                        Vector3 dirToAhead = def.Waypoints[firstAhead].Position - startPos;
                        dirToAhead.y = 0f;
                        if (dirToAhead.sqrMagnitude > 1f)
                        {
                            startRot = Quaternion.LookRotation(dirToAhead.normalized, Vector3.up);
                        }
                    }
                    else
                    {
                        if ((startPos - playerStartPos).sqrMagnitude > 80f * 80f)
                        {
                            // Guard against any unused/outlier .opp files placed far from the player's starting grid
                            int row = (i / 2) + 1;
                            float side = ((i & 1) == 0) ? -2.3f : 2.3f;
                            startPos = playerStartPos - playerFwd * (row * 6.8f) + playerRight * side;
                            startRot = playerStartRot;
                        }
                        else if (Quaternion.Angle(startRot, playerStartRot) > 40f)
                        {
                            // Guard against any outlier .opp files whose row-0 angle deviates from the starting grid direction
                            startRot = playerStartRot;
                        }
                    }
                }
                else
                {
                    // Standard 2-column staggered starting grid behind/beside player
                    int row = (i / 2) + 1;
                    float side = ((i & 1) == 0) ? -2.3f : 2.3f;
                    startPos = playerStartPos - playerFwd * (row * 6.8f) + playerRight * side;
                    startRot = playerStartRot;
                }

                startPos = SnapPointToSameDeck(startPos, startPos.y);

                MM2OpponentAgent opp = CreateOpponentVehicleObject(i, pkgName, startPos, startRot, def);
                if (opp == null) continue;

                BuildOpponentRoute(opp, def);
                activeOpponents.Add(opp);
            }
        }

        private MM2OpponentAgent CreateOpponentVehicleObject(
            int index,
            string pkgName,
            Vector3 startPos,
            Quaternion startRot,
            MM2AiMapData.OpponentDefinition def)
        {
            MM2RuntimePkgLoader.ParsedPkg pkg = MM2RuntimePkgLoader.LoadPkg(pkgName);
            if (pkg == null)
            {
                pkgName = FallbackOpponentPackages[index % FallbackOpponentPackages.Length];
                pkg = MM2RuntimePkgLoader.LoadPkg(pkgName);
                if (pkg == null) return null;
            }

            int pjCount = Mathf.Max(1, pkg.NumPaintjobs);
            int pjIndex = (isCrashCourse ? index : (index + 1)) % pjCount;

            GameObject oppGo = new GameObject($"Opponent_{index + 1}_{pkgName}");
            oppGo.transform.SetParent(opponentsContainer, false);

            if (!MM2RuntimePkgLoader.BuildRuntimeVehicleVisuals(
                pkgName,
                pjIndex,
                oppGo.transform,
                out _,
                out Transform wheelFL,
                out Transform wheelFR,
                out Transform wheelRL,
                out Transform wheelRR,
                out _,
                out _,
                out _,
                out _,
                out float wheelRadius))
            {
                Destroy(oppGo);
                return null;
            }

            Bounds localBounds = MM2TrafficManager.ComputeLocalVisualBounds(oppGo.transform);
            BoxCollider box = oppGo.AddComponent<BoxCollider>();
            box.center = new Vector3(localBounds.center.x, Mathf.Max(0.45f, localBounds.center.y), localBounds.center.z);
            box.size = new Vector3(
                Mathf.Clamp(localBounds.size.x, 1.4f, 2.8f),
                Mathf.Clamp(localBounds.size.y, 1.1f, 3.2f),
                Mathf.Clamp(localBounds.size.z, 3.3f, 8.5f)
            );
            box.sharedMaterial = opponentPhysMat;

            Rigidbody rb = oppGo.AddComponent<Rigidbody>();
            rb.mass = 1420f;
            rb.linearDamping = 0.15f;
            rb.angularDamping = 2.0f;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            MM2OpponentAgent opp = oppGo.AddComponent<MM2OpponentAgent>();
            opp.OpponentIndex = index;
            opp.RacerName = $"Opponent {index + 1}";
            opp.PkgName = pkgName;
            opp.Body = rb;
            opp.MainCollider = box;
            opp.WheelFL = wheelFL;
            opp.WheelFR = wheelFR;
            opp.WheelRL = wheelRL;
            opp.WheelRR = wheelRR;
            opp.WheelRadius = Mathf.Max(0.24f, wheelRadius);
            opp.HalfLength = box.size.z * 0.5f;
            opp.HalfWidth = box.size.x * 0.5f;
            opp.GroundClearanceOffset = Mathf.Max(0f, -localBounds.min.y + 0.02f);

            opp.StartPosition = startPos;
            opp.StartRotation = startRot;

            // Convert .aimap speed values (authored in MPH, e.g. 50.0) into m/s (* 0.44704f)
            // and bound each opponent's speed to its vehicle physics and the active event type.
            const float MphToMs = 0.44704f;
            float skillSpread = isCrashCourse ? 1.0f : Mathf.Lerp(1.02f, 0.88f, index / 7f);
            opp.PersonalSkillFactor = skillSpread;
            opp.MaxThrottle = def != null ? Mathf.Clamp(def.MaxThrottle, 0.55f, 1.05f) : 0.90f;
            opp.CornerBraking = def != null ? Mathf.Clamp(def.CornerBraking, 0.45f, 1.0f) : 0.75f;

            if (isCrashCourse)
            {
                int crashEv = MM2RaceManager.Instance != null ? MM2RaceManager.Instance.ActiveCrashEventType : -1;
                float rawTargetMs = (def != null ? Mathf.Clamp(def.TargetSpeed, 30f, 65f) : 50f) * MphToMs;
                if (crashEv == 2 || (def != null && def.BehaviorType == 1))
                {
                    // "Follow That Car!": pace the lead car at ~26.5-28.5 m/s (59-64 MPH) on straights
                    // so it completes the route in ~85-95s while staying below the player cab's 79 MPH (35.3 m/s) top speed.
                    opp.BaseTopSpeed = Mathf.Clamp(rawTargetMs * 1.24f * Mathf.Clamp(opp.MaxThrottle, 0.90f, 1.02f), 26.0f, 28.5f);
                }
                else if (crashEv == 8)
                {
                    // "Stop That Car!": pace the target car at ~24.5-27.0 m/s (55-60 MPH) so the player can catch and ram it.
                    opp.BaseTopSpeed = Mathf.Clamp(rawTargetMs * 1.15f * Mathf.Clamp(opp.MaxThrottle, 0.85f, 1.0f), 24.5f, 27.0f);
                }
                else
                {
                    // Midterm / Final exam opponent car: ~27-30 m/s (60-67 MPH)
                    opp.BaseTopSpeed = Mathf.Clamp(rawTargetMs * 1.28f, 27.0f, 30.0f);
                }
            }
            else
            {
                VehicleSpec oppSpec = VehicleCatalog.GetSpec(pkgName);
                float oppTransHighMph = (oppSpec != null && oppSpec.CarSim != null && oppSpec.CarSim.TransHigh > 30f)
                    ? oppSpec.CarSim.TransHigh
                    : 85f;
                float oppCruiseMs = Mathf.Clamp(oppTransHighMph * MphToMs * 0.94f, 27.0f, 42.0f);

                float playerCruiseMs = 37.0f;
                if (playerTransform != null)
                {
                    ArcadeVehicleController playerCar = playerTransform.GetComponent<ArcadeVehicleController>();
                    if (playerCar != null && playerCar.maxSpeedMph > 35f)
                    {
                        playerCruiseMs = Mathf.Clamp(playerCar.maxSpeedMph * MphToMs * 0.88f, 26.5f, 42.0f);
                    }
                }

                float refTopMs = Mathf.Min(oppCruiseMs, playerCruiseMs * 1.03f);
                float topMult = def != null ? Mathf.Clamp(def.TopSpeedMultiplier, 0.92f, 1.08f) : 1.0f;
                float profileMult = MM2PlayerManager.IsActiveProfilePro() ? 1.0f : 0.95f;
                opp.BaseTopSpeed = Mathf.Clamp(refTopMs * topMult * skillSpread * profileMult, 25.0f, 42.5f);
            }

            // Spread lateral racing line offsets slightly so opponents don't drive single-file;
            // start at 0f on the starting grid so cars don't shift sideways when the countdown ends.
            float[] laneOffsets = { -1.6f, 1.6f, -0.8f, 0.8f, -2.1f, 2.1f, -0.3f, 0.3f };
            opp.PreferredLateralOffset = isCrashCourse ? 0f : laneOffsets[index % laneOffsets.Length];
            opp.CurrentLateralOffset = 0f;

            Transform lightsRoot = oppGo.transform.Find("VehicleLights");
            if (lightsRoot != null)
            {
                Transform hl = lightsRoot.Find("Headlights_Glow");
                Transform bl = lightsRoot.Find("Brakelights_Glow");
                if (hl != null)
                {
                    opp.HeadlightsGlow = hl.gameObject;
                    opp.HeadlightsGlow.SetActive(nightOrBadWeatherLights);
                }
                if (bl != null)
                {
                    opp.BrakelightsGlow = bl.gameObject;
                    opp.BrakelightsGlow.SetActive(true);
                }
            }

            Vector3 finalPos = startPos + Vector3.up * opp.GroundClearanceOffset;
            oppGo.transform.SetPositionAndRotation(finalPos, startRot);
            rb.position = finalPos;
            rb.rotation = startRot;

            MM2VehicleDamage dmg = oppGo.AddComponent<MM2VehicleDamage>();
            dmg.ConfigureForAgent(pkgName, rb.mass, isPlayer: false);
            opp.Damage = dmg;

            return opp;
        }

        /// <summary>
        /// Builds a continuous, street-following 3D route through the .bai road network
        /// that connects the opponent's starting position through all race checkpoints
        /// while staying strictly on the matching vertical road deck (overpass vs underpass).
        /// </summary>
        private void BuildOpponentRoute(MM2OpponentAgent opp, MM2AiMapData.OpponentDefinition def)
        {
            var rawPoints = new List<Vector3>(256);
            rawPoints.Add(opp.StartPosition);

            Vector3 startFwd = opp.StartRotation * Vector3.forward;

            // Build anchor list & forward tangents at each checkpoint (or .opp waypoints in Crash Course)
            var anchors = new List<Vector3>(raceCheckpointPositions.Count + 16);
            var anchorForwards = new List<Vector3>(raceCheckpointPositions.Count + 16);
            var strictSigns = new List<bool>(raceCheckpointPositions.Count + 16);
            anchors.Add(opp.StartPosition);
            anchorForwards.Add(startFwd);
            strictSigns.Add(true);

            if (isCrashCourse && def != null && def.Waypoints != null && def.Waypoints.Count >= 2)
            {
                int firstAhead = 0;
                Vector3 d0 = def.Waypoints[0].Position - opp.StartPosition;
                Vector3 d1 = def.Waypoints[1].Position - opp.StartPosition;
                d0.y = 0f;
                d1.y = 0f;
                if (Vector3.Dot(d0, d1) < 0f || d0.sqrMagnitude < 4f)
                {
                    firstAhead = 1;
                }

                for (int w = firstAhead; w < def.Waypoints.Count; w++)
                {
                    Vector3 wpPos = def.Waypoints[w].Position;
                    Vector3 wpFwd = Vector3.zero;
                    if (w + 1 < def.Waypoints.Count)
                    {
                        Vector3 toNext = def.Waypoints[w + 1].Position - wpPos;
                        toNext.y = 0f;
                        if (toNext.sqrMagnitude > 1f) wpFwd = toNext.normalized;
                    }
                    else if (w > 0)
                    {
                        Vector3 fromPrev = wpPos - def.Waypoints[w - 1].Position;
                        fromPrev.y = 0f;
                        if (fromPrev.sqrMagnitude > 1f) wpFwd = fromPrev.normalized;
                    }
                    anchors.Add(wpPos);
                    anchorForwards.Add(wpFwd);
                    strictSigns.Add(true);
                }

                if (raceCheckpointPositions.Count > 0)
                {
                    Vector3 finalCp = raceCheckpointPositions[raceCheckpointPositions.Count - 1];
                    if ((anchors[anchors.Count - 1] - finalCp).sqrMagnitude > 9f)
                    {
                        anchors.Add(finalCp);
                        Vector3 cpFwd = raceCheckpointRotations.Count > 0
                            ? (raceCheckpointRotations[raceCheckpointRotations.Count - 1] * Vector3.forward)
                            : Vector3.zero;
                        anchorForwards.Add(cpFwd);
                        strictSigns.Add(false);
                    }
                }
            }
            else
            {
                for (int i = 0; i < raceCheckpointPositions.Count; i++)
                {
                    anchors.Add(raceCheckpointPositions[i]);
                    Vector3 cpFwd = (i < raceCheckpointRotations.Count)
                        ? (raceCheckpointRotations[i] * Vector3.forward)
                        : Vector3.zero;
                    anchorForwards.Add(cpFwd);
                    // Checkpoint gate yaw angles in waypoints.csv orient a double-sided arch across the street
                    // and may point either with or 180° opposite to the travel direction.
                    strictSigns.Add(false);
                }
                if (isCircuitRace && raceCheckpointPositions.Count > 1)
                {
                    // Loop back from the last checkpoint to Checkpoint 0 for multi-lap circuits
                    anchors.Add(raceCheckpointPositions[0]);
                    Vector3 cp0Fwd = (raceCheckpointRotations.Count > 0)
                        ? (raceCheckpointRotations[0] * Vector3.forward)
                        : startFwd;
                    anchorForwards.Add(cp0Fwd);
                    strictSigns.Add(false);
                }
            }

            opp.CheckpointRouteDistances = new float[raceCheckpointPositions.Count];

            if (aiMap != null)
            {
                aiMap.BuildRaceRouteThroughAnchors(anchors, anchorForwards, strictSigns, rawPoints);
            }
            else
            {
                for (int a = 1; a < anchors.Count; a++)
                {
                    AppendPointIfDistinct(rawPoints, anchors[a], 1.2f);
                }
            }

            // Strip any accidental single-vertex hairpin spikes before resampling
            for (int pass = 0; pass < 3 && rawPoints.Count >= 3; pass++)
            {
                bool removed = false;
                for (int k = 1; k < rawPoints.Count - 1; k++)
                {
                    Vector3 v1 = rawPoints[k] - rawPoints[k - 1];
                    Vector3 v2 = rawPoints[k + 1] - rawPoints[k];
                    v1.y = 0f;
                    v2.y = 0f;
                    if (v1.sqrMagnitude > 0.01f && v2.sqrMagnitude > 0.01f)
                    {
                        if (Vector3.Dot(v1.normalized, v2.normalized) < -0.5f)
                        {
                            rawPoints.RemoveAt(k);
                            removed = true;
                            break;
                        }
                    }
                }
                if (!removed) break;
            }

            // Resample and smooth sharp corners slightly so opponents carve clean racing arcs
            List<Vector3> smoothedPoints = ResampleAndSmoothPolyline(rawPoints, stepMeters: 4.0f);
            if (smoothedPoints.Count < 2)
            {
                smoothedPoints.Clear();
                smoothedPoints.Add(opp.StartPosition);
                smoothedPoints.AddRange(raceCheckpointPositions);
            }

            // Snap each resampled point to its local road deck surface within a tight vertical window
            // so opponents hug hills/ramps smoothly and never float or jump onto an overpass overhead.
            float prevDeckY = opp.StartPosition.y;
            smoothedPoints[0] = opp.StartPosition;
            for (int i = 1; i < smoothedPoints.Count; i++)
            {
                Vector3 p = smoothedPoints[i];
                float horizStep = Vector2.Distance(
                    new Vector2(smoothedPoints[i - 1].x, smoothedPoints[i - 1].z),
                    new Vector2(p.x, p.z)
                );
                // Clamp maximum vertical step slope before raycasting so no sudden vertical jump can occur
                float maxVertChange = Mathf.Max(1.4f, horizStep * 0.32f);
                p.y = Mathf.Clamp(p.y, prevDeckY - maxVertChange, prevDeckY + maxVertChange);
                p = SnapPointToSameDeck(p, prevDeckY);
                prevDeckY = p.y;
                smoothedPoints[i] = p;
            }

            int n = smoothedPoints.Count;
            opp.RouteNodes.Clear();
            opp.RouteNodes.Capacity = n;

            float cumDist = 0f;
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    cumDist += Vector3.Distance(smoothedPoints[i - 1], smoothedPoints[i]);
                }

                Vector3 fwd;
                if (i == 0)
                {
                    fwd = startFwd;
                }
                else
                {
                    Vector3 prev = smoothedPoints[Mathf.Max(0, i - 1)];
                    Vector3 next = smoothedPoints[Mathf.Min(n - 1, i + 1)];
                    fwd = next - prev;
                    if (fwd.sqrMagnitude < 0.0001f) fwd = startFwd;
                    else fwd.Normalize();

                    if (i <= 2)
                    {
                        fwd = Vector3.Slerp(startFwd, fwd, i / 3f).normalized;
                    }
                }

                Vector3 horizFwd = new Vector3(fwd.x, 0f, fwd.z);
                if (horizFwd.sqrMagnitude < 0.0001f) horizFwd = Vector3.forward;
                horizFwd.Normalize();

                Vector3 right = new Vector3(horizFwd.z, 0f, -horizFwd.x);
                Vector3 up = Vector3.Cross(fwd, right).normalized;
                if (up.y < 0.2f) up = Vector3.up;

                opp.RouteNodes.Add(new OpponentRouteNode
                {
                    Position = smoothedPoints[i],
                    Forward = fwd,
                    Right = right,
                    Up = up,
                    CumulativeDistance = cumDist,
                    SpeedLimit = opp.BaseTopSpeed
                });
            }

            opp.TotalRouteLength = Mathf.Max(1f, cumDist);
            opp.IsCircuitLoop = isCircuitRace;

            // Compute curvature-based corner braking speeds looking 4m..36m ahead along the route
            for (int i = 0; i < n; i++)
            {
                OpponentRouteNode node = opp.RouteNodes[i];
                float maxTurnAngle = 0f;

                for (int look = 1; look <= 9; look++)
                {
                    int j = i + look;
                    if (j >= n)
                    {
                        if (isCircuitRace) j %= n;
                        else break;
                    }
                    float angle = Vector3.Angle(
                        new Vector3(node.Forward.x, 0f, node.Forward.z),
                        new Vector3(opp.RouteNodes[j].Forward.x, 0f, opp.RouteNodes[j].Forward.z)
                    );
                    if (angle > maxTurnAngle) maxTurnAngle = angle;
                }

                float cornerSpeed = opp.BaseTopSpeed;
                if (maxTurnAngle > 12f)
                {
                    float severity = Mathf.Clamp01((maxTurnAngle - 12f) / 76f);
                    float minCornerSpeed = isCrashCourse
                        ? Mathf.Lerp(16.2f, 13.5f, opp.CornerBraking)
                        : Mathf.Lerp(16.8f, 12.2f, opp.CornerBraking);
                    cornerSpeed = Mathf.Lerp(opp.BaseTopSpeed, minCornerSpeed, severity);
                }

                node.SpeedLimit = cornerSpeed;
                opp.RouteNodes[i] = node;
            }

            // Map each race checkpoint to its monotonic distance along the route
            float searchStartDist = 0f;
            for (int cpIdx = 0; cpIdx < raceCheckpointPositions.Count; cpIdx++)
            {
                Vector3 cpPos = raceCheckpointPositions[cpIdx];
                float bestDistSq = float.MaxValue;
                float bestRouteDist = searchStartDist;

                for (int i = 0; i < n; i++)
                {
                    float nodeDist = opp.RouteNodes[i].CumulativeDistance;
                    if (nodeDist + 15f < searchStartDist) continue;

                    float dSq = (opp.RouteNodes[i].Position - cpPos).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestRouteDist = nodeDist;
                    }
                }

                opp.CheckpointRouteDistances[cpIdx] = bestRouteDist;
                searchStartDist = bestRouteDist;
            }
        }

        /// <summary>
        /// Snaps a route point to the road surface on its current vertical deck using a tight
        /// [-2.8m, +2.2m] vertical window so cars under an overpass never raycast onto the
        /// overpass overhead and cars on an overpass never drop down to the street below.
        /// </summary>
        private static Vector3 SnapPointToSameDeck(Vector3 pt, float referenceY)
        {
            Vector3 origin = new Vector3(pt.x, pt.y + 2.2f, pt.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 5.0f, ~0, QueryTriggerInteraction.Ignore);
            float bestDiff = float.MaxValue;
            float bestY = pt.y;
            bool found = false;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || col.attachedRigidbody != null) continue;
                if (hits[i].normal.y < 0.45f) continue;

                float hitY = hits[i].point.y;
                float diff = Mathf.Abs(hitY - referenceY);
                if (diff <= 2.6f && diff < bestDiff)
                {
                    bestDiff = diff;
                    bestY = hitY;
                    found = true;
                }
            }

            if (found)
            {
                pt.y = bestY;
            }
            return pt;
        }

        private static void AppendPointIfDistinct(List<Vector3> list, Vector3 pt, float minDist)
        {
            if (list.Count == 0 || (list[list.Count - 1] - pt).sqrMagnitude >= minDist * minDist)
            {
                list.Add(pt);
            }
        }

        private static List<Vector3> ResampleAndSmoothPolyline(List<Vector3> input, float stepMeters)
        {
            var resampled = new List<Vector3>(input.Count * 3);
            if (input.Count == 0) return resampled;

            resampled.Add(input[0]);
            for (int i = 0; i < input.Count - 1; i++)
            {
                Vector3 a = input[i];
                Vector3 b = input[i + 1];
                float d = Vector3.Distance(a, b);
                int steps = Mathf.Max(1, Mathf.CeilToInt(d / stepMeters));
                for (int s = 1; s <= steps; s++)
                {
                    resampled.Add(Vector3.Lerp(a, b, s / (float)steps));
                }
            }

            // 2-pass Chaikin-style corner smoothing while preserving endpoints
            for (int pass = 0; pass < 2; pass++)
            {
                if (resampled.Count < 4) break;
                var next = new List<Vector3>(resampled.Count);
                next.Add(resampled[0]);
                for (int i = 1; i < resampled.Count - 1; i++)
                {
                    Vector3 smoothed = resampled[i - 1] * 0.22f + resampled[i] * 0.56f + resampled[i + 1] * 0.22f;
                    next.Add(smoothed);
                }
                next.Add(resampled[resampled.Count - 1]);
                resampled = next;
            }

            return resampled;
        }

        private void FixedUpdate()
        {
            if (MM2MenuController.IsMenuOpen || activeOpponents.Count == 0)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;
            bool isCountdownLocked = ArcadeVehicleController.IsInputLocked;

            for (int i = 0; i < activeOpponents.Count; i++)
            {
                MM2OpponentAgent opp = activeOpponents[i];
                if (opp == null) continue;

                if (isCountdownLocked)
                {
                    // Hold stationary on the starting grid during Ready... Set... Go!!
                    opp.CurrentSpeed = 0f;
                    opp.IsBraking = true;
                    UpdateOpponentVisuals(opp, dt);
                    continue;
                }

                if (opp.Damage != null && opp.Damage.IsTotaled)
                {
                    // Totaled opponent: wheels have fallen off; skid to a halt on the road
                    opp.CurrentSpeed = Mathf.MoveTowards(opp.CurrentSpeed, 0f, 18f * dt);
                    opp.IsBraking = false;
                    if (opp.Body != null)
                    {
                        opp.Body.linearVelocity = Vector3.MoveTowards(opp.Body.linearVelocity, Vector3.zero, 18f * dt);
                        opp.Body.angularVelocity = Vector3.zero;
                    }
                    UpdateOpponentVisuals(opp, dt);
                    continue;
                }

                StepOpponentRaceAI(opp, dt);
                UpdateOpponentVisuals(opp, dt);
            }
        }

        private void StepOpponentRaceAI(MM2OpponentAgent opp, float dt)
        {
            if (opp.RouteNodes.Count < 2) return;

            // 1. Sample current route node and lookahead speed limit
            SampleRouteAtDistance(
                opp,
                opp.DistanceAlongLap,
                out Vector3 centerPos,
                out Vector3 routeFwd,
                out Vector3 routeRight,
                out Vector3 routeUp,
                out float routeSpeedLimit
            );

            // 2. Competitive pacing / gentle rubber-banding relative to player
            float rubberBandMult = 1f;
            float throttleScale = isCrashCourse
                ? 1.0f
                : Mathf.Lerp(0.84f, 1.0f, Mathf.InverseLerp(0.55f, 1.0f, opp.MaxThrottle));

            if (isCrashCourse && playerTransform != null && !opp.HasFinished)
            {
                int crashEv = MM2RaceManager.Instance != null ? MM2RaceManager.Instance.ActiveCrashEventType : -1;
                if (crashEv == 2)
                {
                    // "Follow That Car!": regulate distance gently around the sweet spot between Near and Far
                    float distToPlayer = Vector3.Distance(playerTransform.position, opp.transform.position);
                    float nearDist = MM2RaceManager.Instance != null && MM2RaceManager.Instance.ActiveCrashFollowDistNear > 1f
                        ? MM2RaceManager.Instance.ActiveCrashFollowDistNear
                        : 25f;
                    float farDist = MM2RaceManager.Instance != null && MM2RaceManager.Instance.ActiveCrashFollowDistFar > nearDist + 5f
                        ? MM2RaceManager.Instance.ActiveCrashFollowDistFar
                        : 75f;
                    float midDist = 0.5f * (nearDist + farDist);

                    if (distToPlayer > midDist + 10f)
                    {
                        rubberBandMult = Mathf.Lerp(1.0f, 0.90f, Mathf.Clamp01((distToPlayer - (midDist + 10f)) / Mathf.Max(8f, farDist - midDist)));
                    }
                    else if (distToPlayer < nearDist + 8f)
                    {
                        rubberBandMult = Mathf.Lerp(1.08f, 1.0f, Mathf.Clamp01((distToPlayer - nearDist) / 8f));
                    }
                }
            }
            else if (!isCrashCourse && playerTransform != null && !opp.HasFinished)
            {
                Vector3 toPlayer = playerTransform.position - opp.transform.position;
                float projAlongRoute = Vector3.Dot(toPlayer, routeFwd);
                if (projAlongRoute > 35f)
                {
                    // Player is ahead of this opponent -> slight catch-up boost
                    rubberBandMult = Mathf.Lerp(1.0f, 1.14f, Mathf.Clamp01((projAlongRoute - 35f) / 140f));
                }
                else if (projAlongRoute < -45f)
                {
                    // Opponent is far ahead of player -> ease off slightly
                    rubberBandMult = Mathf.Lerp(1.0f, 0.90f, Mathf.Clamp01((-projAlongRoute - 45f) / 150f));
                }
            }

            float desiredSpeed = opp.HasFinished
                ? 12f
                : (routeSpeedLimit * throttleScale * rubberBandMult);

            // 3. Check for slower vehicles ahead and adjust lateral offset to overtake cleanly
            float targetLatOffset = opp.PreferredLateralOffset;
            if (CheckForwardTrafficOrRacer(opp, routeFwd, routeRight, out float obstacleGap, out float obstacleSpeed, out float avoidSideOffset))
            {
                targetLatOffset = Mathf.Clamp(opp.PreferredLateralOffset + avoidSideOffset, -3.2f, 3.2f);
                if (obstacleGap < 7.5f)
                {
                    desiredSpeed = Mathf.Min(desiredSpeed, Mathf.Max(8f, obstacleSpeed));
                }
            }

            opp.CurrentLateralOffset = Mathf.MoveTowards(opp.CurrentLateralOffset, targetLatOffset, 3.2f * dt);

            // 4. Accelerate or brake toward desiredSpeed at realistic vehicle acceleration rates
            opp.IsBraking = opp.CurrentSpeed > desiredSpeed + 1.6f;
            if (opp.CurrentSpeed < desiredSpeed)
            {
                float maxAccel = isCrashCourse ? 6.8f : (7.6f * Mathf.Clamp(opp.MaxThrottle, 0.80f, 1.05f));
                float highSpeedAccel = isCrashCourse ? 4.4f : (4.8f * Mathf.Clamp(opp.MaxThrottle, 0.80f, 1.05f));
                float accel = Mathf.Lerp(maxAccel, highSpeedAccel, Mathf.Clamp01(opp.CurrentSpeed / Mathf.Max(22f, opp.BaseTopSpeed)));
                opp.CurrentSpeed = Mathf.MoveTowards(opp.CurrentSpeed, desiredSpeed, accel * dt);
            }
            else
            {
                float brakeDecel = opp.IsBraking ? 16.0f : 5.8f;
                opp.CurrentSpeed = Mathf.MoveTowards(opp.CurrentSpeed, desiredSpeed, brakeDecel * dt);
            }

            // 5. Advance along route
            opp.DistanceAlongLap += opp.CurrentSpeed * dt;

            // Update checkpoint & lap progression
            UpdateCheckpointProgress(opp);

            if (opp.DistanceAlongLap >= opp.TotalRouteLength)
            {
                if (opp.IsCircuitLoop && !opp.HasFinished)
                {
                    opp.DistanceAlongLap -= opp.TotalRouteLength;
                }
                else
                {
                    opp.DistanceAlongLap = opp.TotalRouteLength;
                    opp.CurrentSpeed = Mathf.MoveTowards(opp.CurrentSpeed, 0f, 12f * dt);
                }
            }

            // 6. Compute final world pose (including decay of any collision perturbation)
            SampleRouteAtDistance(
                opp,
                opp.DistanceAlongLap,
                out centerPos,
                out routeFwd,
                out routeRight,
                out routeUp,
                out _
            );

            if (opp.ImpactRecoveryTimer > 0f)
            {
                opp.ImpactRecoveryTimer -= dt;
                opp.ImpactPerturbationVelocity = Vector3.Lerp(opp.ImpactPerturbationVelocity, Vector3.zero, 5f * dt);
            }
            else
            {
                opp.ImpactPerturbationVelocity = Vector3.zero;
            }

            Vector3 targetPos = centerPos
                + routeRight * opp.CurrentLateralOffset
                + routeUp * opp.GroundClearanceOffset
                + opp.ImpactPerturbationVelocity * dt;

            Quaternion targetRot = Quaternion.LookRotation(routeFwd, routeUp);

            float yawDelta = Mathf.DeltaAngle(opp.transform.eulerAngles.y, targetRot.eulerAngles.y);
            opp.SteerVisualAngle = Mathf.Clamp(yawDelta * 3.5f, -28f, 28f);

            if (opp.Body != null)
            {
                opp.Body.MovePosition(targetPos);
                opp.Body.MoveRotation(Quaternion.Slerp(opp.Body.rotation, targetRot, Mathf.Clamp01(14f * dt)));
                opp.Body.linearVelocity = routeFwd * opp.CurrentSpeed + opp.ImpactPerturbationVelocity;
                opp.Body.angularVelocity = Vector3.zero;
            }
            else
            {
                opp.transform.SetPositionAndRotation(targetPos, targetRot);
            }
        }

        private void UpdateCheckpointProgress(MM2OpponentAgent opp)
        {
            if (opp.HasFinished || raceCheckpointPositions.Count == 0) return;

            int cpCount = raceCheckpointPositions.Count;
            int targetCp = Mathf.Clamp(opp.CurrentCheckpointIndex, 0, cpCount - 1);

            // Check both route distance threshold and 3D proximity to target checkpoint
            float cpRouteDist = (opp.CheckpointRouteDistances != null && targetCp < opp.CheckpointRouteDistances.Length)
                ? opp.CheckpointRouteDistances[targetCp]
                : float.MaxValue;

            Vector3 toCp = raceCheckpointPositions[targetCp] - opp.transform.position;
            toCp.y = 0f;
            float triggerRadius = raceCheckpointRadii[targetCp] + 10f;

            if (opp.DistanceAlongLap >= cpRouteDist - 4f || toCp.sqrMagnitude <= triggerRadius * triggerRadius)
            {
                opp.CurrentCheckpointIndex++;
                if (opp.CurrentCheckpointIndex >= cpCount)
                {
                    if (isCircuitRace && opp.CurrentLap < totalLaps)
                    {
                        opp.CurrentLap++;
                        opp.CurrentCheckpointIndex = 0;
                    }
                    else
                    {
                        opp.HasFinished = true;
                        opp.CurrentCheckpointIndex = cpCount;
                        if (MM2RaceManager.Instance != null)
                        {
                            opp.FinishTime = MM2RaceManager.Instance.ElapsedTime;
                        }
                    }
                }
            }
        }

        private bool CheckForwardTrafficOrRacer(
            MM2OpponentAgent opp,
            Vector3 routeFwd,
            Vector3 routeRight,
            out float minGap,
            out float obstacleSpeed,
            out float avoidSideOffset)
        {
            minGap = 999f;
            obstacleSpeed = opp.BaseTopSpeed;
            avoidSideOffset = 0f;

            Vector3 myPos = opp.transform.position;

            // Check ambient traffic ahead
            if (MM2TrafficManager.Instance != null)
            {
                var ambients = MM2TrafficManager.Instance.ActiveAgents;
                for (int i = 0; i < ambients.Count; i++)
                {
                    var amb = ambients[i];
                    if (amb == null) continue;
                    Vector3 toAmb = amb.transform.position - myPos;
                    if (toAmb.sqrMagnitude > 28f * 28f) continue;

                    float fwd = Vector3.Dot(toAmb, routeFwd);
                    if (fwd <= 0f || fwd > 26f) continue;

                    float lat = Vector3.Dot(toAmb, routeRight);
                    if (Mathf.Abs(lat) < (opp.HalfWidth + amb.HalfWidth + 0.8f))
                    {
                        float gap = fwd - (opp.HalfLength + amb.HalfLength);
                        if (gap < minGap)
                        {
                            minGap = gap;
                            obstacleSpeed = amb.CurrentSpeed;
                            avoidSideOffset = (lat >= 0f) ? -2.4f : 2.4f;
                        }
                    }
                }
            }

            // Check other opponents ahead
            for (int i = 0; i < activeOpponents.Count; i++)
            {
                var other = activeOpponents[i];
                if (other == null || other == opp) continue;
                Vector3 toOther = other.transform.position - myPos;
                if (toOther.sqrMagnitude > 22f * 22f) continue;

                float fwd = Vector3.Dot(toOther, routeFwd);
                if (fwd <= 0f || fwd > 20f) continue;

                float lat = Vector3.Dot(toOther, routeRight);
                if (Mathf.Abs(lat) < (opp.HalfWidth + other.HalfWidth + 0.6f))
                {
                    float gap = fwd - (opp.HalfLength + other.HalfLength);
                    if (gap < minGap)
                    {
                        minGap = gap;
                        obstacleSpeed = other.CurrentSpeed;
                        avoidSideOffset = (lat >= 0f) ? -2.0f : 2.0f;
                    }
                }
            }

            return minGap < 25f;
        }

        private static void SampleRouteAtDistance(
            MM2OpponentAgent opp,
            float distance,
            out Vector3 pos,
            out Vector3 fwd,
            out Vector3 right,
            out Vector3 up,
            out float speedLimit)
        {
            var nodes = opp.RouteNodes;
            int n = nodes.Count;
            float d = Mathf.Clamp(distance, 0f, opp.TotalRouteLength);

            int lo = 0;
            int hi = n - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) >> 1;
                if (nodes[mid].CumulativeDistance <= d) lo = mid;
                else hi = mid;
            }

            OpponentRouteNode n0 = nodes[lo];
            OpponentRouteNode n1 = nodes[hi];
            float segLen = Mathf.Max(0.001f, n1.CumulativeDistance - n0.CumulativeDistance);
            float t = Mathf.Clamp01((d - n0.CumulativeDistance) / segLen);

            pos = Vector3.Lerp(n0.Position, n1.Position, t);
            fwd = Vector3.Slerp(n0.Forward, n1.Forward, t).normalized;
            right = Vector3.Slerp(n0.Right, n1.Right, t).normalized;
            up = Vector3.Slerp(n0.Up, n1.Up, t).normalized;
            speedLimit = Mathf.Lerp(n0.SpeedLimit, n1.SpeedLimit, t);
        }

        private void UpdateOpponentVisuals(MM2OpponentAgent opp, float dt)
        {
            if (opp.Damage == null || !opp.Damage.IsTotaled)
            {
                if (opp.WheelRadius > 0.05f)
                {
                    float angularDeg = (opp.CurrentSpeed / opp.WheelRadius) * Mathf.Rad2Deg * dt;
                    opp.WheelSpinAngle = (opp.WheelSpinAngle + angularDeg) % 360f;
                }

                Quaternion spinRot = Quaternion.Euler(opp.WheelSpinAngle, 0f, 0f);
                Quaternion steerRot = Quaternion.Euler(0f, opp.SteerVisualAngle, 0f) * spinRot;

                if (opp.WheelFL != null) opp.WheelFL.localRotation = steerRot;
                if (opp.WheelFR != null) opp.WheelFR.localRotation = steerRot;
                if (opp.WheelRL != null) opp.WheelRL.localRotation = spinRot;
                if (opp.WheelRR != null) opp.WheelRR.localRotation = spinRot;
            }

            if (opp.BrakelightsGlow != null && opp.BrakelightsGlow.activeSelf != opp.IsBraking)
            {
                opp.BrakelightsGlow.SetActive(opp.IsBraking);
            }
        }

        /// <summary>
        /// Computes the player's current race standing (1 = 1st place .. N = last place)
        /// by comparing each opponent's lap, checkpoint index, and distance to next checkpoint
        /// against the player's progress.
        /// </summary>
        public int ComputePlayerRacePosition(
            int playerLap,
            int playerCheckpointIndex,
            Vector3 playerPosition,
            bool playerHasFinished,
            float playerFinishTime,
            out int totalRacers)
        {
            totalRacers = 1 + activeOpponents.Count;
            if (activeOpponents.Count == 0 || raceCheckpointPositions.Count == 0)
            {
                return 1;
            }

            int cpCount = raceCheckpointPositions.Count;
            int clampedPlayerCp = Mathf.Clamp(playerCheckpointIndex, 0, cpCount - 1);
            float playerDistToNextCp = Vector3.Distance(playerPosition, raceCheckpointPositions[clampedPlayerCp]);
            float playerScore = playerLap * 1000000f + playerCheckpointIndex * 10000f - playerDistToNextCp;

            int position = 1;
            for (int i = 0; i < activeOpponents.Count; i++)
            {
                MM2OpponentAgent opp = activeOpponents[i];
                if (opp == null) continue;

                if (playerHasFinished && opp.HasFinished)
                {
                    if (opp.FinishTime < playerFinishTime) position++;
                    continue;
                }
                if (opp.HasFinished && !playerHasFinished)
                {
                    position++;
                    continue;
                }
                if (playerHasFinished && !opp.HasFinished)
                {
                    continue;
                }

                int clampedOppCp = Mathf.Clamp(opp.CurrentCheckpointIndex, 0, cpCount - 1);
                float oppDistToNextCp = Vector3.Distance(opp.transform.position, raceCheckpointPositions[clampedOppCp]);
                float oppScore = opp.CurrentLap * 1000000f + opp.CurrentCheckpointIndex * 10000f - oppDistToNextCp;

                if (oppScore > playerScore)
                {
                    position++;
                }
            }

            return position;
        }
    }
}
