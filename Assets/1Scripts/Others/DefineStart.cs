using UnityEngine;

// MultiIntegration シーンでは SkeletonRealtimeSubscriber が既にMQTTを購読して人物マーカーを
// 表示している（headPelvisDistanceが最も短い人物のPelvisマーカーをハイライト表示する機能も
// 持つ）。DefineStart はMQTTを直接購読せず、その SkeletonRealtimeSubscriber がハイライトして
// いる人物のPelvis位置を参照し、このGameObjectのtransform.positionを
// 「経路計算のスタート位置」として更新し続ける。RouteSub の Start(Transform) に
// このGameObjectを割り当てて使う。
//
// スタート位置のX/Z座標はハイライトされたPelvisの座標をそのまま使い、Y座標だけは
// Pelvis直下にある一番近い平面(床のCollider)の高さに置き換える。
public class DefineStart : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("空の場合はシーン内の SkeletonRealtimeSubscriber を自動検索する。")]
    [SerializeField] private SkeletonRealtimeSubscriber skeletonSubscriber;

    [Header("Floor Snapping")]
    [Tooltip("PelvisのX/Z座標はそのまま使い、Y座標はPelvis直下にある一番近い平面(床のCollider)の高さに置き換える。オフの場合はPelvis位置のY座標(姿勢により上下する)をそのまま使う。")]
    [SerializeField] private bool snapToNearestFloor = true;
    [Tooltip("床として判定するCollider(PlaneFinderが生成した歩行可能サーフェス等)のレイヤー。")]
    [SerializeField] private LayerMask floorLayerMask = ~0;
    [Tooltip("Raycastの開始点をPelvisからこの高さ(m)だけ上げる。Pelvisが床より僅かに低く計測された場合でも真下の床を検出できるようにするため。")]
    [SerializeField] private float raycastStartHeight = 1.5f;
    [Tooltip("Raycastの開始点から真下に向かって床を探す最大距離(m)。")]
    [SerializeField] private float floorSearchDistance = 5f;

    public bool HasActivePerson { get; private set; }
    public string ActiveLabel { get; private set; }
    public int StartPositionVersion { get; private set; }

    private void Update()
    {
        UpdateStartPosition();
    }

    private void UpdateStartPosition()
    {
        SkeletonRealtimeSubscriber subscriber = GetSkeletonSubscriber();

        if (subscriber == null || !subscriber.HasShortestPerson)
        {
            HasActivePerson = false;
            ActiveLabel = null;
            return;
        }

        Vector3 startPosition = subscriber.ShortestPersonTransform.position;

        if (snapToNearestFloor && TryFindNearestFloorHeight(startPosition, out float floorY))
            startPosition.y = floorY;

        transform.position = startPosition;
        HasActivePerson = true;
        ActiveLabel = subscriber.ShortestLabel;
        StartPositionVersion++;
    }

    private SkeletonRealtimeSubscriber GetSkeletonSubscriber()
    {
        if (skeletonSubscriber != null)
            return skeletonSubscriber;

        skeletonSubscriber = FindObjectOfType<SkeletonRealtimeSubscriber>();
        return skeletonSubscriber;
    }

    private bool TryFindNearestFloorHeight(Vector3 pelvisPosition, out float floorY)
    {
        floorY = pelvisPosition.y;

        Vector3 origin = pelvisPosition + Vector3.up * raycastStartHeight;

        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            Vector3.down,
            raycastStartHeight + floorSearchDistance,
            floorLayerMask,
            QueryTriggerInteraction.Ignore);

        if (hits.Length == 0)
            return false;

        float nearestDistance = float.MaxValue;
        bool found = false;

        foreach (RaycastHit hit in hits)
        {
            if (hit.distance >= nearestDistance)
                continue;

            nearestDistance = hit.distance;
            floorY = hit.point.y;
            found = true;
        }

        return found;
    }
}
