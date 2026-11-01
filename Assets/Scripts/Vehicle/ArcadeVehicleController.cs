using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using MM2.Archive;

namespace MM2.Vehicle
{
    [RequireComponent(typeof(Rigidbody))]
    public class ArcadeVehicleController : MonoBehaviour
    {
        [Header("Wheel Colliders")]
        public WheelCollider frontLeftCollider;
        public WheelCollider frontRightCollider;
        public WheelCollider rearLeftCollider;
        public WheelCollider rearRightCollider;

        [Header("Wheel Visual Transforms")]
        public Transform frontLeftMesh;
        public Transform frontRightMesh;
        public Transform rearLeftMesh;
        public Transform rearRightMesh;

        [Header("Vehicle Light Meshes")]
        public GameObject headlightsGlowObj;
        public GameObject brakelightsGlowObj;
        public GameObject reverselightsGlowObj;
        public bool headlightsOn;
        private Light leftHeadlightBeam;
        private Light rightHeadlightBeam;

        [Header("Vehicle Simulation Physics")]
        [Tooltip("Vehicle simulation parameters parsed from .vehcarsim, .vehGyro, and .asNode")]
        public MM2CarSimConfig carSimConfig = new MM2CarSimConfig();

        [Header("Engine & Transmission")]
        [Tooltip("Vehicle physics mass in kg")]
        public float vehicleMassKg = 1400f;
        [Tooltip("Motor torque applied to drive wheels")]
        public float motorTorque = 3600f;
        [Tooltip("Braking torque applied when reversing throttle or holding brakes")]
        public float brakeTorque = 16000f;
        [Tooltip("Direct arcade deceleration (m/s^2) when actively braking")]
        public float brakeDecelAssist = 24f;
        [Tooltip("Passive engine/rolling brake torque when throttle is released")]
        public float coastBrakeTorque = 2200f;
        [Tooltip("Passive speed decay (m/s^2) when letting off the gas so the car stops within a few seconds")]
        public float coastDeceleration = 4.8f;
        [Tooltip("Proportional aerodynamic/rolling drag per m/s when coasting")]
        public float coastLinearDrag = 0.08f;
        [Tooltip("Target physics top speed in Miles Per Hour (.vehcarsim Trans.High)")]
        public float maxSpeedMph = 115f;
        [Tooltip("Max front wheel steering angle in degrees (.vehcarsim SteeringLimit 0.40 rad = 23 deg)")]
        public float maxSteerAngle = 24f;
        [Tooltip("Steering responsiveness retained at top speed (0.28 = retains 28% steer angle at top speed)")]
        public float highSpeedSteerReduction = 0.28f;

        [Header("Arcade Grip, Cornering & Drift")]
        [Tooltip("Forward tire friction stiffness (MM2 StaticFric ~ 2.4 - 2.6)")]
        public float forwardGripStiffness = 2.45f;
        [Tooltip("Front lateral tire friction stiffness for crisp turn-in")]
        public float frontSidewaysGripStiffness = 2.15f;
        [Tooltip("Rear lateral tire friction stiffness (slightly lower than front for natural oversteer at limit)")]
        public float rearSidewaysGripStiffness = 1.95f;
        [Tooltip("Gentle lateral velocity damping (1/s) to supplement WheelCollider tire grip without feeling on rails")]
        public float lateralGripAssist = 3.2f;
        [Tooltip("Unused legacy field kept for scene serialization compatibility")]
        public float velocityTurnAlign = 0.0f;
        [Tooltip("Gentle yaw responsiveness assist during cornering")]
        public float steerYawAssist = 0.45f;
        [Tooltip("Lower stiffness during handbrake for arcade slides (default: 0.35)")]
        [Range(0.1f, 1.0f)]
        public float handbrakeDriftStiffness = 0.35f;
        [Tooltip("Deceleration (m/s^2) when holding handbrake")]
        public float handbrakeDecelAssist = 11f;
        [Tooltip("Extra downforce to keep vehicle planted over crests and jumps")]
        public float downforce = 165f;
        [Tooltip("Anti-roll stabilizer bar force to prevent rollovers on hard turns")]
        public float antiRollForce = 5500f;
        [Tooltip("Center of mass offset (Y should be negative to lower center of gravity)")]
        public Vector3 centerOfMassOffset = new Vector3(0f, -0.45f, 0.05f);

        [Header("Current Telemetry")]
        [SerializeField] private float currentSpeedMph;
        [SerializeField] private float currentThrottle;
        [SerializeField] private float currentSteer;
        [SerializeField] private bool isHandbrakeActive;
        [SerializeField] private float currentEngineRpm;
        [SerializeField] private int currentSimGear = 2;

        [SerializeField] private Rigidbody rb;
        private float rawSteerInput;
        private float discreteSteerState;
        private float fullLockDriftFactor;
        private float sustainedTurnDrift;
        private ParticleSystem tireSmokeRL;
        private ParticleSystem tireSmokeRR;

        // Vehicle simulation physics instances
        private readonly MM2Engine simEngine = new MM2Engine();
        private readonly MM2Transmission simTrans = new MM2Transmission();
        private readonly MM2Drivetrain poweredDrivetrain = new MM2Drivetrain();
        private readonly MM2Drivetrain freeDrivetrain = new MM2Drivetrain();
        private readonly MM2Wheel[] simWheels =
        {
            new MM2Wheel(), // 0: Front Left  (+0x4B8 in vehCarSim)
            new MM2Wheel(), // 1: Front Right (+0x724 in vehCarSim)
            new MM2Wheel(), // 2: Rear Left   (+0x990 in vehCarSim)
            new MM2Wheel()  // 3: Rear Right  (+0xBFC in vehCarSim)
        };
        private bool simInitialized;

        public static bool IsInputLocked =>
            MM2.UI.MM2RaceManager.Instance != null &&
            (MM2.UI.MM2RaceManager.Instance.IsCountdownLockActive ||
             MM2.UI.MM2RaceManager.Instance.IsRaceFinished ||
             MM2.UI.MM2RaceManager.Instance.IsSinkingInWater ||
             MM2.UI.MM2RaceManager.Instance.IsPlayerTotaled ||
             MM2.UI.MM2RaceManager.Instance.IsCruiseRepairZoomingIn);
        public MM2VehicleDamage VehicleDamage { get; set; }
        public float CurrentSpeedMph => currentSpeedMph;
        public float CurrentThrottle => currentThrottle;
        public float CurrentSteer => currentSteer;
        public bool IsHandbrakeActive => isHandbrakeActive;
        public float CurrentEngineRPM => currentEngineRpm;
        public int CurrentSimGear => currentSimGear;
        public Rigidbody VehicleRigidbody => rb != null ? rb : (rb = GetComponent<Rigidbody>());
        public float ForwardSpeedMs => VehicleRigidbody != null ? Vector3.Dot(VehicleRigidbody.linearVelocity, transform.forward) : 0f;
        public bool IsReversing
        {
            get
            {
                float fwd = ForwardSpeedMs;
                if (currentThrottle > 0.05f || fwd > 0.25f) return false;
                return fwd < -0.45f || (fwd <= 0.25f && currentThrottle < -0.05f);
            }
        }
        public MM2Engine SimEngine => simEngine;
        public MM2Transmission SimTransmission => simTrans;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Enforce upgraded arcade base tune even if scene/prefab serialized older values
            EnforceMinimumBaseTune();
            ApplyPhysicsAndWheelTune();
        }

        /// <summary>
        /// Applies the `.vehcarsim`, `.vehGyro`, and `.asNode` configuration to this vehicle.
        /// </summary>
        public void ApplyMM2CarSimConfig(MM2CarSimConfig cfg)
        {
            if (cfg == null) return;
            carSimConfig = cfg;
            simInitialized = false;
        }

        /// <summary>
        /// Ensures older serialized scene/prefab values are upgraded to the high-grip, strong-brake MM2 base tune.
        /// </summary>
        public void EnforceMinimumBaseTune()
        {
            if (vehicleMassKg <= 100f || vehicleMassKg > 3800f) vehicleMassKg = 1300f;
            if (carSimConfig != null && carSimConfig.TransHigh > 30f)
            {
                float redlineRatio = Mathf.Clamp(carSimConfig.EngineMaxRPM / Mathf.Max(1000f, carSimConfig.EngineOptRPM), 1.05f, 1.25f);
                maxSpeedMph = carSimConfig.TransHigh * redlineRatio;
            }
            else if (maxSpeedMph > 145f || maxSpeedMph < 60f)
            {
                maxSpeedMph = 135f;
            }
            if (motorTorque < 3200f) motorTorque = 3600f;
            if (brakeTorque < 12000f) brakeTorque = 16000f;
            if (brakeDecelAssist < 15f) brakeDecelAssist = 24f;
            if (coastBrakeTorque < 1000f) coastBrakeTorque = 2200f;
            if (coastDeceleration < 3.0f) coastDeceleration = 4.8f;
            if (coastLinearDrag < 0.04f) coastLinearDrag = 0.08f;
            if (maxSteerAngle > 30f || maxSteerAngle < 18f) maxSteerAngle = 24f;
            if (highSpeedSteerReduction > 0.45f || highSpeedSteerReduction < 0.20f) highSpeedSteerReduction = 0.34f;
            if (forwardGripStiffness < 2.0f || forwardGripStiffness > 2.8f) forwardGripStiffness = 2.45f;
            if (frontSidewaysGripStiffness < 1.8f || frontSidewaysGripStiffness > 2.5f) frontSidewaysGripStiffness = 2.15f;
            if (rearSidewaysGripStiffness < 1.6f || rearSidewaysGripStiffness > 2.3f) rearSidewaysGripStiffness = 1.95f;
            if (lateralGripAssist < 1.5f || lateralGripAssist > 5.0f) lateralGripAssist = 3.2f;
            velocityTurnAlign = 0.0f;
            if (steerYawAssist > 0.8f || steerYawAssist < 0.2f) steerYawAssist = 0.45f;
            if (handbrakeDecelAssist < 6.0f) handbrakeDecelAssist = 11f;
            if (downforce < 140f) downforce = 165f;
            if (antiRollForce < 5000f) antiRollForce = 5500f;
        }

        /// <summary>
        /// Initializes the vehicle simulation subsystems (engine, transmission, drivetrain, and wheels)
        /// and applies Rigidbody mass, inertia box, center of mass, and WheelCollider parameters.
        /// </summary>
        public void ApplyPhysicsAndWheelTune()
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            if (carSimConfig == null) carSimConfig = new MM2CarSimConfig();
            carSimConfig.EnsureValidDefaults();

            carSimConfig.Mass = vehicleMassKg;
            if (carSimConfig.TransHigh > 30f)
            {
                float redlineRatio = Mathf.Clamp(carSimConfig.EngineMaxRPM / Mathf.Max(1000f, carSimConfig.EngineOptRPM), 1.05f, 1.25f);
                maxSpeedMph = carSimConfig.TransHigh * redlineRatio;
            }

            if (rb != null)
            {
                rb.mass = vehicleMassKg;
                rb.centerOfMass = centerOfMassOffset;
                rb.ResetInertiaTensor();
                rb.angularDamping = 2.5f;
            }

            // Initialize MM2Wheels
            Vector3 flPos = frontLeftCollider != null ? frontLeftCollider.transform.localPosition : new Vector3(-0.74f, 0.34f, 1.40f);
            Vector3 frPos = frontRightCollider != null ? frontRightCollider.transform.localPosition : new Vector3(0.74f, 0.34f, 1.40f);
            Vector3 rlPos = rearLeftCollider != null ? rearLeftCollider.transform.localPosition : new Vector3(-0.74f, 0.34f, -1.40f);
            Vector3 rrPos = rearRightCollider != null ? rearRightCollider.transform.localPosition : new Vector3(0.74f, 0.34f, -1.40f);
            float fRad = frontLeftCollider != null ? frontLeftCollider.radius : 0.35f;
            float rRad = rearLeftCollider != null ? rearLeftCollider.radius : 0.35f;

            // Ensure the chassis BoxCollider has generous ground clearance above curbs (bottom >= 0.48m)
            // and zero friction so bumpers/underbody never snag or high-center on sidewalks or steep hills.
            ConfigureForgivingChassisCollider(Mathf.Max(fRad, rRad));

            simWheels[0].Initialize(carSimConfig.WheelFront, flPos, fRad, vehicleMassKg, carSimConfig.CenterOfGravity, true);
            simWheels[1].Initialize(carSimConfig.WheelFront, frPos, fRad, vehicleMassKg, carSimConfig.CenterOfGravity, true);
            simWheels[2].Initialize(carSimConfig.WheelBack, rlPos, rRad, vehicleMassKg, carSimConfig.CenterOfGravity, false);
            simWheels[3].Initialize(carSimConfig.WheelBack, rrPos, rRad, vehicleMassKg, carSimConfig.CenterOfGravity, false);

            // Initialize MM2Engine & MM2Transmission
            simEngine.ApplyConfig(carSimConfig);
            float driveWheelRadius = (carSimConfig.DrivetrainType == 1) ? fRad : rRad;
            simTrans.ApplyConfig(carSimConfig, driveWheelRadius, simEngine);

            // Configure DrivetrainType (0 = RWD, 1 = FWD, 2 = AWD)
            if (carSimConfig.DrivetrainType == 1)
            {
                // Front-Wheel Drive
                poweredDrivetrain.Configure(
                    carSimConfig.DrivetrainAngInertia,
                    carSimConfig.DrivetrainBrakeDynamicCoef,
                    carSimConfig.DrivetrainBrakeStaticCoef,
                    true,
                    simWheels[0], simWheels[1]);
                freeDrivetrain.Configure(
                    carSimConfig.FreetrainAngInertia,
                    carSimConfig.FreetrainBrakeDynamicCoef,
                    carSimConfig.FreetrainBrakeStaticCoef,
                    false,
                    simWheels[2], simWheels[3]);
            }
            else if (carSimConfig.DrivetrainType == 2)
            {
                // All-Wheel Drive
                poweredDrivetrain.Configure(
                    carSimConfig.DrivetrainAngInertia,
                    carSimConfig.DrivetrainBrakeDynamicCoef,
                    carSimConfig.DrivetrainBrakeStaticCoef,
                    true,
                    simWheels[0], simWheels[1], simWheels[2], simWheels[3]);
                freeDrivetrain.Configure(
                    carSimConfig.FreetrainAngInertia,
                    carSimConfig.FreetrainBrakeDynamicCoef,
                    carSimConfig.FreetrainBrakeStaticCoef,
                    false);
            }
            else
            {
                // Rear-Wheel Drive (default)
                poweredDrivetrain.Configure(
                    carSimConfig.DrivetrainAngInertia,
                    carSimConfig.DrivetrainBrakeDynamicCoef,
                    carSimConfig.DrivetrainBrakeStaticCoef,
                    true,
                    simWheels[2], simWheels[3]);
                freeDrivetrain.Configure(
                    carSimConfig.FreetrainAngInertia,
                    carSimConfig.FreetrainBrakeDynamicCoef,
                    carSimConfig.FreetrainBrakeStaticCoef,
                    false,
                    simWheels[0], simWheels[1]);
            }

            ConfigureWheelColliderFromMM2(frontLeftCollider, simWheels[0], forwardGripStiffness, frontSidewaysGripStiffness);
            ConfigureWheelColliderFromMM2(frontRightCollider, simWheels[1], forwardGripStiffness, frontSidewaysGripStiffness);
            ConfigureWheelColliderFromMM2(rearLeftCollider, simWheels[2], forwardGripStiffness, rearSidewaysGripStiffness);
            ConfigureWheelColliderFromMM2(rearRightCollider, simWheels[3], forwardGripStiffness, rearSidewaysGripStiffness);

            simInitialized = true;
        }

        /// <summary>
        /// Ensures the chassis BoxCollider bottom sits well above street curbs (>= 0.48m)
        /// and uses a zero-friction PhysicsMaterial so bumpers and the underbody never snag or beach on curbs.
        /// </summary>
        private void ConfigureForgivingChassisCollider(float maxWheelRadius)
        {
            BoxCollider boxCol = GetComponent<BoxCollider>();
            if (boxCol == null) return;

            boxCol.enabled = true;
            boxCol.isTrigger = false;

            // Keep the bottom of the chassis box above the wheel hubs and above all 15-30cm street curbs
            float minBottomY = Mathf.Max(0.42f, maxWheelRadius * 1.25f);
            float topY = Mathf.Max(minBottomY + 0.80f, boxCol.center.y + boxCol.size.y * 0.5f);
            float newSizeY = topY - minBottomY;
            float newCenterY = minBottomY + newSizeY * 0.5f;

            // Keep full chassis width/length while avoiding sharp overhang snags on angled curbs
            float newSizeX = Mathf.Clamp(boxCol.size.x, 1.65f, 1.82f);
            float newSizeZ = Mathf.Clamp(boxCol.size.z, 4.10f, 4.55f);

            boxCol.size = new Vector3(newSizeX, newSizeY, newSizeZ);
            boxCol.center = new Vector3(boxCol.center.x, newCenterY, boxCol.center.z);

            if (boxCol.sharedMaterial == null || boxCol.sharedMaterial.dynamicFriction > 0.01f)
            {
                var slipMat = new PhysicsMaterial("MM2_ChassisSlip")
                {
                    dynamicFriction = 0f,
                    staticFriction = 0f,
                    bounciness = 0.05f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounceCombine = PhysicsMaterialCombine.Minimum
                };
                boxCol.sharedMaterial = slipMat;
            }
        }

        private void ConfigureWheelColliderFromMM2(
            WheelCollider col,
            MM2Wheel simWheel,
            float forwardStiffness,
            float sidewaysStiffness)
        {
            if (col == null || simWheel == null) return;

            col.wheelDampingRate = 0.4f;

            // Give the WheelCollider 26cm of suspension travel so it can swallow 15-22cm street curbs
            // without bottoming out against the suspension bump-stop.
            if (col.suspensionDistance < 0.24f || col.suspensionDistance > 0.30f)
            {
                col.suspensionDistance = 0.26f;
            }
            JointSpring spring = col.suspensionSpring;
            spring.spring = Mathf.Clamp(spring.spring, 34000f, 45000f);
            spring.damper = Mathf.Clamp(spring.damper, 4200f, 6500f);
            spring.targetPosition = 0.5f;
            col.suspensionSpring = spring;

            float optSlip = Mathf.Clamp(simWheel.Config.OptimumSlipPercent, 0.10f, 0.25f);
            float slidingRatio = Mathf.Clamp(simWheel.Config.SlidingFric / Mathf.Max(0.5f, simWheel.Config.StaticFric), 0.75f, 1.0f);

            float weatherMult = GetEffectiveWeatherFriction();

            // Longitudinal friction curve shaped from MM2 OptimumSlipPercent, StaticFric & SlidingFric
            WheelFrictionCurve fFric = col.forwardFriction;
            fFric.extremumSlip = optSlip * 2.0f;
            fFric.extremumValue = 1.20f;
            fFric.asymptoteSlip = optSlip * 4.5f;
            fFric.asymptoteValue = 1.20f * slidingRatio;
            fFric.stiffness = forwardStiffness * weatherMult;
            col.forwardFriction = fFric;

            // Lateral friction curve shaped from MM2 OptimumSlipPercent, StaticFric & SlidingFric
            WheelFrictionCurve sFric = col.sidewaysFriction;
            sFric.extremumSlip = optSlip * 1.35f;
            sFric.extremumValue = 1.25f;
            sFric.asymptoteSlip = optSlip * 3.5f;
            sFric.asymptoteValue = 1.25f * slidingRatio;
            sFric.stiffness = sidewaysStiffness * weatherMult;
            col.sidewaysFriction = sFric;
        }

        public float GetEffectiveWeatherFriction()
        {
            float handling = carSimConfig != null ? carSimConfig.CarFrictionHandling : 1.0f;
            return MM2EnvironmentManager.GetEffectiveTireFrictionMultiplier(handling);
        }

        public void RefreshWeatherTireGrip()
        {
            ConfigureWheelColliderFromMM2(frontLeftCollider, simWheels[0], forwardGripStiffness, frontSidewaysGripStiffness);
            ConfigureWheelColliderFromMM2(frontRightCollider, simWheels[1], forwardGripStiffness, frontSidewaysGripStiffness);
            ConfigureWheelColliderFromMM2(rearLeftCollider, simWheels[2], forwardGripStiffness, rearSidewaysGripStiffness);
            ConfigureWheelColliderFromMM2(rearRightCollider, simWheels[3], forwardGripStiffness, rearSidewaysGripStiffness);
        }

        private void Update()
        {
            ReadInput();
            UpdateWheelMeshes();
            UpdateTireSmoke();
        }

        private void FixedUpdate()
        {
            if (!simInitialized || rb == null || simWheels[0].RestNormalLoad <= 0f)
            {
                ApplyPhysicsAndWheelTune();
            }

            float dt = Time.fixedDeltaTime;
            currentSpeedMph = rb.linearVelocity.magnitude * 2.23694f;

            StepDiscreteSteeringFilter(dt);
            StepMM2Physics(dt);
            ApplySteering();
            ApplyDriveAndBrakes();
            StepCurbClimbAssist();
            ApplyCorneringGripAssist();
            ApplyAntiRollBars();
            ApplyDownforce();
        }

        private void ReadInput()
        {
            if (IsInputLocked || (VehicleDamage != null && VehicleDamage.IsTotaled))
            {
                rawSteerInput = 0f;
                currentThrottle = 0f;
                var rm = MM2.UI.MM2RaceManager.Instance;
                isHandbrakeActive = (VehicleDamage != null && VehicleDamage.IsTotaled) ||
                                    (rm != null && (rm.IsRaceFinished || rm.IsPlayerTotaled || rm.IsCruiseRepairZoomingIn) && !rm.IsSinkingInWater);
                UpdateVehicleLights();
                return;
            }

            float steer = 0f;
            float throttle = 0f;
            bool handbrake = false;

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) throttle -= 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steer -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steer += 1f;
                if (kb.spaceKey.isPressed) handbrake = true;
                if (kb.lKey.wasPressedThisFrame) SetHeadlights(!headlightsOn);
            }

            var gp = Gamepad.current;
            if (gp != null)
            {
                float stickX = gp.leftStick.x.ReadValue();
                if (Mathf.Abs(stickX) > 0.08f) steer = stickX;

                float rTrigger = gp.rightTrigger.ReadValue();
                float lTrigger = gp.leftTrigger.ReadValue();
                throttle += (rTrigger - lTrigger);

                if (gp.buttonSouth.isPressed || gp.rightShoulder.isPressed)
                    handbrake = true;
            }
#else
            steer = Input.GetAxis("Horizontal");
            throttle = Input.GetAxis("Vertical");
            handbrake = Input.GetKey(KeyCode.Space);
            if (Input.GetKeyDown(KeyCode.L)) SetHeadlights(!headlightsOn);
#endif

            rawSteerInput = Mathf.Clamp(steer, -1f, 1f);
            currentThrottle = Mathf.Clamp(throttle, -1f, 1f);
            isHandbrakeActive = handbrake;
            UpdateVehicleLights();
        }

        /// <summary>
        /// Discrete keyboard steering filter:
        /// 1. Interpolates `DiscreteSteeringDeltaOut`, `DiscreteSteeringDeltaIn`, and `DiscreteSteeringFilter`
        ///    between `SpeedBaseLow` (5.0 m/s) and `SpeedBaseHi` (44.6 m/s) from `.asNode`.
        /// 2. Moves linear discrete state `discreteSteerState` at `deltaOut` when turning outward
        ///    and at `deltaIn` (`5.0 /s`) when releasing or counter-steering.
        /// 3. Applies the non-linear power filter `sign(s) * pow(|s|, DiscreteSteeringFilter)`
        ///    so brief arrow-key taps produce small, fine heading adjustments at 70+ mph.
        /// </summary>
        private void StepDiscreteSteeringFilter(float dt)
        {
            float speedMs = rb != null ? rb.linearVelocity.magnitude : 0f;
            float speedRange = Mathf.Max(1f, carSimConfig.SpeedBaseHi - carSimConfig.SpeedBaseLow);
            float tSpeed = Mathf.Clamp01((speedMs - carSimConfig.SpeedBaseLow) / speedRange);

            // Smooth, immediate outward ramp with no initial deadzone (faster when initiating a handbrake turn)
            float deltaOut = Mathf.Lerp(3.1f, 2.0f, tSpeed);
            if (isHandbrakeActive) deltaOut *= 1.75f;
            float deltaIn = Mathf.Max(5.5f, Mathf.Lerp(carSimConfig.DiscreteSteeringDeltaInLo, carSimConfig.DiscreteSteeringDeltaInHi, tSpeed));
            const float filterExp = 1.05f;

            // Use deltaOut only when |rawSteerInput| >= |discreteSteerState| and same sign
            bool sameDirection = (rawSteerInput * discreteSteerState) > 0f || Mathf.Abs(discreteSteerState) < 0.001f;
            bool isTurningOutward = sameDirection && Mathf.Abs(rawSteerInput) >= Mathf.Abs(discreteSteerState) && Mathf.Abs(rawSteerInput) > 0.01f;

            // Give an immediate initial step when starting a turn from center so the first frame responds right away
            if (isTurningOutward && Mathf.Abs(discreteSteerState) < 0.10f)
            {
                discreteSteerState = Mathf.Sign(rawSteerInput) * 0.10f;
            }

            float rate = isTurningOutward ? deltaOut : deltaIn;
            discreteSteerState = Mathf.MoveTowards(discreteSteerState, rawSteerInput, rate * dt);

            // Non-linear power filter: sign(s) * pow(|s|, filterExp)
            float absS = Mathf.Abs(discreteSteerState);
            currentSteer = (absS > 1e-4f)
                ? (Mathf.Sign(discreteSteerState) * Mathf.Pow(absS, filterExp))
                : 0f;
        }

        /// <summary>
        /// Calculates the speed-sensitive steering factor (`SSSFactor`) from `SSSThreshold` and `SSSValue`.
        /// </summary>
        private float CalcSSSFactor(float speedMs)
        {
            if (carSimConfig.SSSThreshold <= 0.001f)
                return 1.0f;

            if (speedMs < carSimConfig.SSSThreshold)
            {
                return 1.0f + (carSimConfig.SSSValue - 1.0f) * (speedMs / carSimConfig.SSSThreshold);
            }
            return carSimConfig.SSSValue;
        }

        /// <summary>
        /// Steps the vehicle simulation subsystems (wheels, transmission, drivetrain, aerodynamics, and gyro).
        /// </summary>
        private void StepMM2Physics(float dt)
        {
            if (rb == null) return;

            Vector3 vel = rb.linearVelocity;
            float forwardSpeed = Vector3.Dot(vel, transform.forward);
            float absForwardSpeed = Mathf.Abs(forwardSpeed);

            // 1. Compute SSS steering, throttle, brake, and differential handbrake
            float sssFactor = CalcSSSFactor(absForwardSpeed);
            float steeredInput = currentSteer * sssFactor;

            bool wantsReverse = (forwardSpeed < -0.6f && currentThrottle < -0.05f) ||
                                (Mathf.Abs(forwardSpeed) <= 0.6f && currentThrottle < -0.05f);
            bool wantsBrake = (forwardSpeed > 0.6f && currentThrottle < -0.05f) ||
                              (forwardSpeed < -0.6f && currentThrottle > 0.05f);

            float engineThrottle = 0f;
            float pedalBrake = 0f;
            if (wantsBrake)
            {
                pedalBrake = Mathf.Abs(currentThrottle);
                engineThrottle = 0f;
            }
            else if (wantsReverse)
            {
                simTrans.SetCurrentGear(0); // Reverse gear
                engineThrottle = Mathf.Abs(currentThrottle);
            }
            else
            {
                if (simTrans.CurrentGear <= 1 && (forwardSpeed >= -0.25f || currentThrottle > 0.05f))
                {
                    simTrans.SetCurrentGear(2); // 1st forward gear
                }
                engineThrottle = Mathf.Max(0f, currentThrottle);
            }

            float rawHandbrake = isHandbrakeActive ? 1.0f : 0f;
            float frontHandbrake = rawHandbrake < 0f ? -rawHandbrake : 0f;
            float rearHandbrake = Mathf.Max(0f, rawHandbrake);

            // Standing burnout check
            float rearBrake = pedalBrake;
            if (pedalBrake > 0.95f && engineThrottle > 0.95f && absForwardSpeed < 1.0f)
            {
                rearBrake = 0f;
            }

            // Differential rear handbrake multipliers when steering
            float rlHandbrakeScale = (currentSteer <= 0f) ? (1.0f - currentSteer) : 1.0f;
            float rrHandbrakeScale = (currentSteer > 0f) ? (1.0f + currentSteer) : 1.0f;

            simWheels[0].SetInputs(steeredInput, pedalBrake, frontHandbrake);
            simWheels[1].SetInputs(steeredInput, pedalBrake, frontHandbrake);
            simWheels[2].SetInputs(-steeredInput, rearBrake, rearHandbrake * rlHandbrakeScale);
            simWheels[3].SetInputs(-steeredInput, rearBrake, rearHandbrake * rrHandbrakeScale);

            // 2. Step wheel contact queries & tire forces
            WheelCollider[] cols = { frontLeftCollider, frontRightCollider, rearLeftCollider, rearRightCollider };
            int groundedCount = 0;

            for (int i = 0; i < 4; i++)
            {
                WheelCollider col = cols[i];
                MM2Wheel sw = simWheels[i];
                if (col == null)
                {
                    sw.IsGrounded = false;
                    continue;
                }

                if (col.GetGroundHit(out WheelHit hit))
                {
                    sw.IsGrounded = true;
                    groundedCount++;

                    // Use MM2's 19.6 m/s^2 normal load blended with clamped live suspension weight transfer
                    float liveForce = Mathf.Clamp(hit.force * (MM2Wheel.GravityMagnitude / 9.81f), 0f, sw.RestNormalLoad * 2.5f);
                    float normalLoad = Mathf.Lerp(sw.RestNormalLoad, liveForce, 0.35f);

                    Vector3 hubWorldPos = col.transform.position;
                    Vector3 hubVel = rb.GetPointVelocity(hubWorldPos);
                    Quaternion steerRot = Quaternion.AngleAxis(sw.SteerAngleRad * Mathf.Rad2Deg, transform.up);
                    Vector3 wheelRightWorld = steerRot * transform.right;

                    sw.ComputeTireForce(
                        hubVel,
                        hit.normal,
                        wheelRightWorld,
                        normalLoad,
                        carSimConfig.CarFrictionHandling,
                        dt);
                }
                else
                {
                    sw.IsGrounded = false;
                    sw.ComputeTireForce(Vector3.zero, Vector3.up, transform.right, 0f, 1f, dt);
                }
            }

            // 3. Step transmission & drivetrain
            // Keep axle angular velocity anchored to road speed when grounded so RPM and gear shifts track true road speed
            float driveRadius = Mathf.Max(0.2f, (carSimConfig.DrivetrainType == 1) ? simWheels[0].Radius : simWheels[2].Radius);
            float roadWheelAngVel = forwardSpeed / driveRadius;
            if (groundedCount > 0)
            {
                poweredDrivetrain.AxleAngVel = roadWheelAngVel;
                freeDrivetrain.AxleAngVel = roadWheelAngVel;
            }

            simTrans.Update(currentSpeedMph, engineThrottle, groundedCount, dt);
            poweredDrivetrain.Update(simEngine, simTrans, engineThrottle, dt);
            freeDrivetrain.Update(null, null, 0f, dt);

            if (groundedCount > 0)
            {
                float gearRatio = Mathf.Abs(simTrans.GetCurrentGearRatio());
                if (gearRatio > 0.001f)
                {
                    float roadRps = Mathf.Abs(roadWheelAngVel) * gearRatio;
                    simEngine.CurrentRPM = Mathf.Clamp(roadRps * 9.549296f, simEngine.IdleRPM, simEngine.MaxRPM);
                    simEngine.CurrentRPS = simEngine.CurrentRPM * 0.104719758f;
                }
            }

            currentEngineRpm = simEngine.CurrentRPM;
            currentSimGear = simTrans.CurrentGear;

            // 4. Step aerodynamics: Body-space rotational damping & aerodynamic drag/downforce
            MM2Aero.ComputeAeroForcesAndTorques(
                carSimConfig,
                transform,
                vel,
                rb.angularVelocity,
                out Vector3 aeroForce,
                out Vector3 aeroAngularAccel);

            if (aeroForce.sqrMagnitude > 0.01f)
            {
                rb.AddForce(aeroForce, ForceMode.Force);
            }
            if (aeroAngularAccel.sqrMagnitude > 0.001f)
            {
                rb.AddTorque(aeroAngularAccel * 0.25f, ForceMode.Acceleration);
            }

            // 5. Step gyro: Drift yaw assist, Spin180/Reverse180, Pitch/Roll leveling
            Vector3 gyroAngularAccel = MM2Gyro.ComputeGyroTorque(
                carSimConfig,
                transform,
                steeredInput,
                currentSteer,
                pedalBrake,
                isHandbrakeActive,
                roadWheelAngVel,
                groundedCount);

            if (gyroAngularAccel.sqrMagnitude > 0.001f)
            {
                rb.AddTorque(gyroAngularAccel, ForceMode.Acceleration);
            }
        }

        public void SetHeadlights(bool enabled)
        {
            headlightsOn = enabled;
            if (headlightsGlowObj != null && headlightsGlowObj.activeSelf != headlightsOn)
            {
                headlightsGlowObj.SetActive(headlightsOn);
            }
            EnsureHeadlightBeams();
            if (leftHeadlightBeam != null) leftHeadlightBeam.enabled = headlightsOn;
            if (rightHeadlightBeam != null) rightHeadlightBeam.enabled = headlightsOn;
        }

        private void EnsureHeadlightBeams()
        {
            if (leftHeadlightBeam != null && rightHeadlightBeam != null) return;

            float beamX = frontLeftCollider != null ? Mathf.Abs(frontLeftCollider.transform.localPosition.x) * 0.85f : 0.62f;
            float beamY = frontLeftCollider != null ? frontLeftCollider.transform.localPosition.y + 0.35f : 0.68f;
            float beamZ = frontLeftCollider != null ? Mathf.Abs(frontLeftCollider.transform.localPosition.z) + 0.65f : 2.05f;

            leftHeadlightBeam = GetOrCreateHeadlightSpot("MM2_HeadlightBeam_L", new Vector3(-beamX, beamY, beamZ));
            rightHeadlightBeam = GetOrCreateHeadlightSpot("MM2_HeadlightBeam_R", new Vector3(beamX, beamY, beamZ));
        }

        private Light GetOrCreateHeadlightSpot(string childName, Vector3 localPos)
        {
            Transform t = transform.Find(childName);
            GameObject go = t != null ? t.gameObject : new GameObject(childName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(7.5f, 0f, 0f);

            Light spot = go.GetComponent<Light>();
            if (spot == null)
            {
                spot = go.AddComponent<Light>();
            }
            spot.type = LightType.Spot;
            spot.color = new Color(1.0f, 0.96f, 0.86f, 1f);
            spot.range = 68f;
            spot.spotAngle = 56f;
            spot.innerSpotAngle = 28f;
            spot.intensity = 18f;
            spot.shadows = LightShadows.None;
            spot.enabled = headlightsOn;
            return spot;
        }

        private void UpdateVehicleLights()
        {
            if (headlightsGlowObj != null && headlightsGlowObj.activeSelf != headlightsOn)
            {
                headlightsGlowObj.SetActive(headlightsOn);
            }
            if (headlightsOn && (leftHeadlightBeam == null || rightHeadlightBeam == null))
            {
                EnsureHeadlightBeams();
            }
            if (leftHeadlightBeam != null && leftHeadlightBeam.enabled != headlightsOn)
            {
                leftHeadlightBeam.enabled = headlightsOn;
            }
            if (rightHeadlightBeam != null && rightHeadlightBeam.enabled != headlightsOn)
            {
                rightHeadlightBeam.enabled = headlightsOn;
            }

            if (rb == null) return;
            float forwardSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);
            bool braking = isHandbrakeActive ||
                           (forwardSpeed > 0.5f && currentThrottle < -0.05f) ||
                           (Mathf.Abs(forwardSpeed) <= 0.5f && currentThrottle < -0.05f);
            bool reversing = forwardSpeed < -0.4f && currentThrottle < -0.05f;

            if (brakelightsGlowObj != null)
            {
                bool showBrake = braking && !reversing;
                if (brakelightsGlowObj.activeSelf != showBrake) brakelightsGlowObj.SetActive(showBrake);
            }
            if (reverselightsGlowObj != null)
            {
                if (reverselightsGlowObj.activeSelf != reversing) reverselightsGlowObj.SetActive(reversing);
            }
        }

        private void ApplySteering()
        {
            if (frontLeftCollider == null || frontRightCollider == null) return;

            // Instantaneous smooth cornering slide factor based on current steering angle and speed,
            // plus a gentle progressive build in the latter half of sustained full-lock turns.
            float speedFactor = Mathf.Clamp01((currentSpeedMph - 16f) / 28f);
            float steerSlide = Mathf.SmoothStep(0.35f, 0.95f, Mathf.Abs(currentSteer)) * speedFactor;

            if (Mathf.Abs(currentSteer) > 0.72f && currentSpeedMph > 16f && (rawSteerInput * currentSteer) > 0f)
            {
                sustainedTurnDrift = Mathf.MoveTowards(sustainedTurnDrift, speedFactor, Time.fixedDeltaTime * 1.15f);
            }
            else
            {
                sustainedTurnDrift = Mathf.MoveTowards(sustainedTurnDrift, 0f, Time.fixedDeltaTime * 2.8f);
            }

            if (isHandbrakeActive && Mathf.Abs(currentSteer) > 0.20f && currentSpeedMph > 10f)
            {
                fullLockDriftFactor = 1.0f;
            }
            else
            {
                fullLockDriftFactor = Mathf.Clamp01(steerSlide * 0.70f + sustainedTurnDrift * 0.30f);
            }

            // Smooth single-curve speed reduction (never jumps or switches modes mid-turn)
            float speedMs = currentSpeedMph * 0.44704f;
            float steerReduction = Mathf.Max(
                highSpeedSteerReduction,
                1.0f / (1.0f + 0.036f * Mathf.Max(0f, speedMs - 10.0f)));
            if (isHandbrakeActive)
            {
                steerReduction = Mathf.Max(steerReduction, 0.80f);
            }

            float flAckermannDeg = simWheels[0].SteerAngleRad * Mathf.Rad2Deg * steerReduction;
            float frAckermannDeg = simWheels[1].SteerAngleRad * Mathf.Rad2Deg * steerReduction;

            frontLeftCollider.steerAngle = Mathf.Clamp(flAckermannDeg, -maxSteerAngle, maxSteerAngle);
            frontRightCollider.steerAngle = Mathf.Clamp(frAckermannDeg, -maxSteerAngle, maxSteerAngle);
        }

        private void ApplyDriveAndBrakes()
        {
            if (rearLeftCollider == null || rearRightCollider == null) return;

            int groundedWheels = CountGroundedWheels();
            bool isGrounded = groundedWheels >= 1;
            Vector3 vel = rb.linearVelocity;
            Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
            float horizSpeed = horizVel.magnitude;

            // Incorporate MM2Engine torque curve multiplier with motorTorque
            float engineCurveBoost = 1.0f;
            if (simEngine.MaxTorque > 10f)
            {
                float fullTorqueAtRpm = simEngine.CalcTorqueAtFullThrottle(simEngine.CurrentRPM);
                engineCurveBoost = Mathf.Clamp(fullTorqueAtRpm / simEngine.MaxTorque, 0.85f, 1.35f);
            }

            float effectiveTorque = (currentSpeedMph < maxSpeedMph)
                ? (currentThrottle * motorTorque * engineCurveBoost)
                : 0f;

            if (isHandbrakeActive)
            {
                // Lock rear wheels using MM2 HandbrakeCoef while keeping front wheels unbraked when steering
                // so the front axle acts as a crisp pivot for 90/180-degree handbrake turns.
                SetDriveTorque(0f);
                rearLeftCollider.brakeTorque = Mathf.Max(brakeTorque * 1.5f, simWheels[2].CurrentBrakeTorque);
                rearRightCollider.brakeTorque = Mathf.Max(brakeTorque * 1.5f, simWheels[3].CurrentBrakeTorque);

                bool isSteeringHandbrake = Mathf.Abs(rawSteerInput) > 0.10f && horizSpeed > 2.0f;
                float frontHbBrake = isSteeringHandbrake ? 0f : (brakeTorque * 0.35f);
                if (frontLeftCollider != null) frontLeftCollider.brakeTorque = frontHbBrake;
                if (frontRightCollider != null) frontRightCollider.brakeTorque = frontHbBrake;

                SetRearWheelStiffness(isSteeringHandbrake ? 0.22f : handbrakeDriftStiffness);

                if (isGrounded && horizSpeed > 0.25f)
                {
                    if (!isSteeringHandbrake)
                    {
                        Vector3 decelDir = -horizVel.normalized;
                        rb.AddForce(decelDir * handbrakeDecelAssist, ForceMode.Acceleration);
                    }
                }
                else if (isGrounded && horizSpeed <= 0.25f)
                {
                    rb.linearVelocity = new Vector3(0f, vel.y, 0f);
                }
            }
            else
            {
                // Normal Driving, Smooth Cornering Slide, Braking & Off-Throttle Speed Decay
                float dynamicRearStiff = Mathf.Lerp(rearSidewaysGripStiffness, rearSidewaysGripStiffness * 0.50f, fullLockDriftFactor);
                SetRearWheelStiffness(dynamicRearStiff);

                float forwardSpeed = Vector3.Dot(vel, transform.forward);
                bool hasThrottleInput = Mathf.Abs(currentThrottle) > 0.05f;
                bool isBraking = (forwardSpeed > 0.8f && currentThrottle < -0.05f) ||
                                 (forwardSpeed < -0.8f && currentThrottle > 0.05f);

                if (isBraking)
                {
                    // Full 4-wheel braking using MM2Wheel MaxBrakeTorque (19.6 m/s^2 load * StaticFric * BrakeCoef)
                    SetDriveTorque(0f);

                    float flBrake = Mathf.Max(brakeTorque * 1.1f, simWheels[0].CurrentBrakeTorque);
                    float frBrake = Mathf.Max(brakeTorque * 1.1f, simWheels[1].CurrentBrakeTorque);
                    float rlBrake = Mathf.Max(brakeTorque * 0.95f, simWheels[2].CurrentBrakeTorque);
                    float rrBrake = Mathf.Max(brakeTorque * 0.95f, simWheels[3].CurrentBrakeTorque);

                    if (frontLeftCollider != null) frontLeftCollider.brakeTorque = flBrake;
                    if (frontRightCollider != null) frontRightCollider.brakeTorque = frBrake;
                    rearLeftCollider.brakeTorque = rlBrake;
                    rearRightCollider.brakeTorque = rrBrake;

                    // Compute 2G normal-load braking deceleration capacity:
                    // Decel = 19.6 * avgStaticFric * avgBrakeCoef * BrakeDynamicCoef * WeatherFriction
                    float avgStaticFric = 0.5f * (carSimConfig.WheelFront.StaticFric + carSimConfig.WheelBack.StaticFric);
                    float avgBrakeCoef = 0.5f * (carSimConfig.WheelFront.BrakeCoef + carSimConfig.WheelBack.BrakeCoef);
                    float mm2BrakeDecel = MM2Wheel.GravityMagnitude * avgStaticFric * avgBrakeCoef * carSimConfig.DrivetrainBrakeDynamicCoef * 0.72f;
                    float targetBrakeDecel = Mathf.Max(brakeDecelAssist, mm2BrakeDecel) * GetEffectiveWeatherFriction();

                    if (isGrounded && horizSpeed > 0.4f)
                    {
                        Vector3 brakeDir = -horizVel.normalized;
                        rb.AddForce(brakeDir * targetBrakeDecel, ForceMode.Acceleration);
                    }
                    else if (isGrounded && horizSpeed <= 0.4f)
                    {
                        rb.linearVelocity = new Vector3(0f, vel.y, 0f);
                    }
                }
                else if (!hasThrottleInput)
                {
                    // Off-throttle engine braking & tire drag (TireDragCoefLong):
                    // Decays speed briskly so the car comes to a halt within a few seconds at low/medium speeds.
                    SetDriveTorque(0f);

                    float engineZeroTorque = Mathf.Abs(simEngine.CalcTorqueAtZeroThrottle(simEngine.CurrentRPM));
                    float gearRatio = Mathf.Abs(simTrans.GetCurrentGearRatio());
                    float simEngineBrakeTorque = engineZeroTorque * gearRatio * 0.5f;
                    float appliedCoastTorque = Mathf.Max(coastBrakeTorque, simEngineBrakeTorque);

                    if (frontLeftCollider != null) frontLeftCollider.brakeTorque = appliedCoastTorque;
                    if (frontRightCollider != null) frontRightCollider.brakeTorque = appliedCoastTorque;
                    rearLeftCollider.brakeTorque = appliedCoastTorque;
                    rearRightCollider.brakeTorque = appliedCoastTorque;

                    if (isGrounded)
                    {
                        if (horizSpeed > 0.35f)
                        {
                            float engineBrakeDecel = (simEngineBrakeTorque / Mathf.Max(0.2f, simWheels[2].Radius)) / Mathf.Max(500f, vehicleMassKg);
                            float totalCoastDecel = Mathf.Max(coastDeceleration, engineBrakeDecel) + (horizSpeed * coastLinearDrag);
                            float maxStepDecel = horizSpeed / Time.fixedDeltaTime;
                            float appliedDecel = Mathf.Min(totalCoastDecel, maxStepDecel);
                            rb.AddForce(-horizVel.normalized * appliedDecel, ForceMode.Acceleration);
                        }
                        else
                        {
                            // Snap cleanly to a stop at very low speed when off the gas
                            rb.linearVelocity = new Vector3(0f, vel.y, 0f);
                        }
                    }
                }
                else
                {
                    // Accelerating forward or in reverse using
                    // engine torque, gear ratio, coupled rotational inertia (Engine + Drivetrain + Freetrain),
                    // and quadratic tire rolling drag (MM2Drivetrain.ComputeStepDriveAcceleration).
                    if (frontLeftCollider != null) frontLeftCollider.brakeTorque = 0f;
                    if (frontRightCollider != null) frontRightCollider.brakeTorque = 0f;
                    rearLeftCollider.brakeTorque = 0f;
                    rearRightCollider.brakeTorque = 0f;

                    float driveRadius = Mathf.Max(0.2f, (carSimConfig.DrivetrainType == 1) ? simWheels[0].Radius : simWheels[2].Radius);
                    float wheelAngSpeed = Mathf.Abs(forwardSpeed) / driveRadius;

                    // Cancel the 4 WheelColliders' internal wheelDampingRate (4 * 0.4 * w = 1.6 * w)
                    // so PhysX wheels roll smoothly without adding uncalibrated thrust.
                    float dampingCancelTorque = Mathf.Sign(currentThrottle) * Mathf.Max(25f, 0.8f * wheelAngSpeed);
                    SetDriveTorque(dampingCancelTorque);

                    // Allow drive acceleration if any wheel is grounded, OR when briefly cresting a level/downhill street crest
                    // (never while rising off an upward jump ramp).
                    bool canApplyDrive = isGrounded || (vel.y < 1.2f && transform.forward.y < 0.08f && Mathf.Abs(vel.y) < 3.5f);
                    if (canApplyDrive)
                    {
                        float tractionScale = isGrounded ? 1.0f : 0.55f;
                        // Apply slight engine power degradation when heavily damaged (> 70% damage)
                        if (VehicleDamage != null && VehicleDamage.Damage01 > 0.70f)
                        {
                            float dmgLoss = Mathf.InverseLerp(0.70f, 1.0f, VehicleDamage.Damage01);
                            tractionScale *= Mathf.Lerp(1.0f, 0.78f, dmgLoss);
                        }

                        float driveAccel = MM2Drivetrain.ComputeStepDriveAcceleration(
                            carSimConfig,
                            simEngine,
                            simTrans,
                            forwardSpeed,
                            Mathf.Abs(currentThrottle),
                            driveRadius) * tractionScale;

                        // Compensate for 19.62 m/s^2 normal-load gravity on downhill runs and keep high-gear torque strong on uphill rises
                        if (isGrounded && currentThrottle > 0.05f)
                        {
                            float pitchSlope = transform.forward.y;
                            if (pitchSlope < -0.02f)
                            {
                                driveAccel += (-pitchSlope) * 3.5f;
                            }
                            else if (pitchSlope > 0.015f)
                            {
                                driveAccel += pitchSlope * 4.5f;
                            }
                        }

                        if (currentThrottle > 0f && currentSpeedMph >= maxSpeedMph)
                        {
                            driveAccel = Mathf.Min(0f, driveAccel);
                        }
                        else if (currentThrottle < 0f && currentSpeedMph >= simTrans.ReverseMph)
                        {
                            driveAccel = Mathf.Max(0f, driveAccel);
                        }

                        Vector3 driveDir = transform.forward;
                        float maxUpwardDriveY = isGrounded ? 0.22f : 0.0f;
                        driveDir.y = Mathf.Clamp(driveDir.y, -0.35f, maxUpwardDriveY);
                        if (driveDir.sqrMagnitude > 0.001f)
                        {
                            driveDir.Normalize();
                            rb.AddForce(driveDir * driveAccel, ForceMode.Acceleration);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Helps raycast-based WheelColliders roll smoothly up and over 10-32cm street curbs at any angle or speed:
        /// 1. Casts a short low probe near the bottom of each tire along the travel/drive direction.
        /// 2. If a low step is hit while the space 36cm above the tire bottom is clear (curb/sidewalk, not a wall),
        ///    applies a smooth upward+forward wheel climb assist so the tire steps onto the curb effortlessly.
        /// </summary>
        private void StepCurbClimbAssist()
        {
            if (rb == null) return;

            Vector3 vel = rb.linearVelocity;
            Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
            float horizSpeed = horizVel.magnitude;
            bool hasThrottle = Mathf.Abs(currentThrottle) > 0.05f;

            if (!hasThrottle && horizSpeed < 0.8f) return;

            Vector3 moveDir;
            if (horizSpeed > 1.2f)
            {
                moveDir = horizVel / horizSpeed;
            }
            else
            {
                moveDir = (currentThrottle >= 0f) ? transform.forward : -transform.forward;
                moveDir.y = 0f;
                if (moveDir.sqrMagnitude < 0.001f) return;
                moveDir.Normalize();
            }

            WheelCollider[] cols = { frontLeftCollider, frontRightCollider, rearLeftCollider, rearRightCollider };
            int curbHits = 0;

            for (int i = 0; i < 4; i++)
            {
                WheelCollider col = cols[i];
                if (col == null) continue;

                float radius = Mathf.Max(0.22f, col.radius);
                Vector3 hubPos = col.transform.position;
                Vector3 tireBottom = hubPos - transform.up * radius;

                Vector3 lowOrigin = tireBottom + Vector3.up * 0.09f;
                Vector3 highOrigin = tireBottom + Vector3.up * 0.36f;
                float probeDist = radius + 0.28f;

                if (Physics.Raycast(lowOrigin, moveDir, out RaycastHit lowHit, probeDist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    if (lowHit.transform.root == transform.root) continue;

                    // Only treat as a climbable street curb if the face is near-vertical (normal.y < 0.45, not a jump ramp)
                    // and the space 36cm up is clear (not a building/wall)
                    bool highBlocked = Physics.Raycast(highOrigin, moveDir, probeDist + 0.08f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    if (!highBlocked && lowHit.normal.y < 0.45f)
                    {
                        curbHits++;
                        // Smoothly lift this corner of the suspension up onto the curb top
                        if (vel.y < 2.2f)
                        {
                            rb.AddForceAtPosition(Vector3.up * (vehicleMassKg * 2.8f), hubPos, ForceMode.Force);
                        }
                    }
                }
            }

            if (curbHits > 0 && hasThrottle && horizSpeed < 12f)
            {
                rb.AddForce(moveDir * 3.2f, ForceMode.Acceleration);
            }
        }

        private void SetDriveTorque(float torque)
        {
            if (carSimConfig.DrivetrainType == 1)
            {
                // FWD
                if (frontLeftCollider != null) frontLeftCollider.motorTorque = torque;
                if (frontRightCollider != null) frontRightCollider.motorTorque = torque;
                if (rearLeftCollider != null) rearLeftCollider.motorTorque = 0f;
                if (rearRightCollider != null) rearRightCollider.motorTorque = 0f;
            }
            else if (carSimConfig.DrivetrainType == 2)
            {
                // AWD
                float halfTorque = torque * 0.55f;
                if (frontLeftCollider != null) frontLeftCollider.motorTorque = halfTorque;
                if (frontRightCollider != null) frontRightCollider.motorTorque = halfTorque;
                if (rearLeftCollider != null) rearLeftCollider.motorTorque = halfTorque;
                if (rearRightCollider != null) rearRightCollider.motorTorque = halfTorque;
            }
            else
            {
                // RWD
                if (frontLeftCollider != null) frontLeftCollider.motorTorque = 0f;
                if (frontRightCollider != null) frontRightCollider.motorTorque = 0f;
                if (rearLeftCollider != null) rearLeftCollider.motorTorque = torque;
                if (rearRightCollider != null) rearRightCollider.motorTorque = torque;
            }
        }

        private void ApplyCorneringGripAssist()
        {
            int groundedCount = CountGroundedWheels();
            if (groundedCount < 2) return;

            Vector3 vel = rb.linearVelocity;
            Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
            float horizSpeed = horizVel.magnitude;
            if (horizSpeed < 0.5f) return;

            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) return;
            fwd.Normalize();

            Vector3 right = transform.right;
            right.y = 0f;
            right.Normalize();

            float forwardVel = Vector3.Dot(horizVel, fwd);
            float lateralVel = Vector3.Dot(horizVel, right);

            // Scale lateral grip assist by StaticFric and WeatherFriction
            float fricScale = Mathf.Clamp((carSimConfig.WheelFront.StaticFric + carSimConfig.WheelBack.StaticFric) / 6.0f, 0.85f, 1.25f) *
                              GetEffectiveWeatherFriction();

            // 1. Gentle Lateral Tire Damping (no direct velocity rotation so the car never feels "on rails"):
            // Relaxes smoothly as steering angle increases at speed so the rear tires can naturally step out into a slide.
            float baseGrip = isHandbrakeActive
                ? (lateralGripAssist * 0.15f)
                : Mathf.Lerp(lateralGripAssist, lateralGripAssist * 0.18f, fullLockDriftFactor);
            float gripRate = baseGrip * fricScale;
            float maxLatCorrection = Mathf.Abs(lateralVel) / Time.fixedDeltaTime;
            float latAccel = Mathf.Clamp(-lateralVel * gripRate, -maxLatCorrection, maxLatCorrection);
            rb.AddForce(right * latAccel, ForceMode.Acceleration);

            // 2. Natural Tire Scrubbing Drag & Self-Aligning Yaw Balance:
            // When sliding sideways in a hard turn, lateral tire scrub slows the vehicle naturally without forcing velocity onto rails.
            float absLatVel = Mathf.Abs(lateralVel);
            if (!isHandbrakeActive && absLatVel > 1.5f && horizSpeed > 4.0f)
            {
                float scrubDecel = Mathf.Clamp((absLatVel - 1.5f) * 0.35f, 0f, 4.5f) * fricScale;
                rb.AddForce((-horizVel / horizSpeed) * scrubDecel, ForceMode.Acceleration);
            }

            float absSteer = Mathf.Abs(currentSteer);
            if (isHandbrakeActive && horizSpeed > 1.5f)
            {
                // Handbrake Turn (90-degree intersection slide / J-turn):
                // 1. Whip the tail around quickly (~105-120 deg/s) while steering is held, and arrest rotation when steering is released.
                float currentYawRad = Vector3.Dot(rb.angularVelocity, Vector3.up);
                if (Mathf.Abs(rawSteerInput) > 0.08f)
                {
                    float moveSign = (forwardVel >= -1.5f) ? 1f : -1f;
                    float speedYawScale = Mathf.Clamp01((horizSpeed - 1.5f) / 8.0f);
                    float targetHbYawRad = Mathf.Sign(rawSteerInput) * moveSign * Mathf.Lerp(1.45f, 2.05f, absSteer) * speedYawScale;
                    float hbYawAccel = Mathf.Clamp((targetHbYawRad - currentYawRad) * 9.5f, -12.0f, 12.0f);
                    rb.AddTorque(Vector3.up * hbYawAccel, ForceMode.Acceleration);
                }
                else
                {
                    rb.AddTorque(Vector3.up * (-currentYawRad * 6.0f), ForceMode.Acceleration);
                }

                // 2. Scrub sideways velocity across the intersection (~75-85 mph -> 54 -> 32 -> 21 mph)
                // while redirecting part of the entry momentum into ~20-23 mph of forward roll along the new street heading.
                float slideRatio = Mathf.Clamp01(absLatVel / Mathf.Max(2.0f, horizSpeed));
                float broadsideDrag = Mathf.Lerp(handbrakeDecelAssist * 0.70f, 19.5f, slideRatio) * fricScale;
                rb.AddForce((-horizVel / horizSpeed) * broadsideDrag, ForceMode.Acceleration);

                if (absLatVel > 1.5f)
                {
                    float extraLatScrub = -lateralVel * 1.45f * fricScale;
                    rb.AddForce(right * extraLatScrub, ForceMode.Acceleration);
                }

                if (Mathf.Abs(rawSteerInput) > 0.08f && absLatVel > 2.2f)
                {
                    float targetExitForwardMs = Mathf.Min(9.8f, horizSpeed * 0.75f);
                    if (forwardVel < targetExitForwardMs)
                    {
                        float forwardRedirect = Mathf.Clamp((targetExitForwardMs - forwardVel) * 2.2f, 0f, absLatVel * 0.90f) * fricScale;
                        rb.AddForce(fwd * forwardRedirect, ForceMode.Acceleration);
                    }
                }
            }
            else if (!isHandbrakeActive && horizSpeed > 2.0f)
            {
                float moveSign = (forwardVel >= -0.5f) ? 1f : -1f;
                Vector3 targetDir = fwd * moveSign;
                float signedSlipDeg = Vector3.SignedAngle(horizVel, targetDir, Vector3.up);

                // Smooth, continuous turn-in yaw assist right from the start of steering input,
                // building slightly more oversteer rotation in the latter half of sustained full-lock turns.
                if (absSteer > 0.02f)
                {
                    float speedAtten = 1.0f / (1.0f + 0.025f * Mathf.Max(0f, horizSpeed - 12.0f));
                    float lateTurnOversteer = 1.0f + 0.45f * fullLockDriftFactor;
                    float turnYawAccel = currentSteer * moveSign * steerYawAssist * fricScale * speedAtten * lateTurnOversteer;
                    rb.AddTorque(Vector3.up * turnYawAccel, ForceMode.Acceleration);
                }

                // Self-aligning restoring torque if slip angle exceeds ~32 degrees so powerslides stay controllable
                if (Mathf.Abs(signedSlipDeg) > 32f && moveSign > 0f)
                {
                    float excessSlipDeg = signedSlipDeg - Mathf.Sign(signedSlipDeg) * 32f;
                    float restoreYawAccel = Mathf.Clamp(-excessSlipDeg * 0.065f, -2.8f, 2.8f);
                    rb.AddTorque(Vector3.up * restoreYawAccel, ForceMode.Acceleration);
                }
            }
        }

        private int CountGroundedWheels()
        {
            int count = 0;
            if (frontLeftCollider != null && frontLeftCollider.isGrounded) count++;
            if (frontRightCollider != null && frontRightCollider.isGrounded) count++;
            if (rearLeftCollider != null && rearLeftCollider.isGrounded) count++;
            if (rearRightCollider != null && rearRightCollider.isGrounded) count++;
            return count;
        }

        private void SetRearWheelStiffness(float stiffness)
        {
            if (rearLeftCollider == null || rearRightCollider == null) return;

            var curve = rearLeftCollider.sidewaysFriction;
            curve.stiffness = stiffness * GetEffectiveWeatherFriction();
            rearLeftCollider.sidewaysFriction = curve;
            rearRightCollider.sidewaysFriction = curve;
        }

        private void ApplyAntiRollBars()
        {
            // Incorporate AxleFront / AxleBack TorqueCoef from .vehcarsim with antiRollForce (clamped to safe PhysX range)
            float frontStiff = Mathf.Clamp(antiRollForce + carSimConfig.AxleFrontTorqueCoef * 500f, 3500f, 8000f);
            float rearStiff = Mathf.Clamp(antiRollForce + carSimConfig.AxleBackTorqueCoef * 500f, 3500f, 8000f);
            ApplyAntiRollForAxle(frontLeftCollider, frontRightCollider, frontStiff);
            ApplyAntiRollForAxle(rearLeftCollider, rearRightCollider, rearStiff);
        }

        private void ApplyAntiRollForAxle(WheelCollider wl, WheelCollider wr, float axleAntiRollForce)
        {
            if (wl == null || wr == null) return;

            WheelHit hit;
            float travelL = 1.0f;
            float travelR = 1.0f;
            float suspDistL = Mathf.Max(0.08f, wl.suspensionDistance);
            float suspDistR = Mathf.Max(0.08f, wr.suspensionDistance);

            bool groundedL = wl.GetGroundHit(out hit);
            if (groundedL)
                travelL = Mathf.Clamp01((-wl.transform.InverseTransformPoint(hit.point).y - wl.radius) / suspDistL);

            bool groundedR = wr.GetGroundHit(out hit);
            if (groundedR)
                travelR = Mathf.Clamp01((-wr.transform.InverseTransformPoint(hit.point).y - wr.radius) / suspDistR);

            float antiRollDelta = (travelL - travelR) * axleAntiRollForce;

            if (groundedL)
                rb.AddForceAtPosition(wl.transform.up * -antiRollDelta, wl.transform.position);
            if (groundedR)
                rb.AddForceAtPosition(wr.transform.up * antiRollDelta, wr.transform.position);
        }

        private void ApplyDownforce()
        {
            // Aerodynamic downforce proportional to forward velocity squared (clamped so collision spikes cannot launch vehicle)
            Vector3 vel = rb.linearVelocity;
            float speedSqr = Mathf.Min(vel.sqrMagnitude, 6400f);
            rb.AddForce(-transform.up * downforce * (speedSqr / 100f));

            // Airborne gravity assist: MM2 uses higher effective gravity than Unity's default 9.81 m/s^2,
            // so airborne cars arc lower off jump ramps (e.g. under bridge arches) and drop back to the road faster.
            if (CountGroundedWheels() == 0)
            {
                float extraGravity = 4.6f;
                if (vel.y > 2.0f)
                {
                    extraGravity += Mathf.Min((vel.y - 2.0f) * 0.45f, 3.2f);
                }
                rb.AddForce(Vector3.down * extraGravity, ForceMode.Acceleration);
            }
        }

        private void UpdateWheelMeshes()
        {
            if (VehicleDamage != null && VehicleDamage.IsTotaled)
            {
                // Wheels have detached and roll independently in world space
                return;
            }
            SyncWheelPose(frontLeftCollider, frontLeftMesh);
            SyncWheelPose(frontRightCollider, frontRightMesh);
            SyncWheelPose(rearLeftCollider, rearLeftMesh);
            SyncWheelPose(rearRightCollider, rearRightMesh);
        }

        private void SyncWheelPose(WheelCollider col, Transform meshTrans)
        {
            if (col == null || meshTrans == null) return;

            col.GetWorldPose(out Vector3 pos, out Quaternion rot);
            meshTrans.position = pos;
            meshTrans.rotation = rot;
        }

        private void EnsureTireSmokeEmitters()
        {
            if (tireSmokeRL == null && rearLeftCollider != null)
            {
                Vector3 rlPos = rearLeftCollider.transform.localPosition + new Vector3(0f, -rearLeftCollider.radius * 0.82f, -0.15f);
                tireSmokeRL = CreateTireSmokeEmitter("MM2_TireSmoke_RL", rlPos);
            }
            if (tireSmokeRR == null && rearRightCollider != null)
            {
                Vector3 rrPos = rearRightCollider.transform.localPosition + new Vector3(0f, -rearRightCollider.radius * 0.82f, -0.15f);
                tireSmokeRR = CreateTireSmokeEmitter("MM2_TireSmoke_RR", rrPos);
            }
        }

        private ParticleSystem CreateTireSmokeEmitter(string emitterName, Vector3 localOffset)
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
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 1.05f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.48f, 0.92f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = -0.08f;
            main.maxParticles = 240;

            var emission = ps.emission;
            emission.enabled = false;
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 24f;
            shape.radius = 0.18f;
            shape.rotation = new Vector3(-25f, 180f, 0f);

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

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.45f),
                new Keyframe(0.35f, 1.05f),
                new Keyframe(1f, 2.25f)
            ));

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(0.92f, 0.92f, 0.92f), 0.55f),
                    new GradientColorKey(new Color(0.82f, 0.82f, 0.82f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.62f, 0.12f),
                    new GradientAlphaKey(0.35f, 0.55f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(grad);

            var psRenderer = go.GetComponent<ParticleSystemRenderer>();
            if (psRenderer != null)
            {
                psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                psRenderer.sortMode = ParticleSystemSortMode.Distance;
                psRenderer.sharedMaterial = MM2VehicleDamage.GetOrCreateSmokeMaterial();
                psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                psRenderer.receiveShadows = false;
            }

            if (!ps.isPlaying) ps.Play();
            return ps;
        }

        private void UpdateTireSmoke()
        {
            EnsureTireSmokeEmitters();
            if (tireSmokeRL == null || tireSmokeRR == null || rb == null) return;

            bool rlGrounded = rearLeftCollider != null && rearLeftCollider.isGrounded;
            bool rrGrounded = rearRightCollider != null && rearRightCollider.isGrounded;

            Vector3 vel = rb.linearVelocity;
            float latSpeed = Mathf.Abs(Vector3.Dot(vel, transform.right));

            // 1. Launch wheelspin smoke (full throttle from 2..23 mph, matching Video 1 frames 3-5)
            float launchIntensity = 0f;
            if (currentThrottle > 0.80f && currentSpeedMph > 1.5f && currentSpeedMph < 23f)
            {
                launchIntensity = Mathf.Clamp01(1.0f - (currentSpeedMph - 4f) / 19f) * 0.85f;
            }

            // 2. Full-lock cornering drift / lateral slip / handbrake smoke
            float driftIntensity = 0f;
            if (currentSpeedMph > 10f)
            {
                float latFactor = Mathf.InverseLerp(2.0f, 8.5f, latSpeed);
                float hbFactor = isHandbrakeActive ? 1.0f : 0f;
                driftIntensity = Mathf.Max(latFactor, hbFactor);
            }

            float totalIntensity = Mathf.Clamp01(Mathf.Max(launchIntensity, driftIntensity));
            bool emitRL = rlGrounded && totalIntensity > 0.12f;
            bool emitRR = rrGrounded && totalIntensity > 0.12f;

            float rate = Mathf.Lerp(18f, 68f, totalIntensity);
            float size = Mathf.Lerp(0.55f, 1.15f, totalIntensity);
            Color smokeCol = new Color(0.95f, 0.95f, 0.95f, Mathf.Lerp(0.38f, 0.68f, totalIntensity));

            SetTireSmokeState(tireSmokeRL, emitRL, smokeCol, rate, size);
            SetTireSmokeState(tireSmokeRR, emitRR, smokeCol, rate, size);
        }

        private static void SetTireSmokeState(ParticleSystem ps, bool active, Color color, float rate, float size)
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
    }
}
