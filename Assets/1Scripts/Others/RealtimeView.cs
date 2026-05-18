using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;

using System.Collections.Generic;
using System.Threading.Tasks;

using UnityEngine;
using Unity.Barracuda;

using TMPro;

public class KinectCombinedViewer : MonoBehaviour
{
    [Header("UI")]
    [SerializeField]
    private TMP_Text behaviorText;

    [Header("Model")]
    [SerializeField]
    private NNModel modelAsset;

    [Header("Joint")]
    [SerializeField]
    private GameObject jointPrefab;

    // =========================
    // Kinect
    // =========================

    private Device _kinectDevice;

    private Tracker _tracker;

    // =========================
    // Joint Visual
    // =========================

    private Dictionary<JointId, GameObject>
        jointObjects =
            new Dictionary<JointId, GameObject>();

    // =========================
    // Barracuda
    // =========================

    private Model runtimeModel;

    private IWorker worker;

    // =========================
    // Buffer
    // =========================

    private Queue<float[]>
        frameBuffer =
            new Queue<float[]>();

    private const int WINDOW_SIZE = 30;

    // =========================
    // Joint Order
    // =========================

    private readonly JointId[] useJoints =
    {
        JointId.Pelvis,
        JointId.SpineChest,
        JointId.Head,

        JointId.ShoulderLeft,
        JointId.ElbowLeft,
        JointId.WristLeft,

        JointId.ShoulderRight,
        JointId.ElbowRight,
        JointId.WristRight,

        JointId.HipLeft,
        JointId.KneeLeft,
        JointId.AnkleLeft,

        JointId.HipRight,
        JointId.KneeRight,
        JointId.AnkleRight
    };

    // =========================
    // Label Order
    // Python:
    // ['crawl' 'sit' 'walk']
    // =========================

    private readonly string[] labels =
    {
        "CRAWL",
        "SIT",
        "WALK"
    };

    // =========================
    // Start
    // =========================

    private void Start()
    {
        InitKinect();

        InitBarracuda();

        StartLoop();
    }

    // =========================
    // Kinect Init
    // =========================

    private void InitKinect()
    {
        _kinectDevice = Device.Open(0);

        _kinectDevice.StartCameras(
            new DeviceConfiguration
            {
                ColorFormat =
                    ImageFormat.ColorBGRA32,

                ColorResolution =
                    ColorResolution.R720p,

                DepthMode =
                    DepthMode.NFOV_2x2Binned,

                CameraFPS =
                    FPS.FPS30,

                SynchronizedImagesOnly =
                    true
            }
        );

        _tracker =
            Tracker.Create(
                _kinectDevice.GetCalibration(),
                TrackerConfiguration.Default
            );

        Debug.Log("Kinect Started");
    }

    // =========================
    // Barracuda Init
    // =========================

    private void InitBarracuda()
    {
        runtimeModel =
            ModelLoader.Load(modelAsset);

        worker =
            WorkerFactory.CreateWorker(
                WorkerFactory.Type.Auto,
                runtimeModel
            );

        Debug.Log("Behavior Model Loaded");
    }

    // =========================
    // Main Loop
    // =========================

    private async void StartLoop()
    {
        while (true)
        {
            try
            {
                using (
                    Capture capture =
                    await Task.Run(
                        () => _kinectDevice.GetCapture()
                    )
                )
                {
                    CaptureFrame(capture);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError(ex.Message);

                break;
            }

            await Task.Yield();
        }
    }

    // =========================
    // Capture
    // =========================

    private void CaptureFrame(
        Capture capture
    )
    {
        _tracker.EnqueueCapture(capture);

        var frame =
            _tracker.PopResult();

        if (
            frame == null ||
            frame.NumberOfBodies <= 0
        )
        {
            return;
        }

        var skeleton =
            frame.GetBodySkeleton(0);

        Dictionary<JointId, Vector3>
            jointPositions =
                new Dictionary<JointId, Vector3>();

        foreach (
            var jointId
            in useJoints
        )
        {
            var joint =
                skeleton.GetJoint(jointId);

            Vector3 pos =
                new Vector3(
                    joint.Position.X / 1000f,
                    joint.Position.Y / 1000f,
                    joint.Position.Z / 1000f
                );

            jointPositions[jointId] =
                pos;

            // 可視化
            if (
                !jointObjects.ContainsKey(jointId)
            )
            {
                var obj =
                    Instantiate(
                        jointPrefab,
                        pos,
                        Quaternion.identity
                    );

                obj.name =
                    jointId.ToString();

                obj.transform.localScale =
                    Vector3.one * 0.05f;

                jointObjects[jointId] =
                    obj;
            }
            else
            {
                jointObjects[jointId]
                    .transform.position = pos;
            }
        }

        AddSkeletonFrame(
            jointPositions
        );
    }

    // =========================
    // Buffer
    // =========================

    private void AddSkeletonFrame(
        Dictionary<JointId, Vector3> joints
    )
    {
        if (
            !joints.ContainsKey(
                JointId.Pelvis
            )
        )
        {
            return;
        }

        Vector3 pelvis =
            joints[JointId.Pelvis];

        List<float> features =
            new List<float>();

        foreach (var joint in useJoints)
        {
            if (!joints.ContainsKey(joint))
            {
                features.Add(0);
                features.Add(0);
                features.Add(0);

                continue;
            }

            Vector3 rel =
                joints[joint] - pelvis;

            features.Add(rel.x);
            features.Add(rel.y);
            features.Add(rel.z);
        }

        frameBuffer.Enqueue(
            features.ToArray()
        );

        Debug.Log(
            $"Buffer Count = {frameBuffer.Count}"
        );

        while (
            frameBuffer.Count >
            WINDOW_SIZE
        )
        {
            frameBuffer.Dequeue();
        }

        if (
            frameBuffer.Count ==
            WINDOW_SIZE
        )
        {
            RunInference();
        }
    }

    // =========================
    // Inference
    // =========================

    private void RunInference()
    {
        Debug.Log("RunInference!");

        float[] inputData =
            new float[
                WINDOW_SIZE * 45
            ];

        int index = 0;

        foreach (
            var frame
            in frameBuffer
        )
        {
            foreach (var v in frame)
            {
                inputData[index++] = v;
            }
        }

        // Barracuda:
        // (n:1, h:1, w:45, c:20)

        Tensor inputTensor =
            new Tensor(
                1,
                1,
                45,
                WINDOW_SIZE,
                inputData
            );

        worker.Execute(inputTensor);

        Tensor output =
            worker.PeekOutput();

        int[] argmax =
            output.ArgMax();

        int predicted =
            argmax[0];

        Debug.Log(
            $"crawl={output[0]} " +
            $"sit={output[1]} " +
            $"walk={output[2]}"
        );

        Debug.Log(
            $"predicted={predicted}"
        );

        string label =
            labels[predicted];

        Debug.Log(
            $"LABEL = {label}"
        );

        behaviorText.text =
            label;

        inputTensor.Dispose();

        output.Dispose();
    }

    // =========================
    // Cleanup
    // =========================

    private void OnDestroy()
    {
        worker?.Dispose();

        _tracker?.Dispose();

        if (_kinectDevice != null)
        {
            _kinectDevice.StopCameras();

            _kinectDevice.Dispose();
        }
    }
}