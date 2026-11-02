using System;
using System.Collections.Generic;
using MM2.AI;
using MM2.Archive;
using MM2.Vehicle;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MM2.UI
{
    /// <summary>
    /// In-game Midtown Madness 2 HUD (bottom-left tachometer, damage meter, gear indicator, and digital speedometer)
    /// and Minimap (bottom-right scrolling map from geometry/hudmap_*.pkg with rotating player indicator).
    /// </summary>
    public class MM2GameplayHud : MonoBehaviour
    {
        private static MM2GameplayHud instance;

        [SerializeField] private Canvas hudCanvas;
        [SerializeField] private RawImage damageBarRawImage;
        [SerializeField] private RawImage damageLabelRawImage;
        [SerializeField] private RawImage revCounterRawImage;
        [SerializeField] private RectTransform revCounterRect;
        [SerializeField] private RawImage gearRawImage;
        [SerializeField] private RawImage digitHundredsRawImage;
        [SerializeField] private RawImage digitTensRawImage;
        [SerializeField] private RawImage digitOnesRawImage;

        [SerializeField] private RawImage minimapRawImage;
        [SerializeField] private RectTransform playerArrowRect;
        [SerializeField] private RectTransform bottomLeftDashRect;
        [SerializeField] private Text txtFpsCounter;

        // Race HUD elements (top-left counters, top-centre timer, 3D green arrow, and yellow countdown text)
        [SerializeField] private GameObject raceHudContainer;
        [SerializeField] private Text txtLapCounter;
        [SerializeField] private Text txtCheckCounter;
        [SerializeField] private Text txtPositionCounter;
        [SerializeField] private Text txtRaceTimer;
        [SerializeField] private Text txtCenterStatus;

        private readonly List<RectTransform> checkpointMapIcons = new List<RectTransform>();
        private readonly List<Vector3> minimapCheckpointPositions = new List<Vector3>(32);
        private readonly List<Image> opponentMapIcons = new List<Image>();
        private readonly List<Image> policeMapIcons = new List<Image>();
        private Sprite cachedTriangleSprite;
        private Sprite cachedDiamondSprite;

        // Seconds ahead along current vehicle velocity used to aim the top-center checkpoint arrow
        private float hudArrowLeadTimeSeconds = 1.0f;
        public float HudArrowLeadTimeSeconds
        {
            get => hudArrowLeadTimeSeconds;
            set => hudArrowLeadTimeSeconds = Mathf.Max(0f, value);
        }

        [SerializeField] private RawImage hudArrowRawImage;
        [SerializeField] private GameObject hudArrowWorldRoot;
        [SerializeField] private Camera hudArrowCamera;
        [SerializeField] private RenderTexture hudArrowRenderTexture;
        [SerializeField] private Transform hudArrowMeshPivot;
        [SerializeField] private Font hudFontBold;

        [SerializeField] private Texture2D texDamage;
        [SerializeField] private Texture2D texDamageLabel;
        [SerializeField] private Texture2D texSpeedTicks;
        [SerializeField] private Texture2D[] texDigits = new Texture2D[10];
        private readonly Dictionary<string, Texture2D> texGears =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        [SerializeField] private GameObject minimapWorldRoot;
        [SerializeField] private Camera minimapCamera;
        [SerializeField] private RenderTexture minimapRenderTexture;
        [SerializeField] private bool hasHudMapPkgGeometry;
        [SerializeField] private Bounds minimapBounds;
        [SerializeField] private bool hasMinimapBounds;
        [NonSerialized] private string loadedMinimapCity = "";

        private float displayedRpm01 = 0.12f;
        private float currentDamage01 = 0f; // Kept at 0% until damage simulation is added
        private int manualGear = 1; // -1 = R, 0 = N, 1..5 = forward gears
        private int lastAutoGear = 1;
        private int zoomPresetIndex = 0;
        private float smoothedUnscaledDt = 1f / 60f;
        private float fpsDisplayUpdateTimer = 0f;
        private static readonly float[] ZoomMultipliers = { 1.0f, 1.75f, 3.0f };

        private const float RevBarMaxWidth = 258f;
        private const float RevBarHeight = 86f;
        private const float MinimapWorldY = -4000f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInitializeOnLoad()
        {
            EnsureExists();
        }

        public static MM2GameplayHud EnsureExists()
        {
            if (instance != null)
            {
                instance.CheckCityChanged();
                return instance;
            }

            MM2GameplayHud existing = FindFirstObjectByType<MM2GameplayHud>();
            if (existing != null)
            {
                instance = existing;
                instance.CheckCityChanged();
                return instance;
            }

            GameObject go = new GameObject("MM2_GameplayHUD");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MM2GameplayHud>();
            return instance;
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;

            MM2ArchiveSystem.EnsureInitialized();
            LoadHudTextures();
            BuildHudCanvas();
            BuildMinimapCameraAndRenderTexture();
            BuildHudArrowCameraAndMesh();
            RebuildMinimapForCurrentCity();
        }

        private void OnDestroy()
        {
            if (minimapRenderTexture != null)
            {
                minimapRenderTexture.Release();
                Destroy(minimapRenderTexture);
            }
            if (minimapWorldRoot != null)
            {
                Destroy(minimapWorldRoot);
            }
            if (hudArrowRenderTexture != null)
            {
                hudArrowRenderTexture.Release();
                Destroy(hudArrowRenderTexture);
            }
            if (hudArrowWorldRoot != null)
            {
                Destroy(hudArrowWorldRoot);
            }
            if (instance == this)
            {
                instance = null;
            }
        }

        private void LoadHudTextures()
        {
            hudFontBold = Resources.Load<Font>("Fonts/OpenSans-Bold");
            if (hudFontBold == null)
            {
                hudFontBold = Font.CreateDynamicFontFromOSFont(
                    new[] { "Open Sans Bold", "DejaVu Sans", "Arial" },
                    64
                );
            }
            if (hudFontBold == null)
            {
                hudFontBold = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            texDamage = MM2RuntimeDecoders.LoadUiTexture("DAMAGE");
            texDamageLabel = MM2RuntimeDecoders.LoadUiTexture("DAMAGE_LABLE");
            texSpeedTicks = MM2RuntimeDecoders.LoadUiTexture("SPEED_TICKS");

            for (int i = 0; i <= 9; i++)
            {
                texDigits[i] = MM2RuntimeDecoders.LoadUiTexture($"DIGITAC_{i}");
            }

            string[] gearSuffixes = { "D", "R", "N", "P", "1", "2", "3", "4", "5", "6" };
            foreach (string g in gearSuffixes)
            {
                Texture2D gTex = MM2RuntimeDecoders.LoadUiTexture($"DIGITAC_GEAR_{g}");
                if (gTex != null)
                {
                    texGears[g] = gTex;
                }
            }
        }

        private void BuildHudCanvas()
        {
            GameObject canvasGo = new GameObject("HUD_Canvas");
            canvasGo.transform.SetParent(transform, false);

            hudCanvas = canvasGo.AddComponent<Canvas>();
            hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            hudCanvas.sortingOrder = 50; // Below MM2MenuController (which is sortingOrder 100+)

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            // --- BOTTOM-LEFT HUD CONTAINER ---
            GameObject bottomLeftRoot = new GameObject("BottomLeft_DashHUD");
            bottomLeftRoot.transform.SetParent(canvasGo.transform, false);
            RectTransform blRect = bottomLeftRoot.AddComponent<RectTransform>();
            blRect.anchorMin = new Vector2(0f, 0f);
            blRect.anchorMax = new Vector2(0f, 0f);
            blRect.pivot = new Vector2(0f, 0f);
            blRect.anchoredPosition = new Vector2(20f, 16f);
            blRect.sizeDelta = new Vector2(300f, 260f);
            bottomLeftDashRect = blRect;
            UpdateDashResolutionScale();

            // 1. Damage Meter (bottom bar: 129x12 native -> 258x24 at 2x scale)
            damageBarRawImage = CreateBottomLeftRawImage(
                "DamageMeter_Fill",
                blRect,
                texDamage,
                new Vector2(0f, 0f),
                new Vector2(RevBarMaxWidth, 24f)
            );
            UpdateDamageMeterUv(0f);

            damageLabelRawImage = CreateBottomLeftRawImage(
                "DamageMeter_Label",
                blRect,
                texDamageLabel,
                new Vector2(0f, 0f),
                new Vector2(RevBarMaxWidth, 24f)
            );

            // 2. Rev Counter / Tachometer (SPEED_TICKS: 129x43 native -> 258x86 at 2x scale, immediately above damage bar)
            revCounterRawImage = CreateBottomLeftRawImage(
                "RevCounter_Ticks",
                blRect,
                texSpeedTicks,
                new Vector2(0f, 26f),
                new Vector2(RevBarMaxWidth, RevBarHeight)
            );
            revCounterRect = revCounterRawImage.rectTransform;

            // 3. Gear Indicator (DIGITAC_GEAR_*: 20x23 native -> 40x46 at 2x scale, tucked above shortest left rev bars)
            texGears.TryGetValue("D", out Texture2D defaultGearTex);
            gearRawImage = CreateBottomLeftRawImage(
                "Gear_Indicator",
                blRect,
                defaultGearTex,
                new Vector2(16f, 70f),
                new Vector2(40f, 46f)
            );

            // 4. Speedometer Digits (DIGITAC_0..9: 41x56 native -> 82x112 at 2x scale, right-aligned above rev counter)
            digitHundredsRawImage = CreateBottomLeftRawImage(
                "Digit_Hundreds",
                blRect,
                texDigits[1],
                new Vector2(14f, 114f),
                new Vector2(82f, 112f)
            );
            digitTensRawImage = CreateBottomLeftRawImage(
                "Digit_Tens",
                blRect,
                texDigits[0],
                new Vector2(95f, 114f),
                new Vector2(82f, 112f)
            );
            digitOnesRawImage = CreateBottomLeftRawImage(
                "Digit_Ones",
                blRect,
                texDigits[0],
                new Vector2(176f, 114f),
                new Vector2(82f, 112f)
            );

            digitHundredsRawImage.gameObject.SetActive(false);
            digitTensRawImage.gameObject.SetActive(false);

            // --- BOTTOM-RIGHT MINIMAP CONTAINER ---
            // Matches tune/sf.mmHudMap & london.mmHudMap (Size 0.21 x 0.25 of screen in bottom-right corner)
            GameObject minimapContainer = new GameObject("BottomRight_Minimap");
            minimapContainer.transform.SetParent(canvasGo.transform, false);
            RectTransform mapRect = minimapContainer.AddComponent<RectTransform>();
            mapRect.anchorMin = new Vector2(1f, 0f);
            mapRect.anchorMax = new Vector2(1f, 0f);
            mapRect.pivot = new Vector2(1f, 0f);
            mapRect.anchoredPosition = new Vector2(-24f, 20f);
            mapRect.sizeDelta = new Vector2(404f, 272f);

            // Subtle dark border behind minimap for crisp framing
            Image borderImg = minimapContainer.AddComponent<Image>();
            borderImg.color = new Color(0.05f, 0.08f, 0.12f, 0.85f);
            borderImg.raycastTarget = false;

            GameObject mapViewGo = new GameObject("Minimap_RenderView");
            mapViewGo.transform.SetParent(minimapContainer.transform, false);
            RectTransform viewRect = mapViewGo.AddComponent<RectTransform>();
            viewRect.anchorMin = Vector2.zero;
            viewRect.anchorMax = Vector2.one;
            viewRect.offsetMin = new Vector2(2f, 2f);
            viewRect.offsetMax = new Vector2(-2f, -2f);

            minimapRawImage = mapViewGo.AddComponent<RawImage>();
            minimapRawImage.raycastTarget = false;

            RebuildMinimapIcons(mapViewGo.transform);

            // --- TOP RACE HUD CONTAINER (Counters, Timer, 3D Green Arrow, Yellow Countdown Text) ---
            raceHudContainer = new GameObject("Top_RaceHUD");
            raceHudContainer.transform.SetParent(canvasGo.transform, false);
            RectTransform raceRect = raceHudContainer.AddComponent<RectTransform>();
            raceRect.anchorMin = Vector2.zero;
            raceRect.anchorMax = Vector2.one;
            raceRect.offsetMin = Vector2.zero;
            raceRect.offsetMax = Vector2.zero;

            Color32 neonGreen = new Color32(45, 255, 45, 255);
            Color32 countdownYellow = new Color32(255, 242, 20, 255);

            // Top-Left Checkpoint & Lap Counters (matching Screenshot 2)
            txtCheckCounter = CreateHudText(
                "Txt_CheckCounter",
                raceRect,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(12f, -14f),
                new Vector2(420f, 54f),
                36,
                TextAnchor.UpperLeft,
                neonGreen,
                "Check:  0/4"
            );

            txtLapCounter = CreateHudText(
                "Txt_LapCounter",
                raceRect,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(12f, -58f),
                new Vector2(420f, 54f),
                36,
                TextAnchor.UpperLeft,
                neonGreen,
                "Lap:  1/3"
            );
            txtLapCounter.gameObject.SetActive(false);

            txtPositionCounter = CreateHudText(
                "Txt_PosCounter",
                raceRect,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(12f, -102f),
                new Vector2(420f, 54f),
                36,
                TextAnchor.UpperLeft,
                neonGreen,
                "Pos:  1/8"
            );
            txtPositionCounter.gameObject.SetActive(false);

            // Top-Centre Race Timer (MM:SS:CC in large neon green digits at the very top center)
            txtRaceTimer = CreateHudText(
                "Txt_RaceTimer",
                raceRect,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(0f, -2f),
                new Vector2(560f, 92f),
                66,
                TextAnchor.UpperCenter,
                neonGreen,
                "00:25:00"
            );

            // Top-Centre 3D Green Arrow RawImage (rendered from geometry/hudarrow01.pkg right below timer)
            GameObject hudArrowGo = new GameObject("TopCenter_3DGreenArrow");
            hudArrowGo.transform.SetParent(raceRect, false);
            RectTransform hudArrowRect = hudArrowGo.AddComponent<RectTransform>();
            hudArrowRect.anchorMin = new Vector2(0.5f, 1f);
            hudArrowRect.anchorMax = new Vector2(0.5f, 1f);
            hudArrowRect.pivot = new Vector2(0.5f, 1f);
            hudArrowRect.anchoredPosition = new Vector2(0f, -72f);
            hudArrowRect.sizeDelta = new Vector2(240f, 150f);

            hudArrowRawImage = hudArrowGo.AddComponent<RawImage>();
            hudArrowRawImage.raycastTarget = false;

            // Top-Centre Yellow Countdown / Status Text ("Ready...", "Set...", "Go") right below the 3D arrow
            txtCenterStatus = CreateHudText(
                "Txt_CenterStatus",
                raceRect,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(0f, -196f),
                new Vector2(680f, 96f),
                62,
                TextAnchor.UpperCenter,
                countdownYellow,
                ""
            );

            raceHudContainer.SetActive(false);

            // Top-Right Optional FPS Counter (toggled in Options -> Show FPS Counter)
            RectTransform canvasRect = canvasGo.GetComponent<RectTransform>();
            txtFpsCounter = CreateHudText(
                "Txt_FpsCounter",
                canvasRect,
                new Vector2(1f, 1f),
                new Vector2(1f, 1f),
                new Vector2(-18f, -12f),
                new Vector2(240f, 44f),
                28,
                TextAnchor.UpperRight,
                neonGreen,
                "60 FPS"
            );
            MM2GameSession.EnsureSettingsLoaded();
            txtFpsCounter.gameObject.SetActive(MM2GameSession.ShowFpsCounter);
        }

        /// <summary>
        /// Scales the bottom-left dash HUD so that 720p (1280x720) is the baseline (1.0x),
        /// and doubling the screen resolution (e.g. 1080p -> 4K) increases the physical width/height
        /// by 50% (1.5x) rather than doubling (2.0x), while leaving the minimap scaling unchanged.
        /// </summary>
        private void UpdateDashResolutionScale()
        {
            if (bottomLeftDashRect == null) return;

            float resRatioFrom720p = Mathf.Max(1.0f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
            // CanvasScaler scales by resRatioFrom720p relative to 720p.
            // Applying localScale = resRatioFrom720p^(log2(1.5) - 1) yields physical size = resRatioFrom720p^log2(1.5),
            // so any 2x resolution jump (such as 1080p -> 4K) increases physical dash size by exactly 1.5x (+50%).
            const float HalfSlopeExp = -0.4150375f; // Mathf.Log(1.5f, 2f) - 1f
            float dashLocalScale = Mathf.Pow(resRatioFrom720p, HalfSlopeExp);

            bottomLeftDashRect.localScale = new Vector3(dashLocalScale, dashLocalScale, 1f);
            bottomLeftDashRect.anchoredPosition = new Vector2(20f * dashLocalScale, 16f * dashLocalScale);
        }

        private Text CreateHudText(
            string name,
            RectTransform parent,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 anchoredPos,
            Vector2 size,
            int fontSize,
            TextAnchor alignment,
            Color color,
            string initialText
        )
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one;

            Text txt = go.AddComponent<Text>();
            txt.font = hudFontBold;
            txt.fontStyle = FontStyle.Normal;
            txt.fontSize = fontSize;
            txt.alignment = alignment;
            txt.color = color;
            txt.text = initialText;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;

            MM2SmoothTextEffect fx = go.AddComponent<MM2SmoothTextEffect>();
            fx.IsHeaderStyle = false;
            fx.OutlineRadius = 1.4f;
            fx.OutlineColor = new Color32(0, 0, 0, 110);
            fx.ShadowOffset = new Vector2(2.2f, -2.2f);
            fx.ShadowColor = new Color32(0, 0, 0, 185);

            return txt;
        }

        private static RawImage CreateBottomLeftRawImage(
            string name,
            RectTransform parent,
            Texture2D tex,
            Vector2 anchoredPos,
            Vector2 size
        )
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;

            RawImage raw = go.AddComponent<RawImage>();
            raw.texture = tex;
            raw.color = Color.white;
            raw.raycastTarget = false;
            return raw;
        }

        private void BuildMinimapCameraAndRenderTexture()
        {
            minimapRenderTexture = new RenderTexture(512, 384, 16, RenderTextureFormat.ARGB32)
            {
                name = "MM2_MinimapRT",
                antiAliasing = 2,
                filterMode = FilterMode.Bilinear
            };
            minimapRenderTexture.Create();

            if (minimapRawImage != null)
            {
                minimapRawImage.texture = minimapRenderTexture;
            }

            minimapWorldRoot = new GameObject("MM2_MinimapWorld");
            minimapWorldRoot.transform.SetParent(transform, false);

            GameObject camGo = new GameObject("Minimap_OrthoCamera");
            camGo.transform.SetParent(minimapWorldRoot.transform, false);
            camGo.transform.position = new Vector3(0f, MinimapWorldY + 60f, 0f);
            camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            minimapCamera = camGo.AddComponent<Camera>();
            minimapCamera.orthographic = true;
            minimapCamera.orthographicSize = 235f;
            minimapCamera.nearClipPlane = 5f;
            minimapCamera.farClipPlane = 500f;
            minimapCamera.clearFlags = CameraClearFlags.SolidColor;
            // Authentic MM2 hudmap water background color from tune/sf.mmHudMap & london.mmHudMap
            minimapCamera.backgroundColor = new Color(0.084f, 0.70f, 0.94f, 1f);
            minimapCamera.targetTexture = minimapRenderTexture;
            minimapCamera.allowHDR = false;
            minimapCamera.useOcclusionCulling = false;
            minimapCamera.depth = -20f;
        }

        private void BuildHudArrowCameraAndMesh()
        {
            hudArrowRenderTexture = new RenderTexture(256, 160, 16, RenderTextureFormat.ARGB32)
            {
                name = "MM2_HudArrowRT",
                antiAliasing = 4,
                filterMode = FilterMode.Bilinear
            };
            hudArrowRenderTexture.Create();

            if (hudArrowRawImage != null)
            {
                hudArrowRawImage.texture = hudArrowRenderTexture;
            }

            hudArrowWorldRoot = new GameObject("MM2_HudArrowWorld");
            hudArrowWorldRoot.transform.SetParent(transform, false);
            hudArrowWorldRoot.transform.position = new Vector3(0f, -6500f, 0f);

            GameObject camGo = new GameObject("HudArrow_PerspectiveCamera");
            camGo.transform.SetParent(hudArrowWorldRoot.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0f, -2.55f);
            camGo.transform.localRotation = Quaternion.identity;

            hudArrowCamera = camGo.AddComponent<Camera>();
            hudArrowCamera.orthographic = false;
            hudArrowCamera.fieldOfView = 38f;
            hudArrowCamera.nearClipPlane = 0.2f;
            hudArrowCamera.farClipPlane = 20f;
            hudArrowCamera.clearFlags = CameraClearFlags.SolidColor;
            hudArrowCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            hudArrowCamera.targetTexture = hudArrowRenderTexture;
            hudArrowCamera.allowHDR = false;
            hudArrowCamera.useOcclusionCulling = false;
            hudArrowCamera.depth = -25f;

            // Tilted plane pivot so yaw=0 (straight ahead) foreshortens the arrowhead downward/forward
            // while yaw=+90/-90 points right/left on screen, matching MM2's top-center 3D HUD arrow.
            GameObject tiltFrameGo = new GameObject("HudArrow_TiltFrame");
            tiltFrameGo.transform.SetParent(hudArrowWorldRoot.transform, false);
            tiltFrameGo.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            tiltFrameGo.transform.localRotation = Quaternion.Euler(28f, 0f, 0f);

            GameObject pivotGo = new GameObject("HudArrow_YawPivot");
            pivotGo.transform.SetParent(tiltFrameGo.transform, false);
            pivotGo.transform.localPosition = Vector3.zero;
            pivotGo.transform.localRotation = Quaternion.identity;
            hudArrowMeshPivot = pivotGo.transform;

            Mesh arrowMesh = BuildExtrudedHudArrowMesh();
            GameObject meshGo = new GameObject("HudArrow_Mesh");
            meshGo.transform.SetParent(hudArrowMeshPivot, false);
            meshGo.transform.localPosition = Vector3.zero;
            meshGo.transform.localRotation = Quaternion.identity;

            MeshFilter mf = meshGo.AddComponent<MeshFilter>();
            mf.sharedMesh = arrowMesh;

            Shader unlitColorShader =
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Unlit/Color") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Standard");

            Texture2D greenArrowTex = MM2RuntimeDecoders.LoadUiTexture("s_hudarrow_green");

            Material faceMat = new Material(unlitColorShader)
            {
                name = "HudArrow_FaceMat",
                color = new Color32(35, 255, 35, 255)
            };
            if (greenArrowTex != null)
            {
                faceMat.mainTexture = greenArrowTex;
                if (faceMat.HasProperty("_BaseMap")) faceMat.SetTexture("_BaseMap", greenArrowTex);
            }
            if (faceMat.HasProperty("_BaseColor"))
            {
                faceMat.SetColor("_BaseColor", new Color32(35, 255, 35, 255));
            }
            if (faceMat.HasProperty("_Cull"))
            {
                faceMat.SetInt("_Cull", 2);
            }

            Material sideMat = new Material(unlitColorShader)
            {
                name = "HudArrow_SideBevelMat",
                color = new Color32(15, 165, 20, 255)
            };
            if (sideMat.HasProperty("_BaseColor"))
            {
                sideMat.SetColor("_BaseColor", new Color32(15, 165, 20, 255));
            }
            if (sideMat.HasProperty("_Cull"))
            {
                sideMat.SetInt("_Cull", 2);
            }

            MeshRenderer mr = meshGo.AddComponent<MeshRenderer>();
            mr.sharedMaterials = arrowMesh.subMeshCount > 1 ? new[] { faceMat, sideMat } : new[] { faceMat };
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private static Mesh BuildExtrudedHudArrowMesh()
        {
            // Load geometry/hudarrow01.pkg from the .ar archives
            MM2RuntimePkgLoader.ParsedPkg pkg = MM2RuntimePkgLoader.LoadPkg("hudarrow01");
            List<Vector3> baseVerts = null;
            List<Vector2> baseUVs = null;
            List<int> baseTris = null;

            if (pkg != null && pkg.GeometryChunks.Count > 0)
            {
                foreach (var kv in pkg.GeometryChunks)
                {
                    if (kv.Value != null && kv.Value.Count > 0 && kv.Value[0].Positions.Count >= 3)
                    {
                        var sec = kv.Value[0];
                        baseVerts = sec.Positions;
                        baseUVs = sec.UVs;
                        baseTris = sec.Triangles;
                        break;
                    }
                }
            }

            // Fallback contour matching hudarrow01.pkg if missing
            if (baseVerts == null || baseTris == null || baseVerts.Count < 3)
            {
                baseVerts = new List<Vector3>
                {
                    new Vector3(-0.225f, -0.110f, -0.292f),
                    new Vector3( 0.229f, -0.110f, -0.292f),
                    new Vector3( 0.229f, -0.110f,  0.741f),
                    new Vector3(-0.581f, -0.110f, -0.290f),
                    new Vector3( 0.002f, -0.110f, -1.177f),
                    new Vector3( 0.585f, -0.110f, -0.290f),
                    new Vector3(-0.225f, -0.110f,  0.741f)
                };
                baseUVs = new List<Vector2>
                {
                    new Vector2(0.39f, 0.36f),
                    new Vector2(0.61f, 0.36f),
                    new Vector2(0.61f, 0.18f),
                    new Vector2(0.24f, 0.36f),
                    new Vector2(0.50f, 1.00f),
                    new Vector2(0.76f, 0.36f),
                    new Vector2(0.39f, 0.18f)
                };
                baseTris = new List<int>
                {
                    0, 1, 2,
                    0, 3, 4,
                    1, 0, 4,
                    1, 4, 5,
                    2, 6, 0
                };
            }

            const float halfThickness = 0.09f;
            int n = baseVerts.Count;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var faceTris = new List<int>();
            var sideTris = new List<int>();

            // Top face (+Y) and Bottom face (-Y)
            // Map AGE (x, y, z) -> (x, ±halfThickness, -z) so tip (z_age = -1.177) points along +Z
            for (int i = 0; i < n; i++)
            {
                Vector3 p = baseVerts[i];
                positions.Add(new Vector3(p.x, halfThickness, -p.z));
                normals.Add(Vector3.up);
                uvs.Add(i < baseUVs.Count ? baseUVs[i] : Vector2.zero);
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 p = baseVerts[i];
                positions.Add(new Vector3(p.x, -halfThickness, -p.z));
                normals.Add(Vector3.down);
                uvs.Add(i < baseUVs.Count ? baseUVs[i] : Vector2.zero);
            }

            // Orient top-face triangles so their normal points strictly along +Y (Vector3.up),
            // and bottom-face triangles so their normal points strictly along -Y (Vector3.down).
            var directedEdges = new List<(int a, int b)>();
            var directedEdgeSet = new HashSet<(int a, int b)>();

            for (int i = 0; i + 2 < baseTris.Count; i += 3)
            {
                int i0 = baseTris[i];
                int i1 = baseTris[i + 1];
                int i2 = baseTris[i + 2];

                Vector3 cross = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
                int t0 = i0;
                int t1 = cross.y >= 0f ? i1 : i2;
                int t2 = cross.y >= 0f ? i2 : i1;

                // Top face (+Y outward normal)
                faceTris.Add(t0);
                faceTris.Add(t1);
                faceTris.Add(t2);

                // Bottom face (-Y outward normal)
                faceTris.Add(n + t0);
                faceTris.Add(n + t2);
                faceTris.Add(n + t1);

                directedEdges.Add((t0, t1));
                directedEdges.Add((t1, t2));
                directedEdges.Add((t2, t0));
                directedEdgeSet.Add((t0, t1));
                directedEdgeSet.Add((t1, t2));
                directedEdgeSet.Add((t2, t0));
            }

            // Extrude only true outer boundary edges (directed edges (a, b) whose reverse (b, a) is not in the mesh)
            foreach (var edge in directedEdges)
            {
                if (directedEdgeSet.Contains((edge.b, edge.a)))
                {
                    continue; // Shared interior edge
                }

                int a = edge.a;
                int b = edge.b;

                Vector3 paTop = positions[a];
                Vector3 pbTop = positions[b];
                Vector3 paBot = positions[n + a];
                Vector3 pbBot = positions[n + b];

                Vector3 sideNorm = Vector3.Cross(pbTop - paTop, paBot - paTop).normalized;

                int vStart = positions.Count;
                positions.Add(paTop);
                positions.Add(pbTop);
                positions.Add(pbBot);
                positions.Add(paBot);

                normals.Add(sideNorm);
                normals.Add(sideNorm);
                normals.Add(sideNorm);
                normals.Add(sideNorm);

                uvs.Add(new Vector2(0f, 1f));
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(0f, 0f));

                sideTris.Add(vStart + 0);
                sideTris.Add(vStart + 1);
                sideTris.Add(vStart + 2);
                sideTris.Add(vStart + 0);
                sideTris.Add(vStart + 2);
                sideTris.Add(vStart + 3);
            }

            Mesh mesh = new Mesh { name = "MM2_HudArrow3D" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (sideTris.Count > 0)
            {
                mesh.subMeshCount = 2;
                mesh.SetTriangles(faceTris, 0);
                mesh.SetTriangles(sideTris, 1);
            }
            else
            {
                mesh.subMeshCount = 1;
                mesh.SetTriangles(faceTris, 0);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        public void CheckCityChanged()
        {
            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.Location);
            string mapName = spec != null && !string.IsNullOrEmpty(spec.MapName) ? spec.MapName : "sf";
            if (!string.Equals(loadedMinimapCity, mapName, StringComparison.OrdinalIgnoreCase))
            {
                RebuildMinimapForCurrentCity();
            }
        }

        private void RebuildMinimapForCurrentCity()
        {
            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(MM2GameSession.Location);
            string mapName = spec != null && !string.IsNullOrEmpty(spec.MapName) ? spec.MapName : "sf";
            loadedMinimapCity = mapName;

            if (minimapWorldRoot == null) return;

            // Remove old map mesh children (keeping Minimap_OrthoCamera)
            for (int i = minimapWorldRoot.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = minimapWorldRoot.transform.GetChild(i);
                if (minimapCamera != null && child == minimapCamera.transform) continue;
                Destroy(child.gameObject);
            }

            hasMinimapBounds = false;
            hasHudMapPkgGeometry = TryBuildHudMapMesh(mapName, minimapWorldRoot.transform);
            if (!hasHudMapPkgGeometry)
            {
                hasMinimapBounds = TryComputeFallbackCityBounds(out minimapBounds);
            }
        }

        private static bool TryComputeFallbackCityBounds(out Bounds bounds)
        {
            bounds = default;
            bool hasAny = false;
            foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                if (mr == null || !mr.gameObject.activeInHierarchy) continue;
                Transform root = mr.transform.root;
                if (root != null && root.name.StartsWith("MM2_City", StringComparison.OrdinalIgnoreCase))
                {
                    if (!hasAny)
                    {
                        bounds = mr.bounds;
                        hasAny = true;
                    }
                    else
                    {
                        bounds.Encapsulate(mr.bounds);
                    }
                }
            }
            return hasAny && bounds.size.x > 40f && bounds.size.z > 40f;
        }

        private bool TryBuildHudMapMesh(string mapName, Transform parent)
        {
            string pkgName = $"hudmap_{mapName}";
            MM2RuntimePkgLoader.ParsedPkg pkg = MM2RuntimePkgLoader.LoadPkg(pkgName);
            if (pkg == null || pkg.GeometryChunks.Count == 0)
            {
                return false;
            }

            List<MM2RuntimePkgLoader.PkgSection> sections = null;
            if (!pkg.GeometryChunks.TryGetValue("H", out sections) || sections == null || sections.Count == 0)
            {
                foreach (var kv in pkg.GeometryChunks)
                {
                    if (kv.Value != null && kv.Value.Count > 0)
                    {
                        sections = kv.Value;
                        break;
                    }
                }
            }

            if (sections == null || sections.Count == 0)
            {
                return false;
            }

            Shader unlitShader =
                Shader.Find("Unlit/Texture") ??
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Standard");

            GameObject meshGo = new GameObject($"HudMapMesh_{mapName}");
            meshGo.transform.SetParent(parent, false);
            meshGo.transform.position = new Vector3(0f, MinimapWorldY, 0f);
            meshGo.transform.rotation = Quaternion.identity;

            var allPositions = new List<Vector3>();
            var allNormals = new List<Vector3>();
            var allUVs = new List<Vector2>();
            var submeshes = new List<List<int>>();
            var materials = new List<Material>();

            foreach (MM2RuntimePkgLoader.PkgSection sec in sections)
            {
                if (sec.Positions.Count == 0 || sec.Triangles.Count == 0) continue;

                // Custom cities like Manhattan include night-mode map tiles at vy = -10 with _ni shaders.
                // Skip lower/night layers so daytime map tiles render cleanly without z-fighting.
                float avgY = 0f;
                for (int i = 0; i < sec.Positions.Count; i++) avgY += sec.Positions[i].y;
                avgY /= sec.Positions.Count;
                if (avgY < -1.0f) continue;

                string texName = null;
                if (sec.ShaderOffset >= 0 && sec.ShaderOffset < pkg.Shaders.Count)
                {
                    texName = pkg.Shaders[sec.ShaderOffset].TextureName;
                }
                if (string.IsNullOrEmpty(texName)) continue;

                Texture2D tileTex = LoadHudMapTileTexture(texName);
                if ((tileTex == null || IsBlankHudMapTexture(tileTex)) &&
                    !texName.EndsWith("_ni", StringComparison.OrdinalIgnoreCase))
                {
                    // Custom cities like Manhattan pack blank/all-zero .tex placeholders for the base
                    // shader names and store the actual 256x256 map tiles in <name>_ni.tga.
                    Texture2D fallbackNiTex = LoadHudMapTileTexture(texName + "_ni");
                    if (fallbackNiTex != null && !IsBlankHudMapTexture(fallbackNiTex))
                    {
                        tileTex = fallbackNiTex;
                    }
                }
                if (tileTex == null || IsBlankHudMapTexture(tileTex)) continue;

                int baseVert = allPositions.Count;
                for (int i = 0; i < sec.Positions.Count; i++)
                {
                    Vector3 p = sec.Positions[i];
                    // In hudmap_*.pkg, +X is East, -Z is North (where UV.y = 1.0).
                    // Mapping (vx, vy, vz) -> (vx, 0f, -vz) places East at +X and North at +Z.
                    allPositions.Add(new Vector3(p.x, 0f, -p.z));
                    allNormals.Add(Vector3.up);
                    allUVs.Add(sec.UVs[i]);
                }

                var tris = new List<int>(sec.Triangles.Count);
                for (int i = 0; i + 2 < sec.Triangles.Count; i += 3)
                {
                    tris.Add(baseVert + sec.Triangles[i]);
                    tris.Add(baseVert + sec.Triangles[i + 2]);
                    tris.Add(baseVert + sec.Triangles[i + 1]);
                }

                submeshes.Add(tris);

                Material mat = new Material(unlitShader)
                {
                    name = $"HudMapMat_{texName}",
                    mainTexture = tileTex
                };
                if (mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", tileTex);
                }
                materials.Add(mat);
            }

            if (submeshes.Count == 0)
            {
                Destroy(meshGo);
                return false;
            }

            Mesh mesh = new Mesh
            {
                name = $"HudMap_{mapName}",
                indexFormat = IndexFormat.UInt32
            };
            mesh.SetVertices(allPositions);
            mesh.SetNormals(allNormals);
            mesh.SetUVs(0, allUVs);
            mesh.subMeshCount = submeshes.Count;
            for (int s = 0; s < submeshes.Count; s++)
            {
                mesh.SetTriangles(submeshes[s], s);
            }
            mesh.RecalculateBounds();
            minimapBounds = mesh.bounds;
            hasMinimapBounds = minimapBounds.size.x > 10f && minimapBounds.size.z > 10f;

            MeshFilter mf = meshGo.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            MeshRenderer mr = meshGo.AddComponent<MeshRenderer>();
            mr.sharedMaterials = materials.ToArray();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            return true;
        }

        private static Texture2D LoadHudMapTileTexture(string texName)
        {
            // Load with 3D .pkg UV orientation (forUi: false) rather than 2D UI sprite orientation
            // so .tga hudmap tiles (such as Manhattan's as_hudmap_*_ni.tga) are not flipped vertically,
            // while clamping wrap mode so tile edges never bleed across seams.
            Texture2D tex = MM2RuntimeDecoders.LoadTexture(texName).Texture;
            if (tex != null)
            {
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.wrapModeU = TextureWrapMode.Clamp;
                tex.wrapModeV = TextureWrapMode.Clamp;
            }
            return tex;
        }

        private static bool IsBlankHudMapTexture(Texture2D tex)
        {
            if (tex == null || !tex.isReadable) return false;
            Color32[] pixels = tex.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                if (c.r != 0 || c.g != 0 || c.b != 0 || c.a != 0)
                {
                    return false;
                }
            }
            return true;
        }

        private void EnsureHudInitialized()
        {
            if (instance == null) instance = this;

            if (texDigits == null || texDigits.Length < 10)
            {
                texDigits = new Texture2D[10];
            }

            if (texGears.Count == 0 || texDigits[0] == null || texSpeedTicks == null)
            {
                MM2ArchiveSystem.EnsureInitialized();
                LoadHudTextures();
            }

            if (hudCanvas == null)
            {
                Transform canvasTf = transform.Find("HUD_Canvas");
                if (canvasTf != null)
                {
                    hudCanvas = canvasTf.GetComponent<Canvas>();
                }
                else
                {
                    BuildHudCanvas();
                }
            }

            if (hudCanvas != null && (gearRawImage == null || bottomLeftDashRect == null))
            {
                Transform bl = hudCanvas.transform.Find("BottomLeft_DashHUD");
                if (bl != null)
                {
                    bottomLeftDashRect = bl.GetComponent<RectTransform>();
                    damageBarRawImage = bl.Find("DamageMeter_Fill")?.GetComponent<RawImage>();
                    damageLabelRawImage = bl.Find("DamageMeter_Label")?.GetComponent<RawImage>();
                    revCounterRawImage = bl.Find("RevCounter_Ticks")?.GetComponent<RawImage>();
                    if (revCounterRawImage != null) revCounterRect = revCounterRawImage.rectTransform;
                    gearRawImage = bl.Find("Gear_Indicator")?.GetComponent<RawImage>();
                    digitHundredsRawImage = bl.Find("Digit_Hundreds")?.GetComponent<RawImage>();
                    digitTensRawImage = bl.Find("Digit_Tens")?.GetComponent<RawImage>();
                    digitOnesRawImage = bl.Find("Digit_Ones")?.GetComponent<RawImage>();
                }
            }

            if (hudCanvas != null && txtFpsCounter == null)
            {
                Transform fpsTf = hudCanvas.transform.Find("Txt_FpsCounter");
                if (fpsTf != null)
                {
                    txtFpsCounter = fpsTf.GetComponent<Text>();
                }
                else
                {
                    RectTransform canvasRect = hudCanvas.GetComponent<RectTransform>();
                    txtFpsCounter = CreateHudText(
                        "Txt_FpsCounter",
                        canvasRect,
                        new Vector2(1f, 1f),
                        new Vector2(1f, 1f),
                        new Vector2(-18f, -12f),
                        new Vector2(240f, 44f),
                        28,
                        TextAnchor.UpperRight,
                        new Color32(45, 255, 45, 255),
                        "60 FPS"
                    );
                }
            }

            if (minimapRawImage == null && hudCanvas != null)
            {
                minimapRawImage = hudCanvas.transform.Find("BottomRight_Minimap/Minimap_RenderView")?.GetComponent<RawImage>();
            }

            if (minimapRawImage != null &&
                (playerArrowRect == null || playerArrowRect.sizeDelta.x < 50f || checkpointMapIcons.Count == 0 || opponentMapIcons.Count == 0 ||
                 (checkpointMapIcons[0] != null && Mathf.Abs(checkpointMapIcons[0].sizeDelta.x - 36.4f) > 0.5f)))
            {
                RebuildMinimapIcons(minimapRawImage.transform);
            }
        }

        private void RebuildMinimapIcons(Transform mapViewTf)
        {
            if (mapViewTf == null) return;

            for (int i = mapViewTf.childCount - 1; i >= 0; i--)
            {
                Destroy(mapViewTf.GetChild(i).gameObject);
            }

            checkpointMapIcons.Clear();
            opponentMapIcons.Clear();
            policeMapIcons.Clear();

            if (cachedTriangleSprite == null) cachedTriangleSprite = CreatePlayerTriangleSprite();
            if (cachedDiamondSprite == null) cachedDiamondSprite = CreateCheckpointDiamondSprite();

            // 1. Pre-create Opponent / Quest Vehicle Icons (blue arrows for opponents, purple arrows for quest cars)
            for (int i = 0; i < 8; i++)
            {
                GameObject oppIconGo = new GameObject($"Opponent_MapIcon_{i}");
                oppIconGo.transform.SetParent(mapViewTf, false);
                RectTransform oppRt = oppIconGo.AddComponent<RectTransform>();
                oppRt.anchorMin = new Vector2(0.5f, 0.5f);
                oppRt.anchorMax = new Vector2(0.5f, 0.5f);
                oppRt.pivot = new Vector2(0.5f, 0.5f);
                oppRt.anchoredPosition = Vector2.zero;
                oppRt.sizeDelta = new Vector2(44f, 44f);
                Image oppImg = oppIconGo.AddComponent<Image>();
                oppImg.sprite = cachedTriangleSprite;
                oppImg.color = new Color32(45, 145, 255, 255);
                oppImg.raycastTarget = false;
                oppIconGo.SetActive(false);
                opponentMapIcons.Add(oppImg);
            }

            // 2. Pre-create Police Cruiser Icons (red arrows)
            for (int i = 0; i < 6; i++)
            {
                GameObject copIconGo = new GameObject($"Police_MapIcon_{i}");
                copIconGo.transform.SetParent(mapViewTf, false);
                RectTransform copRt = copIconGo.AddComponent<RectTransform>();
                copRt.anchorMin = new Vector2(0.5f, 0.5f);
                copRt.anchorMax = new Vector2(0.5f, 0.5f);
                copRt.pivot = new Vector2(0.5f, 0.5f);
                copRt.anchoredPosition = Vector2.zero;
                copRt.sizeDelta = new Vector2(44f, 44f);
                Image copImg = copIconGo.AddComponent<Image>();
                copImg.sprite = cachedTriangleSprite;
                copImg.color = new Color32(255, 45, 45, 255);
                copImg.raycastTarget = false;
                copIconGo.SetActive(false);
                policeMapIcons.Add(copImg);
            }

            // 3. Pre-create Checkpoint Icons (green diamonds, 1.3x size = 36.4x36.4, rendered ON TOP of opponents and police)
            EnsureCheckpointMapIconCount(24, mapViewTf);

            // 4. Player Vehicle Arrow Icon (yellow arrow, 2.5x larger = 65x65, rendered on top)
            GameObject arrowGo = new GameObject("Player_ArrowIcon");
            arrowGo.transform.SetParent(mapViewTf, false);
            playerArrowRect = arrowGo.AddComponent<RectTransform>();
            playerArrowRect.anchorMin = new Vector2(0.5f, 0.5f);
            playerArrowRect.anchorMax = new Vector2(0.5f, 0.5f);
            playerArrowRect.pivot = new Vector2(0.5f, 0.5f);
            playerArrowRect.anchoredPosition = Vector2.zero;
            playerArrowRect.sizeDelta = new Vector2(65f, 65f);

            Image arrowImg = arrowGo.AddComponent<Image>();
            arrowImg.sprite = cachedTriangleSprite;
            arrowImg.color = new Color32(255, 238, 20, 255);
            arrowImg.raycastTarget = false;
        }

        private void EnsureCheckpointMapIconCount(int requiredCount, Transform mapViewTf)
        {
            if (mapViewTf == null) return;
            if (cachedDiamondSprite == null) cachedDiamondSprite = CreateCheckpointDiamondSprite();

            while (checkpointMapIcons.Count < requiredCount)
            {
                int idx = checkpointMapIcons.Count;
                GameObject cpIconGo = new GameObject($"Checkpoint_MapIcon_{idx}");
                cpIconGo.transform.SetParent(mapViewTf, false);
                // Place checkpoint icons above opponents and police, right below the player arrow
                cpIconGo.transform.SetAsLastSibling();
                if (playerArrowRect != null)
                {
                    playerArrowRect.SetAsLastSibling();
                }
                RectTransform cpRt = cpIconGo.AddComponent<RectTransform>();
                cpRt.anchorMin = new Vector2(0.5f, 0.5f);
                cpRt.anchorMax = new Vector2(0.5f, 0.5f);
                cpRt.pivot = new Vector2(0.5f, 0.5f);
                cpRt.anchoredPosition = Vector2.zero;
                cpRt.sizeDelta = new Vector2(36.4f, 36.4f); // 1.3x of 28f
                Image cpImg = cpIconGo.AddComponent<Image>();
                cpImg.sprite = cachedDiamondSprite;
                cpImg.color = new Color32(40, 255, 65, 255);
                cpImg.raycastTarget = false;
                cpIconGo.SetActive(false);
                checkpointMapIcons.Add(cpRt);
            }
        }

        private void Update()
        {
            EnsureHudInitialized();
            UpdateDashResolutionScale();
            UpdateFpsCounter();

            // Hide HUD & minimap camera while main menu is active
            bool menuOpen = FindFirstObjectByType<MM2MenuController>() != null;
            if (hudCanvas != null && hudCanvas.enabled == menuOpen)
            {
                hudCanvas.enabled = !menuOpen;
            }
            if (minimapCamera != null && minimapCamera.enabled == menuOpen)
            {
                minimapCamera.enabled = !menuOpen;
            }
            if (hudArrowCamera != null && hudArrowCamera.enabled == menuOpen)
            {
                hudArrowCamera.enabled = !menuOpen;
            }
            if (menuOpen) return;

            CheckCityChanged();

            ArcadeVehicleController car = FindActivePlayerCar();
            if (car == null) return;

            if (WasMinimapZoomPressedThisFrame())
            {
                zoomPresetIndex = (zoomPresetIndex + 1) % ZoomMultipliers.Length;
            }

            UpdateSpeedAndTachometer(car);
            currentDamage01 = car.VehicleDamage != null ? car.VehicleDamage.Damage01 : 0f;
            UpdateDamageMeterUv(currentDamage01);
            UpdateMinimapTransform(car);
            UpdateRaceHud(car);
        }

        private void UpdateFpsCounter()
        {
            if (txtFpsCounter == null) return;
            MM2GameSession.EnsureSettingsLoaded();
            bool showFps = MM2GameSession.ShowFpsCounter;
            if (txtFpsCounter.gameObject.activeSelf != showFps)
            {
                txtFpsCounter.gameObject.SetActive(showFps);
            }
            if (!showFps) return;

            float dt = Time.unscaledDeltaTime;
            if (dt > 0.0001f && dt < 1.0f)
            {
                smoothedUnscaledDt += (dt - smoothedUnscaledDt) * 0.1f;
            }
            fpsDisplayUpdateTimer -= dt;
            if (fpsDisplayUpdateTimer <= 0f)
            {
                fpsDisplayUpdateTimer = 0.25f;
                int fps = Mathf.Clamp(Mathf.RoundToInt(1.0f / Mathf.Max(0.0005f, smoothedUnscaledDt)), 1, 9999);
                txtFpsCounter.text = $"{fps} FPS";
            }
        }

        private void UpdateRaceHud(ArcadeVehicleController car)
        {
            MM2RaceManager raceMgr = MM2RaceManager.Instance;
            bool isRace = raceMgr != null && raceMgr.IsRaceActive;
            bool isSinking = raceMgr != null && raceMgr.IsSinkingInWater;
            bool isTotaled = raceMgr != null && raceMgr.IsPlayerTotaled;
            bool showContainer = isRace || isSinking || isTotaled;

            if (raceHudContainer != null && raceHudContainer.activeSelf != showContainer)
            {
                raceHudContainer.SetActive(showContainer);
            }
            if (!showContainer) return;

            bool showPos = isRace && raceMgr.TotalRacers > 1 &&
                (string.Equals(raceMgr.ActiveRaceType, "Circuit", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(raceMgr.ActiveRaceType, "Checkpoint", StringComparison.OrdinalIgnoreCase));
            bool showLaps = isRace && string.Equals(raceMgr.ActiveRaceType, "Circuit", StringComparison.OrdinalIgnoreCase);

            float nextTopY = -14f;

            // 1. Top-Left Place, Check, and Lap Counters (matching Screenshots 1 & 2)
            if (txtPositionCounter != null)
            {
                if (txtPositionCounter.gameObject.activeSelf != showPos)
                {
                    txtPositionCounter.gameObject.SetActive(showPos);
                }
                if (showPos)
                {
                    txtPositionCounter.rectTransform.anchoredPosition = new Vector2(12f, nextTopY);
                    txtPositionCounter.text = $"Place:  {raceMgr.PlayerPosition}/{raceMgr.TotalRacers}";
                    nextTopY -= 44f;
                }
            }

            if (txtCheckCounter != null)
            {
                if (txtCheckCounter.gameObject.activeSelf != isRace)
                {
                    txtCheckCounter.gameObject.SetActive(isRace);
                }
                if (isRace)
                {
                    txtCheckCounter.rectTransform.anchoredPosition = new Vector2(12f, nextTopY);
                    txtCheckCounter.text = $"Check:  {raceMgr.CollectedCheckpoints}/{raceMgr.TotalCheckpoints}";
                    nextTopY -= 44f;
                }
            }

            if (txtLapCounter != null)
            {
                if (txtLapCounter.gameObject.activeSelf != showLaps)
                {
                    txtLapCounter.gameObject.SetActive(showLaps);
                }
                if (showLaps)
                {
                    txtLapCounter.rectTransform.anchoredPosition = new Vector2(12f, nextTopY);
                    var sb = new System.Text.StringBuilder(128);
                    sb.Append($"Lap:  {raceMgr.CurrentLap}/{raceMgr.TotalLaps}");
                    var lapSplits = raceMgr.CompletedLapTimes;
                    if (lapSplits != null && lapSplits.Count > 0)
                    {
                        for (int i = 0; i < lapSplits.Count; i++)
                        {
                            sb.Append('\n');
                            sb.Append($"{i + 1}.   {MM2RaceManager.FormatRaceTime(lapSplits[i], padMinutes: false)}");
                        }
                    }
                    txtLapCounter.text = sb.ToString();
                }
            }

            // 2. Top-Centre Timer (MM:SS:CC)
            if (txtRaceTimer != null)
            {
                if (txtRaceTimer.gameObject.activeSelf != isRace)
                {
                    txtRaceTimer.gameObject.SetActive(isRace);
                }
                if (isRace)
                {
                    txtRaceTimer.text = MM2RaceManager.FormatRaceTime(raceMgr.DisplayTimeSeconds, padMinutes: true);
                }
            }

            // 3. Top-Centre Yellow Countdown / Status Text ("Ready...", "Set...", "Go", "Sleep with the fishes!", "You finished 1st!")
            if (txtCenterStatus != null)
            {
                string status = raceMgr.CenterBannerText ?? "";
                if (txtCenterStatus.text != status)
                {
                    txtCenterStatus.text = status;
                }
            }

            // 4. Top-Centre 3D Green Arrow pointing as-the-crow-flies to nearest/next checkpoint
            if (hudArrowRawImage != null && hudArrowMeshPivot != null)
            {
                Vector3 targetPos = Vector3.zero;
                bool hasTarget = isRace && !isSinking && raceMgr.TryGetTargetCheckpointPosition(car.transform.position, out targetPos);
                if (hudArrowRawImage.gameObject.activeSelf != hasTarget)
                {
                    hudArrowRawImage.gameObject.SetActive(hasTarget);
                }

                if (hasTarget)
                {
                    Vector3 carPos = car.transform.position;
                    Vector3 carFwd = car.transform.forward;
                    carFwd.y = 0f;
                    if (carFwd.sqrMagnitude < 0.0001f) carFwd = Vector3.forward;
                    carFwd.Normalize();

                    Vector3 carVel = car.VehicleRigidbody != null ? car.VehicleRigidbody.linearVelocity : Vector3.zero;
                    carVel.y = 0f;

                    Vector3 toTargetFromNow = targetPos - carPos;
                    toTargetFromNow.y = 0f;

                    // Aim the arrow from the car's predicted position in hudArrowLeadTimeSeconds (default 1.0s),
                    // clamped strictly to the forward distance along velocity and faded out when the car's velocity
                    // ray already passes through the checkpoint gate so the arrow never swings sideways/backwards
                    // right before crossing a checkpoint.
                    Vector3 predictedPos = carPos;
                    float speed = carVel.magnitude;
                    if (speed > 0.05f && hudArrowLeadTimeSeconds > 0f && toTargetFromNow.sqrMagnitude > 0.01f)
                    {
                        Vector3 velDir = carVel / speed;
                        float forwardDist = Vector3.Dot(toTargetFromNow, velDir);
                        if (forwardDist > 0f)
                        {
                            float lateralDist = (toTargetFromNow - velDir * forwardDist).magnitude;
                            float lateralFactor = Mathf.InverseLerp(4.5f, 12.0f, lateralDist);
                            float maxLookaheadDist = Mathf.Min(speed * hudArrowLeadTimeSeconds, forwardDist * 0.85f);
                            predictedPos = carPos + velDir * (maxLookaheadDist * lateralFactor);
                        }
                    }

                    Vector3 toTarget = targetPos - predictedPos;
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude < 0.0001f)
                    {
                        toTarget = toTargetFromNow.sqrMagnitude > 0.0001f ? toTargetFromNow : carFwd;
                    }
                    toTarget.Normalize();

                    float yawDeg = Vector3.SignedAngle(carFwd, toTarget, Vector3.up);
                    hudArrowMeshPivot.localRotation = Quaternion.Euler(0f, yawDeg, 0f);
                }
            }
        }

        private static ArcadeVehicleController FindActivePlayerCar()
        {
            foreach (var car in FindObjectsByType<ArcadeVehicleController>(FindObjectsSortMode.None))
            {
                if (car != null && car.gameObject.activeInHierarchy)
                {
                    return car;
                }
            }
            return null;
        }

        private void UpdateSpeedAndTachometer(ArcadeVehicleController car)
        {
            float speedMph = Mathf.Max(0f, car.CurrentSpeedMph);
            float maxSpeed = Mathf.Max(60f, car.maxSpeedMph);
            float throttle = car.CurrentThrottle;

            bool isReversing = car.IsReversing;
            bool isManual = string.Equals(MM2GameSession.Transmission, "Manual", StringComparison.OrdinalIgnoreCase);

            // Use simulated MM2Transmission gear when available, with speed-band fallback
            float[] gearTopFractions = { 0.20f, 0.38f, 0.58f, 0.79f, 1.00f };
            float speedFrac = Mathf.Clamp01(speedMph / maxSpeed);

            int activeForwardGear = 1;
            if (car.CurrentSimGear >= 2)
            {
                activeForwardGear = Mathf.Clamp(car.CurrentSimGear - 1, 1, 5);
            }
            else
            {
                for (int g = 0; g < gearTopFractions.Length; g++)
                {
                    if (speedFrac <= gearTopFractions[g] || g == gearTopFractions.Length - 1)
                    {
                        activeForwardGear = g + 1;
                        break;
                    }
                }
            }

            string gearKey;
            int rpmGearIndex;

            if (isManual)
            {
                HandleManualGearInput(activeForwardGear, isReversing);
                if (manualGear < 0)
                {
                    gearKey = "R";
                    rpmGearIndex = 0;
                }
                else if (manualGear == 0)
                {
                    gearKey = "N";
                    rpmGearIndex = 0;
                }
                else
                {
                    gearKey = Mathf.Clamp(manualGear, 1, 5).ToString();
                    rpmGearIndex = Mathf.Clamp(manualGear - 1, 0, 4);
                }
            }
            else
            {
                gearKey = isReversing ? "R" : "D";
                rpmGearIndex = isReversing ? 0 : (activeForwardGear - 1);
                lastAutoGear = activeForwardGear;
            }

            // Update Gear Indicator texture
            if (gearRawImage != null && texGears.TryGetValue(gearKey, out Texture2D gearTex) && gearTex != null)
            {
                gearRawImage.texture = gearTex;
            }

            // Compute RPM within the active gear band
            float targetRpm01;
            if (isReversing)
            {
                targetRpm01 = Mathf.Clamp(0.16f + (speedMph / 35f) * 0.72f + Mathf.Abs(throttle) * 0.10f, 0.14f, 0.95f);
            }
            else
            {
                float bandMin = rpmGearIndex == 0 ? 0f : gearTopFractions[rpmGearIndex - 1] * 0.78f;
                float bandMax = gearTopFractions[rpmGearIndex];
                float gearProgress = Mathf.InverseLerp(bandMin, bandMax, speedFrac);
                float idleFloor = 0.13f;
                float throttleBoost = Mathf.Max(0f, throttle) * 0.12f;
                targetRpm01 = Mathf.Clamp(idleFloor + gearProgress * 0.78f + throttleBoost, idleFloor, 0.98f);
            }

            displayedRpm01 = Mathf.Lerp(displayedRpm01, targetRpm01, Time.deltaTime * 10f);

            // Clip SPEED_TICKS horizontally from the left (16 bars total)
            if (revCounterRawImage != null && revCounterRect != null)
            {
                float clampedRpm = Mathf.Clamp(displayedRpm01, 0.06f, 1f);
                revCounterRect.sizeDelta = new Vector2(RevBarMaxWidth * clampedRpm, RevBarHeight);
                revCounterRawImage.uvRect = new Rect(0f, 0f, clampedRpm, 1f);
            }

            // Update 1-3 digit speedometer (DIGITAC_0 .. DIGITAC_9)
            int speedInt = Mathf.Clamp(Mathf.RoundToInt(speedMph), 0, 999);
            int ones = speedInt % 10;
            int tens = (speedInt / 10) % 10;
            int hundreds = (speedInt / 100) % 10;

            if (digitOnesRawImage != null && texDigits[ones] != null)
            {
                digitOnesRawImage.texture = texDigits[ones];
            }

            if (digitTensRawImage != null)
            {
                bool showTens = speedInt >= 10;
                if (digitTensRawImage.gameObject.activeSelf != showTens)
                {
                    digitTensRawImage.gameObject.SetActive(showTens);
                }
                if (showTens && texDigits[tens] != null)
                {
                    digitTensRawImage.texture = texDigits[tens];
                }
            }

            if (digitHundredsRawImage != null)
            {
                bool showHundreds = speedInt >= 100;
                if (digitHundredsRawImage.gameObject.activeSelf != showHundreds)
                {
                    digitHundredsRawImage.gameObject.SetActive(showHundreds);
                }
                if (showHundreds && texDigits[hundreds] != null)
                {
                    digitHundredsRawImage.texture = texDigits[hundreds];
                }
            }
        }

        private void HandleManualGearInput(int suggestedForwardGear, bool isReversing)
        {
            bool shiftUp = false;
            bool shiftDown = false;

#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
            {
                shiftUp = Keyboard.current.eKey.wasPressedThisFrame || Keyboard.current.leftShiftKey.wasPressedThisFrame;
                shiftDown = Keyboard.current.qKey.wasPressedThisFrame || Keyboard.current.leftCtrlKey.wasPressedThisFrame;
            }
#else
            shiftUp = Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.LeftShift);
            shiftDown = Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.LeftControl);
#endif

            if (shiftUp)
            {
                manualGear = Mathf.Clamp(manualGear + 1, -1, 5);
            }
            else if (shiftDown)
            {
                manualGear = Mathf.Clamp(manualGear - 1, -1, 5);
            }
            else if (isReversing)
            {
                manualGear = -1;
            }
            else if (manualGear <= 0 && !isReversing)
            {
                manualGear = suggestedForwardGear;
            }
            else
            {
                // Keep manual gear synchronized if player accelerates/decelerates across gear bands without manual shifting
                manualGear = suggestedForwardGear;
            }
        }

        private void UpdateDamageMeterUv(float damage01)
        {
            if (damageBarRawImage == null) return;
            // DAMAGE.tga is 500x12 (green on left -> red on right); DAMAGE_LABLE.tga window is 129x12.
            const float uWidth = 129f / 500f;
            float uMin = Mathf.Clamp01(damage01) * (1f - uWidth);
            damageBarRawImage.uvRect = new Rect(uMin, 0f, uWidth, 1f);
        }

        private void UpdateMinimapTransform(ArcadeVehicleController car)
        {
            if (minimapCamera == null) return;

            Vector3 carPos = car.transform.position;
            Vector3 carFwd = car.transform.forward;

            Rect viewRect = minimapRawImage != null ? minimapRawImage.rectTransform.rect : new Rect(0f, 0f, 400f, 268f);
            float halfUiW = Mathf.Max(1f, viewRect.width * 0.5f);
            float halfUiH = Mathf.Max(1f, viewRect.height * 0.5f);
            float aspect = halfUiW / halfUiH;
            minimapCamera.aspect = aspect;

            if (!hasHudMapPkgGeometry && !hasMinimapBounds)
            {
                hasMinimapBounds = TryComputeFallbackCityBounds(out minimapBounds);
            }

            // Dynamic speed zoom (matching tune/sf.mmHudMap ZoomInDist/ZoomOutDist behavior)
            float speedT = Mathf.Clamp01(car.CurrentSpeedMph / 110f);
            float baseOrthoSize = Mathf.Lerp(225f, 330f, speedT) * ZoomMultipliers[zoomPresetIndex];
            if (hasMinimapBounds)
            {
                float maxOrthoH = minimapBounds.extents.z;
                float maxOrthoW = minimapBounds.extents.x / Mathf.Max(0.1f, aspect);
                float maxAllowedOrtho = Mathf.Min(maxOrthoH, maxOrthoW);
                if (maxAllowedOrtho > 25f)
                {
                    baseOrthoSize = Mathf.Min(baseOrthoSize, maxAllowedOrtho);
                }
            }
            minimapCamera.orthographicSize = Mathf.Lerp(minimapCamera.orthographicSize, baseOrthoSize, Time.deltaTime * 6f);

            float orthoHalfH = Mathf.Max(1f, minimapCamera.orthographicSize);
            float orthoHalfW = orthoHalfH * aspect;

            Vector3 focusWorldPos = carPos;
            Vector2 playerUiOffset = Vector2.zero;

            if (hasHudMapPkgGeometry)
            {
                // Unity world position is (-vx_age, vy_age, vz_age).
                // Our minimap mesh is built at (vx_age, MinimapWorldY, -vz_age) = (-carPos.x, MinimapWorldY, -carPos.z).
                float rawCamX = -carPos.x;
                float rawCamZ = -carPos.z;
                float camX = rawCamX;
                float camZ = rawCamZ;

                if (hasMinimapBounds)
                {
                    float minCamX = minimapBounds.min.x + orthoHalfW;
                    float maxCamX = minimapBounds.max.x - orthoHalfW;
                    camX = (minCamX <= maxCamX) ? Mathf.Clamp(rawCamX, minCamX, maxCamX) : minimapBounds.center.x;

                    float minCamZ = minimapBounds.min.z + orthoHalfH;
                    float maxCamZ = minimapBounds.max.z - orthoHalfH;
                    camZ = (minCamZ <= maxCamZ) ? Mathf.Clamp(rawCamZ, minCamZ, maxCamZ) : minimapBounds.center.z;
                }

                minimapCamera.transform.position = new Vector3(camX, MinimapWorldY + 60f, camZ);
                minimapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                minimapCamera.nearClipPlane = 5f;
                minimapCamera.farClipPlane = 200f;

                focusWorldPos = new Vector3(-camX, carPos.y, -camZ);
                float playerNx = (rawCamX - camX) / orthoHalfW;
                float playerNy = (rawCamZ - camZ) / orthoHalfH;
                playerUiOffset = new Vector2(
                    Mathf.Clamp(playerNx * halfUiW, -halfUiW * 0.94f, halfUiW * 0.94f),
                    Mathf.Clamp(playerNy * halfUiH, -halfUiH * 0.94f, halfUiH * 0.94f)
                );

                if (playerArrowRect != null)
                {
                    // East on minimap (+X) is -carFwd.x; North on minimap (+Y) is -carFwd.z.
                    float mapDx = -carFwd.x;
                    float mapDy = -carFwd.z;
                    float angleDeg = Mathf.Atan2(-mapDx, mapDy) * Mathf.Rad2Deg;
                    playerArrowRect.anchoredPosition = playerUiOffset;
                    playerArrowRect.localEulerAngles = new Vector3(0f, 0f, angleDeg);
                }
            }
            else
            {
                // Fallback for custom cities without a hudmap_*.pkg: top-down orthographic view of the 3D city
                float rawCamX = carPos.x;
                float rawCamZ = carPos.z;
                float camX = rawCamX;
                float camZ = rawCamZ;

                if (hasMinimapBounds)
                {
                    float minCamX = minimapBounds.min.x + orthoHalfW;
                    float maxCamX = minimapBounds.max.x - orthoHalfW;
                    camX = (minCamX <= maxCamX) ? Mathf.Clamp(rawCamX, minCamX, maxCamX) : minimapBounds.center.x;

                    float minCamZ = minimapBounds.min.z + orthoHalfH;
                    float maxCamZ = minimapBounds.max.z - orthoHalfH;
                    camZ = (minCamZ <= maxCamZ) ? Mathf.Clamp(rawCamZ, minCamZ, maxCamZ) : minimapBounds.center.z;
                }

                minimapCamera.transform.position = new Vector3(camX, carPos.y + 260f, camZ);
                minimapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                minimapCamera.nearClipPlane = 10f;
                minimapCamera.farClipPlane = 600f;

                focusWorldPos = new Vector3(camX, carPos.y, camZ);
                float playerNx = (rawCamX - camX) / orthoHalfW;
                float playerNy = (rawCamZ - camZ) / orthoHalfH;
                playerUiOffset = new Vector2(
                    Mathf.Clamp(playerNx * halfUiW, -halfUiW * 0.94f, halfUiW * 0.94f),
                    Mathf.Clamp(playerNy * halfUiH, -halfUiH * 0.94f, halfUiH * 0.94f)
                );

                if (playerArrowRect != null)
                {
                    float angleDeg = Mathf.Atan2(-carFwd.x, carFwd.z) * Mathf.Rad2Deg;
                    playerArrowRect.anchoredPosition = playerUiOffset;
                    playerArrowRect.localEulerAngles = new Vector3(0f, 0f, angleDeg);
                }
            }

            UpdateMinimapAiIcons(focusWorldPos);
        }

        private void UpdateMinimapAiIcons(Vector3 playerPos)
        {
            if (minimapCamera == null || minimapRawImage == null) return;

            float orthoHalfH = Mathf.Max(1f, minimapCamera.orthographicSize);
            Rect viewRect = minimapRawImage.rectTransform.rect;
            float halfUiW = viewRect.width * 0.5f;
            float halfUiH = viewRect.height * 0.5f;
            float aspect = halfUiH > 1f ? (halfUiW / halfUiH) : 1.333f;
            float orthoHalfW = orthoHalfH * aspect;

            // 1. Update Checkpoint Icons (green diamonds)
            MM2RaceManager raceMgr = MM2RaceManager.Instance;
            if (raceMgr != null)
            {
                raceMgr.GetActiveMinimapCheckpoints(minimapCheckpointPositions);
            }
            else
            {
                minimapCheckpointPositions.Clear();
            }

            int cpCount = minimapCheckpointPositions.Count;
            if (cpCount > checkpointMapIcons.Count)
            {
                EnsureCheckpointMapIconCount(cpCount, minimapRawImage.transform);
            }

            for (int i = 0; i < checkpointMapIcons.Count; i++)
            {
                RectTransform cpRt = checkpointMapIcons[i];
                if (cpRt == null) continue;

                if (i >= cpCount)
                {
                    if (cpRt.gameObject.activeSelf) cpRt.gameObject.SetActive(false);
                    continue;
                }

                if (ProjectWorldToMinimapUi(playerPos, minimapCheckpointPositions[i], Vector3.forward, orthoHalfW, orthoHalfH, halfUiW, halfUiH, out Vector2 cpUiPos, out _))
                {
                    if (!cpRt.gameObject.activeSelf) cpRt.gameObject.SetActive(true);
                    cpRt.anchoredPosition = cpUiPos;
                    cpRt.localEulerAngles = Vector3.zero;
                }
                else
                {
                    if (cpRt.gameObject.activeSelf) cpRt.gameObject.SetActive(false);
                }
            }

            // 2. Update Opponent / Quest Vehicle Icons (blue arrows for opponents, purple arrows for quest target vehicles)
            bool isQuestVehicleStage = raceMgr != null && raceMgr.IsQuestTargetVehicleStage;
            Color32 oppArrowColor = isQuestVehicleStage
                ? new Color32(195, 65, 255, 255)   // Purple arrow for quest vehicles (Follow / Destroy that car)
                : new Color32(45, 145, 255, 255);  // Blue arrow for race opponent vehicles

            IReadOnlyList<MM2OpponentAgent> opps = MM2OpponentManager.Instance != null
                ? MM2OpponentManager.Instance.ActiveOpponents
                : null;
            int oppCount = opps != null ? opps.Count : 0;

            for (int i = 0; i < opponentMapIcons.Count; i++)
            {
                Image oppImg = opponentMapIcons[i];
                if (oppImg == null) continue;
                RectTransform iconRt = oppImg.rectTransform;

                if (i >= oppCount || opps[i] == null)
                {
                    if (iconRt.gameObject.activeSelf) iconRt.gameObject.SetActive(false);
                    continue;
                }

                Transform oppTf = opps[i].transform;
                if (ProjectWorldToMinimapUi(playerPos, oppTf.position, oppTf.forward, orthoHalfW, orthoHalfH, halfUiW, halfUiH, out Vector2 uiPos, out float angleDeg))
                {
                    if (!iconRt.gameObject.activeSelf) iconRt.gameObject.SetActive(true);
                    iconRt.anchoredPosition = uiPos;
                    iconRt.localEulerAngles = new Vector3(0f, 0f, angleDeg);
                    oppImg.color = oppArrowColor;
                }
                else
                {
                    if (iconRt.gameObject.activeSelf) iconRt.gameObject.SetActive(false);
                }
            }

            // 3. Update Police Cruiser Icons (red arrows)
            IReadOnlyList<MM2PoliceAgent> cops = MM2PoliceManager.Instance != null
                ? MM2PoliceManager.Instance.ActiveCops
                : null;
            int copCount = cops != null ? cops.Count : 0;

            for (int i = 0; i < policeMapIcons.Count; i++)
            {
                Image copImg = policeMapIcons[i];
                if (copImg == null) continue;
                RectTransform iconRt = copImg.rectTransform;

                if (i >= copCount || cops[i] == null)
                {
                    if (iconRt.gameObject.activeSelf) iconRt.gameObject.SetActive(false);
                    continue;
                }

                MM2PoliceAgent cop = cops[i];
                Transform copTf = cop.transform;
                if (ProjectWorldToMinimapUi(playerPos, copTf.position, copTf.forward, orthoHalfW, orthoHalfH, halfUiW, halfUiH, out Vector2 uiPos, out float angleDeg))
                {
                    if (!iconRt.gameObject.activeSelf) iconRt.gameObject.SetActive(true);
                    iconRt.anchoredPosition = uiPos;
                    iconRt.localEulerAngles = new Vector3(0f, 0f, angleDeg);
                    copImg.color = new Color32(255, 45, 45, 255);
                }
                else
                {
                    if (iconRt.gameObject.activeSelf) iconRt.gameObject.SetActive(false);
                }
            }
        }

        private bool ProjectWorldToMinimapUi(
            Vector3 playerPos,
            Vector3 worldPos,
            Vector3 worldFwd,
            float orthoHalfW,
            float orthoHalfH,
            float halfUiW,
            float halfUiH,
            out Vector2 uiOffset,
            out float iconAngleDeg)
        {
            float dx, dy;
            if (hasHudMapPkgGeometry)
            {
                dx = -(worldPos.x - playerPos.x);
                dy = -(worldPos.z - playerPos.z);
                iconAngleDeg = Mathf.Atan2(worldFwd.x, -worldFwd.z) * Mathf.Rad2Deg;
            }
            else
            {
                dx = worldPos.x - playerPos.x;
                dy = worldPos.z - playerPos.z;
                iconAngleDeg = Mathf.Atan2(-worldFwd.x, worldFwd.z) * Mathf.Rad2Deg;
            }

            float nx = dx / orthoHalfW;
            float ny = dy / orthoHalfH;
            if (Mathf.Abs(nx) > 0.96f || Mathf.Abs(ny) > 0.96f)
            {
                uiOffset = Vector2.zero;
                return false;
            }

            uiOffset = new Vector2(nx * halfUiW, ny * halfUiH);
            return true;
        }

        private static bool WasMinimapZoomPressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
            {
                return Keyboard.current.tabKey.wasPressedThisFrame || Keyboard.current.mKey.wasPressedThisFrame;
            }
            return false;
#else
            return Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.M);
#endif
        }

        /// <summary>
        /// Generates a crisp 64x64 Sprite of an indicator triangle with a white interior fill
        /// (so Image.color tints cleanly to yellow, blue, red, or purple) and a black outline.
        /// Points North (+Y) at 0 degrees rotation.
        /// </summary>
        private static Sprite CreatePlayerTriangleSprite()
        {
            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "MM2_MinimapVehicleArrow",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color32[] pixels = new Color32[size * size];
            // Triangle vertices in [0, 1] normalized coordinates (pointing up/North):
            Vector2 top = new Vector2(0.50f, 0.88f);
            Vector2 bottomLeft = new Vector2(0.22f, 0.14f);
            Vector2 bottomRight = new Vector2(0.78f, 0.14f);

            Color32 whiteFill = new Color32(255, 255, 255, 255);
            Color32 blackOutline = new Color32(10, 10, 10, 255);
            Color32 transparent = new Color32(0, 0, 0, 0);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                    float sd = SignedDistanceTriangle(p, bottomLeft, bottomRight, top);

                    if (sd <= -0.045f)
                    {
                        pixels[y * size + x] = whiteFill;
                    }
                    else if (sd <= 0.015f)
                    {
                        float alpha = Mathf.Clamp01((0.015f - sd) / 0.02f);
                        byte a = (byte)Mathf.RoundToInt(alpha * 255f);
                        pixels[y * size + x] = new Color32(blackOutline.r, blackOutline.g, blackOutline.b, a);
                    }
                    else
                    {
                        pixels[y * size + x] = transparent;
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// Generates a crisp 64x64 diamond Sprite with a white fill (tinted green via Image.color)
        /// and a dark outline for minimap checkpoints.
        /// </summary>
        private static Sprite CreateCheckpointDiamondSprite()
        {
            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "MM2_MinimapCheckpointDiamond",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color32[] pixels = new Color32[size * size];
            Color32 whiteFill = new Color32(255, 255, 255, 255);
            Color32 blackOutline = new Color32(10, 10, 10, 255);
            Color32 transparent = new Color32(0, 0, 0, 0);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs((x + 0.5f) / size - 0.5f);
                    float dy = Mathf.Abs((y + 0.5f) / size - 0.5f);
                    // L1 (Manhattan) distance forms a 45-degree rotated square diamond
                    float sd = (dx + dy - 0.36f) * 0.70710678f;

                    if (sd <= -0.045f)
                    {
                        pixels[y * size + x] = whiteFill;
                    }
                    else if (sd <= 0.015f)
                    {
                        float alpha = Mathf.Clamp01((0.015f - sd) / 0.02f);
                        byte a = (byte)Mathf.RoundToInt(alpha * 255f);
                        pixels[y * size + x] = new Color32(blackOutline.r, blackOutline.g, blackOutline.b, a);
                    }
                    else
                    {
                        pixels[y * size + x] = transparent;
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static float SignedDistanceTriangle(Vector2 p, Vector2 p0, Vector2 p1, Vector2 p2)
        {
            Vector2 e0 = p1 - p0;
            Vector2 e1 = p2 - p1;
            Vector2 e2 = p0 - p2;

            Vector2 v0 = p - p0;
            Vector2 v1 = p - p1;
            Vector2 v2 = p - p2;

            Vector2 pq0 = v0 - e0 * Mathf.Clamp01(Vector2.Dot(v0, e0) / Vector2.Dot(e0, e0));
            Vector2 pq1 = v1 - e1 * Mathf.Clamp01(Vector2.Dot(v1, e1) / Vector2.Dot(e1, e1));
            Vector2 pq2 = v2 - e2 * Mathf.Clamp01(Vector2.Dot(v2, e2) / Vector2.Dot(e2, e2));

            float s = Mathf.Sign(e0.x * e2.y - e0.y * e2.x);
            Vector2 d = Vector2.Min(
                Vector2.Min(
                    new Vector2(Vector2.Dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                    new Vector2(Vector2.Dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))
                ),
                new Vector2(Vector2.Dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x))
            );

            return -Mathf.Sqrt(d.x) * Mathf.Sign(d.y);
        }
    }
}
