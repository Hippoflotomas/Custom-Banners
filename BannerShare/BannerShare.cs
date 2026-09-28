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
    [BepInDependency(Jotunn.Main.ModGuid, "2.30.2")]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]

    internal class BannerShare : BaseUnityPlugin
    {
        public const string PluginGUID = "com.jotunn.BannerShare";
        public const string PluginName = "BannerShare";
        public const string PluginVersion = "1.1.3";

        public const string PiecePrefabPrefix = "BannerShare_";
        public const string VanillaBannerSource = "piece_banner01";
        private const string FallBackBannerName = "Missing";
        private const string FallBackResourcePrefix = "BannerShare.Missing.";

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

        // Finds a file in a folder regardless of capitalisation (Icon.png / icon.png / ICON.PNG).
        // Windows doesn't care, but Linux dedicated servers do.
        private static string FindFileIgnoreCase(string folder, string fileName)
        {
            string exact = Path.Combine(folder, fileName);
            if (File.Exists(exact))
                return exact;

            return Directory.GetFiles(folder)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }

        // Writes the placeholder banner that is embedded in the dll out to Banners/Missing,
        // overwriting it each launch so it always matches this version of the mod.
        private void WriteFallbackBanner(string bannersFolder)
        {
            string target = Path.Combine(bannersFolder, FallBackBannerName);
            Directory.CreateDirectory(target);

            var assembly = typeof(BannerShare).Assembly;
            int written = 0;
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(FallBackResourcePrefix, StringComparison.Ordinal))
                    continue;

                string fileName = resourceName.Substring(FallBackResourcePrefix.Length);
                try
                {
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    using (var file = File.Create(Path.Combine(target, fileName)))
                        stream.CopyTo(file);
                    written++;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[BannerShare] Failed to write fallback file '{fileName}': {ex.Message}");
                }
            }

            if (written == 0)
                Logger.LogError("[BannerShare] No embedded fallback banner files found in the dll - placeholder banner will be missing.");
            else
                Logger.LogInfo($"[BannerShare] Installed fallback banner '{FallBackBannerName}' ({written} files) to '{target}'.");
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
                if (string.Equals(name, FallBackBannerName, StringComparison.OrdinalIgnoreCase))
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
            string iconPath = FindFileIgnoreCase(bannerFolder, "Icon.png");
            if (iconPath == null)
                return null;

            var iconTexture = AssetUtils.LoadTexture(iconPath, relativePath: false);
            return Sprite.Create(iconTexture, new Rect(0, 0, iconTexture.width, iconTexture.height), new Vector2(0.5f, 0.5f));
        }
        
        private const string ClothShaderName = "Custom/Vegetation";

        // Applies the banner's texture layers to every cloth mesh on the prefab.
        // Each cloth mesh gets new UVs so the image covers the whole cloth, whatever
        // shape or orientation it is. Destruction fragments are left vanilla.
        private void ApplyBannerImage(GameObject bannerPrefab, string bannerFolder, string baseName)
        {
            // Load each layer once and share it between all cloth renderers
            var layerTextures = new Dictionary<string, Texture2D>();
            foreach (var kvp in LayerFileToShaderProperty)
            {
                string path = FindFileIgnoreCase(bannerFolder, kvp.Key + ".png");
                if (path != null)
                    layerTextures[kvp.Value] = AssetUtils.LoadTexture(path, relativePath: false);
            }
            if (layerTextures.Count == 0)
            {
                Logger.LogWarning($"[BannerShare] '{baseName}' has no texture files - it will look like the vanilla banner.");
                return;
            }

            var clothRenderers = GetClothRenderers(bannerPrefab);
            if (clothRenderers.Count == 0)
            {
                Logger.LogWarning($"[BannerShare] '{baseName}': base prefab has no '{ClothShaderName}' cloth to put the image on - skipping.");
                return;
            }

            float mirror = 0f; // set by the first cloth so every cloth reads the same way round
            bool aspectChecked = false;
            foreach (var renderer in clothRenderers)
            {
                if (!RemapClothUVs(renderer, baseName, ref mirror, out float clothWidthOverHeight))
                    continue;

                if (!aspectChecked && layerTextures.TryGetValue("_MainTex", out var mainTex))
                {
                    WarnIfAspectMismatch(baseName, mainTex, clothWidthOverHeight);
                    aspectChecked = true;
                }
            }

            // One material copy per original cloth material, carrying the custom textures
            var materialCopies = new Dictionary<Material, Material>();
            foreach (var renderer in clothRenderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var original = materials[i];
                    if (original == null || original.shader.name != ClothShaderName)
                        continue;

                    if (!materialCopies.TryGetValue(original, out var copy))
                    {
                        copy = new Material(original) { name = original.name + "_" + baseName };
                        foreach (var tex in layerTextures)
                        {
                            if (copy.HasProperty(tex.Key))
                                copy.SetTexture(tex.Key, tex.Value);
                            else
                                Logger.LogWarning($"[BannerShare] '{baseName}': cloth shader has no '{tex.Key}' slot - skipping that layer.");
                        }
                        materialCopies[original] = copy;
                    }
                    materials[i] = copy;
                }
                renderer.sharedMaterials = materials;
            }

            Logger.LogInfo($"[BannerShare] Applied {layerTextures.Count} texture layer(s) to {clothRenderers.Count} cloth mesh(es) on '{baseName}'.");
        }

        // All renderers using the cloth shader, excluding the pieces that fly off when the banner is destroyed.
        private static List<Renderer> GetClothRenderers(GameObject prefab)
        {
            var fragmentRoots = new HashSet<Transform>();
            var wearNTear = prefab.GetComponent<WearNTear>();
            if (wearNTear != null && wearNTear.m_fragmentRoots != null)
            {
                foreach (var root in wearNTear.m_fragmentRoots)
                    if (root != null)
                        fragmentRoots.Add(root.transform);
            }

            var result = new List<Renderer>();
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;
                if (!renderer.sharedMaterials.Any(m => m != null && m.shader.name == ClothShaderName))
                    continue;
                if (IsDestructionFragment(renderer.transform, fragmentRoots, prefab.transform))
                    continue;
                result.Add(renderer);
            }
            return result;
        }

        private static bool IsDestructionFragment(Transform t, HashSet<Transform> fragmentRoots, Transform prefabRoot)
        {
            for (; t != null && t != prefabRoot; t = t.parent)
            {
                if (fragmentRoots.Contains(t) || t.name == "destruction")
                    return true;
            }
            return false;
        }

        // Projects the image flat onto the cloth: V runs bottom to top, U runs left to right
        // as seen from the side the cloth faces. Works in the mesh's own space, so a copy of
        // the cloth rotated 180 degrees (the back face) also reads correctly from its side.
        private bool RemapClothUVs(Renderer renderer, string baseName, ref float mirror, out float clothWidthOverHeight)
        {
            clothWidthOverHeight = 0f;
            var meshFilter = renderer.GetComponent<MeshFilter>();
            Mesh original = meshFilter.sharedMesh;
            Vector3[] vertices = original.vertices;
            if (vertices.Length == 0)
                return false;

            Vector3 up = Vector3.up;
            Vector3 right = Vector3.Cross(GetClothFacing(original), up).normalized;

            // The first cloth keeps the old left-to-right = +Z direction so banners that already
            // look right don't flip; every other cloth follows the same rule relative to its facing.
            if (mirror == 0f)
                mirror = Vector3.Dot(right, Vector3.forward) < 0f ? -1f : 1f;
            right *= mirror;

            float minU = float.MaxValue, maxU = float.MinValue;
            foreach (var v in vertices)
            {
                float d = Vector3.Dot(v, right);
                if (d < minU) minU = d;
                if (d > maxU) maxU = d;
            }
            Bounds bounds = original.bounds;
            float width = maxU - minU;
            float height = bounds.size.y;
            if (width < 0.0001f || height < 0.0001f)
            {
                Logger.LogWarning($"[BannerShare] '{baseName}': cloth mesh '{original.name}' is flat in the wrong direction - skipping it.");
                return false;
            }

            var uvs = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                uvs[i] = new Vector2(
                    (Vector3.Dot(vertices[i], right) - minU) / width,
                    (vertices[i].y - bounds.min.y) / height);
            }

            // Full copy keeps sub-meshes, vertex colours (wind weighting), uv2 etc.
            Mesh custom = UnityEngine.Object.Instantiate(original);
            custom.name = original.name + "_" + baseName;
            custom.uv = uvs;
            custom.RecalculateTangents(); // bump map lighting depends on tangents matching the UVs
            meshFilter.sharedMesh = custom;

            float worldWidth = renderer.transform.TransformVector(right * width).magnitude;
            float worldHeight = renderer.transform.TransformVector(up * height).magnitude;
            clothWidthOverHeight = worldWidth / worldHeight;
            return true;
        }

        // Which horizontal direction the cloth faces, from its averaged normals.
        // Falls back to the mesh's thinnest horizontal axis if the normals cancel out.
        private static Vector3 GetClothFacing(Mesh mesh)
        {
            Vector3[] normals = mesh.normals;
            Vector3 sum = Vector3.zero;
            foreach (var n in normals)
                sum += n;
            sum.y = 0f;

            if (normals.Length > 0 && sum.magnitude > 0.1f * normals.Length)
                return sum.normalized;

            Vector3 size = mesh.bounds.size;
            return size.x <= size.z ? Vector3.right : Vector3.forward;
        }

        private void WarnIfAspectMismatch(string baseName, Texture2D mainTex, float clothWidthOverHeight)
        {
            float imageWidthOverHeight = (float)mainTex.width / mainTex.height;
            if (Mathf.Abs(imageWidthOverHeight / clothWidthOverHeight - 1f) <= 0.3f)
                return;

            int suggestedWidth = Mathf.RoundToInt(mainTex.height * clothWidthOverHeight);
            Logger.LogWarning($"[BannerShare] '{baseName}': MainTex.png is {mainTex.width}x{mainTex.height} but this banner's cloth is " +
                              $"{clothWidthOverHeight:0.00}:1 (width:height), so the image will be stretched. " +
                              $"For this base prefab aim for about {suggestedWidth}x{mainTex.height}.");
        }

        private void LoadAndRegisterBanners()
        {
            // OnVanillaPrefabsAvailable fires every time the main menu loads (e.g. after logging out).
            // Jotunn keeps our custom pieces alive across scene loads, so we only need to register once.
            PrefabManager.OnVanillaPrefabsAvailable -= LoadAndRegisterBanners;

            string folder = GetBannerFolderPath();
            SyncBannersFromDropFolder(folder);
            WriteFallbackBanner(folder);
            Logger.LogInfo($"[BannerShare] scanning folder {folder}");

           var bannerFolders = Directory.GetDirectories(folder);
            Logger.LogInfo($"[BannerShare] Found {bannerFolders.Length} banner folder(s).");

            foreach (var bannerFolder in bannerFolders)
            {
                string baseName = Path.GetFileName(bannerFolder);
                string jsonPath = FindFileIgnoreCase(bannerFolder, "Banner.json");

                if (jsonPath == null)         //logic for missing json file
                {
                    Logger.LogWarning($"[BannerShare] Skipping '{baseName}' - Banner.json missing from its folder.");
                    continue;
                }

                BannerDefinition definition;
                try
                {
                    definition = JsonConvert.DeserializeObject<BannerDefinition>(File.ReadAllText(jsonPath));
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[BannerShare] The file '{baseName}/Banner.json' is wrong: {ex.Message}");
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

#if DEBUG
            DumpPrefabStructure(bannerPrefab, baseSource);
#endif
            ApplyBannerImage(bannerPrefab, bannerFolder, baseName);
            var icon = LoadBannerIcon(bannerFolder, baseName);

            var pieceConfig = new PieceConfig
            {
                Name = definition.DisplayName,
                CraftingStation = definition.CraftingStation,
                PieceTable = PieceTables.Hammer,
                Description = string.IsNullOrWhiteSpace(definition.Description) ? definition.DisplayName : definition.Description,
                Icon = icon,
                Category = "Custom Banners",
                Enabled = !definition.Hidden,
                Requirements = definition.Requirements
                    .Select(r => new RequirementConfig { Item = r.Item, Amount = r.Amount })
                    .ToArray()
            };

            PieceManager.Instance.AddPiece(new CustomPiece(bannerPrefab, false, pieceConfig));
            Logger.LogInfo($"[BannerShare] Registered '{definition.DisplayName}' from '{baseName}'.");
        }

#if DEBUG
        // Diagnostic: logs how a base banner prefab is built so we can see why some
        // bases behave differently. Runs once per base prefab, Debug builds only.
        private static readonly HashSet<string> _dumpedBasePrefabs = new HashSet<string>();

        private void DumpPrefabStructure(GameObject prefab, string baseSource)
        {
            if (!_dumpedBasePrefabs.Add(baseSource))
                return;

            Logger.LogInfo($"[BannerShare][Dump] ===== {baseSource} =====");
            Logger.LogInfo($"[BannerShare][Dump] LODGroup: {(prefab.GetComponentInChildren<LODGroup>(true) != null)}, Cloth components: {prefab.GetComponentsInChildren<Cloth>(true).Length}");

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                string path = renderer.name;
                for (var t = renderer.transform.parent; t != null && t != prefab.transform; t = t.parent)
                    path = t.name + "/" + path;

                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer smr)
                    mesh = smr.sharedMesh;
                else
                    mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;

                string meshInfo = mesh == null
                    ? "no mesh"
                    : $"mesh '{mesh.name}' verts={mesh.vertexCount} bounds size={mesh.bounds.size} readable={mesh.isReadable}";

                Logger.LogInfo($"[BannerShare][Dump] {renderer.GetType().Name} '{path}' enabled={renderer.enabled} localRot={renderer.transform.localEulerAngles} scale={renderer.transform.localScale} {meshInfo}");

                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null)
                    {
                        Logger.LogInfo("[BannerShare][Dump]     material: <null>");
                        continue;
                    }
                    var slots = LayerFileToShaderProperty.Values
                        .Select(prop => mat.HasProperty(prop)
                            ? $"{prop}={(mat.GetTexture(prop) != null ? mat.GetTexture(prop).name : "empty")}"
                            : $"{prop}=n/a");
                    Logger.LogInfo($"[BannerShare][Dump]     material '{mat.name}' shader '{mat.shader.name}' {string.Join(" ", slots)}");
                }
            }
        }
#endif

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