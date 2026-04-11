using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Gaze/GazeData")]
public class GazeData : ScriptableObject
{
    public List<string> objectNames = new List<string>();
    public List<float> gazeDurations = new List<float>();
}

