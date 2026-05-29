using UnityEngine;

public static class ModelViewerBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateViewerIfMissing()
    {
        if (Object.FindObjectOfType<RuntimeModelViewer>() != null)
        {
            return;
        }

        GameObject viewer = new GameObject("Runtime Model Viewer");
        viewer.AddComponent<RuntimeModelViewer>();
        Object.DontDestroyOnLoad(viewer);
    }
}
