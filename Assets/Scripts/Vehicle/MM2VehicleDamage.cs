using System;
using System.Collections.Generic;
using UnityEngine;
using MM2.Archive;
using MM2.AI;
using MM2.UI;

namespace MM2.Vehicle
{
    /// <summary>
    /// Vehicle damage controller (`.vehCarDamage` configuration).
    /// Used by the Player, AI Race Opponents, and Police Cruisers:
    /// - Accumulates collision damage scaled by impact speed and the weight/mass of the object hit.
    /// - Progressively applies directional `*_dmg` body textures (gracefully skipping missing textures on modded cars).
    /// - Emits progressive engine smoke from `.vehCarDamage` `SmokeOffset` (white -> grey -> thick black).
    /// - Plays tiered crash SFX, heavy-damage mechanical wobble (`tirewobble1`), and city-specific announcer voice clips (`AL*` / `AS*`).
    /// - Detaches wheels when `CurrentDamage >= MaxDamage`, disabling the vehicle
    ///   and failing the race for the player with the `"Game over!"` banner.
    /// </summary>
    public class MM2VehicleDamage : MonoBehaviour
    {
        [Header("Vehicle Identity")]
        public string VehicleBaseName = "vpmustang99";
        public bool IsPlayerVehicle = true;
        public float SelfMassKg = 1500f;
        public float WheelRadius = 0.35f;

        [Header("MM2 .vehCarDamage Parameters")]
        public float MaxDamage = 343750f;
        public float MedDamage = 220000f;
        public float ImpactThreshold = 1500f;
        public Vector3 SmokeOffset = new Vector3(0f, 0.65f, 1.75f);
        public Vector3 SmokeOffset2 = new Vector3(0f, 0.65f, 1.75f);
        public bool DoublePivot;

        [Header("Runtime Damage State")]
        public float CurrentDamage;
        public float IncomingDamageMultiplier = 1.0f;
        public bool IsHeavilyDamaged;
        public bool IsTotaled;
        public int ActiveDamageZoneMask;

        public float Damage01 => MaxDamage > 1f ? Mathf.Clamp01(CurrentDamage / MaxDamage) : 0f;

        private Rigidbody _rb;
        private ArcadeVehicleController _playerCar;
        private VehicleAudio _vehicleAudio;

        // Engine smoke particle systems
        private ParticleSystem _smokeEmitter1;
        private ParticleSystem _smokeEmitter2;
        private static Material _sharedSmokeMaterial;

        // Audio sources for impact SFX, mechanical wobble loop, and city announcer speech
        private AudioSource _impactAudioSource;
        private AudioSource _wobbleLoopSource;
        private AudioSource _announcerAudioSource;

        private bool _playedHeavyDamageAnnouncer;
        private float _lastAnnouncerPlayTime = -999f;
        private float _lastImpactSoundTime = -999f;
        private float _lastDamageAppliedTime = -999f;
        private readonly Dictionary<int, float> _recentColliderHitTimes = new Dictionary<int, float>();

        // Detached wheel restoration state
        private struct DetachedWheelRecord
        {
            public Transform WheelMesh;
            public Transform OriginalParent;
            public Vector3 OriginalLocalPos;
            public Quaternion OriginalLocalRot;
            public Vector3 OriginalLocalScale;
            public Rigidbody TempRigidbody;
            public Collider TempCollider;
        }

        private readonly List<DetachedWheelRecord> _detachedWheels = new List<DetachedWheelRecord>();
        private float _savedSuspensionDistance = 0.26f;
        private bool _hasSavedSuspension;

        // Cached audio clip lookup
        private static readonly Dictionary<string, AudioClip> _clipCache =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] SoftImpactStems = { "carimpactsoft1", "carimpactsoft3" };
        private static readonly string[] MedImpactStems = { "carimpactmed1", "carimpactmed2" };
        private static readonly string[] HardImpactStems = { "carimpacthard3", "carimpactmed2" };
        private static readonly string[] HugeImpactStems = { "carimpacthuge1", "carimpacthuge2", "carimpacthard3" };

        private static readonly string[] LondonCollisionStems =
        {
            "AL1collision01", "AL1collision02", "AL1collision03", "AL1collision04",
            "AL1collision05", "AL1collision06", "AL1collision07", "AL1collision08",
            "AL2collision01", "AL2collision02", "AL2collision03", "AL2collision04",
            "AL2collision05", "AL2collision06", "AL2collision07", "AL2collision08",
            "AL2collision09", "AL2collision10", "AL2collision11", "AL2collision12", "AL2collision13",
            "AL3collision01", "AL3collision02", "AL3collision03", "AL3collision04",
            "AL3collision05", "AL3collision06", "AL3collision07", "AL3collision08",
            "AL3collision09", "AL3collision10", "AL3collision11", "AL3collision12", "AL3collision13",
            "AL4Collision01", "AL4Collision02", "AL4Collision03", "AL4Collision04",
            "AL4Collision05", "AL4Collision06", "AL4Collision07",
            "AL5collision01", "AL5collision02", "AL5collision03", "AL5collision04",
            "AL5collision05", "AL5collision06", "AL5collision07", "AL5collision08",
            "AL6collision01", "AL6collision02", "AL6collision03", "AL6collision04",
            "AL6collision05", "AL6collision06", "AL6collision07", "AL6collision08",
            "AL6collision09", "AL6collision10", "AL6collision11", "AL6collision12", "AL6collision13", "AL6collision14"
        };

        private static readonly string[] SfCollisionStems =
        {
            "AS2Collision01", "AS2Collision02", "AS2Collision03", "AS2Collision04",
            "AS2Collision05", "AS2Collision06", "AS2Collision07",
            "AS1resultpoor01", "AS1resultpoor02", "AS2resultpoor01", "AS2resultpoor02",
            "AS4resultpoor01", "AS4resultpoor02", "AS5resultpoor01", "AS5resultpoor02"
        };

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _playerCar = GetComponent<ArcadeVehicleController>();
            _vehicleAudio = GetComponent<VehicleAudio>();
            EnsureAudioSources();
        }

        public void ConfigureFromSpec(VehicleSpec spec, bool isPlayer)
        {
            if (spec == null) return;

            VehicleBaseName = !string.IsNullOrEmpty(spec.BaseName) ? spec.BaseName : "vpmustang99";
            IsPlayerVehicle = isPlayer;
            SelfMassKg = spec.HasCarSim
                ? Mathf.Clamp(spec.SimMassKg, 850f, 12000f)
                : Mathf.Clamp(spec.MassLbs * 0.453592f, 950f, 10000f);
            WheelRadius = Mathf.Max(0.22f, spec.WheelRadius);

            MaxDamage = Mathf.Max(50000f, spec.MaxDamage);
            MedDamage = Mathf.Clamp(spec.MedDamage, MaxDamage * 0.35f, MaxDamage * 0.85f);
            ImpactThreshold = Mathf.Max(200f, spec.ImpactThreshold);
            SmokeOffset = spec.SmokeOffset;
            SmokeOffset2 = spec.SmokeOffset2;
            DoublePivot = spec.DoublePivot;

            _rb = GetComponent<Rigidbody>();
            _playerCar = GetComponent<ArcadeVehicleController>();
            _vehicleAudio = GetComponent<VehicleAudio>();

            EnsureAudioSources();
            EnsureSmokeEmitters();
            ResetDamage();
        }

        public void ConfigureForAgent(string baseName, float massKg, bool isPlayer = false)
        {
            VehicleSpec spec = VehicleCatalog.GetSpecByBaseName(baseName);
            if (spec != null)
            {
                ConfigureFromSpec(spec, isPlayer);
                if (massKg > 400f) SelfMassKg = massKg;
                return;
            }

            VehicleBaseName = !string.IsNullOrEmpty(baseName) ? baseName : "vpmustang99";
            IsPlayerVehicle = isPlayer;
            SelfMassKg = Mathf.Max(900f, massKg);
            WheelRadius = 0.35f;
            MaxDamage = 320000f * (SelfMassKg / 1500f);
            MedDamage = MaxDamage * 0.62f;
            ImpactThreshold = 1500f;
            SmokeOffset = new Vector3(0f, 0.65f, 1.75f);
            SmokeOffset2 = new Vector3(0f, 0.65f, 1.75f);
            DoublePivot = false;

            _rb = GetComponent<Rigidbody>();
            _playerCar = GetComponent<ArcadeVehicleController>();
            _vehicleAudio = GetComponent<VehicleAudio>();

            EnsureAudioSources();
            EnsureSmokeEmitters();
            ResetDamage();
        }

        /// <summary>
        /// Cleans up detached wheel objects prior to rebuilding a vehicle's visual hierarchy.
        /// </summary>
        public void PrepareForReconfigure()
        {
            RestoreDetachedWheels(destroyIfOrphaned: true);
        }

        /// <summary>
        /// Fully repairs the vehicle: resets damage to 0, restores clean textures, stops engine smoke,
        /// and reattaches any detached wheels back onto the vehicle.
        /// </summary>
        public void ResetDamage()
        {
            bool wasTotaled = IsTotaled;
            CurrentDamage = 0f;
            IsHeavilyDamaged = false;
            IsTotaled = false;
            ActiveDamageZoneMask = MM2RuntimePaintjobBinder.DamageZoneNone;
            _playedHeavyDamageAnnouncer = false;
            _recentColliderHitTimes.Clear();

            RestoreDetachedWheels(destroyIfOrphaned: false);

            if (_playerCar != null && _hasSavedSuspension && _savedSuspensionDistance > 0.05f)
            {
                SetPlayerWheelColliderSuspension(_savedSuspensionDistance);
                if (wasTotaled && _rb != null && !_rb.isKinematic)
                {
                    _rb.linearVelocity = Vector3.zero;
                    _rb.angularVelocity = Vector3.zero;
                    _rb.position += Vector3.up * 0.20f;
                }
            }

            MM2RuntimePkgLoader.ApplyDamageZonesToHierarchy(transform, MM2RuntimePaintjobBinder.DamageZoneNone);
            UpdateSmokeEmitters();
            if (_smokeEmitter1 != null) _smokeEmitter1.Clear();
            if (_smokeEmitter2 != null) _smokeEmitter2.Clear();

            if (_wobbleLoopSource != null && _wobbleLoopSource.isPlaying)
            {
                _wobbleLoopSource.Stop();
            }
        }

        private void Update()
        {
            UpdateSmokeEmitters();
            UpdateHeavyDamageWobbleAudio();
        }

        private void OnDestroy()
        {
            RestoreDetachedWheels(destroyIfOrphaned: true);
        }

        /// <summary>
        /// Called by `MM2BangerProp.OnCollisionEnter` before waking a sleeping prop rigidbody so the true impact
        /// speed and prop mass (`Spec.Mass`) are recorded accurately.
        /// </summary>
        public void NotifyPropImpact(Collider propCollider, Vector3 hitPoint, Vector3 hitNormal, float impactSpeedMps, float propMassKg)
        {
            if (IsTotaled) return;
            if (MM2RaceManager.Instance != null && MM2RaceManager.Instance.IsCountdownFrozen) return;

            if (propCollider != null)
            {
                int id = propCollider.GetInstanceID();
                if (_recentColliderHitTimes.TryGetValue(id, out float lastHit) && Time.time - lastHit < 0.45f)
                {
                    return;
                }
                _recentColliderHitTimes[id] = Time.time;
            }

            ApplyCollisionImpact(
                hitPoint,
                hitNormal,
                impactSpeedMps,
                Mathf.Max(5f, propMassKg),
                isImmovableWorld: false,
                isProp: true
            );
        }

        /// <summary>
        /// Called when a kinematic AI agent (Opponent, Police, or Traffic) collides with this vehicle or vice versa.
        /// </summary>
        public void NotifyReciprocalImpact(Transform otherRoot, Vector3 hitPoint, Vector3 hitNormal, float impactSpeedMps, float otherMassKg)
        {
            if (IsTotaled) return;
            if (MM2RaceManager.Instance != null && MM2RaceManager.Instance.IsCountdownFrozen) return;

            if (otherRoot != null)
            {
                int id = otherRoot.GetInstanceID();
                if (_recentColliderHitTimes.TryGetValue(id, out float lastHit) && Time.time - lastHit < 0.45f)
                {
                    return;
                }
                _recentColliderHitTimes[id] = Time.time;
            }

            ApplyCollisionImpact(
                hitPoint,
                hitNormal,
                impactSpeedMps,
                Mathf.Max(150f, otherMassKg),
                isImmovableWorld: false,
                isProp: false
            );
        }

        private void OnCollisionEnter(Collision collision)
        {
            ProcessCollision(collision, isEnter: true);
        }

        private void OnCollisionStay(Collision collision)
        {
            ProcessCollision(collision, isEnter: false);
        }

        private void ProcessCollision(Collision collision, bool isEnter)
        {
            if (IsTotaled || collision == null || collision.contactCount == 0) return;
            if (MM2RaceManager.Instance != null && MM2RaceManager.Instance.IsCountdownFrozen) return;

            Collider otherCol = collision.collider;
            if (otherCol == null) return;

            Transform otherRoot = otherCol.transform.root;
            if (otherRoot == transform.root) return;

            var banger = otherCol.GetComponentInParent<MM2BangerProp>();
            if (banger != null)
            {
                // Breakable props report their exact pre-shatter speed and mass via NotifyPropImpact
                return;
            }

            var trafficAgent = otherCol.GetComponentInParent<MM2AmbientVehicleAgent>();
            var otherVehicleDamage = otherCol.GetComponentInParent<MM2VehicleDamage>();
            Rigidbody otherRb = collision.rigidbody;

            // When the player vehicle collides with a kinematic AI vehicle, let the player's dynamic Rigidbody
            // compute the impact and report it via NotifyReciprocalImpact so kinematic callback ordering never drops speed.
            if (!IsPlayerVehicle && otherVehicleDamage != null && otherVehicleDamage.IsPlayerVehicle)
            {
                return;
            }

            // Key vehicles by their root transform and static city colliders by collider instance ID
            // so touching a curb on one city chunk never locks out hitting a building on another (or vice versa)
            int targetId = (trafficAgent != null || otherVehicleDamage != null || otherRb != null)
                ? otherRoot.GetInstanceID()
                : otherCol.GetInstanceID();

            if (_recentColliderHitTimes.TryGetValue(targetId, out float lastTime) && Time.time - lastTime < 0.35f)
            {
                return;
            }

            // Determine what we crashed into and its effective weight/mass
            float otherMassKg = 5000f;
            bool isImmovableWorld = false;
            bool isProp = false;
            Vector3 otherVel = Vector3.zero;

            if (trafficAgent != null)
            {
                otherMassKg = EstimateTrafficMassKg(trafficAgent.PkgName);
                otherVel = trafficAgent.transform.forward * trafficAgent.CurrentSpeed;
            }
            else if (otherVehicleDamage != null)
            {
                otherMassKg = Mathf.Max(500f, otherVehicleDamage.SelfMassKg);
                otherVel = otherVehicleDamage.GetPreStepVelocity();
            }
            else if (otherRb != null)
            {
                otherMassKg = Mathf.Max(150f, otherRb.mass);
                if (!otherRb.isKinematic) otherVel = otherRb.linearVelocity;
            }
            else
            {
                // Static city building / wall / bridge abutment / guardrail
                otherMassKg = 5000f;
                isImmovableWorld = true;
            }

            Vector3 preVel = GetPreStepVelocity();
            Vector3 curVel = GetEstimatedVelocity();
            Vector3 preRelVel = preVel - otherVel;
            Vector3 colRelVel = collision.relativeVelocity;

            // Find the best non-ground contact across all contact points in this collision
            // (since city district MeshColliders contain both road/sidewalk triangles and building facades)
            bool foundValidContact = false;
            Vector3 bestHitPoint = Vector3.zero;
            Vector3 bestHitNormal = Vector3.forward;
            float bestNormalSpeed = -1f;

            int contactCount = collision.contactCount;
            for (int i = 0; i < contactCount; i++)
            {
                ContactPoint cp = collision.GetContact(i);
                Vector3 n = cp.normal;
                Vector3 p = cp.point;

                Vector3 localNormal = transform.InverseTransformDirection(n);
                Vector3 localPoint = transform.InverseTransformPoint(p);

                // Skip benign road/sidewalk/hill surface contacts
                if (otherCol.attachedRigidbody == null)
                {
                    if (n.y > 0.52f || (localNormal.y > 0.52f && localPoint.y < 0.55f))
                    {
                        continue;
                    }
                }

                float nSpeedPre = Mathf.Abs(Vector3.Dot(preRelVel, n));
                float nSpeedRel = Mathf.Abs(Vector3.Dot(colRelVel, n));
                float nSpeedDeltaV = Mathf.Abs(Vector3.Dot(preVel - curVel, n));
                float nSpeedImpulse = (_rb != null && _rb.mass > 1f)
                    ? Mathf.Abs(Vector3.Dot(collision.impulse, n)) / _rb.mass
                    : 0f;

                float candidateNormalSpeed = Mathf.Max(
                    Mathf.Max(nSpeedPre, nSpeedRel),
                    Mathf.Max(nSpeedDeltaV, nSpeedImpulse)
                );

                // Prefer horizontal wall contacts with the strongest normal impact speed
                float wallScore = candidateNormalSpeed * (1f - Mathf.Clamp01(Mathf.Abs(n.y) * 0.5f));
                if (!foundValidContact || wallScore > bestNormalSpeed)
                {
                    foundValidContact = true;
                    bestNormalSpeed = candidateNormalSpeed;
                    bestHitPoint = p;
                    bestHitNormal = n;
                }
            }

            if (!foundValidContact) return;

            float totalSpeedMps = Mathf.Max(preRelVel.magnitude, colRelVel.magnitude);
            float effectiveSpeedMps;

            if (isEnter)
            {
                // On fresh collision entry, combine direct normal impact speed with a light glancing scrape term
                float tangentialSpeed = Mathf.Sqrt(Mathf.Max(0f, totalSpeedMps * totalSpeedMps - bestNormalSpeed * bestNormalSpeed));
                effectiveSpeedMps = bestNormalSpeed + (bestNormalSpeed > 1.2f ? tangentialSpeed * 0.18f : tangentialSpeed * 0.08f);
            }
            else
            {
                // OnCollisionStay: catches building wall impacts when the chassis was already touching
                // the sidewalk/curb of the same district MeshCollider, while ignoring continuous parallel wall rubbing
                float normalDeltaV = Mathf.Abs(Vector3.Dot(preVel - curVel, bestHitNormal));
                float normalImpulseV = (_rb != null && _rb.mass > 1f)
                    ? Mathf.Abs(Vector3.Dot(collision.impulse, bestHitNormal)) / _rb.mass
                    : 0f;
                effectiveSpeedMps = Mathf.Max(normalDeltaV, normalImpulseV);
                if (effectiveSpeedMps < 3.2f && bestNormalSpeed >= 4.0f && Vector3.Dot(preRelVel, bestHitNormal) < -3.2f)
                {
                    effectiveSpeedMps = bestNormalSpeed;
                }
            }

            if (effectiveSpeedMps < 3.0f) return;

            bool damaged = ApplyCollisionImpact(
                bestHitPoint,
                bestHitNormal,
                effectiveSpeedMps,
                otherMassKg,
                isImmovableWorld,
                isProp
            );

            if (damaged)
            {
                _recentColliderHitTimes[targetId] = Time.time;
            }

            // Ensure the other vehicle (if it is an AI Opponent or Cop) also records its share of the collision!
            if (otherVehicleDamage != null && otherVehicleDamage != this)
            {
                float reciprocalSpeedMps = effectiveSpeedMps;
                if (IsPlayerVehicle && !otherVehicleDamage.IsPlayerVehicle)
                {
                    // When ramming a fleeing AI vehicle moving in the same direction, include high-speed chase energy
                    reciprocalSpeedMps = Mathf.Max(effectiveSpeedMps, preVel.magnitude * 0.62f);
                }

                otherVehicleDamage.NotifyReciprocalImpact(
                    transform.root,
                    bestHitPoint,
                    -bestHitNormal,
                    reciprocalSpeedMps,
                    SelfMassKg
                );
            }
        }

        private Vector3 lastFramePos;
        private Vector3 estimatedKinematicVel;
        private Vector3 preStepVelocity;
        private int lastVelFrame = -1;

        public Vector3 GetEstimatedVelocity()
        {
            if (_rb != null && !_rb.isKinematic)
            {
                return _rb.linearVelocity;
            }
            var opp = GetComponent<MM2OpponentAgent>();
            if (opp != null)
            {
                Vector3 oppVel = transform.forward * opp.CurrentSpeed + opp.ImpactPerturbationVelocity;
                if (oppVel.sqrMagnitude > estimatedKinematicVel.sqrMagnitude)
                {
                    return oppVel;
                }
            }
            return estimatedKinematicVel;
        }

        public Vector3 GetPreStepVelocity()
        {
            if (preStepVelocity.sqrMagnitude > 0.01f)
            {
                return preStepVelocity;
            }
            return GetEstimatedVelocity();
        }

        private void FixedUpdate()
        {
            if (Time.frameCount != lastVelFrame)
            {
                if (lastVelFrame >= 0 && Time.fixedDeltaTime > 1e-4f)
                {
                    estimatedKinematicVel = (transform.position - lastFramePos) / Time.fixedDeltaTime;
                }
                lastFramePos = transform.position;
                lastVelFrame = Time.frameCount;
            }

            if (_rb != null && !_rb.isKinematic)
            {
                preStepVelocity = _rb.linearVelocity;
            }
            else
            {
                preStepVelocity = estimatedKinematicVel;
            }
        }

        private static float EstimateTrafficMassKg(string archetype)
        {
            if (string.IsNullOrEmpty(archetype)) return 1650f;
            string lower = archetype.ToLowerInvariant();
            if (lower.Contains("bus") || lower.Contains("semi") || lower.Contains("truck"))
                return 9500f;
            if (lower.Contains("van") || lower.Contains("cab") || lower.Contains("limo") || lower.Contains("pickup"))
                return 2250f;
            if (lower.Contains("compact") || lower.Contains("mini") || lower.Contains("bug"))
                return 1150f;
            return 1650f;
        }

        private bool ApplyCollisionImpact(
            Vector3 hitWorldPoint,
            Vector3 hitWorldNormal,
            float effectiveSpeedMps,
            float otherMassKg,
            bool isImmovableWorld,
            bool isProp
        )
        {
            if (IsTotaled) return false;
            if (!isProp && Time.time - _lastDamageAppliedTime < 0.22f) return false;

            // Reduced-mass ratio: colliding with a light traffic cone (20 kg) yields ~0.013,
            // colliding with an equal-mass car (1500 kg) yields 0.50,
            // colliding with a building wall or city bus (5000-9500 kg) yields ~0.70-0.86.
            float massRatio = otherMassKg / Mathf.Max(100f, SelfMassKg + otherMassKg);
            if (isImmovableWorld)
            {
                massRatio = Mathf.Max(massRatio, 0.72f);
            }
            else if (isProp)
            {
                // Light street furniture (cones, trashcans, signs, fences, glass) should only lightly scuff the car,
                // while still registering measurable damage when plowing through a streetlamp or traffic light at speed
                massRatio *= 0.36f;
            }

            // Convert impact speed (m/s) + mass ratio into MM2 impulse damage units.
            // Tuned so cars can endure ~10-20 solid crashes across a race before totaling.
            float speedFactor = Mathf.Pow(Mathf.Max(0f, effectiveSpeedMps - 2.8f), 1.24f);
            float rawImpulse = speedFactor * massRatio * 920f;

            float minImpulseFactor = isProp ? 0.14f : 0.28f;
            float subtractImpulseFactor = isProp ? 0.16f : 0.32f;

            if (rawImpulse <= ImpactThreshold * minImpulseFactor)
            {
                // Even if below damage threshold, play a soft scuff sound if noticeable
                if (effectiveSpeedMps > 3.8f)
                {
                    PlayImpactSound(0.15f);
                }
                return false;
            }

            float damageDelta = Mathf.Max(0f, rawImpulse - ImpactThreshold * subtractImpulseFactor);
            // Cap maximum single-impact damage to 16% of MaxDamage so one huge crash can't nearly destroy the car
            damageDelta = Mathf.Min(damageDelta, MaxDamage * 0.16f);

            // In Crash Course "Destroy that car!" challenges (EventType == 8), opponents receive damage
            // 4x as fast as normal in Amateur mode and 3x as fast in Pro mode.
            float damageMultiplier = IncomingDamageMultiplier;
            if (!IsPlayerVehicle &&
                MM2RaceManager.Instance != null &&
                MM2RaceManager.Instance.ActiveCrashEventType == 8)
            {
                float crashDestroyMult = MM2PlayerManager.IsActiveProfilePro() ? 3.0f : 4.0f;
                damageMultiplier = Mathf.Max(damageMultiplier, crashDestroyMult);
            }
            damageDelta *= damageMultiplier;
            if (damageDelta <= 1f) return false;

            if (!isProp)
            {
                _lastDamageAppliedTime = Time.time;
            }

            float prevDamage = CurrentDamage;
            CurrentDamage = Mathf.Min(MaxDamage, CurrentDamage + damageDelta);

            // Classify hit zone in vehicle local coordinates and apply *_dmg texture for that zone
            int hitZone = ClassifyHitZone(hitWorldPoint);
            ActiveDamageZoneMask |= hitZone;

            // If damage exceeds 55%, also mark general/top body panels damaged
            if (Damage01 >= 0.55f)
            {
                ActiveDamageZoneMask |= MM2RuntimePaintjobBinder.DamageZoneTopOrGeneral;
            }
            if (Damage01 >= 0.85f)
            {
                ActiveDamageZoneMask |= MM2RuntimePaintjobBinder.DamageZoneAll;
            }

            MM2RuntimePkgLoader.ApplyDamageZonesToHierarchy(transform, ActiveDamageZoneMask);

            // Normalized impact severity (0..1) for audio selection
            float impactSeverity01 = Mathf.Clamp01(damageDelta / (MaxDamage * 0.10f));
            PlayImpactSound(impactSeverity01);

            // Check if vehicle crossed into Heavy Damage (`MedDamage`)
            bool wasHeavy = IsHeavilyDamaged;
            IsHeavilyDamaged = CurrentDamage >= MedDamage || Damage01 >= 0.60f;

            if (IsPlayerVehicle && IsHeavilyDamaged && CurrentDamage < MaxDamage)
            {
                // Trigger city announcer heavy damage voice clip when crossing threshold or taking a hard hit while heavily damaged
                if (!wasHeavy || (impactSeverity01 >= 0.45f && Time.time - _lastAnnouncerPlayTime > 12f))
                {
                    TryPlayCityHeavyDamageAnnouncer();
                }
            }

            // Check if vehicle reached 100% MaxDamage (Totaled!)
            if (prevDamage < MaxDamage && CurrentDamage >= MaxDamage)
            {
                TriggerTotaled(hitWorldPoint);
            }

            return true;
        }

        private int ClassifyHitZone(Vector3 hitWorldPoint)
        {
            Vector3 local = transform.InverseTransformPoint(hitWorldPoint);
            int mask = 0;

            if (local.z > 0.55f)
                mask |= MM2RuntimePaintjobBinder.DamageZoneFront;
            else if (local.z < -0.55f)
                mask |= MM2RuntimePaintjobBinder.DamageZoneRear;

            if (Mathf.Abs(local.x) > 0.38f && Mathf.Abs(local.z) <= 1.35f)
                mask |= MM2RuntimePaintjobBinder.DamageZoneSide;

            if (local.y > 0.65f || mask == 0)
                mask |= MM2RuntimePaintjobBinder.DamageZoneTopOrGeneral;

            return mask;
        }

        private void TriggerTotaled(Vector3 lastHitWorldPoint)
        {
            if (IsTotaled) return;
            IsTotaled = true;
            IsHeavilyDamaged = true;
            CurrentDamage = MaxDamage;

            // Apply all damage textures across the vehicle
            ActiveDamageZoneMask = MM2RuntimePaintjobBinder.DamageZoneAll;
            MM2RuntimePkgLoader.ApplyDamageZonesToHierarchy(transform, ActiveDamageZoneMask);

            // Detach wheels so they pop off and roll/bounce away
            DetachAllWheels();

            // Update smoke to thick black plume
            UpdateSmokeEmitters();

            // Play totaled audio (`damgelose` + `explosion`)
            PlayTotaledAudio();

            // If this is the player's car, notify MM2RaceManager to fail the race and display "Game over!"
            if (IsPlayerVehicle && MM2RaceManager.Instance != null)
            {
                MM2RaceManager.Instance.TriggerPlayerTotaled(_playerCar);
            }
        }

        private void DetachAllWheels()
        {
            RestoreDetachedWheels(destroyIfOrphaned: false);

            Transform visualWheels = transform.Find("VisualWheels");
            var wheelTransforms = new List<Transform>();

            if (visualWheels != null)
            {
                for (int i = 0; i < visualWheels.childCount; i++)
                {
                    Transform child = visualWheels.GetChild(i);
                    if (child != null && child.gameObject.activeSelf)
                    {
                        wheelTransforms.Add(child);
                    }
                }
            }
            else if (_playerCar != null)
            {
                if (_playerCar.frontLeftMesh != null) wheelTransforms.Add(_playerCar.frontLeftMesh);
                if (_playerCar.frontRightMesh != null) wheelTransforms.Add(_playerCar.frontRightMesh);
                if (_playerCar.rearLeftMesh != null) wheelTransforms.Add(_playerCar.rearLeftMesh);
                if (_playerCar.rearRightMesh != null) wheelTransforms.Add(_playerCar.rearRightMesh);
            }

            Vector3 baseVelocity = GetEstimatedVelocity();

            foreach (Transform wTrans in wheelTransforms)
            {
                if (wTrans == null) continue;

                var record = new DetachedWheelRecord
                {
                    WheelMesh = wTrans,
                    OriginalParent = wTrans.parent,
                    OriginalLocalPos = wTrans.localPosition,
                    OriginalLocalRot = wTrans.localRotation,
                    OriginalLocalScale = wTrans.localScale
                };

                Vector3 localPos = transform.InverseTransformPoint(wTrans.position);
                float sideSign = localPos.x >= 0f ? 1f : -1f;
                float foreSign = localPos.z >= 0f ? 1f : -1f;

                // Unparent wheel into world space so it rolls and bounces independently
                wTrans.SetParent(null, true);

                SphereCollider sc = wTrans.GetComponent<SphereCollider>();
                if (sc == null) sc = wTrans.gameObject.AddComponent<SphereCollider>();
                sc.radius = Mathf.Max(0.24f, WheelRadius);

                Rigidbody wrb = wTrans.GetComponent<Rigidbody>();
                if (wrb == null) wrb = wTrans.gameObject.AddComponent<Rigidbody>();
                wrb.mass = 32f;
                wrb.linearDamping = 0.15f;
                wrb.angularDamping = 0.20f;
                wrb.collisionDetectionMode = CollisionDetectionMode.Continuous;

                Vector3 popDir = (transform.right * (sideSign * UnityEngine.Random.Range(3.2f, 5.8f))) +
                                 (Vector3.up * UnityEngine.Random.Range(3.0f, 5.2f)) +
                                 (transform.forward * (foreSign * UnityEngine.Random.Range(1.0f, 3.0f)));

                wrb.linearVelocity = baseVelocity * 0.75f + popDir;
                wrb.angularVelocity = transform.right * UnityEngine.Random.Range(8f, 18f) +
                                      transform.forward * (sideSign * UnityEngine.Random.Range(4f, 10f));

                record.TempCollider = sc;
                record.TempRigidbody = wrb;
                _detachedWheels.Add(record);
            }

            // Drop the player's WheelCollider suspension so the chassis settles onto the ground without wheels
            if (_playerCar != null)
            {
                if (_playerCar.frontLeftCollider != null && !_hasSavedSuspension)
                {
                    _savedSuspensionDistance = _playerCar.frontLeftCollider.suspensionDistance;
                    _hasSavedSuspension = true;
                }
                SetPlayerWheelColliderSuspension(0.03f);
            }
        }

        private void SetPlayerWheelColliderSuspension(float suspensionDist)
        {
            if (_playerCar == null) return;
            WheelCollider[] cols =
            {
                _playerCar.frontLeftCollider,
                _playerCar.frontRightCollider,
                _playerCar.rearLeftCollider,
                _playerCar.rearRightCollider
            };
            foreach (var wc in cols)
            {
                if (wc != null) wc.suspensionDistance = suspensionDist;
            }
        }

        private void RestoreDetachedWheels(bool destroyIfOrphaned)
        {
            for (int i = 0; i < _detachedWheels.Count; i++)
            {
                var rec = _detachedWheels[i];
                if (rec.WheelMesh == null) continue;

                if (destroyIfOrphaned || rec.OriginalParent == null)
                {
                    Destroy(rec.WheelMesh.gameObject);
                    continue;
                }

                if (rec.TempCollider != null)
                {
                    rec.TempCollider.enabled = false;
                    Destroy(rec.TempCollider);
                }
                if (rec.TempRigidbody != null)
                {
                    rec.TempRigidbody.isKinematic = true;
                    Destroy(rec.TempRigidbody);
                }

                rec.WheelMesh.SetParent(rec.OriginalParent, false);
                rec.WheelMesh.localPosition = rec.OriginalLocalPos;
                rec.WheelMesh.localRotation = rec.OriginalLocalRot;
                rec.WheelMesh.localScale = rec.OriginalLocalScale;
            }
            _detachedWheels.Clear();
        }

        private void EnsureSmokeEmitters()
        {
            if (_smokeEmitter1 == null)
            {
                _smokeEmitter1 = CreateSmokeParticleSystem("EngineSmoke_1", SmokeOffset);
            }
            else
            {
                _smokeEmitter1.transform.localPosition = SmokeOffset;
            }

            if (DoublePivot)
            {
                if (_smokeEmitter2 == null)
                {
                    _smokeEmitter2 = CreateSmokeParticleSystem("EngineSmoke_2", SmokeOffset2);
                }
                else
                {
                    _smokeEmitter2.transform.localPosition = SmokeOffset2;
                }
            }
            else if (_smokeEmitter2 != null)
            {
                var em2 = _smokeEmitter2.emission;
                em2.enabled = false;
            }
        }

        private ParticleSystem CreateSmokeParticleSystem(string emitterName, Vector3 localOffset)
        {
            Transform existing = transform.Find(emitterName);
            GameObject go = existing != null ? existing.gameObject : new GameObject(emitterName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localOffset;
            go.transform.localRotation = Quaternion.identity;

            ParticleSystem ps = go.GetComponent<ParticleSystem>();
            if (ps == null) ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.15f, 2.10f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.0f, 2.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.42f, 0.82f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = -0.16f; // Buoyant rising engine smoke
            main.maxParticles = 300;

            var emission = ps.emission;
            emission.enabled = false;
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 26f;
            shape.radius = 0.22f;
            shape.rotation = new Vector3(-68f, 0f, 0f); // Upward and slightly backward

            // 2x2 sprite sheet animation (4 distinct billowing smoke puff frames from fxpt8 / FBM cloud atlas)
            var texSheet = ps.textureSheetAnimation;
            texSheet.enabled = true;
            texSheet.mode = ParticleSystemAnimationMode.Grid;
            texSheet.numTilesX = 2;
            texSheet.numTilesY = 2;
            texSheet.animation = ParticleSystemAnimationType.WholeSheet;
            texSheet.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
            texSheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f, new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(1f, 0f)
            ));
            texSheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 1f);
            texSheet.cycleCount = 1;

            var rotOverLifetime = ps.rotationOverLifetime;
            rotOverLifetime.enabled = true;
            rotOverLifetime.z = new ParticleSystem.MinMaxCurve(-0.9f, 0.9f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.42f;
            noise.frequency = 0.65f;
            noise.scrollSpeed = 0.5f;
            noise.damping = true;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.38f),
                new Keyframe(0.35f, 0.95f),
                new Keyframe(1f, 2.05f)
            );
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(0.92f, 0.92f, 0.92f), 0.6f),
                    new GradientColorKey(new Color(0.82f, 0.82f, 0.82f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.68f, 0.14f),
                    new GradientAlphaKey(0.42f, 0.58f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(grad);

            var psRenderer = go.GetComponent<ParticleSystemRenderer>();
            if (psRenderer != null)
            {
                psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                psRenderer.sortMode = ParticleSystemSortMode.Distance;
                psRenderer.sharedMaterial = GetOrCreateSmokeMaterial();
                psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                psRenderer.receiveShadows = false;
            }

            if (!ps.isPlaying) ps.Play();
            return ps;
        }

        private void UpdateSmokeEmitters()
        {
            if (_smokeEmitter1 == null) return;

            float dmg01 = Damage01;
            float medRatio = Mathf.Clamp(MedDamage / Mathf.Max(1f, MaxDamage), 0.38f, 0.68f);
            float smokeStart01 = Mathf.Min(0.45f, medRatio * 0.80f);

            if (dmg01 < smokeStart01 && !IsTotaled)
            {
                SetSmokeParticleState(_smokeEmitter1, false, Color.white, 0f, 0.5f);
                if (_smokeEmitter2 != null) SetSmokeParticleState(_smokeEmitter2, false, Color.white, 0f, 0.5f);
                return;
            }

            // Progressively transition smoke color:
            // - smokeStart01 .. 0.72: White steam/smoke -> Light grey
            // - 0.72 .. 1.00: Grey -> Thick black smoke
            float smokeProgress = Mathf.InverseLerp(smokeStart01, 1.0f, dmg01);
            Color whiteSmoke = new Color(0.95f, 0.95f, 0.95f, 0.55f);
            Color greySmoke = new Color(0.48f, 0.48f, 0.50f, 0.72f);
            Color blackSmoke = new Color(0.09f, 0.09f, 0.10f, 0.90f);

            Color smokeColor;
            if (IsTotaled)
            {
                smokeColor = blackSmoke;
                smokeProgress = 1f;
            }
            else if (smokeProgress < 0.50f)
            {
                smokeColor = Color.Lerp(whiteSmoke, greySmoke, smokeProgress * 2f);
            }
            else
            {
                smokeColor = Color.Lerp(greySmoke, blackSmoke, (smokeProgress - 0.50f) * 2f);
            }

            float emissionRate = Mathf.Lerp(14f, 62f, smokeProgress);
            float startSize = Mathf.Lerp(0.55f, 1.25f, smokeProgress);

            SetSmokeParticleState(_smokeEmitter1, true, smokeColor, emissionRate, startSize);
            if (_smokeEmitter2 != null && DoublePivot)
            {
                SetSmokeParticleState(_smokeEmitter2, true, smokeColor, emissionRate * 0.85f, startSize);
            }
        }

        private static void SetSmokeParticleState(ParticleSystem ps, bool active, Color color, float rate, float size)
        {
            if (ps == null) return;
            var emission = ps.emission;
            if (!active)
            {
                if (emission.enabled) emission.enabled = false;
                return;
            }

            if (!emission.enabled) emission.enabled = true;
            emission.rateOverTime = rate;

            var main = ps.main;
            main.startColor = color;
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.75f, size * 1.25f);
        }

        public static Material GetOrCreateSmokeMaterial()
        {
            if (_sharedSmokeMaterial != null) return _sharedSmokeMaterial;

            Texture2D tex = BuildSmokeAtlasTexture();

            // Must prioritize URP's native particle shader over Built-in RP shaders so _SURFACE_TYPE_TRANSPARENT works
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Particles/Standard Unlit");

            _sharedSmokeMaterial = new Material(shader)
            {
                name = "MM2_SmokeParticleMat",
                mainTexture = tex
            };
            if (_sharedSmokeMaterial.HasProperty("_MainTex"))
                _sharedSmokeMaterial.SetTexture("_MainTex", tex);
            if (_sharedSmokeMaterial.HasProperty("_BaseMap"))
                _sharedSmokeMaterial.SetTexture("_BaseMap", tex);
            if (_sharedSmokeMaterial.HasProperty("_BaseColor"))
                _sharedSmokeMaterial.SetColor("_BaseColor", Color.white);
            if (_sharedSmokeMaterial.HasProperty("_Color"))
                _sharedSmokeMaterial.SetColor("_Color", Color.white);

            // Configure full URP transparent alpha-blending state and disable DepthOnly pass so quads never clip as blocks
            if (_sharedSmokeMaterial.HasProperty("_Surface"))
                _sharedSmokeMaterial.SetFloat("_Surface", 1f);
            if (_sharedSmokeMaterial.HasProperty("_Blend"))
                _sharedSmokeMaterial.SetFloat("_Blend", 0f);
            if (_sharedSmokeMaterial.HasProperty("_ColorMode"))
                _sharedSmokeMaterial.SetFloat("_ColorMode", 0f);
            if (_sharedSmokeMaterial.HasProperty("_SrcBlend"))
                _sharedSmokeMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (_sharedSmokeMaterial.HasProperty("_DstBlend"))
                _sharedSmokeMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (_sharedSmokeMaterial.HasProperty("_SrcBlendAlpha"))
                _sharedSmokeMaterial.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            if (_sharedSmokeMaterial.HasProperty("_DstBlendAlpha"))
                _sharedSmokeMaterial.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (_sharedSmokeMaterial.HasProperty("_ZWrite"))
                _sharedSmokeMaterial.SetFloat("_ZWrite", 0f);
            if (_sharedSmokeMaterial.HasProperty("_AlphaClip"))
                _sharedSmokeMaterial.SetFloat("_AlphaClip", 0f);
            if (_sharedSmokeMaterial.HasProperty("_Cull"))
                _sharedSmokeMaterial.SetFloat("_Cull", 0f);

            _sharedSmokeMaterial.DisableKeyword("_ALPHATEST_ON");
            _sharedSmokeMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            _sharedSmokeMaterial.DisableKeyword("_ALPHAMODULATE_ON");
            _sharedSmokeMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _sharedSmokeMaterial.SetOverrideTag("RenderType", "Transparent");
            _sharedSmokeMaterial.SetShaderPassEnabled("DepthOnly", false);
            _sharedSmokeMaterial.SetShaderPassEnabled("ShadowCaster", false);
            _sharedSmokeMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            return _sharedSmokeMaterial;
        }

        /// <summary>
        /// Builds a 2x2 billowing smoke puff atlas texture.
        /// Uses MM2's authentic `fxpt8.tex` 4-frame smoke sprite sheet from `mm2tex.ar` blended with a smooth radial
        /// and multi-octave FBM cloud falloff so cell borders are 100% transparent with zero blocky edges.
        /// </summary>
        private static Texture2D BuildSmokeAtlasTexture()
        {
            const int size = 128;
            const int half = size / 2;

            Color32[] archivePixels = null;
            int archW = 0;
            int archH = 0;

            var fxpt8 = MM2RuntimeDecoders.LoadTexture("fxpt8");
            if (fxpt8.Texture != null)
            {
                try
                {
                    archivePixels = fxpt8.Texture.GetPixels32();
                    archW = fxpt8.Texture.width;
                    archH = fxpt8.Texture.height;
                }
                catch
                {
                    archivePixels = null;
                }
            }

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "MM2_EngineSmokeAtlas_2x2",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color32[] outPixels = new Color32[size * size];
            Vector2[] tileSeeds =
            {
                new Vector2(13.7f, 29.3f),
                new Vector2(71.1f, 43.9f),
                new Vector2(113.5f, 89.2f),
                new Vector2(197.3f, 151.7f)
            };

            for (int y = 0; y < size; y++)
            {
                int tileY = y / half;
                float ly = ((y % half) + 0.5f) / half; // 0..1 inside cell
                float dy = (ly - 0.5f) * 2f;           // -1..1

                for (int x = 0; x < size; x++)
                {
                    int tileX = x / half;
                    int tileIdx = tileY * 2 + tileX;
                    float lx = ((x % half) + 0.5f) / half; // 0..1 inside cell
                    float dx = (lx - 0.5f) * 2f;           // -1..1

                    Vector2 seed = tileSeeds[tileIdx];

                    // Organic angle/radius perturbation using 3-octave Perlin FBM so each puff is a wispy cloud, not a circle or square
                    float n1 = Mathf.PerlinNoise(seed.x + lx * 3.2f, seed.y + ly * 3.2f);
                    float n2 = Mathf.PerlinNoise(seed.x + 19.1f + lx * 6.5f, seed.y + 37.4f + ly * 6.5f);
                    float n3 = Mathf.PerlinNoise(seed.x + 53.8f + lx * 13.0f, seed.y + 81.2f + ly * 13.0f);
                    float fbm = n1 * 0.55f + n2 * 0.30f + n3 * 0.15f;

                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    // Feather strictly to 0 before reaching the cell border (dist >= 0.92 -> alpha = 0)
                    float radialMask = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((0.92f - dist) / 0.68f));
                    float cloudAlpha = radialMask * radialMask * Mathf.Clamp01(0.35f + fbm * 0.85f);

                    // Internal 3D-like billow shading (slightly brighter top/center, softer edges)
                    float shade = Mathf.Clamp01(0.76f + 0.24f * fbm - 0.10f * dy);

                    if (archivePixels != null && archW == size && archH == size)
                    {
                        Color32 src = archivePixels[y * size + x];
                        float archAlpha = (src.a / 255f) * radialMask;
                        float archLum = Mathf.Max(src.r, Mathf.Max(src.g, src.b)) / 255f;
                        cloudAlpha = Mathf.Lerp(cloudAlpha, archAlpha, 0.65f);
                        shade = Mathf.Lerp(shade, Mathf.Clamp(archLum * 1.15f, 0.72f, 1f), 0.55f);
                    }

                    byte rgbByte = (byte)Mathf.Clamp(Mathf.RoundToInt(shade * 255f), 0, 255);
                    byte aByte = (byte)Mathf.Clamp(Mathf.RoundToInt(cloudAlpha * 235f), 0, 255);
                    outPixels[y * size + x] = new Color32(rgbByte, rgbByte, rgbByte, aByte);
                }
            }

            tex.SetPixels32(outPixels);
            tex.Apply(false, true);
            return tex;
        }

        private void EnsureAudioSources()
        {
            if (_impactAudioSource == null)
            {
                _impactAudioSource = gameObject.AddComponent<AudioSource>();
                _impactAudioSource.playOnAwake = false;
                _impactAudioSource.loop = false;
                _impactAudioSource.spatialBlend = IsPlayerVehicle ? 0.15f : 0.85f;
                _impactAudioSource.minDistance = 6f;
                _impactAudioSource.maxDistance = 85f;
                _impactAudioSource.volume = 0.85f;
            }

            if (_wobbleLoopSource == null)
            {
                _wobbleLoopSource = gameObject.AddComponent<AudioSource>();
                _wobbleLoopSource.playOnAwake = false;
                _wobbleLoopSource.loop = true;
                _wobbleLoopSource.spatialBlend = IsPlayerVehicle ? 0.2f : 0.9f;
                _wobbleLoopSource.minDistance = 5f;
                _wobbleLoopSource.maxDistance = 50f;
                _wobbleLoopSource.volume = 0f;
                _wobbleLoopSource.clip = LoadCachedAudioClip("tirewobble1");
            }

            if (IsPlayerVehicle && _announcerAudioSource == null)
            {
                _announcerAudioSource = gameObject.AddComponent<AudioSource>();
                _announcerAudioSource.playOnAwake = false;
                _announcerAudioSource.loop = false;
                _announcerAudioSource.spatialBlend = 0f; // 2D HUD announcer speech
                _announcerAudioSource.volume = 0.95f;
            }
        }

        private void UpdateHeavyDamageWobbleAudio()
        {
            if (_wobbleLoopSource == null || _wobbleLoopSource.clip == null) return;

            if (IsTotaled || Damage01 < 0.58f)
            {
                if (_wobbleLoopSource.isPlaying) _wobbleLoopSource.Stop();
                return;
            }

            float speedMps = GetEstimatedVelocity().magnitude;
            if (speedMps < 1.5f)
            {
                if (_wobbleLoopSource.isPlaying) _wobbleLoopSource.volume = 0f;
                return;
            }

            if (!_wobbleLoopSource.isPlaying) _wobbleLoopSource.Play();

            float severity = Mathf.InverseLerp(0.58f, 1.0f, Damage01);
            float speedFactor = Mathf.Clamp01(speedMps / 22f);
            _wobbleLoopSource.volume = severity * speedFactor * (IsPlayerVehicle ? 0.48f : 0.25f);
            _wobbleLoopSource.pitch = Mathf.Lerp(0.75f, 1.35f, speedFactor);
        }

        private void PlayImpactSound(float severity01)
        {
            if (Time.time - _lastImpactSoundTime < 0.09f) return;
            EnsureAudioSources();
            if (_impactAudioSource == null) return;

            string[] pool;
            float vol;
            if (severity01 < 0.22f)
            {
                pool = SoftImpactStems;
                vol = Mathf.Lerp(0.35f, 0.55f, severity01 / 0.22f);
            }
            else if (severity01 < 0.50f)
            {
                pool = MedImpactStems;
                vol = Mathf.Lerp(0.55f, 0.78f, (severity01 - 0.22f) / 0.28f);
            }
            else if (severity01 < 0.80f)
            {
                pool = HardImpactStems;
                vol = Mathf.Lerp(0.78f, 0.92f, (severity01 - 0.50f) / 0.30f);
            }
            else
            {
                pool = HugeImpactStems;
                vol = 1.0f;
            }

            string chosenStem = pool[UnityEngine.Random.Range(0, pool.Length)];
            AudioClip clip = LoadCachedAudioClip(chosenStem);
            if (clip != null)
            {
                _lastImpactSoundTime = Time.time;
                _impactAudioSource.pitch = UnityEngine.Random.Range(0.92f, 1.08f);
                _impactAudioSource.PlayOneShot(clip, IsPlayerVehicle ? vol : vol * 0.65f);
            }
        }

        private void PlayTotaledAudio()
        {
            EnsureAudioSources();
            if (_impactAudioSource != null)
            {
                AudioClip dmgLose = LoadCachedAudioClip("damgelose");
                if (dmgLose != null)
                {
                    _impactAudioSource.pitch = 1.0f;
                    _impactAudioSource.PlayOneShot(dmgLose, IsPlayerVehicle ? 1.0f : 0.75f);
                }

                AudioClip explosion = LoadCachedAudioClip("explosion");
                if (explosion != null)
                {
                    _impactAudioSource.PlayOneShot(explosion, IsPlayerVehicle ? 0.85f : 0.65f);
                }
            }

            if (IsPlayerVehicle)
            {
                TryPlayCityTotaledAnnouncer();
            }
        }

        private void TryPlayCityHeavyDamageAnnouncer()
        {
            EnsureAudioSources();
            if (_announcerAudioSource == null || _announcerAudioSource.isPlaying) return;

            string cityCode = (MM2GameSession.SelectedCityCode ?? "london").Trim().ToLowerInvariant();
            string[] candidateStems = null;

            if (cityCode == "london")
            {
                candidateStems = LondonCollisionStems;
            }
            else if (cityCode == "sf" || cityCode == "sanfrancisco")
            {
                candidateStems = SfCollisionStems;
            }
            else
            {
                // Modded city: check if aud/spchdata/<city>.csv exists in the archives; if not, skip cleanly
                string spchCsv = MM2ArchiveSystem.ReadFileText($"aud/spchdata/{cityCode}.csv");
                if (string.IsNullOrEmpty(spchCsv))
                {
                    return;
                }
            }

            if (candidateStems == null || candidateStems.Length == 0) return;

            // Try up to 4 random candidates to find a valid clip in the mounted .ar archives
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string stem = candidateStems[UnityEngine.Random.Range(0, candidateStems.Length)];
                AudioClip clip = LoadCachedAudioClip(stem);
                if (clip != null)
                {
                    _playedHeavyDamageAnnouncer = true;
                    _lastAnnouncerPlayTime = Time.time;
                    _announcerAudioSource.PlayOneShot(clip, 0.95f);
                    return;
                }
            }
        }

        private void TryPlayCityTotaledAnnouncer()
        {
            EnsureAudioSources();
            if (_announcerAudioSource == null) return;

            string cityCode = (MM2GameSession.SelectedCityCode ?? "london").Trim().ToLowerInvariant();
            string vehStem = MapBaseNameToSpeechVehicleStem(VehicleBaseName);

            AudioClip loseClip = null;
            if (!string.IsNullOrEmpty(vehStem))
            {
                if (cityCode == "london")
                {
                    string[] prefixes = { "AL1", "AL2", "AL3", "AL4", "AL5", "AL6" };
                    int startIdx = UnityEngine.Random.Range(0, prefixes.Length);
                    for (int i = 0; i < prefixes.Length; i++)
                    {
                        string p = prefixes[(startIdx + i) % prefixes.Length];
                        loseClip = LoadCachedAudioClip($"{p}{vehStem}lose01");
                        if (loseClip != null) break;
                    }
                }
                else if (cityCode == "sf" || cityCode == "sanfrancisco")
                {
                    string[] prefixes = { "AS1", "AS2", "AS4", "AS5" };
                    int startIdx = UnityEngine.Random.Range(0, prefixes.Length);
                    for (int i = 0; i < prefixes.Length; i++)
                    {
                        string p = prefixes[(startIdx + i) % prefixes.Length];
                        loseClip = LoadCachedAudioClip($"{p}{vehStem}lose01");
                        if (loseClip != null) break;
                    }
                }
            }

            if (loseClip == null)
            {
                loseClip = LoadCachedAudioClip("youlose");
            }

            if (loseClip != null)
            {
                _announcerAudioSource.Stop();
                _announcerAudioSource.PlayOneShot(loseClip, 0.95f);
            }
        }

        private static string MapBaseNameToSpeechVehicleStem(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return null;
            switch (baseName.ToLowerInvariant())
            {
                case "vpmustang99": return "must";
                case "vpbug":
                case "vpvwcup":     return "volks";
                case "vpcoop":
                case "vpcoop2k":    return "coop";
                case "vpcab":       return "loncab";
                case "vpdblbus":
                case "vpddbus":     return "lonbus";
                case "vpbus":       return "bus";
                case "vpcaddie":    return "eldo";
                case "vpford":      return "ford";
                case "vpcop":       return "copcruiser";
                case "vpbullet":    return "fast";
                case "vppanoz":     return "roadster";
                case "vppanozgt":   return "gtr";
                case "vpsemi":
                case "vpcentury":   return "freight";
                case "vpdune":      return "dune";
                case "vphumvee":
                case "vp4x4":       return "ltv";
                case "vpauditt":    return "audi";
                case "vpdb7":       return "aston";
                case "vpfire":
                case "vpftruck":    return "fire";
                default:            return null; // Modded car: gracefully falls back
            }
        }

        private static AudioClip LoadCachedAudioClip(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            if (_clipCache.TryGetValue(stem, out AudioClip cached))
            {
                return cached;
            }
            AudioClip loaded = MM2RuntimeDecoders.LoadAudioClip(stem);
            _clipCache[stem] = loaded;
            return loaded;
        }
    }
}
