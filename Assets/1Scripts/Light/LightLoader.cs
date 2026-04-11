using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

public class LightVisualizer : MonoBehaviour
{
    [Header("JSON")]
    [SerializeField] private string lightJson = "light.json";

    [Header("Light Objects")]
    [SerializeField] private Renderer light1Obj;
    [SerializeField] private Renderer light2Obj;
    [SerializeField] private Renderer light3Obj;

    [Header("UI")]
    [SerializeField] private Button startButton;
    [SerializeField] private Button stopButton;

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1.0f;

    [Header("Colors")]
    // Light1
    public Color light1OnColor = Color.red;
    public Color light1OffColor = Color.gray;

    // Light2
    public Color light2OnColor = Color.green;
    public Color light2OffColor = Color.gray;

    // Light3
    public Color light3OnColor = Color.blue;
    public Color light3OffColor = Color.gray;

    private List<LightFrame> frames = new();

    private float playbackTime = 0f;
    private int frameIndex = 0;

    private bool isPlaying = false;
    private bool finished = false;

    // =========================
    void Start()
    {
        LoadLight();
        SetupUI();
        ResetLights();
    }

    void Update()
    {
        if (!isPlaying || finished) return;

        playbackTime += Time.deltaTime * playbackSpeed;

        UpdateLight(playbackTime);
    }

    // =========================
    void SetupUI()
    {
        if (startButton != null)
            startButton.onClick.AddListener(StartPlayback);

        if (stopButton != null)
            stopButton.onClick.AddListener(StopPlayback);
    }

    public void StartPlayback()
    {
        Debug.Log("Start pressed");

        playbackTime = 0f;
        frameIndex = 0;
        finished = false;
        isPlaying = true;

        ResetLights();
    }

    public void StopPlayback()
    {
        Debug.Log("Stop pressed");
        isPlaying = false;
    }

    // =========================
    void LoadLight()
    {
        string path = Path.Combine(Application.dataPath, "Data", lightJson);

        if (!File.Exists(path))
        {
            Debug.LogError($"Light JSON not found: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        LightFrameList list = JsonUtility.FromJson<LightFrameList>(json);
        frames = list.frames;

        Debug.Log($"Loaded light frames: {frames.Count}");
    }

    // =========================
    void UpdateLight(float time)
    {
        if (frameIndex >= frames.Count)
        {
            finished = true;
            isPlaying = false;
            Debug.Log("Playback finished");
            return;
        }

        float frameTime = frames[frameIndex].unityTime;

        if (time < frameTime) return;

        LightFrame lf = frames[frameIndex];

        ApplyLight(light1Obj, lf.light1, light1OnColor, light1OffColor);
        ApplyLight(light2Obj, lf.light2, light2OnColor, light2OffColor);
        ApplyLight(light3Obj, lf.light3, light3OnColor, light3OffColor);

        frameIndex++;
    }

    // =========================
    void ApplyLight(Renderer rend, bool isOn, Color onColor, Color offColor)
    {
        if (rend == null) return;

        rend.material.color = isOn ? onColor : offColor;
    }

    void ResetLights()
    {
        ApplyLight(light1Obj, false, light1OnColor, light1OffColor);
        ApplyLight(light2Obj, false, light2OnColor, light2OffColor);
        ApplyLight(light3Obj, false, light3OnColor, light3OffColor);
    }

    // =========================
    [System.Serializable]
    private class LightFrameList
    {
        public List<LightFrame> frames;
    }

    [System.Serializable]
    private class LightFrame
    {
        public float unityTime;
        public bool light1;
        public bool light2;
        public bool light3;
    }
}