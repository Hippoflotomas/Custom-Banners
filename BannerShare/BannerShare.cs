using BepInEx;
using Jotunn.Entities;
using Jotunn.Managers;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Jotunn.Utils;
using Jotunn.Configs;
using System.Linq;
using System;
using HarmonyLib;
using UnityEngine.Diagnostics;
using UnityEngine;
using System.IO.Compression;

namespace BannerShare
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]

    internal class BannerShare : BaseUnityPlugin
    {
        public const string PluginGUID = "com.jotunn.BannerShare";
        public const string PluginName = "BannerShare";
        public const string PluginVersion = "1.1.2";

        public const string PiecePrefabPrefix = "BannerShare_";
        public const string VanillaBannerSource = "piece_banner01";
        private const string FallBackBannerName = "Missing";

        // Use this class to add your own localization to the game
        // https://valheim-modding.github.io/Jotunn/tutorials/localization.html
        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();
        public class BannerRequirement
        {
            public string Item { get; set; }
            public int Amount { get; set; }
        }

        public class BannerDefinition
        {
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string CraftingStation { get; set; }
            public string BasePrefab { get; set; }
            public bool Hidden { get; set; }
            public List<BannerRequirement> Requirements { get; set; } = new List<BannerRequirement>();
        }

        private Harmony _harmony;

        private const string BannerNameZdoKey = "BannerShare_Name";
        private static readonly HashSet<string> _substitutedBannerNames = new HashSet<string>();

        [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
        public static class Piece_SetCreator_BannerTag_Patch
        {
            private static void Postfix(Piece __instance)
            {
                if (__instance == null || !__instance.name.StartsWith(PiecePrefabPrefix))
                    return;

                var netView = __instance.GetComponent<ZNetView>();
                if (netView == null || netView.GetZDO() == null)
                    return;

                string baseName = __instance.name.Substring(PiecePrefabPrefix.Length);
                const string CloneSuffix = "(Clone)";
                if (baseName.EndsWith(CloneSuffix))
                    baseName = baseName.Substring(0, baseName.Length - CloneSuffix.Length);

                netView.GetZDO().Set(BannerNameZdoKey, baseName);
                Jotunn.Logger.LogInfo($"[BannerShare] Tagged placed piece with banner name '{baseName}'.");
            }
        }
        [HarmonyPatch(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) })]
        public static class ZnetScene_CreateObject_missingBanner_Patch
        {
            private static readonly int MissingBannerHash = (PiecePrefabPrefix + FallBackBannerName).GetStableHashCode();

            private static void Prefix(ZDO zdo)
            {
                string baseName = zdo.GetString(BannerNameZdoKey, "");
                if (string.IsNullOrEmpty(baseName))
                    return;

                int realHash = (PiecePrefabPrefix + baseName).GetStableHashCode();
                if (ZNetScene.instance.GetPrefab(realHash) != null)
                {
                    if (zdo.GetPrefab() != realHash)
                        zdo.SetPrefab(realHash); // real banner is available (again) - point back at it
                    return;
                }

                if (zdo.GetPrefab() == MissingBannerHash)
                    return; // already substituted, nothing more to do

                zdo.SetPrefab(MissingBannerHash);

                if (!_substitutedBannerNames.Contains(baseName))
                {
                    _substitutedBannerNames.Add(baseName);
                    Jotunn.Logger.LogInfo($"[BannerShare] Missing banner '{baseName}' isn't installed locally, swapping with placeholder.");
                    if (Player.m_localPlayer != null && Chat.instance != null)
                        Chat.instance.AddString($"[BannerShare] You don't have the banner '{baseName}'. Ask around for a copy! I have put up a placeholder for you.");
                }
            }
        }


        private static readonly Dictionary<string, string> LayerFileToShaderProperty = new Dictionary<string, string>
        {
            { "MainTex", "_MainTex" },
            { "BumpMap", "_BumpMap" },
            { "EmissiveTex", "_EmissiveTex" },
            { "MetalTex", "_MetalTex" },
            { "MossTex", "_MossTex" },
        };
         
        private string GetBannerFolderPath()
        {
            string path = Path.Combine(BepInEx.Paths.ConfigPath, PluginName, "Banners");
            Directory.CreateDirectory(path);
            return path;
        }

        private string GetBannerDropFolderPath()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string dropFolder = Path.Combine(documents, "Valheim Custom Banners");
            Directory.CreateDirectory(dropFolder);
            return dropFolder;
        }

        private List<string> GetTopLevelFolderNames(string zipPath)
        {
            var names = new HashSet<string>();
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    int slash = entry.FullName.IndexOf('/');
                    if (slash > 0)
                        names.Add(entry.FullName.Substring(0, slash));
                }
            }
            return names.ToList();
        }

        private void SyncBannersFromDropFolder(string bannersFolder)
        {
            string dropFolder = GetBannerDropFolderPath();
            var zipPaths = Directory.GetFiles(dropFolder, "*.zip");
            var shouldExist = new HashSet<string>();
            bool anyReadFailures = false;

            foreach (var zipPath in zipPaths)
            {
                string zipName = Path.GetFileName(zipPath);
                try
                {
                    var producedFolders = GetTopLevelFolderNames(zipPath);
                    foreach (var name in producedFolders)
                    {
                        string existingPath = Path.Combine(bannersFolder, name);
                        if (Directory.Exists(existingPath))
                            Directory.Delete(existingPath, recursive: true);
                    }
                    ZipFile.ExtractToDirectory(zipPath, bannersFolder);
                    foreach (var name in producedFolders)
                        shouldExist.Add(name);
                    Logger.LogInfo($"[BannerShare] Synced {producedFolders.Count} banner(s) from '{zipName}'.");
                }
                catch (Exception ex)
                {
                    anyReadFailures = true;
                    Logger.LogError($"[BannerShare] Failed to sync '{zipName}': {ex.Message}");
                }
            }

            if (anyReadFailures)
            {
                Logger.LogWarning("[BannerShare] Skipping cleanup this time - a zip failed to parse");
                return;
            }

            foreach (var existingFolder in Directory.GetDirectories(bannersFolder))
            {
                string name = Path.GetFileName(existingFolder);
                if (name == FallBackBannerName)
                    continue; // permanent fallback banner, do not kill

                if (!shouldExist.Contains(name))
                {
                    try
                    {
                        Directory.Delete(existingFolder, recursive: true);
                        Logger.LogInfo($"[BannerShare] Removed '{name}' - no matching zip in the drop folder.");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"[BannerShare] Failed to remove '{name}': {ex.Message}");
                    }
                }
            }
        }

        private Sprite LoadBannerIcon(string bannerFolder, string baseName)
        {
            string iconPath = Path.Combine(bannerFolder, "icon.png");
            if (!File.Exists(iconPath))
                return null;

            var iconTexture = AssetUtils.LoadTexture(iconPath, relativePath: false);
            return Sprite.Create(iconTexture, new Rect(0, 0, iconTexture.width, iconTexture.height), new Vector2(0.5f, 0.5f));
        }
        
        private void AddCustomImageLayer(GameObject bannerPrefab, string bannerFolder, string baseName)
        {
            string mainTexPath = Path.Combine(bannerFolder, "MainTex.png");
            if (!File.Exists(mainTexPath))
                return;

            Renderer clothRenderer = bannerPrefab.GetComponentsInChildren<Renderer>(true)
                .FirstOrDefault(r => r.sharedMaterial.shader.name == "Custom/Vegetation");
            if (clothRenderer == null)
            {
                Logger.LogWarning($"[BannerShare] '{baseName}' has no cloth renderer to overlay - skipping image layer.");
                return;
            }

            var meshFilter = clothRenderer.GetComponent<MeshFilter>();
            var originalMesh = meshFilter.sharedMesh;

            var customMesh = new Mesh();
            customMesh.vertices = originalMesh.vertices;
            customMesh.triangles = originalMesh.triangles;
            customMesh.normals = originalMesh.normals;
            customMesh.colors = originalMesh.colors; // carries any wind-weighting data along, if it's there

            var bounds = originalMesh.bounds;
            var vertices = originalMesh.vertices;
            var newUVs = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                newUVs[i] = new Vector2(
                    (vertices[i].z - bounds.min.z) / bounds.size.z,
                    (vertices[i].y - bounds.min.y) / bounds.size.y
                );
            }
            customMesh.uv = newUVs;

            meshFilter.mesh = customMesh; // .mesh (not .sharedMesh) auto-instances this, so the vanilla asset stays untouched

            var texture = AssetUtils.LoadTexture(mainTexPath, relativePath: false);
            clothRenderer.material.SetTexture("_MainTex", texture);

            Logger.LogInfo($"[BannerShare] Rebuilt UVs and applied custom image for '{baseName}'.");
        }
        
        private void LoadAndRegisterBanners()
        {
            // OnVanillaPrefabsAvailable fires every time the main menu loads (e.g. after logging out).
            // Jotunn keeps our custom pieces alive across scene loads, so we only need to register once.
            PrefabManager.OnVanillaPrefabsAvailable -= LoadAndRegisterBanners;

            string folder = GetBannerFolderPath();
            SyncBannersFromDropFolder(folder);
            Logger.LogInfo($"[BannerShare] scanning folder {folder}");

           var bannerFolders = Directory.GetDirectories(folder);
            Logger.LogInfo($"[BannerShare] Found {bannerFolders.Length} banner folder(s).");

            foreach (var bannerFolder in bannerFolders)
            {
                string baseName = Path.GetFileName(bannerFolder);
                string jsonPath = Path.Combine(bannerFolder, "banner.json");

                if (!File.Exists(jsonPath))         //logic for missing json file
                {
                    Logger.LogWarning($"[BannerShare] Skipping '{baseName}' banner.json missing from it's folder.");
                    continue;
                }

                BannerDefinition definition;
                try
                {
                    definition = JsonConvert.DeserializeObject<BannerDefinition>(File.ReadAllText(jsonPath));
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[BannerShare] The file '{baseName}/banner.json' is wrong: {ex.Message}");
                    continue;
                }

                RegisterBannerPiece(baseName, bannerFolder, definition);
            }
        }
        
        private void RegisterBannerPiece(string baseName, string bannerFolder, BannerDefinition definition)
        {
            string prefabName = PiecePrefabPrefix + baseName;
            string baseSource = string.IsNullOrWhiteSpace(definition.BasePrefab) ? VanillaBannerSource: definition.BasePrefab;
            
            if (PrefabManager.Instance.GetPrefab(prefabName) != null)
            {
                Logger.LogWarning($"[BannerShare] '{prefabName}' is already registered - skipping duplicate for '{baseName}'.");
                return;
            }

            var bannerPrefab = PrefabManager.Instance.CreateClonedPrefab(prefabName, baseSource);
            if (bannerPrefab == null)
            {
                Logger.LogError($"[BannerShare] Could not clone '{baseSource}' for banner '{baseName}' - Check the vanilla prefab name and try again.");
                return;
            }

            ApplyTextureLayers(bannerPrefab, bannerFolder, baseName);
            AddCustomImageLayer(bannerPrefab, bannerFolder, baseName); //temp
            var icon = LoadBannerIcon(bannerFolder, baseName);

            var pieceConfig = new PieceConfig
            {
                Name = definition.DisplayName,
                CraftingStation = definition.CraftingStation,
                PieceTable = PieceTables.Hammer,
                Description = string.IsNullOrWhiteSpace(definition.Description) ? definition.DisplayName : definition.Description,
                Icon = icon,
                Enabled = !definition.Hidden,
                Requirements = definition.Requirements
                    .Select(r => new RequirementConfig { Item = r.Item, Amount = r.Amount })
                    .ToArray()
            };

            PieceManager.Instance.AddPiece(new CustomPiece(bannerPrefab, false, pieceConfig));
            Logger.LogInfo($"[BannerShare] Registered '{definition.DisplayName}' from '{baseName}'.");
        }

        private void ApplyTextureLayers(GameObject bannerPrefab, string bannerFolder, string baseName)
        {
            foreach (var renderer in bannerPrefab.GetComponentsInChildren<Renderer>(true))
            {
                
                if (renderer.sharedMaterial.shader.name != "Custom/Vegetation")
                    continue;

                foreach (var kvp in LayerFileToShaderProperty)
                {
                    string layerPngPath = Path.Combine(bannerFolder, kvp.Key + ".png");
                    if (!File.Exists(layerPngPath))
                        continue;

                    if (!renderer.sharedMaterial.HasProperty(kvp.Value))
                    {
                        Logger.LogWarning($"[BannerShare] '{baseName}' has {kvp.Key}.png but the shader has no '{kvp.Value}' slot - Skipping.");
                        continue;
                    }

                    var texture = AssetUtils.LoadTexture(layerPngPath, relativePath: false);
                    renderer.material.SetTexture(kvp.Value, texture);
                    Logger.LogInfo($"[BannerShare] Applied {kvp.Key}.png to '{kvp.Value}' on '{baseName}'.");
                }
            }
        }
        private void Awake()
        {
            // Jotunn comes with its own Logger class to provide a consistent Log style for all mods using it
            Jotunn.Logger.LogInfo("BannerShare is here");
            PrefabManager.OnVanillaPrefabsAvailable += LoadAndRegisterBanners;

            // To learn more about Jotunn's features, go to
            // https://valheim-modding.github.io/Jotunn/tutorials/overview.html

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();
        }
    }
}