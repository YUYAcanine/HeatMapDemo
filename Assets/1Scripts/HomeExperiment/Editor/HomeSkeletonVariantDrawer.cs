using UnityEditor;
using UnityEngine;

// [HomeSkeletonVariant] を付けた文字列フィールドを「テキスト入力 + 既存ファイルから選ぶボタン」で表示する。
//   選べるのは Skeleton/<実験対象者>/Filtered/grouped_<Mode>_<名前>.json の <名前>
//   (Tools/GazeSmooth/smooth_skeleton.py で作った、頭の向きを平滑化した骨格)と、平滑化前(空)。
//   実験の名前は同じGameObjectの HomeEnvLoader から、実験対象者と Mode は同じコンポーネントの
//   subjectName / skeletonSource / filteredMode / Data Root Directory から取得する。
[CustomPropertyDrawer(typeof(HomeSkeletonVariantAttribute))]
public class HomeSkeletonVariantDrawer : PropertyDrawer
{
    private const float ButtonWidth = 60f;
    private const string NoneLabel = "(なし: 平滑化前)";

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.String)
        {
            EditorGUI.LabelField(position, label.text, "[HomeSkeletonVariant] は string にのみ使えます");
            return;
        }

        Rect fieldRect = new Rect(position.x, position.y, position.width - ButtonWidth - 2f, position.height);
        Rect buttonRect = new Rect(position.xMax - ButtonWidth, position.y, ButtonWidth, position.height);

        EditorGUI.PropertyField(fieldRect, property, label);

        bool found = TryGetSource(property, out string rootDirectory, out string experimentName, out string subjectName, out string modeName);

        using (new EditorGUI.DisabledScope(!found))
        {
            if (GUI.Button(buttonRect, "選択"))
                ShowMenu(property, rootDirectory, experimentName, subjectName, modeName);
        }
    }

    // 探す場所(データのルート・実験の名前・実験対象者・Mode)。キネクト1台分の骨格(Kinect)には平滑化した骨格がないので false
    private static bool TryGetSource(SerializedProperty property, out string rootDirectory, out string experimentName, out string subjectName, out string modeName)
    {
        rootDirectory = experimentName = subjectName = modeName = null;

        Component component = property.serializedObject.targetObject as Component;
        IHomeExperimentNameProvider provider =
            component != null ? component.GetComponent<IHomeExperimentNameProvider>() : null;

        SerializedProperty subject = property.serializedObject.FindProperty("subjectName");
        SerializedProperty source = property.serializedObject.FindProperty("skeletonSource");
        SerializedProperty mode = property.serializedObject.FindProperty("filteredMode");

        if (provider == null || subject == null || source == null || mode == null)
            return false;

        rootDirectory = HomeExperimentPaths.GetDataRootDirectory(component);

        if (!HomeExperimentPaths.IsValidRootDirectory(rootDirectory, out _) ||
            !HomeExperimentPaths.IsValidFolderName(provider.ExperimentName, out _) ||
            !HomeExperimentPaths.IsValidFolderName(subject.stringValue, out _))
            return false;

        HomeGazeAnalyzer.SkeletonSource skeletonSource = (HomeGazeAnalyzer.SkeletonSource)source.intValue;

        if (skeletonSource == HomeGazeAnalyzer.SkeletonSource.Kinect)
            return false;

        experimentName = provider.ExperimentName;
        subjectName = subject.stringValue;
        modeName = skeletonSource == HomeGazeAnalyzer.SkeletonSource.Image
            ? "Image"
            : ((HomeSkeletonFilter.ConfidenceMode)mode.intValue).ToString();
        return true;
    }

    private static void ShowMenu(SerializedProperty property, string rootDirectory, string experimentName, string subjectName, string modeName)
    {
        string[] names = HomeExperimentPaths.GetGroupedSkeletonVariants(rootDirectory, experimentName, subjectName, modeName);
        GenericMenu menu = new GenericMenu();

        AddItem(menu, property, NoneLabel, "");

        if (names.Length == 0)
            menu.AddDisabledItem(new GUIContent($"(grouped_{modeName}_<名前>.json がありません。Tools/GazeSmooth で作る)"));
        else
            menu.AddSeparator("");

        foreach (string name in names)
            AddItem(menu, property, name, name);

        menu.ShowAsContext();
    }

    private static void AddItem(GenericMenu menu, SerializedProperty property, string label, string value)
    {
        menu.AddItem(new GUIContent(label), property.stringValue == value, () =>
        {
            property.serializedObject.Update();
            property.stringValue = value;
            property.serializedObject.ApplyModifiedProperties();
        });
    }
}
