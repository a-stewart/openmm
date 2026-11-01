using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using MM2.Archive;

namespace MM2.Vehicle
{
    public class VehicleSpec
    {
        public string DisplayName;
        public string BaseName;
        public string FolderName;
        public string ObjPrefix;
        public string[] Colors;
        public string[] PaintjobNames;
        public Dictionary<string, string> ColorToPrefix;
        public Dictionary<string, string> SectionToSuffix;
        public float Horsepower;
        public float TopSpeedMph;
        public float Durability;
        public float MassLbs;
        public float UIDist = 5.5f;
        public bool HasExplicitUIDist;
        public float ScoringBias = 10f;
        public int UnlockScore;
        public int UnlockFlags;
        public int LockColorMask;
        public bool IsMM1Vehicle;
        public int InfoArchiveType; // 0 = DAVE (MM2), 1 = ARES (MM1), 2 = PKZIP (Mod)
        public float NormalizedHP;
        public float NormalizedTopSpeed;
        public float NormalizedDurability;
        public float NormalizedMass;
        public float HorsepowerScore => NormalizedHP;
        public float TopSpeedScore => NormalizedTopSpeed;
        public float DurabilityScore => NormalizedDurability;
        public float MassScore => NormalizedMass;
        public string Description;
        public Vector3 WheelPosFL;
        public Vector3 WheelPosFR;
        public Vector3 WheelPosRL;
        public Vector3 WheelPosRR;
        public float WheelRadius = 0.35f;
        public string IdleAudioName;
        public string DriveAudioName;
        public string HornAudioName;

        // Physics tune parsed from tune/vehicle/<basename>.vehcarsim, .vehGyro, and .asNode
        public bool HasCarSim;
        public MM2CarSimConfig CarSim = new MM2CarSimConfig();
        public float SimMassKg = 1400f;
        public Vector3 CenterOfGravity = new Vector3(0f, -0.45f, 0.05f);
        public float SteeringLimitRad = 0.42f;
        public float FrontStaticFric = 3.0f;
        public float FrontSlidingFric = 2.9f;
        public float FrontBrakeCoef = 0.65f;
        public float RearStaticFric = 3.0f;
        public float RearSlidingFric = 2.6f;
        public float RearBrakeCoef = 0.65f;
        public float HandbrakeCoef = 2.0f;
        public float AeroDown = 0.0f;

        // Damage tune parsed from tune/vehicle/<basename>.vehCarDamage (with fallback defaults for modded vehicles)
        public float MaxDamage = 343750f;
        public float MedDamage = 200000f;
        public float ImpactThreshold = 1500f;
        public Vector3 SmokeOffset = new Vector3(0f, 0.65f, 1.75f);
        public Vector3 SmokeOffset2 = new Vector3(0f, 0.65f, 1.75f);
        public bool DoublePivot;
    }

    /// <summary>
    /// Dynamically discovers and indexes all base game and mod vehicles from `.ar` archives in `resources/`
    /// by parsing `tune/*.info`, `aud/cardata/player/*.csv`, and wheel `.mtx` files at runtime.
    /// </summary>
    public static class VehicleCatalog
    {
        private static bool _initialized;
        private static string[] _vehicleNames;
        private static readonly Dictionary<string, VehicleSpec> Specs =
            new Dictionary<string, VehicleSpec>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, VehicleSpec> SpecsByBase =
            new Dictionary<string, VehicleSpec>(StringComparer.OrdinalIgnoreCase);

        // Defines the display order of vehicles: vehicles from the base game are shown first
        // in this order, followed by any addon/mod vehicles sorted alphabetically.
        private static readonly string[] BaseGameBaseNameOrder =
        {
            "vpcoop",
            "vpbug",
            "vpcab",
            "vpcaddie",
            "vpford",
            "vpmustang99",
            "vpcop",
            "vpbullet",
            "vppanoz",
            "vpbus",
            "vpdblbus",
            "vpsemi",
            "vpcentury",
            "vpcoop2k",
            "vpdune",
            "vpvwcup",
            "vphumvee",
            "vpauditt",
            "vpdb7",
            "vppanozgt",
            "vpfire"
        };

        public static string[] VehicleNames
        {
            get
            {
                EnsureInitialized();
                return _vehicleNames;
            }
        }

        public static VehicleSpec GetSpec(string vehicleName)
        {
            EnsureInitialized();
            if (!string.IsNullOrEmpty(vehicleName))
            {
                if (Specs.TryGetValue(vehicleName, out var spec))
                    return spec;
                if (SpecsByBase.TryGetValue(vehicleName, out var byBase))
                    return byBase;
            }
            if (SpecsByBase.TryGetValue("vpmustang99", out var defaultByBase))
            {
                return defaultByBase;
            }
            foreach (var kv in Specs)
            {
                return kv.Value;
            }
            return null;
        }

        public static VehicleSpec GetSpecByBaseName(string baseName)
        {
            EnsureInitialized();
            if (!string.IsNullOrEmpty(baseName) && SpecsByBase.TryGetValue(baseName, out var spec))
            {
                return spec;
            }
            return GetSpec(baseName);
        }

        public static List<VehicleSpec> GetAllSpecs()
        {
            EnsureInitialized();
            var list = new List<VehicleSpec>(SpecsByBase.Values);
            list.Sort((a, b) =>
            {
                int cmp = string.Compare(a?.BaseName, b?.BaseName, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                return string.Compare(a?.DisplayName, b?.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        public static void EnsureInitialized()
        {
            if (_initialized && _vehicleNames != null && _vehicleNames.Length > 0) return;

            MM2ArchiveSystem.EnsureInitialized();
            Specs.Clear();
            SpecsByBase.Clear();

            var infoPaths = MM2ArchiveSystem.GetFilesWithPrefixAndExtension("tune/", ".info");
            var parsedByBase = new Dictionary<string, VehicleSpec>(StringComparer.OrdinalIgnoreCase);

            foreach (string vpath in infoPaths)
            {
                byte[] bytes = MM2ArchiveSystem.ReadFileBytes(vpath);
                if (bytes == null || bytes.Length == 0) continue;

                VehicleSpec spec = ParseVehicleInfo(bytes);
                if (spec == null || string.IsNullOrEmpty(spec.BaseName)) continue;

                // Model-type precedence: check if an MM2 .pkg (PKG3) model or MM1 .bms (3HSM) model exists
                bool hasMm2Model = MM2RuntimePkgLoader.HasMm2PkgModel(spec.BaseName);
                bool hasMm1Model = MM2RuntimePkgLoader.HasMm1BmsModel(spec.BaseName);
                if (!hasMm2Model && !hasMm1Model) continue;

                spec.IsMM1Vehicle = !hasMm2Model && hasMm1Model;
                spec.InfoArchiveType = MM2ArchiveSystem.GetFileArchiveType(vpath);

                if (parsedByBase.TryGetValue(spec.BaseName, out VehicleSpec existing))
                {
                    // When both MM1 and MM2 .info files exist for the same BaseName, select the .info
                    // that matches the resolved model type (MM2 .pkg model -> MM2 .info with UIDist).
                    if (hasMm2Model && existing.HasExplicitUIDist && !spec.HasExplicitUIDist)
                    {
                        continue;
                    }
                    if (!hasMm2Model && !existing.HasExplicitUIDist && spec.HasExplicitUIDist)
                    {
                        continue;
                    }
                }

                PopulateWheelLayoutFromArchive(spec);
                PopulateCarSimFromArchive(spec);
                PopulateAudioStemsFromArchive(spec);

                parsedByBase[spec.BaseName] = spec;
            }

            var orderedNames = new List<string>();
            var addedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Add base game vehicles in authentic MM2 order
            foreach (string baseKey in BaseGameBaseNameOrder)
            {
                if (parsedByBase.TryGetValue(baseKey, out var spec))
                {
                    RegisterSpec(spec, orderedNames);
                    addedBases.Add(baseKey);
                }
            }

            // 2. Add all custom mod vehicles from .ar files in alphabetical order
            var modSpecs = new List<VehicleSpec>();
            foreach (var kv in parsedByBase)
            {
                if (!addedBases.Contains(kv.Key))
                {
                    modSpecs.Add(kv.Value);
                }
            }
            modSpecs.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            foreach (var modSpec in modSpecs)
            {
                RegisterSpec(modSpec, orderedNames);
            }

            // Fallback if no archives were mounted
            if (orderedNames.Count == 0)
            {
                var fallback = CreateFallbackDefaultSpec();
                Specs[fallback.DisplayName] = fallback;
                SpecsByBase[fallback.BaseName] = fallback;
                orderedNames.Add(fallback.DisplayName);
            }

            _vehicleNames = orderedNames.ToArray();
            _initialized = true;
            Debug.Log($"[VehicleCatalog] Loaded {_vehicleNames.Length} vehicles from .ar archives.");
        }

        private static void RegisterSpec(VehicleSpec spec, List<string> orderedNames)
        {
            string displayName = spec.DisplayName;
            if (Specs.ContainsKey(displayName))
            {
                displayName = $"{displayName} ({spec.BaseName})";
                spec.DisplayName = displayName;
            }
            Specs[displayName] = spec;
            if (!string.IsNullOrEmpty(spec.BaseName))
            {
                SpecsByBase[spec.BaseName] = spec;
            }
            orderedNames.Add(displayName);
        }

        private static VehicleSpec ParseVehicleInfo(byte[] bytes)
        {
            string text = Encoding.ASCII.GetString(bytes);
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string baseName = null;
            string description = null;
            string colorsRaw = null;
            string paintjobsRaw = null;
            float hp = 350f;
            float topSpeed = 140f;
            float durability = 800000f;
            float mass = 3500f;
            float uiDist = 5.5f;
            bool hasExplicitUiDist = false;
            float scoringBias = 10f;
            int unlockScore = 0;
            int unlockFlags = 0;
            int lockColorMask = 0;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                if (key.Equals("BaseName", StringComparison.OrdinalIgnoreCase))
                    baseName = val;
                else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    description = val;
                else if (key.Equals("Colors", StringComparison.OrdinalIgnoreCase))
                    colorsRaw = val;
                else if (key.Equals("PaintjobNames", StringComparison.OrdinalIgnoreCase))
                    paintjobsRaw = val;
                else if (key.Equals("Horsepower", StringComparison.OrdinalIgnoreCase))
                    float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out hp);
                else if (key.Equals("TopSpeed", StringComparison.OrdinalIgnoreCase) || key.Equals("Top Speed", StringComparison.OrdinalIgnoreCase))
                    float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out topSpeed);
                else if (key.Equals("Durability", StringComparison.OrdinalIgnoreCase))
                    float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out durability);
                else if (key.Equals("Mass", StringComparison.OrdinalIgnoreCase))
                    float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out mass);
                else if (key.Equals("UIDist", StringComparison.OrdinalIgnoreCase))
                {
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out uiDist))
                    {
                        hasExplicitUiDist = true;
                    }
                }
                else if (key.Equals("ScoringBias", StringComparison.OrdinalIgnoreCase))
                    float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out scoringBias);
                else if (key.Equals("UnlockScore", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out unlockScore);
                else if (key.Equals("UnlockFlags", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out unlockFlags);
                else if (key.Equals("LockColorMask", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out lockColorMask);
            }

            if (string.IsNullOrEmpty(baseName)) return null;
            if (string.IsNullOrEmpty(description)) description = baseName;

            string[] colors = ParsePipeList(colorsRaw);
            string[] paintjobs = ParsePipeList(paintjobsRaw);

            if (colors.Length == 0) colors = new[] { "Default" };
            if (paintjobs.Length == 0) paintjobs = new[] { baseName };

            var colorToPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < colors.Length; i++)
            {
                string pj = (i < paintjobs.Length) ? paintjobs[i] : paintjobs[0];
                if (!colorToPrefix.ContainsKey(colors[i]))
                {
                    colorToPrefix[colors[i]] = pj;
                }
            }

            // Compute normalized 0..1 UI score bars matching MM2's relative performance scale
            float normHp = Mathf.Clamp(hp / 1050f, 0.08f, 1.0f);
            float normSpd = Mathf.Clamp((topSpeed - 70f) / 165f, 0.10f, 1.0f);
            float normDur = Mathf.Clamp(durability / 1600000f, 0.10f, 1.0f);
            float normMass = Mathf.Clamp(mass / 12000f, 0.10f, 1.0f);

            string showcaseText =
                $"{description}\n" +
                $"Horsepower: {Mathf.RoundToInt(hp)} HP   |   Top Speed: {Mathf.RoundToInt(topSpeed)} MPH\n" +
                $"Mass: {Mathf.RoundToInt(mass)} lbs";

            // Map legacy folders for the 3 pre-imported cars if needed as fallback
            string folderName = baseName;
            string objPrefix = baseName;
            if (baseName.Equals("vpmustang99", StringComparison.OrdinalIgnoreCase))
            {
                folderName = "Mustang";
                objPrefix = "mustang";
            }
            else if (baseName.Equals("vpbullet", StringComparison.OrdinalIgnoreCase))
            {
                folderName = "Fastback";
                objPrefix = "fastback";
            }
            else if (baseName.Equals("vpdb7", StringComparison.OrdinalIgnoreCase))
            {
                folderName = "DB7";
                objPrefix = "db7";
            }

            return new VehicleSpec
            {
                DisplayName = description,
                BaseName = baseName,
                FolderName = folderName,
                ObjPrefix = objPrefix,
                Colors = colors,
                PaintjobNames = paintjobs,
                ColorToPrefix = colorToPrefix,
                SectionToSuffix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Horsepower = hp,
                TopSpeedMph = topSpeed,
                Durability = durability,
                MassLbs = mass,
                UIDist = Mathf.Clamp(uiDist, 4.2f, 13.5f),
                HasExplicitUIDist = hasExplicitUiDist,
                ScoringBias = scoringBias > 0f ? scoringBias : 10f,
                UnlockScore = Mathf.Max(0, unlockScore),
                UnlockFlags = Mathf.Max(0, unlockFlags),
                LockColorMask = Mathf.Max(0, lockColorMask),
                NormalizedHP = normHp,
                NormalizedTopSpeed = normSpd,
                NormalizedDurability = normDur,
                NormalizedMass = normMass,
                Description = showcaseText,
                WheelPosFL = new Vector3(-0.74f, 0.34f,  1.40f),
                WheelPosFR = new Vector3( 0.74f, 0.34f,  1.40f),
                WheelPosRL = new Vector3(-0.74f, 0.34f, -1.40f),
                WheelPosRR = new Vector3( 0.74f, 0.34f, -1.40f),
                WheelRadius = 0.35f,
                IdleAudioName = "mustangidle.11k",
                DriveAudioName = "mustangdrive.11k",
                HornAudioName = "mustanghorn.11k"
            };
        }

        private static string[] ParsePipeList(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            string[] parts = raw.Split('|');
            var list = new List<string>(parts.Length);
            foreach (string p in parts)
            {
                string trimmed = p.Trim();
                if (trimmed.Length > 0) list.Add(trimmed);
            }
            return list.ToArray();
        }

        private static void PopulateWheelLayoutFromArchive(VehicleSpec spec)
        {
            var mtx0 = MM2RuntimePkgLoader.ReadMtx(spec.BaseName, "whl0");
            var mtx1 = MM2RuntimePkgLoader.ReadMtx(spec.BaseName, "whl1");
            var mtx2 = MM2RuntimePkgLoader.ReadMtx(spec.BaseName, "whl2");
            var mtx3 = MM2RuntimePkgLoader.ReadMtx(spec.BaseName, "whl3");

            if (mtx0.IsValid) spec.WheelPosFL = new Vector3(-mtx0.Position.x, mtx0.Position.y, -mtx0.Position.z);
            if (mtx1.IsValid) spec.WheelPosFR = new Vector3(-mtx1.Position.x, mtx1.Position.y, -mtx1.Position.z);
            if (mtx2.IsValid) spec.WheelPosRL = new Vector3(-mtx2.Position.x, mtx2.Position.y, -mtx2.Position.z);
            if (mtx3.IsValid) spec.WheelPosRR = new Vector3(-mtx3.Position.x, mtx3.Position.y, -mtx3.Position.z);
            if (mtx0.IsValid && mtx0.Max.y > 0.1f) spec.WheelRadius = mtx0.Max.y;
        }

        private static void PopulateCarSimFromArchive(VehicleSpec spec)
        {
            string baseLower = spec.BaseName.ToLowerInvariant();
            bool preferMm2 = MM2RuntimePkgLoader.HasMm2PkgModel(spec.BaseName);
            string[] candidates = preferMm2
                ? new[]
                {
                    $"tune/vehicle/{baseLower}.vehcarsim",
                    $"tune/{baseLower}.vehcarsim",
                    baseLower == "vpfire" ? "tune/vehicle/vpftruck.vehcarsim" : null,
                    baseLower == "vpdblbus" ? "tune/vehicle/vpddbus.vehcarsim" : null,
                    baseLower == "vpcentury" ? "tune/vehicle/vpsemi.vehcarsim" : null,
                    $"tune/{baseLower}.mmcarsim"
                }
                : new[]
                {
                    $"tune/{baseLower}.mmcarsim",
                    $"tune/vehicle/{baseLower}.vehcarsim",
                    $"tune/{baseLower}.vehcarsim"
                };

            string text = null;
            foreach (string cand in candidates)
            {
                if (string.IsNullOrEmpty(cand)) continue;
                text = MM2ArchiveSystem.ReadFileText(cand);
                if (!string.IsNullOrEmpty(text)) break;
            }

            if (spec.CarSim == null) spec.CarSim = new MM2CarSimConfig();
            MM2CarSimConfig cfg = spec.CarSim;

            if (!string.IsNullOrEmpty(text))
            {
                spec.HasCarSim = true;
                string currentBlock = string.Empty;
                string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                    if (line.EndsWith("{"))
                    {
                        int braceIdx = line.IndexOf('{');
                        string blockName = line.Substring(0, braceIdx).Trim();
                        int spIdx = blockName.IndexOf(' ');
                        if (spIdx > 0) blockName = blockName.Substring(0, spIdx);
                        currentBlock = blockName;
                        continue;
                    }
                    if (line == "}")
                    {
                        currentBlock = string.Empty;
                        continue;
                    }

                    string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2) continue;

                    string key = tokens[0];
                    if (!float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v0))
                    {
                        continue;
                    }

                    float v1 = 0f, v2 = 0f;
                    bool hasVec3 = tokens.Length >= 4 &&
                                   float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v1) &&
                                   float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out v2);

                    if (string.IsNullOrEmpty(currentBlock) ||
                        currentBlock.Equals("vehCarSim", StringComparison.OrdinalIgnoreCase) ||
                        currentBlock.Equals("mmCarSim", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.Equals("Mass", StringComparison.OrdinalIgnoreCase) && v0 > 100f)
                        {
                            cfg.Mass = v0;
                            spec.SimMassKg = v0;
                        }
                        else if (key.Equals("InertiaBox", StringComparison.OrdinalIgnoreCase) && hasVec3)
                        {
                            cfg.InertiaBox = new Vector3(Mathf.Max(0.5f, v0), Mathf.Max(0.5f, v1), Mathf.Max(0.5f, v2));
                        }
                        else if ((key.Equals("CenterOfGravity", StringComparison.OrdinalIgnoreCase) ||
                                  key.Equals("BodyCG", StringComparison.OrdinalIgnoreCase)) && hasVec3)
                        {
                            // Convert AGE right-handed (-Z forward) to Unity left-handed (+Z forward)
                            cfg.CenterOfGravity = new Vector3(-v0, v1, -v2);
                            spec.CenterOfGravity = new Vector3(-v0, Mathf.Min(-0.35f, v1 - 0.25f), -v2);
                        }
                        else if (key.Equals("BoundFriction", StringComparison.OrdinalIgnoreCase))
                            cfg.BoundFriction = v0;
                        else if (key.Equals("BoundElasticity", StringComparison.OrdinalIgnoreCase))
                            cfg.BoundElasticity = v0;
                        else if (key.Equals("DrivetrainType", StringComparison.OrdinalIgnoreCase))
                            cfg.DrivetrainType = Mathf.RoundToInt(v0);
                        else if (key.Equals("SSSValue", StringComparison.OrdinalIgnoreCase))
                            cfg.SSSValue = v0;
                        else if (key.Equals("SSSThreshold", StringComparison.OrdinalIgnoreCase))
                            cfg.SSSThreshold = v0;
                        else if (key.Equals("CarFrictionHandling", StringComparison.OrdinalIgnoreCase))
                            cfg.CarFrictionHandling = v0;
                    }
                    else if (currentBlock.Equals("Aero", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.Equals("AngCDamp", StringComparison.OrdinalIgnoreCase) && hasVec3)
                            cfg.AeroAngCDamp = new Vector3(v0, v1, v2);
                        else if (key.Equals("AngVelDamp", StringComparison.OrdinalIgnoreCase) && hasVec3)
                            cfg.AeroAngVelDamp = new Vector3(v0, v1, v2);
                        else if (key.Equals("AngVel2Damp", StringComparison.OrdinalIgnoreCase) && hasVec3)
                            cfg.AeroAngVel2Damp = new Vector3(v0, v1, v2);
                        else if (key.Equals("Drag", StringComparison.OrdinalIgnoreCase))
                            cfg.AeroDrag = v0;
                        else if (key.Equals("Down", StringComparison.OrdinalIgnoreCase))
                        {
                            cfg.AeroDown = v0;
                            spec.AeroDown = v0;
                        }
                    }
                    else if (currentBlock.Equals("Engine", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.Equals("AngInertia", StringComparison.OrdinalIgnoreCase))
                            cfg.EngineAngInertia = v0;
                        else if (key.Equals("MaxHorsePower", StringComparison.OrdinalIgnoreCase))
                            cfg.EngineMaxHorsePower = v0;
                        else if (key.Equals("IdleRPM", StringComparison.OrdinalIgnoreCase))
                            cfg.EngineIdleRPM = v0;
                        else if (key.Equals("OptRPM", StringComparison.OrdinalIgnoreCase))
                            cfg.EngineOptRPM = v0;
                        else if (key.Equals("MaxRPM", StringComparison.OrdinalIgnoreCase))
                            cfg.EngineMaxRPM = v0;
                        else if (key.Equals("GCL", StringComparison.OrdinalIgnoreCase))
                            cfg.EngineGCL = v0;
                    }
                    else if (currentBlock.Equals("Trans", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.Equals("ManualNumGears", StringComparison.OrdinalIgnoreCase))
                            cfg.TransManualNumGears = Mathf.RoundToInt(v0);
                        else if (key.Equals("AutoNumGears", StringComparison.OrdinalIgnoreCase))
                            cfg.TransAutoNumGears = Mathf.RoundToInt(v0);
                        else if (key.Equals("Reverse", StringComparison.OrdinalIgnoreCase))
                            cfg.TransReverse = v0;
                        else if (key.Equals("Low", StringComparison.OrdinalIgnoreCase))
                            cfg.TransLow = v0;
                        else if (key.Equals("High", StringComparison.OrdinalIgnoreCase))
                            cfg.TransHigh = v0;
                        else if (key.Equals("GearBias", StringComparison.OrdinalIgnoreCase))
                            cfg.TransGearBias = v0;
                        else if (key.Equals("UpshiftBias", StringComparison.OrdinalIgnoreCase))
                            cfg.TransUpshiftBias = v0;
                        else if (key.Equals("DownshiftBiasMin", StringComparison.OrdinalIgnoreCase))
                            cfg.TransDownshiftBiasMin = v0;
                        else if (key.Equals("DownshiftBiasMax", StringComparison.OrdinalIgnoreCase))
                            cfg.TransDownshiftBiasMax = v0;
                        else if (key.Equals("GearChangeTime", StringComparison.OrdinalIgnoreCase))
                            cfg.TransGearChangeTime = v0;
                    }
                    else if (currentBlock.Equals("Drivetrain", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.Equals("AngInertia", StringComparison.OrdinalIgnoreCase))
                            cfg.DrivetrainAngInertia = v0;
                        else if (key.Equals("BrakeDynamicCoef", StringComparison.OrdinalIgnoreCase))
                            cfg.DrivetrainBrakeDynamicCoef = v0;
                        else if (key.Equals("BrakeStaticCoef", StringComparison.OrdinalIgnoreCase))
                            cfg.DrivetrainBrakeStaticCoef = v0;
                    }
                    else if (currentBlock.Equals("Freetrain", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.Equals("AngInertia", StringComparison.OrdinalIgnoreCase))
                            cfg.FreetrainAngInertia = v0;
                        else if (key.Equals("BrakeDynamicCoef", StringComparison.OrdinalIgnoreCase))
                            cfg.FreetrainBrakeDynamicCoef = v0;
                        else if (key.Equals("BrakeStaticCoef", StringComparison.OrdinalIgnoreCase))
                            cfg.FreetrainBrakeStaticCoef = v0;
                    }
                    else if (currentBlock.Equals("WheelFront", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseWheelConfigKey(cfg.WheelFront, key, v0);
                        if (key.Equals("SteeringLimit", StringComparison.OrdinalIgnoreCase) && v0 > 0.1f)
                            spec.SteeringLimitRad = v0;
                        else if (key.Equals("StaticFric", StringComparison.OrdinalIgnoreCase) && v0 > 0.2f)
                            spec.FrontStaticFric = v0;
                        else if (key.Equals("SlidingFric", StringComparison.OrdinalIgnoreCase) && v0 > 0.2f)
                            spec.FrontSlidingFric = v0;
                        else if (key.Equals("BrakeCoef", StringComparison.OrdinalIgnoreCase) && v0 > 0.05f)
                            spec.FrontBrakeCoef = v0;
                    }
                    else if (currentBlock.Equals("WheelBack", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseWheelConfigKey(cfg.WheelBack, key, v0);
                        if (key.Equals("StaticFric", StringComparison.OrdinalIgnoreCase) && v0 > 0.2f)
                            spec.RearStaticFric = v0;
                        else if (key.Equals("SlidingFric", StringComparison.OrdinalIgnoreCase) && v0 > 0.2f)
                            spec.RearSlidingFric = v0;
                        else if (key.Equals("BrakeCoef", StringComparison.OrdinalIgnoreCase) && v0 > 0.05f)
                            spec.RearBrakeCoef = v0;
                        else if (key.Equals("HandbrakeCoef", StringComparison.OrdinalIgnoreCase) && v0 > 0.1f)
                            spec.HandbrakeCoef = v0;
                    }
                    else if (currentBlock.Equals("AxleFront", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.Equals("TorqueCoef", StringComparison.OrdinalIgnoreCase))
                            cfg.AxleFrontTorqueCoef = v0;
                        else if (key.Equals("DampCoef", StringComparison.OrdinalIgnoreCase))
                            cfg.AxleFrontDampCoef = v0;
                    }
                    else if (currentBlock.Equals("AxleBack", StringComparison.OrdinalIgnoreCase))
                    {
                        if (key.Equals("TorqueCoef", StringComparison.OrdinalIgnoreCase))
                            cfg.AxleBackTorqueCoef = v0;
                        else if (key.Equals("DampCoef", StringComparison.OrdinalIgnoreCase))
                            cfg.AxleBackDampCoef = v0;
                    }
                }
            }

            // Also parse tune/vehicle/<basename>.vehGyro
            string[] gyroCandidates =
            {
                $"tune/vehicle/{baseLower}.vehgyro",
                $"tune/{baseLower}.vehgyro",
                baseLower == "vpfire" ? "tune/vehicle/vpftruck.vehgyro" : null,
                baseLower == "vpdblbus" ? "tune/vehicle/vpddbus.vehgyro" : null,
                baseLower == "vpcentury" ? "tune/vehicle/vpsemi.vehgyro" : null
            };
            foreach (string gCand in gyroCandidates)
            {
                if (string.IsNullOrEmpty(gCand)) continue;
                string gyroText = MM2ArchiveSystem.ReadFileText(gCand);
                if (string.IsNullOrEmpty(gyroText)) continue;

                foreach (string rawLine in gyroText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] tokens = rawLine.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2) continue;
                    if (!float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float gv)) continue;

                    string k = tokens[0];
                    if (k.Equals("Drift", StringComparison.OrdinalIgnoreCase)) cfg.GyroDrift = gv;
                    else if (k.Equals("Spin180", StringComparison.OrdinalIgnoreCase)) cfg.GyroSpin180 = gv;
                    else if (k.Equals("Reverse180", StringComparison.OrdinalIgnoreCase)) cfg.GyroReverse180 = gv;
                    else if (k.Equals("Pitch", StringComparison.OrdinalIgnoreCase)) cfg.GyroPitch = gv;
                    else if (k.Equals("Roll", StringComparison.OrdinalIgnoreCase)) cfg.GyroRoll = gv;
                }
                break;
            }

            // Also parse tune/<basename>.asNode for discrete keyboard steering rates
            string[] nodeCandidates =
            {
                $"tune/{baseLower}.asnode",
                $"tune/vehicle/{baseLower}.asnode",
                baseLower == "vpfire" ? "tune/vpftruck.asnode" : null,
                baseLower == "vpdblbus" ? "tune/vpddbus.asnode" : null,
                baseLower == "vpcentury" ? "tune/vpsemi.asnode" : null
            };
            foreach (string nCand in nodeCandidates)
            {
                if (string.IsNullOrEmpty(nCand)) continue;
                string nodeText = MM2ArchiveSystem.ReadFileText(nCand);
                if (string.IsNullOrEmpty(nodeText)) continue;

                foreach (string rawLine in nodeText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] tokens = rawLine.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2) continue;
                    if (!float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float nv)) continue;

                    string k = tokens[0];
                    if (k.Equals("SpeedBaseLow", StringComparison.OrdinalIgnoreCase)) cfg.SpeedBaseLow = nv;
                    else if (k.Equals("SpeedBaseHi", StringComparison.OrdinalIgnoreCase)) cfg.SpeedBaseHi = nv;
                    else if (k.Equals("DiscreteSteeringDeltaOutLo", StringComparison.OrdinalIgnoreCase)) cfg.DiscreteSteeringDeltaOutLo = nv;
                    else if (k.Equals("DiscreteSteeringDeltaInLo", StringComparison.OrdinalIgnoreCase)) cfg.DiscreteSteeringDeltaInLo = nv;
                    else if (k.Equals("DiscreteSteeringFilterLo", StringComparison.OrdinalIgnoreCase)) cfg.DiscreteSteeringFilterLo = nv;
                    else if (k.Equals("DiscreteSteeringDeltaOutHi", StringComparison.OrdinalIgnoreCase)) cfg.DiscreteSteeringDeltaOutHi = nv;
                    else if (k.Equals("DiscreteSteeringDeltaInHi", StringComparison.OrdinalIgnoreCase)) cfg.DiscreteSteeringDeltaInHi = nv;
                    else if (k.Equals("DiscreteSteeringFilterHi", StringComparison.OrdinalIgnoreCase)) cfg.DiscreteSteeringFilterHi = nv;
                }
                break;
            }

            // Also parse tune/vehicle/<basename>.vehCarDamage for authentic damage thresholds and engine smoke offsets
            spec.MaxDamage = Mathf.Clamp(spec.Durability * 0.36f, 200000f, 1453000f);
            spec.MedDamage = spec.MaxDamage * 0.65f;
            spec.ImpactThreshold = 1500f;
            spec.SmokeOffset = new Vector3(0f, 0.65f, 1.75f);
            spec.SmokeOffset2 = new Vector3(0f, 0.65f, 1.75f);
            spec.DoublePivot = false;

            string[] dmgCandidates =
            {
                $"tune/vehicle/{baseLower}.vehcardamage",
                $"tune/{baseLower}.vehcardamage",
                baseLower == "vpfire" ? "tune/vehicle/vpftruck.vehcardamage" : null,
                baseLower == "vpdblbus" ? "tune/vehicle/vpddbus.vehcardamage" : null,
                baseLower == "vpcentury" ? "tune/vehicle/vpsemi.vehcardamage" : null,
                baseLower == "vphumvee" ? "tune/vehicle/vp4x4.vehcardamage" : null
            };
            foreach (string dCand in dmgCandidates)
            {
                if (string.IsNullOrEmpty(dCand)) continue;
                string dmgText = MM2ArchiveSystem.ReadFileText(dCand);
                if (string.IsNullOrEmpty(dmgText)) continue;

                foreach (string rawLine in dmgText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] tokens = rawLine.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2) continue;

                    string k = tokens[0];
                    if (k.Equals("MaxDamage", StringComparison.OrdinalIgnoreCase) &&
                        float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float maxDmg) &&
                        maxDmg > 100f)
                    {
                        spec.MaxDamage = maxDmg;
                    }
                    else if (k.Equals("MedDamage", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float medDmg) &&
                             medDmg > 50f)
                    {
                        spec.MedDamage = medDmg;
                    }
                    else if (k.Equals("ImpactThreshold", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float impThresh) &&
                             impThresh >= 0f)
                    {
                        spec.ImpactThreshold = impThresh;
                    }
                    else if (k.Equals("DoublePivot", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dblPiv))
                    {
                        spec.DoublePivot = dblPiv != 0;
                    }
                    else if (k.Equals("SmokeOffset", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 4 &&
                             float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sx) &&
                             float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float sy) &&
                             float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float sz))
                    {
                        // Convert AGE right-handed (-Z forward) to Unity left-handed (+Z forward)
                        spec.SmokeOffset = new Vector3(sx, sy, -sz);
                    }
                    else if (k.Equals("SmokeOffset2", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 4 &&
                             float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sx2) &&
                             float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float sy2) &&
                             float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float sz2))
                    {
                        spec.SmokeOffset2 = new Vector3(sx2, sy2, -sz2);
                    }
                }
                break;
            }
        }

        private static void ParseWheelConfigKey(MM2WheelConfig wCfg, string key, float val)
        {
            if (key.Equals("SuspensionExtent", StringComparison.OrdinalIgnoreCase)) wCfg.SuspensionExtent = val;
            else if (key.Equals("SuspensionLimit", StringComparison.OrdinalIgnoreCase)) wCfg.SuspensionLimit = val;
            else if (key.Equals("SuspensionFactor", StringComparison.OrdinalIgnoreCase)) wCfg.SuspensionFactor = val;
            else if (key.Equals("SuspensionDampCoef", StringComparison.OrdinalIgnoreCase)) wCfg.SuspensionDampCoef = val;
            else if (key.Equals("SteeringLimit", StringComparison.OrdinalIgnoreCase)) wCfg.SteeringLimit = val;
            else if (key.Equals("SteeringOffset", StringComparison.OrdinalIgnoreCase)) wCfg.SteeringOffset = val;
            else if (key.Equals("BrakeCoef", StringComparison.OrdinalIgnoreCase)) wCfg.BrakeCoef = val;
            else if (key.Equals("HandbrakeCoef", StringComparison.OrdinalIgnoreCase)) wCfg.HandbrakeCoef = val;
            else if (key.Equals("CamberLimit", StringComparison.OrdinalIgnoreCase)) wCfg.CamberLimit = val;
            else if (key.Equals("WobbleLimit", StringComparison.OrdinalIgnoreCase)) wCfg.WobbleLimit = val;
            else if (key.Equals("TireDispLimitLong", StringComparison.OrdinalIgnoreCase)) wCfg.TireDispLimitLong = val;
            else if (key.Equals("TireDampCoefLong", StringComparison.OrdinalIgnoreCase)) wCfg.TireDampCoefLong = val;
            else if (key.Equals("TireDragCoefLong", StringComparison.OrdinalIgnoreCase)) wCfg.TireDragCoefLong = val;
            else if (key.Equals("TireDispLimitLat", StringComparison.OrdinalIgnoreCase)) wCfg.TireDispLimitLat = val;
            else if (key.Equals("TireDampCoefLat", StringComparison.OrdinalIgnoreCase)) wCfg.TireDampCoefLat = val;
            else if (key.Equals("TireDragCoefLat", StringComparison.OrdinalIgnoreCase)) wCfg.TireDragCoefLat = val;
            else if (key.Equals("OptimumSlipPercent", StringComparison.OrdinalIgnoreCase)) wCfg.OptimumSlipPercent = val;
            else if (key.Equals("StaticFric", StringComparison.OrdinalIgnoreCase)) wCfg.StaticFric = val;
            else if (key.Equals("SlidingFric", StringComparison.OrdinalIgnoreCase)) wCfg.SlidingFric = val;
        }

        private static void PopulateAudioStemsFromArchive(VehicleSpec spec)
        {
            string baseLower = spec.BaseName.ToLowerInvariant();
            bool preferMm2 = MM2RuntimePkgLoader.HasMm2PkgModel(spec.BaseName);

            if (preferMm2)
            {
                if (TryPopulateMm2AudioStems(spec, baseLower)) return;
                TryPopulateMm1AudioStems(spec, baseLower);
            }
            else
            {
                if (TryPopulateMm1AudioStems(spec, baseLower)) return;
                TryPopulateMm2AudioStems(spec, baseLower);
            }
        }

        private static bool TryPopulateMm2AudioStems(VehicleSpec spec, string baseLower)
        {
            string csvPath = $"aud/cardata/player/{baseLower}.csv";
            byte[] csvBytes = MM2ArchiveSystem.ReadFileBytes(csvPath);
            if (csvBytes == null || csvBytes.Length == 0) return false;

            string text = Encoding.ASCII.GetString(csvBytes);
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // In MM2 aud/cardata/player/<base>.csv:
            // Line 1 col 0 = Horn sound stem
            // Line 3 col 0 = Engine Idle sound stem
            // Line 4+ col 0 = Engine High / Drive sound stem
            if (lines.Length > 1)
            {
                string horn = GetFirstCsvToken(lines[1]);
                if (IsValidSoundStem(horn)) spec.HornAudioName = horn;
            }
            if (lines.Length > 3)
            {
                string idle = GetFirstCsvToken(lines[3]);
                if (IsValidSoundStem(idle)) spec.IdleAudioName = idle;
            }
            for (int i = 4; i < lines.Length; i++)
            {
                string drive = GetFirstCsvToken(lines[i]);
                if (IsValidSoundStem(drive))
                {
                    spec.DriveAudioName = drive;
                    break;
                }
            }
            return true;
        }

        private static bool TryPopulateMm1AudioStems(VehicleSpec spec, string baseLower)
        {
            string audioText = MM2ArchiveSystem.ReadFileText($"tune/{baseLower}.mmplayercaraudio");
            if (string.IsNullOrEmpty(audioText)) return false;

            string[] lines = audioText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                int q1 = line.IndexOf('"');
                int q2 = line.LastIndexOf('"');
                if (q1 <= 0 || q2 <= q1) continue;

                string val = line.Substring(q1 + 1, q2 - q1 - 1).Trim();
                if (!IsValidSoundStem(val)) continue;

                if (line.StartsWith("m_pcHornWaveName", StringComparison.OrdinalIgnoreCase))
                {
                    spec.HornAudioName = val;
                }
                else if (line.StartsWith("m_pcLowRpmWave", StringComparison.OrdinalIgnoreCase))
                {
                    spec.IdleAudioName = val;
                }
                else if (line.StartsWith("m_pcHighRpmWave", StringComparison.OrdinalIgnoreCase))
                {
                    spec.DriveAudioName = val;
                }
            }
            return true;
        }

        private static string GetFirstCsvToken(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int comma = line.IndexOf(',');
            string tok = (comma >= 0) ? line.Substring(0, comma) : line;
            return tok.Trim();
        }

        private static bool IsValidSoundStem(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return false;
            if (stem.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            if (stem.Equals("sfx", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static VehicleSpec CreateFallbackDefaultSpec()
        {
            return new VehicleSpec
            {
                DisplayName = "Default Vehicle",
                BaseName = "vpmustang99",
                FolderName = "Mustang",
                ObjPrefix = "mustang",
                Colors = new[] { "Default" },
                PaintjobNames = new[] { "vpmustangyellow" },
                ColorToPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Default", "vpmustangyellow" }
                },
                SectionToSuffix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Horsepower = 600f,
                TopSpeedMph = 160f,
                Durability = 960000f,
                MassLbs = 5100f,
                UIDist = 5.5f,
                NormalizedHP = 0.58f,
                NormalizedTopSpeed = 0.68f,
                NormalizedDurability = 0.62f,
                NormalizedMass = 0.52f,
                Description = "No vehicle archives (.ar) detected in 'resources/'. Place your game archives in the 'resources/' directory.",
                WheelPosFL = new Vector3(-0.735f, 0.323f,  1.404f),
                WheelPosFR = new Vector3( 0.738f, 0.323f,  1.404f),
                WheelPosRL = new Vector3(-0.733f, 0.323f, -1.427f),
                WheelPosRR = new Vector3( 0.733f, 0.323f, -1.427f),
                WheelRadius = 0.346f,
                IdleAudioName = "mustangidle.11k",
                DriveAudioName = "mustangdrive.11k",
                HornAudioName = "mustanghorn.11k"
            };
        }
    }

    /// <summary>
    /// Applies paintjobs and builds 3D vehicle models dynamically at runtime
    /// directly from `.ar` archives in `resources/`.
    /// </summary>
    public class VehiclePaintjob : MonoBehaviour
    {
        public string currentVehicle = "";
        public string currentColor = "Default";

        private void Start()
        {
            if (transform.Find("Body_Mesh") == null && TryGetComponent<ArcadeVehicleController>(out var car))
            {
                ConfigurePlayableVehicle(car, currentVehicle, currentColor);
                return;
            }

            if (!string.IsNullOrEmpty(currentColor))
            {
                ApplyColor(currentVehicle, currentColor);
            }
        }

        public void ApplyColor(string colorName)
        {
            ApplyColor(currentVehicle, colorName);
        }

        public void ApplyColor(string vehicleName, string colorName)
        {
            if (!string.IsNullOrEmpty(vehicleName)) currentVehicle = vehicleName;
            if (!string.IsNullOrEmpty(colorName)) currentColor = colorName;
            ApplyColorToHierarchy(transform, currentVehicle, currentColor);
        }

        public static void ApplyColorToHierarchy(Transform targetRoot, string colorName)
        {
            ApplyColorToHierarchy(targetRoot, null, colorName);
        }

        public static void ApplyColorToHierarchy(Transform targetRoot, string vehicleName, string colorName)
        {
            if (targetRoot == null) return;

            VehicleSpec spec = VehicleCatalog.GetSpec(vehicleName);
            if (spec == null) return;

            int paintIdx = 0;
            if (!string.IsNullOrEmpty(colorName) && spec.Colors != null)
            {
                int found = Array.FindIndex(spec.Colors, c => string.Equals(c, colorName, StringComparison.OrdinalIgnoreCase));
                if (found >= 0) paintIdx = found;
            }

            // Apply via runtime .pkg shader slot binder
            MM2RuntimePkgLoader.ApplyPaintjobToHierarchy(targetRoot, paintIdx);
        }

        /// <summary>
        /// Builds the complete visual hierarchy (`Body_Mesh` and `VisualWheels`) directly from `.ar` archive `.pkg` and `.mtx` files.
        /// </summary>
        public static void PopulateVehicleVisuals(
            Transform targetParent,
            string vehicleName,
            string colorName,
            out Transform bodyMeshRoot,
            out Transform wheelFL,
            out Transform wheelFR,
            out Transform wheelRL,
            out Transform wheelRR
        )
        {
            VehicleSpec spec = VehicleCatalog.GetSpec(vehicleName);
            int paintIdx = 0;
            if (spec != null && !string.IsNullOrEmpty(colorName) && spec.Colors != null)
            {
                int found = Array.FindIndex(spec.Colors, c => string.Equals(c, colorName, StringComparison.OrdinalIgnoreCase));
                if (found >= 0) paintIdx = found;
            }

            if (spec != null && MM2RuntimePkgLoader.BuildRuntimeVehicleVisuals(
                    spec.BaseName,
                    paintIdx,
                    targetParent,
                    out bodyMeshRoot,
                    out wheelFL,
                    out wheelFR,
                    out wheelRL,
                    out wheelRR,
                    out Vector3 posFL,
                    out Vector3 posFR,
                    out Vector3 posRL,
                    out Vector3 posRR,
                    out float wheelRadius))
            {
                spec.WheelPosFL = posFL;
                spec.WheelPosFR = posFR;
                spec.WheelPosRL = posRL;
                spec.WheelPosRR = posRR;
                if (wheelRadius > 0.1f) spec.WheelRadius = wheelRadius;
                return;
            }

            // Fallback empty hierarchy if archive wasn't found
            bodyMeshRoot = new GameObject("Body_Mesh").transform;
            bodyMeshRoot.SetParent(targetParent, false);
            wheelFL = new GameObject("FL_Mesh").transform;
            wheelFL.SetParent(targetParent, false);
            wheelFR = new GameObject("FR_Mesh").transform;
            wheelFR.SetParent(targetParent, false);
            wheelRL = new GameObject("RL_Mesh").transform;
            wheelRL.SetParent(targetParent, false);
            wheelRR = new GameObject("RR_Mesh").transform;
            wheelRR.SetParent(targetParent, false);
        }

        /// <summary>
        /// Reconfigures an active playable car (its visual meshes, wheel positions, physics mass/top speed, and engine/horn audio)
        /// to match the chosen vehicle and color from `MM2GameSession`.
        /// </summary>
        public static void ConfigurePlayableVehicle(ArcadeVehicleController car, string vehicleName, string colorName)
        {
            if (car == null) return;
            VehicleSpec spec = VehicleCatalog.GetSpec(vehicleName);
            if (spec == null) return;

            var existingDamage = car.GetComponent<MM2VehicleDamage>();
            if (existingDamage != null)
            {
                existingDamage.PrepareForReconfigure();
            }

            Transform oldBody = car.transform.Find("Body_Mesh");
            if (oldBody != null) Destroy(oldBody.gameObject);

            Transform oldWheels = car.transform.Find("VisualWheels");
            if (oldWheels != null) Destroy(oldWheels.gameObject);

            Transform oldLights = car.transform.Find("VehicleLights");
            if (oldLights != null) Destroy(oldLights.gameObject);

            PopulateVehicleVisuals(
                car.transform,
                spec.DisplayName,
                colorName,
                out _,
                out Transform flMesh,
                out Transform frMesh,
                out Transform rlMesh,
                out Transform rrMesh
            );

            car.frontLeftMesh = flMesh;
            car.frontRightMesh = frMesh;
            car.rearLeftMesh = rlMesh;
            car.rearRightMesh = rrMesh;

            Transform lightsRoot = car.transform.Find("VehicleLights");
            car.headlightsGlowObj = lightsRoot != null ? lightsRoot.Find("Headlights_Glow")?.gameObject : null;
            car.brakelightsGlowObj = lightsRoot != null ? lightsRoot.Find("Brakelights_Glow")?.gameObject : null;
            car.reverselightsGlowObj = lightsRoot != null ? lightsRoot.Find("Reverselights_Glow")?.gameObject : null;
            car.SetHeadlights(car.headlightsOn);

            // Update WheelCollider positions & radii to match the vehicle's authentic .mtx offsets
            // (+0.04m Y mount offset matches the 1G resting equilibrium of 0.26m suspensionDistance)
            Vector3 suspMountOffset = new Vector3(0f, 0.04f, 0f);
            if (car.frontLeftCollider != null)
            {
                car.frontLeftCollider.transform.localPosition = spec.WheelPosFL + suspMountOffset;
                car.frontLeftCollider.radius = spec.WheelRadius;
            }
            if (car.frontRightCollider != null)
            {
                car.frontRightCollider.transform.localPosition = spec.WheelPosFR + suspMountOffset;
                car.frontRightCollider.radius = spec.WheelRadius;
            }
            if (car.rearLeftCollider != null)
            {
                car.rearLeftCollider.transform.localPosition = spec.WheelPosRL + suspMountOffset;
                car.rearLeftCollider.radius = spec.WheelRadius;
            }
            if (car.rearRightCollider != null)
            {
                car.rearRightCollider.transform.localPosition = spec.WheelPosRR + suspMountOffset;
                car.rearRightCollider.radius = spec.WheelRadius;
            }

            // Update physics tuning from .info + .vehcarsim
            float targetMassKg = spec.HasCarSim
                ? Mathf.Clamp(spec.SimMassKg, 900f, 3800f)
                : Mathf.Clamp(spec.MassLbs * 0.30f, 1050f, 2600f);

            car.vehicleMassKg = targetMassKg;
            car.centerOfMassOffset = spec.HasCarSim ? spec.CenterOfGravity : new Vector3(0f, -0.45f, 0.05f);
            // .info TopSpeed is for the UI showcase bar; actual physics top speed is .vehcarsim Trans.High
            car.maxSpeedMph = (spec.HasCarSim && spec.CarSim.TransHigh > 30f)
                ? spec.CarSim.TransHigh
                : Mathf.Clamp(spec.TopSpeedMph * 0.72f, 75f, 135f);

            // Scale motor torque and brake torque with vehicle mass and horsepower so heavy vehicles accelerate & stop just as crisply
            float massScale = targetMassKg / 1400f;
            car.motorTorque = Mathf.Clamp(3600f * (spec.Horsepower / 500f) * Mathf.Lerp(1f, massScale, 0.6f), 2400f, 9500f);
            car.brakeTorque = Mathf.Clamp(16000f * massScale, 14000f, 42000f);
            car.coastBrakeTorque = Mathf.Clamp(2200f * massScale, 1800f, 6000f);
            car.brakeDecelAssist = 24f;
            car.coastDeceleration = 4.8f;
            car.coastLinearDrag = 0.08f;

            // Tire friction & progressive cornering/drift tune from .vehcarsim
            car.forwardGripStiffness = Mathf.Clamp(spec.FrontStaticFric * 0.82f, 2.2f, 2.65f);
            car.frontSidewaysGripStiffness = Mathf.Clamp(spec.FrontStaticFric * 0.72f, 1.95f, 2.35f);
            car.rearSidewaysGripStiffness = Mathf.Clamp(spec.RearStaticFric * 0.65f, 1.75f, 2.15f);
            car.maxSteerAngle = Mathf.Clamp(spec.SteeringLimitRad * Mathf.Rad2Deg, 20f, 28f);
            car.highSpeedSteerReduction = 0.34f;
            car.lateralGripAssist = 3.2f;
            car.velocityTurnAlign = 0.0f;
            car.steerYawAssist = 0.45f;

            car.ApplyMM2CarSimConfig(spec.CarSim);
            car.EnforceMinimumBaseTune();
            car.ApplyPhysicsAndWheelTune();

            // Update engine & horn audio clips from .ar archives (with fallback to Resources/Assets)
            var vAudio = car.GetComponent<VehicleAudio>();
            if (vAudio != null)
            {
                AudioClip idle = LoadVehicleAudio(spec.FolderName, spec.IdleAudioName);
                AudioClip drive = LoadVehicleAudio(spec.FolderName, spec.DriveAudioName);
                AudioClip horn = LoadVehicleAudio(spec.FolderName, spec.HornAudioName);
                vAudio.SwitchAudioClips(idle, drive, horn);
            }

            var pj = car.GetComponent<VehiclePaintjob>();
            if (pj == null) pj = car.gameObject.AddComponent<VehiclePaintjob>();
            pj.currentVehicle = spec.DisplayName;
            pj.currentColor = colorName;

            var vDamage = car.GetComponent<MM2VehicleDamage>();
            if (vDamage == null) vDamage = car.gameObject.AddComponent<MM2VehicleDamage>();
            vDamage.ConfigureFromSpec(spec, isPlayer: true);
            car.VehicleDamage = vDamage;
        }

        private static AudioClip LoadVehicleAudio(string folderName, string clipNameWithoutExt)
        {
            if (string.IsNullOrEmpty(clipNameWithoutExt)) return null;
            return MM2RuntimeDecoders.LoadAudioClip(clipNameWithoutExt);
        }
    }
}
