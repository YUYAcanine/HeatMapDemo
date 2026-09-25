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

    // 位置を合わせたときのキネクト本体(シーン0で保存)。deviceIndex の順番はUSBのつなぎ方で変わりうるので、
    // 記録するシーンではシリアル番号が一致するか確認する。古いファイルでは空。
    public string serialNumber;
    public int deviceIndex = -1;

    public Quaternion GetRotation()
    {
        bool hasQuaternion =
            rotationQuaternion.x != 0f || rotationQuaternion.y != 0f ||
            rotationQuaternion.z != 0f || rotationQuaternion.w != 0f;

        return hasQuaternion ? rotationQuaternion : Quaternion.Euler(rotation);
    }
}

// ============================================================
// Env: 部屋オブジェクト (Env/RoomObjects.json) / 対象物体 (Env/TargetObjects.json)
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

    // 対象物体(TargetObjects.json)のときだけ使う。シーン0で部屋オブジェクトの下に置いた物体の名前で、
    // その物体の子のメッシュも同じ group になる。シーン4ではこの group ごとにスコアを数える。
    public string group;

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

    // 以下は画像での推定 (Tools/GazePipeline → filtered_Image.json) のときだけ入る(部屋座標)
    // 頭の中心
    public Vector3 headPosition;
    // 顔の正面の向き (6DRepNet360)
    public bool hasHeadDirection;
    public Vector3 headDirection;
    // 目の視線 (L2CS-Net). 顔がカメラの方を向いているときだけ
    public bool hasGazeDirection;
    public Vector3 gazeDirection;
    public float gazeConfidence;
}

// ============================================================
// Analysis: 視線コーンの解析結果 (Skeleton/<実験対象者>/Analysis/)
// ============================================================
// ConeHeatMapShow / ScoreHeat / ScoreHeatSummary と同じ方法で視線方向を求める:
//   頭部方向(Head→Nose) を 顔の右方向軸まわりに downwardAngle だけ下へ回し、
//   Head を頂点とする角度 coneAngle・長さ coneDistance のコーンの内側にある頂点を「見ている」とする。
[Serializable]
public class HomeGazeParameters
{
    // 解析に使った骨格データ ("filtered_HeadJoints" / "KinectA" など)と人物(-1 は全員)
    public string skeletonSource;
    public int personId;

    public string headDirectionMethod;
    public float downwardAngle;
    public float coneAngle;
    public float coneDistance;
    public bool useCenterWeightedHeat;
    public float heatPerHit;
}

// heatmap_<骨格>.json … 部屋メッシュの頂点ごとのヒート
[Serializable]
public class HomeGazeHeatMapList
{
    public string experimentName;
    public string subjectName;
    public string createdAt;
    public HomeGazeParameters parameters = new HomeGazeParameters();

    // 解析した時刻の数(途中で保存した場合は sampleCount より少ない)
    public int sampleCount;
    public int processedSamples;

    public List<HomeGazeHeatMapMesh> meshes = new List<HomeGazeHeatMapMesh>();
}

[Serializable]
public class HomeGazeHeatMapMesh
{
    public string name;
    // 部屋オブジェクトの親からの階層パス(同じ名前のメッシュを区別する)
    public string path;
    public int vertexCount;
    public float maxHeat;
    public float totalHeat;
    // 全員の合計。メッシュの頂点と同じ並び
    public float[] heat;
    // 人物ごとのヒート
    public List<HomeGazePersonHeat> persons = new List<HomeGazePersonHeat>();
}

[Serializable]
public class HomeGazePersonHeat
{
    // Filtered なら trackId、Kinect なら bodyId
    public int personId;
    public float maxHeat;
    public float totalHeat;
    // メッシュの頂点と同じ並び
    public float[] heat;
}

// score_<骨格>.json … 対象物体ごとのスコアとフレームごとの注視対象
[Serializable]
public class HomeGazeScoreList
{
    public string experimentName;
    public string subjectName;
    public string createdAt;
    public HomeGazeParameters parameters = new HomeGazeParameters();

    public int sampleCount;
    public int processedSamples;

    // frames[].gazeTarget に入る特殊値
    public string noHitLabel;
    public string noDataLabel;

    // 全体の集計(1人・1時刻を1フレームと数える)
    public int frameCount;
    public int gazeFrames;
    public int noTargetFrames;
    public int noDataFrames;
    public float gazeSeconds;
    public float noTargetSeconds;

    public List<HomeGazeTargetScore> targets = new List<HomeGazeTargetScore>();
    public List<HomeGazeFrameRecord> frames = new List<HomeGazeFrameRecord>();
}

[Serializable]
public class HomeGazeTargetScore
{
    public string name;
    public float totalHeat;
    // コーンがこの対象に当たったフレーム数と時間
    public int hitFrames;
    public float hitSeconds;
    // この対象が注視対象(コーン軸に最も近い対象)だったフレーム数と時間
    public int gazeFrames;
    public float gazeSeconds;
}

[Serializable]
public class HomeGazeFrameRecord
{
    public float timeSec;
    // このフレームが代表する時間(次の時刻までの間隔)
    public float durationSec;
    public int personId;

    // 対象名 / noHitLabel / noDataLabel のいずれか
    public string gazeTarget;
    // gazeTarget に対するコーン軸との最小角度(度)。未ヒットは -1
    public float gazeAngle;

    public List<string> hitTargets = new List<string>();
    // targets と同じ並び。当たっていない対象は 0 / -1
    public List<float> heats = new List<float>();
    public List<float> angles = new List<float>();
}

// ============================================================
// Raw: シーン1(Record Raw Mkv)の生データの索引 (Skeleton/<実験対象者>/Raw~/A_raw_index.json)
// ============================================================
// A.mkv の各フレームを、骨格データと同じ時計(recordingTimeSec)に対応づけるための情報。
// あとから Python などで画像を解析し、結果を部屋座標に戻すときに使う。
[Serializable]
public class HomeRawIndex
{
    public string experimentName;
    public string subjectName;
    public string kinectId;
    public int deviceIndex;
    // キネクト本体のシリアル番号。deviceIndex の順番はUSBのつなぎ方で変わりうるので、
    // シーン0で位置を合わせたキネクトと同じ個体かはこれで確認する。
    public string serialNumber;
    public string recordingStartedAt;

    public string mkvFile;
    public string calibrationFile;
    public string colorFormat;
    public string colorResolution;
    public string depthMode;
    public string cameraFps;

    // 深度カメラ座標(Azure Kinect の座標, mm, X右 Y下 Z前)→ 部屋座標(Unity, m, Y上)の 4x4 行列(行優先)。
    // Y を反転して mm→m にする変換と、シーン0で合わせたキネクトの位置姿勢(記録開始時点)を含む。
    //   room = depthToRoom * (x_mm, y_mm, z_mm, 1)
    public float[] depthToRoom = new float[16];
    // 同じ位置姿勢(参考。depthToRoom から Y反転と mm→m を除いたもの)
    public Vector3 kinectPosition;
    public Quaternion kinectRotation;

    public List<HomeRawFrame> frames = new List<HomeRawFrame>();
}

[Serializable]
public class HomeRawFrame
{
    // MKV に書いたキャプチャの順番(0から)
    public int index;
    // キネクト本体のタイムスタンプ(マイクロ秒, MKV 内の時刻と同じ)
    public long colorTimestampUsec;
    public long depthTimestampUsec;
    // Spaceキーで記録を開始した瞬間を0とした時刻(秒, 骨格データの recordingTimeSec と同じ時計)
    public float recordingTimeSec;
}
