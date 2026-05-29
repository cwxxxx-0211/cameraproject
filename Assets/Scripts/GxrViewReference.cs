using GxrSdk;
using UnityEngine;

public static class GxrViewReference
{
    public static Camera ActiveCamera
    {
        get
        {
            if (GxrCameraAccessor.CenterCamera != null)
            {
                return GxrCameraAccessor.CenterCamera;
            }

            if (GxrCameraAccessor.MainCamera != null)
            {
                return GxrCameraAccessor.MainCamera;
            }

            return UnityEngine.Camera.main;
        }
    }

    public static Transform Transform
    {
        get
        {
            Camera camera = ActiveCamera;
            if (camera != null)
            {
                return camera.transform;
            }

            if (GxrInputAccessor.TransformHeadDisplay != null)
            {
                return GxrInputAccessor.TransformHeadDisplay;
            }

            return null;
        }
    }
}
