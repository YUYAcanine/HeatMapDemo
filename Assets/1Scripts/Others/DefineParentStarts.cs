using System.Collections.Generic;
using UnityEngine;

// DefineStart の複数人版。SkeletonRealtimeSubscriber がハイライトしている
// child(headPelvisDistance最短の人物)以外の全員(=parent)について、
// 経路計算のスタート位置を持つTransformを人物ラベルごとに維持する。
// RouteSub の parentStarts にこのコンポーネントを割り当てて使う。
public class DefineParentStarts : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("空の場合はシーン内の SkeletonRealtimeSubscriber を自動検索する。")]
    [SerializeField] private SkeletonRealtimeSubscriber skeletonSubscriber;

    [Header("Floor Snapping")]
    [Tooltip("PelvisのX/Z座標はそのまま使い、Y座標はPelvis直下にある一番近い平面(床のCollider)の高さに置き換える。")]
    [SerializeField] private bool snapToNearestFloor = true;
    [SerializeField] private LayerMask floorLayerMask = ~0;
    [SerializeField] private float raycastStartHeight = 1.5f;
    [SerializeField] private float floorSearchDistance = 5f;

    private readonly Dictionary<string, Transform> startsByLabel = new Dictionary<string, Transform>();

    public IReadOnlyDictionary<string, Transform> StartsByLabel => startsByLabel;
    public int StartPositionVersion { get; private set; }

    private void Update()
    {
        UpdateStartPositions();
    }

    private void UpdateStartPositions()
    {
        SkeletonRealtimeSubscriber subscriber = GetSkeletonSubscriber();

        if (subscriber == null)
        {
            ClearAll();
            return;
        }

        HashSet<string> activeLabels = new HashSet<string>();

        foreach (KeyValuePair<string, Transform> entry in subscriber.GetOtherPersons())
        {
            if (entry.Value == null)
                continue;

            string label = entry.Key;
            activeLabels.Add(label);

            Vector3 startPosition = entry.Value.position;

            if (snapToNearestFloor && TryFindNearestFloorHeight(startPosition, out float floorY))
                startPosition.y = floorY;

            if (!startsByLabel.TryGetValue(label, out Transform marker) || marker == null)
            {
                GameObject go = new GameObject($"ParentStart_{label}");
                go.transform.SetParent(transform, false);
                marker = go.transform;
                startsByLabel[label] = marker;
            }

            marker.position = startPosition;
        }

        RemoveInactiveLabels(activeLabels);
        StartPositionVersion++;
    }

    private void RemoveInactiveLabels(HashSet<string> activeLabels)
    {
        List<string> staleLabels = null;

        foreach (KeyValuePair<string, Transform> entry in startsByLabel)
        {
            if (activeLabels.Contains(entry.Key))
                continue;

            staleLabels ??= new List<string>();
            staleLabels.Add(entry.Key);
        }

        if (staleLabels == null)
            return;

        foreach (string label in staleLabels)
        {
            if (startsByLabel[label] != null)
                Destroy(startsByLabel[label].gameObject);

            startsByLabel.Remove(label);
        }
    }

    private void ClearAll()
    {
        foreach (Transform marker in startsByLabel.Values)
        {
            if (marker != null)
                Destroy(marker.gameObject);
        }

        startsByLabel.Clear();
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
            // 床は必ずPelvisより下にあるはずなので、Pelvisと同じかそれより上のヒット
            // (テーブル等、真上から見て手前にある別のColliderを誤検出したもの)は除外する。
            if (hit.point.y >= pelvisPosition.y)
                continue;

            if (hit.distance >= nearestDistance)
                continue;

            nearestDistance = hit.distance;
            floorY = hit.point.y;
            found = true;
        }

        return found;
    }

    private void OnDestroy()
    {
        ClearAll();
    }
}
