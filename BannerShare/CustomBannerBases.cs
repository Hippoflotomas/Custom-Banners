using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;
using Logger = Jotunn.Logger;

namespace BannerShare
{
    // Banner shapes that ship with BannerShare. They are modelled in Blender, built into the
    // "bannershare" AssetBundle (embedded in the dll) and turned into templates at startup.
    // A Banner.json can then name one as its BasePrefab, exactly like a vanilla prefab name:
    //   "BasePrefab": "StandardLarge"
    internal static class CustomBannerBases
    {
        private const string BundleResourceName = "bannershare";

        // BasePrefab name used in Banner.json -> prefab name inside the AssetBundle
        private static readonly Dictionary<string, string> BundlePrefabs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "StandardLarge", "BannerLarge" },
            };

        // Placeholder material name in the bundle -> vanilla material that replaces it at runtime.
        // The cloth placeholder ("Banner") gets the vanilla banner cloth material instead.
        private const string ClothPlaceholder = "Banner";
        private static readonly Dictionary<string, string> FrameMaterials =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Wood", "woodwall" },
                { "Metal", "iron" },
                { "Stone", "stone" },
            };

        private static readonly Dictionary<string, GameObject> Templates =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static bool _built;

        public static bool TryGetTemplate(string basePrefab, out GameObject template)
        {
            template = null;
            return !string.IsNullOrWhiteSpace(basePrefab) && Templates.TryGetValue(basePrefab, out template);
        }

        public static void Build(string clothShaderName)
        {
            if (_built)
                return;
            _built = true;

            AssetBundle bundle = LoadEmbeddedBundle();
            if (bundle == null)
            {
                Logger.LogError($"[BannerShare] Embedded asset bundle '{BundleResourceName}' not found - custom banner shapes are unavailable.");
                return;
            }

            foreach (var entry in BundlePrefabs)
            {
                var model = bundle.LoadAsset<GameObject>(entry.Value);
                if (model == null)
                {
                    Logger.LogError($"[BannerShare] Prefab '{entry.Value}' is missing from the '{BundleResourceName}' bundle.");
                    continue;
                }

                var template = BuildTemplate(entry.Key, model, clothShaderName);
                if (template != null)
                {
                    Templates[entry.Key] = template;
                    Logger.LogDebug($"[BannerShare] Custom base prefab '{entry.Key}' ready.");
                }
            }
        }

        // Loaded from bytes in memory rather than from a stream: Unity reads stream-loaded bundles
        // lazily, so a stream that has already been closed fails with "ManagedStream object must be
        // readable" as soon as an asset is loaded. The bundle stays loaded for the whole session.
        private static AssetBundle _bundle;

        private static AssetBundle LoadEmbeddedBundle()
        {
            if (_bundle != null)
                return _bundle;

            Assembly assembly = typeof(CustomBannerBases).Assembly;
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + BundleResourceName, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
                return null;

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                _bundle = AssetBundle.LoadFromMemory(memory.ToArray());
            }
            return _bundle;
        }

        private static GameObject BuildTemplate(string baseName, GameObject model, string clothShaderName)
        {
            // Start from the vanilla banner so the template keeps its Piece, WearNTear, ZNetView,
            // build/break effects and so on. Only the visuals and colliders are replaced.
            GameObject template = PrefabManager.Instance.CreateClonedPrefab("BSTemplate_" + baseName, BannerShare.VanillaBannerSource);
            if (template == null)
            {
                Logger.LogError($"[BannerShare] Could not clone '{BannerShare.VanillaBannerSource}' to build '{baseName}'.");
                return null;
            }

            // Keep the vanilla cloth material: it carries the ripple, alpha cutout, double-sided
            // rendering and snow/rain, and it is how ApplyBannerImage recognises the cloth.
            Material clothMaterial = template.GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials)
                .FirstOrDefault(m => m != null && m.shader != null && m.shader.name == clothShaderName);
            if (clothMaterial == null)
            {
                Logger.LogError($"[BannerShare] '{BannerShare.VanillaBannerSource}' has no '{clothShaderName}' cloth material - cannot build '{baseName}'.");
                return null;
            }

            // Strip the vanilla colliders and every child that holds visuals
            foreach (var collider in template.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(collider);
            foreach (var child in template.transform.Cast<Transform>().ToList())
            {
                if (child.GetComponentInChildren<Renderer>(true) != null)
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            // Add our model (Frame + Cloth meshes and the colliders set up in Unity)
            GameObject visual = UnityEngine.Object.Instantiate(model, template.transform, false);
            visual.name = "model";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;

            // Swap the placeholder materials for the real vanilla ones
            foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    string placeholder = materials[i] != null ? materials[i].name : "<none>";
                    if (string.Equals(placeholder, ClothPlaceholder, StringComparison.OrdinalIgnoreCase))
                    {
                        materials[i] = clothMaterial;
                    }
                    else if (FrameMaterials.TryGetValue(placeholder, out var vanillaName))
                    {
                        var vanilla = PrefabManager.Cache.GetPrefab<Material>(vanillaName);
                        if (vanilla != null)
                            materials[i] = vanilla;
                        else
                            Logger.LogWarning($"[BannerShare] '{baseName}': vanilla material '{vanillaName}' not found - '{placeholder}' left as is.");
                    }
                    else
                    {
                        Logger.LogWarning($"[BannerShare] '{baseName}': unknown placeholder material '{placeholder}' on '{renderer.name}'.");
                    }
                }
                renderer.sharedMaterials = materials;
            }

            // The vanilla damage-state objects and destruction fragments were removed with its
            // visuals, so point the damage states at our model and drop the fragments.
            var wearNTear = template.GetComponent<WearNTear>();
            if (wearNTear != null)
            {
                wearNTear.m_new = visual;
                wearNTear.m_worn = visual;
                wearNTear.m_broken = visual;
                wearNTear.m_fragmentRoots = new GameObject[0];
            }

            // piece_banner01 is a hanging banner, so it is flagged "not on floor". The standing
            // banners are freestanding like the maypole: allow floors, and allow slopes too since
            // the foot is designed to sink into uneven ground.
            var piece = template.GetComponent<Piece>();
            if (piece != null)
            {
                piece.m_notOnFloor = false;
                piece.m_notOnTiltingSurface = false;
            }

            return template;
        }
    }
}
