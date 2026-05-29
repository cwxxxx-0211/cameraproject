using UnityEngine;
using UnityEngine.EventSystems;

public sealed class ModelManipulator : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, IScrollHandler
{
    [SerializeField] private float dragMetersPerPixel = 0.002f;
    [SerializeField] private float rotateDegreesPerPixel = 0.25f;
    [SerializeField] private float scrollScaleSpeed = 0.12f;
    [SerializeField] private float minScale = 0.05f;
    [SerializeField] private float maxScale = 5f;

    private Camera activeCamera;
    private bool dragging;
    private Vector2 lastPointerPosition;
    private float previousTouchDistance;

    private void Awake()
    {
        activeCamera = GxrViewReference.ActiveCamera;
    }

    private void Update()
    {
        HandleMouseFallback();
        HandleTouchFallback();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        dragging = true;
        lastPointerPosition = eventData.position;
    }

    public void OnDrag(PointerEventData eventData)
    {
        Vector2 delta = eventData.position - lastPointerPosition;
        lastPointerPosition = eventData.position;

        if (eventData.button == PointerEventData.InputButton.Right || Input.GetKey(KeyCode.LeftShift))
        {
            Rotate(delta);
        }
        else
        {
            Drag(delta);
        }
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        dragging = false;
    }

    public void OnScroll(PointerEventData eventData)
    {
        Scale(1f + eventData.scrollDelta.y * scrollScaleSpeed);
    }

    private void HandleMouseFallback()
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            return;
        }

        if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
        {
            dragging = true;
            lastPointerPosition = Input.mousePosition;
        }

        if (dragging && (Input.GetMouseButton(0) || Input.GetMouseButton(1)))
        {
            Vector2 current = Input.mousePosition;
            Vector2 delta = current - lastPointerPosition;
            lastPointerPosition = current;

            if (Input.GetMouseButton(1) || Input.GetKey(KeyCode.LeftShift))
            {
                Rotate(delta);
            }
            else
            {
                Drag(delta);
            }
        }

        if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1))
        {
            dragging = false;
        }

        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Abs(wheel) > 0.0001f)
        {
            Scale(1f + wheel * scrollScaleSpeed);
        }
    }

    private void HandleTouchFallback()
    {
        if (Input.touchCount == 1)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Moved)
            {
                Drag(touch.deltaPosition);
            }
        }
        else if (Input.touchCount >= 2)
        {
            Touch first = Input.GetTouch(0);
            Touch second = Input.GetTouch(1);
            float distance = Vector2.Distance(first.position, second.position);

            if (first.phase == TouchPhase.Began || second.phase == TouchPhase.Began || previousTouchDistance <= 0f)
            {
                previousTouchDistance = distance;
                return;
            }

            float ratio = distance / Mathf.Max(previousTouchDistance, 1f);
            previousTouchDistance = distance;
            Scale(ratio);
        }
        else
        {
            previousTouchDistance = 0f;
        }
    }

    private void Drag(Vector2 delta)
    {
        Camera camera = GetCamera();
        if (camera == null)
        {
            transform.position += new Vector3(delta.x, delta.y, 0f) * dragMetersPerPixel;
            return;
        }

        Vector3 right = camera.transform.right;
        Vector3 up = camera.transform.up;
        transform.position += (right * delta.x + up * delta.y) * dragMetersPerPixel;
    }

    private void Rotate(Vector2 delta)
    {
        Camera camera = GetCamera();
        Vector3 upAxis = camera != null ? camera.transform.up : Vector3.up;
        Vector3 rightAxis = camera != null ? camera.transform.right : Vector3.right;

        transform.Rotate(upAxis, -delta.x * rotateDegreesPerPixel, Space.World);
        transform.Rotate(rightAxis, delta.y * rotateDegreesPerPixel, Space.World);
    }

    private void Scale(float factor)
    {
        if (factor <= 0f)
        {
            return;
        }

        float current = transform.localScale.x;
        float next = Mathf.Clamp(current * factor, minScale, maxScale);
        transform.localScale = transform.localScale * (next / Mathf.Max(current, 0.0001f));
    }

    private Camera GetCamera()
    {
        if (activeCamera == null)
        {
            activeCamera = GxrViewReference.ActiveCamera;
        }

        return activeCamera;
    }
}
