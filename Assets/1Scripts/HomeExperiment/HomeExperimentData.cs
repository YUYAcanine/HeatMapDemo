using System;
using System.Collections.Generic;
using UnityEngine;

// 4HomeExperiment のシーン間でやり取りするJSONの構造。

// ============================================================
// Env: キネクトの位置姿勢 (Env/KinectA_Transform.json)
// ============================================================
// position / rotation(オイラー角) は従来の Data/Calibration/KinectA_Transform.json と同じ形式。
// オイラー角の丸め誤差を避けるため rotationQuaternion も併記し、読み込み時はこちらを優先する。
[Serializable]
public class HomeKinectTransformData
{
    public Vector3 position;
    public Vector3 rotation;
    public Quaternion rotationQuaternion;

    public Quaternion GetRotation()
    {
        bool hasQuaternion =
            rotationQuaternion.x != 0f || rotationQuaternion.y != 0f ||
            rotationQuaternion.z != 0f || rotationQuaternion.w != 0f;

        return hasQuaternion ? rotationQuaternion : Quaternion.Euler(rotation);
    }
}

// ============================================================
// Env: 部屋オブジェクト (Env/RoomObjects.json)
// ============================================================
[Serializable]
public class HomeRoomObjectList
{
    public string experimentName;
    public string savedAt;
    public List<HomeRoomObjectData> objects = new List<HomeRoomObjectData>();
}

[Serializable]
public class HomeRoomObjectData
{
    public const string SourceAsset = "Asset";
    public const string SourceMesh = "Mesh";

    public string name;

    // "Asset": モデル(.obj/.fbx)やPrefabのインスタンス。assetGuid/assetPath からそのまま再生成する。
    // "Mesh" : メッシュ1つ分。メッシュがアセット(モデル内のメッシュなど)なら meshAssetGuid/meshLocalId で参照し、
    //          アセットでなければ Env/Meshes/<meshFile> に書き出したメッシュから再生成する。
    public string sourceType;
    public string assetGuid;
    public string assetPath;
    public string meshAssetGuid;
    public string meshAssetPath;
    public long meshLocalId;
    public string meshFile;

    // ワールド座標でのTransform
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale = Vector3.one;

    // sourceType == "Mesh" のときのマテリアル(サブメッシュ順)
    public List<HomeMaterialData> materials = new List<HomeMaterialData>();
}

[Serializable]
public class HomeMaterialData
{
    public string assetGuid;
    public string assetPath;
    // モデル(.obj)に埋め込まれたマテリアルのようなサブアセットを区別するためのID
    public long localId;
    public Color color = Color.white;
}

[Serializable]
public class HomeMeshData
{
    public Vector3[] vertices;
    public Vector3[] normals;
    public Vector2[] uv;
    public Color[] colors;
    public List<HomeSubMeshData> subMeshes = new List<HomeSubMeshData>();
}

[Serializable]
public class HomeSubMeshData
{
    public int topology;
    public int[] indices;
}

// ============================================================
// Skeleton: 骨格データ (Skeleton/<実験対象者>/A_skeleton.json)
// ============================================================
// frames 以下は従来の SkeletonRealtimeMulti が出力する A_skeleton.json と同じ形式
// (deviceTimestampTicks / normalizedTimestampTicks / unityTime / bodies[joints])。
// 追加で recordingTimeSec を持つ。
[Serializable]
public class HomeSkeletonFrameList
{
    public string experimentName;
    public string subjectName;
    public string kinectId;
    public int deviceIndex;
    public string recordingStartedAt;
    public List<HomeSkeletonFrame> frames = new List<HomeSkeletonFrame>();
}

[Serializable]
public class HomeSkeletonFrame
{
    // キネクト本体のタイムスタンプ(1tick = 100ns)
    public long deviceTimestampTicks;

    // このキネクトで最初に記録したフレームを0としたタイムスタンプ
    public long normalizedTimestampTicks;

    // キネクトからキャプチャを受け取った時刻(全キネクト共通の時計, 秒)
    public float unityTime;

    // Spaceキーで記録を開始した瞬間を0とした時刻(秒)。全キネクトで共通の時計なので、
    // 複数キネクトのデータを並べて再生するときはこちらを使う。
    public float recordingTimeSec;

    public List<HomeSkeletonBody> bodies = new List<HomeSkeletonBody>();
}

[Serializable]
public class HomeSkeletonBody
{
    public uint bodyId;
    public List<HomeSkeletonJoint> joints = new List<HomeSkeletonJoint>();
}

[Serializable]
public class HomeSkeletonJoint
{
    public string jointId;

    // 部屋(ワールド)座標
    public Vector3 position;

    // Microsoft.Azure.Kinect.BodyTracking.JointConfidenceLevel (0:None 1:Low 2:Medium 3:High)
    public int confidence;
}

// ============================================================
// Filtered: 信頼度フィルタリング後の骨格 (Skeleton/<実験対象者>/Filtered/filtered_skeleton.json)
// ============================================================
// SkeletonRealtimePublisher と同じ方法で全キネクトの骨格を統合した結果。
// 各時刻で「近い骨格は同一人物とみなして信頼度の高いものだけ」を残し、人物ごとにトラックIDを振る。
[Serializable]
public class HomeFilteredSkeletonList
{
    public string experimentName;
    public string subjectName;
    public string createdAt;
    public List<string> sourceKinects = new List<string>();

    // 使ったパラメータ
    // confidenceMode: "AllJoints"(全関節の平均) / "HeadJoints"(headJoints の平均)
    public string confidenceMode;
    public List<string> headJoints = new List<string>();
    public float sameBodyDistance;
    public float trackMatchDistance;
    public float trackTimeout;
    public float sampleInterval;
    public float maxFrameAge;

    public List<HomeFilteredFrame> frames = new List<HomeFilteredFrame>();
}

[Serializable]
public class HomeFilteredFrame
{
    // 記録開始からの時刻(秒, recordingTimeSec と同じ時計)
    public float timeSec;
    public List<HomeFilteredPerson> persons = new List<HomeFilteredPerson>();
}

[Serializable]
public class HomeFilteredPerson
{
    public int trackId;
    public string label;

    // 採用した骨格の出どころ
    public string kinectId;
    public uint bodyId;

    // 採用の判定に使った信頼度(confidenceMode に応じて allJointConfidence か headJointConfidence)
    public float confidence;
    public float allJointConfidence;
    public float headJointConfidence;

    public List<HomeSkeletonJoint> joints = new List<HomeSkeletonJoint>();
}
