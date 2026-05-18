using System.Collections.Generic;
using Unity.Barracuda;
using UnityEngine;
using TMPro;
using Microsoft.Azure.Kinect.BodyTracking;

public class BehaviorInference : MonoBehaviour
{
    [Header("Model")]
    public NNModel modelAsset;

    [Header("UI")]
    public TMP_Text behaviorText;

    // =========================
    // Barracuda
    // =========================

    private Model runtimeModel;

    private IWorker worker;

    // =========================
    // Skeleton Buffer
    // =========================

    private Queue<float[]> frameBuffer =
        new Queue<float[]>();

    private const int WINDOW_SIZE = 20;

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
    // Start
    // =========================

    void Start()
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
    // Add Frame
    // =========================

    public void AddSkeletonFrame(
        Dictionary<JointId, Vector3> joints
    )
    {
        if (!joints.ContainsKey(JointId.Pelvis))
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

            Vector3 p = joints[joint];

            Vector3 rel =
                p - pelvis;

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

        // window維持
        while (
            frameBuffer.Count >
            WINDOW_SIZE
        )
        {
            frameBuffer.Dequeue();
        }

        // 推論
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

        foreach (var frame in frameBuffer)
        {
            foreach (var v in frame)
            {
                inputData[index++] = v;
            }
        }

        // =========================
        // IMPORTANT
        // Barracuda shape:
        // (n:1, h:1, w:45, c:20)
        // =========================

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

        string[] labels =
        {
            "CRAWL",
            "SIT",
            "WALK"
        };

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
    }
}