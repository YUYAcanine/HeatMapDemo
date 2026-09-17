using System.Collections.Generic;
using UnityEngine;
using Button = UnityEngine.UI.Button;

public class ICP : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private MeshFilter sourcePointCloud;
    [SerializeField] private Transform movingRoot;

    [Header("References")]
    [SerializeField] private Transform referenceRoot;
    [SerializeField] private bool includeInactiveReferences = false;
    [SerializeField] private bool requirePointMeshTopology = true;
    [SerializeField] private int minReferencePointCount = 100;

    [Header("UI")]
    [SerializeField] private Button alignButton;

    [Header("ICP")]
    [SerializeField] private int iterations = 35;
    [SerializeField] private int maxSourcePoints = 3000;
    [SerializeField] private int maxTargetPoints = 12000;
    [SerializeField] private float maxCorrespondenceDistance = 0.35f;
    [SerializeField] private float fineCorrespondenceDistance = 0.06f;
    [SerializeField, Range(0.2f, 1f)] private float expectedOverlapRatio = 0.55f;
    [SerializeField] private int minCorrespondenceCount = 30;
    [SerializeField] private float robustDistanceMultiplier = 2.5f;
    [SerializeField] private bool shrinkCorrespondenceDistance = true;
    [SerializeField] private bool requireReciprocalCorrespondences = true;
    [SerializeField] private float minMeanErrorImprovement = 0.0001f;
    [SerializeField] private bool logProgress = true;
    [SerializeField] private bool logReferenceCandidates = true;

    private void Reset()
    {
        sourcePointCloud = GetComponent<MeshFilter>();
        movingRoot = transform.parent != null ? transform.parent : transform;
    }

    private void Awake()
    {
        if (sourcePointCloud == null)
            sourcePointCloud = GetComponent<MeshFilter>();

        if (movingRoot == null)
            movingRoot = transform.parent != null ? transform.parent : transform;
    }

    private void OnEnable()
    {
        if (alignButton == null)
        {
            if (logProgress)
                Debug.LogWarning("ICP: alignButton is not assigned. Assign a UI Button or call Align() from another script.");

            return;
        }

        alignButton.onClick.RemoveListener(Align);
        alignButton.onClick.AddListener(Align);

        if (logProgress)
            Debug.Log($"ICP: align button registered. button={alignButton.name}");
    }

    private void OnDisable()
    {
        if (alignButton != null)
            alignButton.onClick.RemoveListener(Align);
    }

    public void Align()
    {
        if (logProgress)
            Debug.Log("ICP: Align button pressed.");

        ResolveRuntimeReferences();
        LogCurrentValidationState();

        if (sourcePointCloud == null ||
            sourcePointCloud.sharedMesh == null)
        {
            LogStop("source point cloud is not assigned or has no mesh.");
            return;
        }

        if (movingRoot == null)
        {
            LogStop("movingRoot is not assigned.");
            return;
        }

        Mesh sourceMesh =
            sourcePointCloud.sharedMesh;

        if (logProgress)
        {
            Debug.Log(
                $"ICP: start. source={GetObjectPath(sourcePointCloud.transform)}, sourceVertices={sourceMesh.vertexCount}, sourceIsPointMesh={IsPointCloudMesh(sourceMesh)}, movingRoot={GetObjectPath(movingRoot)}, movingRootPosition={movingRoot.position}, movingRootRotation={movingRoot.rotation.eulerAngles}");
            Debug.Log(
                $"ICP: settings. referenceRoot={GetObjectPath(referenceRoot)}, iterations={iterations}, maxSourcePoints={maxSourcePoints}, maxTargetPoints={maxTargetPoints}, maxCorrespondenceDistance={maxCorrespondenceDistance:F4}, fineCorrespondenceDistance={fineCorrespondenceDistance:F4}, expectedOverlapRatio={expectedOverlapRatio:F2}, minCorrespondenceCount={minCorrespondenceCount}, robustDistanceMultiplier={robustDistanceMultiplier:F2}, shrinkCorrespondenceDistance={shrinkCorrespondenceDistance}, requireReciprocalCorrespondences={requireReciprocalCorrespondences}, minReferencePointCount={minReferencePointCount}, requirePointMeshTopology={requirePointMeshTopology}, includeInactiveReferences={includeInactiveReferences}");
        }

        List<MeshFilter> references =
            GetReferencePointClouds();

        if (references.Count == 0)
        {
            LogStop("no reference point clouds were found.");
            return;
        }

        List<Vector3> targetPoints =
            CollectWorldPoints(references, maxTargetPoints);

        if (logProgress)
            Debug.Log($"ICP: sampled target points. references={references.Count}, targetPoints={targetPoints.Count}");

        if (targetPoints.Count < 3)
        {
            LogStop($"target point count is too small. count={targetPoints.Count}");
            return;
        }

        float previousMeanError =
            float.PositiveInfinity;

        for (int i = 0; i < Mathf.Max(iterations, 1); i++)
        {
            float currentCorrespondenceDistance =
                GetCurrentCorrespondenceDistance(i, Mathf.Max(iterations, 1));
            List<Vector3> sourcePoints =
                CollectWorldPoints(sourcePointCloud, maxSourcePoints);

            if (logProgress)
                Debug.Log($"ICP: iteration={i + 1}/{iterations}, sampled source points={sourcePoints.Count}, correspondenceDistance={currentCorrespondenceDistance:F4}");

            if (sourcePoints.Count < 3)
            {
                LogStop($"source point count is too small. count={sourcePoints.Count}");
                return;
            }

            if (!TryBuildCorrespondences(
                sourcePoints,
                targetPoints,
                currentCorrespondenceDistance,
                out List<Vector3> matchedSource,
                out List<Vector3> matchedTarget,
                out float meanError,
                out int candidateCorrespondenceCount))
            {
                LogStop(
                    $"not enough correspondences. iteration={i + 1}, sourcePoints={sourcePoints.Count}, targetPoints={targetPoints.Count}, candidates={candidateCorrespondenceCount}, kept={matchedSource.Count}, correspondenceDistance={currentCorrespondenceDistance:F4}, expectedOverlapRatio={expectedOverlapRatio:F2}. Try increasing Max Correspondence Distance if the clouds start far apart.");
                return;
            }

            if (!TryEstimateRigidDelta(
                matchedSource,
                matchedTarget,
                out Quaternion deltaRotation,
                out Vector3 deltaTranslation))
            {
                LogStop($"failed to estimate transform. iteration={i + 1}");
                return;
            }

            Vector3 beforePosition =
                movingRoot.position;
            Vector3 beforeRotation =
                movingRoot.rotation.eulerAngles;

            ApplyWorldDelta(deltaRotation, deltaTranslation);

            if (logProgress)
            {
                float deltaAngle =
                    Quaternion.Angle(Quaternion.identity, deltaRotation);

                Debug.Log(
                    $"ICP: iteration={i + 1}/{iterations}, correspondenceDistance={currentCorrespondenceDistance:F4}, candidateCorrespondences={candidateCorrespondenceCount}, keptCorrespondences={matchedSource.Count}, meanError={meanError:F5}, deltaPosition={deltaTranslation.magnitude:F5}, deltaAngle={deltaAngle:F4}, beforePosition={beforePosition}, afterPosition={movingRoot.position}, beforeRotation={beforeRotation}, afterRotation={movingRoot.rotation.eulerAngles}");

                if (deltaTranslation.magnitude < 0.00001f &&
                    deltaAngle < 0.001f)
                {
                    Debug.LogWarning("ICP: estimated movement is almost zero. The clouds may already overlap, or the current correspondences are too weak/symmetric.");
                }
            }

            if (Mathf.Abs(previousMeanError - meanError) < minMeanErrorImprovement)
            {
                if (logProgress)
                    Debug.Log($"ICP: stopped by small error improvement. improvement={Mathf.Abs(previousMeanError - meanError):F6}, threshold={minMeanErrorImprovement:F6}");

                break;
            }

            previousMeanError = meanError;
        }

        if (logProgress)
            Debug.Log($"ICP: finished. movingRoot={movingRoot.name}, position={movingRoot.position}, rotation={movingRoot.rotation.eulerAngles}");
    }

    private void ResolveRuntimeReferences()
    {
        if (sourcePointCloud == null ||
            sourcePointCloud.sharedMesh == null ||
            sourcePointCloud.sharedMesh.vertexCount == 0)
        {
            MeshFilter foundSource =
                FindRuntimeSourcePointCloud();

            if (foundSource != null)
            {
                sourcePointCloud =
                    foundSource;

                if (logProgress)
                    Debug.Log($"ICP: source point cloud auto-detected. source={GetObjectPath(sourcePointCloud.transform)}, vertices={sourcePointCloud.sharedMesh.vertexCount}");
            }
        }

        if (movingRoot == null)
        {
            if (sourcePointCloud != null &&
                sourcePointCloud.transform.parent != null)
            {
                movingRoot =
                    sourcePointCloud.transform.parent;
            }
            else
            {
                movingRoot =
                    transform.parent != null ? transform.parent : transform;
            }

            if (logProgress)
                Debug.Log($"ICP: movingRoot auto-detected. movingRoot={GetObjectPath(movingRoot)}");
        }
    }

    private MeshFilter FindRuntimeSourcePointCloud()
    {
        MeshFilter movingRootSource =
            FindBestPointCloudMeshFilter(
                movingRoot != null
                    ? movingRoot.GetComponentsInChildren<MeshFilter>(true)
                    : null,
                "movingRoot");

        if (movingRootSource != null)
            return movingRootSource;

        MeshFilter childSource =
            FindBestPointCloudMeshFilter(
                GetComponentsInChildren<MeshFilter>(true),
                "ICP object children");

        if (childSource != null)
            return childSource;

        MeshFilter ownMeshFilter =
            GetComponent<MeshFilter>();

        if (ownMeshFilter != null &&
            ownMeshFilter.sharedMesh != null &&
            ownMeshFilter.sharedMesh.vertexCount > 0)
        {
            return ownMeshFilter;
        }

        return null;
    }

    private MeshFilter FindBestPointCloudMeshFilter(
        MeshFilter[] meshFilters,
        string searchLabel)
    {
        if (meshFilters == null ||
            meshFilters.Length == 0)
        {
            if (logProgress)
                Debug.Log($"ICP: source search skipped. label={searchLabel}, candidates=0");

            return null;
        }

        if (logProgress)
            Debug.Log($"ICP: searching source point cloud. label={searchLabel}, candidates={meshFilters.Length}");

        MeshFilter best =
            null;
        int bestVertexCount =
            0;

        foreach (MeshFilter meshFilter in meshFilters)
        {
            if (meshFilter == null ||
                meshFilter.sharedMesh == null ||
                meshFilter.sharedMesh.vertexCount == 0)
            {
                if (logProgress && logReferenceCandidates)
                {
                    string objectPath =
                        meshFilter != null ? GetObjectPath(meshFilter.transform) : "(null)";
                    Debug.Log($"ICP: source candidate rejected. object={objectPath}, reason=no mesh or zero vertices");
                }

                continue;
            }

            if (requirePointMeshTopology &&
                !IsPointCloudMesh(meshFilter.sharedMesh))
            {
                if (logProgress && logReferenceCandidates)
                    Debug.Log($"ICP: source candidate rejected. object={GetObjectPath(meshFilter.transform)}, reason=mesh topology is not Points, vertices={meshFilter.sharedMesh.vertexCount}");

                continue;
            }

            if (referenceRoot != null &&
                meshFilter.transform.IsChildOf(referenceRoot))
            {
                if (logProgress && logReferenceCandidates)
                    Debug.Log($"ICP: source candidate rejected. object={GetObjectPath(meshFilter.transform)}, reason=under referenceRoot");

                continue;
            }

            if (logProgress && logReferenceCandidates)
                Debug.Log($"ICP: source candidate accepted. object={GetObjectPath(meshFilter.transform)}, vertices={meshFilter.sharedMesh.vertexCount}");

            if (meshFilter.sharedMesh.vertexCount > bestVertexCount)
            {
                best =
                    meshFilter;
                bestVertexCount =
                    meshFilter.sharedMesh.vertexCount;
            }
        }

        return best;
    }

    private void LogCurrentValidationState()
    {
        if (!logProgress)
            return;

        string sourcePath =
            sourcePointCloud != null ? GetObjectPath(sourcePointCloud.transform) : "(null)";
        Mesh sourceMesh =
            sourcePointCloud != null ? sourcePointCloud.sharedMesh : null;
        string meshState =
            sourceMesh != null
                ? $"mesh={sourceMesh.name}, vertices={sourceMesh.vertexCount}, isPointMesh={IsPointCloudMesh(sourceMesh)}"
                : "mesh=(null)";
        string movingRootPath =
            movingRoot != null ? GetObjectPath(movingRoot) : "(null)";
        string referenceRootPath =
            referenceRoot != null ? GetObjectPath(referenceRoot) : "(null)";

        Debug.Log($"ICP: validation. source={sourcePath}, {meshState}, movingRoot={movingRootPath}, referenceRoot={referenceRootPath}");
    }

    private void LogStop(
        string reason)
    {
        Debug.Log($"ICP: stopped. reason={reason}");
        Debug.LogWarning($"ICP: stopped. reason={reason}");
    }

    private List<MeshFilter> GetReferencePointClouds()
    {
        List<MeshFilter> references =
            new List<MeshFilter>();

        if (referenceRoot != null)
            AddKinectPointCloudOnceReferences(references);

        MeshFilter[] allMeshFilters =
            referenceRoot != null
                ? referenceRoot.GetComponentsInChildren<MeshFilter>(includeInactiveReferences)
                : FindObjectsOfType<MeshFilter>(includeInactiveReferences);

        if (logProgress)
        {
            Debug.Log(
                referenceRoot != null
                    ? $"ICP: searching reference point clouds under referenceRoot={GetObjectPath(referenceRoot)}, candidates={allMeshFilters.Length}"
                    : $"ICP: searching reference point clouds in scene, candidates={allMeshFilters.Length}");
        }

        foreach (MeshFilter meshFilter in allMeshFilters)
            AddReferenceIfValid(references, meshFilter);

        if (logProgress)
            Debug.Log($"ICP: runtime reference search found {references.Count} point cloud meshes.");

        return references;
    }

    private void AddKinectPointCloudOnceReferences(
        List<MeshFilter> references)
    {
        KinectPointCloudOnce[] pointCloudScripts =
            referenceRoot.GetComponentsInChildren<KinectPointCloudOnce>(includeInactiveReferences);

        if (logProgress)
            Debug.Log($"ICP: searching KinectPointCloudOnce references under referenceRoot={GetObjectPath(referenceRoot)}, scripts={pointCloudScripts.Length}");

        foreach (KinectPointCloudOnce pointCloudScript in pointCloudScripts)
        {
            if (pointCloudScript == null)
                continue;

            MeshFilter meshFilter =
                pointCloudScript.GetComponent<MeshFilter>();

            if (logProgress && logReferenceCandidates)
            {
                Mesh mesh =
                    meshFilter != null ? meshFilter.sharedMesh : null;
                string meshInfo =
                    mesh != null
                        ? $"mesh={mesh.name}, vertices={mesh.vertexCount}, isPointMesh={IsPointCloudMesh(mesh)}"
                        : "mesh=(null)";

                Debug.Log($"ICP: KinectPointCloudOnce candidate. object={GetObjectPath(pointCloudScript.transform)}, {meshInfo}");
            }

            AddReferenceIfValid(references, meshFilter);
        }
    }

    private void AddReferenceIfValid(
        List<MeshFilter> references,
        MeshFilter meshFilter)
    {
        if (meshFilter != null &&
            references.Contains(meshFilter))
        {
            return;
        }

        if (IsValidReference(meshFilter, out string rejectReason))
        {
            references.Add(meshFilter);

            if (logProgress && logReferenceCandidates)
            {
                string sourceType =
                    meshFilter.GetComponent<KinectPointCloudOnce>() != null
                        ? "KinectPointCloudOnce"
                        : "MeshFilter";

                Debug.Log($"ICP: reference accepted. type={sourceType}, object={GetObjectPath(meshFilter.transform)}, vertices={meshFilter.sharedMesh.vertexCount}");
            }
        }
        else if (logProgress && logReferenceCandidates)
        {
            string objectName =
                meshFilter != null ? GetObjectPath(meshFilter.transform) : "(null)";

            Debug.Log($"ICP: reference rejected. object={objectName}, reason={rejectReason}");
        }
    }

    private bool IsValidReference(
        MeshFilter meshFilter,
        out string rejectReason)
    {
        rejectReason =
            string.Empty;

        if (meshFilter == null)
        {
            rejectReason = "MeshFilter is null.";
            return false;
        }

        if (meshFilter == sourcePointCloud)
        {
            rejectReason = "This is the source point cloud.";
            return false;
        }

        if (meshFilter.sharedMesh == null)
        {
            rejectReason = "MeshFilter has no sharedMesh.";
            return false;
        }

        if (meshFilter.sharedMesh.vertexCount < Mathf.Max(minReferencePointCount, 1))
        {
            rejectReason =
                $"Vertex count is too small. vertices={meshFilter.sharedMesh.vertexCount}, min={Mathf.Max(minReferencePointCount, 1)}";
            return false;
        }

        if (movingRoot != null &&
            meshFilter.transform.IsChildOf(movingRoot))
        {
            rejectReason = "MeshFilter is under movingRoot, so it would move together with the source.";
            return false;
        }

        if (requirePointMeshTopology &&
            !IsPointCloudMesh(meshFilter.sharedMesh))
        {
            rejectReason = "Mesh topology is not Points.";
            return false;
        }

        return true;
    }

    private bool IsPointCloudMesh(
        Mesh mesh)
    {
        if (mesh == null ||
            mesh.subMeshCount == 0)
        {
            return false;
        }

        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            if (mesh.GetTopology(i) == MeshTopology.Points)
                return true;
        }

        return false;
    }

    private List<Vector3> CollectWorldPoints(
        List<MeshFilter> meshFilters,
        int maxPoints)
    {
        List<Vector3> points =
            new List<Vector3>();

        foreach (MeshFilter meshFilter in meshFilters)
        {
            AddWorldPoints(meshFilter, points, maxPoints);

            if (points.Count >= maxPoints)
                break;
        }

        return points;
    }

    private List<Vector3> CollectWorldPoints(
        MeshFilter meshFilter,
        int maxPoints)
    {
        List<Vector3> points =
            new List<Vector3>();
        AddWorldPoints(meshFilter, points, maxPoints);
        return points;
    }

    private void AddWorldPoints(
        MeshFilter meshFilter,
        List<Vector3> points,
        int maxPoints)
    {
        if (meshFilter == null ||
            meshFilter.sharedMesh == null ||
            maxPoints <= 0)
        {
            return;
        }

        Vector3[] vertices =
            meshFilter.sharedMesh.vertices;
        int remaining =
            maxPoints - points.Count;

        if (remaining <= 0)
            return;

        int stride =
            Mathf.Max(1, Mathf.CeilToInt((float)vertices.Length / remaining));

        for (int i = 0; i < vertices.Length && points.Count < maxPoints; i += stride)
            points.Add(meshFilter.transform.TransformPoint(vertices[i]));
    }

    private bool TryBuildCorrespondences(
        List<Vector3> sourcePoints,
        List<Vector3> targetPoints,
        float correspondenceDistance,
        out List<Vector3> matchedSource,
        out List<Vector3> matchedTarget,
        out float meanError,
        out int candidateCorrespondenceCount)
    {
        matchedSource =
            new List<Vector3>();
        matchedTarget =
            new List<Vector3>();
        meanError = 0f;
        candidateCorrespondenceCount = 0;
        float maxDistanceSqr =
            correspondenceDistance * correspondenceDistance;
        List<CorrespondenceCandidate> candidates =
            new List<CorrespondenceCandidate>();
        int[] reciprocalSourceByTarget =
            requireReciprocalCorrespondences
                ? BuildNearestSourceByTarget(sourcePoints, targetPoints, maxDistanceSqr)
                : null;

        for (int sourceIndex = 0; sourceIndex < sourcePoints.Count; sourceIndex++)
        {
            Vector3 sourcePoint =
                sourcePoints[sourceIndex];
            Vector3 nearest =
                Vector3.zero;
            int nearestTargetIndex =
                -1;
            float nearestDistanceSqr =
                float.PositiveInfinity;

            for (int targetIndex = 0; targetIndex < targetPoints.Count; targetIndex++)
            {
                Vector3 targetPoint =
                    targetPoints[targetIndex];
                float distanceSqr =
                    (sourcePoint - targetPoint).sqrMagnitude;

                if (distanceSqr < nearestDistanceSqr)
                {
                    nearestDistanceSqr = distanceSqr;
                    nearest = targetPoint;
                    nearestTargetIndex = targetIndex;
                }
            }

            if (nearestDistanceSqr > maxDistanceSqr)
                continue;

            if (requireReciprocalCorrespondences &&
                (nearestTargetIndex < 0 ||
                 reciprocalSourceByTarget[nearestTargetIndex] != sourceIndex))
            {
                continue;
            }

            candidates.Add(
                new CorrespondenceCandidate(
                    sourcePoint,
                    nearest,
                    nearestDistanceSqr));
        }

        candidateCorrespondenceCount =
            candidates.Count;

        if (candidates.Count < Mathf.Max(minCorrespondenceCount, 3))
            return false;

        candidates.Sort(
            (a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));

        int overlapKeepCount =
            Mathf.Clamp(
                Mathf.CeilToInt(candidates.Count * Mathf.Clamp01(expectedOverlapRatio)),
                Mathf.Max(minCorrespondenceCount, 3),
                candidates.Count);
        float robustLimitSqr =
            candidates[overlapKeepCount - 1].DistanceSqr * robustDistanceMultiplier * robustDistanceMultiplier;

        for (int i = 0; i < candidates.Count && matchedSource.Count < overlapKeepCount; i++)
        {
            CorrespondenceCandidate candidate =
                candidates[i];

            if (candidate.DistanceSqr > robustLimitSqr)
                break;

            matchedSource.Add(candidate.Source);
            matchedTarget.Add(candidate.Target);
            meanError += Mathf.Sqrt(candidate.DistanceSqr);
        }

        if (matchedSource.Count < 3)
            return false;

        meanError /= matchedSource.Count;
        return true;
    }

    private int[] BuildNearestSourceByTarget(
        List<Vector3> sourcePoints,
        List<Vector3> targetPoints,
        float maxDistanceSqr)
    {
        int[] nearestSourceByTarget =
            new int[targetPoints.Count];

        for (int i = 0; i < nearestSourceByTarget.Length; i++)
            nearestSourceByTarget[i] = -1;

        for (int targetIndex = 0; targetIndex < targetPoints.Count; targetIndex++)
        {
            Vector3 targetPoint =
                targetPoints[targetIndex];
            float nearestDistanceSqr =
                float.PositiveInfinity;
            int nearestSourceIndex =
                -1;

            for (int sourceIndex = 0; sourceIndex < sourcePoints.Count; sourceIndex++)
            {
                float distanceSqr =
                    (sourcePoints[sourceIndex] - targetPoint).sqrMagnitude;

                if (distanceSqr < nearestDistanceSqr)
                {
                    nearestDistanceSqr = distanceSqr;
                    nearestSourceIndex = sourceIndex;
                }
            }

            if (nearestDistanceSqr <= maxDistanceSqr)
                nearestSourceByTarget[targetIndex] = nearestSourceIndex;
        }

        return nearestSourceByTarget;
    }

    private float GetCurrentCorrespondenceDistance(
        int iteration,
        int totalIterations)
    {
        if (!shrinkCorrespondenceDistance)
            return maxCorrespondenceDistance;

        float startDistance =
            Mathf.Max(maxCorrespondenceDistance, 0.0001f);
        float endDistance =
            Mathf.Clamp(
                fineCorrespondenceDistance,
                0.0001f,
                startDistance);

        if (totalIterations <= 1)
            return endDistance;

        float t =
            (float)iteration / (totalIterations - 1);
        float eased =
            1f - Mathf.Pow(1f - t, 2f);

        return Mathf.Lerp(startDistance, endDistance, eased);
    }

    private bool TryEstimateRigidDelta(
        List<Vector3> source,
        List<Vector3> target,
        out Quaternion rotation,
        out Vector3 translation)
    {
        rotation = Quaternion.identity;
        translation = Vector3.zero;

        if (source.Count != target.Count ||
            source.Count < 3)
        {
            return false;
        }

        Vector3 sourceCentroid =
            ComputeCentroid(source);
        Vector3 targetCentroid =
            ComputeCentroid(target);

        float sxx = 0f;
        float sxy = 0f;
        float sxz = 0f;
        float syx = 0f;
        float syy = 0f;
        float syz = 0f;
        float szx = 0f;
        float szy = 0f;
        float szz = 0f;

        for (int i = 0; i < source.Count; i++)
        {
            Vector3 p =
                source[i] - sourceCentroid;
            Vector3 q =
                target[i] - targetCentroid;

            sxx += p.x * q.x;
            sxy += p.x * q.y;
            sxz += p.x * q.z;
            syx += p.y * q.x;
            syy += p.y * q.y;
            syz += p.y * q.z;
            szx += p.z * q.x;
            szy += p.z * q.y;
            szz += p.z * q.z;
        }

        rotation =
            EstimateRotationFromCovariance(sxx, sxy, sxz, syx, syy, syz, szx, szy, szz);
        translation =
            targetCentroid - rotation * sourceCentroid;

        return true;
    }

    private Vector3 ComputeCentroid(
        List<Vector3> points)
    {
        Vector3 sum =
            Vector3.zero;

        foreach (Vector3 point in points)
            sum += point;

        return sum / points.Count;
    }

    private Quaternion EstimateRotationFromCovariance(
        float sxx,
        float sxy,
        float sxz,
        float syx,
        float syy,
        float syz,
        float szx,
        float szy,
        float szz)
    {
        float[,] n =
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz }
        };

        Vector4 q =
            new Vector4(1f, 0f, 0f, 0f);

        for (int i = 0; i < 32; i++)
        {
            q =
                Multiply(n, q);
            float magnitude =
                q.magnitude;

            if (magnitude <= 0.000001f)
                return Quaternion.identity;

            q /= magnitude;
        }

        return new Quaternion(q.y, q.z, q.w, q.x).normalized;
    }

    private Vector4 Multiply(
        float[,] matrix,
        Vector4 vector)
    {
        return new Vector4(
            matrix[0, 0] * vector.x + matrix[0, 1] * vector.y + matrix[0, 2] * vector.z + matrix[0, 3] * vector.w,
            matrix[1, 0] * vector.x + matrix[1, 1] * vector.y + matrix[1, 2] * vector.z + matrix[1, 3] * vector.w,
            matrix[2, 0] * vector.x + matrix[2, 1] * vector.y + matrix[2, 2] * vector.z + matrix[2, 3] * vector.w,
            matrix[3, 0] * vector.x + matrix[3, 1] * vector.y + matrix[3, 2] * vector.z + matrix[3, 3] * vector.w);
    }

    private void ApplyWorldDelta(
        Quaternion deltaRotation,
        Vector3 deltaTranslation)
    {
        movingRoot.position =
            deltaRotation * movingRoot.position + deltaTranslation;
        movingRoot.rotation =
            deltaRotation * movingRoot.rotation;
    }

    private readonly struct CorrespondenceCandidate
    {
        public readonly Vector3 Source;
        public readonly Vector3 Target;
        public readonly float DistanceSqr;

        public CorrespondenceCandidate(
            Vector3 source,
            Vector3 target,
            float distanceSqr)
        {
            Source = source;
            Target = target;
            DistanceSqr = distanceSqr;
        }
    }

    private string GetObjectPath(
        Transform target)
    {
        if (target == null)
            return "(null)";

        string path =
            target.name;

        Transform current =
            target.parent;

        while (current != null)
        {
            path =
                current.name + "/" + path;
            current =
                current.parent;
        }

        return path;
    }
}
