using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.AI;

public class RouteSearch : MonoBehaviour
{
    [Header("Attention")]
    [SerializeField] private AttentionRiskProvider attentionRiskProvider;
    [SerializeField] private int interestRank = 1;

    [Header("Route")]
    [SerializeField] private Material routeLineMaterial;
    [SerializeField] private float startSearchRadius = 2.0f;
    [SerializeField] private float goalSearchRadius = 0.3f;
    [SerializeField] private float routeLineWidth = 0.05f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [SerializeField] private float routeHeightSampleInterval = 0.2f;
    [SerializeField] private float routeHeightSampleRadius = 1.0f;
    [SerializeField] private bool logRouteSearch = true;

    [Header("Arrival Time")]
    [SerializeField] private bool showArrivalTimeText = true;
    [SerializeField] private float movementSpeedMetersPerSecond = 0.8f;
    [SerializeField] private float arrivalTimeTextYOffset = 0.2f;
    [SerializeField] private float arrivalTimeTextSize = 0.8f;

    [Header("Nearby Objects")]
    [SerializeField] private bool showNearbyObjectText = true;
    [SerializeField] private TMP_Text nearbyObjectSummaryText;
    [SerializeField] private List<GameObject> nearbyObjects = new();
    [SerializeField] private LayerMask nearbyObjectLayerMask = ~0;
    [SerializeField] private string nearbyObjectTag = "";
    [SerializeField] private float nearbyObjectSurfaceDistance = 0.5f;
    [SerializeField] private float nearbyObjectTextYOffset = 0.8f;
    [SerializeField] private float nearbyObjectTextSize = 0.45f;
    [SerializeField] private Color nearbyObjectTextColor = Color.white;
    [SerializeField] private bool showNearbyObjectTextBackground = true;
    [SerializeField] private Color nearbyObjectTextBackgroundColor = new(0f, 0f, 0f, 0.65f);
    [SerializeField] private Vector2 nearbyObjectTextBackgroundPadding = new(0.25f, 0.12f);
    [SerializeField] private float nearbyObjectPanelSpacing = 0.08f;
    [SerializeField] private int maxNearbyObjectsToShow = 8;
    [SerializeField] private bool includeRouteTargetAsNearbyObject = false;

    [Header("Accident Data")]
    [SerializeField] private string accidentCsvAssetPath = "Data/Accidents/accidents.csv";

    private NavMeshPath attentionPath;
    private bool hasAttentionPath = false;
    private LineRenderer routeLine;
    private TextMeshPro arrivalTimeText;
    private readonly List<TextMeshPro> nearbyWorldTexts = new();
    private readonly List<SpriteRenderer> nearbyWorldBackgrounds = new();
    private readonly Dictionary<string, string> accidentTextByFurnitureName =
        new(System.StringComparer.OrdinalIgnoreCase);
    private Sprite nearbyObjectBackgroundSprite;
    private Transform currentRouteTarget;

    private int lastProviderFrameIndex = -1;

    private void Start()
    {
        if (attentionRiskProvider == null)
            attentionRiskProvider = FindObjectOfType<AttentionRiskProvider>();

        LoadAccidentCsv();
        CreateRouteLine();
        CreateArrivalTimeText();
        attentionPath = new NavMeshPath();
    }

    private void Update()
    {
        UpdateRouteFromProvider();
        SyncArrivalTimeTextVisibility();
        SyncNearbyObjectTextVisibility();
        FaceArrivalTimeTextToCamera();
        FaceNearbyObjectTextToCamera();
    }

    private void CreateRouteLine()
    {
        routeLine = new GameObject($"AttentionRouteLine_Rank{Mathf.Max(1, interestRank)}").AddComponent<LineRenderer>();
        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;
        routeLine.positionCount = 0;
        routeLine.enabled = false;
    }

    private void CreateArrivalTimeText()
    {
        arrivalTimeText = new GameObject($"ArrivalTimeText_Rank{Mathf.Max(1, interestRank)}").AddComponent<TextMeshPro>();
        arrivalTimeText.alignment = TextAlignmentOptions.Center;
        arrivalTimeText.fontSize = arrivalTimeTextSize;
        arrivalTimeText.text = "";
        arrivalTimeText.enabled = false;
    }

    private void UpdateRouteFromProvider()
    {
        if (attentionRiskProvider == null ||
            !attentionRiskProvider.HasCurrentFrame ||
            !attentionRiskProvider.HasPelvisPosition)
        {
            ClearAttentionPath();
            return;
        }

        if (lastProviderFrameIndex == attentionRiskProvider.CurrentFrameIndex)
            return;

        lastProviderFrameIndex = attentionRiskProvider.CurrentFrameIndex;

        if (!attentionRiskProvider.TryGetTargetByRank(
            interestRank,
            out Transform target,
            out float attentionScore
        ))
        {
            if (logRouteSearch)
            {
                Debug.Log(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: no route target."
                );
            }

            ClearAttentionPath();
            return;
        }

        currentRouteTarget = target;
        UpdateAttentionPath(
            attentionRiskProvider.PelvisPosition,
            target,
            attentionScore
        );
    }

    private void UpdateAttentionPath(
        Vector3 pelvisPosition,
        Transform target,
        float attentionScore
    )
    {
        if (attentionScore <= 0f || target == null)
        {
            if (logRouteSearch)
            {
                Debug.Log(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: no route. attentionScore={attentionScore:F2}, target={(target == null ? "null" : target.name)}"
                );
            }

            ClearAttentionPath();
            return;
        }

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: trying route. pelvis={pelvisPosition}, target={target.name}, targetPosition={target.position}, attentionScore={attentionScore:F2}"
            );
        }

        if (!NavMesh.SamplePosition(
            pelvisPosition,
            out NavMeshHit startHit,
            startSearchRadius,
            NavMesh.AllAreas
        ))
        {
            if (logRouteSearch)
            {
                Debug.LogWarning(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: start is not on NavMesh. pelvis={pelvisPosition}, searchRadius={startSearchRadius:F2}"
                );
            }

            ClearAttentionPath();
            return;
        }

        if (!NavMesh.SamplePosition(
            target.position,
            out NavMeshHit goalHit,
            goalSearchRadius,
            NavMesh.AllAreas
        ))
        {
            if (logRouteSearch)
            {
                Debug.LogWarning(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: goal is not near NavMesh. target={target.name}, targetPosition={target.position}, searchRadius={goalSearchRadius:F2}"
                );
            }

            ClearAttentionPath();
            return;
        }

        bool calculated = NavMesh.CalculatePath(
            startHit.position,
            goalHit.position,
            NavMesh.AllAreas,
            attentionPath
        );

        hasAttentionPath =
            calculated &&
            attentionPath.status == NavMeshPathStatus.PathComplete;

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: path result. calculated={calculated}, status={attentionPath.status}, corners={attentionPath.corners.Length}, visible={hasAttentionPath}"
            );
        }

        ApplyRouteLine();
    }

    private void ClearAttentionPath()
    {
        hasAttentionPath = false;
        currentRouteTarget = null;

        if (attentionPath != null)
            attentionPath.ClearCorners();

        if (routeLine != null)
        {
            routeLine.positionCount = 0;
            routeLine.enabled = false;
        }

        if (arrivalTimeText != null)
        {
            arrivalTimeText.text = "";
            arrivalTimeText.enabled = false;
        }

        ClearNearbyObjectText();
    }

    private void ApplyRouteLine()
    {
        if (!hasAttentionPath ||
            attentionPath == null ||
            attentionPath.corners.Length < 2 ||
            routeLine == null)
        {
            ClearAttentionPath();
            return;
        }

        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;

        List<Vector3> displayCorners =
            BuildHeightAwareRoutePoints(attentionPath);

        if (displayCorners.Count < 2)
        {
            ClearAttentionPath();
            return;
        }

        routeLine.positionCount = displayCorners.Count;
        routeLine.SetPositions(displayCorners.ToArray());
        routeLine.enabled = true;

        ApplyArrivalTimeText(displayCorners);
        ApplyNearbyObjectText(displayCorners);

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: route line updated. originalCorners={attentionPath.corners.Length}, displayPoints={displayCorners.Count}"
            );
        }
    }

    private void ApplyArrivalTimeText(List<Vector3> routePoints)
    {
        if (arrivalTimeText == null)
            return;

        if (!showArrivalTimeText ||
            routePoints == null ||
            routePoints.Count < 2 ||
            movementSpeedMetersPerSecond <= 0f)
        {
            arrivalTimeText.text = "";
            arrivalTimeText.enabled = false;
            return;
        }

        float routeLength = CalculateRouteLength(routePoints);
        float arrivalTimeSec =
            routeLength /
            movementSpeedMetersPerSecond;

        arrivalTimeText.fontSize = arrivalTimeTextSize;
        arrivalTimeText.color = GetRouteLineColor();
        arrivalTimeText.text =
            $"{arrivalTimeSec:F1}s\n{routeLength:F2}m";
        arrivalTimeText.transform.position =
            GetRouteMidpoint(routePoints) +
            Vector3.up * arrivalTimeTextYOffset;
        arrivalTimeText.enabled = true;
        FaceArrivalTimeTextToCamera();
    }

    private void ApplyNearbyObjectText(List<Vector3> routePoints)
    {
        if (!showNearbyObjectText ||
            routePoints == null ||
            routePoints.Count < 2 ||
            nearbyObjectSurfaceDistance < 0f ||
            maxNearbyObjectsToShow <= 0)
        {
            ClearNearbyObjectText();
            return;
        }

        List<NearbyRouteObject> nearbyRouteObjects =
            FindNearbyRouteObjects(routePoints);

        if (nearbyRouteObjects.Count == 0)
        {
            ClearNearbyObjectText();
            return;
        }

        ApplyNearbyObjectWorldTexts(nearbyRouteObjects);
        ApplyNearbyObjectSummaryText(nearbyRouteObjects);
    }

    private void ApplyNearbyObjectWorldTexts(
        List<NearbyRouteObject> nearbyRouteObjects
    )
    {
        Camera labelCamera = Camera.main;
        List<NearbyPanelRect> occupiedRects = new();

        for (int i = 0; i < nearbyRouteObjects.Count; i++)
        {
            TextMeshPro text = GetNearbyObjectWorldText(i);
            NearbyRouteObject routeObject = nearbyRouteObjects[i];

            text.fontSize = nearbyObjectTextSize;
            text.color = nearbyObjectTextColor;
            text.alignment = TextAlignmentOptions.Center;
            text.text = BuildNearbyObjectLabelText(routeObject, i + 1);
            text.transform.position =
                routeObject.LabelPosition +
                Vector3.up * nearbyObjectTextYOffset;

            if (labelCamera != null)
            {
                text.transform.rotation =
                    Quaternion.LookRotation(
                        text.transform.position -
                        labelCamera.transform.position
                    );
            }

            text.enabled = true;
            Vector2 panelSize = GetNearbyObjectPanelSize(text);
            text.transform.position =
                ResolveNearbyObjectPanelPosition(
                    text.transform.position,
                    panelSize,
                    occupiedRects,
                    labelCamera
                );

            ApplyNearbyObjectBackground(i, text, panelSize);
        }

        for (int i = nearbyRouteObjects.Count; i < nearbyWorldTexts.Count; i++)
        {
            nearbyWorldTexts[i].text = "";
            nearbyWorldTexts[i].enabled = false;
            SetNearbyObjectBackgroundVisible(i, false);
        }

        FaceNearbyObjectTextToCamera();
    }

    private void ApplyNearbyObjectSummaryText(
        List<NearbyRouteObject> nearbyRouteObjects
    )
    {
        if (nearbyObjectSummaryText == null)
            return;

        nearbyObjectSummaryText.text =
            BuildNearbyObjectSummaryText(nearbyRouteObjects);
        nearbyObjectSummaryText.enabled = true;
    }

    private TextMeshPro GetNearbyObjectWorldText(int index)
    {
        while (nearbyWorldTexts.Count <= index)
        {
            TextMeshPro text =
                new GameObject(
                    $"NearbyObjectText_Rank{Mathf.Max(1, interestRank)}_{nearbyWorldTexts.Count + 1}"
                ).AddComponent<TextMeshPro>();

            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = nearbyObjectTextSize;
            text.text = "";
            text.enabled = false;
            text.GetComponent<Renderer>().sortingOrder = 1;
            nearbyWorldTexts.Add(text);

            SpriteRenderer background =
                new GameObject(
                    $"NearbyObjectTextBackground_Rank{Mathf.Max(1, interestRank)}_{nearbyWorldBackgrounds.Count + 1}"
                ).AddComponent<SpriteRenderer>();

            background.sprite = GetNearbyObjectBackgroundSprite();
            background.color = nearbyObjectTextBackgroundColor;
            background.sortingOrder = 0;
            background.enabled = false;
            nearbyWorldBackgrounds.Add(background);
        }

        return nearbyWorldTexts[index];
    }

    private void ApplyNearbyObjectBackground(
        int index,
        TextMeshPro text,
        Vector2 panelSize
    )
    {
        if (index < 0 ||
            index >= nearbyWorldBackgrounds.Count)
            return;

        SpriteRenderer background = nearbyWorldBackgrounds[index];

        if (background == null)
            return;

        if (!showNearbyObjectTextBackground ||
            text == null ||
            !text.enabled)
        {
            background.enabled = false;
            return;
        }

        background.color = nearbyObjectTextBackgroundColor;
        background.transform.position =
            text.transform.position +
            text.transform.forward * 0.01f;
        background.transform.rotation = text.transform.rotation;
        background.transform.localScale =
            new Vector3(panelSize.x, panelSize.y, 1f);
        background.enabled = true;
    }

    private Vector2 GetNearbyObjectPanelSize(TextMeshPro text)
    {
        text.ForceMeshUpdate();
        Vector2 renderedSize = text.GetRenderedValues(false);

        return new Vector2(
            Mathf.Max(
                renderedSize.x + nearbyObjectTextBackgroundPadding.x * 2f,
                0.01f
            ),
            Mathf.Max(
                renderedSize.y + nearbyObjectTextBackgroundPadding.y * 2f,
                0.01f
            )
        );
    }

    private Vector3 ResolveNearbyObjectPanelPosition(
        Vector3 desiredPosition,
        Vector2 panelSize,
        List<NearbyPanelRect> occupiedRects,
        Camera labelCamera
    )
    {
        if (labelCamera == null)
            return desiredPosition;

        Vector2 center =
            ProjectToCameraPlane(
                desiredPosition,
                labelCamera
            );
        Vector2 spacedSize =
            panelSize +
            Vector2.one * Mathf.Max(nearbyObjectPanelSpacing, 0f) * 2f;
        Vector3 cameraRight = labelCamera.transform.right;
        Vector3 cameraUp = labelCamera.transform.up;

        for (int ring = 0; ring <= 8; ring++)
        {
            foreach (Vector2 offset in GetPanelOffsetCandidates(
                ring,
                spacedSize
            ))
            {
                NearbyPanelRect candidateRect =
                    new(center + offset, spacedSize);

                if (DoesPanelOverlapAny(candidateRect, occupiedRects))
                    continue;

                occupiedRects.Add(candidateRect);
                return desiredPosition +
                       cameraRight * offset.x +
                       cameraUp * offset.y;
            }
        }

        NearbyPanelRect fallbackRect =
            new(center, spacedSize);
        occupiedRects.Add(fallbackRect);
        return desiredPosition;
    }

    private IEnumerable<Vector2> GetPanelOffsetCandidates(
        int ring,
        Vector2 panelSize
    )
    {
        if (ring == 0)
        {
            yield return Vector2.zero;
            yield break;
        }

        float x = panelSize.x * ring;
        float y = panelSize.y * ring;

        yield return new Vector2(0f, y);
        yield return new Vector2(x, y);
        yield return new Vector2(-x, y);
        yield return new Vector2(x, 0f);
        yield return new Vector2(-x, 0f);
        yield return new Vector2(0f, -y);
        yield return new Vector2(x, -y);
        yield return new Vector2(-x, -y);
    }

    private Vector2 ProjectToCameraPlane(
        Vector3 worldPosition,
        Camera labelCamera
    )
    {
        return new Vector2(
            Vector3.Dot(worldPosition, labelCamera.transform.right),
            Vector3.Dot(worldPosition, labelCamera.transform.up)
        );
    }

    private bool DoesPanelOverlapAny(
        NearbyPanelRect candidate,
        List<NearbyPanelRect> occupiedRects
    )
    {
        foreach (NearbyPanelRect occupiedRect in occupiedRects)
        {
            if (candidate.Overlaps(occupiedRect))
                return true;
        }

        return false;
    }

    private void SetNearbyObjectBackgroundVisible(int index, bool visible)
    {
        if (index < 0 ||
            index >= nearbyWorldBackgrounds.Count ||
            nearbyWorldBackgrounds[index] == null)
            return;

        nearbyWorldBackgrounds[index].enabled = visible;
    }

    private Sprite GetNearbyObjectBackgroundSprite()
    {
        if (nearbyObjectBackgroundSprite != null)
            return nearbyObjectBackgroundSprite;

        Texture2D texture = new(1, 1);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();
        nearbyObjectBackgroundSprite =
            Sprite.Create(
                texture,
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                1f
            );

        return nearbyObjectBackgroundSprite;
    }

    private void ClearNearbyObjectText()
    {
        if (nearbyObjectSummaryText != null)
        {
            nearbyObjectSummaryText.text = "";
            nearbyObjectSummaryText.enabled = false;
        }

        for (int i = 0; i < nearbyWorldTexts.Count; i++)
        {
            TextMeshPro text = nearbyWorldTexts[i];

            if (text == null)
                continue;

            text.text = "";
            text.enabled = false;
            SetNearbyObjectBackgroundVisible(i, false);
        }
    }

    private List<NearbyRouteObject> FindNearbyRouteObjects(List<Vector3> routePoints)
    {
        List<NearbyObjectCandidate> candidates = CollectNearbyObjectCandidates();
        List<NearbyRouteObject> results = new();
        HashSet<GameObject> addedObjects = new();
        float walkedLength = 0f;

        for (int i = 0; i < routePoints.Count; i++)
        {
            if (i > 0)
            {
                walkedLength += Vector3.Distance(
                    routePoints[i - 1],
                    routePoints[i]
                );
            }

            Vector3 routePoint = routePoints[i];

            foreach (NearbyObjectCandidate candidate in candidates)
            {
                if (candidate.Root == null ||
                    addedObjects.Contains(candidate.Root))
                    continue;

                float surfaceDistance =
                    CalculateSurfaceDistance(
                        routePoint,
                        candidate,
                        out Vector3 closestPoint
                    );

                if (surfaceDistance > nearbyObjectSurfaceDistance)
                    continue;

                addedObjects.Add(candidate.Root);
                results.Add(
                    new NearbyRouteObject(
                        candidate.DisplayName,
                        walkedLength,
                        surfaceDistance,
                        closestPoint,
                        candidate.AccidentText
                    )
                );

                if (results.Count >= maxNearbyObjectsToShow)
                    return results;
            }
        }

        return results;
    }

    private List<NearbyObjectCandidate> CollectNearbyObjectCandidates()
    {
        List<NearbyObjectCandidate> candidates = new();
        HashSet<GameObject> addedObjects = new();

        if (nearbyObjects != null && nearbyObjects.Count > 0)
        {
            foreach (GameObject obj in nearbyObjects)
            {
                AddNearbyObjectCandidate(obj, candidates, addedObjects);
            }

            return candidates;
        }

        foreach (Collider collider in FindObjectsOfType<Collider>())
        {
            if (collider == null)
                continue;

            AddNearbyObjectCandidate(
                collider.gameObject,
                candidates,
                addedObjects
            );
        }

        foreach (Renderer renderer in FindObjectsOfType<Renderer>())
        {
            if (renderer == null)
                continue;

            AddNearbyObjectCandidate(
                renderer.gameObject,
                candidates,
                addedObjects
            );
        }

        return candidates;
    }

    private void AddNearbyObjectCandidate(
        GameObject obj,
        List<NearbyObjectCandidate> candidates,
        HashSet<GameObject> addedObjects
    )
    {
        if (obj == null ||
            !obj.activeInHierarchy ||
            addedObjects.Contains(obj) ||
            IsGeneratedRouteObject(obj) ||
            !IsNearbyObjectAllowed(obj))
            return;

        if (!includeRouteTargetAsNearbyObject &&
            currentRouteTarget != null &&
            (obj.transform == currentRouteTarget ||
             obj.transform.IsChildOf(currentRouteTarget) ||
             currentRouteTarget.IsChildOf(obj.transform)))
            return;

        Collider[] colliders =
            obj.GetComponentsInChildren<Collider>();
        Renderer[] renderers =
            obj.GetComponentsInChildren<Renderer>();

        if (colliders.Length == 0 &&
            renderers.Length == 0)
            return;

        addedObjects.Add(obj);
        string displayName = GetNearbyObjectDisplayName(obj);
        candidates.Add(
            new NearbyObjectCandidate(
                obj,
                colliders,
                renderers,
                displayName,
                GetAccidentText(displayName, obj.name)
            )
        );
    }

    private void LoadAccidentCsv()
    {
        accidentTextByFurnitureName.Clear();

        if (string.IsNullOrWhiteSpace(accidentCsvAssetPath))
            return;

        string path =
            Path.Combine(Application.dataPath, accidentCsvAssetPath);

        if (!File.Exists(path))
        {
            if (logRouteSearch)
            {
                Debug.LogWarning(
                    $"RouteSearch[rank {Mathf.Max(1, interestRank)}]: accident csv not found. path={path}"
                );
            }

            return;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] columns = line.Split(',');

            if (columns.Length < 2)
                continue;

            string furnitureName = columns[0].Trim();
            string accidentText = columns[1].Trim();

            if (string.IsNullOrEmpty(furnitureName) ||
                string.IsNullOrEmpty(accidentText))
                continue;

            accidentTextByFurnitureName[furnitureName] = accidentText;
        }
    }

    private string GetAccidentText(
        string displayName,
        string objectName
    )
    {
        if (!string.IsNullOrEmpty(displayName) &&
            accidentTextByFurnitureName.TryGetValue(
                displayName,
                out string accidentTextByDisplayName
            ))
        {
            return accidentTextByDisplayName;
        }

        if (!string.IsNullOrEmpty(objectName) &&
            accidentTextByFurnitureName.TryGetValue(
                objectName,
                out string accidentTextByObjectName
            ))
        {
            return accidentTextByObjectName;
        }

        return "";
    }

    private string GetNearbyObjectDisplayName(GameObject obj)
    {
        FurnitureTextData textData =
            obj.GetComponentInParent<FurnitureTextData>();

        if (textData == null)
        {
            textData =
                obj.GetComponentInChildren<FurnitureTextData>();
        }

        if (textData != null)
            return textData.DisplayText;

        return obj.name;
    }

    private bool IsNearbyObjectAllowed(GameObject obj)
    {
        if ((nearbyObjectLayerMask.value & (1 << obj.layer)) == 0)
            return false;

        if (!string.IsNullOrEmpty(nearbyObjectTag) &&
            !obj.CompareTag(nearbyObjectTag))
            return false;

        return true;
    }

    private bool IsGeneratedRouteObject(GameObject obj)
    {
        return (routeLine != null &&
                obj == routeLine.gameObject) ||
               (arrivalTimeText != null &&
                obj == arrivalTimeText.gameObject) ||
               IsNearbyWorldTextObject(obj) ||
               IsNearbyWorldBackgroundObject(obj);
    }

    private bool IsNearbyWorldTextObject(GameObject obj)
    {
        foreach (TextMeshPro text in nearbyWorldTexts)
        {
            if (text != null &&
                obj == text.gameObject)
                return true;
        }

        return false;
    }

    private bool IsNearbyWorldBackgroundObject(GameObject obj)
    {
        foreach (SpriteRenderer background in nearbyWorldBackgrounds)
        {
            if (background != null &&
                obj == background.gameObject)
                return true;
        }

        return false;
    }

    private float CalculateSurfaceDistance(
        Vector3 routePoint,
        NearbyObjectCandidate candidate,
        out Vector3 closestSurfacePoint
    )
    {
        float minDistance = float.PositiveInfinity;
        closestSurfacePoint = routePoint;

        foreach (Collider collider in candidate.Colliders)
        {
            if (collider == null ||
                !collider.enabled)
                continue;

            Vector3 closestPoint =
                collider.ClosestPoint(routePoint);
            float distance =
                Vector3.Distance(routePoint, closestPoint);

            if (distance >= minDistance)
                continue;

            minDistance = distance;
            closestSurfacePoint = closestPoint;
        }

        if (!float.IsInfinity(minDistance))
            return minDistance;

        foreach (Renderer renderer in candidate.Renderers)
        {
            if (renderer == null ||
                !renderer.enabled)
                continue;

            Vector3 closestPoint =
                renderer.bounds.ClosestPoint(routePoint);
            float distance =
                Vector3.Distance(routePoint, closestPoint);

            if (distance >= minDistance)
                continue;

            minDistance = distance;
            closestSurfacePoint = closestPoint;
        }

        return minDistance;
    }

    private string BuildNearbyObjectLabelText(
        NearbyRouteObject routeObject,
        int rank
    )
    {
        StringBuilder builder = new();
        builder.Append("#");
        builder.Append(rank);
        builder.Append(" ");
        builder.AppendLine(routeObject.DisplayName);

        if (movementSpeedMetersPerSecond > 0f)
        {
            builder.Append(
                (routeObject.RouteDistance /
                 movementSpeedMetersPerSecond).ToString("F1")
            );
            builder.Append("s ");
        }

        builder.Append(routeObject.RouteDistance.ToString("F2"));
        builder.Append("m");

        if (!string.IsNullOrEmpty(routeObject.AccidentText))
        {
            builder.AppendLine();
            builder.Append(routeObject.AccidentText);
        }

        return builder.ToString();
    }

    private string BuildNearbyObjectSummaryText(
        List<NearbyRouteObject> nearbyRouteObjects
    )
    {
        StringBuilder builder = new();
        builder.AppendLine("Nearby furniture");

        for (int i = 0; i < nearbyRouteObjects.Count; i++)
        {
            NearbyRouteObject routeObject = nearbyRouteObjects[i];
            builder.Append("#");
            builder.Append(i + 1);
            builder.Append(" ");
            builder.AppendLine(routeObject.DisplayName);

            if (movementSpeedMetersPerSecond > 0f)
            {
                builder.Append(
                    (routeObject.RouteDistance /
                     movementSpeedMetersPerSecond).ToString("F1")
                );
                builder.Append("s ");
            }

            builder.Append(routeObject.RouteDistance.ToString("F2"));
            builder.AppendLine("m");

            if (!string.IsNullOrEmpty(routeObject.AccidentText))
            {
                builder.AppendLine(routeObject.AccidentText);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private float CalculateRouteLength(List<Vector3> routePoints)
    {
        float length = 0f;

        for (int i = 0; i < routePoints.Count - 1; i++)
        {
            length += Vector3.Distance(
                routePoints[i],
                routePoints[i + 1]
            );
        }

        return length;
    }

    private Vector3 GetRouteMidpoint(List<Vector3> routePoints)
    {
        float totalLength = CalculateRouteLength(routePoints);
        float halfLength = totalLength * 0.5f;
        float walkedLength = 0f;

        for (int i = 0; i < routePoints.Count - 1; i++)
        {
            Vector3 from = routePoints[i];
            Vector3 to = routePoints[i + 1];
            float segmentLength = Vector3.Distance(from, to);

            if (walkedLength + segmentLength >= halfLength)
            {
                float t =
                    (halfLength - walkedLength) /
                    Mathf.Max(segmentLength, 0.0001f);

                return Vector3.Lerp(
                    from,
                    to,
                    t
                );
            }

            walkedLength += segmentLength;
        }

        return routePoints[routePoints.Count - 1];
    }

    private Color GetRouteLineColor()
    {
        if (routeLineMaterial != null &&
            routeLineMaterial.HasProperty("_Color"))
        {
            return routeLineMaterial.color;
        }

        if (routeLineMaterial != null &&
            routeLineMaterial.HasProperty("_BaseColor"))
        {
            return routeLineMaterial.GetColor("_BaseColor");
        }

        return Color.white;
    }

    private void SyncArrivalTimeTextVisibility()
    {
        if (showArrivalTimeText ||
            arrivalTimeText == null)
            return;

        arrivalTimeText.text = "";
        arrivalTimeText.enabled = false;
    }

    private void SyncNearbyObjectTextVisibility()
    {
        if (showNearbyObjectText)
            return;

        ClearNearbyObjectText();
    }

    private void FaceArrivalTimeTextToCamera()
    {
        if (arrivalTimeText == null ||
            !arrivalTimeText.enabled ||
            Camera.main == null)
            return;

        arrivalTimeText.transform.rotation =
            Quaternion.LookRotation(
                arrivalTimeText.transform.position -
                Camera.main.transform.position
            );
    }

    private void FaceNearbyObjectTextToCamera()
    {
        if (Camera.main == null)
            return;

        foreach (TextMeshPro text in nearbyWorldTexts)
        {
            if (text == null ||
                !text.enabled)
                continue;

            text.transform.rotation =
                Quaternion.LookRotation(
                    text.transform.position -
                    Camera.main.transform.position
                );

            int index = nearbyWorldTexts.IndexOf(text);

            if (index < 0 ||
                index >= nearbyWorldBackgrounds.Count ||
                nearbyWorldBackgrounds[index] == null ||
                !nearbyWorldBackgrounds[index].enabled)
                continue;

            nearbyWorldBackgrounds[index].transform.rotation =
                text.transform.rotation;
            nearbyWorldBackgrounds[index].transform.position =
                text.transform.position +
                text.transform.forward * 0.01f;
        }
    }

    private List<Vector3> BuildHeightAwareRoutePoints(NavMeshPath targetPath)
    {
        List<Vector3> points = new();
        Vector3[] corners = targetPath.corners;

        for (int i = 0; i < corners.Length - 1; i++)
        {
            Vector3 from = corners[i];
            Vector3 to = corners[i + 1];
            float distance = Vector3.Distance(from, to);
            int sampleCount = Mathf.Max(
                1,
                Mathf.CeilToInt(
                    distance /
                    Mathf.Max(routeHeightSampleInterval, 0.01f)
                )
            );

            for (int j = 0; j <= sampleCount; j++)
            {
                if (i > 0 && j == 0)
                    continue;

                Vector3 samplePoint =
                    Vector3.Lerp(
                        from,
                        to,
                        (float)j / sampleCount
                    );

                if (NavMesh.SamplePosition(
                    samplePoint,
                    out NavMeshHit heightHit,
                    routeHeightSampleRadius,
                    NavMesh.AllAreas
                ))
                {
                    points.Add(
                        heightHit.position +
                        Vector3.up * routeLineYOffset
                    );
                }
                else
                {
                    points.Add(
                        samplePoint +
                        Vector3.up * routeLineYOffset
                    );
                }
            }
        }

        return points;
    }

    private void OnDrawGizmos()
    {
        if (!hasAttentionPath ||
            attentionPath == null ||
            attentionPath.corners.Length < 2)
            return;

        List<Vector3> displayCorners =
            BuildHeightAwareRoutePoints(attentionPath);

        if (displayCorners.Count < 2)
            return;

        Gizmos.color = Color.cyan;

        for (int i = 0; i < displayCorners.Count - 1; i++)
        {
            Gizmos.DrawLine(
                displayCorners[i],
                displayCorners[i + 1]
            );
        }
    }

    private readonly struct NearbyPanelRect
    {
        public NearbyPanelRect(
            Vector2 center,
            Vector2 size
        )
        {
            Center = center;
            Size = size;
        }

        private Vector2 Center { get; }
        private Vector2 Size { get; }

        public bool Overlaps(NearbyPanelRect other)
        {
            float halfWidth = Size.x * 0.5f;
            float halfHeight = Size.y * 0.5f;
            float otherHalfWidth = other.Size.x * 0.5f;
            float otherHalfHeight = other.Size.y * 0.5f;

            return Mathf.Abs(Center.x - other.Center.x) <
                   halfWidth + otherHalfWidth &&
                   Mathf.Abs(Center.y - other.Center.y) <
                   halfHeight + otherHalfHeight;
        }
    }

    private class NearbyObjectCandidate
    {
        public NearbyObjectCandidate(
            GameObject root,
            Collider[] colliders,
            Renderer[] renderers,
            string displayName,
            string accidentText
        )
        {
            Root = root;
            Colliders = colliders;
            Renderers = renderers;
            DisplayName = displayName;
            AccidentText = accidentText;
        }

        public GameObject Root { get; }
        public Collider[] Colliders { get; }
        public Renderer[] Renderers { get; }
        public string DisplayName { get; }
        public string AccidentText { get; }
    }

    private class NearbyRouteObject
    {
        public NearbyRouteObject(
            string displayName,
            float routeDistance,
            float surfaceDistance,
            Vector3 labelPosition,
            string accidentText
        )
        {
            DisplayName = displayName;
            RouteDistance = routeDistance;
            SurfaceDistance = surfaceDistance;
            LabelPosition = labelPosition;
            AccidentText = accidentText;
        }

        public string DisplayName { get; }
        public float RouteDistance { get; }
        public float SurfaceDistance { get; }
        public Vector3 LabelPosition { get; }
        public string AccidentText { get; }
    }
}
