using System;
using System.Collections.Generic;
using UnityEngine;
using MM2.Archive;
using MM2.UI;
using MM2.Vehicle;

namespace MM2.AI
{
    /// <summary>
    /// Attached to each ambient traffic vehicle to detect collisions with the player,
    /// opponents, police cruisers, or other knocked vehicles, transitioning the ambient
    /// car from rail-following into reactive rigid-body physics (matching MM2's aiVehicleAmbient).
    /// </summary>
    public class MM2AmbientVehicleAgent : MonoBehaviour
    {
        public string PkgName;
        public int NumPaintjobs = 1;
        public Rigidbody Body;
        public BoxCollider MainCollider;
        public Transform WheelFL;
        public Transform WheelFR;
        public Transform WheelRL;
        public Transform WheelRR;
        public float WheelRadius = 0.34f;
        public float HalfLength = 2.2f;
        public float HalfWidth = 0.9f;
        public float GroundClearanceOffset = 0.02f;

        public GameObject HeadlightsGlow;
        public GameObject BrakelightsGlow;
        public GameObject SignalLeftGlow;
        public GameObject SignalRightGlow;

        // Rail-following state
        public MM2AiMapData.BaiDirectedRoad CurrentRoad;
        public int CurrentLane;
        public float DistanceAlongRoad;

        public MM2AiMapData.BaiDirectedRoad NextRoad;
        public int NextLane;
        public int TurnSignalDir; // -1 = left, 0 = straight, +1 = right

        // Intersection transition curve state
        public bool InIntersectionTransition;
        public Vector3 TransitionStartPos;
        public Vector3 TransitionStartTan;
        public Vector3 TransitionEndPos;
        public Vector3 TransitionEndTan;
        public Vector3 TransitionUp = Vector3.up;
        public float TransitionLength;
        public float TransitionDist;

        public float CurrentSpeed;
        public float PersonalSpeedMultiplier = 1f;
        public float WheelSpinAngle;
        public float SteerVisualAngle;
        public bool IsBraking;
        public float StopSignTimer;
        public bool StoppedAtCurrentSign;

        // Impact / free-physics reaction state
        public bool IsKnockedOffRail;
        public float KnockedTimer;
        public float RecoverTimer;

        private void OnCollisionEnter(Collision collision)
        {
            HandleCollision(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            if (!IsKnockedOffRail && collision.relativeVelocity.sqrMagnitude > 9f)
            {
                HandleCollision(collision);
            }
        }

        private void HandleCollision(Collision collision)
        {
            if (collision == null || collision.collider == null) return;

            Rigidbody otherRb = collision.rigidbody;
            if (otherRb == null) return; // Ignore static world geometry touches while on rail

            // Ignore gentle touches with other rail-bound ambient traffic cars
            MM2AmbientVehicleAgent otherAmbient = otherRb.GetComponent<MM2AmbientVehicleAgent>();
            float relSpeed = collision.relativeVelocity.magnitude;
            if (otherAmbient != null && (!otherAmbient.IsKnockedOffRail || relSpeed < 6.5f))
            {
                return;
            }

            if (relSpeed < 2.2f) return;

            if (!IsKnockedOffRail)
            {
                IsKnockedOffRail = true;
                KnockedTimer = 0f;
                RecoverTimer = 0f;
                if (Body != null)
                {
                    Body.useGravity = true;
                    Body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                }
            }
            else
            {
                KnockedTimer = 0f;
            }
        }
    }

    /// <summary>
    /// Spawns, routes, and simulates Midtown Madness 2 ambient city traffic using road and
    /// intersection data parsed from city/&lt;map&gt;.bai and city/&lt;map&gt;.aimap.
    /// Traffic count scales dynamically with MM2GameSession.TrafficDensity.
    /// </summary>
    public class MM2TrafficManager : MonoBehaviour
    {
        public static MM2TrafficManager Instance { get; private set; }

        private MM2AiMapData aiMap;
        private Transform playerTransform;
        private Rigidbody playerRigidbody;
        private Transform vehiclesContainer;
        private PhysicsMaterial lowFrictionMaterial;

        private readonly List<MM2AmbientVehicleAgent> activeAgents = new List<MM2AmbientVehicleAgent>();
        private readonly System.Random rng = new System.Random(19990512);

        private int targetVehicleCount;
        private float spawnCheckTimer;
        private bool nightOrBadWeatherLights;

        private const float MinSpawnDistFromPlayer = 48f;
        private const float MaxSpawnDistFromPlayer = 195f;
        private const float DespawnDistAhead = 235f;
        private const float DespawnDistBehind = 145f;
        private const float MinVehicleSpacing = 14f;

        public IReadOnlyList<MM2AmbientVehicleAgent> ActiveAgents => activeAgents;

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
            ClearTraffic();

            aiMap = mapData;
            playerTransform = player;
            playerRigidbody = player != null ? player.GetComponent<Rigidbody>() : null;

            if (aiMap == null || aiMap.DirectedRoads.Count == 0)
            {
                targetVehicleCount = 0;
                return;
            }

            if (lowFrictionMaterial == null)
            {
                lowFrictionMaterial = new PhysicsMaterial("MM2_TrafficCar_PhysMat")
                {
                    dynamicFriction = 0.25f,
                    staticFriction = 0.25f,
                    bounciness = 0.08f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounceCombine = PhysicsMaterialCombine.Average
                };
            }

            if (vehiclesContainer == null)
            {
                GameObject root = new GameObject("MM2_AmbientTraffic");
                root.transform.SetParent(transform, false);
                vehiclesContainer = root.transform;
            }

            // Headlights/Taillights turn on in Evening (2), Night (3), Fog (2), or Rain (3)
            nightOrBadWeatherLights =
                MM2GameSession.TimeOfDayIndex >= 2 ||
                MM2GameSession.WeatherIndex >= 2;

            float density01 = Mathf.Clamp01(MM2GameSession.TrafficDensity / 100f);
            if (MM2GameSession.TrafficDensity <= 0)
            {
                targetVehicleCount = 0;
            }
            else
            {
                targetVehicleCount = Mathf.RoundToInt(Mathf.Lerp(5f, 40f, density01));
            }

            if (targetVehicleCount > 0 && playerTransform != null)
            {
                PopulateInitialTraffic();
            }

            Debug.Log($"[MM2TrafficManager] Initialized with TrafficDensity={MM2GameSession.TrafficDensity}% -> targetVehicleCount={targetVehicleCount} (spawned {activeAgents.Count}).");
        }

        public void ClearTraffic()
        {
            for (int i = 0; i < activeAgents.Count; i++)
            {
                if (activeAgents[i] != null && activeAgents[i].gameObject != null)
                {
                    Destroy(activeAgents[i].gameObject);
                }
            }
            activeAgents.Clear();
        }

        private void PopulateInitialTraffic()
        {
            int attempts = targetVehicleCount * 4;
            for (int i = 0; i < attempts && activeAgents.Count < targetVehicleCount; i++)
            {
                TrySpawnOrPlaceAgent(null, initialPopulate: true);
            }
        }

        private void FixedUpdate()
        {
            if (MM2MenuController.IsMenuOpen || aiMap == null || playerTransform == null)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;
            float worldTime = Time.time;
            Vector3 playerPos = playerTransform.position;
            Vector3 playerForward = playerTransform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude > 0.001f) playerForward.Normalize();
            else playerForward = Vector3.forward;

            // Periodically spawn missing vehicles up to targetVehicleCount
            spawnCheckTimer -= dt;
            if (spawnCheckTimer <= 0f)
            {
                spawnCheckTimer = 0.35f;
                if (activeAgents.Count < targetVehicleCount)
                {
                    TrySpawnOrPlaceAgent(null, initialPopulate: false);
                }
            }

            for (int i = 0; i < activeAgents.Count; i++)
            {
                MM2AmbientVehicleAgent agent = activeAgents[i];
                if (agent == null) continue;

                Vector3 toAgent = agent.transform.position - playerPos;
                float distSq = toAgent.sqrMagnitude;
                float forwardDot = Vector3.Dot(toAgent.normalized, playerForward);
                float maxAllowedDist = forwardDot >= -0.15f ? DespawnDistAhead : DespawnDistBehind;

                if (distSq > maxAllowedDist * maxAllowedDist)
                {
                    TrySpawnOrPlaceAgent(agent, initialPopulate: false);
                    continue;
                }

                if (agent.IsKnockedOffRail)
                {
                    StepKnockedAgent(agent, dt, distSq);
                }
                else
                {
                    StepRailAgent(agent, dt, worldTime, playerPos);
                }

                UpdateVisuals(agent, dt, worldTime);
            }
        }

        private void StepKnockedAgent(MM2AmbientVehicleAgent agent, float dt, float distSqToPlayer)
        {
            if (agent.Body == null) return;

            agent.KnockedTimer += dt;
            agent.IsBraking = true;

            // Apply gentle tire drag/damping so knocked ambient cars settle realistically
            Vector3 vel = agent.Body.linearVelocity;
            Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
            float speed = horizVel.magnitude;
            agent.CurrentSpeed = Vector3.Dot(vel, agent.transform.forward);

            if (speed > 0.1f)
            {
                Vector3 dragForce = -horizVel * 2.2f;
                agent.Body.AddForce(dragForce, ForceMode.Acceleration);
            }

            // If upright and nearly stopped after being bumped, either recover to nearest road or recycle when far
            bool isUpright = Vector3.Dot(agent.transform.up, Vector3.up) > 0.65f;
            if (speed < 1.0f && agent.KnockedTimer > 3.5f)
            {
                agent.RecoverTimer += dt;
                if (isUpright && agent.RecoverTimer > 2.5f && distSqToPlayer > 35f * 35f)
                {
                    // Snap back onto nearest road if close enough to the road centerline
                    MM2AiMapData.BaiDirectedRoad nearestRoad = aiMap.FindNearestRoad(
                        agent.transform.position,
                        agent.transform.forward,
                        out int lane,
                        out float distAlong
                    );

                    if (nearestRoad != null)
                    {
                        nearestRoad.SampleLanePose(lane, distAlong, out Vector3 lanePos, out Vector3 laneFwd, out _);
                        if ((agent.transform.position - lanePos).sqrMagnitude < 25f &&
                            Vector3.Dot(agent.transform.forward, laneFwd) > 0.5f)
                        {
                            agent.CurrentRoad = nearestRoad;
                            agent.CurrentLane = lane;
                            agent.DistanceAlongRoad = distAlong;
                            agent.InIntersectionTransition = false;
                            agent.IsKnockedOffRail = false;
                            agent.Body.useGravity = false;
                            agent.Body.angularVelocity = Vector3.zero;
                            AssignNextRoad(agent);
                            return;
                        }
                    }
                }

                if (agent.KnockedTimer > 9f && distSqToPlayer > 55f * 55f)
                {
                    TrySpawnOrPlaceAgent(agent, initialPopulate: false);
                }
            }
        }

        private void StepRailAgent(MM2AmbientVehicleAgent agent, float dt, float worldTime, Vector3 playerPos)
        {
            if (agent.CurrentRoad == null)
            {
                TrySpawnOrPlaceAgent(agent, initialPopulate: false);
                return;
            }

            // 1. Determine desired speed from road speed limit + traffic lights + obstacles ahead
            float speedLimit = agent.CurrentRoad.SpeedLimit * agent.PersonalSpeedMultiplier;
            float desiredSpeed = speedLimit;
            agent.IsBraking = false;

            // Check intersection traffic lights or stop signs when approaching the end of CurrentRoad
            if (!agent.InIntersectionTransition)
            {
                float distToEnd = agent.CurrentRoad.TotalLength - agent.DistanceAlongRoad;
                float stopLineOffset = Mathf.Max(2.5f, agent.CurrentRoad.StopDistAtEnd) + agent.HalfLength;
                float distToStopLine = distToEnd - stopLineOffset;

                if (distToStopLine > -1.5f && distToStopLine < 32f)
                {
                    if (agent.CurrentRoad.HasTrafficLightAtEnd &&
                        aiMap.IntersectionById.TryGetValue(agent.CurrentRoad.EndIntersectionId, out MM2AiMapData.BaiIntersection inter))
                    {
                        MM2AiMapData.TrafficLightPhase phase = inter.GetLightPhase(agent.CurrentRoad, worldTime);
                        if (phase == MM2AiMapData.TrafficLightPhase.Red ||
                            (phase == MM2AiMapData.TrafficLightPhase.Yellow && distToStopLine > 4.5f))
                        {
                            if (distToStopLine <= 0.8f)
                            {
                                desiredSpeed = 0f;
                            }
                            else
                            {
                                desiredSpeed = Mathf.Min(desiredSpeed, Mathf.Sqrt(2f * 6.5f * Mathf.Max(0f, distToStopLine - 0.5f)));
                            }
                            agent.IsBraking = true;
                        }
                    }
                    else if (agent.CurrentRoad.HasStopSignAtEnd && !agent.StoppedAtCurrentSign)
                    {
                        if (distToStopLine <= 0.9f)
                        {
                            desiredSpeed = 0f;
                            agent.IsBraking = true;
                            agent.StopSignTimer += dt;
                            if (agent.StopSignTimer >= 1.1f)
                            {
                                agent.StoppedAtCurrentSign = true;
                            }
                        }
                        else
                        {
                            desiredSpeed = Mathf.Min(desiredSpeed, Mathf.Sqrt(2f * 6.5f * Mathf.Max(0f, distToStopLine - 0.5f)));
                            agent.IsBraking = true;
                        }
                    }
                }

                // Slow down slightly for sharp turns at the upcoming intersection
                if (agent.TurnSignalDir != 0 && distToEnd < 22f)
                {
                    desiredSpeed = Mathf.Min(desiredSpeed, Mathf.Max(7.5f, speedLimit * 0.62f));
                }
            }
            else
            {
                if (agent.TurnSignalDir != 0)
                {
                    desiredSpeed = Mathf.Min(desiredSpeed, Mathf.Max(7.5f, speedLimit * 0.62f));
                }
            }

            // 2. Check for vehicles ahead in the same lane or in a forward cone (including player, opponents, cops)
            float obstacleGap = FindMinimumForwardGap(agent, playerPos, out float obstacleSpeed);
            float safeGap = 3.8f + agent.CurrentSpeed * 0.65f;
            if (obstacleGap < safeGap + 14f)
            {
                if (obstacleGap <= 3.2f)
                {
                    desiredSpeed = 0f;
                    agent.IsBraking = true;
                }
                else
                {
                    float t = Mathf.Clamp01((obstacleGap - 3.2f) / Mathf.Max(1f, safeGap + 10f));
                    float followSpeed = Mathf.Lerp(Mathf.Max(0f, obstacleSpeed - 2f), desiredSpeed, t);
                    if (followSpeed < desiredSpeed - 0.5f)
                    {
                        agent.IsBraking = true;
                    }
                    desiredSpeed = Mathf.Min(desiredSpeed, followSpeed);
                }
            }

            // 3. Accelerate or brake toward desiredSpeed
            if (agent.CurrentSpeed < desiredSpeed)
            {
                float accel = 5.5f;
                agent.CurrentSpeed = Mathf.MoveTowards(agent.CurrentSpeed, desiredSpeed, accel * dt);
            }
            else
            {
                float decel = agent.IsBraking ? 11.5f : 5.0f;
                agent.CurrentSpeed = Mathf.MoveTowards(agent.CurrentSpeed, desiredSpeed, decel * dt);
            }

            // 4. Advance position along road or intersection transition curve
            float stepDist = agent.CurrentSpeed * dt;
            Vector3 targetPos;
            Vector3 targetForward;
            Vector3 targetUp;

            if (!agent.InIntersectionTransition)
            {
                agent.DistanceAlongRoad += stepDist;
                if (agent.DistanceAlongRoad >= agent.CurrentRoad.TotalLength)
                {
                    float overshoot = agent.DistanceAlongRoad - agent.CurrentRoad.TotalLength;
                    BeginIntersectionOrNextRoad(agent, overshoot);
                }
            }
            else
            {
                agent.TransitionDist += stepDist;
                if (agent.TransitionDist >= agent.TransitionLength)
                {
                    float overshoot = agent.TransitionDist - agent.TransitionLength;
                    CompleteIntersectionTransition(agent, overshoot);
                }
            }

            // Sample current pose
            if (agent.InIntersectionTransition)
            {
                float u = agent.TransitionLength > 0.01f
                    ? Mathf.Clamp01(agent.TransitionDist / agent.TransitionLength)
                    : 1f;
                EvaluateHermite(
                    agent.TransitionStartPos,
                    agent.TransitionStartTan * agent.TransitionLength,
                    agent.TransitionEndPos,
                    agent.TransitionEndTan * agent.TransitionLength,
                    u,
                    out targetPos,
                    out targetForward
                );
                targetUp = agent.TransitionUp;
            }
            else if (agent.CurrentRoad != null)
            {
                agent.CurrentRoad.SampleLanePose(
                    agent.CurrentLane,
                    agent.DistanceAlongRoad,
                    out targetPos,
                    out targetForward,
                    out targetUp
                );
            }
            else
            {
                return;
            }

            if (targetUp.y < 0.5f)
            {
                targetUp = Vector3.up;
            }
            // Orthogonalize targetUp against lateral axis so vehicles never roll onto their side on hills
            Vector3 flatFwd = new Vector3(targetForward.x, 0f, targetForward.z);
            if (flatFwd.sqrMagnitude < 0.0001f) flatFwd = Vector3.forward;
            flatFwd.Normalize();
            Vector3 levelRight = new Vector3(flatFwd.z, 0f, -flatFwd.x);
            Vector3 cleanUp = Vector3.Cross(targetForward.normalized, levelRight).normalized;
            if (cleanUp.y < 0.5f) cleanUp = Vector3.up;

            targetPos += cleanUp * agent.GroundClearanceOffset;
            Quaternion targetRot = Quaternion.LookRotation(targetForward, cleanUp);

            // Compute visual front-wheel steering angle from angular yaw rate
            float yawDelta = Mathf.DeltaAngle(agent.transform.eulerAngles.y, targetRot.eulerAngles.y);
            float targetSteer = Mathf.Clamp(yawDelta * 3.5f, -28f, 28f);
            agent.SteerVisualAngle = Mathf.MoveTowards(agent.SteerVisualAngle, targetSteer, 120f * dt);

            if (agent.Body != null)
            {
                // Move via velocity/pose on non-kinematic Rigidbody so collisions transfer realistic momentum
                Vector3 posError = targetPos - agent.Body.position;
                if (posError.sqrMagnitude > 25f)
                {
                    agent.Body.position = targetPos;
                    agent.Body.rotation = targetRot;
                    agent.Body.linearVelocity = targetForward * agent.CurrentSpeed;
                    agent.Body.angularVelocity = Vector3.zero;
                }
                else
                {
                    agent.Body.MovePosition(targetPos);
                    agent.Body.MoveRotation(Quaternion.Slerp(agent.Body.rotation, targetRot, Mathf.Clamp01(14f * dt)));
                    agent.Body.linearVelocity = targetForward * agent.CurrentSpeed;
                    agent.Body.angularVelocity = Vector3.zero;
                }
            }
            else
            {
                agent.transform.SetPositionAndRotation(targetPos, targetRot);
            }
        }

        private float FindMinimumForwardGap(MM2AmbientVehicleAgent agent, Vector3 playerPos, out float obstacleSpeed)
        {
            float minGap = 999f;
            obstacleSpeed = 0f;

            Vector3 myPos = agent.transform.position;
            Vector3 myFwd = agent.transform.forward;

            // 1. Check other ambient agents
            for (int i = 0; i < activeAgents.Count; i++)
            {
                MM2AmbientVehicleAgent other = activeAgents[i];
                if (other == null || other == agent) continue;

                // Same road & lane check
                if (!agent.InIntersectionTransition && !other.IsKnockedOffRail && !other.InIntersectionTransition &&
                    other.CurrentRoad == agent.CurrentRoad && other.CurrentLane == agent.CurrentLane)
                {
                    float centerDist = other.DistanceAlongRoad - agent.DistanceAlongRoad;
                    if (centerDist > 0f)
                    {
                        float gap = centerDist - (agent.HalfLength + other.HalfLength);
                        if (gap < minGap)
                        {
                            minGap = gap;
                            obstacleSpeed = other.CurrentSpeed;
                        }
                        continue;
                    }
                }

                // Spatial forward corridor check (catches cars in intersection transitions or knocked cars)
                Vector3 toOther = other.transform.position - myPos;
                float sqrDist = toOther.sqrMagnitude;
                if (sqrDist > 30f * 30f) continue;

                float fwdProj = Vector3.Dot(toOther, myFwd);
                if (fwdProj <= 0f) continue;

                Vector3 latVec = toOther - myFwd * fwdProj;
                float latDist = Mathf.Sqrt(latVec.x * latVec.x + latVec.z * latVec.z);
                if (latDist < (agent.HalfWidth + other.HalfWidth + 0.55f))
                {
                    float gap = fwdProj - (agent.HalfLength + other.HalfLength);
                    if (gap < minGap)
                    {
                        minGap = gap;
                        obstacleSpeed = other.CurrentSpeed;
                    }
                }
            }

            // 2. Check Player vehicle
            CheckExternalObstacle(
                myPos,
                myFwd,
                agent.HalfLength,
                agent.HalfWidth,
                playerPos,
                playerRigidbody != null ? Vector3.Dot(playerRigidbody.linearVelocity, myFwd) : 0f,
                2.2f,
                0.95f,
                ref minGap,
                ref obstacleSpeed
            );

            // 3. Check Opponents if active
            if (MM2OpponentManager.Instance != null)
            {
                var opponents = MM2OpponentManager.Instance.ActiveOpponents;
                for (int i = 0; i < opponents.Count; i++)
                {
                    var opp = opponents[i];
                    if (opp == null) continue;
                    CheckExternalObstacle(
                        myPos,
                        myFwd,
                        agent.HalfLength,
                        agent.HalfWidth,
                        opp.transform.position,
                        opp.CurrentSpeed,
                        opp.HalfLength,
                        opp.HalfWidth,
                        ref minGap,
                        ref obstacleSpeed
                    );
                }
            }

            // 4. Check Police Cruisers if active
            if (MM2PoliceManager.Instance != null)
            {
                var cops = MM2PoliceManager.Instance.ActiveCops;
                for (int i = 0; i < cops.Count; i++)
                {
                    var cop = cops[i];
                    if (cop == null) continue;
                    CheckExternalObstacle(
                        myPos,
                        myFwd,
                        agent.HalfLength,
                        agent.HalfWidth,
                        cop.transform.position,
                        cop.CurrentSpeed,
                        cop.HalfLength,
                        cop.HalfWidth,
                        ref minGap,
                        ref obstacleSpeed
                    );
                }
            }

            return minGap;
        }

        private static void CheckExternalObstacle(
            Vector3 myPos,
            Vector3 myFwd,
            float myHalfLen,
            float myHalfWid,
            Vector3 otherPos,
            float otherSpeedAlongFwd,
            float otherHalfLen,
            float otherHalfWid,
            ref float minGap,
            ref float obstacleSpeed)
        {
            Vector3 toOther = otherPos - myPos;
            if (toOther.sqrMagnitude > 32f * 32f) return;

            float fwdProj = Vector3.Dot(toOther, myFwd);
            if (fwdProj <= 0f) return;

            Vector3 latVec = toOther - myFwd * fwdProj;
            float latDist = Mathf.Sqrt(latVec.x * latVec.x + latVec.z * latVec.z);
            if (latDist < (myHalfWid + otherHalfWid + 0.65f))
            {
                float gap = fwdProj - (myHalfLen + otherHalfLen);
                if (gap < minGap)
                {
                    minGap = gap;
                    obstacleSpeed = Mathf.Max(0f, otherSpeedAlongFwd);
                }
            }
        }

        private void AssignNextRoad(MM2AmbientVehicleAgent agent)
        {
            if (agent.CurrentRoad == null)
            {
                agent.NextRoad = null;
                agent.TurnSignalDir = 0;
                return;
            }

            agent.NextRoad = aiMap.ChooseNextOutgoingRoad(agent.CurrentRoad, rng);
            if (agent.NextRoad != null)
            {
                agent.NextLane = Mathf.Clamp(agent.CurrentLane, 0, Mathf.Max(0, agent.NextRoad.LaneCount - 1));
                Vector3 cross = Vector3.Cross(agent.CurrentRoad.EndTangent, agent.NextRoad.StartTangent);
                if (cross.y < -0.28f) agent.TurnSignalDir = -1; // Left turn
                else if (cross.y > 0.28f) agent.TurnSignalDir = 1; // Right turn
                else agent.TurnSignalDir = 0;
            }
            else
            {
                agent.TurnSignalDir = 0;
            }
        }

        private void BeginIntersectionOrNextRoad(MM2AmbientVehicleAgent agent, float overshoot)
        {
            if (agent.NextRoad == null)
            {
                AssignNextRoad(agent);
            }

            if (agent.NextRoad == null)
            {
                // Dead-end intersection: recycle vehicle onto a new road
                TrySpawnOrPlaceAgent(agent, initialPopulate: false);
                return;
            }

            agent.CurrentRoad.SampleLanePose(
                agent.CurrentLane,
                agent.CurrentRoad.TotalLength,
                out Vector3 endPos,
                out Vector3 endFwd,
                out Vector3 endUp
            );
            agent.NextRoad.SampleLanePose(
                agent.NextLane,
                0f,
                out Vector3 nextStartPos,
                out Vector3 nextStartFwd,
                out Vector3 nextStartUp
            );

            float chordDist = Vector3.Distance(endPos, nextStartPos);
            if (chordDist > 1.0f && chordDist < 65f)
            {
                agent.InIntersectionTransition = true;
                agent.TransitionStartPos = endPos;
                agent.TransitionStartTan = endFwd;
                agent.TransitionEndPos = nextStartPos;
                agent.TransitionEndTan = nextStartFwd;
                agent.TransitionUp = Vector3.Slerp(endUp, nextStartUp, 0.5f).normalized;
                agent.TransitionLength = chordDist * 1.12f;
                agent.TransitionDist = Mathf.Min(overshoot, agent.TransitionLength * 0.9f);
            }
            else
            {
                CompleteIntersectionTransition(agent, overshoot);
            }
        }

        private void CompleteIntersectionTransition(MM2AmbientVehicleAgent agent, float overshoot)
        {
            agent.InIntersectionTransition = false;
            agent.CurrentRoad = agent.NextRoad;
            agent.CurrentLane = agent.NextLane;
            agent.DistanceAlongRoad = Mathf.Clamp(overshoot, 0f, Mathf.Max(0f, agent.CurrentRoad.TotalLength - 0.5f));
            agent.StopSignTimer = 0f;
            agent.StoppedAtCurrentSign = false;
            AssignNextRoad(agent);
        }

        private static void EvaluateHermite(
            Vector3 p0,
            Vector3 m0,
            Vector3 p1,
            Vector3 m1,
            float t,
            out Vector3 position,
            out Vector3 tangent)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            position = h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;

            float dh00 = 6f * t2 - 6f * t;
            float dh10 = 3f * t2 - 4f * t + 1f;
            float dh01 = -6f * t2 + 6f * t;
            float dh11 = 3f * t2 - 2f * t;

            Vector3 deriv = dh00 * p0 + dh10 * m0 + dh01 * p1 + dh11 * m1;
            tangent = deriv.sqrMagnitude > 0.0001f ? deriv.normalized : (p1 - p0).normalized;
        }

        private void UpdateVisuals(MM2AmbientVehicleAgent agent, float dt, float worldTime)
        {
            // Spin wheels and steer front wheels
            if (agent.WheelRadius > 0.05f)
            {
                float angularDeg = (agent.CurrentSpeed / agent.WheelRadius) * Mathf.Rad2Deg * dt;
                agent.WheelSpinAngle = (agent.WheelSpinAngle + angularDeg) % 360f;
            }

            Quaternion spinRot = Quaternion.Euler(agent.WheelSpinAngle, 0f, 0f);
            Quaternion steerRot = Quaternion.Euler(0f, agent.SteerVisualAngle, 0f) * spinRot;

            if (agent.WheelFL != null) agent.WheelFL.localRotation = steerRot;
            if (agent.WheelFR != null) agent.WheelFR.localRotation = steerRot;
            if (agent.WheelRL != null) agent.WheelRL.localRotation = spinRot;
            if (agent.WheelRR != null) agent.WheelRR.localRotation = spinRot;

            // Brake lights
            if (agent.BrakelightsGlow != null && agent.BrakelightsGlow.activeSelf != agent.IsBraking)
            {
                agent.BrakelightsGlow.SetActive(agent.IsBraking);
            }

            // Turn signals when approaching an intersection turn
            bool approachingTurn = !agent.IsKnockedOffRail &&
                agent.TurnSignalDir != 0 &&
                (agent.InIntersectionTransition ||
                 (agent.CurrentRoad != null && (agent.CurrentRoad.TotalLength - agent.DistanceAlongRoad) < 28f));

            bool blinkOn = approachingTurn && (Mathf.FloorToInt(worldTime * 3.2f) & 1) == 0;
            bool leftSignal = blinkOn && agent.TurnSignalDir < 0;
            bool rightSignal = blinkOn && agent.TurnSignalDir > 0;

            if (agent.SignalLeftGlow != null && agent.SignalLeftGlow.activeSelf != leftSignal)
            {
                agent.SignalLeftGlow.SetActive(leftSignal);
            }
            if (agent.SignalRightGlow != null && agent.SignalRightGlow.activeSelf != rightSignal)
            {
                agent.SignalRightGlow.SetActive(rightSignal);
            }
        }

        private bool TrySpawnOrPlaceAgent(MM2AmbientVehicleAgent existingAgent, bool initialPopulate)
        {
            if (aiMap == null || aiMap.DirectedRoads.Count == 0 || playerTransform == null)
            {
                return false;
            }

            Vector3 playerPos = playerTransform.position;
            Vector3 playerFwd = playerTransform.forward;
            playerFwd.y = 0f;
            if (playerFwd.sqrMagnitude > 0.001f) playerFwd.Normalize();
            else playerFwd = Vector3.forward;

            float minDist = initialPopulate ? 32f : MinSpawnDistFromPlayer;
            float maxDist = MaxSpawnDistFromPlayer;

            int roadCount = aiMap.DirectedRoads.Count;
            MM2AiMapData.BaiDirectedRoad chosenRoad = null;
            int chosenLane = 0;
            float chosenDist = 0f;
            Vector3 spawnPos = Vector3.zero;
            Vector3 spawnFwd = Vector3.forward;
            Vector3 spawnUp = Vector3.up;

            // Sample candidate roads around the player
            for (int attempt = 0; attempt < 40; attempt++)
            {
                MM2AiMapData.BaiDirectedRoad road = aiMap.DirectedRoads[rng.Next(roadCount)];
                if (road.DensityMultiplier <= 0.01f || road.LaneCount == 0 || road.TotalLength < 10f)
                {
                    continue;
                }

                // Probabilistic rejection if density multiplier < 1
                if (road.DensityMultiplier < 1f && rng.NextDouble() > road.DensityMultiplier)
                {
                    continue;
                }

                int lane = rng.Next(road.LaneCount);
                float distAlong = (float)rng.NextDouble() * Mathf.Max(1f, road.TotalLength - 6f) + 3f;
                road.SampleLanePose(lane, distAlong, out Vector3 candidatePos, out Vector3 candidateFwd, out Vector3 candidateUp);

                Vector3 toCandidate = candidatePos - playerPos;
                float distSq = toCandidate.sqrMagnitude;
                if (distSq < minDist * minDist || distSq > maxDist * maxDist)
                {
                    continue;
                }

                // When respawning during gameplay, prefer spawning ahead of the player's travel direction
                if (!initialPopulate)
                {
                    float fwdDot = Vector3.Dot(toCandidate.normalized, playerFwd);
                    if (fwdDot < -0.25f && rng.NextDouble() < 0.80)
                    {
                        continue;
                    }
                    // Avoid popping directly in close front line-of-sight (< 72m right in front of camera)
                    if (fwdDot > 0.65f && distSq < 72f * 72f)
                    {
                        continue;
                    }
                }

                if (!IsPositionClearOfVehicles(candidatePos, existingAgent))
                {
                    continue;
                }

                chosenRoad = road;
                chosenLane = lane;
                chosenDist = distAlong;
                spawnPos = candidatePos;
                spawnFwd = candidateFwd;
                spawnUp = candidateUp;
                break;
            }

            if (chosenRoad == null)
            {
                return false;
            }

            if (existingAgent == null)
            {
                string pkgName = aiMap.SampleAmbientVehiclePkg((float)rng.NextDouble());
                existingAgent = CreateAmbientVehicleObject(pkgName);
                if (existingAgent == null) return false;
                activeAgents.Add(existingAgent);
            }
            else
            {
                // Randomize paintjob when recycling an existing ambient car so traffic looks varied
                if (existingAgent.NumPaintjobs > 1)
                {
                    MM2RuntimePkgLoader.ApplyPaintjobToHierarchy(existingAgent.transform, rng.Next(existingAgent.NumPaintjobs));
                }
            }

            existingAgent.CurrentRoad = chosenRoad;
            existingAgent.CurrentLane = chosenLane;
            existingAgent.DistanceAlongRoad = chosenDist;
            existingAgent.InIntersectionTransition = false;
            existingAgent.IsKnockedOffRail = false;
            existingAgent.KnockedTimer = 0f;
            existingAgent.RecoverTimer = 0f;
            existingAgent.StopSignTimer = 0f;
            existingAgent.StoppedAtCurrentSign = false;
            existingAgent.PersonalSpeedMultiplier = Mathf.Lerp(0.88f, 1.08f, (float)rng.NextDouble());
            existingAgent.CurrentSpeed = chosenRoad.SpeedLimit * existingAgent.PersonalSpeedMultiplier * 0.85f;
            existingAgent.SteerVisualAngle = 0f;
            existingAgent.IsBraking = false;

            AssignNextRoad(existingAgent);

            Vector3 finalPos = spawnPos + spawnUp * existingAgent.GroundClearanceOffset;
            Quaternion finalRot = Quaternion.LookRotation(spawnFwd, spawnUp);

            existingAgent.transform.SetPositionAndRotation(finalPos, finalRot);
            if (existingAgent.Body != null)
            {
                existingAgent.Body.useGravity = false;
                existingAgent.Body.constraints = RigidbodyConstraints.None;
                existingAgent.Body.position = finalPos;
                existingAgent.Body.rotation = finalRot;
                existingAgent.Body.linearVelocity = spawnFwd * existingAgent.CurrentSpeed;
                existingAgent.Body.angularVelocity = Vector3.zero;
            }

            return true;
        }

        private bool IsPositionClearOfVehicles(Vector3 candidatePos, MM2AmbientVehicleAgent ignoreAgent)
        {
            float minSpacingSq = MinVehicleSpacing * MinVehicleSpacing;

            for (int i = 0; i < activeAgents.Count; i++)
            {
                MM2AmbientVehicleAgent other = activeAgents[i];
                if (other == null || other == ignoreAgent) continue;
                if ((other.transform.position - candidatePos).sqrMagnitude < minSpacingSq)
                {
                    return false;
                }
            }

            if (MM2OpponentManager.Instance != null)
            {
                var opponents = MM2OpponentManager.Instance.ActiveOpponents;
                for (int i = 0; i < opponents.Count; i++)
                {
                    if (opponents[i] != null && (opponents[i].transform.position - candidatePos).sqrMagnitude < minSpacingSq)
                    {
                        return false;
                    }
                }
            }

            if (MM2PoliceManager.Instance != null)
            {
                var cops = MM2PoliceManager.Instance.ActiveCops;
                for (int i = 0; i < cops.Count; i++)
                {
                    if (cops[i] != null && (cops[i].transform.position - candidatePos).sqrMagnitude < minSpacingSq)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private MM2AmbientVehicleAgent CreateAmbientVehicleObject(string pkgName)
        {
            MM2RuntimePkgLoader.ParsedPkg pkg = MM2RuntimePkgLoader.LoadPkg(pkgName);
            if (pkg == null)
            {
                pkgName = "va_compact_s";
                pkg = MM2RuntimePkgLoader.LoadPkg(pkgName);
                if (pkg == null) return null;
            }

            int pjCount = Mathf.Max(1, pkg.NumPaintjobs);
            int pjIndex = rng.Next(pjCount);

            GameObject carGo = new GameObject($"Traffic_{pkgName}");
            carGo.transform.SetParent(vehiclesContainer, false);

            if (!MM2RuntimePkgLoader.BuildRuntimeVehicleVisuals(
                pkgName,
                pjIndex,
                carGo.transform,
                out Transform bodyMesh,
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
                Destroy(carGo);
                return null;
            }

            // Compute accurate bounding box from Body_Mesh and wheels
            Bounds localBounds = ComputeLocalVisualBounds(carGo.transform);
            BoxCollider box = carGo.AddComponent<BoxCollider>();
            box.center = new Vector3(localBounds.center.x, Mathf.Max(0.45f, localBounds.center.y), localBounds.center.z);
            box.size = new Vector3(
                Mathf.Clamp(localBounds.size.x, 1.4f, 3.2f),
                Mathf.Clamp(localBounds.size.y, 1.1f, 4.2f),
                Mathf.Clamp(localBounds.size.z, 3.2f, 13.5f)
            );
            box.sharedMaterial = lowFrictionMaterial;

            // Estimate vehicle mass from volume (buses/trucks are heavier than compacts)
            bool isLargeVehicle = box.size.z > 6.5f || pkgName.IndexOf("bus", StringComparison.OrdinalIgnoreCase) >= 0 || pkgName.IndexOf("truck", StringComparison.OrdinalIgnoreCase) >= 0;
            Rigidbody rb = carGo.AddComponent<Rigidbody>();
            rb.mass = isLargeVehicle ? 4800f : 1350f;
            rb.linearDamping = 0.15f;
            rb.angularDamping = 1.8f;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            MM2AmbientVehicleAgent agent = carGo.AddComponent<MM2AmbientVehicleAgent>();
            agent.PkgName = pkgName;
            agent.NumPaintjobs = pjCount;
            agent.Body = rb;
            agent.MainCollider = box;
            agent.WheelFL = wheelFL;
            agent.WheelFR = wheelFR;
            agent.WheelRL = wheelRL;
            agent.WheelRR = wheelRR;
            agent.WheelRadius = Mathf.Max(0.22f, wheelRadius);
            agent.HalfLength = box.size.z * 0.5f;
            agent.HalfWidth = box.size.x * 0.5f;
            agent.GroundClearanceOffset = Mathf.Max(0f, -localBounds.min.y + 0.02f);

            Transform lightsRoot = carGo.transform.Find("VehicleLights");
            if (lightsRoot != null)
            {
                Transform hl = lightsRoot.Find("Headlights_Glow");
                Transform bl = lightsRoot.Find("Brakelights_Glow");
                Transform sl = lightsRoot.Find("SignalLeft_Glow");
                Transform sr = lightsRoot.Find("SignalRight_Glow");

                if (hl != null)
                {
                    agent.HeadlightsGlow = hl.gameObject;
                    agent.HeadlightsGlow.SetActive(nightOrBadWeatherLights);
                }
                if (bl != null) agent.BrakelightsGlow = bl.gameObject;
                if (sl != null) agent.SignalLeftGlow = sl.gameObject;
                if (sr != null) agent.SignalRightGlow = sr.gameObject;
            }

            return agent;
        }

        public static Bounds ComputeLocalVisualBounds(Transform root)
        {
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            Bounds bounds = new Bounds(new Vector3(0f, 0.65f, 0f), new Vector3(1.8f, 1.3f, 4.2f));
            bool initialized = false;

            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.name.EndsWith("_Glow", StringComparison.OrdinalIgnoreCase)) continue;

                Bounds meshBounds = mf.sharedMesh.bounds;
                Vector3 localOffset = root.InverseTransformPoint(mf.transform.position);
                Bounds shifted = new Bounds(meshBounds.center + localOffset, meshBounds.size);

                if (!initialized)
                {
                    bounds = shifted;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(shifted.min);
                    bounds.Encapsulate(shifted.max);
                }
            }

            return bounds;
        }
    }
}
