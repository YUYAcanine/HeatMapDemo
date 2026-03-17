using UnityEngine;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.IO;

public class LightControl : MonoBehaviour
{
    string host = "127.0.0.1";
    int port = 9999;

    bool light1 = false;
    bool light2 = false;
    bool light3 = false;

    List<LightFrame> frames = new List<LightFrame>();

    string outputDir;
    string outputFile = "Light_log.json";

    void Start()
    {
        outputDir = Path.Combine(Application.dataPath, "Data");

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        Debug.Log("LightControl started");
    }

    void Update()
    {
        // 状態ログ保存
        frames.Add(new LightFrame
        {
            unityTime = Time.time,
            light1 = light1,
            light2 = light2,
            light3 = light3
        });
    }

    // =========================
    // Light1
    // =========================
    public void SetLight1(bool state)
    {
        if (light1 == state)
            return;

        light1 = state;

        if (state)
            SendCommand("L1_ON");
        else
            SendCommand("L1_OFF");

        Debug.Log("Light1: " + state);
    }

    // =========================
    // Light2
    // =========================
    public void SetLight2(bool state)
    {
        if (light2 == state)
            return;

        light2 = state;

        if (state)
            SendCommand("L2_ON");
        else
            SendCommand("L2_OFF");

        Debug.Log("Light2: " + state);
    }

    // =========================
    // Light3
    // =========================
    public void SetLight3(bool state)
    {
        if (light3 == state)
            return;

        light3 = state;

        if (state)
            SendCommand("L3_ON");
        else
            SendCommand("L3_OFF");

        Debug.Log("Light3: " + state);
    }

    // =========================
    // Python通信
    // =========================
    void SendCommand(string msg)
    {
        try
        {
            TcpClient client = new TcpClient(host, port);
            NetworkStream stream = client.GetStream();

            byte[] data = Encoding.UTF8.GetBytes(msg);
            stream.Write(data, 0, data.Length);

            stream.Close();
            client.Close();

            Debug.Log("Sent: " + msg);
        }
        catch
        {
            Debug.Log("Light server not running");
        }
    }

    // =========================
    // JSON保存
    // =========================
    void OnDestroy()
    {
        SaveJson();
    }

    void SaveJson()
    {
        LightFrameList wrapper = new LightFrameList { frames = frames };

        string json = JsonUtility.ToJson(wrapper, true);

        string path = Path.Combine(outputDir, outputFile);

        File.WriteAllText(path, json);

        Debug.Log("Saved Light JSON: " + path);
    }

    // =========================
    // Serializable
    // =========================

    [System.Serializable]
    public class LightFrame
    {
        public float unityTime;
        public bool light1;
        public bool light2;
        public bool light3;
    }

    [System.Serializable]
    public class LightFrameList
    {
        public List<LightFrame> frames;
    }
}
