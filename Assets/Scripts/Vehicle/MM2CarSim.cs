using System;
using UnityEngine;

namespace MM2.Vehicle
{
    /// <summary>
    /// Complete configuration parsed from `tune/vehicle/*.vehcarsim`, `tune/vehicle/*.vehGyro`,
    /// and `tune/*.asNode`, initialized with standard default values.
    /// </summary>
    [Serializable]
    public class MM2CarSimConfig
    {
        public float Mass = 1500f;
        public Vector3 InertiaBox = new Vector3(2.0f, 1.0f, 3.0f);
        public Vector3 CenterOfGravity = new Vector3(0.0f, -0.10f, 0.20f);
        public float BoundFriction = 0.30f;
        public float BoundElasticity = 0.20f;
        public int DrivetrainType = 0; // 0 = RWD, 1 = FWD, 2 = AWD
        public float SSSValue = 1.0f;
        public float SSSThreshold = 0.0f;
        public float CarFrictionHandling = 1.0f;

        // Aerodynamics
        public Vector3 AeroAngCDamp = new Vector3(0.47f, 5.0f, 4.0f);
        public Vector3 AeroAngVelDamp = Vector3.zero;
        public Vector3 AeroAngVel2Damp = new Vector3(0.40f, 1.50f, 0.50f);
        public float AeroDrag = 0.0f;
        public float AeroDown = 0.0f;

        // Engine
        public float EngineAngInertia = 1.0f;
        public float EngineMaxHorsePower = 500f;
        public float EngineIdleRPM = 750f;
        public float EngineOptRPM = 5000f;
        public float EngineMaxRPM = 7000f;
        public float EngineGCL = 0.25f;

        // Transmission
        public int TransManualNumGears = 7;
        public int TransAutoNumGears = 6;
        public float TransReverse = 20.0f;
        public float TransLow = 20.0f;
        public float TransHigh = 75.0f;
        public float TransGearBias = 0.5f;
        public float TransUpshiftBias = 0.05f;
        public float TransDownshiftBiasMin = 0.05f;
        public float TransDownshiftBiasMax = 0.30f;
        public float TransGearChangeTime = 0.80f;

        // Drivetrain
        public float DrivetrainAngInertia = 4020f;
        public float DrivetrainBrakeDynamicCoef = 1.0f;
        public float DrivetrainBrakeStaticCoef = 1.2f;

        public float FreetrainAngInertia = 3420f;
        public float FreetrainBrakeDynamicCoef = 1.0f;
        public float FreetrainBrakeStaticCoef = 1.2f;

        // Wheels (Front & Back)
        public MM2WheelConfig WheelFront = MM2WheelConfig.CreateDefaultFront();
        public MM2WheelConfig WheelBack = MM2WheelConfig.CreateDefaultBack();

        // Axles (Front & Back)
        public float AxleFrontTorqueCoef = 0.0f;
        public float AxleFrontDampCoef = 0.0f;
        public float AxleBackTorqueCoef = 0.0f;
        public float AxleBackDampCoef = 0.0f;

        // Gyroscopic Stabilization
        public float GyroDrift = 0.20f;
        public float GyroSpin180 = 0.452f;
        public float GyroReverse180 = 1.80f;
        public float GyroPitch = 0.0f;
        public float GyroRoll = 0.0f;

        // asNode steering filter (tune/<basename>.asNode)
        public float SpeedBaseLow = 5.0f;
        public float SpeedBaseHi = 44.6f;
        public float DiscreteSteeringDeltaOutLo = 2.5f;
        public float DiscreteSteeringDeltaInLo = 5.0f;
        public float DiscreteSteeringFilterLo = 1.2f;
        public float DiscreteSteeringDeltaOutHi = 1.0f;
        public float DiscreteSteeringDeltaInHi = 5.0f;
        public float DiscreteSteeringFilterHi = 1.2f;

        /// <summary>
        /// Repairs any zeroed-out fields if Unity's scene serializer creates a default instance
        /// on a scene GameObject saved before `carSimConfig` was added.
        /// </summary>
        public void EnsureValidDefaults()
        {
            if (Mass <= 100f) Mass = 1300f;
            if (InertiaBox.sqrMagnitude < 0.5f) InertiaBox = new Vector3(1.85f, 1.38f, 4.74f);
            if (SSSValue <= 0.05f) SSSValue = 1.0f;
            if (CarFrictionHandling <= 0.05f) CarFrictionHandling = 1.0f;
            if (AeroAngCDamp.sqrMagnitude < 0.01f) AeroAngCDamp = new Vector3(0.47f, 5.0f, 4.0f);
            if (AeroAngVel2Damp.sqrMagnitude < 0.01f) AeroAngVel2Damp = new Vector3(0.40f, 1.50f, 0.50f);
            if (EngineAngInertia <= 0.01f) EngineAngInertia = 1.0f;
            if (EngineMaxHorsePower <= 10f) EngineMaxHorsePower = 500f;
            if (EngineIdleRPM <= 100f) EngineIdleRPM = 800f;
            if (EngineOptRPM <= 500f) EngineOptRPM = 5000f;
            if (EngineMaxRPM <= 1000f) EngineMaxRPM = 7000f;
            if (EngineGCL <= 0.01f) EngineGCL = 0.25f;
            if (TransManualNumGears < 4) TransManualNumGears = 7;
            if (TransAutoNumGears < 4) TransAutoNumGears = 6;
            if (TransReverse <= 5f) TransReverse = 35f;
            if (TransLow <= 5f) TransLow = 35f;
            if (TransHigh <= 30f) TransHigh = 115f;
            if (TransGearBias <= 0.01f) TransGearBias = 0.5f;
            if (TransUpshiftBias <= 0.001f) TransUpshiftBias = 0.05f;
            if (TransDownshiftBiasMin <= 0.001f) TransDownshiftBiasMin = 0.05f;
            if (TransDownshiftBiasMax <= 0.01f) TransDownshiftBiasMax = 0.30f;
            if (TransGearChangeTime <= 0.05f) TransGearChangeTime = 0.80f;
            if (DrivetrainAngInertia <= 10f) DrivetrainAngInertia = 1000f;
            if (DrivetrainBrakeDynamicCoef <= 0.05f) DrivetrainBrakeDynamicCoef = 1.0f;
            if (DrivetrainBrakeStaticCoef <= 0.05f) DrivetrainBrakeStaticCoef = 1.2f;
            if (FreetrainAngInertia <= 10f) FreetrainAngInertia = 1000f;
            if (FreetrainBrakeDynamicCoef <= 0.05f) FreetrainBrakeDynamicCoef = 1.0f;
            if (FreetrainBrakeStaticCoef <= 0.05f) FreetrainBrakeStaticCoef = 1.2f;
            if (SpeedBaseLow <= 0.1f) SpeedBaseLow = 5.0f;
            if (SpeedBaseHi <= 5.0f) SpeedBaseHi = 44.6f;
            if (DiscreteSteeringDeltaOutLo <= 0.1f) DiscreteSteeringDeltaOutLo = 2.5f;
            if (DiscreteSteeringDeltaInLo <= 0.1f) DiscreteSteeringDeltaInLo = 5.0f;
            if (DiscreteSteeringFilterLo <= 0.1f) DiscreteSteeringFilterLo = 1.2f;
            if (DiscreteSteeringDeltaOutHi <= 0.1f) DiscreteSteeringDeltaOutHi = 1.0f;
            if (DiscreteSteeringDeltaInHi <= 0.1f) DiscreteSteeringDeltaInHi = 5.0f;
            if (DiscreteSteeringFilterHi <= 0.1f) DiscreteSteeringFilterHi = 1.2f;

            if (WheelFront == null) WheelFront = MM2WheelConfig.CreateDefaultFront();
            else WheelFront.EnsureValidDefaults(true);

            if (WheelBack == null) WheelBack = MM2WheelConfig.CreateDefaultBack();
            else WheelBack.EnsureValidDefaults(false);
        }
    }

    [Serializable]
    public class MM2WheelConfig
    {
        public float SuspensionExtent = 0.13f;
        public float SuspensionLimit = 0.11f;
        public float SuspensionFactor = 1.0f;
        public float SuspensionDampCoef = 0.10f;
        public float SteeringLimit = 0.40f;
        public float SteeringOffset = 0.25f;
        public float BrakeCoef = 0.611f;
        public float HandbrakeCoef = 2.0f;
        public float CamberLimit = 0.49f;
        public float WobbleLimit = 0.0f;
        public float TireDispLimitLong = 0.125f;
        public float TireDampCoefLong = 0.25f;
        public float TireDragCoefLong = 0.02f;
        public float TireDispLimitLat = 0.125f;
        public float TireDampCoefLat = 0.25f;
        public float TireDragCoefLat = 0.05f;
        public float OptimumSlipPercent = 0.14f;
        public float StaticFric = 3.0f;
        public float SlidingFric = 2.98f;

        public static MM2WheelConfig CreateDefaultFront()
        {
            return new MM2WheelConfig
            {
                SuspensionExtent = 0.13f,
                SuspensionLimit = 0.11f,
                SuspensionFactor = 1.0f,
                SuspensionDampCoef = 0.10f,
                SteeringLimit = 0.40f,
                SteeringOffset = 0.25f,
                BrakeCoef = 0.611f,
                HandbrakeCoef = 2.0f,
                CamberLimit = 0.49f,
                WobbleLimit = 0.0f,
                TireDispLimitLong = 0.125f,
                TireDampCoefLong = 0.25f,
                TireDragCoefLong = 0.02f,
                TireDispLimitLat = 0.125f,
                TireDampCoefLat = 0.25f,
                TireDragCoefLat = 0.05f,
                OptimumSlipPercent = 0.14f,
                StaticFric = 3.0f,
                SlidingFric = 2.98f
            };
        }

        public static MM2WheelConfig CreateDefaultBack()
        {
            return new MM2WheelConfig
            {
                SuspensionExtent = 0.13f,
                SuspensionLimit = 0.10f,
                SuspensionFactor = 1.0f,
                SuspensionDampCoef = 0.10f,
                SteeringLimit = 0.0f,
                SteeringOffset = 0.0f,
                BrakeCoef = 0.649f,
                HandbrakeCoef = 2.0f,
                CamberLimit = 0.17f,
                WobbleLimit = 0.0f,
                TireDispLimitLong = 0.125f,
                TireDampCoefLong = 0.25f,
                TireDragCoefLong = 0.02f,
                TireDispLimitLat = 0.125f,
                TireDampCoefLat = 0.25f,
                TireDragCoefLat = 0.05f,
                OptimumSlipPercent = 0.15f,
                StaticFric = 3.0f,
                SlidingFric = 2.60f
            };
        }

        public void EnsureValidDefaults(bool isFront)
        {
            if (SuspensionExtent <= 0.01f) SuspensionExtent = 0.13f;
            if (SuspensionLimit <= 0.01f) SuspensionLimit = isFront ? 0.11f : 0.10f;
            if (SuspensionFactor <= 0.1f) SuspensionFactor = 1.0f;
            if (SuspensionDampCoef <= 0.01f) SuspensionDampCoef = 0.10f;
            if (isFront && SteeringLimit <= 0.01f) SteeringLimit = 0.40f;
            if (isFront && SteeringOffset <= 0.001f) SteeringOffset = 0.25f;
            if (BrakeCoef <= 0.05f) BrakeCoef = isFront ? 0.611f : 0.649f;
            if (HandbrakeCoef <= 0.05f) HandbrakeCoef = 2.0f;
            if (TireDispLimitLong <= 0.005f) TireDispLimitLong = 0.125f;
            if (TireDampCoefLong <= 0.01f) TireDampCoefLong = 0.25f;
            if (TireDragCoefLong <= 0.001f) TireDragCoefLong = 0.02f;
            if (TireDispLimitLat <= 0.005f) TireDispLimitLat = 0.125f;
            if (TireDampCoefLat <= 0.01f) TireDampCoefLat = 0.25f;
            if (TireDragCoefLat <= 0.001f) TireDragCoefLat = 0.05f;
            if (OptimumSlipPercent <= 0.02f) OptimumSlipPercent = isFront ? 0.14f : 0.15f;
            if (StaticFric <= 0.2f) StaticFric = 3.0f;
            if (SlidingFric <= 0.2f) SlidingFric = isFront ? 2.98f : 2.60f;
        }
    }

    /// <summary>
    /// Vehicle engine torque and RPM simulation model (`tune/vehicle/*.vehcarsim`).
    /// </summary>
    public class MM2Engine
    {
        private const float RpmToRadPerSec = 0.104719758f; // 2*pi/60
        private const float RadPerSecToRpm = 9.549296f;    // 60/(2*pi)
        private const float WattsPerHorsepower = 745.7f;
        private const float Sqrt2Approx = 1.41421356f;

        public float IdleRPM = 750f;
        public float OptRPM = 5000f;
        public float MaxRPM = 7000f;
        public float GCL = 0.25f;
        public float AngInertia = 1.0f;
        public float MaxHorsePower = 500f;
        public float HorsePowerScale = 1.0f;

        // Computed constants
        public float OptRPS;
        public float MaxTorque;
        public float ParabolicTorqueScale;
        public float InvOverRevRange;

        // Live state
        public float CurrentRPS;
        public float CurrentRPM;
        public float CurrentTorque;
        public float CurrentGearRatio;
        public bool WaitingForShift;

        public void ApplyConfig(MM2CarSimConfig cfg)
        {
            AngInertia = Mathf.Max(0.1f, cfg.EngineAngInertia);
            MaxHorsePower = Mathf.Max(50f, cfg.EngineMaxHorsePower);
            IdleRPM = Mathf.Max(300f, cfg.EngineIdleRPM);
            OptRPM = Mathf.Max(IdleRPM + 500f, cfg.EngineOptRPM);
            MaxRPM = Mathf.Max(OptRPM + 200f, cfg.EngineMaxRPM);
            GCL = Mathf.Max(0.05f, cfg.EngineGCL);
            ComputeConstants();
            Reset();
        }

        public void Reset()
        {
            CurrentRPM = IdleRPM;
            CurrentRPS = IdleRPM * RpmToRadPerSec;
            CurrentTorque = 0f;
            CurrentGearRatio = 0f;
            WaitingForShift = false;
        }

        /// <summary>
        /// Computes derived engine torque constants from `OptRPM`, `MaxRPM`, and `MaxHorsePower`.
        /// </summary>
        public void ComputeConstants()
        {
            OptRPS = OptRPM * RpmToRadPerSec;
            MaxTorque = (MaxHorsePower * WattsPerHorsepower) / Mathf.Max(1f, OptRPS);
            ParabolicTorqueScale = MaxTorque / (OptRPM * OptRPM);
            InvOverRevRange = 1.0f / Mathf.Max(1f, MaxRPM - OptRPM);
        }

        /// <summary>
        /// Calculates engine torque at full throttle for a given RPM.
        /// Below `OptRPM`, torque follows a parabola peaking at `MaxTorque`.
        /// Above `OptRPM`, torque drops off to 0 at `MaxRPM` via a quartic polynomial.
        /// </summary>
        public float CalcTorqueAtFullThrottle(float rpm)
        {
            float clampedRpm = Mathf.Clamp(rpm, IdleRPM, MaxRPM);
            float root = OptRPM * Sqrt2Approx;
            if (clampedRpm <= OptRPM)
            {
                return (root + clampedRpm) * (root - clampedRpm) * ParabolicTorqueScale * HorsePowerScale;
            }
            else
            {
                return (clampedRpm + MaxRPM - 2.0f * OptRPM)
                     * (root + clampedRpm)
                     * (root - clampedRpm)
                     * (MaxRPM - clampedRpm)
                     * ParabolicTorqueScale
                     * InvOverRevRange
                     * InvOverRevRange
                     * HorsePowerScale;
            }
        }

        /// <summary>
        /// Computes engine-braking drag torque when the player lets off the gas.
        /// </summary>
        public float CalcTorqueAtZeroThrottle(float rpm)
        {
            float span = Mathf.Max(1f, OptRPM - IdleRPM);
            return -GCL * MaxTorque * ((rpm - IdleRPM) / span);
        }

        /// <summary>
        /// Interpolates engine torque between zero throttle and full throttle.
        /// </summary>
        public float CalcTorque(float throttle)
        {
            float full = CalcTorqueAtFullThrottle(CurrentRPM);
            float zero = CalcTorqueAtZeroThrottle(CurrentRPM);
            return zero + throttle * (full - zero);
        }

        /// <summary>
        /// Calculates engine power output at full throttle for a given angular speed (rad/s).
        /// </summary>
        public float CalcPowerAtFullThrottle(float rps)
        {
            float rpm = rps * RadPerSecToRpm;
            return CalcTorqueAtFullThrottle(rpm) * rps;
        }
    }

    /// <summary>
    /// Vehicle automatic and manual transmission model (`tune/vehicle/*.vehcarsim`).
    /// Gear indices: 0 = Reverse, 1 = Neutral, 2..N-1 = Forward gears.
    /// </summary>
    public class MM2Transmission
    {
        private const float MphToMetersPerSec = 1609.344f * 0.016666668f; // 26.8224 m/min
        private const float TwoPi = 6.2831855f;
        private const float RpmToRadPerSec = 0.104719758f;

        public int ManualNumGears = 7;
        public int AutoNumGears = 6;
        public float ReverseMph = 35f;
        public float LowMph = 30f;
        public float HighMph = 115f;
        public float GearBias = 0.5f;
        public float UpshiftBias = 0.05f;
        public float DownshiftBiasMin = 0.05f;
        public float DownshiftBiasMax = 0.30f;
        public float GearChangeTime = 1.0f;

        public readonly float[] AutoGearRatios = new float[8];
        public readonly float[] ManualGearRatios = new float[8];
        public readonly float[] UpshiftSpeedMph = new float[8];
        public readonly float[] DownshiftMinSpeedMph = new float[8];
        public readonly float[] DownshiftMaxSpeedMph = new float[8];

        public int IsAutomatic = 1; // 1 = Auto, 0 = Manual
        public int CurrentGear = 2; // 0 = Reverse, 1 = Neutral, 2 = 1st gear
        public float ShiftTimer = 0f;
        public bool GearChangedFlag;

        public void ApplyConfig(MM2CarSimConfig cfg, float driveWheelRadius, MM2Engine engine)
        {
            ManualNumGears = Mathf.Clamp(cfg.TransManualNumGears, 4, 8);
            AutoNumGears = Mathf.Clamp(cfg.TransAutoNumGears, 4, 8);
            ReverseMph = Mathf.Max(28f, cfg.TransReverse);
            LowMph = Mathf.Max(10f, cfg.TransLow);
            HighMph = Mathf.Max(LowMph + 20f, cfg.TransHigh);
            GearBias = cfg.TransGearBias;
            UpshiftBias = cfg.TransUpshiftBias;
            DownshiftBiasMin = cfg.TransDownshiftBiasMin;
            DownshiftBiasMax = cfg.TransDownshiftBiasMax;
            GearChangeTime = Mathf.Max(0.1f, cfg.TransGearChangeTime);

            ComputeConstants(driveWheelRadius, engine);
            Reset();
        }

        public void Reset()
        {
            CurrentGear = 2;
            ShiftTimer = 0f;
            GearChangedFlag = false;
        }

        /// <summary>
        /// Computes the gear ratio required to reach `speedMph` at `optRpm` for the given wheel radius.
        /// </summary>
        public static float GearRatioFromMPH(float speedMph, float optRpm, float wheelRadius)
        {
            float safeSpeed = Mathf.Max(1f, speedMph);
            float safeRadius = Mathf.Max(0.15f, wheelRadius);
            float wheelRpmAtSpeed = (safeSpeed * MphToMetersPerSec) / (safeRadius * TwoPi);
            return optRpm / Mathf.Max(1f, wheelRpmAtSpeed);
        }

        /// <summary>
        /// Computes automatic & manual gear ratio tables and optimal power-crossover shift speeds.
        /// </summary>
        public void ComputeConstants(float wheelRadius, MM2Engine engine)
        {
            // 1. Auto gear ratios
            AutoGearRatios[0] = -GearRatioFromMPH(ReverseMph, engine.OptRPM, wheelRadius);
            AutoGearRatios[1] = 0f;
            AutoGearRatios[2] = GearRatioFromMPH(LowMph, engine.OptRPM, wheelRadius);
            int topAutoIdx = AutoNumGears - 1;
            AutoGearRatios[topAutoIdx] = GearRatioFromMPH(HighMph, engine.OptRPM, wheelRadius);

            int numAutoForward = AutoNumGears - 2;
            if (numAutoForward > 2)
            {
                int steps = numAutoForward - 1;
                float stepBase = Mathf.Pow(AutoGearRatios[topAutoIdx] / AutoGearRatios[2], 1.0f / steps);
                for (int i = 1; i < steps; i++)
                {
                    float exponent = i + (i * (steps - i) * GearBias) / steps;
                    AutoGearRatios[2 + i] = AutoGearRatios[2] * Mathf.Pow(stepBase, exponent);
                }
            }

            // 2. Binary search power crossover RPM between gear g and g+1
            for (int g = 2; g < topAutoIdx; g++)
            {
                float nextToCurrRatio = AutoGearRatios[g + 1] / AutoGearRatios[g];
                float highRpm = engine.MaxRPM;
                float lowRpm = engine.OptRPM;
                if (lowRpm * nextToCurrRatio > highRpm)
                {
                    lowRpm = highRpm / nextToCurrRatio;
                }

                while (highRpm - lowRpm > 1.0f)
                {
                    float midRpm = 0.5f * (highRpm + lowRpm);
                    float midRps = midRpm * RpmToRadPerSec;
                    float powerCurr = engine.CalcPowerAtFullThrottle(midRps);
                    float powerNext = engine.CalcPowerAtFullThrottle(midRps * nextToCurrRatio);
                    if (powerNext > powerCurr)
                        highRpm = midRpm;
                    else
                        lowRpm = midRpm;
                }

                float crossoverRpm = 0.5f * (highRpm + lowRpm);
                // Convert RPM shift points to MPH for gear g
                float gearMaxMph = (engine.MaxRPM / AutoGearRatios[g]) * (wheelRadius * TwoPi) / MphToMetersPerSec;
                float crossoverFrac = crossoverRpm / engine.MaxRPM;
                UpshiftSpeedMph[g] = gearMaxMph * Mathf.Clamp01(crossoverFrac * (1.0f + UpshiftBias));
                DownshiftMaxSpeedMph[g + 1] = gearMaxMph * Mathf.Clamp01(crossoverFrac * (1.0f - DownshiftBiasMin) * nextToCurrRatio);
                DownshiftMinSpeedMph[g + 1] = gearMaxMph * Mathf.Clamp01(crossoverFrac * (1.0f - DownshiftBiasMax) * nextToCurrRatio);
            }
            UpshiftSpeedMph[topAutoIdx] = HighMph * Mathf.Clamp(engine.MaxRPM / Mathf.Max(1000f, engine.OptRPM), 1.05f, 1.35f);
            DownshiftMaxSpeedMph[2] = 0f;
            DownshiftMinSpeedMph[2] = 0f;

            // 3. Manual gear ratios
            ManualGearRatios[0] = -GearRatioFromMPH(ReverseMph, engine.OptRPM, wheelRadius);
            ManualGearRatios[1] = 0f;
            ManualGearRatios[2] = GearRatioFromMPH(LowMph, engine.OptRPM, wheelRadius);
            int topManualIdx = ManualNumGears - 1;
            ManualGearRatios[topManualIdx] = GearRatioFromMPH(HighMph, engine.OptRPM, wheelRadius);

            int numManualForward = ManualNumGears - 2;
            if (numManualForward > 2)
            {
                int steps = numManualForward - 1;
                float stepBase = Mathf.Pow(ManualGearRatios[topManualIdx] / ManualGearRatios[2], 1.0f / steps);
                for (int i = 1; i < steps; i++)
                {
                    float exponent = i + (i * (steps - i) * GearBias) / steps;
                    ManualGearRatios[2 + i] = ManualGearRatios[2] * Mathf.Pow(stepBase, exponent);
                }
            }
        }

        public float GetCurrentGearRatio()
        {
            int g = Mathf.Clamp(CurrentGear, 0, 7);
            return (IsAutomatic != 0) ? AutoGearRatios[g] : ManualGearRatios[g];
        }

        /// <summary>
        /// Sets the active transmission gear index and resets the shift timer.
        /// </summary>
        public int SetCurrentGear(int newGear)
        {
            if (newGear != CurrentGear)
            {
                int maxGears = (IsAutomatic != 0) ? AutoNumGears : ManualNumGears;
                if (newGear >= 0 && newGear < maxGears)
                {
                    ShiftTimer = 0f;
                    GearChangedFlag = true;
                    CurrentGear = newGear;
                }
            }
            return CurrentGear;
        }

        /// <summary>
        /// Updates automatic gear shifting based on road speed and throttle position.
        /// </summary>
        public void Update(float speedMph, float throttle, int groundedWheels, float dt)
        {
            if (groundedWheels <= 0) return;

            if (IsAutomatic == 1 && CurrentGear > 1 && ShiftTimer >= GearChangeTime && !GearChangedFlag)
            {
                int topGear = AutoNumGears - 1;
                if (CurrentGear < topGear && speedMph > UpshiftSpeedMph[CurrentGear])
                {
                    SetCurrentGear(CurrentGear + 1);
                }
                else if (CurrentGear > 2)
                {
                    float t = Mathf.Clamp01(throttle);
                    float downshiftThreshold = (1.0f - t) * DownshiftMinSpeedMph[CurrentGear] + t * DownshiftMaxSpeedMph[CurrentGear];
                    if (speedMph < downshiftThreshold)
                    {
                        SetCurrentGear(CurrentGear - 1);
                    }
                }
            }

            ShiftTimer += dt;
        }
    }

    /// <summary>
    /// Vehicle wheel suspension and tire slip-friction model (`tune/vehicle/*.vehcarsim`).
    /// Implements tire deflection spring-damper, parabolic slip-friction curve,
    /// friction circle clamping, and quadratic tire drag.
    /// </summary>
    public class MM2Wheel
    {
        public const float GravityMagnitude = 19.6f;

        public MM2WheelConfig Config = new MM2WheelConfig();
        public Vector3 LocalPosition;
        public float Radius = 0.35f;
        public bool IsFrontWheel;

        // Computed suspension & tire spring/damper constants
        public float RestNormalLoad;
        public float InvOptSlipSquared;
        public float SuspStiffnessLinear;
        public float SuspStiffnessNonLin;
        public float SuspDamping;
        public float TireStiffLong;
        public float TireDampLong;
        public float TireStiffLat;
        public float TireDampLat;
        public float MaxBrakeTorque;
        public float MaxHandbrakeTorque;

        // Live wheel state
        public float SteerAngleRad;
        public float CurrentBrakeTorque;
        public float NormalLoad;
        public float TireDispLat;
        public float TireDispLong;
        public float ForceLat;
        public float ForceLong;
        public float ReactionTorque;
        public float AngularVelocity;      // rad/s
        public float SlipLat;
        public float SlipLong;
        public float SlipRatioMetric;
        public bool IsSlipping;
        public bool IsGrounded;
        public float VisualRotationRad;
        public float SurfaceFriction = 1.0f;
        public float SurfaceDrag = 1.0f;

        // Contact velocities
        public float ContactVelLat;
        public float ContactVelLong;
        public float ContactVelNorm;
        public float ContactSlipVelLong;

        /// <summary>
        /// Initializes wheel geometry, static normal load, and suspension/tire constants.
        /// </summary>
        public void Initialize(MM2WheelConfig cfg, Vector3 localPos, float radius, float vehicleMass, Vector3 cg, bool isFront)
        {
            Config = cfg ?? (isFront ? MM2WheelConfig.CreateDefaultFront() : MM2WheelConfig.CreateDefaultBack());
            Config.EnsureValidDefaults(isFront);
            LocalPosition = localPos;
            Radius = Mathf.Max(0.15f, radius);
            IsFrontWheel = isFront;

            float optSlip = Mathf.Max(0.02f, Config.OptimumSlipPercent);
            InvOptSlipSquared = 1.0f / (optSlip * optSlip);

            // Compute static wheel normal load from CG Z-offset and MM2 gravity (19.6 m/s^2)
            float absWheelZ = Mathf.Max(0.2f, Mathf.Abs(localPos.z));
            float distFromOppositeAxle = Mathf.Abs(localPos.z - cg.z);
            float axleWeightShare = Mathf.Clamp(distFromOppositeAxle / (2.0f * absWheelZ), 0.15f, 0.35f);
            float restLoad = GravityMagnitude * vehicleMass * axleWeightShare;

            ComputeConstants(restLoad);
            Reset();
        }

        public void Reset()
        {
            SteerAngleRad = 0f;
            CurrentBrakeTorque = 0f;
            NormalLoad = RestNormalLoad;
            TireDispLat = 0f;
            TireDispLong = 0f;
            ForceLat = 0f;
            ForceLong = 0f;
            ReactionTorque = 0f;
            AngularVelocity = 0f;
            SlipLat = 0f;
            SlipLong = 0f;
            SlipRatioMetric = 0f;
            IsSlipping = false;
            IsGrounded = false;
        }

        /// <summary>
        /// Computes suspension and tire spring-damper constants for the given static normal load.
        /// </summary>
        public void ComputeConstants(float restLoad)
        {
            RestNormalLoad = Mathf.Max(100f, restLoad);
            float suspFactor = Mathf.Max(0.75f, Config.SuspensionFactor);
            float extent = Mathf.Max(0.05f, Config.SuspensionExtent);
            float limit = Mathf.Max(0.05f, Config.SuspensionLimit);

            float invTravelProd = 1.0f / ((extent + limit) * limit);
            SuspStiffnessLinear = (suspFactor * limit + extent) * invTravelProd * RestNormalLoad;
            SuspStiffnessNonLin = ((suspFactor - 1.0f) * invTravelProd * RestNormalLoad) / Mathf.Max(1f, SuspStiffnessLinear);
            SuspDamping = 2.0f * Config.SuspensionDampCoef * Mathf.Sqrt(SuspStiffnessLinear * RestNormalLoad);

            float effectiveWheelMass = RestNormalLoad / GravityMagnitude;
            float dispLong = Mathf.Max(0.01f, Config.TireDispLimitLong);
            float dispLat = Mathf.Max(0.01f, Config.TireDispLimitLat);

            TireStiffLong = (2.0f * RestNormalLoad) / dispLong;
            TireDampLong = 2.0f * Config.TireDampCoefLong * Mathf.Sqrt(TireStiffLong * effectiveWheelMass);

            TireStiffLat = (2.0f * RestNormalLoad) / dispLat;
            TireDampLat = 2.0f * Config.TireDampCoefLat * Mathf.Sqrt(TireStiffLat * effectiveWheelMass);

            float baseMaxTorque = Config.StaticFric * Radius * RestNormalLoad;
            MaxBrakeTorque = baseMaxTorque * Config.BrakeCoef;
            MaxHandbrakeTorque = baseMaxTorque * Config.HandbrakeCoef;
        }

        /// <summary>
        /// Applies Ackermann steering offset (`SteeringOffset`) and computes total wheel brake torque.
        /// </summary>
        public void SetInputs(float steerInput, float brakeInput, float handbrakeInput)
        {
            // Note: In Unity (+Z forward, +X right, +Y up), positive steerInput (right turn)
            // rotates around +Y by +SteerAngleRad. Ackermann inside-wheel boost uses sign(LocalPosition.x).
            float rawAngle = steerInput * Config.SteeringLimit;
            float sideSign = LocalPosition.x < 0f ? -1f : (LocalPosition.x > 0f ? 1f : 0f);
            SteerAngleRad = rawAngle * (1.0f + rawAngle * Config.SteeringOffset * sideSign);

            CurrentBrakeTorque = brakeInput * MaxBrakeTorque + handbrakeInput * MaxHandbrakeTorque;
        }

        /// <summary>
        /// Evaluates the parabolic tire friction curve from `OptimumSlipPercent`, `StaticFric`, and `SlidingFric`.
        /// </summary>
        public float CalcFriction(float slip, out float outSlipMetric)
        {
            float absSlip = Mathf.Abs(slip);
            float muStatic = Config.StaticFric * SurfaceFriction;
            float muSliding = Config.SlidingFric * SurfaceFriction;
            float optSlip = Mathf.Max(0.02f, Config.OptimumSlipPercent);

            float parabola = (2.0f * optSlip - absSlip) * InvOptSlipSquared * muStatic * absSlip;
            if (absSlip <= optSlip)
            {
                outSlipMetric = 0.5f * absSlip / optSlip;
                return Mathf.Max(0f, parabola);
            }

            if (parabola > muSliding)
            {
                float denom = Mathf.Max(0.001f, muStatic - muSliding);
                outSlipMetric = ((parabola - muSliding) * 0.5f + (muStatic - parabola)) / denom;
                return parabola;
            }

            outSlipMetric = 1.0f;
            return muSliding;
        }

        /// <summary>
        /// Computes longitudinal and lateral tire forces in world space using tire deflection spring-damper,
        /// friction circle clamping, and rolling/lateral drag.
        /// </summary>
        public Vector3 ComputeTireForce(
            Vector3 hubPointVel,
            Vector3 groundNormal,
            Vector3 wheelRightWorld,
            float normalLoad,
            float carFrictionHandling,
            float dt)
        {
            if (!IsGrounded || normalLoad <= 1f)
            {
                IsGrounded = false;
                SlipRatioMetric = 0f;
                IsSlipping = false;
                TireDispLat = 0f;
                TireDispLong = 0f;
                ForceLat = 0f;
                ForceLong = 0f;
                ReactionTorque = 0f;
                return Vector3.zero;
            }

            NormalLoad = normalLoad;

            // Project wheel axes onto ground contact plane
            // In Unity (+Z forward, +X right, +Y up):
            // wheelForward tangent = normalize(cross(wheelRightWorld, groundNormal))
            // wheelRight tangent = cross(groundNormal, wheelForward)
            Vector3 forwardTangent = Vector3.Cross(wheelRightWorld, groundNormal);
            if (forwardTangent.sqrMagnitude < 0.0004f)
            {
                IsGrounded = false;
                return Vector3.zero;
            }
            forwardTangent.Normalize();
            Vector3 rightTangent = Vector3.Cross(groundNormal, forwardTangent).normalized;

            // Contact patch velocities
            // Positive ContactVelLong = moving forward along +forwardTangent.
            // Positive AngularVelocity = wheel rolling forward.
            // Positive ContactSlipVelLong = wheel spinning faster than road (accelerating forward).
            ContactVelLat = Vector3.Dot(hubPointVel, rightTangent);
            ContactVelLong = Vector3.Dot(hubPointVel, forwardTangent);
            ContactVelNorm = Vector3.Dot(hubPointVel, groundNormal);

            float wheelCircumVel = AngularVelocity * Radius;
            ContactSlipVelLong = wheelCircumVel - ContactVelLong;

            // Lateral slip ratio
            float absVLong = Mathf.Max(0.5f, Mathf.Abs(ContactVelLong));
            float absVLat = Mathf.Abs(ContactVelLat);
            if (absVLat < 1e-5f)
                SlipLat = 0f;
            else
                SlipLat = Mathf.Clamp(ContactVelLat / absVLong, -1f, 1f);

            // Longitudinal slip ratio
            float absVSlipLong = Mathf.Abs(ContactSlipVelLong);
            if (absVSlipLong < 1e-5f)
                SlipLong = 0f;
            else
                SlipLong = Mathf.Clamp(ContactSlipVelLong / absVLong, -1f, 1f);

            // Effective surface friction with CarFrictionHandling
            float effectiveSurfFric = SurfaceFriction;
            if (effectiveSurfFric < 1.0f)
            {
                if (carFrictionHandling < 1.0f)
                    effectiveSurfFric = (1.0f - effectiveSurfFric) * (1.0f - carFrictionHandling) + effectiveSurfFric;
                else if (carFrictionHandling > 0.01f)
                    effectiveSurfFric = effectiveSurfFric / carFrictionHandling;
            }
            SurfaceFriction = effectiveSurfFric;

            // Maximum tire spring deflection limits before sliding
            float signSlipLong = ContactSlipVelLong < 0f ? -1f : (ContactSlipVelLong > 0f ? 1f : 0f);
            float dispUnitLong = signSlipLong * NormalLoad / TireStiffLong;

            float signVelLat = ContactVelLat < 0f ? -1f : (ContactVelLat > 0f ? 1f : 0f);
            float dispUnitLat = signVelLat * NormalLoad / TireStiffLat;

            float deltaDispLong = ContactSlipVelLong * dt;
            float deltaDispLat = ContactVelLat * dt;

            float muStatic = Config.StaticFric * SurfaceFriction;
            float muLong = muStatic;
            float muLat = muStatic;
            float slipMetricLong = 0f;
            float slipMetricLat = 0f;

            // Evaluate longitudinal friction coefficient if past optimum slip or spring limit
            float maxDispLong = muStatic * dispUnitLong;
            bool evalLongCurve = Mathf.Abs(SlipLong) > Config.OptimumSlipPercent;
            if (!evalLongCurve)
            {
                if (deltaDispLong >= 0f && (TireDispLong > maxDispLong || (maxDispLong - TireDispLong) < deltaDispLong))
                    evalLongCurve = true;
                else if (deltaDispLong < 0f && (TireDispLong < maxDispLong || (maxDispLong - TireDispLong) > deltaDispLong))
                    evalLongCurve = true;
            }
            if (evalLongCurve)
            {
                muLong = CalcFriction(SlipLong, out slipMetricLong);
                maxDispLong = muLong * dispUnitLong;
            }

            // Evaluate lateral friction coefficient if past optimum slip or spring limit
            float maxDispLat = muStatic * dispUnitLat;
            bool evalLatCurve = Mathf.Abs(SlipLat) > Config.OptimumSlipPercent;
            if (!evalLatCurve)
            {
                if (deltaDispLat >= 0f && (TireDispLat > maxDispLat || (maxDispLat - TireDispLat) < deltaDispLat))
                    evalLatCurve = true;
                else if (deltaDispLat < 0f && (TireDispLat < maxDispLat || (maxDispLat - TireDispLat) > deltaDispLat))
                    evalLatCurve = true;
            }
            if (evalLatCurve)
            {
                muLat = CalcFriction(SlipLat, out slipMetricLat);
                maxDispLat = muLat * dispUnitLat;
            }

            // Combined friction coefficient
            float dominantSlip = Mathf.Max(Mathf.Abs(SlipLong), Mathf.Abs(SlipLat));
            float muCombined = muStatic;
            if (dominantSlip > Config.OptimumSlipPercent)
            {
                if (Mathf.Abs(SlipLong) >= Mathf.Abs(SlipLat))
                {
                    muCombined = muLong;
                    if (muLat > muLong) maxDispLat = muLong * dispUnitLat;
                }
                else
                {
                    muCombined = muLat;
                    if (muLong > muLat) maxDispLong = muLat * dispUnitLong;
                }
            }

            // Update TireDispLong & TireDispLat springs with relaxation
            float relaxStep = Mathf.Abs(wheelCircumVel) * dt * 0.1f;

            float dampVelLong = 0f;
            bool clampedLong = false;
            float nextDispLong = TireDispLong + deltaDispLong;
            if ((deltaDispLong >= 0f && nextDispLong <= maxDispLong) || (deltaDispLong < 0f && nextDispLong >= maxDispLong))
            {
                TireDispLong = nextDispLong;
                dampVelLong = ContactSlipVelLong;
                clampedLong = true;
            }
            else
            {
                TireDispLong = Mathf.MoveTowards(TireDispLong, maxDispLong, relaxStep);
            }

            float dampVelLat = 0f;
            bool clampedLat = false;
            float nextDispLat = TireDispLat + deltaDispLat;
            if ((deltaDispLat >= 0f && nextDispLat <= maxDispLat) || (deltaDispLat < 0f && nextDispLat >= maxDispLat))
            {
                TireDispLat = nextDispLat;
                dampVelLat = ContactVelLat;
                clampedLat = true;
            }
            else
            {
                TireDispLat = Mathf.MoveTowards(TireDispLat, maxDispLat, relaxStep);
            }

            // Compute raw spring-damper tire forces
            // ForceLat opposes ContactVelLat; ForceLong follows ContactSlipVelLong (positive = forward drive).
            ForceLat = -TireDispLat * TireStiffLat - dampVelLat * TireDampLat;
            ForceLong = TireDispLong * TireStiffLong + dampVelLong * TireDampLong;

            // Friction circle clamping
            float maxTotalForce = muCombined * NormalLoad;
            float forceSq = ForceLat * ForceLat + ForceLong * ForceLong;
            if (forceSq > maxTotalForce * maxTotalForce && forceSq > 1e-4f)
            {
                bool fullSlide = (!clampedLong && Mathf.Abs(SlipLong) > Config.OptimumSlipPercent) &&
                                 (!clampedLat && Mathf.Abs(SlipLat) > Config.OptimumSlipPercent);
                float slipVelSq = ContactVelLat * ContactVelLat + ContactSlipVelLong * ContactSlipVelLong;
                if (fullSlide && slipVelSq > 1e-4f)
                {
                    float scale = maxTotalForce / Mathf.Sqrt(slipVelSq);
                    ForceLat = -ContactVelLat * scale;
                    ForceLong = ContactSlipVelLong * scale;
                }
                else
                {
                    float scale = maxTotalForce / Mathf.Sqrt(forceSq);
                    ForceLat *= scale;
                    ForceLong *= scale;
                }
            }

            SlipRatioMetric = Mathf.Max(slipMetricLat, slipMetricLong);
            IsSlipping = SlipRatioMetric > 0.5f;

            // Reaction torque fed back to drivetrain
            // Positive ForceLong (forward drive) produces positive ReactionTorque opposing engine drive torque.
            ReactionTorque = ForceLong * Radius;

            // Quadratic lateral and longitudinal rolling/scrubbing drag
            // Both strictly oppose ContactVelLat and ContactVelLong.
            float dragLat = -Mathf.Abs(ContactVelLat) * ContactVelLat * Config.TireDragCoefLat * NormalLoad * SurfaceDrag;
            float dragLong = -Mathf.Abs(ContactVelLong) * ContactVelLong * Config.TireDragCoefLong * NormalLoad * SurfaceDrag;

            Vector3 totalPlanarForce =
                rightTangent * (ForceLat + dragLat) +
                forwardTangent * (ForceLong + dragLong);

            return totalPlanarForce;
        }
    }

    /// <summary>
    /// Vehicle drivetrain model (`tune/vehicle/*.vehcarsim`).
    /// Couples `MM2Engine`, `MM2Transmission`, and attached `MM2Wheel` instances.
    /// </summary>
    public class MM2Drivetrain
    {
        private const float RadPerSecToRpm = 9.549296f;
        private const float RpmToRadPerSec = 0.104719758f;

        public float AngInertia = 4020f;
        public float BrakeDynamicCoef = 1.0f;
        public float BrakeStaticCoef = 1.2f;
        public float AxleAngVel = 0f;          // rad/s

        public MM2Wheel[] Wheels = Array.Empty<MM2Wheel>();
        public bool IsPowered;

        public void Configure(float inertia, float dynBrake, float statBrake, bool powered, params MM2Wheel[] attachedWheels)
        {
            AngInertia = Mathf.Max(10f, inertia);
            BrakeDynamicCoef = Mathf.Max(0.1f, dynBrake);
            BrakeStaticCoef = Mathf.Max(0.1f, statBrake);
            IsPowered = powered;
            Wheels = attachedWheels ?? Array.Empty<MM2Wheel>();
            AxleAngVel = 0f;
        }

        public void Reset()
        {
            AxleAngVel = 0f;
        }

        /// <summary>
        /// Updates axle angular velocity from engine drive torque, tire reaction torque, and brakes.
        /// </summary>
        public void Update(MM2Engine engine, MM2Transmission trans, float throttle, float dt)
        {
            if (Wheels == null || Wheels.Length == 0) return;

            // Sum wheel reaction torques and brake torques across attached wheels
            float totalReactionTorque = 0f;
            float totalBrakeTorque = 0f;
            for (int i = 0; i < Wheels.Length; i++)
            {
                totalReactionTorque += Wheels[i].ReactionTorque;
                totalBrakeTorque += Wheels[i].CurrentBrakeTorque;
            }

            float dynamicBrakeTorque = totalBrakeTorque * BrakeDynamicCoef;
            float staticBrakeTorque = totalBrakeTorque * BrakeStaticCoef;

            float combinedInertia = AngInertia;
            float engineDriveTorque = 0f;

            if (IsPowered && engine != null && trans != null)
            {
                float gearRatio = trans.GetCurrentGearRatio();
                engine.CurrentGearRatio = gearRatio;

                if (trans.GearChangedFlag)
                {
                    trans.GearChangedFlag = false;
                    engine.WaitingForShift = true;
                }

                if (engine.WaitingForShift)
                {
                    if (Mathf.Abs(gearRatio) > 0.001f)
                    {
                        float matchedRps = AxleAngVel * gearRatio;
                        engine.CurrentRPS = matchedRps;
                        engine.CurrentRPM = matchedRps * RadPerSecToRpm;
                    }
                    engine.WaitingForShift = false;
                }

                engine.CurrentTorque = engine.CalcTorque(throttle);

                if (Mathf.Abs(gearRatio) < 0.001f)
                {
                    // Neutral gear: spin engine freely
                    engine.CurrentRPS += (engine.CurrentTorque / engine.AngInertia) * dt;
                    engine.CurrentRPM = Mathf.Clamp(engine.CurrentRPS * RadPerSecToRpm, engine.IdleRPM, engine.MaxRPM);
                    engine.CurrentRPS = engine.CurrentRPM * RpmToRadPerSec;
                }
                else
                {
                    // In gear: couple engine inertia and torque through gear ratio
                    combinedInertia += engine.AngInertia * gearRatio * gearRatio * 0.5f;
                    if (engine.CurrentRPM <= engine.MaxRPM)
                    {
                        engineDriveTorque = engine.CurrentTorque * gearRatio;
                    }
                }
            }

            // Integrate axle angular velocity from engine drive torque and tire reaction torque
            float netDriveTorque = engineDriveTorque - totalReactionTorque;
            AxleAngVel += (netDriveTorque / combinedInertia) * dt;
            AxleAngVel = Mathf.Clamp(AxleAngVel, -240f, 240f);

            // Apply dynamic and static brake torque opposing wheel rotation
            float brakeAngDelta = (dynamicBrakeTorque * dt) / combinedInertia;
            if (Mathf.Abs(AxleAngVel) > brakeAngDelta)
            {
                if (AxleAngVel > 0f)
                    AxleAngVel -= brakeAngDelta;
                else
                    AxleAngVel += brakeAngDelta;
            }
            else
            {
                // Within static brake lock threshold
                if (Mathf.Abs(netDriveTorque) <= staticBrakeTorque)
                {
                    AxleAngVel = 0f;
                }
                else
                {
                    if (AxleAngVel > 0f)
                        AxleAngVel = Mathf.Max(0f, AxleAngVel - brakeAngDelta);
                    else if (AxleAngVel < 0f)
                        AxleAngVel = Mathf.Min(0f, AxleAngVel + brakeAngDelta);
                }
            }

            // Sync engine RPM with axle angular velocity when in gear
            if (IsPowered && engine != null && trans != null)
            {
                float gearRatio = engine.CurrentGearRatio;
                if (Mathf.Abs(gearRatio) > 0.001f)
                {
                    engine.CurrentRPS = Mathf.Abs(AxleAngVel * gearRatio);
                    engine.CurrentRPM = engine.CurrentRPS * RadPerSecToRpm;

                    float overRevLimit = engine.MaxRPM + 500f;
                    if (engine.CurrentRPM > overRevLimit)
                    {
                        engine.CurrentRPM = overRevLimit;
                        engine.CurrentRPS = overRevLimit * RpmToRadPerSec;
                        AxleAngVel = Mathf.Sign(AxleAngVel) * (engine.CurrentRPS / Mathf.Abs(gearRatio));
                    }
                    else if (engine.CurrentRPM < engine.IdleRPM)
                    {
                        engine.CurrentRPM = engine.IdleRPM;
                        engine.CurrentRPS = engine.IdleRPM * RpmToRadPerSec;
                    }
                }
            }

            // Distribute angular velocity to attached wheels
            for (int i = 0; i < Wheels.Length; i++)
            {
                Wheels[i].AngularVelocity = AxleAngVel;
            }
        }

        /// <summary>
        /// Computes longitudinal drive acceleration (m/s^2), accounting for:
        /// 1. Gear-scaled engine torque `T_engine(RPM) * GearRatio / WheelRadius`
        /// 2. Coupled rotational inertia `Engine.AngInertia * G^2 + (Drivetrain.AngInertia + Freetrain.AngInertia) * dt`
        /// 3. Quadratic tire rolling drag `TireDragCoefLong * Mass * 0.12 * v^2` + `AeroDrag * v^2`
        /// </summary>
        public static float ComputeStepDriveAcceleration(
            MM2CarSimConfig cfg,
            MM2Engine engine,
            MM2Transmission trans,
            float forwardSpeedMs,
            float throttleMag,
            float driveWheelRadius)
        {
            if (cfg == null || engine == null || trans == null) return 0f;

            float r = Mathf.Max(0.20f, driveWheelRadius);
            float gearRatio = trans.GetCurrentGearRatio();
            float absGearRatio = Mathf.Abs(gearRatio);
            if (absGearRatio < 0.001f) return 0f;

            float absSpeed = Mathf.Abs(forwardSpeedMs);
            float wheelRps = absSpeed / r;
            float rawEngineRpm = wheelRps * absGearRatio * RadPerSecToRpm;

            // Rev limiter check (if RPM exceeds MaxRPM, engine drive torque cuts off)
            float engineTorque = 0f;
            if (rawEngineRpm <= engine.MaxRPM)
            {
                float clampedRpm = Mathf.Clamp(rawEngineRpm, engine.IdleRPM, engine.MaxRPM);
                float fullTorque = engine.CalcTorqueAtFullThrottle(clampedRpm);
                float zeroTorque = engine.CalcTorqueAtZeroThrottle(clampedRpm);
                engineTorque = zeroTorque + Mathf.Clamp01(throttleMag) * (fullTorque - zeroTorque);
            }

            float rawDriveForce = Mathf.Max(0f, engineTorque) * absGearRatio / r;

            // Opposing quadratic tire rolling drag; linear AeroDrag is applied separately in MM2Aero
            float avgTireDragCoef = 0.5f * (cfg.WheelFront.TireDragCoefLong + cfg.WheelBack.TireDragCoefLong);
            float speedSq = absSpeed * absSpeed;
            float dragForce = (avgTireDragCoef * cfg.Mass * 0.032f) * speedSq;

            // Effective translational mass M_eff = Mass + M_rot:
            // In 1st gear (CurrentGear == 2) and Reverse (CurrentGear == 0), launch wheelspin/clutch slip decouples
            // excess gear reduction above ~7.2 so short-geared vehicles (e.g. vpcab) accelerate briskly in both 1st and Reverse.
            bool isFirstOrReverse = trans.CurrentGear == 2 || trans.CurrentGear == 0;
            float effectiveGearForInertia = isFirstOrReverse ? Mathf.Min(absGearRatio, 7.2f) : absGearRatio;
            float massInertiaScale = Mathf.Clamp(cfg.Mass / 1400f, 0.55f, 1.50f);
            float dtRefStep = (1.0f / 46.0f) * massInertiaScale * (isFirstOrReverse ? 0.35f : 1.0f);

            float rotMassKg = (0.82f * engine.AngInertia * effectiveGearForInertia * effectiveGearForInertia +
                               (cfg.DrivetrainAngInertia + cfg.FreetrainAngInertia) * dtRefStep) / (r * r);
            float chassisMassKg = Mathf.Max(500f, cfg.Mass);
            float effectiveMassKg = chassisMassKg + rotMassKg;

            float coupledAccel = (rawDriveForce - dragForce) / effectiveMassKg;

            // Driven axle tire traction limit under 19.62 m/s^2 normal load:
            // When wheel torque exceeds tire grip (launch wheelspin), tire traction pushes the chassis mass directly
            // using the blend of StaticFric and SlidingFric.
            MM2WheelConfig drivenWheel = (cfg.DrivetrainType == 1) ? cfg.WheelFront : cfg.WheelBack;
            float drivenNormalLoad = (cfg.DrivetrainType == 2) ? (chassisMassKg * 19.62f) : (chassisMassKg * 19.62f * 0.5f);
            float launchMu = Mathf.Max(1.5f, 0.5f * (drivenWheel.StaticFric + drivenWheel.SlidingFric));
            float maxTractionForce = drivenNormalLoad * launchMu * 0.62f;
            float tractionLimitedAccel = (maxTractionForce - dragForce) / chassisMassKg;

            float netAccel = Mathf.Min(coupledAccel, tractionLimitedAccel);
            return (gearRatio >= 0f) ? netAccel : -netAccel;
        }
    }

    /// <summary>
    /// Vehicle aerodynamic damping and drag/downforce model (`tune/vehicle/*.vehcarsim`).
    /// Applies body-space rotational damping (`AngCDamp`, `AngVelDamp`, `AngVel2Damp`) and aerodynamic drag/downforce.
    /// Returns angular acceleration (rad/s^2) and linear force (N) clamped to prevent explicit Euler overshoot.
    /// </summary>
    public static class MM2Aero
    {
        public static void ComputeAeroForcesAndTorques(
            MM2CarSimConfig cfg,
            Transform carTransform,
            Vector3 linearVelocity,
            Vector3 angularVelocity,
            out Vector3 aeroForce,
            out Vector3 aeroAngularAccel)
        {
            aeroForce = Vector3.zero;
            aeroAngularAccel = Vector3.zero;

            Vector3 right = carTransform.right;
            Vector3 up = carTransform.up;
            Vector3 forward = carTransform.forward;

            // Body-space angular velocity components (wx = pitch, wy = yaw, wz = roll)
            float wx = Vector3.Dot(angularVelocity, right);
            float wy = Vector3.Dot(angularVelocity, up);
            float wz = Vector3.Dot(angularVelocity, forward);

            // Angular damping acceleration opposing body-space pitch, yaw, and roll rates.
            // Clamped so a single 50 Hz fixed step can never overshoot zero and cause oscillation.
            float maxPitchStep = Mathf.Abs(wx) * 12f;
            float maxYawStep = Mathf.Abs(wy) * 12f;
            float maxRollStep = Mathf.Abs(wz) * 12f;

            float ax = -Mathf.Clamp(
                wx * (cfg.AeroAngCDamp.x * 0.8f + cfg.AeroAngVelDamp.x) + Mathf.Abs(wx) * wx * cfg.AeroAngVel2Damp.x * 0.5f,
                -maxPitchStep, maxPitchStep);

            float ay = -Mathf.Clamp(
                wy * (cfg.AeroAngCDamp.y * 0.8f + cfg.AeroAngVelDamp.y) + Mathf.Abs(wy) * wy * cfg.AeroAngVel2Damp.y * 0.5f,
                -maxYawStep, maxYawStep);

            float az = -Mathf.Clamp(
                wz * (cfg.AeroAngCDamp.z * 0.8f + cfg.AeroAngVelDamp.z) + Mathf.Abs(wz) * wz * cfg.AeroAngVel2Damp.z * 0.5f,
                -maxRollStep, maxRollStep);

            aeroAngularAccel = right * ax + up * ay + forward * az;

            // Aerodynamic linear drag & downforce
            if (cfg.AeroDrag > 0f || cfg.AeroDown > 0f)
            {
                Vector3 horizVel = new Vector3(linearVelocity.x, 0f, linearVelocity.z);
                float dragCoef = Mathf.Clamp(cfg.AeroDrag, 0f, 5.0f);
                Vector3 dragForce = -horizVel * dragCoef;

                float clampedSpeedSq = Mathf.Min(horizVel.sqrMagnitude, 6400f); // Cap at 80 m/s (~180 mph)
                float downCoef = Mathf.Clamp(cfg.AeroDown, 0f, 3.0f);
                Vector3 downForce = -up * (clampedSpeedSq * downCoef);
                aeroForce = dragForce + downForce;
            }
        }
    }

    /// <summary>
    /// Vehicle gyroscopic stabilization and spin assist model (`tune/vehicle/*.vehGyro`).
    /// Applies gyroscopic yaw assist (`Drift`), J-turn / handbrake spin (`Spin180` / `Reverse180`),
    /// and mid-air pitch/roll leveling (`Pitch` / `Roll`) as bounded angular acceleration (rad/s^2).
    /// </summary>
    public static class MM2Gyro
    {
        public static Vector3 ComputeGyroTorque(
            MM2CarSimConfig cfg,
            Transform carTransform,
            float steeredInput,
            float rawSteerInput,
            float brakeInput,
            bool handbrakeActive,
            float rearAxleAngVel,
            int groundedWheels)
        {
            Vector3 totalAngAccel = Vector3.zero;
            Vector3 right = carTransform.right;
            Vector3 up = carTransform.up;
            Vector3 forward = carTransform.forward;

            float groundedRatio = Mathf.Clamp01(groundedWheels / 4.0f);
            float clampedAxleVel = Mathf.Clamp(rearAxleAngVel, -80f, 80f);

            // 1. Gyro Drift yaw assist:
            // Only active during sustained medium-speed turns; tapers off at high speed so straight-line driving stays planted.
            if (cfg.GyroDrift != 0f && groundedWheels > 0)
            {
                float cappedAxleVel = Mathf.Clamp(clampedAxleVel, -32f, 32f);
                float highSpeedAtten = Mathf.Clamp01(1.25f - Mathf.Abs(rearAxleAngVel) / 85f);
                float driftYawAccel = steeredInput * Mathf.Abs(steeredInput) * groundedRatio * (cappedAxleVel * 0.03f) * cfg.GyroDrift * highSpeedAtten;
                driftYawAccel = Mathf.Clamp(driftYawAccel, -1.2f, 1.2f);
                totalAngAccel += up * driftYawAccel;
            }

            // 2. Spin180 / Reverse180 J-turn assist:
            if ((cfg.GyroSpin180 != 0f || cfg.GyroReverse180 != 0f) && groundedWheels > 0)
            {
                if (clampedAxleVel < -2.0f)
                {
                    float revYawAccel = -rawSteerInput * groundedRatio * (-clampedAxleVel * 0.04f) * cfg.GyroReverse180;
                    revYawAccel = Mathf.Clamp(revYawAccel, -3.5f, 3.5f);
                    totalAngAccel += up * revYawAccel;
                }
                else if (handbrakeActive && clampedAxleVel > 2.0f)
                {
                    float spinYawAccel = rawSteerInput * groundedRatio * (clampedAxleVel * 0.04f) * cfg.GyroSpin180;
                    spinYawAccel = Mathf.Clamp(spinYawAccel, -3.5f, 3.5f);
                    totalAngAccel += up * spinYawAccel;
                }
            }

            // 3. Mid-air Pitch & Roll stabilization
            if ((cfg.GyroPitch != 0f || cfg.GyroRoll != 0f) && groundedWheels == 0)
            {
                float airBrakeSq = Mathf.Clamp01(brakeInput * brakeInput);
                float pitchAccel = Mathf.Clamp(forward.y * cfg.GyroPitch * airBrakeSq * 0.5f, -2.0f, 2.0f);
                totalAngAccel += right * pitchAccel;

                float rollAccel = Mathf.Clamp(-right.y * cfg.GyroRoll * airBrakeSq * 0.5f, -2.0f, 2.0f);
                totalAngAccel += forward * rollAccel;
            }

            return totalAngAccel;
        }
    }
}
