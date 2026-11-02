# Open Madness — Agent & Developer Guide (`AGENTS.md`)

Welcome to **Open Madness**, a clean-room, open-source Unity 6 LTS (`6000.3.27f2`, Universal Render Pipeline) game engine reimplementation for **Midtown Madness 2** (2000), **Midtown Madness 1** (1999), and community `.ar` mods.

This document captures the architectural rules, binary format specifications, coordinate conventions, physics behaviors, and hard-won engineering lessons from building the project. **AI coding agents and contributors must read and follow these guidelines before modifying the codebase.**

---

## 1. Clean-Room, Legal & Distribution Guardrails (CRITICAL)

Open Madness is a **Bring Your Own `.ar` Archives** engine reimplementation. Every change must preserve the repository's clean-room status:

1. **Zero Proprietary Game Assets in Git or Builds:**
   - **Never** commit or bundle `.ar` archives, extracted `.pkg`/`.psdl`/`.bms`/`.tex`/`.dds`/`.bai`/`.wav` assets, or retail save files (`.dat`, `.sav`, `.rec`, `players.dir`) in the repository or inside `Assets/Resources/` or `Assets/StreamingAssets/`.
   - All game assets load dynamically at runtime from `<ProjectRootOrBuildDir>/resources/`.
   - `.gitignore` excludes `/resources/*.ar`, `/resources/players/`, and `/settings.json`.
   - Standalone builds (`MM2StandaloneBuilder`, `MM2ResourcesBuildProcessor`) ship an **empty** `resources/` and `resources/players/` directory with a `README.txt` instructing players to copy in their own `.ar` files and optional `players/` save folder.

2. **Code & Comment Hygiene:**
   - **Never** hardcode licensed car manufacturer brand names (`Ford`, `Aston Martin`, `Ferrari`, `Audi`, `Panoz`, `Freightliner`, `Volkswagen`, etc.) or verbatim copyrighted vehicle marketing blurbs in C# source files or scenes. Always use internal `.ar` basenames (`vpmustang99`, `vpbullet`, `vpdb7`, `vpbug`, etc.) and read display names/descriptions dynamically from mounted `.ar` archives (`tune/vehicle.csv`, `localize/*.str`, `mm1_car_names.txt`) with generic fallbacks (`"Default Vehicle"`, `"Default City"`).
   - **Never** hardcode developer-specific absolute paths in code or comments.

3. **Zero Assumptions About Installed Archives (Graceful Degradation):**
   - Users may run Open Madness with **only MM2**, **only MM1**, **both MM1 + MM2**, or **custom community mod `.ar` files** (including when zero archives are present).
   - Every system must degrade gracefully when specific cities, vehicles, or modes are missing, for example we:
     - Hide the `CRASH COURSE` button if `MM2RuntimeCityLoader.GetCrashCourseCities().Count == 0` (e.g., MM1-only installs).
     - Hide individual race mode tabs (`BLITZ`, `CIRCUIT`, `CHECKPOINT`) if the selected city defines 0 events for that mode.
     - If the live 3D menu background city is not installed, fall back automatically to the static gradient background.
     - If a city lacks a `hudmap_<city>` texture, synthesize the placeholder preview sprite in `MM2MenuController`.

4. **Third-Party Font Licensing:**
   - Bundled UI fonts (`Assets/Resources/Fonts/OpenSans-*.ttf`) are licensed under the SIL Open Font License 1.1 (`Assets/Resources/Fonts/OFL.txt` and `THIRD_PARTY_NOTICES.md`). Both files are automatically copied into standalone build output directories.

---

## 2. Project & Scene Architecture

### Scenes (`Assets/Scenes/`)
- **`MenuSystem.unity` (Build Index 0):** Startup scene containing `MM2_MenuSystem` (`MM2MenuController`). If the user runs `GoDrive.unity` first in the Editor without going through the menu, `MM2MenuBootstrap` redirects to `MenuSystem.unity` on startup.
- **`GoDrive.unity` (Build Index 1):** Active driving scene containing `PlayerVehicle` (`ArcadeVehicleController`, `VehiclePaintjob`, `MM2VehicleDamage`, `VehicleAudio`), `Main Camera` (`ArcadeChaseCamera`), and `Directional Light`. Everything else (city geometry, sky, lighting, props, traffic, cops, opponents, pedestrians, HUD) is constructed dynamically at runtime from `.ar` archives.

### Core Script Modules (`Assets/Scripts/`)
| Directory | Script | Responsibility |
| :--- | :--- | :--- |
| `Archive/` | `MM2ArchiveSystem.cs` | Virtual File System (VFS) mounting `.ar` files from `resources/` (`DAVE`, `ARES`, `PKZIP`, and `AR-locked PKZIP`). Base archives mount first; mod archives mount second so mods can override base assets. |
| `Archive/` | `MM2RuntimeDecoders.cs` | Runtime binary decoders for `.tex` (14-byte header + BGRA palette), `.tga`, `.dds` (MM1 16/24/32-bit + `.lut` palettes), `.bmp`/`.jpg`/`.png`, and `.wav` (PCM & 4-bit IMA ADPCM -> `AudioClip`). |
| `Archive/` | `MM2RuntimePkgLoader.cs` | Parses MM2 `PKG3` (`.pkg`) and MM1 `3HSM` (`.bms`) 3D meshes and 48-byte `.mtx` wheel/pivot matrices into 32-bit `UnityEngine.Mesh` and URP materials. |
| `Archive/` | `MM2RuntimeCityLoader.cs` | Discovers cities (`tune/*.cinfo`), races, Crash Courses (`mmcrashdata.csv`, `crash*data.csv`), Cruise spawn mods (`city/spawn_<city>_*.txt`), and rewards (`*_rewards.csv`). Builds 3D cities at runtime from MM2 `.psdl`/`.inst`/`.pathset` or MM1 `.ptl`/`.cells`/`cull*.bms`/`.fcd`/`.bng`/`.gizmo`, plus `MM2DrawbridgeGroup` and `MM2SkyDomeFollower`. |
| `Archive/` | `MM2BangerSystem.cs` | Interactive crashable street props, trees, traffic lights, procedural sidewalk props (`proprules.csv`/`propdefs.csv`), and breakable glass (`tune/banger/*.dgBangerData` & MM1 `*.mmbangerdata`). |
| `Archive/` | `MM2EnvironmentManager.cs` | 16-condition (`4 TimeOfDay × 4 Weather`) lighting (`.lt00..15`), linear fog (`_fog.csv`), sky dome textures (`.sky`), `_fa` wet road / `_ni` night window material swapping, rain particles (`rain.asBirthRule`), weather audio, and wet-road friction scaling. |
| `Archive/` | `MM2PlayerManager.cs` | Binary-compatible reader/writer for `resources/players/` (`players.dir`, `playerN.sav`, `<city>/playerN.rec`, `<city>/amateur.dat`, `<city>/pro.dat`) with CRC-32 validation (`0xEDB8B23B`). Automatically creates a clean Amateur `DriverX` profile if `resources/players/` is missing. *(Note: MM1 savegames are not supported.)* |
| `Vehicle/` | `MM2CarSim.cs` | Authentic vehicle simulation (`MM2Engine`, `MM2Transmission`, `MM2Drivetrain`, `MM2Wheel`, `MM2Aero`, `MM2Gyro`, `MM2CarSim`) driven by `.vehcarsim`/`.mmcarsim` and `.vehGyro`. |
| `Vehicle/` | `ArcadeVehicleController.cs` | Master vehicle physics controller, `.asNode` discrete steering filter, curb-climbing assist, full-lock drift & handbrake turns, tire smoke, and dynamic lights (`L` headlight toggle, brake/reverse lights). |
| `Vehicle/` | `VehiclePaintjob.cs` | `VehicleCatalog` discovery (`tune/*.info`), paintjob material switcher, and runtime mesh/wheel-collider/audio configurator. |
| `Vehicle/` | `MM2VehicleDamage.cs` | Speed/mass-based collision damage (`.vehcardamage`), directional `_dmg` texture swapping, progressive white-to-black engine smoke (`fxpt8.tex`), impact/wobble/city-announcer audio, wheel detachment on total, and Cruise-mode auto-repair. |
| `Vehicle/` | `VehicleAudio.cs` | Engine idle/drive pitch modulation, heavy-damage sputter, horn (`H`), and police siren toggle (`H` on police vehicles). |
| `AI/` | `MM2AiMapData.cs` | Parses MM2 `CAI1` `.bai` and MM1 `.bai` road networks, `.aimap` configs, and `.opp` opponent routes. Provides elevation-aware road snapping and U-turn-penalized race route finding. |
| `AI/` | `MM2TrafficManager.cs` | Ambient rail-following traffic (`va_*`) with lane following, Hermite intersection turns, traffic light/stop sign compliance, turn signals (`SLIGHT0_L`/`SLIGHT1_L`), and dynamic rigidbody handoff on collision. |
| `AI/` | `MM2PoliceManager.cs` | `vpcop` and Crash Course pursuer AI (`StationaryWatch`, `Patrol`, `Pursuit` states), alternating roof lightbars (`SIREN0_L`/`SIREN1_L`), and 3D sirens. |
| `AI/` | `MM2OpponentManager.cs` | Checkpoint & Circuit race opponents (`1..7`) and Crash Course target/follow cars. |
| `AI/` | `MM2PedestrianManager.cs` | Sidewalk pedestrians decoded from `.skel`/`.mod`/`.anim` (MM2 `anim/` and MM1 `rex/`) with outfit palettes, screams, dive-away animations, and hard vehicle exclusion so they can never be struck. |
| `AI/` | `MM2SpecialTrafficManager.cs` | San Francisco Cable Cars (`va_cablecar_f`), London Underground trains (`va_ug_l`), and Chicago Elevated "L" trains (`r_l_train`). |
| `UI/` | `MM2MenuController.cs` | Widescreen-responsive menu UI (`ScreenMatchMode.Expand`), Driver profiles/stats/records, Races & Starting Point picker, Crash Course Hub, Select Vehicle 3D turntable & `640x480` Showcase modal, Options (`settings.json`), Help/About, and Event Results. |
| `UI/` | `MM2GameplayHud.cs` | Digital/needle gauges, checkpoint/lap/place counters, timer, 3D green guidance arrow (`hudarrow01.pkg`), and bottom-right rotating minimap (`hudmap_<map>.pkg`). |
| `UI/` | `MM2RaceManager.cs` | Orchestrates Cruise, Blitz, Checkpoint, Circuit, and Crash Course rules, gates (`pt_check.pkg` / `check_f.bms`), water hazards, announcers, music, and progression unlocks. |
| `Camera/` | `ArcadeChaseCamera.cs` | Near/Far chase and bumper views (`C` key), velocity-trail drift angle, steep downhill pitch elevation, terrain anti-clipping, and post-event/water-sinking/totaled 3/4 orbit zoom-out. |
| `Editor/` | `MM2StandaloneBuilder.cs` | CLI/menu standalone build pipeline (`OpenMadness/Build/`) and `ShaderKeepAlive` generator to prevent URP shader stripping. |

---

## 3. Binary Formats & Virtual File System Gotchas

### `.ar` Archive Containers (`MM2ArchiveSystem`)
`MM2ArchiveSystem` detects container types by header magic rather than filename:
1. **`DAVE` (`0x45564144` / `DAVE`):** Standard MM2 archive (`0x800` header, 16-byte MFT entries, null-terminated string table). When `real_size != ar_size`, decompress using raw Deflate (`DeflateStream`, equivalent to `zlib` window bits `-15`).
2. **`ARES` (`0x53455241` / `ARES`):** Standard MM1 archive (`ui.ar`, `core.ar`, `audio.ar`). Uses a 16-byte header, 12-byte bitpacked hierarchical directory tree entries, uncompressed 16-byte aligned payloads, and `\x01` integer token substitution in the string table.
3. **Standard `PKZIP` (`PK\x03\x04`):** Used by most community `.ar` vehicle and city mods.
4. **`AR-locked PKZIP` (`AR\x00\x00` or zeroed local headers with a valid `PK\x05\x06` End of Central Directory):** Used by protected community mods. Parse directly from the Central Directory at the end of the file and calculate payload start past the 30-byte Local File Header.

### `.tex` & `.dds` Textures (`MM2RuntimeDecoders`)
- **`.tex` Header is Strictly 14 Bytes:** `uint16 width, height, format, mipCount, pad; uint32 flags`. Do **not** read 16 or 28 bytes before the palette—doing so shifts palette indices and corrupts colors.
- **BGRA Palette & Bottom-Up Scanlines:** Paletted `.tex` formats (`1` P8, `2` P8A8, `14` PA8, `15` P4, `16` PA4) store 4-byte palette entries in **BGRA** order (`B, G, R, A`), and image rows are stored bottom-to-top.
- **MM1 `.dds` Textures:** MM1 `.dds` files are *not* DXT1/DXT5 compressed; they are uncompressed 16-bit (`ARGB4444`, `RGB565`, `ARGB1555`), 24-bit, or 32-bit textures, or 8-bit paletted textures paired with external `.lut` lookup tables.
- **Texture Wrap Modes & Alpha Bleeding:**
  - **City `.tex` textures** must force `TextureWrapMode.Repeat` even when header `flags & 0x2` is set. Clamping city textures causes stretched "barcode" stripes on PSDL building walls and roads that tile UVs outside `[0, 1]`.
  - **UI textures (`LoadUiTexture`), foliage (`treesmall*`), and light glow sprites** must use `TextureWrapMode.Clamp` and bleed opaque neighbor RGB colors into `alpha == 0` pixels so bilinear filtering and mipmaps do not produce dark fringes or top/bottom border lines.

### `.pkg` (`PKG3`) & `.bms` (`3HSM`) 3D Models (`MM2RuntimePkgLoader`)
- **Model-Type Precedence (`PKG3` over `3HSM`):** When a vehicle basename exists in both MM2 `PKG3` (`.pkg`) and MM1 `3HSM` (`.bms`) formats (e.g., `vpbug`, `vpmustang99`, `vpcop`, `vpbus`, `vpsemi`), **always prioritize `PKG3` (`.pkg`) based on model format** (`HasMm2PkgModel` over `HasMm1BmsModel`), regardless of archive filename.
- **Triangle Lists, Not Strips:** Despite the field name `nStrips` in `PKG3` documentation, geometry index buffers are standard **Triangle Lists** (`3` indices per face).
- **Shader Count Header & City Compact Shaders:**
  - In `.pkg` `shaders` chunks, the first `uint32` is a header flag (`132`/`133`) and the second `uint32` is `nShadersPerPaintjob`.
  - In city `.pkg` props (`nPaintjobs & 0x80` set), shader entries use a compact **16-byte** tail after the null-terminated texture name instead of the 68-byte vehicle shader layout.
- **Untextured Diffuse-Color Shaders:** Some `.pkg` models (such as San Francisco's ferry `sp_carferry01_f.pkg`) have empty texture names (`""`) and rely on the shader's `DiffuseColor` RGB values. Synthesize `__color_RRGGBB` solid-color materials rather than skipping sections with empty texture names.
- **Left/Right Split Damage Sections:** Several vehicles (`vpbullet`, `vpdb7`) split `BODY_H` into left and right halves where one half references `*_dmg` shader slots by default. Remap `*_dmg` shader indices to their clean counterparts during initial load and swap them dynamically in `MM2VehicleDamage` only when that zone takes damage.
- **Dynamic Light Chunks (`HLIGHT_*`, `TLIGHT_*`, `BLIGHT_*`, `RLIGHT_*`):** Never merge `HLIGHT_H`/`_L` or `TLIGHT_H`/`_L` into static `Body_Mesh` (many mod cars use flat white billboard quads for `HLIGHT_H`). Build them as separate child GameObjects under `VehicleLights` with **Transparent Additive** (`SrcAlpha One`, `ZWrite Off`) materials.

---

## 4. Coordinate Systems, Handedness & Rotation Conventions

The original Angel Game Engine (AGE) is **Right-Handed** (`-Z` forward, `+Y` up, `-X` left / `+X` right). Unity is **Left-Handed** (`+Z` forward, `+Y` up, `-X` left / `+X` right).

### Runtime Vehicles (`.pkg`, `.bms`, `.mtx`)
- **Vertices:** `(x, y, -z)`
- **Normals:** `(nx, ny, -nz)`
- **Triangle Winding:** Reverse `(i0, i2, i1)`
- **Wheel `.mtx` Pivot Offsets:** `(pos.x, pos.y, -pos.z)`
> **WARNING:** Do **not** negate `X` (`(-x, y, -z)`) when building vehicle `Mesh` objects in memory at runtime! Negating both `X` and `Z` was only used in legacy offline `.obj` exporters because Unity's `.obj` file importer automatically negates `X` a second time. Negating both `X` and `Z` in runtime `Mesh` construction mirrors the vehicle left-to-right (putting the steering wheel on the wrong side and flipping asymmetric textures).

### Runtime Cities, Props, AI Roads & Waypoints (`.psdl`, `.inst`, `.pathset`, `.bai`, `waypoints.csv`)
- **World Positions:** Convert AGE `(x, y, z)` to Unity `(-x, y, z)`.
- **3x3 Transform Matrices (`.inst`, `.bng`, `.gizmo`, `.opp`):** Convert a right-handed basis `(right, up, back)` into Unity by mapping `unityRight = (-r.x, r.y, r.z)`, `unityUp = (-u.x, u.y, u.z)`, and `unityForward = (b.x, -b.y, -b.z)` (since AGE forward is `-back`).
- **Yaw Angles Across File Types:**
  - **`waypoints.csv` (Column 3 `a`):** Stores `-yawDeg` in AGE space -> Unity rotation is `Quaternion.Euler(0f, a + 180f, 0f)`.
  - **`.opp` Opponent Files (Row 0, Column 3), `.aimap` `[Police]`, and Cruise `spawn_*.txt`:** Store `+yawDeg` -> Unity rotation is `Quaternion.Euler(0f, 180f - col3, 0f)`.
  - **Checkpoint Arches (`pt_check.pkg`):** Arches are symmetric and frequently authored 180° reversed relative to street direction in `waypoints.csv`. When snapping checkpoints to `.bai` lanes in `MM2AiMapData.BuildRaceRouteThroughAnchors`, treat gate forward vectors as **unsigned** (`Mathf.Abs(Vector3.Dot(laneDir, gateFwd)) >= 0.35f`).

---

## 5. City Loading, Water, Bridges & Bangers

### MM2 (`.psdl`) vs. MM1 (`.ptl` / `.cells` / `.fcd`) Cities
- **MM2 Cities:** Built from `city/<map>.psdl` (rooms, road strips, crosswalks, tunnels, facade slivers), `city/<map>.inst` (static & breakable prop placements), and `city/<map>*.pathset` (extra props such as Crash Course ramps, ferries, slalom cars, and London's movable Tower Bridge spans `london_bridge.pathset`).
- **MM1 Cities (e.g., Chicago):** Built from `<map>.ptl` + `<map>.cells` + `cull*.bms`. Static cell geometry is split across `_h.bms`, `_h2.bms`, `_a.bms`, and `_a2.bms` across both `<map>city` and `<map>lm` archives. Facades (`.fcd`) require rendering `GRND_*` ground plazas alongside `BLDG_*`, `UPPER_*`, and `TOP_*` chunks so city blocks have solid ground beneath buildings.

### Water Planes & Deep Water vs. Driveable Ponds
- **Deep Water vs. Shallow Water:** Only `deepwater` textures (`s_ocean`, `s_thames`, `l3water`, and MM1's `t_water` at or below lake level `Y <= 0.05m` in `cull81_a2..cull95_a2.bms`) should have their `MeshCollider` stripped and trigger `"Sleep with the fishes!"` water sinking. Shallow `water` textures (`s_pond`, `s_water` used in Hyde Park Lake and Palace of Fine Arts lagoon, as well as elevated fountain basins like MM1's Buckingham Fountain `cull36_h.bms` at `Y = +0.15m`) must retain solid `MeshCollider`s so vehicles can splash and drive across them.
- **`.water` Sea Level Parsing:** `city/<map>.water` contains the sea-level elevation on line 0 followed by integer room indices on subsequent lines. Parse **only line 0**, and ignore sloped coastal boundary triangles (`maxY - minY > 1.2f`) when computing water elevation so the horizon water plane is never lifted into the sky (e.g., on mod maps like Manhattan).
- **Post-Race Water Physics:** Keep `CheckWaterHazard()` active even after `raceFinished` triggers so a car flying into the bay/river as time expires still floats/sinks instead of falling endlessly through the stripped ocean floor.

### Interactive Props ("Bangers" — `MM2BangerSystem`)
- **Dormant Trigger Until Impact:** Breakable props (`MM2BangerProp`) use a **trigger** `BoxCollider` (shifted by `spec.CG`) while dormant. When a vehicle hits the trigger, the prop either spawns `BREAK01..0N` shards or enables a solid `BoxCollider` + dynamic `Rigidbody` and applies impulse from the car's pre-impact velocity. This prevents streetlights, cones, trees, and mailboxes from stopping the player's car for 1 physics frame on impact.
- **Debris Chain-Reaction Prevention:** `MM2BangerProp.OnTriggerEnter` ignores collisions from other `MM2BangerProp` rigidbodies—only vehicles can knock down dormant props.
- **Trees & Poles Are Breakable:** Never classify trees, foliage, lampposts, traffic lights, or signs as `IsImmovableObstacle` even if their `.dgBangerData` mass/impulse threshold is high. Only massive structural obstacles (`sp_fwy*` freeway pillars, `lp_gantry*`, bridge spans, ferries, and solid buildings) remain immovable.
- **Procedural Sidewalk Props:** `MM2RuntimeCityLoader` populates sidewalk trees, streetlights, trash cans, benches, and meters along PSDL block perimeters using `tune/city/<map>/proprules.csv` and `propdefs.csv`, with intersection corner clearance (`>= 6m`), road triangle overlap rejection, and `Physics.Raycast` grounding.

### URP Transparency, Glass & Billboards
- **Breakable Glass & Smoke Particles in URP:** When configuring transparent materials (`s_glass.tex`, `kp_harrod_winbot_*`, or `Universal Render Pipeline/Particles/Unlit` smoke), explicitly set `_Surface = 1`, `SrcAlpha` / `OneMinusSrcAlpha`, `ZWrite = 0`, enable `_SURFACE_TYPE_TRANSPARENT`, and call `mat.SetShaderPassEnabled("DepthOnly", false)`. If `DepthOnly` is left enabled in URP, transparent quads write to the depth buffer and render as opaque rectangles.
- **Tree Billboard Normals:** Assign uniform upward vertex normals (`Vector3.up`) on `X`-shaped tree billboard meshes (`treesmall*`, `sp_tree*`, `lp_tree*`, `t_*`) so the two intersecting planes receive identical directional lighting without a harsh 90° shadow seam.

---

## 6. Vehicle Physics & Handling (`MM2CarSim` & `ArcadeVehicleController`)

- **Always Use `.vehcarsim` (or `.mmcarsim`) for Physics:** `.info` files contain display-only UI bar numbers (`Mass`, `TopSpeed`, `Horsepower`). Actual simulation parameters—`Mass`, `MomentOfInertia`, `Engine.MaxHorsePower`, `OptRPM`, `MaxRPM`, `Trans.Low`/`High`/`GearBias`, and `Tire.StaticFric`/`SlidingFric`—must be read from `tune/vehcarsim/<basename>.vehcarsim` (or MM1 `.mmcarsim`).
- **Gear Ratio Curve & 1st-Gear Rotational Inertia:**
  - `GearRatioFromMPH` uses `Engine.OptRPM` (not `MaxRPM`), and intermediate gears follow `exponent = i + (i * (steps - i) * GearBias) / steps`.
  - In `MM2Engine.UpdateRPM`, clamp `effectiveGearForInertia <= 7.2f` and scale drivetrain inertia by `Mathf.Clamp(carMass / 1400f, 0.75f, 1.35f)` so vehicles with short 1st gears (e.g., `vpcab` with `Low = 17.5`) accelerate briskly (`0–27 mph` in `~1.0s`) instead of bogging down on launch.
- **Steering Filter & Drift Physics:**
  - Keyboard/gamepad steering uses the discrete `.asNode` ramp filter (`SteerRate = 4.0/s` in, `ReturnRate = 4.8/s` center return, speed-sensitive max angle reduction) rather than `Input.GetAxis("Horizontal")` double-smoothing.
  - Full-lock steering at speed (`> 22 mph`) engages dynamic rear-end oversteer slip and spawns continuous `TireSmoke_RL`/`_RR` plumes (`fxpt8.tex`). Holding `Space` (handbrake) locks the rear wheels (`3.5x` yaw torque) for 90°/180° J-turns.
- **Curb-Forgiving Colliders & Building Wall Damage:**
  - Keep the vehicle's root `BoxCollider` bottom elevated (`>= 0.42m–0.48m`) with a zero-friction `PhysicsMaterial` (`Minimum` friction combine), `>= 0.26m` suspension travel, and forward wheel raycast probes (`StepCurbClimbAssist`) so cars glide over `15–25cm` sidewalk curbs without snagging.
  - Because roads, curbs, and building walls in a city block often share a single combined district `MeshCollider`, `MM2VehicleDamage` records `preStepVelocity` at the start of `FixedUpdate` and inspects all contact normals in both `OnCollisionEnter` and `OnCollisionStay` (filtering for horizontal wall normals `|normal.y| < 0.35f`) so crashing into a building while already touching the road reliably deals damage and plays impact audio.

---

## 7. Unity Lifecycle, Domain Reload & Build Best Practices

1. **Avoid Unity Fake-Null (`??` and `?.`) Pitfalls:**
   - Never use C#'s null-coalescing operator (`??`) on `UnityEngine.Object` methods such as `GetComponent<T>()` or `Shader.Find()` (e.g., `go.GetComponent<Light>() ?? go.AddComponent<Light>()` fails because a destroyed or missing Unity component returns a "fake-null" wrapper that is non-null in managed C#). Always use explicit `if (comp == null)` checks.
2. **Never Call Unity Constructors in Field Initializers:**
   - Never call `new Texture2D(...)`, `new Material(...)`, or `Resources.Load(...)` in field initializers on `MonoBehaviour` classes—Unity throws `UnityException` during serialization. Initialize them inside `Awake()` or `OnEnable()`.
3. **Domain Reload & Scene Transition Resilience:**
   - When switching between `MenuSystem.unity` and `GoDrive.unity` (or when Unity recompiles scripts during Play Mode), `Awake()`/`Start()` will not re-run on persistent objects.
   - Always subscribe to `SceneManager.sceneLoaded` and maintain self-healing `EnsureInitialized()` checks in `MM2GameplayHud`, `MM2BangerProp`, `MM2EnvironmentManager`, and `ArcadeVehicleController`.
4. **Menu Camera GPU Optimization:**
   - While in `MM2MenuController`, disable 3D scene rendering on `Main Camera` (unless the live 3D city preview is active), enable `TurntableCamera` **only** on `MenuScreen.SelectVehicle`, and keep a lightweight `MM2_MenuDisplay1Camera` (`cullingMask = UI`, `farClipPlane = 1f`) active on Display 1 so Unity's Game View never displays `"Display 1 - No cameras rendering"`.
5. **CLI Compilation Check & Standalone Builds:**
   - Before running CLI batchmode commands, check if an interactive Unity Editor instance is already open (`ps aux | grep Unity`)—Unity only allows one instance per project folder at a time.
   - To verify C# compilation or build standalone binaries from the command line:
     ```bash
     # Build Linux x86_64 and Windows x86_64 standalone binaries into Build/
     Unity -batchmode -nographics -quit -projectPath . -executeMethod MM2StandaloneBuilder.BuildAll -logFile /tmp/unity_build.log
     ```
