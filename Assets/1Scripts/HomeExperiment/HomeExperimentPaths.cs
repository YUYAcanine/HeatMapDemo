using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;

// 家庭での頭部方向計測実験(4HomeExperiment)で使うディレクトリ構成をまとめたクラス。
//
// Assets/Data/HomeExperiment/
//   └ <実験の名前>/
//       ├ Env/
//       │   ├ KinectA_Transform.json  … キネクトの位置姿勢(1台1ファイル)
//       │   ├ KinectA_PointCloud.ply  … シーン0で撮った点群(1台1ファイル, キネクトのローカル座標, メートル)
//       │   ├ RoomObjects.json        … 部屋オブジェクトの一覧とTransform
//       │   ├ TargetObjects.json      … 注視などの対象物体(シーン0で部屋オブジェクトの下に置いた物体)の一覧とTransform
//       │   └ Meshes/                 … アセットを参照できない部屋オブジェクト/対象物体のメッシュ
//       └ Skeleton/
//           └ <実験対象者>/
//               ├ A_skeleton.json     … キネクト1台ごとの骨格データ
//               ├ Raw~/                  … シーン1 で Record Raw Mkv にチェックを入れて記録した生データ(Unityは「~」で終わるフォルダを読み込まない)
//               │   ├ A.mkv              … キネクト1台ごとのカラー(MJPG)・深度・赤外線(Azure Kinect 公式の録画形式)
//               │   ├ A_raw_index.json   … フレームごとの時刻(全キネクト共通の時計)・キネクトの位置姿勢・シリアル番号
//               │   └ A_calibration.json … キネクトの校正情報(カメラの内部パラメータ・深度→カラーの外部パラメータ)
//               ├ Filtered/
//               │   ├ filtered_AllJoints.json  … 全キネクトを統合した骨格(シーン3, 全関節の信頼度で選択。人物IDはまとめる前)
//               │   ├ filtered_HeadJoints.json … 全キネクトを統合した骨格(シーン3, 頭部の関節の信頼度で選択。人物IDはまとめる前)
//               │   ├ filtered_Image.json      … 画像から推定した骨格(Tools/GazePipeline。人物IDはまとめる前)
//               │   └ grouped_<Mode>.json      … 人物ごとの骨格(シーン3でグループごとに1つに絞ったもの。シーン4/5はこれを読む)
//               └ Analysis/
//                   ├ Groups/
//                   │   └ person_groups_<人物IDの元の骨格>.json … 同じ人の人物IDをまとめたグループ(シーン3で作る・使う)
//                   ├ HeatMap/
//                   │   └ heatmap_<骨格>.json … 視線コーンのヒートマップ(シーン4, 部屋メッシュの頂点ごとのヒート)
//                   ├ Score/
//                   │   └ score_<骨格>.json   … 対象物体ごとのスコアとフレームごとの注視対象(シーン4)
//                   ├ Interaction/
//                   │   ├ interaction_<骨格>.json          … 人物どうし・対象物体の相互作用の解析(シーン5)
//                   │   ├ interaction_<骨格>_summary.csv   … 同じ集計を表にしたもの(Excel などで開く)
//                   │   └ interaction_<骨格>_episodes.csv  … 1回1回の出来事(注視・相互注視など)の開始・終了時刻
//                   └ Figures/
//                       └ gaze_timeline.html … 2人の注視の時系列グラフ(Tools/GazeTimeline/make_gaze_timeline.py で Interaction/ から作る)
//                   (<骨格> は filtered_HeadJoints / KinectA など、解析に使った骨格データ)
//                   以前は Analysis/ の直下に置いていた。シーン4/5を開くと MigrateLegacyAnalysisFiles で上のフォルダへ移す。
public static class HomeExperimentPaths
{
    public const string EnvFolderName = "Env";
    public const string SkeletonFolderName = "Skeleton";
    public const string MeshFolderName = "Meshes";
    public const string RoomObjectsFileName = "RoomObjects.json";
    public const string TargetObjectsFileName = "TargetObjects.json";
    public const string SkeletonFileSuffix = "_skeleton.json";
    public const string FilteredFolderName = "Filtered";
    public const string AnalysisFolderName = "Analysis";
    public const string GroupsFolderName = "Groups";
    public const string HeatMapFolderName = "HeatMap";
    public const string ScoreFolderName = "Score";
    public const string InteractionFolderName = "Interaction";

    public const string PersonGroupsFilePrefix = "person_groups_";
    public const string HeatMapFilePrefix = "heatmap_";
    public const string ScoreFilePrefix = "score_";
    public const string InteractionFilePrefix = "interaction_";
    public const string RawFolderName = "Raw~";

    public static string RootDirectory =>
        Path.Combine(Application.dataPath, "Data", "HomeExperiment");

    public static string GetExperimentDirectory(string experimentName) =>
        Path.Combine(RootDirectory, experimentName.Trim());

    public static string GetEnvDirectory(string experimentName) =>
        Path.Combine(GetExperimentDirectory(experimentName), EnvFolderName);

    public static string GetMeshDirectory(string experimentName) =>
        Path.Combine(GetEnvDirectory(experimentName), MeshFolderName);

    public static string GetRoomObjectsPath(string experimentName) =>
        Path.Combine(GetEnvDirectory(experimentName), RoomObjectsFileName);

    public static string GetTargetObjectsPath(string experimentName) =>
        Path.Combine(GetEnvDirectory(experimentName), TargetObjectsFileName);

    public static string GetKinectTransformPath(string experimentName, string kinectId) =>
        Path.Combine(GetEnvDirectory(experimentName), $"Kinect{kinectId}_Transform.json");

    public static string GetKinectPointCloudPath(string experimentName, string kinectId) =>
        Path.Combine(GetEnvDirectory(experimentName), $"Kinect{kinectId}_PointCloud.ply");

    public static string GetSkeletonRootDirectory(string experimentName) =>
        Path.Combine(GetExperimentDirectory(experimentName), SkeletonFolderName);

    public static string GetSubjectDirectory(string experimentName, string subjectName) =>
        Path.Combine(GetSkeletonRootDirectory(experimentName), subjectName.Trim());

    public static string GetSkeletonPath(string experimentName, string subjectName, string kinectId) =>
        Path.Combine(GetSubjectDirectory(experimentName, subjectName), kinectId + SkeletonFileSuffix);

    public static string GetFilteredSkeletonPath(string experimentName, string subjectName, string modeName) =>
        Path.Combine(GetSubjectDirectory(experimentName, subjectName), FilteredFolderName, $"filtered_{modeName}.json");

    // シーン3でグループごとに1つに絞った人物ごとの骨格(シーン4/5が読む)
    public static string GetGroupedSkeletonPath(string experimentName, string subjectName, string modeName) =>
        Path.Combine(GetSubjectDirectory(experimentName, subjectName), FilteredFolderName, $"grouped_{modeName}.json");

    public static string GetRawDirectory(string experimentName, string subjectName) =>
        Path.Combine(GetSubjectDirectory(experimentName, subjectName), RawFolderName);

    public static string GetAnalysisDirectory(string experimentName, string subjectName) =>
        Path.Combine(GetSubjectDirectory(experimentName, subjectName), AnalysisFolderName);

    public static string GetGroupsDirectory(string experimentName, string subjectName) =>
        Path.Combine(GetAnalysisDirectory(experimentName, subjectName), GroupsFolderName);

    public static string GetHeatMapDirectory(string experimentName, string subjectName) =>
        Path.Combine(GetAnalysisDirectory(experimentName, subjectName), HeatMapFolderName);

    public static string GetScoreDirectory(string experimentName, string subjectName) =>
        Path.Combine(GetAnalysisDirectory(experimentName, subjectName), ScoreFolderName);

    public static string GetInteractionDirectory(string experimentName, string subjectName) =>
        Path.Combine(GetAnalysisDirectory(experimentName, subjectName), InteractionFolderName);

    // シーン3で同じ人の人物IDをまとめたグループ(人物IDは骨格データごとに違うので、骨格データごとに持つ)
    public static string GetPersonGroupsPath(string experimentName, string subjectName, string personIdSource) =>
        Path.Combine(GetGroupsDirectory(experimentName, subjectName), $"{PersonGroupsFilePrefix}{personIdSource}.json");

    public static string GetGazeHeatMapPath(string experimentName, string subjectName, string sourceName) =>
        Path.Combine(GetHeatMapDirectory(experimentName, subjectName), $"{HeatMapFilePrefix}{sourceName}.json");

    public static string GetGazeScorePath(string experimentName, string subjectName, string sourceName) =>
        Path.Combine(GetScoreDirectory(experimentName, subjectName), $"{ScoreFilePrefix}{sourceName}.json");

    // シーン5の相互作用の解析(JSON と、同じ内容の CSV 2つ)
    public static string GetInteractionPath(string experimentName, string subjectName, string sourceName) =>
        Path.Combine(GetInteractionDirectory(experimentName, subjectName), $"{InteractionFilePrefix}{sourceName}.json");

    public static string GetInteractionSummaryCsvPath(string experimentName, string subjectName, string sourceName) =>
        Path.Combine(GetInteractionDirectory(experimentName, subjectName), $"{InteractionFilePrefix}{sourceName}_summary.csv");

    public static string GetInteractionEpisodesCsvPath(string experimentName, string subjectName, string sourceName) =>
        Path.Combine(GetInteractionDirectory(experimentName, subjectName), $"{InteractionFilePrefix}{sourceName}_episodes.csv");

    // 以前 Analysis/ の直下に置いていたファイルを、種類ごとのフォルダ(Groups / HeatMap / Score / Interaction)へ移す。
    // Unity の .meta も一緒に移す。移す先に同じ名前のファイルがあれば、更新日時の新しい方だけを残す(解析結果は古い版を残さない)。
    public static void MigrateLegacyAnalysisFiles(string experimentName, string subjectName)
    {
        if (!IsValidFolderName(experimentName, out _) || !IsValidFolderName(subjectName, out _))
            return;

        string analysisDir = GetAnalysisDirectory(experimentName, subjectName);

        if (!Directory.Exists(analysisDir))
            return;

        bool moved = false;

        foreach (string file in Directory.GetFiles(analysisDir))
        {
            string name = Path.GetFileName(file);

            if (name.EndsWith(".meta"))
                continue;

            string folder =
                name.StartsWith(PersonGroupsFilePrefix) ? GroupsFolderName :
                name.StartsWith(HeatMapFilePrefix) ? HeatMapFolderName :
                name.StartsWith(ScoreFilePrefix) ? ScoreFolderName :
                name.StartsWith(InteractionFilePrefix) ? InteractionFolderName :
                null;

            if (folder == null)
                continue;

            string destination = Path.Combine(analysisDir, folder, name);

            try
            {
                // 解析結果は古い版を残さない。移す先に同じ名前があれば、新しい方だけを残す
                if (File.Exists(destination))
                {
                    if (File.GetLastWriteTime(file) <= File.GetLastWriteTime(destination))
                    {
                        File.Delete(file);
                        DeleteIfExists(file + ".meta");
                        moved = true;
                        Debug.Log($"[HomeExperimentPaths] {folder}/ の方が新しいので、古い Analysis/{name} を削除しました。");
                        continue;
                    }

                    File.Delete(destination);
                    DeleteIfExists(destination + ".meta");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Move(file, destination);

                if (File.Exists(file + ".meta"))
                    File.Move(file + ".meta", destination + ".meta");

                moved = true;
                Debug.Log($"[HomeExperimentPaths] Analysis/{name} を Analysis/{folder}/ へ移しました。");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HomeExperimentPaths] 移せませんでした: {file}\n{e.Message}");
            }
        }

        if (moved)
            RefreshAssetDatabase();
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    // フォルダ名として使える名前か(空でなく、パス区切りやWindowsの禁止文字を含まない)。
    public static bool IsValidFolderName(string name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "名前が空です。";
            return false;
        }

        if (name.Trim().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = $"フォルダ名に使えない文字が含まれています: {name}";
            return false;
        }

        error = null;
        return true;
    }

    public static string[] GetChildDirectoryNames(string directory)
    {
        if (!Directory.Exists(directory))
            return new string[0];

        string[] dirs = Directory.GetDirectories(directory);

        for (int i = 0; i < dirs.Length; i++)
            dirs[i] = Path.GetFileName(dirs[i]);

        System.Array.Sort(dirs);
        return dirs;
    }

#if UNITY_EDITOR
    // Assets配下に書き出したファイルをProjectウィンドウに反映する。
    public static void RefreshAssetDatabase()
    {
        UnityEditor.AssetDatabase.Refresh();
    }
#else
    public static void RefreshAssetDatabase() { }
#endif

    // UIボタンをクリックすると選択状態が残り、Spaceキーが「Submit」としてそのボタンを
    // もう一度押してしまう(例: ICPボタンが再実行される)。Spaceキーを合図に使うシーンでは
    // 毎フレームこれを呼んで選択を外しておく。
    public static void ClearUISelection()
    {
        if (EventSystem.current != null &&
            EventSystem.current.currentSelectedGameObject != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }
    }
}

// 実験の名前を持つコンポーネント(HomeEnvSetup / HomeEnvLoader)の共通インターフェース。
// 同じGameObject上の他コンポーネントが実験の名前を参照するのに使う。
public interface IHomeExperimentNameProvider
{
    string ExperimentName { get; }
}

// インスペクターで実験名/実験対象者名を既存フォルダから選べるようにする属性。
// 描画は Editor/HomeExperimentFolderDrawer.cs。
public class HomeExperimentFolderAttribute : PropertyAttribute
{
    public enum Kind
    {
        Experiment,
        Subject
    }

    public readonly Kind kind;

    public HomeExperimentFolderAttribute(Kind kind)
    {
        this.kind = kind;
    }
}
