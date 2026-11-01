using System;
using System.Collections.Generic;
using UnityEngine;
using MM2.Archive;
using MM2.UI;
using MM2.Vehicle;

namespace MM2.AI
{
    public enum MM2PoliceState
    {
        StationaryWatch,
        Patrol,
        Pursuit
    }

    public class MM2PoliceAgent : MonoBehaviour
    {
        public string PkgName = "vpcop";
        public MM2PoliceState State = MM2PoliceState.StationaryWatch;
        public Rigidbody Body;
        public BoxCollider MainCollider;
        public MM2VehicleDamage Damage;

        public Transform WheelFL;
        public Transform WheelFR;
        public Transform WheelRL;
        public Transform WheelRR;
        public float WheelRadius = 0.34f;
        public float HalfLength = 2.35f;
        public float HalfWidth = 0.95f;
        public float GroundClearanceOffset = 0.02f;

        public GameObject HeadlightsGlow;
        public GameObject BrakelightsGlow;
        public GameObject ReverselightsGlow;
        public GameObject Siren0Glow;
        public GameObject Siren1Glow;
        public Light RedRoofLight;
        public Light BlueRoofLight;

        public AudioSource SirenSource;
        public AudioClip WailClip;
        public AudioClip YelpClip;

        // Spawn / config
        public bool IsFixedRaceSpawn;
        public Vector3 HomePosition;
        public Quaternion HomeRotation;
        public float MaxPursuitSpeed = 46f;
        public float DetectRadius = 72f;

        // Road-following state (used in Patrol and long-range Pursuit)
        public MM2AiMapData.BaiDirectedRoad CurrentRoad;
        public int CurrentLane;
        public float DistanceAlongRoad;
        public List<MM2AiMapData.BaiDirectedRoad> PlannedRoute = new List<MM2AiMapData.BaiDirectedRoad>();
        public int PlannedRouteIndex;
        public float RouteReplanTimer;

        // Pursuit & physics state
        public Transform TargetTransform;
        public Rigidbody TargetRigidbody;
        public float CurrentSpeed;
        public float SteerAngleDeg;
        public float WheelSpinAngle;
        public float PursuitLostTimer;
        public float StuckTimer;
        public float ReverseRecoveryTimer;
        public float ReverseSteerSign = 1f;
        public bool IsBraking;

        private void OnCollisionEnter(Collision collision)
        {
            if (collision == null || collision.rigidbody == null) return;
            if (collision.relativeVelocity.sqrMagnitude < 4f) return;

            // Any vehicle hitting a cop immediately triggers pursuit of that vehicle!
            if (State != MM2PoliceState.Pursuit)
            {
                State = MM2PoliceState.Pursuit;
                TargetTransform = collision.rigidbody.transform;
                TargetRigidbody = collision.rigidbody;
                PursuitLostTimer = 0f;
            }
        }
    }

    /// <summary>
    /// Spawns and simulates Midtown Madness 2 Police Cruisers (vpcop) with SFPD / London Met
    /// liveries, patrol &amp; pursuit AI, flashing roof lightbars (SIREN0_L / SIREN1_L + point lights),
    /// and 3D wail/yelp sirens. Scaled by MM2GameSession.CopDensity.
    /// </summary>
    public class MM2PoliceManager : MonoBehaviour
    {
        public static MM2PoliceManager Instance { get; private set; }

        private MM2AiMapData aiMap;
        private Transform playerTransform;
        private Rigidbody playerRigidbody;
        private Transform copsContainer;
        private PhysicsMaterial copPhysMat;

        private readonly List<MM2PoliceAgent> activeCops = new List<MM2PoliceAgent>();
        private readonly System.Random rng = new System.Random(19991024);

        private AudioClip wailSirenClip;
        private AudioClip yelpSirenClip;
        private bool isLondon;
        private bool nightOrBadWeatherLights;

        public IReadOnlyList<MM2PoliceAgent> ActiveCops => activeCops;

        public bool IsPlayerPursued
        {
            get
            {
                for (int i = 0; i < activeCops.Count; i++)
                {
                    var cop = activeCops[i];
                    if (cop != null && cop.State == MM2PoliceState.Pursuit && cop.TargetTransform == playerTransform)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public bool AreAnyCopsPursuingWithinDistance(float maxDistance)
        {
            if (playerTransform == null) return false;
            float maxSq = maxDistance * maxDistance;
            for (int i = 0; i < activeCops.Count; i++)
            {
                var cop = activeCops[i];
                if (cop == null || (cop.Damage != null && cop.Damage.IsTotaled)) continue;
                if (cop.State == MM2PoliceState.Pursuit && cop.TargetTransform == playerTransform)
                {
                    if ((cop.transform.position - playerTransform.position).sqrMagnitude <= maxSq)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Initialize(MM2AiMapData mapData, Transform player)
        {
            ClearCops();

            aiMap = mapData;
            playerTransform = player;
            playerRigidbody = player != null ? player.GetComponent<Rigidbody>() : null;

            bool isCrashCourse = string.Equals(MM2GameSession.SelectedGameMode, "CrashCourse", StringComparison.OrdinalIgnoreCase);
            bool hasFixedSpawns = aiMap != null && aiMap.PoliceSpawns.Count > 0;
            if (aiMap == null || MM2GameSession.CopDensity <= 0)
            {
                return;
            }

            isLondon = string.Equals(aiMap.CityName, "london", StringComparison.OrdinalIgnoreCase) || aiMap.DriveOnLeft;
            nightOrBadWeatherLights = MM2GameSession.TimeOfDayIndex >= 2 || MM2GameSession.WeatherIndex >= 2;

            if (copPhysMat == null)
            {
                copPhysMat = new PhysicsMaterial("MM2_Police_PhysMat")
                {
                    dynamicFriction = 0.2f,
                    staticFriction = 0.2f,
                    bounciness = 0.1f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounceCombine = PhysicsMaterialCombine.Average
                };
            }

            if (copsContainer == null)
            {
                GameObject root = new GameObject("MM2_PoliceCruisers");
                root.transform.SetParent(transform, false);
                copsContainer = root.transform;
            }

            LoadSirenAudioClips();

            float density01 = isCrashCourse && hasFixedSpawns
                ? 1f
                : Mathf.Clamp01(MM2GameSession.CopDensity / 100f);

            float playerTopSpeedMs = 36f;
            if (playerTransform != null)
            {
                ArcadeVehicleController playerCar = playerTransform.GetComponent<ArcadeVehicleController>();
                if (playerCar != null && playerCar.maxSpeedMph > 35f)
                {
                    playerTopSpeedMs = playerCar.maxSpeedMph * 0.44704f;
                }
            }
            float targetPursuitSpeed = isCrashCourse
                ? Mathf.Clamp(playerTopSpeedMs * 0.80f, 24.5f, 28.5f)
                : Mathf.Clamp(playerTopSpeedMs * 0.92f, 28.5f, 36.5f);

            // 1. Spawn race-specific [Police] placements from <race>.aimap
            if (aiMap.PoliceSpawns.Count > 0)
            {
                int maxRaceCops = Mathf.Clamp(Mathf.CeilToInt(aiMap.PoliceSpawns.Count * density01), 1, aiMap.PoliceSpawns.Count);
                for (int i = 0; i < maxRaceCops; i++)
                {
                    var def = aiMap.PoliceSpawns[i];
                    MM2PoliceAgent cop = CreatePoliceAgent(def.PkgName);
                    if (cop == null) continue;

                    cop.IsFixedRaceSpawn = true;
                    Vector3 snappedHomePos = SnapPolicePositionToRoad(def.Position, def.Rotation * Vector3.forward, out Vector3 roadUp);
                    Vector3 flatFwd = def.Rotation * Vector3.forward;
                    flatFwd.y = 0f;
                    if (flatFwd.sqrMagnitude < 0.001f) flatFwd = Vector3.forward;
                    flatFwd.Normalize();
                    Vector3 alignedFwd = Vector3.ProjectOnPlane(flatFwd, roadUp).normalized;
                    Quaternion alignedRot = Quaternion.LookRotation(alignedFwd.sqrMagnitude > 0.001f ? alignedFwd : flatFwd, roadUp);

                    cop.HomePosition = snappedHomePos;
                    cop.HomeRotation = alignedRot;
                    cop.MaxPursuitSpeed = targetPursuitSpeed;
                    cop.DetectRadius = Mathf.Clamp(def.DetectRadius, 48f, 110f);
                    cop.State = MM2PoliceState.StationaryWatch;

                    Vector3 spawnPos = cop.HomePosition + roadUp * cop.GroundClearanceOffset;
                    cop.transform.SetPositionAndRotation(spawnPos, cop.HomeRotation);
                    if (cop.Body != null)
                    {
                        cop.Body.position = spawnPos;
                        cop.Body.rotation = cop.HomeRotation;
                        cop.Body.linearVelocity = Vector3.zero;
                        cop.Body.angularVelocity = Vector3.zero;
                    }

                    activeCops.Add(cop);
                }
            }

            // 2. Also spawn ambient patrolling police cruisers on the city road graph
            // (especially important in Cruise mode or races without [Police] entries)
            int patrolCopTarget = isCrashCourse
                ? 0
                : (aiMap.PoliceSpawns.Count == 0
                    ? Mathf.Clamp(Mathf.CeilToInt(Mathf.Lerp(1f, 4f, density01)), 1, 4)
                    : (density01 >= 0.5f ? 1 : 0));

            for (int i = 0; i < patrolCopTarget; i++)
            {
                MM2PoliceAgent patrolCop = CreatePoliceAgent("vpcop");
                if (patrolCop == null) continue;

                patrolCop.IsFixedRaceSpawn = false;
                patrolCop.MaxPursuitSpeed = targetPursuitSpeed;
                patrolCop.DetectRadius = 72f;
                patrolCop.State = MM2PoliceState.Patrol;

                if (TryPlacePatrolCopOnRoad(patrolCop, initialSpawn: true))
                {
                    activeCops.Add(patrolCop);
                }
                else
                {
                    Destroy(patrolCop.gameObject);
                }
            }

            Debug.Log($"[MM2PoliceManager] Initialized with CopDensity={MM2GameSession.CopDensity}% -> spawned {activeCops.Count} police cruisers.");
        }

        public void ClearCops()
        {
            for (int i = 0; i < activeCops.Count; i++)
            {
                if (activeCops[i] != null && activeCops[i].gameObject != null)
                {
                    Destroy(activeCops[i].gameObject);
                }
            }
            activeCops.Clear();
        }

        public void ResetAllCopsToStart()
        {
            for (int i = 0; i < activeCops.Count; i++)
            {
                MM2PoliceAgent cop = activeCops[i];
                if (cop == null) continue;

                SetSirenActive(cop, false);
                cop.CurrentSpeed = 0f;
                cop.SteerAngleDeg = 0f;
                cop.PursuitLostTimer = 0f;
                cop.StuckTimer = 0f;
                cop.ReverseRecoveryTimer = 0f;
                cop.TargetTransform = null;
                cop.TargetRigidbody = null;
                cop.PlannedRoute.Clear();

                if (cop.Damage != null)
                {
                    cop.Damage.ResetDamage();
                }

                if (cop.IsFixedRaceSpawn)
                {
                    cop.State = MM2PoliceState.StationaryWatch;
                    Vector3 pos = cop.HomePosition + Vector3.up * cop.GroundClearanceOffset;
                    cop.transform.SetPositionAndRotation(pos, cop.HomeRotation);
                    if (cop.Body != null)
                    {
                        cop.Body.position = pos;
                        cop.Body.rotation = cop.HomeRotation;
                        cop.Body.linearVelocity = Vector3.zero;
                        cop.Body.angularVelocity = Vector3.zero;
                    }
                }
                else
                {
                    cop.State = MM2PoliceState.Patrol;
                    TryPlacePatrolCopOnRoad(cop, initialSpawn: true);
                }
            }
        }

        private void LoadSirenAudioClips()
        {
            if (isLondon)
            {
                wailSirenClip = MM2RuntimeDecoders.LoadAudioClip("Siren_London.11k");
                if (wailSirenClip == null) wailSirenClip = MM2RuntimeDecoders.LoadAudioClip("police2hilowloop.11k");

                yelpSirenClip = MM2RuntimeDecoders.LoadAudioClip("Siren_London2.11k");
                if (yelpSirenClip == null) yelpSirenClip = MM2RuntimeDecoders.LoadAudioClip("police1fastsirenloop.11k");
            }
            else
            {
                wailSirenClip = MM2RuntimeDecoders.LoadAudioClip("police1whailerloop.11k");
                if (wailSirenClip == null) wailSirenClip = MM2RuntimeDecoders.LoadAudioClip("police2sirenloop.11k");

                yelpSirenClip = MM2RuntimeDecoders.LoadAudioClip("police1fastsirenloop.11k");
            }

            if (wailSirenClip == null)
            {
                wailSirenClip = CreateSynthesizedSirenClip("Synthesized_Siren_Wail", isYelp: false);
            }
            if (yelpSirenClip == null)
            {
                yelpSirenClip = CreateSynthesizedSirenClip("Synthesized_Siren_Yelp", isYelp: true);
            }
        }

        private static AudioClip CreateSynthesizedSirenClip(string name, bool isYelp)
        {
            int sampleRate = 22050;
            float duration = isYelp ? 0.7f : 3.2f;
            int count = Mathf.RoundToInt(sampleRate * duration);
            float[] samples = new float[count];
            float phase = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)sampleRate;
                float lfo = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * t / duration);
                float freq = Mathf.Lerp(620f, 1280f, lfo);
                phase += 2f * Mathf.PI * freq / sampleRate;
                if (phase > 2f * Mathf.PI) phase -= 2f * Mathf.PI;
                samples[i] = Mathf.Clamp(Mathf.Sin(phase) * 0.45f + Mathf.Sin(phase * 2f) * 0.15f, -0.6f, 0.6f);
            }

            AudioClip clip = AudioClip.Create(name, count, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private void FixedUpdate()
        {
            if (MM2MenuController.IsMenuOpen)
            {
                for (int i = 0; i < activeCops.Count; i++)
                {
                    var cop = activeCops[i];
                    if (cop != null && cop.SirenSource != null && cop.SirenSource.isPlaying)
                    {
                        cop.SirenSource.Pause();
                    }
                }
                return;
            }

            if (aiMap == null || playerTransform == null)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;
            float worldTime = Time.time;

            for (int i = 0; i < activeCops.Count; i++)
            {
                MM2PoliceAgent cop = activeCops[i];
                if (cop == null) continue;

                if (cop.Damage != null && cop.Damage.IsTotaled)
                {
                    SetSirenActive(cop, false);
                    cop.CurrentSpeed = Mathf.MoveTowards(cop.CurrentSpeed, 0f, 18f * dt);
                    cop.IsBraking = false;
                    if (cop.Body != null)
                    {
                        cop.Body.linearVelocity = Vector3.MoveTowards(cop.Body.linearVelocity, Vector3.zero, 18f * dt);
                        cop.Body.angularVelocity = Vector3.zero;
                    }
                    UpdateCopVisualsAndSiren(cop, dt, worldTime);
                    continue;
                }

                switch (cop.State)
                {
                    case MM2PoliceState.StationaryWatch:
                        StepStationaryWatch(cop, dt);
                        break;
                    case MM2PoliceState.Patrol:
                        StepPatrol(cop, dt);
                        break;
                    case MM2PoliceState.Pursuit:
                        StepPursuit(cop, dt);
                        break;
                }

                UpdateCopVisualsAndSiren(cop, dt, worldTime);
            }
        }

        private void StepStationaryWatch(MM2PoliceAgent cop, float dt)
        {
            cop.IsBraking = true;
            cop.CurrentSpeed = 0f;

            if (CheckForPursuitTarget(cop, out Transform target, out Rigidbody targetRb))
            {
                BeginPursuit(cop, target, targetRb);
            }
        }

        private void StepPatrol(MM2PoliceAgent cop, float dt)
        {
            // Recycle patrol cop if too far from player
            float distSqToPlayer = (cop.transform.position - playerTransform.position).sqrMagnitude;
            if (distSqToPlayer > 235f * 235f)
            {
                TryPlacePatrolCopOnRoad(cop, initialSpawn: false);
                return;
            }

            if (CheckForPursuitTarget(cop, out Transform target, out Rigidbody targetRb))
            {
                BeginPursuit(cop, target, targetRb);
                return;
            }

            if (cop.CurrentRoad == null)
            {
                TryPlacePatrolCopOnRoad(cop, initialSpawn: false);
                return;
            }

            // Cruise along CurrentRoad at road speed limit
            float desiredSpeed = cop.CurrentRoad.SpeedLimit;
            cop.IsBraking = false;
            cop.CurrentSpeed = Mathf.MoveTowards(cop.CurrentSpeed, desiredSpeed, 6f * dt);
            cop.DistanceAlongRoad += cop.CurrentSpeed * dt;

            if (cop.DistanceAlongRoad >= cop.CurrentRoad.TotalLength)
            {
                float over = cop.DistanceAlongRoad - cop.CurrentRoad.TotalLength;
                MM2AiMapData.BaiDirectedRoad next = aiMap.ChooseNextOutgoingRoad(cop.CurrentRoad, rng);
                if (next == null)
                {
                    TryPlacePatrolCopOnRoad(cop, initialSpawn: false);
                    return;
                }
                cop.CurrentRoad = next;
                cop.CurrentLane = Mathf.Clamp(cop.CurrentLane, 0, Mathf.Max(0, next.LaneCount - 1));
                cop.DistanceAlongRoad = Mathf.Min(over, next.TotalLength * 0.5f);
            }

            cop.CurrentRoad.SampleLanePose(cop.CurrentLane, cop.DistanceAlongRoad, out Vector3 targetPos, out Vector3 targetFwd, out Vector3 targetUp);
            if (targetUp.y < 0.5f) targetUp = Vector3.up;
            Vector3 flatPatrolFwd = new Vector3(targetFwd.x, 0f, targetFwd.z);
            if (flatPatrolFwd.sqrMagnitude < 0.0001f) flatPatrolFwd = Vector3.forward;
            flatPatrolFwd.Normalize();
            Vector3 levelRight = new Vector3(flatPatrolFwd.z, 0f, -flatPatrolFwd.x);
            Vector3 cleanUp = Vector3.Cross(targetFwd.normalized, levelRight).normalized;
            if (cleanUp.y < 0.5f) cleanUp = Vector3.up;

            targetPos += cleanUp * cop.GroundClearanceOffset;
            Quaternion targetRot = Quaternion.LookRotation(targetFwd, cleanUp);

            float yawDelta = Mathf.DeltaAngle(cop.transform.eulerAngles.y, targetRot.eulerAngles.y);
            cop.SteerAngleDeg = Mathf.Clamp(yawDelta * 3f, -26f, 26f);

            if (cop.Body != null)
            {
                cop.Body.MovePosition(targetPos);
                cop.Body.MoveRotation(Quaternion.Slerp(cop.Body.rotation, targetRot, Mathf.Clamp01(12f * dt)));
                cop.Body.linearVelocity = targetFwd * cop.CurrentSpeed;
                cop.Body.angularVelocity = Vector3.zero;
            }
        }

        private bool CheckForPursuitTarget(MM2PoliceAgent cop, out Transform triggeredTarget, out Rigidbody triggeredRb)
        {
            triggeredTarget = null;
            triggeredRb = null;

            if (ArcadeVehicleController.IsInputLocked) return false;

            Vector3 copPos = cop.transform.position;
            float detectRadiusSq = cop.DetectRadius * cop.DetectRadius;

            // 1. Check Player
            if (playerTransform != null)
            {
                float dSq = (playerTransform.position - copPos).sqrMagnitude;
                if (dSq <= detectRadiusSq)
                {
                    float playerSpeed = playerRigidbody != null ? playerRigidbody.linearVelocity.magnitude : 0f;
                    bool isCrashCourse = string.Equals(MM2GameSession.SelectedGameMode, "CrashCourse", StringComparison.OrdinalIgnoreCase);
                    bool isRaceMode = !string.Equals(MM2GameSession.SelectedGameMode, "Cruise", StringComparison.OrdinalIgnoreCase);
                    float speedThreshold = isCrashCourse ? 1.0f : (isRaceMode ? 11.5f : (aiMap.DefaultSpeedLimit + 3.0f));

                    if (playerSpeed >= speedThreshold || (dSq < 18f * 18f && playerSpeed > 7f))
                    {
                        triggeredTarget = playerTransform;
                        triggeredRb = playerRigidbody;
                        return true;
                    }
                }
            }

            // 2. Check Opponents (in races, cops also chase speeding AI opponents!)
            if (MM2OpponentManager.Instance != null)
            {
                var opponents = MM2OpponentManager.Instance.ActiveOpponents;
                for (int i = 0; i < opponents.Count; i++)
                {
                    var opp = opponents[i];
                    if (opp == null) continue;
                    float dSq = (opp.transform.position - copPos).sqrMagnitude;
                    if (dSq <= detectRadiusSq * 0.75f && opp.CurrentSpeed > 15f)
                    {
                        triggeredTarget = opp.transform;
                        triggeredRb = opp.Body;
                        return true;
                    }
                }
            }

            return false;
        }

        private void BeginPursuit(MM2PoliceAgent cop, Transform target, Rigidbody targetRb)
        {
            cop.State = MM2PoliceState.Pursuit;
            cop.TargetTransform = target;
            cop.TargetRigidbody = targetRb;
            cop.PursuitLostTimer = 0f;
            cop.StuckTimer = 0f;
            cop.ReverseRecoveryTimer = 0f;
            cop.RouteReplanTimer = 0f;
            SetSirenActive(cop, true);

            if (cop.Body != null)
            {
                cop.Body.useGravity = false;
                cop.Body.constraints = RigidbodyConstraints.None;
                cop.Body.angularVelocity = Vector3.zero;
            }
        }

        private void StepPursuit(MM2PoliceAgent cop, float dt)
        {
            if (cop.TargetTransform == null)
            {
                cop.TargetTransform = playerTransform;
                cop.TargetRigidbody = playerRigidbody;
            }
            if (cop.TargetTransform == null)
            {
                EndPursuit(cop);
                return;
            }

            Vector3 copPos = cop.transform.position;
            Vector3 targetPos = cop.TargetTransform.position;
            Vector3 toTarget = targetPos - copPos;
            float distToTarget = toTarget.magnitude;

            // Check if target has escaped far away for an extended period
            if (distToTarget > 235f)
            {
                cop.PursuitLostTimer += dt;
                if (cop.PursuitLostTimer > 8f)
                {
                    EndPursuit(cop);
                    return;
                }
            }
            else
            {
                cop.PursuitLostTimer = Mathf.Max(0f, cop.PursuitLostTimer - dt);
            }

            // Determine pursuit waypoint:
            // When within 48m, aim directly for an intercept point ahead of the target.
            // When farther than 48m, route along the .bai road network toward the target's nearest intersection.
            Vector3 steerGoal = targetPos;
            float desiredSpeed = cop.MaxPursuitSpeed;

            if (distToTarget <= 48f)
            {
                Vector3 targetVel = cop.TargetRigidbody != null ? cop.TargetRigidbody.linearVelocity : Vector3.zero;
                float leadTime = Mathf.Clamp(distToTarget / Mathf.Max(15f, cop.MaxPursuitSpeed), 0.1f, 0.85f);
                steerGoal = targetPos + targetVel * leadTime;
            }
            else if (aiMap.DirectedRoads.Count > 0)
            {
                cop.RouteReplanTimer -= dt;
                if (cop.RouteReplanTimer <= 0f || cop.CurrentRoad == null)
                {
                    cop.RouteReplanTimer = 1.1f;
                    UpdatePursuitRoadRoute(cop, copPos, targetPos);
                }

                if (cop.CurrentRoad != null)
                {
                    float lookAheadDist = Mathf.Min(cop.CurrentRoad.TotalLength, cop.DistanceAlongRoad + Mathf.Max(14f, cop.CurrentSpeed * 0.65f));
                    cop.CurrentRoad.SampleLanePose(0, lookAheadDist, out steerGoal, out Vector3 roadFwd, out _);

                    // Advance along CurrentRoad as cop approaches the lookahead point
                    Vector3 toEnd = cop.CurrentRoad.EndPosition - copPos;
                    toEnd.y = 0f;
                    if (toEnd.sqrMagnitude < 16f * 16f && cop.PlannedRouteIndex < cop.PlannedRoute.Count)
                    {
                        cop.CurrentRoad = cop.PlannedRoute[cop.PlannedRouteIndex++];
                        cop.DistanceAlongRoad = 0f;
                    }
                    else
                    {
                        cop.DistanceAlongRoad = Mathf.MoveTowards(cop.DistanceAlongRoad, cop.CurrentRoad.TotalLength, cop.CurrentSpeed * dt);
                    }

                    // Slow down slightly before sharp intersection turns
                    if (cop.PlannedRouteIndex < cop.PlannedRoute.Count)
                    {
                        var nextRoad = cop.PlannedRoute[cop.PlannedRouteIndex];
                        float turnDot = Vector3.Dot(cop.CurrentRoad.EndTangent, nextRoad.StartTangent);
                        if (turnDot < 0.55f && toEnd.sqrMagnitude < 32f * 32f)
                        {
                            desiredSpeed = Mathf.Min(desiredSpeed, 21f);
                        }
                    }
                }
            }

            DrivePhysicsTowardGoal(cop, steerGoal, desiredSpeed, dt);
        }

        private void UpdatePursuitRoadRoute(MM2PoliceAgent cop, Vector3 copPos, Vector3 targetPos)
        {
            MM2AiMapData.BaiDirectedRoad nearestRoad = aiMap.FindNearestRoad(copPos, cop.transform.forward, out int lane, out float distAlong);
            if (nearestRoad == null) return;

            cop.CurrentRoad = nearestRoad;
            cop.CurrentLane = lane;
            cop.DistanceAlongRoad = distAlong;

            MM2AiMapData.BaiIntersection goalInt = aiMap.FindNearestIntersection(targetPos);
            if (goalInt != null)
            {
                cop.PlannedRoute = aiMap.FindShortestRoadRoute(nearestRoad.EndIntersectionId, goalInt.Id);
                cop.PlannedRouteIndex = 0;
            }
        }

        private void DrivePhysicsTowardGoal(MM2PoliceAgent cop, Vector3 goalPos, float desiredSpeed, float dt)
        {
            if (cop.Body == null) return;

            Transform tr = cop.transform;
            Vector3 pos = tr.position;
            Vector3 horizFwd = new Vector3(tr.forward.x, 0f, tr.forward.z);
            if (horizFwd.sqrMagnitude < 0.0001f) horizFwd = Vector3.forward;
            horizFwd.Normalize();

            // Stuck detection & automatic reverse recovery
            if (cop.ReverseRecoveryTimer > 0f)
            {
                cop.ReverseRecoveryTimer -= dt;
                cop.IsBraking = false;
                if (cop.ReverselightsGlow != null && !cop.ReverselightsGlow.activeSelf)
                {
                    cop.ReverselightsGlow.SetActive(true);
                }

                cop.SteerAngleDeg = 26f * cop.ReverseSteerSign;
                cop.CurrentSpeed = Mathf.MoveTowards(cop.CurrentSpeed, -10.5f, 16f * dt);
                float revYaw = tr.eulerAngles.y - cop.ReverseSteerSign * 65f * dt;
                Vector3 revHorizFwd = Quaternion.Euler(0f, revYaw, 0f) * Vector3.forward;
                Vector3 revNextPos = pos + revHorizFwd * (cop.CurrentSpeed * dt);

                SnapCopToRoadSurface(cop, revNextPos, pos.y, out float revGroundY, out Vector3 revGroundUp);
                revNextPos.y = Mathf.MoveTowards(pos.y, revGroundY + cop.GroundClearanceOffset, 12f * dt);

                Vector3 revAlignedFwd = Vector3.ProjectOnPlane(revHorizFwd, revGroundUp).normalized;
                if (revAlignedFwd.sqrMagnitude < 0.001f) revAlignedFwd = revHorizFwd;
                Quaternion revRot = Quaternion.LookRotation(revAlignedFwd, revGroundUp);

                cop.Body.MovePosition(revNextPos);
                cop.Body.MoveRotation(Quaternion.Slerp(cop.Body.rotation, revRot, Mathf.Clamp01(14f * dt)));
                cop.Body.linearVelocity = revAlignedFwd * cop.CurrentSpeed;
                cop.Body.angularVelocity = Vector3.zero;
                return;
            }

            if (cop.ReverselightsGlow != null && cop.ReverselightsGlow.activeSelf)
            {
                cop.ReverselightsGlow.SetActive(false);
            }

            // Compute desired steering toward goalPos with whisker raycast obstacle avoidance
            Vector3 toGoal = goalPos - pos;
            toGoal.y = 0f;
            Vector3 desiredDir = toGoal.sqrMagnitude > 0.01f ? toGoal.normalized : horizFwd;

            // Cast left/right/center feelers to avoid static walls/buildings
            Vector3 rayOrigin = pos + Vector3.up * 0.65f + horizFwd * cop.HalfLength;
            float feelerDist = Mathf.Clamp(6f + Mathf.Max(0f, cop.CurrentSpeed) * 0.45f, 6f, 18f);
            Vector3 leftRayDir = Quaternion.Euler(0f, -22f, 0f) * horizFwd;
            Vector3 rightRayDir = Quaternion.Euler(0f, 22f, 0f) * horizFwd;

            bool hitCenter = Physics.Raycast(rayOrigin, horizFwd, out RaycastHit centerHit, 3.2f) && centerHit.rigidbody == null && centerHit.normal.y < 0.35f;
            bool hitLeft = Physics.Raycast(rayOrigin, leftRayDir, out RaycastHit leftHit, feelerDist) && leftHit.rigidbody == null && leftHit.normal.y < 0.35f;
            bool hitRight = Physics.Raycast(rayOrigin, rightRayDir, out RaycastHit rightHit, feelerDist) && rightHit.rigidbody == null && rightHit.normal.y < 0.35f;

            if (hitCenter)
            {
                cop.StuckTimer += dt * 2.5f;
                if (cop.StuckTimer > 0.8f)
                {
                    cop.StuckTimer = 0f;
                    cop.ReverseRecoveryTimer = 1.15f;
                    cop.ReverseSteerSign = (rng.Next(2) == 0) ? 1f : -1f;
                    return;
                }
            }
            else
            {
                cop.StuckTimer = Mathf.Max(0f, cop.StuckTimer - dt * 2f);
            }

            float steerBias = 0f;
            if (hitLeft && !hitRight) steerBias = 0.65f;
            else if (hitRight && !hitLeft) steerBias = -0.65f;
            else if (hitLeft && hitRight) steerBias = (leftHit.distance < rightHit.distance) ? 0.85f : -0.85f;

            float signedAngle = Vector3.SignedAngle(horizFwd, desiredDir, Vector3.up);
            float steerInput = Mathf.Clamp(signedAngle / 35f + steerBias, -1f, 1f);
            cop.SteerAngleDeg = steerInput * 28f;

            // Reduce target speed when making a hard turn
            if (Mathf.Abs(signedAngle) > 35f)
            {
                desiredSpeed = Mathf.Min(desiredSpeed, 13.5f);
            }

            cop.IsBraking = cop.CurrentSpeed > desiredSpeed + 2.2f;
            float accelRate = cop.IsBraking ? 15f : 7.4f;
            cop.CurrentSpeed = Mathf.MoveTowards(cop.CurrentSpeed, desiredSpeed, accelRate * dt);

            // Apply yaw rotation proportional to speed and steering angle
            float turnFactor = Mathf.Clamp01(Mathf.Abs(cop.CurrentSpeed) / 4f);
            float yawRateDeg = steerInput * 115f * turnFactor * Mathf.Sign(cop.CurrentSpeed >= -0.1f ? 1f : -1f);
            float newYaw = tr.eulerAngles.y + yawRateDeg * dt;
            Vector3 newHorizFwd = Quaternion.Euler(0f, newYaw, 0f) * Vector3.forward;

            Vector3 nextPos = pos + newHorizFwd * (cop.CurrentSpeed * dt);
            SnapCopToRoadSurface(cop, nextPos, pos.y, out float groundY, out Vector3 groundUp);
            nextPos.y = Mathf.MoveTowards(pos.y, groundY + cop.GroundClearanceOffset, 14f * dt);

            Vector3 alignedFwd = Vector3.ProjectOnPlane(newHorizFwd, groundUp).normalized;
            if (alignedFwd.sqrMagnitude < 0.001f) alignedFwd = newHorizFwd;
            Quaternion targetRot = Quaternion.LookRotation(alignedFwd, groundUp);

            cop.Body.MovePosition(nextPos);
            cop.Body.MoveRotation(Quaternion.Slerp(cop.Body.rotation, targetRot, Mathf.Clamp01(14f * dt)));
            cop.Body.linearVelocity = alignedFwd * cop.CurrentSpeed;
            cop.Body.angularVelocity = Vector3.zero;
        }

        /// <summary>
        /// Snaps a stationary [Police] spawn position from .aimap (which in MM2 .aimap files is often
        /// authored 2.5m-5.5m above the pavement) down onto the road surface and returns the surface normal.
        /// </summary>
        private Vector3 SnapPolicePositionToRoad(Vector3 rawPos, Vector3 forwardDir, out Vector3 roadUp)
        {
            roadUp = Vector3.up;
            float refY = rawPos.y;

            if (aiMap != null && aiMap.DirectedRoads.Count > 0)
            {
                MM2AiMapData.BaiDirectedRoad nearestRoad = aiMap.FindNearestRoadOnSameDeck(
                    rawPos,
                    forwardDir,
                    maxHorizontalDist: 38f,
                    maxVerticalDiff: 8.5f,
                    out int lane,
                    out float distAlong
                );
                if (nearestRoad != null)
                {
                    nearestRoad.SampleLanePose(lane, distAlong, out Vector3 roadPos, out _, out Vector3 rUp);
                    refY = roadPos.y;
                    if (rUp.y >= 0.5f) roadUp = rUp;
                }
            }

            Vector3 origin = new Vector3(rawPos.x, Mathf.Max(rawPos.y, refY) + 2.5f, rawPos.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 16f, ~0, QueryTriggerInteraction.Ignore);
            float bestDiff = float.MaxValue;
            float bestY = refY;
            bool found = false;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || col.attachedRigidbody != null) continue;
                if (hits[i].normal.y < 0.5f) continue;

                float hitY = hits[i].point.y;
                float diff = Mathf.Abs(hitY - refY);
                if (diff <= 6.0f && diff < bestDiff)
                {
                    bestDiff = diff;
                    bestY = hitY;
                    roadUp = hits[i].normal;
                    found = true;
                }
            }

            return new Vector3(rawPos.x, found ? bestY : refY, rawPos.z);
        }

        private void SnapCopToRoadSurface(MM2PoliceAgent cop, Vector3 worldPos, float currentY, out float groundY, out Vector3 groundUp)
        {
            groundY = currentY - cop.GroundClearanceOffset;
            groundUp = Vector3.up;

            // 1. Check .bai road height if the cop is tracking a road
            if (cop.CurrentRoad != null)
            {
                cop.CurrentRoad.SampleLanePose(0, cop.DistanceAlongRoad, out Vector3 rPos, out _, out Vector3 rUp);
                if (Mathf.Abs(rPos.y - groundY) <= 4.5f)
                {
                    groundY = rPos.y;
                    if (rUp.y >= 0.5f) groundUp = rUp;
                }
            }

            // 2. Raycast within a tight vertical window around the cop's current deck so it hugs the 3D road mesh
            Vector3 origin = new Vector3(worldPos.x, currentY + 2.4f, worldPos.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 6.8f, ~0, QueryTriggerInteraction.Ignore);
            float bestDiff = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null || col.attachedRigidbody != null) continue;
                if (hits[i].normal.y < 0.5f) continue;

                float hitY = hits[i].point.y;
                float diff = Mathf.Abs(hitY - groundY);
                if (diff <= 3.5f && diff < bestDiff)
                {
                    bestDiff = diff;
                    groundY = hitY;
                    groundUp = hits[i].normal;
                }
            }
        }

        private void EndPursuit(MM2PoliceAgent cop)
        {
            SetSirenActive(cop, false);
            cop.TargetTransform = null;
            cop.TargetRigidbody = null;

            if (cop.IsFixedRaceSpawn)
            {
                cop.State = MM2PoliceState.StationaryWatch;
                Vector3 pos = cop.HomePosition + Vector3.up * cop.GroundClearanceOffset;
                cop.transform.SetPositionAndRotation(pos, cop.HomeRotation);
                if (cop.Body != null)
                {
                    cop.Body.useGravity = false;
                    cop.Body.position = pos;
                    cop.Body.rotation = cop.HomeRotation;
                    cop.Body.linearVelocity = Vector3.zero;
                    cop.Body.angularVelocity = Vector3.zero;
                }
            }
            else
            {
                cop.State = MM2PoliceState.Patrol;
                if (cop.Body != null)
                {
                    cop.Body.useGravity = false;
                }
                TryPlacePatrolCopOnRoad(cop, initialSpawn: false);
            }
        }

        private void SetSirenActive(MM2PoliceAgent cop, bool active)
        {
            if (cop.Siren0Glow != null) cop.Siren0Glow.SetActive(false);
            if (cop.Siren1Glow != null) cop.Siren1Glow.SetActive(false);
            if (cop.RedRoofLight != null) cop.RedRoofLight.enabled = false;
            if (cop.BlueRoofLight != null) cop.BlueRoofLight.enabled = false;

            if (cop.HeadlightsGlow != null)
            {
                cop.HeadlightsGlow.SetActive(active || nightOrBadWeatherLights);
            }

            if (cop.SirenSource != null)
            {
                if (active)
                {
                    cop.SirenSource.clip = cop.WailClip;
                    if (!cop.SirenSource.isPlaying && cop.SirenSource.clip != null)
                    {
                        cop.SirenSource.Play();
                    }
                }
                else
                {
                    if (cop.SirenSource.isPlaying)
                    {
                        cop.SirenSource.Stop();
                    }
                }
            }
        }

        private void UpdateCopVisualsAndSiren(MM2PoliceAgent cop, float dt, float worldTime)
        {
            if (cop.Damage == null || !cop.Damage.IsTotaled)
            {
                if (cop.WheelRadius > 0.05f)
                {
                    float angularDeg = (cop.CurrentSpeed / cop.WheelRadius) * Mathf.Rad2Deg * dt;
                    cop.WheelSpinAngle = (cop.WheelSpinAngle + angularDeg) % 360f;
                }

                Quaternion spinRot = Quaternion.Euler(cop.WheelSpinAngle, 0f, 0f);
                Quaternion steerRot = Quaternion.Euler(0f, cop.SteerAngleDeg, 0f) * spinRot;

                if (cop.WheelFL != null) cop.WheelFL.localRotation = steerRot;
                if (cop.WheelFR != null) cop.WheelFR.localRotation = steerRot;
                if (cop.WheelRL != null) cop.WheelRL.localRotation = spinRot;
                if (cop.WheelRR != null) cop.WheelRR.localRotation = spinRot;
            }

            if (cop.BrakelightsGlow != null && cop.BrakelightsGlow.activeSelf != cop.IsBraking)
            {
                cop.BrakelightsGlow.SetActive(cop.IsBraking);
            }

            if (cop.State == MM2PoliceState.Pursuit)
            {
                // Alternate SIREN0_L and SIREN1_L + Red/Blue point lights at ~6.5 Hz
                bool phase0 = (Mathf.FloorToInt(worldTime * 6.5f) & 1) == 0;
                if (cop.Siren0Glow != null && cop.Siren0Glow.activeSelf != phase0)
                {
                    cop.Siren0Glow.SetActive(phase0);
                }
                if (cop.Siren1Glow != null && cop.Siren1Glow.activeSelf == phase0)
                {
                    cop.Siren1Glow.SetActive(!phase0);
                }
                if (cop.RedRoofLight != null) cop.RedRoofLight.enabled = phase0;
                if (cop.BlueRoofLight != null) cop.BlueRoofLight.enabled = !phase0;

                // Switch between wail siren (far) and fast yelp siren (within 24m of target)
                if (cop.SirenSource != null && cop.TargetTransform != null)
                {
                    float distSq = (cop.TargetTransform.position - cop.transform.position).sqrMagnitude;
                    AudioClip desiredClip = (distSq < 24f * 24f && cop.YelpClip != null) ? cop.YelpClip : cop.WailClip;
                    if (desiredClip != null && cop.SirenSource.clip != desiredClip)
                    {
                        cop.SirenSource.clip = desiredClip;
                        cop.SirenSource.Play();
                    }
                    else if (!cop.SirenSource.isPlaying && desiredClip != null)
                    {
                        cop.SirenSource.clip = desiredClip;
                        cop.SirenSource.Play();
                    }
                }
            }
        }

        private bool TryPlacePatrolCopOnRoad(MM2PoliceAgent cop, bool initialSpawn)
        {
            if (aiMap == null || aiMap.DirectedRoads.Count == 0 || playerTransform == null)
            {
                return false;
            }

            Vector3 playerPos = playerTransform.position;
            int roadCount = aiMap.DirectedRoads.Count;

            for (int attempt = 0; attempt < 35; attempt++)
            {
                MM2AiMapData.BaiDirectedRoad road = aiMap.DirectedRoads[rng.Next(roadCount)];
                if (road.LaneCount == 0 || road.TotalLength < 12f) continue;

                int lane = rng.Next(road.LaneCount);
                float distAlong = (float)rng.NextDouble() * Mathf.Max(1f, road.TotalLength - 6f) + 3f;
                road.SampleLanePose(lane, distAlong, out Vector3 pos, out Vector3 fwd, out Vector3 up);

                float distSq = (pos - playerPos).sqrMagnitude;
                float minD = initialSpawn ? 55f : 70f;
                float maxD = 195f;
                if (distSq < minD * minD || distSq > maxD * maxD) continue;

                cop.CurrentRoad = road;
                cop.CurrentLane = lane;
                cop.DistanceAlongRoad = distAlong;
                cop.CurrentSpeed = road.SpeedLimit;

                Vector3 finalPos = pos + up * cop.GroundClearanceOffset;
                Quaternion finalRot = Quaternion.LookRotation(fwd, up);
                cop.transform.SetPositionAndRotation(finalPos, finalRot);
                if (cop.Body != null)
                {
                    cop.Body.useGravity = false;
                    cop.Body.position = finalPos;
                    cop.Body.rotation = finalRot;
                    cop.Body.linearVelocity = fwd * cop.CurrentSpeed;
                    cop.Body.angularVelocity = Vector3.zero;
                }
                return true;
            }

            return false;
        }

        private MM2PoliceAgent CreatePoliceAgent(string pkgName)
        {
            if (string.IsNullOrEmpty(pkgName)) pkgName = "vpcop";
            MM2RuntimePkgLoader.ParsedPkg pkg = MM2RuntimePkgLoader.LoadPkg(pkgName);
            if (pkg == null)
            {
                pkgName = "vpcop";
                pkg = MM2RuntimePkgLoader.LoadPkg(pkgName);
                if (pkg == null) return null;
            }

            // vpcop has 2 paintjobs: index 0 = SFPD (California), index 1 = London Met Police
            int pjIndex = (isLondon && pkg.NumPaintjobs > 1) ? 1 : 0;

            GameObject copGo = new GameObject($"Police_{pkgName}");
            copGo.transform.SetParent(copsContainer, false);

            if (!MM2RuntimePkgLoader.BuildRuntimeVehicleVisuals(
                pkgName,
                pjIndex,
                copGo.transform,
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
                Destroy(copGo);
                return null;
            }

            Bounds localBounds = MM2TrafficManager.ComputeLocalVisualBounds(copGo.transform);
            BoxCollider box = copGo.AddComponent<BoxCollider>();
            box.center = new Vector3(localBounds.center.x, Mathf.Max(0.45f, localBounds.center.y), localBounds.center.z);
            box.size = new Vector3(
                Mathf.Clamp(localBounds.size.x, 1.5f, 2.5f),
                Mathf.Clamp(localBounds.size.y, 1.1f, 2.2f),
                Mathf.Clamp(localBounds.size.z, 3.8f, 5.8f)
            );
            box.sharedMaterial = copPhysMat;

            Rigidbody rb = copGo.AddComponent<Rigidbody>();
            rb.mass = 1750f;
            rb.linearDamping = 0.15f;
            rb.angularDamping = 2.2f;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            MM2PoliceAgent cop = copGo.AddComponent<MM2PoliceAgent>();
            cop.PkgName = pkgName;
            cop.Body = rb;
            cop.MainCollider = box;
            cop.WheelFL = wheelFL;
            cop.WheelFR = wheelFR;
            cop.WheelRL = wheelRL;
            cop.WheelRR = wheelRR;
            cop.WheelRadius = Mathf.Max(0.24f, wheelRadius);
            cop.HalfLength = box.size.z * 0.5f;
            cop.HalfWidth = box.size.x * 0.5f;
            cop.GroundClearanceOffset = Mathf.Max(0f, -localBounds.min.y + 0.02f);

            Transform lightsRoot = copGo.transform.Find("VehicleLights");
            if (lightsRoot != null)
            {
                Transform hl = lightsRoot.Find("Headlights_Glow");
                Transform bl = lightsRoot.Find("Brakelights_Glow");
                Transform rl = lightsRoot.Find("Reverselights_Glow");
                Transform s0 = lightsRoot.Find("Siren0_Glow");
                Transform s1 = lightsRoot.Find("Siren1_Glow");

                if (hl != null)
                {
                    cop.HeadlightsGlow = hl.gameObject;
                    cop.HeadlightsGlow.SetActive(nightOrBadWeatherLights);
                }
                if (bl != null) cop.BrakelightsGlow = bl.gameObject;
                if (rl != null) cop.ReverselightsGlow = rl.gameObject;
                if (s0 != null) cop.Siren0Glow = s0.gameObject;
                if (s1 != null) cop.Siren1Glow = s1.gameObject;
            }

            // Create Red & Blue Roof Lightbar Point Lights for pursuit illumination
            float roofY = localBounds.max.y + 0.12f;
            GameObject redGo = new GameObject("SirenLight_Red");
            redGo.transform.SetParent(copGo.transform, false);
            redGo.transform.localPosition = new Vector3(-0.42f, roofY, -0.1f);
            Light redLight = redGo.AddComponent<Light>();
            redLight.type = LightType.Point;
            redLight.color = new Color(1f, 0.12f, 0.12f);
            redLight.intensity = 4.5f;
            redLight.range = 14f;
            redLight.shadows = LightShadows.None;
            redLight.enabled = false;
            cop.RedRoofLight = redLight;

            GameObject blueGo = new GameObject("SirenLight_Blue");
            blueGo.transform.SetParent(copGo.transform, false);
            blueGo.transform.localPosition = new Vector3(0.42f, roofY, -0.1f);
            Light blueLight = blueGo.AddComponent<Light>();
            blueLight.type = LightType.Point;
            blueLight.color = new Color(0.15f, 0.45f, 1f);
            blueLight.intensity = 4.5f;
            blueLight.range = 14f;
            blueLight.shadows = LightShadows.None;
            blueLight.enabled = false;
            cop.BlueRoofLight = blueLight;

            // 3D Siren AudioSource
            AudioSource aud = copGo.AddComponent<AudioSource>();
            aud.playOnAwake = false;
            aud.loop = true;
            aud.spatialBlend = 1.0f;
            aud.rolloffMode = AudioRolloffMode.Linear;
            aud.minDistance = 8f;
            aud.maxDistance = 155f;
            aud.volume = 0.68f;
            aud.dopplerLevel = 0.6f;

            cop.SirenSource = aud;
            cop.WailClip = wailSirenClip;
            cop.YelpClip = yelpSirenClip;

            MM2VehicleDamage dmg = copGo.AddComponent<MM2VehicleDamage>();
            dmg.ConfigureForAgent(pkgName, rb.mass, isPlayer: false);
            cop.Damage = dmg;

            return cop;
        }
    }
}
