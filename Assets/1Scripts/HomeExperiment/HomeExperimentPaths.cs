using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;

// 家庭での頭部方向計測実験(4HomeExperiment)で使うディレクトリ構成をまとめたクラス。
//
// Assets/Data/HomeExperiment/
//   └ <実験の名前>/
//       ├ Env/
//       │   ├ KinectA_Transform.json  … キネクトの位置姿勢(1台1ファイル)
//       │   ├ RoomObjects.json        … 部屋オブジェクトの一覧とTransform
//       │   └ Meshes/                 … アセットを参照できない部屋オブジェクトのメッシュ
//       └ Skeleton/
//           └ <実験対象者>/
//               ├ A_skeleton.json     … キネクト1台ごとの骨格データ
//               └ Filtered/
//                   ├ filtered_AllJoints.json  … 信頼度フィルタリング後の骨格(シーン3, 全関節の信頼度で選択)
//                   └ filtered_HeadJoints.json … 信頼度フィルタリング後の骨格(シーン3, 頭部の関節の信頼度で選択)
public static class HomeExperimentPaths
{
    public const string EnvFolderName = "Env";
    public const string SkeletonFolderName = "Skeleton";
    public const string MeshFolderName = "Meshes";
    public const string RoomObjectsFileName = "RoomObjects.json";
    public const string SkeletonFileSuffix = "_skeleton.json";
    public const string FilteredFolderName = "Filtered";

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

    public static string GetKinectTransformPath(string experimentName, string kinectId) =>
        Path.Combine(GetEnvDirectory(experimentName), $"Kinect{kinectId}_Transform.json");

    public static string GetSkeletonRootDirectory(string experimentName) =>
        Path.Combine(GetExperimentDirectory(experimentName), SkeletonFolderName);

    public static string GetSubjectDirectory(string experimentName, string subjectName) =>
        Path.Combine(GetSkeletonRootDirectory(experimentName), subjectName.Trim());

    public static string GetSkeletonPath(string experimentName, string subjectName, string kinectId) =>
        Path.Combine(GetSubjectDirectory(experimentName, subjectName), kinectId + SkeletonFileSuffix);

    public static string GetFilteredSkeletonPath(string experimentName, string subjectName, string modeName) =>
        Path.Combine(GetSubjectDirectory(experimentName, subjectName), FilteredFolderName, $"filtered_{modeName}.json");

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
