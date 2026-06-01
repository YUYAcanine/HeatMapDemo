using System.IO;
using UnityEngine;

public class HeatMapSwitcher : MonoBehaviour
{
    // =========================================================
    // Normalization Mode
    // =========================================================
    public enum NormalizationMode
    {
        None,
        Sum,
        Max
    }

    [Header("Files")]
    [SerializeField]
    private string beforeFile =
        "before.json";

    [SerializeField]
    private string afterFile =
        "after.json";

    [Header("Target Mesh")]
    [SerializeField]
    private GameObject meshObject;

    [Header("Normalization")]
    [SerializeField]
    private NormalizationMode
        normalizationMode =
        NormalizationMode.Max;

    [Header("Display")]
    [SerializeField]
    private float maxHeatDisplay = 1f;

    [SerializeField]
    private float maxDifference = 1f;

    [SerializeField]
    private float diffThreshold = 0.05f;

    private Mesh mesh;

    // =========================================================
    [System.Serializable]
    public class HeatMapSaveData
    {
        public string meshName;
        public int vertexCount;
        public float[] heat;
        public string savedTime;
    }

    // =========================================================
    void Start()
    {
        MeshFilter mf =
            meshObject.GetComponent<MeshFilter>();

        mesh = mf.mesh;
    }

    // =========================================================
    // BEFORE
    // =========================================================
    public void ShowBefore()
    {
        HeatMapSaveData data =
            LoadHeatMap(beforeFile);

        if (data == null)
            return;

        float[] heat =
            GetProcessedHeat(data);

        ApplyNormalHeatMap(heat);

        Debug.Log(
            "Showing BEFORE"
        );
    }

    // =========================================================
    // AFTER
    // =========================================================
    public void ShowAfter()
    {
        HeatMapSaveData data =
            LoadHeatMap(afterFile);

        if (data == null)
            return;

        float[] heat =
            GetProcessedHeat(data);

        ApplyNormalHeatMap(heat);

        Debug.Log(
            "Showing AFTER"
        );
    }

    // =========================================================
    // DIFFERENCE
    // =========================================================
    public void ShowDifference()
    {
        float[] before =
            GetProcessedHeat(
                LoadHeatMap(beforeFile)
            );

        float[] after =
            GetProcessedHeat(
                LoadHeatMap(afterFile)
            );

        if (
            before == null
            || after == null
        )
        {
            return;
        }

        Color[] colors =
            new Color[mesh.vertexCount];

        for (
            int i = 0;
            i < mesh.vertexCount;
            i++
        )
        {
            float diff =
                after[i]
                - before[i];

            // 小さい差分除去
            if (
                Mathf.Abs(diff)
                < diffThreshold
            )
            {
                colors[i] =
                    Color.clear;

                continue;
            }

            float normalized =
                Mathf.Clamp(
                    diff / maxDifference,
                    -1f,
                    1f
                );

            // AFTER優勢
            if (normalized > 0f)
            {
                colors[i] =
                    new Color(
                        1f,
                        0f,
                        0f,
                        normalized
                    );
            }
            // BEFORE優勢
            else
            {
                colors[i] =
                    new Color(
                        0f,
                        0f,
                        1f,
                        -normalized
                    );
            }
        }

        mesh.colors = colors;

        Debug.Log(
            "Showing DIFFERENCE"
        );
    }

    // =========================================================
    // GAIN
    // AFTER - BEFORE
    // =========================================================
    public void ShowGain()
    {
        float[] before =
            GetProcessedHeat(
                LoadHeatMap(beforeFile)
            );

        float[] after =
            GetProcessedHeat(
                LoadHeatMap(afterFile)
            );

        if (
            before == null
            || after == null
        )
        {
            return;
        }

        Color[] colors =
            new Color[mesh.vertexCount];

        for (
            int i = 0;
            i < mesh.vertexCount;
            i++
        )
        {
            float diff =
                after[i]
                - before[i];

            if (diff <= diffThreshold)
            {
                colors[i] =
                    Color.clear;

                continue;
            }

            float normalized =
                Mathf.Clamp01(
                    diff / maxDifference
                );

            colors[i] =
                new Color(
                    1f,
                    0f,
                    0f,
                    normalized
                );
        }

        mesh.colors = colors;

        Debug.Log(
            "Showing GAIN"
        );
    }

    // =========================================================
    // LOSS
    // BEFORE - AFTER
    // =========================================================
    public void ShowLoss()
    {
        float[] before =
            GetProcessedHeat(
                LoadHeatMap(beforeFile)
            );

        float[] after =
            GetProcessedHeat(
                LoadHeatMap(afterFile)
            );

        if (
            before == null
            || after == null
        )
        {
            return;
        }

        Color[] colors =
            new Color[mesh.vertexCount];

        for (
            int i = 0;
            i < mesh.vertexCount;
            i++
        )
        {
            float diff =
                before[i]
                - after[i];

            if (diff <= diffThreshold)
            {
                colors[i] =
                    Color.clear;

                continue;
            }

            float normalized =
                Mathf.Clamp01(
                    diff / maxDifference
                );

            colors[i] =
                new Color(
                    0f,
                    0f,
                    1f,
                    normalized
                );
        }

        mesh.colors = colors;

        Debug.Log(
            "Showing LOSS"
        );
    }

    // =========================================================
    // Normal HeatMap
    // =========================================================
    void ApplyNormalHeatMap(
        float[] heat
    )
    {
        Color[] colors =
            new Color[mesh.vertexCount];

        for (
            int i = 0;
            i < mesh.vertexCount;
            i++
        )
        {
            float a =
                Mathf.Clamp01(
                    heat[i]
                    / maxHeatDisplay
                );

            colors[i] =
                new Color(
                    1f,
                    0f,
                    0f,
                    a
                );
        }

        mesh.colors = colors;
    }

    // =========================================================
    // Heat Processing
    // =========================================================
    float[] GetProcessedHeat(
        HeatMapSaveData data
    )
    {
        if (data == null)
            return null;

        float[] result =
            new float[data.heat.Length];

        data.heat.CopyTo(result, 0);

        // =========================
        // NONE
        // =========================

        if (
            normalizationMode
            == NormalizationMode.None
        )
        {
            return result;
        }

        // =========================
        // SUM NORMALIZATION
        // =========================

        if (
            normalizationMode
            == NormalizationMode.Sum
        )
        {
            float total = 0f;

            foreach (float h in result)
            {
                total += h;
            }

            if (total > 0f)
            {
                for (
                    int i = 0;
                    i < result.Length;
                    i++
                )
                {
                    result[i] /= total;
                }
            }

            return result;
        }

        // =========================
        // MAX NORMALIZATION
        // =========================

        if (
            normalizationMode
            == NormalizationMode.Max
        )
        {
            float maxValue = 0f;

            foreach (float h in result)
            {
                if (h > maxValue)
                {
                    maxValue = h;
                }
            }

            if (maxValue > 0f)
            {
                for (
                    int i = 0;
                    i < result.Length;
                    i++
                )
                {
                    result[i] /= maxValue;
                }
            }

            return result;
        }

        return result;
    }

    // =========================================================
    // JSON Load
    // =========================================================
    HeatMapSaveData LoadHeatMap(
        string fileName
    )
    {
        string path =
            Path.Combine(
                Application.dataPath,
                "Data/HeatMap",
                fileName
            );

        if (!File.Exists(path))
        {
            Debug.LogError(
                "File not found:\n"
                + path
            );

            return null;
        }

        string json =
            File.ReadAllText(path);

        return JsonUtility.FromJson
            <HeatMapSaveData>(json);
    }
}