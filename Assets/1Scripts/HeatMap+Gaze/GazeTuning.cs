using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GazeTuning : MonoBehaviour
{
    [Header("Kinect")]
    public Transform kinectTransform;

    [Header("Targets")]
    public Transform pointA;
    public Transform pointB;
    public Transform pointC;
    public Transform pointD;
    public Transform pointE;
    public Transform pointF;
    public Transform pointG;
    public Transform pointH;
    public Transform pointI;

    [Header("UI")]
    public TMP_Text resultText;

    [Header("Buttons")]
    public Button pointAButton;
    public Button pointBButton;
    public Button pointCButton;
    public Button pointDButton;
    public Button pointEButton;
    public Button pointFButton;
    public Button pointGButton;
    public Button pointHButton;
    public Button pointIButton;

    [Header("Raycast")]
    public float rayDistance = 10f;

    [Header("Gaze Angle Correction")]
    [Tooltip("Positive values move the plotted gaze direction downward.")]
    [Range(0f, 90f)]
    public float downwardCorrectionAngle = 0f;

    [Header("Visualization")]
    public GameObject hitSpherePrefab;

    [Header("Target Mesh")]
    public GameObject targetMeshObject;

    // =========================
    // Kinect
    // =========================

    private Device kinect;
    private Tracker tracker;

    // =========================
    // Current Skeleton
    // =========================

    private Vector3 headPos;
    private Vector3 rawDir;

    // =========================
    // Calibration
    // =========================

    private float totalPitchOffset = 0f;
    private int sampleCount = 0;

    // =========================

    async void Start()
    {
        InitKinect();

        tracker = Tracker.Create(
            kinect.GetCalibration(),
            TrackerConfiguration.Default
        );

        // ボタン登録
        RegisterPointButton(pointAButton, pointA, "A");
        RegisterPointButton(pointBButton, pointB, "B");
        RegisterPointButton(pointCButton, pointC, "C");
        RegisterPointButton(pointDButton, pointD, "D");
        RegisterPointButton(pointEButton, pointE, "E");
        RegisterPointButton(pointFButton, pointF, "F");
        RegisterPointButton(pointGButton, pointG, "G");
        RegisterPointButton(pointHButton, pointH, "H");
        RegisterPointButton(pointIButton, pointI, "I");

        // Kinect Loop
        while (true)
        {
            try
            {
                using Capture cap =
                    await Task.Run(() => kinect.GetCapture())
                    .ConfigureAwait(true);

                UpdateSkeleton(cap);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                break;
            }

            await Task.Yield();
        }
    }

    // =========================

    void RegisterPointButton(Button button, Transform target, string label)
    {
        if (button == null)
            return;

        button.onClick.AddListener(() => CapturePoint(target, label));
    }

    // =========================

    void OnDestroy()
    {
        kinect?.StopCameras();
        kinect?.Dispose();
        tracker?.Dispose();
    }

    // =========================

    void InitKinect()
    {
        kinect = Device.Open(0);

        kinect.StartCameras(new DeviceConfiguration
        {
            ColorFormat = ImageFormat.ColorBGRA32,
            ColorResolution = ColorResolution.R720p,
            DepthMode = DepthMode.NFOV_2x2Binned,
            SynchronizedImagesOnly = true,
            CameraFPS = FPS.FPS30
        });
    }

    // =========================
    // Kinect Local → Room
    // =========================

    Vector3 ConvertToRoomCoordinate(Vector3 local)
    {
        if (kinectTransform == null)
            return local;

        return kinectTransform.TransformPoint(local);
    }

    // =========================

    void UpdateSkeleton(Capture cap)
    {
        tracker.EnqueueCapture(cap);

        using var frame = tracker.PopResult();

        if (frame == null || frame.NumberOfBodies == 0)
            return;

        Skeleton skeleton =
            frame.GetBodySkeleton(0);

        Vector3 headLocal =
            new Vector3(
                skeleton.GetJoint(JointId.Head).Position.X / 1000f,
                -skeleton.GetJoint(JointId.Head).Position.Y / 1000f,
                skeleton.GetJoint(JointId.Head).Position.Z / 1000f
            );

        Vector3 noseLocal =
            new Vector3(
                skeleton.GetJoint(JointId.Nose).Position.X / 1000f,
                -skeleton.GetJoint(JointId.Nose).Position.Y / 1000f,
                skeleton.GetJoint(JointId.Nose).Position.Z / 1000f
            );

        headPos =
            ConvertToRoomCoordinate(headLocal);

        Vector3 nosePos =
            ConvertToRoomCoordinate(noseLocal);

        // 視線方向
        rawDir =
            (nosePos - headPos).normalized;

        Vector3 correctedDir =
            ApplyDownwardCorrection(rawDir);

        // Debug表示
        Debug.DrawRay(
            headPos,
            correctedDir * rayDistance,
            Color.red
        );
    }

    // =========================

    Vector3 ApplyDownwardCorrection(Vector3 direction)
    {
        if (direction.sqrMagnitude < Mathf.Epsilon)
            return direction;

        direction.Normalize();

        Vector3 rightAxis =
            Vector3.Cross(Vector3.up, direction);

        if (rightAxis.sqrMagnitude < Mathf.Epsilon)
            rightAxis = Vector3.right;
        else
            rightAxis.Normalize();

        return (
            Quaternion.AngleAxis(
                downwardCorrectionAngle,
                rightAxis
            ) * direction
        ).normalized;
    }

    // =========================
    // キャリブレーション + 可視化
    // =========================

    void CapturePoint(Transform target, string label)
    {
        if (target == null)
            return;

        // =========================
        // 本来の方向
        // =========================

        Vector3 targetDir =
            (target.position - headPos).normalized;

        Vector3 correctedDir =
            ApplyDownwardCorrection(rawDir);

        // =========================
        // Pitch角
        // =========================

        float measuredPitch =
            Mathf.Asin(correctedDir.y) * Mathf.Rad2Deg;

        float targetPitch =
            Mathf.Asin(targetDir.y) * Mathf.Rad2Deg;

        float measuredYaw =
            Mathf.Atan2(correctedDir.x, correctedDir.z) * Mathf.Rad2Deg;

        float targetYaw =
            Mathf.Atan2(targetDir.x, targetDir.z) * Mathf.Rad2Deg;

        // =========================
        // 誤差
        // =========================

        float pitchError =
            targetPitch - measuredPitch;

        float yawError =
            Mathf.DeltaAngle(measuredYaw, targetYaw);

        totalPitchOffset += pitchError;

        sampleCount++;

        float averageOffset =
            totalPitchOffset / sampleCount;

        // =========================
        // Raycast
        // =========================

        Ray ray =
            new Ray(headPos, correctedDir);

        bool hitDetected = false;

        Vector3 hitPoint = Vector3.zero;

        if (
            Physics.Raycast(
                ray,
                out RaycastHit hit,
                rayDistance
            )
        )
        {
            hitDetected = true;

            hitPoint = hit.point;

            Debug.Log(
                $"Hit : {hit.collider.name}"
            );

            // =========================
            // Sphere生成
            // =========================

            if (hitSpherePrefab != null)
            {
                Instantiate(
                    hitSpherePrefab,
                    hitPoint,
                    Quaternion.identity
                );
            }

            // Debug
            Debug.DrawLine(
                target.position,
                hitPoint,
                Color.blue,
                5f
            );
        }

        // =========================
        // ターゲットとの差
        // =========================

        float distanceError = -1f;

        if (hitDetected)
        {
            distanceError =
                Vector3.Distance(
                    target.position,
                    hitPoint
                );
        }

        // =========================
        // ログ
        // =========================

        Debug.Log(
            $"[{label}] " +
            $"MeasuredPitch={measuredPitch:F2}°, " +
            $"TargetPitch={targetPitch:F2}°, " +
            $"PitchError={pitchError:F2}°, " +
            $"AverageOffset={averageOffset:F2}°, " +
            $"DistanceError={distanceError:F3}m"
        );

        Debug.Log(
            $"[{label}] MeasuredYaw={measuredYaw:F2}deg, " +
            $"TargetYaw={targetYaw:F2}deg, " +
            $"YawError={yawError:F2}deg"
        );

        Debug.Log(
            $"[{label}] DownwardCorrection={downwardCorrectionAngle:F2}deg"
        );

        // =========================
        // UI
        // =========================

        if (resultText != null)
        {
            resultText.text =
                $"Label : {label}\n" +
                $"Samples : {sampleCount}\n\n" +

                $"Measured Pitch : {measuredPitch:F2}°\n" +
                $"Target Pitch : {targetPitch:F2}°\n" +
                $"Pitch Error : {pitchError:F2}°\n\n" +

                $"Average Offset : {averageOffset:F2}°\n\n" +

                $"Distance Error : {distanceError:F3} m";

            resultText.text +=
                $"\n\nMeasured Yaw : {measuredYaw:F2} deg\n" +
                $"Target Yaw : {targetYaw:F2} deg\n" +
                $"Yaw Error : {yawError:F2} deg\n\n" +
                $"Downward Correction : {downwardCorrectionAngle:F2} deg";
        }
    }

    // =========================
    // 補正値取得
    // =========================

    public float GetAveragePitchOffset()
    {
        if (sampleCount == 0)
            return 0f;

        return totalPitchOffset / sampleCount;
    }

    // =========================
    // 補正済み視線
    // =========================

    public Vector3 GetCorrectedDirection()
    {
        float avg =
            GetAveragePitchOffset();

        Quaternion correction =
            Quaternion.Euler(avg, 0, 0);

        return ApplyDownwardCorrection(correction * rawDir);
    }
}
