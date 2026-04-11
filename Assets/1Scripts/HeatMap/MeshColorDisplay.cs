using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class MeshColorDisplay : MonoBehaviour
{
    [Header("Target Mesh Object")]
    public GameObject meshObject;

    [Header("Heat Data Asset")]
    public HeatMeshData heatDataAsset;

    [Header("Back Button (Optional)")]
    public Button backButton; // ← UnityのボタンUI

    void Start()
    {
        // 頂点カラーを読み込んで反映
        if (meshObject == null || heatDataAsset == null)
        {
            Debug.LogError("MeshColorDisplay: 必要な設定が不足しています");
            return;
        }

        MeshFilter mf = meshObject.GetComponent<MeshFilter>();
        if (mf == null)
        {
            Debug.LogError("MeshColorDisplay: meshObject に MeshFilter がありません");
            return;
        }

        Mesh mesh = mf.mesh;

        if (mesh.vertexCount != heatDataAsset.vertexColors.Length)
        {
            Debug.LogWarning($"頂点数が一致しません: Mesh={mesh.vertexCount}, ColorData={heatDataAsset.vertexColors.Length}");
            return;
        }

        mesh.colors = heatDataAsset.vertexColors;

        // 戻るボタンが指定されていれば、クリックイベントを追加
        if (backButton != null)
        {
            backButton.onClick.AddListener(OnBackToSceneA);
        }
    }

    public void OnBackToSceneA()
    {
        SceneManager.LoadScene("5HeadDirection");
    }
}
