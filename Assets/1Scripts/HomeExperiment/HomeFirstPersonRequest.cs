using UnityEngine;
using UnityEngine.SceneManagement;

// シーン5(5InteractionAnalyze)と一人称視点のシーン(6FirstPersonView)の間で設定を受け渡す。
// シーンを切り替えても残るように static に持つ。
//   シーン5の [1st Person] → Open(): 骨格・視線の設定と、選んだ人物・時刻を渡して一人称視点のシーンを開く
//   一人称視点の [Back]    → Return(): その時点の人物・時刻を持ってシーン5に戻る(シーン5は TakeReturning() で受け取る)
public static class HomeFirstPersonRequest
{
    public const string ViewerSceneName = "6FirstPersonView";

    public class Settings
    {
        public string dataRootDirectory;
        public string experimentName;
        public string subjectName;
        public HomeGazeAnalyzer.SkeletonSource skeletonSource;
        public HomeSkeletonFilter.ConfidenceMode filteredMode;
        public string skeletonVariant;
        public string kinectId;
        public HomeGazeAnalyzer.ImageDirection imageDirection;
        public float maxFrameDuration;
        public HomeGazeAnalyzer.HeadDirectionMethod headDirectionMethod;
        public float downwardAngle;
        public float coneAngle;
        public float minPersonSeconds;

        public int personId;
        public float time;

        // 戻る先のシーン(シーン5)のパス
        public string returnScenePath;
    }

    // シーン5から開いたときの設定(一人称視点のシーンを直接開いたときは null)
    public static Settings Current { get; private set; }

    private static Settings returning;

    public static void Open(Settings settings)
    {
        Current = settings;
        returning = null;
        LoadScene(ViewerSceneName);
    }

    public static void Return(int personId, float time)
    {
        if (Current == null || string.IsNullOrEmpty(Current.returnScenePath))
            return;

        returning = Current;
        returning.personId = personId;
        returning.time = time;
        Current = null;
        LoadScene(returning.returnScenePath);
    }

    // 一人称視点から戻ってきたときの人物・時刻(1回読んだら消す)
    public static Settings TakeReturning()
    {
        Settings settings = returning;
        returning = null;
        return settings;
    }

    // sceneNameOrPath: Build Settings に入っていればそのまま読み込む。
    // エディタでは Build Settings に入っていなくても、プロジェクト内のシーンを探して読み込む。
    private static void LoadScene(string sceneNameOrPath)
    {
        if (Application.CanStreamedLevelBeLoaded(sceneNameOrPath))
        {
            SceneManager.LoadScene(sceneNameOrPath);
            return;
        }

#if UNITY_EDITOR
        string path = sceneNameOrPath.EndsWith(".unity") ? sceneNameOrPath : null;

        if (path == null)
        {
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets($"{sceneNameOrPath} t:Scene"))
            {
                string candidate = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);

                if (System.IO.Path.GetFileNameWithoutExtension(candidate) == sceneNameOrPath)
                {
                    path = candidate;
                    break;
                }
            }
        }

        if (path != null)
        {
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
            return;
        }
#endif

        Debug.LogError($"[HomeFirstPersonRequest] シーンが見つかりません: {sceneNameOrPath}(ビルドするときは Build Settings に追加してください)");
    }
}
