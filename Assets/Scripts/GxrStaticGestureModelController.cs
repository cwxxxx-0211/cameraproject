using GxrSdk;
using UnityEngine;

public sealed class GxrStaticGestureModelController : MonoBehaviour, IGxrL3StaticCategoryHandler
{
    [SerializeField] private float gestureHoldSeconds = 0.8f;
    [SerializeField] private float yawDegreesPerSecond = 32f;
    [SerializeField] private float pitchDegreesPerSecond = 26f;
    [SerializeField] private float scaleSpeed = 0.24f;
    [SerializeField] private float minScale = 0.03f;
    [SerializeField] private float maxScale = 6f;

    private Transform target;
    private Vector3 initialPosition;
    private Quaternion initialRotation;
    private Vector3 initialScale;
    private GxrL3StaticCategory leftGesture = GxrL3StaticCategory.None;
    private GxrL3StaticCategory rightGesture = GxrL3StaticCategory.None;
    private GxrL3StaticCategory trackedGesture = GxrL3StaticCategory.None;
    private float trackedGestureStartTime;
    private bool resetTriggered;

    private void OnEnable()
    {
        GxrSystemAccessor.InputSystem?.RegisterHandler<IGxrL3StaticCategoryHandler>(this);
    }

    private void OnDisable()
    {
        GxrSystemAccessor.InputSystem?.UnregisterHandler<IGxrL3StaticCategoryHandler>(this);
    }

    private void Update()
    {
        if (target == null)
        {
            return;
        }

        GxrL3StaticCategory activeGesture = GetActiveGesture();
        if (activeGesture != trackedGesture)
        {
            trackedGesture = activeGesture;
            trackedGestureStartTime = Time.time;
            resetTriggered = false;
        }

        if (activeGesture == GxrL3StaticCategory.None ||
            Time.time - trackedGestureStartTime < gestureHoldSeconds)
        {
            return;
        }

        switch (activeGesture)
        {
            case GxrL3StaticCategory.Five:
                ScaleTarget(scaleSpeed * Time.deltaTime);
                break;
            case GxrL3StaticCategory.Fist:
                ScaleTarget(-scaleSpeed * Time.deltaTime);
                break;
            case GxrL3StaticCategory.Like:
                RotateYaw();
                break;
            case GxrL3StaticCategory.Peace:
                RotatePitch();
                break;
            case GxrL3StaticCategory.Ok:
                if (!resetTriggered)
                {
                    ResetTarget();
                    resetTriggered = true;
                }

                break;
        }
    }

    public void SetTarget(Transform model)
    {
        target = model;
        if (target == null)
        {
            return;
        }

        initialPosition = target.position;
        initialRotation = target.rotation;
        initialScale = target.localScale;
    }

    public void OnL3StaticCategoryUpdated(GxrL3StaticCategoryEventData eventData)
    {
        if (eventData.Handedness == GxrHandedness.Left)
        {
            leftGesture = eventData.StaticCategory;
        }
        else if (eventData.Handedness == GxrHandedness.Right)
        {
            rightGesture = eventData.StaticCategory;
        }

    }

    private GxrL3StaticCategory GetActiveGesture()
    {
        if (rightGesture != GxrL3StaticCategory.None)
        {
            return rightGesture;
        }

        return leftGesture;
    }

    private void RotateYaw()
    {
        Transform viewTransform = GxrViewReference.Transform;
        Vector3 upAxis = viewTransform != null ? viewTransform.up : Vector3.up;
        target.Rotate(upAxis, yawDegreesPerSecond * Time.deltaTime, Space.World);
    }

    private void RotatePitch()
    {
        Transform viewTransform = GxrViewReference.Transform;
        Vector3 rightAxis = viewTransform != null ? viewTransform.right : Vector3.right;
        target.Rotate(rightAxis, pitchDegreesPerSecond * Time.deltaTime, Space.World);
    }

    private void ScaleTarget(float delta)
    {
        float current = target.localScale.x;
        float next = Mathf.Clamp(current * (1f + delta), minScale, maxScale);
        target.localScale *= next / Mathf.Max(current, 0.0001f);
    }

    private void ResetTarget()
    {
        if (target == null)
        {
            return;
        }

        target.position = initialPosition;
        target.rotation = initialRotation;
        target.localScale = initialScale;
    }
}
