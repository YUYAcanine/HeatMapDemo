using UnityEngine;

public class FurnitureTextData : MonoBehaviour
{
    [SerializeField] private string displayText = "";

    public string DisplayText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(displayText))
                return gameObject.name;

            return displayText;
        }
    }
}
