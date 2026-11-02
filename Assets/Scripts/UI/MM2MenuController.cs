using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using MM2.Vehicle;
using MM2.CameraSystem;
using MM2.Archive;

namespace MM2.UI
{
    public class MM2EventResultEntry
    {
        public int Rank;
        public string RacerName;
        public string TimeOrStatus;
        public bool IsPlayer;
    }

    public class MM2EventResultsData
    {
        public string RaceModeLabel = "Circuit Race";
        public string RaceName = "";
        public bool CanAdvanceToNext = true;
        public readonly List<MM2EventResultEntry> Entries = new List<MM2EventResultEntry>();
    }

    /// <summary>
    /// High-quality circular multi-sample outline & drop shadow effect for Unity UI Text.
    /// Replaces Unity's 4-corner diagonal Outline + cascading Shadow (which caused 45-degree corner staircase notches
    /// and uneven overlapping shadow blobs) with smooth radial ring sampling directly from the clean base glyph.
    /// </summary>
    [DisallowMultipleComponent]
    public class MM2SmoothTextEffect : BaseMeshEffect
    {
        public bool IsHeaderStyle;
        public float OutlineRadius = 1.25f;
        public Color32 OutlineColor = new Color32(0, 0, 0, 96);
        public Vector2 ShadowOffset = new Vector2(2.0f, -2.0f);
        public Color32 ShadowColor = new Color32(0, 0, 0, 175);

        private static readonly List<UIVertex> s_BaseVerts = new List<UIVertex>(2048);
        private static readonly List<UIVertex> s_OutVerts = new List<UIVertex>(32768);

        private static readonly Vector2[] s_Dir8 = BuildCircleDirections(8);
        private static readonly Vector2[] s_Dir12 = BuildCircleDirections(12);
        private static readonly Vector2[] s_Dir16 = BuildCircleDirections(16);

        private static Vector2[] BuildCircleDirections(int count)
        {
            var dirs = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                float angle = (2f * Mathf.PI * i) / count;
                dirs[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            return dirs;
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh == null) return;

            s_BaseVerts.Clear();
            vh.GetUIVertexStream(s_BaseVerts);
            int baseCount = s_BaseVerts.Count;
            if (baseCount == 0) return;

            s_OutVerts.Clear();

            if (IsHeaderStyle)
            {
                int needed = baseCount * (8 + 16 + 12 + 12 + 1);
                if (s_OutVerts.Capacity < needed) s_OutVerts.Capacity = needed;

                // 1. Soft feathered drop shadow (8 circular samples around +5.5, -5.5)
                Color32 shCol = new Color32(0, 0, 0, 48);
                for (int i = 0; i < s_Dir8.Length; i++)
                {
                    Vector2 d = s_Dir8[i];
                    AppendShiftedCopy(s_BaseVerts, baseCount, s_OutVerts, 5.5f + d.x * 1.6f, -5.5f + d.y * 1.6f, shCol);
                }

                // 2. Outer dark-navy/black stroke: feathered outer AA ring (16 samples) + solid inner ring (12 samples)
                Color32 outerFeathered = new Color32(8, 8, 28, 110);
                for (int i = 0; i < s_Dir16.Length; i++)
                {
                    Vector2 d = s_Dir16[i];
                    AppendShiftedCopy(s_BaseVerts, baseCount, s_OutVerts, d.x * 4.2f, d.y * 4.2f, outerFeathered);
                }

                Color32 outerCore = new Color32(8, 8, 28, 195);
                for (int i = 0; i < s_Dir12.Length; i++)
                {
                    Vector2 d = s_Dir12[i];
                    AppendShiftedCopy(s_BaseVerts, baseCount, s_OutVerts, d.x * 2.65f, d.y * 2.65f, outerCore);
                }

                // 3. Inner warm red-orange bevel rim (12 circular samples shifted slightly lower-right)
                Color32 innerRim = new Color32(205, 50, 8, 150);
                for (int i = 0; i < s_Dir12.Length; i++)
                {
                    Vector2 d = s_Dir12[i];
                    AppendShiftedCopy(s_BaseVerts, baseCount, s_OutVerts, 0.55f + d.x * 1.45f, -0.55f + d.y * 1.45f, innerRim);
                }

                // 4. Crisp base glyph on top
                for (int i = 0; i < baseCount; i++)
                {
                    s_OutVerts.Add(s_BaseVerts[i]);
                }
            }
            else
            {
                // Guard against Unity UI's 65,000 vertex-per-mesh limit on long text blocks
                const int MaxStreamVerts = 63000;
                bool canIncludeOutline = (baseCount * 10) <= MaxStreamVerts;
                bool canIncludeShadow = (baseCount * 2) <= MaxStreamVerts;
                if (!canIncludeShadow)
                {
                    return;
                }

                int needed = baseCount * (1 + (canIncludeOutline ? 8 : 0) + 1);
                if (s_OutVerts.Capacity < needed) s_OutVerts.Capacity = needed;

                // 1. Clean drop shadow of the base glyph only (no cascaded shadow-of-outline artifacts)
                if (ShadowColor.a > 0)
                {
                    AppendShiftedCopy(s_BaseVerts, baseCount, s_OutVerts, ShadowOffset.x, ShadowOffset.y, ShadowColor);
                }

                // 2. Smooth 8-direction circular outline around the base glyph
                if (canIncludeOutline && OutlineColor.a > 0 && OutlineRadius > 0.01f)
                {
                    for (int i = 0; i < s_Dir8.Length; i++)
                    {
                        Vector2 d = s_Dir8[i];
                        AppendShiftedCopy(s_BaseVerts, baseCount, s_OutVerts, d.x * OutlineRadius, d.y * OutlineRadius, OutlineColor);
                    }
                }

                // 3. Crisp base glyph on top
                for (int i = 0; i < baseCount; i++)
                {
                    s_OutVerts.Add(s_BaseVerts[i]);
                }
            }

            vh.Clear();
            vh.AddUIVertexTriangleStream(s_OutVerts);
        }

        private static void AppendShiftedCopy(
            List<UIVertex> src,
            int count,
            List<UIVertex> dst,
            float dx,
            float dy,
            Color32 effectColor
        )
        {
            for (int i = 0; i < count; i++)
            {
                UIVertex v = src[i];
                Vector3 pos = v.position;
                pos.x += dx;
                pos.y += dy;
                v.position = pos;
                Color32 c = effectColor;
                c.a = (byte)((c.a * v.color.a) / 255);
                v.color = c;
                dst.Add(v);
            }
        }
    }

    [Serializable]
    public class MM2SettingsData
    {
        public int DisplayIndex = -1;
        public string DisplayName = "";
        public int WindowMode = -1;
        public int ResolutionWidth = 0;
        public int ResolutionHeight = 0;
        public bool EnableLiveMenuBackground = false;
        public bool ShowFpsCounter = false;
    }

    /// <summary>
    /// Persistent session configuration selected in the Open Madness menus.
    /// </summary>
    public static class MM2GameSession
    {
        public static string DriverName = "DriverX";
        public static string RaceType = "Cruise";
        public static int RaceIndex = 0;
        public static string RaceName = "";
        public static int CruiseSpawnIndex = 0;
        public static string CruiseSpawnFileName = "";
        public static int NumLaps = 3;
        public static int NumOpponents = 7;
        public static float RaceTimeLimit = 45f;
        public static string Location = "";
        public static string TimeOfDay = "Noon";
        public static string Weather = "Clear";
        public static int PedestrianDensity = 20;
        public static int TrafficDensity = 45;
        public static int CopDensity = 100;
        public static string VehicleName = "";
        public static string VehicleColor = "Default";
        public static string Transmission = "Automatic";
        public static bool LaunchedFromGoDrive = false;
        public static MM2EventResultsData LastEventResults;
        public static string LastUnlockedRewardMessage = "";

        public static bool IsCrashCourseSession = false;
        public static string CrashCourseCity = "sf";
        public static int CrashLessonIndex = 0;
        public static int CrashSubMode = 0; // 0 = Training, 1 = Work Experience Blitz, 2 = Work Experience Checkpoint
        public static int CrashWorkRaceIndex = 0;
        public static bool EnableLiveMenuBackground = false;
        public static bool ShowFpsCounter = false;
        public static int SavedDisplayIndex = -1;
        public static string SavedDisplayName = "";
        public static int SavedWindowMode = -1; // -1 = Default, 0 = FullScreenWindow, 1 = ExclusiveFullScreen, 2 = Windowed
        public static int SavedResolutionWidth = 0;
        public static int SavedResolutionHeight = 0;
        private static bool _settingsLoaded = false;

        public static FullScreenMode GetDesiredFullScreenMode()
        {
            switch (SavedWindowMode)
            {
                case 0: return FullScreenMode.FullScreenWindow;
                case 1: return FullScreenMode.ExclusiveFullScreen;
                case 2: return FullScreenMode.Windowed;
                default: return Screen.fullScreenMode;
            }
        }

        public static int WindowModeIndexFromFullScreenMode(FullScreenMode mode)
        {
            switch (mode)
            {
                case FullScreenMode.ExclusiveFullScreen:
                    return 1;
                case FullScreenMode.Windowed:
                    return 2;
                default:
                    return 0;
            }
        }

        public static bool TryResolveSavedDisplay(List<DisplayInfo> displays, out int resolvedIndex, out DisplayInfo resolvedDisplay)
        {
            resolvedIndex = -1;
            resolvedDisplay = default;
            if (displays == null || displays.Count == 0) return false;

            if (!string.IsNullOrEmpty(SavedDisplayName))
            {
                for (int i = 0; i < displays.Count; i++)
                {
                    if (string.Equals(displays[i].name, SavedDisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedIndex = i;
                        resolvedDisplay = displays[i];
                        return true;
                    }
                }
            }

            if (SavedDisplayIndex >= 0 && SavedDisplayIndex < displays.Count)
            {
                resolvedIndex = SavedDisplayIndex;
                resolvedDisplay = displays[SavedDisplayIndex];
                return true;
            }

            return false;
        }

        public static string GetSettingsFilePath()
        {
            try
            {
                DirectoryInfo parentDir = Directory.GetParent(Application.dataPath);
                string rootDir = parentDir != null ? parentDir.FullName : Application.persistentDataPath;
                return Path.Combine(rootDir, "settings.json");
            }
            catch
            {
                return Path.Combine(Application.persistentDataPath, "settings.json");
            }
        }

        public static void EnsureSettingsLoaded()
        {
            if (_settingsLoaded) return;
            _settingsLoaded = true;

            string path = GetSettingsFilePath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    MM2SettingsData data = JsonUtility.FromJson<MM2SettingsData>(json);
                    if (data != null)
                    {
                        EnableLiveMenuBackground = data.EnableLiveMenuBackground;
                        ShowFpsCounter = data.ShowFpsCounter;
                        SavedDisplayIndex = data.DisplayIndex;
                        SavedDisplayName = data.DisplayName ?? "";
                        SavedWindowMode = data.WindowMode;

                        var displays = new List<DisplayInfo>();
                        Screen.GetDisplayLayout(displays);
                        if (TryResolveSavedDisplay(displays, out int dispIdx, out DisplayInfo dispInfo))
                        {
                            PlayerPrefs.SetInt("UnitySelectMonitor", dispIdx);
                            PlayerPrefs.Save();
                            if (!Screen.mainWindowDisplayInfo.Equals(dispInfo))
                            {
                                Screen.MoveMainWindowTo(dispInfo, Vector2Int.zero);
                            }
                        }

                        FullScreenMode targetMode = GetDesiredFullScreenMode();
                        if (data.ResolutionWidth >= 640 && data.ResolutionHeight >= 480)
                        {
                            SavedResolutionWidth = data.ResolutionWidth;
                            SavedResolutionHeight = data.ResolutionHeight;
                            Screen.SetResolution(SavedResolutionWidth, SavedResolutionHeight, targetMode);
                        }
                        else if (SavedWindowMode >= 0)
                        {
                            Screen.fullScreenMode = targetMode;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MM2 Settings] Failed to read settings.json: {ex.Message}");
                }
            }
            else
            {
                SaveSettings();
            }
        }

        public static void SaveSettings()
        {
            try
            {
                string path = GetSettingsFilePath();
                var data = new MM2SettingsData
                {
                    DisplayIndex = SavedDisplayIndex,
                    DisplayName = SavedDisplayName ?? "",
                    WindowMode = SavedWindowMode >= 0 ? SavedWindowMode : WindowModeIndexFromFullScreenMode(Screen.fullScreenMode),
                    ResolutionWidth = SavedResolutionWidth > 0 ? SavedResolutionWidth : Screen.width,
                    ResolutionHeight = SavedResolutionHeight > 0 ? SavedResolutionHeight : Screen.height,
                    EnableLiveMenuBackground = EnableLiveMenuBackground,
                    ShowFpsCounter = ShowFpsCounter
                };
                string json = JsonUtility.ToJson(data, true);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2 Settings] Failed to save settings.json: {ex.Message}");
            }
        }

        public static int GetCrashLessonStatus(string cityMapName, int lessonIndex)
        {
            if (string.IsNullOrEmpty(cityMapName)) cityMapName = "sf";
            if (MM2PlayerManager.IsCrashLessonPassed(cityMapName, lessonIndex))
            {
                return 1;
            }
            string key = $"MM2_CC_{DriverName}_{cityMapName.ToLowerInvariant()}_{lessonIndex}";
            return PlayerPrefs.GetInt(key, 0);
        }

        public static void SetCrashLessonStatus(string cityMapName, int lessonIndex, int status)
        {
            if (string.IsNullOrEmpty(cityMapName)) cityMapName = "sf";
            if (MM2PlayerManager.IsCrashLessonPassed(cityMapName, lessonIndex) && status == 2)
            {
                return;
            }
            string key = $"MM2_CC_{DriverName}_{cityMapName.ToLowerInvariant()}_{lessonIndex}";
            int current = PlayerPrefs.GetInt(key, 0);
            // Once a lesson has been passed (1), keep Passed even if replayed and failed later
            if (current == 1 && status == 2) return;
            PlayerPrefs.SetInt(key, status);
            PlayerPrefs.Save();
        }

        public static bool IsCrashLessonUnlocked(string cityMapName, int lessonIndex)
        {
            if (lessonIndex < 0) return false;

            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(cityMapName);
            MM2CrashLessonSpec[] lessons = spec != null ? spec.CrashLessons : null;

            if (lessons != null && lessons.Length > 0)
            {
                if (lessonIndex >= lessons.Length) return false;
                MM2CrashLessonType targetType = lessons[lessonIndex].LessonType;

                switch (targetType)
                {
                    case MM2CrashLessonType.Lesson:
                        // Standard lessons are open to practice in any order
                        return true;

                    case MM2CrashLessonType.Midterm:
                        // Blocked on all lessons before this midterm (after the previous midterm or final) being completed
                        for (int i = lessonIndex - 1; i >= 0; i--)
                        {
                            MM2CrashLessonType prevType = lessons[i].LessonType;
                            if (prevType == MM2CrashLessonType.Midterm || prevType == MM2CrashLessonType.Final)
                            {
                                break;
                            }
                            if (prevType == MM2CrashLessonType.Lesson && GetCrashLessonStatus(cityMapName, i) != 1)
                            {
                                return false;
                            }
                        }
                        return true;

                    case MM2CrashLessonType.Final:
                        // Blocked on all midterms before this final (since the previous final) being completed
                        bool foundMidtermSincePrevFinal = false;
                        for (int i = lessonIndex - 1; i >= 0; i--)
                        {
                            MM2CrashLessonType prevType = lessons[i].LessonType;
                            if (prevType == MM2CrashLessonType.Final)
                            {
                                break;
                            }
                            if (prevType == MM2CrashLessonType.Midterm)
                            {
                                foundMidtermSincePrevFinal = true;
                                if (GetCrashLessonStatus(cityMapName, i) != 1)
                                {
                                    return false;
                                }
                            }
                        }
                        // If a custom curriculum has a final with no preceding midterms, require the lessons since the previous final
                        if (!foundMidtermSincePrevFinal)
                        {
                            for (int i = lessonIndex - 1; i >= 0; i--)
                            {
                                MM2CrashLessonType prevType = lessons[i].LessonType;
                                if (prevType == MM2CrashLessonType.Final)
                                {
                                    break;
                                }
                                if (GetCrashLessonStatus(cityMapName, i) != 1)
                                {
                                    return false;
                                }
                            }
                        }
                        return true;

                    default:
                        // Anything without a lesson, midtrm, or final prefix is blocked on all previous entries being completed
                        for (int i = 0; i < lessonIndex; i++)
                        {
                            if (GetCrashLessonStatus(cityMapName, i) != 1)
                            {
                                return false;
                            }
                        }
                        return true;
                }
            }

            return true;
        }

        public static int GetNextUnlockedCrashLessonIndex(string cityMapName, int currentLessonIndex, int totalLessons)
        {
            if (totalLessons <= 0) return -1;

            // 1. Prefer the next unlocked and not-yet-passed lesson after currentLessonIndex
            for (int i = currentLessonIndex + 1; i < totalLessons; i++)
            {
                if (IsCrashLessonUnlocked(cityMapName, i) && GetCrashLessonStatus(cityMapName, i) != 1)
                {
                    return i;
                }
            }

            // 2. Otherwise, any earlier unlocked and not-yet-passed lesson
            for (int i = 0; i < currentLessonIndex && i < totalLessons; i++)
            {
                if (IsCrashLessonUnlocked(cityMapName, i) && GetCrashLessonStatus(cityMapName, i) != 1)
                {
                    return i;
                }
            }

            // 3. If all unlocked lessons are already passed, advance to the next unlocked lesson after currentLessonIndex
            for (int i = currentLessonIndex + 1; i < totalLessons; i++)
            {
                if (IsCrashLessonUnlocked(cityMapName, i))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int GetMaxUnlockedCrashLessonIndex(string cityMapName, int totalLessons)
        {
            // Default to the first unlocked lesson that has not yet been passed
            for (int i = 0; i < totalLessons; i++)
            {
                if (IsCrashLessonUnlocked(cityMapName, i) && GetCrashLessonStatus(cityMapName, i) != 1)
                {
                    return i;
                }
            }

            // If all unlocked lessons are passed, return the highest unlocked lesson index
            for (int i = totalLessons - 1; i >= 0; i--)
            {
                if (IsCrashLessonUnlocked(cityMapName, i))
                {
                    return i;
                }
            }
            return 0;
        }

        public static string SelectedGameMode => RaceType ?? "Cruise";

        public static string SelectedCityCode
        {
            get
            {
                CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(Location);
                if (spec != null && !string.IsNullOrEmpty(spec.MapName))
                {
                    return spec.MapName;
                }
                if (!string.IsNullOrEmpty(Location) &&
                    Location.IndexOf("London", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "london";
                }
                return "sf";
            }
        }

        public static int TimeOfDayIndex
        {
            get
            {
                if (string.Equals(TimeOfDay, "Morning", StringComparison.OrdinalIgnoreCase)) return 0;
                if (string.Equals(TimeOfDay, "Noon", StringComparison.OrdinalIgnoreCase)) return 1;
                if (string.Equals(TimeOfDay, "Evening", StringComparison.OrdinalIgnoreCase)) return 2;
                if (string.Equals(TimeOfDay, "Night", StringComparison.OrdinalIgnoreCase)) return 3;
                return 1;
            }
        }

        public static int WeatherIndex
        {
            get
            {
                if (string.IsNullOrEmpty(Weather)) return 0;
                if (Weather.StartsWith("Cloud", StringComparison.OrdinalIgnoreCase)) return 1;
                if (Weather.StartsWith("Fog", StringComparison.OrdinalIgnoreCase)) return 2;
                if (Weather.StartsWith("Rain", StringComparison.OrdinalIgnoreCase)) return 3;
                return 0;
            }
        }
    }

    /// <summary>
    /// Authentic Midtown Madness 2 Widescreen Menu System (Option A).
    /// Automatically initializes in menu scenes or overlays, and deactivates when "GO DRIVE" launches the track.
    /// </summary>
    public class MM2MenuController : MonoBehaviour
    {
        public static bool IsMenuOpen => FindFirstObjectByType<MM2MenuController>() != null;
        public static bool EnableMultiplayerButton = false;

        public enum MenuScreen
        {
            MainMenu = 0,
            Races = 1,
            SelectVehicle = 2,
            EventResults = 3,
            CrashCourseIntro = 4,
            CrashCourseHub = 5,
            Options = 6,
            Help = 7
        }

        [Header("Current State")]
        public MenuScreen currentScreen = MenuScreen.MainMenu;
        private MenuScreen previousScreenBeforeOptions = MenuScreen.MainMenu;

        // Loaded Sprites & Textures
        private Sprite spOrangeBase;
        private Sprite spBlueCollage;
        private Sprite spBoxBlack9;
        private Sprite spBoxNavy9;
        private Sprite spFrameTrans9;
        private Sprite spFrameGlass9;
        private Texture2D texGridBlue;
        private Sprite spBarVert;
        private Sprite spBarHoriz;
        private Sprite spVehicleSpot;
        private Sprite spBtnGlowRect;
        private Sprite spBtnGlowRectHover;
        private Sprite spBtnArrowRight;
        private Sprite spBtnArrowRightHover;
        private Sprite spBtnArrowLeft;
        private Sprite spBtnArrowLeftHover;
        private Sprite spChevronUp;
        private Sprite spChevronDown;
        private Sprite spChevronLeft;
        private Sprite spChevronRight;
        private Sprite spOrbGreen;
        private Sprite spOrbRed;
        private Sprite spOrbBlue;
        private Sprite spIconDropdownV;
        private Sprite spLogoMM2;
        private Sprite spCcSmChk;
        private Sprite spCcSmFail;
        private Sprite spCcPass;
        private Sprite spCcFail;
        private Sprite spCheckboxUnchecked;
        private Sprite spCheckboxCheckedGreen;
        private Sprite spCheckboxCheckedBlue;

        private GameObject activeDropdownOverlay;
        private Action onActiveDropdownClosed;

        private struct ResponsiveRectEntry
        {
            public float BaseX;
            public float BaseY;
            public float BaseW;
            public float BaseH;
            public bool IsHalfScaleText;
        }

        private RectTransform canvasRootRect;
        private Vector2 lastResponsiveCanvasSize = Vector2.zero;
        private readonly Dictionary<RectTransform, ResponsiveRectEntry> responsiveRects =
            new Dictionary<RectTransform, ResponsiveRectEntry>();
        private bool suppressResponsiveRegistration;

        private Font fontHeaderItalic;       // OpenSans-ExtraBoldItalic (for "RACES", "SELECT VEHICLE" headers)
        private Font fontCondensedExtraBold; // OpenSans-CondensedExtraBold (for MM2 condensed heavy field labels)
        private Font fontUIExtraBold;        // OpenSans-ExtraBold (for glowing & arrow buttons)
        private Font fontUIBold;             // OpenSans-Bold (for selector values & description boxes)

        // Screen Root Panels
        private GameObject panelMainMenu;
        private GameObject panelRaces;
        private GameObject panelSelectVehicle;
        private GameObject panelEventResults;
        private GameObject panelCrashCourseIntro;
        private GameObject panelCrashCourseHub;
        private GameObject panelOptions;
        private GameObject panelHelp;
        private ScrollRect helpScrollRect;
        private GameObject activeModalOverlay;

        // Options Screen Interactive References
        private Text txtDisplayValue;
        private Text txtResolutionValue;
        private Text txtWindowModeValue;
        private Image imgLiveUiCheckboxBox;
        private Image imgLiveUiCheckTick;
        private Text txtLiveUiCheckMark;
        private Image imgFpsCheckboxBox;
        private Text txtOptionsBottomHelp;
        private readonly List<DisplayInfo> availableDisplays = new List<DisplayInfo>();
        private string[] displayLabels = { "1: Display 1" };
        private int idxDisplay = 0;
        private readonly List<Vector2Int> availableResolutions = new List<Vector2Int>();
        private string[] resolutionLabels = { "1920 x 1080" };
        private int idxResolution = 0;
        private static readonly string[] windowModeLabels = { "Fullscreen Window", "Exclusive Fullscreen", "Windowed" };
        private int idxWindowMode = 0;
        private Coroutine activeMoveDisplayCoroutine;

        // Shared Top-Left Header Logo & Real Text Title
        private Image imgHeaderLogo;
        private Text txtHeaderTitle;
        private GameObject leftSplitRacesDecor;
        private GameObject leftFullOrangeDecor;
        private GameObject dividerMidVert;
        private GameObject topRightNavBarGroup;
        private GameObject blueStripEventResultsTopExt;

        // Main Menu (Driver Chooser & Info) Interactive References
        private Text txtDriverSelectorValue;
        private Text txtMainMenuDriverStats;
        private Text txtMainMenuBottomHelp;
        private Button btnCrashCourse;
        private int mainMenuStatsViewMode = 0; // 0 = Overview, 1 = Detailed Stats, 2 = Race Records
        private int mainMenuRecordsModeIndex = 0; // 0 = Blitz, 1 = Checkpoint, 2 = Circuit, 3 = CrashCourse

        // Event Results Screen Interactive References
        private const int MaxEventResultRows = 9;
        private readonly Text[] eventResultsRankTexts = new Text[MaxEventResultRows];
        private readonly Text[] eventResultsNameTexts = new Text[MaxEventResultRows];
        private readonly Text[] eventResultsTimeTexts = new Text[MaxEventResultRows];
        private Text txtEventResultsRaceType;
        private Text txtEventResultsRaceName;
        private Text txtEventResultsBtnRestart;
        private Text txtEventResultsBtnNext;
        private Text txtEventResultsBtnMenu;
        private Text txtEventResultsBtnExit;

        // Crash Course Hub Interactive References
        private const int MaxCrashCurriculumRows = 16;
        private readonly GameObject[] crashCurriculumRowObjects = new GameObject[MaxCrashCurriculumRows];
        private readonly Text[] crashCurriculumRowLabels = new Text[MaxCrashCurriculumRows];
        private readonly Image[] crashCurriculumPassIcons = new Image[MaxCrashCurriculumRows];
        private readonly Image[] crashCurriculumFailIcons = new Image[MaxCrashCurriculumRows];
        private readonly Text[] crashCurriculumPassTexts = new Text[MaxCrashCurriculumRows];
        private readonly Text[] crashCurriculumFailTexts = new Text[MaxCrashCurriculumRows];
        private Text txtCrashSchoolTitle;
        private Text txtCrashSchoolSubtitle;
        private readonly Image[] radioCrashSubModeOrbs = new Image[3];
        private readonly Text[] radioCrashSubModeLabels = new Text[3];
        private GameObject boxCrashLessonDisabled;
        private GameObject selectorCrashLessonGroup;
        private Text txtCrashLessonValue;
        private GameObject boxCrashWorkRaceDisabled;
        private GameObject selectorCrashWorkRaceGroup;
        private Text txtCrashWorkRaceValue;
        private Text txtCrashBottomHelp;
        private string[] currentCrashLessonDropdownNames = Array.Empty<string>();
        private string[] currentCrashWorkRaceNames = Array.Empty<string>();

        // Races Screen Interactive References
        private readonly GameObject[] radioModeRows = new GameObject[4];
        private readonly Image[] radioModeOrbs = new Image[4];
        private readonly Text[] radioModeLabels = new Text[4];
        private Text lblRaceName;
        private GameObject boxRaceNameDisabled;
        private GameObject selectorRaceNameGroup;
        private Text txtRaceNameValue;
        private GameObject lapsRowGroup;
        private Text txtLapsValue;
        private GameObject opponentsRowGroup;
        private Text txtOpponentsValue;
        private Text txtLocationValue;
        private Text txtTimeOfDayValue;
        private Text txtWeatherValue;
        private Image imgCityMap;
        private RectTransform barPedestrianFill;
        private RectTransform barTrafficFill;
        private RectTransform barCopFill;
        private Text txtBottomRaceHelp;

        // Select Vehicle Screen Interactive References
        private Text txtVehicleValue;
        private Text txtColorValue;
        private Text txtTransmissionValue;
        private Text txtVehicleDesc;
        private Text txtVehicleLockedBanner;
        private RectTransform scoreBarHorsepower;
        private RectTransform scoreBarTopSpeed;
        private RectTransform scoreBarDurability;
        private RectTransform scoreBarMass;
        private RawImage rawVehiclePreview;
        private GameObject showcaseRoot;
        private Transform showcaseCarPivot;
        private Transform showcaseCameraTransform;
        private Camera showcaseCamera;
        private RenderTexture showcaseRenderTex;
        private AudioSource vehicleSelectAudioSource;

        // Live 3D San Francisco Menu Background (el_sharpimg_f + Frozen Airborne Car + Duotone Shader)
        private RawImage rawLiveMenuBg;
        private GameObject menuBgRigRoot;
        private Camera menuBgCamera;
        private RenderTexture menuBgRenderTex;
        private Material menuBgDuotoneMat;
        private Vector3 menuBgBaseCamPos;
        private Quaternion menuBgBaseCamRot;
        private float menuBgStartTime;

        // Option Lists (Populated dynamically in Awake via SyncSelectionsFromSession from .ar archives)
        private static readonly string[] raceModes = { "Cruise", "Blitz", "Checkpoint", "Circuit" };
        private static readonly string[] raceModeUpperLabels = { "CRUISE", "BLITZ", "CHECKPOINT", "CIRCUIT" };
        private static readonly string[] lapOptions = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10" };
        private string[] currentRaceNames = Array.Empty<string>();
        private string[] currentCruiseSpawnNames = { "Random" };
        private string[] locations = { "Default City" };
        private readonly string[] timesOfDay = { "Morning", "Noon", "Evening", "Night" };
        private readonly string[] weathers = { "Clear", "Cloudy", "Foggy", "Rainy" };
        private string[] vehicles = { "Default Vehicle" };
        private string[] vehicleColors = { "Default" };
        private readonly string[] transmissions = { "Automatic", "Manual" };

        private int idxRaceMode = 0;      // 0 = Cruise, 1 = Blitz, 2 = Checkpoint, 3 = Circuit
        private int idxRaceName = 0;
        private int idxCruiseSpawn = 0;   // 0 = Random, >0 = specific spawn location from !SpawnLocationSelector.ar
        private int idxLocation = 0;
        private int idxTimeOfDay = 1;     // Noon
        private int idxWeather = 0;       // Clear
        private int idxVehicle = 0;
        private int idxVehicleColor = 0;
        private int idxTransmission = 0;  // Automatic

        private struct SuspendedCameraState
        {
            public Camera Cam;
            public int CullingMask;
            public CameraClearFlags ClearFlags;
            public Color BackgroundColor;
        }

        private readonly List<GameObject> disabledSceneGameplayObjects = new List<GameObject>();
        private readonly List<SuspendedCameraState> suspendedSceneCameras = new List<SuspendedCameraState>();
        private Camera menuDisplayCamera;
        private InputField activeCreateDriverInput;
        private byte activeCreateDriverSkillLevel = 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBootstrapOnSceneLoad()
        {
            SceneManager.sceneLoaded -= OnRuntimeSceneLoaded;
            SceneManager.sceneLoaded += OnRuntimeSceneLoaded;

            // If we just clicked "GO DRIVE" to load GoDrive, let the user drive immediately!
            if (MM2GameSession.LaunchedFromGoDrive)
            {
                MM2GameSession.LaunchedFromGoDrive = false;
                ApplySelectedPaintjobToActiveVehicles();
                EnsureEscapeReturnListener();
                return;
            }

            // Otherwise, if no MM2MenuController exists in the current scene, create it automatically
            if (FindFirstObjectByType<MM2MenuController>() == null)
            {
                GameObject menuGo = new GameObject("MM2_MenuSystem");
                menuGo.AddComponent<MM2MenuController>();
            }
        }

        private static void OnRuntimeSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (MM2GameSession.LaunchedFromGoDrive)
            {
                MM2GameSession.LaunchedFromGoDrive = false;
                ApplySelectedPaintjobToActiveVehicles();
                EnsureEscapeReturnListener();
            }
        }

        private static void ApplySelectedPaintjobToActiveVehicles()
        {
            MM2RuntimeCityLoader.EnsureCityLoadedInScene(
                MM2GameSession.Location,
                out Vector3 spawnPos,
                out Quaternion spawnRot
            );

            // Set up race checkpoints/timers and resolve session spawn pose (including Cruise Starting Point / Random)
            MM2RaceManager raceMgr = MM2RaceManager.EnsureExists();
            if (raceMgr != null)
            {
                raceMgr.SetupSessionAndGetSpawnPose(out Vector3 raceSpawnPos, out Quaternion raceSpawnRot);
                spawnPos = raceSpawnPos;
                spawnRot = raceSpawnRot;
            }

            Transform primaryCarTransform = null;
            foreach (var car in FindObjectsByType<ArcadeVehicleController>(FindObjectsSortMode.None))
            {
                if (car != null)
                {
                    if (primaryCarTransform == null) primaryCarTransform = car.transform;
                    var rb = car.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.useGravity = true;
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.position = spawnPos;
                        rb.rotation = spawnRot;
                    }
                    car.transform.SetPositionAndRotation(spawnPos, spawnRot);
                    VehiclePaintjob.ConfigurePlayableVehicle(car, MM2GameSession.VehicleName, MM2GameSession.VehicleColor);
                    bool autoHeadlights =
                        string.Equals(MM2GameSession.TimeOfDay, "Evening", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(MM2GameSession.TimeOfDay, "Night", StringComparison.OrdinalIgnoreCase) ||
                        (MM2GameSession.Weather != null && (
                            MM2GameSession.Weather.StartsWith("Rain", StringComparison.OrdinalIgnoreCase) ||
                            MM2GameSession.Weather.StartsWith("Fog", StringComparison.OrdinalIgnoreCase)));
                    car.SetHeadlights(autoHeadlights);
                }
            }

            if (raceMgr != null && primaryCarTransform != null)
            {
                raceMgr.InitializeAiSystemsForSession(primaryCarTransform);
            }

            // Apply selected Time of Day (Morning/Noon/Evening/Night) and Weather (Clear/Cloudy/Foggy/Rainy)
            MM2EnvironmentManager.EnsureAndApply(
                MM2GameSession.Location,
                MM2GameSession.TimeOfDay,
                MM2GameSession.Weather,
                showBanner: string.Equals(MM2GameSession.RaceType, "Cruise", StringComparison.OrdinalIgnoreCase)
            );

            // Snap chase camera directly behind the spawned vehicle so start line framing is immediate
            foreach (var chase in FindObjectsByType<ArcadeChaseCamera>(FindObjectsSortMode.None))
            {
                if (chase != null && chase.target != null)
                {
                    Vector3 baseOffset = chase.currentMode == ArcadeChaseCamera.CameraMode.ChaseFar
                        ? chase.farOffset
                        : chase.nearOffset;
                    chase.transform.position = chase.target.TransformPoint(baseOffset);
                    Vector3 lookTarget = chase.target.position + (chase.target.forward * 2.0f) + (Vector3.up * 1.0f);
                    chase.transform.rotation = Quaternion.LookRotation(lookTarget - chase.transform.position, Vector3.up);
                }
            }
        }

        private static void EnsureEscapeReturnListener()
        {
            if (FindFirstObjectByType<MM2InGameMenuReturn>() == null)
            {
                GameObject go = new GameObject("MM2_InGameMenuReturn");
                go.AddComponent<MM2InGameMenuReturn>();
            }
            MM2GameplayHud.EnsureExists();
        }

        private void Awake()
        {
            MM2GameSession.EnsureSettingsLoaded();
            MM2PlayerManager.EnsureInitialized();
            SyncSelectionsFromSession();
            LoadAllResources();
            SuspendSceneGameplayWhileInMenu();
            EnsureEventSystem();
            BuildMenuCanvasHierarchy();
            Build3DVehicleTurntablePreview();
            BuildLiveMenuBackgroundScene();
            ShowScreen(MenuScreen.MainMenu);
            ApplySavedDisplayOnStartup();
        }

        private void SyncSelectionsFromSession()
        {
            string[] loadedLocations = MM2RuntimeCityLoader.GetCityNames();
            if (loadedLocations != null && loadedLocations.Length > 0) locations = loadedLocations;

            string[] loadedVehicles = VehicleCatalog.VehicleNames;
            if (loadedVehicles != null && loadedVehicles.Length > 0) vehicles = loadedVehicles;

            int vIdx = Array.FindIndex(vehicles, v => string.Equals(v, MM2GameSession.VehicleName, StringComparison.OrdinalIgnoreCase));
            if (vIdx >= 0) idxVehicle = vIdx;
            else idxVehicle = 0;
            MM2GameSession.VehicleName = vehicles[idxVehicle];

            VehicleSpec spec = VehicleCatalog.GetSpec(MM2GameSession.VehicleName);
            vehicleColors = (spec != null && spec.Colors != null && spec.Colors.Length > 0) ? spec.Colors : new[] { "Default" };

            int cIdx = Array.FindIndex(vehicleColors, c => string.Equals(c, MM2GameSession.VehicleColor, StringComparison.OrdinalIgnoreCase));
            idxVehicleColor = (cIdx >= 0) ? cIdx : 0;
            MM2GameSession.VehicleColor = vehicleColors[idxVehicleColor];

            int lIdx = Array.FindIndex(locations, l => string.Equals(l, MM2GameSession.Location, StringComparison.OrdinalIgnoreCase));
            if (lIdx >= 0) idxLocation = lIdx;
            else idxLocation = 0;
            MM2GameSession.Location = locations[idxLocation];

            bool isCrashSession = MM2GameSession.IsCrashCourseSession;
            int rmIdx = Array.FindIndex(raceModes, m => string.Equals(m, MM2GameSession.RaceType, StringComparison.OrdinalIgnoreCase));
            idxRaceMode = (rmIdx >= 0) ? rmIdx : 0;
            if (!IsRaceModeAvailable(MM2GameSession.Location, idxRaceMode))
            {
                idxRaceMode = 0;
            }
            if (!isCrashSession)
            {
                MM2GameSession.RaceType = raceModes[idxRaceMode];
            }

            currentRaceNames = MM2RuntimeCityLoader.GetRaceNames(MM2GameSession.Location, raceModes[idxRaceMode]) ?? Array.Empty<string>();
            if (!isCrashSession)
            {
                if (currentRaceNames.Length > 0)
                {
                    idxRaceName = Mathf.Clamp(MM2GameSession.RaceIndex, 0, currentRaceNames.Length - 1);
                    if (!MM2PlayerManager.IsRaceUnlocked(MM2GameSession.Location, raceModes[idxRaceMode], idxRaceName))
                    {
                        idxRaceName = 0;
                    }
                    MM2GameSession.RaceIndex = idxRaceName;
                    MM2GameSession.RaceName = currentRaceNames[idxRaceName];
                }
                else
                {
                    idxRaceName = 0;
                    MM2GameSession.RaceIndex = 0;
                    MM2GameSession.RaceName = "";
                }
            }

            RefreshCruiseSpawnListForLocation(MM2GameSession.Location);

            int tIdx = Array.FindIndex(timesOfDay, t => string.Equals(t, MM2GameSession.TimeOfDay, StringComparison.OrdinalIgnoreCase));
            if (tIdx >= 0) idxTimeOfDay = tIdx;

            int wIdx = Array.FindIndex(weathers, w => string.Equals(w, MM2GameSession.Weather, StringComparison.OrdinalIgnoreCase));
            if (wIdx >= 0) idxWeather = wIdx;

            int trIdx = Array.FindIndex(transmissions, tr => string.Equals(tr, MM2GameSession.Transmission, StringComparison.OrdinalIgnoreCase));
            if (trIdx >= 0) idxTransmission = trIdx;
        }

        private void Update()
        {
            if (canvasRootRect == null)
            {
                Transform staleCanvas = transform.Find("MM2_MenuCanvas");
                if (staleCanvas != null)
                {
                    DestroyImmediate(staleCanvas.gameObject);
                }
                responsiveRects.Clear();
                lastResponsiveCanvasSize = Vector2.zero;
                MM2GameSession.EnsureSettingsLoaded();
                MM2PlayerManager.EnsureInitialized();
                SyncSelectionsFromSession();
                LoadAllResources();
                SuspendSceneGameplayWhileInMenu();
                EnsureEventSystem();
                BuildMenuCanvasHierarchy();
                if (showcaseRoot == null)
                {
                    Build3DVehicleTurntablePreview();
                }
                if (menuBgRigRoot == null)
                {
                    BuildLiveMenuBackgroundScene();
                }
                ShowScreen(currentScreen);
            }

            if (activeModalOverlay != null)
            {
                bool submitPressed = false;
                bool cancelPressed = false;
#if ENABLE_INPUT_SYSTEM
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                {
                    if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) submitPressed = true;
                    if (kb.escapeKey.wasPressedThisFrame) cancelPressed = true;
                }
#else
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) submitPressed = true;
                if (Input.GetKeyDown(KeyCode.Escape)) cancelPressed = true;
#endif
                if (activeCreateDriverInput != null)
                {
                    if (submitPressed)
                    {
                        ConfirmCreateNewDriverFromModal();
                    }
                    else if (cancelPressed)
                    {
                        CloseActiveModal();
                    }
                }
                else if (cancelPressed)
                {
                    CloseActiveModal();
                }
            }

            if (canvasRootRect != null)
            {
                Vector2 curSize = canvasRootRect.rect.size;
                if (curSize.x >= 100f && curSize.y >= 100f &&
                    (Mathf.Abs(curSize.x - lastResponsiveCanvasSize.x) > 0.5f ||
                     Mathf.Abs(curSize.y - lastResponsiveCanvasSize.y) > 0.5f))
                {
                    lastResponsiveCanvasSize = curSize;
                    ApplyResponsiveLayout(curSize.x, curSize.y);
                }
            }

            UpdateLiveMenuBackgroundCamera();

            if (showcaseCarPivot != null && currentScreen == MenuScreen.SelectVehicle)
            {
                showcaseCarPivot.Rotate(0f, 28f * Time.unscaledDeltaTime, 0f, Space.Self);
            }
        }

        private void OnDestroy()
        {
            RestoreDisabledSceneCameras();
            if (menuDisplayCamera != null)
            {
                Destroy(menuDisplayCamera.gameObject);
                menuDisplayCamera = null;
            }
            if (showcaseRenderTex != null)
            {
                showcaseRenderTex.Release();
                Destroy(showcaseRenderTex);
            }
            if (showcaseRoot != null)
            {
                Destroy(showcaseRoot);
            }
            CleanupLiveMenuBackground();
        }

        private void SuspendSceneGameplayWhileInMenu()
        {
            disabledSceneGameplayObjects.Clear();
            suspendedSceneCameras.Clear();
            foreach (var car in FindObjectsByType<ArcadeVehicleController>(FindObjectsSortMode.None))
            {
                if (car != null && car.gameObject.activeSelf)
                {
                    disabledSceneGameplayObjects.Add(car.gameObject);
                    car.gameObject.SetActive(false);
                }
            }
            foreach (var chase in FindObjectsByType<ArcadeChaseCamera>(FindObjectsSortMode.None))
            {
                if (chase != null && chase.enabled)
                {
                    chase.enabled = false;
                }
            }

            // Ensure a dedicated lightweight Display 1 camera is always active on the menu object
            // with a non-zero cullingMask (UI layer 5 only) so Unity/URP never displays "Display 1 - No cameras rendering"
            // while performing zero 3D scene draw calls.
            if (menuDisplayCamera == null)
            {
                Transform existingCamTr = transform.Find("MM2_MenuDisplay1Camera");
                GameObject camGo = existingCamTr != null ? existingCamTr.gameObject : new GameObject("MM2_MenuDisplay1Camera");
                camGo.transform.SetParent(transform, false);
                menuDisplayCamera = camGo.GetComponent<Camera>();
                if (menuDisplayCamera == null)
                {
                    menuDisplayCamera = camGo.AddComponent<Camera>();
                }
            }
            menuDisplayCamera.enabled = true;
            menuDisplayCamera.targetTexture = null;
            menuDisplayCamera.targetDisplay = 0;
            menuDisplayCamera.clearFlags = CameraClearFlags.SolidColor;
            menuDisplayCamera.backgroundColor = Color.black;
            menuDisplayCamera.cullingMask = 1 << 5; // UI layer only (non-zero mask, 0 3D scene meshes)
            menuDisplayCamera.nearClipPlane = 0.1f;
            menuDisplayCamera.farClipPlane = 1.0f;
            menuDisplayCamera.depth = -100f;
            menuDisplayCamera.allowHDR = false;
            menuDisplayCamera.allowMSAA = false;

            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (cam != null && cam != menuBgCamera && cam != showcaseCamera && cam != menuDisplayCamera && cam.targetTexture == null)
                {
                    int origMask = (cam.cullingMask != 0 && cam.cullingMask != (1 << 5)) ? cam.cullingMask : ~0;
                    CameraClearFlags origFlags = (cam.cullingMask != 0 && cam.cullingMask != (1 << 5)) ? cam.clearFlags : CameraClearFlags.Skybox;
                    Color origBg = cam.backgroundColor;
                    suspendedSceneCameras.Add(new SuspendedCameraState
                    {
                        Cam = cam,
                        CullingMask = origMask,
                        ClearFlags = origFlags,
                        BackgroundColor = origBg
                    });
                    cam.enabled = true;
                    cam.cullingMask = 1 << 5;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = Color.black;
                }
            }
        }

        private void RestoreDisabledSceneCameras()
        {
            if (menuDisplayCamera != null)
            {
                menuDisplayCamera.enabled = false;
            }
            foreach (var state in suspendedSceneCameras)
            {
                if (state.Cam != null)
                {
                    state.Cam.enabled = true;
                    state.Cam.cullingMask = state.CullingMask;
                    state.Cam.clearFlags = state.ClearFlags;
                    state.Cam.backgroundColor = state.BackgroundColor;
                }
            }
            suspendedSceneCameras.Clear();
        }

        private void LoadAllResources()
        {
            fontHeaderItalic = LoadFontResource(
                "Fonts/OpenSans-ExtraBoldItalic",
                new[] { "Open Sans ExtraBold", "DejaVu Sans", "Arial" }
            );
            fontCondensedExtraBold = LoadFontResource(
                "Fonts/OpenSans-CondensedExtraBold",
                new[] { "Open Sans Condensed ExtraBold", "Open Sans Condensed", "DejaVu Sans Condensed", "Arial" }
            );
            fontUIExtraBold = LoadFontResource(
                "Fonts/OpenSans-ExtraBold",
                new[] { "Open Sans ExtraBold", "Open Sans Bold", "DejaVu Sans", "Arial" }
            );
            fontUIBold = LoadFontResource(
                "Fonts/OpenSans-Bold",
                new[] { "Open Sans Bold", "DejaVu Sans", "Arial" }
            );

            spOrangeBase = LoadSprite("UI/MM2/bg_orange_base");
            spBlueCollage = LoadSprite("UI/MM2/bg_blue_collage");
            spBoxBlack9 = LoadSprite9Slice("UI/MM2/box_recessed_black", 12);
            spBoxNavy9 = LoadSprite9Slice("UI/MM2/box_recessed_navy", 12);
            spFrameTrans9 = LoadSprite9Slice("UI/MM2/frame_recessed_transparent", 12);
            spFrameGlass9 = LoadSprite9Slice("UI/MM2/frame_raised_glass", 12);
            texGridBlue = Resources.Load<Texture2D>("UI/MM2/grid_blue_tile");
            if (texGridBlue != null)
            {
                texGridBlue.wrapMode = TextureWrapMode.Repeat;
            }

            spBarVert = LoadSprite("UI/MM2/bar_vertical_chrome");
            spBarHoriz = LoadSprite("UI/MM2/bar_horizontal_header");
            spVehicleSpot = LoadSprite("UI/MM2/bg_vehicle_showcase_spot");

            spBtnGlowRect = LoadSprite9Slice("UI/MM2/btn_glow_rect", 16);
            spBtnGlowRectHover = LoadSprite9Slice("UI/MM2/btn_glow_rect_hover", 16);

            spBtnArrowRight = LoadSprite("UI/MM2/btn_arrow_right_wide");
            spBtnArrowRightHover = LoadSprite("UI/MM2/btn_arrow_right_wide_hover");
            spBtnArrowLeft = LoadSprite("UI/MM2/btn_arrow_left_back");
            spBtnArrowLeftHover = LoadSprite("UI/MM2/btn_arrow_left_back_hover");

            spChevronUp = LoadSprite("UI/MM2/btn_chevron_up");
            spChevronDown = LoadSprite("UI/MM2/btn_chevron_down");
            spChevronLeft = LoadSprite("UI/MM2/btn_chevron_left");
            spChevronRight = LoadSprite("UI/MM2/btn_chevron_right");

            spOrbGreen = LoadSprite("UI/MM2/orb_green");
            spOrbRed = LoadSprite("UI/MM2/orb_red");
            spOrbBlue = LoadSprite("UI/MM2/orb_blue");
            spIconDropdownV = LoadSprite("UI/MM2/icon_dropdown_v");

            spLogoMM2 = LoadSprite("UI/MM2/logo");

            spCheckboxUnchecked = BuildHighResCheckboxSprite(mode: 0);
            spCheckboxCheckedGreen = BuildHighResCheckboxSprite(mode: 1);
            spCheckboxCheckedBlue = BuildHighResCheckboxSprite(mode: 2);

            spCcSmChk = BuildHighResStandaloneIconSprite(isCheckTick: true);
            spCcSmFail = BuildHighResStandaloneIconSprite(isCheckTick: false);
            spCcPass = LoadArchiveUiSprite("cc_pass");
            spCcFail = LoadArchiveUiSprite("cc_fail");
        }

        private static Sprite LoadArchiveUiSprite(string texName)
        {
            Texture2D tex = MM2RuntimeDecoders.LoadUiTexture(texName);
            if (tex == null) return null;
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax;
            float vy = by - ay;
            float wx = px - ax;
            float wy = py - ay;
            float c1 = vx * wx + vy * wy;
            if (c1 <= 0f) return Mathf.Sqrt(wx * wx + wy * wy);
            float c2 = vx * vx + vy * vy;
            if (c2 <= c1)
            {
                float dx = px - bx;
                float dy = py - by;
                return Mathf.Sqrt(dx * dx + dy * dy);
            }
            float b = c1 / c2;
            float pbx = ax + b * vx;
            float pby = ay + b * vy;
            float rx = px - pbx;
            float ry = py - pby;
            return Mathf.Sqrt(rx * rx + ry * ry);
        }

        private static float SmoothStep01(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(0.0001f, edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Synthesizes a crisp 128x128 high-DPI MM2 beveled navy checkbox sprite with custom box-filtered mipmaps
        /// (mode 0 = unchecked, mode 1 = checked with glossy vibrant green tick, mode 2 = checked with electric blue/cyan tick).
        /// </summary>
        private static Sprite BuildHighResCheckboxSprite(int mode)
        {
            const int size = 128;
            Color32[] pixels = new Color32[size * size];
            float inv = 1f / (size - 1f);
            float aaPx = 1.35f / size;

            for (int y = 0; y < size; y++)
            {
                // Top-down normalized Y (0 = top, 1 = bottom)
                float ny = 1f - (y * inv);
                for (int x = 0; x < size; x++)
                {
                    float nx = x * inv;
                    float margin = Mathf.Max(Mathf.Abs(nx - 0.5f), Mathf.Abs(ny - 0.5f)) * 2f;

                    float r, g, b, a;
                    if (margin > 0.96f)
                    {
                        float aa = Mathf.Clamp01((1f - margin) * (size * 0.5f));
                        r = 6f;
                        g = 8f;
                        b = 34f;
                        a = 255f * aa;
                    }
                    else if (margin > 0.78f)
                    {
                        float bevelT = Mathf.Clamp01(((1f - nx) + (1f - ny)) * 0.5f);
                        float frameU = (margin - 0.78f) / (0.96f - 0.78f);
                        float ridge = Mathf.Clamp01(1f - Mathf.Abs(frameU - 0.45f) * 1.4f);
                        r = 58f + 78f * bevelT + 36f * ridge;
                        g = 78f + 92f * bevelT + 42f * ridge;
                        b = 195f + 48f * bevelT + 12f * ridge;
                        a = 255f;
                    }
                    else if (margin > 0.72f)
                    {
                        float bevelT = Mathf.Clamp01((nx + ny) * 0.5f);
                        r = 12f + 24f * bevelT;
                        g = 18f + 32f * bevelT;
                        b = 58f + 75f * bevelT;
                        a = 255f;
                    }
                    else
                    {
                        r = 4f + 8f * ny;
                        g = 7f + 14f * ny;
                        b = 38f + 36f * ny;
                        a = 255f;
                    }

                    if (mode == 1 || mode == 2)
                    {
                        float d1 = DistanceToSegment(nx, ny, 0.23f, 0.54f, 0.43f, 0.73f);
                        float d2 = DistanceToSegment(nx, ny, 0.43f, 0.73f, 0.78f, 0.26f);
                        float dTick = Mathf.Min(d1, d2);

                        float sd1 = DistanceToSegment(nx - 0.025f, ny - 0.03f, 0.23f, 0.54f, 0.43f, 0.73f);
                        float sd2 = DistanceToSegment(nx - 0.025f, ny - 0.03f, 0.43f, 0.73f, 0.78f, 0.26f);
                        float dShadow = Mathf.Min(sd1, sd2);

                        const float strokeR = 0.074f;
                        const float outlineR = strokeR + 0.024f;

                        float shAlpha = (1f - SmoothStep01(outlineR - aaPx, outlineR + aaPx * 3f, dShadow)) * 0.65f;
                        if (shAlpha > 0f)
                        {
                            r *= (1f - shAlpha);
                            g *= (1f - shAlpha);
                            b *= (1f - shAlpha);
                        }

                        float olAlpha = 1f - SmoothStep01(outlineR - aaPx, outlineR + aaPx, dTick);
                        if (olAlpha > 0f)
                        {
                            float orC = mode == 1 ? 4f : 4f;
                            float ogC = mode == 1 ? 24f : 14f;
                            float obC = mode == 1 ? 10f : 38f;
                            r = Mathf.Lerp(r, orC, olAlpha);
                            g = Mathf.Lerp(g, ogC, olAlpha);
                            b = Mathf.Lerp(b, obC, olAlpha);
                        }

                        float fillAlpha = 1f - SmoothStep01(strokeR - aaPx, strokeR + aaPx, dTick);
                        if (fillAlpha > 0f)
                        {
                            float ridge = Mathf.Clamp01(1f - (dTick / strokeR));
                            float gloss = Mathf.Clamp01((0.75f - ny) * 0.8f + ridge * 0.5f);
                            float cr, cg, cb;
                            if (mode == 1)
                            {
                                cr = 24f + 85f * gloss;
                                cg = 215f + 40f * gloss;
                                cb = 52f + 75f * gloss;
                            }
                            else
                            {
                                cr = 65f + 115f * gloss;
                                cg = 155f + 85f * gloss;
                                cb = 245f + 10f * gloss;
                            }
                            r = Mathf.Lerp(r, cr, fillAlpha);
                            g = Mathf.Lerp(g, cg, fillAlpha);
                            b = Mathf.Lerp(b, cb, fillAlpha);
                        }
                    }

                    pixels[y * size + x] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(r), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(g), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(b), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(a), 0, 255)
                    );
                }
            }

            Texture2D tex = CreateCrispMipmappedUiTexture($"MM2_HighResCheckbox_{mode}", size, pixels);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// Synthesizes a crisp 128x128 standalone green checkmark or red X sprite for Crash Course & Driver Records rows.
        /// </summary>
        private static Sprite BuildHighResStandaloneIconSprite(bool isCheckTick)
        {
            const int size = 128;
            Color32[] pixels = new Color32[size * size];
            float inv = 1f / (size - 1f);
            float aaPx = 1.4f / size;

            for (int y = 0; y < size; y++)
            {
                float ny = 1f - (y * inv);
                for (int x = 0; x < size; x++)
                {
                    float nx = x * inv;
                    float dShape, dShadow;
                    if (isCheckTick)
                    {
                        float d1 = DistanceToSegment(nx, ny, 0.16f, 0.54f, 0.41f, 0.78f);
                        float d2 = DistanceToSegment(nx, ny, 0.41f, 0.78f, 0.85f, 0.20f);
                        dShape = Mathf.Min(d1, d2);

                        float sd1 = DistanceToSegment(nx - 0.03f, ny - 0.035f, 0.16f, 0.54f, 0.41f, 0.78f);
                        float sd2 = DistanceToSegment(nx - 0.03f, ny - 0.035f, 0.41f, 0.78f, 0.85f, 0.20f);
                        dShadow = Mathf.Min(sd1, sd2);
                    }
                    else
                    {
                        float d1 = DistanceToSegment(nx, ny, 0.22f, 0.22f, 0.78f, 0.78f);
                        float d2 = DistanceToSegment(nx, ny, 0.78f, 0.22f, 0.22f, 0.78f);
                        dShape = Mathf.Min(d1, d2);

                        float sd1 = DistanceToSegment(nx - 0.03f, ny - 0.035f, 0.22f, 0.22f, 0.78f, 0.78f);
                        float sd2 = DistanceToSegment(nx - 0.03f, ny - 0.035f, 0.78f, 0.22f, 0.22f, 0.78f);
                        dShadow = Mathf.Min(sd1, sd2);
                    }

                    const float strokeR = 0.095f;
                    const float outlineR = strokeR + 0.032f;

                    float shAlpha = (1f - SmoothStep01(outlineR - aaPx, outlineR + aaPx * 2.5f, dShadow)) * 0.55f;
                    float olAlpha = 1f - SmoothStep01(outlineR - aaPx, outlineR + aaPx, dShape);
                    float fillAlpha = 1f - SmoothStep01(strokeR - aaPx, strokeR + aaPx, dShape);

                    float outA = Mathf.Clamp01(shAlpha + olAlpha * (1f - shAlpha));
                    if (outA <= 0.001f)
                    {
                        pixels[y * size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    float r = isCheckTick ? 4f : 28f;
                    float g = isCheckTick ? 24f : 4f;
                    float b = isCheckTick ? 8f : 4f;

                    if (fillAlpha > 0f)
                    {
                        float ridge = Mathf.Clamp01(1f - (dShape / strokeR));
                        float gloss = Mathf.Clamp01((0.75f - ny) * 0.8f + ridge * 0.5f);
                        float cr = isCheckTick ? (26f + 85f * gloss) : (225f + 30f * gloss);
                        float cg = isCheckTick ? (220f + 35f * gloss) : (32f + 70f * gloss);
                        float cb = isCheckTick ? (54f + 75f * gloss) : (32f + 65f * gloss);
                        r = Mathf.Lerp(r, cr, fillAlpha);
                        g = Mathf.Lerp(g, cg, fillAlpha);
                        b = Mathf.Lerp(b, cb, fillAlpha);
                    }

                    pixels[y * size + x] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(r), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(g), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(b), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(outA * 255f), 0, 255)
                    );
                }
            }

            Texture2D tex = CreateCrispMipmappedUiTexture(isCheckTick ? "MM2_HighResTick" : "MM2_HighResCross", size, pixels);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Texture2D CreateCrispMipmappedUiTexture(string name, int size, Color32[] mip0)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                mipMapBias = -0.65f
            };
            tex.SetPixels32(mip0, 0);

            // Generate premultiplied-alpha-aware box-filtered mipmaps so downscaling stays razor-sharp with no dark fringes
            Color32[] prev = mip0;
            int prevSize = size;
            int mipLevel = 1;
            while (prevSize > 1)
            {
                int nextSize = prevSize >> 1;
                Color32[] next = new Color32[nextSize * nextSize];
                for (int y = 0; y < nextSize; y++)
                {
                    int sy0 = y * 2;
                    int sy1 = sy0 + 1;
                    for (int x = 0; x < nextSize; x++)
                    {
                        int sx0 = x * 2;
                        int sx1 = sx0 + 1;
                        Color32 c00 = prev[sy0 * prevSize + sx0];
                        Color32 c10 = prev[sy0 * prevSize + sx1];
                        Color32 c01 = prev[sy1 * prevSize + sx0];
                        Color32 c11 = prev[sy1 * prevSize + sx1];

                        int aSum = c00.a + c10.a + c01.a + c11.a;
                        if (aSum == 0)
                        {
                            next[y * nextSize + x] = new Color32(0, 0, 0, 0);
                        }
                        else
                        {
                            int rPremul = c00.r * c00.a + c10.r * c10.a + c01.r * c01.a + c11.r * c11.a;
                            int gPremul = c00.g * c00.a + c10.g * c10.a + c01.g * c01.a + c11.g * c11.a;
                            int bPremul = c00.b * c00.a + c10.b * c10.a + c01.b * c01.a + c11.b * c11.a;
                            next[y * nextSize + x] = new Color32(
                                (byte)Mathf.Clamp((rPremul + (aSum >> 1)) / aSum, 0, 255),
                                (byte)Mathf.Clamp((gPremul + (aSum >> 1)) / aSum, 0, 255),
                                (byte)Mathf.Clamp((bPremul + (aSum >> 1)) / aSum, 0, 255),
                                (byte)((aSum + 2) >> 2)
                            );
                        }
                    }
                }
                tex.SetPixels32(next, mipLevel);
                prev = next;
                prevSize = nextSize;
                mipLevel++;
            }

            tex.Apply(false, false);
            return tex;
        }

        private static Font LoadFontResource(string resourcePath, string[] osFallbackNames)
        {
            Font f = Resources.Load<Font>(resourcePath);
            if (f != null) return f;
            f = Font.CreateDynamicFontFromOSFont(osFallbackNames, 64);
            if (f != null) return f;
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        private static Sprite LoadSprite(string path)
        {
            Texture2D tex = Resources.Load<Texture2D>(path);
            if (tex == null) return null;
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite LoadSprite9Slice(string path, float borderPx)
        {
            Texture2D tex = Resources.Load<Texture2D>(path);
            if (tex == null) return null;
            return Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(borderPx, borderPx, borderPx, borderPx)
            );
        }

        private void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            GameObject esGo = new GameObject("EventSystem");
            esGo.transform.SetParent(transform, false);
            esGo.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            esGo.AddComponent<InputSystemUIInputModule>();
#else
            esGo.AddComponent<StandaloneInputModule>();
#endif
        }

        // =====================================================================
        // MASTER CANVAS & SHARED CHROME (1920x1080 Reference, 720p to 8K Ready)
        // =====================================================================
        private void BuildMenuCanvasHierarchy()
        {
            GameObject canvasGo = new GameObject("MM2_MenuCanvas");
            canvasGo.transform.SetParent(transform, false);

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            canvasGo.AddComponent<GraphicRaycaster>();
            canvasRootRect = canvasGo.GetComponent<RectTransform>();
            RectTransform rootRect = canvasRootRect;

            // 1. Full-Bleed Orange Base Layer (Static gradient fallback + Live 3D Duotone view)
            // Stretches edge-to-edge across the entire screen on all aspect ratios (16:9, 16:10, 4:3, 21:9) with no black bars.
            GameObject orangeBgGo = CreateUIObject("Layer1_OrangeBase", rootRect);
            StretchFull(orangeBgGo.GetComponent<RectTransform>());
            Image imgOrange = orangeBgGo.AddComponent<Image>();
            imgOrange.sprite = spOrangeBase;
            imgOrange.preserveAspect = false;

            GameObject liveBgGo = CreateUIObject("Layer1_Live3DBackground", orangeBgGo.transform);
            StretchFull(liveBgGo.GetComponent<RectTransform>());
            rawLiveMenuBg = liveBgGo.AddComponent<RawImage>();
            rawLiveMenuBg.raycastTarget = false;
            rawLiveMenuBg.gameObject.SetActive(false);

            // 2. Masked Blue Collage Strips (Far-Left Strip x:0..60 & Top Header Strip x:0..830, y:0..102)
            GameObject chromeBlueStrips = CreateUIObject("SharedChrome_BlueStrips", rootRect);
            StretchFull(chromeBlueStrips.GetComponent<RectTransform>());
            CreateMaskedBlueRegion("BlueStrip_FarLeft", chromeBlueStrips.transform, 0f, 0f, 60f, 945f);
            CreateMaskedBlueRegion("BlueStrip_TopHeader", chromeBlueStrips.transform, 0f, 0f, 830f, 102f);
            // Event Results extends the top blue header strip up to its vertical divider at x=1256 (placed here so it stays behind Bar_HorizMain & Divider_TopHoriz)
            blueStripEventResultsTopExt = CreateMaskedBlueRegion("BlueStrip_EventResultsTopExt", chromeBlueStrips.transform, 830f, 0f, 426f, 102f);

            // 3. Left Panel Mode Decor (Switches between Full Orange vs Top-Orange/Bottom-Blue)
            leftFullOrangeDecor = CreateUIObject("LeftDecor_FullOrange", rootRect);
            StretchFull(leftFullOrangeDecor.GetComponent<RectTransform>());
            CreateSlicedBox("LeftGlass_Full", leftFullOrangeDecor.transform, spFrameGlass9, 94f, 102f, 736f, 758f);

            leftSplitRacesDecor = CreateUIObject("LeftDecor_SplitRaces", rootRect);
            StretchFull(leftSplitRacesDecor.GetComponent<RectTransform>());
            // Top-left orange raised glass frame (for CRUISE / BLITZ / CHECKPOINT / CIRCUIT)
            CreateSlicedBox("LeftGlass_TopOrange", leftSplitRacesDecor.transform, spFrameGlass9, 124f, 102f, 706f, 328f);
            // Bottom-left blue collage window + raised glass frame
            CreateMaskedBlueRegion("LeftBlue_BottomWindow", leftSplitRacesDecor.transform, 124f, 430f, 706f, 430f);
            CreateSlicedBox("LeftGlass_BottomBlue", leftSplitRacesDecor.transform, spFrameGlass9, 124f, 430f, 706f, 430f);
            // Horizontal divider line at y=430
            CreateSolidRect("Divider_MidHoriz", leftSplitRacesDecor.transform, 124f, 428f, 1796f, 4f, new Color32(8, 8, 52, 255));

            // 4. Structural Navy Lines & Chrome Bars
            GameObject chromeBarsGroup = CreateUIObject("SharedChrome_BarsAndDividers", rootRect);
            StretchFull(chromeBarsGroup.GetComponent<RectTransform>());
            CreateSolidRect("Divider_TopHoriz", chromeBarsGroup.transform, 94f, 100f, 1826f, 4f, new Color32(8, 8, 52, 255));
            dividerMidVert = CreateSolidRect("Divider_MidVert", chromeBarsGroup.transform, 828f, 0f, 4f, 860f, new Color32(8, 8, 52, 255));

            // Top Horizontal Silver-to-Navy Header Bar (stops flush at the solid left border of Btn_TopOptions at x=1457)
            CreateSpriteImage("Bar_HorizLeftStub", chromeBarsGroup.transform, spBarHoriz, 0f, 36f, 60f, 34f);
            CreateSpriteImage("Bar_HorizMain", chromeBarsGroup.transform, spBarHoriz, 94f, 36f, 1363f, 34f);

            // Left Vertical Silver-to-Navy Chrome Bar
            CreateSpriteImage("Bar_VertChrome", chromeBarsGroup.transform, spBarVert, 60f, 0f, 34f, 1080f);

            // 5. Top-Right Persistent Navigation Bar: [ OPTIONS ] [ ? ] [ X ]
            topRightNavBarGroup = CreateUIObject("TopRight_NavBarGroup", rootRect);
            StretchFull(topRightNavBarGroup.GetComponent<RectTransform>());
            BuildTopRightNavigationBar(topRightNavBarGroup.transform);

            // 6. Top-Left Header Logo (Main Menu) & Dynamic Screen Title ("RACES" / "SELECT VEHICLE" / "EVENT RESULTS" / "OPTIONS")
            GameObject chromeHeaderTitles = CreateUIObject("SharedChrome_HeaderTitle", rootRect);
            StretchFull(chromeHeaderTitles.GetComponent<RectTransform>());
            imgHeaderLogo = CreateSpriteImage("Header_LogoMM2", chromeHeaderTitles.transform, spLogoMM2, 92f, 8f, 690f, 265f);
            imgHeaderLogo.raycastTarget = false;

            txtHeaderTitle = CreateHeaderTitleText("Header_ScreenTitleText", chromeHeaderTitles.transform, "RACES", 26f, 2f, 1050f, 96f);

            // 7. Build the Individual Screen Containers
            panelMainMenu = CreateUIObject("Screen_MainMenu", rootRect);
            StretchFull(panelMainMenu.GetComponent<RectTransform>());
            BuildMainMenuScreen(panelMainMenu.transform);

            panelRaces = CreateUIObject("Screen_Races", rootRect);
            StretchFull(panelRaces.GetComponent<RectTransform>());
            BuildRacesScreen(panelRaces.transform);

            panelSelectVehicle = CreateUIObject("Screen_SelectVehicle", rootRect);
            StretchFull(panelSelectVehicle.GetComponent<RectTransform>());
            BuildSelectVehicleScreen(panelSelectVehicle.transform);

            panelEventResults = CreateUIObject("Screen_EventResults", rootRect);
            StretchFull(panelEventResults.GetComponent<RectTransform>());
            BuildEventResultsScreen(panelEventResults.transform);

            panelCrashCourseIntro = CreateUIObject("Screen_CrashCourseIntro", rootRect);
            StretchFull(panelCrashCourseIntro.GetComponent<RectTransform>());
            BuildCrashCourseIntroScreen(panelCrashCourseIntro.transform);

            panelCrashCourseHub = CreateUIObject("Screen_CrashCourseHub", rootRect);
            StretchFull(panelCrashCourseHub.GetComponent<RectTransform>());
            BuildCrashCourseHubScreen(panelCrashCourseHub.transform);

            panelOptions = CreateUIObject("Screen_Options", rootRect);
            StretchFull(panelOptions.GetComponent<RectTransform>());
            BuildOptionsScreen(panelOptions.transform);

            panelHelp = CreateUIObject("Screen_Help", rootRect);
            StretchFull(panelHelp.GetComponent<RectTransform>());
            BuildHelpScreen(panelHelp.transform);

            Canvas.ForceUpdateCanvases();
            if (canvasRootRect != null && canvasRootRect.rect.width >= 100f)
            {
                lastResponsiveCanvasSize = canvasRootRect.rect.size;
                ApplyResponsiveLayout(lastResponsiveCanvasSize.x, lastResponsiveCanvasSize.y);
            }
        }

        private void BuildTopRightNavigationBar(Transform parent)
        {
            // Top-right persistent bar: [ OPTIONS ] [ ? ] [ X ]
            // Vertically centered on Bar_HorizMain (y=36, h=34 -> center y=53; y=18, h=70 -> center y=53)
            CreateGlowButton("Btn_TopOptions", parent, "OPTIONS", 1445f, 18f, 265f, 70f, 28, () =>
            {
                CloseActiveDropdown();
                CloseActiveModal();
                if (currentScreen != MenuScreen.Options)
                {
                    if (currentScreen != MenuScreen.Help)
                    {
                        previousScreenBeforeOptions = currentScreen;
                    }
                    ShowScreen(MenuScreen.Options);
                }
                else
                {
                    ShowScreen(previousScreenBeforeOptions);
                }
            });
            CreateGlowButton("Btn_TopHelp", parent, "?", 1706f, 18f, 84f, 70f, 34, () =>
            {
                CloseActiveDropdown();
                CloseActiveModal();
                if (currentScreen != MenuScreen.Help)
                {
                    if (currentScreen != MenuScreen.Options)
                    {
                        previousScreenBeforeOptions = currentScreen;
                    }
                    ShowScreen(MenuScreen.Help);
                }
                else
                {
                    ShowScreen(previousScreenBeforeOptions);
                }
            });
            CreateGlowButton("Btn_TopExit", parent, "X", 1786f, 18f, 84f, 70f, 32, () =>
            {
                Debug.Log("[MM2 Menu] Exit (X) clicked.");
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            });
        }

        // =====================================================================
        // SCREEN 1: MAIN MENU (SELECT DRIVER + RIGHT ARROW BUTTONS)
        // =====================================================================
        private void BuildMainMenuScreen(Transform parent)
        {
            // "SELECT DRIVER" label + DriverX box + Up/Down chevron buttons
            CreateShadowLabel("Lbl_SelectDriver", parent, "SELECT DRIVER", 135f, 282f, 380f, 56f, 40, TextAnchor.MiddleLeft, Color.white);

            txtDriverSelectorValue = CreateSelectorBoxWithSteppers(
                parent,
                "DriverSelector",
                525f,
                278f,
                640f,
                64f,
                MM2GameSession.DriverName,
                onUp: () => CycleDriverFromMenu(-1),
                onDown: () => CycleDriverFromMenu(1),
                options: MM2PlayerManager.GetPlayerNames(),
                getCurrentIndex: () => MM2PlayerManager.GetActivePlayerIndex(),
                onSelectIndex: idx => SelectDriverFromMenu(idx),
                getOptionsDynamic: () => MM2PlayerManager.GetPlayerNames()
            );

            // Left column driver buttons: CREATE NEW DRIVER, DELETE THIS DRIVER, DRIVER'S STATS
            CreateGlowButton("Btn_CreateDriver", parent, "CREATE\nNEW\nDRIVER", 155f, 365f, 355f, 130f, 25, () =>
            {
                CloseActiveDropdown();
                ShowCreateDriverModal();
            });
            CreateGlowButton("Btn_DeleteDriver", parent, "DELETE\nTHIS\nDRIVER", 155f, 505f, 355f, 130f, 25, () =>
            {
                CloseActiveDropdown();
                ShowDeleteDriverConfirmModal();
            });
            CreateGlowButton("Btn_DriverStats", parent, "DRIVER'S\nSTATS", 155f, 645f, 355f, 130f, 25, () =>
            {
                CloseActiveDropdown();
                ShowDriverStatsModal();
            });

            // Center Blue Wireframe Grid Box with Driver Info
            CreateTiledGridBox("Grid_DriverInfo", parent, 525f, 365f, 640f, 470f, 14f, 8f);
            txtMainMenuDriverStats = CreateShadowLabel(
                "Txt_DriverStats",
                parent,
                MM2PlayerManager.GetDriverOverviewText(),
                545f,
                380f,
                600f,
                440f,
                30,
                TextAnchor.UpperLeft,
                new Color32(255, 240, 20, 255)
            );

            // Bottom-Left Recessed Black Status Box
            GameObject helpBox = CreateSlicedBox("Box_BottomLeftHelp", parent, spBoxBlack9, 108f, 882f, 695f, 175f);
            txtMainMenuBottomHelp = CreateShadowLabel(
                "Txt_MainMenuHelp",
                helpBox.transform,
                "Select your driver profile above or choose a race mode on the right to hit the streets!",
                22f,
                16f,
                650f,
                145f,
                25,
                TextAnchor.UpperLeft,
                new Color32(255, 240, 20, 255)
            );

            // Bottom-Center "RACE RECORDS" Button
            CreateGlowButton("Btn_RaceRecords", parent, "RACE\nRECORDS", 820f, 882f, 355f, 120f, 26, () =>
            {
                CloseActiveDropdown();
                ShowRaceRecordsModal();
            });

            // Right-Side Iconic Arrow Buttons: CRASH COURSE, RACES, MULTIPLAYER, QUICK RACE
            btnCrashCourse = CreateRightArrowButton("Btn_CrashCourse", parent, "CRASH COURSE", 1345f, 540f, 540f, 122f, 28, () =>
            {
                if (MM2RuntimeCityLoader.GetCrashCourseCities().Count > 0)
                {
                    ShowScreen(MenuScreen.CrashCourseIntro);
                }
            });
            btnCrashCourse.gameObject.SetActive(MM2RuntimeCityLoader.GetCrashCourseCities().Count > 0);
            CreateRightArrowButton("Btn_Races", parent, "RACES", 1345f, 668f, 540f, 122f, 28, () =>
            {
                MM2GameSession.IsCrashCourseSession = false;
                if (string.Equals(MM2GameSession.RaceType, "CrashCourse", StringComparison.OrdinalIgnoreCase))
                {
                    MM2GameSession.RaceType = raceModes[idxRaceMode];
                }
                ShowScreen(MenuScreen.Races);
            });
            Button btnMultiplayer = CreateRightArrowButton("Btn_Multiplayer", parent, "MULTIPLAYER", 1345f, 796f, 540f, 122f, 28, () =>
            {
                Debug.Log("[MM2 Menu] Multiplayer clicked (placeholder).");
            });
            btnMultiplayer.gameObject.SetActive(EnableMultiplayerButton);
            CreateRightArrowButton("Btn_QuickRace", parent, "QUICK RACE", 1345f, 924f, 540f, 122f, 28, OnQuickRaceClicked);

            RefreshMainMenuDriverUI();
        }

        private void CycleDriverFromMenu(int dir)
        {
            string[] names = MM2PlayerManager.GetPlayerNames();
            if (names == null || names.Length == 0) return;
            int nextIdx = (MM2PlayerManager.GetActivePlayerIndex() + dir + names.Length) % names.Length;
            SelectDriverFromMenu(nextIdx);
        }

        private void SelectDriverFromMenu(int index)
        {
            MM2PlayerManager.SelectPlayer(index, syncGameSession: true, saveDirectory: true);
            mainMenuStatsViewMode = 0;
            ApplySelectedDriverToMenuUI();
        }

        private void ApplySelectedDriverToMenuUI()
        {
            SyncSelectionsFromSession();

            // Refresh Location & Race Mode UI controls on Races screen
            if (txtLocationValue != null) txtLocationValue.text = MM2GameSession.Location;
            UpdateRaceModeVisualsAndControls(applyRaceMetadata: idxRaceMode != 0);

            // Refresh Vehicle & Paintjob UI controls on Select Vehicle screen
            if (txtVehicleValue != null) txtVehicleValue.text = MM2GameSession.VehicleName;
            if (txtColorValue != null) txtColorValue.text = MM2GameSession.VehicleColor;
            VehicleSpec spec = VehicleCatalog.GetSpec(MM2GameSession.VehicleName);
            if (spec != null)
            {
                float maxW = 408f;
                if (scoreBarHorsepower != null) scoreBarHorsepower.sizeDelta = new Vector2(Mathf.Clamp01(spec.HorsepowerScore) * maxW, 22f);
                if (scoreBarTopSpeed != null) scoreBarTopSpeed.sizeDelta = new Vector2(Mathf.Clamp01(spec.TopSpeedScore) * maxW, 22f);
                if (scoreBarDurability != null) scoreBarDurability.sizeDelta = new Vector2(Mathf.Clamp01(spec.DurabilityScore) * maxW, 22f);
                if (scoreBarMass != null) scoreBarMass.sizeDelta = new Vector2(Mathf.Clamp01(spec.MassScore) * maxW, 22f);
            }
            RefreshVehicleLockUI();
            RebuildTurntableCarModel();

            RefreshMainMenuDriverUI();
        }

        private void RefreshMainMenuDriverUI()
        {
            if (btnCrashCourse != null)
            {
                btnCrashCourse.gameObject.SetActive(MM2RuntimeCityLoader.GetCrashCourseCities().Count > 0);
            }
            if (txtDriverSelectorValue != null)
            {
                txtDriverSelectorValue.text = MM2GameSession.DriverName;
            }
            if (txtMainMenuDriverStats == null) return;

            if (mainMenuStatsViewMode == 1)
            {
                int cityCount = MM2PlayerManager.GetAllCityCodes().Count;
                txtMainMenuDriverStats.fontSize = cityCount >= 3 ? 18 : 22; // Scale font cleanly when 3+ cities are installed
                txtMainMenuDriverStats.text = MM2PlayerManager.GetDriverDetailedStatsText();
                if (txtMainMenuBottomHelp != null)
                {
                    txtMainMenuBottomHelp.text =
                        $"Viewing {MM2GameSession.DriverName}'s overall progress.\n" +
                        "Click DRIVER'S STATS again to return to overview.";
                }
            }
            else if (mainMenuStatsViewMode == 2)
            {
                string[] recModes = { "Blitz", "Checkpoint", "Circuit", "CrashCourse" };
                var cities = MM2PlayerManager.GetAllCityCodes();
                int cityIdx = cities.Count > 0 ? (mainMenuRecordsModeIndex / 4) % cities.Count : 0;
                int modeIdx = mainMenuRecordsModeIndex % 4;
                string cityCode = cities.Count > 0 ? cities[cityIdx] : MM2GameSession.SelectedCityCode;
                CitySpec cSpec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
                string cityDisplay = (cSpec != null && !string.IsNullOrEmpty(cSpec.DisplayName)) ? cSpec.DisplayName : cityCode;
                string modeName = recModes[Mathf.Clamp(modeIdx, 0, recModes.Length - 1)];

                txtMainMenuDriverStats.fontSize = 20;
                txtMainMenuDriverStats.text = MM2PlayerManager.GetDriverRaceRecordsSummaryText(
                    cityCode,
                    modeName
                );
                if (txtMainMenuBottomHelp != null)
                {
                    txtMainMenuBottomHelp.text =
                        $"Viewing {cityDisplay} {modeName} personal bests.\n" +
                        "Click RACE RECORDS again to cycle modes & cities.";
                }
            }
            else
            {
                txtMainMenuDriverStats.fontSize = 30;
                txtMainMenuDriverStats.text = MM2PlayerManager.GetDriverOverviewText();
                if (txtMainMenuBottomHelp != null)
                {
                    txtMainMenuBottomHelp.text =
                        "Select your driver profile above or choose a race mode on the right to hit the streets!";
                }
            }
        }

        // =====================================================================
        // SCREEN 2: RACES SCREEN (CRUISE / BLITZ / CHECKPOINT / CIRCUIT, MAP, ENVIRONMENT, DENSITIES)
        // =====================================================================
        private void BuildRacesScreen(Transform parent)
        {
            // 1. Top-Left Race Mode Radio Group (CRUISE, BLITZ, CHECKPOINT, CIRCUIT)
            for (int i = 0; i < raceModeUpperLabels.Length; i++)
            {
                bool isSelected = (i == idxRaceMode);
                float rowY = 128f + i * 68f;
                Sprite orbSprite = isSelected ? spOrbGreen : spOrbRed;
                Color textCol = isSelected ? new Color32(15, 225, 65, 255) : Color.white;

                GameObject radioRow = CreateUIObject($"Radio_{raceModeUpperLabels[i]}", parent);
                SetTopLeftRect(radioRow.GetComponent<RectTransform>(), 155f, rowY, 520f, 60f);

                // Transparent hit graphic so the entire row (orb + text) is clickable
                Image hitImg = radioRow.AddComponent<Image>();
                hitImg.color = new Color(0f, 0f, 0f, 0f);

                Image orbImg = CreateSpriteImage("Orb", radioRow.transform, orbSprite, 0f, 4f, 52f, 52f);
                orbImg.raycastTarget = false;
                Text modeLbl = CreateShadowLabel("Label", radioRow.transform, raceModeUpperLabels[i], 70f, 0f, 440f, 60f, 42, TextAnchor.MiddleLeft, textCol);

                radioModeRows[i] = radioRow;
                radioModeOrbs[i] = orbImg;
                radioModeLabels[i] = modeLbl;

                Button btn = radioRow.AddComponent<Button>();
                btn.targetGraphic = hitImg;
                int capturedIdx = i;
                btn.onClick.AddListener(() =>
                {
                    CloseActiveDropdown();
                    SetRaceMode(capturedIdx);
                });
            }

            // 2. Bottom-Left City Map inside the Blue Collage Window (loaded directly from .ar archives)
            Sprite initialMap = MM2RuntimeCityLoader.LoadRaceMapPreviewSprite(
                MM2GameSession.Location,
                MM2GameSession.RaceType,
                MM2GameSession.RaceIndex
            );
            imgCityMap = CreateSpriteImage("Img_CityMap", parent, initialMap, 146f, 446f, 662f, 398f);

            // 3. Bottom-Left Recessed Black Description Box
            GameObject helpBox = CreateSlicedBox("Box_BottomLeftHelp", parent, spBoxBlack9, 108f, 882f, 695f, 175f);
            txtBottomRaceHelp = CreateShadowLabel(
                "Txt_RaceHelp",
                helpBox.transform,
                "Test drive different vehicles and explore the city without the pressure of racing. Red lights are optional!",
                22f,
                16f,
                650f,
                145f,
                25,
                TextAnchor.UpperLeft,
                new Color32(255, 240, 20, 255)
            );

            // 4. Right Top: STARTING POINT (Cruise) / RACE NAME (Blitz/Checkpoint/Circuit), LAPS (Circuit), OPPONENTS (Checkpoint/Circuit)
            lblRaceName = CreateShadowLabel("Lbl_RaceName", parent, idxRaceMode == 0 ? "STARTING POINT" : "RACE NAME", 860f, 138f, 330f, 60f, 38, TextAnchor.MiddleRight, Color.white);
            boxRaceNameDisabled = CreateSlicedBox("Box_RaceName_Disabled", parent, spBoxNavy9, 1215f, 138f, 635f, 62f);

            selectorRaceNameGroup = CreateUIObject("Group_RaceNameSelector", parent);
            StretchFull(selectorRaceNameGroup.GetComponent<RectTransform>());
            string initialRaceLabel = idxRaceMode == 0
                ? ((currentCruiseSpawnNames != null && currentCruiseSpawnNames.Length > 0)
                    ? currentCruiseSpawnNames[Mathf.Clamp(idxCruiseSpawn, 0, currentCruiseSpawnNames.Length - 1)]
                    : "Random")
                : ((currentRaceNames != null && currentRaceNames.Length > 0)
                    ? currentRaceNames[Mathf.Clamp(idxRaceName, 0, currentRaceNames.Length - 1)]
                    : "");
            txtRaceNameValue = CreateSelectorBoxWithSteppers(
                selectorRaceNameGroup.transform,
                "Selector_RaceName",
                1215f,
                138f,
                635f,
                60f,
                initialRaceLabel,
                onUp: () =>
                {
                    if (idxRaceMode == 0) CycleCruiseSpawnIndex(-1);
                    else CycleRaceIndex(-1);
                },
                onDown: () =>
                {
                    if (idxRaceMode == 0) CycleCruiseSpawnIndex(1);
                    else CycleRaceIndex(1);
                },
                options: null,
                getCurrentIndex: () => idxRaceMode == 0 ? idxCruiseSpawn : idxRaceName,
                onSelectIndex: idx =>
                {
                    if (idxRaceMode == 0) SetCruiseSpawnIndex(idx);
                    else SetRaceIndex(idx);
                },
                getOptionsDynamic: () => idxRaceMode == 0 ? currentCruiseSpawnNames : currentRaceNames,
                isOptionEnabled: idx => idxRaceMode == 0 || MM2PlayerManager.IsRaceUnlocked(MM2GameSession.Location, MM2GameSession.RaceType, idx),
                customMaxRowsPerCol: 13
            );

            // LAPS row (y = 226f, configurable in Circuit mode)
            lapsRowGroup = CreateUIObject("Group_LapsRow", parent);
            StretchFull(lapsRowGroup.GetComponent<RectTransform>());
            CreateShadowLabel("Lbl_Laps", lapsRowGroup.transform, "LAPS", 860f, 226f, 330f, 58f, 34, TextAnchor.MiddleRight, Color.white);
            GameObject lapsBox = CreateSlicedBox("Box_Laps", lapsRowGroup.transform, spBoxNavy9, 1215f, 226f, 155f, 58f);
            txtLapsValue = CreateShadowLabel(
                "Txt_LapsValue",
                lapsBox.transform,
                MM2GameSession.NumLaps.ToString(),
                8f,
                4f,
                139f,
                50f,
                30,
                TextAnchor.MiddleCenter,
                new Color32(255, 240, 20, 255)
            );
            Button lapsBoxBtn = lapsBox.AddComponent<Button>();
            lapsBoxBtn.targetGraphic = lapsBox.GetComponent<Image>();
            lapsBoxBtn.onClick.AddListener(() => CycleLaps(1));
            float lapStepH = (58f - 4f) * 0.5f;
            CreateChevronButton("Laps_Up", lapsBox.transform, spChevronUp, 155f + 6f, 0f, 54f, lapStepH, () => CycleLaps(1));
            CreateChevronButton("Laps_Down", lapsBox.transform, spChevronDown, 155f + 6f, lapStepH + 4f, 54f, lapStepH, () => CycleLaps(-1));

            // OPPONENTS row (y = 312f, shown in Checkpoint & Circuit modes)
            opponentsRowGroup = CreateUIObject("Group_OpponentsRow", parent);
            StretchFull(opponentsRowGroup.GetComponent<RectTransform>());
            CreateShadowLabel("Lbl_Opponents", opponentsRowGroup.transform, "OPPONENTS", 860f, 312f, 330f, 58f, 34, TextAnchor.MiddleRight, Color.white);
            GameObject oppBox = CreateSlicedBox("Box_Opponents", opponentsRowGroup.transform, spBoxNavy9, 1215f, 312f, 155f, 58f);
            txtOpponentsValue = CreateShadowLabel(
                "Txt_OpponentsValue",
                oppBox.transform,
                MM2GameSession.NumOpponents.ToString(),
                8f,
                4f,
                139f,
                50f,
                30,
                TextAnchor.MiddleCenter,
                new Color32(255, 240, 20, 255)
            );
            Button oppBoxBtn = oppBox.AddComponent<Button>();
            oppBoxBtn.targetGraphic = oppBox.GetComponent<Image>();
            oppBoxBtn.onClick.AddListener(() => CycleOpponents(1));
            CreateChevronButton("Opponents_Up", oppBox.transform, spChevronUp, 155f + 6f, 0f, 54f, lapStepH, () => CycleOpponents(1));
            CreateChevronButton("Opponents_Down", oppBox.transform, spChevronDown, 155f + 6f, lapStepH + 4f, 54f, lapStepH, () => CycleOpponents(-1));

            // 5. Right Lower: LOCATION, TIME OF DAY, WEATHER
            CreateShadowLabel("Lbl_Location", parent, "LOCATION", 860f, 464f, 330f, 58f, 36, TextAnchor.MiddleRight, Color.white);
            txtLocationValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_Location",
                1215f,
                464f,
                635f,
                60f,
                locations[idxLocation],
                onUp: () => CycleLocation(-1),
                onDown: () => CycleLocation(1),
                options: locations,
                getCurrentIndex: () => idxLocation,
                onSelectIndex: SetLocation
            );

            CreateShadowLabel("Lbl_TimeOfDay", parent, "TIME OF DAY", 860f, 548f, 330f, 58f, 34, TextAnchor.MiddleRight, Color.white);
            txtTimeOfDayValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_TimeOfDay",
                1215f,
                548f,
                390f,
                60f,
                timesOfDay[idxTimeOfDay],
                onUp: () => CycleTimeOfDay(-1),
                onDown: () => CycleTimeOfDay(1),
                options: timesOfDay,
                getCurrentIndex: () => idxTimeOfDay,
                onSelectIndex: SetTimeOfDay
            );

            CreateShadowLabel("Lbl_Weather", parent, "WEATHER", 860f, 632f, 330f, 58f, 34, TextAnchor.MiddleRight, Color.white);
            txtWeatherValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_Weather",
                1215f,
                632f,
                390f,
                60f,
                weathers[idxWeather],
                onUp: () => CycleWeather(-1),
                onDown: () => CycleWeather(1),
                options: weathers,
                getCurrentIndex: () => idxWeather,
                onSelectIndex: SetWeather
            );

            // 6. Density Sliders (0% to 100%) Over Blue Wireframe Grid with `<` and `>` Arrow Steppers + Click/Drag on Bar
            CreateTiledGridBox("Grid_Densities", parent, 1430f, 712f, 415f, 224f, 11f, 6.5f);

            barPedestrianFill = CreateDensitySliderRow(
                parent,
                "PEDESTRIAN DENSITY",
                720f,
                MM2GameSession.PedestrianDensity,
                delta => AdjustDensity(ref MM2GameSession.PedestrianDensity, barPedestrianFill, delta),
                pct => SetDensity(ref MM2GameSession.PedestrianDensity, barPedestrianFill, pct)
            );
            barTrafficFill = CreateDensitySliderRow(
                parent,
                "TRAFFIC DENSITY",
                796f,
                MM2GameSession.TrafficDensity,
                delta => AdjustDensity(ref MM2GameSession.TrafficDensity, barTrafficFill, delta),
                pct => SetDensity(ref MM2GameSession.TrafficDensity, barTrafficFill, pct)
            );
            barCopFill = CreateDensitySliderRow(
                parent,
                "COP DENSITY",
                872f,
                MM2GameSession.CopDensity,
                delta => AdjustDensity(ref MM2GameSession.CopDensity, barCopFill, delta),
                pct => SetDensity(ref MM2GameSession.CopDensity, barCopFill, pct)
            );

            // 7. Bottom Navigation Arrow Buttons: [ <-- BACK ] and [ SELECT VEHICLE --> ]
            CreateLeftBackButton("Btn_BackToMain", parent, "BACK", 895f, 950f, 315f, 118f, 28, () =>
            {
                ShowScreen(MenuScreen.MainMenu);
            });
            CreateRightArrowButton("Btn_SelectVehicle", parent, "SELECT VEHICLE", 1345f, 950f, 545f, 118f, 28, () =>
            {
                ShowScreen(MenuScreen.SelectVehicle);
            });

            UpdateRaceModeVisualsAndControls(applyRaceMetadata: false);
        }

        // =====================================================================
        // SCREEN 3: SELECT VEHICLE (3D TURNTABLE, SCORES, VEHICLE/COLOR/TRANS, GO DRIVE)
        // =====================================================================
        private void BuildSelectVehicleScreen(Transform parent)
        {
            // 1. Top Full-Width Vehicle Showroom Spotlight Panel (x: 94..1920, y: 104..556)
            GameObject spotGo = CreateSpriteImage("Showroom_Backdrop", parent, spVehicleSpot, 94f, 104f, 1826f, 452f).gameObject;
            CreateSlicedBox("Showroom_GlassBevel", parent, spFrameGlass9, 94f, 104f, 1826f, 452f);

            // RawImage displaying live 3D rotating vehicle RenderTexture
            GameObject previewGo = CreateUIObject("Vehicle3D_RawImage", parent);
            SetTopLeftRect(previewGo.GetComponent<RectTransform>(), 460f, 110f, 1100f, 440f);
            rawVehiclePreview = previewGo.AddComponent<RawImage>();
            rawVehiclePreview.raycastTarget = false;

            // Large bold white "LOCKED" overlay across the 3D vehicle preview when vehicle or paint job is locked
            txtVehicleLockedBanner = CreateShadowLabel(
                "Lbl_VehicleLockedBanner",
                parent,
                "LOCKED",
                460f,
                235f,
                1100f,
                210f,
                128,
                TextAnchor.MiddleCenter,
                Color.white,
                fontUIExtraBold
            );
            txtVehicleLockedBanner.gameObject.SetActive(false);

            // Horizontal divider below showroom
            CreateSolidRect("Divider_ShowroomBot", parent, 94f, 556f, 1826f, 4f, new Color32(8, 8, 52, 255));

            // 2. Bottom-Left: RELATIVE PERFORMANCE SCORES + 4 Red Bar Graphs on Blue Grid
            CreateShadowLabel(
                "Lbl_RelPerfScores",
                parent,
                "RELATIVE PERFORMANCE SCORES",
                260f,
                576f,
                640f,
                36f,
                25,
                TextAnchor.MiddleLeft,
                new Color32(235, 115, 25, 255)
            );

            CreateTiledGridBox("Grid_PerfScores", parent, 445f, 616f, 425f, 232f, 11f, 6.5f);

            // Authentic relative performance bars for the active vehicle
            VehicleSpec currentSpec = VehicleCatalog.GetSpec(MM2GameSession.VehicleName);
            scoreBarHorsepower = CreatePerformanceScoreRow(parent, "HORSEPOWER", 624f, currentSpec.NormalizedHP);
            scoreBarTopSpeed = CreatePerformanceScoreRow(parent, "TOP SPEED", 680f, currentSpec.NormalizedTopSpeed);
            scoreBarDurability = CreatePerformanceScoreRow(parent, "DURABILITY", 736f, currentSpec.NormalizedDurability);
            scoreBarMass = CreatePerformanceScoreRow(parent, "MASS", 792f, currentSpec.NormalizedMass);

            // 3. Bottom-Left Recessed Black Vehicle Description Box
            GameObject descBox = CreateSlicedBox("Box_VehicleDesc", parent, spBoxBlack9, 108f, 882f, 695f, 175f);
            txtVehicleDesc = CreateShadowLabel(
                "Txt_VehicleDesc",
                descBox.transform,
                currentSpec.Description,
                22f,
                16f,
                650f,
                145f,
                25,
                TextAnchor.UpperLeft,
                new Color32(255, 240, 20, 255)
            );

            // 4. Bottom-Right Selectors: VEHICLE, COLOR, TRANSMISSION
            CreateShadowLabel("Lbl_Vehicle", parent, "VEHICLE", 910f, 616f, 280f, 58f, 36, TextAnchor.MiddleRight, Color.white);
            txtVehicleValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_Vehicle",
                1215f,
                616f,
                625f,
                60f,
                vehicles[idxVehicle],
                onUp: () => CycleVehicle(-1),
                onDown: () => CycleVehicle(1),
                options: vehicles,
                getCurrentIndex: () => idxVehicle,
                onSelectIndex: SetVehicle
            );

            CreateShadowLabel("Lbl_Color", parent, "COLOR", 910f, 698f, 280f, 58f, 32, TextAnchor.MiddleRight, Color.white);
            txtColorValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_Color",
                1215f,
                698f,
                625f,
                60f,
                vehicleColors[idxVehicleColor],
                onUp: () => CycleVehicleColor(-1),
                onDown: () => CycleVehicleColor(1),
                options: null,
                getCurrentIndex: () => idxVehicleColor,
                onSelectIndex: SetVehicleColor,
                getOptionsDynamic: () => vehicleColors
            );

            CreateShadowLabel("Lbl_Transmission", parent, "TRANSMISSION", 910f, 780f, 280f, 58f, 30, TextAnchor.MiddleRight, Color.white);
            txtTransmissionValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_Transmission",
                1215f,
                780f,
                390f,
                60f,
                transmissions[idxTransmission],
                onUp: () => CycleTransmission(-1),
                onDown: () => CycleTransmission(1),
                options: transmissions,
                getCurrentIndex: () => idxTransmission,
                onSelectIndex: SetTransmission
            );

            // 5. "VEHICLE SHOWCASE" Wide Rectangular Glowing Button
            CreateGlowButton("Btn_VehicleShowcase", parent, "VEHICLE SHOWCASE", 1075f, 862f, 650f, 72f, 28, ShowVehicleShowcaseModal);

            // 6. Bottom Navigation Buttons: [ <-- BACK ] and [ GO DRIVE --> ]
            CreateLeftBackButton("Btn_BackToRaces", parent, "BACK", 895f, 950f, 315f, 118f, 28, () =>
            {
                ShowScreen(MenuScreen.Races);
            });
            CreateRightArrowButton("Btn_GoDrive", parent, "GO DRIVE", 1345f, 950f, 545f, 118f, 30, OnGoDriveClicked);
            RefreshVehicleLockUI();
        }

        // =====================================================================
        // 3D VEHICLE TURNTABLE PREVIEW (SELECT VEHICLE SCREEN)
        // =====================================================================
        private void Build3DVehicleTurntablePreview()
        {
            showcaseRenderTex = new RenderTexture(1280, 560, 24, RenderTextureFormat.ARGB32);
            showcaseRenderTex.antiAliasing = 4;
            showcaseRenderTex.Create();

            if (rawVehiclePreview != null)
            {
                rawVehiclePreview.texture = showcaseRenderTex;
            }

            showcaseRoot = new GameObject("MM2_VehicleTurntableRig");
            showcaseRoot.transform.position = new Vector3(0f, -500f, 0f);

            // Camera pointed at the turntable pivot (matching MM2's elevated 3/4 showroom angle)
            GameObject camGo = new GameObject("TurntableCamera");
            camGo.transform.SetParent(showcaseRoot.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.95f, -5.1f);
            camGo.transform.LookAt(showcaseRoot.transform.TransformPoint(new Vector3(0f, 0.58f, 0f)));
            showcaseCameraTransform = camGo.transform;

            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.allowHDR = false;
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 18f;
            cam.targetTexture = showcaseRenderTex;
            showcaseCamera = cam;

            // Dedicated Showroom Point Lights around the turntable at y=-500 so they don't over-illuminate the 3D city at y=0
            GameObject keyLightGo = new GameObject("TurntableKeyLight");
            keyLightGo.transform.SetParent(showcaseRoot.transform, false);
            keyLightGo.transform.localPosition = new Vector3(-3.5f, 4.2f, -4.8f);
            Light keyLight = keyLightGo.AddComponent<Light>();
            keyLight.type = LightType.Point;
            keyLight.range = 35f;
            keyLight.intensity = 2.6f;
            keyLight.color = new Color(1f, 0.98f, 0.92f);

            GameObject fillLightGo = new GameObject("TurntableFillLight");
            fillLightGo.transform.SetParent(showcaseRoot.transform, false);
            fillLightGo.transform.localPosition = new Vector3(4.0f, 2.6f, 3.8f);
            Light fillLight = fillLightGo.AddComponent<Light>();
            fillLight.type = LightType.Point;
            fillLight.range = 35f;
            fillLight.intensity = 1.4f;
            fillLight.color = new Color(0.75f, 0.85f, 1f);

            // Turntable Pivot
            GameObject pivotGo = new GameObject("CarPivot");
            pivotGo.transform.SetParent(showcaseRoot.transform, false);
            pivotGo.transform.localPosition = Vector3.zero;
            pivotGo.transform.localRotation = Quaternion.Euler(0f, -135f, 0f);
            showcaseCarPivot = pivotGo.transform;

            RebuildTurntableCarModel();
        }

        // =====================================================================
        // LIVE 3D SAN FRANCISCO MENU BACKGROUND (el_sharpimg_f + AIRBORNE CAR)
        // =====================================================================
        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   !string.IsNullOrEmpty(needle) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Deterministic vehicle selection for the menu background scene based on archive basenames,
        /// falling back gracefully to the first available loaded vehicle.
        /// </summary>
        private static VehicleSpec SelectMenuBackgroundVehicleSpec()
        {
            List<VehicleSpec> allSpecs = VehicleCatalog.GetAllSpecs();
            if (allSpecs == null || allSpecs.Count == 0)
            {
                return null;
            }

            string[] preferredBasenames = { "vpdb7", "vpbullet", "vpmustang99", "vppanozgt" };
            for (int p = 0; p < preferredBasenames.Length; p++)
            {
                string targetBase = preferredBasenames[p];
                for (int i = 0; i < allSpecs.Count; i++)
                {
                    VehicleSpec s = allSpecs[i];
                    if (s != null && string.Equals(s.BaseName, targetBase, StringComparison.OrdinalIgnoreCase))
                    {
                        return s;
                    }
                }
            }

            // Fallback: First available vehicle in deterministic order
            for (int i = 0; i < allSpecs.Count; i++)
            {
                if (allSpecs[i] != null && !string.IsNullOrEmpty(allSpecs[i].BaseName))
                {
                    return allSpecs[i];
                }
            }

            return null;
        }

        private void BuildLiveMenuBackgroundScene()
        {
            CleanupLiveMenuBackground();

            if (!MM2GameSession.EnableLiveMenuBackground || rawLiveMenuBg == null)
            {
                return;
            }

            // 1. Check if San Francisco exists by its MM2 basename ("sf").
            //    If not available, fall back to the static spOrangeBase image.
            MM2RuntimeCityLoader.EnsureCatalogLoaded();
            IReadOnlyList<CitySpec> allCities = MM2RuntimeCityLoader.GetAllCities();
            CitySpec sfSpec = null;
            if (allCities != null)
            {
                for (int i = 0; i < allCities.Count; i++)
                {
                    if (allCities[i] != null && string.Equals(allCities[i].MapName, "sf", StringComparison.OrdinalIgnoreCase))
                    {
                        sfSpec = allCities[i];
                        break;
                    }
                }
            }

            if (sfSpec == null || !MM2ArchiveSystem.FileExists("city/sf.psdl"))
            {
                rawLiveMenuBg.gameObject.SetActive(false);
                return;
            }

            // 2. Choose the vehicle according to the 5-tier priority rules.
            //    If no vehicles exist, fall back to the static spOrangeBase image.
            VehicleSpec carSpec = SelectMenuBackgroundVehicleSpec();
            if (carSpec == null)
            {
                rawLiveMenuBg.gameObject.SetActive(false);
                return;
            }

            // 3. Ensure San Francisco ("sf") is loaded in the scene with clear daytime lighting for the backdrop
            string savedTod = MM2GameSession.TimeOfDay;
            string savedWeather = MM2GameSession.Weather;
            MM2RuntimeCityLoader.EnsureCityLoadedInScene(sfSpec.DisplayName, out _, out _);
            MM2EnvironmentManager.EnsureAndApply("sf", "Noon", "Clear", showBanner: false);
            MM2GameSession.TimeOfDay = savedTod;
            MM2GameSession.Weather = savedWeather;

            // Hide any lingering gameplay traffic/police/opponent roots while in the menu so the intersection stays clean
            string[] gameplayRootsToHide = { "MM2_TrafficRoot", "MM2_PoliceRoot", "MM2_OpponentRoot", "MM2_PedestrianRoot" };
            foreach (string rootName in gameplayRootsToHide)
            {
                GameObject go = GameObject.Find(rootName);
                if (go != null && go.activeSelf)
                {
                    disabledSceneGameplayObjects.Add(go);
                    go.SetActive(false);
                }
            }

            // 4. Create the frozen airborne vehicle and 3D camera rig at the Market & Sutter/Montgomery intersection
            //    facing the northeast Flatiron wedge of el_sharpimg_f (~50cm above road level, looking upwards).
            menuBgRigRoot = new GameObject("MM2_MenuBackground3DRig");

            float roadY = 11.85f;
            Vector3 camXZ = new Vector3(431.5f, 30.0f, 274.5f);
            if (Physics.Raycast(camXZ, Vector3.down, out RaycastHit hit, 40f, ~0, QueryTriggerInteraction.Ignore))
            {
                roadY = hit.point.y;
            }

            // Camera ~50cm above road level, positioned further right in the intersection while aiming so the building corner stays right of the driver select entries (and left of the mode arrows)
            menuBgBaseCamPos = new Vector3(431.5f, roadY + 0.50f, 274.5f);
            Vector3 lookTarget = new Vector3(468.0f, roadY + 11.2f, 307.8f);
            menuBgBaseCamRot = Quaternion.LookRotation((lookTarget - menuBgBaseCamPos).normalized, Vector3.up);

            // Spawn the chosen car frozen in mid-air flying past in front of the building
            GameObject airborneCarGo = new GameObject($"MenuBg_AirborneCar_{carSpec.BaseName}");
            airborneCarGo.transform.SetParent(menuBgRigRoot.transform, false);

            // Keep the car in the same camera-relative position, pitched nose-down (+18f X) and rolled toward the camera (-20f Z)
            Vector3 carPos = menuBgBaseCamPos + menuBgBaseCamRot * new Vector3(0.75f, 0.54f, 8.95f);
            Quaternion carRot = menuBgBaseCamRot * Quaternion.Euler(0f, 120f, 0f) * Quaternion.Euler(18f, 0f, -20f);
            airborneCarGo.transform.SetPositionAndRotation(carPos, carRot);

            string chosenColor = (carSpec.Colors != null && carSpec.Colors.Length > 0) ? carSpec.Colors[0] : "Default";
            VehiclePaintjob.PopulateVehicleVisuals(
                airborneCarGo.transform,
                carSpec.DisplayName,
                chosenColor,
                out _,
                out Transform flWheel,
                out Transform frWheel,
                out _,
                out _
            );

            // Verify at least one mesh renderer was created for the car; otherwise fall back to static image
            if (airborneCarGo.GetComponentsInChildren<MeshRenderer>(true).Length == 0)
            {
                CleanupLiveMenuBackground();
                return;
            }

            // Slight steering turn on front wheels around their vertical steering axis
            if (flWheel != null) flWheel.localRotation = Quaternion.Euler(0f, -10f, 0f);
            if (frWheel != null) frWheel.localRotation = Quaternion.Euler(0f, -10f, 0f);

            // Dedicated local key light on the airborne car so its bodywork, top hood/roof, and grille gleam cleanly
            GameObject carLightGo = new GameObject("MenuBg_CarKeyLight");
            carLightGo.transform.SetParent(menuBgRigRoot.transform, false);
            carLightGo.transform.position = carPos + menuBgBaseCamRot * new Vector3(-0.8f, 2.4f, -3.2f);
            Light carLight = carLightGo.AddComponent<Light>();
            carLight.type = LightType.Point;
            carLight.range = 18f;
            carLight.intensity = 2.4f;
            carLight.color = new Color(1f, 0.96f, 0.88f);

            // 5. Create RenderTexture, Camera, and Duotone Orange-to-Blue Material
            menuBgRenderTex = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            menuBgRenderTex.antiAliasing = 4;
            menuBgRenderTex.Create();

            GameObject camGo = new GameObject("MenuBg_Camera");
            camGo.transform.SetParent(menuBgRigRoot.transform, false);
            camGo.transform.SetPositionAndRotation(menuBgBaseCamPos, menuBgBaseCamRot);

            menuBgCamera = camGo.AddComponent<Camera>();
            menuBgCamera.clearFlags = CameraClearFlags.Skybox;
            menuBgCamera.backgroundColor = new Color(0.55f, 0.72f, 0.92f, 1f);
            menuBgCamera.fieldOfView = 40f;
            menuBgCamera.nearClipPlane = 0.25f;
            menuBgCamera.farClipPlane = 1800f;
            menuBgCamera.allowHDR = false;
            menuBgCamera.targetTexture = menuBgRenderTex;

            Shader duotoneShader =
                Resources.Load<Shader>("Shaders/MM2MenuDuotoneBackground") ??
                Shader.Find("UI/MM2MenuDuotoneBackground");
            if (duotoneShader != null)
            {
                menuBgDuotoneMat = new Material(duotoneShader) { name = "Mat_MM2MenuDuotoneBg" };
                rawLiveMenuBg.material = menuBgDuotoneMat;
            }

            rawLiveMenuBg.texture = menuBgRenderTex;
            rawLiveMenuBg.color = Color.white;
            rawLiveMenuBg.gameObject.SetActive(true);
            menuBgStartTime = Time.unscaledTime;
            UpdateLiveMenuBackgroundCamera();
        }

        private void UpdateLiveMenuBackgroundCamera()
        {
            if (menuBgCamera == null) return;

            // Very slow, continuous cinematic zoom & pan while the 3D scene and airborne car remain frozen
            float t = Time.unscaledTime - menuBgStartTime;
            float zoomWave = 0.5f - 0.5f * Mathf.Cos(t * 0.16f); // ~39s smooth zoom cycle (0 -> 1 -> 0)
            float panWaveX = Mathf.Sin(t * 0.11f);               // ~57s horizontal pan cycle
            float panWaveY = Mathf.Sin(t * 0.085f);              // ~74s subtle vertical pan cycle

            menuBgCamera.fieldOfView = Mathf.Lerp(41.5f, 35.0f, zoomWave);

            Vector3 localOffset = new Vector3(
                panWaveX * 0.42f,
                panWaveY * 0.08f,
                zoomWave * 0.65f
            );
            Quaternion panRot = Quaternion.Euler(
                -panWaveY * 1.1f - zoomWave * 0.8f,
                panWaveX * 2.6f,
                0f
            );

            menuBgCamera.transform.SetPositionAndRotation(
                menuBgBaseCamPos + menuBgBaseCamRot * localOffset,
                menuBgBaseCamRot * panRot
            );
        }

        private void CleanupLiveMenuBackground()
        {
            if (rawLiveMenuBg != null)
            {
                rawLiveMenuBg.texture = null;
                rawLiveMenuBg.material = null;
                rawLiveMenuBg.gameObject.SetActive(false);
            }
            if (menuBgCamera != null)
            {
                menuBgCamera.targetTexture = null;
                menuBgCamera = null;
            }
            if (menuBgRigRoot != null)
            {
                Destroy(menuBgRigRoot);
                menuBgRigRoot = null;
            }
            else
            {
                GameObject staleRig = GameObject.Find("MM2_MenuBackground3DRig");
                if (staleRig != null)
                {
                    DestroyImmediate(staleRig);
                }
            }
            if (menuBgRenderTex != null)
            {
                menuBgRenderTex.Release();
                Destroy(menuBgRenderTex);
                menuBgRenderTex = null;
            }
            if (menuBgDuotoneMat != null)
            {
                Destroy(menuBgDuotoneMat);
                menuBgDuotoneMat = null;
            }
        }

        private void RebuildTurntableCarModel()
        {
            if (showcaseCarPivot == null) return;

            for (int i = showcaseCarPivot.childCount - 1; i >= 0; i--)
            {
                Destroy(showcaseCarPivot.GetChild(i).gameObject);
            }

            VehiclePaintjob.PopulateVehicleVisuals(
                showcaseCarPivot,
                MM2GameSession.VehicleName,
                MM2GameSession.VehicleColor,
                out _,
                out _,
                out _,
                out _,
                out _
            );

            // Frame the turntable camera dynamically using UIDist from tune/<base>.info
            // so both compact cars and large buses/semis/fire trucks fit the showroom cleanly
            if (showcaseCameraTransform != null && showcaseRoot != null)
            {
                VehicleSpec spec = VehicleCatalog.GetSpec(MM2GameSession.VehicleName);
                float uiDist = (spec != null && spec.UIDist > 0.5f) ? spec.UIDist : 5.5f;
                float scaleFactor = Mathf.Clamp(uiDist / 5.3f, 0.90f, 2.35f);
                float camZ = -5.1f * scaleFactor;
                float camY = 1.95f * Mathf.Lerp(1f, scaleFactor, 0.72f);
                float lookY = 0.58f * Mathf.Lerp(1f, scaleFactor, 0.55f);
                showcaseCameraTransform.localPosition = new Vector3(0f, camY, camZ);
                showcaseCameraTransform.LookAt(showcaseRoot.transform.TransformPoint(new Vector3(0f, lookY, 0f)));
            }
        }

        // =====================================================================
        // SCREEN 4: EVENT RESULTS SCREEN (POST-RACE / WATER / TIMEOUT STANDINGS)
        // =====================================================================
        private void BuildEventResultsScreen(Transform parent)
        {
            // Vertical divider between the wide Event Results table on the left and the MM2 logo + action menu on the right
            CreateSolidRect("Divider_EventResultsVert", parent, 1256f, 0f, 4f, 860f, new Color32(8, 8, 52, 255));

            // Top-Right Midtown Madness 2 Logo (matching Screenshot 3)
            Image rightLogo = CreateSpriteImage("EventResults_LogoMM2", parent, spLogoMM2, 1282f, 8f, 616f, 236f);
            rightLogo.raycastTarget = false;

            // Main Recessed Black Standings Box (x: 110, y: 132, w: 1118, h: 708)
            GameObject tableBox = CreateSlicedBox("Box_EventResultsTable", parent, spBoxBlack9, 110f, 132f, 1118f, 708f);
            Color32 rowColor = new Color32(226, 230, 238, 255);

            for (int i = 0; i < MaxEventResultRows; i++)
            {
                float rowY = 38f + i * 48f;
                eventResultsRankTexts[i] = CreateShadowLabel(
                    $"Txt_Rank_{i}",
                    tableBox.transform,
                    "",
                    24f,
                    rowY,
                    80f,
                    46f,
                    34,
                    TextAnchor.MiddleLeft,
                    rowColor,
                    fontUIBold
                );
                eventResultsNameTexts[i] = CreateShadowLabel(
                    $"Txt_Name_{i}",
                    tableBox.transform,
                    "",
                    118f,
                    rowY,
                    580f,
                    46f,
                    34,
                    TextAnchor.MiddleLeft,
                    rowColor,
                    fontUIBold
                );
                eventResultsTimeTexts[i] = CreateShadowLabel(
                    $"Txt_Time_{i}",
                    tableBox.transform,
                    "",
                    725f,
                    rowY,
                    375f,
                    46f,
                    34,
                    TextAnchor.MiddleLeft,
                    rowColor,
                    fontUIBold
                );
            }

            // Bottom-Left Recessed Black Event Info Box (x: 108, y: 882, w: 695, h: 175)
            // Shows "<RaceType> Race" on line 1 and "<RaceName>" on line 2 (matching Screenshot 3)
            GameObject infoBox = CreateSlicedBox("Box_EventResultsInfo", parent, spBoxBlack9, 108f, 882f, 695f, 175f);
            txtEventResultsRaceType = CreateShadowLabel(
                "Txt_EventRaceType",
                infoBox.transform,
                "Circuit Race",
                20f,
                16f,
                650f,
                52f,
                34,
                TextAnchor.MiddleLeft,
                rowColor,
                fontUIBold
            );
            txtEventResultsRaceName = CreateShadowLabel(
                "Txt_EventRaceName",
                infoBox.transform,
                "Take It Easy",
                20f,
                66f,
                650f,
                92f,
                34,
                TextAnchor.UpperLeft,
                rowColor,
                fontUIBold
            );

            // Right Column Action Menu Items (matching Screenshot 3):
            // Restart Race, Next Race, Race Menu, Return to Desktop
            txtEventResultsBtnRestart = CreateEventResultsActionItem("Btn_RestartRace", parent, "Restart Race", 1325f, 205f, 520f, 68f, OnRestartRaceClicked);
            txtEventResultsBtnNext = CreateEventResultsActionItem("Btn_NextRace", parent, "Next Race", 1325f, 308f, 520f, 68f, OnNextRaceClicked);
            txtEventResultsBtnMenu = CreateEventResultsActionItem("Btn_RaceMenu", parent, "Race Menu", 1325f, 411f, 520f, 68f, () =>
            {
                if (MM2GameSession.IsCrashCourseSession)
                {
                    ShowScreen(MenuScreen.CrashCourseHub);
                }
                else
                {
                    ShowScreen(MenuScreen.Races);
                }
            });
            txtEventResultsBtnExit = CreateEventResultsActionItem("Btn_ReturnToDesktop", parent, "Return to Desktop", 1325f, 514f, 520f, 68f, () =>
            {
                Debug.Log("[MM2 Menu] Return to Desktop clicked.");
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            });
        }

        private Text CreateEventResultsActionItem(
            string name,
            Transform parent,
            string label,
            float x,
            float y,
            float w,
            float h,
            Action onClick
        )
        {
            GameObject itemGo = CreateSolidRect(name, parent, x, y, w, h, new Color(0f, 0f, 0f, 0f));
            Image hitImg = itemGo.GetComponent<Image>();
            hitImg.raycastTarget = true;

            Color32 normalCol = new Color32(220, 226, 236, 255);
            Color32 hoverCol = new Color32(255, 240, 25, 255);

            Text lbl = CreateShadowLabel(
                "Label",
                itemGo.transform,
                label,
                0f,
                0f,
                w,
                h,
                44,
                TextAnchor.MiddleLeft,
                normalCol,
                fontUIExtraBold
            );

            var hoverRelay = itemGo.AddComponent<MM2TextButtonHoverRelay>();
            hoverRelay.labelText = lbl;
            hoverRelay.normalColor = normalCol;
            hoverRelay.hoverColor = hoverCol;

            Button btn = itemGo.AddComponent<Button>();
            btn.targetGraphic = hitImg;
            btn.transition = Selectable.Transition.None;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return lbl;
        }

        private void RefreshEventResultsUI()
        {
            bool isCrashLesson = MM2GameSession.IsCrashCourseSession && MM2GameSession.CrashSubMode == 0;
            if (txtEventResultsBtnRestart != null)
            {
                txtEventResultsBtnRestart.text = isCrashLesson ? "Restart Lesson" : "Restart Race";
            }
            if (txtEventResultsBtnNext != null)
            {
                txtEventResultsBtnNext.text = isCrashLesson ? "Next Lesson" : "Next Race";
            }
            if (txtEventResultsBtnMenu != null)
            {
                txtEventResultsBtnMenu.text = MM2GameSession.IsCrashCourseSession ? "Back to School" : "Race Menu";
            }

            MM2EventResultsData data = MM2GameSession.LastEventResults;
            if (data == null)
            {
                string mode = string.IsNullOrEmpty(MM2GameSession.RaceType) ? "Cruise" : MM2GameSession.RaceType;
                data = new MM2EventResultsData
                {
                    RaceModeLabel = string.Equals(mode, "Cruise", StringComparison.OrdinalIgnoreCase) ? "Cruise" : $"{mode} Race",
                    RaceName = string.IsNullOrEmpty(MM2GameSession.RaceName) ? MM2GameSession.Location : MM2GameSession.RaceName,
                    CanAdvanceToNext = true
                };
                data.Entries.Add(new MM2EventResultEntry
                {
                    Rank = 1,
                    RacerName = MM2GameSession.DriverName,
                    TimeOrStatus = "0:00:00",
                    IsPlayer = true
                });
            }

            bool showNextBtn = data.CanAdvanceToNext;
            if (isCrashLesson)
            {
                CitySpec ccSpec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.CrashCourseCity);
                int totalLessons = (ccSpec != null && ccSpec.CrashLessons != null) ? ccSpec.CrashLessons.Length : 0;
                int nextLessonIdx = MM2GameSession.GetNextUnlockedCrashLessonIndex(
                    MM2GameSession.CrashCourseCity,
                    MM2GameSession.CrashLessonIndex,
                    totalLessons
                );
                showNextBtn = showNextBtn && nextLessonIdx >= 0;
            }
            else if (MM2GameSession.IsCrashCourseSession)
            {
                string modeType = MM2GameSession.CrashSubMode == 1 ? "Blitz" : "Checkpoint";
                int nextWorkIdx = MM2PlayerManager.GetNextUnlockedRaceIndex(
                    MM2GameSession.Location,
                    modeType,
                    MM2GameSession.CrashWorkRaceIndex
                );
                showNextBtn = showNextBtn && nextWorkIdx >= 0;
            }
            else if (idxRaceMode > 0)
            {
                int nextRaceIdx = MM2PlayerManager.GetNextUnlockedRaceIndex(
                    MM2GameSession.Location,
                    MM2GameSession.RaceType,
                    MM2GameSession.RaceIndex
                );
                showNextBtn = showNextBtn && nextRaceIdx >= 0;
            }

            if (txtEventResultsBtnNext != null && txtEventResultsBtnNext.transform.parent != null)
            {
                txtEventResultsBtnNext.transform.parent.gameObject.SetActive(showNextBtn);
            }
            if (txtEventResultsBtnMenu != null && txtEventResultsBtnMenu.transform.parent != null)
            {
                SetTopLeftRect(
                    txtEventResultsBtnMenu.transform.parent.GetComponent<RectTransform>(),
                    1325f,
                    showNextBtn ? 411f : 308f,
                    520f,
                    68f
                );
            }
            if (txtEventResultsBtnExit != null && txtEventResultsBtnExit.transform.parent != null)
            {
                SetTopLeftRect(
                    txtEventResultsBtnExit.transform.parent.GetComponent<RectTransform>(),
                    1325f,
                    showNextBtn ? 514f : 411f,
                    520f,
                    68f
                );
            }

            if (txtEventResultsRaceType != null) txtEventResultsRaceType.text = data.RaceModeLabel ?? "";
            if (txtEventResultsRaceName != null) txtEventResultsRaceName.text = data.RaceName ?? "";

            for (int i = 0; i < MaxEventResultRows; i++)
            {
                bool hasRow = i < data.Entries.Count;
                if (eventResultsRankTexts[i] != null)
                {
                    eventResultsRankTexts[i].text = hasRow ? data.Entries[i].Rank.ToString() : "";
                }
                if (eventResultsNameTexts[i] != null)
                {
                    eventResultsNameTexts[i].text = hasRow ? (data.Entries[i].RacerName ?? "") : "";
                }
                if (eventResultsTimeTexts[i] != null)
                {
                    eventResultsTimeTexts[i].text = hasRow ? (data.Entries[i].TimeOrStatus ?? "") : "";
                }
            }

            if (!string.IsNullOrEmpty(MM2GameSession.LastUnlockedRewardMessage))
            {
                string rewardMsg = MM2GameSession.LastUnlockedRewardMessage;
                MM2GameSession.LastUnlockedRewardMessage = string.Empty;
                ShowModalDialog("CONGRATULATIONS!", rewardMsg, "OK", CloseActiveModal, null, null);
            }
        }

        private void OnRestartRaceClicked()
        {
            if (MM2GameSession.IsCrashCourseSession)
            {
                OnCrashCourseGoDriveClicked();
                return;
            }
            OnGoDriveClicked();
        }

        private void OnNextRaceClicked()
        {
            if (MM2GameSession.IsCrashCourseSession)
            {
                if (MM2GameSession.CrashSubMode == 0)
                {
                    CitySpec ccSpec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.CrashCourseCity);
                    int count = (ccSpec != null && ccSpec.CrashLessons != null) ? ccSpec.CrashLessons.Length : 13;
                    int nextIdx = MM2GameSession.GetNextUnlockedCrashLessonIndex(
                        MM2GameSession.CrashCourseCity,
                        MM2GameSession.CrashLessonIndex,
                        count
                    );
                    if (nextIdx >= 0 && nextIdx < count)
                    {
                        MM2GameSession.CrashLessonIndex = nextIdx;
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    string modeType = MM2GameSession.CrashSubMode == 1 ? "Blitz" : "Checkpoint";
                    string[] rNames = MM2RuntimeCityLoader.GetRaceNames(MM2GameSession.Location, modeType) ?? Array.Empty<string>();
                    if (rNames.Length > 0)
                    {
                        int nextWorkIdx = MM2PlayerManager.GetNextUnlockedRaceIndex(
                            MM2GameSession.Location,
                            modeType,
                            MM2GameSession.CrashWorkRaceIndex
                        );
                        if (nextWorkIdx >= 0)
                        {
                            MM2GameSession.CrashWorkRaceIndex = nextWorkIdx;
                        }
                    }
                }
                OnCrashCourseGoDriveClicked();
                return;
            }

            if (idxRaceMode == 0)
            {
                // If in Cruise mode, advance to the first available race mode
                for (int m = 1; m < raceModes.Length; m++)
                {
                    if (IsRaceModeAvailable(MM2GameSession.Location, m))
                    {
                        SetRaceMode(m);
                        break;
                    }
                }
            }
            else
            {
                string[] modeRaceNames = MM2RuntimeCityLoader.GetRaceNames(MM2GameSession.Location, MM2GameSession.RaceType) ?? Array.Empty<string>();
                if (modeRaceNames.Length > 0)
                {
                    int nextIdx = MM2PlayerManager.GetNextUnlockedRaceIndex(
                        MM2GameSession.Location,
                        MM2GameSession.RaceType,
                        MM2GameSession.RaceIndex
                    );
                    if (nextIdx >= 0)
                    {
                        currentRaceNames = modeRaceNames;
                        SetRaceIndex(nextIdx);
                    }
                }
            }

            OnGoDriveClicked();
        }

        // =====================================================================
        // SCREEN 5: CRASH COURSE INTRO (DYNAMIC LIST OF SCHOOLS FROM .AR FILES)
        // =====================================================================
        private void BuildCrashCourseIntroScreen(Transform parent)
        {
            List<CitySpec> crashCities = MM2RuntimeCityLoader.GetCrashCourseCities();
            string optionsCountWord = crashCities.Count == 2 ? "TWO OPTIONS" : "OPTIONS";
            string storyIntro =
                "SO, YOU'VE JUST BEEN FIRED FROM YOUR JOB AS A\n" +
                "TELEPHONE PSYCHIC.  TOO BAD YOU DIDN'T SEE\n" +
                "THAT ONE COMING!  FORTUNATELY, YOUR RICH\n" +
                "UNCLE DONALD IS WILLING TO HELP YOU FIND A\n" +
                $"NEW LINE OF WORK.  HE'S LINED UP {optionsCountWord}\n" +
                "FOR YOU:";

            CreateShadowLabel(
                "Txt_CrashStoryIntro",
                parent,
                storyIntro,
                122f,
                142f,
                1140f,
                250f,
                29,
                TextAnchor.UpperLeft,
                Color.white,
                fontUIExtraBold
            );

            // Render dynamic school descriptions and right-arrow buttons for all cities that define a Crash Course
            float blurbY = 468f;
            float blurbStep = crashCities.Count > 2 ? 128f : 162f;

            for (int i = 0; i < crashCities.Count; i++)
            {
                CitySpec cSpec = crashCities[i];
                if (cSpec == null) continue;

                CreateShadowLabel(
                    $"Txt_SchoolTitle_{cSpec.MapName}",
                    parent,
                    cSpec.CrashSchoolTitle,
                    122f,
                    blurbY,
                    1000f,
                    44f,
                    29,
                    TextAnchor.UpperLeft,
                    Color.white,
                    fontUIExtraBold
                );

                CreateShadowLabel(
                    $"Txt_SchoolBlurb_{cSpec.MapName}",
                    parent,
                    cSpec.CrashIntroBlurb,
                    585f,
                    blurbY + 38f,
                    640f,
                    118f,
                    24,
                    TextAnchor.UpperLeft,
                    Color.white,
                    fontUIExtraBold
                );

                blurbY += blurbStep;

                // Right-side arrow button (stacked from bottom y=924 upwards, matching Screenshot 1)
                float btnY = 924f - (crashCities.Count - 1 - i) * 128f;
                string capturedMapName = cSpec.MapName;
                string capturedDisplayName = cSpec.DisplayName;
                int capturedLessonCount = cSpec.CrashLessons != null ? cSpec.CrashLessons.Length : 0;
                CreateRightArrowButton(
                    $"Btn_CrashCity_{cSpec.MapName}",
                    parent,
                    cSpec.CrashButtonLabel,
                    1345f,
                    btnY,
                    540f,
                    122f,
                    28,
                    () =>
                    {
                        MM2GameSession.CrashCourseCity = capturedMapName;
                        MM2GameSession.Location = capturedDisplayName;
                        MM2GameSession.CrashLessonIndex = MM2GameSession.GetMaxUnlockedCrashLessonIndex(capturedMapName, capturedLessonCount);
                        MM2GameSession.CrashSubMode = 0;
                        MM2GameSession.CrashWorkRaceIndex = 0;
                        ShowScreen(MenuScreen.CrashCourseHub);
                    }
                );
            }

            // Bottom-Left Recessed Black Status Box
            GameObject helpBox = CreateSlicedBox("Box_CrashIntroHelp", parent, spBoxBlack9, 108f, 882f, 695f, 175f);
            CreateShadowLabel(
                "Txt_CrashIntroHelp",
                helpBox.transform,
                "Choose a driving school to learn stunt driving or cabbie skills and unlock special vehicles!",
                22f,
                16f,
                650f,
                145f,
                25,
                TextAnchor.UpperLeft,
                new Color32(255, 240, 20, 255)
            );

            // Bottom-Center [ <-- BACK ] Button
            CreateLeftBackButton("Btn_BackFromCrashIntro", parent, "BACK", 895f, 924f, 315f, 118f, 28, () =>
            {
                ShowScreen(MenuScreen.MainMenu);
            });
        }

        // =====================================================================
        // SCREEN 6: CRASH COURSE CURRICULUM HUB (PASS/FAIL TABLE, TRAINING, WORK EXPERIENCE)
        // =====================================================================
        private void BuildCrashCourseHubScreen(Transform parent)
        {
            // 1. Left-Side Course Curriculum Recessed Black Panel (x: 108, y: 120, w: 695, h: 726)
            GameObject currBox = CreateSlicedBox("Box_CourseCurriculum", parent, spBoxBlack9, 108f, 120f, 695f, 726f);
            Color32 royalBlue = new Color32(45, 115, 255, 255);

            CreateShadowLabel("Lbl_CourseCurriculum", currBox.transform, "Course Curriculum", 24f, 22f, 380f, 38f, 27, TextAnchor.MiddleLeft, royalBlue, fontUIBold);
            CreateShadowLabel("Lbl_ColPass", currBox.transform, "PASS", 458f, 22f, 105f, 38f, 27, TextAnchor.MiddleCenter, royalBlue, fontUIBold);
            CreateShadowLabel("Lbl_ColFail", currBox.transform, "FAIL", 574f, 22f, 105f, 38f, 27, TextAnchor.MiddleCenter, royalBlue, fontUIBold);

            // Build up to MaxCrashCurriculumRows clickable curriculum rows
            for (int i = 0; i < MaxCrashCurriculumRows; i++)
            {
                GameObject rowGo = CreateUIObject($"CurriculumRow_{i}", currBox.transform);
                SetTopLeftRect(rowGo.GetComponent<RectTransform>(), 16f, 78f + i * 38f, 662f, 34f);

                Image hitImg = rowGo.AddComponent<Image>();
                hitImg.color = new Color(0f, 0f, 0f, 0f);

                Text lbl = CreateShadowLabel("Label", rowGo.transform, "", 8f, 0f, 430f, 34f, 25, TextAnchor.MiddleLeft, royalBlue, fontUIBold);

                Image passIcon = CreateSpriteImage("PassIcon", rowGo.transform, spCcSmChk, 478f, 3f, 32f, 28f);
                passIcon.preserveAspect = true;
                passIcon.raycastTarget = false;
                passIcon.gameObject.SetActive(false);

                Image failIcon = CreateSpriteImage("FailIcon", rowGo.transform, spCcFail, 594f, 3f, 32f, 28f);
                failIcon.preserveAspect = true;
                failIcon.raycastTarget = false;
                failIcon.gameObject.SetActive(false);

                Text passTxt = CreateShadowLabel("PassText", rowGo.transform, "", 442f, 0f, 105f, 34f, 22, TextAnchor.MiddleCenter, new Color32(30, 235, 65, 255), fontUIExtraBold);
                Text failTxt = CreateShadowLabel("FailText", rowGo.transform, "", 558f, 0f, 105f, 34f, 22, TextAnchor.MiddleCenter, new Color32(245, 45, 45, 255), fontUIExtraBold);

                Button btn = rowGo.AddComponent<Button>();
                btn.targetGraphic = hitImg;
                int capturedIdx = i;
                btn.onClick.AddListener(() =>
                {
                    CloseActiveDropdown();
                    SetCrashSubMode(0);
                    SetCrashLessonIndex(capturedIdx);
                });

                crashCurriculumRowObjects[i] = rowGo;
                crashCurriculumRowLabels[i] = lbl;
                crashCurriculumPassIcons[i] = passIcon;
                crashCurriculumFailIcons[i] = failIcon;
                crashCurriculumPassTexts[i] = passTxt;
                crashCurriculumFailTexts[i] = failTxt;
            }

            // 2. Bottom-Left Recessed Black Lesson Description Box
            GameObject helpBox = CreateSlicedBox("Box_CrashHubHelp", parent, spBoxBlack9, 108f, 882f, 695f, 175f);
            txtCrashBottomHelp = CreateShadowLabel(
                "Txt_CrashHubHelp",
                helpBox.transform,
                "",
                22f,
                16f,
                650f,
                145f,
                25,
                TextAnchor.UpperLeft,
                new Color32(255, 240, 20, 255)
            );

            // 3. Right Top: School Title, Subtitle, TRAINING Radio, and LESSON Selector
            Color32 navyInk = new Color32(10, 12, 52, 255);
            txtCrashSchoolTitle = CreateHeaderSubLabel("Txt_CrashSchoolHeader", parent, "GOLDEN GATE STUNT DRIVING SCHOOL:", 858f, 118f, 1020f, 40f, 29, navyInk);
            txtCrashSchoolSubtitle = CreateHeaderSubLabel("Txt_CrashSchoolSub", parent, "", 1318f, 156f, 575f, 92f, 22, navyInk);

            BuildCrashSubModeRadioRow(parent, 0, "TRAINING", 900f, 246f);

            CreateShadowLabel("Lbl_CrashLesson", parent, "LESSON", 860f, 332f, 330f, 60f, 38, TextAnchor.MiddleRight, Color.white);
            boxCrashLessonDisabled = CreateSlicedBox("Box_CrashLesson_Disabled", parent, spBoxNavy9, 1215f, 332f, 635f, 62f);

            selectorCrashLessonGroup = CreateUIObject("Group_CrashLessonSelector", parent);
            StretchFull(selectorCrashLessonGroup.GetComponent<RectTransform>());
            txtCrashLessonValue = CreateSelectorBoxWithSteppers(
                selectorCrashLessonGroup.transform,
                "Selector_CrashLesson",
                1215f,
                332f,
                635f,
                60f,
                "",
                onUp: () => CycleCrashLessonIndex(-1),
                onDown: () => CycleCrashLessonIndex(1),
                options: null,
                getCurrentIndex: () => MM2GameSession.CrashLessonIndex,
                onSelectIndex: idx =>
                {
                    SetCrashSubMode(0);
                    SetCrashLessonIndex(idx);
                },
                getOptionsDynamic: () => currentCrashLessonDropdownNames,
                isOptionEnabled: idx => MM2GameSession.IsCrashLessonUnlocked(MM2GameSession.CrashCourseCity, idx),
                customMaxRowsPerCol: 13
            );

            // Horizontal divider separating Training from Work Experience on the right panel
            CreateSolidRect("Divider_CrashRightHoriz", parent, 830f, 428f, 1090f, 4f, new Color32(8, 8, 52, 255));

            // 4. Right Bottom: WORK EXPERIENCE Header, BLITZ / CHECKPOINT Radios, and RACE NAME Selector
            CreateHeaderSubLabel("Txt_WorkExpHeader", parent, "WORK EXPERIENCE:", 858f, 444f, 450f, 40f, 29, navyInk);
            CreateHeaderSubLabel(
                "Txt_WorkExpSub",
                parent,
                "APPLY YOUR TRAINING\nIN THE REAL WORLD\n(NOT FOR CLASS CREDIT)",
                1318f,
                448f,
                575f,
                92f,
                22,
                navyInk
            );

            BuildCrashSubModeRadioRow(parent, 1, "BLITZ", 900f, 554f);
            BuildCrashSubModeRadioRow(parent, 2, "CHECKPOINT", 900f, 620f);

            CreateShadowLabel("Lbl_CrashWorkRace", parent, "RACE NAME", 860f, 716f, 330f, 60f, 38, TextAnchor.MiddleRight, Color.white);
            boxCrashWorkRaceDisabled = CreateSlicedBox("Box_CrashWorkRace_Disabled", parent, spBoxNavy9, 1215f, 716f, 635f, 62f);

            selectorCrashWorkRaceGroup = CreateUIObject("Group_CrashWorkRaceSelector", parent);
            StretchFull(selectorCrashWorkRaceGroup.GetComponent<RectTransform>());
            txtCrashWorkRaceValue = CreateSelectorBoxWithSteppers(
                selectorCrashWorkRaceGroup.transform,
                "Selector_CrashWorkRace",
                1215f,
                716f,
                635f,
                60f,
                "",
                onUp: () => CycleCrashWorkRaceIndex(-1),
                onDown: () => CycleCrashWorkRaceIndex(1),
                options: null,
                getCurrentIndex: () => MM2GameSession.CrashWorkRaceIndex,
                onSelectIndex: SetCrashWorkRaceIndex,
                getOptionsDynamic: () => currentCrashWorkRaceNames,
                isOptionEnabled: idx => MM2PlayerManager.IsRaceUnlocked(
                    MM2GameSession.Location,
                    MM2GameSession.CrashSubMode == 1 ? "Blitz" : "Checkpoint",
                    idx
                ),
                customMaxRowsPerCol: 13
            );

            // 5. Bottom Navigation Buttons: [ <-- BACK ] and [ GO DRIVE --> ]
            CreateLeftBackButton("Btn_BackToCrashIntro", parent, "BACK", 895f, 950f, 315f, 118f, 28, () =>
            {
                ShowScreen(MenuScreen.CrashCourseIntro);
            });
            CreateRightArrowButton("Btn_CrashGoDrive", parent, "GO DRIVE", 1345f, 950f, 545f, 118f, 30, OnCrashCourseGoDriveClicked);
        }

        private Text CreateHeaderSubLabel(
            string name,
            Transform parent,
            string content,
            float x,
            float y,
            float w,
            float h,
            int fontSize,
            Color color
        )
        {
            GameObject go = CreateUIObject(name, parent);
            SetTopLeftRect(go.GetComponent<RectTransform>(), x, y, w, h);
            Text txt = go.AddComponent<Text>();
            txt.font = fontUIExtraBold;
            txt.fontStyle = FontStyle.Normal;
            txt.fontSize = fontSize;
            txt.alignment = TextAnchor.UpperLeft;
            txt.color = color;
            txt.text = content;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            return txt;
        }

        private void BuildCrashSubModeRadioRow(Transform parent, int subModeIdx, string label, float x, float y)
        {
            GameObject radioRow = CreateUIObject($"Radio_CrashSub_{label}", parent);
            SetTopLeftRect(radioRow.GetComponent<RectTransform>(), x, y, 520f, 60f);

            Image hitImg = radioRow.AddComponent<Image>();
            hitImg.color = new Color(0f, 0f, 0f, 0f);

            Image orbImg = CreateSpriteImage("Orb", radioRow.transform, spOrbRed, 0f, 4f, 52f, 52f);
            orbImg.raycastTarget = false;
            Text modeLbl = CreateShadowLabel("Label", radioRow.transform, label, 70f, 0f, 440f, 60f, 42, TextAnchor.MiddleLeft, Color.white);

            radioCrashSubModeOrbs[subModeIdx] = orbImg;
            radioCrashSubModeLabels[subModeIdx] = modeLbl;

            Button btn = radioRow.AddComponent<Button>();
            btn.targetGraphic = hitImg;
            btn.onClick.AddListener(() =>
            {
                CloseActiveDropdown();
                SetCrashSubMode(subModeIdx);
            });
        }

        private void SetCrashSubMode(int subModeIdx)
        {
            MM2GameSession.CrashSubMode = Mathf.Clamp(subModeIdx, 0, 2);
            RefreshCrashCourseHubUI();
        }

        private void CycleCrashLessonIndex(int dir)
        {
            if (currentCrashLessonDropdownNames == null || currentCrashLessonDropdownNames.Length == 0) return;
            SetCrashSubMode(0);
            int len = currentCrashLessonDropdownNames.Length;
            int step = dir >= 0 ? 1 : -1;
            int cur = MM2GameSession.CrashLessonIndex;
            for (int attempt = 1; attempt <= len; attempt++)
            {
                int candidate = (cur + step * attempt + len * 2) % len;
                if (MM2GameSession.IsCrashLessonUnlocked(MM2GameSession.CrashCourseCity, candidate))
                {
                    SetCrashLessonIndex(candidate);
                    return;
                }
            }
        }

        private void SetCrashLessonIndex(int idx)
        {
            if (currentCrashLessonDropdownNames == null || currentCrashLessonDropdownNames.Length == 0) return;
            int clamped = Mathf.Clamp(idx, 0, currentCrashLessonDropdownNames.Length - 1);
            if (!MM2GameSession.IsCrashLessonUnlocked(MM2GameSession.CrashCourseCity, clamped)) return;
            MM2GameSession.CrashLessonIndex = clamped;
            RefreshCrashCourseHubUI();
        }

        private void CycleCrashWorkRaceIndex(int dir)
        {
            if (currentCrashWorkRaceNames == null || currentCrashWorkRaceNames.Length == 0) return;
            string workMode = MM2GameSession.CrashSubMode == 1 ? "Blitz" : "Checkpoint";
            int len = currentCrashWorkRaceNames.Length;
            int step = dir >= 0 ? 1 : -1;
            int cur = MM2GameSession.CrashWorkRaceIndex;
            for (int attempt = 1; attempt <= len; attempt++)
            {
                int candidate = (cur + step * attempt + len * 2) % len;
                if (MM2PlayerManager.IsRaceUnlocked(MM2GameSession.Location, workMode, candidate))
                {
                    SetCrashWorkRaceIndex(candidate);
                    return;
                }
            }
        }

        private void SetCrashWorkRaceIndex(int idx)
        {
            if (currentCrashWorkRaceNames == null || currentCrashWorkRaceNames.Length == 0) return;
            int clamped = Mathf.Clamp(idx, 0, currentCrashWorkRaceNames.Length - 1);
            string workMode = MM2GameSession.CrashSubMode == 1 ? "Blitz" : "Checkpoint";
            if (!MM2PlayerManager.IsRaceUnlocked(MM2GameSession.Location, workMode, clamped)) return;
            MM2GameSession.CrashWorkRaceIndex = clamped;
            RefreshCrashCourseHubUI();
        }

        private void RefreshCrashCourseHubUI()
        {
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.CrashCourseCity);
            if (citySpec == null || !citySpec.HasCrashCourse)
            {
                List<CitySpec> ccCities = MM2RuntimeCityLoader.GetCrashCourseCities();
                if (ccCities.Count > 0)
                {
                    citySpec = ccCities[0];
                    MM2GameSession.CrashCourseCity = citySpec.MapName;
                }
            }
            if (citySpec == null) return;

            MM2GameSession.Location = citySpec.DisplayName;
            if (txtCrashSchoolTitle != null) txtCrashSchoolTitle.text = citySpec.CrashSchoolTitle ?? "";
            if (txtCrashSchoolSubtitle != null) txtCrashSchoolSubtitle.text = citySpec.CrashSchoolSubtitle ?? "";

            MM2CrashLessonSpec[] lessons = citySpec.CrashLessons ?? Array.Empty<MM2CrashLessonSpec>();

            currentCrashLessonDropdownNames = new string[lessons.Length];
            for (int i = 0; i < lessons.Length; i++)
            {
                currentCrashLessonDropdownNames[i] = lessons[i].DropdownLabel;
            }

            if (lessons.Length > 0)
            {
                MM2GameSession.CrashLessonIndex = Mathf.Clamp(MM2GameSession.CrashLessonIndex, 0, lessons.Length - 1);
                if (!MM2GameSession.IsCrashLessonUnlocked(citySpec.MapName, MM2GameSession.CrashLessonIndex))
                {
                    MM2GameSession.CrashLessonIndex = MM2GameSession.GetMaxUnlockedCrashLessonIndex(citySpec.MapName, lessons.Length);
                }
            }
            else
            {
                MM2GameSession.CrashLessonIndex = 0;
            }

            // Populate left curriculum list with extra vertical spacing around Midterms & Final Exam (matching Screenshot 2)
            Color32 royalBlue = new Color32(45, 115, 255, 255);
            Color32 lockedBlue = new Color32(32, 82, 185, 185);
            Color32 activeLessonCol = new Color32(115, 195, 255, 255);
            float curY = 78f;

            for (int i = 0; i < MaxCrashCurriculumRows; i++)
            {
                if (crashCurriculumRowObjects[i] == null) continue;
                if (i >= lessons.Length)
                {
                    crashCurriculumRowObjects[i].SetActive(false);
                    continue;
                }

                MM2CrashLessonSpec lesson = lessons[i];
                if (lesson.IsExam && i > 0)
                {
                    curY += 18f;
                }

                crashCurriculumRowObjects[i].SetActive(true);
                SetTopLeftRect(crashCurriculumRowObjects[i].GetComponent<RectTransform>(), 16f, curY, 662f, 34f);

                bool isUnlocked = MM2GameSession.IsCrashLessonUnlocked(citySpec.MapName, i);
                Button rowBtn = crashCurriculumRowObjects[i].GetComponent<Button>();
                if (rowBtn != null)
                {
                    rowBtn.interactable = isUnlocked;
                }

                bool isSelectedLesson = (MM2GameSession.CrashSubMode == 0 && i == MM2GameSession.CrashLessonIndex);
                if (crashCurriculumRowLabels[i] != null)
                {
                    crashCurriculumRowLabels[i].text = lesson.CurriculumLabel;
                    crashCurriculumRowLabels[i].color = isSelectedLesson
                        ? activeLessonCol
                        : (isUnlocked ? royalBlue : lockedBlue);
                }

                int status = MM2GameSession.GetCrashLessonStatus(citySpec.MapName, i);
                bool passed = (status == 1);
                bool failed = (status == 2);

                bool usePassSprite = passed && spCcSmChk != null;
                bool useFailSprite = failed && spCcSmFail != null;
                if (crashCurriculumPassIcons[i] != null)
                {
                    crashCurriculumPassIcons[i].sprite = spCcSmChk;
                    SetTopLeftRect(crashCurriculumPassIcons[i].GetComponent<RectTransform>(), 483f, 6f, 22f, 22f);
                    crashCurriculumPassIcons[i].gameObject.SetActive(usePassSprite);
                }
                if (crashCurriculumFailIcons[i] != null)
                {
                    crashCurriculumFailIcons[i].sprite = spCcSmFail;
                    SetTopLeftRect(crashCurriculumFailIcons[i].GetComponent<RectTransform>(), 599f, 6f, 22f, 22f);
                    crashCurriculumFailIcons[i].gameObject.SetActive(useFailSprite);
                }
                if (crashCurriculumPassTexts[i] != null)
                {
                    crashCurriculumPassTexts[i].text = (passed && !usePassSprite) ? "✓" : "";
                }
                if (crashCurriculumFailTexts[i] != null)
                {
                    crashCurriculumFailTexts[i].text = (failed && !useFailSprite) ? "X" : "";
                }

                curY += lesson.IsExam ? 52f : 35f;
            }

            // Update Training vs Work Experience radio buttons
            Color32 selectedGreen = new Color32(15, 225, 65, 255);
            for (int m = 0; m < 3; m++)
            {
                bool active = (m == MM2GameSession.CrashSubMode);
                if (radioCrashSubModeOrbs[m] != null) radioCrashSubModeOrbs[m].sprite = active ? spOrbGreen : spOrbRed;
                if (radioCrashSubModeLabels[m] != null) radioCrashSubModeLabels[m].color = active ? selectedGreen : Color.white;
            }

            bool isTraining = (MM2GameSession.CrashSubMode == 0);
            if (boxCrashLessonDisabled != null) boxCrashLessonDisabled.SetActive(!isTraining || lessons.Length == 0);
            if (selectorCrashLessonGroup != null) selectorCrashLessonGroup.SetActive(isTraining && lessons.Length > 0);
            if (txtCrashLessonValue != null)
            {
                txtCrashLessonValue.text = (isTraining && lessons.Length > 0)
                    ? lessons[MM2GameSession.CrashLessonIndex].DropdownLabel
                    : "";
            }

            if (!isTraining)
            {
                string workMode = MM2GameSession.CrashSubMode == 1 ? "Blitz" : "Checkpoint";
                currentCrashWorkRaceNames = MM2RuntimeCityLoader.GetRaceNames(citySpec.DisplayName, workMode) ?? Array.Empty<string>();
                if (currentCrashWorkRaceNames.Length > 0)
                {
                    MM2GameSession.CrashWorkRaceIndex = Mathf.Clamp(MM2GameSession.CrashWorkRaceIndex, 0, currentCrashWorkRaceNames.Length - 1);
                    if (!MM2PlayerManager.IsRaceUnlocked(citySpec.DisplayName, workMode, MM2GameSession.CrashWorkRaceIndex))
                    {
                        MM2GameSession.CrashWorkRaceIndex = 0;
                    }
                }
                else
                {
                    MM2GameSession.CrashWorkRaceIndex = 0;
                }
            }
            else
            {
                currentCrashWorkRaceNames = Array.Empty<string>();
            }

            bool showWorkSelector = !isTraining && currentCrashWorkRaceNames.Length > 0;
            if (boxCrashWorkRaceDisabled != null) boxCrashWorkRaceDisabled.SetActive(!showWorkSelector);
            if (selectorCrashWorkRaceGroup != null) selectorCrashWorkRaceGroup.SetActive(showWorkSelector);
            if (txtCrashWorkRaceValue != null)
            {
                txtCrashWorkRaceValue.text = showWorkSelector
                    ? currentCrashWorkRaceNames[MM2GameSession.CrashWorkRaceIndex]
                    : "";
            }

            if (txtCrashBottomHelp != null)
            {
                if (isTraining && lessons.Length > 0)
                {
                    txtCrashBottomHelp.text = lessons[MM2GameSession.CrashLessonIndex].Description;
                }
                else if (MM2GameSession.CrashSubMode == 1)
                {
                    txtCrashBottomHelp.text = "Practice your skills in a Blitz race! Pass through all checkpoints before time runs out.";
                }
                else
                {
                    txtCrashBottomHelp.text = "Apply your training in a Checkpoint race against rival drivers across the city!";
                }
            }
        }

        private void OnCrashCourseGoDriveClicked()
        {
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.CrashCourseCity);
            if (citySpec == null) return;

            MM2GameSession.IsCrashCourseSession = true;
            MM2GameSession.Location = citySpec.DisplayName;

            // Assign the city's configured training vehicle (e.g. vpbullet or vpcab)
            VehicleSpec vehSpec = VehicleCatalog.GetSpec(citySpec.CrashDefaultVehicle);
            if (vehSpec != null)
            {
                MM2GameSession.VehicleName = vehSpec.DisplayName;
                if (vehSpec.Colors != null && vehSpec.Colors.Length > 0)
                {
                    int colorIdx = Mathf.Clamp(citySpec.CrashDefaultColor, 0, vehSpec.Colors.Length - 1);
                    MM2GameSession.VehicleColor = vehSpec.Colors[colorIdx];
                }
            }

            if (MM2GameSession.CrashSubMode == 0 && citySpec.CrashLessons != null && citySpec.CrashLessons.Length > 0)
            {
                int lIdx = Mathf.Clamp(MM2GameSession.CrashLessonIndex, 0, citySpec.CrashLessons.Length - 1);
                MM2CrashLessonSpec lesson = citySpec.CrashLessons[lIdx];
                MM2CrashLessonStage stage0 = lesson.Stages[0];

                MM2GameSession.CrashLessonIndex = lIdx;
                MM2GameSession.RaceType = "CrashCourse";
                MM2GameSession.RaceIndex = lIdx;
                MM2GameSession.RaceName = lesson.DropdownLabel;
                MM2GameSession.RaceTimeLimit = stage0.TimeLimit;
                MM2GameSession.NumLaps = 1;
                MM2GameSession.NumOpponents = stage0.NumOpponents;
                MM2GameSession.TimeOfDay = timesOfDay[Mathf.Clamp(lesson.TimeOfDayIndex, 0, timesOfDay.Length - 1)];
                MM2GameSession.Weather = weathers[Mathf.Clamp(lesson.WeatherIndex, 0, weathers.Length - 1)];
                MM2GameSession.PedestrianDensity = lesson.PedestrianDensity;
                MM2GameSession.TrafficDensity = lesson.TrafficDensity;
                MM2GameSession.CopDensity = lesson.CopDensity;
            }
            else
            {
                // Work Experience mode (Blitz or Checkpoint in the school's city with the school's vehicle)
                string workMode = MM2GameSession.CrashSubMode == 1 ? "Blitz" : "Checkpoint";
                string[] rNames = MM2RuntimeCityLoader.GetRaceNames(citySpec.DisplayName, workMode) ?? Array.Empty<string>();
                int rIdx = rNames.Length > 0 ? Mathf.Clamp(MM2GameSession.CrashWorkRaceIndex, 0, rNames.Length - 1) : 0;

                MM2GameSession.RaceType = workMode;
                MM2GameSession.RaceIndex = rIdx;
                MM2GameSession.RaceName = rNames.Length > 0 ? rNames[rIdx] : workMode;

                MM2RaceMetadata meta = MM2RuntimeCityLoader.GetRaceMetadata(citySpec.DisplayName, workMode, rIdx);
                MM2GameSession.RaceTimeLimit = Mathf.Max(5f, meta.TimeLimit);
                MM2GameSession.NumLaps = Mathf.Clamp(meta.NumLaps > 0 ? meta.NumLaps : 3, 1, 10);
                MM2GameSession.NumOpponents = Mathf.Clamp(meta.Opponents, 0, 8);
                MM2GameSession.TimeOfDay = timesOfDay[Mathf.Clamp(meta.TimeOfDayIndex, 0, timesOfDay.Length - 1)];
                MM2GameSession.Weather = weathers[Mathf.Clamp(meta.WeatherIndex, 0, weathers.Length - 1)];
                MM2GameSession.PedestrianDensity = Mathf.Clamp(meta.PedestrianDensity, 0, 100);
                MM2GameSession.TrafficDensity = Mathf.Clamp(meta.TrafficDensity, 0, 100);
                MM2GameSession.CopDensity = Mathf.Clamp(meta.CopDensity, 0, 100);
            }

            OnGoDriveClicked();
        }

        // =====================================================================
        // SCREEN 6: OPTIONS SCREEN (DISPLAY, RESOLUTION, WINDOW MODE, LIVE 3D UI) & MODALS
        // =====================================================================
        private void PopulateAvailableDisplays()
        {
            availableDisplays.Clear();
            try
            {
                Screen.GetDisplayLayout(availableDisplays);
            }
            catch
            {
                // Ignore if unavailable on platform
            }

            if (availableDisplays.Count == 0)
            {
                availableDisplays.Add(Screen.mainWindowDisplayInfo);
            }

            displayLabels = new string[availableDisplays.Count];
            for (int i = 0; i < availableDisplays.Count; i++)
            {
                DisplayInfo d = availableDisplays[i];
                string rawName = !string.IsNullOrWhiteSpace(d.name) ? d.name.Trim() : $"Display {i + 1}";
                int w = d.width > 0 ? d.width : Screen.width;
                int h = d.height > 0 ? d.height : Screen.height;
                displayLabels[i] = $"{i + 1}: {rawName} ({w}x{h})";
            }

            if (MM2GameSession.TryResolveSavedDisplay(availableDisplays, out int savedIdx, out _))
            {
                idxDisplay = savedIdx;
            }
            else
            {
                DisplayInfo currentDisp = Screen.mainWindowDisplayInfo;
                int found = 0;
                for (int i = 0; i < availableDisplays.Count; i++)
                {
                    if (availableDisplays[i].Equals(currentDisp))
                    {
                        found = i;
                        break;
                    }
                }
                idxDisplay = found;
            }

            idxWindowMode = MM2GameSession.SavedWindowMode >= 0
                ? Mathf.Clamp(MM2GameSession.SavedWindowMode, 0, windowModeLabels.Length - 1)
                : MM2GameSession.WindowModeIndexFromFullScreenMode(Screen.fullScreenMode);
        }

        private void ApplySavedDisplayOnStartup()
        {
            PopulateAvailableDisplays();
            if (MM2GameSession.TryResolveSavedDisplay(availableDisplays, out int targetIdx, out DisplayInfo targetDisp))
            {
                if (!Screen.mainWindowDisplayInfo.Equals(targetDisp))
                {
                    if (activeMoveDisplayCoroutine != null)
                    {
                        StopCoroutine(activeMoveDisplayCoroutine);
                    }
                    activeMoveDisplayCoroutine = StartCoroutine(MoveWindowToDisplayCoroutine(targetDisp, targetIdx, silentStatus: true));
                }
            }
        }

        private void PopulateAvailableResolutions()
        {
            availableResolutions.Clear();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            void AddRes(int w, int h)
            {
                if (w < 640 || h < 480) return;
                string key = $"{w}x{h}";
                if (seen.Add(key))
                {
                    availableResolutions.Add(new Vector2Int(w, h));
                }
            }

            // Include standard desktop/widescreen resolutions plus all OS-reported Screen.resolutions
            AddRes(1024, 768);
            AddRes(1280, 720);
            AddRes(1366, 768);
            AddRes(1600, 900);
            AddRes(1920, 1080);
            AddRes(1920, 1200);
            AddRes(2560, 1080);
            AddRes(2560, 1440);
            AddRes(3440, 1440);
            AddRes(3840, 2160);

            for (int i = 0; i < availableDisplays.Count; i++)
            {
                AddRes(availableDisplays[i].width, availableDisplays[i].height);
            }

            Resolution[] sysRes = Screen.resolutions;
            if (sysRes != null)
            {
                for (int i = 0; i < sysRes.Length; i++)
                {
                    AddRes(sysRes[i].width, sysRes[i].height);
                }
            }

            if (MM2GameSession.SavedResolutionWidth >= 640 && MM2GameSession.SavedResolutionHeight >= 480)
            {
                AddRes(MM2GameSession.SavedResolutionWidth, MM2GameSession.SavedResolutionHeight);
            }
            AddRes(Screen.width, Screen.height);

            availableResolutions.Sort((a, b) =>
            {
                int cmpW = a.x.CompareTo(b.x);
                return cmpW != 0 ? cmpW : a.y.CompareTo(b.y);
            });

            int targetW = MM2GameSession.SavedResolutionWidth >= 640 ? MM2GameSession.SavedResolutionWidth : Screen.width;
            int targetH = MM2GameSession.SavedResolutionHeight >= 480 ? MM2GameSession.SavedResolutionHeight : Screen.height;

            resolutionLabels = new string[availableResolutions.Count];
            int bestIdx = 0;
            int bestDist = int.MaxValue;
            for (int i = 0; i < availableResolutions.Count; i++)
            {
                Vector2Int r = availableResolutions[i];
                resolutionLabels[i] = $"{r.x} x {r.y}";
                int dist = Mathf.Abs(r.x - targetW) + Mathf.Abs(r.y - targetH);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            idxResolution = bestIdx;
        }

        private void BuildOptionsScreen(Transform parent)
        {
            PopulateAvailableDisplays();
            PopulateAvailableResolutions();

            // 1. Left Column Recessed Black Box: Configuration Overview
            GameObject leftBox = CreateSlicedBox("Box_OptionsInfo", parent, spBoxBlack9, 118f, 132f, 688f, 706f);
            Color32 headerBlue = new Color32(48, 128, 255, 255);
            Color32 bodyWhite = new Color32(220, 228, 240, 255);

            CreateShadowLabel(
                "Lbl_OptionsOverviewTitle",
                leftBox.transform,
                "Configuration",
                24f,
                22f,
                640f,
                46f,
                34,
                TextAnchor.MiddleLeft,
                headerBlue,
                fontUIExtraBold
            );

            CreateShadowLabel(
                "Lbl_OptionsOverviewBody",
                leftBox.transform,
                "Configure display monitor, resolution, window mode, and menu presentation settings.\n\n" +
                "• Display Monitor: Choose which connected screen (e.g. laptop panel or external monitor) displays the game.\n\n" +
                "• Display Resolution: Select the rendering resolution for the game window.\n\n" +
                "• Window Mode: Switch between Fullscreen Window, Exclusive Fullscreen, or Windowed.\n\n" +
                "• Live UI Background: Renders a real-time 3D scene behind the menus instead of the static backdrop.\n\n" +
                "• Show FPS Counter: Displays a real-time frames-per-second counter during gameplay.",
                24f,
                90f,
                640f,
                580f,
                25,
                TextAnchor.UpperLeft,
                bodyWhite,
                fontUIBold
            );

            // 2. Bottom-Left Recessed Black Status Box
            GameObject helpBox = CreateSlicedBox("Box_OptionsBottomHelp", parent, spBoxBlack9, 108f, 882f, 695f, 175f);
            txtOptionsBottomHelp = CreateShadowLabel(
                "Txt_OptionsBottomHelp",
                helpBox.transform,
                "Adjust display and interface settings on the right, or click BACK to return.",
                22f,
                16f,
                650f,
                145f,
                25,
                TextAnchor.UpperLeft,
                new Color32(255, 240, 20, 255)
            );

            // 3. Right Side Section Header
            Color32 navyHeading = new Color32(8, 16, 84, 255);
            CreateHeaderSubLabel("Lbl_OptionsSectionGraphics", parent, "DISPLAY & INTERFACE OPTIONS:", 876f, 120f, 980f, 44f, 32, navyHeading);

            // 4a. Display / Monitor Dropdown Selector
            CreateShadowLabel("Lbl_Display", parent, "DISPLAY", 860f, 195f, 330f, 60f, 36, TextAnchor.MiddleRight, Color.white);
            txtDisplayValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_Display",
                1215f,
                195f,
                580f,
                60f,
                displayLabels.Length > 0 ? displayLabels[idxDisplay] : "1: Display 1",
                onUp: () => CycleDisplay(-1),
                onDown: () => CycleDisplay(1),
                options: displayLabels,
                getCurrentIndex: () => idxDisplay,
                onSelectIndex: SetDisplayIndex,
                getOptionsDynamic: () => displayLabels
            );

            // 4b. Resolution Dropdown Selector
            CreateShadowLabel("Lbl_Resolution", parent, "RESOLUTION", 860f, 275f, 330f, 60f, 36, TextAnchor.MiddleRight, Color.white);
            txtResolutionValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_Resolution",
                1215f,
                275f,
                580f,
                60f,
                resolutionLabels.Length > 0 ? resolutionLabels[idxResolution] : $"{Screen.width} x {Screen.height}",
                onUp: () => CycleResolution(-1),
                onDown: () => CycleResolution(1),
                options: resolutionLabels,
                getCurrentIndex: () => idxResolution,
                onSelectIndex: SetResolutionIndex,
                getOptionsDynamic: () => resolutionLabels
            );

            // 4c. Window Mode Dropdown Selector
            CreateShadowLabel("Lbl_WindowMode", parent, "WINDOW MODE", 860f, 355f, 330f, 60f, 36, TextAnchor.MiddleRight, Color.white);
            txtWindowModeValue = CreateSelectorBoxWithSteppers(
                parent,
                "Selector_WindowMode",
                1215f,
                355f,
                580f,
                60f,
                windowModeLabels[Mathf.Clamp(idxWindowMode, 0, windowModeLabels.Length - 1)],
                onUp: () => CycleWindowMode(-1),
                onDown: () => CycleWindowMode(1),
                options: windowModeLabels,
                getCurrentIndex: () => idxWindowMode,
                onSelectIndex: SetWindowModeIndex,
                getOptionsDynamic: () => windowModeLabels
            );

            CreateSolidRect("Divider_OptionsMid", parent, 832f, 445f, 1088f, 4f, new Color32(8, 8, 52, 255));

            // 5. Checkbox: Enable / Disable Live 3D UI Background (Disabled by default)
            GameObject chkRow = CreateUIObject("Row_ToggleLiveUI", parent);
            SetTopLeftRect(chkRow.GetComponent<RectTransform>(), 910f, 480f, 860f, 64f);
            Image rowHit = chkRow.AddComponent<Image>();
            rowHit.color = new Color(0f, 0f, 0f, 0f);

            imgLiveUiCheckboxBox = CreateSpriteImage(
                "Box_LiveUiCheckbox",
                chkRow.transform,
                MM2GameSession.EnableLiveMenuBackground ? spCheckboxCheckedGreen : spCheckboxUnchecked,
                0f,
                6f,
                52f,
                52f
            );
            imgLiveUiCheckboxBox.raycastTarget = false;
            imgLiveUiCheckTick = null;
            txtLiveUiCheckMark = null;

            CreateShadowLabel(
                "Lbl_LiveUiCheckbox",
                chkRow.transform,
                "LIVE 3D UI BACKGROUND",
                74f,
                4f,
                760f,
                56f,
                36,
                TextAnchor.MiddleLeft,
                Color.white
            );

            Button chkBtn = chkRow.AddComponent<Button>();
            chkBtn.targetGraphic = rowHit;
            chkBtn.onClick.AddListener(() =>
            {
                CloseActiveDropdown();
                ToggleLiveUiBackground();
            });

            // 5b. Checkbox: Enable / Disable FPS Counter (Disabled by default)
            GameObject fpsChkRow = CreateUIObject("Row_ToggleFpsCounter", parent);
            SetTopLeftRect(fpsChkRow.GetComponent<RectTransform>(), 910f, 560f, 860f, 64f);
            Image fpsRowHit = fpsChkRow.AddComponent<Image>();
            fpsRowHit.color = new Color(0f, 0f, 0f, 0f);

            imgFpsCheckboxBox = CreateSpriteImage(
                "Box_FpsCheckbox",
                fpsChkRow.transform,
                MM2GameSession.ShowFpsCounter ? spCheckboxCheckedGreen : spCheckboxUnchecked,
                0f,
                6f,
                52f,
                52f
            );
            imgFpsCheckboxBox.raycastTarget = false;

            CreateShadowLabel(
                "Lbl_FpsCheckbox",
                fpsChkRow.transform,
                "SHOW FPS COUNTER",
                74f,
                4f,
                760f,
                56f,
                36,
                TextAnchor.MiddleLeft,
                Color.white
            );

            Button fpsChkBtn = fpsChkRow.AddComponent<Button>();
            fpsChkBtn.targetGraphic = fpsRowHit;
            fpsChkBtn.onClick.AddListener(() =>
            {
                CloseActiveDropdown();
                ToggleFpsCounter();
            });

            // 6. Bottom-Left Navigation on Right Side: [ <-- BACK ]
            CreateLeftBackButton("Btn_OptionsBack", parent, "BACK", 895f, 950f, 315f, 118f, 28, () =>
            {
                ShowScreen(previousScreenBeforeOptions);
            });

            RefreshOptionsUI();
        }

        private void BuildHelpScreen(Transform parent)
        {
            // Full-width recessed box spanning across the screen (x: 118..1886, y: 122..926 -> 1768 x 804)
            float boxX = 118f;
            float boxY = 122f;
            float boxW = 1768f;
            float boxH = 804f;

            GameObject helpBox = CreateSlicedBox("Box_HelpFullScroll", parent, spBoxBlack9, boxX, boxY, boxW, boxH);

            bool prevSuppress = suppressResponsiveRegistration;
            suppressResponsiveRegistration = true;
            try
            {
                helpScrollRect = CreateScrollableTableViewport(
                    helpBox.transform,
                    boxW,
                    boxH,
                    10f,
                    out RectTransform contentRect,
                    out float contentInnerW,
                    out float _
                );

                TextAsset oflAsset = Resources.Load<TextAsset>("Fonts/OFL");
                string oflLicenseText = (oflAsset != null && !string.IsNullOrEmpty(oflAsset.text))
                    ? oflAsset.text.Trim()
                    : "Copyright 2020 The Open Sans Project Authors (https://github.com/googlefonts/opensans)\n" +
                      "This Font Software is licensed under the SIL Open Font License, Version 1.1.\n" +
                      "https://openfontlicense.org";

                string helpDocument =
                    "<size=28><color=#FFE818>OPEN MADNESS</color></size>\n" +
                    "<color=#4882EB>────────────────────────────────────────────────────────────────────────────────</color>\n" +
                    "Open Madness is an independent, clean-room open-source game engine reimplementation built in Unity that reads cities, vehicles, race missions, physics tuning, and audio directly from user-supplied (.ar) archive files.\n\n\n" +
                    "<size=28><color=#FFE818>LEGAL & TRADEMARK DISCLAIMER</color></size>\n" +
                    "<color=#4882EB>────────────────────────────────────────────────────────────────────────────────</color>\n" +
                    "Open Madness is an independent community project and is NOT affiliated with, endorsed by, or sponsored by Microsoft Corporation, Angel Studios, or any vehicle manufacturers.\n\n" +
                    "NO original game assets, textures, 3D models, audio files, or (.ar) archives are included with this distribution. You must own a legitimate copy of Midtown Madness 2 (or Midtown Madness 1) and supply your own (.ar) archive files to play original content.\n\n\n" +
                    "<size=28><color=#FFE818>SETUP INSTRUCTIONS (REQUIRED .AR FILES)</color></size>\n" +
                    "<color=#4882EB>────────────────────────────────────────────────────────────────────────────────</color>\n" +
                    "1. Locate the 'resources/' folder next to the OpenMadness executable.\n" +
                    "2. Copy your original Midtown Madness 2 (.ar) archive files into 'resources/':\n" +
                    "   • mm2core.ar, mm2tex.ar, mm2aud.ar, mm2audex.ar (plus any city/vehicle .ar files)\n" +
                    "3. (Optional) Copy Midtown Madness 1 archives (core.ar, ui.ar, audio.ar) or community mod (.ar) packages into 'resources/' to load additional cities and vehicles.\n" +
                    "4. Player profiles and race records are stored in 'resources/players/' and are created automatically on first launch.\n\n\n" +
                    "<size=28><color=#FFE818>DRIVING & MENU CONTROLS</color></size>\n" +
                    "<color=#4882EB>────────────────────────────────────────────────────────────────────────────────</color>\n" +
                    "• Throttle / Brake & Reverse:   Up / Down Arrows  or  W / S\n" +
                    "• Steer Left / Right:           Left / Right Arrows  or  A / D\n" +
                    "• Handbrake:                    Spacebar\n" +
                    "• Horn / Police Siren:          H\n" +
                    "• Headlights Toggle:            L\n" +
                    "• Cycle Camera View:            C  (Near Chase, Far Chase, Bumper, Pov/Dash)\n" +
                    "• Look Back / Left / Right:     NumPad / Look Keys\n" +
                    "• Toggle HUD / Minimap:         Tab / M\n" +
                    "• Pause / Return to Menu:       Escape\n\n\n" +
                    "<size=28><color=#FFE818>THIRD-PARTY OPEN SOURCE NOTICES</color></size>\n" +
                    "<color=#4882EB>────────────────────────────────────────────────────────────────────────────────</color>\n" +
                    "Open Sans Font Family (v3.003)\n" +
                    "Files: OpenSans-Bold.ttf, OpenSans-ExtraBold.ttf, OpenSans-ExtraBoldItalic.ttf, OpenSans-CondensedExtraBold.ttf\n\n" +
                    "<size=21><color=#C3D0E8>" + oflLicenseText + "</color></size>";

                float padX = 28f;
                float padY = 20f;
                float textW = Mathf.Max(400f, contentInnerW - padX * 2f);

                // Plain 1-quad-per-character UI.Text (with Rich Text enabled and no 10x outline mesh multiplier)
                GameObject txtGo = CreateUIObject("Txt_HelpScrollBody", contentRect);
                SetTopLeftRect(txtGo.GetComponent<RectTransform>(), padX, padY, textW, 2200f);

                Text txtBody = txtGo.AddComponent<Text>();
                txtBody.font = fontUIBold;
                txtBody.fontStyle = FontStyle.Normal;
                txtBody.fontSize = 23;
                txtBody.alignment = TextAnchor.UpperLeft;
                txtBody.color = new Color32(235, 240, 250, 255);
                txtBody.supportRichText = true;
                txtBody.horizontalOverflow = HorizontalWrapMode.Wrap;
                txtBody.verticalOverflow = VerticalWrapMode.Overflow;
                txtBody.raycastTarget = false;
                txtBody.text = helpDocument;

                Vector2 genExtents = new Vector2(textW, 0f);
                TextGenerationSettings settings = txtBody.GetGenerationSettings(genExtents);
                float measuredHeight = txtBody.cachedTextGeneratorForLayout.GetPreferredHeight(helpDocument, settings) / Mathf.Max(0.01f, txtBody.pixelsPerUnit);
                float finalContentH = Mathf.Max(boxH - 28f, measuredHeight + padY * 2f + 32f);
                txtBody.rectTransform.sizeDelta = new Vector2(textW, finalContentH);
                contentRect.sizeDelta = new Vector2(contentInnerW, finalContentH);
                helpScrollRect.verticalNormalizedPosition = 1f;
            }
            finally
            {
                suppressResponsiveRegistration = prevSuppress;
            }

            // Bottom [ <-- BACK ] Button
            CreateLeftBackButton("Btn_HelpBack", parent, "BACK", 895f, 950f, 315f, 118f, 28, () =>
            {
                ShowScreen(previousScreenBeforeOptions);
            });
        }

        private void CycleDisplay(int dir)
        {
            if (availableDisplays.Count == 0) return;
            SetDisplayIndex((idxDisplay + dir + availableDisplays.Count) % availableDisplays.Count);
        }

        private void SetDisplayIndex(int idx)
        {
            PopulateAvailableDisplays();
            if (availableDisplays.Count == 0) return;

            idxDisplay = Mathf.Clamp(idx, 0, availableDisplays.Count - 1);
            DisplayInfo targetDisplay = availableDisplays[idxDisplay];
            MM2GameSession.SavedDisplayIndex = idxDisplay;
            MM2GameSession.SavedDisplayName = targetDisplay.name ?? "";
            PlayerPrefs.SetInt("UnitySelectMonitor", idxDisplay);
            PlayerPrefs.Save();
            MM2GameSession.SaveSettings();

            if (activeMoveDisplayCoroutine != null)
            {
                StopCoroutine(activeMoveDisplayCoroutine);
            }
            activeMoveDisplayCoroutine = StartCoroutine(MoveWindowToDisplayCoroutine(targetDisplay, idxDisplay, silentStatus: false));
        }

        private IEnumerator MoveWindowToDisplayCoroutine(DisplayInfo targetDisplay, int targetIdx, bool silentStatus)
        {
            FullScreenMode desiredMode = MM2GameSession.GetDesiredFullScreenMode();
            int targetW = MM2GameSession.SavedResolutionWidth >= 640
                ? MM2GameSession.SavedResolutionWidth
                : (targetDisplay.width >= 640 ? targetDisplay.width : Screen.width);
            int targetH = MM2GameSession.SavedResolutionHeight >= 480
                ? MM2GameSession.SavedResolutionHeight
                : (targetDisplay.height >= 480 ? targetDisplay.height : Screen.height);

            AsyncOperation moveOp = null;
            try
            {
                moveOp = Screen.MoveMainWindowTo(targetDisplay, Vector2Int.zero);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2 Settings] MoveMainWindowTo failed: {ex.Message}");
            }

            if (moveOp != null)
            {
                yield return moveOp;
            }
            yield return null;

            // On Linux X11/Wayland, some window managers ignore moving a fullscreen window across outputs
            // until it is briefly un-fullscreened, moved to the target monitor's workArea, and restored.
            if (!Screen.mainWindowDisplayInfo.Equals(targetDisplay) && Screen.fullScreenMode != FullScreenMode.Windowed)
            {
                int winW = Mathf.Clamp(targetW, 640, Mathf.Max(640, targetDisplay.width));
                int winH = Mathf.Clamp(targetH, 480, Mathf.Max(480, targetDisplay.height));
                Screen.SetResolution(winW, winH, FullScreenMode.Windowed);
                yield return null;
                yield return null;

                Vector2Int centeredPos = new Vector2Int(
                    Mathf.Max(0, (targetDisplay.width - winW) / 2),
                    Mathf.Max(0, (targetDisplay.height - winH) / 2)
                );
                AsyncOperation fallbackOp = null;
                try
                {
                    fallbackOp = Screen.MoveMainWindowTo(targetDisplay, centeredPos);
                }
                catch
                {
                    // Ignore
                }
                if (fallbackOp != null)
                {
                    yield return fallbackOp;
                }
                yield return null;
            }

            Screen.SetResolution(targetW, targetH, desiredMode);
            yield return null;

            PopulateAvailableDisplays();
            PopulateAvailableResolutions();
            RefreshOptionsUI();

            if (!silentStatus && txtOptionsBottomHelp != null)
            {
                string dispLabel = (targetIdx >= 0 && targetIdx < displayLabels.Length)
                    ? displayLabels[targetIdx]
                    : $"Display {targetIdx + 1}";
                txtOptionsBottomHelp.text = $"Display switched to {dispLabel} (saved to settings.json).";
            }

            activeMoveDisplayCoroutine = null;
        }

        private void CycleResolution(int dir)
        {
            if (availableResolutions.Count == 0) return;
            SetResolutionIndex((idxResolution + dir + availableResolutions.Count) % availableResolutions.Count);
        }

        private void SetResolutionIndex(int idx)
        {
            if (availableResolutions.Count == 0) return;
            idxResolution = Mathf.Clamp(idx, 0, availableResolutions.Count - 1);
            Vector2Int res = availableResolutions[idxResolution];
            MM2GameSession.SavedResolutionWidth = res.x;
            MM2GameSession.SavedResolutionHeight = res.y;
            Screen.SetResolution(res.x, res.y, MM2GameSession.GetDesiredFullScreenMode());
            MM2GameSession.SaveSettings();
            RefreshOptionsUI();
            if (txtOptionsBottomHelp != null)
            {
                txtOptionsBottomHelp.text = $"Display resolution set to {res.x} x {res.y} (saved to settings.json).";
            }
        }

        private void CycleWindowMode(int dir)
        {
            SetWindowModeIndex((idxWindowMode + dir + windowModeLabels.Length) % windowModeLabels.Length);
        }

        private void SetWindowModeIndex(int idx)
        {
            idxWindowMode = Mathf.Clamp(idx, 0, windowModeLabels.Length - 1);
            MM2GameSession.SavedWindowMode = idxWindowMode;
            FullScreenMode mode = MM2GameSession.GetDesiredFullScreenMode();
            int targetW = MM2GameSession.SavedResolutionWidth >= 640 ? MM2GameSession.SavedResolutionWidth : Screen.width;
            int targetH = MM2GameSession.SavedResolutionHeight >= 480 ? MM2GameSession.SavedResolutionHeight : Screen.height;
            Screen.SetResolution(targetW, targetH, mode);
            MM2GameSession.SaveSettings();
            RefreshOptionsUI();
            if (txtOptionsBottomHelp != null)
            {
                txtOptionsBottomHelp.text = $"Window mode set to {windowModeLabels[idxWindowMode]} (saved to settings.json).";
            }
        }

        private void ToggleLiveUiBackground()
        {
            MM2GameSession.EnableLiveMenuBackground = !MM2GameSession.EnableLiveMenuBackground;
            MM2GameSession.SaveSettings();
            if (MM2GameSession.EnableLiveMenuBackground)
            {
                BuildLiveMenuBackgroundScene();
            }
            else
            {
                CleanupLiveMenuBackground();
            }
            RefreshOptionsUI();
            if (txtOptionsBottomHelp != null)
            {
                txtOptionsBottomHelp.text = MM2GameSession.EnableLiveMenuBackground
                    ? "Live 3D San Francisco menu background enabled (saved to settings.json)."
                    : "Live 3D menu background disabled (saved to settings.json).";
            }
        }

        private void ToggleFpsCounter()
        {
            MM2GameSession.ShowFpsCounter = !MM2GameSession.ShowFpsCounter;
            MM2GameSession.SaveSettings();
            RefreshOptionsUI();
            if (txtOptionsBottomHelp != null)
            {
                txtOptionsBottomHelp.text = MM2GameSession.ShowFpsCounter
                    ? "FPS counter enabled (saved to settings.json)."
                    : "FPS counter disabled (saved to settings.json).";
            }
        }

        private void RefreshOptionsUI()
        {
            if (txtDisplayValue != null && idxDisplay >= 0 && idxDisplay < displayLabels.Length)
            {
                txtDisplayValue.text = displayLabels[idxDisplay];
            }

            if (txtResolutionValue != null && idxResolution >= 0 && idxResolution < resolutionLabels.Length)
            {
                txtResolutionValue.text = resolutionLabels[idxResolution];
            }

            if (txtWindowModeValue != null && idxWindowMode >= 0 && idxWindowMode < windowModeLabels.Length)
            {
                txtWindowModeValue.text = windowModeLabels[idxWindowMode];
            }

            bool liveOn = MM2GameSession.EnableLiveMenuBackground;
            if (imgLiveUiCheckboxBox != null)
            {
                imgLiveUiCheckboxBox.sprite = liveOn ? spCheckboxCheckedGreen : spCheckboxUnchecked;
            }
            if (imgLiveUiCheckTick != null)
            {
                imgLiveUiCheckTick.gameObject.SetActive(false);
            }
            if (txtLiveUiCheckMark != null)
            {
                txtLiveUiCheckMark.gameObject.SetActive(false);
            }
            if (imgFpsCheckboxBox != null)
            {
                imgFpsCheckboxBox.sprite = MM2GameSession.ShowFpsCounter ? spCheckboxCheckedGreen : spCheckboxUnchecked;
            }
        }

        // =====================================================================
        // MODAL DIALOGS (CREATE DRIVER, DELETE DRIVER CONFIRMATION & '?' HELP)
        // =====================================================================
        private void CloseActiveModal()
        {
            activeCreateDriverInput = null;
            if (activeModalOverlay != null)
            {
                Destroy(activeModalOverlay);
                activeModalOverlay = null;
            }
        }

        private void ConfirmCreateNewDriverFromModal()
        {
            string requestedName = activeCreateDriverInput != null ? activeCreateDriverInput.text : null;
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                requestedName = requestedName.Trim();
            }
            byte skillLevel = activeCreateDriverSkillLevel;
            CloseActiveModal();

            var created = MM2PlayerManager.CreateNewPlayer(requestedName, skillLevel);
            mainMenuStatsViewMode = 0;
            ApplySelectedDriverToMenuUI();
            if (txtMainMenuBottomHelp != null && created != null)
            {
                string skillLabel = created.SkillLevel != 0 ? "Pro" : "Amateur";
                txtMainMenuBottomHelp.text = $"Created new {skillLabel} driver profile:\n{created.PlayerName} ({created.FileTag}.sav)";
            }
        }

        private void ShowCreateDriverModal()
        {
            CloseActiveDropdown();
            CloseActiveModal();

            if (canvasRootRect == null) return;

            activeCreateDriverSkillLevel = 0; // Default to Amateur (0)

            // Determine suggested default name (e.g. Driver2, Driver3)
            string[] existingNames = MM2PlayerManager.GetPlayerNames() ?? Array.Empty<string>();
            int num = existingNames.Length + 1;
            string suggestedName;
            while (true)
            {
                suggestedName = $"Driver{num}";
                bool exists = Array.Exists(existingNames, n => string.Equals(n, suggestedName, StringComparison.OrdinalIgnoreCase));
                if (!exists) break;
                num++;
            }

            bool prevSuppress = suppressResponsiveRegistration;
            suppressResponsiveRegistration = true;
            try
            {
                activeModalOverlay = CreateUIObject("MM2_ModalDialogOverlay", canvasRootRect);
                StretchFull(activeModalOverlay.GetComponent<RectTransform>());
                activeModalOverlay.transform.SetAsLastSibling();

                Canvas modalCanvas = activeModalOverlay.AddComponent<Canvas>();
                modalCanvas.overrideSorting = true;
                modalCanvas.sortingOrder = 300;
                activeModalOverlay.AddComponent<GraphicRaycaster>();

                GameObject backdropGo = CreateUIObject("ModalBackdrop", activeModalOverlay.transform);
                StretchFull(backdropGo.GetComponent<RectTransform>());
                Image backdropImg = backdropGo.AddComponent<Image>();
                backdropImg.color = new Color(0f, 0f, 0f, 0.68f);
                backdropImg.raycastTarget = true;

                float dialogW = 820f;
                float dialogH = 490f;
                GameObject dialogGo = CreateUIObject("ModalDialogBox", activeModalOverlay.transform);
                RectTransform dialogRt = dialogGo.GetComponent<RectTransform>();
                dialogRt.anchorMin = new Vector2(0.5f, 0.5f);
                dialogRt.anchorMax = new Vector2(0.5f, 0.5f);
                dialogRt.pivot = new Vector2(0.5f, 0.5f);
                dialogRt.anchoredPosition = Vector2.zero;
                dialogRt.sizeDelta = new Vector2(dialogW, dialogH);

                Image dialogBg = dialogGo.AddComponent<Image>();
                dialogBg.sprite = spBoxNavy9 != null ? spBoxNavy9 : spBoxBlack9;
                dialogBg.type = Image.Type.Sliced;
                dialogBg.color = Color.white;

                CreateShadowLabel(
                    "Txt_ModalTitle",
                    dialogGo.transform,
                    "CREATE NEW DRIVER",
                    36f,
                    24f,
                    dialogW - 72f,
                    54f,
                    36,
                    TextAnchor.MiddleCenter,
                    new Color32(255, 232, 16, 255),
                    fontUIExtraBold
                );

                CreateSolidRect("ModalHeaderDivider", dialogGo.transform, 36f, 84f, dialogW - 72f, 3f, new Color32(110, 130, 235, 190));

                CreateShadowLabel(
                    "Txt_ModalPrompt",
                    dialogGo.transform,
                    "Enter a name for the new driver profile:",
                    48f,
                    102f,
                    dialogW - 96f,
                    44f,
                    28,
                    TextAnchor.MiddleCenter,
                    Color.white,
                    fontUIBold
                );

                // Indented text input box using spBoxBlack9
                float inputW = 560f;
                float inputH = 64f;
                float inputX = (dialogW - inputW) * 0.5f;
                float inputY = 156f;
                GameObject inputBoxGo = CreateSlicedBox("InputBox_DriverName", dialogGo.transform, spBoxBlack9 != null ? spBoxBlack9 : spBoxNavy9, inputX, inputY, inputW, inputH);
                Image inputBoxImg = inputBoxGo.GetComponent<Image>();
                inputBoxImg.raycastTarget = true;

                // 1x-scale Text and Placeholder for Unity InputField so the caret and selection align accurately
                GameObject placeholderGo = CreateUIObject("Placeholder", inputBoxGo.transform);
                RectTransform phRt = placeholderGo.GetComponent<RectTransform>();
                phRt.anchorMin = Vector2.zero;
                phRt.anchorMax = Vector2.one;
                phRt.offsetMin = new Vector2(20f, 8f);
                phRt.offsetMax = new Vector2(-20f, -8f);
                Text phTxt = placeholderGo.AddComponent<Text>();
                phTxt.font = fontUIBold;
                phTxt.fontStyle = FontStyle.Italic;
                phTxt.fontSize = 30;
                phTxt.alignment = TextAnchor.MiddleLeft;
                phTxt.color = new Color32(140, 150, 185, 160);
                phTxt.text = "Enter driver name...";
                phTxt.raycastTarget = false;

                GameObject textGo = CreateUIObject("Text", inputBoxGo.transform);
                RectTransform txtRt = textGo.GetComponent<RectTransform>();
                txtRt.anchorMin = Vector2.zero;
                txtRt.anchorMax = Vector2.one;
                txtRt.offsetMin = new Vector2(20f, 8f);
                txtRt.offsetMax = new Vector2(-20f, -8f);
                Text valTxt = textGo.AddComponent<Text>();
                valTxt.font = fontUIBold;
                valTxt.fontStyle = FontStyle.Normal;
                valTxt.fontSize = 32;
                valTxt.alignment = TextAnchor.MiddleLeft;
                valTxt.color = new Color32(255, 240, 20, 255);
                valTxt.supportRichText = false;
                valTxt.raycastTarget = false;

                InputField inputField = inputBoxGo.AddComponent<InputField>();
                inputField.targetGraphic = inputBoxImg;
                inputField.textComponent = valTxt;
                inputField.placeholder = phTxt;
                inputField.characterLimit = 15;
                inputField.contentType = InputField.ContentType.Standard;
                inputField.lineType = InputField.LineType.SingleLine;
                inputField.customCaretColor = true;
                inputField.caretColor = Color.white;
                inputField.caretWidth = 3;
                inputField.selectionColor = new Color32(90, 120, 235, 160);
                inputField.text = suggestedName;

                activeCreateDriverInput = inputField;

                // Skill Level Selector: AMATEUR (0) vs PROFESSIONAL (1)
                CreateShadowLabel(
                    "Txt_SkillPrompt",
                    dialogGo.transform,
                    "SELECT RANKING / SKILL LEVEL:",
                    48f,
                    240f,
                    dialogW - 96f,
                    40f,
                    25,
                    TextAnchor.MiddleCenter,
                    new Color32(200, 215, 255, 255),
                    fontUIBold
                );

                Color selectedGreen = new Color32(15, 225, 65, 255);
                Image orbAmateur = null;
                Image orbPro = null;
                Text lblAmateur = null;
                Text lblPro = null;

                void RefreshSkillRadioUI()
                {
                    bool isPro = activeCreateDriverSkillLevel != 0;
                    if (orbAmateur != null) orbAmateur.sprite = !isPro ? spOrbGreen : spOrbRed;
                    if (orbPro != null) orbPro.sprite = isPro ? spOrbGreen : spOrbRed;
                    if (lblAmateur != null) lblAmateur.color = !isPro ? selectedGreen : Color.white;
                    if (lblPro != null) lblPro.color = isPro ? selectedGreen : Color.white;
                }

                // Amateur Radio Option
                GameObject amateurRow = CreateUIObject("Radio_SkillAmateur", dialogGo.transform);
                SetTopLeftRect(amateurRow.GetComponent<RectTransform>(), 145f, 290f, 240f, 56f);
                Image amHit = amateurRow.AddComponent<Image>();
                amHit.color = new Color(0f, 0f, 0f, 0f);
                orbAmateur = CreateSpriteImage("Orb", amateurRow.transform, spOrbGreen, 0f, 4f, 48f, 48f);
                orbAmateur.raycastTarget = false;
                lblAmateur = CreateShadowLabel("Label", amateurRow.transform, "AMATEUR", 62f, 0f, 175f, 56f, 32, TextAnchor.MiddleLeft, selectedGreen);
                Button btnAmateur = amateurRow.AddComponent<Button>();
                btnAmateur.targetGraphic = amHit;
                btnAmateur.onClick.AddListener(() =>
                {
                    activeCreateDriverSkillLevel = 0;
                    RefreshSkillRadioUI();
                });

                // Professional Radio Option
                GameObject proRow = CreateUIObject("Radio_SkillPro", dialogGo.transform);
                SetTopLeftRect(proRow.GetComponent<RectTransform>(), 415f, 290f, 300f, 56f);
                Image proHit = proRow.AddComponent<Image>();
                proHit.color = new Color(0f, 0f, 0f, 0f);
                orbPro = CreateSpriteImage("Orb", proRow.transform, spOrbRed, 0f, 4f, 48f, 48f);
                orbPro.raycastTarget = false;
                lblPro = CreateShadowLabel("Label", proRow.transform, "PROFESSIONAL", 62f, 0f, 235f, 56f, 32, TextAnchor.MiddleLeft, Color.white);
                Button btnPro = proRow.AddComponent<Button>();
                btnPro.targetGraphic = proHit;
                btnPro.onClick.AddListener(() =>
                {
                    activeCreateDriverSkillLevel = 1;
                    RefreshSkillRadioUI();
                });

                RefreshSkillRadioUI();

                float btnW = 240f;
                float btnH = 74f;
                float btnY = dialogH - btnH - 26f;
                float gap = 48f;
                float startX = (dialogW - (btnW * 2f + gap)) * 0.5f;

                CreateGlowButton("Btn_ModalCreate", dialogGo.transform, "CREATE", startX, btnY, btnW, btnH, 28, ConfirmCreateNewDriverFromModal);
                CreateGlowButton("Btn_ModalCancel", dialogGo.transform, "CANCEL", startX + btnW + gap, btnY, btnW, btnH, 28, CloseActiveModal);

                if (EventSystem.current != null)
                {
                    EventSystem.current.SetSelectedGameObject(inputBoxGo);
                }
                inputField.ActivateInputField();
                inputField.Select();
            }
            finally
            {
                suppressResponsiveRegistration = prevSuppress;
            }
        }

        private string statsModalSelectedCityCode = "";
        private string statsModalSelectedMode = "Blitz";
        private string recordsModalSelectedCityCode = "";
        private string recordsModalSelectedMode = "Blitz";
        private int recordsModalSelectedCategory = 0; // 0 = Amateur Times, 1 = Pro Times, 2 = Pro Points

        private static string FormatModeDisplayLabel(string modeName)
        {
            if (string.Equals(modeName, "CrashCourse", StringComparison.OrdinalIgnoreCase))
            {
                return "CRASH COURSE";
            }
            return (modeName ?? "BLITZ").ToUpperInvariant();
        }

        private void CreateRecordsFilterCheckbox(
            Transform parent,
            string name,
            string label,
            float x,
            float y,
            float w,
            float h,
            bool isChecked,
            Action onClick
        )
        {
            GameObject rowGo = CreateUIObject(name, parent);
            SetTopLeftRect(rowGo.GetComponent<RectTransform>(), x, y, w, h);
            Image hitImg = rowGo.AddComponent<Image>();
            hitImg.color = new Color(0f, 0f, 0f, 0f);

            float boxSize = Mathf.Min(38f, h - 4f);
            float boxY = (h - boxSize) * 0.5f;
            Image chkBoxImg = CreateSpriteImage(
                "Box",
                rowGo.transform,
                isChecked ? spCheckboxCheckedGreen : spCheckboxUnchecked,
                0f,
                boxY,
                boxSize,
                boxSize
            );
            chkBoxImg.raycastTarget = false;

            CreateShadowLabel(
                "Label",
                rowGo.transform,
                label,
                boxSize + 14f,
                0f,
                w - boxSize - 16f,
                h,
                25,
                TextAnchor.MiddleLeft,
                isChecked ? new Color32(255, 240, 25, 255) : Color.white,
                fontUIExtraBold
            );

            Button btn = rowGo.AddComponent<Button>();
            btn.targetGraphic = hitImg;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
        }

        private ScrollRect CreateScrollableTableViewport(
            Transform tableBoxTransform,
            float tableW,
            float tableH,
            float headerH,
            out RectTransform contentRect,
            out float viewportW,
            out float viewportH
        )
        {
            float scrollbarW = 28f;
            float vpX = 12f;
            float vpY = headerH + 6f;
            viewportW = tableW - vpX - scrollbarW - 18f;
            viewportH = tableH - vpY - 10f;

            GameObject scrollRootGo = CreateUIObject("ScrollRoot", tableBoxTransform);
            SetTopLeftRect(scrollRootGo.GetComponent<RectTransform>(), vpX, vpY, viewportW, viewportH);
            Image scrollHitImg = scrollRootGo.AddComponent<Image>();
            scrollHitImg.color = new Color(0f, 0f, 0f, 0.01f);
            scrollHitImg.raycastTarget = true;
            scrollRootGo.AddComponent<RectMask2D>();

            GameObject contentGo = CreateUIObject("Content", scrollRootGo.transform);
            contentRect = contentGo.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(0f, 1f);
            contentRect.pivot = new Vector2(0f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(viewportW, viewportH);

            ScrollRect sr = scrollRootGo.AddComponent<ScrollRect>();
            sr.viewport = scrollRootGo.GetComponent<RectTransform>();
            sr.content = contentRect;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.inertia = true;
            sr.decelerationRate = 0.12f;
            sr.scrollSensitivity = 42f;

            // Vertical Scrollbar + Up/Down Steppers on the right edge of the table
            float sbX = tableW - scrollbarW - 10f;
            float stepH = 28f;
            float trackY = vpY + stepH + 2f;
            float trackH = Mathf.Max(40f, viewportH - (stepH + 2f) * 2f);

            ScrollRect capturedSr = sr;
            CreateChevronButton("Sb_Up", tableBoxTransform, spChevronUp, sbX, vpY, scrollbarW, stepH, () =>
            {
                if (capturedSr != null)
                {
                    capturedSr.verticalNormalizedPosition = Mathf.Clamp01(capturedSr.verticalNormalizedPosition + 0.15f);
                }
            });
            CreateChevronButton("Sb_Down", tableBoxTransform, spChevronDown, sbX, vpY + viewportH - stepH, scrollbarW, stepH, () =>
            {
                if (capturedSr != null)
                {
                    capturedSr.verticalNormalizedPosition = Mathf.Clamp01(capturedSr.verticalNormalizedPosition - 0.15f);
                }
            });

            GameObject sbTrackGo = CreateSolidRect("ScrollbarTrack", tableBoxTransform, sbX, trackY, scrollbarW, trackH, new Color32(6, 10, 48, 255));
            sbTrackGo.GetComponent<Image>().raycastTarget = true;
            GameObject sbFrame = CreateSlicedBox("TrackFrame", sbTrackGo.transform, spFrameTrans9 != null ? spFrameTrans9 : spBoxNavy9, 0f, 0f, scrollbarW, trackH);
            sbFrame.GetComponent<Image>().raycastTarget = false;

            GameObject slidingAreaGo = CreateUIObject("SlidingArea", sbTrackGo.transform);
            RectTransform saRt = slidingAreaGo.GetComponent<RectTransform>();
            saRt.anchorMin = Vector2.zero;
            saRt.anchorMax = Vector2.one;
            saRt.offsetMin = new Vector2(3f, 3f);
            saRt.offsetMax = new Vector2(-3f, -3f);

            GameObject handleGo = CreateUIObject("Handle", slidingAreaGo.transform);
            RectTransform handleRt = handleGo.GetComponent<RectTransform>();
            handleRt.anchorMin = Vector2.zero;
            handleRt.anchorMax = Vector2.one;
            handleRt.offsetMin = Vector2.zero;
            handleRt.offsetMax = Vector2.zero;
            Image handleImg = handleGo.AddComponent<Image>();
            handleImg.color = new Color32(76, 114, 248, 255);
            handleImg.raycastTarget = true;

            Scrollbar sb = sbTrackGo.AddComponent<Scrollbar>();
            sb.targetGraphic = handleImg;
            sb.handleRect = handleRt;
            sb.direction = Scrollbar.Direction.BottomToTop;

            sr.verticalScrollbar = sb;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            return sr;
        }

        private void ShowDriverStatsModal()
        {
            CloseActiveDropdown();
            CloseActiveModal();

            if (canvasRootRect == null) return;

            List<CitySpec> raceCities = MM2PlayerManager.GetRaceCapableCities();
            if (string.IsNullOrEmpty(statsModalSelectedCityCode) ||
                !raceCities.Exists(c => string.Equals(c.MapName, statsModalSelectedCityCode, StringComparison.OrdinalIgnoreCase)))
            {
                CitySpec curSpec = raceCities.Find(c => string.Equals(c.MapName, MM2GameSession.SelectedCityCode, StringComparison.OrdinalIgnoreCase));
                statsModalSelectedCityCode = curSpec != null ? curSpec.MapName : (raceCities.Count > 0 ? raceCities[0].MapName : "sf");
            }

            List<string> availModes = MM2PlayerManager.GetAvailableRaceModesForCity(statsModalSelectedCityCode, includeCrashCourse: true);
            if (!availModes.Exists(m => string.Equals(m, statsModalSelectedMode, StringComparison.OrdinalIgnoreCase)))
            {
                statsModalSelectedMode = availModes.Count > 0 ? availModes[0] : "Blitz";
            }

            bool prevSuppress = suppressResponsiveRegistration;
            suppressResponsiveRegistration = true;
            try
            {
                activeModalOverlay = CreateUIObject("MM2_ModalDialogOverlay", canvasRootRect);
                StretchFull(activeModalOverlay.GetComponent<RectTransform>());
                activeModalOverlay.transform.SetAsLastSibling();

                Canvas modalCanvas = activeModalOverlay.AddComponent<Canvas>();
                modalCanvas.overrideSorting = true;
                modalCanvas.sortingOrder = 300;
                activeModalOverlay.AddComponent<GraphicRaycaster>();

                GameObject backdropGo = CreateUIObject("ModalBackdrop", activeModalOverlay.transform);
                StretchFull(backdropGo.GetComponent<RectTransform>());
                Image backdropImg = backdropGo.AddComponent<Image>();
                backdropImg.color = new Color(0f, 0f, 0f, 0.72f);
                backdropImg.raycastTarget = true;

                // Use generous space on widescreen and higher-res displays
                float canvasW = (canvasRootRect != null && canvasRootRect.rect.width >= 100f) ? canvasRootRect.rect.width : 1920f;
                float canvasH = (canvasRootRect != null && canvasRootRect.rect.height >= 100f) ? canvasRootRect.rect.height : 1080f;
                float dialogW = Mathf.Clamp(canvasW - 120f, 1560f, 2400f);
                float dialogH = Mathf.Clamp(canvasH - 70f, 920f, 1360f);

                GameObject dialogGo = CreateUIObject("DriverStatsDialogBox", activeModalOverlay.transform);
                RectTransform dialogRt = dialogGo.GetComponent<RectTransform>();
                dialogRt.anchorMin = new Vector2(0.5f, 0.5f);
                dialogRt.anchorMax = new Vector2(0.5f, 0.5f);
                dialogRt.pivot = new Vector2(0.5f, 0.5f);
                dialogRt.anchoredPosition = Vector2.zero;
                dialogRt.sizeDelta = new Vector2(dialogW, dialogH);

                Image dialogBg = dialogGo.AddComponent<Image>();
                dialogBg.sprite = spBoxNavy9 != null ? spBoxNavy9 : spBoxBlack9;
                dialogBg.type = Image.Type.Sliced;
                dialogBg.color = Color.white;

                // Top Header Bar
                CitySpec activeCitySpec = raceCities.Find(c => string.Equals(c.MapName, statsModalSelectedCityCode, StringComparison.OrdinalIgnoreCase));
                string activeCityDisplay = (activeCitySpec != null && !string.IsNullOrEmpty(activeCitySpec.DisplayName))
                    ? activeCitySpec.DisplayName
                    : statsModalSelectedCityCode.ToUpperInvariant();

                CreateShadowLabel(
                    "Txt_DriverSubtitle",
                    dialogGo.transform,
                    $"DRIVER: {MM2GameSession.DriverName}   •   {activeCityDisplay.ToUpperInvariant()} ({FormatModeDisplayLabel(statsModalSelectedMode)})",
                    36f,
                    20f,
                    dialogW * 0.62f,
                    50f,
                    28,
                    TextAnchor.MiddleLeft,
                    new Color32(255, 240, 25, 255),
                    fontUIExtraBold
                );

                CreateShadowLabel(
                    "Txt_DialogTitle",
                    dialogGo.transform,
                    "DRIVER'S STATS",
                    dialogW - 560f,
                    16f,
                    520f,
                    56f,
                    40,
                    TextAnchor.MiddleRight,
                    Color.white,
                    fontUIExtraBold
                );

                CreateSolidRect("HeaderDivider", dialogGo.transform, 24f, 76f, dialogW - 48f, 3f, new Color32(110, 130, 235, 200));

                // Main Table Box
                float tableX = 36f;
                float tableY = 92f;
                float tableW = dialogW - 72f;
                float tableH = dialogH - 375f;
                GameObject tableBox = CreateSlicedBox("Box_StatsTable", dialogGo.transform, spBoxBlack9 != null ? spBoxBlack9 : spBoxNavy9, tableX, tableY, tableW, tableH);

                float headerH = 44f;
                float colRaceX = 64f;
                float colTimeX = tableW * 0.54f;
                float colVehX = tableW * 0.72f;

                bool isCrashSelected = string.Equals(statsModalSelectedMode, "CrashCourse", StringComparison.OrdinalIgnoreCase);
                CreateShadowLabel("Hdr_Race", tableBox.transform, isCrashSelected ? "LESSON" : "RACE", colRaceX, 6f, colTimeX - colRaceX - 16f, 38f, 25, TextAnchor.MiddleLeft, new Color32(200, 215, 255, 255), fontUIExtraBold);
                CreateShadowLabel("Hdr_Time", tableBox.transform, "TIME", colTimeX, 6f, colVehX - colTimeX - 16f, 38f, 25, TextAnchor.MiddleLeft, new Color32(200, 215, 255, 255), fontUIExtraBold);
                CreateShadowLabel("Hdr_Vehicle", tableBox.transform, "VEHICLE", colVehX, 6f, tableW - colVehX - 54f, 38f, 25, TextAnchor.MiddleLeft, new Color32(200, 215, 255, 255), fontUIExtraBold);
                CreateSolidRect("Hdr_Line", tableBox.transform, 14f, headerH, tableW - 28f, 2f, new Color32(110, 130, 235, 200));

                ScrollRect sr = CreateScrollableTableViewport(
                    tableBox.transform,
                    tableW,
                    tableH,
                    headerH,
                    out RectTransform contentRt,
                    out float vpW,
                    out float vpH
                );

                List<MM2DriverStatRow> rows = MM2PlayerManager.GetDriverStatsRows(statsModalSelectedCityCode, statsModalSelectedMode);
                float rowH = 42f;
                float totalContentH = Mathf.Max(vpH, Mathf.Max(12, rows.Count) * rowH);
                contentRt.sizeDelta = new Vector2(vpW, totalContentH);

                int displayRowCount = Mathf.Max(12, rows.Count);
                for (int i = 0; i < displayRowCount; i++)
                {
                    float ry = i * rowH;
                    Color32 stripeCol = (i % 2 == 0)
                        ? new Color32(18, 22, 78, 235)
                        : new Color32(10, 14, 56, 235);
                    GameObject rowBg = CreateSolidRect($"Row_{i}", contentRt, 0f, ry, vpW, rowH - 1f, stripeCol);

                    if (i < rows.Count)
                    {
                        MM2DriverStatRow r = rows[i];

                        // Status indicator (Tick / Cross / Uncompleted)
                        if (r.Status == 1)
                        {
                            if (spCcSmChk != null)
                            {
                                Image chk = CreateSpriteImage("Chk", rowBg.transform, spCcSmChk, 16f, 8f, 26f, 26f);
                                chk.raycastTarget = false;
                            }
                            else
                            {
                                CreateShadowLabel("ChkTxt", rowBg.transform, "✓", 12f, 2f, 36f, rowH - 4f, 26, TextAnchor.MiddleCenter, new Color32(40, 245, 75, 255), fontUIExtraBold);
                            }
                        }
                        else if (r.Status == 2)
                        {
                            if (spCcSmFail != null)
                            {
                                Image fail = CreateSpriteImage("Fail", rowBg.transform, spCcSmFail, 16f, 8f, 26f, 26f);
                                fail.raycastTarget = false;
                            }
                            else
                            {
                                CreateShadowLabel("FailTxt", rowBg.transform, "✗", 12f, 2f, 36f, rowH - 4f, 26, TextAnchor.MiddleCenter, new Color32(255, 65, 55, 255), fontUIExtraBold);
                            }
                        }
                        else
                        {
                            CreateShadowLabel("UnpassedTxt", rowBg.transform, "•", 12f, 2f, 36f, rowH - 4f, 24, TextAnchor.MiddleCenter, new Color32(235, 175, 35, 200), fontUIExtraBold);
                        }

                        CreateShadowLabel("Txt_Event", rowBg.transform, r.EventName, colRaceX - 12f, 2f, colTimeX - colRaceX - 12f, rowH - 4f, 25, TextAnchor.MiddleLeft, Color.white, fontUIBold);
                        CreateShadowLabel("Txt_Time", rowBg.transform, r.TimeString, colTimeX - 12f, 2f, colVehX - colTimeX - 12f, rowH - 4f, 25, TextAnchor.MiddleLeft, Color.white, fontUIBold);
                        CreateShadowLabel("Txt_Veh", rowBg.transform, r.VehicleDisplayName, colVehX - 12f, 2f, vpW - (colVehX - 12f) - 8f, rowH - 4f, 25, TextAnchor.MiddleLeft, Color.white, fontUIBold);
                    }
                }

                sr.verticalNormalizedPosition = 1f;

                // Bottom Filter Controls ("RECORDS:")
                float filterTopY = tableY + tableH + 14f;
                CreateShadowLabel("Lbl_RecordsHdr", dialogGo.transform, "RECORDS:", 40f, filterTopY, 280f, 40f, 30, TextAnchor.MiddleLeft, Color.white, fontUIExtraBold);

                // Left Column: Available Race Modes in the selected city (Blitz, Circuit, Checkpoint, Crash Course)
                float modeStartX = 64f;
                float modeStartY = filterTopY + 44f;
                float modeRowH = 42f;
                for (int m = 0; m < availModes.Count; m++)
                {
                    string modeKey = availModes[m];
                    bool isSelected = string.Equals(modeKey, statsModalSelectedMode, StringComparison.OrdinalIgnoreCase);
                    CreateRecordsFilterCheckbox(
                        dialogGo.transform,
                        $"Chk_Mode_{modeKey}",
                        FormatModeDisplayLabel(modeKey),
                        modeStartX,
                        modeStartY + m * modeRowH,
                        360f,
                        modeRowH - 2f,
                        isSelected,
                        () =>
                        {
                            statsModalSelectedMode = modeKey;
                            ShowDriverStatsModal();
                        }
                    );
                }

                // Right Area: All Cities with Races (multi-column up to 4 rows per column)
                float cityAreaX = dialogW * 0.42f;
                float cityColW = 420f;
                int maxCityRows = 4;
                for (int c = 0; c < raceCities.Count; c++)
                {
                    CitySpec cSpec = raceCities[c];
                    int col = c / maxCityRows;
                    int row = c % maxCityRows;
                    string cCode = cSpec.MapName;
                    string cLabel = (!string.IsNullOrEmpty(cSpec.DisplayName) ? cSpec.DisplayName : cSpec.MapName).ToUpperInvariant();
                    bool isSelected = string.Equals(cCode, statsModalSelectedCityCode, StringComparison.OrdinalIgnoreCase);

                    CreateRecordsFilterCheckbox(
                        dialogGo.transform,
                        $"Chk_City_{cCode}",
                        cLabel,
                        cityAreaX + col * cityColW,
                        modeStartY + row * modeRowH,
                        cityColW - 16f,
                        modeRowH - 2f,
                        isSelected,
                        () =>
                        {
                            statsModalSelectedCityCode = cCode;
                            ShowDriverStatsModal();
                        }
                    );
                }

                // Bottom Disclaimer & [ DONE -> ] Button
                CreateShadowLabel(
                    "Txt_StatsFootnote",
                    dialogGo.transform,
                    "STATISTICS AND RECORDS ARE ONLY SAVED WHEN USING DEFAULT RACE SETTINGS.\nEVENTS RUN WITH CUSTOM LAPS, CHECKPOINTS, OR OPPONENT COUNTS ARE NOT RECORDED.",
                    40f,
                    dialogH - 72f,
                    dialogW - 420f,
                    58f,
                    18,
                    TextAnchor.MiddleLeft,
                    new Color32(215, 222, 245, 255),
                    fontUIExtraBold
                );

                CreateRightArrowButton("Btn_StatsDone", dialogGo.transform, "DONE", dialogW - 330f, dialogH - 94f, 290f, 76f, 26, CloseActiveModal);
            }
            finally
            {
                suppressResponsiveRegistration = prevSuppress;
            }
        }

        private void ShowRaceRecordsModal()
        {
            CloseActiveDropdown();
            CloseActiveModal();

            if (canvasRootRect == null) return;

            List<CitySpec> raceCities = MM2PlayerManager.GetRaceCapableCities();
            if (string.IsNullOrEmpty(recordsModalSelectedCityCode) ||
                !raceCities.Exists(c => string.Equals(c.MapName, recordsModalSelectedCityCode, StringComparison.OrdinalIgnoreCase)))
            {
                CitySpec curSpec = raceCities.Find(c => string.Equals(c.MapName, MM2GameSession.SelectedCityCode, StringComparison.OrdinalIgnoreCase));
                recordsModalSelectedCityCode = curSpec != null ? curSpec.MapName : (raceCities.Count > 0 ? raceCities[0].MapName : "sf");
            }

            List<string> availModes = MM2PlayerManager.GetAvailableRaceModesForCity(recordsModalSelectedCityCode, includeCrashCourse: true);
            if (!availModes.Exists(m => string.Equals(m, recordsModalSelectedMode, StringComparison.OrdinalIgnoreCase)))
            {
                recordsModalSelectedMode = availModes.Count > 0 ? availModes[0] : "Blitz";
            }

            bool prevSuppress = suppressResponsiveRegistration;
            suppressResponsiveRegistration = true;
            try
            {
                activeModalOverlay = CreateUIObject("MM2_ModalDialogOverlay", canvasRootRect);
                StretchFull(activeModalOverlay.GetComponent<RectTransform>());
                activeModalOverlay.transform.SetAsLastSibling();

                Canvas modalCanvas = activeModalOverlay.AddComponent<Canvas>();
                modalCanvas.overrideSorting = true;
                modalCanvas.sortingOrder = 300;
                activeModalOverlay.AddComponent<GraphicRaycaster>();

                GameObject backdropGo = CreateUIObject("ModalBackdrop", activeModalOverlay.transform);
                StretchFull(backdropGo.GetComponent<RectTransform>());
                Image backdropImg = backdropGo.AddComponent<Image>();
                backdropImg.color = new Color(0f, 0f, 0f, 0.72f);
                backdropImg.raycastTarget = true;

                // Use generous space on widescreen and higher-res displays
                float canvasW = (canvasRootRect != null && canvasRootRect.rect.width >= 100f) ? canvasRootRect.rect.width : 1920f;
                float canvasH = (canvasRootRect != null && canvasRootRect.rect.height >= 100f) ? canvasRootRect.rect.height : 1080f;
                float dialogW = Mathf.Clamp(canvasW - 120f, 1560f, 2400f);
                float dialogH = Mathf.Clamp(canvasH - 70f, 920f, 1360f);

                GameObject dialogGo = CreateUIObject("RaceRecordsDialogBox", activeModalOverlay.transform);
                RectTransform dialogRt = dialogGo.GetComponent<RectTransform>();
                dialogRt.anchorMin = new Vector2(0.5f, 0.5f);
                dialogRt.anchorMax = new Vector2(0.5f, 0.5f);
                dialogRt.pivot = new Vector2(0.5f, 0.5f);
                dialogRt.anchoredPosition = Vector2.zero;
                dialogRt.sizeDelta = new Vector2(dialogW, dialogH);

                Image dialogBg = dialogGo.AddComponent<Image>();
                dialogBg.sprite = spBoxNavy9 != null ? spBoxNavy9 : spBoxBlack9;
                dialogBg.type = Image.Type.Sliced;
                dialogBg.color = Color.white;

                CitySpec activeCitySpec = raceCities.Find(c => string.Equals(c.MapName, recordsModalSelectedCityCode, StringComparison.OrdinalIgnoreCase));
                string activeCityDisplay = (activeCitySpec != null && !string.IsNullOrEmpty(activeCitySpec.DisplayName))
                    ? activeCitySpec.DisplayName
                    : recordsModalSelectedCityCode.ToUpperInvariant();
                string[] categoryLabels = { "AMATEUR TIMES", "PRO TIMES", "PRO POINTS" };
                string activeCatLabel = categoryLabels[Mathf.Clamp(recordsModalSelectedCategory, 0, categoryLabels.Length - 1)];

                CreateShadowLabel(
                    "Txt_RecordsSubtitle",
                    dialogGo.transform,
                    $"{activeCityDisplay.ToUpperInvariant()}   •   {FormatModeDisplayLabel(recordsModalSelectedMode)}   •   {activeCatLabel}",
                    36f,
                    20f,
                    dialogW * 0.62f,
                    50f,
                    28,
                    TextAnchor.MiddleLeft,
                    new Color32(255, 240, 25, 255),
                    fontUIExtraBold
                );

                CreateShadowLabel(
                    "Txt_DialogTitle",
                    dialogGo.transform,
                    "RACE RECORDS",
                    dialogW - 560f,
                    16f,
                    520f,
                    56f,
                    40,
                    TextAnchor.MiddleRight,
                    Color.white,
                    fontUIExtraBold
                );

                CreateSolidRect("HeaderDivider", dialogGo.transform, 24f, 76f, dialogW - 48f, 3f, new Color32(110, 130, 235, 200));

                // Main Scrollable Table Box (Top 5 per race across all players)
                float tableX = 36f;
                float tableY = 92f;
                float tableW = dialogW - 72f;
                float tableH = dialogH - 375f;
                GameObject tableBox = CreateSlicedBox("Box_RecordsTable", dialogGo.transform, spBoxBlack9 != null ? spBoxBlack9 : spBoxNavy9, tableX, tableY, tableW, tableH);

                float headerH = 44f;
                float colRaceX = 28f;
                float colDriverX = tableW * 0.36f;
                float colTimeX = tableW * 0.58f;
                float colVehX = tableW * 0.73f;

                bool isCrashSelected = string.Equals(recordsModalSelectedMode, "CrashCourse", StringComparison.OrdinalIgnoreCase);
                bool isPointsSelected = (recordsModalSelectedCategory == 2);
                CreateShadowLabel("Hdr_Race", tableBox.transform, isCrashSelected ? "LESSON" : "RACE", colRaceX, 6f, colDriverX - colRaceX - 12f, 38f, 25, TextAnchor.MiddleLeft, new Color32(200, 215, 255, 255), fontUIExtraBold);
                CreateShadowLabel("Hdr_Driver", tableBox.transform, "DRIVER", colDriverX, 6f, colTimeX - colDriverX - 12f, 38f, 25, TextAnchor.MiddleLeft, new Color32(200, 215, 255, 255), fontUIExtraBold);
                CreateShadowLabel("Hdr_Time", tableBox.transform, isPointsSelected ? "POINTS" : "TIME", colTimeX, 6f, colVehX - colTimeX - 12f, 38f, 25, TextAnchor.MiddleLeft, new Color32(200, 215, 255, 255), fontUIExtraBold);
                CreateShadowLabel("Hdr_Vehicle", tableBox.transform, "VEHICLE", colVehX, 6f, tableW - colVehX - 54f, 38f, 25, TextAnchor.MiddleLeft, new Color32(200, 215, 255, 255), fontUIExtraBold);
                CreateSolidRect("Hdr_Line", tableBox.transform, 14f, headerH, tableW - 28f, 2f, new Color32(110, 130, 235, 200));

                ScrollRect sr = CreateScrollableTableViewport(
                    tableBox.transform,
                    tableW,
                    tableH,
                    headerH,
                    out RectTransform contentRt,
                    out float vpW,
                    out float vpH
                );

                List<MM2RaceRecordRow> rows = MM2PlayerManager.GetTop5RaceRecordsRows(
                    recordsModalSelectedCityCode,
                    recordsModalSelectedMode,
                    recordsModalSelectedCategory
                );

                float rowH = 40f;
                float totalContentH = Mathf.Max(vpH, Mathf.Max(12, rows.Count) * rowH);
                contentRt.sizeDelta = new Vector2(vpW, totalContentH);

                int displayRowCount = Mathf.Max(12, rows.Count);
                for (int i = 0; i < displayRowCount; i++)
                {
                    float ry = i * rowH;
                    Color32 stripeCol = (i % 2 == 0)
                        ? new Color32(18, 22, 78, 235)
                        : new Color32(10, 14, 56, 235);
                    GameObject rowBg = CreateSolidRect($"Row_{i}", contentRt, 0f, ry, vpW, rowH - 1f, stripeCol);

                    if (i < rows.Count)
                    {
                        MM2RaceRecordRow r = rows[i];
                        bool hasDriver = !string.IsNullOrEmpty(r.DriverName);
                        Color rowTextCol = hasDriver ? Color.white : new Color32(175, 185, 220, 210);

                        CreateShadowLabel("Txt_Race", rowBg.transform, r.RaceName, colRaceX - 12f, 2f, colDriverX - colRaceX - 12f, rowH - 4f, 24, TextAnchor.MiddleLeft, Color.white, fontUIBold);
                        CreateShadowLabel("Txt_Driver", rowBg.transform, r.DriverName, colDriverX - 12f, 2f, colTimeX - colDriverX - 12f, rowH - 4f, 24, TextAnchor.MiddleLeft, rowTextCol, fontUIBold);
                        CreateShadowLabel("Txt_Time", rowBg.transform, r.TimeOrScoreString, colTimeX - 12f, 2f, colVehX - colTimeX - 12f, rowH - 4f, 24, TextAnchor.MiddleLeft, rowTextCol, fontUIBold);
                        CreateShadowLabel("Txt_Veh", rowBg.transform, r.VehicleDisplayName, colVehX - 12f, 2f, vpW - (colVehX - 12f) - 8f, rowH - 4f, 24, TextAnchor.MiddleLeft, rowTextCol, fontUIBold);

                        // Subtle divider line after every 5th row (end of each race's Top 5 block)
                        if (r.RankInRace == 5)
                        {
                            CreateSolidRect("RaceBlockSep", rowBg.transform, 0f, rowH - 2f, vpW, 2f, new Color32(75, 95, 195, 150));
                        }
                    }
                }

                sr.verticalNormalizedPosition = 1f;

                // Bottom Filter Controls ("RECORDS:")
                float filterTopY = tableY + tableH + 14f;
                CreateShadowLabel("Lbl_RecordsHdr", dialogGo.transform, "RECORDS:", 40f, filterTopY, 280f, 40f, 30, TextAnchor.MiddleLeft, Color.white, fontUIExtraBold);

                float modeStartY = filterTopY + 44f;
                float modeRowH = 42f;

                // Column 1: Available Race Modes in selected city
                float col1X = 56f;
                for (int m = 0; m < availModes.Count; m++)
                {
                    string modeKey = availModes[m];
                    bool isSelected = string.Equals(modeKey, recordsModalSelectedMode, StringComparison.OrdinalIgnoreCase);
                    CreateRecordsFilterCheckbox(
                        dialogGo.transform,
                        $"Chk_Mode_{modeKey}",
                        FormatModeDisplayLabel(modeKey),
                        col1X,
                        modeStartY + m * modeRowH,
                        330f,
                        modeRowH - 2f,
                        isSelected,
                        () =>
                        {
                            recordsModalSelectedMode = modeKey;
                            ShowRaceRecordsModal();
                        }
                    );
                }

                // Column 2: Amateur Times / Pro Times / Pro Points
                float col2X = dialogW * 0.27f;
                for (int cat = 0; cat < categoryLabels.Length; cat++)
                {
                    int capturedCat = cat;
                    bool isSelected = (recordsModalSelectedCategory == capturedCat);
                    CreateRecordsFilterCheckbox(
                        dialogGo.transform,
                        $"Chk_Cat_{capturedCat}",
                        categoryLabels[capturedCat],
                        col2X,
                        modeStartY + capturedCat * modeRowH,
                        340f,
                        modeRowH - 2f,
                        isSelected,
                        () =>
                        {
                            recordsModalSelectedCategory = capturedCat;
                            ShowRaceRecordsModal();
                        }
                    );
                }

                // Column 3+: All Cities with Races
                float col3X = dialogW * 0.54f;
                float cityColW = 360f;
                int maxCityRows = 4;
                for (int c = 0; c < raceCities.Count; c++)
                {
                    CitySpec cSpec = raceCities[c];
                    int col = c / maxCityRows;
                    int row = c % maxCityRows;
                    string cCode = cSpec.MapName;
                    string cLabel = (!string.IsNullOrEmpty(cSpec.DisplayName) ? cSpec.DisplayName : cSpec.MapName).ToUpperInvariant();
                    bool isSelected = string.Equals(cCode, recordsModalSelectedCityCode, StringComparison.OrdinalIgnoreCase);

                    CreateRecordsFilterCheckbox(
                        dialogGo.transform,
                        $"Chk_City_{cCode}",
                        cLabel,
                        col3X + col * cityColW,
                        modeStartY + row * modeRowH,
                        cityColW - 14f,
                        modeRowH - 2f,
                        isSelected,
                        () =>
                        {
                            recordsModalSelectedCityCode = cCode;
                            ShowRaceRecordsModal();
                        }
                    );
                }

                // Bottom Disclaimer & [ DONE -> ] Button
                CreateShadowLabel(
                    "Txt_RecordsFootnote",
                    dialogGo.transform,
                    "STATISTICS AND RECORDS ARE ONLY SAVED WHEN USING DEFAULT RACE SETTINGS.\nEVENTS RUN WITH CUSTOM LAPS, CHECKPOINTS, OR OPPONENT COUNTS ARE NOT RECORDED.",
                    40f,
                    dialogH - 72f,
                    dialogW - 420f,
                    58f,
                    18,
                    TextAnchor.MiddleLeft,
                    new Color32(215, 222, 245, 255),
                    fontUIExtraBold
                );

                CreateRightArrowButton("Btn_RecordsDone", dialogGo.transform, "DONE", dialogW - 330f, dialogH - 94f, 290f, 76f, 26, CloseActiveModal);
            }
            finally
            {
                suppressResponsiveRegistration = prevSuppress;
            }
        }

        private void ShowDeleteDriverConfirmModal()
        {
            string[] allPlayers = MM2PlayerManager.GetPlayerNames();
            if (allPlayers != null && allPlayers.Length <= 1)
            {
                ShowModalDialog(
                    "CANNOT DELETE DRIVER",
                    "You cannot delete the last remaining driver profile.",
                    "OK",
                    CloseActiveModal,
                    null,
                    null
                );
                return;
            }

            string driverToDelete = MM2GameSession.DriverName;
            ShowModalDialog(
                "DELETE DRIVER PROFILE",
                $"Deleting driver '{driverToDelete}' cannot be undone.\n\nAre you sure you want to delete this driver?",
                "YES",
                () =>
                {
                    CloseActiveModal();
                    if (MM2PlayerManager.DeleteActivePlayer())
                    {
                        mainMenuStatsViewMode = 0;
                        ApplySelectedDriverToMenuUI();
                        if (txtMainMenuBottomHelp != null)
                        {
                            txtMainMenuBottomHelp.text = $"Deleted driver '{driverToDelete}'.\nActive driver: {MM2GameSession.DriverName}";
                        }
                    }
                },
                "NO",
                CloseActiveModal
            );
        }

        private static readonly Dictionary<string, Sprite> VehicleShowcaseSpriteCache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private static Sprite LoadVehicleShowcaseSprite(string vehicleNameOrBase)
        {
            VehicleSpec spec = VehicleCatalog.GetSpec(vehicleNameOrBase);
            string baseName = (spec != null && !string.IsNullOrEmpty(spec.BaseName))
                ? spec.BaseName.Trim()
                : (vehicleNameOrBase ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(baseName)) return null;

            if (VehicleShowcaseSpriteCache.TryGetValue(baseName, out Sprite cached) && cached != null)
            {
                return cached;
            }

            string[] candidates =
            {
                $"jpg/{baseName}_show.jpg",
                $"bmp/{baseName}_show.bmp",
                $"bmp16/{baseName}_show.jpg"
            };

            foreach (string cand in candidates)
            {
                byte[] bytes = MM2ArchiveSystem.ReadFileBytes(cand);
                if (bytes != null && bytes.Length > 64)
                {
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(bytes))
                    {
                        tex.name = $"VehicleShowcase_{baseName}";
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.filterMode = FilterMode.Bilinear;
                        Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                        VehicleShowcaseSpriteCache[baseName] = sp;
                        return sp;
                    }
                    Destroy(tex);
                }
            }

            Texture2D uiTex = MM2RuntimeDecoders.LoadUiTexture($"{baseName}_show");
            if (uiTex != null)
            {
                Sprite sp = Sprite.Create(uiTex, new Rect(0f, 0f, uiTex.width, uiTex.height), new Vector2(0.5f, 0.5f), 100f);
                VehicleShowcaseSpriteCache[baseName] = sp;
                return sp;
            }

            return null;
        }

        private void ShowVehicleShowcaseModal()
        {
            CloseActiveDropdown();
            CloseActiveModal();

            if (canvasRootRect == null) return;

            Sprite showcaseSprite = LoadVehicleShowcaseSprite(MM2GameSession.VehicleName);
            if (showcaseSprite == null || showcaseSprite.texture == null)
            {
                ShowModalDialog(
                    "VEHICLE SHOWCASE",
                    $"No showcase image is available in the .ar archives for {MM2GameSession.VehicleName}.",
                    "DONE",
                    CloseActiveModal,
                    null,
                    null
                );
                return;
            }

            bool prevSuppress = suppressResponsiveRegistration;
            suppressResponsiveRegistration = true;
            try
            {
                activeModalOverlay = CreateUIObject("MM2_VehicleShowcaseOverlay", canvasRootRect);
                StretchFull(activeModalOverlay.GetComponent<RectTransform>());
                activeModalOverlay.transform.SetAsLastSibling();

                Canvas modalCanvas = activeModalOverlay.AddComponent<Canvas>();
                modalCanvas.overrideSorting = true;
                modalCanvas.sortingOrder = 300;
                activeModalOverlay.AddComponent<GraphicRaycaster>();

                int screenW = Mathf.Max(1, Screen.width);
                int screenH = Mathf.Max(1, Screen.height);
                bool showAsModal = screenW > 640 && screenH > 480;

                float canvasW = canvasRootRect.rect.width > 100f ? canvasRootRect.rect.width : 1920f;
                float canvasH = canvasRootRect.rect.height > 100f ? canvasRootRect.rect.height : 1080f;

                RectTransform imageContainerRt;
                float imgWCanvas;
                float imgHCanvas;

                if (showAsModal)
                {
                    // Full-screen dimmed backdrop; clicking outside the popup also closes it
                    GameObject backdropGo = CreateUIObject("ShowcaseBackdrop", activeModalOverlay.transform);
                    StretchFull(backdropGo.GetComponent<RectTransform>());
                    Image backdropImg = backdropGo.AddComponent<Image>();
                    backdropImg.color = new Color(0f, 0f, 0f, 0.72f);
                    backdropImg.raycastTarget = true;
                    Button backdropBtn = backdropGo.AddComponent<Button>();
                    backdropBtn.transition = Selectable.Transition.None;
                    backdropBtn.onClick.AddListener(CloseActiveModal);

                    // Size the popup to match the native 640x480 showcase image dimensions in screen pixels
                    float targetPxW = showcaseSprite.texture.width > 0 ? showcaseSprite.texture.width : 640f;
                    float targetPxH = showcaseSprite.texture.height > 0 ? showcaseSprite.texture.height : 480f;

                    float pxToCanvasX = canvasW / screenW;
                    float pxToCanvasY = canvasH / screenH;
                    imgWCanvas = targetPxW * pxToCanvasX;
                    imgHCanvas = targetPxH * pxToCanvasY;

                    // Ensure it stays comfortably within the canvas bounds with a 4:3 aspect ratio
                    float maxW = canvasW * 0.94f;
                    float maxH = canvasH * 0.94f;
                    if (imgWCanvas > maxW || imgHCanvas > maxH)
                    {
                        float fitScale = Mathf.Min(maxW / imgWCanvas, maxH / imgHCanvas);
                        imgWCanvas *= fitScale;
                        imgHCanvas *= fitScale;
                    }

                    const float framePad = 6f;
                    GameObject frameGo = CreateUIObject("ShowcaseModalFrame", activeModalOverlay.transform);
                    RectTransform frameRt = frameGo.GetComponent<RectTransform>();
                    frameRt.anchorMin = new Vector2(0.5f, 0.5f);
                    frameRt.anchorMax = new Vector2(0.5f, 0.5f);
                    frameRt.pivot = new Vector2(0.5f, 0.5f);
                    frameRt.anchoredPosition = Vector2.zero;
                    frameRt.sizeDelta = new Vector2(imgWCanvas + framePad * 2f, imgHCanvas + framePad * 2f);

                    Image frameBg = frameGo.AddComponent<Image>();
                    frameBg.sprite = spBoxNavy9 != null ? spBoxNavy9 : spBoxBlack9;
                    frameBg.type = Image.Type.Sliced;
                    frameBg.color = Color.white;

                    GameObject imgGo = CreateUIObject("ShowcaseImage", frameGo.transform);
                    imageContainerRt = imgGo.GetComponent<RectTransform>();
                    SetTopLeftRect(imageContainerRt, framePad, framePad, imgWCanvas, imgHCanvas);
                    Image showcaseImg = imgGo.AddComponent<Image>();
                    showcaseImg.sprite = showcaseSprite;
                    showcaseImg.preserveAspect = false;
                    showcaseImg.color = Color.white;
                }
                else
                {
                    // Screen is <= 640x480: display full-screen
                    imgWCanvas = canvasW;
                    imgHCanvas = canvasH;

                    GameObject imgGo = CreateUIObject("ShowcaseFullscreenImage", activeModalOverlay.transform);
                    imageContainerRt = imgGo.GetComponent<RectTransform>();
                    StretchFull(imageContainerRt);
                    Image showcaseImg = imgGo.AddComponent<Image>();
                    showcaseImg.sprite = showcaseSprite;
                    showcaseImg.preserveAspect = false;
                    showcaseImg.color = Color.white;
                }

                // Position the [ DONE -> ] arrow button at the bottom-right corner matching the original 640x480 layout
                // (x: 450..622 out of 640, y: 414..472 out of 480)
                float btnX = imgWCanvas * (450f / 640f);
                float btnY = imgHCanvas * (414f / 480f);
                float btnW = imgWCanvas * (172f / 640f);
                float btnH = imgHCanvas * (58f / 480f);
                int btnFontSize = Mathf.Clamp(Mathf.RoundToInt(btnH * 0.28f), 14, 32);

                CreateRightArrowButton(
                    "Btn_ShowcaseDone",
                    imageContainerRt,
                    "DONE",
                    btnX,
                    btnY,
                    btnW,
                    btnH,
                    btnFontSize,
                    CloseActiveModal
                );
            }
            finally
            {
                suppressResponsiveRegistration = prevSuppress;
            }
        }

        private void ShowHelpModalDialog()
        {
            ShowModalDialog(
                "HELP & ABOUT",
                "This is a vibe-coded project that can read cities, races, cars, and missions directly from Midtown Madness 1 and Midtown Madness 2 (.ar) archive files.\n\n" +
                "Fonts: Open Sans (Copyright 2020 The Open Sans Project Authors, SIL Open Font License 1.1).",
                "OK",
                CloseActiveModal,
                null,
                null
            );
        }

        private void ShowModalMessage(string title, string message, string buttonLabel, Action onClick)
        {
            ShowModalDialog(title, message, buttonLabel, onClick ?? CloseActiveModal, null, null);
        }

        private void ShowModalDialog(
            string title,
            string message,
            string primaryButtonLabel,
            Action onPrimaryClick,
            string secondaryButtonLabel,
            Action onSecondaryClick
        )
        {
            CloseActiveDropdown();
            CloseActiveModal();

            if (canvasRootRect == null) return;

            bool prevSuppress = suppressResponsiveRegistration;
            suppressResponsiveRegistration = true;
            try
            {
                activeModalOverlay = CreateUIObject("MM2_ModalDialogOverlay", canvasRootRect);
                StretchFull(activeModalOverlay.GetComponent<RectTransform>());
                activeModalOverlay.transform.SetAsLastSibling();

                Canvas modalCanvas = activeModalOverlay.AddComponent<Canvas>();
                modalCanvas.overrideSorting = true;
                modalCanvas.sortingOrder = 300;
                activeModalOverlay.AddComponent<GraphicRaycaster>();

                // Full-screen dimmed backdrop
                GameObject backdropGo = CreateUIObject("ModalBackdrop", activeModalOverlay.transform);
                StretchFull(backdropGo.GetComponent<RectTransform>());
                Image backdropImg = backdropGo.AddComponent<Image>();
                backdropImg.color = new Color(0f, 0f, 0f, 0.68f);
                backdropImg.raycastTarget = true;

                // Centered dialog panel (anchored at 0.5, 0.5 so it stays centered on all aspect ratios)
                float dialogW = 820f;
                float dialogH = 380f;
                GameObject dialogGo = CreateUIObject("ModalDialogBox", activeModalOverlay.transform);
                RectTransform dialogRt = dialogGo.GetComponent<RectTransform>();
                dialogRt.anchorMin = new Vector2(0.5f, 0.5f);
                dialogRt.anchorMax = new Vector2(0.5f, 0.5f);
                dialogRt.pivot = new Vector2(0.5f, 0.5f);
                dialogRt.anchoredPosition = Vector2.zero;
                dialogRt.sizeDelta = new Vector2(dialogW, dialogH);

                Image dialogBg = dialogGo.AddComponent<Image>();
                dialogBg.sprite = spBoxNavy9 != null ? spBoxNavy9 : spBoxBlack9;
                dialogBg.type = Image.Type.Sliced;
                dialogBg.color = Color.white;

                // Title
                CreateShadowLabel(
                    "Txt_ModalTitle",
                    dialogGo.transform,
                    title,
                    36f,
                    26f,
                    dialogW - 72f,
                    54f,
                    36,
                    TextAnchor.MiddleCenter,
                    new Color32(255, 232, 16, 255),
                    fontUIExtraBold
                );

                CreateSolidRect("ModalHeaderDivider", dialogGo.transform, 36f, 88f, dialogW - 72f, 3f, new Color32(110, 130, 235, 190));

                // Body Message
                CreateShadowLabel(
                    "Txt_ModalMessage",
                    dialogGo.transform,
                    message,
                    48f,
                    110f,
                    dialogW - 96f,
                    155f,
                    28,
                    TextAnchor.MiddleCenter,
                    Color.white,
                    fontUIBold,
                    wordWrap: true
                );

                // Buttons
                bool hasTwoButtons = !string.IsNullOrEmpty(secondaryButtonLabel);
                float btnW = 240f;
                float btnH = 74f;
                float btnY = dialogH - btnH - 28f;

                if (hasTwoButtons)
                {
                    float gap = 48f;
                    float startX = (dialogW - (btnW * 2f + gap)) * 0.5f;
                    CreateGlowButton("Btn_ModalPrimary", dialogGo.transform, primaryButtonLabel, startX, btnY, btnW, btnH, 28, onPrimaryClick);
                    CreateGlowButton("Btn_ModalSecondary", dialogGo.transform, secondaryButtonLabel, startX + btnW + gap, btnY, btnW, btnH, 28, onSecondaryClick);
                }
                else
                {
                    float startX = (dialogW - btnW) * 0.5f;
                    CreateGlowButton("Btn_ModalPrimary", dialogGo.transform, primaryButtonLabel, startX, btnY, btnW, btnH, 28, onPrimaryClick);
                }
            }
            finally
            {
                suppressResponsiveRegistration = prevSuppress;
            }
        }

        // =====================================================================
        // SCREEN SWITCHING & INTERACTION LOGIC
        // =====================================================================
        public void ShowScreen(MenuScreen screen)
        {
            CloseActiveDropdown();
            CloseActiveModal();
            if (screen != MenuScreen.SelectVehicle)
            {
                StopVehicleSelectSfx();
            }
            currentScreen = screen;

            panelMainMenu.SetActive(screen == MenuScreen.MainMenu);
            panelRaces.SetActive(screen == MenuScreen.Races);
            panelSelectVehicle.SetActive(screen == MenuScreen.SelectVehicle);
            if (panelEventResults != null) panelEventResults.SetActive(screen == MenuScreen.EventResults);
            if (panelCrashCourseIntro != null) panelCrashCourseIntro.SetActive(screen == MenuScreen.CrashCourseIntro);
            if (panelCrashCourseHub != null) panelCrashCourseHub.SetActive(screen == MenuScreen.CrashCourseHub);
            if (panelOptions != null) panelOptions.SetActive(screen == MenuScreen.Options);
            if (panelHelp != null) panelHelp.SetActive(screen == MenuScreen.Help);

            // Only enable the 3D Vehicle Turntable camera & rig when on the Select Vehicle screen
            if (showcaseCamera != null) showcaseCamera.enabled = (screen == MenuScreen.SelectVehicle);
            if (showcaseRoot != null) showcaseRoot.SetActive(screen == MenuScreen.SelectVehicle);

            // Left panel background switching (MainMenu / CrashCourse / Options = Full Orange, Races = Top Orange / Bottom Blue, SelectVehicle = Showroom, EventResults / Help = Full-Width)
            bool useFullOrangeLeft =
                screen == MenuScreen.MainMenu ||
                screen == MenuScreen.CrashCourseIntro ||
                screen == MenuScreen.CrashCourseHub ||
                screen == MenuScreen.Options;
            leftFullOrangeDecor.SetActive(useFullOrangeLeft);
            leftSplitRacesDecor.SetActive(screen == MenuScreen.Races);
            if (dividerMidVert != null) dividerMidVert.SetActive(screen != MenuScreen.EventResults && screen != MenuScreen.Help);
            if (topRightNavBarGroup != null) topRightNavBarGroup.SetActive(screen != MenuScreen.EventResults);
            if (blueStripEventResultsTopExt != null) blueStripEventResultsTopExt.SetActive(screen == MenuScreen.EventResults);

            // Header Logo vs Real Rendered Screen Title Text
            imgHeaderLogo.gameObject.SetActive(screen == MenuScreen.MainMenu);
            txtHeaderTitle.gameObject.SetActive(screen != MenuScreen.MainMenu);

            if (screen == MenuScreen.MainMenu)
            {
                RefreshMainMenuDriverUI();
            }
            else if (screen == MenuScreen.Races)
            {
                txtHeaderTitle.text = "RACES";
                UpdateRaceModeVisualsAndControls(applyRaceMetadata: false);
            }
            else if (screen == MenuScreen.SelectVehicle)
            {
                txtHeaderTitle.text = "SELECT VEHICLE";
                RefreshVehicleLockUI();
            }
            else if (screen == MenuScreen.EventResults)
            {
                txtHeaderTitle.text = "EVENT RESULTS";
                RefreshEventResultsUI();
            }
            else if (screen == MenuScreen.CrashCourseIntro)
            {
                txtHeaderTitle.text = "CRASH COURSE";
            }
            else if (screen == MenuScreen.CrashCourseHub)
            {
                txtHeaderTitle.text = "CRASH COURSE";
                RefreshCrashCourseHubUI();
            }
            else if (screen == MenuScreen.Options)
            {
                txtHeaderTitle.text = "OPTIONS";
                PopulateAvailableDisplays();
                PopulateAvailableResolutions();
                RefreshOptionsUI();
            }
            else if (screen == MenuScreen.Help)
            {
                txtHeaderTitle.text = "HELP";
                if (helpScrollRect != null)
                {
                    helpScrollRect.verticalNormalizedPosition = 1f;
                }
            }

            if (lastResponsiveCanvasSize.x >= 100f && lastResponsiveCanvasSize.y >= 100f)
            {
                ApplyResponsiveLayout(lastResponsiveCanvasSize.x, lastResponsiveCanvasSize.y);
            }
        }

        private bool IsRaceModeAvailable(string locationName, int modeIdx)
        {
            if (modeIdx <= 0) return true; // Cruise is always available
            if (modeIdx >= raceModes.Length) return false;
            string[] rNames = MM2RuntimeCityLoader.GetRaceNames(locationName, raceModes[modeIdx]);
            return rNames != null && rNames.Length > 0;
        }

        private void SetRaceMode(int modeIdx)
        {
            MM2GameSession.IsCrashCourseSession = false;
            int clamped = Mathf.Clamp(modeIdx, 0, raceModes.Length - 1);
            if (!IsRaceModeAvailable(MM2GameSession.Location, clamped))
            {
                clamped = 0;
            }
            idxRaceMode = clamped;
            MM2GameSession.RaceType = raceModes[idxRaceMode];
            idxRaceName = 0;
            MM2GameSession.RaceIndex = 0;
            UpdateRaceModeVisualsAndControls(applyRaceMetadata: true);
        }

        private void OnQuickRaceClicked()
        {
            MM2GameSession.IsCrashCourseSession = false;
            if (locations == null || locations.Length == 0)
            {
                ShowScreen(MenuScreen.SelectVehicle);
                return;
            }

            // Collect cities that have at least one Blitz (1), Checkpoint (2), or Circuit (3) race
            var validCityIndices = new List<int>();
            for (int c = 0; c < locations.Length; c++)
            {
                string loc = locations[c];
                for (int m = 1; m <= 3; m++)
                {
                    string[] rNames = MM2RuntimeCityLoader.GetRaceNames(loc, raceModes[m]);
                    if (rNames != null && rNames.Length > 0)
                    {
                        validCityIndices.Add(c);
                        break;
                    }
                }
            }

            int chosenLocIdx = validCityIndices.Count > 0
                ? validCityIndices[UnityEngine.Random.Range(0, validCityIndices.Count)]
                : UnityEngine.Random.Range(0, locations.Length);

            string chosenLocation = locations[chosenLocIdx];

            // Collect which of the 3 race modes (1=Blitz, 2=Checkpoint, 3=Circuit) have races in the chosen city
            var validModeIndices = new List<int>();
            for (int m = 1; m <= 3; m++)
            {
                string[] rNames = MM2RuntimeCityLoader.GetRaceNames(chosenLocation, raceModes[m]);
                if (rNames != null && rNames.Length > 0)
                {
                    validModeIndices.Add(m);
                }
            }

            int chosenModeIdx = validModeIndices.Count > 0
                ? validModeIndices[UnityEngine.Random.Range(0, validModeIndices.Count)]
                : 0;

            string[] modeRaceNames = MM2RuntimeCityLoader.GetRaceNames(chosenLocation, raceModes[chosenModeIdx]) ?? Array.Empty<string>();
            var unlockedRaceIndices = new List<int>();
            for (int r = 0; r < modeRaceNames.Length; r++)
            {
                if (MM2PlayerManager.IsRaceUnlocked(chosenLocation, raceModes[chosenModeIdx], r))
                {
                    unlockedRaceIndices.Add(r);
                }
            }
            int chosenRaceIdx = unlockedRaceIndices.Count > 0
                ? unlockedRaceIndices[UnityEngine.Random.Range(0, unlockedRaceIndices.Count)]
                : 0;

            idxLocation = chosenLocIdx;
            MM2GameSession.Location = chosenLocation;
            if (txtLocationValue != null) txtLocationValue.text = MM2GameSession.Location;

            idxRaceMode = chosenModeIdx;
            MM2GameSession.RaceType = raceModes[idxRaceMode];

            idxRaceName = chosenRaceIdx;
            MM2GameSession.RaceIndex = chosenRaceIdx;

            UpdateRaceModeVisualsAndControls(applyRaceMetadata: true);

            Debug.Log(
                $"[MM2 Menu] Quick Race selected: Location={MM2GameSession.Location}, " +
                $"Mode={MM2GameSession.RaceType}, Race={MM2GameSession.RaceName} (#{MM2GameSession.RaceIndex})"
            );

            ShowScreen(MenuScreen.SelectVehicle);
        }

        private void UpdateRaceModeVisualsAndControls(bool applyRaceMetadata)
        {
            if (!IsRaceModeAvailable(MM2GameSession.Location, idxRaceMode))
            {
                idxRaceMode = 0;
                MM2GameSession.RaceType = raceModes[0];
                idxRaceName = 0;
                MM2GameSession.RaceIndex = 0;
                applyRaceMetadata = false;
            }

            Color32 selectedGreen = new Color32(15, 225, 65, 255);
            int visibleSlot = 0;
            for (int i = 0; i < raceModes.Length; i++)
            {
                bool modeAvailable = IsRaceModeAvailable(MM2GameSession.Location, i);
                if (radioModeRows[i] != null)
                {
                    radioModeRows[i].SetActive(modeAvailable);
                    if (modeAvailable)
                    {
                        float rowY = 128f + visibleSlot * 68f;
                        SetTopLeftRect(radioModeRows[i].GetComponent<RectTransform>(), 155f, rowY, 520f, 60f);
                        visibleSlot++;
                    }
                }

                bool active = (i == idxRaceMode);
                if (radioModeOrbs[i] != null) radioModeOrbs[i].sprite = active ? spOrbGreen : spOrbRed;
                if (radioModeLabels[i] != null) radioModeLabels[i].color = active ? selectedGreen : Color.white;
            }

            bool isCruise = (idxRaceMode == 0);
            bool isBlitz = (idxRaceMode == 1);
            bool isCheckpoint = (idxRaceMode == 2);
            bool isCircuit = (idxRaceMode == 3);

            if (txtBottomRaceHelp != null)
            {
                if (isCruise)
                {
                    txtBottomRaceHelp.text =
                        "Test drive different vehicles and explore the city without the pressure of racing. Red lights are optional!";
                }
                else if (isBlitz)
                {
                    txtBottomRaceHelp.text =
                        "Race against the clock! Pass through all the checkpoints in any order before time runs out.";
                }
                else if (isCheckpoint)
                {
                    txtBottomRaceHelp.text =
                        "Pass through all the checkpoints in any order and cross the finish line first!";
                }
                else
                {
                    txtBottomRaceHelp.text =
                        "Follow the checkpoints in order around a closed circuit for the selected number of laps.";
                }
            }

            currentRaceNames = MM2RuntimeCityLoader.GetRaceNames(MM2GameSession.Location, MM2GameSession.RaceType) ?? Array.Empty<string>();
            if (currentRaceNames.Length > 0)
            {
                idxRaceName = Mathf.Clamp(idxRaceName, 0, currentRaceNames.Length - 1);
                if (!MM2PlayerManager.IsRaceUnlocked(MM2GameSession.Location, MM2GameSession.RaceType, idxRaceName))
                {
                    idxRaceName = 0;
                }
                MM2GameSession.RaceIndex = idxRaceName;
                MM2GameSession.RaceName = currentRaceNames[idxRaceName];
            }
            else
            {
                idxRaceName = 0;
                MM2GameSession.RaceIndex = 0;
                MM2GameSession.RaceName = "";
            }

            RefreshCruiseSpawnListForLocation(MM2GameSession.Location);
            bool hasCruiseSpawns = currentCruiseSpawnNames != null && currentCruiseSpawnNames.Length > 0;

            if (lblRaceName != null)
            {
                lblRaceName.text = (isCruise && hasCruiseSpawns) ? "STARTING POINT" : "RACE NAME";
            }

            bool showSelector = isCruise
                ? hasCruiseSpawns
                : (currentRaceNames.Length > 0);
            if (boxRaceNameDisabled != null) boxRaceNameDisabled.SetActive(!showSelector);
            if (selectorRaceNameGroup != null) selectorRaceNameGroup.SetActive(showSelector);
            if (txtRaceNameValue != null)
            {
                if (isCruise)
                {
                    txtRaceNameValue.text = showSelector ? currentCruiseSpawnNames[idxCruiseSpawn] : "";
                }
                else
                {
                    txtRaceNameValue.text = showSelector ? currentRaceNames[idxRaceName] : "";
                }
            }

            if (lapsRowGroup != null) lapsRowGroup.SetActive(isCircuit);
            if (opponentsRowGroup != null) opponentsRowGroup.SetActive(isCheckpoint || isCircuit);

            if (!isCruise && applyRaceMetadata)
            {
                ApplyCurrentRaceMetadata();
            }
            else
            {
                if (txtLapsValue != null) txtLapsValue.text = MM2GameSession.NumLaps.ToString();
                if (txtOpponentsValue != null) txtOpponentsValue.text = MM2GameSession.NumOpponents.ToString();
            }

            UpdateCityMapPreview(MM2GameSession.Location);
        }

        private void RefreshCruiseSpawnListForLocation(string locationName)
        {
            IReadOnlyList<MM2CruiseSpawnLocation> locs = MM2RuntimeCityLoader.GetCruiseSpawnLocations(locationName);
            currentCruiseSpawnNames = MM2RuntimeCityLoader.GetCruiseSpawnDisplayNames(locationName);
            if (locs == null || locs.Count == 0)
            {
                idxCruiseSpawn = 0;
                MM2GameSession.CruiseSpawnIndex = 0;
                MM2GameSession.CruiseSpawnFileName = string.Empty;
                return;
            }

            idxCruiseSpawn = Mathf.Clamp(MM2GameSession.CruiseSpawnIndex, 0, locs.Count - 1);
            MM2GameSession.CruiseSpawnIndex = idxCruiseSpawn;
            MM2GameSession.CruiseSpawnFileName = locs[idxCruiseSpawn].FileName ?? string.Empty;
        }

        private void CycleCruiseSpawnIndex(int dir)
        {
            if (currentCruiseSpawnNames == null || currentCruiseSpawnNames.Length == 0) return;
            SetCruiseSpawnIndex((idxCruiseSpawn + dir + currentCruiseSpawnNames.Length) % currentCruiseSpawnNames.Length);
        }

        private void SetCruiseSpawnIndex(int idx)
        {
            IReadOnlyList<MM2CruiseSpawnLocation> locs = MM2RuntimeCityLoader.GetCruiseSpawnLocations(MM2GameSession.Location);
            if (locs == null || locs.Count == 0) return;

            idxCruiseSpawn = Mathf.Clamp(idx, 0, locs.Count - 1);
            MM2GameSession.CruiseSpawnIndex = idxCruiseSpawn;
            MM2GameSession.CruiseSpawnFileName = locs[idxCruiseSpawn].FileName ?? string.Empty;

            if (txtRaceNameValue != null && idxRaceMode == 0)
            {
                txtRaceNameValue.text = locs[idxCruiseSpawn].DisplayName;
            }

            UpdateCityMapPreview(MM2GameSession.Location);
        }

        private void CycleRaceIndex(int dir)
        {
            if (currentRaceNames == null || currentRaceNames.Length == 0) return;
            int len = currentRaceNames.Length;
            int step = dir >= 0 ? 1 : -1;
            int cur = idxRaceName;
            for (int attempt = 1; attempt <= len; attempt++)
            {
                int candidate = (cur + step * attempt + len * 2) % len;
                if (MM2PlayerManager.IsRaceUnlocked(MM2GameSession.Location, MM2GameSession.RaceType, candidate))
                {
                    SetRaceIndex(candidate);
                    return;
                }
            }
        }

        private void SetRaceIndex(int idx)
        {
            if (currentRaceNames == null || currentRaceNames.Length == 0) return;
            int clamped = Mathf.Clamp(idx, 0, currentRaceNames.Length - 1);
            if (!MM2PlayerManager.IsRaceUnlocked(MM2GameSession.Location, MM2GameSession.RaceType, clamped)) return;
            idxRaceName = clamped;
            MM2GameSession.RaceIndex = idxRaceName;
            MM2GameSession.RaceName = currentRaceNames[idxRaceName];
            if (txtRaceNameValue != null) txtRaceNameValue.text = MM2GameSession.RaceName;

            ApplyCurrentRaceMetadata();
            UpdateCityMapPreview(MM2GameSession.Location);
        }

        private void ApplyCurrentRaceMetadata()
        {
            if (idxRaceMode == 0) return;

            MM2RaceMetadata meta = MM2RuntimeCityLoader.GetRaceMetadata(
                MM2GameSession.Location,
                MM2GameSession.RaceType,
                idxRaceName
            );

            MM2GameSession.RaceTimeLimit = Mathf.Max(5f, meta.TimeLimit);
            MM2GameSession.NumLaps = Mathf.Clamp(meta.NumLaps > 0 ? meta.NumLaps : 3, 1, 10);
            MM2GameSession.NumOpponents = Mathf.Clamp(meta.Opponents, 0, 8);

            if (txtLapsValue != null) txtLapsValue.text = MM2GameSession.NumLaps.ToString();
            if (txtOpponentsValue != null) txtOpponentsValue.text = MM2GameSession.NumOpponents.ToString();

            int todIdx = Mathf.Clamp(meta.TimeOfDayIndex, 0, timesOfDay.Length - 1);
            SetTimeOfDay(todIdx);

            int weathIdx = Mathf.Clamp(meta.WeatherIndex, 0, weathers.Length - 1);
            SetWeather(weathIdx);

            SetDensity(ref MM2GameSession.PedestrianDensity, barPedestrianFill, Mathf.Clamp(meta.PedestrianDensity, 0, 100));
            SetDensity(ref MM2GameSession.TrafficDensity, barTrafficFill, Mathf.Clamp(meta.TrafficDensity, 0, 100));
            SetDensity(ref MM2GameSession.CopDensity, barCopFill, Mathf.Clamp(meta.CopDensity, 0, 100));
        }

        private void CycleLaps(int dir)
        {
            int nextLaps = MM2GameSession.NumLaps + dir;
            if (nextLaps > 10) nextLaps = 1;
            if (nextLaps < 1) nextLaps = 10;
            SetLaps(nextLaps);
        }

        private void SetLaps(int laps)
        {
            MM2GameSession.NumLaps = Mathf.Clamp(laps, 1, 10);
            if (txtLapsValue != null) txtLapsValue.text = MM2GameSession.NumLaps.ToString();
        }

        private void CycleOpponents(int dir)
        {
            int nextOpps = MM2GameSession.NumOpponents + dir;
            if (nextOpps > 8) nextOpps = 0;
            if (nextOpps < 0) nextOpps = 8;
            MM2GameSession.NumOpponents = nextOpps;
            if (txtOpponentsValue != null) txtOpponentsValue.text = MM2GameSession.NumOpponents.ToString();
        }

        private void CycleLocation(int dir)
        {
            SetLocation((idxLocation + dir + locations.Length) % locations.Length);
        }

        private void SetLocation(int idx)
        {
            int clamped = Mathf.Clamp(idx, 0, locations.Length - 1);
            bool changed = (clamped != idxLocation) || !string.Equals(MM2GameSession.Location, locations[clamped], StringComparison.OrdinalIgnoreCase);
            idxLocation = clamped;
            MM2GameSession.Location = locations[idxLocation];
            if (txtLocationValue != null) txtLocationValue.text = MM2GameSession.Location;
            if (changed)
            {
                idxRaceName = 0;
                MM2GameSession.RaceIndex = 0;
                idxCruiseSpawn = 0;
                MM2GameSession.CruiseSpawnIndex = 0;
                MM2GameSession.CruiseSpawnFileName = string.Empty;
            }
            UpdateRaceModeVisualsAndControls(applyRaceMetadata: idxRaceMode != 0);
        }

        private void UpdateCityMapPreview(string locationName)
        {
            if (imgCityMap == null) return;

            // In Cruise mode, if a specific Starting Point is selected (index > 0), display its location photo from jpg/spawn_*.jpg
            if (idxRaceMode == 0 && idxCruiseSpawn > 0)
            {
                IReadOnlyList<MM2CruiseSpawnLocation> spawnLocs = MM2RuntimeCityLoader.GetCruiseSpawnLocations(locationName);
                if (spawnLocs != null && idxCruiseSpawn < spawnLocs.Count && !spawnLocs[idxCruiseSpawn].IsRandom)
                {
                    Sprite spawnPhoto = MM2RuntimeCityLoader.LoadCruiseSpawnPreviewSprite(spawnLocs[idxCruiseSpawn].PhotoStem);
                    if (spawnPhoto != null)
                    {
                        imgCityMap.sprite = spawnPhoto;
                        return;
                    }
                }
            }

            Sprite raceMap = MM2RuntimeCityLoader.LoadRaceMapPreviewSprite(
                locationName,
                MM2GameSession.RaceType,
                MM2GameSession.RaceIndex
            );

            if (raceMap != null)
            {
                imgCityMap.sprite = raceMap;
                return;
            }

            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(locationName);
            if (citySpec != null && citySpec.CustomMapPreviewSprite != null)
            {
                imgCityMap.sprite = citySpec.CustomMapPreviewSprite;
            }
        }

        private void CycleTimeOfDay(int dir)
        {
            SetTimeOfDay((idxTimeOfDay + dir + timesOfDay.Length) % timesOfDay.Length);
        }

        private void SetTimeOfDay(int idx)
        {
            int prev = idxTimeOfDay;
            idxTimeOfDay = Mathf.Clamp(idx, 0, timesOfDay.Length - 1);
            MM2GameSession.TimeOfDay = timesOfDay[idxTimeOfDay];
            if (txtTimeOfDayValue != null) txtTimeOfDayValue.text = MM2GameSession.TimeOfDay;
            if (prev != idxTimeOfDay && currentScreen == MenuScreen.Races)
            {
                string clipName = idxTimeOfDay == 0 ? "UImorning.22k"
                    : (idxTimeOfDay == 1 ? "UInoon.22k"
                    : (idxTimeOfDay == 2 ? "UImorning.22k" : "UInight.22k"));
                PlayMenuSfx(clipName);
            }
        }

        private void CycleWeather(int dir)
        {
            SetWeather((idxWeather + dir + weathers.Length) % weathers.Length);
        }

        private void SetWeather(int idx)
        {
            int prev = idxWeather;
            idxWeather = Mathf.Clamp(idx, 0, weathers.Length - 1);
            MM2GameSession.Weather = weathers[idxWeather];
            if (txtWeatherValue != null) txtWeatherValue.text = MM2GameSession.Weather;
            if (prev != idxWeather && currentScreen == MenuScreen.Races)
            {
                string clipName = idxWeather == 3 ? "UIrain.22k" : "UIcloudy.22k";
                PlayMenuSfx(clipName);
            }
        }

        private void PlayMenuSfx(string clipName)
        {
            AudioClip clip = MM2RuntimeDecoders.LoadAudioClip(clipName);
            if (clip == null) return;
            AudioSource src = GetComponent<AudioSource>();
            if (src == null)
            {
                src = gameObject.AddComponent<AudioSource>();
                src.spatialBlend = 0f;
                src.playOnAwake = false;
            }
            src.PlayOneShot(clip, 0.75f);
        }

        private void StopVehicleSelectSfx()
        {
            if (vehicleSelectAudioSource != null && vehicleSelectAudioSource.isPlaying)
            {
                vehicleSelectAudioSource.Stop();
            }
        }

        private void PlayVehicleSelectSfx(VehicleSpec spec)
        {
            StopVehicleSelectSfx();
            if (spec == null) return;

            string selectWave = !string.IsNullOrEmpty(spec.SelectAudioName)
                ? spec.SelectAudioName
                : (!string.IsNullOrEmpty(spec.BaseName) ? $"{spec.BaseName}_select" : null);
            if (string.IsNullOrEmpty(selectWave)) return;

            AudioClip clip = MM2RuntimeDecoders.LoadAudioClip(selectWave);
            if (clip == null) return;

            if (vehicleSelectAudioSource == null)
            {
                GameObject sfxGo = new GameObject("MM2_VehicleSelectAudio");
                sfxGo.transform.SetParent(transform, false);
                vehicleSelectAudioSource = sfxGo.AddComponent<AudioSource>();
                vehicleSelectAudioSource.spatialBlend = 0f;
                vehicleSelectAudioSource.playOnAwake = false;
                vehicleSelectAudioSource.loop = false;
            }

            vehicleSelectAudioSource.clip = clip;
            vehicleSelectAudioSource.volume = 0.8f;
            vehicleSelectAudioSource.Play();
        }

        private void AdjustDensity(ref int valueField, RectTransform fillBar, int delta)
        {
            SetDensity(ref valueField, fillBar, valueField + delta);
        }

        private void SetDensity(ref int valueField, RectTransform fillBar, int newPercent)
        {
            valueField = Mathf.Clamp(newPercent, 0, 100);
            UpdateDensityBarFill(fillBar, valueField);
        }

        private void UpdateDensityBarFill(RectTransform fillBar, int percent)
        {
            if (fillBar == null) return;
            float maxW = 399f;
            float clamped = Mathf.Clamp(percent, 0, 100);
            float w = (clamped <= 0f) ? 0f : Mathf.Max(4f, maxW * (clamped / 100f));
            fillBar.sizeDelta = new Vector2(w, 12f);
        }

        private void CycleVehicle(int dir)
        {
            SetVehicle((idxVehicle + dir + vehicles.Length) % vehicles.Length);
        }

        private void SetVehicle(int idx)
        {
            idxVehicle = Mathf.Clamp(idx, 0, vehicles.Length - 1);
            MM2GameSession.VehicleName = vehicles[idxVehicle];
            if (txtVehicleValue != null) txtVehicleValue.text = MM2GameSession.VehicleName;

            VehicleSpec spec = VehicleCatalog.GetSpec(MM2GameSession.VehicleName);
            vehicleColors = spec.Colors;
            idxVehicleColor = 0;
            MM2GameSession.VehicleColor = vehicleColors[0];
            if (txtColorValue != null) txtColorValue.text = MM2GameSession.VehicleColor;

            float maxW = 408f;
            if (scoreBarHorsepower != null) scoreBarHorsepower.sizeDelta = new Vector2(Mathf.Clamp01(spec.HorsepowerScore) * maxW, 22f);
            if (scoreBarTopSpeed != null) scoreBarTopSpeed.sizeDelta = new Vector2(Mathf.Clamp01(spec.TopSpeedScore) * maxW, 22f);
            if (scoreBarDurability != null) scoreBarDurability.sizeDelta = new Vector2(Mathf.Clamp01(spec.DurabilityScore) * maxW, 22f);
            if (scoreBarMass != null) scoreBarMass.sizeDelta = new Vector2(Mathf.Clamp01(spec.MassScore) * maxW, 22f);

            RebuildTurntableCarModel();
            RefreshVehicleLockUI();

            if (currentScreen == MenuScreen.SelectVehicle)
            {
                PlayVehicleSelectSfx(spec);
            }
        }

        private void CycleVehicleColor(int dir)
        {
            SetVehicleColor((idxVehicleColor + dir + vehicleColors.Length) % vehicleColors.Length);
        }

        private void SetVehicleColor(int idx)
        {
            idxVehicleColor = Mathf.Clamp(idx, 0, vehicleColors.Length - 1);
            MM2GameSession.VehicleColor = vehicleColors[idxVehicleColor];
            if (txtColorValue != null) txtColorValue.text = MM2GameSession.VehicleColor;
            if (showcaseCarPivot != null)
            {
                VehiclePaintjob.ApplyColorToHierarchy(showcaseCarPivot, MM2GameSession.VehicleName, MM2GameSession.VehicleColor);
            }
            RefreshVehicleLockUI();
        }

        private void RefreshVehicleLockUI()
        {
            bool unlocked = MM2PlayerManager.IsVehicleAndColorUnlocked(MM2GameSession.VehicleName, idxVehicleColor);
            if (txtVehicleLockedBanner != null)
            {
                txtVehicleLockedBanner.gameObject.SetActive(!unlocked);
            }
            if (txtVehicleDesc != null)
            {
                txtVehicleDesc.text = MM2PlayerManager.GetVehicleOrColorLockDescription(MM2GameSession.VehicleName, idxVehicleColor);
            }
        }

        private void CycleTransmission(int dir)
        {
            SetTransmission((idxTransmission + dir + transmissions.Length) % transmissions.Length);
        }

        private void SetTransmission(int idx)
        {
            idxTransmission = Mathf.Clamp(idx, 0, transmissions.Length - 1);
            MM2GameSession.Transmission = transmissions[idxTransmission];
            if (txtTransmissionValue != null) txtTransmissionValue.text = MM2GameSession.Transmission;
        }

        private void OnGoDriveClicked()
        {
            if (!MM2GameSession.IsCrashCourseSession &&
                !MM2PlayerManager.IsVehicleAndColorUnlocked(MM2GameSession.VehicleName, idxVehicleColor))
            {
                bool vehicleUnlocked = MM2PlayerManager.IsVehicleUnlocked(MM2GameSession.VehicleName);
                string lockDesc = MM2PlayerManager.GetVehicleOrColorLockDescription(MM2GameSession.VehicleName, idxVehicleColor);
                string title = vehicleUnlocked ? "PAINT JOB LOCKED" : "VEHICLE LOCKED";
                string body = vehicleUnlocked
                    ? $"YOU CANNOT PICK A LOCKED PAINT JOB.\n\n{lockDesc}"
                    : $"YOU CANNOT PICK A LOCKED VEHICLE.\n\n{lockDesc}";
                PlayMenuSfx("UIbad.22k");
                ShowModalDialog(title, body, "OK", CloseActiveModal, null, null);
                return;
            }

            CleanupLiveMenuBackground();
            RestoreDisabledSceneCameras();
            MM2PlayerManager.SaveCurrentSelectionToActiveProfile();

            Debug.Log(
                $"[MM2 Menu] GO DRIVE! Launching map with Vehicle={MM2GameSession.VehicleName} ({MM2GameSession.VehicleColor}, {MM2GameSession.Transmission}), " +
                $"Location={MM2GameSession.Location}, Time={MM2GameSession.TimeOfDay}, Weather={MM2GameSession.Weather}, " +
                $"Peds={MM2GameSession.PedestrianDensity}%, Traffic={MM2GameSession.TrafficDensity}%, Cops={MM2GameSession.CopDensity}%"
            );

            MM2GameSession.LaunchedFromGoDrive = true;

            // If we are already in a scene that has the playable car (e.g. GoDrive or a recovered scene backup), re-enable it directly
            if (disabledSceneGameplayObjects.Count > 0)
            {
                MM2GameSession.LaunchedFromGoDrive = false;
                foreach (var go in disabledSceneGameplayObjects)
                {
                    if (go != null) go.SetActive(true);
                }
                ApplySelectedPaintjobToActiveVehicles();
                foreach (var chase in FindObjectsByType<ArcadeChaseCamera>(FindObjectsSortMode.None))
                {
                    if (chase != null)
                    {
                        chase.enabled = true;
                        Camera chaseCam = chase.GetComponent<Camera>();
                        if (chaseCam != null) chaseCam.enabled = true;
                    }
                }
                EnsureEscapeReturnListener();
                Destroy(gameObject);
                return;
            }

            // Load the GoDrive gameplay scene
            SceneManager.LoadScene("GoDrive");
        }

        // =====================================================================
        // UI FACTORY HELPERS (TOP-LEFT 1920x1080 COORDINATE SYSTEM)
        // =====================================================================
        private static GameObject CreateUIObject(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private void SetTopLeftRect(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);

            if (!suppressResponsiveRegistration && rt != null)
            {
                var entry = new ResponsiveRectEntry
                {
                    BaseX = x,
                    BaseY = y,
                    BaseW = w,
                    BaseH = h,
                    IsHalfScaleText = false
                };
                responsiveRects[rt] = entry;
                if (lastResponsiveCanvasSize.x >= 100f && lastResponsiveCanvasSize.y >= 100f)
                {
                    ApplySingleResponsiveRect(rt, entry, lastResponsiveCanvasSize.x, lastResponsiveCanvasSize.y);
                }
            }
        }

        private void ApplyResponsiveLayout(float canvasW, float canvasH)
        {
            var keys = new List<RectTransform>(responsiveRects.Keys);
            foreach (RectTransform rt in keys)
            {
                if (rt == null)
                {
                    responsiveRects.Remove(rt);
                    continue;
                }
                ApplySingleResponsiveRect(rt, responsiveRects[rt], canvasW, canvasH);
            }
        }

        private bool IsFullScreenStretchContainer(RectTransform rt)
        {
            if (rt == null) return false;
            if (rt == canvasRootRect) return true;
            return rt.anchorMin == Vector2.zero && rt.anchorMax == Vector2.one;
        }

        private void ApplySingleResponsiveRect(RectTransform rt, ResponsiveRectEntry entry, float canvasW, float canvasH)
        {
            if (rt == null) return;

            float extraW = Mathf.Max(0f, canvasW - 1920f);
            float extraH = Mathf.Max(0f, canvasH - 1080f);

            // Vertical spacing parameters for taller screens (16:10, 4:3):
            // - Modest breathing room above the title/header bar (8% of extraH)
            // - Extra breathing room below the title before the horizontal divider (7% of extraH)
            // - Remaining 82% of extraH spreads the content (y >= 100) vertically across the screen
            float topHeaderShift = extraH * 0.08f;
            float belowTitleShift = extraH * 0.07f;
            float headerBottomShift = topHeaderShift + belowTitleShift;
            float contentScaleY = 1f + (extraH * 0.82f) / 980f;

            float MapY(float y) => 100f + headerBottomShift + (y - 100f) * contentScaleY;

            RectTransform parentRt = rt.parent as RectTransform;
            string objName = rt.gameObject.name;

            // 1. Handle widget-local children (whose parent is not a StretchFull screen container)
            if (!IsFullScreenStretchContainer(parentRt))
            {
                if (objName == "BlueCollagePlate" && parentRt != null)
                {
                    rt.anchoredPosition = new Vector2(-parentRt.anchoredPosition.x, -parentRt.anchoredPosition.y);
                    rt.sizeDelta = new Vector2(canvasW, canvasH);
                    return;
                }
                if (objName == "BevelFrame" && parentRt != null)
                {
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = parentRt.sizeDelta;
                    return;
                }
                if (parentRt != null && parentRt.gameObject.name == "Box_CourseCurriculum" && objName.StartsWith("CurriculumRow_", StringComparison.Ordinal))
                {
                    float mappedRowY = 78f + (entry.BaseY - 78f) * contentScaleY;
                    rt.anchoredPosition = new Vector2(entry.BaseX, -mappedRowY);
                    rt.sizeDelta = new Vector2(entry.BaseW, entry.BaseH);
                    return;
                }
                if (parentRt != null && parentRt.gameObject.name == "Box_EventResultsTable" && entry.BaseY >= 36f)
                {
                    float mappedRowY = 38f + (entry.BaseY - 38f) * contentScaleY;
                    rt.anchoredPosition = new Vector2(entry.BaseX, -mappedRowY);
                    return;
                }
                return;
            }

            // 2. Top-level screen elements inside a StretchFull container
            float newX = entry.BaseX;
            float newY = entry.BaseY;
            float newW = entry.BaseW;
            float newH = entry.BaseH;

            // --- HORIZONTAL MAPPING (21:9 & wider) ---
            bool isTopRightNav = (topRightNavBarGroup != null && rt.parent == topRightNavBarGroup.transform);
            bool isEventResultsChild = (panelEventResults != null && rt.IsChildOf(panelEventResults.transform));

            if (isTopRightNav)
            {
                // Right-align [ OPTIONS ] [ ? ] [ _ ] [ X ]
                newX = entry.BaseX + extraW;
            }
            else if (objName == "Bar_HorizMain")
            {
                // On Event Results, stop the top silver-to-navy bar at the x=1256 divider so it doesn't poke behind the MM2 logo;
                // on all other screens, stretch it to meet the right-aligned OPTIONS button.
                newX = entry.BaseX;
                newW = (currentScreen == MenuScreen.EventResults)
                    ? (1256f - entry.BaseX)
                    : (entry.BaseW + extraW);
            }
            else if (!entry.IsHalfScaleText && entry.BaseX + entry.BaseW >= 1915f)
            {
                // Horizontal dividers & showroom panels that reach the right edge of the screen
                newX = entry.BaseX;
                newW = entry.BaseW + extraW;
            }
            else if (objName == "Vehicle3D_RawImage")
            {
                // Center 3D turntable viewport horizontally inside the expanded showroom
                newX = entry.BaseX + 0.5f * extraW;
            }
            else if (isEventResultsChild)
            {
                // Event Results: keep left standings table & divider left-aligned (<= 1260), center right action column in remaining space
                if (entry.BaseX > 1260f)
                {
                    newX = entry.BaseX + 0.5f * extraW;
                }
            }
            else
            {
                // Standard screens (MainMenu, Races, SelectVehicle, CrashCourseIntro, CrashCourseHub):
                // Left column (<= 830, including blue strips, vertical chrome, curriculum, bottom help box) stays left-aligned;
                // Main content on the right (> 830) is centered in the remaining horizontal space.
                if (entry.BaseX > 830f)
                {
                    newX = entry.BaseX + 0.5f * extraW;
                }
            }

            // --- VERTICAL MAPPING (16:10, 4:3 & taller) ---
            if (objName == "Bar_VertChrome")
            {
                newY = 0f;
                newH = canvasH;
            }
            else if (objName == "BlueStrip_FarLeft")
            {
                newY = 0f;
                newH = MapY(945f);
            }
            else if (entry.BaseY == 0f && entry.BaseH <= 105f)
            {
                // BlueStrip_TopHeader & BlueStrip_EventResultsTopExt
                newY = 0f;
                newH = entry.BaseH + headerBottomShift;
            }
            else if (entry.BaseY == 0f && entry.BaseH >= 800f)
            {
                // Divider_MidVert & Divider_EventResultsVert
                newY = 0f;
                newH = MapY(entry.BaseH);
            }
            else if (entry.BaseY < 98f)
            {
                // Top Header Title, Logo, Silver Header Bar, and [ OPTIONS ] [ ? ] [ _ ] [ X ]
                newY = entry.BaseY + topHeaderShift;
                newH = entry.BaseH;
            }
            else
            {
                // Main Content Region (y >= 98) spreads vertically across the remaining screen height
                newY = MapY(entry.BaseY);
                if (!entry.IsHalfScaleText && entry.BaseH >= 150f)
                {
                    newH = entry.BaseH * contentScaleY;
                }
                else
                {
                    newH = entry.BaseH;
                }
            }

            rt.anchoredPosition = new Vector2(newX, -newY);
            if (entry.IsHalfScaleText)
            {
                rt.sizeDelta = new Vector2(newW * 2f, newH * 2f);
            }
            else
            {
                rt.sizeDelta = new Vector2(newW, newH);
            }
        }

        private GameObject CreateMaskedBlueRegion(string name, Transform parent, float x, float y, float w, float h)
        {
            GameObject maskGo = CreateUIObject(name, parent);
            SetTopLeftRect(maskGo.GetComponent<RectTransform>(), x, y, w, h);
            maskGo.AddComponent<RectMask2D>();

            GameObject blueChild = CreateUIObject("BlueCollagePlate", maskGo.transform);
            SetTopLeftRect(blueChild.GetComponent<RectTransform>(), -x, -y, 1920f, 1080f);
            Image img = blueChild.AddComponent<Image>();
            img.sprite = spBlueCollage;
            img.raycastTarget = false;
            return maskGo;
        }

        private Image CreateSpriteImage(string name, Transform parent, Sprite sprite, float x, float y, float w, float h)
        {
            GameObject go = CreateUIObject(name, parent);
            SetTopLeftRect(go.GetComponent<RectTransform>(), x, y, w, h);
            Image img = go.AddComponent<Image>();
            img.sprite = sprite;
            return img;
        }

        private GameObject CreateSlicedBox(string name, Transform parent, Sprite slicedSprite, float x, float y, float w, float h)
        {
            Image img = CreateSpriteImage(name, parent, slicedSprite, x, y, w, h);
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1.0f;
            return img.gameObject;
        }

        private GameObject CreateSolidRect(string name, Transform parent, float x, float y, float w, float h, Color color)
        {
            GameObject go = CreateUIObject(name, parent);
            SetTopLeftRect(go.GetComponent<RectTransform>(), x, y, w, h);
            Image img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return go;
        }

        private void CreateTiledGridBox(string name, Transform parent, float x, float y, float w, float h, float cols, float rows)
        {
            GameObject go = CreateUIObject(name, parent);
            SetTopLeftRect(go.GetComponent<RectTransform>(), x, y, w, h);

            RawImage raw = go.AddComponent<RawImage>();
            raw.texture = texGridBlue;
            // grid_blue_tile.png has 4x4 cells per tile
            raw.uvRect = new Rect(0f, 0f, cols / 4f, rows / 4f);
            raw.raycastTarget = false;

            GameObject frame = CreateSlicedBox("BevelFrame", go.transform, spFrameTrans9, 0f, 0f, w, h);
            frame.GetComponent<Image>().raycastTarget = false;
        }

        private Text CreateHeaderTitleText(
            string name,
            Transform parent,
            string content,
            float x,
            float y,
            float w,
            float h
        )
        {
            // Render at 1:1 native CanvasScaler resolution with FontStyle.Normal on OpenSans-ExtraBoldItalic
            // so FreeType rasterizes exact 1:1 screen-pixel glyphs without synthetic double-slant/embolden distortion
            GameObject go = CreateUIObject(name, parent);
            SetTopLeftRect(go.GetComponent<RectTransform>(), x, y, w, h);

            Text txt = go.AddComponent<Text>();
            txt.font = fontHeaderItalic;
            txt.fontStyle = FontStyle.Normal;
            txt.fontSize = 72;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = new Color32(255, 232, 16, 255); // Classic MM2 gold-yellow
            txt.text = content;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;

            // Smooth multi-ring radial outline + warm orange-red bevel rim + feathered drop shadow
            MM2SmoothTextEffect fx = go.AddComponent<MM2SmoothTextEffect>();
            fx.IsHeaderStyle = true;

            return txt;
        }

        private Text CreateShadowLabel(
            string name,
            Transform parent,
            string content,
            float x,
            float y,
            float w,
            float h,
            int fontSize,
            TextAnchor align,
            Color color,
            Font overrideFont = null,
            bool? wordWrap = null
        )
        {
            // Render at 1:1 native CanvasScaler resolution so Unity's dynamic font atlas maps 1 texel = 1 physical pixel
            GameObject go = CreateUIObject(name, parent);
            SetTopLeftRect(go.GetComponent<RectTransform>(), x, y, w, h);

            // Pick the matching font weight automatically if not overridden:
            // - Yellow value/description text uses bold regular-width fontUIBold
            // - White/green/orange headings & field labels use condensed extra-bold fontCondensedExtraBold
            Font targetFont = overrideFont;
            if (targetFont == null)
            {
                bool isYellowBodyOrValue = (color.r > 0.9f && color.g > 0.85f && color.b < 0.3f);
                targetFont = isYellowBodyOrValue ? fontUIBold : fontCondensedExtraBold;
            }

            bool shouldWrap = wordWrap ?? (align == TextAnchor.UpperLeft || align == TextAnchor.UpperCenter || align == TextAnchor.UpperRight);

            Text txt = go.AddComponent<Text>();
            txt.font = targetFont;
            txt.fontStyle = FontStyle.Normal;
            txt.fontSize = fontSize;
            txt.alignment = align;
            txt.color = color;
            txt.text = content;
            txt.horizontalOverflow = shouldWrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;

            MM2SmoothTextEffect fx = go.AddComponent<MM2SmoothTextEffect>();
            fx.IsHeaderStyle = false;
            fx.OutlineRadius = 1.25f;
            fx.OutlineColor = new Color32(0, 0, 0, 96);
            fx.ShadowOffset = new Vector2(2.0f, -2.0f);
            fx.ShadowColor = new Color32(0, 0, 0, 175);

            return txt;
        }

        private Button CreateGlowButton(
            string name,
            Transform parent,
            string label,
            float x,
            float y,
            float w,
            float h,
            int fontSize,
            Action onClick
        )
        {
            GameObject go = CreateSlicedBox(name, parent, spBtnGlowRect, x, y, w, h);
            Image img = go.GetComponent<Image>();

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            SpriteState st = new SpriteState
            {
                highlightedSprite = spBtnGlowRectHover,
                pressedSprite = spBtnGlowRectHover,
                selectedSprite = spBtnGlowRectHover
            };
            btn.transition = Selectable.Transition.SpriteSwap;
            btn.spriteState = st;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            CreateShadowLabel("Label", go.transform, label, 8f, 6f, w - 16f, h - 12f, fontSize, TextAnchor.MiddleCenter, Color.white, fontUIExtraBold);
            return btn;
        }

        private Button CreateRightArrowButton(
            string name,
            Transform parent,
            string label,
            float x,
            float y,
            float w,
            float h,
            int fontSize,
            Action onClick
        )
        {
            Image img = CreateSpriteImage(name, parent, spBtnArrowRight, x, y, w, h);
            Button btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            SpriteState st = new SpriteState
            {
                highlightedSprite = spBtnArrowRightHover,
                pressedSprite = spBtnArrowRightHover,
                selectedSprite = spBtnArrowRightHover
            };
            btn.transition = Selectable.Transition.SpriteSwap;
            btn.spriteState = st;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            // Offset text slightly left so it centers inside the arrow shaft across all button sizes
            float padLeft = Mathf.Min(18f, w * 0.06f);
            float padRight = Mathf.Min(57f, w * 0.18f);
            float padVert = Mathf.Min(8f, h * 0.14f);
            CreateShadowLabel("Label", img.transform, label, padLeft, padVert, Mathf.Max(24f, w - padLeft - padRight), Mathf.Max(16f, h - padVert * 2f), fontSize, TextAnchor.MiddleCenter, Color.white, fontUIExtraBold);
            return btn;
        }

        private Button CreateLeftBackButton(
            string name,
            Transform parent,
            string label,
            float x,
            float y,
            float w,
            float h,
            int fontSize,
            Action onClick
        )
        {
            Image img = CreateSpriteImage(name, parent, spBtnArrowLeft, x, y, w, h);
            Button btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            SpriteState st = new SpriteState
            {
                highlightedSprite = spBtnArrowLeftHover,
                pressedSprite = spBtnArrowLeftHover,
                selectedSprite = spBtnArrowLeftHover
            };
            btn.transition = Selectable.Transition.SpriteSwap;
            btn.spriteState = st;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            CreateShadowLabel("Label", img.transform, label, 55f, 8f, w - 75f, h - 16f, fontSize, TextAnchor.MiddleCenter, Color.white, fontUIExtraBold);
            return btn;
        }

        private Text CreateSelectorBoxWithSteppers(
            Transform parent,
            string name,
            float x,
            float y,
            float boxW,
            float boxH,
            string initialValue,
            Action onUp,
            Action onDown,
            string[] options = null,
            Func<int> getCurrentIndex = null,
            Action<int> onSelectIndex = null,
            Func<string[]> getOptionsDynamic = null,
            Func<int, bool> isOptionEnabled = null,
            int customMaxRowsPerCol = 7
        )
        {
            GameObject boxGo = CreateSlicedBox(name, parent, spBoxNavy9, x, y, boxW, boxH);
            RectTransform boxRt = boxGo.GetComponent<RectTransform>();

            // Yellow selected value text (turns red-orange while dropdown picker is open, matching MM2)
            Color defaultYellow = new Color32(255, 240, 20, 255);
            Color activeRedOrange = new Color32(235, 38, 15, 255);
            Text valTxt = CreateShadowLabel(
                "Txt_Value",
                boxGo.transform,
                initialValue,
                18f,
                4f,
                boxW - 70f,
                boxH - 8f,
                30,
                TextAnchor.MiddleLeft,
                defaultYellow
            );

            // Right-side indicator inside the box: blue circle by default, changes to 'v' down arrow on hover or when open
            float orbSize = boxH - 22f;
            Image indicatorImg = CreateSpriteImage("Orb_Or_Chevron", boxGo.transform, spOrbBlue, boxW - orbSize - 14f, 11f, orbSize, orbSize);
            indicatorImg.raycastTarget = false;

            var hoverRelay = boxGo.AddComponent<MM2SelectorHoverRelay>();
            hoverRelay.indicatorImage = indicatorImg;
            hoverRelay.orbSprite = spOrbBlue;
            hoverRelay.downArrowSprite = spIconDropdownV != null ? spIconDropdownV : spChevronDown;

            // Clicking the input box opens/closes the MM2 dropdown picker
            Button boxBtn = boxGo.AddComponent<Button>();
            boxBtn.targetGraphic = boxGo.GetComponent<Image>();
            boxBtn.onClick.AddListener(() =>
            {
                string[] activeOptions = getOptionsDynamic != null ? getOptionsDynamic() : options;
                if (activeOptions != null && activeOptions.Length > 0)
                {
                    float currentBoxX = boxRt != null ? boxRt.anchoredPosition.x : x;
                    float currentBoxY = boxRt != null ? -boxRt.anchoredPosition.y : y;
                    ToggleDropdownPicker(
                        parent,
                        currentBoxX,
                        currentBoxY,
                        boxW,
                        boxH,
                        activeOptions,
                        getCurrentIndex != null ? getCurrentIndex() : 0,
                        selectedIdx =>
                        {
                            if (onSelectIndex != null) onSelectIndex(selectedIdx);
                            valTxt.text = activeOptions[Mathf.Clamp(selectedIdx, 0, activeOptions.Length - 1)];
                        },
                        hoverRelay,
                        valTxt,
                        defaultYellow,
                        activeRedOrange,
                        isOptionEnabled,
                        customMaxRowsPerCol
                    );
                }
                else if (onDown != null)
                {
                    onDown();
                }
            });

            // Stacked Up & Down Chevron Stepper Buttons parented to boxGo so they stay locked to the right of the box on all aspect ratios
            float stepW = 54f;
            float stepH = (boxH - 4f) * 0.5f;
            CreateChevronButton($"{name}_Up", boxGo.transform, spChevronUp, boxW + 6f, 0f, stepW, stepH, () =>
            {
                CloseActiveDropdown();
                if (onUp != null) onUp();
            });
            CreateChevronButton($"{name}_Down", boxGo.transform, spChevronDown, boxW + 6f, stepH + 4f, stepW, stepH, () =>
            {
                CloseActiveDropdown();
                if (onDown != null) onDown();
            });

            return valTxt;
        }

        private void CloseActiveDropdown()
        {
            if (activeDropdownOverlay != null)
            {
                Destroy(activeDropdownOverlay);
                activeDropdownOverlay = null;
            }
            if (onActiveDropdownClosed != null)
            {
                var cb = onActiveDropdownClosed;
                onActiveDropdownClosed = null;
                cb();
            }
        }

        private void ToggleDropdownPicker(
            Transform screenParent,
            float boxX,
            float boxY,
            float boxW,
            float boxH,
            string[] options,
            int currentIndex,
            Action<int> onPick,
            MM2SelectorHoverRelay hoverRelay,
            Text boxValueText,
            Color normalColor,
            Color openColor,
            Func<int, bool> isOptionEnabled = null,
            int customMaxRowsPerCol = 7
        )
        {
            // If this selector's dropdown is already open, close it
            if (activeDropdownOverlay != null && hoverRelay != null && hoverRelay.isDropdownOpen)
            {
                CloseActiveDropdown();
                return;
            }

            CloseActiveDropdown();

            if (hoverRelay != null) hoverRelay.SetDropdownOpen(true);
            if (boxValueText != null) boxValueText.color = openColor;

            onActiveDropdownClosed = () =>
            {
                if (hoverRelay != null) hoverRelay.SetDropdownOpen(false);
                if (boxValueText != null) boxValueText.color = normalColor;
            };

            bool prevSuppress = suppressResponsiveRegistration;
            suppressResponsiveRegistration = true;
            try
            {
                // Full-screen overlay container so clicking anywhere outside closes the picker.
                // Uses an override-sorting sub-Canvas + GraphicRaycaster so the chooser is always rendered on top of all UI elements.
                activeDropdownOverlay = CreateUIObject("MM2_DropdownPickerOverlay", screenParent);
                StretchFull(activeDropdownOverlay.GetComponent<RectTransform>());
                activeDropdownOverlay.transform.SetAsLastSibling();

                Canvas overlayCanvas = activeDropdownOverlay.AddComponent<Canvas>();
                overlayCanvas.overrideSorting = true;
                overlayCanvas.sortingOrder = 250;
                activeDropdownOverlay.AddComponent<GraphicRaycaster>();

                float canvasW = (canvasRootRect != null && canvasRootRect.rect.width >= 100f) ? canvasRootRect.rect.width : 1920f;
                float canvasH = (canvasRootRect != null && canvasRootRect.rect.height >= 100f) ? canvasRootRect.rect.height : 1080f;

                GameObject dismissBg = CreateSolidRect("DismissBlocker", activeDropdownOverlay.transform, 0f, 0f, canvasW, canvasH, new Color(0f, 0f, 0, 0f));
                StretchFull(dismissBg.GetComponent<RectTransform>());
                Image dismissImg = dismissBg.GetComponent<Image>();
                dismissImg.raycastTarget = true;
                Button dismissBtn = dismissBg.AddComponent<Button>();
                dismissBtn.transition = Selectable.Transition.None;
                dismissBtn.onClick.AddListener(CloseActiveDropdown);

                // Layout: single column right under the box for <= maxRowsPerCol options, or multi-column (up to 3 columns) for large lists like Vehicles
                int maxRowsPerCol = customMaxRowsPerCol > 0 ? customMaxRowsPerCol : 7;
                int cols = Mathf.Clamp(Mathf.CeilToInt(options.Length / (float)maxRowsPerCol), 1, 3);
                int rows = Mathf.CeilToInt(options.Length / (float)cols);

                float rowH = (rows > 7) ? 44f : 48f;
                float colW = (cols == 1) ? boxW : 590f;
                float totalW = colW * cols + 12f;
                float totalH = rows * rowH + 12f;

                float rightLimit = Mathf.Min(canvasW - 20f, boxX + boxW + 60f);
                float panelX = (cols == 1) ? boxX : Mathf.Max(20f, rightLimit - totalW);
                float panelY = boxY + boxH + 2f;
                if (panelY + totalH > canvasH - 12f)
                {
                    panelY = Mathf.Max(104f, canvasH - 12f - totalH);
                }

                // Recessed black picker box with modern blue edge matching the UI controls
                GameObject pickerPanel = spBoxBlack9 != null
                    ? CreateSlicedBox("PickerPanel", activeDropdownOverlay.transform, spBoxBlack9, panelX, panelY, totalW, totalH)
                    : CreateSolidRect("PickerPanel", activeDropdownOverlay.transform, panelX, panelY, totalW, totalH, new Color32(0, 0, 0, 248));
                pickerPanel.GetComponent<Image>().raycastTarget = true;

                List<GameObject> highlightFrames = new List<GameObject>();
                Color disabledOptionColor = new Color32(138, 130, 32, 255);

                for (int i = 0; i < options.Length; i++)
                {
                    int colIdx = i / rows;
                    int rowIdx = i % rows;
                    float itemX = 6f + colIdx * colW;
                    float itemY = 6f + rowIdx * rowH;
                    float itemW = colW - 8f;
                    float itemH = rowH - 2f;

                    bool optionEnabled = isOptionEnabled == null || isOptionEnabled(i);

                    GameObject itemGo = CreateSolidRect(
                        $"Option_{i}_{options[i]}",
                        pickerPanel.transform,
                        itemX,
                        itemY,
                        itemW,
                        itemH,
                        new Color(0f, 0f, 0f, 0f)
                    );
                    Image itemHitImg = itemGo.GetComponent<Image>();
                    itemHitImg.raycastTarget = true;

                    // Crisp white rectangular selection frame around the active/hovered option (matching screenshot)
                    GameObject frameGo = CreateUIObject("WhiteSelectionFrame", itemGo.transform);
                    SetTopLeftRect(frameGo.GetComponent<RectTransform>(), 0f, 0f, itemW, itemH);
                    CreateSolidRect("Top", frameGo.transform, 0f, 0f, itemW, 2.5f, Color.white);
                    CreateSolidRect("Bottom", frameGo.transform, 0f, itemH - 2.5f, itemW, 2.5f, Color.white);
                    CreateSolidRect("Left", frameGo.transform, 0f, 0f, 2.5f, itemH, Color.white);
                    CreateSolidRect("Right", frameGo.transform, itemW - 2.5f, 0f, 2.5f, itemH, Color.white);
                    frameGo.SetActive(optionEnabled && i == currentIndex);
                    highlightFrames.Add(frameGo);

                    CreateShadowLabel(
                        "Txt_Option",
                        itemGo.transform,
                        options[i],
                        12f,
                        2f,
                        itemW - 24f,
                        itemH - 4f,
                        28,
                        TextAnchor.MiddleLeft,
                        optionEnabled ? normalColor : disabledOptionColor
                    );

                    if (optionEnabled)
                    {
                        int capturedIdx = i;
                        var rowHover = itemGo.AddComponent<MM2DropdownRowHoverRelay>();
                        rowHover.onPointerEnter = () =>
                        {
                            for (int k = 0; k < highlightFrames.Count; k++)
                            {
                                if (highlightFrames[k] != null) highlightFrames[k].SetActive(k == capturedIdx);
                            }
                        };

                        Button itemBtn = itemGo.AddComponent<Button>();
                        itemBtn.transition = Selectable.Transition.None;
                        itemBtn.onClick.AddListener(() =>
                        {
                            if (onPick != null) onPick(capturedIdx);
                            CloseActiveDropdown();
                        });
                    }
                }
            }
            finally
            {
                suppressResponsiveRegistration = prevSuppress;
            }
        }

        private Button CreateChevronButton(
            string name,
            Transform parent,
            Sprite chevronSprite,
            float x,
            float y,
            float w,
            float h,
            Action onClick
        )
        {
            Image img = CreateSpriteImage(name, parent, chevronSprite, x, y, w, h);
            Button btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.2f, 1.2f, 1.35f, 1f);
            cb.pressedColor = new Color(0.8f, 0.85f, 1f, 1f);
            btn.colors = cb;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
        }

        private RectTransform CreateDensitySliderRow(
            Transform parent,
            string label,
            float y,
            int initialPercent,
            Action<int> onDelta,
            Action<int> onSetPercent = null
        )
        {
            // Group the entire slider row inside a right-column container (baseX = 835 > 830) so it shifts and spreads as a unit
            GameObject rowGo = CreateUIObject($"SliderRow_{label}", parent);
            SetTopLeftRect(rowGo.GetComponent<RectTransform>(), 835f, y, 1067f, 52f);

            // Label on left
            CreateShadowLabel($"Lbl_{label}", rowGo.transform, label, -15f, 0f, 535f, 52f, 30, TextAnchor.MiddleRight, Color.white);

            // Left `<` button at x=1370 (local 535)
            CreateChevronButton($"Btn_{label}_Left", rowGo.transform, spChevronLeft, 535f, 2f, 50f, 48f, () => onDelta(-10));

            // Right `>` button at x=1852 (local 1017)
            CreateChevronButton($"Btn_{label}_Right", rowGo.transform, spChevronRight, 1017f, 2f, 50f, 48f, () => onDelta(10));

            // Clickable & draggable horizontal bar area inside the blue wireframe grid (x: 1438..1837 -> local 603)
            GameObject hitArea = CreateSolidRect(
                $"BarHitArea_{label}",
                rowGo.transform,
                603f,
                2f,
                399f,
                48f,
                new Color(0f, 0f, 0f, 0f)
            );
            hitArea.GetComponent<Image>().raycastTarget = true;

            // Visual Red/Orange horizontal bar centered vertically inside the clickable hit area
            GameObject barTrack = CreateUIObject($"BarTrack_{label}", hitArea.transform);
            SetTopLeftRect(barTrack.GetComponent<RectTransform>(), 0f, 18f, 399f, 12f);

            GameObject barFill = CreateSolidRect(
                "Fill",
                barTrack.transform,
                0f,
                0f,
                399f,
                12f,
                new Color32(235, 50, 15, 255)
            );
            Shadow sh = barFill.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.8f);
            sh.effectDistance = new Vector2(2f, -2f);

            RectTransform fillRt = barFill.GetComponent<RectTransform>();
            UpdateDensityBarFill(fillRt, initialPercent);

            if (onSetPercent != null)
            {
                var clickSlider = hitArea.AddComponent<MM2ClickableBarRegion>();
                clickSlider.trackRect = hitArea.GetComponent<RectTransform>();
                clickSlider.onValueChanged = onSetPercent;
            }

            return fillRt;
        }

        private RectTransform CreatePerformanceScoreRow(Transform parent, string label, float y, float normalizedScore)
        {
            // Group label, baseline, and red bar inside a left-column row container (baseX = 120) so they move together
            GameObject rowGo = CreateUIObject($"PerfRow_{label}", parent);
            SetTopLeftRect(rowGo.GetComponent<RectTransform>(), 120f, y, 745f, 44f);

            CreateShadowLabel($"Lbl_{label}", rowGo.transform, label, 0f, 0f, 305f, 44f, 28, TextAnchor.MiddleRight, Color.white);

            // Subtle red baseline + thick red score bar inside the grid (x: 453..862 -> local 333)
            float maxW = 408f;
            CreateSolidRect($"BaseLine_{label}", rowGo.transform, 333f, 34f, maxW, 2f, new Color32(160, 35, 15, 190));
            GameObject scoreGo = CreateSolidRect(
                $"ScoreBar_{label}",
                rowGo.transform,
                333f,
                12f,
                Mathf.Clamp01(normalizedScore) * maxW,
                22f,
                new Color32(230, 42, 12, 255)
            );
            return scoreGo.GetComponent<RectTransform>();
        }
    }

    /// <summary>
    /// Swaps the right-side blue circle (orb) on a dropdown input box to a down-arrow ('v') when hovered or open.
    /// </summary>
    public class MM2SelectorHoverRelay : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Image indicatorImage;
        public Sprite orbSprite;
        public Sprite downArrowSprite;
        public bool isHovered;
        public bool isDropdownOpen;

        public void OnPointerEnter(PointerEventData eventData)
        {
            isHovered = true;
            RefreshIcon();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            isHovered = false;
            RefreshIcon();
        }

        public void SetDropdownOpen(bool open)
        {
            isDropdownOpen = open;
            RefreshIcon();
        }

        private void RefreshIcon()
        {
            if (indicatorImage == null) return;
            indicatorImage.sprite = (isHovered || isDropdownOpen) ? downArrowSprite : orbSprite;
        }
    }

    /// <summary>
    /// Moves the white rectangular highlight frame across dropdown picker options on mouse hover.
    /// </summary>
    public class MM2DropdownRowHoverRelay : MonoBehaviour, IPointerEnterHandler
    {
        public Action onPointerEnter;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (onPointerEnter != null) onPointerEnter();
        }
    }

    /// <summary>
    /// Allows clicking or dragging anywhere along a density bar in the wireframe grid to set 0..100%.
    /// </summary>
    public class MM2ClickableBarRegion : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        public RectTransform trackRect;
        public Action<int> onValueChanged;

        public void OnPointerDown(PointerEventData eventData)
        {
            UpdateFromPointer(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            UpdateFromPointer(eventData);
        }

        private void UpdateFromPointer(PointerEventData eventData)
        {
            if (trackRect == null || onValueChanged == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    trackRect,
                    eventData.position,
                    eventData.pressEventCamera,
                    out Vector2 localPt))
            {
                float width = Mathf.Max(1f, trackRect.rect.width);
                // Because pivot is (0, 1) (top-left), localPt.x runs from 0 to width
                float normalized = Mathf.Clamp01(localPt.x / width);
                int pct = Mathf.RoundToInt(normalized * 100f);
                onValueChanged(pct);
            }
        }
    }

    /// <summary>
    /// Highlights plain text action buttons (such as "Restart Race", "Next Race", "Race Menu", "Exit to Windows" on Event Results) on hover.
    /// </summary>
    public class MM2TextButtonHoverRelay : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Text labelText;
        public Color normalColor = Color.white;
        public Color hoverColor = new Color32(255, 240, 25, 255);

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (labelText != null) labelText.color = hoverColor;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (labelText != null) labelText.color = normalColor;
        }
    }

    /// <summary>
    /// Allows pressing ESC while driving on the map to return to the Midtown Madness 2 Menus.
    /// </summary>
    public class MM2InGameMenuReturn : MonoBehaviour
    {
        private void Update()
        {
            bool escPressed = false;
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) escPressed = true;
#else
            if (Input.GetKeyDown(KeyCode.Escape)) escPressed = true;
#endif
            if (escPressed && FindFirstObjectByType<MM2MenuController>() == null)
            {
                GameObject menuGo = new GameObject("MM2_MenuSystem");
                var ctrl = menuGo.AddComponent<MM2MenuController>();
                ctrl.ShowScreen(MM2GameSession.IsCrashCourseSession
                    ? MM2MenuController.MenuScreen.CrashCourseHub
                    : MM2MenuController.MenuScreen.MainMenu);
            }
        }
    }
}
