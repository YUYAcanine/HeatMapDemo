using UnityEditor;
using UnityEngine;

// [HomeDataRootFolder] を付けた文字列フィールドを「テキスト入力 + フォルダを選ぶボタン + 既定に戻すボタン」で表示する。
// 空のときは既定の保存先(Assets/Data/HomeExperiment)を薄く表示する。
[CustomPropertyDrawer(typeof(HomeDataRootFolderAttribute))]
public class HomeDataRootFolderDrawer : PropertyDrawer
{
    private const float ButtonWidth = 40f;

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.String)
        {
            EditorGUI.LabelField(position, label.text, "[HomeDataRootFolder] は string にのみ使えます");
            return;
        }

        Rect fieldRect = new Rect(position.x, position.y, position.width - ButtonWidth * 2f - 4f, position.height);
        Rect browseRect = new Rect(fieldRect.xMax + 2f, position.y, ButtonWidth, position.height);
        Rect resetRect = new Rect(browseRect.xMax + 2f, position.y, ButtonWidth, position.height);

        EditorGUI.PropertyField(fieldRect, property, label);

        if (string.IsNullOrWhiteSpace(property.stringValue) && Event.current.type == EventType.Repaint)
        {
            Rect hintRect = new Rect(fieldRect.x + EditorGUIUtility.labelWidth + 4f, fieldRect.y,
                fieldRect.width - EditorGUIUtility.labelWidth - 4f, fieldRect.height);
            GUI.Label(hintRect, "(既定) Assets/Data/HomeExperiment", EditorStyles.centeredGreyMiniLabel);
        }

        if (GUI.Button(browseRect, "選択"))
        {
            string current = HomeExperimentPaths.ResolveRootDirectory(property.stringValue);
            string selected = EditorUtility.OpenFolderPanel("データのルートフォルダ", current, "");

            if (!string.IsNullOrEmpty(selected))
            {
                property.stringValue = selected.Replace('/', '\\');
                property.serializedObject.ApplyModifiedProperties();
            }

            GUIUtility.ExitGUI();
        }

        if (GUI.Button(resetRect, "既定"))
        {
            property.stringValue = "";
            property.serializedObject.ApplyModifiedProperties();
        }
    }
}
