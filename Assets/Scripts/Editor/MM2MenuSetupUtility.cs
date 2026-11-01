using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MM2.UI;

namespace MM2.Editor
{
    public static class MM2MenuSetupUtility
    {
        [MenuItem("MM2/Setup Midtown Madness 2 Menus")]
        public static void SetupMenusInScenes()
        {
            // Configure TextureImporter settings for all UI sprites in Assets/Resources/UI/MM2
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Resources/UI/MM2" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                bool isGrid = path.Contains("grid_blue_tile");
                if (isGrid)
                {
                    importer.textureType = TextureImporterType.Default;
                    importer.wrapMode = TextureWrapMode.Repeat;
                    importer.mipmapEnabled = true;
                    importer.filterMode = FilterMode.Trilinear;
                }
                else
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = path.Contains("bg_");
                    importer.filterMode = FilterMode.Bilinear;
                }
                importer.maxTextureSize = 4096;
                importer.SaveAndReimport();
            }

            // Ensure MenuSystem has the MM2MenuController object saved in it
            string menuScenePath = "Assets/Scenes/MenuSystem.unity";
            var scene = EditorSceneManager.OpenScene(menuScenePath, OpenSceneMode.Single);
            if (Object.FindFirstObjectByType<MM2MenuController>() == null)
            {
                GameObject menuGo = new GameObject("MM2_MenuSystem");
                menuGo.AddComponent<MM2MenuController>();
                EditorSceneManager.SaveScene(scene, menuScenePath);
                Debug.Log("[MM2] Added MM2_MenuSystem to MenuSystem.unity");
            }
        }
    }
}
