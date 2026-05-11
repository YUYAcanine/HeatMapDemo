#if UNITY_EDITOR
using UnityEditor;
#endif
using System.IO;
using UnityEngine;

[System.Serializable]
public class TransformData
{
    public Vector3 position;
    public Vector3 rotation;
}

public class KinectTransformSaver : MonoBehaviour
{
    public string fileName = "KinectA_Transform.json";

#if UNITY_EDITOR
    string FilePath
    {
        get
        {
            string folderPath = Path.Combine(Application.dataPath, "Data/Calibration");

            // フォルダがなければ作成
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            return Path.Combine(folderPath, fileName);
        }
    }
#else
    string FilePath => "";
#endif

    void Update()
    {
#if UNITY_EDITOR
        if (Input.GetKeyDown(KeyCode.S))
        {
            SaveTransform();
        }

        if (Input.GetKeyDown(KeyCode.L))
        {
            LoadTransform();
        }
#endif
    }

    public void SaveTransform()
    {
#if UNITY_EDITOR
        TransformData data = new TransformData
        {
            position = transform.position,
            rotation = transform.eulerAngles
        };

        string json = JsonUtility.ToJson(data, true);
        File.WriteAllText(FilePath, json);

        AssetDatabase.Refresh();

        Debug.Log("Saved Kinect Transform to: " + FilePath);
#endif
    }

    public void LoadTransform()
    {
#if UNITY_EDITOR
        if (!File.Exists(FilePath))
        {
            Debug.LogWarning("Transform file not found: " + FilePath);
            return;
        }

        string json = File.ReadAllText(FilePath);
        TransformData data = JsonUtility.FromJson<TransformData>(json);

        transform.position = data.position;
        transform.eulerAngles = data.rotation;

        Debug.Log("Loaded Kinect Transform from: " + FilePath);
#endif
    }
}