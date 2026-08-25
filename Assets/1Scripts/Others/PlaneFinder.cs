using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Unity.AI.Navigation;
using Microsoft.Azure.Kinect.Sensor;
using UnityEngine.AI;
using Button = UnityEngine.UI.Button;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PlaneFinder : MonoBehaviour
{
    [System.Serializable]
    public class KinectCaptureSource
    {
        public bool enabled = true;
        public int deviceIndex;
        public Transform kinectTransform;
    }

    public enum PointCloudObjectQuality
    {
        Fast,
        Fine,
        SmoothFine
    }

    public int deviceIndex = 0;

    [Header("Multi Kinect")]
    public bool useMultipleKinects = false;
    public KinectCaptureSource[] kinectSources;

    [Header("Point Skip")]
    public int stride = 4;

    [Header("Save Settings")]
    public string saveFileName = "pointcloud.pcd";

    [Header("UI Buttons")]
    public Button capturePointCloudButton;
    [HideInInspector]
    public Button generatePlaneButton;
    [HideInInspector]
    public Button generateWalkableSurfaceButton;
    public Button buildPointCloudNavMeshButton;
    public Button captureBuildNavMeshAndLinksButton;
    public Button savePcdButton;
    public PointNavLink pointNavLink;

    [Header("Auto Update")]
    public bool autoUpdateCaptureBuildNavMeshAndLinks = false;
    public float autoUpdateIntervalSeconds = 20f;

    [Header("Auxiliary Floor Objects")]
    [Tooltip("点群の平面検知が不安定な場合の補助。実際の床の位置・向きに合わせて配置したPlane/Quad/CubeなどのMeshFilterをドラッグ&ドロップしてください。")]
    public List<MeshFilter> auxiliaryFloorObjects = new List<MeshFilter>();
    [Tooltip("補助床オブジェクトの表面から点をサンプリングする間隔(m)。navMeshCellSize程度を推奨。")]
    public float auxiliaryFloorPointSpacing = 0.03f;
    [Tooltip("補助床オブジェクトの点をRANSAC平面検知・NavMesh生成用の点群に混ぜる。")]
    public bool useAuxiliaryFloorPoints = true;
    [Tooltip("補助床オブジェクトがあれば、その法線(up方向)を床の向きとして直接採用しRANSACをスキップする。点群ノイズによる法線のブレを防げる。")]
    public bool bypassRansacWithAuxiliaryFloor = true;
    [Tooltip("補助床オブジェクトの直上(水平footprint内)にある実点群を、きれいな補助平面の点で置き換える。")]
    public bool prioritizeAuxiliaryFloorOverPointCloud = true;
    [Tooltip("footprint内で実点群を置き換える高さ許容範囲(m)。この範囲内の高さの点だけを床ノイズとみなして除去し、これより高い点(家具など)はそのまま残す。")]
    public float auxiliaryFloorHeightTolerance = 0.12f;
    [Tooltip("補助床オブジェクトのfootprint(水平方向の範囲)を広げるマージン(m)。境界付近の実点群ノイズも除去したい場合に増やす。")]
    public float auxiliaryFloorFootprintMargin = 0.02f;

    [Header("Plane Detection")]
    [HideInInspector]
    public int ransacIterations = 300;
    [HideInInspector]
    public float planeDistanceThreshold = 0.025f;
    [HideInInspector]
    public int minPlanePoints = 80;
    [HideInInspector]
    [Range(0f, 1f)]
    public float minPlanePointRatio = 0.02f;
    [HideInInspector]
    [Range(0f, 1f)]
    public float minUpDot = 0.7f;
    [HideInInspector]
    public bool requireWalkablePlane = true;
    [HideInInspector]
    public bool snapWalkablePlaneToLevel = false;
    [HideInInspector]
    public float levelSnapMaxAngle = 3f;

    [Header("Generated Plane")]
    [HideInInspector]
    public string generatedPlaneName = "DetectedCleanPlane";
    [HideInInspector]
    public float planeMargin = 0.05f;
    [HideInInspector]
    public float minPlaneSize = 0.2f;
    [HideInInspector]
    public bool addMeshCollider = true;
    [HideInInspector]
    public string generatedPlaneLayerName = "";
    [HideInInspector]
    public Color generatedPlaneColor = new Color(0f, 0.8f, 1f, 0.35f);

    [Header("Generated Walkable Surface")]
    [HideInInspector]
    public string generatedWalkableSurfaceName = "DetectedWalkableSurface";
    [HideInInspector]
    public float walkableCellSize = 0.08f;
    [HideInInspector]
    public int minWalkableCellPoints = 1;
    [HideInInspector]
    public bool filterWalkableCellHeightRange = false;
    [HideInInspector]
    public float maxCellHeightRange = 0.18f;
    [HideInInspector]
    public bool useUpperWalkableSurface = true;
    [HideInInspector]
    public float maxNeighborHeightStep = 0.12f;
    [HideInInspector]
    public int walkableSmoothingIterations = 1;
    [HideInInspector]
    public int walkableHoleFillIterations = 3;
    [HideInInspector]
    public int minWalkableHoleNeighborCount = 2;
    [HideInInspector]
    public int walkableSurfaceExpansionIterations = 1;
    [HideInInspector]
    public int minWalkableExpansionNeighborCount = 1;
    [HideInInspector]
    public bool averageWalkableCornerHeights = true;
    [HideInInspector]
    public bool doubleSidedWalkableSurface = true;
    [HideInInspector]
    public bool addWalkableSurfaceCollider = true;
    [HideInInspector]
    public Color generatedWalkableSurfaceColor = new Color(0.1f, 0.9f, 0.35f, 0.45f);

    [Header("Generated NavMesh")]
    public string generatedNavMeshObjectName = "PointCloudNavMeshSource";
    [HideInInspector]
    public string generatedNavMeshSourceMeshObjectName = "SourceMesh";
    public bool showNavMeshSourceMesh = false;
    [HideInInspector]
    public Color generatedNavMeshSourceColor = new Color(0.15f, 0.9f, 0.25f, 0.35f);
    [HideInInspector]
    public float navMeshCellSize = 0.08f;
    [HideInInspector]
    public int minNavMeshCellPoints = 1;
    [HideInInspector]
    public float horizontalSurfaceHeightBinSize = 0.1f;
    [HideInInspector]
    public int minHorizontalSurfaceLayerPoints = 5;
    [HideInInspector]
    public int minHorizontalSurfaceClusterCells = 3;
    [HideInInspector]
    public float minHorizontalSurfaceClusterSize = 0.08f;
    [HideInInspector]
    public float minHorizontalSurfaceClusterDensity = 0.08f;
    [HideInInspector]
    public float verticalColumnRejectHeight = 0.18f;
    [HideInInspector]
    public float verticalColumnTopTolerance = 0.12f;
    [HideInInspector]
    public float maxHorizontalSurfaceNormalAngle = 15f;
    [HideInInspector]
    public float maxNavMeshNeighborHeightStep = 0.08f;
    [Tooltip("小さい穴を埋める処理を何回繰り返すか。")]
    public int navMeshHoleFillIterations = 2;
    [Tooltip("穴埋め対象のセルについて、これ以上の数の隣接セル(8方向)が既に埋まっていないと埋めない。小さくするほど積極的に穴を埋める。")]
    public int minNavMeshHoleNeighborCount = 6;
    [Tooltip("向かい合う2セルの間に1セル分の隙間がある場合、その間を埋める。")]
    public bool mergeOneCellNavMeshGaps = true;
    [Tooltip("隙間埋め処理を何回繰り返すか。")]
    public int navMeshGapMergeIterations = 1;
    [Tooltip("穴埋め/隙間埋め/拡張で、隣接セルの高さ差がこれを超える場合は埋めない(段差を誤って埋めないため)。")]
    public float maxNavMeshMergeHeightDifference = 0.06f;
    [Tooltip("生成された平面の外周をさらに何セル分外側に広げるか。実際の点群より少し大きめに平面を取りたい場合に増やす。")]
    public int navMeshSurfaceExpansionIterations = 1;
    [Tooltip("拡張先のセルについて、これ以上の数の隣接セル(8方向)が既に埋まっていないと拡張しない。小さくするほど積極的に広げる。")]
    public int minNavMeshExpansionNeighborCount = 1;
    [HideInInspector]
    public int pointCloudNavMeshAgentTypeId = 0;
    [HideInInspector]
    public float pointCloudNavMeshVoxelSize = 0.03f;
    [HideInInspector]
    public float pointCloudNavMeshIgnoredAgentRadius = 0.01f;
    [HideInInspector]
    public float pointCloudNavMeshIgnoredAgentHeight = 0.05f;
    [HideInInspector]
    public float pointCloudNavMeshMinRegionArea = 0f;
    [HideInInspector]
    public string generatedPointCloudObjectName = "CoveredPointCloudObject";
    [HideInInspector]
    public PointCloudObjectQuality pointCloudObjectQuality = PointCloudObjectQuality.SmoothFine;
    [HideInInspector]
    public bool addPointCloudObjectCollider = true;
    [HideInInspector]
    public bool buildNavMeshAfterPointCloudObject = false;
    [HideInInspector]
    public Color generatedPointCloudObjectColor = new Color(0.95f, 0.72f, 0.18f, 0.65f);
    [HideInInspector]
    public float pointCloudObjectVoxelSize = 0.025f;
    [HideInInspector]
    public float pointCloudObjectSurfacePadding = 0.006f;
    [HideInInspector]
    public int minPointCloudObjectVoxelPoints = 1;
    [HideInInspector]
    public bool removeSparseVoxelNoise = true;
    [HideInInspector]
    public int minSparseVoxelNeighborCount = 1;
    [HideInInspector]
    public bool removeSmallVoxelClusters = true;
    [HideInInspector]
    public int minVoxelClusterSize = 20;
    [HideInInspector]
    public bool fillSmallVoxelGaps = false;
    [HideInInspector]
    public int minVoxelGapNeighborCount = 5;
    [HideInInspector]
    public int pointCloudObjectExpansionVoxels = 0;
    [HideInInspector]
    public int pointCloudObjectSmoothIterations = 3;
    [HideInInspector]
    public float pointCloudObjectSmoothStrength = 0.35f;

    // 保存先
    private string saveFolderPath;
    private const float PlaneEpsilon = 0.0001f;

    // 保存用
    private List<Vector3> savedVertices =
        new List<Vector3>();

    private List<Color32> savedColors =
        new List<Color32>();

    private bool isCapturing;
    private int lastCaptureRequestFrame = -1;
    private float nextAutoUpdateTime;

    // Kinectデバイスは毎回開閉すると不安定になりやすいため、CaptureOnce間で使い回す。
    private Device singleDevice;
    private readonly Dictionary<int, Device> multiDevicesByIndex = new Dictionary<int, Device>();

    void Awake()
    {
        // Assets/3DObject/KinectPCD
        saveFolderPath = Path.Combine(
            Application.dataPath,
            "3DObject",
            "KinectPCD");

        // フォルダ自動生成
        if (!Directory.Exists(saveFolderPath))
        {
            Directory.CreateDirectory(saveFolderPath);
        }
    }

    void OnEnable()
    {
        RegisterButtonEvents();
        ScheduleNextAutoUpdate();
    }

    void OnDisable()
    {
        UnregisterButtonEvents();
        CloseAllDevices();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            Debug.Log("PlaneFinder: R key triggered reload.");
            CaptureBuildNavMeshAndLinks();
        }

        if (!autoUpdateCaptureBuildNavMeshAndLinks)
        {
            ScheduleNextAutoUpdate();
            return;
        }

        if (Time.time < nextAutoUpdateTime)
            return;

        ScheduleNextAutoUpdate();

        if (isCapturing)
        {
            Debug.Log("PlaneFinder: auto update skipped because capture is already running.");
            return;
        }

        Debug.Log(
            $"PlaneFinder: auto update triggered. interval={Mathf.Max(autoUpdateIntervalSeconds, 1f):F1}s");
        CaptureBuildNavMeshAndLinks();
    }

    private void ScheduleNextAutoUpdate()
    {
        nextAutoUpdateTime =
            Time.time + Mathf.Max(autoUpdateIntervalSeconds, 1f);
    }

    private void RegisterButtonEvents()
    {
        if (capturePointCloudButton != null)
        {
            capturePointCloudButton.onClick.RemoveListener(CaptureOnce);
            capturePointCloudButton.onClick.AddListener(CaptureOnce);
        }

        if (buildPointCloudNavMeshButton != null)
        {
            buildPointCloudNavMeshButton.onClick.RemoveListener(BuildNavMeshFromSavedPointCloud);
            buildPointCloudNavMeshButton.onClick.AddListener(BuildNavMeshFromSavedPointCloud);
        }

        if (captureBuildNavMeshAndLinksButton != null)
        {
            captureBuildNavMeshAndLinksButton.onClick.RemoveListener(CaptureBuildNavMeshAndLinks);
            captureBuildNavMeshAndLinksButton.onClick.AddListener(CaptureBuildNavMeshAndLinks);
        }

        if (savePcdButton != null)
        {
            savePcdButton.onClick.RemoveListener(SavePCD);
            savePcdButton.onClick.AddListener(SavePCD);
        }
    }

    private void UnregisterButtonEvents()
    {
        if (capturePointCloudButton != null)
            capturePointCloudButton.onClick.RemoveListener(CaptureOnce);

        if (buildPointCloudNavMeshButton != null)
            buildPointCloudNavMeshButton.onClick.RemoveListener(BuildNavMeshFromSavedPointCloud);

        if (captureBuildNavMeshAndLinksButton != null)
            captureBuildNavMeshAndLinksButton.onClick.RemoveListener(CaptureBuildNavMeshAndLinks);

        if (savePcdButton != null)
            savePcdButton.onClick.RemoveListener(SavePCD);
    }

    // NavMesh/NavMeshLinkの生成が完了したタイミングで発火する。NavPub等の
    // MQTTパブリッシュ側はこのイベントを購読して最新のNavMeshを送信する。
    public static event System.Action OnNavMeshPipelineFinished;

    public void CaptureBuildNavMeshAndLinks()
    {
        if (isCapturing)
        {
            Debug.LogWarning("PlaneFinder: capture/build/link pipeline skipped because capture is already running.");
            return;
        }

        ClearPreviousPipelineOutput();

        Debug.Log("PlaneFinder: pipeline started. Step 1/3 CaptureOnce.");
        CaptureOnce();

        if (savedVertices == null ||
            savedVertices.Count == 0)
        {
            Debug.LogWarning("PlaneFinder: pipeline stopped. Captured point cloud is empty.");
            return;
        }

        Debug.Log($"PlaneFinder: pipeline Step 2/3 BuildNavMeshFromSavedPointCloud. points={savedVertices.Count}");
        BuildNavMeshFromSavedPointCloud();

        PointNavLink linkGenerator =
            pointNavLink;

        if (linkGenerator == null)
        {
            linkGenerator =
                FindObjectOfType<PointNavLink>();

            if (linkGenerator != null)
                pointNavLink = linkGenerator;
        }

        if (linkGenerator == null)
        {
            Debug.LogWarning("PlaneFinder: pipeline finished without NavMeshLinks. PointNavLink is not assigned and was not found in the scene.");
            OnNavMeshPipelineFinished?.Invoke();
            return;
        }

        Debug.Log($"PlaneFinder: pipeline Step 3/3 PointNavLink.GenerateLinks. generator={linkGenerator.name}");
        linkGenerator.GenerateLinks();

        Debug.Log(
            $"PlaneFinder: pipeline finished. points={savedVertices.Count}, navLinkSourceItems={linkGenerator.LastSourceCellCount}, clusters={linkGenerator.LastClusterCount}, generatedLinks={linkGenerator.LastGeneratedLinkCount}");

        OnNavMeshPipelineFinished?.Invoke();
    }

    private void ClearPreviousPipelineOutput()
    {
        Debug.Log("PlaneFinder: clearing previous point cloud, routes, generated NavMesh, and NavMeshLinks.");

        savedVertices.Clear();
        savedColors.Clear();
        ClearGeneratedRoutes();

        MeshFilter meshFilter =
            GetComponent<MeshFilter>();

        if (meshFilter != null)
            meshFilter.sharedMesh = null;

        MeshCollider meshCollider =
            GetComponent<MeshCollider>();

        if (meshCollider != null)
            meshCollider.sharedMesh = null;

        DestroyGeneratedChildIfExists(generatedNavMeshObjectName);
        DestroyGeneratedChildIfExists(generatedPointCloudObjectName);

        PointNavLink linkGenerator =
            pointNavLink != null
                ? pointNavLink
                : FindObjectOfType<PointNavLink>();

        if (linkGenerator != null)
        {
            pointNavLink = linkGenerator;
            linkGenerator.ClearGeneratedLinks();
        }
    }

    private void ClearGeneratedRoutes()
    {
        RouteSub[] routeSubs =
            FindObjectsOfType<RouteSub>();

        foreach (RouteSub routeSub in routeSubs)
            routeSub.ClearRoute();

        RouteTestSimple[] routeTests =
            FindObjectsOfType<RouteTestSimple>();

        foreach (RouteTestSimple routeTest in routeTests)
            routeTest.ClearRoute();

        if (routeSubs.Length > 0 ||
            routeTests.Length > 0)
        {
            Debug.Log(
                $"PlaneFinder: cleared routes before NavMesh rebuild. routeSubs={routeSubs.Length}, routeTestSimple={routeTests.Length}");
        }
    }

    private void DestroyGeneratedChildIfExists(
        string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
            return;

        Transform child =
            transform.Find(objectName);

        if (child == null)
            return;

        GeneratedNavMeshDataOwner[] owners =
            child.GetComponentsInChildren<GeneratedNavMeshDataOwner>(true);

        foreach (GeneratedNavMeshDataOwner owner in owners)
            owner.SetNavMeshData(null);

        DestroyImmediate(child.gameObject);
    }

    // =========================
    // 点群取得
    // =========================
    public void CaptureOnce()
    {
        if (isCapturing)
            return;

        if (lastCaptureRequestFrame == Time.frameCount)
        {
            Debug.LogWarning(
                "PlaneFinder: CaptureOnce was called more than once in the same frame. Ignored duplicate call.");
            return;
        }

        lastCaptureRequestFrame = Time.frameCount;
        isCapturing = true;

        try
        {
            if (useMultipleKinects &&
                kinectSources != null &&
                kinectSources.Length > 0)
            {
                CaptureFromMultipleKinects();
                return;
            }

            CaptureFromSingleKinect();
        }
        finally
        {
            isCapturing = false;
        }
    }

    private void CaptureFromSingleKinect()
    {
        Device dev = EnsureSingleDeviceOpen();

        if (dev == null)
            return;

        try
        {
            // カメラは撮影の直前だけ回し、撮影後は必ず止める。デバイスハンドル自体は
            // 使い回すが、ストリーミングを常時回しっぱなしにはしない(マルチKinect構成で
            // 複数台のIR投光が同時に干渉して深度品質が落ちるのを防ぐため)。
            dev.StartCameras(CreateKinectDeviceConfiguration());

            try
            {
            using (Capture cap = dev.GetCapture())
            {
                Image depth = cap.Depth;
                Image color = cap.Color;

                Calibration calib = dev.GetCalibration();

                using (Transformation trans = calib.CreateTransformation())
                using (Image pointCloudImage =
                       trans.DepthImageToPointCloud(depth))
                {
                    using (Image transformedColor =
                        new Image(
                            ImageFormat.ColorBGRA32,
                            depth.WidthPixels,
                            depth.HeightPixels,
                            depth.WidthPixels * 4))
                    {
                        trans.ColorImageToDepthCamera(
                            depth,
                            color,
                            transformedColor);

                        var pointMem =
                            pointCloudImage.GetPixels<Short3>();

                        var points = pointMem.Span;

                        byte[] colorBytes =
                            transformedColor.Memory.ToArray();

                        List<Vector3> vertices =
                            new List<Vector3>();

                        List<Color32> colors =
                            new List<Color32>();

                        for (int i = 0; i < points.Length; i += stride)
                        {
                            Short3 p = points[i];

                            if (p.Z <= 0)
                                continue;

                            Vector3 v = new Vector3(
                                p.X / 1000f,
                                -p.Y / 1000f,
                                p.Z / 1000f
                            );

                            vertices.Add(v);

                            int ci = i * 4;

                            if (ci + 3 < colorBytes.Length)
                            {
                                byte b = colorBytes[ci + 0];
                                byte g = colorBytes[ci + 1];
                                byte r = colorBytes[ci + 2];
                                byte a = colorBytes[ci + 3];

                                colors.Add(
                                    new Color32(r, g, b, a));
                            }
                            else
                            {
                                colors.Add(Color.white);
                            }
                        }

                        // 保存用に保持
                        savedVertices = vertices;
                        savedColors = colors;

                        CreateMesh(vertices, colors);

                        Debug.Log(
                            $"PointCloud Captured : {vertices.Count} points");
                    }
                }
            }
            }
            finally
            {
                try { dev.StopCameras(); }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"PlaneFinder: StopCameras failed. {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError(
                $"PlaneFinder: Kinect capture failed. {ex.GetType().Name}: {ex.Message}");

            // 取得中に例外が起きた場合はデバイスが不安定な状態になっている可能性があるため、
            // 一旦閉じて次回のCaptureOnceで開き直す。
            CloseSingleDevice();
        }
    }

    // Kinectデバイスハンドル自体(Device.Open/Dispose)は毎回作り直すと不安定になりやすいので
    // 使い回す。ただしカメラストリーミング(StartCameras/StopCameras)は撮影のたびに
    // 開始/停止する(常時ストリーミングにすると、マルチKinect構成で複数台のIR投光が
    // 同時に干渉して深度品質が落ちるため)。
    private Device EnsureSingleDeviceOpen()
    {
        if (singleDevice != null)
            return singleDevice;

        try
        {
            int installedCount =
                Device.GetInstalledCount();

            if (installedCount <= deviceIndex)
            {
                Debug.LogError(
                    $"PlaneFinder: Kinect deviceIndex {deviceIndex} is not available. Installed devices: {installedCount}");
                return null;
            }

            singleDevice = Device.Open(deviceIndex);
            Debug.Log($"PlaneFinder: opened Kinect device {deviceIndex} (kept open for reuse).");
            return singleDevice;
        }
        catch (System.Exception ex)
        {
            Debug.LogError(
                $"PlaneFinder: failed to open Kinect device {deviceIndex}. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static DeviceConfiguration CreateKinectDeviceConfiguration()
    {
        return new DeviceConfiguration
        {
            ColorFormat = ImageFormat.ColorBGRA32,
            ColorResolution = ColorResolution.R720p,
            DepthMode = DepthMode.NFOV_2x2Binned,
            CameraFPS = FPS.FPS30,
            SynchronizedImagesOnly = true
        };
    }

    private void CloseSingleDevice()
    {
        if (singleDevice == null)
            return;

        try { singleDevice.StopCameras(); }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"PlaneFinder: StopCameras failed. {ex.GetType().Name}: {ex.Message}");
        }

        singleDevice.Dispose();
        singleDevice = null;
    }

    // =========================
    // PCD保存
    // =========================
    // =========================
    // Multi Kinect capture
    // =========================
    private void CaptureFromMultipleKinects()
    {
        int installedCount =
            Device.GetInstalledCount();
        List<Vector3> vertices =
            new List<Vector3>();
        List<Color32> colors =
            new List<Color32>();
        int activeSourceCount =
            0;

        foreach (KinectCaptureSource source in kinectSources)
        {
            if (source == null ||
                !source.enabled)
            {
                continue;
            }

            activeSourceCount++;

            CapturePointCloudFromDevice(
                source.deviceIndex,
                source.kinectTransform,
                installedCount,
                vertices,
                colors);
        }

        if (activeSourceCount == 0)
        {
            Debug.LogWarning(
                "PlaneFinder: multi Kinect capture has no enabled sources.");
        }

        savedVertices = vertices;
        savedColors = colors;

        CreateMesh(vertices, colors);

        Debug.Log(
            $"PointCloud Captured : {vertices.Count} points from {activeSourceCount} Kinect sources");
    }

    private void CapturePointCloudFromDevice(
        int sourceDeviceIndex,
        Transform sourceTransform,
        int installedCount,
        List<Vector3> vertices,
        List<Color32> colors)
    {
        if (sourceDeviceIndex < 0 ||
            installedCount <= sourceDeviceIndex)
        {
            Debug.LogError(
                $"PlaneFinder: Kinect deviceIndex {sourceDeviceIndex} is not available. Installed devices: {installedCount}");
            return;
        }

        Device dev = EnsureMultiDeviceOpen(sourceDeviceIndex);

        if (dev == null)
            return;

        int beforeCount =
            vertices.Count;

        try
        {
            // カメラは撮影の直前だけ回し、撮影後は必ず止める(複数台のIR投光が同時に
            // 干渉して深度品質が落ちるのを防ぐため、常時ストリーミングにはしない)。
            dev.StartCameras(CreateKinectDeviceConfiguration());

            try
            {
            using (Capture cap = dev.GetCapture())
            {
                Image depth = cap.Depth;
                Image color = cap.Color;

                Calibration calib = dev.GetCalibration();

                using (Transformation trans = calib.CreateTransformation())
                using (Image pointCloudImage =
                       trans.DepthImageToPointCloud(depth))
                {
                    using (Image transformedColor =
                        new Image(
                            ImageFormat.ColorBGRA32,
                            depth.WidthPixels,
                            depth.HeightPixels,
                            depth.WidthPixels * 4))
                    {
                        trans.ColorImageToDepthCamera(
                            depth,
                            color,
                            transformedColor);

                        var pointMem =
                            pointCloudImage.GetPixels<Short3>();
                        var points =
                            pointMem.Span;
                        byte[] colorBytes =
                            transformedColor.Memory.ToArray();

                        for (int i = 0; i < points.Length; i += stride)
                        {
                            Short3 p = points[i];

                            if (p.Z <= 0)
                                continue;

                            Vector3 v =
                                new Vector3(
                                    p.X / 1000f,
                                    -p.Y / 1000f,
                                    p.Z / 1000f);

                            vertices.Add(
                                TransformCapturedPointToLocal(
                                    v,
                                    sourceTransform));

                            int ci = i * 4;

                            if (ci + 3 < colorBytes.Length)
                            {
                                byte b = colorBytes[ci + 0];
                                byte g = colorBytes[ci + 1];
                                byte r = colorBytes[ci + 2];
                                byte a = colorBytes[ci + 3];

                                colors.Add(
                                    new Color32(r, g, b, a));
                            }
                            else
                            {
                                colors.Add(Color.white);
                            }
                        }
                    }
                }
            }
            }
            finally
            {
                try { dev.StopCameras(); }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"PlaneFinder: StopCameras failed. {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning(
                $"PlaneFinder: Kinect {sourceDeviceIndex} capture failed. {ex.GetType().Name}: {ex.Message}");

            // 取得中に例外が起きた場合はデバイスが不安定な状態になっている可能性があるため、
            // 一旦閉じて次回のCaptureOnceで開き直す。
            CloseMultiDevice(sourceDeviceIndex);
        }

        Debug.Log(
            $"PlaneFinder: Kinect {sourceDeviceIndex} captured {vertices.Count - beforeCount} points.");
    }

    // Kinectデバイスハンドル自体(Device.Open/Dispose)は毎回作り直すと不安定になりやすいので
    // 使い回す。ただしカメラストリーミング(StartCameras/StopCameras)は撮影のたびに
    // 開始/停止する(複数台を常時同時ストリーミングさせるとIR投光が干渉するため)。
    private Device EnsureMultiDeviceOpen(int index)
    {
        if (multiDevicesByIndex.TryGetValue(index, out Device existing) && existing != null)
            return existing;

        try
        {
            Device dev = Device.Open(index);

            multiDevicesByIndex[index] = dev;
            Debug.Log($"PlaneFinder: opened Kinect device {index} (kept open for reuse).");
            return dev;
        }
        catch (System.Exception ex)
        {
            Debug.LogError(
                $"PlaneFinder: failed to open Kinect device {index}. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void CloseMultiDevice(int index)
    {
        if (!multiDevicesByIndex.TryGetValue(index, out Device dev) || dev == null)
            return;

        try { dev.StopCameras(); }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"PlaneFinder: StopCameras failed. {ex.GetType().Name}: {ex.Message}");
        }

        dev.Dispose();
        multiDevicesByIndex.Remove(index);
    }

    private void CloseAllDevices()
    {
        CloseSingleDevice();

        foreach (Device dev in multiDevicesByIndex.Values)
        {
            if (dev == null)
                continue;

            try { dev.StopCameras(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"PlaneFinder: StopCameras failed. {ex.GetType().Name}: {ex.Message}");
            }

            dev.Dispose();
        }

        multiDevicesByIndex.Clear();
    }

    private Vector3 TransformCapturedPointToLocal(
        Vector3 point,
        Transform sourceTransform)
    {
        if (sourceTransform == null)
            return point;

        Vector3 worldPoint =
            sourceTransform.TransformPoint(point);

        return transform.InverseTransformPoint(worldPoint);
    }

    // =========================
    // PCD save
    // =========================
    public void SavePCD()
    {
        if (savedVertices == null ||
            savedVertices.Count == 0)
        {
            Debug.LogWarning(
                "保存する点群がありません。先にCaptureOnceしてください。");
            return;
        }

        string path =
            Path.Combine(
                saveFolderPath,
                saveFileName);

        StringBuilder sb = new StringBuilder();

        // =========================
        // PCD Header
        // =========================
        sb.AppendLine("# .PCD v0.7 - Point Cloud Data file format");
        sb.AppendLine("VERSION 0.7");
        sb.AppendLine("FIELDS x y z rgb");
        sb.AppendLine("SIZE 4 4 4 4");
        sb.AppendLine("TYPE F F F U");
        sb.AppendLine("COUNT 1 1 1 1");
        sb.AppendLine($"WIDTH {savedVertices.Count}");
        sb.AppendLine("HEIGHT 1");
        sb.AppendLine("VIEWPOINT 0 0 0 1 0 0 0");
        sb.AppendLine($"POINTS {savedVertices.Count}");
        sb.AppendLine("DATA ascii");

        // =========================
        // Point Data
        // =========================
        for (int i = 0; i < savedVertices.Count; i++)
        {
            Vector3 p = savedVertices[i];
            Color32 c = savedColors[i];

            uint rgb =
                ((uint)c.r << 16) |
                ((uint)c.g << 8) |
                c.b;

            sb.AppendLine(
                $"{p.x} {p.y} {p.z} {rgb}");
        }

        File.WriteAllText(path, sb.ToString());

        Debug.Log($"PCD Saved : {path}");
    }

    // =========================
    // Mesh生成
    // =========================
    void CreateMesh(
        List<Vector3> pts,
        List<Color32> cols)
    {
        Mesh mesh = new Mesh();

        mesh.indexFormat =
            UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.vertices = pts.ToArray();

        mesh.colors32 = cols.ToArray();

        int[] indices = new int[pts.Count];

        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        mesh.SetIndices(
            indices,
            MeshTopology.Points,
            0);

        GetComponent<MeshFilter>().mesh = mesh;

        Material mat = new Material(
            Shader.Find("Sprites/Default"));

        GetComponent<MeshRenderer>().material = mat;
    }

    // =========================
    // 平面検出
    // =========================
    public void GeneratePlaneFromSavedPoints()
    {
        if (savedVertices == null ||
            savedVertices.Count == 0)
        {
            Debug.LogWarning(
                "平面検出する点群がありません。先にCaptureOnceしてください。");
            return;
        }

        FindAndCreatePlane(savedVertices);
    }

    public void FindPlaneFromSavedPoints()
    {
        GeneratePlaneFromSavedPoints();
    }

    public void GenerateWalkableSurfaceFromSavedPoints()
    {
        GeneratePointCloudObjectFromSavedPoints();
    }

    private void GenerateWalkableSurfaceLegacyFromSavedPoints()
    {
        if (savedVertices == null ||
            savedVertices.Count == 0)
        {
            Debug.LogWarning(
                "歩行可能サーフェスを生成する点群がありません。先にCaptureOnceしてください。");
            return;
        }

        if (!TryCreateWalkableSurface(savedVertices, out int vertexCount, out int triangleCount))
        {
            Debug.LogWarning(
                "PlaneFinder: 歩行可能サーフェスを生成できませんでした。walkableCellSize / minWalkableCellPoints / maxCellHeightRange を調整してください。");
            return;
        }

        Debug.Log(
            $"PlaneFinder: walkable surface generated. vertices={vertexCount}, triangles={triangleCount}");
    }

    public void GeneratePointCloudObjectFromSavedPoints()
    {
        bool buildNavMeshOnly = true;

        if (buildNavMeshOnly)
        {
            BuildNavMeshFromSavedPointCloud();
            return;
        }

        if (savedVertices == null ||
            savedVertices.Count == 0)
        {
            Debug.LogWarning(
                "PlaneFinder: 点群オブジェクトを生成する点群がありません。先にCaptureOnceしてください。");
            return;
        }

        if (!TryCreatePointCloudObject(savedVertices, out int vertexCount, out int triangleCount, out int voxelCount))
        {
            Debug.LogWarning(
                "PlaneFinder: 点群オブジェクトを生成できませんでした。pointCloudObjectVoxelSize / minPointCloudObjectVoxelPoints を調整してください。");
            return;
        }

        Debug.Log(
            $"PlaneFinder: point cloud object generated. voxels={voxelCount}, vertices={vertexCount}, triangles={triangleCount}");
    }

    public void BuildNavMeshFromSavedPointCloud()
    {
        if (savedVertices == null ||
            savedVertices.Count == 0)
        {
            Debug.LogWarning(
                "PlaneFinder: NavMeshを生成する点群がありません。先にCaptureOnceしてください。");
            return;
        }

        List<Vector3> pointsForNavMesh = savedVertices;

        if (useAuxiliaryFloorPoints &&
            auxiliaryFloorObjects != null &&
            auxiliaryFloorObjects.Count > 0)
        {
            pointsForNavMesh =
                BuildPointsWithAuxiliaryFloorPriority(
                    savedVertices,
                    out int addedCount,
                    out int removedCount);

            if (addedCount > 0 ||
                removedCount > 0)
            {
                Debug.Log(
                    $"PlaneFinder: auxiliary floor override applied. addedPoints={addedCount}, removedPoints={removedCount}, objects={auxiliaryFloorObjects.Count}.");
            }
        }

        if (!TryBuildNavMeshFromPointCloud(pointsForNavMesh, out int vertexCount, out int triangleCount, out int cellCount))
        {
            Debug.LogWarning(
                "PlaneFinder: NavMeshを生成できませんでした。点群の密度や対象範囲を確認してください。");
            return;
        }

        Debug.Log(
            $"PlaneFinder: point cloud NavMesh generated. cells={cellCount}, vertices={vertexCount}, triangles={triangleCount}");
    }

    private void FindAndCreatePlane(List<Vector3> points)
    {
        if (!TryFindPlane(points, out PlaneResult plane))
        {
            Debug.LogWarning(
                "PlaneFinder: 平面を検出できませんでした。threshold / minPlanePoints / minUpDot を調整してください。");
            return;
        }

        CreateCleanPlaneMesh(plane, points);

        Debug.Log(
            $"PlaneFinder: plane found. points={plane.Inliers.Count}, normal={plane.Normal}, distanceThreshold={planeDistanceThreshold:F3}");
    }

    private bool TryFindPlane(
        List<Vector3> points,
        out PlaneResult bestPlane)
    {
        bestPlane = new PlaneResult();

        if (points == null ||
            points.Count < 3)
            return false;

        int requiredPoints =
            Mathf.Max(
                minPlanePoints,
                Mathf.CeilToInt(points.Count * minPlanePointRatio));

        System.Random random = new System.Random();

        for (int i = 0; i < ransacIterations; i++)
        {
            Vector3 a = points[random.Next(points.Count)];
            Vector3 b = points[random.Next(points.Count)];
            Vector3 c = points[random.Next(points.Count)];

            if (!TryCreatePlane(a, b, c, out Vector3 normal, out float distance))
                continue;

            if (requireWalkablePlane &&
                Vector3.Dot(GetWorldNormal(normal), Vector3.up) < minUpDot)
                continue;

            List<int> inliers = new List<int>();

            for (int p = 0; p < points.Count; p++)
            {
                float pointDistance =
                    Mathf.Abs(Vector3.Dot(normal, points[p]) + distance);

                if (pointDistance <= planeDistanceThreshold)
                    inliers.Add(p);
            }

            if (inliers.Count <= bestPlane.InlierCount)
                continue;

            bestPlane =
                new PlaneResult(normal, distance, inliers);
        }

        if (bestPlane.InlierCount < requiredPoints)
            return false;

        RefinePlaneWithInliers(points, ref bestPlane);
        return true;
    }

    private bool TryCreatePlane(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out Vector3 normal,
        out float distance)
    {
        normal = Vector3.Cross(b - a, c - a);

        if (normal.sqrMagnitude < PlaneEpsilon)
        {
            distance = 0f;
            return false;
        }

        normal.Normalize();

        if (Vector3.Dot(normal, Vector3.up) < 0f)
            normal = -normal;

        distance = -Vector3.Dot(normal, a);
        return true;
    }

    private void RefinePlaneWithInliers(
        List<Vector3> points,
        ref PlaneResult plane)
    {
        if (plane.Inliers == null ||
            plane.Inliers.Count < 3)
            return;

        Vector3 centroid =
            CalculateCentroid(points, plane.Inliers);

        Vector3 normal =
            FitPlaneNormal(points, plane.Inliers, centroid);

        if (Vector3.Dot(normal, plane.Normal) < 0f)
            normal = -normal;

        if (Vector3.Dot(GetWorldNormal(normal), Vector3.up) < 0f)
            normal = -normal;

        if (ShouldSnapToLevel(normal))
        {
            normal =
                transform.InverseTransformDirection(Vector3.up).normalized;
        }

        float averageDistance =
            Vector3.Dot(normal, centroid);

        plane =
            new PlaneResult(
                normal,
                -averageDistance,
                plane.Inliers);
    }

    private Vector3 CalculateCentroid(
        List<Vector3> points,
        List<int> indices)
    {
        Vector3 sum =
            Vector3.zero;

        foreach (int index in indices)
        {
            sum += points[index];
        }

        return sum / indices.Count;
    }

    private Vector3 FitPlaneNormal(
        List<Vector3> points,
        List<int> indices,
        Vector3 centroid)
    {
        float xx = 0f;
        float xy = 0f;
        float xz = 0f;
        float yy = 0f;
        float yz = 0f;
        float zz = 0f;

        foreach (int index in indices)
        {
            Vector3 p =
                points[index] - centroid;

            xx += p.x * p.x;
            xy += p.x * p.y;
            xz += p.x * p.z;
            yy += p.y * p.y;
            yz += p.y * p.z;
            zz += p.z * p.z;
        }

        Vector3 axisA =
            PowerIteration(xx, xy, xz, yy, yz, zz, Vector3.right);

        Deflate(
            ref xx,
            ref xy,
            ref xz,
            ref yy,
            ref yz,
            ref zz,
            axisA);

        Vector3 secondAxisSeed =
            Vector3.Cross(axisA, Vector3.up);

        if (secondAxisSeed.sqrMagnitude < PlaneEpsilon)
            secondAxisSeed = Vector3.Cross(axisA, Vector3.right);

        Vector3 axisB =
            PowerIteration(xx, xy, xz, yy, yz, zz, secondAxisSeed);

        Vector3 normal =
            Vector3.Cross(axisA, axisB);

        if (normal.sqrMagnitude < PlaneEpsilon)
            return Vector3.up;

        return normal.normalized;
    }

    private Vector3 PowerIteration(
        float xx,
        float xy,
        float xz,
        float yy,
        float yz,
        float zz,
        Vector3 initial)
    {
        Vector3 v =
            initial.normalized;

        for (int i = 0; i < 12; i++)
        {
            v =
                new Vector3(
                    xx * v.x + xy * v.y + xz * v.z,
                    xy * v.x + yy * v.y + yz * v.z,
                    xz * v.x + yz * v.y + zz * v.z);

            if (v.sqrMagnitude < PlaneEpsilon)
                return initial.normalized;

            v.Normalize();
        }

        return v;
    }

    private void Deflate(
        ref float xx,
        ref float xy,
        ref float xz,
        ref float yy,
        ref float yz,
        ref float zz,
        Vector3 axis)
    {
        Vector3 av =
            new Vector3(
                xx * axis.x + xy * axis.y + xz * axis.z,
                xy * axis.x + yy * axis.y + yz * axis.z,
                xz * axis.x + yz * axis.y + zz * axis.z);
        float eigenvalue =
            Vector3.Dot(axis, av);

        xx -= eigenvalue * axis.x * axis.x;
        xy -= eigenvalue * axis.x * axis.y;
        xz -= eigenvalue * axis.x * axis.z;
        yy -= eigenvalue * axis.y * axis.y;
        yz -= eigenvalue * axis.y * axis.z;
        zz -= eigenvalue * axis.z * axis.z;
    }

    private bool ShouldSnapToLevel(
        Vector3 localNormal)
    {
        if (!snapWalkablePlaneToLevel)
            return false;

        Vector3 worldNormal =
            GetWorldNormal(localNormal);
        float angle =
            Vector3.Angle(worldNormal, Vector3.up);

        return angle <= Mathf.Max(levelSnapMaxAngle, 0f);
    }

    private Vector3 GetWorldNormal(
        Vector3 localNormal)
    {
        return transform.TransformDirection(localNormal).normalized;
    }

    private bool TryCreateWalkableSurface(
        List<Vector3> points,
        out int vertexCount,
        out int triangleCount)
    {
        vertexCount = 0;
        triangleCount = 0;

        float cellSize =
            Mathf.Max(walkableCellSize, 0.01f);
        Dictionary<Vector2Int, SurfaceCell> cells =
            BuildSurfaceCells(points, cellSize);
        Dictionary<Vector2Int, float> heights =
            BuildValidSurfaceHeights(cells);
        int initialValidCellCount =
            heights.Count;

        if (heights.Count < 4)
        {
            Debug.LogWarning(
                $"PlaneFinder: walkable cells are too few. sourceCells={cells.Count}, validCells={heights.Count}");
            return false;
        }

        FillWalkableHoles(heights);
        int holeFilledCellCount =
            heights.Count;
        ExpandWalkableSurface(heights);
        SmoothSurfaceHeights(heights);

        Mesh mesh =
            BuildWalkableSurfaceMesh(heights, cellSize);

        if (mesh == null ||
            mesh.vertexCount == 0)
        {
            Debug.LogWarning(
                $"PlaneFinder: walkable mesh has no triangles. sourceCells={cells.Count}, initialValidCells={initialValidCellCount}, filledCells={holeFilledCellCount}, finalCells={heights.Count}");
            return false;
        }

        vertexCount = mesh.vertexCount;
        triangleCount = mesh.triangles.Length / 3;

        GameObject surfaceObject =
            GetOrCreateGeneratedChild(generatedWalkableSurfaceName);

        MeshFilter meshFilter =
            surfaceObject.GetComponent<MeshFilter>();

        if (meshFilter == null)
            meshFilter = surfaceObject.AddComponent<MeshFilter>();

        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer =
            surfaceObject.GetComponent<MeshRenderer>();

        if (meshRenderer == null)
            meshRenderer = surfaceObject.AddComponent<MeshRenderer>();

        meshRenderer.sharedMaterial =
            CreateTransparentMaterial(generatedWalkableSurfaceColor);

        if (addWalkableSurfaceCollider)
        {
            MeshCollider meshCollider =
                surfaceObject.GetComponent<MeshCollider>();

            if (meshCollider == null)
                meshCollider = surfaceObject.AddComponent<MeshCollider>();

            meshCollider.sharedMesh = mesh;
        }

        Debug.Log(
            $"PlaneFinder: walkable cells source={cells.Count}, initialValid={initialValidCellCount}, filled={holeFilledCellCount}, final={heights.Count}");

        return true;
    }

    private Dictionary<Vector2Int, SurfaceCell> BuildSurfaceCells(
        List<Vector3> points,
        float cellSize)
    {
        Dictionary<Vector2Int, SurfaceCell> cells =
            new Dictionary<Vector2Int, SurfaceCell>();

        foreach (Vector3 point in points)
        {
            Vector2Int key =
                new Vector2Int(
                    Mathf.FloorToInt(point.x / cellSize),
                    Mathf.FloorToInt(point.z / cellSize));

            if (!cells.TryGetValue(key, out SurfaceCell cell))
            {
                cell = new SurfaceCell();
                cells.Add(key, cell);
            }

            cell.Add(point.y);
        }

        return cells;
    }

    private Dictionary<Vector2Int, float> BuildValidSurfaceHeights(
        Dictionary<Vector2Int, SurfaceCell> cells)
    {
        Dictionary<Vector2Int, float> heights =
            new Dictionary<Vector2Int, float>();

        foreach (KeyValuePair<Vector2Int, SurfaceCell> pair in cells)
        {
            SurfaceCell cell =
                pair.Value;

            if (cell.Count < minWalkableCellPoints)
                continue;

            if (filterWalkableCellHeightRange &&
                cell.HeightRange > maxCellHeightRange)
                continue;

            heights.Add(
                pair.Key,
                useUpperWalkableSurface ? cell.MaxY : cell.AverageY);
        }

        return heights;
    }

    private void FillWalkableHoles(
        Dictionary<Vector2Int, float> heights)
    {
        int iterations =
            Mathf.Max(walkableHoleFillIterations, 0);

        for (int i = 0; i < iterations; i++)
        {
            Dictionary<Vector2Int, float> additions =
                new Dictionary<Vector2Int, float>();
            HashSet<Vector2Int> candidates =
                new HashSet<Vector2Int>();

            foreach (Vector2Int key in heights.Keys)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 &&
                            dz == 0)
                            continue;

                        Vector2Int candidate =
                            new Vector2Int(
                                key.x + dx,
                                key.y + dz);

                        if (!heights.ContainsKey(candidate))
                            candidates.Add(candidate);
                    }
                }
            }

            foreach (Vector2Int candidate in candidates)
            {
                if (TryEstimateHoleHeight(
                    heights,
                    candidate,
                    out float height))
                {
                    additions.Add(candidate, height);
                }
            }

            if (additions.Count == 0)
                break;

            foreach (KeyValuePair<Vector2Int, float> addition in additions)
            {
                heights.Add(addition.Key, addition.Value);
            }
        }
    }

    private bool TryEstimateHoleHeight(
        Dictionary<Vector2Int, float> heights,
        Vector2Int candidate,
        out float height)
    {
        height = 0f;
        float sum =
            0f;
        int count =
            0;
        float minHeight =
            float.PositiveInfinity;
        float maxHeight =
            float.NegativeInfinity;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 &&
                    dz == 0)
                    continue;

                Vector2Int neighbor =
                    new Vector2Int(
                        candidate.x + dx,
                        candidate.y + dz);

                if (!heights.TryGetValue(neighbor, out float neighborHeight))
                    continue;

                sum += neighborHeight;
                count++;
                minHeight = Mathf.Min(minHeight, neighborHeight);
                maxHeight = Mathf.Max(maxHeight, neighborHeight);
            }
        }

        if (count < Mathf.Max(minWalkableHoleNeighborCount, 1))
            return false;

        if (maxHeight - minHeight > maxNeighborHeightStep)
            return false;

        height = sum / count;
        return true;
    }

    private void ExpandWalkableSurface(
        Dictionary<Vector2Int, float> heights)
    {
        int iterations =
            Mathf.Max(walkableSurfaceExpansionIterations, 0);

        for (int i = 0; i < iterations; i++)
        {
            Dictionary<Vector2Int, float> additions =
                new Dictionary<Vector2Int, float>();
            HashSet<Vector2Int> candidates =
                new HashSet<Vector2Int>();

            foreach (Vector2Int key in heights.Keys)
            {
                foreach (Vector2Int neighbor in GetAllNeighbors(key))
                {
                    if (!heights.ContainsKey(neighbor))
                        candidates.Add(neighbor);
                }
            }

            foreach (Vector2Int candidate in candidates)
            {
                if (TryEstimateExpansionHeight(
                    heights,
                    candidate,
                    out float height))
                {
                    additions.Add(candidate, height);
                }
            }

            if (additions.Count == 0)
                break;

            foreach (KeyValuePair<Vector2Int, float> addition in additions)
            {
                heights.Add(addition.Key, addition.Value);
            }
        }
    }

    private bool TryEstimateExpansionHeight(
        Dictionary<Vector2Int, float> heights,
        Vector2Int candidate,
        out float height)
    {
        height = 0f;
        float sum =
            0f;
        int count =
            0;
        float minHeight =
            float.PositiveInfinity;
        float maxHeight =
            float.NegativeInfinity;

        foreach (Vector2Int neighbor in GetAllNeighbors(candidate))
        {
            if (!heights.TryGetValue(neighbor, out float neighborHeight))
                continue;

            sum += neighborHeight;
            count++;
            minHeight = Mathf.Min(minHeight, neighborHeight);
            maxHeight = Mathf.Max(maxHeight, neighborHeight);
        }

        if (count < Mathf.Max(minWalkableExpansionNeighborCount, 1))
            return false;

        if (maxHeight - minHeight > maxNeighborHeightStep)
            return false;

        height = sum / count;
        return true;
    }

    private void SmoothSurfaceHeights(
        Dictionary<Vector2Int, float> heights)
    {
        int iterations =
            Mathf.Max(walkableSmoothingIterations, 0);

        for (int i = 0; i < iterations; i++)
        {
            Dictionary<Vector2Int, float> smoothed =
                new Dictionary<Vector2Int, float>();

            foreach (KeyValuePair<Vector2Int, float> pair in heights)
            {
                float sum =
                    pair.Value;
                int count =
                    1;

                foreach (Vector2Int neighbor in GetCardinalNeighbors(pair.Key))
                {
                    if (!heights.TryGetValue(neighbor, out float neighborHeight))
                        continue;

                    if (Mathf.Abs(neighborHeight - pair.Value) > maxNeighborHeightStep)
                        continue;

                    sum += neighborHeight;
                    count++;
                }

                smoothed.Add(pair.Key, sum / count);
            }

            heights.Clear();

            foreach (KeyValuePair<Vector2Int, float> pair in smoothed)
            {
                heights.Add(pair.Key, pair.Value);
            }
        }
    }

    private Mesh BuildWalkableSurfaceMesh(
        Dictionary<Vector2Int, float> heights,
        float cellSize)
    {
        List<Vector3> vertices =
            new List<Vector3>();
        List<int> triangles =
            new List<int>();

        foreach (KeyValuePair<Vector2Int, float> pair in heights)
        {
            AddWalkableCellQuad(
                heights,
                vertices,
                triangles,
                pair.Key,
                cellSize);
        }

        if (vertices.Count == 0 ||
            triangles.Count == 0)
            return null;

        Mesh mesh =
            new Mesh
            {
                name = generatedWalkableSurfaceName + "Mesh",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray()
            };

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    private void AddWalkableCellQuad(
        Dictionary<Vector2Int, float> heights,
        List<Vector3> vertices,
        List<int> triangles,
        Vector2Int key,
        float cellSize)
    {
        if (!heights.TryGetValue(key, out float centerHeight))
            return;

        float x0 =
            key.x * cellSize;
        float x1 =
            (key.x + 1) * cellSize;
        float z0 =
            key.y * cellSize;
        float z1 =
            (key.y + 1) * cellSize;
        float y00 =
            GetWalkableCornerHeight(
                heights,
                key,
                centerHeight,
                false,
                false);
        float y10 =
            GetWalkableCornerHeight(
                heights,
                key,
                centerHeight,
                true,
                false);
        float y01 =
            GetWalkableCornerHeight(
                heights,
                key,
                centerHeight,
                false,
                true);
        float y11 =
            GetWalkableCornerHeight(
                heights,
                key,
                centerHeight,
                true,
                true);

        int v0 =
            vertices.Count;
        vertices.Add(new Vector3(x0, y00, z0));
        vertices.Add(new Vector3(x1, y10, z0));
        vertices.Add(new Vector3(x0, y01, z1));
        vertices.Add(new Vector3(x1, y11, z1));

        int v1 =
            v0 + 1;
        int v2 =
            v0 + 2;
        int v3 =
            v0 + 3;

        triangles.Add(v0);
        triangles.Add(v2);
        triangles.Add(v1);
        triangles.Add(v1);
        triangles.Add(v2);
        triangles.Add(v3);

        if (!doubleSidedWalkableSurface)
            return;

        triangles.Add(v0);
        triangles.Add(v1);
        triangles.Add(v2);
        triangles.Add(v1);
        triangles.Add(v3);
        triangles.Add(v2);
    }

    private float GetWalkableCornerHeight(
        Dictionary<Vector2Int, float> heights,
        Vector2Int cell,
        float centerHeight,
        bool positiveX,
        bool positiveZ)
    {
        if (!averageWalkableCornerHeights)
            return centerHeight;

        float sum =
            0f;
        int count =
            0;

        int minDx =
            positiveX ? 0 : -1;
        int maxDx =
            positiveX ? 1 : 0;
        int minDz =
            positiveZ ? 0 : -1;
        int maxDz =
            positiveZ ? 1 : 0;

        for (int dx = minDx; dx <= maxDx; dx++)
        {
            for (int dz = minDz; dz <= maxDz; dz++)
            {
                Vector2Int neighbor =
                    new Vector2Int(
                        cell.x + dx,
                        cell.y + dz);

                if (!heights.TryGetValue(neighbor, out float neighborHeight))
                    continue;

                if (Mathf.Abs(neighborHeight - centerHeight) > maxNeighborHeightStep)
                    continue;

                sum += neighborHeight;
                count++;
            }
        }

        if (count == 0)
            return centerHeight;

        return sum / count;
    }

    private IEnumerable<Vector2Int> GetCardinalNeighbors(
        Vector2Int key)
    {
        yield return new Vector2Int(key.x - 1, key.y);
        yield return new Vector2Int(key.x + 1, key.y);
        yield return new Vector2Int(key.x, key.y - 1);
        yield return new Vector2Int(key.x, key.y + 1);
    }

    private IEnumerable<Vector2Int> GetAllNeighbors(
        Vector2Int key)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 &&
                    dz == 0)
                    continue;

                yield return new Vector2Int(
                    key.x + dx,
                    key.y + dz);
            }
        }
    }

    private bool TryBuildNavMeshFromPointCloud(
        List<Vector3> points,
        out int vertexCount,
        out int triangleCount,
        out int cellCount)
    {
        vertexCount = 0;
        triangleCount = 0;
        cellCount = 0;

        if (!TryGetBaseHorizontalFrame(
            points,
            out Vector3 horizontalNormal,
            out Vector3 axisU,
            out Vector3 axisV))
        {
            horizontalNormal =
                transform.InverseTransformDirection(Vector3.up).normalized;
            CreateHorizontalAxes(horizontalNormal, out axisU, out axisV);
        }

        float cellSize =
            Mathf.Max(navMeshCellSize, 0.02f);
        Dictionary<int, HorizontalSurfaceLayer> layers =
            BuildHorizontalSurfaceLayers(
                points,
                horizontalNormal,
                axisU,
                axisV,
                cellSize);
        Mesh mesh =
            BuildDenoisedHorizontalSurfaceNavMeshSourceMesh(
                layers,
                horizontalNormal,
                axisU,
                axisV,
                cellSize,
                out int sourceCellCount,
                out int layerCount);

        if (mesh == null ||
            mesh.vertexCount == 0)
        {
            Debug.LogWarning(
                $"PlaneFinder: denoised horizontal surface mesh has no triangles. layers={layers.Count}");
            return false;
        }

        GameObject navMeshRoot =
            GetOrCreateGeneratedChild(generatedNavMeshObjectName);
        navMeshRoot.transform.localPosition = Vector3.zero;
        navMeshRoot.transform.localRotation = Quaternion.identity;
        navMeshRoot.transform.localScale = Vector3.one;

        ClearGeneratedNavMeshSourceMeshChild(navMeshRoot);

        NavMeshSurface surface =
            navMeshRoot.GetComponent<NavMeshSurface>();

        if (surface == null)
            surface = navMeshRoot.AddComponent<NavMeshSurface>();

        surface.enabled = false;

        BuildDirectNavMeshObject(
            navMeshRoot,
            "SurfaceNavMesh",
            mesh,
            generatedNavMeshSourceColor);

        vertexCount = mesh.vertexCount;
        triangleCount = mesh.triangles.Length / 3;
        cellCount = sourceCellCount;

        Debug.Log(
            $"PlaneFinder: denoised surface NavMesh built. layers={layerCount}/{layers.Count}, cells={sourceCellCount}, cellSize={cellSize:F3}, normal={horizontalNormal}");

        return true;
    }

    private void BuildDirectNavMeshObject(
        GameObject parent,
        string objectName,
        Mesh mesh,
        Color meshColor)
    {
        GameObject navMeshObject =
            GetOrCreateChild(parent, objectName);
        navMeshObject.SetActive(true);
        navMeshObject.transform.localPosition = Vector3.zero;
        navMeshObject.transform.localRotation = Quaternion.identity;
        navMeshObject.transform.localScale = Vector3.one;
        navMeshObject.layer =
            parent.layer;

        MeshFilter meshFilter =
            navMeshObject.GetComponent<MeshFilter>();

        if (meshFilter == null)
            meshFilter = navMeshObject.AddComponent<MeshFilter>();

        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer =
            navMeshObject.GetComponent<MeshRenderer>();

        if (meshRenderer == null)
            meshRenderer = navMeshObject.AddComponent<MeshRenderer>();

        meshRenderer.sharedMaterial =
            CreateTransparentMaterial(meshColor);
        meshRenderer.enabled =
            showNavMeshSourceMesh;

        MeshCollider meshCollider =
            navMeshObject.GetComponent<MeshCollider>();

        if (meshCollider == null)
            meshCollider = navMeshObject.AddComponent<MeshCollider>();

        meshCollider.sharedMesh = null;
        meshCollider.sharedMesh = mesh;

        GeneratedNavMeshDataOwner owner =
            navMeshObject.GetComponent<GeneratedNavMeshDataOwner>();

        if (owner == null)
            owner = navMeshObject.AddComponent<GeneratedNavMeshDataOwner>();

        owner.SetNavMeshData(
            BuildDirectNavMeshData(
                mesh,
                navMeshObject.transform.localToWorldMatrix));
    }

    private void ClearDirectNavMeshObject(
        GameObject parent,
        string objectName)
    {
        Transform child =
            parent.transform.Find(objectName);

        if (child == null)
            return;

        GeneratedNavMeshDataOwner owner =
            child.GetComponent<GeneratedNavMeshDataOwner>();

        if (owner != null)
            owner.SetNavMeshData(null);
    }

    private void DisableDirectNavMeshObject(
        GameObject parent,
        string objectName)
    {
        Transform child =
            parent.transform.Find(objectName);

        if (child == null)
            return;

        ClearDirectNavMeshObject(
            parent,
            objectName);
        child.gameObject.SetActive(false);
    }

    private void ClearGeneratedNavMeshSourceMeshChild(
        GameObject parent)
    {
        Transform child =
            parent.transform.Find(generatedNavMeshSourceMeshObjectName);

        if (child == null)
            return;

        MeshRenderer renderer =
            child.GetComponent<MeshRenderer>();

        if (renderer != null)
            renderer.enabled = false;

        MeshCollider collider =
            child.GetComponent<MeshCollider>();

        if (collider != null)
            collider.sharedMesh = null;
    }

    private NavMeshData BuildDirectNavMeshData(
        Mesh mesh,
        Matrix4x4 sourceTransform)
    {
        if (mesh == null ||
            mesh.vertexCount == 0)
        {
            return null;
        }

        NavMeshBuildSettings buildSettings =
            NavMesh.GetSettingsByID(pointCloudNavMeshAgentTypeId);
        buildSettings.agentRadius =
            Mathf.Max(pointCloudNavMeshIgnoredAgentRadius, 0.001f);
        buildSettings.agentHeight =
            Mathf.Max(pointCloudNavMeshIgnoredAgentHeight, 0.001f);
        buildSettings.agentClimb =
            100f;
        buildSettings.agentSlope =
            89f;
        buildSettings.overrideVoxelSize =
            true;
        buildSettings.voxelSize =
            Mathf.Max(pointCloudNavMeshVoxelSize, 0.005f);
        buildSettings.overrideTileSize =
            true;
        buildSettings.tileSize =
            64;
        buildSettings.minRegionArea =
            Mathf.Max(pointCloudNavMeshMinRegionArea, 0f);

        List<NavMeshBuildSource> sources =
            new List<NavMeshBuildSource>
            {
                new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = mesh,
                    transform = sourceTransform,
                    area = 0
                }
            };

        Bounds bounds =
            TransformBounds(mesh.bounds, sourceTransform);
        bounds.Expand(Vector3.one * 2f);

        NavMeshData navMeshData =
            NavMeshBuilder.BuildNavMeshData(
                buildSettings,
                sources,
                bounds,
                Vector3.zero,
                Quaternion.identity);

        if (navMeshData == null)
        {
            Debug.LogWarning(
                "PlaneFinder: direct NavMesh build returned null.");
            return null;
        }

        return navMeshData;
    }

    private Bounds TransformBounds(
        Bounds bounds,
        Matrix4x4 matrix)
    {
        Vector3 center =
            matrix.MultiplyPoint3x4(bounds.center);
        Vector3 extents =
            bounds.extents;

        Vector3 axisX =
            matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        Vector3 axisY =
            matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
        Vector3 axisZ =
            matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
        extents =
            new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

        return new Bounds(center, extents * 2f);
    }

    private class AuxiliaryFloorFootprint
    {
        public float UMin;
        public float UMax;
        public float VMin;
        public float VMax;
        public float Height;
        public List<Vector2> Triangles2D = new List<Vector2>();
    }

    private int CollectAuxiliaryFloorPoints(
        List<Vector3> outPoints,
        Vector3 normal,
        bool flattenToPlane)
    {
        if (auxiliaryFloorObjects == null ||
            auxiliaryFloorObjects.Count == 0)
            return 0;

        float spacing =
            Mathf.Max(auxiliaryFloorPointSpacing, 0.005f);
        float cellArea =
            spacing * spacing;
        int addedCount = 0;

        foreach (MeshFilter meshFilter in auxiliaryFloorObjects)
        {
            if (meshFilter == null ||
                meshFilter.sharedMesh == null ||
                !meshFilter.gameObject.activeInHierarchy)
                continue;

            Mesh mesh =
                meshFilter.sharedMesh;
            Vector3[] vertices =
                mesh.vertices;
            int[] triangles =
                mesh.triangles;
            Transform meshTransform =
                meshFilter.transform;

            float averageHeight = 0f;

            if (flattenToPlane)
            {
                averageHeight =
                    GetTopLocalHeight(vertices, meshTransform, normal);
            }

            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 worldA =
                    meshTransform.TransformPoint(vertices[triangles[i]]);
                Vector3 worldB =
                    meshTransform.TransformPoint(vertices[triangles[i + 1]]);
                Vector3 worldC =
                    meshTransform.TransformPoint(vertices[triangles[i + 2]]);

                float triangleArea =
                    Vector3.Cross(worldB - worldA, worldC - worldA).magnitude * 0.5f;

                if (triangleArea < PlaneEpsilon)
                    continue;

                int sampleCount =
                    Mathf.Max(1, Mathf.RoundToInt(triangleArea / cellArea));

                for (int s = 0; s < sampleCount; s++)
                {
                    float r1 =
                        Mathf.Sqrt(Random.value);
                    float r2 =
                        Random.value;

                    Vector3 worldPoint =
                        (1f - r1) * worldA +
                        (r1 * (1f - r2)) * worldB +
                        (r1 * r2) * worldC;

                    Vector3 localPoint =
                        transform.InverseTransformPoint(worldPoint);

                    if (flattenToPlane)
                    {
                        float height =
                            Vector3.Dot(normal, localPoint);
                        localPoint += normal * (averageHeight - height);
                    }

                    outPoints.Add(localPoint);
                    addedCount++;
                }
            }
        }

        return addedCount;
    }

    private float GetTopLocalHeight(
        Vector3[] localVertices,
        Transform meshTransform,
        Vector3 normal)
    {
        if (localVertices == null ||
            localVertices.Length == 0)
            return 0f;

        float topHeight =
            float.MinValue;

        foreach (Vector3 vertex in localVertices)
        {
            Vector3 worldPoint =
                meshTransform.TransformPoint(vertex);
            Vector3 localPoint =
                transform.InverseTransformPoint(worldPoint);

            topHeight =
                Mathf.Max(topHeight, Vector3.Dot(normal, localPoint));
        }

        return topHeight;
    }

    private List<AuxiliaryFloorFootprint> BuildAuxiliaryFloorFootprints(
        Vector3 normal,
        Vector3 axisU,
        Vector3 axisV)
    {
        List<AuxiliaryFloorFootprint> footprints =
            new List<AuxiliaryFloorFootprint>();
        float margin =
            Mathf.Max(auxiliaryFloorFootprintMargin, 0f);

        foreach (MeshFilter meshFilter in auxiliaryFloorObjects)
        {
            if (meshFilter == null ||
                meshFilter.sharedMesh == null ||
                !meshFilter.gameObject.activeInHierarchy)
                continue;

            Mesh mesh =
                meshFilter.sharedMesh;
            Vector3[] vertices =
                mesh.vertices;
            int[] triangles =
                mesh.triangles;

            if (vertices.Length == 0 ||
                triangles.Length == 0)
                continue;

            Transform meshTransform =
                meshFilter.transform;

            // ローカル頂点をあらかじめ(u, v, h)に変換しておく。
            Vector2[] projected =
                new Vector2[vertices.Length];
            float[] heights =
                new float[vertices.Length];
            float uMin = float.MaxValue;
            float uMax = float.MinValue;
            float vMin = float.MaxValue;
            float vMax = float.MinValue;
            float topHeight = float.MinValue;

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 worldPoint =
                    meshTransform.TransformPoint(vertices[i]);
                Vector3 localPoint =
                    transform.InverseTransformPoint(worldPoint);

                float u =
                    Vector3.Dot(axisU, localPoint);
                float v =
                    Vector3.Dot(axisV, localPoint);
                float h =
                    Vector3.Dot(normal, localPoint);

                projected[i] = new Vector2(u, v);
                heights[i] = h;

                uMin = Mathf.Min(uMin, u);
                uMax = Mathf.Max(uMax, u);
                vMin = Mathf.Min(vMin, v);
                vMax = Mathf.Max(vMax, v);
                // Cubeなど厚みのあるメッシュの場合、床として使うべきなのは上面(normal方向で最も高い頂点群)の高さ。
                // 全頂点の平均を使うと底面の頂点に引っ張られて厚みの半分だけ低い位置になってしまう。
                topHeight = Mathf.Max(topHeight, h);
            }

            AuxiliaryFloorFootprint footprint =
                new AuxiliaryFloorFootprint
                {
                    UMin = uMin - margin,
                    UMax = uMax + margin,
                    VMin = vMin - margin,
                    VMax = vMax + margin,
                    Height = topHeight
                };

            // メッシュの実際の三角形をそのまま2D形状として保持する(AABBだけだと
            // 回転した/矩形でないメッシュのfootprintが実際より大きく・違う形になってしまうため)。
            for (int i = 0; i < triangles.Length; i += 3)
            {
                footprint.Triangles2D.Add(projected[triangles[i]]);
                footprint.Triangles2D.Add(projected[triangles[i + 1]]);
                footprint.Triangles2D.Add(projected[triangles[i + 2]]);
            }

            footprints.Add(footprint);
        }

        return footprints;
    }

    private static float Cross2D(
        Vector2 a,
        Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static bool IsPointInTriangle2D(
        Vector2 p,
        Vector2 a,
        Vector2 b,
        Vector2 c)
    {
        float d1 =
            Cross2D(p - a, b - a);
        float d2 =
            Cross2D(p - b, c - b);
        float d3 =
            Cross2D(p - c, a - c);

        bool hasNeg =
            d1 < 0f || d2 < 0f || d3 < 0f;
        bool hasPos =
            d1 > 0f || d2 > 0f || d3 > 0f;

        return !(hasNeg && hasPos);
    }

    private static float PointToSegmentDistance2D(
        Vector2 p,
        Vector2 a,
        Vector2 b)
    {
        Vector2 ab =
            b - a;
        float sqrLength =
            Mathf.Max(ab.sqrMagnitude, 1e-8f);
        float t =
            Mathf.Clamp01(Vector2.Dot(p - a, ab) / sqrLength);
        Vector2 closest =
            a + ab * t;

        return Vector2.Distance(p, closest);
    }

    // footprintの実際のメッシュ形状(三角形の集合)に対して、点が内部にあるか、
    // または境界からmargin以内にあるかを判定する。AABBだけの判定と違い、
    // 回転した/矩形でないメッシュでも実際の形状に沿った判定になる。
    private static bool IsPointInsideFootprintShape(
        AuxiliaryFloorFootprint footprint,
        float u,
        float v,
        float margin)
    {
        Vector2 p =
            new Vector2(u, v);

        for (int i = 0; i < footprint.Triangles2D.Count; i += 3)
        {
            if (IsPointInTriangle2D(
                p,
                footprint.Triangles2D[i],
                footprint.Triangles2D[i + 1],
                footprint.Triangles2D[i + 2]))
                return true;
        }

        if (margin <= 0f)
            return false;

        for (int i = 0; i < footprint.Triangles2D.Count; i += 3)
        {
            Vector2 a =
                footprint.Triangles2D[i];
            Vector2 b =
                footprint.Triangles2D[i + 1];
            Vector2 c =
                footprint.Triangles2D[i + 2];

            if (PointToSegmentDistance2D(p, a, b) <= margin ||
                PointToSegmentDistance2D(p, b, c) <= margin ||
                PointToSegmentDistance2D(p, c, a) <= margin)
                return true;
        }

        return false;
    }

    private List<Vector3> RemovePointsInsideAuxiliaryFootprints(
        List<Vector3> sourcePoints,
        List<AuxiliaryFloorFootprint> footprints,
        Vector3 normal,
        Vector3 axisU,
        Vector3 axisV,
        out int removedCount)
    {
        removedCount = 0;

        if (footprints == null ||
            footprints.Count == 0)
            return new List<Vector3>(sourcePoints);

        float heightTolerance =
            Mathf.Max(auxiliaryFloorHeightTolerance, 0.01f);
        float margin =
            Mathf.Max(auxiliaryFloorFootprintMargin, 0f);
        List<Vector3> result =
            new List<Vector3>(sourcePoints.Count);

        foreach (Vector3 point in sourcePoints)
        {
            float u =
                Vector3.Dot(axisU, point);
            float v =
                Vector3.Dot(axisV, point);
            float h =
                Vector3.Dot(normal, point);
            bool insideFloorFootprint =
                false;

            foreach (AuxiliaryFloorFootprint footprint in footprints)
            {
                if (u < footprint.UMin ||
                    u > footprint.UMax ||
                    v < footprint.VMin ||
                    v > footprint.VMax)
                    continue;

                if (Mathf.Abs(h - footprint.Height) > heightTolerance)
                    continue;

                if (!IsPointInsideFootprintShape(footprint, u, v, margin))
                    continue;

                insideFloorFootprint = true;
                break;
            }

            if (insideFloorFootprint)
                removedCount++;
            else
                result.Add(point);
        }

        return result;
    }

    private List<Vector3> BuildPointsWithAuxiliaryFloorPriority(
        List<Vector3> sourcePoints,
        out int addedCount,
        out int removedCount)
    {
        addedCount = 0;
        removedCount = 0;

        if (!TryGetAuxiliaryFloorNormal(out Vector3 normal))
        {
            List<Vector3> appended =
                new List<Vector3>(sourcePoints);
            addedCount =
                CollectAuxiliaryFloorPoints(appended, Vector3.up, false);
            return appended;
        }

        CreateHorizontalAxes(normal, out Vector3 axisU, out Vector3 axisV);

        List<Vector3> baseResult;

        if (prioritizeAuxiliaryFloorOverPointCloud)
        {
            List<AuxiliaryFloorFootprint> footprints =
                BuildAuxiliaryFloorFootprints(normal, axisU, axisV);

            baseResult =
                RemovePointsInsideAuxiliaryFootprints(
                    sourcePoints,
                    footprints,
                    normal,
                    axisU,
                    axisV,
                    out removedCount);
        }
        else
        {
            baseResult =
                new List<Vector3>(sourcePoints);
        }

        addedCount =
            CollectAuxiliaryFloorPoints(
                baseResult,
                normal,
                prioritizeAuxiliaryFloorOverPointCloud);

        return baseResult;
    }

    private bool TryGetAuxiliaryFloorNormal(
        out Vector3 normal)
    {
        normal =
            Vector3.zero;

        if (auxiliaryFloorObjects == null ||
            auxiliaryFloorObjects.Count == 0)
            return false;

        Vector3 sum =
            Vector3.zero;
        int count = 0;

        foreach (MeshFilter meshFilter in auxiliaryFloorObjects)
        {
            if (meshFilter == null ||
                !meshFilter.gameObject.activeInHierarchy)
                continue;

            sum += transform.InverseTransformDirection(meshFilter.transform.up).normalized;
            count++;
        }

        if (count == 0 ||
            sum.sqrMagnitude < PlaneEpsilon)
            return false;

        normal = sum.normalized;
        return true;
    }

    private bool TryGetBaseHorizontalFrame(
        List<Vector3> points,
        out Vector3 horizontalNormal,
        out Vector3 axisU,
        out Vector3 axisV)
    {
        if (useAuxiliaryFloorPoints &&
            bypassRansacWithAuxiliaryFloor &&
            TryGetAuxiliaryFloorNormal(out horizontalNormal))
        {
            if (Vector3.Dot(GetWorldNormal(horizontalNormal), Vector3.up) < 0f)
                horizontalNormal = -horizontalNormal;

            CreateHorizontalAxes(horizontalNormal, out axisU, out axisV);
            return true;
        }

        horizontalNormal =
            Vector3.zero;
        axisU =
            Vector3.right;
        axisV =
            Vector3.forward;

        if (!TryFindPlane(points, out PlaneResult plane))
            return false;

        horizontalNormal =
            plane.Normal.normalized;

        if (Vector3.Dot(GetWorldNormal(horizontalNormal), Vector3.up) < 0f)
            horizontalNormal = -horizontalNormal;

        CreateHorizontalAxes(horizontalNormal, out axisU, out axisV);
        return true;
    }

    private void CreateHorizontalAxes(
        Vector3 normal,
        out Vector3 axisU,
        out Vector3 axisV)
    {
        axisU =
            Vector3.ProjectOnPlane(Vector3.right, normal);

        if (axisU.sqrMagnitude < PlaneEpsilon)
            axisU = Vector3.ProjectOnPlane(Vector3.forward, normal);

        axisU.Normalize();
        axisV =
            Vector3.Cross(normal, axisU).normalized;
    }

    private Dictionary<int, HorizontalSurfaceLayer> BuildHorizontalSurfaceLayers(
        List<Vector3> points,
        Vector3 normal,
        Vector3 axisU,
        Vector3 axisV,
        float cellSize)
    {
        Dictionary<int, HorizontalSurfaceLayer> layers =
            new Dictionary<int, HorizontalSurfaceLayer>();
        Dictionary<Vector2Int, HorizontalColumnStats> columns =
            new Dictionary<Vector2Int, HorizontalColumnStats>();
        float heightBinSize =
            Mathf.Max(horizontalSurfaceHeightBinSize, 0.01f);

        foreach (Vector3 point in points)
        {
            float height =
                Vector3.Dot(normal, point);
            int layerKey =
                Mathf.RoundToInt(height / heightBinSize);

            if (!layers.TryGetValue(layerKey, out HorizontalSurfaceLayer layer))
            {
                layer = new HorizontalSurfaceLayer();
                layers.Add(layerKey, layer);
            }

            float u =
                Vector3.Dot(axisU, point);
            float v =
                Vector3.Dot(axisV, point);
            Vector2Int cellKey =
                new Vector2Int(
                    Mathf.FloorToInt(u / cellSize),
                    Mathf.FloorToInt(v / cellSize));

            layer.Add(cellKey, height);

            if (!columns.TryGetValue(cellKey, out HorizontalColumnStats column))
            {
                column = new HorizontalColumnStats();
                columns.Add(cellKey, column);
            }

            column.Add(height, layerKey);
        }

        RemoveVerticalColumnCells(layers, columns);

        return layers;
    }

    private void RemoveVerticalColumnCells(
        Dictionary<int, HorizontalSurfaceLayer> layers,
        Dictionary<Vector2Int, HorizontalColumnStats> columns)
    {
        float rejectHeight =
            Mathf.Max(verticalColumnRejectHeight, 0.01f);
        float topTolerance =
            Mathf.Max(verticalColumnTopTolerance, 0.01f);

        foreach (KeyValuePair<int, HorizontalSurfaceLayer> layerPair in layers)
        {
            List<Vector2Int> removals =
                new List<Vector2Int>();

            foreach (KeyValuePair<Vector2Int, HorizontalSurfaceCell> cellPair in layerPair.Value.Cells)
            {
                if (!columns.TryGetValue(cellPair.Key, out HorizontalColumnStats column))
                    continue;

                bool verticalColumn =
                    column.HeightRange >= rejectHeight &&
                    column.OccupiedLayerCount >= 4;
                bool nearColumnTop =
                    cellPair.Value.AverageHeight >= column.MaxHeight - topTolerance;

                if (verticalColumn &&
                    !nearColumnTop)
                {
                    removals.Add(cellPair.Key);
                }
            }

            foreach (Vector2Int key in removals)
            {
                layerPair.Value.Cells.Remove(key);
            }
        }
    }

    private Mesh BuildDenoisedHorizontalSurfaceNavMeshSourceMesh(
        Dictionary<int, HorizontalSurfaceLayer> layers,
        Vector3 normal,
        Vector3 axisU,
        Vector3 axisV,
        float cellSize,
        out int sourceCellCount,
        out int usedLayerCount)
    {
        sourceCellCount = 0;
        usedLayerCount = 0;
        List<Vector3> vertices =
            new List<Vector3>();
        List<int> triangles =
            new List<int>();
        int minLayerPoints =
            Mathf.Max(minHorizontalSurfaceLayerPoints, 1);

        foreach (KeyValuePair<int, HorizontalSurfaceLayer> pair in layers)
        {
            HorizontalSurfaceLayer layer =
                pair.Value;

            if (layer.PointCount < minLayerPoints)
                continue;

            Dictionary<Vector2Int, float> rawDenoisedHeights =
                BuildRawDenoisedHorizontalLayerCells(layer);

            if (rawDenoisedHeights.Count < minHorizontalSurfaceClusterCells)
                continue;

            RemoveSmallHorizontalSurfaceClusters(rawDenoisedHeights, cellSize);
            FillSmallNavMeshSurfaceHoles(rawDenoisedHeights);
            MergeOneCellNavMeshSurfaceGaps(rawDenoisedHeights);
            RemoveThinHorizontalSurfaceCells(rawDenoisedHeights);
            RemoveSmallHorizontalSurfaceClusters(rawDenoisedHeights, cellSize);

            if (rawDenoisedHeights.Count < minHorizontalSurfaceClusterCells)
                continue;

            // ここまでのノイズ除去/穴埋めが終わった最終形の輪郭を、外側にさらに広げる。
            // 点群が実際の床/机の縁より内側にしか取れていない場合の取りこぼしを補う。
            ExpandNavMeshSurface(rawDenoisedHeights);

            AppendHorizontalSurfaceQuads(
                rawDenoisedHeights,
                vertices,
                triangles,
                normal,
                axisU,
                axisV,
                cellSize);

            sourceCellCount += rawDenoisedHeights.Count;
            usedLayerCount++;
        }

        if (useAuxiliaryFloorPoints &&
            prioritizeAuxiliaryFloorOverPointCloud)
        {
            int guaranteedCellCount =
                AppendAuxiliaryFloorGuaranteedQuads(
                    vertices,
                    triangles,
                    normal,
                    axisU,
                    axisV,
                    cellSize);

            if (guaranteedCellCount > 0)
            {
                sourceCellCount += guaranteedCellCount;
                usedLayerCount++;
            }
        }

        return CreateNavMeshSourceMesh(
            generatedNavMeshObjectName + "Mesh",
            vertices,
            triangles);
    }

    private int AppendAuxiliaryFloorGuaranteedQuads(
        List<Vector3> vertices,
        List<int> triangles,
        Vector3 normal,
        Vector3 axisU,
        Vector3 axisV,
        float cellSize)
    {
        if (auxiliaryFloorObjects == null ||
            auxiliaryFloorObjects.Count == 0)
            return 0;

        // 点群由来の穴埋め/ノイズ除去(垂直構造の除去・薄いセルの除去など)は、
        // 実世界で確実に平面だとわかっている補助オブジェクトのfootprintには適用したくない。
        // footprint内のセルは、点群側で既に何らかのセルが(別の高さで)存在していても
        // 無条件に補助平面の高さで埋め直し、完全な床を保証する
        // (「既に埋まっているからスキップ」という判定は、家具などのノイズが
        // 同じ(x,y)セルの別の高さに存在するだけで補助平面側が抜けてしまうため使わない)。
        List<AuxiliaryFloorFootprint> footprints =
            BuildAuxiliaryFloorFootprints(normal, axisU, axisV);
        int addedCellCount = 0;

        foreach (AuxiliaryFloorFootprint footprint in footprints)
        {
            int minX = Mathf.FloorToInt(footprint.UMin / cellSize);
            int maxX = Mathf.FloorToInt(footprint.UMax / cellSize);
            int minY = Mathf.FloorToInt(footprint.VMin / cellSize);
            int maxY = Mathf.FloorToInt(footprint.VMax / cellSize);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    // セル中心が実際のメッシュ形状(投影した三角形)の内部にあるセルだけを埋める。
                    // AABB全体を埋めると、回転した/矩形でない補助オブジェクトの場合に
                    // 実物より広く・違う形のNavMeshができてしまう。
                    float centerU =
                        (x + 0.5f) * cellSize;
                    float centerV =
                        (y + 0.5f) * cellSize;

                    if (!IsPointInsideFootprintShape(footprint, centerU, centerV, 0f))
                        continue;

                    AddHorizontalSurfaceQuad(
                        vertices,
                        triangles,
                        new Vector2Int(x, y),
                        footprint.Height,
                        normal,
                        axisU,
                        axisV,
                        cellSize);

                    addedCellCount++;
                }
            }
        }

        return addedCellCount;
    }

    private Mesh CreateNavMeshSourceMesh(
        string meshName,
        List<Vector3> vertices,
        List<int> triangles)
    {
        if (vertices.Count == 0 ||
            triangles.Count == 0)
        {
            return null;
        }

        Mesh mesh =
            new Mesh
            {
                name = meshName,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray()
            };

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    private void AppendHorizontalSurfaceQuads(
        Dictionary<Vector2Int, float> heights,
        List<Vector3> vertices,
        List<int> triangles,
        Vector3 normal,
        Vector3 axisU,
        Vector3 axisV,
        float cellSize)
    {
        foreach (KeyValuePair<Vector2Int, float> cell in heights)
        {
            AddHorizontalSurfaceQuad(
                vertices,
                triangles,
                cell.Key,
                cell.Value,
                normal,
                axisU,
                axisV,
                cellSize);
        }
    }

    private Dictionary<Vector2Int, float> BuildRawDenoisedHorizontalLayerCells(
        HorizontalSurfaceLayer layer)
    {
        Dictionary<Vector2Int, float> heights =
            new Dictionary<Vector2Int, float>();
        int minPoints =
            Mathf.Max(minNavMeshCellPoints, 1);

        foreach (KeyValuePair<Vector2Int, HorizontalSurfaceCell> pair in layer.Cells)
        {
            if (pair.Value.Count < minPoints)
                continue;

            heights.Add(pair.Key, pair.Value.AverageHeight);
        }

        return heights;
    }

    private void FillSmallNavMeshSurfaceHoles(
        Dictionary<Vector2Int, float> heights)
    {
        int iterations =
            Mathf.Max(navMeshHoleFillIterations, 0);

        for (int i = 0; i < iterations; i++)
        {
            if (!TryGetCellBounds(heights, out int minX, out int maxX, out int minY, out int maxY))
                return;

            Dictionary<Vector2Int, float> additions =
                new Dictionary<Vector2Int, float>();

            for (int x = minX + 1; x <= maxX - 1; x++)
            {
                for (int y = minY + 1; y <= maxY - 1; y++)
                {
                    Vector2Int key =
                        new Vector2Int(x, y);

                    if (heights.ContainsKey(key))
                        continue;

                    if (TryEstimateConservativeNavMeshFillHeight(
                        heights,
                        key,
                        Mathf.Max(minNavMeshHoleNeighborCount, 1),
                        out float height))
                    {
                        additions.Add(key, height);
                    }
                }
            }

            if (additions.Count == 0)
                break;

            foreach (KeyValuePair<Vector2Int, float> addition in additions)
                heights[addition.Key] = addition.Value;
        }
    }

    private void MergeOneCellNavMeshSurfaceGaps(
        Dictionary<Vector2Int, float> heights)
    {
        if (!mergeOneCellNavMeshGaps)
            return;

        int iterations =
            Mathf.Max(navMeshGapMergeIterations, 0);

        for (int i = 0; i < iterations; i++)
        {
            if (!TryGetCellBounds(heights, out int minX, out int maxX, out int minY, out int maxY))
                return;

            Dictionary<Vector2Int, float> additions =
                new Dictionary<Vector2Int, float>();

            for (int x = minX + 1; x <= maxX - 1; x++)
            {
                for (int y = minY + 1; y <= maxY - 1; y++)
                {
                    Vector2Int key =
                        new Vector2Int(x, y);

                    if (heights.ContainsKey(key))
                        continue;

                    if (TryEstimateOneCellGapHeight(heights, key, out float height))
                        additions.Add(key, height);
                }
            }

            if (additions.Count == 0)
                break;

            foreach (KeyValuePair<Vector2Int, float> addition in additions)
                heights[addition.Key] = addition.Value;
        }
    }

    private void ExpandNavMeshSurface(
        Dictionary<Vector2Int, float> heights)
    {
        int iterations =
            Mathf.Max(navMeshSurfaceExpansionIterations, 0);

        for (int i = 0; i < iterations; i++)
        {
            Dictionary<Vector2Int, float> additions =
                new Dictionary<Vector2Int, float>();
            HashSet<Vector2Int> candidates =
                new HashSet<Vector2Int>();

            foreach (Vector2Int key in heights.Keys)
            {
                foreach (Vector2Int neighbor in GetAllNeighbors(key))
                {
                    if (!heights.ContainsKey(neighbor))
                        candidates.Add(neighbor);
                }
            }

            foreach (Vector2Int candidate in candidates)
            {
                if (TryEstimateNavMeshExpansionHeight(heights, candidate, out float height))
                    additions.Add(candidate, height);
            }

            if (additions.Count == 0)
                break;

            foreach (KeyValuePair<Vector2Int, float> addition in additions)
                heights[addition.Key] = addition.Value;
        }
    }

    private bool TryEstimateNavMeshExpansionHeight(
        Dictionary<Vector2Int, float> heights,
        Vector2Int candidate,
        out float height)
    {
        height = 0f;
        float sum = 0f;
        int count = 0;
        float minHeight = float.PositiveInfinity;
        float maxHeight = float.NegativeInfinity;

        foreach (Vector2Int neighbor in GetAllNeighbors(candidate))
        {
            if (!heights.TryGetValue(neighbor, out float neighborHeight))
                continue;

            sum += neighborHeight;
            count++;
            minHeight = Mathf.Min(minHeight, neighborHeight);
            maxHeight = Mathf.Max(maxHeight, neighborHeight);
        }

        if (count < Mathf.Max(minNavMeshExpansionNeighborCount, 1))
            return false;

        if (maxHeight - minHeight > GetNavMeshMergeHeightLimit())
            return false;

        height = sum / count;
        return true;
    }

    private bool TryEstimateConservativeNavMeshFillHeight(
        Dictionary<Vector2Int, float> heights,
        Vector2Int key,
        int minNeighborCount,
        out float height)
    {
        height = 0f;
        int count =
            0;
        float minHeight =
            float.PositiveInfinity;
        float maxHeight =
            float.NegativeInfinity;

        foreach (Vector2Int neighbor in GetAllNeighbors(key))
        {
            if (!heights.TryGetValue(neighbor, out float neighborHeight))
                continue;

            height += neighborHeight;
            count++;
            minHeight = Mathf.Min(minHeight, neighborHeight);
            maxHeight = Mathf.Max(maxHeight, neighborHeight);
        }

        if (count < minNeighborCount)
            return false;

        if (maxHeight - minHeight > GetNavMeshMergeHeightLimit())
            return false;

        height /= count;
        return true;
    }

    private bool TryEstimateOneCellGapHeight(
        Dictionary<Vector2Int, float> heights,
        Vector2Int key,
        out float height)
    {
        height = 0f;

        if (TryEstimateOppositeNeighborHeight(
            heights,
            key + Vector2Int.left,
            key + Vector2Int.right,
            out height))
        {
            return true;
        }

        if (TryEstimateOppositeNeighborHeight(
            heights,
            key + Vector2Int.down,
            key + Vector2Int.up,
            out height))
        {
            return true;
        }

        return false;
    }

    private bool TryEstimateOppositeNeighborHeight(
        Dictionary<Vector2Int, float> heights,
        Vector2Int a,
        Vector2Int b,
        out float height)
    {
        height = 0f;

        if (!heights.TryGetValue(a, out float heightA) ||
            !heights.TryGetValue(b, out float heightB))
        {
            return false;
        }

        if (Mathf.Abs(heightA - heightB) > GetNavMeshMergeHeightLimit())
            return false;

        height =
            (heightA + heightB) * 0.5f;
        return true;
    }

    private float GetNavMeshMergeHeightLimit()
    {
        return Mathf.Max(
            0.001f,
            Mathf.Min(
                Mathf.Max(maxNavMeshMergeHeightDifference, 0.001f),
                Mathf.Max(maxNavMeshNeighborHeightStep, 0.001f)));
    }

    private bool TryGetCellBounds(
        Dictionary<Vector2Int, float> heights,
        out int minX,
        out int maxX,
        out int minY,
        out int maxY)
    {
        minX = int.MaxValue;
        maxX = int.MinValue;
        minY = int.MaxValue;
        maxY = int.MinValue;

        if (heights.Count == 0)
            return false;

        foreach (Vector2Int key in heights.Keys)
        {
            minX = Mathf.Min(minX, key.x);
            maxX = Mathf.Max(maxX, key.x);
            minY = Mathf.Min(minY, key.y);
            maxY = Mathf.Max(maxY, key.y);
        }

        return true;
    }

    private void RemoveThinHorizontalSurfaceCells(
        Dictionary<Vector2Int, float> heights)
    {
        List<Vector2Int> removals =
            new List<Vector2Int>();

        foreach (KeyValuePair<Vector2Int, float> pair in heights)
        {
            if (HasTwoByTwoHorizontalSurfacePatch(heights, pair.Key))
                continue;

            removals.Add(pair.Key);
        }

        foreach (Vector2Int key in removals)
        {
            heights.Remove(key);
        }
    }

    private bool HasTwoByTwoHorizontalSurfacePatch(
        Dictionary<Vector2Int, float> heights,
        Vector2Int key)
    {
        for (int dx = -1; dx <= 0; dx++)
        {
            for (int dz = -1; dz <= 0; dz++)
            {
                Vector2Int corner =
                    new Vector2Int(
                        key.x + dx,
                        key.y + dz);

                if (heights.ContainsKey(corner) &&
                    heights.ContainsKey(new Vector2Int(corner.x + 1, corner.y)) &&
                    heights.ContainsKey(new Vector2Int(corner.x, corner.y + 1)) &&
                    heights.ContainsKey(new Vector2Int(corner.x + 1, corner.y + 1)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RemoveSmallHorizontalSurfaceClusters(
        Dictionary<Vector2Int, float> heights,
        float cellSize)
    {
        int minClusterCells =
            Mathf.Max(minHorizontalSurfaceClusterCells, 1);
        int minClusterSpan =
            Mathf.Max(
                2,
                Mathf.CeilToInt(
                    minHorizontalSurfaceClusterSize /
                    Mathf.Max(cellSize, 0.001f)));
        float minDensity =
            Mathf.Clamp01(minHorizontalSurfaceClusterDensity);
        HashSet<Vector2Int> visited =
            new HashSet<Vector2Int>();
        List<List<Vector2Int>> removals =
            new List<List<Vector2Int>>();

        foreach (Vector2Int key in heights.Keys)
        {
            if (visited.Contains(key))
                continue;

            List<Vector2Int> cluster =
                CollectHorizontalSurfaceCluster(heights, key, visited);

            if (!IsUsableHorizontalSurfaceCluster(
                cluster,
                minClusterCells,
                minClusterSpan,
                minDensity))
            {
                removals.Add(cluster);
            }
        }

        foreach (List<Vector2Int> cluster in removals)
        {
            foreach (Vector2Int key in cluster)
            {
                heights.Remove(key);
            }
        }
    }

    private bool IsUsableHorizontalSurfaceCluster(
        List<Vector2Int> cluster,
        int minClusterCells,
        int minClusterSpan,
        float minDensity)
    {
        if (cluster.Count < minClusterCells)
            return false;

        int minX =
            int.MaxValue;
        int maxX =
            int.MinValue;
        int minY =
            int.MaxValue;
        int maxY =
            int.MinValue;

        foreach (Vector2Int key in cluster)
        {
            minX = Mathf.Min(minX, key.x);
            maxX = Mathf.Max(maxX, key.x);
            minY = Mathf.Min(minY, key.y);
            maxY = Mathf.Max(maxY, key.y);
        }

        int width =
            maxX - minX + 1;
        int depth =
            maxY - minY + 1;

        if (width < minClusterSpan ||
            depth < minClusterSpan)
        {
            return false;
        }

        float boundingArea =
            Mathf.Max(width * depth, 1);
        float density =
            cluster.Count / boundingArea;

        return density >= minDensity;
    }

    private List<Vector2Int> CollectHorizontalSurfaceCluster(
        Dictionary<Vector2Int, float> heights,
        Vector2Int start,
        HashSet<Vector2Int> visited)
    {
        List<Vector2Int> cluster =
            new List<Vector2Int>();
        Queue<Vector2Int> queue =
            new Queue<Vector2Int>();

        visited.Add(start);
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            Vector2Int current =
                queue.Dequeue();
            cluster.Add(current);

            foreach (Vector2Int neighbor in GetAllNeighbors(current))
            {
                if (visited.Contains(neighbor) ||
                    !heights.ContainsKey(neighbor))
                    continue;

                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return cluster;
    }

    private void AddHorizontalSurfaceQuad(
        List<Vector3> vertices,
        List<int> triangles,
        Vector2Int key,
        float height,
        Vector3 normal,
        Vector3 axisU,
        Vector3 axisV,
        float cellSize)
    {
        float u0 =
            key.x * cellSize;
        float u1 =
            (key.x + 1) * cellSize;
        float v0 =
            key.y * cellSize;
        float v1 =
            (key.y + 1) * cellSize;
        Vector3 basePoint =
            normal * height;

        int index =
            vertices.Count;
        vertices.Add(basePoint + axisU * u0 + axisV * v0);
        vertices.Add(basePoint + axisU * u1 + axisV * v0);
        vertices.Add(basePoint + axisU * u0 + axisV * v1);
        vertices.Add(basePoint + axisU * u1 + axisV * v1);

        if (Vector3.Dot(Vector3.Cross(vertices[index + 2] - vertices[index], vertices[index + 1] - vertices[index]), normal) >= 0f)
        {
            triangles.Add(index);
            triangles.Add(index + 2);
            triangles.Add(index + 1);
            triangles.Add(index + 1);
            triangles.Add(index + 2);
            triangles.Add(index + 3);
        }
        else
        {
            triangles.Add(index);
            triangles.Add(index + 1);
            triangles.Add(index + 2);
            triangles.Add(index + 1);
            triangles.Add(index + 3);
            triangles.Add(index + 2);
        }
    }

    private bool TryCreatePointCloudObject(
        List<Vector3> points,
        out int vertexCount,
        out int triangleCount,
        out int voxelCount)
    {
        vertexCount = 0;
        triangleCount = 0;
        voxelCount = 0;

        PointCloudObjectSettings settings =
            GetPointCloudObjectSettings();
        float voxelSize =
            settings.VoxelSize;
        Dictionary<Vector3Int, VoxelCell> cells =
            BuildOccupiedVoxelCells(points, settings);
        HashSet<Vector3Int> voxels =
            new HashSet<Vector3Int>(cells.Keys);
        int rawVoxelCount =
            voxels.Count;

        if (voxels.Count == 0)
            return false;

        if (settings.RemoveSparseNoise)
            RemoveSparseVoxelNoise(voxels, settings.MinSparseNeighborCount);

        if (settings.RemoveSmallClusters)
            RemoveSmallVoxelClusters(voxels, settings.MinClusterSize);

        int filteredVoxelCount =
            voxels.Count;

        if (voxels.Count == 0)
        {
            Debug.LogWarning(
                $"PlaneFinder: all point cloud object voxels were filtered as noise. rawVoxels={rawVoxelCount}");
            return false;
        }

        if (settings.FillSmallGaps)
        {
            FillSmallVoxelGaps(voxels, settings.MinGapNeighborCount);
            EnsureVoxelCells(cells, voxels, voxelSize);
        }

        ExpandOccupiedVoxels(voxels, settings.ExpansionVoxels);
        EnsureVoxelCells(cells, voxels, voxelSize);

        Mesh mesh =
            BuildVoxelObjectMesh(cells, voxels, settings);

        if (mesh == null ||
            mesh.vertexCount == 0)
            return false;

        vertexCount = mesh.vertexCount;
        triangleCount = mesh.triangles.Length / 3;
        voxelCount = voxels.Count;

        GameObject objectRoot =
            GetOrCreateGeneratedChild(generatedPointCloudObjectName);

        MeshFilter meshFilter =
            objectRoot.GetComponent<MeshFilter>();

        if (meshFilter == null)
            meshFilter = objectRoot.AddComponent<MeshFilter>();

        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer =
            objectRoot.GetComponent<MeshRenderer>();

        if (meshRenderer == null)
            meshRenderer = objectRoot.AddComponent<MeshRenderer>();

        meshRenderer.sharedMaterial =
            CreateTransparentMaterial(generatedPointCloudObjectColor);

        if (addPointCloudObjectCollider)
        {
            MeshCollider meshCollider =
                objectRoot.GetComponent<MeshCollider>();

            if (meshCollider == null)
                meshCollider = objectRoot.AddComponent<MeshCollider>();

            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = mesh;
        }

        if (buildNavMeshAfterPointCloudObject)
            BuildNavMeshForGeneratedPointCloudObject(objectRoot);

        Debug.Log(
            $"PlaneFinder: point cloud object voxels raw={rawVoxelCount}, filtered={filteredVoxelCount}, final={voxels.Count}");

        return true;
    }

    public void BuildNavMeshForGeneratedPointCloudObject()
    {
        Transform objectTransform =
            transform.Find(generatedPointCloudObjectName);

        if (objectTransform == null)
        {
            Debug.LogWarning(
                "PlaneFinder: NavMeshを張る点群オブジェクトがありません。先に点群オブジェクトを生成してください。");
            return;
        }

        BuildNavMeshForGeneratedPointCloudObject(objectTransform.gameObject);
    }

    private void BuildNavMeshForGeneratedPointCloudObject(
        GameObject objectRoot)
    {
        if (objectRoot == null)
            return;

        MeshFilter meshFilter =
            objectRoot.GetComponent<MeshFilter>();

        if (meshFilter == null ||
            meshFilter.sharedMesh == null)
        {
            Debug.LogWarning(
                "PlaneFinder: NavMeshを張るMeshがありません。先に点群オブジェクトを生成してください。");
            return;
        }

        NavMeshSurface surface =
            objectRoot.GetComponent<NavMeshSurface>();

        if (surface == null)
            surface = objectRoot.AddComponent<NavMeshSurface>();

        surface.agentTypeID = pointCloudNavMeshAgentTypeId;
        surface.collectObjects = CollectObjects.Children;
        surface.layerMask = 1 << objectRoot.layer;
        surface.useGeometry =
            addPointCloudObjectCollider
                ? NavMeshCollectGeometry.PhysicsColliders
                : NavMeshCollectGeometry.RenderMeshes;
        surface.overrideVoxelSize = true;
        surface.voxelSize = Mathf.Max(pointCloudNavMeshVoxelSize, 0.005f);
        surface.minRegionArea = Mathf.Max(pointCloudNavMeshMinRegionArea, 0f);
        surface.buildHeightMesh = true;
        surface.BuildNavMesh();

        Debug.Log(
            $"PlaneFinder: NavMesh built on {objectRoot.name}. agentType={pointCloudNavMeshAgentTypeId}, geometry={surface.useGeometry}");
    }

    private PointCloudObjectSettings GetPointCloudObjectSettings()
    {
        switch (pointCloudObjectQuality)
        {
            case PointCloudObjectQuality.Fast:
                return new PointCloudObjectSettings(
                    0.035f,
                    0.006f,
                    1,
                    true,
                    1,
                    true,
                    12,
                    false,
                    5,
                    0,
                    1,
                    0.25f);

            case PointCloudObjectQuality.Fine:
                return new PointCloudObjectSettings(
                    0.022f,
                    0.004f,
                    1,
                    true,
                    1,
                    true,
                    20,
                    false,
                    5,
                    0,
                    2,
                    0.32f);

            default:
                return new PointCloudObjectSettings(
                    0.015f,
                    0.003f,
                    1,
                    true,
                    1,
                    true,
                    30,
                    false,
                    5,
                    0,
                    4,
                    0.38f);
        }
    }

    private Dictionary<Vector3Int, VoxelCell> BuildOccupiedVoxelCells(
        List<Vector3> points,
        PointCloudObjectSettings settings)
    {
        Dictionary<Vector3Int, VoxelCell> cells =
            new Dictionary<Vector3Int, VoxelCell>();
        float voxelSize =
            settings.VoxelSize;

        foreach (Vector3 point in points)
        {
            Vector3Int key =
                new Vector3Int(
                    Mathf.FloorToInt(point.x / voxelSize),
                    Mathf.FloorToInt(point.y / voxelSize),
                    Mathf.FloorToInt(point.z / voxelSize));

            if (!cells.TryGetValue(key, out VoxelCell cell))
            {
                cell = new VoxelCell(key, voxelSize);
                cells.Add(key, cell);
            }

            cell.Add(point);
        }

        Dictionary<Vector3Int, VoxelCell> filtered =
            new Dictionary<Vector3Int, VoxelCell>();
        int minPoints =
            Mathf.Max(settings.MinVoxelPoints, 1);

        foreach (KeyValuePair<Vector3Int, VoxelCell> pair in cells)
        {
            if (pair.Value.Count >= minPoints)
                filtered.Add(pair.Key, pair.Value);
        }

        return filtered;
    }

    private void EnsureVoxelCells(
        Dictionary<Vector3Int, VoxelCell> cells,
        HashSet<Vector3Int> voxels,
        float voxelSize)
    {
        foreach (Vector3Int voxel in voxels)
        {
            if (!cells.ContainsKey(voxel))
                cells.Add(voxel, new VoxelCell(voxel, voxelSize));
        }
    }

    private void RemoveSparseVoxelNoise(
        HashSet<Vector3Int> voxels,
        int minNeighborCount)
    {
        int requiredNeighbors =
            Mathf.Max(minNeighborCount, 0);

        if (requiredNeighbors == 0)
            return;

        List<Vector3Int> removals =
            new List<Vector3Int>();

        foreach (Vector3Int voxel in voxels)
        {
            int neighborCount =
                CountOccupiedVoxelNeighbors(voxels, voxel);

            if (neighborCount < requiredNeighbors)
                removals.Add(voxel);
        }

        foreach (Vector3Int removal in removals)
        {
            voxels.Remove(removal);
        }
    }

    private void RemoveSmallVoxelClusters(
        HashSet<Vector3Int> voxels,
        int minClusterVoxelCount)
    {
        int minClusterSize =
            Mathf.Max(minClusterVoxelCount, 1);

        HashSet<Vector3Int> visited =
            new HashSet<Vector3Int>();
        List<List<Vector3Int>> smallClusters =
            new List<List<Vector3Int>>();

        foreach (Vector3Int voxel in voxels)
        {
            if (visited.Contains(voxel))
                continue;

            List<Vector3Int> cluster =
                CollectVoxelCluster(voxels, voxel, visited);

            if (cluster.Count < minClusterSize)
                smallClusters.Add(cluster);
        }

        foreach (List<Vector3Int> cluster in smallClusters)
        {
            foreach (Vector3Int voxel in cluster)
            {
                voxels.Remove(voxel);
            }
        }
    }

    private List<Vector3Int> CollectVoxelCluster(
        HashSet<Vector3Int> voxels,
        Vector3Int start,
        HashSet<Vector3Int> visited)
    {
        List<Vector3Int> cluster =
            new List<Vector3Int>();
        Queue<Vector3Int> queue =
            new Queue<Vector3Int>();

        visited.Add(start);
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            Vector3Int current =
                queue.Dequeue();

            cluster.Add(current);

            foreach (Vector3Int neighbor in GetVoxelNeighbors26(current))
            {
                if (visited.Contains(neighbor) ||
                    !voxels.Contains(neighbor))
                    continue;

                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        return cluster;
    }

    private void FillSmallVoxelGaps(
        HashSet<Vector3Int> voxels,
        int minNeighborCount)
    {
        HashSet<Vector3Int> additions =
            new HashSet<Vector3Int>();
        HashSet<Vector3Int> candidates =
            new HashSet<Vector3Int>();

        foreach (Vector3Int voxel in voxels)
        {
            foreach (Vector3Int neighbor in GetVoxelNeighbors26(voxel))
            {
                if (!voxels.Contains(neighbor))
                    candidates.Add(neighbor);
            }
        }

        int requiredNeighbors =
            Mathf.Max(minNeighborCount, 1);

        foreach (Vector3Int candidate in candidates)
        {
            int neighborCount =
                CountOccupiedVoxelNeighbors(voxels, candidate);

            if (neighborCount >= requiredNeighbors)
                additions.Add(candidate);
        }

        foreach (Vector3Int addition in additions)
        {
            voxels.Add(addition);
        }
    }

    private void ExpandOccupiedVoxels(
        HashSet<Vector3Int> voxels,
        int expansionVoxels)
    {
        int iterations =
            Mathf.Max(expansionVoxels, 0);

        for (int i = 0; i < iterations; i++)
        {
            HashSet<Vector3Int> additions =
                new HashSet<Vector3Int>();

            foreach (Vector3Int voxel in voxels)
            {
                foreach (Vector3Int neighbor in GetVoxelNeighbors6(voxel))
                {
                    additions.Add(neighbor);
                }
            }

            foreach (Vector3Int addition in additions)
            {
                voxels.Add(addition);
            }
        }
    }

    private int CountOccupiedVoxelNeighbors(
        HashSet<Vector3Int> voxels,
        Vector3Int voxel)
    {
        int count =
            0;

        foreach (Vector3Int neighbor in GetVoxelNeighbors26(voxel))
        {
            if (voxels.Contains(neighbor))
                count++;
        }

        return count;
    }

    private Mesh BuildVoxelObjectMesh(
        Dictionary<Vector3Int, VoxelCell> cells,
        HashSet<Vector3Int> voxels,
        PointCloudObjectSettings settings)
    {
        List<Vector3> vertices =
            new List<Vector3>();
        List<int> triangles =
            new List<int>();
        float voxelSize =
            settings.VoxelSize;

        foreach (Vector3Int voxel in voxels)
        {
            AddVisibleVoxelFaces(
                cells,
                voxels,
                vertices,
                triangles,
                voxel,
                settings);
        }

        if (vertices.Count == 0 ||
            triangles.Count == 0)
            return null;

        Mesh mesh =
            new Mesh
            {
                name = generatedPointCloudObjectName + "Mesh",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray()
            };

        SmoothMeshVertices(
            mesh,
            settings.SmoothIterations,
            voxelSize * 1.75f,
            settings.SmoothStrength);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    private void AddVisibleVoxelFaces(
        Dictionary<Vector3Int, VoxelCell> cells,
        HashSet<Vector3Int> voxels,
        List<Vector3> vertices,
        List<int> triangles,
        Vector3Int voxel,
        PointCloudObjectSettings settings)
    {
        float voxelSize =
            settings.VoxelSize;
        Vector3Int right =
            new Vector3Int(1, 0, 0);
        Vector3Int left =
            new Vector3Int(-1, 0, 0);
        Vector3Int up =
            new Vector3Int(0, 1, 0);
        Vector3Int down =
            new Vector3Int(0, -1, 0);
        Vector3Int forward =
            new Vector3Int(0, 0, 1);
        Vector3Int back =
            new Vector3Int(0, 0, -1);

        if (!cells.TryGetValue(voxel, out VoxelCell cell))
            cell = new VoxelCell(voxel, voxelSize);

        Vector3 min =
            cell.GetPaddedMin(settings.SurfacePadding);
        Vector3 max =
            cell.GetPaddedMax(settings.SurfacePadding);

        Vector3 p000 =
            new Vector3(min.x, min.y, min.z);
        Vector3 p100 =
            new Vector3(max.x, min.y, min.z);
        Vector3 p010 =
            new Vector3(min.x, max.y, min.z);
        Vector3 p110 =
            new Vector3(max.x, max.y, min.z);
        Vector3 p001 =
            new Vector3(min.x, min.y, max.z);
        Vector3 p101 =
            new Vector3(max.x, min.y, max.z);
        Vector3 p011 =
            new Vector3(min.x, max.y, max.z);
        Vector3 p111 =
            new Vector3(max.x, max.y, max.z);

        if (!voxels.Contains(voxel + right))
            AddQuad(vertices, triangles, p100, p110, p101, p111);

        if (!voxels.Contains(voxel + left))
            AddQuad(vertices, triangles, p000, p001, p010, p011);

        if (!voxels.Contains(voxel + up))
            AddQuad(vertices, triangles, p010, p011, p110, p111);

        if (!voxels.Contains(voxel + down))
            AddQuad(vertices, triangles, p000, p100, p001, p101);

        if (!voxels.Contains(voxel + forward))
            AddQuad(vertices, triangles, p001, p101, p011, p111);

        if (!voxels.Contains(voxel + back))
            AddQuad(vertices, triangles, p000, p010, p100, p110);
    }

    private void AddQuad(
        List<Vector3> vertices,
        List<int> triangles,
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        Vector3 v3)
    {
        int index =
            vertices.Count;

        vertices.Add(v0);
        vertices.Add(v1);
        vertices.Add(v2);
        vertices.Add(v3);

        triangles.Add(index);
        triangles.Add(index + 1);
        triangles.Add(index + 2);
        triangles.Add(index + 2);
        triangles.Add(index + 1);
        triangles.Add(index + 3);
    }

    private void SmoothMeshVertices(
        Mesh mesh,
        int iterations,
        float radius,
        float strength)
    {
        iterations =
            Mathf.Max(iterations, 0);
        radius =
            Mathf.Max(radius, 0.0001f);
        strength =
            Mathf.Clamp01(strength);

        if (iterations == 0 ||
            strength <= 0f)
            return;

        Vector3[] vertices =
            mesh.vertices;

        if (vertices.Length == 0)
            return;

        float radiusSqr =
            radius * radius;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Dictionary<Vector3Int, List<int>> buckets =
                BuildVertexBuckets(vertices, radius);
            Vector3[] smoothed =
                new Vector3[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 sum =
                    Vector3.zero;
                int count =
                    0;
                Vector3Int bucket =
                    GetVertexBucket(vertices[i], radius);

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            Vector3Int neighborBucket =
                                new Vector3Int(
                                    bucket.x + dx,
                                    bucket.y + dy,
                                    bucket.z + dz);

                            if (!buckets.TryGetValue(neighborBucket, out List<int> indices))
                                continue;

                            foreach (int index in indices)
                            {
                                if ((vertices[index] - vertices[i]).sqrMagnitude > radiusSqr)
                                    continue;

                                sum += vertices[index];
                                count++;
                            }
                        }
                    }
                }

                if (count <= 1)
                {
                    smoothed[i] = vertices[i];
                    continue;
                }

                smoothed[i] =
                    Vector3.Lerp(
                        vertices[i],
                        sum / count,
                        strength);
            }

            vertices = smoothed;
        }

        mesh.vertices = vertices;
    }

    private Dictionary<Vector3Int, List<int>> BuildVertexBuckets(
        Vector3[] vertices,
        float bucketSize)
    {
        Dictionary<Vector3Int, List<int>> buckets =
            new Dictionary<Vector3Int, List<int>>();

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3Int bucket =
                GetVertexBucket(vertices[i], bucketSize);

            if (!buckets.TryGetValue(bucket, out List<int> indices))
            {
                indices = new List<int>();
                buckets.Add(bucket, indices);
            }

            indices.Add(i);
        }

        return buckets;
    }

    private Vector3Int GetVertexBucket(
        Vector3 vertex,
        float bucketSize)
    {
        return new Vector3Int(
            Mathf.FloorToInt(vertex.x / bucketSize),
            Mathf.FloorToInt(vertex.y / bucketSize),
            Mathf.FloorToInt(vertex.z / bucketSize));
    }

    private IEnumerable<Vector3Int> GetVoxelNeighbors6(
        Vector3Int voxel)
    {
        yield return voxel + new Vector3Int(1, 0, 0);
        yield return voxel + new Vector3Int(-1, 0, 0);
        yield return voxel + new Vector3Int(0, 1, 0);
        yield return voxel + new Vector3Int(0, -1, 0);
        yield return voxel + new Vector3Int(0, 0, 1);
        yield return voxel + new Vector3Int(0, 0, -1);
    }

    private IEnumerable<Vector3Int> GetVoxelNeighbors26(
        Vector3Int voxel)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 &&
                        dy == 0 &&
                        dz == 0)
                        continue;

                    yield return new Vector3Int(
                        voxel.x + dx,
                        voxel.y + dy,
                        voxel.z + dz);
                }
            }
        }
    }

    private GameObject GetOrCreateGeneratedChild(
        string objectName)
    {
        Transform child =
            transform.Find(objectName);

        if (child != null)
            return child.gameObject;

        GameObject created =
            new GameObject(objectName);
        created.transform.SetParent(transform, false);
        created.transform.localPosition = Vector3.zero;
        created.transform.localRotation = Quaternion.identity;
        created.transform.localScale = Vector3.one;

        return created;
    }

    private GameObject GetOrCreateChild(
        GameObject parent,
        string objectName)
    {
        Transform child =
            parent.transform.Find(objectName);

        if (child != null)
            return child.gameObject;

        GameObject created =
            new GameObject(objectName);
        created.transform.SetParent(parent.transform, false);
        created.transform.localPosition = Vector3.zero;
        created.transform.localRotation = Quaternion.identity;
        created.transform.localScale = Vector3.one;

        return created;
    }

    private void CreateCleanPlaneMesh(
        PlaneResult plane,
        List<Vector3> points)
    {
        Transform planeTransform =
            transform.Find(generatedPlaneName);

        GameObject planeObject;

        if (planeTransform == null)
        {
            planeObject = new GameObject(generatedPlaneName);
            planeObject.transform.SetParent(transform, false);
        }
        else
        {
            planeObject = planeTransform.gameObject;
        }

        planeObject.transform.localPosition = Vector3.zero;
        planeObject.transform.localRotation = Quaternion.identity;
        planeObject.transform.localScale = Vector3.one;

        int layer =
            LayerMask.NameToLayer(generatedPlaneLayerName);

        if (!string.IsNullOrEmpty(generatedPlaneLayerName) &&
            layer >= 0)
        {
            planeObject.layer = layer;
        }

        Vector3 normal =
            plane.Normal.normalized;
        Vector3 axisU =
            Vector3.ProjectOnPlane(Vector3.forward, normal);

        if (axisU.sqrMagnitude < PlaneEpsilon)
            axisU = Vector3.ProjectOnPlane(Vector3.right, normal);

        axisU.Normalize();

        Vector3 axisV =
            Vector3.Cross(normal, axisU).normalized;

        float minU = float.PositiveInfinity;
        float maxU = float.NegativeInfinity;
        float minV = float.PositiveInfinity;
        float maxV = float.NegativeInfinity;

        foreach (int index in plane.Inliers)
        {
            Vector3 projected =
                ProjectPointOnPlane(points[index], normal, plane.Distance);
            float u =
                Vector3.Dot(projected, axisU);
            float v =
                Vector3.Dot(projected, axisV);

            minU = Mathf.Min(minU, u);
            maxU = Mathf.Max(maxU, u);
            minV = Mathf.Min(minV, v);
            maxV = Mathf.Max(maxV, v);
        }

        minU -= planeMargin;
        maxU += planeMargin;
        minV -= planeMargin;
        maxV += planeMargin;

        ExpandToMinSize(ref minU, ref maxU, minPlaneSize);
        ExpandToMinSize(ref minV, ref maxV, minPlaneSize);

        Vector3 center =
            -normal * plane.Distance;

        Vector3 v0 =
            center + axisU * minU + axisV * minV;
        Vector3 v1 =
            center + axisU * maxU + axisV * minV;
        Vector3 v2 =
            center + axisU * minU + axisV * maxV;
        Vector3 v3 =
            center + axisU * maxU + axisV * maxV;

        Mesh mesh =
            new Mesh
            {
                name = generatedPlaneName + "Mesh",
                vertices = new[] { v0, v1, v2, v3 },
                triangles = new[] { 0, 1, 2, 1, 3, 2 },
                normals = new[] { normal, normal, normal, normal },
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f)
                }
            };

        mesh.RecalculateBounds();

        MeshFilter meshFilter =
            planeObject.GetComponent<MeshFilter>();

        if (meshFilter == null)
            meshFilter = planeObject.AddComponent<MeshFilter>();

        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer =
            planeObject.GetComponent<MeshRenderer>();

        if (meshRenderer == null)
            meshRenderer = planeObject.AddComponent<MeshRenderer>();

        meshRenderer.sharedMaterial =
            CreatePlaneMaterial();

        if (addMeshCollider)
        {
            MeshCollider meshCollider =
                planeObject.GetComponent<MeshCollider>();

            if (meshCollider == null)
                meshCollider = planeObject.AddComponent<MeshCollider>();

            meshCollider.sharedMesh = mesh;
        }
    }

    private Vector3 ProjectPointOnPlane(
        Vector3 point,
        Vector3 normal,
        float distance)
    {
        float signedDistance =
            Vector3.Dot(normal, point) + distance;

        return point - normal * signedDistance;
    }

    private void ExpandToMinSize(
        ref float min,
        ref float max,
        float minSize)
    {
        float size =
            max - min;

        if (size >= minSize)
            return;

        float center =
            (min + max) * 0.5f;
        float halfSize =
            minSize * 0.5f;

        min = center - halfSize;
        max = center + halfSize;
    }

    private Material CreatePlaneMaterial()
    {
        return CreateTransparentMaterial(generatedPlaneColor);
    }

    private Material CreateTransparentMaterial(
        Color color)
    {
        Shader shader =
            Shader.Find("Standard");

        Material material =
            new Material(shader);

        material.color = color;
        material.SetFloat("_Mode", 3f);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;

        return material;
    }

    private class SurfaceCell
    {
        private float sumY;
        private float minY = float.PositiveInfinity;
        private float maxY = float.NegativeInfinity;

        public int Count { get; private set; }

        public float AverageY =>
            sumY / Count;

        public float MaxY =>
            maxY;

        public float HeightRange =>
            maxY - minY;

        public void Add(float y)
        {
            sumY += y;
            minY = Mathf.Min(minY, y);
            maxY = Mathf.Max(maxY, y);
            Count++;
        }
    }

    private class HorizontalSurfaceLayer
    {
        public Dictionary<Vector2Int, HorizontalSurfaceCell> Cells { get; } =
            new Dictionary<Vector2Int, HorizontalSurfaceCell>();

        public int PointCount { get; private set; }

        public void Add(
            Vector2Int key,
            float height)
        {
            if (!Cells.TryGetValue(key, out HorizontalSurfaceCell cell))
            {
                cell = new HorizontalSurfaceCell();
                Cells.Add(key, cell);
            }

            cell.Add(height);
            PointCount++;
        }
    }

    private class HorizontalSurfaceCell
    {
        private float sumHeight;

        public int Count { get; private set; }

        public float AverageHeight =>
            Count == 0 ? 0f : sumHeight / Count;

        public void Add(
            float height)
        {
            sumHeight += height;
            Count++;
        }
    }

    private class HorizontalColumnStats
    {
        private readonly HashSet<int> occupiedLayers =
            new HashSet<int>();
        private float minHeight =
            float.PositiveInfinity;
        private float maxHeight =
            float.NegativeInfinity;

        public float MaxHeight =>
            maxHeight;

        public float HeightRange =>
            maxHeight - minHeight;

        public int OccupiedLayerCount =>
            occupiedLayers.Count;

        public void Add(
            float height,
            int layerKey)
        {
            minHeight = Mathf.Min(minHeight, height);
            maxHeight = Mathf.Max(maxHeight, height);
            occupiedLayers.Add(layerKey);
        }
    }

    private class GeneratedNavMeshDataOwner : MonoBehaviour
    {
        private NavMeshData navMeshData;
        private NavMeshDataInstance navMeshDataInstance;

        public void SetNavMeshData(
            NavMeshData data)
        {
            RemoveNavMeshData();
            navMeshData = data;

            if (isActiveAndEnabled)
                AddNavMeshData();
        }

        private void OnEnable()
        {
            AddNavMeshData();
        }

        private void OnDisable()
        {
            RemoveNavMeshData();
        }

        private void OnDestroy()
        {
            RemoveNavMeshData();
        }

        private void AddNavMeshData()
        {
            if (navMeshData == null ||
                navMeshDataInstance.valid)
            {
                return;
            }

            navMeshDataInstance =
                NavMesh.AddNavMeshData(navMeshData);
        }

        private void RemoveNavMeshData()
        {
            if (!navMeshDataInstance.valid)
                return;

            navMeshDataInstance.Remove();
            navMeshDataInstance =
                new NavMeshDataInstance();
        }
    }

    private class VoxelCell
    {
        private Vector3 min;
        private Vector3 max;

        public int Count { get; private set; }

        public VoxelCell(
            Vector3Int key,
            float voxelSize)
        {
            min =
                new Vector3(
                    key.x * voxelSize,
                    key.y * voxelSize,
                    key.z * voxelSize);
            max =
                min + Vector3.one * voxelSize;
        }

        public void Add(
            Vector3 point)
        {
            if (Count == 0)
            {
                min = point;
                max = point;
            }
            else
            {
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            Count++;
        }

        public Vector3 GetPaddedMin(
            float padding)
        {
            return min - Vector3.one * Mathf.Max(padding, 0f);
        }

        public Vector3 GetPaddedMax(
            float padding)
        {
            return max + Vector3.one * Mathf.Max(padding, 0f);
        }
    }

    private struct PointCloudObjectSettings
    {
        public readonly float VoxelSize;
        public readonly float SurfacePadding;
        public readonly int MinVoxelPoints;
        public readonly bool RemoveSparseNoise;
        public readonly int MinSparseNeighborCount;
        public readonly bool RemoveSmallClusters;
        public readonly int MinClusterSize;
        public readonly bool FillSmallGaps;
        public readonly int MinGapNeighborCount;
        public readonly int ExpansionVoxels;
        public readonly int SmoothIterations;
        public readonly float SmoothStrength;

        public PointCloudObjectSettings(
            float voxelSize,
            float surfacePadding,
            int minVoxelPoints,
            bool removeSparseNoise,
            int minSparseNeighborCount,
            bool removeSmallClusters,
            int minClusterSize,
            bool fillSmallGaps,
            int minGapNeighborCount,
            int expansionVoxels,
            int smoothIterations,
            float smoothStrength)
        {
            VoxelSize = Mathf.Max(voxelSize, 0.005f);
            SurfacePadding = Mathf.Max(surfacePadding, 0f);
            MinVoxelPoints = Mathf.Max(minVoxelPoints, 1);
            RemoveSparseNoise = removeSparseNoise;
            MinSparseNeighborCount = Mathf.Max(minSparseNeighborCount, 0);
            RemoveSmallClusters = removeSmallClusters;
            MinClusterSize = Mathf.Max(minClusterSize, 1);
            FillSmallGaps = fillSmallGaps;
            MinGapNeighborCount = Mathf.Max(minGapNeighborCount, 1);
            ExpansionVoxels = Mathf.Max(expansionVoxels, 0);
            SmoothIterations = Mathf.Max(smoothIterations, 0);
            SmoothStrength = Mathf.Clamp01(smoothStrength);
        }
    }

    private struct PlaneResult
    {
        public readonly Vector3 Normal;
        public readonly float Distance;
        public readonly List<int> Inliers;

        public int InlierCount =>
            Inliers == null ? 0 : Inliers.Count;

        public PlaneResult(
            Vector3 normal,
            float distance,
            List<int> inliers)
        {
            Normal = normal;
            Distance = distance;
            Inliers = inliers;
        }
    }
}
