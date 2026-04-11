using UnityEngine;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Collections;

public class LightControl : MonoBehaviour
{
    string host = "127.0.0.1";
    int port = 9999;

    bool light1 = false;
    bool light2 = false;
    bool light3 = false;

    bool sequenceRunning = false;

    List<LightFrame> frames = new List<LightFrame>();

    string outputDir;
    string outputFile = "Light_log.json";

    void Start()
    {
        outputDir = Path.Combine(Application.dataPath, "Data");

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        Debug.Log("LightControl started (logging enabled)");
    }

    void Update()
    {
        // ---------- Light 1 ----------
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            light1 = !light1;

            if (light1)
                SendCommand("L1_ON");
            else
                SendCommand("L1_OFF");

            Debug.Log("Light1: " + light1);
        }

        // ---------- Light 2 ----------
        if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            light2 = !light2;

            if (light2)
                SendCommand("L2_ON");
            else
                SendCommand("L2_OFF");

            Debug.Log("Light2: " + light2);
        }

        // ---------- Light 3 ----------
        if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            light3 = !light3;

            if (light3)
                SendCommand("L3_ON");
            else
                SendCommand("L3_OFF");

            Debug.Log("Light3: " + light3);
        }

        // ---------- Sequence (0キー) ----------
        if (Input.GetKeyDown(KeyCode.Alpha0) && !sequenceRunning)
        {
            StartCoroutine(LightSequence());
        }

        // ---------- ログ保存 ----------
        frames.Add(new LightFrame
        {
            unityTime = Time.time,
            light1 = light1,
            light2 = light2,
            light3 = light3
        });
    }

    IEnumerator LightSequence()
    {
        sequenceRunning = true;

        Debug.Log("Light sequence start");

        // ---------- Light1 ----------
        SendCommand("L1_ON");
        light1 = true;
        yield return new WaitForSeconds(5);

        SendCommand("L1_OFF");
        light1 = false;
        yield return new WaitForSeconds(5);

        // ---------- Light2 ----------
        SendCommand("L2_ON");
        light2 = true;
        yield return new WaitForSeconds(5);

        SendCommand("L2_OFF");
        light2 = false;
        yield return new WaitForSeconds(5);

        // ---------- Light3 ----------
        SendCommand("L3_ON");
        light3 = true;
        yield return new WaitForSeconds(5);

        SendCommand("L3_OFF");
        light3 = false;
        yield return new WaitForSeconds(5);

        Debug.Log("Light sequence end");

        sequenceRunning = false;
    }

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

    // ========================
    // Serializable
    // ========================

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
