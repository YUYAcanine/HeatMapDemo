using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;

public class ShowAccidents : MonoBehaviour
{
    [Header("Avatar")]
    [SerializeField] private Transform avatar;

    [Header("Objects")]
    [SerializeField] private List<GameObject> accidentObjects = new();
    [SerializeField] private LayerMask accidentObjectLayerMask = ~0;
    [SerializeField] private string accidentObjectTag = "";
    [SerializeField] private float surfaceDistance = 0.5f;
    [SerializeField] private int maxObjectsToShow = 3;

    [Header("Text")]
    [SerializeField] private TMP_Text summaryText;
    [SerializeField] private float worldTextYOffset = 0.8f;
    [SerializeField] private float worldTextSize = 0.4f;
    [SerializeField] private Color worldTextColor = Color.white;
    [SerializeField] private bool showWorldText = true;
    [SerializeField] private bool showSummaryText = true;

    [Header("Background")]
    [SerializeField] private bool showBackground = true;
    [SerializeField] private Color backgroundColor = new(0f, 0f, 0f, 0.65f);
    [SerializeField] private Vector2 backgroundPadding = new(0.25f, 0.12f);

    [Header("Accident Data")]
    [SerializeField] private string accidentCsvAssetPath = "Data/Accidents/accidents.csv";

    [Header("Debug")]
    [SerializeField] private bool logAccidentChanges = true;

    private readonly List<TextMeshPro> worldTexts = new();
    private readonly List<SpriteRenderer> backgrounds = new();
    private readonly Dictionary<string, string> accidentTextByFurnitureName =
        new(System.StringComparer.OrdinalIgnoreCase);
    private readonly List<ObjectCandidate> cachedCandidates = new();
    private Sprite backgroundSprite;
    private string lastSummary = "";

    private void Start()
    {
        if (avatar == null)
            avatar = transform;

        LoadAccidentCsv();
        RebuildCandidates();
    }

    private void Update()
    {
        if (avatar == null)
            return;

        List<NearbyAccidentObject> nearbyObjects =
            FindNearbyAccidentObjects(avatar.position);

        ApplyWorldTexts(nearbyObjects);
        ApplySummaryText(nearbyObjects);
        FaceWorldTextsToCamera();
    }

    public void RebuildCandidates()
    {
        cachedCandidates.Clear();
        HashSet<GameObject> addedObjects = new();

        if (accidentObjects != null &&
            accidentObjects.Count > 0)
        {
            foreach (GameObject obj in accidentObjects)
            {
                AddCandidate(obj, addedObjects);
            }

            return;
        }

        foreach (Collider collider in FindObjectsOfType<Collider>())
        {
            if (collider != null)
                AddCandidate(collider.gameObject, addedObjects);
        }

        foreach (Renderer renderer in FindObjectsOfType<Renderer>())
        {
            if (renderer != null)
                AddCandidate(renderer.gameObject, addedObjects);
        }
    }

    private void AddCandidate(
        GameObject obj,
        HashSet<GameObject> addedObjects
    )
    {
        if (obj == null ||
            !obj.activeInHierarchy ||
            addedObjects.Contains(obj) ||
            !IsAccidentObjectAllowed(obj) ||
            IsGeneratedDisplayObject(obj))
            return;

        Collider[] colliders =
            obj.GetComponentsInChildren<Collider>();
        Renderer[] renderers =
            obj.GetComponentsInChildren<Renderer>();

        if (colliders.Length == 0 &&
            renderers.Length == 0)
            return;

        string displayName =
            GetDisplayName(obj);
        string accidentText =
            GetAccidentText(displayName, obj.name);

        if (string.IsNullOrEmpty(accidentText))
            return;

        addedObjects.Add(obj);
        cachedCandidates.Add(
            new ObjectCandidate(
                obj,
                colliders,
                renderers,
                displayName,
                accidentText
            )
        );
    }

    private List<NearbyAccidentObject> FindNearbyAccidentObjects(Vector3 position)
    {
        List<NearbyAccidentObject> results = new();

        foreach (ObjectCandidate candidate in cachedCandidates)
        {
            float distance =
                GetSurfaceDistance(
                    candidate,
                    position,
                    out Vector3 closestPoint
                );

            if (distance > surfaceDistance)
                continue;

            results.Add(
                new NearbyAccidentObject(
                    candidate,
                    distance,
                    closestPoint
                )
            );
        }

        results.Sort(
            (a, b) => a.Distance.CompareTo(b.Distance)
        );

        if (maxObjectsToShow > 0 &&
            results.Count > maxObjectsToShow)
        {
            results.RemoveRange(
                maxObjectsToShow,
                results.Count - maxObjectsToShow
            );
        }

        return results;
    }

    private void ApplyWorldTexts(List<NearbyAccidentObject> nearbyObjects)
    {
        if (!showWorldText)
        {
            ClearWorldTexts();
            return;
        }

        for (int i = 0; i < nearbyObjects.Count; i++)
        {
            TextMeshPro text =
                GetWorldText(i);
            NearbyAccidentObject nearby =
                nearbyObjects[i];

            text.text =
                BuildLabelText(nearby);
            text.fontSize = worldTextSize;
            text.color = worldTextColor;
            text.alignment = TextAlignmentOptions.Center;
            text.transform.position =
                nearby.ClosestPoint + Vector3.up * worldTextYOffset;
            text.enabled = true;

            ApplyBackground(i, text);
        }

        for (int i = nearbyObjects.Count; i < worldTexts.Count; i++)
        {
            if (worldTexts[i] != null)
            {
                worldTexts[i].text = "";
                worldTexts[i].enabled = false;
            }

            SetBackgroundVisible(i, false);
        }
    }

    private void ApplySummaryText(List<NearbyAccidentObject> nearbyObjects)
    {
        if (summaryText == null)
            return;

        if (!showSummaryText ||
            nearbyObjects.Count == 0)
        {
            summaryText.text = "";
            summaryText.enabled = false;
            LogSummaryChange("");
            return;
        }

        string summary =
            BuildSummaryText(nearbyObjects);
        summaryText.text = summary;
        summaryText.enabled = true;
        LogSummaryChange(summary);
    }

    private TextMeshPro GetWorldText(int index)
    {
        while (worldTexts.Count <= index)
        {
            TextMeshPro text =
                new GameObject(
                    $"AccidentText_{worldTexts.Count + 1}"
                ).AddComponent<TextMeshPro>();
            text.text = "";
            text.enabled = false;
            worldTexts.Add(text);

            SpriteRenderer background =
                new GameObject(
                    $"AccidentTextBackground_{backgrounds.Count + 1}"
                ).AddComponent<SpriteRenderer>();
            background.sprite = GetBackgroundSprite();
            background.color = backgroundColor;
            background.enabled = false;
            backgrounds.Add(background);
        }

        return worldTexts[index];
    }

    private void ApplyBackground(
        int index,
        TextMeshPro text
    )
    {
        if (!showBackground ||
            index >= backgrounds.Count ||
            backgrounds[index] == null)
        {
            SetBackgroundVisible(index, false);
            return;
        }

        text.ForceMeshUpdate();
        Vector2 renderedSize =
            text.GetRenderedValues(false);
        Vector2 size =
            new(
                Mathf.Max(renderedSize.x + backgroundPadding.x * 2f, 0.01f),
                Mathf.Max(renderedSize.y + backgroundPadding.y * 2f, 0.01f)
            );

        SpriteRenderer background =
            backgrounds[index];
        background.color = backgroundColor;
        background.transform.position =
            text.transform.position - text.transform.forward * 0.01f;
        background.transform.rotation =
            text.transform.rotation;
        background.transform.localScale =
            new Vector3(size.x, size.y, 1f);
        background.enabled = text.enabled;
    }

    private void FaceWorldTextsToCamera()
    {
        Camera targetCamera =
            Camera.main;

        if (targetCamera == null)
            return;

        Quaternion rotation =
            Quaternion.LookRotation(
                targetCamera.transform.forward,
                targetCamera.transform.up
            );

        for (int i = 0; i < worldTexts.Count; i++)
        {
            TextMeshPro text =
                worldTexts[i];

            if (text == null ||
                !text.enabled)
                continue;

            text.transform.rotation = rotation;

            if (i < backgrounds.Count &&
                backgrounds[i] != null &&
                backgrounds[i].enabled)
            {
                backgrounds[i].transform.rotation = rotation;
                backgrounds[i].transform.position =
                    text.transform.position - text.transform.forward * 0.01f;
            }
        }
    }

    private void ClearWorldTexts()
    {
        for (int i = 0; i < worldTexts.Count; i++)
        {
            if (worldTexts[i] != null)
            {
                worldTexts[i].text = "";
                worldTexts[i].enabled = false;
            }

            SetBackgroundVisible(i, false);
        }
    }

    private void SetBackgroundVisible(
        int index,
        bool visible
    )
    {
        if (index < 0 ||
            index >= backgrounds.Count ||
            backgrounds[index] == null)
            return;

        backgrounds[index].enabled = visible;
    }

    private float GetSurfaceDistance(
        ObjectCandidate candidate,
        Vector3 position,
        out Vector3 closestSurfacePoint
    )
    {
        float minDistance = float.PositiveInfinity;
        closestSurfacePoint = position;

        foreach (Collider collider in candidate.Colliders)
        {
            if (collider == null ||
                !collider.enabled)
                continue;

            Vector3 closestPoint =
                collider.ClosestPoint(position);
            float distance =
                Vector3.Distance(position, closestPoint);

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
                renderer.bounds.ClosestPoint(position);
            float distance =
                Vector3.Distance(position, closestPoint);

            if (distance >= minDistance)
                continue;

            minDistance = distance;
            closestSurfacePoint = closestPoint;
        }

        return minDistance;
    }

    private string BuildLabelText(NearbyAccidentObject nearby)
    {
        return $"{nearby.DisplayName}\n{nearby.AccidentText}";
    }

    private string BuildSummaryText(List<NearbyAccidentObject> nearbyObjects)
    {
        StringBuilder builder = new();
        builder.AppendLine("Nearby accidents");

        foreach (NearbyAccidentObject nearby in nearbyObjects)
        {
            builder.Append(nearby.DisplayName);
            builder.Append(" ");
            builder.Append(nearby.Distance.ToString("F2"));
            builder.AppendLine("m");
            builder.AppendLine(nearby.AccidentText);
        }

        return builder.ToString();
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
            Debug.LogWarning(
                $"ShowAccidents: accident csv not found. path={path}"
            );
            return;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] columns =
                line.Split(',');

            if (columns.Length < 2)
                continue;

            string furnitureName =
                columns[0].Trim();
            string accidentText =
                columns[1].Trim();

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
                out string accidentByDisplayName
            ))
            return accidentByDisplayName;

        if (!string.IsNullOrEmpty(objectName) &&
            accidentTextByFurnitureName.TryGetValue(
                objectName,
                out string accidentByObjectName
            ))
            return accidentByObjectName;

        return "";
    }

    private string GetDisplayName(GameObject obj)
    {
        FurnitureTextData textData =
            obj.GetComponentInParent<FurnitureTextData>();

        if (textData == null)
            textData = obj.GetComponentInChildren<FurnitureTextData>();

        if (textData != null)
            return textData.DisplayText;

        return obj.name;
    }

    private bool IsAccidentObjectAllowed(GameObject obj)
    {
        if ((accidentObjectLayerMask.value & (1 << obj.layer)) == 0)
            return false;

        if (!string.IsNullOrEmpty(accidentObjectTag) &&
            !obj.CompareTag(accidentObjectTag))
            return false;

        return true;
    }

    private bool IsGeneratedDisplayObject(GameObject obj)
    {
        return obj.GetComponent<TextMeshPro>() != null ||
            obj.GetComponent<SpriteRenderer>() != null;
    }

    private Sprite GetBackgroundSprite()
    {
        if (backgroundSprite != null)
            return backgroundSprite;

        Texture2D texture =
            Texture2D.whiteTexture;
        backgroundSprite =
            Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                1f
            );

        return backgroundSprite;
    }

    private void LogSummaryChange(string summary)
    {
        if (!logAccidentChanges ||
            summary == lastSummary)
            return;

        lastSummary = summary;
        Debug.Log(
            string.IsNullOrEmpty(summary)
                ? "ShowAccidents: no nearby accident."
                : $"ShowAccidents:\n{summary}"
        );
    }

    private readonly struct ObjectCandidate
    {
        public ObjectCandidate(
            GameObject obj,
            Collider[] colliders,
            Renderer[] renderers,
            string displayName,
            string accidentText
        )
        {
            Object = obj;
            Colliders = colliders;
            Renderers = renderers;
            DisplayName = displayName;
            AccidentText = accidentText;
        }

        public GameObject Object { get; }
        public Collider[] Colliders { get; }
        public Renderer[] Renderers { get; }
        public string DisplayName { get; }
        public string AccidentText { get; }
    }

    private readonly struct NearbyAccidentObject
    {
        public NearbyAccidentObject(
            ObjectCandidate candidate,
            float distance,
            Vector3 closestPoint
        )
        {
            DisplayName = candidate.DisplayName;
            AccidentText = candidate.AccidentText;
            Distance = distance;
            ClosestPoint = closestPoint;
        }

        public string DisplayName { get; }
        public string AccidentText { get; }
        public float Distance { get; }
        public Vector3 ClosestPoint { get; }
    }
}
