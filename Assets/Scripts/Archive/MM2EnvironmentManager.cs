using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using MM2.CameraSystem;
using MM2.UI;
using MM2.Vehicle;

namespace MM2.Archive
{
    /// <summary>
    /// Time-of-Day (Morning, Noon, Evening, Night) and Weather (Clear, Cloudy, Foggy, Rainy) manager:
    /// - Combined condition index `idx = Weather + TimeOfDay * 4` (`0..15`).
    /// - Parses `city/<map>.lt00` .. `city/<map>.lt15` (Key, Fill1, Fill2 headings/pitches/colors + packed ARGB Ambient).
    /// - Parses `city/<map>.sky` (`sky_dome` / `sky_dome_l` + rotation speed `0.005` rad/s) and
    ///   `city/<map>_fog.csv` (16 rows of RGB fog color + fog start/end distances), selecting sky texture
    ///   `sky_<w><t>_f` / `skylondon_<w><t>_l` (`<w>` in `c,p,f,r`, `<t>` in `m,a,d,n`).
    /// - Night & Rain texture variants:
    ///   Swaps `<tex>` -> `<tex>_fa` when `Weather == 3` (Rainy) and `<tex>` -> `<tex>_ni` when `TimeOfDay == 3` (Night).
    /// - Rain particle birth rule:
    ///   Loads `tune/rain.asBirthRule` and `texture/ptx_rain.tex`.
    /// - Weather audio:
    ///   Loops `rainexterior.22k` / `raininterior.22k` + `thunder.22k` in Rainy weather, and `foghorn.22k` in Foggy weather.
    /// - Weather physics & headlights:
    ///   Sets `WeatherFriction` to `0.75f` (Rainy Night) or `0.80f` (Rainy Day),
    ///   modulated by each car's `CarFrictionHandling`, and enables vehicle headlights in Evening, Night, Foggy, or Rainy.
    /// </summary>
    public sealed class MM2EnvironmentManager : MonoBehaviour
    {
        public static readonly string[] TimeOfDayNames = { "Morning", "Noon", "Evening", "Night" };
        public static readonly string[] WeatherNames = { "Clear", "Cloudy", "Foggy", "Rainy" };

        private static readonly char[] WeatherCodeChars = { 'c', 'p', 'f', 'r' };
        // Matches shader order in sky_dome.pkg (SF) and sky_dome_l.pkg (London):
        // 0 = Morning ('a' = AM), 1 = Noon ('n' = Noon), 2 = Evening ('d' = Dusk), 3 = Night ('m' = Midnight)
        private static readonly char[] TimeCodeChars = { 'a', 'n', 'd', 'm' };

        public struct LightConditionSpec
        {
            public float KeyHeading;
            public float KeyPitch;
            public Color KeyColor;

            public float Fill1Heading;
            public float Fill1Pitch;
            public Color Fill1Color;

            public float Fill2Heading;
            public float Fill2Pitch;
            public Color Fill2Color;

            public Color AmbientColor;
        }

        public struct FogConditionSpec
        {
            public Color FogColor;
            public float FogStart;
            public float FogEnd;
            public string Description;
        }

        public struct RainBirthRuleSpec
        {
            public Vector3 PositionVar;
            public Vector3 Velocity;
            public float SpewRate;
            public float Radius;
            public float Life;
        }

        private sealed class RegisteredMaterialEntry
        {
            public Material Material;
            public string BaseTextureName;
            public bool IsRoadOrSidewalk;
            public bool IsGlowOrLamp;
            public float BaseSmoothness;
        }

        public static MM2EnvironmentManager Instance { get; private set; }

        /// <summary>
        /// Global surface friction multiplier for weather conditions.
        /// 1.0 in Clear/Cloudy, 0.92 in Foggy, 0.80 in daytime Rainy, 0.75 in Night Rainy.
        /// </summary>
        public static float WeatherFriction { get; private set; } = 1.0f;

        public static int CurrentTimeOfDayIndex { get; private set; } = 1; // Noon
        public static int CurrentWeatherIndex { get; private set; } = 0;   // Clear
        public static string CurrentMapName { get; private set; } = "sf";

        private static readonly List<RegisteredMaterialEntry> registeredMaterials = new List<RegisteredMaterialEntry>(512);
        private static readonly Dictionary<Material, RegisteredMaterialEntry> registeredByMaterial =
            new Dictionary<Material, RegisteredMaterialEntry>();
        private static readonly Dictionary<string, bool> variantExistsCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly LightConditionSpec[] lightSpecs = new LightConditionSpec[16];
        private readonly FogConditionSpec[] fogSpecs = new FogConditionSpec[16];
        private RainBirthRuleSpec rainSpec;
        private string loadedCityForSpecs = string.Empty;

        private string skyPkgName = "sky_dome";
        private float skyYOffset = 0f;
        private float skyScale = 0.95f;
        private float skyRotSpeedRad = 0.005f;

        // Scene Lights
        private Light keyLight;
        private Light fillLight1;
        private Light fillLight2;

        // Rain Particle System
        private GameObject rainRootGo;
        private ParticleSystem rainParticles;
        private ParticleSystemRenderer rainRenderer;
        private Material rainMaterial;

        // Weather & UI Audio
        private AudioSource rainExtSource;
        private AudioSource rainIntSource;
        private AudioSource windSource;
        private AudioSource sfxOneShotSource;
        private AudioClip clipRainExterior;
        private AudioClip clipRainInterior;
        private AudioClip clipThunder;
        private AudioClip clipFoghorn;
        private AudioClip clipWind;
        private float nextAmbientEventTime;
        private float lightningFlashIntensity;

        // Cruise Mode Toast Notification
        private string toastMessage = string.Empty;
        private float toastTimer;
        private GUIStyle toastBoxStyle;
        private GUIStyle toastTitleStyle;
        private GUIStyle toastSubStyle;

        public static MM2EnvironmentManager EnsureExists()
        {
            if (Instance != null) return Instance;

            Instance = FindFirstObjectByType<MM2EnvironmentManager>();
            if (Instance != null) return Instance;

            GameObject go = new GameObject("MM2_EnvironmentManager");
            Instance = go.AddComponent<MM2EnvironmentManager>();
            return Instance;
        }

        public static void ClearRegisteredMaterials()
        {
            registeredMaterials.Clear();
            registeredByMaterial.Clear();
        }

        public static void RegisterDynamicMaterial(Material mat, string baseTextureName)
        {
            if (mat == null || string.IsNullOrEmpty(baseTextureName)) return;

            string cleanBase = CleanTexName(baseTextureName);
            if (string.IsNullOrEmpty(cleanBase)) return;

            // Strip any existing _ni or _fa suffix so we always track the canonical base texture name
            if (cleanBase.EndsWith("_ni", StringComparison.OrdinalIgnoreCase))
            {
                cleanBase = cleanBase.Substring(0, cleanBase.Length - 3);
            }
            else if (cleanBase.EndsWith("_fa", StringComparison.OrdinalIgnoreCase))
            {
                cleanBase = cleanBase.Substring(0, cleanBase.Length - 3);
            }

            if (registeredByMaterial.TryGetValue(mat, out RegisteredMaterialEntry existing))
            {
                existing.BaseTextureName = cleanBase;
                return;
            }

            bool isRoadOrSw =
                cleanBase.StartsWith("r1_", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.StartsWith("r2_", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.StartsWith("r3_", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.StartsWith("r4", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.StartsWith("r6_", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.StartsWith("rfwy", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.StartsWith("rxwalk", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.StartsWith("r_alley", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.StartsWith("swalk", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.IndexOf("road", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cleanBase.IndexOf("asphalt", StringComparison.OrdinalIgnoreCase) >= 0;

            bool isGlowOrLamp =
                cleanBase.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cleanBase.Equals("sp_light", StringComparison.OrdinalIgnoreCase) ||
                cleanBase.IndexOf("lightpost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cleanBase.IndexOf("glass_light", StringComparison.OrdinalIgnoreCase) >= 0;

            float baseSmooth = mat.HasProperty("_Smoothness") ? mat.GetFloat("_Smoothness") : 0.08f;

            var entry = new RegisteredMaterialEntry
            {
                Material = mat,
                BaseTextureName = cleanBase,
                IsRoadOrSidewalk = isRoadOrSw,
                IsGlowOrLamp = isGlowOrLamp,
                BaseSmoothness = baseSmooth
            };

            registeredMaterials.Add(entry);
            registeredByMaterial[mat] = entry;
        }

        public static int ParseTimeOfDayIndex(string timeOfDay)
        {
            if (string.IsNullOrEmpty(timeOfDay)) return 1;
            for (int i = 0; i < TimeOfDayNames.Length; i++)
            {
                if (string.Equals(TimeOfDayNames[i], timeOfDay.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            if (timeOfDay.StartsWith("Morn", StringComparison.OrdinalIgnoreCase)) return 0;
            if (timeOfDay.StartsWith("Noon", StringComparison.OrdinalIgnoreCase) ||
                timeOfDay.StartsWith("Day", StringComparison.OrdinalIgnoreCase)) return 1;
            if (timeOfDay.StartsWith("Eve", StringComparison.OrdinalIgnoreCase) ||
                timeOfDay.StartsWith("Dusk", StringComparison.OrdinalIgnoreCase) ||
                timeOfDay.StartsWith("Sun", StringComparison.OrdinalIgnoreCase)) return 2;
            if (timeOfDay.StartsWith("Nig", StringComparison.OrdinalIgnoreCase)) return 3;
            return 1;
        }

        public static int ParseWeatherIndex(string weather)
        {
            if (string.IsNullOrEmpty(weather)) return 0;
            for (int i = 0; i < WeatherNames.Length; i++)
            {
                if (string.Equals(WeatherNames[i], weather.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            if (weather.StartsWith("Clo", StringComparison.OrdinalIgnoreCase) ||
                weather.StartsWith("Part", StringComparison.OrdinalIgnoreCase)) return 1;
            if (weather.StartsWith("Fog", StringComparison.OrdinalIgnoreCase)) return 2;
            if (weather.StartsWith("Rain", StringComparison.OrdinalIgnoreCase) ||
                weather.StartsWith("Snow", StringComparison.OrdinalIgnoreCase)) return 3;
            return 0;
        }

        /// <summary>
        /// Computes the effective tire friction multiplier from `WeatherFriction`
        /// and the vehicle's `CarFrictionHandling`.
        /// </summary>
        public static float GetEffectiveTireFrictionMultiplier(float carFrictionHandling)
        {
            float f = WeatherFriction;
            if (f >= 0.999f) return 1.0f;

            float handling = Mathf.Max(0.1f, carFrictionHandling);
            float effective;
            if (handling < 1.0f)
            {
                effective = f + (1.0f - f) * (1.0f - handling);
            }
            else
            {
                effective = (1.0f / handling) * f;
            }
            return Mathf.Clamp(effective, 0.55f, 1.0f);
        }

        /// <summary>
        /// Returns true when vehicle headlights should automatically turn on (Evening, Night, Foggy, or Rainy).
        /// </summary>
        public static bool ShouldHeadlightsBeOn(int timeOfDayIndex, int weatherIndex)
        {
            return timeOfDayIndex == 2 || timeOfDayIndex == 3 || weatherIndex == 2 || weatherIndex == 3;
        }

        public static void EnsureAndApply(string locationOrMapName, string timeOfDay, string weather, bool showBanner = false)
        {
            MM2EnvironmentManager mgr = EnsureExists();
            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(locationOrMapName);
            string mapName = spec != null && !string.IsNullOrEmpty(spec.MapName) ? spec.MapName : "sf";
            int todIdx = ParseTimeOfDayIndex(timeOfDay);
            int weathIdx = ParseWeatherIndex(weather);
            mgr.ApplyEnvironment(mapName, todIdx, weathIdx, showBanner);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            EnsureAudioSources();
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            // Handle Cruise Mode real-time '[' (Time of Day) and ']' (Weather) hotkeys
            CheckCruiseHotkeys();

            // Update rain particle follower and audio crossfade
            UpdateWeatherEffectsAndAudio(dt);

            if (toastTimer > 0f)
            {
                toastTimer = Mathf.Max(0f, toastTimer - Time.unscaledDeltaTime);
            }
        }

        private void CheckCruiseHotkeys()
        {
            // Only allow '[' and ']' cycling in Cruise mode while actively driving (menu closed)
            bool isCruise = string.Equals(MM2GameSession.RaceType, "Cruise", StringComparison.OrdinalIgnoreCase);
            if (!isCruise) return;
            if (MM2RaceManager.Instance != null && MM2RaceManager.Instance.IsRaceActive) return;
            if (FindFirstObjectByType<MM2MenuController>() != null) return;

            bool cycleTod = false;
            bool cycleWeather = false;

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.leftBracketKey.wasPressedThisFrame) cycleTod = true;
                if (kb.rightBracketKey.wasPressedThisFrame) cycleWeather = true;
            }
#else
            if (Input.GetKeyDown(KeyCode.LeftBracket)) cycleTod = true;
            if (Input.GetKeyDown(KeyCode.RightBracket)) cycleWeather = true;
#endif

            if (cycleTod)
            {
                int nextTod = (CurrentTimeOfDayIndex + 1) % TimeOfDayNames.Length;
                MM2GameSession.TimeOfDay = TimeOfDayNames[nextTod];
                PlayUiConditionSound(nextTod, -1);
                ApplyEnvironment(CurrentMapName, nextTod, CurrentWeatherIndex, showBanner: true);
            }
            else if (cycleWeather)
            {
                int nextWeath = (CurrentWeatherIndex + 1) % WeatherNames.Length;
                MM2GameSession.Weather = WeatherNames[nextWeath];
                PlayUiConditionSound(-1, nextWeath);
                ApplyEnvironment(CurrentMapName, CurrentTimeOfDayIndex, nextWeath, showBanner: true);
            }
        }

        public void ApplyEnvironment(string mapName, int timeOfDayIndex, int weatherIndex, bool showBanner = false)
        {
            MM2ArchiveSystem.EnsureInitialized();

            if (string.IsNullOrEmpty(mapName)) mapName = "sf";
            mapName = mapName.Trim().ToLowerInvariant();

            timeOfDayIndex = Mathf.Clamp(timeOfDayIndex, 0, 3);
            weatherIndex = Mathf.Clamp(weatherIndex, 0, 3);

            CurrentMapName = mapName;
            CurrentTimeOfDayIndex = timeOfDayIndex;
            CurrentWeatherIndex = weatherIndex;

            MM2GameSession.TimeOfDay = TimeOfDayNames[timeOfDayIndex];
            MM2GameSession.Weather = WeatherNames[weatherIndex];

            if (!string.Equals(loadedCityForSpecs, mapName, StringComparison.OrdinalIgnoreCase))
            {
                LoadCityEnvironmentSpecs(mapName);
                loadedCityForSpecs = mapName;
            }

            int combinedIdx = weatherIndex + timeOfDayIndex * 4;
            LightConditionSpec lt = lightSpecs[combinedIdx];
            FogConditionSpec fg = fogSpecs[combinedIdx];

            // 1. Update global WeatherFriction
            if (weatherIndex == 3) // Rainy
            {
                WeatherFriction = (timeOfDayIndex == 3) ? 0.75f : 0.80f;
            }
            else if (weatherIndex == 2) // Foggy
            {
                WeatherFriction = 0.92f;
            }
            else
            {
                WeatherFriction = 1.0f;
            }

            // 2. Apply Fog & Camera Clear Color
            ApplyFogSettings(fg, timeOfDayIndex, weatherIndex, out Color activeFogColor, out float activeFogStart, out float activeFogEnd);

            // 3. Apply 3-Point Directional Lighting & Ambient
            ApplyLightingSettings(lt, activeFogColor, timeOfDayIndex, weatherIndex);

            // 4. Apply Sky Dome Texture & Parameters
            ApplySkyDomeSettings(mapName, timeOfDayIndex, weatherIndex, activeFogColor, activeFogStart, activeFogEnd);

            // 5. Apply Horizon Water Plane Tint & Smoothness
            ApplyHorizonWaterSettings(lt, activeFogColor, timeOfDayIndex, weatherIndex);

            // 6. Swap City & Banger Textures (_ni for Night, _fa for Rainy) & Wet Road Specular
            ApplyDynamicMaterialVariants(timeOfDayIndex, weatherIndex);

            // 7. Configure Rain Particle System & Weather Audio
            ConfigureWeatherParticlesAndAudio(timeOfDayIndex, weatherIndex);

            // 8. Refresh Playable & AI Vehicle Headlights and Wet-Road Wheel Grip
            RefreshAllVehiclesInScene(timeOfDayIndex, weatherIndex);

            if (showBanner)
            {
                toastMessage = $"TIME: {TimeOfDayNames[timeOfDayIndex].ToUpperInvariant()}   |   WEATHER: {WeatherNames[weatherIndex].ToUpperInvariant()}";
                toastTimer = 3.2f;
            }

            Debug.Log(
                $"[MM2EnvironmentManager] Applied [{combinedIdx:D2}] {TimeOfDayNames[timeOfDayIndex]} / {WeatherNames[weatherIndex]} " +
                $"for city '{mapName}' (Fog={activeFogStart:F0}..{activeFogEnd:F0}m, WeatherFriction={WeatherFriction:F2})"
            );
        }

        // =====================================================================
        // ARCHIVE PARSERS (.lt00..15, _fog.csv, .sky, rain.asBirthRule)
        // =====================================================================
        private void LoadCityEnvironmentSpecs(string mapName)
        {
            string fallbackMap = (mapName == "london") ? "london" : "sf";

            // 1. Parse .sky
            skyPkgName = (mapName == "london") ? "sky_dome_l" : "sky_dome";
            skyYOffset = 0f;
            skyScale = 0.95f;
            skyRotSpeedRad = 0.005f;

            string skyText = MM2ArchiveSystem.ReadFileText($"city/{mapName}.sky");
            if (string.IsNullOrEmpty(skyText))
            {
                skyText = MM2ArchiveSystem.ReadFileText($"city/{fallbackMap}.sky");
            }
            if (!string.IsNullOrEmpty(skyText))
            {
                string[] lines = skyText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length >= 1) skyPkgName = tok[0].Trim();
                    if (tok.Length >= 2) float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out skyYOffset);
                    if (tok.Length >= 3) float.TryParse(tok[2], NumberStyles.Float, CultureInfo.InvariantCulture, out skyScale);
                    if (tok.Length >= 4) float.TryParse(tok[3], NumberStyles.Float, CultureInfo.InvariantCulture, out skyRotSpeedRad);
                    break;
                }
            }

            // 2. Parse _fog.csv (16 rows after header)
            InitializeDefaultFogSpecs();
            string fogCsv = MM2ArchiveSystem.ReadFileText($"city/{mapName}_fog.csv");
            if (string.IsNullOrEmpty(fogCsv))
            {
                fogCsv = MM2ArchiveSystem.ReadFileText($"city/{fallbackMap}_fog.csv");
            }
            if (!string.IsNullOrEmpty(fogCsv))
            {
                string[] lines = fogCsv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int rowIdx = 0;
                for (int i = 0; i < lines.Length && rowIdx < 16; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("fog", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string[] cols = line.Split(',');
                    if (cols.Length >= 5 &&
                        float.TryParse(cols[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                        float.TryParse(cols[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                        float.TryParse(cols[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b) &&
                        float.TryParse(cols[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float fStart) &&
                        float.TryParse(cols[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float fEnd))
                    {
                        fogSpecs[rowIdx] = new FogConditionSpec
                        {
                            FogColor = new Color(Mathf.Clamp01(r / 255f), Mathf.Clamp01(g / 255f), Mathf.Clamp01(b / 255f), 1f),
                            FogStart = Mathf.Max(0f, fStart),
                            FogEnd = Mathf.Max(fStart + 10f, fEnd),
                            Description = cols.Length >= 6 ? cols[5].Trim() : $"{rowIdx}"
                        };
                        rowIdx++;
                    }
                }
            }

            // 3. Parse .lt00 .. .lt15
            for (int idx = 0; idx < 16; idx++)
            {
                string ltText = MM2ArchiveSystem.ReadFileText($"city/{mapName}.lt{idx:D2}");
                if (string.IsNullOrEmpty(ltText))
                {
                    ltText = MM2ArchiveSystem.ReadFileText($"city/{fallbackMap}.lt{idx:D2}");
                }
                lightSpecs[idx] = ParseLtFile(ltText, idx);
            }

            // 4. Parse tune/rain.asBirthRule
            rainSpec = new RainBirthRuleSpec
            {
                PositionVar = new Vector3(25f, 0f, 25f),
                Velocity = new Vector3(2f, -35f, 0f),
                SpewRate = 200f,
                Radius = 0.5f,
                Life = 1.0f
            };
            string rainText = MM2ArchiveSystem.ReadFileText("tune/rain.asBirthRule");
            if (!string.IsNullOrEmpty(rainText))
            {
                string[] lines = rainText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in lines)
                {
                    string[] tok = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length < 2) continue;
                    if (string.Equals(tok[0], "PositionVar", StringComparison.OrdinalIgnoreCase) && tok.Length >= 4)
                    {
                        rainSpec.PositionVar = new Vector3(
                            ParseFloat(tok[1], 25f),
                            ParseFloat(tok[2], 0f),
                            ParseFloat(tok[3], 25f));
                    }
                    else if (string.Equals(tok[0], "Velocity", StringComparison.OrdinalIgnoreCase) && tok.Length >= 4)
                    {
                        rainSpec.Velocity = new Vector3(
                            ParseFloat(tok[1], 2f),
                            ParseFloat(tok[2], -35f),
                            ParseFloat(tok[3], 0f));
                    }
                    else if (string.Equals(tok[0], "SpewRate", StringComparison.OrdinalIgnoreCase))
                    {
                        rainSpec.SpewRate = ParseFloat(tok[1], 200f);
                    }
                    else if (string.Equals(tok[0], "Radius", StringComparison.OrdinalIgnoreCase))
                    {
                        rainSpec.Radius = ParseFloat(tok[1], 0.5f);
                    }
                    else if (string.Equals(tok[0], "Life", StringComparison.OrdinalIgnoreCase))
                    {
                        rainSpec.Life = ParseFloat(tok[1], 1.0f);
                    }
                }
            }
        }

        private void InitializeDefaultFogSpecs()
        {
            for (int i = 0; i < 16; i++)
            {
                int tod = i / 4;
                int weath = i % 4;
                Color c = tod == 3
                    ? new Color(0.06f, 0.07f, 0.12f, 1f)
                    : (tod == 2 ? new Color(0.85f, 0.65f, 0.50f, 1f) : new Color(0.62f, 0.68f, 0.78f, 1f));
                float start = (weath == 2) ? 5f : (weath == 3 ? 40f : 600f);
                float end = (weath == 2) ? 100f : (weath == 3 ? 220f : 1000f);
                fogSpecs[i] = new FogConditionSpec
                {
                    FogColor = c,
                    FogStart = start,
                    FogEnd = end,
                    Description = $"{WeatherNames[weath]}-{TimeOfDayNames[tod]}"
                };
            }
        }

        private static LightConditionSpec ParseLtFile(string text, int idx)
        {
            var spec = new LightConditionSpec
            {
                KeyHeading = 2.2f,
                KeyPitch = -0.8f,
                KeyColor = Color.white,
                Fill1Heading = 1.5f,
                Fill1Pitch = -0.5f,
                Fill1Color = new Color(0.5f, 0.5f, 0.6f, 1f),
                Fill2Heading = 4.5f,
                Fill2Pitch = -0.3f,
                Fill2Color = new Color(0.6f, 0.6f, 0.7f, 1f),
                AmbientColor = new Color(0.2f, 0.2f, 0.25f, 1f)
            };

            if (string.IsNullOrEmpty(text)) return spec;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 2) continue;

                string key = tok[0];
                if (string.Equals(key, "KeyHeading", StringComparison.OrdinalIgnoreCase))
                {
                    spec.KeyHeading = ParseFloat(tok[1], spec.KeyHeading);
                }
                else if (string.Equals(key, "KeyPitch", StringComparison.OrdinalIgnoreCase))
                {
                    spec.KeyPitch = ParseFloat(tok[1], spec.KeyPitch);
                }
                else if (string.Equals(key, "KeyColor", StringComparison.OrdinalIgnoreCase) && tok.Length >= 4)
                {
                    spec.KeyColor = new Color(
                        Mathf.Clamp01(ParseFloat(tok[1], 1f)),
                        Mathf.Clamp01(ParseFloat(tok[2], 1f)),
                        Mathf.Clamp01(ParseFloat(tok[3], 1f)),
                        1f);
                }
                else if (string.Equals(key, "Fill1Heading", StringComparison.OrdinalIgnoreCase))
                {
                    spec.Fill1Heading = ParseFloat(tok[1], spec.Fill1Heading);
                }
                else if (string.Equals(key, "Fill1Pitch", StringComparison.OrdinalIgnoreCase))
                {
                    spec.Fill1Pitch = ParseFloat(tok[1], spec.Fill1Pitch);
                }
                else if (string.Equals(key, "Fill1Color", StringComparison.OrdinalIgnoreCase) && tok.Length >= 4)
                {
                    spec.Fill1Color = new Color(
                        Mathf.Clamp01(ParseFloat(tok[1], 0.5f)),
                        Mathf.Clamp01(ParseFloat(tok[2], 0.5f)),
                        Mathf.Clamp01(ParseFloat(tok[3], 0.5f)),
                        1f);
                }
                else if (string.Equals(key, "Fill2Heading", StringComparison.OrdinalIgnoreCase))
                {
                    spec.Fill2Heading = ParseFloat(tok[1], spec.Fill2Heading);
                }
                else if (string.Equals(key, "Fill2Pitch", StringComparison.OrdinalIgnoreCase))
                {
                    spec.Fill2Pitch = ParseFloat(tok[1], spec.Fill2Pitch);
                }
                else if (string.Equals(key, "Fill2Color", StringComparison.OrdinalIgnoreCase) && tok.Length >= 4)
                {
                    spec.Fill2Color = new Color(
                        Mathf.Clamp01(ParseFloat(tok[1], 0.5f)),
                        Mathf.Clamp01(ParseFloat(tok[2], 0.5f)),
                        Mathf.Clamp01(ParseFloat(tok[3], 0.5f)),
                        1f);
                }
                else if (string.Equals(key, "Ambient", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(tok[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int packedAmb))
                    {
                        uint u = unchecked((uint)packedAmb);
                        float ar = ((u >> 16) & 0xFF) / 255f;
                        float ag = ((u >> 8) & 0xFF) / 255f;
                        float ab = (u & 0xFF) / 255f;
                        spec.AmbientColor = new Color(ar, ag, ab, 1f);
                    }
                }
            }

            return spec;
        }

        // =====================================================================
        // FOG, LIGHTING, SKY DOME & HORIZON WATER APPLICATION
        // =====================================================================
        private void ApplyFogSettings(
            FogConditionSpec fg,
            int todIdx,
            int weathIdx,
            out Color activeFogColor,
            out float activeFogStart,
            out float activeFogEnd
        )
        {
            activeFogColor = fg.FogColor;

            // In Night conditions where the CSV fog color is near pure black (0,0,0),
            // give a subtle deep midnight blue-slate tint so distant silhouettes and fog horizon read cleanly.
            if (todIdx == 3)
            {
                activeFogColor = new Color(
                    Mathf.Max(activeFogColor.r, 0.035f),
                    Mathf.Max(activeFogColor.g, 0.045f),
                    Mathf.Max(activeFogColor.b, 0.085f),
                    1f
                );
            }

            if (weathIdx == 2) // Foggy: thick, authentic near-camera fog
            {
                activeFogStart = Mathf.Clamp(fg.FogStart, 2f, 20f);
                activeFogEnd = Mathf.Clamp(Mathf.Max(fg.FogEnd * 1.35f, 95f), 90f, 180f);
            }
            else if (weathIdx == 3) // Rainy: misty rain visibility
            {
                activeFogStart = Mathf.Clamp(fg.FogStart, 15f, 80f);
                activeFogEnd = Mathf.Clamp(Mathf.Max(fg.FogEnd * 1.35f, 180f), 170f, 450f);
            }
            else if (weathIdx == 1) // Cloudy: overcast horizon haze
            {
                activeFogStart = Mathf.Max(150f, fg.FogStart * 1.15f);
                activeFogEnd = Mathf.Max(550f, fg.FogEnd * 1.45f);
            }
            else // Clear: long-distance atmospheric haze
            {
                activeFogStart = Mathf.Max(300f, fg.FogStart * 1.35f);
                activeFogEnd = Mathf.Max(850f, fg.FogEnd * 1.65f);
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = activeFogColor;
            RenderSettings.fogStartDistance = activeFogStart;
            RenderSettings.fogEndDistance = activeFogEnd;

            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = activeFogColor;
                if (cam.farClipPlane < 12000f)
                {
                    cam.farClipPlane = 12000f;
                }
            }
        }

        private void ApplyLightingSettings(
            LightConditionSpec lt,
            Color activeFogColor,
            int todIdx,
            int weathIdx
        )
        {
            EnsureSceneLights();

            // Street-grid-friendly diagonal sun/moon angles so tall downtown buildings cast crisp diagonal shadows
            // partway across streets (rather than blocking 100% of street-level light) and vehicles cast clear shadows.
            Quaternion keyRot;
            if (todIdx == 0) // Morning: warm sun from East-Southeast
            {
                keyRot = Quaternion.Euler(46f, -68f, 0f);
            }
            else if (todIdx == 1) // Noon: high overhead midday sun
            {
                keyRot = Quaternion.Euler(58f, -32f, 0f);
            }
            else if (todIdx == 2) // Evening: golden sunset sun from West-Southwest
            {
                keyRot = Quaternion.Euler(42f, 58f, 0f);
            }
            else // Night: diagonal moonlight casting crisp building shadows down hills
            {
                keyRot = Quaternion.Euler(52f, -72f, 0f);
            }

            if (keyLight != null)
            {
                keyLight.transform.rotation = keyRot;
                RenderSettings.sun = keyLight;
            }
            if (fillLight1 != null)
            {
                fillLight1.transform.rotation = HeadingPitchToRotation(lt.Fill1Heading, lt.Fill1Pitch, 0.35f);
            }
            if (fillLight2 != null)
            {
                fillLight2.transform.rotation = HeadingPitchToRotation(lt.Fill2Heading, lt.Fill2Pitch, 0.28f);
            }

            // Compute Time-of-Day & Weather balanced colors and intensities for Unity URP Lit materials
            Color keyCol = lt.KeyColor;
            Color fill1Col = lt.Fill1Color;
            Color fill2Col = lt.Fill2Color;
            Color ambCol = lt.AmbientColor;

            float keyIntensity;
            float fill1Intensity;
            float fill2Intensity;
            float shadowStrength;

            if (todIdx == 0) // Morning: warm golden-rose morning light
            {
                Color morningTint = new Color(1.0f, 0.91f, 0.78f, 1f);
                keyCol = Color.Lerp(keyCol, morningTint, 0.45f);
                keyIntensity = weathIdx == 0 ? 1.30f : (weathIdx == 1 ? 1.00f : (weathIdx == 2 ? 0.72f : 0.62f));
                fill1Intensity = 0.40f;
                fill2Intensity = 0.30f;
                shadowStrength = weathIdx == 0 ? 0.68f : (weathIdx == 1 ? 0.48f : 0.25f);

                Color minAmb = new Color(0.48f, 0.45f, 0.50f, 1f);
                ambCol = Color.Lerp(MaxColor(ambCol, minAmb), activeFogColor, 0.30f);
            }
            else if (todIdx == 1) // Noon: bright sunny overhead daylight with crisp shadows
            {
                Color noonSun = new Color(1.0f, 0.98f, 0.92f, 1f);
                keyCol = weathIdx == 0 ? noonSun : Color.Lerp(keyCol, noonSun, 0.50f);
                keyIntensity = weathIdx == 0 ? 1.50f : (weathIdx == 1 ? 1.15f : (weathIdx == 2 ? 0.82f : 0.70f));
                fill1Intensity = 0.45f;
                fill2Intensity = 0.35f;
                shadowStrength = weathIdx == 0 ? 0.72f : (weathIdx == 1 ? 0.50f : 0.28f);

                Color minAmb = new Color(0.56f, 0.58f, 0.64f, 1f);
                ambCol = Color.Lerp(MaxColor(ambCol, minAmb), activeFogColor, 0.25f);
            }
            else if (todIdx == 2) // Evening: warm sunset amber/coral glow and dusk shadows
            {
                Color sunsetTint = new Color(1.0f, 0.68f, 0.42f, 1f);
                keyCol = Color.Lerp(keyCol, sunsetTint, 0.55f);
                fill1Col = Color.Lerp(fill1Col, new Color(0.58f, 0.45f, 0.70f, 1f), 0.40f);
                keyIntensity = weathIdx == 0 ? 1.18f : (weathIdx == 1 ? 0.90f : (weathIdx == 2 ? 0.60f : 0.52f));
                fill1Intensity = 0.35f;
                fill2Intensity = 0.28f;
                shadowStrength = weathIdx == 0 ? 0.70f : (weathIdx == 1 ? 0.48f : 0.25f);

                Color duskAmb = new Color(0.40f, 0.32f, 0.38f, 1f);
                ambCol = Color.Lerp(MaxColor(ambCol, duskAmb), activeFogColor, 0.28f);
            }
            else // Night (todIdx == 3): crisp cool moonlight with visible building/car shadows and glowing _ni windows
            {
                Color moonTint = new Color(0.58f, 0.66f, 0.88f, 1f);
                keyCol = Color.Lerp(lt.KeyColor * 0.4f + moonTint * 0.6f, moonTint, 0.60f);
                fill1Col = new Color(0.32f, 0.38f, 0.56f, 1f);
                fill2Col = new Color(0.35f, 0.32f, 0.46f, 1f);

                keyIntensity = weathIdx == 0 ? 0.52f : (weathIdx == 1 ? 0.40f : (weathIdx == 2 ? 0.28f : 0.25f));
                fill1Intensity = 0.18f;
                fill2Intensity = 0.14f;
                shadowStrength = weathIdx == 0 ? 0.58f : (weathIdx == 1 ? 0.42f : 0.25f);

                ambCol = new Color(0.18f, 0.20f, 0.28f, 1f);
            }

            if (keyLight != null)
            {
                keyLight.color = keyCol;
                keyLight.intensity = keyIntensity;
                keyLight.shadows = LightShadows.Soft;
                keyLight.shadowStrength = shadowStrength;
            }
            if (fillLight1 != null)
            {
                fillLight1.color = fill1Col;
                fillLight1.intensity = fill1Intensity;
                fillLight1.shadows = LightShadows.None;
            }
            if (fillLight2 != null)
            {
                fillLight2.color = fill2Col;
                fillLight2.intensity = fill2Intensity;
                fillLight2.shadows = LightShadows.None;
            }

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambCol;
            RenderSettings.ambientIntensity = 1.0f;
        }

        private void EnsureSceneLights()
        {
            const float TargetShadowDistance = 380f;
            QualitySettings.shadowDistance = TargetShadowDistance;
            if (QualitySettings.renderPipeline is UniversalRenderPipelineAsset qUrp)
            {
                qUrp.shadowDistance = TargetShadowDistance;
                qUrp.shadowCascadeCount = 4;
            }
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset gUrp)
            {
                gUrp.shadowDistance = TargetShadowDistance;
                gUrp.shadowCascadeCount = 4;
            }

            if (keyLight == null)
            {
                Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
                foreach (Light l in lights)
                {
                    if (l != null && l.type == LightType.Directional &&
                        !l.name.StartsWith("MM2_FillLight", StringComparison.Ordinal))
                    {
                        keyLight = l;
                        break;
                    }
                }

                if (keyLight == null)
                {
                    GameObject keyGo = new GameObject("Directional Light");
                    keyLight = keyGo.AddComponent<Light>();
                    keyLight.type = LightType.Directional;
                }
            }

            if (fillLight1 == null)
            {
                Transform t = transform.Find("MM2_FillLight1");
                GameObject go = t != null ? t.gameObject : new GameObject("MM2_FillLight1");
                go.transform.SetParent(transform, false);
                fillLight1 = go.GetComponent<Light>();
                if (fillLight1 == null)
                {
                    fillLight1 = go.AddComponent<Light>();
                }
                fillLight1.type = LightType.Directional;
                fillLight1.shadows = LightShadows.None;
            }

            if (fillLight2 == null)
            {
                Transform t = transform.Find("MM2_FillLight2");
                GameObject go = t != null ? t.gameObject : new GameObject("MM2_FillLight2");
                go.transform.SetParent(transform, false);
                fillLight2 = go.GetComponent<Light>();
                if (fillLight2 == null)
                {
                    fillLight2 = go.AddComponent<Light>();
                }
                fillLight2.type = LightType.Directional;
                fillLight2.shadows = LightShadows.None;
            }
        }

        private static Color MaxColor(Color a, Color b)
        {
            return new Color(Mathf.Max(a.r, b.r), Mathf.Max(a.g, b.g), Mathf.Max(a.b, b.b), 1f);
        }

        private static Quaternion HeadingPitchToRotation(float headingRad, float pitchRad, float minDownwardSin)
        {
            // Convert spherical heading/pitch to Unity directional light rotation,
            // clamped downward so light always shines from above.
            float cosP = Mathf.Max(0.25f, Mathf.Abs(Mathf.Cos(pitchRad)));
            float downY = -Mathf.Max(minDownwardSin, Mathf.Abs(Mathf.Sin(pitchRad)));
            Vector3 lightDir = new Vector3(
                -Mathf.Cos(headingRad) * cosP,
                downY,
                Mathf.Sin(headingRad) * cosP
            ).normalized;

            return lightDir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(lightDir, Vector3.up) : Quaternion.Euler(50f, -30f, 0f);
        }

        private void ApplySkyDomeSettings(
            string mapName,
            int todIdx,
            int weathIdx,
            Color activeFogColor,
            float activeFogStart,
            float activeFogEnd
        )
        {
            GameObject skyGo = GameObject.Find("Runtime_SkyDome");
            if (skyGo == null) return;

            var follower = skyGo.GetComponent<MM2SkyDomeFollower>();
            if (follower != null)
            {
                follower.ConfigureSkyParameters(skyYOffset, skyRotSpeedRad, activeFogStart, activeFogEnd);
            }

            var mr = skyGo.GetComponent<MeshRenderer>();
            if (mr == null || mr.sharedMaterial == null) return;

            char wChar = WeatherCodeChars[Mathf.Clamp(weathIdx, 0, 3)];
            char tChar = TimeCodeChars[Mathf.Clamp(todIdx, 0, 3)];

            // Candidate sky texture names from sky_dome.pkg (SF) and sky_dome_l.pkg (London)
            string[] candidates = string.Equals(mapName, "london", StringComparison.OrdinalIgnoreCase)
                ? new[]
                {
                    $"skylondon_{wChar}{tChar}_l",
                    $"sky_{wChar}{tChar}_l",
                    $"sky_{wChar}{tChar}_f"
                }
                : new[]
                {
                    $"sky_{wChar}{tChar}_f",
                    $"skylondon_{wChar}{tChar}_l",
                    $"sky_{wChar}{tChar}_l"
                };

            Texture2D skyTex = null;
            foreach (string cand in candidates)
            {
                var info = MM2RuntimeDecoders.LoadTexture(cand);
                if (info.Texture != null)
                {
                    skyTex = info.Texture;
                    break;
                }
            }

            Material mat = mr.sharedMaterial;
            if (skyTex != null)
            {
                mat.mainTexture = skyTex;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", skyTex);
            }

            // Subtle sky tint so Night/Rain/Fog skies blend naturally with the horizon
            Color skyTint = Color.white;
            if (todIdx == 3)
            {
                skyTint = new Color(0.75f, 0.78f, 0.90f, 1f);
            }
            else if (weathIdx == 3)
            {
                skyTint = new Color(0.82f, 0.84f, 0.88f, 1f);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", skyTint);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", skyTint);
        }

        private static void ApplyHorizonWaterSettings(
            LightConditionSpec lt,
            Color activeFogColor,
            int todIdx,
            int weathIdx
        )
        {
            GameObject waterGo = GameObject.Find("Runtime_HorizonWater");
            if (waterGo == null) return;

            var mr = waterGo.GetComponent<MeshRenderer>();
            if (mr == null || mr.sharedMaterial == null) return;

            Material wMat = mr.sharedMaterial;
            Color waterTint;
            float waterSmoothness;

            if (todIdx == 3) // Night
            {
                waterTint = new Color(0.18f, 0.22f, 0.34f, 1f);
                waterSmoothness = 0.45f;
            }
            else if (todIdx == 2) // Evening
            {
                waterTint = new Color(0.85f, 0.68f, 0.58f, 1f);
                waterSmoothness = 0.40f;
            }
            else if (todIdx == 0) // Morning
            {
                waterTint = new Color(0.88f, 0.86f, 0.84f, 1f);
                waterSmoothness = 0.30f;
            }
            else // Noon
            {
                waterTint = weathIdx >= 2 ? new Color(0.72f, 0.76f, 0.80f, 1f) : Color.white;
                waterSmoothness = weathIdx == 3 ? 0.50f : 0.25f;
            }

            if (wMat.HasProperty("_BaseColor")) wMat.SetColor("_BaseColor", waterTint);
            if (wMat.HasProperty("_Color")) wMat.SetColor("_Color", waterTint);
            if (wMat.HasProperty("_Smoothness")) wMat.SetFloat("_Smoothness", waterSmoothness);
        }

        // =====================================================================
        // REAL-TIME TEXTURE SWAPPING (_ni FOR NIGHT, _fa FOR RAINY)
        // =====================================================================
        private static bool VariantTextureExists(string variantTexName)
        {
            if (string.IsNullOrEmpty(variantTexName)) return false;
            if (variantExistsCache.TryGetValue(variantTexName, out bool exists))
            {
                return exists;
            }
            string vPath = MM2ArchiveSystem.FindTextureVirtualPath(variantTexName);
            exists = !string.IsNullOrEmpty(vPath);
            variantExistsCache[variantTexName] = exists;
            return exists;
        }

        private static void EnsureSceneMaterialsRegistered()
        {
            // Clean up any destroyed material references first
            for (int i = registeredMaterials.Count - 1; i >= 0; i--)
            {
                if (registeredMaterials[i] == null || registeredMaterials[i].Material == null)
                {
                    registeredMaterials.RemoveAt(i);
                }
            }

            if (registeredMaterials.Count >= 10) return;

            // Recover city and banger materials from active scene Renderers (e.g. after Unity Domain Reload)
            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                if (mats == null) continue;

                for (int mIdx = 0; mIdx < mats.Length; mIdx++)
                {
                    Material mat = mats[mIdx];
                    if (mat == null) continue;

                    string matName = mat.name ?? string.Empty;
                    if (matName.StartsWith("Mat_City_", StringComparison.OrdinalIgnoreCase))
                    {
                        RegisterDynamicMaterial(mat, matName.Substring("Mat_City_".Length));
                    }
                    else if (matName.StartsWith("Mat_Banger_", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(matName, "Mat_Banger_Color", StringComparison.OrdinalIgnoreCase))
                    {
                        RegisterDynamicMaterial(mat, matName.Substring("Mat_Banger_".Length));
                    }
                    else if (mat.mainTexture != null && !string.IsNullOrEmpty(mat.mainTexture.name) &&
                             !matName.StartsWith("Mat_Runtime_", StringComparison.OrdinalIgnoreCase))
                    {
                        RegisterDynamicMaterial(mat, mat.mainTexture.name);
                    }
                }
            }
        }

        private static void ApplyDynamicMaterialVariants(int todIdx, int weathIdx)
        {
            EnsureSceneMaterialsRegistered();

            bool isNight = (todIdx == 3);
            bool isEvening = (todIdx == 2);
            bool isRainy = (weathIdx == 3);
            bool isFoggy = (weathIdx == 2);

            for (int i = registeredMaterials.Count - 1; i >= 0; i--)
            {
                RegisteredMaterialEntry entry = registeredMaterials[i];
                if (entry == null || entry.Material == null)
                {
                    registeredMaterials.RemoveAt(i);
                    continue;
                }

                Material mat = entry.Material;
                string baseTex = entry.BaseTextureName;
                string targetTex = baseTex;
                bool usingNightTex = false;

                // 1. Check Weather == 3 (Rainy) -> try <base>_fa (wet road/sidewalk textures)
                // 2. Check TimeOfDay > 2 (Night) -> try <base>_ni (lit building windows/signs/streetlamps)
                if (isRainy && VariantTextureExists($"{baseTex}_fa"))
                {
                    targetTex = $"{baseTex}_fa";
                }
                else if (isNight && VariantTextureExists($"{baseTex}_ni"))
                {
                    targetTex = $"{baseTex}_ni";
                    usingNightTex = true;
                }
                else if (isEvening && entry.IsGlowOrLamp && VariantTextureExists($"{baseTex}_ni"))
                {
                    targetTex = $"{baseTex}_ni";
                    usingNightTex = true;
                }

                var texInfo = MM2RuntimeDecoders.LoadTexture(targetTex);
                if (texInfo.Texture != null)
                {
                    mat.mainTexture = texInfo.Texture;
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texInfo.Texture);
                }

                // Wet road specular reflection in Rainy / Foggy weather
                if (mat.HasProperty("_Smoothness"))
                {
                    if (entry.IsRoadOrSidewalk)
                    {
                        float wetSmooth = isRainy ? 0.56f : (isFoggy ? 0.22f : entry.BaseSmoothness);
                        mat.SetFloat("_Smoothness", wetSmooth);
                    }
                }

                // Nighttime emission on _ni building windows, signs, and streetlamp bulbs
                if (entry.IsGlowOrLamp)
                {
                    mat.EnableKeyword("_EMISSION");
                    if (texInfo.Texture != null && mat.HasProperty("_EmissionMap"))
                    {
                        mat.SetTexture("_EmissionMap", texInfo.Texture);
                    }
                    float glowIntensity = (isNight || isEvening) ? 1.55f : 0.85f;
                    if (mat.HasProperty("_EmissionColor"))
                    {
                        mat.SetColor("_EmissionColor", new Color(glowIntensity, glowIntensity * 0.96f, glowIntensity * 0.88f, 1f));
                    }
                }
                else if (usingNightTex)
                {
                    mat.EnableKeyword("_EMISSION");
                    if (texInfo.Texture != null && mat.HasProperty("_EmissionMap"))
                    {
                        mat.SetTexture("_EmissionMap", texInfo.Texture);
                    }
                    if (mat.HasProperty("_EmissionColor"))
                    {
                        mat.SetColor("_EmissionColor", new Color(0.82f, 0.76f, 0.64f, 1f));
                    }
                }
                else
                {
                    if (mat.HasProperty("_EmissionColor"))
                    {
                        mat.SetColor("_EmissionColor", Color.black);
                    }
                    mat.DisableKeyword("_EMISSION");
                }
            }
        }

        // =====================================================================
        // RAIN PARTICLE SYSTEM & WEATHER AUDIO
        // =====================================================================
        private void EnsureAudioSources()
        {
            if (rainExtSource == null)
            {
                rainExtSource = gameObject.AddComponent<AudioSource>();
                rainExtSource.loop = true;
                rainExtSource.playOnAwake = false;
                rainExtSource.spatialBlend = 0f;
            }
            if (rainIntSource == null)
            {
                rainIntSource = gameObject.AddComponent<AudioSource>();
                rainIntSource.loop = true;
                rainIntSource.playOnAwake = false;
                rainIntSource.spatialBlend = 0f;
            }
            if (windSource == null)
            {
                windSource = gameObject.AddComponent<AudioSource>();
                windSource.loop = true;
                windSource.playOnAwake = false;
                windSource.spatialBlend = 0f;
            }
            if (sfxOneShotSource == null)
            {
                sfxOneShotSource = gameObject.AddComponent<AudioSource>();
                sfxOneShotSource.loop = false;
                sfxOneShotSource.playOnAwake = false;
                sfxOneShotSource.spatialBlend = 0f;
            }
        }

        private void ConfigureWeatherParticlesAndAudio(int todIdx, int weathIdx)
        {
            EnsureAudioSources();

            if (clipRainExterior == null) clipRainExterior = MM2RuntimeDecoders.LoadAudioClip("rainexterior.22k");
            if (clipRainInterior == null) clipRainInterior = MM2RuntimeDecoders.LoadAudioClip("raininterior.22k");
            if (clipThunder == null) clipThunder = MM2RuntimeDecoders.LoadAudioClip("thunder.22k");
            if (clipFoghorn == null) clipFoghorn = MM2RuntimeDecoders.LoadAudioClip("foghorn.22k");
            if (clipWind == null) clipWind = MM2RuntimeDecoders.LoadAudioClip("wind.22k");

            bool isRainy = (weathIdx == 3);
            bool isFoggy = (weathIdx == 2);

            // Rain Particle System
            EnsureRainParticleSystem();
            if (rainParticles != null)
            {
                if (isRainy)
                {
                    if (!rainParticles.isPlaying) rainParticles.Play();
                }
                else
                {
                    if (rainParticles.isPlaying)
                    {
                        rainParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    }
                }
            }

            // Rain Loops (Rainexterior = 0.82, Raininterior = 0.85)
            if (isRainy)
            {
                if (clipRainExterior != null && rainExtSource.clip != clipRainExterior)
                {
                    rainExtSource.clip = clipRainExterior;
                }
                if (clipRainInterior != null && rainIntSource.clip != clipRainInterior)
                {
                    rainIntSource.clip = clipRainInterior;
                }
                if (rainExtSource.clip != null && !rainExtSource.isPlaying) rainExtSource.Play();
                if (rainIntSource.clip != null && !rainIntSource.isPlaying) rainIntSource.Play();
                nextAmbientEventTime = Time.time + UnityEngine.Random.Range(10f, 22f);
            }
            else
            {
                if (rainExtSource != null && rainExtSource.isPlaying) rainExtSource.Stop();
                if (rainIntSource != null && rainIntSource.isPlaying) rainIntSource.Stop();
            }

            // Foggy Wind & Foghorn
            if (isFoggy)
            {
                if (clipWind != null)
                {
                    windSource.clip = clipWind;
                    windSource.volume = 0.28f;
                    if (!windSource.isPlaying) windSource.Play();
                }
                nextAmbientEventTime = Time.time + UnityEngine.Random.Range(6f, 16f);
            }
            else
            {
                if (windSource != null && windSource.isPlaying) windSource.Stop();
            }
        }

        private void EnsureRainParticleSystem()
        {
            if (rainParticles != null) return;

            rainRootGo = new GameObject("MM2_RainParticleSystem");
            rainRootGo.transform.SetParent(transform, false);

            rainParticles = rainRootGo.AddComponent<ParticleSystem>();
            var main = rainParticles.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 2500;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.85f, 1.25f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.11f);
            main.startColor = new Color(0.82f, 0.86f, 0.94f, 0.52f);
            main.gravityModifier = 0f;

            var emission = rainParticles.emission;
            emission.enabled = true;
            // Scale SpewRate from rain.asBirthRule (200) to full 3D camera frustum coverage
            emission.rateOverTime = Mathf.Max(900f, rainSpec.SpewRate * 4.5f);

            var shape = rainParticles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(
                Mathf.Max(45f, rainSpec.PositionVar.x * 2f),
                2f,
                Mathf.Max(45f, rainSpec.PositionVar.z * 2f)
            );

            var velOverLife = rainParticles.velocityOverLifetime;
            velOverLife.enabled = true;
            velOverLife.space = ParticleSystemSimulationSpace.World;
            velOverLife.x = new ParticleSystem.MinMaxCurve(rainSpec.Velocity.x - 0.8f, rainSpec.Velocity.x + 0.8f);
            velOverLife.y = new ParticleSystem.MinMaxCurve(rainSpec.Velocity.y - 4f, rainSpec.Velocity.y + 4f);
            velOverLife.z = new ParticleSystem.MinMaxCurve(rainSpec.Velocity.z - 0.8f, rainSpec.Velocity.z + 0.8f);

            rainRenderer = rainRootGo.GetComponent<ParticleSystemRenderer>();
            rainRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            rainRenderer.velocityScale = 0.065f;
            rainRenderer.lengthScale = 3.2f;
            rainRenderer.shadowCastingMode = ShadowCastingMode.Off;
            rainRenderer.receiveShadows = false;

            Shader pShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (pShader == null) pShader = Shader.Find("Particles/Standard Unlit");
            if (pShader == null) pShader = Shader.Find("Sprites/Default");

            rainMaterial = new Material(pShader) { name = "Mat_MM2_RainStreak" };
            Texture2D streakTex = CreateRainStreakTexture();
            rainMaterial.mainTexture = streakTex;
            if (rainMaterial.HasProperty("_BaseMap")) rainMaterial.SetTexture("_BaseMap", streakTex);
            if (rainMaterial.HasProperty("_BaseColor")) rainMaterial.SetColor("_BaseColor", new Color(0.85f, 0.90f, 0.98f, 0.55f));
            if (rainMaterial.HasProperty("_Surface")) rainMaterial.SetFloat("_Surface", 1f);
            rainMaterial.renderQueue = (int)RenderQueue.Transparent;
            rainRenderer.sharedMaterial = rainMaterial;
        }

        private static Texture2D CreateRainStreakTexture()
        {
            const int W = 16;
            const int H = 64;
            Texture2D tex = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                name = "Tex_RainStreak",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color32[] pixels = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float v = (y + 0.5f) / H;
                float vertFade = Mathf.Sin(v * Mathf.PI);
                for (int x = 0; x < W; x++)
                {
                    float u = Mathf.Abs((x + 0.5f) / W - 0.5f) * 2f;
                    float horizFade = Mathf.Clamp01(1f - u * u);
                    byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(vertFade * horizFade * 210f), 0, 255);
                    pixels[y * W + x] = new Color32(225, 238, 255, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        private void UpdateWeatherEffectsAndAudio(float dt)
        {
            bool inMenu = FindFirstObjectByType<MM2MenuController>() != null;
            Camera cam = Camera.main;

            if (CurrentWeatherIndex == 3) // Rainy
            {
                if (cam != null && rainRootGo != null)
                {
                    Vector3 fwd = cam.transform.forward;
                    fwd.y = 0f;
                    rainRootGo.transform.position = cam.transform.position + Vector3.up * 16f + fwd * 10f;
                }

                bool isInteriorView = false;
                if (cam != null)
                {
                    var chase = cam.GetComponent<ArcadeChaseCamera>();
                    if (chase != null && chase.currentMode == ArcadeChaseCamera.CameraMode.Bumper)
                    {
                        isInteriorView = true;
                    }
                }

                if (rainExtSource != null)
                {
                    rainExtSource.volume = inMenu ? 0f : (isInteriorView ? 0.15f : 0.72f);
                }
                if (rainIntSource != null)
                {
                    rainIntSource.volume = inMenu ? 0f : (isInteriorView ? 0.78f : 0.0f);
                }

                // Periodic thunder & subtle lightning flash in Rainy weather
                if (!inMenu && Time.time >= nextAmbientEventTime)
                {
                    nextAmbientEventTime = Time.time + UnityEngine.Random.Range(18f, 35f);
                    if (clipThunder != null && sfxOneShotSource != null)
                    {
                        sfxOneShotSource.PlayOneShot(clipThunder, 0.75f);
                        lightningFlashIntensity = 0.85f;
                    }
                }

                if (lightningFlashIntensity > 0.001f && keyLight != null)
                {
                    lightningFlashIntensity = Mathf.MoveTowards(lightningFlashIntensity, 0f, dt * 3.5f);
                    int combinedIdx = CurrentWeatherIndex + CurrentTimeOfDayIndex * 4;
                    float baseInt = CurrentTimeOfDayIndex == 3 ? 0.15f : 0.50f;
                    keyLight.intensity = baseInt + lightningFlashIntensity * 1.4f;
                }
            }
            else if (CurrentWeatherIndex == 2) // Foggy
            {
                if (windSource != null)
                {
                    windSource.volume = inMenu ? 0f : 0.25f;
                }
                if (!inMenu && Time.time >= nextAmbientEventTime)
                {
                    nextAmbientEventTime = Time.time + UnityEngine.Random.Range(20f, 38f);
                    if (clipFoghorn != null && sfxOneShotSource != null)
                    {
                        sfxOneShotSource.PlayOneShot(clipFoghorn, 0.45f);
                    }
                }
            }
        }

        private void PlayUiConditionSound(int todIdx, int weathIdx)
        {
            EnsureAudioSources();
            if (sfxOneShotSource == null) return;

            string clipName = null;
            if (todIdx >= 0)
            {
                clipName = todIdx == 0 ? "UImorning.22k"
                    : (todIdx == 1 ? "UInoon.22k"
                    : (todIdx == 2 ? "UImorning.22k" : "UInight.22k"));
            }
            else if (weathIdx >= 0)
            {
                clipName = weathIdx == 3 ? "UIrain.22k" : "UIcloudy.22k";
            }

            if (!string.IsNullOrEmpty(clipName))
            {
                AudioClip clip = MM2RuntimeDecoders.LoadAudioClip(clipName);
                if (clip != null)
                {
                    sfxOneShotSource.PlayOneShot(clip, 0.75f);
                }
            }
        }

        // =====================================================================
        // VEHICLE HEADLIGHTS & TIRE FRICTION REFRESH
        // =====================================================================
        private static void RefreshAllVehiclesInScene(int todIdx, int weathIdx)
        {
            bool autoHeadlights = ShouldHeadlightsBeOn(todIdx, weathIdx);

            // 1. Playable vehicle(s)
            foreach (var car in FindObjectsByType<ArcadeVehicleController>(FindObjectsSortMode.None))
            {
                if (car != null)
                {
                    car.SetHeadlights(autoHeadlights);
                    car.RefreshWeatherTireGrip();
                }
            }

            // 2. AI Opponent & Traffic vehicles (toggle HLI_H / TLI_H child meshes if present)
            if (MM2RaceManager.Instance != null)
            {
                MM2RaceManager.Instance.RefreshAiVehicleLights(autoHeadlights);
            }
        }

        private static string CleanTexName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string s = name.Trim().ToLowerInvariant();
            int dash = s.IndexOf('-');
            return dash >= 0 ? s.Substring(0, dash) : s;
        }

        private static float ParseFloat(string s, float fallback)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) &&
                   !float.IsNaN(v)
                ? v
                : fallback;
        }

        // =====================================================================
        // CRUISE MODE ON-SCREEN TOAST BANNER
        // =====================================================================
        private void OnGUI()
        {
            if (toastTimer <= 0f || string.IsNullOrEmpty(toastMessage)) return;
            if (FindFirstObjectByType<MM2MenuController>() != null) return;

            if (toastBoxStyle == null)
            {
                Texture2D bgTex = new Texture2D(1, 1);
                bgTex.SetPixel(0, 0, new Color(0.04f, 0.08f, 0.18f, 0.86f));
                bgTex.Apply();

                toastBoxStyle = new GUIStyle(GUI.skin.box)
                {
                    normal = { background = bgTex }
                };

                toastTitleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 20,
                    normal = { textColor = new Color(1f, 0.92f, 0.15f, 1f) }
                };

                toastSubStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 13,
                    normal = { textColor = new Color(0.82f, 0.90f, 1f, 0.92f) }
                };
            }

            float alpha = Mathf.Clamp01(toastTimer / 0.5f);
            Color prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);

            float boxW = 480f;
            float boxH = 58f;
            float boxX = (Screen.width - boxW) * 0.5f;
            float boxY = 22f;

            GUI.Box(new Rect(boxX, boxY, boxW, boxH), GUIContent.none, toastBoxStyle);
            GUI.Label(new Rect(boxX, boxY + 6f, boxW, 28f), toastMessage, toastTitleStyle);
            GUI.Label(new Rect(boxX, boxY + 32f, boxW, 20f), "Cruise Controls:  [  Next Time of Day    |    ]  Next Weather", toastSubStyle);

            GUI.color = prevColor;
        }
    }
}
