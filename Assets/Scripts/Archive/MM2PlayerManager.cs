using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using MM2.UI;
using MM2.Vehicle;

namespace MM2.Archive
{
    [Serializable]
    public class MM2PlayerDirEntry
    {
        public string PlayerName = "DriverX";
        public string FileTag = "player0";
    }

    [Serializable]
    public class MM2PlayerProfileData
    {
        public int Version = 3;
        public float TotalScore = 0f;
        public byte SkillLevel = 0; // 0 = Amateur, 1 = Pro
        public string VehicleBaseName = "vpbug";
        public int PaintjobIndex = 0;
        public int LastGameMode = 0; // 0 = Cruise, 1 = Blitz, 3 = Circuit, 4 = Checkpoint, 6 = CrashCourse
        public int LastRaceId = 0;
        public string PlayerName = "DriverX";
        public string NetName = "DriverX";
        public string FileTag = "player0";
        public string LastCityName = "sf";
        public byte[] ExtraV3 = new byte[40];
    }

    [Serializable]
    public struct MM2CityRaceRecordEntry
    {
        public float BestTime;
        public string VehicleBaseName;
        public int Score;
        public int Passed;
    }

    [Serializable]
    public class MM2CityRecordsData
    {
        public float Version = 2.0f;
        public float PlayerScore = 0f;
        public int BlitzCount = 12;
        public int CircuitCount = 10;
        public int CheckpointCount = 10;
        public int CrashCourseCount = 13;
        public uint BlitzPassedMask = 0;
        public uint CircuitPassedMask = 0;
        public uint CheckpointPassedMask = 0;
        public uint CrashCoursePassedMask = 0;
        public MM2CityRaceRecordEntry[] Records = new MM2CityRaceRecordEntry[45];
    }

    [Serializable]
    public struct MM2HallOfFameEntry
    {
        public string PlayerName;
        public string VehicleBaseName;
        public float Time;
        public int Score;
    }

    public struct MM2DriverStatRow
    {
        public int EventIndex;
        public string EventName;
        public int Status; // 0 = Not passed / Locked, 1 = Passed (Tick), 2 = Failed (Cross)
        public string TimeString;
        public string VehicleDisplayName;
    }

    public struct MM2RaceRecordRow
    {
        public int RaceIndex;
        public int RankInRace; // 1..5
        public string RaceName;
        public string DriverName;
        public string TimeOrScoreString;
        public string VehicleDisplayName;
    }

    /// <summary>
    /// Reads and writes binary player profiles and race records in <resources>/players/:
    /// - players.dir (player directory)
    /// - playerN.sav (player profile data)
    /// - <city>/playerN.rec (player city records)
    /// - <city>/amateur.dat & <city>/pro.dat (Hall of Fame leaderboards)
    /// Uses the custom CRC-32 lookup table and field hash order required by the binary save format.
    /// </summary>
    public static class MM2PlayerManager
    {
        // Custom 256-entry CRC-32 lookup table (poly 0xEDB8B23B)
        private static readonly uint[] CrcTable =
        {
            0x00000000u, 0x7763A455u, 0xEEC748AAu, 0x99A4ECFFu, 0x076B9415u, 0x70083040u, 0xE9ACDCBFu, 0x9ECF78EAu,
            0x0ED7282Au, 0x79B48C7Fu, 0xE0106080u, 0x9773C4D5u, 0x09BCBC3Fu, 0x7EDF186Au, 0xE77BF495u, 0x901850C0u,
            0x1DAE5054u, 0x6ACDF401u, 0xF36918FEu, 0x840ABCABu, 0x1AC5C441u, 0x6DA66014u, 0xF4028CEBu, 0x836128BEu,
            0x1379787Eu, 0x641ADC2Bu, 0xFDBE30D4u, 0x8ADD9481u, 0x1412EC6Bu, 0x6371483Eu, 0xFAD5A4C1u, 0x8DB60094u,
            0x3B5CA0A8u, 0x4C3F04FDu, 0xD59BE802u, 0xA2F84C57u, 0x3C3734BDu, 0x4B5490E8u, 0xD2F07C17u, 0xA593D842u,
            0x358B8882u, 0x42E82CD7u, 0xDB4CC028u, 0xAC2F647Du, 0x32E01C97u, 0x4583B8C2u, 0xDC27543Du, 0xAB44F068u,
            0x26F2F0FCu, 0x519154A9u, 0xC835B856u, 0xBF561C03u, 0x219964E9u, 0x56FAC0BCu, 0xCF5E2C43u, 0xB83D8816u,
            0x2825D8D6u, 0x5F467C83u, 0xC6E2907Cu, 0xB1813429u, 0x2F4E4CC3u, 0x582DE896u, 0xC1890469u, 0xB6EAA03Cu,
            0x76B94150u, 0x01DAE505u, 0x987E09FAu, 0xEF1DADAFu, 0x71D2D545u, 0x06B17110u, 0x9F159DEFu, 0xE87639BAu,
            0x786E697Au, 0x0F0DCD2Fu, 0x96A921D0u, 0xE1CA8585u, 0x7F05FD6Fu, 0x0866593Au, 0x91C2B5C5u, 0xE6A11190u,
            0x6B171104u, 0x1C74B551u, 0x85D059AEu, 0xF2B3FDFBu, 0x6C7C8511u, 0x1B1F2144u, 0x82BBCDBBu, 0xF5D869EEu,
            0x65C0392Eu, 0x12A39D7Bu, 0x8B077184u, 0xFC64D5D1u, 0x62ABAD3Bu, 0x15C8096Eu, 0x8C6CE591u, 0xFB0F41C4u,
            0x4DE5E1F8u, 0x3A8645ADu, 0xA322A952u, 0xD4410D07u, 0x4A8E75EDu, 0x3DEDD1B8u, 0xA4493D47u, 0xD32A9912u,
            0x4332C9D2u, 0x34516D87u, 0xADF58178u, 0xDA96252Du, 0x44595DC7u, 0x333AF992u, 0xAA9E156Du, 0xDDFDB138u,
            0x504BB1ACu, 0x272815F9u, 0xBE8CF906u, 0xC9EF5D53u, 0x572025B9u, 0x204381ECu, 0xB9E76D13u, 0xCE84C946u,
            0x5E9C9986u, 0x29FF3DD3u, 0xB05BD12Cu, 0xC7387579u, 0x59F70D93u, 0x2E94A9C6u, 0xB7304539u, 0xC053E16Cu,
            0xED7282A0u, 0x9A1126F5u, 0x03B5CA0Au, 0x74D66E5Fu, 0xEA1916B5u, 0x9D7AB2E0u, 0x04DE5E1Fu, 0x73BDFA4Au,
            0xE3A5AA8Au, 0x94C60EDFu, 0x0D62E220u, 0x7A014675u, 0xE4CE3E9Fu, 0x93AD9ACAu, 0x0A097635u, 0x7D6AD260u,
            0xF0DCD2F4u, 0x87BF76A1u, 0x1E1B9A5Eu, 0x69783E0Bu, 0xF7B746E1u, 0x80D4E2B4u, 0x19700E4Bu, 0x6E13AA1Eu,
            0xFE0BFADEu, 0x89685E8Bu, 0x10CCB274u, 0x67AF1621u, 0xF9606ECBu, 0x8E03CA9Eu, 0x17A72661u, 0x60C48234u,
            0xD62E2208u, 0xA14D865Du, 0x38E96AA2u, 0x4F8ACEF7u, 0xD145B61Du, 0xA6261248u, 0x3F82FEB7u, 0x48E15AE2u,
            0xD8F90A22u, 0xAF9AAE77u, 0x363E4288u, 0x415DE6DDu, 0xDF929E37u, 0xA8F13A62u, 0x3155D69Du, 0x463672C8u,
            0xCB80725Cu, 0xBCE3D609u, 0x25473AF6u, 0x52249EA3u, 0xCCEBE649u, 0xBB88421Cu, 0x222CAEE3u, 0x554F0AB6u,
            0xC5575A76u, 0xB234FE23u, 0x2B9012DCu, 0x5CF3B689u, 0xC23CCE63u, 0xB55F6A36u, 0x2CFB86C9u, 0x5B98229Cu,
            0x9BCBC3F0u, 0xECA867A5u, 0x750C8B5Au, 0x026F2F0Fu, 0x9CA057E5u, 0xEBC3F3B0u, 0x72671F4Fu, 0x0504BB1Au,
            0x951CEBDAu, 0xE27F4F8Fu, 0x7BDBA370u, 0x0CB80725u, 0x92777FCFu, 0xE514DB9Au, 0x7CB03765u, 0x0BD39330u,
            0x866593A4u, 0xF10637F1u, 0x68A2DB0Eu, 0x1FC17F5Bu, 0x810E07B1u, 0xF66DA3E4u, 0x6FC94F1Bu, 0x18AAEB4Eu,
            0x88B2BB8Eu, 0xFFD11FDBu, 0x6675F324u, 0x11165771u, 0x8FD92F9Bu, 0xF8BA8BCEu, 0x611E6731u, 0x167DC364u,
            0xA0976358u, 0xD7F4C70Du, 0x4E502BF2u, 0x39338FA7u, 0xA7FCF74Du, 0xD09F5318u, 0x493BBFE7u, 0x3E581BB2u,
            0xAE404B72u, 0xD923EF27u, 0x408703D8u, 0x37E4A78Du, 0xA92BDF67u, 0xDE487B32u, 0x47EC97CDu, 0x308F3398u,
            0xBD39330Cu, 0xCA5A9759u, 0x53FE7BA6u, 0x249DDFF3u, 0xBA52A719u, 0xCD31034Cu, 0x5495EFB3u, 0x23F64BE6u,
            0xB3EE1B26u, 0xC48DBF73u, 0x5D29538Cu, 0x2A4AF7D9u, 0xB4858F33u, 0xC3E62B66u, 0x5A42C799u, 0x2D2163CCu
        };

        private static bool _initialized;
        private static bool _isInitializing;
        private static readonly List<MM2PlayerDirEntry> _directoryEntries = new List<MM2PlayerDirEntry>();
        private static string _lastActivePlayerName = "DriverX";
        private static int _activePlayerIndex = 0;
        private static MM2PlayerProfileData _activeProfile;
        private static readonly Dictionary<string, MM2CityRecordsData> _activeCityRecords =
            new Dictionary<string, MM2CityRecordsData>(StringComparer.OrdinalIgnoreCase);

        public static string GetPlayersFolderPath()
        {
            string resRoot = MM2ArchiveSystem.ResourcesFolderPath;
            return Path.Combine(resRoot, "players");
        }

        public static void EnsureInitialized()
        {
            if (_isInitializing) return;
            if (_initialized && _activeProfile != null && _directoryEntries.Count > 0)
            {
                return;
            }
            ReloadFromDisk(syncGameSession: true);
        }

        public static void ReloadFromDisk(bool syncGameSession = true)
        {
            if (_isInitializing) return;
            _isInitializing = true;
            try
            {
                _initialized = true;
                _directoryEntries.Clear();
                _activeCityRecords.Clear();

                string playersDir = GetPlayersFolderPath();
                if (!Directory.Exists(playersDir))
                {
                    Directory.CreateDirectory(playersDir);
                }

                string dirFilePath = Path.Combine(playersDir, "players.dir");
                if (File.Exists(dirFilePath))
                {
                    LoadPlayersDirectoryFile(dirFilePath);
                }

                // Also auto-discover any playerN.sav files in playersDir that were dropped in (e.g. player1.sav)
                bool dirModified = AutoDiscoverSavFiles(playersDir);

                if (_directoryEntries.Count == 0)
                {
                    var defaultEntry = new MM2PlayerDirEntry
                    {
                        PlayerName = "DriverX",
                        FileTag = "player0"
                    };
                    _directoryEntries.Add(defaultEntry);
                    _lastActivePlayerName = defaultEntry.PlayerName;

                    var defaultProf = CreateDefaultProfile(defaultEntry.PlayerName, defaultEntry.FileTag);
                    SaveProfileToDisk(defaultProf);
                    EnsureCityRecordsExist(defaultProf);
                    dirModified = true;
                }

                // Find active player index matching _lastActivePlayerName
                int idx = _directoryEntries.FindIndex(e =>
                    string.Equals(e.PlayerName, _lastActivePlayerName, StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    idx = 0;
                    _lastActivePlayerName = _directoryEntries[0].PlayerName;
                    dirModified = true;
                }

                if (dirModified)
                {
                    SavePlayersDirectoryFile(dirFilePath);
                }

                SelectPlayer(idx, syncGameSession: syncGameSession, saveDirectory: false);
            }
            finally
            {
                _isInitializing = false;
            }
        }

        public static string[] GetPlayerNames()
        {
            EnsureInitialized();
            string[] names = new string[_directoryEntries.Count];
            for (int i = 0; i < _directoryEntries.Count; i++)
            {
                names[i] = _directoryEntries[i].PlayerName;
            }
            return names;
        }

        public static int GetActivePlayerIndex()
        {
            EnsureInitialized();
            return Mathf.Clamp(_activePlayerIndex, 0, Mathf.Max(0, _directoryEntries.Count - 1));
        }

        public static MM2PlayerProfileData GetActiveProfile()
        {
            EnsureInitialized();
            return _activeProfile;
        }

        public static bool IsActiveProfilePro()
        {
            EnsureInitialized();
            return _activeProfile != null && _activeProfile.SkillLevel != 0;
        }

        public static MM2CityRecordsData GetCityRecords(string cityCode)
        {
            EnsureInitialized();
            cityCode = NormalizeCityCode(cityCode);
            if (_activeCityRecords.TryGetValue(cityCode, out var cached) && cached != null)
            {
                return cached;
            }

            if (_activeProfile == null)
            {
                return CreateDefaultCityRecords(cityCode, 0f);
            }

            string recPath = Path.Combine(GetPlayersFolderPath(), cityCode, _activeProfile.FileTag + ".rec");
            MM2CityRecordsData loaded = LoadCityRecordsFromDisk(recPath, _activeProfile.TotalScore);
            if (loaded == null)
            {
                loaded = CreateDefaultCityRecords(cityCode, _activeProfile.TotalScore);
                SaveCityRecordsToDisk(recPath, loaded);
            }

            _activeCityRecords[cityCode] = loaded;
            return loaded;
        }

        public static void SelectPlayer(int index, bool syncGameSession = true, bool saveDirectory = true)
        {
            EnsureInitialized();
            if (_directoryEntries.Count == 0) return;

            _activePlayerIndex = Mathf.Clamp(index, 0, _directoryEntries.Count - 1);
            MM2PlayerDirEntry entry = _directoryEntries[_activePlayerIndex];
            _lastActivePlayerName = entry.PlayerName;

            string savPath = Path.Combine(GetPlayersFolderPath(), entry.FileTag + ".sav");
            _activeProfile = LoadProfileFromDisk(savPath);
            if (_activeProfile == null)
            {
                _activeProfile = CreateDefaultProfile(entry.PlayerName, entry.FileTag);
                SaveProfileToDisk(_activeProfile);
            }
            else
            {
                // Keep entry name and fileTag authoritative
                if (string.IsNullOrEmpty(_activeProfile.FileTag))
                    _activeProfile.FileTag = entry.FileTag;
                if (string.IsNullOrEmpty(_activeProfile.PlayerName))
                    _activeProfile.PlayerName = entry.PlayerName;
                else
                    entry.PlayerName = _activeProfile.PlayerName;
            }

            _activeCityRecords.Clear();
            EnsureCityRecordsExist(_activeProfile);
            foreach (string cCode in GetAllCityCodes())
            {
                GetCityRecords(cCode);
            }

            if (saveDirectory)
            {
                string dirPath = Path.Combine(GetPlayersFolderPath(), "players.dir");
                SavePlayersDirectoryFile(dirPath);
            }

            if (syncGameSession)
            {
                ApplyActiveProfileToGameSession();
            }
        }

        public static MM2PlayerProfileData CreateNewPlayer(string customName = null, byte skillLevel = 0)
        {
            EnsureInitialized();
            string playersDir = GetPlayersFolderPath();

            // Pick next unused playerN tag
            int tagIndex = 0;
            string fileTag;
            while (true)
            {
                fileTag = $"player{tagIndex}";
                bool tagUsed = _directoryEntries.Exists(e =>
                    string.Equals(e.FileTag, fileTag, StringComparison.OrdinalIgnoreCase));
                string savCandidate = Path.Combine(playersDir, fileTag + ".sav");
                if (!tagUsed && !File.Exists(savCandidate))
                {
                    break;
                }
                tagIndex++;
            }

            string playerName = customName;
            if (string.IsNullOrWhiteSpace(playerName))
            {
                int nameNum = _directoryEntries.Count + 1;
                while (true)
                {
                    string candidate = $"Driver{nameNum}";
                    if (!_directoryEntries.Exists(e => string.Equals(e.PlayerName, candidate, StringComparison.OrdinalIgnoreCase)))
                    {
                        playerName = candidate;
                        break;
                    }
                    nameNum++;
                }
            }

            var newEntry = new MM2PlayerDirEntry
            {
                PlayerName = playerName,
                FileTag = fileTag
            };
            _directoryEntries.Add(newEntry);

            byte clampedSkill = (byte)(skillLevel != 0 ? 1 : 0);
            MM2PlayerProfileData newProf = CreateDefaultProfile(playerName, fileTag, clampedSkill);
            SaveProfileToDisk(newProf);
            EnsureCityRecordsExist(newProf);

            SelectPlayer(_directoryEntries.Count - 1, syncGameSession: true, saveDirectory: true);
            Debug.Log($"[MM2PlayerManager] Created new driver '{playerName}' ({fileTag}, SkillLevel={clampedSkill}).");
            return newProf;
        }

        public static bool DeleteActivePlayer()
        {
            EnsureInitialized();
            if (_directoryEntries.Count <= 1)
            {
                Debug.Log("[MM2PlayerManager] Cannot delete the last remaining driver.");
                return false;
            }

            int delIdx = GetActivePlayerIndex();
            MM2PlayerDirEntry delEntry = _directoryEntries[delIdx];
            string playersDir = GetPlayersFolderPath();

            try
            {
                string savPath = Path.Combine(playersDir, delEntry.FileTag + ".sav");
                if (File.Exists(savPath)) File.Delete(savPath);

                string cfgPath = Path.Combine(playersDir, delEntry.FileTag + ".cfg");
                if (File.Exists(cfgPath)) File.Delete(cfgPath);

                foreach (string city in GetAllCityCodes())
                {
                    string recPath = Path.Combine(playersDir, city, delEntry.FileTag + ".rec");
                    if (File.Exists(recPath)) File.Delete(recPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Error deleting files for {delEntry.FileTag}: {ex.Message}");
            }

            _directoryEntries.RemoveAt(delIdx);
            int nextIdx = Mathf.Clamp(delIdx, 0, _directoryEntries.Count - 1);
            SelectPlayer(nextIdx, syncGameSession: true, saveDirectory: true);
            Debug.Log($"[MM2PlayerManager] Deleted driver '{delEntry.PlayerName}' ({delEntry.FileTag}).");
            return true;
        }

        public static void ApplyActiveProfileToGameSession()
        {
            if (_activeProfile == null) return;

            MM2GameSession.DriverName = _activeProfile.PlayerName;

            // Resolve Location from discovered cities ("sf" -> "San Francisco", "london" -> "London", "as_manhattan" -> "New York City", etc.)
            string cityCode = NormalizeCityCode(_activeProfile.LastCityName);
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            MM2GameSession.Location = (citySpec != null && !string.IsNullOrEmpty(citySpec.DisplayName))
                ? citySpec.DisplayName
                : "San Francisco";
            MM2GameSession.CrashCourseCity = (citySpec != null && citySpec.HasCrashCourse) ? citySpec.MapName : "sf";

            // Mode & RaceIndex
            switch (_activeProfile.LastGameMode)
            {
                case 1:
                    MM2GameSession.RaceType = "Blitz";
                    MM2GameSession.RaceIndex = Mathf.Max(0, _activeProfile.LastRaceId);
                    break;
                case 3:
                    MM2GameSession.RaceType = "Circuit";
                    MM2GameSession.RaceIndex = Mathf.Max(0, _activeProfile.LastRaceId);
                    break;
                case 4:
                    MM2GameSession.RaceType = "Checkpoint";
                    MM2GameSession.RaceIndex = Mathf.Max(0, _activeProfile.LastRaceId);
                    if (!IsRaceUnlocked(cityCode, "Checkpoint", MM2GameSession.RaceIndex))
                    {
                        MM2GameSession.RaceIndex = 0;
                    }
                    break;
                case 6:
                    if (!MM2GameSession.IsCrashCourseSession)
                    {
                        MM2GameSession.RaceType = "Cruise";
                    }
                    MM2GameSession.CrashLessonIndex = Mathf.Clamp(_activeProfile.LastRaceId, 0, 12);
                    break;
                default:
                    MM2GameSession.RaceType = "Cruise";
                    MM2GameSession.RaceIndex = 0;
                    break;
            }

            // Vehicle & Paintjob Color (ensure unlocked fallback if saved vehicle/color is locked for this profile)
            VehicleSpec spec = VehicleCatalog.GetSpecByBaseName(_activeProfile.VehicleBaseName);
            if (spec == null || !IsVehicleUnlocked(spec.BaseName))
            {
                List<VehicleSpec> allSpecs = VehicleCatalog.GetAllSpecs();
                spec = null;
                if (allSpecs != null)
                {
                    for (int i = 0; i < allSpecs.Count; i++)
                    {
                        if (allSpecs[i] != null && IsVehicleUnlocked(allSpecs[i].BaseName))
                        {
                            spec = allSpecs[i];
                            break;
                        }
                    }
                    if (spec == null && allSpecs.Count > 0)
                    {
                        spec = allSpecs[0];
                    }
                }
            }
            if (spec != null)
            {
                MM2GameSession.VehicleName = spec.DisplayName;
                if (spec.Colors != null && spec.Colors.Length > 0)
                {
                    int colorIdx = Mathf.Clamp(_activeProfile.PaintjobIndex, 0, spec.Colors.Length - 1);
                    if (!IsVehicleColorUnlocked(spec.BaseName, colorIdx))
                    {
                        colorIdx = 0;
                        for (int c = 0; c < spec.Colors.Length; c++)
                        {
                            if (IsVehicleColorUnlocked(spec.BaseName, c))
                            {
                                colorIdx = c;
                                break;
                            }
                        }
                    }
                    MM2GameSession.VehicleColor = spec.Colors[colorIdx];
                }
            }
        }

        /// <summary>
        /// Saves the current MM2GameSession vehicle, paintjob, city, mode, and race index into the active player's .sav file.
        /// </summary>
        public static void SaveCurrentSelectionToActiveProfile()
        {
            EnsureInitialized();
            if (_activeProfile == null) return;

            SyncActiveProfileFieldsFromSession();
            SaveProfileToDisk(_activeProfile);
        }

        private static void SyncActiveProfileFieldsFromSession()
        {
            if (_activeProfile == null) return;

            _activeProfile.LastCityName = MM2GameSession.SelectedCityCode;

            VehicleSpec spec = VehicleCatalog.GetSpec(MM2GameSession.VehicleName);
            if (spec != null && !string.IsNullOrEmpty(spec.BaseName))
            {
                _activeProfile.VehicleBaseName = spec.BaseName;
                if (spec.Colors != null && spec.Colors.Length > 0)
                {
                    int cIdx = Array.FindIndex(spec.Colors, c =>
                        string.Equals(c, MM2GameSession.VehicleColor, StringComparison.OrdinalIgnoreCase));
                    _activeProfile.PaintjobIndex = cIdx >= 0 ? cIdx : 0;
                }
            }

            string mode = MM2GameSession.RaceType ?? "Cruise";
            if (string.Equals(mode, "Blitz", StringComparison.OrdinalIgnoreCase))
            {
                _activeProfile.LastGameMode = 1;
                _activeProfile.LastRaceId = Mathf.Max(0, MM2GameSession.RaceIndex);
            }
            else if (string.Equals(mode, "Circuit", StringComparison.OrdinalIgnoreCase))
            {
                _activeProfile.LastGameMode = 3;
                _activeProfile.LastRaceId = Mathf.Max(0, MM2GameSession.RaceIndex);
            }
            else if (string.Equals(mode, "Checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                _activeProfile.LastGameMode = 4;
                _activeProfile.LastRaceId = Mathf.Max(0, MM2GameSession.RaceIndex);
            }
            else if (string.Equals(mode, "CrashCourse", StringComparison.OrdinalIgnoreCase))
            {
                _activeProfile.LastGameMode = 6;
                _activeProfile.LastRaceId = Mathf.Clamp(MM2GameSession.CrashLessonIndex, 0, 12);
            }
            else
            {
                _activeProfile.LastGameMode = 0;
                _activeProfile.LastRaceId = 0;
            }
        }

        public static bool IsCrashLessonPassed(string cityCode, int lessonIndex)
        {
            if (lessonIndex < 0 || lessonIndex >= 32) return false;
            MM2CityRecordsData rec = GetCityRecords(cityCode);
            if (rec == null) return false;
            if ((rec.CrashCoursePassedMask & (1u << lessonIndex)) != 0) return true;
            int slot = GetRecordSlotIndex(rec, 6, lessonIndex);
            return slot >= 0 && slot < rec.Records.Length && rec.Records[slot].Passed != 0;
        }

        public static bool IsRacePassed(string cityCodeOrName, string modeName, int raceIndex)
        {
            EnsureInitialized();
            if (raceIndex < 0 || raceIndex >= 32) return false;

            string cityCode = NormalizeCityCode(cityCodeOrName);
            int mmMode = ModeNameToMmModeId(modeName);
            if (mmMode == 6)
            {
                return IsCrashLessonPassed(cityCode, raceIndex) ||
                       MM2GameSession.GetCrashLessonStatus(cityCode, raceIndex) == 1;
            }
            if (mmMode == 0) return true;

            MM2CityRecordsData rec = GetCityRecords(cityCode);
            if (rec == null) return false;

            uint mask = mmMode == 1 ? rec.BlitzPassedMask
                      : mmMode == 3 ? rec.CircuitPassedMask
                      : rec.CheckpointPassedMask;
            if ((mask & (1u << raceIndex)) != 0) return true;

            int slot = GetRecordSlotIndex(rec, mmMode, raceIndex);
            return slot >= 0 && slot < rec.Records.Length && rec.Records[slot].Passed != 0;
        }

        public static int GetPassedRaceCount(string cityCodeOrName, string modeName, int totalRaces)
        {
            int count = 0;
            int limit = Mathf.Clamp(totalRaces, 0, 32);
            for (int i = 0; i < limit; i++)
            {
                if (IsRacePassed(cityCodeOrName, modeName, i))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Evaluates whether the specified race or lesson is unlocked for the active player profile:
        /// - Cruise, Blitz, and Circuit races are always unlocked.
        /// - Checkpoint races unlock in groups of CitySpec.UnlockGroup (default 3):
        ///   Group 0 (0..UnlockGroup-1) is always unlocked; Group g > 0 unlocks when all races in Group g-1 are passed.
        /// - Crash Course lessons follow MM2GameSession.IsCrashLessonUnlocked.
        /// </summary>
        public static bool IsRaceUnlocked(string cityCodeOrName, string modeName, int raceIndex)
        {
            EnsureInitialized();
            if (raceIndex < 0) return false;

            string cityCode = NormalizeCityCode(cityCodeOrName);
            int mmMode = ModeNameToMmModeId(modeName);
            if (mmMode == 6)
            {
                return MM2GameSession.IsCrashLessonUnlocked(cityCode, raceIndex);
            }
            if (mmMode != 4) // Cruise (0), Blitz (1), Circuit (3) are always unlocked
            {
                return true;
            }

            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            int groupSize = (spec != null && spec.UnlockGroup > 0) ? spec.UnlockGroup : 3;
            int groupIdx = raceIndex / groupSize;
            if (groupIdx <= 0)
            {
                return true;
            }

            // Every Checkpoint race in the preceding group (and any earlier group) must be passed
            int requiredEnd = groupIdx * groupSize;
            for (int r = 0; r < requiredEnd; r++)
            {
                if (!IsRacePassed(cityCode, "Checkpoint", r))
                {
                    return false;
                }
            }
            return true;
        }

        public static int GetNextUnlockedRaceIndex(string cityCodeOrName, string modeName, int currentRaceIndex, int totalRacesOverride = -1)
        {
            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(cityCodeOrName);
            string cityDisplay = (spec != null && !string.IsNullOrEmpty(spec.DisplayName)) ? spec.DisplayName : cityCodeOrName;
            string[] raceNames = MM2RuntimeCityLoader.GetRaceNames(cityDisplay, modeName);
            int total = totalRacesOverride >= 0 ? totalRacesOverride : (raceNames != null ? raceNames.Length : 0);
            if (total <= 0) return -1;

            // 1. Advance to the next unlocked race after currentRaceIndex
            for (int i = currentRaceIndex + 1; i < total; i++)
            {
                if (IsRaceUnlocked(cityCodeOrName, modeName, i))
                {
                    return i;
                }
            }

            // 2. Wrap around to the first unlocked race before currentRaceIndex
            for (int i = 0; i <= currentRaceIndex && i < total; i++)
            {
                if (IsRaceUnlocked(cityCodeOrName, modeName, i))
                {
                    return i;
                }
            }

            return 0;
        }

        public static bool IsRewardConditionMet(MM2CityRewardSpec reward)
        {
            if (reward == null) return true;
            EnsureInitialized();

            string cityCode = NormalizeCityCode(reward.CityMapName);
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            MM2CityRecordsData rec = GetCityRecords(cityCode);

            string rType = reward.RaceType ?? string.Empty;
            if (string.Equals(rType, "crash", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rType, "crashcourse", StringComparison.OrdinalIgnoreCase))
            {
                int totalLessons = (citySpec != null && citySpec.CrashLessons != null && citySpec.CrashLessons.Length > 0)
                    ? citySpec.CrashLessons.Length
                    : (rec != null && rec.CrashCourseCount > 0 ? rec.CrashCourseCount : 13);

                if (reward.IsHalf)
                {
                    int req = Mathf.Max(1, totalLessons / 2);
                    return GetPassedRaceCount(cityCode, "CrashCourse", totalLessons) >= req;
                }
                if (reward.IsAll)
                {
                    return totalLessons > 0 && GetPassedRaceCount(cityCode, "CrashCourse", totalLessons) >= totalLessons;
                }
                if (reward.SpecificRaceIndex >= 0)
                {
                    return IsRacePassed(cityCode, "CrashCourse", reward.SpecificRaceIndex);
                }
                return false;
            }

            string modeName = "Blitz";
            int totalRaces = 10;
            if (string.Equals(rType, "blitz", StringComparison.OrdinalIgnoreCase))
            {
                modeName = "Blitz";
                totalRaces = (citySpec != null && citySpec.BlitzNames != null && citySpec.BlitzNames.Length > 0)
                    ? citySpec.BlitzNames.Length
                    : (rec != null && rec.BlitzCount > 0 ? rec.BlitzCount : 10);
            }
            else if (string.Equals(rType, "circuit", StringComparison.OrdinalIgnoreCase))
            {
                modeName = "Circuit";
                totalRaces = (citySpec != null && citySpec.CircuitNames != null && citySpec.CircuitNames.Length > 0)
                    ? citySpec.CircuitNames.Length
                    : (rec != null && rec.CircuitCount > 0 ? rec.CircuitCount : 10);
            }
            else if (string.Equals(rType, "race", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(rType, "checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                modeName = "Checkpoint";
                totalRaces = (citySpec != null && citySpec.CheckpointNames != null && citySpec.CheckpointNames.Length > 0)
                    ? citySpec.CheckpointNames.Length
                    : (rec != null && rec.CheckpointCount > 0 ? rec.CheckpointCount : 12);
            }

            if (reward.IsHalf)
            {
                int req = Mathf.Max(1, totalRaces / 2);
                return totalRaces > 0 && GetPassedRaceCount(cityCode, modeName, totalRaces) >= req;
            }
            if (reward.IsAll)
            {
                return totalRaces > 0 && GetPassedRaceCount(cityCode, modeName, totalRaces) >= totalRaces;
            }
            if (reward.SpecificRaceIndex >= 0)
            {
                return IsRacePassed(cityCode, modeName, reward.SpecificRaceIndex);
            }
            return false;
        }

        /// <summary>
        /// Evaluates MM1 UnlockFlags and UnlockScore criteria across all discovered cities.
        /// Used when the MM1 version of a vehicle is loaded (or when a mod vehicle specifies UnlockFlags/UnlockScore in .info).
        /// </summary>
        private static bool EvaluateMM1UnlockCriteria(VehicleSpec spec)
        {
            if (spec == null) return true;
            int flags = spec.UnlockFlags;
            int reqScore = spec.UnlockScore;
            if (flags == 0 && reqScore <= 0) return true;

            int totalScore = Mathf.Max(
                Mathf.RoundToInt(_activeProfile != null ? _activeProfile.TotalScore : 0f),
                ComputeTotalScoreAllCities()
            );
            if (reqScore > 0 && totalScore >= reqScore)
            {
                return true;
            }

            IReadOnlyList<CitySpec> allCities = MM2RuntimeCityLoader.GetAllCities();
            int totalPassedAllCities = 0;

            if (allCities != null)
            {
                for (int i = 0; i < allCities.Count; i++)
                {
                    CitySpec city = allCities[i];
                    if (city == null || string.IsNullOrEmpty(city.MapName)) continue;

                    MM2CityRecordsData rec = GetCityRecords(city.MapName);
                    int bTotal = (city.BlitzNames != null && city.BlitzNames.Length > 0) ? city.BlitzNames.Length : (rec != null ? rec.BlitzCount : 10);
                    int cTotal = (city.CircuitNames != null && city.CircuitNames.Length > 0) ? city.CircuitNames.Length : (rec != null ? rec.CircuitCount : 10);
                    int kTotal = (city.CheckpointNames != null && city.CheckpointNames.Length > 0) ? city.CheckpointNames.Length : (rec != null ? rec.CheckpointCount : 12);

                    int bPass = GetPassedRaceCount(city.MapName, "Blitz", bTotal);
                    int cPass = GetPassedRaceCount(city.MapName, "Circuit", cTotal);
                    int kPass = GetPassedRaceCount(city.MapName, "Checkpoint", kTotal);
                    totalPassedAllCities += (bPass + cPass + kPass);

                    if ((flags & 1) != 0 && bTotal > 0 && bPass >= Mathf.Max(1, bTotal / 2)) return true;
                    if ((flags & 2) != 0 && cTotal > 0 && cPass >= Mathf.Max(1, cTotal / 2)) return true;
                    if ((flags & 4) != 0 && kTotal > 0 && kPass >= Mathf.Max(1, kTotal / 2)) return true;
                    if ((flags & 8) != 0 && ((bTotal > 0 && bPass >= bTotal) || (cTotal > 0 && cPass >= cTotal) || (kTotal > 0 && kPass >= kTotal))) return true;
                    if ((flags & 16) != 0 && kTotal > 0 && kPass >= kTotal) return true;
                }
            }

            if ((flags & 32) != 0 && totalPassedAllCities >= 2)
            {
                return true;
            }

            return false;
        }

        public static bool IsVehicleUnlocked(string vehicleNameOrBase)
        {
            EnsureInitialized();
            VehicleSpec spec = VehicleCatalog.GetSpec(vehicleNameOrBase);
            if (spec == null || string.IsNullOrEmpty(spec.BaseName)) return true;

            IReadOnlyList<MM2CityRewardSpec> allRewards = MM2RuntimeCityLoader.GetAllCityRewards();
            bool hasVehicleRewardRule = false;
            if (allRewards != null)
            {
                for (int i = 0; i < allRewards.Count; i++)
                {
                    MM2CityRewardSpec r = allRewards[i];
                    if (r != null && r.VariantNum == 0 &&
                        string.Equals(r.CarBaseName, spec.BaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        hasVehicleRewardRule = true;
                        if (IsRewardConditionMet(r))
                        {
                            return true;
                        }
                    }
                }
            }

            // 1. If the MM1 version of the vehicle is loaded, evaluate MM1 UnlockFlags / UnlockScore
            if (spec.IsMM1Vehicle)
            {
                if (spec.UnlockFlags > 0 || spec.UnlockScore > 0)
                {
                    return EvaluateMM1UnlockCriteria(spec);
                }
                return !hasVehicleRewardRule;
            }

            // 2. If the MM2 version of the vehicle is loaded:
            //    - If any <city>_rewards.csv defines VariantNum == 0 for this car, it unlocks via that reward rule.
            if (hasVehicleRewardRule)
            {
                return false;
            }

            //    - If a modded car (.info from a mod PKZIP archive) defines UnlockFlags or UnlockScore without a rewards.csv entry, honor it.
            if (spec.InfoArchiveType == 2 && (spec.UnlockFlags > 0 || spec.UnlockScore > 0))
            {
                return EvaluateMM1UnlockCriteria(spec);
            }

            return true;
        }

        public static bool IsVehicleColorUnlocked(string vehicleNameOrBase, int colorIndex)
        {
            EnsureInitialized();
            if (colorIndex <= 0) return true;

            VehicleSpec spec = VehicleCatalog.GetSpec(vehicleNameOrBase);
            if (spec == null || string.IsNullOrEmpty(spec.BaseName)) return true;

            IReadOnlyList<MM2CityRewardSpec> allRewards = MM2RuntimeCityLoader.GetAllCityRewards();
            bool hasColorRewardRule = false;
            if (allRewards != null)
            {
                for (int i = 0; i < allRewards.Count; i++)
                {
                    MM2CityRewardSpec r = allRewards[i];
                    if (r != null && r.VariantNum == colorIndex &&
                        string.Equals(r.CarBaseName, spec.BaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        hasColorRewardRule = true;
                        if (IsRewardConditionMet(r))
                        {
                            return true;
                        }
                    }
                }
            }

            if (hasColorRewardRule)
            {
                return false;
            }

            // If a modded vehicle specifies LockColorMask in its .info without a rewards.csv row
            if (spec.InfoArchiveType == 2 && (spec.LockColorMask & (1 << colorIndex)) != 0)
            {
                return EvaluateMM1UnlockCriteria(new VehicleSpec { UnlockFlags = 1 | 2 | 4 });
            }

            return true;
        }

        public static bool IsVehicleAndColorUnlocked(string vehicleNameOrBase, int colorIndex)
        {
            return IsVehicleUnlocked(vehicleNameOrBase) && IsVehicleColorUnlocked(vehicleNameOrBase, colorIndex);
        }

        private static string NumberToWord(int n)
        {
            switch (n)
            {
                case 1: return "one";
                case 2: return "two";
                case 3: return "three";
                case 4: return "four";
                case 5: return "five";
                case 6: return "six";
                case 7: return "seven";
                case 8: return "eight";
                case 9: return "nine";
                case 10: return "ten";
                case 11: return "eleven";
                case 12: return "twelve";
                default: return n.ToString();
            }
        }

        private static string FormatPlacePhrase(CitySpec citySpec, bool isPro)
        {
            if (isPro) return "finish first";
            int mustPlace = (citySpec != null && citySpec.MustPlace > 0) ? citySpec.MustPlace : 3;
            if (mustPlace <= 1) return "finish first";
            if (mustPlace == 2) return "place 1st or 2nd";
            if (mustPlace == 3) return "place 1st, 2nd, or 3rd";
            return $"place in the top {mustPlace}";
        }

        private static string FormatRewardRequirementDescription(MM2CityRewardSpec reward, bool isPro)
        {
            if (reward == null) return string.Empty;

            string cityCode = NormalizeCityCode(reward.CityMapName);
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            string cityDisplay = (citySpec != null && !string.IsNullOrEmpty(citySpec.DisplayName))
                ? citySpec.DisplayName
                : (!string.IsNullOrEmpty(reward.CityDisplayName) ? reward.CityDisplayName : cityCode);

            string targetNoun = reward.VariantNum > 0 ? "custom paint job" : "vehicle";
            string rType = reward.RaceType ?? string.Empty;

            if (string.Equals(rType, "crash", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rType, "crashcourse", StringComparison.OrdinalIgnoreCase))
            {
                if (reward.SpecificRaceIndex == 3)
                {
                    return $"To unlock this {targetNoun}, pass {cityDisplay} Crash Course Midterm Exam One";
                }
                if (reward.SpecificRaceIndex == 7)
                {
                    return $"To unlock this {targetNoun}, pass {cityDisplay} Crash Course Midterm Exam Two";
                }
                if (reward.SpecificRaceIndex == 11)
                {
                    return $"To unlock this {targetNoun}, pass {cityDisplay} Crash Course Midterm Exam Three";
                }
                if (reward.SpecificRaceIndex == 12)
                {
                    return $"To unlock this {targetNoun}, pass the {cityDisplay} Crash Course Final Exam";
                }
                if (reward.SpecificRaceIndex >= 0 && citySpec != null && citySpec.CrashLessons != null &&
                    reward.SpecificRaceIndex < citySpec.CrashLessons.Length)
                {
                    string lessonLabel = citySpec.CrashLessons[reward.SpecificRaceIndex].DropdownLabel;
                    return $"To unlock this {targetNoun}, pass {cityDisplay} Crash Course \"{lessonLabel}\"";
                }
                return $"To unlock this {targetNoun}, complete the {cityDisplay} Crash Course";
            }

            if (string.Equals(rType, "blitz", StringComparison.OrdinalIgnoreCase))
            {
                int total = (citySpec != null && citySpec.BlitzNames != null && citySpec.BlitzNames.Length > 0)
                    ? citySpec.BlitzNames.Length
                    : 10;
                if (reward.IsHalf)
                {
                    string cntWord = NumberToWord(Mathf.Max(1, total / 2));
                    return $"To unlock this {targetNoun}, win {cntWord} Blitz Races in {cityDisplay}";
                }
                if (reward.IsAll)
                {
                    return $"To unlock this {targetNoun}, win all {NumberToWord(total)} {cityDisplay} Blitz Races";
                }
                if (reward.SpecificRaceIndex >= 0)
                {
                    string rName = (citySpec != null && citySpec.BlitzNames != null && reward.SpecificRaceIndex < citySpec.BlitzNames.Length)
                        ? citySpec.BlitzNames[reward.SpecificRaceIndex]
                        : $"#{reward.SpecificRaceIndex + 1}";
                    return $"To unlock this {targetNoun}, win {cityDisplay} Blitz Race \"{rName}\"";
                }
            }

            string placePhrase = FormatPlacePhrase(citySpec, isPro);
            if (string.Equals(rType, "circuit", StringComparison.OrdinalIgnoreCase))
            {
                int total = (citySpec != null && citySpec.CircuitNames != null && citySpec.CircuitNames.Length > 0)
                    ? citySpec.CircuitNames.Length
                    : 10;
                if (reward.IsHalf)
                {
                    string cntWord = NumberToWord(Mathf.Max(1, total / 2));
                    return $"To unlock this {targetNoun}, {placePhrase} in {cntWord} {cityDisplay} Circuit Races";
                }
                if (reward.IsAll)
                {
                    return $"To unlock this {targetNoun}, {placePhrase} in all {NumberToWord(total)} {cityDisplay} Circuit Races";
                }
                if (reward.SpecificRaceIndex >= 0)
                {
                    string rName = (citySpec != null && citySpec.CircuitNames != null && reward.SpecificRaceIndex < citySpec.CircuitNames.Length)
                        ? citySpec.CircuitNames[reward.SpecificRaceIndex]
                        : $"#{reward.SpecificRaceIndex + 1}";
                    return $"To unlock this {targetNoun}, {placePhrase} in {cityDisplay} Circuit Race \"{rName}\"";
                }
            }

            if (string.Equals(rType, "race", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rType, "checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                int total = (citySpec != null && citySpec.CheckpointNames != null && citySpec.CheckpointNames.Length > 0)
                    ? citySpec.CheckpointNames.Length
                    : 12;
                if (reward.IsHalf)
                {
                    string cntWord = NumberToWord(Mathf.Max(1, total / 2));
                    return $"To unlock this {targetNoun}, {placePhrase} in {cntWord} {cityDisplay} Checkpoint Races";
                }
                if (reward.IsAll)
                {
                    return $"To unlock this {targetNoun}, {placePhrase} in all {NumberToWord(total)} {cityDisplay} Checkpoint Races";
                }
                if (reward.SpecificRaceIndex >= 0)
                {
                    string rName = (citySpec != null && citySpec.CheckpointNames != null && reward.SpecificRaceIndex < citySpec.CheckpointNames.Length)
                        ? citySpec.CheckpointNames[reward.SpecificRaceIndex]
                        : $"#{reward.SpecificRaceIndex + 1}";
                    return $"To unlock this {targetNoun}, {placePhrase} in {cityDisplay} Checkpoint Race \"{rName}\"";
                }
            }

            return $"To unlock this {targetNoun}, complete the required races in {cityDisplay}";
        }

        private static string FormatMM1UnlockRequirementDescription(VehicleSpec spec, bool isPro)
        {
            if (spec == null) return string.Empty;
            int flags = spec.UnlockFlags;
            int reqScore = spec.UnlockScore;

            CitySpec chicagoSpec = MM2RuntimeCityLoader.GetCitySpec("chicago");
            string cityLabel = (chicagoSpec != null && !string.IsNullOrEmpty(chicagoSpec.DisplayName))
                ? chicagoSpec.DisplayName
                : "a city";
            string placePhrase = FormatPlacePhrase(chicagoSpec, isPro);

            if ((flags & 32) != 0)
            {
                return "To unlock this vehicle, pass any two Blitz, Circuit, or Checkpoint Races";
            }
            if ((flags & 1) != 0)
            {
                return $"To unlock this vehicle, win at least half of the Blitz Races in {cityLabel}";
            }
            if ((flags & 2) != 0)
            {
                return $"To unlock this vehicle, {placePhrase} in at least half of the Circuit Races in {cityLabel}";
            }
            if ((flags & 4) != 0)
            {
                return $"To unlock this vehicle, {placePhrase} in at least half of the Checkpoint Races in {cityLabel}";
            }
            if ((flags & 8) != 0)
            {
                return $"To unlock this vehicle, {placePhrase} in all races of any race mode in {cityLabel}";
            }
            if (reqScore > 0 || (flags & 16) != 0)
            {
                int scoreTarget = reqScore > 0 ? reqScore : 8000;
                return $"To unlock this vehicle, accumulate {scoreTarget:N0} points or pass all Checkpoint Races in {cityLabel}";
            }
            return "To unlock this vehicle, complete more races";
        }

        /// <summary>
        /// Returns the bottom-left description text for the SELECT VEHICLE screen:
        /// - If the selected custom paint job is locked, returns its unlock requirement text.
        /// - Else if the selected vehicle itself is locked, returns its unlock requirement text.
        /// - Otherwise returns the vehicle's unlocked description.
        /// </summary>
        public static string GetVehicleOrColorLockDescription(string vehicleNameOrBase, int colorIndex)
        {
            EnsureInitialized();
            VehicleSpec spec = VehicleCatalog.GetSpec(vehicleNameOrBase);
            if (spec == null) return string.Empty;

            bool isPro = _activeProfile != null && _activeProfile.SkillLevel != 0;
            IReadOnlyList<MM2CityRewardSpec> allRewards = MM2RuntimeCityLoader.GetAllCityRewards();

            // 1. Check if the selected custom paint job is locked
            if (colorIndex > 0 && !IsVehicleColorUnlocked(spec.BaseName, colorIndex))
            {
                if (allRewards != null)
                {
                    for (int i = 0; i < allRewards.Count; i++)
                    {
                        MM2CityRewardSpec r = allRewards[i];
                        if (r != null && r.VariantNum == colorIndex &&
                            string.Equals(r.CarBaseName, spec.BaseName, StringComparison.OrdinalIgnoreCase) &&
                            !IsRewardConditionMet(r))
                        {
                            return FormatRewardRequirementDescription(r, isPro);
                        }
                    }
                }
                return "To unlock this custom paint job, pass half of the races in any race mode";
            }

            // 2. Check if the vehicle itself is locked
            if (!IsVehicleUnlocked(spec.BaseName))
            {
                if (spec.IsMM1Vehicle && (spec.UnlockFlags > 0 || spec.UnlockScore > 0))
                {
                    return FormatMM1UnlockRequirementDescription(spec, isPro);
                }

                if (allRewards != null)
                {
                    for (int i = 0; i < allRewards.Count; i++)
                    {
                        MM2CityRewardSpec r = allRewards[i];
                        if (r != null && r.VariantNum == 0 &&
                            string.Equals(r.CarBaseName, spec.BaseName, StringComparison.OrdinalIgnoreCase) &&
                            !IsRewardConditionMet(r))
                        {
                            return FormatRewardRequirementDescription(r, isPro);
                        }
                    }
                }

                if (spec.UnlockFlags > 0 || spec.UnlockScore > 0)
                {
                    return FormatMM1UnlockRequirementDescription(spec, isPro);
                }

                return "To unlock this vehicle, complete more races";
            }

            return spec.Description ?? string.Empty;
        }

        /// <summary>
        /// Called after every race (Blitz, Checkpoint, Circuit, CrashCourse — never Cruise) to update:
        /// 1. playerN.sav (last vehicle, paintjob, city, mode, raceId, skill/ranking)
        /// 2. <city>/playerN.rec (passed bitmask, personal best time, vehicle, score, passed flag)
        /// 3. <city>/amateur.dat or <city>/pro.dat (Hall of Fame top-5 time and score leaderboards for Blitz/Checkpoint/Circuit)
        /// </summary>
        public static void RecordRaceResult(
            string cityCode,
            string modeName,
            int raceId,
            bool playerFinishedCleanly,
            int finishRank,
            float finishTimeSeconds,
            string vehicleDisplayName
        )
        {
            EnsureInitialized();
            if (_activeProfile == null) return;
            if (string.IsNullOrEmpty(modeName) ||
                string.Equals(modeName, "Cruise", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            cityCode = NormalizeCityCode(cityCode);
            int mmMode = ModeNameToMmModeId(modeName);
            if (mmMode == 0) return;

            VehicleSpec spec = VehicleCatalog.GetSpec(vehicleDisplayName ?? MM2GameSession.VehicleName);
            string vehicleBaseName = (spec != null && !string.IsNullOrEmpty(spec.BaseName))
                ? spec.BaseName
                : (!string.IsNullOrEmpty(_activeProfile.VehicleBaseName) ? _activeProfile.VehicleBaseName : "vpmustang99");

            // Always update last-played session info in playerN.sav
            _activeProfile.LastCityName = cityCode;
            _activeProfile.LastGameMode = mmMode;
            _activeProfile.LastRaceId = Mathf.Max(0, raceId);
            _activeProfile.VehicleBaseName = vehicleBaseName;
            if (spec != null && spec.Colors != null && spec.Colors.Length > 0)
            {
                int cIdx = Array.FindIndex(spec.Colors, c =>
                    string.Equals(c, MM2GameSession.VehicleColor, StringComparison.OrdinalIgnoreCase));
                if (cIdx >= 0) _activeProfile.PaintjobIndex = cIdx;
            }

            if (!playerFinishedCleanly)
            {
                SaveProfileToDisk(_activeProfile);
                return;
            }

            // Snapshot unlocked rewards & vehicles prior to recording this race result so we can notify on newly unlocked items
            var previouslyMetRewards = new HashSet<int>();
            IReadOnlyList<MM2CityRewardSpec> allRewards = MM2RuntimeCityLoader.GetAllCityRewards();
            if (allRewards != null)
            {
                for (int i = 0; i < allRewards.Count; i++)
                {
                    if (allRewards[i] != null && IsRewardConditionMet(allRewards[i]))
                    {
                        previouslyMetRewards.Add(i);
                    }
                }
            }
            var previouslyUnlockedVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<VehicleSpec> allVehicleSpecs = VehicleCatalog.GetAllSpecs();
            if (allVehicleSpecs != null)
            {
                for (int i = 0; i < allVehicleSpecs.Count; i++)
                {
                    if (allVehicleSpecs[i] != null && IsVehicleUnlocked(allVehicleSpecs[i].BaseName))
                    {
                        previouslyUnlockedVehicles.Add(allVehicleSpecs[i].BaseName);
                    }
                }
            }

            MM2CityRecordsData cityRec = GetCityRecords(cityCode);
            int slotIndex = GetRecordSlotIndex(cityRec, mmMode, raceId);
            if (slotIndex < 0 || slotIndex >= cityRec.Records.Length)
            {
                SaveProfileToDisk(_activeProfile);
                return;
            }

            // Compute passed flag & score:
            // score = baseRankScore (50 / 25 / 10) * int(vehicle.ScoringBias)
            int newPassed = 0;
            int newScore = 0;
            float validTime = Mathf.Max(0.01f, finishTimeSeconds);
            int scoringBiasInt = Mathf.Max(1, Mathf.RoundToInt(spec != null ? spec.ScoringBias : 10f));

            if (mmMode == 6) // Crash Course
            {
                newPassed = 1;
                newScore = 1;
            }
            else if (mmMode == 1) // Blitz
            {
                newPassed = (finishRank <= 1) ? 1 : 0;
                newScore = (finishRank == 1) ? (50 * scoringBiasInt) : 0;
            }
            else // Checkpoint (4) or Circuit (3)
            {
                bool alreadyPassed = false;
                if (mmMode == 3) alreadyPassed = (cityRec.CircuitPassedMask & (1u << raceId)) != 0;
                else if (mmMode == 4) alreadyPassed = (cityRec.CheckpointPassedMask & (1u << raceId)) != 0;

                if (alreadyPassed)
                {
                    newPassed = 1;
                }
                else if (_activeProfile.SkillLevel == 0) // Amateur: Top MustPlace (default 3) passes
                {
                    CitySpec citySpecForPlace = MM2RuntimeCityLoader.GetCitySpec(cityCode);
                    int mustPlace = (citySpecForPlace != null && citySpecForPlace.MustPlace > 0) ? citySpecForPlace.MustPlace : 3;
                    newPassed = (finishRank >= 1 && finishRank <= mustPlace) ? 1 : 0;
                }
                else // Pro: 1st place passes
                {
                    newPassed = (finishRank == 1) ? 1 : 0;
                }

                int basePts = 0;
                if (finishRank == 1) basePts = 50;
                else if (finishRank == 2) basePts = 25;
                else if (finishRank == 3) basePts = 10;
                newScore = basePts * scoringBiasInt;
            }

            // Update completion bitmask in header
            if (newPassed != 0)
            {
                uint bit = 1u << raceId;
                switch (mmMode)
                {
                    case 1: cityRec.BlitzPassedMask |= bit; break;
                    case 3: cityRec.CircuitPassedMask |= bit; break;
                    case 4: cityRec.CheckpointPassedMask |= bit; break;
                    case 6: cityRec.CrashCoursePassedMask |= bit; break;
                }
            }

            // Merge with existing record
            MM2CityRaceRecordEntry oldEntry = cityRec.Records[slotIndex];
            MM2CityRaceRecordEntry mergedEntry;

            if (oldEntry.BestTime <= 0.0001f)
            {
                mergedEntry = new MM2CityRaceRecordEntry
                {
                    BestTime = validTime,
                    VehicleBaseName = vehicleBaseName,
                    Score = newScore,
                    Passed = newPassed != 0 ? 1 : 0
                };
            }
            else
            {
                bool isFaster = validTime < oldEntry.BestTime;
                mergedEntry = new MM2CityRaceRecordEntry
                {
                    BestTime = isFaster ? validTime : oldEntry.BestTime,
                    VehicleBaseName = isFaster ? vehicleBaseName : (string.IsNullOrEmpty(oldEntry.VehicleBaseName) ? vehicleBaseName : oldEntry.VehicleBaseName),
                    Score = Mathf.Max(oldEntry.Score, newScore),
                    Passed = (oldEntry.Passed != 0 || newPassed != 0) ? 1 : 0
                };
            }

            cityRec.Records[slotIndex] = mergedEntry;

            // Keep _activeProfile.TotalScore and cityRec.PlayerScore synced across all cities
            _activeProfile.TotalScore = ComputeTotalScoreAllCities();
            cityRec.PlayerScore = _activeProfile.TotalScore;

            string recPath = Path.Combine(GetPlayersFolderPath(), cityCode, _activeProfile.FileTag + ".rec");
            SaveCityRecordsToDisk(recPath, cityRec);
            SaveProfileToDisk(_activeProfile);

            // Detect newly unlocked vehicles or paint jobs from this race
            var unlockedMessages = new List<string>();
            if (allRewards != null)
            {
                for (int i = 0; i < allRewards.Count; i++)
                {
                    MM2CityRewardSpec r = allRewards[i];
                    if (r != null && !previouslyMetRewards.Contains(i) && IsRewardConditionMet(r))
                    {
                        VehicleSpec rewardCar = VehicleCatalog.GetSpecByBaseName(r.CarBaseName);
                        if (rewardCar == null) continue;
                        string msg = !string.IsNullOrEmpty(r.UnlockMessage)
                            ? r.UnlockMessage
                            : (r.VariantNum > 0
                                ? $"Congrats! New paint job for the {rewardCar.DisplayName}!"
                                : $"Congratulations! You've unlocked the {rewardCar.DisplayName}!");
                        if (!unlockedMessages.Contains(msg))
                        {
                            unlockedMessages.Add(msg);
                        }
                    }
                }
            }
            if (allVehicleSpecs != null)
            {
                for (int i = 0; i < allVehicleSpecs.Count; i++)
                {
                    VehicleSpec vSpec = allVehicleSpecs[i];
                    if (vSpec != null && !previouslyUnlockedVehicles.Contains(vSpec.BaseName) && IsVehicleUnlocked(vSpec.BaseName))
                    {
                        string msg = $"Congratulations! You've unlocked the {vSpec.DisplayName}!";
                        bool alreadyCovered = false;
                        for (int m = 0; m < unlockedMessages.Count; m++)
                        {
                            if (unlockedMessages[m].IndexOf(vSpec.DisplayName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                alreadyCovered = true;
                                break;
                            }
                        }
                        if (!alreadyCovered)
                        {
                            unlockedMessages.Add(msg);
                        }
                    }
                }
            }
            if (unlockedMessages.Count > 0)
            {
                MM2GameSession.LastUnlockedRewardMessage = string.Join("\n", unlockedMessages);
                Debug.Log($"[MM2PlayerManager] Reward(s) Unlocked: {MM2GameSession.LastUnlockedRewardMessage}");
            }

            // Update Hall of Fame (<city>/amateur.dat or <city>/pro.dat) for Blitz, Circuit, and Checkpoint
            if (mmMode == 1 || mmMode == 3 || mmMode == 4)
            {
                string datFileName = (_activeProfile.SkillLevel != 0) ? "pro.dat" : "amateur.dat";
                string datPath = Path.Combine(GetPlayersFolderPath(), cityCode, datFileName);
                UpdateHallOfFameFile(
                    datPath,
                    mmMode,
                    raceId,
                    _activeProfile.PlayerName,
                    vehicleBaseName,
                    validTime,
                    newScore
                );
            }

            Debug.Log(
                $"[MM2PlayerManager] Updated records for '{_activeProfile.PlayerName}' ({_activeProfile.FileTag}) " +
                $"in {cityCode} {modeName} #{raceId}: time={MM2RaceManager.FormatRaceTime(validTime)}, " +
                $"rank={finishRank}, score={newScore}, passed={newPassed}, vehicle={vehicleBaseName}"
            );
        }

        // =====================================================================
        // UI SUMMARY FORMATTERS FOR HOME SCREEN (MAIN MENU)
        // =====================================================================
        public static string GetDriverOverviewText()
        {
            EnsureInitialized();
            if (_activeProfile == null)
            {
                return
                    "RANKING:\n      Amateur\n" +
                    "LAST RACE:\n      Cruise\n" +
                    "LAST VEHICLE:\n      Default\n" +
                    "CONTROLLER:\n      Keyboard";
            }

            string ranking = _activeProfile.SkillLevel != 0 ? "Pro" : "Amateur";
            string lastRaceDesc = FormatLastRaceDescription(_activeProfile);
            VehicleSpec spec = VehicleCatalog.GetSpecByBaseName(_activeProfile.VehicleBaseName);
            string vehicleDisplay = (spec != null && !string.IsNullOrEmpty(spec.DisplayName))
                ? spec.DisplayName
                : _activeProfile.VehicleBaseName;

            return
                $"RANKING:\n      {ranking}\n" +
                $"LAST RACE:\n      {lastRaceDesc}\n" +
                $"LAST VEHICLE:\n      {vehicleDisplay}\n" +
                $"CONTROLLER:\n      Keyboard";
        }

        public static List<CitySpec> GetRaceCapableCities()
        {
            var result = new List<CitySpec>();
            IReadOnlyList<CitySpec> all = MM2RuntimeCityLoader.GetAllCities();
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    CitySpec c = all[i];
                    if (c == null || string.IsNullOrEmpty(c.MapName)) continue;
                    bool hasBlitz = c.BlitzNames != null && c.BlitzNames.Length > 0;
                    bool hasCircuit = c.CircuitNames != null && c.CircuitNames.Length > 0;
                    bool hasCheckpoint = c.CheckpointNames != null && c.CheckpointNames.Length > 0;
                    bool hasCrash = c.HasCrashCourse && c.CrashLessons != null && c.CrashLessons.Length > 0;
                    if (hasBlitz || hasCircuit || hasCheckpoint || hasCrash)
                    {
                        result.Add(c);
                    }
                }
            }
            return result;
        }

        public static List<string> GetAvailableRaceModesForCity(string cityCode, bool includeCrashCourse = true)
        {
            var modes = new List<string>();
            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            if (spec != null)
            {
                if (spec.BlitzNames != null && spec.BlitzNames.Length > 0) modes.Add("Blitz");
                if (spec.CircuitNames != null && spec.CircuitNames.Length > 0) modes.Add("Circuit");
                if (spec.CheckpointNames != null && spec.CheckpointNames.Length > 0) modes.Add("Checkpoint");
                if (includeCrashCourse && spec.HasCrashCourse && spec.CrashLessons != null && spec.CrashLessons.Length > 0)
                {
                    modes.Add("CrashCourse");
                }
            }
            if (modes.Count == 0)
            {
                modes.Add("Blitz");
                modes.Add("Circuit");
                modes.Add("Checkpoint");
            }
            return modes;
        }

        public static string ResolveVehicleDisplayName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName) || baseName == "---") return "---";
            VehicleSpec spec = VehicleCatalog.GetSpecByBaseName(baseName);
            if (spec != null && !string.IsNullOrEmpty(spec.DisplayName))
            {
                return spec.DisplayName;
            }
            return baseName;
        }

        public static List<MM2DriverStatRow> GetDriverStatsRows(string cityCode, string modeName)
        {
            EnsureInitialized();
            var rows = new List<MM2DriverStatRow>();
            cityCode = NormalizeCityCode(cityCode);
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            string cityDisplay = (citySpec != null && !string.IsNullOrEmpty(citySpec.DisplayName)) ? citySpec.DisplayName : cityCode;

            int mmMode = ModeNameToMmModeId(modeName);
            if (mmMode == 0) mmMode = 1;

            MM2CityRecordsData rec = GetCityRecords(cityCode);

            if (mmMode == 6) // CrashCourse
            {
                int lessonCount = (citySpec != null && citySpec.CrashLessons != null && citySpec.CrashLessons.Length > 0)
                    ? citySpec.CrashLessons.Length
                    : (rec != null ? rec.CrashCourseCount : 0);

                for (int i = 0; i < lessonCount; i++)
                {
                    string lessonName = (citySpec != null && citySpec.CrashLessons != null && i < citySpec.CrashLessons.Length && !string.IsNullOrEmpty(citySpec.CrashLessons[i].DropdownLabel))
                        ? citySpec.CrashLessons[i].DropdownLabel
                        : $"Lesson {i + 1}";

                    int slot = GetRecordSlotIndex(rec, 6, i);
                    MM2CityRaceRecordEntry entry = (rec != null && slot >= 0 && slot < rec.Records.Length)
                        ? rec.Records[slot]
                        : default;

                    bool maskPassed = rec != null && (rec.CrashCoursePassedMask & (1u << i)) != 0;
                    int sessionStatus = MM2GameSession.GetCrashLessonStatus(cityCode, i);
                    bool isPassed = maskPassed || entry.Passed != 0 || sessionStatus == 1;
                    bool isFailed = !isPassed && sessionStatus == 2;

                    int status = isPassed ? 1 : (isFailed ? 2 : 0);
                    string timeStr = entry.BestTime > 0.001f
                        ? MM2RaceManager.FormatRaceTime(entry.BestTime)
                        : "---";

                    string vehStr = "---";
                    if (!string.IsNullOrEmpty(entry.VehicleBaseName) && entry.VehicleBaseName != "---")
                    {
                        vehStr = ResolveVehicleDisplayName(entry.VehicleBaseName);
                    }
                    else if (isPassed && citySpec != null && !string.IsNullOrEmpty(citySpec.CrashDefaultVehicle))
                    {
                        vehStr = ResolveVehicleDisplayName(citySpec.CrashDefaultVehicle);
                    }

                    rows.Add(new MM2DriverStatRow
                    {
                        EventIndex = i,
                        EventName = lessonName,
                        Status = status,
                        TimeString = timeStr,
                        VehicleDisplayName = vehStr
                    });
                }
                return rows;
            }

            string modeLabel = MmModeIdToDisplayName(mmMode);
            string[] raceNames = MM2RuntimeCityLoader.GetRaceNames(cityDisplay, modeLabel) ?? Array.Empty<string>();
            int count = raceNames.Length > 0
                ? raceNames.Length
                : (rec != null ? (mmMode == 1 ? rec.BlitzCount : mmMode == 3 ? rec.CircuitCount : rec.CheckpointCount) : 0);

            uint passedMask = rec != null
                ? (mmMode == 1 ? rec.BlitzPassedMask : mmMode == 3 ? rec.CircuitPassedMask : rec.CheckpointPassedMask)
                : 0u;

            for (int i = 0; i < count; i++)
            {
                string rName = (i < raceNames.Length && !string.IsNullOrEmpty(raceNames[i]))
                    ? raceNames[i]
                    : $"Race {i + 1}";

                int slot = GetRecordSlotIndex(rec, mmMode, i);
                MM2CityRaceRecordEntry entry = (rec != null && slot >= 0 && slot < rec.Records.Length)
                    ? rec.Records[slot]
                    : default;

                bool hasRecord = entry.BestTime > 0.001f;
                bool isPassed = ((passedMask & (1u << i)) != 0) || entry.Passed != 0;

                rows.Add(new MM2DriverStatRow
                {
                    EventIndex = i,
                    EventName = rName,
                    Status = isPassed ? 1 : 0,
                    TimeString = hasRecord ? MM2RaceManager.FormatRaceTime(entry.BestTime) : "---",
                    VehicleDisplayName = hasRecord ? ResolveVehicleDisplayName(entry.VehicleBaseName) : "---"
                });
            }

            return rows;
        }

        /// <summary>
        /// Returns 5 rows per race (Top 5 across all players) for the given city, race mode, and category:
        /// category: 0 = Amateur Times, 1 = Pro Times, 2 = Pro Points
        /// </summary>
        public static List<MM2RaceRecordRow> GetTop5RaceRecordsRows(string cityCode, string modeName, int category)
        {
            EnsureInitialized();
            var rows = new List<MM2RaceRecordRow>();
            cityCode = NormalizeCityCode(cityCode);
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            string cityDisplay = (citySpec != null && !string.IsNullOrEmpty(citySpec.DisplayName)) ? citySpec.DisplayName : cityCode;

            int mmMode = ModeNameToMmModeId(modeName);
            if (mmMode == 0) mmMode = 1;

            bool isPointsMode = (category == 2);
            bool isProCategory = (category == 1 || category == 2);

            string[] eventNames;
            if (mmMode == 6)
            {
                if (citySpec != null && citySpec.CrashLessons != null && citySpec.CrashLessons.Length > 0)
                {
                    eventNames = new string[citySpec.CrashLessons.Length];
                    for (int i = 0; i < citySpec.CrashLessons.Length; i++)
                    {
                        eventNames[i] = citySpec.CrashLessons[i].DropdownLabel ?? $"Lesson {i + 1}";
                    }
                }
                else
                {
                    eventNames = Array.Empty<string>();
                }
            }
            else
            {
                eventNames = MM2RuntimeCityLoader.GetRaceNames(cityDisplay, MmModeIdToDisplayName(mmMode)) ?? Array.Empty<string>();
            }

            if (eventNames.Length == 0) return rows;

            // Load Hall of Fame file (<city>/amateur.dat or <city>/pro.dat) if available
            byte[] hofData = null;
            int hofBlitz = 0, hofCircuit = 0, hofCheckpoint = 0;
            if (mmMode != 6)
            {
                string datFileName = isProCategory ? "pro.dat" : "amateur.dat";
                string datPath = Path.Combine(GetPlayersFolderPath(), cityCode, datFileName);
                if (File.Exists(datPath))
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(datPath);
                        if (bytes.Length >= 16 && BitConverter.ToInt32(bytes, 0x00) == 1)
                        {
                            hofData = bytes;
                            hofBlitz = BitConverter.ToInt32(bytes, 0x04);
                            hofCircuit = BitConverter.ToInt32(bytes, 0x08);
                            hofCheckpoint = BitConverter.ToInt32(bytes, 0x0C);
                        }
                    }
                    catch
                    {
                        hofData = null;
                    }
                }
            }

            // Also load all registered players' profiles & city .rec files so records across all players
            // (including Crash Course and custom cities without .dat files) are always included
            string playersDir = GetPlayersFolderPath();
            var playerRecordsList = new List<(MM2PlayerProfileData prof, MM2CityRecordsData rec)>();
            for (int p = 0; p < _directoryEntries.Count; p++)
            {
                var dirEntry = _directoryEntries[p];
                if (dirEntry == null || string.IsNullOrEmpty(dirEntry.FileTag)) continue;
                string savPath = Path.Combine(playersDir, dirEntry.FileTag + ".sav");
                MM2PlayerProfileData prof = LoadProfileFromDisk(savPath);
                if (prof == null) continue;
                if (string.IsNullOrEmpty(prof.PlayerName)) prof.PlayerName = dirEntry.PlayerName;

                string recPath = Path.Combine(playersDir, cityCode, dirEntry.FileTag + ".rec");
                MM2CityRecordsData pRec = LoadCityRecordsFromDisk(recPath, prof.TotalScore);
                if (pRec != null)
                {
                    playerRecordsList.Add((prof, pRec));
                }
            }

            for (int raceIdx = 0; raceIdx < eventNames.Length; raceIdx++)
            {
                string rName = eventNames[raceIdx];
                var candidates = new List<MM2HallOfFameEntry>();

                // 1. Read from amateur.dat / pro.dat
                if (hofData != null)
                {
                    int raceSlot = -1;
                    if (mmMode == 1 && raceIdx < hofBlitz) raceSlot = raceIdx;
                    else if (mmMode == 3 && raceIdx < hofCircuit) raceSlot = hofBlitz + raceIdx;
                    else if (mmMode == 4 && raceIdx < hofCheckpoint) raceSlot = hofBlitz + hofCircuit + raceIdx;

                    if (raceSlot >= 0)
                    {
                        int baseOffset = 16 + raceSlot * 10 * 132;
                        if (baseOffset + 10 * 132 <= hofData.Length)
                        {
                            for (int r = 0; r < 5; r++)
                            {
                                int entryOff = baseOffset + (r * 2 + (isPointsMode ? 1 : 0)) * 132;
                                MM2HallOfFameEntry e = ReadHallOfFameEntry(hofData, entryOff);
                                bool isValid = !string.IsNullOrEmpty(e.PlayerName) &&
                                               e.PlayerName != "---" &&
                                               (isPointsMode ? e.Score > 0 : e.Time > 0.001f);
                                if (isValid)
                                {
                                    candidates.Add(e);
                                }
                            }
                        }
                    }
                }

                // 2. Merge personal bests from all players' .rec files matching the skill level (or all players for Crash Course)
                for (int p = 0; p < playerRecordsList.Count; p++)
                {
                    var (prof, pRec) = playerRecordsList[p];
                    if (mmMode != 6)
                    {
                        bool profIsPro = prof.SkillLevel != 0;
                        if (profIsPro != isProCategory) continue;
                    }

                    int slot = GetRecordSlotIndex(pRec, mmMode, raceIdx);
                    if (slot < 0 || slot >= pRec.Records.Length) continue;
                    MM2CityRaceRecordEntry recEntry = pRec.Records[slot];

                    bool validRec = isPointsMode ? (recEntry.Score > 0) : (recEntry.BestTime > 0.001f);
                    if (!validRec) continue;

                    string vehBase = !string.IsNullOrEmpty(recEntry.VehicleBaseName)
                        ? recEntry.VehicleBaseName
                        : (mmMode == 6 && citySpec != null && !string.IsNullOrEmpty(citySpec.CrashDefaultVehicle)
                            ? citySpec.CrashDefaultVehicle
                            : prof.VehicleBaseName);

                    bool alreadyInList = candidates.Exists(c =>
                        string.Equals(c.PlayerName, prof.PlayerName, StringComparison.OrdinalIgnoreCase) &&
                        (isPointsMode
                            ? c.Score == recEntry.Score
                            : Mathf.Abs(c.Time - recEntry.BestTime) < 0.02f));

                    if (!alreadyInList)
                    {
                        candidates.Add(new MM2HallOfFameEntry
                        {
                            PlayerName = prof.PlayerName,
                            VehicleBaseName = vehBase,
                            Time = recEntry.BestTime,
                            Score = recEntry.Score
                        });
                    }
                }

                // Sort Top 5 (ascending by Time, or descending by Score)
                if (isPointsMode)
                {
                    candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
                }
                else
                {
                    candidates.Sort((a, b) => a.Time.CompareTo(b.Time));
                }

                // Emit exactly 5 rows for this race
                for (int rank = 0; rank < 5; rank++)
                {
                    if (rank < candidates.Count)
                    {
                        MM2HallOfFameEntry e = candidates[rank];
                        string valStr = isPointsMode
                            ? $"{e.Score} pts"
                            : MM2RaceManager.FormatRaceTime(e.Time);
                        rows.Add(new MM2RaceRecordRow
                        {
                            RaceIndex = raceIdx,
                            RankInRace = rank + 1,
                            RaceName = rName,
                            DriverName = e.PlayerName,
                            TimeOrScoreString = valStr,
                            VehicleDisplayName = ResolveVehicleDisplayName(e.VehicleBaseName)
                        });
                    }
                    else
                    {
                        rows.Add(new MM2RaceRecordRow
                        {
                            RaceIndex = raceIdx,
                            RankInRace = rank + 1,
                            RaceName = rName,
                            DriverName = string.Empty,
                            TimeOrScoreString = "---",
                            VehicleDisplayName = "---"
                        });
                    }
                }
            }

            return rows;
        }

        public static string GetDriverDetailedStatsText()
        {
            EnsureInitialized();
            if (_activeProfile == null) return GetDriverOverviewText();

            IReadOnlyList<CitySpec> cities = MM2RuntimeCityLoader.GetAllCities();
            int totalPts = ComputeTotalScoreAllCities();
            string ranking = _activeProfile.SkillLevel != 0 ? "Pro" : "Amateur";

            var sb = new StringBuilder();
            sb.AppendLine($"DRIVER: {_activeProfile.PlayerName} ({ranking})");
            sb.AppendLine($"TOTAL SCORE: {totalPts} pts");

            if (cities != null && cities.Count > 0)
            {
                for (int i = 0; i < cities.Count; i++)
                {
                    CitySpec city = cities[i];
                    if (city == null || string.IsNullOrEmpty(city.MapName)) continue;

                    MM2CityRecordsData rec = GetCityRecords(city.MapName);
                    int bTotal = city.BlitzNames != null ? city.BlitzNames.Length : rec.BlitzCount;
                    int kTotal = city.CheckpointNames != null ? city.CheckpointNames.Length : rec.CheckpointCount;
                    int cTotal = city.CircuitNames != null ? city.CircuitNames.Length : rec.CircuitCount;
                    int ccTotal = city.HasCrashCourse
                        ? (city.CrashLessons != null && city.CrashLessons.Length > 0 ? city.CrashLessons.Length : rec.CrashCourseCount)
                        : 0;

                    int bDone = CountBits(rec.BlitzPassedMask, Mathf.Max(bTotal, rec.BlitzCount));
                    int kDone = CountBits(rec.CheckpointPassedMask, Mathf.Max(kTotal, rec.CheckpointCount));
                    int cDone = CountBits(rec.CircuitPassedMask, Mathf.Max(cTotal, rec.CircuitCount));
                    int ccDone = ccTotal > 0 ? CountBits(rec.CrashCoursePassedMask, Mathf.Max(ccTotal, rec.CrashCourseCount)) : 0;
                    int cityPts = ComputeCityScore(city.MapName);

                    string cityTitle = (!string.IsNullOrEmpty(city.DisplayName) ? city.DisplayName : city.MapName).ToUpperInvariant();
                    sb.AppendLine();
                    sb.AppendLine($"{cityTitle} ({cityPts} pts):");
                    if (ccTotal > 0)
                    {
                        sb.AppendLine($"   Blitz: {bDone}/{bTotal}   Checkpoint: {kDone}/{kTotal}");
                        sb.Append($"   Circuit: {cDone}/{cTotal}   Crash Course: {ccDone}/{ccTotal}");
                    }
                    else
                    {
                        sb.Append($"   Blitz: {bDone}/{bTotal}   Checkpoint: {kDone}/{kTotal}   Circuit: {cDone}/{cTotal}");
                    }
                    if (i < cities.Count - 1) sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        public static string GetDriverRaceRecordsSummaryText(string cityCode, string modeName)
        {
            EnsureInitialized();
            cityCode = NormalizeCityCode(cityCode);
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            string cityDisplay = (citySpec != null && !string.IsNullOrEmpty(citySpec.DisplayName))
                ? citySpec.DisplayName
                : cityCode;

            int mmMode = ModeNameToMmModeId(modeName);
            if (mmMode == 0) mmMode = 1; // Default to Blitz if Cruise was active

            MM2CityRecordsData rec = GetCityRecords(cityCode);
            string modeLabel = MmModeIdToDisplayName(mmMode);
            string[] raceNames = MM2RuntimeCityLoader.GetRaceNames(cityDisplay, modeLabel) ?? Array.Empty<string>();

            int count = mmMode == 1 ? (raceNames.Length > 0 ? raceNames.Length : rec.BlitzCount)
                      : mmMode == 3 ? (raceNames.Length > 0 ? raceNames.Length : rec.CircuitCount)
                      : mmMode == 4 ? (raceNames.Length > 0 ? raceNames.Length : rec.CheckpointCount)
                      : (citySpec != null && citySpec.HasCrashCourse ? rec.CrashCourseCount : 0);

            var sb = new StringBuilder();
            sb.AppendLine($"{cityDisplay.ToUpperInvariant()} - {modeLabel.ToUpperInvariant()} RECORDS:");
            if (count <= 0)
            {
                sb.AppendLine("No events for this mode in this city.");
                return sb.ToString().TrimEnd();
            }

            int shown = Mathf.Min(count, 10);
            for (int i = 0; i < shown; i++)
            {
                int slot = GetRecordSlotIndex(rec, mmMode, i);
                if (slot < 0 || slot >= rec.Records.Length) continue;
                var entry = rec.Records[slot];
                string rName = (i < raceNames.Length && !string.IsNullOrEmpty(raceNames[i]))
                    ? raceNames[i]
                    : $"Race {i + 1}";
                if (rName.Length > 18) rName = rName.Substring(0, 18);

                if (entry.BestTime > 0.001f)
                {
                    string timeStr = MM2RaceManager.FormatRaceTime(entry.BestTime);
                    sb.AppendLine($"{i + 1}. {rName}: {timeStr} ({entry.Score}p)");
                }
                else
                {
                    sb.AppendLine($"{i + 1}. {rName}: ---");
                }
            }
            return sb.ToString().TrimEnd();
        }

        public static int ComputeTotalScoreAllCities()
        {
            int total = 0;
            foreach (string cCode in GetAllCityCodes())
            {
                total += ComputeCityScore(cCode);
            }
            return total;
        }

        public static int ComputeCityScore(string cityCode)
        {
            MM2CityRecordsData rec = GetCityRecords(cityCode);
            if (rec == null || rec.Records == null) return 0;

            // Sum of Blitz + Checkpoint + Circuit record scores (slots 0..Blitz+Circuit+Checkpoint-1)
            int sum = 0;
            int raceSlots = Mathf.Min(rec.BlitzCount + rec.CircuitCount + rec.CheckpointCount, rec.Records.Length);
            for (int i = 0; i < raceSlots; i++)
            {
                sum += Mathf.Max(0, rec.Records[i].Score);
            }
            return sum;
        }

        public static List<string> GetAllCityCodes()
        {
            var result = new List<string>();
            IReadOnlyList<CitySpec> specs = MM2RuntimeCityLoader.GetAllCities();
            if (specs != null && specs.Count > 0)
            {
                for (int i = 0; i < specs.Count; i++)
                {
                    string code = specs[i]?.MapName;
                    if (!string.IsNullOrEmpty(code) &&
                        !result.Exists(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(code.ToLowerInvariant());
                    }
                }
            }
            if (result.Count == 0)
            {
                result.Add("sf");
                result.Add("london");
            }
            return result;
        }

        private static int CountBits(uint mask, int count)
        {
            int total = 0;
            for (int i = 0; i < count && i < 32; i++)
            {
                if ((mask & (1u << i)) != 0) total++;
            }
            return total;
        }

        private static string FormatLastRaceDescription(MM2PlayerProfileData prof)
        {
            string cityCode = NormalizeCityCode(prof.LastCityName);
            CitySpec citySpec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            string cityDisplay = (citySpec != null && !string.IsNullOrEmpty(citySpec.DisplayName))
                ? citySpec.DisplayName
                : cityCode;

            if (prof.LastGameMode == 0)
            {
                return $"Cruise ({cityDisplay})";
            }

            string modeLabel = MmModeIdToDisplayName(prof.LastGameMode);
            if (prof.LastGameMode == 6)
            {
                return $"Crash Course #{prof.LastRaceId + 1} ({cityDisplay})";
            }

            string[] raceNames = MM2RuntimeCityLoader.GetRaceNames(cityDisplay, modeLabel);
            if (raceNames != null && prof.LastRaceId >= 0 && prof.LastRaceId < raceNames.Length)
            {
                return $"{modeLabel}: {raceNames[prof.LastRaceId]}";
            }
            return $"{modeLabel} #{prof.LastRaceId + 1} ({cityDisplay})";
        }

        private static int ModeNameToMmModeId(string modeName)
        {
            if (string.IsNullOrEmpty(modeName)) return 0;
            if (string.Equals(modeName, "Blitz", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(modeName, "Circuit", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(modeName, "Checkpoint", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modeName, "race", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(modeName, "CrashCourse", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(modeName, "crash", StringComparison.OrdinalIgnoreCase)) return 6;
            return 0;
        }

        private static string MmModeIdToDisplayName(int mmMode)
        {
            switch (mmMode)
            {
                case 1: return "Blitz";
                case 3: return "Circuit";
                case 4: return "Checkpoint";
                case 6: return "CrashCourse";
                default: return "Cruise";
            }
        }

        private static string NormalizeCityCode(string city)
        {
            if (string.IsNullOrEmpty(city)) return "sf";
            string trimmed = city.Trim();
            IReadOnlyList<CitySpec> allCities = MM2RuntimeCityLoader.GetAllCities();
            if (allCities != null)
            {
                for (int i = 0; i < allCities.Count; i++)
                {
                    CitySpec c = allCities[i];
                    if (c == null) continue;
                    if (string.Equals(c.MapName, trimmed, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.RaceDir, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return c.MapName.ToLowerInvariant();
                    }
                }
            }
            if (trimmed.IndexOf("london", StringComparison.OrdinalIgnoreCase) >= 0) return "london";
            if (trimmed.IndexOf("san francisco", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(trimmed, "sf", StringComparison.OrdinalIgnoreCase)) return "sf";
            return trimmed.ToLowerInvariant();
        }

        private static int GetRecordSlotIndex(MM2CityRecordsData rec, int mmMode, int raceId)
        {
            if (rec == null || raceId < 0) return -1;
            switch (mmMode)
            {
                case 1: // Blitz (0..11)
                    return raceId < rec.BlitzCount ? raceId : -1;
                case 3: // Circuit (12..21)
                    return raceId < rec.CircuitCount ? rec.BlitzCount + raceId : -1;
                case 4: // Checkpoint (22..31)
                    return raceId < rec.CheckpointCount ? rec.BlitzCount + rec.CircuitCount + raceId : -1;
                case 6: // CrashCourse (32..44)
                    return raceId < rec.CrashCourseCount ? rec.BlitzCount + rec.CircuitCount + rec.CheckpointCount + raceId : -1;
                default:
                    return -1;
            }
        }

        // =====================================================================
        // BINARY I/O: players.dir
        // =====================================================================
        private static void LoadPlayersDirectoryFile(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 8) return;

                int version = BitConverter.ToInt32(bytes, 0);
                int count = BitConverter.ToInt32(bytes, 4);
                if (version != 6 || count < 0 || bytes.Length < 8 + count * 80)
                {
                    return;
                }

                int offset = 8;
                for (int i = 0; i < count; i++)
                {
                    string pName = ReadFixedString(bytes, offset, 40);
                    string fTag = ReadFixedString(bytes, offset + 40, 40);
                    offset += 80;

                    if (!string.IsNullOrEmpty(fTag))
                    {
                        _directoryEntries.Add(new MM2PlayerDirEntry
                        {
                            PlayerName = string.IsNullOrEmpty(pName) ? fTag : pName,
                            FileTag = fTag
                        });
                    }
                }

                if (bytes.Length >= offset + 80)
                {
                    string lastActive = ReadFixedString(bytes, offset, 80);
                    if (!string.IsNullOrEmpty(lastActive))
                    {
                        _lastActivePlayerName = lastActive;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Failed to read players.dir: {ex.Message}");
            }
        }

        private static void SavePlayersDirectoryFile(string path)
        {
            try
            {
                int count = _directoryEntries.Count;
                byte[] bytes = new byte[8 + count * 80 + 80];
                WriteInt32(bytes, 0, 6); // Version 6
                WriteInt32(bytes, 4, count);

                int offset = 8;
                for (int i = 0; i < count; i++)
                {
                    WriteFixedString(bytes, offset, 40, _directoryEntries[i].PlayerName);
                    WriteFixedString(bytes, offset + 40, 40, _directoryEntries[i].FileTag);
                    offset += 80;
                }

                WriteFixedString(bytes, offset, 80, _lastActivePlayerName);
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Failed to write players.dir: {ex.Message}");
            }
        }

        private static bool AutoDiscoverSavFiles(string playersDir)
        {
            bool modified = false;
            try
            {
                string[] savFiles = Directory.GetFiles(playersDir, "player*.sav");
                Array.Sort(savFiles, StringComparer.OrdinalIgnoreCase);

                foreach (string savFile in savFiles)
                {
                    string fileTag = Path.GetFileNameWithoutExtension(savFile);
                    if (string.IsNullOrEmpty(fileTag)) continue;

                    bool exists = _directoryEntries.Exists(e =>
                        string.Equals(e.FileTag, fileTag, StringComparison.OrdinalIgnoreCase));
                    if (exists) continue;

                    MM2PlayerProfileData prof = LoadProfileFromDisk(savFile);
                    if (prof != null && !string.IsNullOrEmpty(prof.PlayerName))
                    {
                        _directoryEntries.Add(new MM2PlayerDirEntry
                        {
                            PlayerName = prof.PlayerName,
                            FileTag = fileTag
                        });
                        modified = true;
                        Debug.Log($"[MM2PlayerManager] Auto-registered profile '{prof.PlayerName}' ({fileTag}.sav) into players.dir.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] AutoDiscoverSavFiles warning: {ex.Message}");
            }
            return modified;
        }

        // =====================================================================
        // BINARY I/O: playerN.sav (345 bytes)
        // =====================================================================
        private static MM2PlayerProfileData CreateDefaultProfile(string playerName, string fileTag, byte skillLevel = 0)
        {
            var prof = new MM2PlayerProfileData
            {
                Version = 3,
                TotalScore = 0f,
                SkillLevel = skillLevel,
                VehicleBaseName = "vpbug",
                PaintjobIndex = 0,
                LastGameMode = 0,
                LastRaceId = 0,
                PlayerName = playerName,
                NetName = playerName,
                FileTag = fileTag,
                LastCityName = "sf",
                ExtraV3 = new byte[40]
            };
            prof.ExtraV3[0] = 0x20; // Default 32
            return prof;
        }

        public static MM2PlayerProfileData LoadProfileFromDisk(string savPath)
        {
            try
            {
                if (!File.Exists(savPath)) return null;
                byte[] data = File.ReadAllBytes(savPath);
                if (data.Length < 305) return null;

                int version = BitConverter.ToInt32(data, 0x00);
                if (version != 2 && version != 3) return null;

                uint fileCrc = BitConverter.ToUInt32(data, 0x04);
                var prof = new MM2PlayerProfileData
                {
                    Version = 3,
                    TotalScore = BitConverter.ToSingle(data, 0x08),
                    SkillLevel = data[0x0C],
                    VehicleBaseName = ReadFixedString(data, 0x0D, 80),
                    PaintjobIndex = BitConverter.ToInt32(data, 0x5D),
                    LastGameMode = BitConverter.ToInt32(data, 0x61),
                    LastRaceId = BitConverter.ToInt32(data, 0x65),
                    PlayerName = ReadFixedString(data, 0x69, 40),
                    NetName = ReadFixedString(data, 0x91, 40),
                    FileTag = ReadFixedString(data, 0xB9, 40),
                    LastCityName = ReadFixedString(data, 0xE1, 80),
                    ExtraV3 = new byte[40]
                };

                if (version == 3 && data.Length >= 345)
                {
                    Buffer.BlockCopy(data, 0x131, prof.ExtraV3, 0, 40);
                }
                else
                {
                    prof.ExtraV3[0] = 0x20;
                }

                uint calcCrc = ComputeSavCrc(data, version);
                if (fileCrc != calcCrc)
                {
                    Debug.LogWarning($"[MM2PlayerManager] CRC mismatch in {Path.GetFileName(savPath)} (file=0x{fileCrc:X8}, calc=0x{calcCrc:X8}); repairing on next save.");
                }

                return prof;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Failed to load {savPath}: {ex.Message}");
                return null;
            }
        }

        public static void SaveProfileToDisk(MM2PlayerProfileData prof)
        {
            if (prof == null || string.IsNullOrEmpty(prof.FileTag)) return;

            try
            {
                string playersDir = GetPlayersFolderPath();
                if (!Directory.Exists(playersDir)) Directory.CreateDirectory(playersDir);

                byte[] data = new byte[345];
                WriteInt32(data, 0x00, 3); // Version 3
                WriteSingle(data, 0x08, prof.TotalScore);
                data[0x0C] = prof.SkillLevel;
                WriteFixedString(data, 0x0D, 80, prof.VehicleBaseName);
                WriteInt32(data, 0x5D, prof.PaintjobIndex);
                WriteInt32(data, 0x61, prof.LastGameMode);
                WriteInt32(data, 0x65, prof.LastRaceId);
                WriteFixedString(data, 0x69, 40, prof.PlayerName);
                WriteFixedString(data, 0x91, 40, string.IsNullOrEmpty(prof.NetName) ? prof.PlayerName : prof.NetName);
                WriteFixedString(data, 0xB9, 40, prof.FileTag);
                WriteFixedString(data, 0xE1, 80, NormalizeCityCode(prof.LastCityName));

                if (prof.ExtraV3 != null && prof.ExtraV3.Length == 40)
                {
                    Buffer.BlockCopy(prof.ExtraV3, 0, data, 0x131, 40);
                }
                else
                {
                    data[0x131] = 0x20;
                }

                uint crc = ComputeSavCrc(data, 3);
                WriteUInt32(data, 0x04, crc);

                string savPath = Path.Combine(playersDir, prof.FileTag + ".sav");
                File.WriteAllBytes(savPath, data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Failed to save {prof.FileTag}.sav: {ex.Message}");
            }
        }

        /// <summary>
        /// Computes the `.sav` file CRC-32 over the serialized profile fields:
        /// totalScore(4), skillLevel(1), vehicleBaseName(80), paintjob(4), lastMode(4), lastRaceId(4),
        /// lastCityName(80 at 0xE1), playerName(40 at 0x69), netName(40 at 0x91), fileTag(40 at 0xB9), extraV3(40 at 0x131).
        /// </summary>
        private static uint ComputeSavCrc(byte[] data, int version)
        {
            uint state = 0xFFFFFFFFu;
            state = UpdateCrcState(state, data, 0x08, 4);  // totalScore
            state = UpdateCrcState(state, data, 0x0C, 1);  // skillLevel
            state = UpdateCrcState(state, data, 0x0D, 80); // vehicleBaseName
            state = UpdateCrcState(state, data, 0x5D, 4);  // paintjobIndex
            state = UpdateCrcState(state, data, 0x61, 4);  // lastGameMode
            state = UpdateCrcState(state, data, 0x65, 4);  // lastRaceId
            state = UpdateCrcState(state, data, 0xE1, 80); // lastCityName
            state = UpdateCrcState(state, data, 0x69, 40); // playerName
            state = UpdateCrcState(state, data, 0x91, 40); // netName
            state = UpdateCrcState(state, data, 0xB9, 40); // fileTag
            if (version >= 3 && data.Length >= 345)
            {
                state = UpdateCrcState(state, data, 0x131, 40); // extraV3
            }
            return ~state;
        }

        // =====================================================================
        // BINARY I/O: <city>/playerN.rec (4364 bytes)
        // =====================================================================
        private static void EnsureCityRecordsExist(MM2PlayerProfileData prof)
        {
            if (prof == null || string.IsNullOrEmpty(prof.FileTag)) return;

            foreach (string city in GetAllCityCodes())
            {
                string cityDir = Path.Combine(GetPlayersFolderPath(), city);
                if (!Directory.Exists(cityDir)) Directory.CreateDirectory(cityDir);

                string recPath = Path.Combine(cityDir, prof.FileTag + ".rec");
                if (!File.Exists(recPath))
                {
                    MM2CityRecordsData defRec = CreateDefaultCityRecords(city, prof.TotalScore);
                    SaveCityRecordsToDisk(recPath, defRec);
                }
            }
        }

        private static MM2CityRecordsData CreateDefaultCityRecords(string cityCode, float playerScore)
        {
            CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(cityCode);
            int blitzCnt = (spec != null && spec.BlitzNames != null && spec.BlitzNames.Length > 0) ? spec.BlitzNames.Length : 12;
            int circuitCnt = (spec != null && spec.CircuitNames != null) ? spec.CircuitNames.Length : 10;
            int checkpointCnt = (spec != null && spec.CheckpointNames != null) ? spec.CheckpointNames.Length : 10;
            int crashCnt = (spec != null) ? (spec.HasCrashCourse ? (spec.CrashLessons != null && spec.CrashLessons.Length > 0 ? spec.CrashLessons.Length : 13) : 0) : 13;
            int totalSlots = Mathf.Max(1, blitzCnt + circuitCnt + checkpointCnt + crashCnt);

            var rec = new MM2CityRecordsData
            {
                Version = 2.0f,
                PlayerScore = playerScore,
                BlitzCount = blitzCnt,
                CircuitCount = circuitCnt,
                CheckpointCount = checkpointCnt,
                CrashCourseCount = crashCnt,
                BlitzPassedMask = 0,
                CircuitPassedMask = 0,
                CheckpointPassedMask = 0,
                CrashCoursePassedMask = 0,
                Records = new MM2CityRaceRecordEntry[totalSlots]
            };
            for (int i = 0; i < rec.Records.Length; i++)
            {
                rec.Records[i] = new MM2CityRaceRecordEntry
                {
                    BestTime = 0f,
                    VehicleBaseName = string.Empty,
                    Score = 0,
                    Passed = 0
                };
            }
            return rec;
        }

        public static MM2CityRecordsData LoadCityRecordsFromDisk(string recPath, float expectedPlayerScore)
        {
            try
            {
                if (!File.Exists(recPath)) return null;
                byte[] data = File.ReadAllBytes(recPath);
                if (data.Length < 44) return null;

                var rec = new MM2CityRecordsData
                {
                    Version = BitConverter.ToSingle(data, 0x00),
                    PlayerScore = BitConverter.ToSingle(data, 0x08),
                    BlitzCount = BitConverter.ToInt32(data, 0x0C),
                    CircuitCount = BitConverter.ToInt32(data, 0x10),
                    CheckpointCount = BitConverter.ToInt32(data, 0x14),
                    CrashCourseCount = BitConverter.ToInt32(data, 0x18),
                    BlitzPassedMask = BitConverter.ToUInt32(data, 0x1C),
                    CircuitPassedMask = BitConverter.ToUInt32(data, 0x20),
                    CheckpointPassedMask = BitConverter.ToUInt32(data, 0x24),
                    CrashCoursePassedMask = BitConverter.ToUInt32(data, 0x28)
                };

                int totalSlots = Mathf.Max(1, rec.BlitzCount + rec.CircuitCount + rec.CheckpointCount + rec.CrashCourseCount);
                rec.Records = new MM2CityRaceRecordEntry[totalSlots];

                bool repairedSwappedFields = false;
                int offset = 0x2C;
                for (int i = 0; i < totalSlots; i++)
                {
                    if (offset + 96 > data.Length)
                    {
                        rec.Records[i] = new MM2CityRaceRecordEntry
                        {
                            BestTime = 0f,
                            VehicleBaseName = string.Empty,
                            Score = 0,
                            Passed = 0
                        };
                        continue;
                    }

                    uint entryCrc = BitConverter.ToUInt32(data, offset);
                    uint calcEntryCrc = ComputeCrc32(data, offset + 4, 92);

                    if (entryCrc == calcEntryCrc)
                    {
                        // Binary record layout:
                        // +0x04: BestTime (float)
                        // +0x08: VehicleBaseName (char[80])
                        // +0x58: Passed (int, 0 or 1)
                        // +0x5C: Score (int)
                        float bestTime = BitConverter.ToSingle(data, offset + 0x04);
                        string vehBase = ReadFixedString(data, offset + 0x08, 80);
                        int passedVal = BitConverter.ToInt32(data, offset + 0x58);
                        int scoreVal = BitConverter.ToInt32(data, offset + 0x5C);

                        // Auto-repair entries written when Passed (+0x58) and Score (+0x5C) were swapped
                        if (passedVal > 1 && scoreVal <= 1)
                        {
                            int rawBasePts = passedVal;
                            passedVal = scoreVal;
                            VehicleSpec vSpec = VehicleCatalog.GetSpecByBaseName(vehBase);
                            int bias = Mathf.Max(1, Mathf.RoundToInt(vSpec != null ? vSpec.ScoringBias : 10f));
                            scoreVal = rawBasePts <= 50 ? rawBasePts * bias : rawBasePts;
                            repairedSwappedFields = true;
                        }

                        rec.Records[i] = new MM2CityRaceRecordEntry
                        {
                            BestTime = bestTime,
                            VehicleBaseName = vehBase,
                            Passed = passedVal,
                            Score = scoreVal
                        };
                    }
                    else
                    {
                        rec.Records[i] = new MM2CityRaceRecordEntry
                        {
                            BestTime = 0f,
                            VehicleBaseName = string.Empty,
                            Score = 0,
                            Passed = 0
                        };
                    }

                    offset += 96;
                }

                rec.PlayerScore = expectedPlayerScore;
                if (repairedSwappedFields)
                {
                    SaveCityRecordsToDisk(recPath, rec);
                }
                return rec;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Failed to load {recPath}: {ex.Message}");
                return null;
            }
        }

        public static void SaveCityRecordsToDisk(string recPath, MM2CityRecordsData rec)
        {
            if (rec == null) return;

            try
            {
                string dir = Path.GetDirectoryName(recPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                int totalSlots = rec.Records != null ? rec.Records.Length : 45;
                byte[] data = new byte[44 + totalSlots * 96];

                WriteSingle(data, 0x00, 2.0f);
                WriteSingle(data, 0x08, rec.PlayerScore);
                WriteInt32(data, 0x0C, rec.BlitzCount);
                WriteInt32(data, 0x10, rec.CircuitCount);
                WriteInt32(data, 0x14, rec.CheckpointCount);
                WriteInt32(data, 0x18, rec.CrashCourseCount);
                WriteUInt32(data, 0x1C, rec.BlitzPassedMask);
                WriteUInt32(data, 0x20, rec.CircuitPassedMask);
                WriteUInt32(data, 0x24, rec.CheckpointPassedMask);
                WriteUInt32(data, 0x28, rec.CrashCoursePassedMask);

                // Header CRC-32 over bytes 0x08..0x2B (36 bytes)
                uint headerCrc = ComputeCrc32(data, 0x08, 36);
                WriteUInt32(data, 0x04, headerCrc);

                int offset = 0x2C;
                for (int i = 0; i < totalSlots; i++)
                {
                    MM2CityRaceRecordEntry entry = rec.Records[i];
                    WriteSingle(data, offset + 0x04, entry.BestTime);
                    WriteFixedString(data, offset + 0x08, 80, entry.VehicleBaseName ?? string.Empty);
                    WriteInt32(data, offset + 0x58, entry.Passed);
                    WriteInt32(data, offset + 0x5C, entry.Score);

                    // Entry CRC-32 over bytes +0x04..+0x5F (92 bytes)
                    uint entryCrc = ComputeCrc32(data, offset + 0x04, 92);
                    WriteUInt32(data, offset + 0x00, entryCrc);

                    offset += 96;
                }

                File.WriteAllBytes(recPath, data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Failed to save {recPath}: {ex.Message}");
            }
        }

        // =====================================================================
        // BINARY I/O: <city>/amateur.dat & <city>/pro.dat (42256 bytes)
        // =====================================================================
        private static void EnsureDefaultHallOfFameFile(string datPath)
        {
            if (File.Exists(datPath)) return;
            try
            {
                string dir = Path.GetDirectoryName(datPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string cityCode = !string.IsNullOrEmpty(dir) ? Path.GetFileName(dir) : "sf";
                CitySpec spec = MM2RuntimeCityLoader.GetCitySpec(cityCode);

                int blitzCnt = (spec != null && spec.BlitzNames != null && spec.BlitzNames.Length > 0) ? spec.BlitzNames.Length : 12;
                int circuitCnt = (spec != null && spec.CircuitNames != null && spec.CircuitNames.Length > 0) ? spec.CircuitNames.Length : 10;
                int checkpointCnt = (spec != null && spec.CheckpointNames != null && spec.CheckpointNames.Length > 0) ? spec.CheckpointNames.Length : 10;
                int totalRaces = Mathf.Max(1, blitzCnt + circuitCnt + checkpointCnt);

                byte[] data = new byte[16 + totalRaces * 10 * 132];
                WriteInt32(data, 0x00, 1);
                WriteInt32(data, 0x04, blitzCnt);
                WriteInt32(data, 0x08, circuitCnt);
                WriteInt32(data, 0x0C, checkpointCnt);

                MM2HallOfFameEntry emptyEntry = new MM2HallOfFameEntry
                {
                    PlayerName = "---",
                    VehicleBaseName = "---",
                    Time = 0f,
                    Score = 0
                };
                for (int i = 0; i < totalRaces * 10; i++)
                {
                    WriteHallOfFameEntry(data, 16 + i * 132, emptyEntry);
                }
                File.WriteAllBytes(datPath, data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Failed to create default Hall of Fame {datPath}: {ex.Message}");
            }
        }

        private static void UpdateHallOfFameFile(
            string datPath,
            int mmMode,
            int raceId,
            string playerName,
            string vehicleBaseName,
            float finishTime,
            int score
        )
        {
            try
            {
                EnsureDefaultHallOfFameFile(datPath);
                if (!File.Exists(datPath)) return;
                byte[] data = File.ReadAllBytes(datPath);
                if (data.Length < 16) return;

                int version = BitConverter.ToInt32(data, 0x00);
                int blitzCount = BitConverter.ToInt32(data, 0x04);
                int circuitCount = BitConverter.ToInt32(data, 0x08);
                int checkpointCount = BitConverter.ToInt32(data, 0x0C);
                if (version != 1) return;

                int raceSlot;
                if (mmMode == 1 && raceId >= 0 && raceId < blitzCount)
                    raceSlot = raceId;
                else if (mmMode == 3 && raceId >= 0 && raceId < circuitCount)
                    raceSlot = blitzCount + raceId;
                else if (mmMode == 4 && raceId >= 0 && raceId < checkpointCount)
                    raceSlot = blitzCount + circuitCount + raceId;
                else
                    return;

                int baseOffset = 16 + raceSlot * 10 * 132;
                if (baseOffset + 10 * 132 > data.Length) return;

                MM2HallOfFameEntry[] timeEntries = new MM2HallOfFameEntry[5];
                MM2HallOfFameEntry[] scoreEntries = new MM2HallOfFameEntry[5];

                for (int r = 0; r < 5; r++)
                {
                    int tOff = baseOffset + (r * 2) * 132;
                    int sOff = baseOffset + (r * 2 + 1) * 132;
                    timeEntries[r] = ReadHallOfFameEntry(data, tOff);
                    scoreEntries[r] = ReadHallOfFameEntry(data, sOff);
                }

                bool modified = false;

                // 1. Insert into top-5 fastest times
                if (finishTime > 0.001f)
                {
                    for (int r = 0; r < 5; r++)
                    {
                        if (timeEntries[r].Time <= 0.0001f || finishTime < timeEntries[r].Time)
                        {
                            for (int k = 4; k > r; k--)
                            {
                                timeEntries[k] = timeEntries[k - 1];
                            }
                            timeEntries[r] = new MM2HallOfFameEntry
                            {
                                PlayerName = playerName,
                                VehicleBaseName = vehicleBaseName,
                                Time = finishTime,
                                Score = score
                            };
                            modified = true;
                            break;
                        }
                    }
                }

                // 2. Insert into top-5 highest scores
                for (int r = 0; r < 5; r++)
                {
                    if (score > scoreEntries[r].Score)
                    {
                        for (int k = 4; k > r; k--)
                        {
                            scoreEntries[k] = scoreEntries[k - 1];
                        }
                        scoreEntries[r] = new MM2HallOfFameEntry
                        {
                            PlayerName = playerName,
                            VehicleBaseName = vehicleBaseName,
                            Time = finishTime,
                            Score = score
                        };
                        modified = true;
                        break;
                    }
                }

                if (modified)
                {
                    for (int r = 0; r < 5; r++)
                    {
                        int tOff = baseOffset + (r * 2) * 132;
                        int sOff = baseOffset + (r * 2 + 1) * 132;
                        WriteHallOfFameEntry(data, tOff, timeEntries[r]);
                        WriteHallOfFameEntry(data, sOff, scoreEntries[r]);
                    }
                    File.WriteAllBytes(datPath, data);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MM2PlayerManager] Failed to update Hall of Fame {datPath}: {ex.Message}");
            }
        }

        private static MM2HallOfFameEntry ReadHallOfFameEntry(byte[] data, int offset)
        {
            uint fileCrc = BitConverter.ToUInt32(data, offset);
            uint calcCrc = ComputeHallOfFameEntryCrc(data, offset);
            if (fileCrc != calcCrc)
            {
                return new MM2HallOfFameEntry
                {
                    PlayerName = "---",
                    VehicleBaseName = "---",
                    Time = 0f,
                    Score = 0
                };
            }

            return new MM2HallOfFameEntry
            {
                PlayerName = ReadFixedString(data, offset + 0x04, 40),
                VehicleBaseName = ReadFixedString(data, offset + 0x2C, 80),
                Time = BitConverter.ToSingle(data, offset + 0x7C),
                Score = BitConverter.ToInt32(data, offset + 0x80)
            };
        }

        private static void WriteHallOfFameEntry(byte[] data, int offset, MM2HallOfFameEntry entry)
        {
            WriteFixedString(data, offset + 0x04, 40, string.IsNullOrEmpty(entry.PlayerName) ? "---" : entry.PlayerName);
            WriteFixedString(data, offset + 0x2C, 80, string.IsNullOrEmpty(entry.VehicleBaseName) ? "---" : entry.VehicleBaseName);
            WriteSingle(data, offset + 0x7C, entry.Time);
            WriteInt32(data, offset + 0x80, entry.Score);

            uint crc = ComputeHallOfFameEntryCrc(data, offset);
            WriteUInt32(data, offset + 0x00, crc);
        }

        /// <summary>
        /// Computes the Hall of Fame entry CRC-32:
        /// time(4 at +0x7C), vehicleBaseName(80 at +0x2C), playerName(40 at +0x04), score(4 at +0x80).
        /// </summary>
        private static uint ComputeHallOfFameEntryCrc(byte[] data, int offset)
        {
            uint state = 0xFFFFFFFFu;
            state = UpdateCrcState(state, data, offset + 0x7C, 4);  // time
            state = UpdateCrcState(state, data, offset + 0x2C, 80); // vehicleBaseName
            state = UpdateCrcState(state, data, offset + 0x04, 40); // playerName
            state = UpdateCrcState(state, data, offset + 0x80, 4);  // score
            return ~state;
        }

        // =====================================================================
        // LOW-LEVEL CRC & BUFFER HELPERS
        // =====================================================================
        public static uint ComputeCrc32(byte[] buffer, int offset, int count)
        {
            uint state = UpdateCrcState(0xFFFFFFFFu, buffer, offset, count);
            return ~state;
        }

        private static uint UpdateCrcState(uint state, byte[] buffer, int offset, int count)
        {
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                byte b = buffer[i];
                state = (state >> 8) ^ CrcTable[(state ^ b) & 0xFF];
            }
            return state;
        }

        private static string ReadFixedString(byte[] data, int offset, int maxLen)
        {
            int len = 0;
            while (len < maxLen && offset + len < data.Length && data[offset + len] != 0)
            {
                len++;
            }
            return len > 0 ? Encoding.ASCII.GetString(data, offset, len) : string.Empty;
        }

        private static void WriteFixedString(byte[] data, int offset, int maxLen, string value)
        {
            for (int i = 0; i < maxLen; i++)
            {
                data[offset + i] = 0;
            }
            if (string.IsNullOrEmpty(value)) return;

            byte[] strBytes = Encoding.ASCII.GetBytes(value);
            int copyLen = Mathf.Min(strBytes.Length, maxLen - 1);
            Buffer.BlockCopy(strBytes, 0, data, offset, copyLen);
        }

        private static void WriteInt32(byte[] data, int offset, int val)
        {
            byte[] b = BitConverter.GetBytes(val);
            Buffer.BlockCopy(b, 0, data, offset, 4);
        }

        private static void WriteUInt32(byte[] data, int offset, uint val)
        {
            byte[] b = BitConverter.GetBytes(val);
            Buffer.BlockCopy(b, 0, data, offset, 4);
        }

        private static void WriteSingle(byte[] data, int offset, float val)
        {
            byte[] b = BitConverter.GetBytes(val);
            Buffer.BlockCopy(b, 0, data, offset, 4);
        }
    }
}
