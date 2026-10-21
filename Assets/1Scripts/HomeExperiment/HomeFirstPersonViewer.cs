using System.Collections.Generic;
using System.IO;
using Microsoft.Azure.Kinect.BodyTracking;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 一人称視点のシーン(6FirstPersonView)用。選んだ人物の頭の位置・視線の向きにカメラを置いて、
// その人から部屋がどう見えていたかを再生する(3RealtimeDemo/0RealTimeGaze の HeadDirectionAlpha と同じく、
// カメラを頭の位置へ・視線の向きへ毎フレーム近づける)。
//
// シーン5(HomeInteractionAnalyzer)の [1st Person] から開くと、シーン5の骨格・視線の設定と、
// Skeleton で選んでいた人物・時刻をそのまま使う(HomeFirstPersonRequest)。[Back] でシーン5に戻る。
// このシーンを直接開いたときはインスペクターの設定を使う。
//
// 画面: 中央の円 = 視線のコーン(シーン5の Cone Angle)。他の人の骨格と視線も表示する(自分の骨格は表示しない)。
// 部屋オブジェクト・対象物体・Env は同じGameObjectの HomeEnvLoader が読み込む。
// HomeEnvLoader(DefaultExecutionOrder -100)が Awake で読み込む前に、実験の名前・データの場所を差し替えるため先に Awake を実行する。
[DefaultExecutionOrder(-200)]
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeFirstPersonViewer : MonoBehaviour, IHomeDataRootProvider
{
    [Header("Data Location")]
    [Tooltip("データのルートフォルダ。空なら既定の Assets/Data/HomeExperiment。例: D:\\HomeExperiment\n" +
             "シーン5の [1st Person] から開いたときは、シーン5の設定を使う。")]
    [HomeDataRootFolder]
    [SerializeField] private string dataRootDirectory = "";

    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Skeleton (シーン5と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.SkeletonSource skeletonSource = HomeGazeAnalyzer.SkeletonSource.Filtered;
    [SerializeField] private HomeSkeletonFilter.ConfidenceMode filteredMode = HomeSkeletonFilter.ConfidenceMode.HeadJoints;
    [HomeSkeletonVariant]
    [SerializeField] private string skeletonVariant = "";
    [SerializeField] private string kinectId = "A";
    [SerializeField] private HomeGazeAnalyzer.ImageDirection imageDirection = HomeGazeAnalyzer.ImageDirection.GazeElseHead;
    [SerializeField] private float maxFrameDuration = 0.2f;

    [Header("Gaze (シーン5と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.HeadDirectionMethod headDirectionMethod = HomeGazeAnalyzer.HeadDirectionMethod.HeadToNose;
    [SerializeField] private float downwardAngle = 24.4f;
    [Tooltip("画面中央に円で表示する視線のコーンの半角(度)")]
    [SerializeField] private float coneAngle = 10.14f;

    [Header("Person")]
    [Tooltip("一人称視点で見る人物ID。-1 なら映っている時間が一番長い人。")]
    [SerializeField] private int personId = -1;
    [Tooltip("これより短い人物(誤検出など)は一覧に出さない(シーン5と同じ)")]
    [SerializeField] private float minPersonSeconds = 1f;

    [Header("Camera")]
    [Tooltip("空なら Main Camera")]
    [SerializeField] private Camera viewCamera;
    [SerializeField] private float fieldOfView = 70f;
    [SerializeField] private float nearClipPlane = 0.05f;
    [Tooltip("カメラを頭の位置・向きへなめらかに近づける(0RealTimeGaze と同じ)。オフなら毎フレームそのまま合わせる。")]
    [SerializeField] private bool smoothCamera = true;
    [SerializeField] private float cameraLerpSpeed = 15f;
    [SerializeField] private bool showConeCircle = true;
    [SerializeField] private Color coneCircleColor = new Color(1f, 0.85f, 0.1f, 0.9f);

    [Header("Visual (他の人の骨格)")]
    [SerializeField] private bool showOtherPersons = true;
    [SerializeField] private GameObject jointPrefab;
    [SerializeField] private Material lineMaterial;
    [SerializeField] private float jointScale = 0.06f;
    [SerializeField] private float lineWidth = 0.02f;
    [SerializeField] private float gazeLineLength = 1.5f;
    [SerializeField] private float gazeLineWidth = 0.015f;
    [SerializeField] private Color[] personColors =
    {
        Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta
    };

    [Header("Playback")]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private KeyCode playStopKey = KeyCode.Space;

    private HomeEnvLoader env;
    private HomeFirstPersonRequest.Settings request;

    public string DataRootDirectory => dataRootDirectory;

    private readonly List<Frame> frames = new List<Frame>();
    private readonly SortedDictionary<int, float> personSeconds = new SortedDictionary<int, float>();
    private readonly Dictionary<int, int> personFrameCounts = new Dictionary<int, int>();
    private readonly Dictionary<int, float> personFirstTimes = new Dictionary<int, float>();
    private readonly Dictionary<int, string> personNames = new Dictionary<int, string>();

    private float duration;
    private float playbackTime;
    private bool isPlaying;
    private bool snapCamera = true;
    private bool personVisible;

    // 表示
    private Transform visualRoot;
    private HomeSkeletonBodyVisual.Style visualStyle;
    private Material sharedLineMaterial;
    private readonly Dictionary<int, HomeSkeletonBodyVisual> bodies = new Dictionary<int, HomeSkeletonBodyVisual>();
    private readonly Dictionary<int, LineRenderer> gazeLines = new Dictionary<int, LineRenderer>();

    // UI
    private Sprite uiSprite;
    private Sprite checkmarkSprite;
    private Slider timelineSlider;
    private TMP_Text playPauseText;
    private TMP_Text timeText;
    private TMP_Text infoText;
    private RectTransform personListRect;
    private RectTransform personListContent;
    private TMP_Text personHeaderText;
    private readonly List<KeyValuePair<int, Toggle>> personToggles = new List<KeyValuePair<int, Toggle>>();

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
        request = HomeFirstPersonRequest.Current;

        if (request != null)
            ApplyRequest(request);
    }

    // シーン5の設定で上書きする(HomeEnvLoader の Awake より前)
    private void ApplyRequest(HomeFirstPersonRequest.Settings settings)
    {
        dataRootDirectory = settings.dataRootDirectory;
        subjectName = settings.subjectName;
        skeletonSource = settings.skeletonSource;
        filteredMode = settings.filteredMode;
        skeletonVariant = settings.skeletonVariant;
        kinectId = settings.kinectId;
        imageDirection = settings.imageDirection;
        maxFrameDuration = settings.maxFrameDuration;
        headDirectionMethod = settings.headDirectionMethod;
        downwardAngle = settings.downwardAngle;
        coneAngle = settings.coneAngle;
        minPersonSeconds = settings.minPersonSeconds;
        personId = settings.personId;
        playbackTime = settings.time;
        env.SetExperimentName(settings.experimentName);
    }

    private void Start()
    {
        if (viewCamera == null)
            viewCamera = Camera.main;

        if (viewCamera != null)
        {
            viewCamera.fieldOfView = fieldOfView;
            viewCamera.nearClipPlane = nearClipPlane;
        }

        visualRoot = new GameObject("FirstPersonPlayback").transform;
        visualStyle = new HomeSkeletonBodyVisual.Style
        {
            jointPrefab = jointPrefab,
            lineMaterial = lineMaterial,
            jointScale = jointScale,
            lineWidth = lineWidth,
            showHeadDirection = false
        };
        sharedLineMaterial = lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default"));

        LoadFrames();

        if (personId < 0 || !personSeconds.ContainsKey(personId))
            personId = GetLongestPerson();

        playbackTime = Mathf.Clamp(playbackTime, 0f, duration);

        // 選んだ人が今の時刻に映っていなければ、最初に出てくる時刻へ移動する
        if (personId >= 0 && FindBody(FindFrameIndex(playbackTime), personId) == null &&
            personFirstTimes.TryGetValue(personId, out float firstTime))
            playbackTime = firstTime;

        if (viewCamera != null && showConeCircle)
            CreateConeCircle();

        SetupUI();
        ShowFrame();
    }

    private void Update()
    {
        if (Input.GetKeyDown(playStopKey))
            TogglePlay();

        if (isPlaying)
        {
            playbackTime += Time.deltaTime * playbackSpeed;

            if (playbackTime >= duration)
            {
                playbackTime = duration;
                isPlaying = false;
            }
        }

        ShowFrame();
        UpdateUI();
    }

    private void LateUpdate()
    {
        HomeExperimentPaths.ClearUISelection();
    }

    // ------------------------------------------------------------
    // Controls
    // ------------------------------------------------------------
    public void TogglePlay()
    {
        if (isPlaying)
        {
            isPlaying = false;
            return;
        }

        if (frames.Count == 0)
            return;

        if (playbackTime >= duration - 0.001f)
            playbackTime = 0f;

        isPlaying = true;
    }

    public void Seek(float time)
    {
        playbackTime = Mathf.Clamp(time, 0f, duration);
        snapCamera = true;
    }

    public void SetPerson(int id)
    {
        personId = id;
        snapCamera = true;

        if (FindBody(FindFrameIndex(playbackTime), id) == null && personFirstTimes.TryGetValue(id, out float firstTime))
            playbackTime = firstTime;

        RefreshPersonToggles();
    }

    public void Back()
    {
        HomeFirstPersonRequest.Return(personId, playbackTime);
    }

    // ------------------------------------------------------------
    // Load skeleton (シーン5と同じ骨格・同じ視線の作り方)
    // ------------------------------------------------------------
    private void LoadFrames()
    {
        frames.Clear();

        if (!HomeExperimentPaths.IsValidRootDirectory(dataRootDirectory, out string error) ||
            !HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out error) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            Debug.LogError($"[HomeFirstPersonViewer] データのフォルダ/実験の名前/実験対象者を正しく指定してください。{error}");
            return;
        }

        if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Filtered)
            LoadFiltered(filteredMode.ToString());
        else if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image)
            LoadFiltered("Image");
        else
            LoadKinect();

        for (int i = 0; i < frames.Count; i++)
        {
            float interval = i + 1 < frames.Count
                ? frames[i + 1].time - frames[i].time
                : (i > 0 ? frames[i].time - frames[i - 1].time : 0f);
            float frameDuration = Mathf.Clamp(interval, 0f, maxFrameDuration);

            foreach (Body body in frames[i].bodies)
            {
                if (!body.fixedGaze)
                    body.hasGaze = TryGetGaze(body.joints, out body.origin, out body.direction);

                personSeconds.TryGetValue(body.id, out float seconds);
                personSeconds[body.id] = seconds + frameDuration;
                personFrameCounts.TryGetValue(body.id, out int count);
                personFrameCounts[body.id] = count + 1;

                if (!personFirstTimes.ContainsKey(body.id))
                    personFirstTimes[body.id] = frames[i].time;
            }
        }

        duration = frames.Count > 0 ? frames[frames.Count - 1].time : 0f;
        Debug.Log($"[HomeFirstPersonViewer] {frames.Count} frames, {duration:F1}s を読み込みました。人物ID {personSeconds.Count} 個");
    }

    private void LoadFiltered(string modeName)
    {
        string path = HomeExperimentPaths.GetGroupedSkeletonPath(dataRootDirectory, env.ExperimentName, subjectName, modeName, skeletonVariant);

        if (!File.Exists(path))
        {
            Debug.LogError($"[HomeFirstPersonViewer] 人物ごとの骨格のファイルがありません: {path}");
            return;
        }

        HomeFilteredSkeletonList data = JsonUtility.FromJson<HomeFilteredSkeletonList>(File.ReadAllText(path));

        if (data == null || data.frames == null)
            return;

        foreach (HomeFilteredFrame frame in data.frames)
        {
            Frame f = new Frame { time = frame.timeSec };

            foreach (HomeFilteredPerson person in frame.persons)
            {
                if (!personNames.ContainsKey(person.trackId))
                    personNames[person.trackId] = person.label;

                Body body = new Body { id = person.trackId, joints = person.joints };

                if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image)
                {
                    // 画像で推定した頭の位置と向きをそのまま使う(シーン5と同じ)
                    bool useGaze = imageDirection != HomeGazeAnalyzer.ImageDirection.Head && person.hasGazeDirection;
                    bool useHead = imageDirection != HomeGazeAnalyzer.ImageDirection.GazeOnly && person.hasHeadDirection;

                    body.fixedGaze = true;
                    body.origin = person.headPosition;
                    body.hasGaze = useGaze || useHead;
                    body.direction = useGaze ? person.gazeDirection.normalized : person.headDirection.normalized;
                }

                f.bodies.Add(body);
            }

            frames.Add(f);
        }
    }

    private void LoadKinect()
    {
        foreach (HomeSkeletonTrack track in HomeSkeletonIO.LoadSubject(dataRootDirectory, env.ExperimentName, subjectName, "HomeFirstPersonViewer"))
        {
            if (track.kinectId != kinectId.Trim())
                continue;

            for (int i = 0; i < track.frames.Count; i++)
            {
                Frame f = new Frame { time = track.times[i] };

                if (track.frames[i].bodies != null)
                {
                    foreach (HomeSkeletonBody body in track.frames[i].bodies)
                        f.bodies.Add(new Body { id = (int)body.bodyId, joints = body.joints });
                }

                frames.Add(f);
            }

            return;
        }

        Debug.LogError($"[HomeFirstPersonViewer] Kinect{kinectId} の骨格データがありません。");
    }

    // HomeInteractionAnalyzer.TryGetGaze と同じ視線方向の作り方
    private bool TryGetGaze(List<HomeSkeletonJoint> joints, out Vector3 head, out Vector3 direction)
    {
        direction = Vector3.zero;

        if (!HomeSkeletonIO.TryGetJointPosition(joints, JointId.Head, out head) ||
            !HomeSkeletonIO.TryGetJointPosition(joints, JointId.Nose, out Vector3 nose))
            return false;

        Vector3 from = head;

        if (headDirectionMethod == HomeGazeAnalyzer.HeadDirectionMethod.EarsToNose &&
            HomeSkeletonIO.TryGetJointPosition(joints, JointId.EarLeft, out Vector3 earLeft) &&
            HomeSkeletonIO.TryGetJointPosition(joints, JointId.EarRight, out Vector3 earRight))
        {
            from = (earLeft + earRight) * 0.5f;
        }

        Vector3 rawDir = nose - from;

        if (rawDir.sqrMagnitude < 0.0001f)
            return false;

        rawDir.Normalize();

        Vector3 rightAxis = Vector3.Cross(Vector3.up, rawDir).normalized;

        if (rightAxis.sqrMagnitude < 0.0001f)
            rightAxis = Vector3.right;

        direction = (Quaternion.AngleAxis(downwardAngle, rightAxis) * rawDir).normalized;
        return true;
    }

    private int GetLongestPerson()
    {
        int best = -1;
        float bestSeconds = -1f;

        foreach (KeyValuePair<int, float> pair in personSeconds)
        {
            if (pair.Value > bestSeconds)
            {
                best = pair.Key;
                bestSeconds = pair.Value;
            }
        }

        return best;
    }

    private string GetPersonName(int id) =>
        personNames.TryGetValue(id, out string name) && !string.IsNullOrEmpty(name) ? name : $"person_{id}";

    // ------------------------------------------------------------
    // Show
    // ------------------------------------------------------------
    private void ShowFrame()
    {
        int index = FindFrameIndex(playbackTime);
        HashSet<int> shown = new HashSet<int>();
        Body self = null;

        if (index >= 0)
        {
            foreach (Body body in frames[index].bodies)
            {
                if (body.id == personId)
                {
                    self = body;
                    continue;
                }

                if (showOtherPersons)
                {
                    ShowOther(body);
                    shown.Add(body.id);
                }
            }
        }

        foreach (KeyValuePair<int, HomeSkeletonBodyVisual> pair in bodies)
        {
            if (!shown.Contains(pair.Key))
            {
                pair.Value.SetVisible(false);
                gazeLines[pair.Key].enabled = false;
            }
        }

        // 選んだ人が映っていないフレームでは、最後に映っていたときの視点のまま止める
        personVisible = self != null && self.hasGaze;

        if (personVisible && viewCamera != null)
            MoveCamera(self.origin, self.direction);
    }

    private void MoveCamera(Vector3 position, Vector3 direction)
    {
        // 真上・真下を向いているときは、上方向を前にとる
        Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
        Quaternion rotation = Quaternion.LookRotation(direction, up);
        Transform cam = viewCamera.transform;

        if (smoothCamera && !snapCamera)
        {
            float t = Mathf.Clamp01(Time.deltaTime * cameraLerpSpeed);
            cam.position = Vector3.Lerp(cam.position, position, t);
            cam.rotation = Quaternion.Slerp(cam.rotation, rotation, t);
        }
        else
        {
            cam.position = position;
            cam.rotation = rotation;
        }

        snapCamera = false;
    }

    private void ShowOther(Body body)
    {
        if (!bodies.TryGetValue(body.id, out HomeSkeletonBodyVisual visual))
        {
            Color color = personColors.Length > 0 ? personColors[Mathf.Abs(body.id) % personColors.Length] : Color.white;
            visual = new HomeSkeletonBodyVisual(visualRoot, $"person_{body.id}", color, visualStyle);
            bodies[body.id] = visual;
            gazeLines[body.id] = CreateLine($"person_{body.id}_Gaze", Color.Lerp(color, Color.white, 0.5f), gazeLineWidth, visualRoot, true);
        }

        visual.Apply(body.joints);

        LineRenderer gaze = gazeLines[body.id];
        gaze.enabled = body.hasGaze;

        if (body.hasGaze)
        {
            gaze.SetPosition(0, body.origin);
            gaze.SetPosition(1, body.origin + body.direction * gazeLineLength);
        }
    }

    // カメラの前 1m に、視線のコーン(半角 coneAngle)の輪を描く
    private void CreateConeCircle()
    {
        const int segments = 48;
        const float distance = 1f;
        float radius = Mathf.Tan(coneAngle * Mathf.Deg2Rad) * distance;

        LineRenderer ring = CreateLine("ConeCircle", coneCircleColor, 0.003f, viewCamera.transform, false);
        ring.positionCount = segments;
        ring.loop = true;

        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            ring.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, distance));
        }

        LineRenderer cross = CreateLine("ConeCenter", coneCircleColor, 0.003f, viewCamera.transform, false);
        cross.positionCount = 2;
        cross.SetPosition(0, new Vector3(-0.01f, 0f, distance));
        cross.SetPosition(1, new Vector3(0.01f, 0f, distance));
    }

    private LineRenderer CreateLine(string name, Color color, float width, Transform parent, bool worldSpace)
    {
        LineRenderer line = new GameObject(name).AddComponent<LineRenderer>();
        line.transform.SetParent(parent, false);
        line.useWorldSpace = worldSpace;
        line.positionCount = 2;
        line.startWidth = width;
        line.endWidth = width;
        line.material = sharedLineMaterial;
        line.startColor = color;
        line.endColor = color;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.enabled = !worldSpace;
        return line;
    }

    private int FindFrameIndex(float time)
    {
        int low = 0;
        int high = frames.Count - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;

            if (frames[mid].time <= time)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private Body FindBody(int index, int id)
    {
        if (index < 0 || index >= frames.Count)
            return null;

        foreach (Body body in frames[index].bodies)
        {
            if (body.id == id)
                return body;
        }

        return null;
    }

    // ------------------------------------------------------------
    // UI (画面下の再生バー・左上の情報と [Back]・右上の人物の一覧。見た目はシーン5と同じ)
    // ------------------------------------------------------------
    private void SetupUI()
    {
#if UNITY_EDITOR
        uiSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        checkmarkSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd");
#endif

        Canvas canvas = FindObjectOfType<Canvas>();

        if (canvas == null)
        {
            canvas = new GameObject("Canvas").AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvas.gameObject.AddComponent<GraphicRaycaster>();
        }

        if (FindObjectOfType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        // 画面下: [>] 時刻 [=====o-----]
        RectTransform bar = CreateRect("Timeline", canvas.transform);
        bar.anchorMin = new Vector2(0f, 0f);
        bar.anchorMax = new Vector2(1f, 0f);
        bar.pivot = new Vector2(0.5f, 0f);
        bar.anchoredPosition = new Vector2(0f, 10f);
        bar.sizeDelta = new Vector2(-40f, 48f);
        AddImage(bar.gameObject, uiSprite, new Color(0.95f, 0.95f, 0.95f, 0.9f));

        Button playPause = CreateButton(bar, ">", 0f, 50f, out playPauseText);
        playPause.onClick.AddListener(TogglePlay);

        timeText = CreateText("Time", bar, TextAlignmentOptions.MidlineLeft);
        PlaceLeft(timeText.rectTransform, 60f, 380f);

        timelineSlider = CreateSlider(bar, new Color(0.25f, 0.45f, 0.95f));
        RectTransform sliderRect = (RectTransform)timelineSlider.transform;
        sliderRect.anchorMin = new Vector2(0f, 0f);
        sliderRect.anchorMax = new Vector2(1f, 1f);
        sliderRect.offsetMin = new Vector2(450f, 10f);
        sliderRect.offsetMax = new Vector2(-10f, -10f);
        timelineSlider.minValue = 0f;
        timelineSlider.maxValue = Mathf.Max(duration, 0.001f);
        timelineSlider.onValueChanged.AddListener(Seek);

        // 左上: [Back] と情報
        RectTransform top = CreateRect("Info", canvas.transform);
        top.anchorMin = top.anchorMax = top.pivot = new Vector2(0f, 1f);
        top.anchoredPosition = new Vector2(20f, -20f);
        top.sizeDelta = new Vector2(620f, 110f);
        AddImage(top.gameObject, uiSprite, new Color(0.95f, 0.95f, 0.95f, 0.85f));

        RectTransform buttonRow = CreateRect("Row", top);
        buttonRow.anchorMin = new Vector2(0f, 1f);
        buttonRow.anchorMax = new Vector2(1f, 1f);
        buttonRow.pivot = new Vector2(0.5f, 1f);
        buttonRow.sizeDelta = new Vector2(0f, 44f);

        Button back = CreateButton(buttonRow, "< Back to Scene 5", 4f, 220f, out _);
        back.onClick.AddListener(Back);
        back.interactable = request != null && !string.IsNullOrEmpty(request.returnScenePath);

        infoText = CreateText("Text", top, TextAlignmentOptions.TopLeft);
        SetStretch(infoText.rectTransform, new Vector2(10f, 4f), new Vector2(-10f, -46f));
        infoText.fontSize = 18f;
        infoText.enableWordWrapping = true;

        SetupPersonSelector(canvas);
        UpdateUI();
    }

    private void UpdateUI()
    {
        if (timelineSlider == null)
            return;

        timelineSlider.SetValueWithoutNotify(playbackTime);
        playPauseText.text = isPlaying ? "||" : ">";
        timeText.text = $"{playbackTime:F1} / {duration:F1} s  " +
                        $"({HomeSkeletonIO.FormatClock(playbackTime)} / {HomeSkeletonIO.FormatClock(duration)})";

        string person = personId >= 0 ? GetPersonName(personId) : "-";
        // UI のフォントに日本語が無いことがあるので英語で出す
        string state = personId < 0 ? "  (no person)" :
            personVisible ? "" : "  (not detected now - holding last view)";
        infoText.text = $"1st person : {person}{state}\n{env.ExperimentName} / {subjectName}  ({GetSourceLabel()})";
    }

    private string GetSourceLabel()
    {
        string variant = string.IsNullOrWhiteSpace(skeletonVariant) ? "" : $"_{skeletonVariant.Trim()}";

        return skeletonSource == HomeGazeAnalyzer.SkeletonSource.Filtered ? $"filtered_{filteredMode}{variant}" :
            skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image ? $"image_{imageDirection}{variant}" :
            $"Kinect{kinectId.Trim()}";
    }

    //   [Person : mother  v]
    //   (o) 1700 frames  mother (0.1 s -)
    //   ( ) 900 frames   child (2.3 s -)
    //   (選んでも一覧は閉じない。見出しを押すと閉じる)
    private void SetupPersonSelector(Canvas canvas)
    {
        RectTransform root = CreateRect("PersonSelector", canvas.transform);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(1f, 1f);
        root.anchoredPosition = new Vector2(-20f, -20f);
        root.sizeDelta = new Vector2(DropdownWidth, 40f);

        Button header = root.gameObject.AddComponent<Button>();
        header.targetGraphic = AddImage(root.gameObject, uiSprite, Color.white);
        personHeaderText = CreateText("Label", root, TextAlignmentOptions.MidlineLeft);
        SetStretch(personHeaderText.rectTransform, new Vector2(10f, 0f), new Vector2(-10f, 0f));

        personListRect = CreateRect("List", root);
        personListRect.anchorMin = new Vector2(0f, 0f);
        personListRect.anchorMax = new Vector2(1f, 0f);
        personListRect.pivot = new Vector2(0.5f, 1f);
        personListRect.anchoredPosition = new Vector2(0f, -2f);
        AddImage(personListRect.gameObject, uiSprite, new Color(0.95f, 0.95f, 0.95f, 0.95f));
        personListRect.gameObject.AddComponent<RectMask2D>();

        personListContent = CreateRect("Content", personListRect);
        personListContent.anchorMin = new Vector2(0f, 1f);
        personListContent.anchorMax = new Vector2(1f, 1f);
        personListContent.pivot = new Vector2(0.5f, 1f);

        ScrollRect scroll = personListRect.gameObject.AddComponent<ScrollRect>();
        scroll.content = personListContent;
        scroll.viewport = personListRect;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = DropdownRowHeight;

        header.onClick.AddListener(() => personListRect.gameObject.SetActive(!personListRect.gameObject.activeSelf));
        personListRect.gameObject.SetActive(false);

        int row = 0;

        foreach (KeyValuePair<int, float> pair in personSeconds)
        {
            int id = pair.Key;

            // シーン5の一覧と同じく、短い人物(誤検出など)は出さない。ただし今見ている人は出す
            if (pair.Value < minPersonSeconds && id != personId)
                continue;

            personFrameCounts.TryGetValue(id, out int count);
            personFirstTimes.TryGetValue(id, out float firstTime);
            string label = $"{count} frames<pos=140>{GetPersonName(id)} ({firstTime:F1} s -)";

            Toggle toggle = CreateToggleRow(personListContent, label, row++ * DropdownRowHeight, DropdownRowHeight);
            toggle.onValueChanged.AddListener(isOn =>
            {
                // 1つだけ選ぶ(選んでいるものを外そうとしたときは選んだままにする)
                if (isOn)
                    SetPerson(id);
                else
                    RefreshPersonToggles();
            });
            personToggles.Add(new KeyValuePair<int, Toggle>(id, toggle));
        }

        personListRect.sizeDelta = new Vector2(0f, Mathf.Min(row, DropdownMaxVisibleRows) * DropdownRowHeight);
        personListContent.sizeDelta = new Vector2(0f, row * DropdownRowHeight);
        RefreshPersonToggles();
    }

    private void RefreshPersonToggles()
    {
        foreach (KeyValuePair<int, Toggle> pair in personToggles)
            pair.Value.SetIsOnWithoutNotify(pair.Key == personId);

        if (personHeaderText != null)
            personHeaderText.text = $"Person : {(personId >= 0 ? GetPersonName(personId) : "-")}";
    }

    private const float DropdownWidth = 480f;
    private const float DropdownRowHeight = 30f;
    private const int DropdownMaxVisibleRows = 14;

    private static RectTransform CreateRect(string name, Transform parent)
    {
        RectTransform rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.gameObject.layer = parent.gameObject.layer;
        return rect;
    }

    private static Image AddImage(GameObject obj, Sprite sprite, Color color)
    {
        Image image = obj.AddComponent<Image>();
        image.sprite = sprite;
        image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        image.color = color;
        return image;
    }

    private static TMP_Text CreateText(string name, RectTransform parent, TextAlignmentOptions alignment)
    {
        TextMeshProUGUI text = CreateRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = 20f;
        text.color = new Color(0.2f, 0.2f, 0.2f);
        text.alignment = alignment;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    private static void SetStretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    private static void PlaceLeft(RectTransform rect, float x, float width, float margin = 0f)
    {
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.offsetMin = new Vector2(x, margin);
        rect.offsetMax = new Vector2(x + width, -margin);
    }

    private Button CreateButton(RectTransform parent, string label, float x, float width, out TMP_Text text)
    {
        RectTransform rect = CreateRect("Button", parent);
        PlaceLeft(rect, x, width, 4f);
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = AddImage(rect.gameObject, uiSprite, Color.white);

        text = CreateText("Label", rect, TextAlignmentOptions.Center);
        SetStretch(text.rectTransform, new Vector2(4f, 0f), new Vector2(-4f, 0f));
        text.text = label;
        text.fontStyle = FontStyles.Bold;
        text.overflowMode = TextOverflowModes.Overflow;
        text.enableAutoSizing = true;
        text.fontSizeMin = 10f;
        text.fontSizeMax = 20f;
        return button;
    }

    // Unity の既定のスライダーと同じ構成 (Background / Fill Area / Handle Slide Area)
    private Slider CreateSlider(RectTransform parent, Color fillColor)
    {
        RectTransform root = CreateRect("Slider", parent);

        RectTransform background = CreateRect("Background", root);
        background.anchorMin = new Vector2(0f, 0.3f);
        background.anchorMax = new Vector2(1f, 0.7f);
        background.offsetMin = background.offsetMax = Vector2.zero;
        AddImage(background.gameObject, uiSprite, new Color(0.75f, 0.75f, 0.75f));

        RectTransform fillArea = CreateRect("Fill Area", root);
        fillArea.anchorMin = new Vector2(0f, 0.3f);
        fillArea.anchorMax = new Vector2(1f, 0.7f);
        fillArea.offsetMin = fillArea.offsetMax = Vector2.zero;

        RectTransform fill = CreateRect("Fill", fillArea);
        fill.offsetMin = fill.offsetMax = Vector2.zero;
        AddImage(fill.gameObject, uiSprite, new Color(fillColor.r, fillColor.g, fillColor.b, 0.35f)).raycastTarget = false;

        RectTransform handleArea = CreateRect("Handle Slide Area", root);
        SetStretch(handleArea, new Vector2(8f, 0f), new Vector2(-8f, 0f));

        RectTransform handle = CreateRect("Handle", handleArea);
        handle.sizeDelta = new Vector2(16f, 0f);
        Image handleImage = AddImage(handle.gameObject, uiSprite, fillColor);

        Slider slider = root.gameObject.AddComponent<Slider>();
        slider.fillRect = fill;
        slider.handleRect = handle;
        slider.targetGraphic = handleImage;
        slider.direction = Slider.Direction.LeftToRight;
        return slider;
    }

    private Toggle CreateToggleRow(RectTransform parent, string label, float y, float height)
    {
        RectTransform row = CreateRect("Toggle", parent);
        row.anchorMin = new Vector2(0f, 1f);
        row.anchorMax = new Vector2(1f, 1f);
        row.pivot = new Vector2(0.5f, 1f);
        row.anchoredPosition = new Vector2(0f, -y);
        row.sizeDelta = new Vector2(0f, height);

        // 行のどこを押しても切り替わるように、透明な Image でクリックを受ける
        AddImage(row.gameObject, null, new Color(1f, 1f, 1f, 0f));

        RectTransform box = CreateRect("Box", row);
        box.anchorMin = box.anchorMax = new Vector2(0f, 0.5f);
        box.pivot = new Vector2(0f, 0.5f);
        box.anchoredPosition = new Vector2(10f, 0f);
        box.sizeDelta = new Vector2(20f, 20f);
        Image boxImage = AddImage(box.gameObject, uiSprite, Color.white);

        RectTransform check = CreateRect("Checkmark", box);
        SetStretch(check, Vector2.zero, Vector2.zero);
        Image checkImage = AddImage(check.gameObject, checkmarkSprite, checkmarkSprite != null ? Color.black : new Color(0.2f, 0.4f, 0.9f));

        if (checkmarkSprite == null)
            SetStretch(check, new Vector2(4f, 4f), new Vector2(-4f, -4f));

        TMP_Text text = CreateText("Label", row, TextAlignmentOptions.MidlineLeft);
        SetStretch(text.rectTransform, new Vector2(38f, 0f), new Vector2(-10f, 0f));
        text.text = label;

        Toggle toggle = row.gameObject.AddComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = checkImage;
        return toggle;
    }

    // ------------------------------------------------------------
    // Types
    // ------------------------------------------------------------
    private class Frame
    {
        public float time;
        public readonly List<Body> bodies = new List<Body>();
    }

    private class Body
    {
        public int id;
        public List<HomeSkeletonJoint> joints;
        public bool hasGaze;
        public Vector3 origin;
        public Vector3 direction;
        public bool fixedGaze;
    }
}
