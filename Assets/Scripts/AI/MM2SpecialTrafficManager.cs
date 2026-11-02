using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using MM2.Archive;
using MM2.UI;

namespace MM2.AI
{
    /// <summary>
    /// Spawns and simulates special city rail traffic across Midtown Madness 1 &amp; 2 cities:
    ///   - San Francisco (MM2): Cable Cars (geometry/va_cablecar_f.pkg) following .bai cable car rails
    ///   - London (MM2): Underground Trains (geometry/va_ug_l.pkg) following race/london/london_train.pathset
    ///   - Chicago (MM1): Elevated "L" Trains (bms/r_l_train) following city/chicago/anim_eltrain.csv
    /// </summary>
    public class MM2SpecialTrafficManager : MonoBehaviour
    {
        public static MM2SpecialTrafficManager Instance { get; private set; }

        private class CableCarAgent
        {
            public GameObject RootObject;
            public Rigidbody Body;
            public BoxCollider Collider;
            public GameObject HeadlightsGlow;
            public AudioSource LoopAudio;
            public AudioSource BellAudio;

            public MM2AiMapData.BaiCableCarTrack CurrentTrack;
            public MM2AiMapData.BaiCableCarTrack NextTrack;
            public float DistanceAlongTrack;
            public float CurrentSpeed;
            public float TargetCruiseSpeed = 7.6f;
            public float TerminusWaitTimer;
            public float BellTimer;
            public bool WasStoppedForObstacle;

            public bool InIntersectionTransition;
            public Vector3 TransitionStartPos;
            public Vector3 TransitionStartTan;
            public Vector3 TransitionEndPos;
            public Vector3 TransitionEndTan;
            public Vector3 TransitionUp = Vector3.up;
            public float TransitionLength;
            public float TransitionDist;
        }

        private class CoupledTrainCar
        {
            public GameObject RootObject;
            public Rigidbody Body;
            public BoxCollider Collider;
            public GameObject HeadlightsGlow;
        }

        private class PathsetTrainInstance
        {
            public string Name;
            public Vector3[] Points;
            public float[] CumulativeDistances;
            public float TotalLength;
            public float CarSpacing = 15.8f;
            public float VerticalOffset = 1.75f;
            public float CruiseSpeed = 18.0f;
            public float AccelRate = 0.51f;
            public float StationWaitDuration = 10.0f;
            public bool IsClosedLoop;

            public int State; // 0 = Station Wait, 1 = Accelerating, 2 = Cruising, 3 = Decelerating
            public bool ForwardDirection = true;
            public float StationTimer;
            public float SpeedMultiplier;
            public float LeadDistanceAlongPath;
            public readonly List<CoupledTrainCar> Cars = new List<CoupledTrainCar>();
            public AudioSource TrainAudio;
        }

        private MM2AiMapData aiMap;
        private Transform playerTransform;
        private Transform containerRoot;
        private bool nightOrBadWeatherLights;

        private readonly List<CableCarAgent> cableCars = new List<CableCarAgent>();
        private readonly List<PathsetTrainInstance> trains = new List<PathsetTrainInstance>();
        private readonly Dictionary<int, List<MM2AiMapData.BaiCableCarTrack>> cableTracksByStartInt =
            new Dictionary<int, List<MM2AiMapData.BaiCableCarTrack>>();

        private AudioClip clipCableLoop;
        private AudioClip clipCableBell1;
        private AudioClip clipCableBell2;
        private AudioClip clipCableGoBell;
        private AudioClip clipLondonTube;
        private AudioClip clipChicagoEl;

        public IReadOnlyList< Vector3 > GetActiveHazardPositions(List<Vector3> outPositions, List<Vector3> outVelocities)
        {
            outPositions?.Clear();
            outVelocities?.Clear();
            if (outPositions == null) return Array.Empty<Vector3>();

            for (int i = 0; i < cableCars.Count; i++)
            {
                CableCarAgent cc = cableCars[i];
                if (cc?.RootObject == null) continue;
                outPositions.Add(cc.RootObject.transform.position);
                outVelocities?.Add(cc.RootObject.transform.forward * cc.CurrentSpeed);
            }

            for (int i = 0; i < trains.Count; i++)
            {
                PathsetTrainInstance tr = trains[i];
                if (tr == null) continue;
                float signedSpeed = tr.CruiseSpeed * tr.SpeedMultiplier * (tr.ForwardDirection ? 1f : -1f);
                for (int c = 0; c < tr.Cars.Count; c++)
                {
                    CoupledTrainCar car = tr.Cars[c];
                    if (car?.RootObject == null) continue;
                    outPositions.Add(car.RootObject.transform.position);
                    outVelocities?.Add(car.RootObject.transform.forward * Mathf.Abs(signedSpeed));
                }
            }

            return outPositions;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            ClearSpecialTraffic();
            if (Instance == this) Instance = null;
        }

        public void Initialize(MM2AiMapData mapData, Transform player, string cityName, string raceDir)
        {
            ClearSpecialTraffic();

            aiMap = mapData;
            playerTransform = player;

            if (containerRoot == null)
            {
                Transform existingRoot = transform.Find("MM2_SpecialCityTraffic");
                if (existingRoot != null)
                {
                    containerRoot = existingRoot;
                    for (int i = containerRoot.childCount - 1; i >= 0; i--)
                    {
                        Destroy(containerRoot.GetChild(i).gameObject);
                    }
                }
                else
                {
                    GameObject go = new GameObject("MM2_SpecialCityTraffic");
                    go.transform.SetParent(transform, false);
                    containerRoot = go.transform;
                }
            }

            nightOrBadWeatherLights =
                MM2GameSession.TimeOfDayIndex >= 2 ||
                MM2GameSession.WeatherIndex >= 2;

            string normCity = (cityName ?? (aiMap != null ? aiMap.CityName : string.Empty)).Trim().ToLowerInvariant();
            string normRaceDir = (raceDir ?? normCity).Trim().ToLowerInvariant();

            // 1. San Francisco Cable Cars (from CAI1 .bai cableCar rails + geometry/va_cablecar_f.pkg)
            if (aiMap != null && aiMap.CableCarTracks.Count > 0)
            {
                InitializeCableCars();
            }

            // 2. London Underground Trains (from race/<city>/<city>_train.pathset + geometry/va_ug_l.pkg)
            InitializeUndergroundTrains(normCity, normRaceDir);

            // 3. Chicago Elevated "L" Trains (from city/<city>/anim_eltrain.csv + bms/r_l_train)
            InitializeChicagoElTrains(normCity);
        }

        public void ClearSpecialTraffic()
        {
            for (int i = 0; i < cableCars.Count; i++)
            {
                if (cableCars[i]?.RootObject != null)
                {
                    Destroy(cableCars[i].RootObject);
                }
            }
            cableCars.Clear();
            cableTracksByStartInt.Clear();

            for (int i = 0; i < trains.Count; i++)
            {
                PathsetTrainInstance tr = trains[i];
                if (tr == null) continue;
                for (int c = 0; c < tr.Cars.Count; c++)
                {
                    if (tr.Cars[c]?.RootObject != null)
                    {
                        Destroy(tr.Cars[c].RootObject);
                    }
                }
                tr.Cars.Clear();
            }
            trains.Clear();

            if (containerRoot != null)
            {
                for (int i = containerRoot.childCount - 1; i >= 0; i--)
                {
                    Destroy(containerRoot.GetChild(i).gameObject);
                }
            }
        }

        public void RefreshHeadlights(bool headlightsOn)
        {
            nightOrBadWeatherLights = headlightsOn;
            for (int i = 0; i < cableCars.Count; i++)
            {
                if (cableCars[i]?.HeadlightsGlow != null)
                {
                    cableCars[i].HeadlightsGlow.SetActive(headlightsOn);
                }
            }
            for (int i = 0; i < trains.Count; i++)
            {
                var cars = trains[i].Cars;
                for (int c = 0; c < cars.Count; c++)
                {
                    if (cars[c]?.HeadlightsGlow != null)
                    {
                        cars[c].HeadlightsGlow.SetActive(headlightsOn);
                    }
                }
            }
        }

        // =====================================================================
        // 1. SAN FRANCISCO CABLE CARS (va_cablecar_f)
        // =====================================================================

        private void InitializeCableCars()
        {
            if (MM2RuntimePkgLoader.LoadPkg("va_cablecar_f") == null)
            {
                return;
            }

            if (clipCableLoop == null)
            {
                clipCableLoop = MM2RuntimeDecoders.LoadAudioClip("streetcable") ??
                                MM2RuntimeDecoders.LoadAudioClip("cablecar");
            }
            if (clipCableBell1 == null) clipCableBell1 = MM2RuntimeDecoders.LoadAudioClip("cablecarbell");
            if (clipCableBell2 == null) clipCableBell2 = MM2RuntimeDecoders.LoadAudioClip("cablecarbell2");
            if (clipCableGoBell == null) clipCableGoBell = MM2RuntimeDecoders.LoadAudioClip("cablecargobell");

            cableTracksByStartInt.Clear();
            var connectedPathsByInt = new Dictionary<int, HashSet<int>>();

            for (int i = 0; i < aiMap.CableCarTracks.Count; i++)
            {
                MM2AiMapData.BaiCableCarTrack tr = aiMap.CableCarTracks[i];
                if (!cableTracksByStartInt.TryGetValue(tr.StartIntersectionId, out var list))
                {
                    list = new List<MM2AiMapData.BaiCableCarTrack>(4);
                    cableTracksByStartInt[tr.StartIntersectionId] = list;
                }
                list.Add(tr);

                if (!connectedPathsByInt.TryGetValue(tr.StartIntersectionId, out var setS))
                {
                    setS = new HashSet<int>();
                    connectedPathsByInt[tr.StartIntersectionId] = setS;
                }
                setS.Add(tr.PathId);

                if (!connectedPathsByInt.TryGetValue(tr.EndIntersectionId, out var setE))
                {
                    setE = new HashSet<int>();
                    connectedPathsByInt[tr.EndIntersectionId] = setE;
                }
                setE.Add(tr.PathId);
            }

            // Collect terminal tracks (starting at an intersection with only 1 cable car road)
            var spawnTracks = new List<(MM2AiMapData.BaiCableCarTrack track, float startRatio)>();
            foreach (var kvp in connectedPathsByInt)
            {
                if (kvp.Value.Count == 1 && cableTracksByStartInt.TryGetValue(kvp.Key, out var outTracks) && outTracks.Count > 0)
                {
                    spawnTracks.Add((outTracks[0], 0.15f));
                }
            }

            // Also add mid-route cable cars along the lines so the player encounters active trams throughout San Francisco
            int step = Mathf.Max(1, aiMap.CableCarTracks.Count / 6);
            for (int i = 0; i < aiMap.CableCarTracks.Count && spawnTracks.Count < 10; i += step)
            {
                MM2AiMapData.BaiCableCarTrack candidate = aiMap.CableCarTracks[i];
                bool alreadyUsed = false;
                for (int j = 0; j < spawnTracks.Count; j++)
                {
                    if (spawnTracks[j].track.PathId == candidate.PathId && spawnTracks[j].track.SideIndex == candidate.SideIndex)
                    {
                        alreadyUsed = true;
                        break;
                    }
                }
                if (!alreadyUsed)
                {
                    spawnTracks.Add((candidate, 0.5f));
                }
            }

            float density01 = Mathf.Clamp01(MM2GameSession.TrafficDensity / 100f);
            int maxCableCars = MM2GameSession.TrafficDensity <= 0
                ? 4 // Always keep at least the 4 iconic SF line cable cars active even when ambient car density is 0
                : Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(4f, 10f, density01)), 4, spawnTracks.Count);

            for (int i = 0; i < maxCableCars && i < spawnTracks.Count; i++)
            {
                SpawnCableCar(i, spawnTracks[i].track, spawnTracks[i].startRatio);
            }

            Debug.Log($"[MM2SpecialTrafficManager] Spawned {cableCars.Count} San Francisco cable cars across {aiMap.CableCarTracks.Count} rail segments.");
        }

        private void SpawnCableCar(int index, MM2AiMapData.BaiCableCarTrack startTrack, float startRatio)
        {
            if (startTrack == null) return;

            GameObject go = new GameObject($"SF_CableCar_{index:D2}");
            go.transform.SetParent(containerRoot, false);

            if (!MM2RuntimePkgLoader.BuildRuntimePropOrTrainVisuals("va_cablecar_f", 0, go.transform, out GameObject hlGlow))
            {
                Destroy(go);
                return;
            }

            if (hlGlow != null)
            {
                hlGlow.SetActive(nightOrBadWeatherLights);
            }

            // BoxCollider & Kinematic Rigidbody matching tune/vehicle/va_cablecar_f.aivehicledata (Size 2.64 x 3.29 x 8.72, Mass 7572.5)
            BoxCollider col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(2.64f, 3.29f, 8.72f);
            col.center = new Vector3(0f, 1.65f, 0f);

            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.mass = 7572.5f;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            AudioSource loopSrc = null;
            if (clipCableLoop != null)
            {
                loopSrc = go.AddComponent<AudioSource>();
                loopSrc.clip = clipCableLoop;
                loopSrc.loop = true;
                loopSrc.spatialBlend = 1.0f;
                loopSrc.rolloffMode = AudioRolloffMode.Linear;
                loopSrc.minDistance = 4.0f;
                loopSrc.maxDistance = 85.0f;
                loopSrc.volume = 0.55f;
                loopSrc.dopplerLevel = 0.5f;
                loopSrc.Play();
            }

            AudioSource bellSrc = go.AddComponent<AudioSource>();
            bellSrc.loop = false;
            bellSrc.playOnAwake = false;
            bellSrc.spatialBlend = 1.0f;
            bellSrc.rolloffMode = AudioRolloffMode.Linear;
            bellSrc.minDistance = 6.0f;
            bellSrc.maxDistance = 110.0f;
            bellSrc.volume = 0.78f;

            float dist = Mathf.Clamp(startTrack.TotalLength * startRatio, 0f, startTrack.TotalLength);
            startTrack.SamplePose(dist, out Vector3 pos, out Vector3 fwd, out Vector3 up);
            Quaternion rot = Quaternion.LookRotation(fwd, up);
            go.transform.SetPositionAndRotation(pos, rot);

            var agent = new CableCarAgent
            {
                RootObject = go,
                Body = rb,
                Collider = col,
                HeadlightsGlow = hlGlow,
                LoopAudio = loopSrc,
                BellAudio = bellSrc,
                CurrentTrack = startTrack,
                DistanceAlongTrack = dist,
                CurrentSpeed = 7.5f,
                TargetCruiseSpeed = 7.6f,
                BellTimer = 2.5f + (index * 1.9f)
            };
            agent.NextTrack = ChooseNextCableCarTrack(startTrack, out _);

            cableCars.Add(agent);
        }

        private MM2AiMapData.BaiCableCarTrack ChooseNextCableCarTrack(MM2AiMapData.BaiCableCarTrack current, out bool isTerminusTurnaround)
        {
            isTerminusTurnaround = false;
            if (current == null) return null;

            if (!cableTracksByStartInt.TryGetValue(current.EndIntersectionId, out var candidates) || candidates.Count == 0)
            {
                return null;
            }

            // Collect non-U-turn cable car tracks (PathId != current.PathId)
            MM2AiMapData.BaiCableCarTrack bestContinuation = null;
            float bestDot = -2f;

            for (int i = 0; i < candidates.Count; i++)
            {
                MM2AiMapData.BaiCableCarTrack cand = candidates[i];
                if (cand.PathId == current.PathId) continue;

                float dot = Vector3.Dot(current.EndTangent, cand.StartTangent);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestContinuation = cand;
                }
            }

            if (bestContinuation != null)
            {
                return bestContinuation;
            }

            // Terminus intersection (only 1 cable car road connected): reverse onto opposite rail of the same road
            isTerminusTurnaround = true;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].PathId == current.PathId && candidates[i].SideIndex != current.SideIndex)
                {
                    return candidates[i];
                }
            }

            return candidates[0];
        }

        private void UpdateCableCars(float dt)
        {
            for (int i = 0; i < cableCars.Count; i++)
            {
                CableCarAgent cc = cableCars[i];
                if (cc?.RootObject == null || cc.CurrentTrack == null) continue;

                // Check if waiting at a terminus turntable
                if (cc.TerminusWaitTimer > 0f)
                {
                    cc.TerminusWaitTimer -= dt;
                    cc.CurrentSpeed = 0f;
                    if (cc.LoopAudio != null) cc.LoopAudio.volume = 0.1f;

                    if (cc.TerminusWaitTimer <= 0f)
                    {
                        RingCableCarBell(cc, clipCableGoBell ?? clipCableBell1);
                    }
                    continue;
                }

                // Check for vehicle obstacles directly ahead on the cable car rail
                bool blockedAhead = IsCableCarBlockedAhead(cc);
                if (blockedAhead && !cc.WasStoppedForObstacle)
                {
                    RingCableCarBell(cc, clipCableBell2 ?? clipCableBell1);
                }
                else if (!blockedAhead && cc.WasStoppedForObstacle && cc.CurrentSpeed < 1f)
                {
                    RingCableCarBell(cc, clipCableGoBell ?? clipCableBell1);
                }
                cc.WasStoppedForObstacle = blockedAhead;

                float desiredSpeed = blockedAhead ? 0f : cc.TargetCruiseSpeed;
                float accel = blockedAhead ? 9.5f : 3.8f;
                cc.CurrentSpeed = Mathf.MoveTowards(cc.CurrentSpeed, desiredSpeed, accel * dt);

                // Periodic authentic cable car bell ringing while moving
                if (cc.CurrentSpeed > 1.5f)
                {
                    cc.BellTimer -= dt;
                    if (cc.BellTimer <= 0f)
                    {
                        RingCableCarBell(cc, (UnityEngine.Random.value < 0.5f && clipCableBell2 != null) ? clipCableBell2 : clipCableBell1);
                        cc.BellTimer = UnityEngine.Random.Range(5.5f, 12.0f);
                    }
                }

                if (cc.LoopAudio != null)
                {
                    float speedRatio = Mathf.Clamp01(cc.CurrentSpeed / cc.TargetCruiseSpeed);
                    cc.LoopAudio.volume = Mathf.Lerp(0.12f, 0.58f, speedRatio);
                    cc.LoopAudio.pitch = Mathf.Lerp(0.85f, 1.05f, speedRatio);
                }

                Vector3 targetPos;
                Vector3 targetFwd;
                Vector3 targetUp;

                if (cc.InIntersectionTransition)
                {
                    cc.TransitionDist += cc.CurrentSpeed * dt;
                    if (cc.TransitionDist >= cc.TransitionLength)
                    {
                        float over = cc.TransitionDist - cc.TransitionLength;
                        cc.InIntersectionTransition = false;
                        cc.CurrentTrack = cc.NextTrack;
                        cc.DistanceAlongTrack = Mathf.Clamp(over, 0f, cc.CurrentTrack.TotalLength);
                        cc.NextTrack = ChooseNextCableCarTrack(cc.CurrentTrack, out _);
                        cc.CurrentTrack.SamplePose(cc.DistanceAlongTrack, out targetPos, out targetFwd, out targetUp);
                    }
                    else
                    {
                        float t = Mathf.Clamp01(cc.TransitionDist / Mathf.Max(0.01f, cc.TransitionLength));
                        EvaluateHermite(
                            cc.TransitionStartPos,
                            cc.TransitionStartTan * cc.TransitionLength,
                            cc.TransitionEndPos,
                            cc.TransitionEndTan * cc.TransitionLength,
                            t,
                            out targetPos,
                            out targetFwd);
                        targetUp = cc.TransitionUp;
                    }
                }
                else
                {
                    cc.DistanceAlongTrack += cc.CurrentSpeed * dt;
                    if (cc.DistanceAlongTrack >= cc.CurrentTrack.TotalLength)
                    {
                        float over = cc.DistanceAlongTrack - cc.CurrentTrack.TotalLength;
                        MM2AiMapData.BaiCableCarTrack next = ChooseNextCableCarTrack(cc.CurrentTrack, out bool isTerminus);
                        if (next == null)
                        {
                            cc.DistanceAlongTrack = 0f;
                            cc.CurrentTrack.SamplePose(0f, out targetPos, out targetFwd, out targetUp);
                        }
                        else if (isTerminus)
                        {
                            cc.CurrentTrack = next;
                            cc.DistanceAlongTrack = 0f;
                            cc.NextTrack = ChooseNextCableCarTrack(cc.CurrentTrack, out _);
                            cc.TerminusWaitTimer = 3.5f;
                            cc.CurrentSpeed = 0f;
                            cc.CurrentTrack.SamplePose(0f, out targetPos, out targetFwd, out targetUp);
                        }
                        else
                        {
                            RingCableCarBell(cc, clipCableBell1);
                            cc.CurrentTrack.SamplePose(cc.CurrentTrack.TotalLength, out Vector3 endPos, out Vector3 endFwd, out Vector3 endUp);
                            next.SamplePose(0f, out Vector3 nextStartPos, out Vector3 nextStartFwd, out Vector3 nextStartUp);
                            float chord = Vector3.Distance(endPos, nextStartPos);
                            if (chord > 0.5f && chord < 55f)
                            {
                                cc.InIntersectionTransition = true;
                                cc.NextTrack = next;
                                cc.TransitionStartPos = endPos;
                                cc.TransitionStartTan = endFwd;
                                cc.TransitionEndPos = nextStartPos;
                                cc.TransitionEndTan = nextStartFwd;
                                cc.TransitionUp = Vector3.Slerp(endUp, nextStartUp, 0.5f).normalized;
                                cc.TransitionLength = chord * 1.05f;
                                cc.TransitionDist = Mathf.Min(over, cc.TransitionLength * 0.85f);

                                float t = Mathf.Clamp01(cc.TransitionDist / Mathf.Max(0.01f, cc.TransitionLength));
                                EvaluateHermite(
                                    cc.TransitionStartPos,
                                    cc.TransitionStartTan * cc.TransitionLength,
                                    cc.TransitionEndPos,
                                    cc.TransitionEndTan * cc.TransitionLength,
                                    t,
                                    out targetPos,
                                    out targetFwd);
                                targetUp = cc.TransitionUp;
                            }
                            else
                            {
                                cc.CurrentTrack = next;
                                cc.DistanceAlongTrack = Mathf.Clamp(over, 0f, cc.CurrentTrack.TotalLength);
                                cc.NextTrack = ChooseNextCableCarTrack(cc.CurrentTrack, out _);
                                cc.CurrentTrack.SamplePose(cc.DistanceAlongTrack, out targetPos, out targetFwd, out targetUp);
                            }
                        }
                    }
                    else
                    {
                        cc.CurrentTrack.SamplePose(cc.DistanceAlongTrack, out targetPos, out targetFwd, out targetUp);
                    }
                }

                if (targetFwd.sqrMagnitude < 0.0001f) targetFwd = Vector3.forward;
                Quaternion targetRot = Quaternion.LookRotation(targetFwd, targetUp);
                cc.Body.MovePosition(targetPos);
                cc.Body.MoveRotation(targetRot);
            }
        }

        private bool IsCableCarBlockedAhead(CableCarAgent cc)
        {
            Transform t = cc.RootObject.transform;
            Vector3 pos = t.position;
            Vector3 fwd = t.forward;

            // Check player vehicle
            if (playerTransform != null)
            {
                Vector3 toPlayer = playerTransform.position - pos;
                if (Mathf.Abs(toPlayer.y) < 3.5f)
                {
                    float fwdDist = Vector3.Dot(toPlayer, fwd);
                    if (fwdDist > 3.5f && fwdDist < 15.5f)
                    {
                        Vector3 lat = toPlayer - fwd * fwdDist;
                        if (lat.sqrMagnitude < 2.3f * 2.3f) return true;
                    }
                }
            }

            // Check other cable cars
            for (int i = 0; i < cableCars.Count; i++)
            {
                CableCarAgent other = cableCars[i];
                if (other == null || other == cc || other.RootObject == null) continue;
                Vector3 toOther = other.RootObject.transform.position - pos;
                if (Mathf.Abs(toOther.y) < 3.5f)
                {
                    float fwdDist = Vector3.Dot(toOther, fwd);
                    if (fwdDist > 4.0f && fwdDist < 16.0f)
                    {
                        Vector3 lat = toOther - fwd * fwdDist;
                        if (lat.sqrMagnitude < 2.2f * 2.2f) return true;
                    }
                }
            }

            return false;
        }

        private void RingCableCarBell(CableCarAgent cc, AudioClip clip)
        {
            if (cc?.BellAudio == null || clip == null || playerTransform == null) return;
            if ((cc.RootObject.transform.position - playerTransform.position).sqrMagnitude > 115f * 115f) return;
            if (!cc.BellAudio.isPlaying)
            {
                cc.BellAudio.PlayOneShot(clip);
            }
        }

        // =====================================================================
        // 2. LONDON UNDERGROUND TRAINS (va_ug_l & race/london/london_train.pathset)
        // =====================================================================

        private void InitializeUndergroundTrains(string cityName, string raceDir)
        {
            string pathsetVirtualPath = $"race/{raceDir}/{cityName}_train.pathset";
            byte[] data = MM2ArchiveSystem.ReadFileBytes(pathsetVirtualPath);
            if (data == null || data.Length < 12)
            {
                data = MM2ArchiveSystem.ReadFileBytes($"race/{cityName}/{cityName}_train.pathset");
            }
            if (data == null && (cityName == "london" || raceDir == "london"))
            {
                data = MM2ArchiveSystem.ReadFileBytes("race/london/london_train.pathset");
            }
            if (data == null || data.Length < 12) return;

            if (data[0] != 'P' || data[1] != 'T' || data[2] != 'H' || data[3] != '1')
            {
                return;
            }

            if (MM2RuntimePkgLoader.LoadPkg("va_ug_l") == null)
            {
                return;
            }

            if (clipLondonTube == null)
            {
                clipLondonTube = MM2RuntimeDecoders.LoadAudioClip("londontube");
            }

            uint numPaths = BitConverter.ToUInt32(data, 4);
            int pos = 12;

            for (uint p = 0; p < numPaths && pos + 44 <= data.Length; p++)
            {
                int nameEnd = pos;
                while (nameEnd < pos + 32 && data[nameEnd] != 0) nameEnd++;
                string pathName = Encoding.ASCII.GetString(data, pos, nameEnd - pos).Trim();
                uint nPts = BitConverter.ToUInt32(data, pos + 32);
                pos += 44;

                if (nPts > 10000 || pos + nPts * 16 > data.Length) break;

                var pts = new List<Vector3>((int)nPts);
                for (uint i = 0; i < nPts; i++)
                {
                    int ptOff = pos + (int)i * 16;
                    float x = BitConverter.ToSingle(data, ptOff);
                    float y = BitConverter.ToSingle(data, ptOff + 4);
                    float z = BitConverter.ToSingle(data, ptOff + 8);
                    Vector3 uPt = new Vector3(-x, y, z);
                    if (pts.Count == 0 || (pts[pts.Count - 1] - uPt).sqrMagnitude > 0.01f)
                    {
                        pts.Add(uPt);
                    }
                }
                pos += (int)nPts * 16;

                if (pts.Count < 2) continue;

                SpawnTrainOnPolyline(
                    $"LondonTube_{p}_{pathName}",
                    "va_ug_l",
                    pts.ToArray(),
                    carsPerTrain: 3,
                    carSpacing: 15.8f,
                    verticalOffset: 1.75f, // Matches CG.y = 1.75 in tune/banger/va_ug_l.dgbangerdata
                    colliderSize: new Vector3(3.0f, 2.9f, 15.0f),
                    colliderCenter: new Vector3(0f, -0.29f, 0f),
                    cruiseSpeed: 18.0f,
                    isClosedLoop: false,
                    startProgress01: (p % 3) * 0.25f,
                    loopClip: clipLondonTube
                );
            }

            if (trains.Count > 0)
            {
                Debug.Log($"[MM2SpecialTrafficManager] Spawned {trains.Count} London Underground 3-car trains.");
            }
        }

        // =====================================================================
        // 3. CHICAGO ELEVATED "L" TRAINS (bms/r_l_train & city/chicago/anim_eltrain.csv)
        // =====================================================================

        private void InitializeChicagoElTrains(string cityName)
        {
            string csvPath = $"city/{cityName}/anim_eltrain.csv";
            string csvText = MM2ArchiveSystem.ReadFileText(csvPath);
            if (string.IsNullOrEmpty(csvText) && cityName.StartsWith("chi", StringComparison.OrdinalIgnoreCase))
            {
                csvText = MM2ArchiveSystem.ReadFileText("city/chicago/anim_eltrain.csv");
            }
            if (string.IsNullOrEmpty(csvText)) return;

            if (MM2RuntimePkgLoader.LoadPkg("r_l_train") == null)
            {
                return;
            }

            if (clipChicagoEl == null)
            {
                clipChicagoEl = MM2RuntimeDecoders.LoadAudioClip("thel") ??
                                MM2RuntimeDecoders.LoadAudioClip("londontube");
            }

            var loopPts = new List<Vector3>(64);
            string[] lines = csvText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] cols = line.Split(',');
                if (cols.Length < 3) continue;

                if (float.TryParse(cols[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(cols[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    Vector3 uPt = new Vector3(-x, y, z);
                    if (loopPts.Count == 0 || (loopPts[loopPts.Count - 1] - uPt).sqrMagnitude > 0.04f)
                    {
                        loopPts.Add(uPt);
                    }
                }
            }

            if (loopPts.Count < 3) return;

            // Close the Chicago Loop track
            if ((loopPts[0] - loopPts[loopPts.Count - 1]).sqrMagnitude > 0.25f)
            {
                loopPts.Add(loopPts[0]);
            }

            Vector3[] ptsArray = loopPts.ToArray();

            // Spawn 3 4-car Elevated "L" trains evenly spaced around the Chicago Loop
            const int numElTrains = 3;
            for (int t = 0; t < numElTrains; t++)
            {
                SpawnTrainOnPolyline(
                    $"Chicago_L_Train_{t:D2}",
                    "r_l_train",
                    ptsArray,
                    carsPerTrain: 4,
                    carSpacing: 9.35f,
                    verticalOffset: 0.0f,
                    colliderSize: new Vector3(3.4f, 3.35f, 9.05f),
                    colliderCenter: new Vector3(0f, 1.95f, 0f),
                    cruiseSpeed: 16.5f,
                    isClosedLoop: true,
                    startProgress01: (float)t / numElTrains,
                    loopClip: clipChicagoEl
                );
            }

            Debug.Log($"[MM2SpecialTrafficManager] Spawned {numElTrains} Chicago Elevated 'L' 4-car trains on closed loop ({ptsArray.Length} waypoints).");
        }

        private void SpawnTrainOnPolyline(
            string trainName,
            string pkgName,
            Vector3[] points,
            int carsPerTrain,
            float carSpacing,
            float verticalOffset,
            Vector3 colliderSize,
            Vector3 colliderCenter,
            float cruiseSpeed,
            bool isClosedLoop,
            float startProgress01,
            AudioClip loopClip)
        {
            int n = points.Length;
            float[] cumDist = new float[n];
            float totalLen = 0f;
            cumDist[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                totalLen += Vector3.Distance(points[i - 1], points[i]);
                cumDist[i] = totalLen;
            }

            float minRequiredLen = carsPerTrain * carSpacing + 4f;
            if (totalLen < minRequiredLen) return;

            var train = new PathsetTrainInstance
            {
                Name = trainName,
                Points = points,
                CumulativeDistances = cumDist,
                TotalLength = totalLen,
                CarSpacing = carSpacing,
                VerticalOffset = verticalOffset,
                CruiseSpeed = cruiseSpeed,
                AccelRate = 0.51f,
                StationWaitDuration = 8.0f,
                IsClosedLoop = isClosedLoop,
                ForwardDirection = true,
                State = isClosedLoop ? 2 : 1,
                SpeedMultiplier = isClosedLoop ? 1.0f : 0.4f
            };

            float usableSpan = isClosedLoop ? totalLen : Mathf.Max(1f, totalLen - (carsPerTrain - 1) * carSpacing);
            train.LeadDistanceAlongPath = isClosedLoop
                ? (startProgress01 * totalLen) % totalLen
                : (carsPerTrain - 1) * carSpacing + startProgress01 * (usableSpan * 0.5f);

            GameObject trainGroupGo = new GameObject(trainName);
            trainGroupGo.transform.SetParent(containerRoot, false);

            for (int c = 0; c < carsPerTrain; c++)
            {
                GameObject carGo = new GameObject($"{trainName}_Car{c}");
                carGo.transform.SetParent(trainGroupGo.transform, false);

                if (!MM2RuntimePkgLoader.BuildRuntimePropOrTrainVisuals(pkgName, 0, carGo.transform, out GameObject hlGlow))
                {
                    Destroy(carGo);
                    continue;
                }

                if (hlGlow != null)
                {
                    hlGlow.SetActive(nightOrBadWeatherLights);
                }

                BoxCollider box = carGo.AddComponent<BoxCollider>();
                box.size = colliderSize;
                box.center = colliderCenter;

                Rigidbody rb = carGo.AddComponent<Rigidbody>();
                rb.mass = 13082f;
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

                // Attach 3D train audio to the middle car
                if (c == carsPerTrain / 2 && loopClip != null)
                {
                    AudioSource src = carGo.AddComponent<AudioSource>();
                    src.clip = loopClip;
                    src.loop = true;
                    src.spatialBlend = 1.0f;
                    src.rolloffMode = AudioRolloffMode.Linear;
                    src.minDistance = 8.0f;
                    src.maxDistance = 150.0f;
                    src.volume = 0.68f;
                    src.dopplerLevel = 0.65f;
                    src.Play();
                    train.TrainAudio = src;
                }

                train.Cars.Add(new CoupledTrainCar
                {
                    RootObject = carGo,
                    Body = rb,
                    Collider = box,
                    HeadlightsGlow = hlGlow
                });
            }

            PositionTrainCars(train, immediateTransform: true);
            trains.Add(train);
        }

        private void UpdateTrains(float dt)
        {
            for (int i = 0; i < trains.Count; i++)
            {
                PathsetTrainInstance tr = trains[i];
                if (tr == null || tr.Cars.Count == 0) continue;

                if (tr.IsClosedLoop)
                {
                    tr.SpeedMultiplier = 1.0f;
                    tr.LeadDistanceAlongPath = (tr.LeadDistanceAlongPath + tr.CruiseSpeed * dt) % tr.TotalLength;
                    if (tr.LeadDistanceAlongPath < 0f) tr.LeadDistanceAlongPath += tr.TotalLength;
                }
                else
                {
                    // 4-state shuttle machine
                    float trainSpan = (tr.Cars.Count - 1) * tr.CarSpacing;
                    float minLeadDist = trainSpan;
                    float maxLeadDist = tr.TotalLength;
                    float stopDecelDist = (tr.CruiseSpeed / Mathf.Max(0.1f, tr.AccelRate)) * 0.5f;

                    switch (tr.State)
                    {
                        case 0: // Station Wait
                            tr.SpeedMultiplier = 0f;
                            tr.StationTimer += dt;
                            if (tr.StationTimer >= tr.StationWaitDuration)
                            {
                                tr.StationTimer = 0f;
                                tr.State = 1;
                            }
                            break;

                        case 1: // Accelerating out of station
                            tr.SpeedMultiplier = Mathf.MoveTowards(tr.SpeedMultiplier, 1f, tr.AccelRate * dt);
                            AdvanceShuttleTrain(tr, dt, minLeadDist, maxLeadDist, stopDecelDist);
                            if (tr.SpeedMultiplier >= 0.999f && tr.State == 1)
                            {
                                tr.SpeedMultiplier = 1f;
                                tr.State = 2;
                            }
                            break;

                        case 2: // Cruising
                            tr.SpeedMultiplier = 1f;
                            AdvanceShuttleTrain(tr, dt, minLeadDist, maxLeadDist, stopDecelDist);
                            break;

                        case 3: // Decelerating into station
                            tr.SpeedMultiplier = Mathf.MoveTowards(tr.SpeedMultiplier, 0.08f, tr.AccelRate * dt);
                            AdvanceShuttleTrain(tr, dt, minLeadDist, maxLeadDist, stopDecelDist);
                            break;
                    }
                }

                if (tr.TrainAudio != null)
                {
                    if (tr.State == 0 && !tr.IsClosedLoop)
                    {
                        tr.TrainAudio.volume = 0f;
                    }
                    else
                    {
                        tr.TrainAudio.volume = Mathf.Lerp(0.18f, 0.68f, tr.SpeedMultiplier);
                        tr.TrainAudio.pitch = Mathf.Lerp(0.82f, 1.04f, tr.SpeedMultiplier);
                    }
                }

                PositionTrainCars(tr, immediateTransform: false);
            }
        }

        private static void AdvanceShuttleTrain(
            PathsetTrainInstance tr,
            float dt,
            float minLeadDist,
            float maxLeadDist,
            float stopDecelDist)
        {
            float step = tr.CruiseSpeed * tr.SpeedMultiplier * dt;
            if (tr.ForwardDirection)
            {
                tr.LeadDistanceAlongPath += step;
                if (tr.State != 3 && (maxLeadDist - tr.LeadDistanceAlongPath) <= stopDecelDist)
                {
                    tr.State = 3;
                }
                if (tr.LeadDistanceAlongPath >= maxLeadDist)
                {
                    tr.LeadDistanceAlongPath = maxLeadDist;
                    tr.ForwardDirection = false;
                    tr.SpeedMultiplier = 0f;
                    tr.StationTimer = 0f;
                    tr.State = 0;
                }
            }
            else
            {
                tr.LeadDistanceAlongPath -= step;
                if (tr.State != 3 && (tr.LeadDistanceAlongPath - minLeadDist) <= stopDecelDist)
                {
                    tr.State = 3;
                }
                if (tr.LeadDistanceAlongPath <= minLeadDist)
                {
                    tr.LeadDistanceAlongPath = minLeadDist;
                    tr.ForwardDirection = true;
                    tr.SpeedMultiplier = 0f;
                    tr.StationTimer = 0f;
                    tr.State = 0;
                }
            }
        }

        private static void PositionTrainCars(PathsetTrainInstance tr, bool immediateTransform)
        {
            for (int c = 0; c < tr.Cars.Count; c++)
            {
                CoupledTrainCar car = tr.Cars[c];
                if (car?.RootObject == null) continue;

                float carDist = tr.LeadDistanceAlongPath - c * tr.CarSpacing;
                if (tr.IsClosedLoop)
                {
                    carDist %= tr.TotalLength;
                    if (carDist < 0f) carDist += tr.TotalLength;
                }
                else
                {
                    carDist = Mathf.Clamp(carDist, 0f, tr.TotalLength);
                }

                SamplePolylinePose(tr.Points, tr.CumulativeDistances, tr.TotalLength, carDist, out Vector3 pos, out Vector3 fwd, out Vector3 up);
                pos.y += tr.VerticalOffset;

                Quaternion rot = Quaternion.LookRotation(fwd, up);
                if (immediateTransform || car.Body == null)
                {
                    car.RootObject.transform.SetPositionAndRotation(pos, rot);
                }
                else
                {
                    car.Body.MovePosition(pos);
                    car.Body.MoveRotation(rot);
                }
            }
        }

        private static void SamplePolylinePose(
            Vector3[] pts,
            float[] cumDist,
            float totalLen,
            float distance,
            out Vector3 pos,
            out Vector3 forward,
            out Vector3 up)
        {
            int n = pts.Length;
            float d = Mathf.Clamp(distance, 0f, totalLen);
            int seg = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if (d <= cumDist[i + 1] || i == n - 2)
                {
                    seg = i;
                    break;
                }
            }

            float d0 = cumDist[seg];
            float d1 = cumDist[seg + 1];
            float segLen = Mathf.Max(0.001f, d1 - d0);
            float t = Mathf.Clamp01((d - d0) / segLen);

            pos = Vector3.Lerp(pts[seg], pts[seg + 1], t);
            Vector3 diff = pts[seg + 1] - pts[seg];
            forward = diff.sqrMagnitude > 0.0001f ? diff.normalized : Vector3.forward;

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

        private void FixedUpdate()
        {
            if (MM2MenuController.IsMenuOpen) return;

            float dt = Time.fixedDeltaTime;
            if (cableCars.Count > 0)
            {
                UpdateCableCars(dt);
            }
            if (trains.Count > 0)
            {
                UpdateTrains(dt);
            }
        }
    }
}
