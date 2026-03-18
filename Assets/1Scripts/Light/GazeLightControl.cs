using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

using K4AImage = Microsoft.Azure.Kinect.Sensor.Image;

public class GazeLightControl : MonoBehaviour
{
    [Header("Preview")]
    public RawImage viewerRawImage;

    [Header("Target Mesh")]
    public GameObject meshObject;

    [Header("Kinect Transform (Room Coordinate)")]
    public Transform kinectTransform;

    [Header("Ray Settings")]
    public float rayDistance = 100f;
    public float rayInterval = 0.05f;

    [Header("Camera Follow")]
    public bool enableSmoothCamera = true;
    public float cameraLerpSpeed = 15f;

    [Header("Heat Parameters")]
    public float paintRadius = 0.3f;
    public float heatPerHit = 0.2f;
    public float heatDecayPerSec = 0.02f;
    public float colorUpdateInterval = 0.2f;
    public float maxHeatDisplay = 5f;

    [Header("Light Gaze Control")]
    public float gazeLightDelay = 0.5f;

    Device kinect;
    Tracker tracker;

    Vector3 headPos;
    Vector3 viewDir;
    Quaternion headRot;

    Mesh targetMesh;
    Vector3[] verts;
    Color[] colors;
    float[] vertHeat;

    float nextRayT;
    float nextClrT;

    Texture2D previewTex;

    float lastLookTime1 = -10f;
    float lastLookTime2 = -10f;
    float lastLookTime3 = -10f;

    LightControl lightControl;

    async void Start()
    {
        if (!InitMesh())
        {
            enabled = false;
            return;
        }

        lightControl = FindObjectOfType<LightControl>();

        InitKinect();

        tracker = Tracker.Create(
            kinect.GetCalibration(),
            TrackerConfiguration.Default
        );

        while (true)
        {
            try
            {
                using Capture cap =
                    await Task.Run(() => kinect.GetCapture())
                    .ConfigureAwait(true);

                ShowPreview(cap);
                UpdateSkeleton(cap);
            }
            catch
            {
                break;
            }

            if (enableSmoothCamera)
                SmoothCamera();

            HeatAndGazeLogic();

            await Task.Yield();
        }
    }

    void OnDestroy()
    {
        kinect?.StopCameras();
        kinect?.Dispose();
        tracker?.Dispose();
    }

    bool InitMesh()
    {
        if (!meshObject)
            return false;

        MeshFilter mf = meshObject.GetComponent<MeshFilter>();

        if (!mf)
            return false;

        targetMesh = mf.mesh;

        verts = targetMesh.vertices;

        colors =
            Enumerable.Repeat(new Color(0, 0, 0, 0), verts.Length)
            .ToArray();

        vertHeat = new float[verts.Length];

        targetMesh.colors = colors;

        var col =
            meshObject.GetComponent<MeshCollider>()
            ?? meshObject.AddComponent<MeshCollider>();

        col.sharedMesh = targetMesh;

        return true;
    }

    void InitKinect()
    {
        kinect = Device.Open(0);

        kinect.StartCameras(new DeviceConfiguration
        {
            ColorFormat = ImageFormat.ColorBGRA32,
            ColorResolution = ColorResolution.R1080p,
            DepthMode = DepthMode.NFOV_2x2Binned,
            SynchronizedImagesOnly = true,
            CameraFPS = FPS.FPS15
        });
    }

    void ShowPreview(Capture cap)
    {
        if (!viewerRawImage)
            return;

        var img = cap.Color;

        int w = img.WidthPixels;
        int h = img.HeightPixels;

        var src = img.GetPixels<BGRA>().ToArray();

        if (previewTex == null
            || previewTex.width != w
            || previewTex.height != h)
        {
            previewTex = new Texture2D(
                w,
                h,
                TextureFormat.BGRA32,
                false
            );

            viewerRawImage.texture = previewTex;
        }

        Color32[] dst = new Color32[src.Length];

        for (int i = 0; i < src.Length; ++i)
        {
            var p = src[src.Length - 1 - i];

            dst[i] =
                new Color32(p.R, p.G, p.B, p.A);
        }

        previewTex.SetPixels32(dst);
        previewTex.Apply();
    }

    Vector3 ConvertToRoomCoordinate(Vector3 local)
    {
        if (kinectTransform == null)
            return local;

        return kinectTransform.TransformPoint(local);
    }

    void UpdateSkeleton(Capture cap)
    {
        tracker.EnqueueCapture(cap);

        using var fr = tracker.PopResult();

        if (fr == null || fr.NumberOfBodies == 0)
            return;

        var sk = fr.GetBodySkeleton(0);

        Vector3 headLocal =
            new Vector3(
                sk.GetJoint(JointId.Head).Position.X / 1000f,
                -sk.GetJoint(JointId.Head).Position.Y / 1000f,
                sk.GetJoint(JointId.Head).Position.Z / 1000f
            );

        Vector3 noseLocal =
            new Vector3(
                sk.GetJoint(JointId.Nose).Position.X / 1000f,
                -sk.GetJoint(JointId.Nose).Position.Y / 1000f,
                sk.GetJoint(JointId.Nose).Position.Z / 1000f
            );

        headPos =
            ConvertToRoomCoordinate(headLocal);

        Vector3 nose =
            ConvertToRoomCoordinate(noseLocal);

        viewDir =
            (nose - headPos
            + new Vector3(0, -0.05f, 0))
            .normalized;

        headRot =
            Quaternion.LookRotation(
                viewDir,
                Vector3.up
            );
    }

    void SmoothCamera()
    {
        var cam = Camera.main;

        cam.transform.position =
            Vector3.Lerp(
                cam.transform.position,
                headPos,
                Time.deltaTime * cameraLerpSpeed
            );

        cam.transform.rotation =
            Quaternion.Slerp(
                cam.transform.rotation,
                headRot,
                Time.deltaTime * cameraLerpSpeed
            );
    }

    void HeatAndGazeLogic()
    {
        if (Time.time >= nextRayT)
        {
            nextRayT = Time.time + rayInterval;

            Ray ray = new Ray(headPos, viewDir);

            Debug.DrawRay(
                headPos,
                viewDir * rayDistance,
                Color.red
            );

            if (Physics.Raycast(
                ray,
                out var hit,
                rayDistance))
            {
                if (
                    meshObject
                    && hit.collider.transform
                    == meshObject.transform
                )
                {
                    AddHeat(hit.point);
                }

                string objName =
                    hit.collider.gameObject.name.Trim();

                if (objName == "Light1")
                {
                    lastLookTime1 = Time.time;
                    lightControl?.SetLight1(true);
                }

                if (objName == "Light2")
                {
                    lastLookTime2 = Time.time;
                    lightControl?.SetLight2(true);
                }

                if (objName == "Light3")
                {
                    lastLookTime3 = Time.time;
                    lightControl?.SetLight3(true);
                }
            }
        }

        if (Time.time - lastLookTime1 > gazeLightDelay)
            lightControl?.SetLight1(false);

        if (Time.time - lastLookTime2 > gazeLightDelay)
            lightControl?.SetLight2(false);

        if (Time.time - lastLookTime3 > gazeLightDelay)
            lightControl?.SetLight3(false);

        if (Time.time >= nextClrT)
        {
            nextClrT =
                Time.time + colorUpdateInterval;

            DecayHeat();
            ApplyHeatColors();
        }
    }

    void AddHeat(Vector3 hitW)
    {
        float r2 =
            paintRadius * paintRadius;

        for (int i = 0; i < verts.Length; ++i)
        {
            Vector3 w =
                meshObject.transform
                .TransformPoint(verts[i]);

            if (
                (w - hitW).sqrMagnitude <= r2
            )
                vertHeat[i] += heatPerHit;
        }
    }

    void DecayHeat()
    {
        float dec =
            heatDecayPerSec
            * colorUpdateInterval;

        for (int i = 0; i < vertHeat.Length; ++i)
            vertHeat[i] =
                Mathf.Max(
                    0,
                    vertHeat[i] - dec
                );
    }

    void ApplyHeatColors()
    {
        for (int i = 0; i < verts.Length; ++i)
            colors[i].a =
                Mathf.Clamp01(
                    vertHeat[i]
                    / maxHeatDisplay
                );

        targetMesh.colors = colors;
    }
}
