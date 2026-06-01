using System;
using System.IO;
using UnityEngine;

public class HeatMapSave : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private ConeHeatMapShow heatMapSource;

    [Header("Save")]
    [SerializeField] private string saveFolderName = "Data/HeatMap";

    [Header("Optional")]
    [SerializeField] private string customFileName = "";

    // =========================================================
    // 保存用データ
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
    // Button から呼ぶ関数
    // =========================================================
    public void SaveHeatMap()
    {
        if (heatMapSource == null)
        {
            Debug.LogError(
                "HeatMapSource is not assigned."
            );
            return;
        }

        float[] heat =
            heatMapSource.GetHeatData();

        if (heat == null || heat.Length == 0)
        {
            Debug.LogWarning(
                "Heat data is empty."
            );
            return;
        }

        HeatMapSaveData data =
            new HeatMapSaveData();

        data.meshName =
            heatMapSource.GetMeshName();

        data.vertexCount =
            heat.Length;

        data.heat =
            heat;

        data.savedTime =
            DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss"
            );

        string json =
            JsonUtility.ToJson(
                data,
                true
            );

        string fileName;

        if (
            string.IsNullOrEmpty(
                customFileName
            )
        )
        {
            fileName =
                "heatmap_" +
                DateTime.Now.ToString(
                    "yyyyMMdd_HHmmss"
                ) +
                ".json";
        }
        else
        {
            fileName =
                customFileName +
                ".json";
        }

        string folderPath =
            Path.Combine(
                Application.dataPath,
                saveFolderName
            );

        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(
                folderPath
            );
        }

        string savePath =
            Path.Combine(
                folderPath,
                fileName
            );

        File.WriteAllText(
            savePath,
            json
        );

        Debug.Log(
            "HeatMap Saved:\n" +
            savePath
        );
    }
}

