using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Azure.Kinect.BodyTracking;
using UnityEngine;

// シーン5(5GazeExport)用。Python で解析するために、人物ごとに「誰が・いつ・何を見ていたか」と「対象物体までの距離」を
// フレームごとの CSV に書き出す。集計はしない(集計・グラフは Python で行う)。
//
// 書き出すもの(Skeleton/<実験対象者>/Analysis/Export/<骨格>/。人物 = シーン3のグループ):
//   <人物>_gaze.csv          … 注視対象(対象物体だけ)。視線コーンに表面(メッシュの三角形)が入った対象物体のうち、
//                             コーン軸に最も近いもの(シーン4の gazeTarget と同じ判定。軸が貫けば 0 度で、0 度どうしなら近い方)
//   <人物>_gaze_persons.csv  … 注視対象(相手の人物も対象に含める)。人物が2人以上のときだけ。
//                             相手は旧シーン5と同じ縦の円柱(直径 personDiameter、上部 faceHeight が顔)で表し、
//                             対象物体と相手のうちコーン軸に最も近いものを注視対象にする(軸が円柱を貫けば 0 度)
//   <人物>_distance.csv      … 対象物体までの水平距離(骨盤から、物のメッシュの頂点を囲む床に平行な箱まで。箱の中なら 0。
//                             シーン6/7と同じ)。人物が2人以上のときは相手の骨盤までの水平距離も
//                             (gaze.csv の最後の列に、肩の位置 shoulderLeft/RightX,Y,Z も入れる。頭と肩の向きの解析に使う)
//   <人物>_skeleton.csv      … 骨格の全関節(Azure Kinect の 32 関節)の位置と信頼度。列は <関節>X,<関節>Y,<関節>Z,<関節>C
//                             (部屋の座標。C = 0:None 1:Low 2:Medium 3:High)。無い関節は空。手で触った物の判定など、
//                             書き出し直さずに Python 側で必要な関節を取り出して解析するため
//   export_params.json       … 書き出しに使った設定(対象物体の中心 targetCenters と大きさ targetSizes も入れる)
// 行は骨格データの全フレーム(全員で同じ時刻の並び)。その人物が映っていないフレームは present = 0 で、値は空にする。
// target は 対象の名前 / None(視線はあるが何も見ていない)/ NoData(映っていない・視線が取れない)。
//
// 視線はシーン4と同じ(キネクト: 頭→鼻を下へ downwardAngle 補正 / 画像: 推定した視線や頭の向き)。
// 遮蔽(間に物や人があって見えない)は考えていない。
//
// 操作: 再生するとすぐに書き出す(Export On Start)。画面左上の Export ボタン・インスペクターの「Export」でも書き出せる。
[RequireComponent(typeof(HomeEnvLoader))]
public class HomeGazeExporter : MonoBehaviour, IHomeDataRootProvider
{
    [Header("Data Location")]
    [Tooltip("データのルートフォルダ。<ここ>/<実験の名前>/Skeleton/<実験対象者>/ から人物ごとの骨格を読み、Analysis/Export/ に書き出す。\n" +
             "空なら既定の Assets/Data/HomeExperiment。例: D:\\HomeExperiment\n" +
             "Env(部屋オブジェクト・対象物体)もここの <実験の名前>/Env/ から読む。シーン0と同じフォルダを指定する。")]
    [HomeDataRootFolder]
    [SerializeField] private string dataRootDirectory = "";

    [Header("Subject")]
    [HomeExperimentFolder(HomeExperimentFolderAttribute.Kind.Subject)]
    [SerializeField] private string subjectName = "";

    [Header("Skeleton (シーン4と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.SkeletonSource skeletonSource = HomeGazeAnalyzer.SkeletonSource.Filtered;
    [Tooltip("Filtered のとき: 読み込む grouped_<Mode>.json")]
    [SerializeField] private HomeSkeletonFilter.ConfidenceMode filteredMode = HomeSkeletonFilter.ConfidenceMode.HeadJoints;
    [Tooltip("Filtered / Image のとき: 頭の向きを平滑化した骨格の名前(Tools/GazeSmooth/smooth_skeleton.py で作る grouped_<Mode>_<名前>.json。\n" +
             "例: smooth05)。空なら平滑化前の grouped_<Mode>.json")]
    [HomeSkeletonVariant]
    [SerializeField] private string skeletonVariant = "";
    [Tooltip("Kinect のとき: 読み込む <ID>_skeleton.json のキネクトID")]
    [SerializeField] private string kinectId = "A";
    [Tooltip("Image のとき: 視線として使う向き")]
    [SerializeField] private HomeGazeAnalyzer.ImageDirection imageDirection = HomeGazeAnalyzer.ImageDirection.GazeElseHead;
    [Tooltip("次のフレームまでの間隔がこれより長いときは、フレームの時間(durationSec)をこの秒数で打ち切る(記録の抜けを時間に数えない)。")]
    [SerializeField] private float maxFrameDuration = 0.2f;

    [Header("Gaze (シーン4と同じ)")]
    [SerializeField] private HomeGazeAnalyzer.HeadDirectionMethod headDirectionMethod = HomeGazeAnalyzer.HeadDirectionMethod.HeadToNose;
    [Tooltip("頭部方向を下へ補正する角度(度)")]
    [SerializeField] private float downwardAngle = 24.4f;
    [Tooltip("「見ている」とするコーンの角度(度, 中心軸からの半角)。対象物体・相手の人物のどちらにも使う")]
    [SerializeField] private float coneAngle = 10.14f;
    [Tooltip("コーンの長さ(m)")]
    [SerializeField] private float coneDistance = 5f;

    [Header("Targets (シーン4と同じ)")]
    [Tooltip("シーン0で保存した対象物体(Env/TargetObjects.json)を対象にする。")]
    [SerializeField] private bool useEnvTargetObjects = true;
    [Tooltip("対象にする部屋オブジェクト/対象物体(またはその子)の名前")]
    [SerializeField] private string[] targetNames = new string[0];
    [Tooltip("対象にするシーン上のオブジェクト(部屋オブジェクト以外に置いた箱など)")]
    [SerializeField] private GameObject[] targetObjects = new GameObject[0];

    [Header("Person (旧シーン5と同じ)")]
    [Tooltip("見られる側の人物を表す円柱の直径(m)。円柱は縦向きで、骨格の一番上の関節から一番下の関節までの高さ")]
    [SerializeField] private float personDiameter = 0.5f;
    [Tooltip("顔とする範囲(m)。円柱の上からこの高さまで")]
    [SerializeField] private float faceHeight = 0.3f;
    [Tooltip("相手がこれより遠いときは見ているとしない(m)")]
    [SerializeField] private float lookAtPersonDistance = 5f;
    [Tooltip("映っていた時間の合計がこれより短い人物(誤検出など)は書き出さない(秒)。grouped の骨格はシーン3でまとめた人物だけなので、主に Kinect のとき用")]
    [SerializeField] private float minPersonSeconds = 1f;

    [Header("Output")]
    [Tooltip("出力フォルダ名の末尾に付ける文字(パラメータ違いで書き出し分けるとき)。例: Analysis/Export/filtered_HeadJoints_<label>/")]
    [SerializeField] private string outputLabel = "";
    [Tooltip("再生したらすぐに書き出す")]
    [SerializeField] private bool exportOnStart = true;

    private const string NoHitLabel = "None";
    private const string NoDataLabel = "NoData";

    // 円柱の中心軸(水平位置)を決めるのに使う関節
    private static readonly JointId[] BodyJoints =
    {
        JointId.Head, JointId.Neck, JointId.SpineChest, JointId.SpineNavel, JointId.Pelvis
    };

    // 円柱に視線が当たるかを調べる点の置き方: 高さ CylinderStep(m)ごとの輪に、中心1点と円周 CylinderSides 点
    private const float CylinderStep = 0.1f;
    private const int CylinderSides = 12;

    private HomeEnvLoader env;
    private readonly List<Sample> samples = new List<Sample>();
    private readonly List<Target> targets = new List<Target>();
    // 書き出す人物(人物IDの順)
    private readonly List<Unit> units = new List<Unit>();
    private readonly Dictionary<int, string> personNames = new Dictionary<int, string>();
    private string status = "";

    public string DataRootDirectory => dataRootDirectory;

    private string SourceName =>
        skeletonSource == HomeGazeAnalyzer.SkeletonSource.Filtered ? $"filtered_{filteredMode}{VariantSuffix}" :
        skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image ? $"image_{imageDirection}{VariantSuffix}" :
        $"Kinect{kinectId.Trim()}";

    private string VariantSuffix =>
        string.IsNullOrWhiteSpace(skeletonVariant) ? "" : $"_{skeletonVariant.Trim()}";

    private string OutputName =>
        string.IsNullOrWhiteSpace(outputLabel) ? SourceName : $"{SourceName}_{outputLabel.Trim()}";

    private void Awake()
    {
        env = GetComponent<HomeEnvLoader>();
    }

    private void Start()
    {
        if (exportOnStart)
            Export();
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 720, 400));
        GUILayout.Label($"5GazeExport  {env?.ExperimentName} / {subjectName} / {OutputName}");

        if (GUILayout.Button("Export", GUILayout.Width(120)))
            Export();

        GUILayout.Label(status);
        GUILayout.EndArea();
    }

    // ------------------------------------------------------------
    // Export
    // ------------------------------------------------------------
    [ContextMenu("Export")]
    public void Export()
    {
        if (env == null)
            env = GetComponent<HomeEnvLoader>();

        if (!LoadSamples())
        {
            status = "骨格データを読めませんでした。Console を確認してください。";
            return;
        }

        SetupTargets();
        BuildUnits();

        if (units.Count == 0)
        {
            status = "書き出す人物がいません。";
            Debug.LogError("[HomeGazeExporter] 書き出す人物がいません。");
            return;
        }

        ComputeGaze();

        string folder = HomeExperimentPaths.GetExportDirectory(dataRootDirectory, env.ExperimentName, subjectName, OutputName);
        List<string> written = new List<string>();

        try
        {
            Directory.CreateDirectory(folder);

            for (int u = 0; u < units.Count; u++)
            {
                string fileName = SafeFileName(units[u].name);
                written.Add(WriteGazeCsv(Path.Combine(folder, $"{fileName}_gaze.csv"), u, false));

                if (units.Count >= 2)
                    written.Add(WriteGazeCsv(Path.Combine(folder, $"{fileName}_gaze_persons.csv"), u, true));

                written.Add(WriteDistanceCsv(Path.Combine(folder, $"{fileName}_distance.csv"), u));
                written.Add(WriteSkeletonCsv(Path.Combine(folder, $"{fileName}_skeleton.csv"), u));
            }

            string paramsPath = Path.Combine(folder, "export_params.json");
            File.WriteAllText(paramsPath, JsonUtility.ToJson(CreateParameters(), true));
            written.Add(paramsPath);
            HomeExperimentPaths.RefreshAssetDatabase();
        }
        catch (System.Exception e)
        {
            status = $"書き出しに失敗しました: {e.Message}";
            Debug.LogError($"[HomeGazeExporter] 書き出しに失敗しました: {folder}\n{e}");
            return;
        }

        status = $"{samples.Count} フレーム / 人物 {units.Count} 人 / 対象物体 {targets.Count} 個 を書き出しました:\n{folder}\n" +
                 string.Join("\n", written.ConvertAll(Path.GetFileName));
        Debug.Log($"[HomeGazeExporter] {status}");
    }

    // ------------------------------------------------------------
    // Load skeleton (旧シーン5と同じ)
    // ------------------------------------------------------------
    private bool LoadSamples()
    {
        samples.Clear();
        personNames.Clear();

        if (!HomeExperimentPaths.IsValidRootDirectory(dataRootDirectory, out string error) ||
            !HomeExperimentPaths.IsValidFolderName(env.ExperimentName, out error) ||
            !HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            Debug.LogError($"[HomeGazeExporter] データのフォルダ/実験の名前/実験対象者を正しく指定してください。{error}");
            return false;
        }

        if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Filtered)
            LoadFiltered(filteredMode.ToString());
        else if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image)
            LoadFiltered("Image");
        else
            LoadKinect();

        for (int i = 0; i < samples.Count; i++)
        {
            float interval = i + 1 < samples.Count
                ? samples[i + 1].time - samples[i].time
                : (i > 0 ? samples[i].time - samples[i - 1].time : 0f);

            samples[i].duration = Mathf.Clamp(interval, 0f, maxFrameDuration);

            foreach (Person person in samples[i].persons)
            {
                if (!person.fixedGaze)
                    person.hasGaze = TryGetGaze(person.joints, out person.origin, out person.direction);

                SetupBody(person);
            }
        }

        Debug.Log($"[HomeGazeExporter] {SourceName}: {samples.Count} samples を読み込みました。");
        return samples.Count > 0;
    }

    private void LoadFiltered(string modeName)
    {
        string path = HomeExperimentPaths.GetGroupedSkeletonPath(dataRootDirectory, env.ExperimentName, subjectName, modeName, skeletonVariant);

        if (!File.Exists(path))
        {
            string how = !string.IsNullOrWhiteSpace(skeletonVariant)
                ? $"Tools/GazeSmooth/smooth_skeleton.py で平滑化した骨格({skeletonVariant.Trim()})を作るか、Skeleton Variant を空にしてください"
                : "シーン3でグループを作って Save grouped を押してください";
            Debug.LogError($"[HomeGazeExporter] 人物ごとの骨格のファイルがありません。{how}: {path}");
            return;
        }

        HomeFilteredSkeletonList data = JsonUtility.FromJson<HomeFilteredSkeletonList>(File.ReadAllText(path));

        if (data == null || data.frames == null)
            return;

        foreach (HomeFilteredFrame frame in data.frames)
        {
            Sample sample = new Sample { time = frame.timeSec };

            foreach (HomeFilteredPerson person in frame.persons)
            {
                if (!personNames.ContainsKey(person.trackId))
                    personNames[person.trackId] = person.label;

                Person p = new Person { id = person.trackId, joints = person.joints };

                if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image)
                {
                    // 画像で推定した頭の位置と向きをそのまま使う(関節からの計算・下向きの補正はしない)
                    bool useGaze = imageDirection != HomeGazeAnalyzer.ImageDirection.Head && person.hasGazeDirection;
                    bool useHead = imageDirection != HomeGazeAnalyzer.ImageDirection.GazeOnly && person.hasHeadDirection;

                    p.fixedGaze = true;
                    p.origin = person.headPosition;
                    p.hasGaze = useGaze || useHead;
                    p.direction = useGaze ? person.gazeDirection.normalized : person.headDirection.normalized;
                    p.hasHead = true;
                    p.head = person.headPosition;
                }

                sample.persons.Add(p);
            }

            samples.Add(sample);
        }
    }

    private void LoadKinect()
    {
        foreach (HomeSkeletonTrack track in HomeSkeletonIO.LoadSubject(dataRootDirectory, env.ExperimentName, subjectName, "HomeGazeExporter"))
        {
            if (track.kinectId != kinectId.Trim())
                continue;

            for (int i = 0; i < track.frames.Count; i++)
            {
                Sample sample = new Sample { time = track.times[i] };

                if (track.frames[i].bodies != null)
                {
                    foreach (HomeSkeletonBody body in track.frames[i].bodies)
                        sample.persons.Add(new Person { id = (int)body.bodyId, joints = body.joints });
                }

                samples.Add(sample);
            }

            return;
        }

        Debug.LogError($"[HomeGazeExporter] Kinect{kinectId} の骨格データがありません。");
    }

    // HomeGazeAnalyzer.TryGetGaze と同じ視線方向の作り方
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

    // 距離に使う位置(骨盤。無ければ頭)と、見られる側の体を表す縦の円柱(旧シーン5の SetupCylinder と同じ)
    private void SetupBody(Person person)
    {
        if (!person.hasHead && person.joints != null && HomeSkeletonIO.TryGetJointPosition(person.joints, JointId.Head, out Vector3 head))
        {
            person.hasHead = true;
            person.head = head;
        }

        person.hasShoulders = person.joints != null &&
                              HomeSkeletonIO.TryGetJointPosition(person.joints, JointId.ShoulderLeft, out person.shoulderLeft) &&
                              HomeSkeletonIO.TryGetJointPosition(person.joints, JointId.ShoulderRight, out person.shoulderRight);

        if (person.joints != null && HomeSkeletonIO.TryGetJointPosition(person.joints, JointId.Pelvis, out Vector3 pelvis))
        {
            person.hasPosition = true;
            person.position = pelvis;
            person.positionJoint = "Pelvis";
        }
        else if (person.hasHead)
        {
            person.hasPosition = true;
            person.position = person.head;
            person.positionJoint = "Head";
        }

        float bottomY = float.MaxValue;
        float topY = float.MinValue;
        Vector3 allSum = Vector3.zero;
        int allCount = 0;
        Vector3 trunkSum = Vector3.zero;
        int trunkCount = 0;

        if (person.joints != null)
        {
            foreach (HomeSkeletonJoint joint in person.joints)
            {
                bottomY = Mathf.Min(bottomY, joint.position.y);
                topY = Mathf.Max(topY, joint.position.y);
                allSum += joint.position;
                allCount++;
            }

            foreach (JointId jointId in BodyJoints)
            {
                if (jointId != JointId.Head && HomeSkeletonIO.TryGetJointPosition(person.joints, jointId, out Vector3 position))
                {
                    trunkSum += position;
                    trunkCount++;
                }
            }
        }

        if (person.hasHead)
        {
            trunkSum += person.head;
            trunkCount++;
        }

        if (allCount == 0)
        {
            if (!person.hasHead)
                return;

            // 関節が無く頭の位置だけ分かるとき(画像での推定)は、頭を中心にした顔の範囲だけの円柱にする
            bottomY = person.head.y - faceHeight * 0.5f;
            topY = person.head.y + faceHeight * 0.5f;
        }
        else if (person.hasHead)
        {
            bottomY = Mathf.Min(bottomY, person.head.y);
            topY = Mathf.Max(topY, person.head.y);
        }

        // 中心軸は体幹(頭〜骨盤)の水平位置の平均。腕や脚を伸ばしても軸が体から外れないようにする
        Vector3 center = trunkCount > 0 ? trunkSum / trunkCount : allSum / allCount;

        person.hasCylinder = true;
        person.axisX = center.x;
        person.axisZ = center.z;
        person.bottomY = bottomY;
        person.topY = topY;
    }

    // ------------------------------------------------------------
    // Persons (人物のまとめ方はシーン3で済ませてある)
    // ------------------------------------------------------------
    private void BuildUnits()
    {
        units.Clear();
        SortedDictionary<int, float> seconds = new SortedDictionary<int, float>();

        foreach (Sample sample in samples)
        {
            foreach (Person person in sample.persons)
            {
                seconds.TryGetValue(person.id, out float s);
                seconds[person.id] = s + sample.duration;
            }
        }

        foreach (KeyValuePair<int, float> pair in seconds)
        {
            // 短い人物(誤検出など)は書き出さない
            if (pair.Value < minPersonSeconds)
                continue;

            string name = personNames.TryGetValue(pair.Key, out string label) && !string.IsNullOrEmpty(label) ? label : $"person_{pair.Key}";
            units.Add(new Unit { id = pair.Key, name = name, seconds = pair.Value });
        }

        StringBuilder text = new StringBuilder($"[HomeGazeExporter] 書き出す人物: {units.Count} 人");

        foreach (Unit unit in units)
            text.Append($"\n  {unit.name}: {unit.seconds:F1} s");

        Debug.Log(text.ToString());
    }

    private static Person FindPerson(Sample sample, int id)
    {
        foreach (Person person in sample.persons)
        {
            if (person.id == id)
                return person;
        }

        return null;
    }

    // ------------------------------------------------------------
    // Targets (シーン4のスコアの対象と同じ)
    // ------------------------------------------------------------
    private void SetupTargets()
    {
        targets.Clear();

        if (useEnvTargetObjects)
        {
            // シーン0で保存した対象物体を group ごとにまとめる(保存した順)
            List<string> groups = new List<string>();
            Dictionary<string, List<GameObject>> objectsByGroup = new Dictionary<string, List<GameObject>>();

            foreach (GameObject obj in env.TargetObjects)
            {
                HomeTargetObject marker = obj != null ? obj.GetComponent<HomeTargetObject>() : null;

                if (marker == null)
                    continue;

                if (!objectsByGroup.TryGetValue(marker.group, out List<GameObject> objects))
                {
                    objects = new List<GameObject>();
                    objectsByGroup[marker.group] = objects;
                    groups.Add(marker.group);
                }

                objects.Add(obj);
            }

            foreach (string group in groups)
                AddTarget(group, objectsByGroup[group]);
        }

        if (targetNames != null)
        {
            foreach (string targetName in targetNames)
            {
                if (string.IsNullOrWhiteSpace(targetName) || targets.Exists(t => t.name == targetName.Trim()))
                    continue;

                List<GameObject> found = FindRoomObjects(targetName.Trim());

                if (found.Count == 0)
                {
                    Debug.LogWarning($"[HomeGazeExporter] 対象が部屋オブジェクト/対象物体にありません: {targetName}");
                    continue;
                }

                AddTarget(targetName.Trim(), found);
            }
        }

        if (targetObjects != null)
        {
            foreach (GameObject target in targetObjects)
            {
                if (target != null && !targets.Exists(t => t.name == target.name))
                    AddTarget(target.name, new List<GameObject> { target });
            }
        }

        if (targets.Count == 0)
            Debug.LogWarning("[HomeGazeExporter] 対象物体がありません。シーン0で対象物体を置くか、Target Names / Target Objects を指定してください。");
        else
            Debug.Log($"[HomeGazeExporter] 対象物体: {string.Join(", ", targets.ConvertAll(t => t.name))}");
    }

    private void AddTarget(string targetName, List<GameObject> objects)
    {
        List<HomeConeSurface> clouds = new List<HomeConeSurface>();
        Bounds bounds = new Bounds();
        bool hasBounds = false;

        foreach (GameObject obj in objects)
        {
            foreach (MeshFilter meshFilter in obj.GetComponentsInChildren<MeshFilter>())
            {
                if (!EnsureReadable(meshFilter))
                    continue;

                Vector3[] vertices = meshFilter.sharedMesh.vertices;

                if (vertices.Length == 0)
                    continue;

                HomeConeSurface cloud = new HomeConeSurface(meshFilter.transform, vertices, meshFilter.sharedMesh.triangles);
                clouds.Add(cloud);

                foreach (Vector3 position in cloud.positions)
                {
                    if (!hasBounds)
                    {
                        bounds = new Bounds(position, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(position);
                    }
                }
            }
        }

        if (clouds.Count == 0)
        {
            Debug.LogWarning($"[HomeGazeExporter] 対象に頂点を読めるメッシュがありません: {targetName}");
            return;
        }

        targets.Add(new Target { name = targetName, clouds = clouds, bounds = bounds });
    }

    private List<GameObject> FindRoomObjects(string targetName)
    {
        List<GameObject> found = new List<GameObject>();
        List<GameObject> envObjects = new List<GameObject>(env.RoomObjects);
        envObjects.AddRange(env.TargetObjects);

        foreach (GameObject roomObject in envObjects)
        {
            if (roomObject == null)
                continue;

            foreach (Transform child in roomObject.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == targetName)
                    found.Add(child.gameObject);
            }
        }

        return found;
    }

    private static bool EnsureReadable(MeshFilter meshFilter)
    {
        Mesh mesh = meshFilter.sharedMesh;

        if (mesh == null)
            return false;

        if (mesh.isReadable)
            return true;

#if UNITY_EDITOR
        string path = UnityEditor.AssetDatabase.GetAssetPath(mesh);
        string meshName = mesh.name;

        if (UnityEditor.AssetImporter.GetAtPath(path) is UnityEditor.ModelImporter importer)
        {
            if (!importer.isReadable)
            {
                Debug.Log($"[HomeGazeExporter] {path} の Read/Write を有効にして再インポートします。");
                importer.isReadable = true;
                importer.SaveAndReimport();
            }

            if (meshFilter.sharedMesh == null || !meshFilter.sharedMesh.isReadable)
            {
                foreach (Object asset in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is Mesh reloaded && reloaded.name == meshName)
                    {
                        meshFilter.sharedMesh = reloaded;
                        break;
                    }
                }
            }
        }

        return meshFilter.sharedMesh != null && meshFilter.sharedMesh.isReadable;
#else
        return false;
#endif
    }

    // ------------------------------------------------------------
    // 注視の判定
    // ------------------------------------------------------------
    // 対象物体ごとのコーン軸との角度(シーン4と同じ: コーン軸と表面との最小角度。軸が貫けば 0。コーンに入らなければ -1)
    private void ComputeGaze()
    {
        float coneAngleRad = Mathf.Max(coneAngle, 0.0001f) * Mathf.Deg2Rad;

        foreach (Sample sample in samples)
        {
            foreach (Person person in sample.persons)
            {
                person.targetAngles = new float[targets.Count];
                person.targetDistances = new float[targets.Count];

                for (int t = 0; t < targets.Count; t++)
                {
                    person.targetAngles[t] = -1f;
                    person.targetDistances[t] = float.MaxValue;
                }

                if (!person.hasGaze)
                    continue;

                for (int t = 0; t < targets.Count; t++)
                {
                    float best = float.MaxValue;
                    float bestDistance = float.MaxValue;

                    foreach (HomeConeSurface cloud in targets[t].clouds)
                    {
                        if (cloud.MinConeAngle(person.origin, person.direction, coneAngleRad, coneDistance,
                                               out float angleRad, out float distance) &&
                            HomeConeSurface.IsBetter(angleRad * Mathf.Rad2Deg, distance, best, bestDistance))
                        {
                            best = angleRad * Mathf.Rad2Deg;
                            bestDistance = distance;
                        }
                    }

                    if (best < float.MaxValue)
                    {
                        person.targetAngles[t] = best;
                        person.targetDistances[t] = bestDistance;
                    }
                }
            }
        }
    }

    // viewer の視線のコーンが、other の円柱の高さ bottomY 〜 topY の部分に当たるときのコーン軸との角度(度)。当たらなければ -1。
    // 軸が円柱を貫けば 0 度(distance は手前の面まで)。貫かなければ旧シーン5の LooksAtCylinder と同じく、円柱の上に並べた点で判定する
    private float CylinderAngle(Person viewer, Person other, float bottomY, float topY, out float distance)
    {
        distance = float.MaxValue;

        if (!viewer.hasGaze || !other.hasCylinder)
            return -1f;

        float radius = personDiameter * 0.5f;

        if (RayHitsCylinder(viewer.origin, viewer.direction, other.axisX, other.axisZ, radius, bottomY, topY, out float hit) &&
            hit > 0.05f && hit <= lookAtPersonDistance)
        {
            distance = hit;
            return 0f;
        }

        int rings = Mathf.Max(1, Mathf.CeilToInt((topY - bottomY) / CylinderStep));
        float bestDot = -2f;

        for (int i = 0; i <= rings; i++)
        {
            float y = Mathf.Lerp(bottomY, topY, i / (float)rings);

            // k = -1 は中心軸上の点(上から/下から見たときのため)
            for (int k = -1; k < CylinderSides; k++)
            {
                Vector3 p = new Vector3(other.axisX, y, other.axisZ);

                if (k >= 0)
                {
                    float angle = k * 2f * Mathf.PI / CylinderSides;
                    p.x += Mathf.Cos(angle) * radius;
                    p.z += Mathf.Sin(angle) * radius;
                }

                Vector3 toPoint = p - viewer.origin;
                float pointDistance = toPoint.magnitude;

                if (pointDistance <= 0.05f || pointDistance > lookAtPersonDistance)
                    continue;

                float dot = Vector3.Dot(viewer.direction, toPoint) / pointDistance;

                if (dot > bestDot)
                {
                    bestDot = dot;
                    distance = pointDistance;
                }
            }
        }

        return bestDot >= Mathf.Cos(coneAngle * Mathf.Deg2Rad)
            ? Mathf.Acos(Mathf.Clamp(bestDot, -1f, 1f)) * Mathf.Rad2Deg
            : -1f;
    }

    // 始点 origin・向き direction の半直線が、縦の円柱(中心軸 axisX, axisZ・半径 radius・高さ bottomY 〜 topY)に
    // 外から入るときの距離。始点が円柱の中(水平に見て円の中)のときは false
    private static bool RayHitsCylinder(Vector3 origin, Vector3 direction, float axisX, float axisZ, float radius,
                                        float bottomY, float topY, out float distance)
    {
        distance = float.MaxValue;
        float ox = origin.x - axisX;
        float oz = origin.z - axisZ;
        float c = ox * ox + oz * oz - radius * radius;

        if (c <= 0f)
            return false;

        // 側面
        float a = direction.x * direction.x + direction.z * direction.z;
        float b = ox * direction.x + oz * direction.z;

        if (a > 1e-8f && b < 0f && b * b - a * c >= 0f)
        {
            float s = (-b - Mathf.Sqrt(b * b - a * c)) / a;
            float y = origin.y + s * direction.y;

            if (s > 0f && y >= bottomY && y <= topY)
                distance = s;
        }

        // 上下の面
        if (Mathf.Abs(direction.y) > 1e-6f)
        {
            foreach (float capY in new[] { bottomY, topY })
            {
                float s = (capY - origin.y) / direction.y;

                if (s <= 0f || s >= distance)
                    continue;

                float x = ox + s * direction.x;
                float z = oz + s * direction.z;

                if (x * x + z * z <= radius * radius)
                    distance = s;
            }
        }

        return distance < float.MaxValue;
    }

    private float FaceBottomY(Person person) => Mathf.Max(person.bottomY, person.topY - faceHeight);

    // ------------------------------------------------------------
    // CSV
    // ------------------------------------------------------------
    // 列: timeSec, durationSec, present, hasGaze, target, targetType, angleDeg, 視線の始点と向き, angle_<対象>...
    //     (withPersons のとき) angle_<相手>, face_<相手>(視線が相手の顔の範囲に当たっていれば 1)
    private string WriteGazeCsv(string path, int unitIndex, bool withPersons)
    {
        Unit unit = units[unitIndex];
        List<Unit> others = withPersons ? units.FindAll(u => u.id != unit.id) : new List<Unit>();

        using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            StringBuilder line = new StringBuilder("timeSec,durationSec,present,hasGaze,target,targetType,angleDeg," +
                                                   "originX,originY,originZ,directionX,directionY,directionZ");

            foreach (Target target in targets)
                line.Append(',').Append(Csv("angle_" + target.name));

            foreach (Unit other in others)
                line.Append(',').Append(Csv("angle_" + other.name)).Append(',').Append(Csv("face_" + other.name));

            line.Append(",shoulderLeftX,shoulderLeftY,shoulderLeftZ,shoulderRightX,shoulderRightY,shoulderRightZ");
            writer.WriteLine(line.ToString());

            foreach (Sample sample in samples)
            {
                line.Clear();
                line.Append(F(sample.time)).Append(',').Append(F(sample.duration));
                Person person = FindPerson(sample, unit.id);

                if (person == null || !person.hasGaze)
                {
                    line.Append(person == null ? ",0,0," : ",1,0,").Append(NoDataLabel).Append(',').Append(NoDataLabel).Append(",");
                    line.Append(',', 6 + targets.Count + others.Count * 2 + 6);
                    writer.WriteLine(line.ToString());
                    continue;
                }

                // 対象物体と相手のうち、コーン軸に最も近いもの(同じ角度なら近い方)
                string best = NoHitLabel;
                string bestType = NoHitLabel;
                float bestAngle = float.MaxValue;
                float bestDistance = float.MaxValue;

                for (int t = 0; t < targets.Count; t++)
                {
                    float angle = person.targetAngles[t];

                    if (angle >= 0f && HomeConeSurface.IsBetter(angle, person.targetDistances[t], bestAngle, bestDistance))
                    {
                        bestAngle = angle;
                        bestDistance = person.targetDistances[t];
                        best = targets[t].name;
                        bestType = "Object";
                    }
                }

                float[] personAngles = new float[others.Count];
                bool[] faces = new bool[others.Count];

                for (int o = 0; o < others.Count; o++)
                {
                    Person other = FindPerson(sample, others[o].id);
                    float personDistance = float.MaxValue;
                    personAngles[o] = other != null ? CylinderAngle(person, other, other.bottomY, other.topY, out personDistance) : -1f;
                    faces[o] = other != null && personAngles[o] >= 0f &&
                               CylinderAngle(person, other, FaceBottomY(other), other.topY, out _) >= 0f;

                    if (personAngles[o] >= 0f && HomeConeSurface.IsBetter(personAngles[o], personDistance, bestAngle, bestDistance))
                    {
                        bestAngle = personAngles[o];
                        bestDistance = personDistance;
                        best = others[o].name;
                        bestType = "Person";
                    }
                }

                line.Append(",1,1,").Append(Csv(best)).Append(',').Append(bestType).Append(',')
                    .Append(bestAngle < float.MaxValue ? F(bestAngle) : "");

                foreach (float v in new[] { person.origin.x, person.origin.y, person.origin.z, person.direction.x, person.direction.y, person.direction.z })
                    line.Append(',').Append(F(v));

                foreach (float angle in person.targetAngles)
                    line.Append(',').Append(F(angle));

                for (int o = 0; o < others.Count; o++)
                    line.Append(',').Append(F(personAngles[o])).Append(',').Append(faces[o] ? "1" : "0");

                if (person.hasShoulders)
                {
                    foreach (float v in new[] { person.shoulderLeft.x, person.shoulderLeft.y, person.shoulderLeft.z,
                                                person.shoulderRight.x, person.shoulderRight.y, person.shoulderRight.z })
                        line.Append(',').Append(F(v));
                }
                else
                {
                    line.Append(',', 6);
                }

                writer.WriteLine(line.ToString());
            }
        }

        return path;
    }

    // 列: timeSec, durationSec, present, positionJoint, positionX, positionZ, dist_<対象>..., (2人以上のとき) dist_<相手>...
    private string WriteDistanceCsv(string path, int unitIndex)
    {
        Unit unit = units[unitIndex];
        List<Unit> others = units.FindAll(u => u.id != unit.id);

        using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            StringBuilder line = new StringBuilder("timeSec,durationSec,present,positionJoint,positionX,positionZ");

            foreach (Target target in targets)
                line.Append(',').Append(Csv("dist_" + target.name));

            foreach (Unit other in others)
                line.Append(',').Append(Csv("dist_" + other.name));

            writer.WriteLine(line.ToString());

            foreach (Sample sample in samples)
            {
                line.Clear();
                line.Append(F(sample.time)).Append(',').Append(F(sample.duration));
                Person person = FindPerson(sample, unit.id);

                if (person == null || !person.hasPosition)
                {
                    line.Append(person == null ? ",0" : ",1").Append(',', 3 + targets.Count + others.Count);
                    writer.WriteLine(line.ToString());
                    continue;
                }

                line.Append(",1,").Append(person.positionJoint).Append(',').Append(F(person.position.x)).Append(',').Append(F(person.position.z));

                foreach (Target target in targets)
                    line.Append(',').Append(F(HorizontalDistance(target.bounds, person.position)));

                foreach (Unit otherUnit in others)
                {
                    Person other = FindPerson(sample, otherUnit.id);
                    line.Append(',');

                    if (other != null && other.hasPosition)
                        line.Append(F(new Vector2(other.position.x - person.position.x, other.position.z - person.position.z).magnitude));
                }

                writer.WriteLine(line.ToString());
            }
        }

        return path;
    }

    // 列: timeSec, durationSec, present, そして関節ごと(JointId の順)に <関節>X, <関節>Y, <関節>Z, <関節>C
    private string WriteSkeletonCsv(string path, int unitIndex)
    {
        Unit unit = units[unitIndex];
        int jointCount = (int)JointId.Count;
        string[] names = new string[jointCount];

        using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            StringBuilder line = new StringBuilder("timeSec,durationSec,present");

            for (int j = 0; j < jointCount; j++)
            {
                names[j] = ((JointId)j).ToString();
                line.Append(',').Append(names[j]).Append("X,").Append(names[j]).Append("Y,")
                    .Append(names[j]).Append("Z,").Append(names[j]).Append('C');
            }

            writer.WriteLine(line.ToString());
            Dictionary<string, HomeSkeletonJoint> byName = new Dictionary<string, HomeSkeletonJoint>();

            foreach (Sample sample in samples)
            {
                line.Clear();
                line.Append(F(sample.time)).Append(',').Append(F(sample.duration));
                Person person = FindPerson(sample, unit.id);
                line.Append(person == null ? ",0" : ",1");
                byName.Clear();

                if (person != null && person.joints != null)
                {
                    foreach (HomeSkeletonJoint joint in person.joints)
                        byName[joint.jointId] = joint;
                }

                for (int j = 0; j < jointCount; j++)
                {
                    if (byName.TryGetValue(names[j], out HomeSkeletonJoint joint))
                    {
                        line.Append(',').Append(F(joint.position.x)).Append(',').Append(F(joint.position.y))
                            .Append(',').Append(F(joint.position.z)).Append(',').Append(joint.confidence);
                    }
                    else
                    {
                        line.Append(',', 4);
                    }
                }

                writer.WriteLine(line.ToString());
            }
        }

        return path;
    }

    // 位置から箱(床に平行)までの水平の最短距離。箱の中なら 0(シーン6/7と同じ)
    private static float HorizontalDistance(Bounds bounds, Vector3 position)
    {
        float dx = Mathf.Max(bounds.min.x - position.x, 0f, position.x - bounds.max.x);
        float dz = Mathf.Max(bounds.min.z - position.z, 0f, position.z - bounds.max.z);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private ExportParameters CreateParameters()
    {
        ExportParameters p = new ExportParameters
        {
            experimentName = env.ExperimentName,
            subjectName = subjectName,
            createdAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            skeletonSource = SourceName,
            headDirectionMethod = headDirectionMethod.ToString(),
            downwardAngle = skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image ? 0f : downwardAngle,
            coneAngle = coneAngle,
            coneDistance = coneDistance,
            personDiameter = personDiameter,
            faceHeight = faceHeight,
            lookAtPersonDistance = lookAtPersonDistance,
            minPersonSeconds = minPersonSeconds,
            maxFrameDuration = maxFrameDuration,
            frameCount = samples.Count,
            noHitLabel = NoHitLabel,
            noDataLabel = NoDataLabel,
            distanceDefinition = "骨盤(無ければ頭)から、対象物体のメッシュの頂点を囲む床に平行な箱までの水平距離(箱の中なら0)。相手は骨盤どうしの水平距離",
            gazeHitTest = "surface: コーン軸と対象物体の表面(メッシュの三角形)との最小角度。軸が貫けば0度。同じ角度なら近い方。相手は円柱(貫けば0度、それ以外は円柱上の点)"
        };

        foreach (Unit unit in units)
        {
            p.persons.Add(unit.name);
            p.personSeconds.Add(unit.seconds);
        }

        foreach (Target target in targets)
        {
            p.targets.Add(target.name);
            p.targetCenters.Add(target.bounds.center);
            p.targetSizes.Add(target.bounds.size);
        }

        return p;
    }

    private static string F(float value) =>
        float.IsNaN(value) ? "" : value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }

    // ------------------------------------------------------------
    // Types
    // ------------------------------------------------------------
    private class Sample
    {
        public float time;
        public float duration;
        public readonly List<Person> persons = new List<Person>();
    }

    private class Person
    {
        public int id;
        public List<HomeSkeletonJoint> joints;
        public bool hasGaze;
        public Vector3 origin;
        public Vector3 direction;
        public bool fixedGaze;

        public bool hasHead;
        public Vector3 head;

        // 肩の位置(頭と肩の向きの解析に使う。画像での推定など関節が無いときは false)
        public bool hasShoulders;
        public Vector3 shoulderLeft;
        public Vector3 shoulderRight;

        // 距離に使う位置(骨盤。無ければ頭)
        public bool hasPosition;
        public Vector3 position;
        public string positionJoint;

        // 見られる側としての体を表す縦の円柱(中心軸の水平位置と、下端・上端の高さ)。上部 faceHeight が顔
        public bool hasCylinder;
        public float axisX;
        public float axisZ;
        public float bottomY;
        public float topY;

        // 対象物体ごとのコーン軸との角度(度)。コーンに入らなければ -1
        public float[] targetAngles;
        // 対象物体ごとの、その角度の点までの距離(同じ角度のときに近い方を選ぶため)
        public float[] targetDistances;
    }

    private class Unit
    {
        public int id;
        public string name;
        public float seconds;
    }

    private class Target
    {
        public string name;
        public List<HomeConeSurface> clouds;
        // 頂点を囲む箱(距離に使う)
        public Bounds bounds;
    }

    [System.Serializable]
    private class ExportParameters
    {
        public string experimentName;
        public string subjectName;
        public string createdAt;
        public string skeletonSource;
        public string headDirectionMethod;
        public float downwardAngle;
        public float coneAngle;
        public float coneDistance;
        public float personDiameter;
        public float faceHeight;
        public float lookAtPersonDistance;
        public float minPersonSeconds;
        public float maxFrameDuration;
        public int frameCount;
        public string noHitLabel;
        public string noDataLabel;
        public string distanceDefinition;
        public string gazeHitTest;
        public List<string> persons = new List<string>();
        public List<float> personSeconds = new List<float>();
        public List<string> targets = new List<string>();
        // 対象物体の頂点を囲む箱の中心と大きさ(targets と同じ順)
        public List<Vector3> targetCenters = new List<Vector3>();
        public List<Vector3> targetSizes = new List<Vector3>();
    }
}
