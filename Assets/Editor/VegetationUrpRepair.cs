using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
internal static class VegetationUrpRepair
{
    private const string PipelinePath = "Assets/Settings/VegetationURP.asset";
    private const string TerrainMaterialPath =
        "Packages/com.unity.render-pipelines.universal/Runtime/Materials/TerrainLit.mat";
    private const string Tree9BarkMaterialPath = "Assets/Tree9/bark14.mat";
    private const string Tree9LeafMaterialPath = "Assets/Tree9/Tree9_leafs.mat";

    static VegetationUrpRepair()
    {
        EditorApplication.delayCall += RepairLoadedScenes;
        EditorSceneManager.sceneOpened += OnSceneOpened;
    }

    [MenuItem("Tools/Vegetation/Repair URP Rendering")]
    private static void RepairLoadedScenes()
    {
        var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(PipelinePath);
        var terrainMaterial = AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterialPath);
        var tree9Bark = AssetDatabase.LoadAssetAtPath<Material>(Tree9BarkMaterialPath);
        var tree9Leaf = AssetDatabase.LoadAssetAtPath<Material>(Tree9LeafMaterialPath);

        if (pipeline == null || terrainMaterial == null)
        {
            Debug.LogError("[Vegetation URP Fix] Required URP assets could not be loaded.");
            return;
        }

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        var repairedTerrains = 0;
        foreach (var terrain in Resources.FindObjectsOfTypeAll<Terrain>())
        {
            var scene = terrain.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
                continue;

            if (terrain.materialTemplate != terrainMaterial || !terrain.drawInstanced)
            {
                terrain.materialTemplate = terrainMaterial;
                terrain.drawInstanced = true;
                terrain.Flush();
                EditorUtility.SetDirty(terrain);
                EditorSceneManager.MarkSceneDirty(scene);
                repairedTerrains++;
            }
        }

        var repairedTrees = 0;
        if (tree9Bark != null && tree9Leaf != null)
        {
            foreach (var renderer in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                var scene = renderer.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded ||
                    !renderer.gameObject.name.StartsWith("Tree9_"))
                    continue;

                var materials = renderer.sharedMaterials;
                if (materials.Length < 2 ||
                    (materials[0] == tree9Bark && materials[1] == tree9Leaf))
                    continue;

                materials[0] = tree9Bark;
                materials[1] = tree9Leaf;
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
                EditorSceneManager.MarkSceneDirty(scene);
                repairedTrees++;
            }
        }

        SceneView.RepaintAll();
        Debug.Log($"[Vegetation URP Fix] URP is active; repaired {repairedTerrains} terrain(s) and {repairedTrees} Tree9 renderer(s).");
    }

    private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        EditorApplication.delayCall += RepairLoadedScenes;
    }
}
