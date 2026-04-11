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
    public Button backButton; // �� Unity�̃{�^��UI

    void Start()
    {
        // ���_�J���[��ǂݍ���Ŕ��f
        if (meshObject == null || heatDataAsset == null)
        {
            Debug.LogError("MeshColorDisplay: �K�v�Ȑݒ肪�s�����Ă��܂�");
            return;
        }

        MeshFilter mf = meshObject.GetComponent<MeshFilter>();
        if (mf == null)
        {
            Debug.LogError("MeshColorDisplay: meshObject �� MeshFilter ������܂���");
            return;
        }

        Mesh mesh = mf.mesh;

        if (mesh.vertexCount != heatDataAsset.vertexColors.Length)
        {
            Debug.LogWarning($"���_������v���܂���: Mesh={mesh.vertexCount}, ColorData={heatDataAsset.vertexColors.Length}");
            return;
        }

        mesh.colors = heatDataAsset.vertexColors;

        // �߂�{�^�����w�肳��Ă���΁A�N���b�N�C�x���g��ǉ�
        if (backButton != null)
        {
            backButton.onClick.AddListener(OnBackToSceneA);
        }
    }

    public void OnBackToSceneA()
    {
        SceneManager.LoadScene("0RealTimeGaze");
    }
}
