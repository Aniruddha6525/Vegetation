using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Builds the "Vegetation POC" presentation scene: six separate Unity terrains, each showing a
/// different vegetation environment, laid out on a 3 x 2 grid with titled signs, a presentation
/// camera, lighting and post-processing.
///
/// Uses ONLY assets that already exist in the project (TerrainSampleAssets, Tree9,
/// Tom's Terrain Tools, Fantasy Skybox FREE). Nothing is downloaded or imported.
///
/// Menu:  Tools > Vegetation > Build Vegetation POC Scene
/// Batch: Unity.exe -batchmode -projectPath &lt;path&gt; -executeMethod VegetationPOCBuilder.BuildFromCommandLine -quit
///
/// Everything generated lives under Assets/VegetationPOC/Generated and is recreated on every build.
/// Source assets are never modified (only copies are made).
/// </summary>
public static class VegetationPOCBuilder
{
    // ------------------------------------------------------------------ paths

    const string PocRoot = "Assets/VegetationPOC";
    const string GenRoot = PocRoot + "/Generated";
    const string GenMaterials = GenRoot + "/Materials";
    const string GenPrefabs = GenRoot + "/Prefabs";
    const string GenTerrain = GenRoot + "/TerrainData";
    const string GenLayers = GenRoot + "/TerrainLayers";
    const string GenSettings = GenRoot + "/Settings";
    const string SceneFolder = PocRoot + "/Scenes";
    const string ScenePath = SceneFolder + "/VegetationPOC.unity";

    const string TerrainLitMaterialPath = "Packages/com.unity.render-pipelines.universal/Runtime/Materials/TerrainLit.mat";
    const string SkyboxPath = "Assets/Fantasy Skybox FREE/Cubemaps/Classic/FS000_Day_01_Sunless.mat";

    const string SampleAssets = "Assets/TerrainSampleAssets";
    const string TomsAssets = "Assets/Tom's Terrain Tools/Unity Terrain Assets";
    const string TomsAO = TomsAssets + "/Trees Ambient-Occlusion";
    const string TomsTrees = "Assets/Tom's Terrain Tools/Example Data/Trees";

    // ------------------------------------------------------------------ layout

    const float TerrainSize = 160f;   // metres, square
    const float TerrainHeight = 60f;  // max height range of each terrain
    const float Gap = 40f;            // gravel path between terrains
    const int Columns = 3;
    const float Margin = 120f;        // base ground around the grid
    const float BorderWidth = 9f;     // flat gravel strip at the terrain edge
    const float RampWidth = 14f;      // blend from border to terrain relief
    const float TreeEdgeClearance = 15f;
    const int HeightmapRes = 257;
    const int AlphamapRes = 256;
    const int DetailRes = 512;
    const int DetailPatchRes = 32;
    const float GravelTile = 8f;      // must divide the terrain spacing (200 m) and margin for seamless paths

    static float GridWidth => Columns * TerrainSize + (Columns - 1) * Gap;

    // ------------------------------------------------------------------ menu

    [MenuItem("Tools/Vegetation/Build Vegetation POC Scene", priority = 0)]
    static void BuildFromMenu()
    {
        if (!EditorUtility.DisplayDialog("Build Vegetation POC Scene",
                "This creates (or overwrites)\n\n  " + ScenePath + "\n  " + GenRoot + "/\n\n" +
                "using only vegetation assets already in the project. Source assets are not modified.\n\nContinue?",
                "Build", "Cancel"))
            return;

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        Build();
    }

    [MenuItem("Tools/Vegetation/Open Vegetation POC Scene", priority = 1)]
    static void OpenScene()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
        {
            EditorUtility.DisplayDialog("Vegetation POC", "The POC scene has not been built yet.\nUse Tools > Vegetation > Build Vegetation POC Scene.", "OK");
            return;
        }
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            EditorSceneManager.OpenScene(ScenePath);
    }

    /// <summary>Entry point for -executeMethod (batch mode).</summary>
    public static void BuildFromCommandLine()
    {
        Build();
    }

    // ------------------------------------------------------------------ data model

    struct Ctx
    {
        public float x, z;     // local metres
        public float h;        // height in metres
        public float slope;    // degrees
        public float n, n2, n3; // 0..1 noise fields (low / mid / patch frequency)
        public float edge;     // distance to the nearest terrain edge (m)
        public float depth;    // waterLevel - h (positive = under water), -999 when the biome has no water
    }

    class TreeRule
    {
        public string[] keys;
        public float spacing, probability, minH, maxH, minW = 0.9f, maxW = 1.1f, maxSlope = 30f, clearance;
        public float sink = 0.15f;
        public Func<Ctx, float> mask;
    }

    class DetailRule
    {
        public string key;
        public float density;          // instances per square metre at mask = 1
        public float minScale = 0.8f, maxScale = 1.25f;
        public float maxSlope = 35f;
        public Func<Ctx, float> mask;
    }

    class Biome
    {
        public string title, subtitle, species;
        public Color accent;
        public int seed;
        public float baseHeight, amplitude, frequency = 0.011f;
        public Func<Biome, float, float, float, float> shape; // (biome, x, z, h) -> h, optional
        public float waterLevel = -1f;
        public Vector2 pondCenter;
        public float pondRadius;
        public string[] layers = new string[3];
        public float[] tiles = new float[3];
        public Func<Ctx, Vector3> splat;   // weights of layers 0..2 (border gravel is layer 3)
        public List<TreeRule> trees = new List<TreeRule>();
        public List<DetailRule> details = new List<DetailRule>();
        public Vector2 groundView = new Vector2(80f, 26f);
        public Vector2 groundLookAt = new Vector2(80f, 110f);

        // runtime
        public Vector2 off1, off2, off3, off4;
        public Vector3 origin;
    }

    // ------------------------------------------------------------------ build state

    static readonly Dictionary<string, GameObject> TreePrefabs = new Dictionary<string, GameObject>();
    static readonly Dictionary<string, float> TreeHeights = new Dictionary<string, float>();
    static readonly Dictionary<string, GameObject> DetailPrefabs = new Dictionary<string, GameObject>();
    static readonly Dictionary<string, TerrainLayer> LayerCache = new Dictionary<string, TerrainLayer>();
    static readonly Dictionary<string, Material> MaterialCache = new Dictionary<string, Material>();
    static readonly List<string> Warnings = new List<string>();
    static Font _font;

    // ------------------------------------------------------------------ main

    static void Build()
    {
        TreePrefabs.Clear(); TreeHeights.Clear(); DetailPrefabs.Clear();
        LayerCache.Clear(); MaterialCache.Clear(); Warnings.Clear();

        try
        {
            // Replace the open scene first so no loaded scene still references the
            // generated assets that are about to be deleted.
            Progress("Creating scene", 0.02f);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Progress("Preparing folders", 0.05f);
            PrepareFolders();

            Progress("Building vegetation library", 0.08f);
            BuildVegetationLibrary();

            var biomes = DefineBiomes();
            var biomesRoot = new GameObject("Biomes");
            var labelsRoot = new GameObject("Overview Labels");

            var overviewPos = new Vector3(GridWidth * 0.5f, 300f, -280f);
            var overviewLook = new Vector3(GridWidth * 0.5f, 0f, 190f);

            for (int i = 0; i < biomes.Count; i++)
            {
                var b = biomes[i];
                int col = i % Columns, row = i / Columns;
                b.origin = new Vector3(col * (TerrainSize + Gap), 0f, row * (TerrainSize + Gap));
                Progress("Building terrain: " + b.title, 0.15f + 0.7f * i / biomes.Count);
                var terrain = BuildBiomeTerrain(b, i, biomesRoot.transform);
                BuildPlaque(b, i, terrain.transform.parent);
                BuildOverheadLabel(b, i, labelsRoot.transform, overviewPos);
            }

            Progress("Base ground", 0.87f);
            BuildBaseGround();

            Progress("Lighting & post-processing", 0.9f);
            BuildEnvironment();
            BuildHeaderLabel(labelsRoot.transform, overviewPos);

            Progress("Camera", 0.93f);
            BuildCamera(biomes, labelsRoot, overviewPos, overviewLook);

            Progress("Saving", 0.97f);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings();
            AssetDatabase.SaveAssets();

            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp && urp.shadowDistance < RecommendedShadowDistance)
                Warnings.Add($"URP shadow distance is {urp.shadowDistance} m, so tree shadows fade out in the aerial views. " +
                             "Optional: Tools > Vegetation > Apply Recommended Shadow Distance (edits the project's URP asset).");

            foreach (var w in Warnings)
                Debug.LogWarning("[Vegetation POC] " + w);
            Debug.Log($"[Vegetation POC] Scene built: {ScenePath}  ({biomes.Count} terrains, {TreePrefabs.Count} tree prefabs, {DetailPrefabs.Count} detail prefabs, {Warnings.Count} warning(s)). Press Play to present.");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static void Progress(string msg, float p)
    {
        if (!Application.isBatchMode)
            EditorUtility.DisplayProgressBar("Vegetation POC", msg, p);
    }

    static void PrepareFolders()
    {
        if (AssetDatabase.IsValidFolder(GenRoot))
            AssetDatabase.DeleteAsset(GenRoot);

        EnsureFolder(PocRoot);
        EnsureFolder(SceneFolder);
        EnsureFolder(GenRoot);
        EnsureFolder(GenMaterials);
        EnsureFolder(GenPrefabs);
        EnsureFolder(GenTerrain);
        EnsureFolder(GenLayers);
        EnsureFolder(GenSettings);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        int slash = path.LastIndexOf('/');
        string parent = path.Substring(0, slash);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
    }

    // ================================================================== vegetation library

    static void BuildVegetationLibrary()
    {
        // Trees from the Tree9 pack (Tree Creator meshes with URP materials)
        for (int i = 2; i <= 5; i++)
            AddTree("Tree9_" + i, $"Assets/Tree9/Tree9_{i}.prefab", null, null);

        // Tom's Terrain Tools sample Tree Creator trees
        for (int i = 1; i <= 3; i++)
            AddTree("SampleTree" + i, $"{TomsTrees}/SampleTree{i}.prefab", null, null);

        // Classic Unity terrain trees (materials rebuilt for URP from their source textures)
        AddTree("Alder", TomsAO + "/Models/Alder.fbx", TomsAO + "/Textures/AlderBark.PSD", TomsAO + "/Textures/AlderBranches.psd");
        AddTree("Mimosa", TomsAO + "/Models/Mimosa.fbx", TomsAO + "/Textures/MimosaBark.PSD", TomsAO + "/Textures/MimosaBranch.psd");
        AddTree("ScotsPineA", TomsAO + "/Models/ScotsPineTypeA.fbx", TomsAO + "/Textures/ScotsPineTrunk.psd", TomsAO + "/Textures/ScotsPinebranches.psd");
        AddTree("ScotsPineB", TomsAO + "/Models/ScotsPineTypeB.fbx", TomsAO + "/Textures/ScotsPineTrunk.psd", TomsAO + "/Textures/ScotsPinebranches.psd");
        AddTree("Sycamore", TomsAO + "/Models/Sycamore.fbx", TomsAO + "/Textures/SycamoreBark4.psd", TomsAO + "/Textures/SycamoreLeaves5.psd");
        AddTree("ThinTree", TomsAO + "/Models/ThinTree.fbx", TomsAO + "/Textures/ThinTree.psd", TomsAO + "/Textures/ThinTree.psd");
        AddTree("Bush5", TomsAssets + "/Bushes/Model/Bush5.fbx", TomsAssets + "/Bushes/Textures/Bush5-tif.tif", TomsAssets + "/Bushes/Textures/Bush5-tif.tif");
        AddTree("Bush7", TomsAssets + "/Bushes/Model/Bush7.fbx", TomsAssets + "/Bushes/Textures/Bush7-tif.tif", TomsAssets + "/Bushes/Textures/Bush7-tif.tif");

        // Rocks, textured with the Terrain Sample rock layer
        AddTree("Rock", TomsAssets + "/Rocks/Models/RockMesh.fbx", null, null, rock: true);

        // Detail meshes (grass, ferns, plants, heather, bushes) from the Terrain Sample Asset Pack
        foreach (var name in new[]
                 {
                     "Grass_A", "Grass_B", "Grass_C", "Grass_D", "GrassDry_A", "GrassDry_B", "GrassDry_C",
                     "Fern_A", "Fern_B", "Fern_C", "Plant_A", "Plant_B", "Plant_C", "Plant_D",
                     "Heather_A", "Heather_B", "Bush_A", "Bush_B", "BushDry_A", "BushDry_B"
                 })
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{SampleAssets}/Prefabs/{name}.prefab");
            if (go != null) DetailPrefabs[name] = go;
            else Warnings.Add("Missing detail prefab: " + name);
        }
    }

    static void AddTree(string key, string sourcePath, string barkTexPath, string leafTexPath, bool rock = false)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
        if (src == null)
        {
            Warnings.Add($"Tree source not found, skipped: {key} ({sourcePath})");
            return;
        }

        var barkTex = barkTexPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(barkTexPath) : null;
        var leafTex = leafTexPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(leafTexPath) : null;
        if (barkTexPath != null && barkTex == null) Warnings.Add("Texture not found: " + barkTexPath);
        if (leafTexPath != null && leafTex == null) Warnings.Add("Texture not found: " + leafTexPath);

        var model = (GameObject)UnityEngine.Object.Instantiate(src);
        model.name = "Model";
        model.transform.position = Vector3.zero;

        // Keep only LOD0 if the source has LODs
        foreach (var lg in model.GetComponentsInChildren<LODGroup>(true))
        {
            var lods = lg.GetLODs();
            if (lods.Length > 1)
            {
                var keep = new HashSet<Renderer>(lods[0].renderers.Where(r => r != null));
                foreach (var r in lg.GetComponentsInChildren<Renderer>(true))
                    if (!keep.Contains(r)) UnityEngine.Object.DestroyImmediate(r);
            }
            UnityEngine.Object.DestroyImmediate(lg);
        }

        foreach (var c in model.GetComponentsInChildren<UnityEngine.Tree>(true)) UnityEngine.Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(c);

        var renderers = model.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length == 0)
        {
            Warnings.Add($"No mesh renderers in {sourcePath}, skipped.");
            UnityEngine.Object.DestroyImmediate(model);
            return;
        }

        foreach (var r in renderers)
        {
            var mats = r.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
                mats[m] = ConvertMaterial(key, mats[m], barkTex, leafTex, rock);
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        // Sit the model on the ground (pivot at the base)
        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        float sink = rock ? bounds.size.y * 0.3f : 0.1f;
        model.transform.position = new Vector3(0f, -bounds.min.y - sink, 0f);

        var root = new GameObject("POC_" + key);
        model.transform.SetParent(root.transform, true);
        var lod = root.AddComponent<LODGroup>();
        lod.SetLODs(new[] { new LOD(0.0015f, renderers.Cast<Renderer>().ToArray()) });
        lod.RecalculateBounds();

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{GenPrefabs}/POC_{key}.prefab");
        UnityEngine.Object.DestroyImmediate(root);

        if (prefab != null)
        {
            TreePrefabs[key] = prefab;
            TreeHeights[key] = Mathf.Max(0.1f, bounds.size.y - sink);
        }
    }

    static Material ConvertMaterial(string key, Material src, Texture2D barkTex, Texture2D leafTex, bool rock)
    {
        string srcName = src != null ? src.name : "none";
        string shaderName = src != null && src.shader != null ? src.shader.name : "";

        string role;
        if (rock) role = "rock";
        else if (shaderName.Contains("Leaves") || shaderName.Contains("Leaf")) role = "leaf";
        else if (shaderName.Contains("Bark")) role = "bark";
        else
        {
            string n = srcName.ToLowerInvariant();
            role = (n.Contains("bark") || n.Contains("trunk") || n.Contains("wood") || n.Contains("stem")) ? "bark" : "leaf";
        }

        Texture tex = null, normal = null;
        if (role == "rock")
        {
            var rockLayer = AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{SampleAssets}/TerrainLayers/Rock_TerrainLayer.terrainlayer");
            if (rockLayer != null) { tex = rockLayer.diffuseTexture; normal = rockLayer.normalMapTexture; }
        }
        else
        {
            tex = role == "bark" ? (barkTex != null ? barkTex : null) : (leafTex != null ? leafTex : null);
            if (tex == null && src != null) tex = src.mainTexture;
            if (src != null && src.HasProperty("_BumpMap")) normal = src.GetTexture("_BumpMap");
        }

        string cacheKey = $"{(src != null ? src.GetInstanceID() : 0)}_{role}_{(tex != null ? tex.GetInstanceID() : 0)}";
        if (MaterialCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.name = $"POC_{key}_{role}_{MaterialCache.Count}";

        Color tint = Color.white;
        if (src != null)
        {
            if (src.HasProperty("_BaseColor")) tint = src.GetColor("_BaseColor");
            else if (src.HasProperty("_Color")) tint = src.GetColor("_Color");
            if (Mathf.Max(tint.r, Mathf.Max(tint.g, tint.b)) < 0.35f) tint = Color.white;
            tint.a = 1f;
        }
        if (role == "rock") tint = new Color(0.82f, 0.8f, 0.76f, 1f);

        if (tex != null) mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", tint);
        if (normal != null)
        {
            mat.SetTexture("_BumpMap", normal);
            mat.EnableKeyword("_NORMALMAP");
        }
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", role == "leaf" ? 0.18f : 0.08f);

        if (role == "leaf")
        {
            float cutoff = 0.4f;
            if (src != null && src.HasProperty("_Cutoff")) cutoff = Mathf.Clamp(src.GetFloat("_Cutoff"), 0.25f, 0.6f);
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", cutoff);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)RenderQueue.AlphaTest;
            mat.doubleSidedGI = true;
        }

        mat.enableInstancing = true;
        AssetDatabase.CreateAsset(mat, $"{GenMaterials}/{mat.name}.mat");
        MaterialCache[cacheKey] = mat;
        return mat;
    }

    static TerrainLayer Layer(string name, float tile)
    {
        string cacheKey = name + "_" + tile;
        if (LayerCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var src = AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{SampleAssets}/TerrainLayers/{name}_TerrainLayer.terrainlayer");
        if (src == null)
        {
            Warnings.Add("Missing terrain layer: " + name);
            return null;
        }

        var layer = UnityEngine.Object.Instantiate(src);
        layer.name = $"POC_{name}_{tile:0}m";
        layer.tileSize = new Vector2(tile, tile);
        layer.tileOffset = Vector2.zero;
        AssetDatabase.CreateAsset(layer, $"{GenLayers}/{layer.name}.terrainlayer");
        LayerCache[cacheKey] = layer;
        return layer;
    }

    // ================================================================== biome definitions

    static float S(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    static TreeRule T(string[] keys, float spacing, float prob, float minH, float maxH, float clearance, Func<Ctx, float> mask = null,
        float minW = 0.9f, float maxW = 1.1f, float maxSlope = 30f)
    {
        return new TreeRule { keys = keys, spacing = spacing, probability = prob, minH = minH, maxH = maxH, clearance = clearance, mask = mask, minW = minW, maxW = maxW, maxSlope = maxSlope };
    }

    static DetailRule D(string key, float density, float minScale, float maxScale, Func<Ctx, float> mask = null, float maxSlope = 35f)
    {
        return new DetailRule { key = key, density = density, minScale = minScale, maxScale = maxScale, mask = mask, maxSlope = maxSlope };
    }

    static List<Biome> DefineBiomes()
    {
        var list = new List<Biome>();

        // 1 ─ Temperate broadleaf forest ------------------------------------------------
        var broadleaf = new Biome
        {
            title = "Temperate Broadleaf Forest",
            subtitle = "Dense deciduous canopy with a fern and shrub understorey",
            species = "Broadleaf trees · Sycamore · Alder · Ferns · Woodland plants",
            accent = new Color(0.42f, 0.72f, 0.33f),
            seed = 101, baseHeight = 2f, amplitude = 9f, frequency = 0.012f,
            layers = new[] { "Grass_Moss", "Grass_Soil", "Muddy" },
            tiles = new[] { 3f, 4f, 3f },
            splat = c => new Vector3(1f, S(0.55f, 0.75f, c.n2) + S(22f, 32f, c.slope), S(0.32f, 0.18f, c.n) * 0.9f),
        };
        broadleaf.trees.Add(T(new[] { "Tree9_2", "Tree9_3", "Tree9_4", "Tree9_5", "Sycamore", "SampleTree1", "SampleTree2", "SampleTree3" },
            9f, 0.85f, 13f, 21f, 6.5f, c => 0.55f + 0.45f * S(0.3f, 0.6f, c.n)));
        broadleaf.trees.Add(T(new[] { "Alder" }, 14f, 0.45f, 9f, 13f, 4.5f));
        broadleaf.trees.Add(T(new[] { "Bush5", "Bush7" }, 7f, 0.35f, 1.2f, 2.8f, 2.5f, null, 0.9f, 1.4f));
        broadleaf.details.Add(D("Fern_A", 0.35f, 0.8f, 1.35f, c => 0.25f + 0.75f * S(0.4f, 0.65f, c.n2)));
        broadleaf.details.Add(D("Fern_B", 0.25f, 0.8f, 1.3f, c => 0.2f + 0.8f * S(0.45f, 0.7f, c.n3)));
        broadleaf.details.Add(D("Fern_C", 0.18f, 0.8f, 1.2f, c => S(0.5f, 0.75f, c.n2)));
        broadleaf.details.Add(D("Plant_A", 0.07f, 0.8f, 1.2f, c => S(0.55f, 0.75f, c.n3)));
        broadleaf.details.Add(D("Plant_B", 0.07f, 0.8f, 1.2f, c => S(0.45f, 0.25f, c.n3)));
        broadleaf.details.Add(D("Bush_A", 0.025f, 0.8f, 1.3f));
        broadleaf.details.Add(D("Grass_C", 0.35f, 0.7f, 1.1f, c => S(0.45f, 0.3f, c.n)));
        list.Add(broadleaf);

        // 2 ─ Coniferous pine forest --------------------------------------------------
        var pine = new Biome
        {
            title = "Coniferous Pine Forest",
            subtitle = "Tall Scots pine stands on rocky, needle-covered hills",
            species = "Scots pine (two forms) · Heather · Ferns · Dry grasses · Rocks",
            accent = new Color(0.25f, 0.55f, 0.5f),
            seed = 202, baseHeight = 2f, amplitude = 18f, frequency = 0.009f,
            layers = new[] { "Soil_Rocks", "Grass_Moss", "Rock" },
            tiles = new[] { 4f, 3f, 6f },
            splat = c => new Vector3(1f, S(0.5f, 0.7f, c.n2) * (1f - S(18f, 28f, c.slope)), S(20f, 32f, c.slope)),
        };
        pine.trees.Add(T(new[] { "ScotsPineA", "ScotsPineB" }, 7.5f, 0.85f, 16f, 25f, 5f, c => 0.6f + 0.4f * c.n, 0.85f, 1.1f, 32f));
        pine.trees.Add(T(new[] { "ScotsPineA", "ScotsPineB" }, 11f, 0.3f, 5f, 9f, 3f));
        pine.trees.Add(T(new[] { "Rock" }, 13f, 0.35f, 0.8f, 2.6f, 3f, c => c.slope > 15f ? 1f : 0.35f, 0.9f, 1.6f, 45f));
        pine.details.Add(D("Heather_A", 0.25f, 0.8f, 1.2f, c => S(0.5f, 0.7f, c.n2), 28f));
        pine.details.Add(D("Fern_A", 0.15f, 0.8f, 1.2f, c => S(0.45f, 0.7f, c.n3)));
        pine.details.Add(D("GrassDry_A", 0.2f, 0.7f, 1.1f, c => 0.4f + 0.6f * S(0.4f, 0.6f, c.n)));
        pine.details.Add(D("Grass_C", 0.1f, 0.7f, 1.0f));
        pine.details.Add(D("BushDry_A", 0.02f, 0.8f, 1.2f));
        list.Add(pine);

        // 3 ─ Wildflower meadow ---------------------------------------------------------
        var meadow = new Biome
        {
            title = "Wildflower Meadow",
            subtitle = "Open rolling grassland with flowering plants and solitary trees",
            species = "Meadow grasses · Flowering plants · Solitary sycamore & broadleaf trees",
            accent = new Color(0.78f, 0.78f, 0.3f),
            seed = 303, baseHeight = 1f, amplitude = 4f, frequency = 0.01f,
            layers = new[] { "Grass_A", "Grass_B", "Grass_Soil" },
            tiles = new[] { 3f, 3f, 4f },
            splat = c => new Vector3(1f, S(0.45f, 0.65f, c.n), S(0.75f, 0.88f, c.n2) * 0.8f),
            groundView = new Vector2(80f, 30f),
        };
        meadow.trees.Add(T(new[] { "Sycamore", "Tree9_3", "Tree9_4" }, 34f, 0.45f, 14f, 20f, 20f, null, 1.0f, 1.25f));
        meadow.trees.Add(T(new[] { "Bush5", "Bush7" }, 9f, 0.6f, 1.2f, 2.4f, 2.5f, c => S(0.68f, 0.78f, c.n3), 0.9f, 1.4f));
        meadow.details.Add(D("Grass_A", 1.1f, 0.8f, 1.3f, c => 0.35f + 0.65f * S(0.6f, 0.4f, c.n)));
        meadow.details.Add(D("Grass_B", 0.8f, 0.8f, 1.3f, c => 0.35f + 0.65f * S(0.4f, 0.6f, c.n)));
        meadow.details.Add(D("Grass_D", 0.5f, 0.8f, 1.2f));
        meadow.details.Add(D("Grass_C", 0.25f, 0.8f, 1.2f, c => S(0.5f, 0.7f, c.n2)));
        meadow.details.Add(D("Plant_A", 0.12f, 0.8f, 1.2f, c => S(0.55f, 0.72f, c.n3)));
        meadow.details.Add(D("Plant_B", 0.12f, 0.8f, 1.2f, c => S(0.45f, 0.28f, c.n3)));
        meadow.details.Add(D("Plant_C", 0.1f, 0.8f, 1.2f, c => S(0.6f, 0.78f, c.n2)));
        meadow.details.Add(D("Plant_D", 0.1f, 0.8f, 1.2f, c => S(0.4f, 0.22f, c.n2)));
        list.Add(meadow);

        // 4 ─ Heathland & moor --------------------------------------------------------
        var heath = new Biome
        {
            title = "Heathland & Moor",
            subtitle = "Windswept heather carpets, dry grasses and rocky outcrops",
            species = "Heather · Dry grasses · Dry shrubs · Sparse stunted pine & thin trees",
            accent = new Color(0.62f, 0.38f, 0.62f),
            seed = 404, baseHeight = 2f, amplitude = 10f, frequency = 0.01f,
            shape = (b, x, z, h) =>
            {
                float r = Mathf.PerlinNoise(x * 0.03f + b.off4.x, z * 0.03f + b.off4.y);
                float ridge = 1f - Mathf.Abs(2f * r - 1f);
                return h + Mathf.Pow(ridge, 3f) * 7f * S(0.45f, 0.65f, Mathf.PerlinNoise(x * 0.008f + b.off3.x, z * 0.008f + b.off3.y));
            },
            layers = new[] { "Heather", "Grass_Dry", "Rock" },
            tiles = new[] { 3f, 3f, 6f },
            splat = c => new Vector3(1f, S(0.5f, 0.7f, c.n2) * 0.9f, S(16f, 28f, c.slope)),
        };
        heath.trees.Add(T(new[] { "ThinTree" }, 18f, 0.35f, 6f, 10f, 6f));
        heath.trees.Add(T(new[] { "ScotsPineB" }, 24f, 0.3f, 7f, 12f, 8f, null, 0.9f, 1.2f));
        heath.trees.Add(T(new[] { "Rock" }, 8f, 1f, 0.8f, 3.2f, 3f, c => c.slope > 14f ? 0.8f : 0.1f, 0.9f, 1.6f, 50f));
        heath.details.Add(D("Heather_A", 1.0f, 0.8f, 1.3f, c => 1f - 0.6f * S(0.55f, 0.75f, c.n2), 24f));
        heath.details.Add(D("Heather_B", 0.8f, 0.8f, 1.3f, c => 0.4f + 0.6f * S(0.4f, 0.6f, c.n3), 24f));
        heath.details.Add(D("GrassDry_A", 0.4f, 0.7f, 1.1f, c => S(0.45f, 0.7f, c.n2), 28f));
        heath.details.Add(D("GrassDry_B", 0.3f, 0.7f, 1.1f, c => S(0.5f, 0.75f, c.n2), 28f));
        heath.details.Add(D("BushDry_A", 0.03f, 0.8f, 1.3f));
        heath.details.Add(D("BushDry_B", 0.03f, 0.8f, 1.3f));
        list.Add(heath);

        // 5 ─ Dry savanna -------------------------------------------------------------
        var savanna = new Biome
        {
            title = "Dry Savanna Scrubland",
            subtitle = "Golden grassland with umbrella-crowned trees and dry scrub",
            species = "Mimosa (acacia-like) trees · Dry grasses · Dry shrubs · Scattered rocks",
            accent = new Color(0.86f, 0.62f, 0.25f),
            seed = 505, baseHeight = 1f, amplitude = 5f, frequency = 0.009f,
            layers = new[] { "Grass_Dry", "Sand", "Soil_Rocks" },
            tiles = new[] { 3f, 3f, 4f },
            splat = c => new Vector3(1f, S(0.55f, 0.72f, c.n), S(0.65f, 0.8f, c.n2) * 0.8f),
            groundView = new Vector2(80f, 30f),
        };
        savanna.trees.Add(T(new[] { "Mimosa" }, 22f, 0.55f, 7f, 11f, 12f, null, 1.2f, 1.6f));
        savanna.trees.Add(T(new[] { "Bush5", "Bush7" }, 12f, 0.25f, 1.0f, 2.0f, 3f, null, 1.0f, 1.5f));
        savanna.trees.Add(T(new[] { "Rock" }, 25f, 0.15f, 0.6f, 1.8f, 3f, null, 0.9f, 1.6f, 45f));
        Func<Ctx, float> notSand = c => 1f - 0.85f * S(0.55f, 0.72f, c.n);
        savanna.details.Add(D("GrassDry_A", 0.9f, 0.8f, 1.3f, notSand));
        savanna.details.Add(D("GrassDry_B", 0.7f, 0.8f, 1.3f, c => notSand(c) * (0.4f + 0.6f * S(0.4f, 0.6f, c.n3))));
        savanna.details.Add(D("GrassDry_C", 0.5f, 0.8f, 1.3f, c => notSand(c) * (0.4f + 0.6f * S(0.6f, 0.4f, c.n3))));
        savanna.details.Add(D("BushDry_A", 0.04f, 0.8f, 1.4f));
        savanna.details.Add(D("BushDry_B", 0.04f, 0.8f, 1.4f));
        list.Add(savanna);

        // 6 ─ Riparian wetland ---------------------------------------------------------
        var wetland = new Biome
        {
            title = "Riparian Wetland",
            subtitle = "Marsh pond fringed by reeds, ferns and alder woodland",
            species = "Alder · Broadleaf trees · Reed grass · Ferns · Marsh plants",
            accent = new Color(0.3f, 0.58f, 0.82f),
            seed = 606, baseHeight = 4f, amplitude = 1.8f, frequency = 0.014f,
            waterLevel = 2.6f, pondCenter = new Vector2(80f, 88f), pondRadius = 30f,
            shape = (b, x, z, h) =>
            {
                float dx = x - b.pondCenter.x, dz = z - b.pondCenter.y;
                float wobble = 1f + 0.35f * (Mathf.PerlinNoise(x * 0.04f + b.off4.x, z * 0.04f + b.off4.y) - 0.5f) * 2f;
                float d = Mathf.Sqrt(dx * dx + dz * dz) / (b.pondRadius * wobble);
                return Mathf.Lerp(h, 0.9f, S(1.0f, 0.6f, d));
            },
            layers = new[] { "Grass_B", "Muddy", "Tidal_Pools" },
            tiles = new[] { 3f, 3f, 4f },
            splat = c => new Vector3(1f, S(-1.6f, -0.3f, c.depth) + S(0.62f, 0.8f, c.n2) * 0.5f, S(-0.2f, 0.4f, c.depth)),
            groundView = new Vector2(80f, 32f),
            groundLookAt = new Vector2(80f, 88f),
        };
        // Thresholds are in "depth" (water level - ground height). The plateau sits at 4.0-5.8 m against a
        // 2.6 m water level, i.e. depth -1.4 (low ground) to -3.2 (high ground); the pond bank spans 0 to -1.4.
        Func<Ctx, float> shore = c => S(-2.4f, -1.4f, c.depth) * S(-0.2f, -0.9f, c.depth);
        wetland.trees.Add(T(new[] { "Alder" }, 7f, 0.9f, 9f, 15f, 4.5f, c => 0.3f + 0.7f * S(-2.4f, -1.4f, c.depth)));
        wetland.trees.Add(T(new[] { "Tree9_2", "Tree9_5", "Sycamore" }, 16f, 0.6f, 13f, 19f, 7f, c => S(-2.0f, -2.8f, c.depth)));
        wetland.trees.Add(T(new[] { "Bush5", "Bush7" }, 6f, 0.5f, 1.2f, 2.5f, 2.2f, c => 0.3f + 0.7f * shore(c), 0.9f, 1.4f));
        wetland.details.Add(D("Grass_C", 2.0f, 0.9f, 1.5f, c => Mathf.Max(0.12f, S(-1.3f, -0.6f, c.depth) * S(0.5f, -0.4f, c.depth))));
        wetland.details.Add(D("Fern_A", 0.25f, 0.8f, 1.3f, c => S(-5f, -3f, c.depth) * S(-0.6f, -1.6f, c.depth)));
        wetland.details.Add(D("Fern_B", 0.2f, 0.8f, 1.3f, c => S(-1.2f, -2.2f, c.depth) * S(0.45f, 0.7f, c.n3)));
        wetland.details.Add(D("Grass_A", 0.6f, 0.8f, 1.2f, c => S(-1.4f, -2.4f, c.depth)));
        wetland.details.Add(D("Plant_A", 0.1f, 0.8f, 1.2f, c => S(-0.6f, -1.6f, c.depth) * S(0.5f, 0.7f, c.n2)));
        wetland.details.Add(D("Plant_C", 0.1f, 0.8f, 1.2f, c => S(-1.0f, -2.5f, c.depth) * S(0.5f, 0.3f, c.n2)));
        wetland.details.Add(D("Bush_A", 0.02f, 0.8f, 1.2f, c => S(-1.0f, -2.0f, c.depth)));
        list.Add(wetland);

        foreach (var biome in list)
        {
            var rng = new System.Random(biome.seed);
            biome.off1 = new Vector2(rng.Next(100, 9000), rng.Next(100, 9000));
            biome.off2 = new Vector2(rng.Next(100, 9000), rng.Next(100, 9000));
            biome.off3 = new Vector2(rng.Next(100, 9000), rng.Next(100, 9000));
            biome.off4 = new Vector2(rng.Next(100, 9000), rng.Next(100, 9000));
        }
        return list;
    }

    // ================================================================== terrain generation

    static float Fbm(float x, float z, float freq, int octaves, Vector2 off)
    {
        float sum = 0f, amp = 1f, norm = 0f, f = freq;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * Mathf.PerlinNoise(x * f + off.x + i * 17.13f, z * f + off.y + i * 31.71f);
            norm += amp;
            amp *= 0.5f;
            f *= 2.03f;
        }
        return Mathf.Clamp01((sum / norm - 0.5f) * 1.9f + 0.5f);
    }

    static float EdgeDistance(float x, float z) => Mathf.Min(Mathf.Min(x, z), Mathf.Min(TerrainSize - x, TerrainSize - z));

    static float HeightAt(Biome b, float x, float z)
    {
        float h = b.baseHeight + b.amplitude * Fbm(x, z, b.frequency, 4, b.off1);
        if (b.shape != null) h = b.shape(b, x, z, h);
        float e = S(BorderWidth, BorderWidth + RampWidth, EdgeDistance(x, z));
        return Mathf.Clamp(h * e, 0f, TerrainHeight - 1f);
    }

    static Ctx Context(Biome b, TerrainData td, float x, float z)
    {
        float u = x / TerrainSize, v = z / TerrainSize;
        var c = new Ctx
        {
            x = x, z = z,
            h = td.GetInterpolatedHeight(u, v),
            slope = td.GetSteepness(u, v),
            n = Fbm(x, z, 0.012f, 3, b.off2),
            n2 = Fbm(x, z, 0.035f, 3, b.off3),
            n3 = Fbm(x, z, 0.07f, 2, b.off4),
            edge = EdgeDistance(x, z),
        };
        c.depth = b.waterLevel > 0f ? b.waterLevel - c.h : -999f;
        return c;
    }

    static Terrain BuildBiomeTerrain(Biome b, int index, Transform parent)
    {
        var td = new TerrainData();
        td.heightmapResolution = HeightmapRes;
        td.size = new Vector3(TerrainSize, TerrainHeight, TerrainSize);
        td.alphamapResolution = AlphamapRes;
        td.baseMapResolution = 512;
        td.SetDetailResolution(DetailRes, DetailPatchRes);
        td.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
        td.name = $"POC_Terrain_{index + 1}";

        // ---- heights
        var heights = new float[HeightmapRes, HeightmapRes];
        for (int iy = 0; iy < HeightmapRes; iy++)
        for (int ix = 0; ix < HeightmapRes; ix++)
        {
            float x = ix / (float)(HeightmapRes - 1) * TerrainSize;
            float z = iy / (float)(HeightmapRes - 1) * TerrainSize;
            heights[iy, ix] = HeightAt(b, x, z) / TerrainHeight;
        }
        td.SetHeights(0, 0, heights);

        // ---- layers (3 biome layers + shared gravel border)
        var layers = new List<TerrainLayer>();
        for (int i = 0; i < 3; i++) layers.Add(Layer(b.layers[i], b.tiles[i]));
        layers.Add(Layer("Pebbles_B", GravelTile));
        td.terrainLayers = layers.Where(l => l != null).ToArray();
        bool layersOk = td.terrainLayers.Length == 4;
        if (!layersOk) Warnings.Add($"{b.title}: some terrain layers are missing; splat painting simplified.");

        // ---- splatmap
        int layerCount = td.terrainLayers.Length;
        var alpha = new float[AlphamapRes, AlphamapRes, layerCount];
        for (int iy = 0; iy < AlphamapRes; iy++)
        for (int ix = 0; ix < AlphamapRes; ix++)
        {
            float x = (ix + 0.5f) / AlphamapRes * TerrainSize;
            float z = (iy + 0.5f) / AlphamapRes * TerrainSize;
            var c = Context(b, td, x, z);
            float inner = S(BorderWidth - 1.5f, BorderWidth + 2.5f, c.edge);

            if (!layersOk)
            {
                alpha[iy, ix, 0] = 1f;
                continue;
            }

            var w = b.splat(c);
            float w0 = Mathf.Max(0f, w.x), w1 = Mathf.Clamp01(w.y), w2 = Mathf.Clamp01(w.z);
            // layer 2 dominates layer 1 which dominates layer 0 (simple priority blend)
            float l2 = w2;
            float l1 = w1 * (1f - l2);
            float l0 = w0 * (1f - l1 - l2);
            float sum = l0 + l1 + l2;
            if (sum <= 0.0001f) { l0 = 1f; sum = 1f; }
            alpha[iy, ix, 0] = l0 / sum * inner;
            alpha[iy, ix, 1] = l1 / sum * inner;
            alpha[iy, ix, 2] = l2 / sum * inner;
            alpha[iy, ix, 3] = 1f - inner;
        }
        td.SetAlphamaps(0, 0, alpha);

        // ---- trees
        PlaceTrees(b, td);

        // ---- details
        PlaceDetails(b, td);

        AssetDatabase.CreateAsset(td, $"{GenTerrain}/{td.name}.asset");

        // ---- game objects
        var biomeRoot = new GameObject($"{index + 1:00} - {b.title}");
        biomeRoot.transform.SetParent(parent, false);

        var go = Terrain.CreateTerrainGameObject(td);
        go.name = "Terrain - " + b.title;
        go.transform.SetParent(biomeRoot.transform, false);
        go.transform.position = b.origin;

        var terrain = go.GetComponent<Terrain>();
        ConfigureTerrain(terrain, index + 1);

        if (b.waterLevel > 0f)
            BuildWater(b, biomeRoot.transform);

        return terrain;
    }

    static void ConfigureTerrain(Terrain t, int groupId)
    {
        var lit = AssetDatabase.LoadAssetAtPath<Material>(TerrainLitMaterialPath);
        if (lit != null) t.materialTemplate = lit;
        else Warnings.Add("URP TerrainLit material not found; terrain may render with the default material.");
        t.drawInstanced = true;
        t.allowAutoConnect = false;
        t.groupingID = 100 + groupId;
        t.heightmapPixelError = 3f;
        t.basemapDistance = 600f;
        t.treeDistance = 2000f;
        t.treeBillboardDistance = 2000f;
        t.treeMaximumFullLODCount = 5000;
        t.detailObjectDistance = 160f;
        t.detailObjectDensity = 1f;
        t.shadowCastingMode = ShadowCastingMode.TwoSided;
    }

    static void PlaceTrees(Biome b, TerrainData td)
    {
        var protoIndex = new Dictionary<string, int>();
        var protos = new List<TreePrototype>();
        foreach (var rule in b.trees)
        foreach (var key in rule.keys)
        {
            if (protoIndex.ContainsKey(key) || !TreePrefabs.ContainsKey(key)) continue;
            protoIndex[key] = protos.Count;
            protos.Add(new TreePrototype { prefab = TreePrefabs[key], bendFactor = 0f });
        }
        td.treePrototypes = protos.ToArray();
        if (protos.Count == 0)
        {
            Warnings.Add($"{b.title}: no tree prefabs available.");
            return;
        }

        var rng = new System.Random(b.seed * 7 + 1);
        var placed = new List<Vector3>(); // x, z, clearance
        var instances = new List<TreeInstance>();

        foreach (var rule in b.trees)
        {
            var keys = rule.keys.Where(k => protoIndex.ContainsKey(k)).ToArray();
            if (keys.Length == 0) continue;

            int cells = Mathf.CeilToInt(TerrainSize / rule.spacing);
            for (int gz = 0; gz < cells; gz++)
            for (int gx = 0; gx < cells; gx++)
            {
                float x = (gx + 0.5f + (float)(rng.NextDouble() - 0.5) * 0.9f) * rule.spacing;
                float z = (gz + 0.5f + (float)(rng.NextDouble() - 0.5) * 0.9f) * rule.spacing;
                double roll = rng.NextDouble();
                if (x < 0f || z < 0f || x > TerrainSize || z > TerrainSize) continue;

                var c = Context(b, td, x, z);
                if (c.edge < TreeEdgeClearance || c.slope > rule.maxSlope) continue;
                if (b.waterLevel > 0f && c.depth > -0.4f) continue;
                if (Vector2.Distance(new Vector2(x, z), b.groundView) < 9f) continue;

                float p = rule.probability * (rule.mask != null ? Mathf.Clamp01(rule.mask(c)) : 1f);
                if (roll >= p) continue;

                bool blocked = false;
                foreach (var q in placed)
                {
                    float min = (rule.clearance + q.z) * 0.5f;
                    float dx = q.x - x, dz = q.y - z;
                    if (dx * dx + dz * dz < min * min) { blocked = true; break; }
                }
                if (blocked) continue;

                string key = keys[rng.Next(keys.Length)];
                float baseH = TreeHeights[key];
                float hs = Mathf.Lerp(rule.minH, rule.maxH, (float)rng.NextDouble()) / baseH;
                float ws = hs * Mathf.Lerp(rule.minW, rule.maxW, (float)rng.NextDouble());

                instances.Add(new TreeInstance
                {
                    position = new Vector3(x / TerrainSize, 0f, z / TerrainSize),
                    prototypeIndex = protoIndex[key],
                    heightScale = hs,
                    widthScale = ws,
                    rotation = (float)(rng.NextDouble() * Math.PI * 2.0),
                    color = Color.white,
                    lightmapColor = Color.white,
                });
                placed.Add(new Vector3(x, z, rule.clearance));
            }
        }

        td.SetTreeInstances(instances.ToArray(), true);
    }

    static void PlaceDetails(Biome b, TerrainData td)
    {
        var rules = b.details.Where(d => DetailPrefabs.ContainsKey(d.key)).ToList();
        foreach (var missing in b.details.Where(d => !DetailPrefabs.ContainsKey(d.key)))
            Warnings.Add($"{b.title}: detail '{missing.key}' not available, skipped.");

        td.detailPrototypes = rules.Select(d => new DetailPrototype
        {
            prototype = DetailPrefabs[d.key],
            usePrototypeMesh = true,
            renderMode = DetailRenderMode.VertexLit,
            useInstancing = true,
            minWidth = d.minScale,
            maxWidth = d.maxScale,
            minHeight = d.minScale,
            maxHeight = d.maxScale * 1.05f,
            noiseSpread = 0.35f,
            healthyColor = Color.white,
            dryColor = Color.white,
            // Defaults are 0 / false: without jitter, non-legacy distribution places instances on a
            // regular grid; density scaling lets the terrain's "Detail Density" setting apply.
            positionJitter = 1f,
            useDensityScaling = true,
        }).ToArray();

        if (rules.Count == 0) return;

        float cell = TerrainSize / DetailRes;
        float cellArea = cell * cell;
        var maps = rules.Select(_ => new int[DetailRes, DetailRes]).ToList();
        var rng = new System.Random(b.seed * 13 + 5);

        for (int iy = 0; iy < DetailRes; iy++)
        for (int ix = 0; ix < DetailRes; ix++)
        {
            float x = (ix + 0.5f) * cell, z = (iy + 0.5f) * cell;
            float edge = EdgeDistance(x, z);
            if (edge < BorderWidth + 1f) continue;

            var c = Context(b, td, x, z);
            if (b.waterLevel > 0f && c.depth > -0.05f) continue;
            float edgeFade = S(BorderWidth + 1f, BorderWidth + 5f, edge);

            for (int r = 0; r < rules.Count; r++)
            {
                var rule = rules[r];
                if (c.slope > rule.maxSlope) continue;
                float m = (rule.mask != null ? Mathf.Clamp01(rule.mask(c)) : 1f) * edgeFade;
                float expected = rule.density * cellArea * m;
                if (expected <= 0f) continue;
                int count = (int)expected;
                if (rng.NextDouble() < expected - count) count++;
                maps[r][iy, ix] = Mathf.Min(count, 16);
            }
        }

        for (int r = 0; r < rules.Count; r++)
            td.SetDetailLayer(0, 0, r, maps[r]);
    }

    static void BuildWater(Biome b, Transform parent)
    {
        var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
        water.name = "Pond Water";
        UnityEngine.Object.DestroyImmediate(water.GetComponent<Collider>());
        water.transform.SetParent(parent, false);
        float size = b.pondRadius * 2.8f;
        water.transform.position = b.origin + new Vector3(b.pondCenter.x, b.waterLevel, b.pondCenter.y);
        water.transform.localScale = new Vector3(size / 10f, 1f, size / 10f);

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "POC_PondWater" };
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)RenderQueue.Transparent;
        mat.SetColor("_BaseColor", new Color(0.09f, 0.15f, 0.14f, 0.82f));
        mat.SetFloat("_Smoothness", 0.93f);
        mat.SetFloat("_Metallic", 0f);
        var tidal = AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{SampleAssets}/TerrainLayers/Tidal_Pools_TerrainLayer.terrainlayer");
        if (tidal != null && tidal.normalMapTexture != null)
        {
            mat.SetTexture("_BumpMap", tidal.normalMapTexture);
            mat.SetFloat("_BumpScale", 0.15f);
            mat.SetTextureScale("_BaseMap", new Vector2(12f, 12f));
            mat.EnableKeyword("_NORMALMAP");
        }
        AssetDatabase.CreateAsset(mat, $"{GenMaterials}/{mat.name}.mat");
        var mr = water.GetComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
    }

    static void BuildBaseGround()
    {
        float width = GridWidth + Margin * 2f;
        float length = 2 * TerrainSize + Gap + Margin * 2f;

        var td = new TerrainData();
        td.heightmapResolution = 33;
        td.size = new Vector3(width, 10f, length);
        td.alphamapResolution = 64;
        td.baseMapResolution = 256;
        td.name = "POC_BaseGround";
        var gravel = Layer("Pebbles_B", GravelTile);
        if (gravel != null) td.terrainLayers = new[] { gravel };
        AssetDatabase.CreateAsset(td, $"{GenTerrain}/{td.name}.asset");

        var go = Terrain.CreateTerrainGameObject(td);
        go.name = "Base Ground (gravel paths)";
        go.transform.position = new Vector3(-Margin, -0.05f, -Margin);
        ConfigureTerrain(go.GetComponent<Terrain>(), 99);
    }

    // ================================================================== signs & labels

    static Font UiFont()
    {
        if (_font != null) return _font;
        try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { /* older editors */ }
        if (_font == null)
        {
            try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { /* ignore */ }
        }
        return _font;
    }

    static Canvas MakeCanvas(string name, Transform parent, Vector2 sizePx, float metresPerPx)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        go.transform.SetParent(parent, false);
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = sizePx;
        rt.localScale = Vector3.one * metresPerPx;
        go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 1.5f;
        return canvas;
    }

    static RectTransform UiRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        return rt;
    }

    static Image UiImage(string name, Transform parent, Color color, Vector2 aMin, Vector2 aMax, Vector2 oMin, Vector2 oMax)
    {
        var rt = UiRect(name, parent, aMin, aMax, oMin, oMax);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static Text UiText(string name, Transform parent, string text, int size, FontStyle style, Color color, TextAnchor anchor,
        Vector2 aMin, Vector2 aMax, Vector2 oMin, Vector2 oMax)
    {
        var rt = UiRect(name, parent, aMin, aMax, oMin, oMax);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = UiFont();
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = color;
        t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    static Material SolidMaterial(string name, Color c, float smoothness)
    {
        string key = "solid_" + name;
        if (MaterialCache.TryGetValue(key, out var m)) return m;
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", smoothness);
        AssetDatabase.CreateAsset(m, $"{GenMaterials}/{name}.mat");
        MaterialCache[key] = m;
        return m;
    }

    /// <summary>Physical title sign standing in front of each terrain.</summary>
    static void BuildPlaque(Biome b, int index, Transform parent)
    {
        const float width = 34f, height = 7.5f, bottom = 1.6f;
        var root = new GameObject("Title Sign").transform;
        root.SetParent(parent, false);
        root.position = b.origin + new Vector3(TerrainSize * 0.5f, 0f, -11f);

        // backing board + posts
        var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
        board.name = "Board";
        UnityEngine.Object.DestroyImmediate(board.GetComponent<Collider>());
        board.transform.SetParent(root, false);
        board.transform.localPosition = new Vector3(0f, bottom + height * 0.5f, 0.16f);
        board.transform.localScale = new Vector3(width + 0.5f, height + 0.5f, 0.25f);
        board.GetComponent<MeshRenderer>().sharedMaterial = SolidMaterial("POC_SignBoard", new Color(0.07f, 0.08f, 0.075f), 0.35f);

        foreach (float sx in new[] { -1f, 1f })
        {
            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            post.name = "Post";
            UnityEngine.Object.DestroyImmediate(post.GetComponent<Collider>());
            post.transform.SetParent(root, false);
            float h = bottom + height + 0.25f;
            post.transform.localPosition = new Vector3(sx * (width * 0.5f - 1.2f), h * 0.5f - 0.3f, 0.45f);
            post.transform.localScale = new Vector3(0.45f, h * 0.5f + 0.3f, 0.45f);
            post.GetComponent<MeshRenderer>().sharedMaterial = SolidMaterial("POC_SignPost", new Color(0.2f, 0.16f, 0.12f), 0.2f);
        }

        // UI face (1 px = 2 cm)
        var canvas = MakeCanvas("Sign Face", root, new Vector2(width / 0.02f, height / 0.02f), 0.02f);
        canvas.transform.localPosition = new Vector3(0f, bottom + height * 0.5f, 0f);
        var c = canvas.transform;
        UiImage("Background", c, new Color(0.06f, 0.075f, 0.07f, 0.95f), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        UiImage("Accent", c, b.accent, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(30f, 0f));
        UiText("Number", c, (index + 1).ToString("00"), 170, FontStyle.Bold, b.accent, TextAnchor.MiddleCenter,
            Vector2.zero, new Vector2(0f, 1f), new Vector2(50f, 0f), new Vector2(300f, 0f));
        UiImage("Divider", c, new Color(1f, 1f, 1f, 0.15f), Vector2.zero, new Vector2(0f, 1f), new Vector2(310f, 50f), new Vector2(314f, -50f));
        // Text column is 1290 px wide. Sizes are chosen so the longest strings fit on one line
        // (title "Temperate Broadleaf Forest" ≈ 13.3 em at 90 px ≈ 1200 px); each region is tall
        // enough for two lines so a wrap never overlaps the next block.
        UiText("Title", c, b.title, 90, FontStyle.Bold, Color.white, TextAnchor.LowerLeft,
            new Vector2(0f, 0.55f), Vector2.one, new Vector2(360f, 0f), new Vector2(-50f, -24f));
        UiText("Subtitle", c, b.subtitle, 42, FontStyle.Normal, new Color(0.86f, 0.9f, 0.87f), TextAnchor.UpperLeft,
            new Vector2(0f, 0.28f), new Vector2(1f, 0.55f), new Vector2(362f, 0f), new Vector2(-50f, -6f));
        UiText("Species", c, b.species, 38, FontStyle.Italic, new Color(b.accent.r * 0.5f + 0.5f, b.accent.g * 0.5f + 0.5f, b.accent.b * 0.5f + 0.5f), TextAnchor.UpperLeft,
            Vector2.zero, new Vector2(1f, 0.28f), new Vector2(362f, 18f), new Vector2(-50f, 0f));
    }

    /// <summary>Large floating title above each terrain, readable from the overview camera.</summary>
    static void BuildOverheadLabel(Biome b, int index, Transform parent, Vector3 lookFrom)
    {
        var pos = b.origin + new Vector3(TerrainSize * 0.5f, 72f, TerrainSize * 0.45f);
        var canvas = MakeCanvas($"Label {index + 1:00} - {b.title}", parent, new Vector2(1700f, 300f), 0.075f);
        canvas.transform.position = pos;
        canvas.transform.rotation = Quaternion.LookRotation(pos - lookFrom);
        var c = canvas.transform;
        UiImage("Background", c, new Color(0.04f, 0.05f, 0.045f, 0.62f), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        UiImage("Accent", c, b.accent, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 16f));
        // Longest label "01   Temperate Broadleaf Forest" ≈ 15.3 em; 100 px (≈ 1530 px) fits the 1640 px width.
        UiText("Title", c, $"{index + 1:00}   {b.title}", 100, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter,
            Vector2.zero, Vector2.one, new Vector2(30f, 16f), new Vector2(-30f, 0f));
    }

    static void BuildHeaderLabel(Transform parent, Vector3 lookFrom)
    {
        var pos = new Vector3(GridWidth * 0.5f, 105f, 2 * TerrainSize + Gap + 70f);
        var canvas = MakeCanvas("Header - Vegetation Showcase", parent, new Vector2(2600f, 380f), 0.12f);
        canvas.transform.position = pos;
        canvas.transform.rotation = Quaternion.LookRotation(pos - lookFrom);
        var c = canvas.transform;
        // Separate regions so a wrapped title can never run into the subtitle.
        UiText("Title", c, "VEGETATION SHOWCASE", 170, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter,
            new Vector2(0f, 0.4f), Vector2.one, Vector2.zero, Vector2.zero);
        UiText("Subtitle", c, "Six terrain environments  ·  Proof of Concept", 84, FontStyle.Normal, new Color(0.88f, 0.92f, 0.89f), TextAnchor.UpperCenter,
            Vector2.zero, new Vector2(1f, 0.4f), Vector2.zero, Vector2.zero);
    }

    // ================================================================== lighting / camera

    static void BuildEnvironment()
    {
        var env = new GameObject("Environment").transform;

        var sunGo = new GameObject("Sun");
        sunGo.transform.SetParent(env, false);
        sunGo.transform.rotation = Quaternion.Euler(38f, -55f, 0f);
        var sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.955f, 0.88f);
        sun.intensity = 1.35f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.9f;
        sun.shadowBias = 0.05f;
        sun.shadowNormalBias = 0.4f;
        if (!sunGo.TryGetComponent(out UniversalAdditionalLightData lightData))
            lightData = sunGo.AddComponent<UniversalAdditionalLightData>();
        lightData.usePipelineSettings = true;

        var windGo = new GameObject("Wind");
        windGo.transform.SetParent(env, false);
        windGo.transform.rotation = Quaternion.Euler(0f, 60f, 0f);
        var wind = windGo.AddComponent<WindZone>();
        wind.mode = WindZoneMode.Directional;
        wind.windMain = 0.45f;
        wind.windTurbulence = 0.35f;
        wind.windPulseMagnitude = 0.4f;
        wind.windPulseFrequency = 0.08f;

        var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
        if (sky == null)
        {
            Warnings.Add("Skybox not found (" + SkyboxPath + "), using a procedural sky.");
            sky = new Material(Shader.Find("Skybox/Procedural")) { name = "POC_ProceduralSky" };
            sky.SetFloat("_AtmosphereThickness", 0.9f);
            sky.SetFloat("_Exposure", 1.2f);
            AssetDatabase.CreateAsset(sky, $"{GenMaterials}/{sky.name}.mat");
        }
        RenderSettings.skybox = sky;
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1.05f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.reflectionIntensity = 0.85f;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.7f, 0.77f, 0.82f);
        RenderSettings.fogStartDistance = 350f;
        RenderSettings.fogEndDistance = 2200f;

        // Post-processing
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, $"{GenSettings}/POC_PostProcess.asset");
        var tonemap = profile.Add<Tonemapping>(true);
        tonemap.mode.value = TonemappingMode.ACES;
        var color = profile.Add<ColorAdjustments>(true);
        color.postExposure.value = 0.25f;
        color.contrast.value = 10f;
        color.saturation.value = 6f;
        color.colorFilter.overrideState = false;
        color.hueShift.overrideState = false;
        var wb = profile.Add<WhiteBalance>(true);
        wb.temperature.value = 4f;
        wb.tint.value = 0f;
        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.value = 0.25f;
        bloom.threshold.value = 1.1f;
        bloom.scatter.value = 0.6f;
        bloom.tint.overrideState = false;
        bloom.dirtTexture.overrideState = false;
        bloom.dirtIntensity.overrideState = false;
        var vignette = profile.Add<Vignette>(true);
        vignette.intensity.value = 0.2f;
        vignette.smoothness.value = 0.45f;
        vignette.color.overrideState = false;
        vignette.center.overrideState = false;
        vignette.rounded.overrideState = false;
        foreach (var comp in profile.components)
            AssetDatabase.AddObjectToAsset(comp, profile);
        EditorUtility.SetDirty(profile);

        var volGo = new GameObject("Post Processing");
        volGo.transform.SetParent(env, false);
        var vol = volGo.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.priority = 1f;
        vol.sharedProfile = profile;

        DynamicGI.UpdateEnvironment();
    }

    static void BuildCamera(List<Biome> biomes, GameObject labels, Vector3 overviewPos, Vector3 overviewLook)
    {
        var camGo = new GameObject("Showcase Camera") { tag = "MainCamera" };
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 50f;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 4000f;
        camGo.AddComponent<AudioListener>();
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        data.antialiasingQuality = AntialiasingQuality.High;
        data.renderShadows = true;

        var showcase = camGo.AddComponent<VegetationShowcaseCamera>();
        showcase.overheadLabels = labels;
        showcase.viewpoints.Add(new VegetationShowcaseCamera.Viewpoint
        {
            title = "All Vegetation Environments",
            subtitle = "Six separate Unity terrains, each populated with a distinct vegetation community.",
            kind = VegetationShowcaseCamera.ViewKind.Overview,
            position = overviewPos,
            euler = Quaternion.LookRotation(overviewLook - overviewPos).eulerAngles,
        });

        for (int i = 0; i < biomes.Count; i++)
        {
            var b = biomes[i];
            var aerialPos = b.origin + new Vector3(TerrainSize * 0.5f, 42f, -72f);
            var aerialLook = b.origin + new Vector3(TerrainSize * 0.5f, 4f, TerrainSize * 0.45f);
            showcase.viewpoints.Add(new VegetationShowcaseCamera.Viewpoint
            {
                title = $"{i + 1:00}  {b.title}",
                subtitle = b.subtitle + "\n" + b.species,
                kind = VegetationShowcaseCamera.ViewKind.Aerial,
                biomeIndex = i,
                position = aerialPos,
                euler = Quaternion.LookRotation(aerialLook - aerialPos).eulerAngles,
            });

            float gh = HeightAt(b, b.groundView.x, b.groundView.y);
            float lh = HeightAt(b, b.groundLookAt.x, b.groundLookAt.y);
            var groundPos = b.origin + new Vector3(b.groundView.x, gh + 1.7f, b.groundView.y);
            var groundLook = b.origin + new Vector3(b.groundLookAt.x, Mathf.Max(lh, b.waterLevel) + 2.5f, b.groundLookAt.y);
            showcase.viewpoints.Add(new VegetationShowcaseCamera.Viewpoint
            {
                title = $"{i + 1:00}  {b.title}",
                subtitle = b.subtitle + "\n" + b.species,
                kind = VegetationShowcaseCamera.ViewKind.Ground,
                biomeIndex = i,
                position = groundPos,
                euler = Quaternion.LookRotation(groundLook - groundPos).eulerAngles,
            });
        }

        camGo.transform.SetPositionAndRotation(overviewPos, Quaternion.LookRotation(overviewLook - overviewPos));
    }

    const float RecommendedShadowDistance = 220f;

    /// <summary>
    /// Opt-in only: the build never calls this, because it modifies an existing project asset
    /// (the active URP pipeline asset). The user must choose it explicitly.
    /// </summary>
    [MenuItem("Tools/Vegetation/Apply Recommended Shadow Distance", priority = 20)]
    static void ApplyRecommendedShadowDistance()
    {
        if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp))
        {
            EditorUtility.DisplayDialog("Vegetation POC", "No URP pipeline asset is active.", "OK");
            return;
        }
        string path = AssetDatabase.GetAssetPath(urp);
        if (!EditorUtility.DisplayDialog("Apply Recommended Shadow Distance",
                $"This changes the shadow distance of the project's URP asset\n\n  {path}\n\nfrom {urp.shadowDistance} m to {RecommendedShadowDistance} m " +
                "(affects every scene). You can set it back in the asset's Shadows section.\n\nApply?", "Apply", "Cancel"))
            return;
        Undo.RecordObject(urp, "Apply Recommended Shadow Distance");
        urp.shadowDistance = RecommendedShadowDistance;
        EditorUtility.SetDirty(urp);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Vegetation POC] URP shadow distance set to {RecommendedShadowDistance} m on {path}.");
    }

    static void AddSceneToBuildSettings()
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == ScenePath)) return;
        scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
