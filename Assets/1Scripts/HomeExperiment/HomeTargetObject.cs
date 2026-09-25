using UnityEngine;

// Env/TargetObjects.json から再生成した対象物体に付ける目印。
// group はシーン0で部屋オブジェクトの下に置いた物体の名前(その子のメッシュも同じ group)。
// シーン4(HomeGazeAnalyzer)は group ごとにスコアを数える。
public class HomeTargetObject : MonoBehaviour
{
    public string group;
}
