using UnityEngine;

/// <summary>
/// Kept only for backward compatibility with scenes or launch icons that still
/// reference this component. The project now stays inside Unity and uses the
/// native runtime model viewer instead of opening CAD Assistant.
/// </summary>
public sealed class CadAssistantLauncher : MonoBehaviour
{
    private void Start()
    {
        Debug.Log("[CadAssistantLauncher] Disabled. Using RuntimeModelViewer in Unity.");
    }
}
