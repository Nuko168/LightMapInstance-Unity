using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
public class PrefabLightmapData : MonoBehaviour
{
    [Serializable]
    struct RendererInfo
    {
        public Renderer renderer;
        public int lightmapIndex;
        public Vector4 lightmapScaleOffset;
    }

    [Serializable]
    struct LightInfo
    {
        public Light light;
        public LightmapBakeType lightmapBakeType;
        public MixedLightingMode mixedLightingMode;
        public int occlusionMaskChannel;
        public int probeOcclusionLightIndex;
    }

    [SerializeField] RendererInfo[] m_RendererInfo = Array.Empty<RendererInfo>();
    [SerializeField] Texture2D[] m_Lightmaps = Array.Empty<Texture2D>();
    [SerializeField] Texture2D[] m_LightmapsDir = Array.Empty<Texture2D>();
    [SerializeField] Texture2D[] m_ShadowMasks = Array.Empty<Texture2D>();
    [SerializeField] LightInfo[] m_LightInfo = Array.Empty<LightInfo>();

    void OnEnable() => Apply();

    public void Apply()
    {
        if (m_RendererInfo == null || m_RendererInfo.Length == 0) return;

        var lightmaps = new List<LightmapData>(LightmapSettings.lightmaps);
        var indexMap = new int[m_Lightmaps.Length];

        for (int i = 0; i < m_Lightmaps.Length; i++)
        {
            var color = m_Lightmaps[i];
            int found = lightmaps.FindIndex(d => d.lightmapColor == color);
            if (found < 0)
            {
                lightmaps.Add(new LightmapData
                {
                    lightmapColor = color,
                    lightmapDir = GetOrNull(m_LightmapsDir, i),
                    shadowMask = GetOrNull(m_ShadowMasks, i),
                });
                found = lightmaps.Count - 1;
            }
            indexMap[i] = found;
        }
        LightmapSettings.lightmaps = lightmaps.ToArray();

        foreach (var info in m_RendererInfo)
        {
            if (info.renderer == null) continue;
            if (info.lightmapIndex < 0 || info.lightmapIndex >= indexMap.Length) continue;
            info.renderer.lightmapIndex = indexMap[info.lightmapIndex];
            info.renderer.lightmapScaleOffset = info.lightmapScaleOffset;
        }

        foreach (var info in m_LightInfo)
        {
            if (info.light == null) continue;
            info.light.bakingOutput = new LightBakingOutput
            {
                isBaked = true,
                lightmapBakeType = info.lightmapBakeType,
                mixedLightingMode = info.mixedLightingMode,
                occlusionMaskChannel = info.occlusionMaskChannel,
                probeOcclusionLightIndex = info.probeOcclusionLightIndex,
            };
        }
    }

    static Texture2D GetOrNull(Texture2D[] array, int i)
        => array != null && i < array.Length ? array[i] : null;

#if UNITY_EDITOR
    const string OutputRoot = "Assets/PrefabLightmaps";

    [MenuItem("Tools/Prefab Lightmap/Bake and Store")]
    static void BakeAndStore()
    {
        var targets = FindObjectsByType<PrefabLightmapData>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (targets.Length == 0)
        {
            Debug.LogWarning("[PrefabLightmapData] シーン内に PrefabLightmapData が見つかりません。");
            return;
        }

        if (!Lightmapping.Bake())
        {
            Debug.LogError("[PrefabLightmapData] ベイクに失敗しました。");
            return;
        }

        foreach (var t in targets) t.Store();
        AssetDatabase.SaveAssets();
    }

    void Store()
    {
        var root = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
        string baseName = root != null ? root.name : gameObject.name;
        string folder = EnsureFolder(baseName);

        var sceneMaps = LightmapSettings.lightmaps;
        var renderers = new List<RendererInfo>();
        var colors = new List<Texture2D>();
        var dirs = new List<Texture2D>();
        var masks = new List<Texture2D>();
        var sceneIndices = new List<int>();

        foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
        {
            int si = r.lightmapIndex;
            if (si < 0 || si >= sceneMaps.Length) continue;

            int local = sceneIndices.IndexOf(si);
            if (local < 0)
            {
                local = sceneIndices.Count;
                sceneIndices.Add(si);
                var d = sceneMaps[si];
                colors.Add(CopyTexture(d.lightmapColor, folder, $"{baseName}_{local}_light"));
                dirs.Add(CopyTexture(d.lightmapDir, folder, $"{baseName}_{local}_dir"));
                masks.Add(CopyTexture(d.shadowMask, folder, $"{baseName}_{local}_shadowmask"));
            }

            renderers.Add(new RendererInfo
            {
                renderer = r,
                lightmapIndex = local,
                lightmapScaleOffset = r.lightmapScaleOffset,
            });
        }

        var lights = new List<LightInfo>();
        foreach (var l in GetComponentsInChildren<Light>(true))
        {
            var o = l.bakingOutput;
            if (!o.isBaked) continue;
            lights.Add(new LightInfo
            {
                light = l,
                lightmapBakeType = o.lightmapBakeType,
                mixedLightingMode = o.mixedLightingMode,
                occlusionMaskChannel = o.occlusionMaskChannel,
                probeOcclusionLightIndex = o.probeOcclusionLightIndex,
            });
        }

        Undo.RecordObject(this, "Store Prefab Lightmap");
        m_RendererInfo = renderers.ToArray();
        m_Lightmaps = colors.ToArray();
        m_LightmapsDir = dirs.ToArray();
        m_ShadowMasks = masks.ToArray();
        m_LightInfo = lights.ToArray();
        EditorUtility.SetDirty(this);

        if (PrefabUtility.IsPartOfPrefabInstance(this))
        {
            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(this);
            PrefabUtility.ApplyObjectOverride(this, assetPath, InteractionMode.AutomatedAction);
        }

        Debug.Log($"[PrefabLightmapData] {baseName}: Renderer {renderers.Count} 個 / ライトマップ {colors.Count} 枚を保存しました。", this);
    }

    static string EnsureFolder(string name)
    {
        if (!AssetDatabase.IsValidFolder(OutputRoot))
            AssetDatabase.CreateFolder("Assets", "PrefabLightmaps");
        string folder = $"{OutputRoot}/{name}";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(OutputRoot, name);
        return folder;
    }

    static Texture2D CopyTexture(Texture2D src, string folder, string fileName)
    {
        if (src == null) return null;
        string srcPath = AssetDatabase.GetAssetPath(src);
        if (string.IsNullOrEmpty(srcPath)) return null;

        string dst = $"{folder}/{fileName}{Path.GetExtension(srcPath)}";
        if (File.Exists(dst))
        {
            File.Copy(srcPath, dst, true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
        }
        else
        {
            AssetDatabase.CopyAsset(srcPath, dst);
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
    }
#endif
}
