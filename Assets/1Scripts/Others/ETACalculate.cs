using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;

public class ETACalculate : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private RouteSub routeSub;

    [Header("ETA")]
    [Tooltip("Movement speed in Unity world units per second")]
    [SerializeField, Min(0.01f)] private float movementSpeed = 1.0f;

    [Header("Display")]
    [SerializeField] private TMP_Text etaText;
    [SerializeField] private string unavailableText = "Unreachable";

    private readonly Dictionary<string, float> routeLengths =
        new Dictionary<string, float>();
    private readonly List<string> sortedLabels = new List<string>();
    private int lastRouteVersion = -1;
    private float lastMovementSpeed = -1f;

    public IReadOnlyDictionary<string, float> RouteLengths => routeLengths;
    public float MovementSpeed => Mathf.Max(movementSpeed, 0.01f);
    public int EtaVersion { get; private set; }

    private void Start()
    {
        RefreshText();
    }

    private void Update()
    {
        RouteSub currentRouteSub = GetRouteSub();
        if (currentRouteSub == null)
        {
            if (etaText != null && etaText.text.Length > 0)
                etaText.text = string.Empty;
            return;
        }

        if (currentRouteSub.RouteVersion == lastRouteVersion &&
            Mathf.Approximately(movementSpeed, lastMovementSpeed))
        {
            return;
        }

        RefreshText();
    }

    public void RefreshText()
    {
        RouteSub currentRouteSub = GetRouteSub();
        if (currentRouteSub == null)
            return;

        currentRouteSub.GetRouteLengths(routeLengths);
        sortedLabels.Clear();
        sortedLabels.AddRange(routeLengths.Keys);
        sortedLabels.Sort(System.StringComparer.OrdinalIgnoreCase);

        float safeSpeed = Mathf.Max(movementSpeed, 0.01f);
        StringBuilder text = new StringBuilder();
        foreach (string label in sortedLabels)
        {
            if (text.Length > 0)
                text.AppendLine();

            float routeLength = routeLengths[label];
            text.Append(label);
            text.Append(": ");
            if (routeLength < 0f)
            {
                text.Append(unavailableText);
            }
            else
            {
                float seconds = routeLength / safeSpeed;
                text.Append(seconds.ToString("F1", CultureInfo.InvariantCulture));
                text.Append('s');
            }
        }

        if (etaText != null)
            etaText.text = text.ToString();

        lastRouteVersion = currentRouteSub.RouteVersion;
        lastMovementSpeed = movementSpeed;
        EtaVersion++;
    }

    private RouteSub GetRouteSub()
    {
        if (routeSub != null)
            return routeSub;

        routeSub = FindObjectOfType<RouteSub>();
        return routeSub;
    }
}
