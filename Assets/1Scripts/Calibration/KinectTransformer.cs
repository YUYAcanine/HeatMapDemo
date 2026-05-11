using System.IO;
using UnityEngine;

[System.Serializable]
public class TransformedData
{
    public Vector3 position;
    public Vector3 rotation;
}

public class KinectTransformer : MonoBehaviour
{
    public string fileName = "KinectA_Transform.json";

    string FilePath
    {
        get
        {
            string folderPath = Path.Combine(Application.dataPath, "Data/Calibration");
            return Path.Combine(folderPath, fileName);
        }
    }

    void Start()
    {
        ApplyTransform();
    }

    public void ApplyTransform()
    {
        if (!File.Exists(FilePath))
        {
            Debug.LogError("JSONファイルが存在しない: " + FilePath);
            return;
        }

        string json = File.ReadAllText(FilePath);
        TransformedData data = JsonUtility.FromJson<TransformedData>(json);

        transform.position = data.position;
        transform.eulerAngles = data.rotation;

        Debug.Log("Transform適用完了: " + FilePath);
    }
}