using System.Collections.Generic;
using System.IO;
using Microsoft.Azure.Kinect.BodyTracking;
using UnityEngine;

// シーン2(HomeSkeletonPlayer) / シーン3(HomeSkeletonFilter) で共通の、骨格データの読み込みと表示。

// キネクト1台分の骨格データ(<ID>_skeleton.json)
public class HomeSkeletonTrack
{
    public string kinectId;
    public List<HomeSkeletonFrame> frames;

    // 各フレームの時刻(秒)。全キネクト共通の recordingTimeSec を使い、古い形式で無ければキネクト本体のタイムスタンプを使う。
    public float[] times;

    public float Duration => times.Length > 0 ? times[times.Length - 1] : 0f;

    // time 以下で最も新しいフレームの番号(無ければ -1)
    public int FindFrameIndex(float time)
    {
        int low = 0;
        int high = times.Length - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;

            if (times[mid] <= time)
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
}

public static class HomeSkeletonIO
{
    private static readonly Dictionary<string, JointId> jointIdByName = CreateJointIdTable();

    public static bool TryParseJointId(string name, out JointId jointId) =>
        jointIdByName.TryGetValue(name, out jointId);

    public static bool TryGetJointPosition(List<HomeSkeletonJoint> joints, JointId jointId, out Vector3 position)
    {
        string name = jointId.ToString();

        foreach (HomeSkeletonJoint joint in joints)
        {
            if (joint.jointId == name)
            {
                position = joint.position;
                return true;
            }
        }

        position = Vector3.zero;
        return false;
    }

    // SkeletonRealtimeMulti.BodyData.GetAverageConfidence と同じ(全関節の confidence の平均)
    public static float GetAverageConfidence(List<HomeSkeletonJoint> joints)
    {
        if (joints == null || joints.Count == 0)
            return 0f;

        int sum = 0;

        foreach (HomeSkeletonJoint joint in joints)
            sum += joint.confidence;

        return (float)sum / joints.Count;
    }

    // 指定した関節だけの confidence の平均(該当する関節が無ければ 0)
    public static float GetAverageConfidence(List<HomeSkeletonJoint> joints, HashSet<string> jointNames)
    {
        if (joints == null)
            return 0f;

        int sum = 0;
        int count = 0;

        foreach (HomeSkeletonJoint joint in joints)
        {
            if (!jointNames.Contains(joint.jointId))
                continue;

            sum += joint.confidence;
            count++;
        }

        return count > 0 ? (float)sum / count : 0f;
    }

    // 実験対象者のフォルダ直下の *_skeleton.json をすべて読み込む。エラー時は空のリストを返す。
    public static List<HomeSkeletonTrack> LoadSubject(string experimentName, string subjectName, string logTag)
    {
        List<HomeSkeletonTrack> tracks = new List<HomeSkeletonTrack>();

        if (!HomeExperimentPaths.IsValidFolderName(experimentName, out string error))
        {
            Debug.LogError($"[{logTag}] 実験の名前を正しく入力してください。{error}");
            return tracks;
        }

        if (!HomeExperimentPaths.IsValidFolderName(subjectName, out error))
        {
            Debug.LogError($"[{logTag}] 実験対象者を正しく入力してください。{error}");
            return tracks;
        }

        string subjectDir = HomeExperimentPaths.GetSubjectDirectory(experimentName, subjectName);

        if (!Directory.Exists(subjectDir))
        {
            Debug.LogError($"[{logTag}] 実験対象者のフォルダがありません: {subjectDir}");
            return tracks;
        }

        string[] files = Directory.GetFiles(subjectDir, "*" + HomeExperimentPaths.SkeletonFileSuffix);
        System.Array.Sort(files);

        foreach (string file in files)
        {
            HomeSkeletonFrameList data = JsonUtility.FromJson<HomeSkeletonFrameList>(File.ReadAllText(file));

            if (data == null || data.frames == null || data.frames.Count == 0)
            {
                Debug.LogWarning($"[{logTag}] フレームがないためスキップします: {file}");
                continue;
            }

            HomeSkeletonTrack track = new HomeSkeletonTrack
            {
                kinectId = string.IsNullOrEmpty(data.kinectId)
                    ? Path.GetFileName(file).Replace(HomeExperimentPaths.SkeletonFileSuffix, "")
                    : data.kinectId,
                frames = data.frames,
                times = new float[data.frames.Count]
            };

            bool hasRecordingTime = data.frames.Exists(f => f.recordingTimeSec != 0f);

            for (int i = 0; i < data.frames.Count; i++)
            {
                track.times[i] = hasRecordingTime
                    ? data.frames[i].recordingTimeSec
                    : data.frames[i].normalizedTimestampTicks * 1e-7f;
            }

            tracks.Add(track);
            Debug.Log($"[{logTag}] Kinect{track.kinectId}: {data.frames.Count} frames, {track.Duration:F1}s ({file})");
        }

        if (tracks.Count == 0)
            Debug.LogError($"[{logTag}] *{HomeExperimentPaths.SkeletonFileSuffix} が見つかりません: {subjectDir}");

        return tracks;
    }

    private static Dictionary<string, JointId> CreateJointIdTable()
    {
        Dictionary<string, JointId> table = new Dictionary<string, JointId>();

        for (JointId jointId = JointId.Pelvis; jointId < JointId.Count; jointId++)
            table[jointId.ToString()] = jointId;

        return table;
    }
}

// 骨格1人分の表示(関節の球・骨の線・頭部方向の線)
public class HomeSkeletonBodyVisual
{
    public class Style
    {
        public GameObject jointPrefab;
        public Material lineMaterial;
        public float jointScale = 0.06f;
        public float lineWidth = 0.02f;
        public bool showHeadDirection = true;
        public float headDirectionLength = 0.4f;
        public float headDirectionWidth = 0.015f;
    }

    private static readonly JointId[][] boneChains =
    {
        new[] { JointId.Pelvis, JointId.SpineNavel, JointId.SpineChest, JointId.Neck, JointId.Head, JointId.Nose },
        new[] { JointId.FootRight, JointId.AnkleRight, JointId.KneeRight, JointId.HipRight, JointId.Pelvis,
                JointId.HipLeft, JointId.KneeLeft, JointId.AnkleLeft, JointId.FootLeft },
        new[] { JointId.HandTipLeft, JointId.HandLeft, JointId.WristLeft, JointId.ElbowLeft, JointId.ShoulderLeft,
                JointId.ClavicleLeft, JointId.SpineChest, JointId.ClavicleRight, JointId.ShoulderRight,
                JointId.ElbowRight, JointId.WristRight, JointId.HandRight, JointId.HandTipRight }
    };

    private static Material defaultLineMaterial;

    private readonly Style style;
    private readonly Dictionary<JointId, GameObject> joints = new Dictionary<JointId, GameObject>();
    private readonly List<LineRenderer> lines = new List<LineRenderer>();
    private readonly LineRenderer headDirection;

    public HomeSkeletonBodyVisual(Transform parent, string name, Color color, Style style)
    {
        this.style = style;

        Transform root = new GameObject(name).transform;
        root.SetParent(parent, false);

        for (JointId jointId = JointId.Pelvis; jointId < JointId.Count; jointId++)
        {
            GameObject obj = style.jointPrefab != null
                ? Object.Instantiate(style.jointPrefab)
                : GameObject.CreatePrimitive(PrimitiveType.Sphere);

            obj.transform.SetParent(root, false);
            obj.transform.localScale = Vector3.one * style.jointScale;
            obj.name = jointId.ToString();

            foreach (Collider collider in obj.GetComponentsInChildren<Collider>())
                Object.Destroy(collider);

            Renderer renderer = obj.GetComponentInChildren<Renderer>();

            if (renderer != null)
                renderer.material.color = color;

            obj.SetActive(false);
            joints[jointId] = obj;
        }

        foreach (JointId[] chain in boneChains)
            lines.Add(CreateLine(root, "Bone", chain.Length, style.lineWidth, color));

        if (style.showHeadDirection)
            headDirection = CreateLine(root, "HeadDirection", 2, style.headDirectionWidth, Color.Lerp(color, Color.white, 0.5f));
    }

    public void Apply(List<HomeSkeletonJoint> bodyJoints)
    {
        foreach (GameObject joint in joints.Values)
            joint.SetActive(false);

        foreach (HomeSkeletonJoint joint in bodyJoints)
        {
            if (HomeSkeletonIO.TryParseJointId(joint.jointId, out JointId jointId) &&
                joints.TryGetValue(jointId, out GameObject obj))
            {
                obj.transform.position = joint.position;
                obj.SetActive(true);
            }
        }

        for (int i = 0; i < boneChains.Length; i++)
        {
            JointId[] chain = boneChains[i];
            LineRenderer line = lines[i];
            bool valid = true;

            for (int j = 0; j < chain.Length; j++)
            {
                if (!TryGetVisibleJoint(chain[j], out Vector3 position))
                {
                    valid = false;
                    break;
                }

                line.SetPosition(j, position);
            }

            line.enabled = valid;
        }

        if (headDirection != null)
        {
            bool hasHead = TryGetVisibleJoint(JointId.Head, out Vector3 head);
            bool hasNose = TryGetVisibleJoint(JointId.Nose, out Vector3 nose);
            bool valid = hasHead && hasNose;

            if (valid)
            {
                // 両耳の中点→鼻 の向きを頭部方向とする(耳が無ければ 頭→鼻)
                bool hasEarLeft = TryGetVisibleJoint(JointId.EarLeft, out Vector3 earLeft);
                bool hasEarRight = TryGetVisibleJoint(JointId.EarRight, out Vector3 earRight);
                Vector3 from = hasEarLeft && hasEarRight ? (earLeft + earRight) * 0.5f : head;

                Vector3 direction = nose - from;
                valid = direction.sqrMagnitude > 1e-8f;

                if (valid)
                {
                    headDirection.SetPosition(0, head);
                    headDirection.SetPosition(1, head + direction.normalized * style.headDirectionLength);
                }
            }

            headDirection.enabled = valid;
        }
    }

    public void SetVisible(bool visible)
    {
        foreach (GameObject joint in joints.Values)
            joint.SetActive(visible);

        foreach (LineRenderer line in lines)
            line.enabled = visible;

        if (headDirection != null)
            headDirection.enabled = visible;
    }

    private bool TryGetVisibleJoint(JointId jointId, out Vector3 position)
    {
        if (joints.TryGetValue(jointId, out GameObject obj) && obj.activeSelf)
        {
            position = obj.transform.position;
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    private LineRenderer CreateLine(Transform parent, string name, int positionCount, float width, Color color)
    {
        LineRenderer line = new GameObject(name).AddComponent<LineRenderer>();
        line.transform.SetParent(parent, false);
        line.sharedMaterial = style.lineMaterial != null ? style.lineMaterial : GetDefaultLineMaterial();
        line.startColor = color;
        line.endColor = color;
        line.startWidth = width;
        line.endWidth = width;
        line.positionCount = positionCount;
        line.enabled = false;
        return line;
    }

    private static Material GetDefaultLineMaterial()
    {
        // 頂点カラー対応なので LineRenderer の startColor/endColor がそのまま色になる
        if (defaultLineMaterial == null)
            defaultLineMaterial = new Material(Shader.Find("Sprites/Default"));

        return defaultLineMaterial;
    }
}
