using UnityEditor;
using UnityEngine;

// [HomeExperimentFolder] を付けた文字列フィールドを「テキスト入力 + 既存フォルダから選ぶボタン」で表示する。
//   Experiment: Assets/Data/HomeExperiment/ 直下のフォルダ
//   Subject   : Assets/Data/HomeExperiment/<実験の名前>/Skeleton/ 直下のフォルダ
//               (実験の名前は同じGameObjectの HomeEnvLoader / HomeEnvSetup から取得)
[CustomPropertyDrawer(typeof(HomeExperimentFolderAttribute))]
public class HomeExperimentFolderDrawer : PropertyDrawer
{
    private const float ButtonWidth = 60f;

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.String)
        {
            EditorGUI.LabelField(position, label.text, "[HomeExperimentFolder] は string にのみ使えます");
            return;
        }

        HomeExperimentFolderAttribute folder = (HomeExperimentFolderAttribute)attribute;

        Rect fieldRect = new Rect(position.x, position.y, position.width - ButtonWidth - 2f, position.height);
        Rect buttonRect = new Rect(position.xMax - ButtonWidth, position.y, ButtonWidth, position.height);

        EditorGUI.PropertyField(fieldRect, property, label);

        string parentDirectory = GetParentDirectory(property, folder.kind);

        using (new EditorGUI.DisabledScope(parentDirectory == null))
        {
            if (GUI.Button(buttonRect, "選択"))
                ShowMenu(property, parentDirectory);
        }
    }

    private static string GetParentDirectory(SerializedProperty property, HomeExperimentFolderAttribute.Kind kind)
    {
        if (kind == HomeExperimentFolderAttribute.Kind.Experiment)
            return HomeExperimentPaths.RootDirectory;

        Component component = property.serializedObject.targetObject as Component;
        IHomeExperimentNameProvider provider =
            component != null ? component.GetComponent<IHomeExperimentNameProvider>() : null;

        if (provider == null || !HomeExperimentPaths.IsValidFolderName(provider.ExperimentName, out _))
            return null;

        return HomeExperimentPaths.GetSkeletonRootDirectory(provider.ExperimentName);
    }

    private static void ShowMenu(SerializedProperty property, string parentDirectory)
    {
        string[] names = HomeExperimentPaths.GetChildDirectoryNames(parentDirectory);
        GenericMenu menu = new GenericMenu();

        if (names.Length == 0)
            menu.AddDisabledItem(new GUIContent("(フォルダがありません)"));

        foreach (string name in names)
        {
            string selected = name;

            menu.AddItem(new GUIContent(name), property.stringValue == name, () =>
            {
                property.serializedObject.Update();
                property.stringValue = selected;
                property.serializedObject.ApplyModifiedProperties();
            });
        }

        menu.ShowAsContext();
    }
}
