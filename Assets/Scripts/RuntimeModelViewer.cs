using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GxrSdk;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed class RuntimeModelViewer : MonoBehaviour
{
    [Header("Model Search")]
    [SerializeField] private string androidModelDirectory = "/sdcard/CadModels";
    [SerializeField] private string editorModelDirectory = "CadModels";

    [Header("Placement")]
    [SerializeField] private float distanceFromCamera = 2.0f;
    [SerializeField] private float targetSizeMeters = 0.8f;
    [SerializeField] private Material defaultMaterial;

    [Header("File Picker")]
    [SerializeField] private bool showFilePicker = true;
    [SerializeField] private bool autoLoadFirstModel = false;
    [SerializeField] private float filePickerDistanceFromCamera = 1.4f;
    [SerializeField] private Vector2 filePickerSize = new Vector2(260f, 160f);

    private GameObject loadedModel;
    private Canvas filePickerCanvas;
    private RectTransform fileListRoot;

    private IEnumerator Start()
    {
        EnsureSceneBasics();
        yield return WaitForViewReference();
        RefreshFilePicker();
        if (autoLoadFirstModel)
        {
            LoadFirstSupportedModel();
        }
    }

    private IEnumerator WaitForViewReference()
    {
        const float timeoutSeconds = 5f;
        float startTime = Time.realtimeSinceStartup;

        while (GxrViewReference.Transform == null && Time.realtimeSinceStartup - startTime < timeoutSeconds)
        {
            yield return null;
        }

        if (GxrViewReference.Transform == null)
        {
            Debug.LogWarning("[RuntimeModelViewer] GXR view transform is not ready. Model will load at scene origin.");
        }
    }

    public void LoadFirstSupportedModel()
    {
        string directory = GetModelDirectory();
        if (!Directory.Exists(directory))
        {
            Debug.LogWarning($"[RuntimeModelViewer] Model directory does not exist: {directory}");
            return;
        }

        string[] files = GetSupportedModelFiles(directory);
        foreach (string file in files)
        {
            LoadModel(file);
            return;
        }

        Debug.LogWarning("[RuntimeModelViewer] No supported .ply or .obj model found in: " + directory);
    }

    public void LoadModel(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".ply")
        {
            LoadPly(path);
        }
        else if (extension == ".obj")
        {
            LoadObj(path);
        }
        else
        {
            Debug.LogWarning("[RuntimeModelViewer] Unsupported model type: " + path);
        }
    }

    private string GetModelDirectory()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return androidModelDirectory;
#else
        if (Path.IsPathRooted(editorModelDirectory))
        {
            return editorModelDirectory;
        }

        return Path.Combine(Application.dataPath, "..", editorModelDirectory);
#endif
    }

    private void LoadPly(string path)
    {
        try
        {
            Mesh mesh = PlyLoader.Load(path);
            if (mesh == null)
            {
                Debug.LogError("[RuntimeModelViewer] Failed to load PLY: " + path);
                return;
            }

            GameObject model = new GameObject(Path.GetFileNameWithoutExtension(path));
            model.transform.SetParent(transform, false);

            MeshFilter meshFilter = model.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = mesh;

            MeshRenderer meshRenderer = model.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = defaultMaterial != null ? defaultMaterial : CreateDefaultMaterial();

            PrepareLoadedModel(model);
            HideFilePicker();
            Debug.Log("[RuntimeModelViewer] Loaded PLY: " + path);
        }
        catch (Exception ex)
        {
            Debug.LogError("[RuntimeModelViewer] PLY load error: " + ex.Message);
        }
    }

    private void LoadObj(string path)
    {
        try
        {
            ObjLoader.Result result = ObjLoader.Load(path, defaultMaterial != null ? defaultMaterial : CreateDefaultMaterial());
            if (result.Mesh == null)
            {
                Debug.LogError("[RuntimeModelViewer] Failed to load OBJ: " + path);
                return;
            }

            GameObject model = new GameObject(Path.GetFileNameWithoutExtension(path));
            model.transform.SetParent(transform, false);

            MeshFilter meshFilter = model.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = result.Mesh;

            MeshRenderer meshRenderer = model.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = result.Materials;

            PrepareLoadedModel(model);
            HideFilePicker();
            Debug.Log("[RuntimeModelViewer] Loaded OBJ: " + path);
        }
        catch (Exception ex)
        {
            Debug.LogError("[RuntimeModelViewer] OBJ load error: " + ex.Message);
        }
    }

    private void PrepareLoadedModel(GameObject model)
    {
        if (loadedModel != null)
        {
            Destroy(loadedModel);
        }

        loadedModel = model;
        loadedModel.AddComponent<BoxCollider>();

        Rigidbody rigidbody = loadedModel.AddComponent<Rigidbody>();
        rigidbody.isKinematic = true;
        rigidbody.useGravity = false;

        loadedModel.AddComponent<GxrManipulatable>();

        GxrStaticGestureModelController gestureController = FindObjectOfType<GxrStaticGestureModelController>();
        if (gestureController == null)
        {
            GameObject gestureControllerObject = new GameObject("GXR Static Gesture Model Controller");
            gestureController = gestureControllerObject.AddComponent<GxrStaticGestureModelController>();
            DontDestroyOnLoad(gestureControllerObject);
        }

        PlaceModel(loadedModel);
        gestureController.SetTarget(loadedModel.transform);
    }

    private void PlaceModel(GameObject model)
    {
        Renderer renderer = model.GetComponentInChildren<Renderer>();
        if (renderer == null)
        {
            return;
        }

        Bounds bounds = renderer.bounds;
        Vector3 centerOffset = bounds.center - model.transform.position;
        model.transform.position -= centerOffset;

        float maxSize = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        if (maxSize > 0.0001f)
        {
            model.transform.localScale *= targetSizeMeters / maxSize;
        }

        Transform viewTransform = GxrViewReference.Transform;
        if (viewTransform != null)
        {
            model.transform.position = viewTransform.position + viewTransform.forward * distanceFromCamera;
            model.transform.rotation = Quaternion.LookRotation(viewTransform.forward, Vector3.up);
        }
    }

    private void RefreshFilePicker()
    {
        if (!showFilePicker)
        {
            return;
        }

        EnsureFilePickerCanvas();

        for (int i = fileListRoot.childCount - 1; i >= 0; i--)
        {
            Destroy(fileListRoot.GetChild(i).gameObject);
        }

        string directory = GetModelDirectory();
        if (!Directory.Exists(directory))
        {
            AddFilePickerLabel("No folder: " + directory);
            return;
        }

        string[] files = GetSupportedModelFiles(directory);
        if (files.Length == 0)
        {
            AddFilePickerLabel("No .ply or .obj files");
            return;
        }

        for (int i = 0; i < files.Length; i++)
        {
            AddFilePickerButton(files[i]);
        }

        PositionFilePickerCanvas();
    }

    private void EnsureFilePickerCanvas()
    {
        if (filePickerCanvas != null)
        {
            return;
        }

        EnsureEventSystem();

        GameObject canvasObject = new GameObject("Model File Picker Canvas");
        canvasObject.transform.SetParent(transform, false);
        filePickerCanvas = canvasObject.AddComponent<Canvas>();
        filePickerCanvas.renderMode = RenderMode.WorldSpace;
        filePickerCanvas.sortingOrder = 20;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 1000f;
        canvasObject.AddComponent<GraphicRaycaster>();
        canvasObject.AddComponent<GxrCanvasRaycastable>();

        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = filePickerSize;

        Image background = canvasObject.AddComponent<Image>();
        background.color = new Color(1f, 1f, 1f, 0.08f);

        GameObject listObject = new GameObject("File List");
        listObject.transform.SetParent(canvasObject.transform, false);
        fileListRoot = listObject.AddComponent<RectTransform>();
        fileListRoot.anchorMin = new Vector2(0.04f, 0.04f);
        fileListRoot.anchorMax = new Vector2(0.96f, 0.96f);
        fileListRoot.offsetMin = Vector2.zero;
        fileListRoot.offsetMax = Vector2.zero;

        VerticalLayoutGroup layout = listObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 4f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = listObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        PositionFilePickerCanvas();
    }

    private void PositionFilePickerCanvas()
    {
        if (filePickerCanvas == null)
        {
            return;
        }

        Transform viewTransform = GxrViewReference.Transform;
        Transform canvasTransform = filePickerCanvas.transform;
        canvasTransform.localScale = Vector3.one * 0.00075f;

        if (viewTransform != null)
        {
            canvasTransform.position = viewTransform.position + viewTransform.forward * filePickerDistanceFromCamera + viewTransform.right * -0.45f;
            canvasTransform.rotation = Quaternion.LookRotation(canvasTransform.position - viewTransform.position, viewTransform.up);
        }
    }

    private void AddFilePickerButton(string path)
    {
        GameObject buttonObject = new GameObject(Path.GetFileName(path));
        buttonObject.transform.SetParent(fileListRoot, false);

        Image image = buttonObject.AddComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.96f);

        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() => LoadModel(path));

        LayoutElement layoutElement = buttonObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = 28f;

        AddText(buttonObject.transform, Path.GetFileName(path), 14, TextAnchor.MiddleLeft, Color.black);
    }

    private void AddFilePickerLabel(string message)
    {
        GameObject labelObject = new GameObject("File Picker Message");
        labelObject.transform.SetParent(fileListRoot, false);
        LayoutElement layoutElement = labelObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = 90f;
        AddText(labelObject.transform, message, 22, TextAnchor.MiddleCenter, new Color(0.95f, 0.82f, 0.62f, 1f));
    }

    private static void AddText(Transform parent, string value, int fontSize, TextAnchor alignment, Color color)
    {
        GameObject textObject = new GameObject("Text");
        textObject.transform.SetParent(parent, false);

        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = ToTmpAlignment(alignment);
        text.color = color;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;

        RectTransform rectTransform = textObject.GetComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = new Vector2(8f, 0f);
        rectTransform.offsetMax = new Vector2(-8f, 0f);
    }

    private static TextAlignmentOptions ToTmpAlignment(TextAnchor alignment)
    {
        switch (alignment)
        {
            case TextAnchor.MiddleCenter:
                return TextAlignmentOptions.Center;
            case TextAnchor.MiddleRight:
                return TextAlignmentOptions.MidlineRight;
            default:
                return TextAlignmentOptions.MidlineLeft;
        }
    }

    private void HideFilePicker()
    {
        if (filePickerCanvas != null)
        {
            filePickerCanvas.gameObject.SetActive(false);
        }
    }

    private static void EnsureEventSystem()
    {
        EventSystem eventSystem = FindObjectOfType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject eventSystemObject = new GameObject("EventSystem");
            eventSystem = eventSystemObject.AddComponent<EventSystem>();
        }

        if (eventSystem.GetComponent<GxrInputModule>() == null)
        {
            eventSystem.gameObject.AddComponent<GxrInputModule>();
        }
    }

    private static string[] GetSupportedModelFiles(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
        List<string> supportedFiles = new List<string>();

        for (int i = 0; i < files.Length; i++)
        {
            string extension = Path.GetExtension(files[i]).ToLowerInvariant();
            if (extension == ".ply" || extension == ".obj")
            {
                supportedFiles.Add(files[i]);
            }
            else if (extension == ".glb")
            {
                Debug.LogWarning("[RuntimeModelViewer] Found GLB, but no runtime GLB loader is installed: " + files[i]);
            }
        }

        supportedFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return supportedFiles.ToArray();
    }

    private void EnsureSceneBasics()
    {
        if (FindObjectOfType<Light>() == null)
        {
            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
    }

    private static Material CreateDefaultMaterial()
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        Material material = new Material(shader);
        material.name = "Runtime Model Material";
        material.color = new Color(0.78f, 0.82f, 0.86f, 1f);
        return material;
    }

    private static class PlyLoader
    {
        private enum PlyFormat
        {
            Unknown,
            Ascii,
            BinaryLittleEndian
        }

        private enum PlyScalarType
        {
            Invalid,
            Char,
            UChar,
            Short,
            UShort,
            Int,
            UInt,
            Float,
            Double
        }

        private struct VertexProperty
        {
            public string Name;
            public int Index;
            public PlyScalarType Type;
        }

        public static Mesh Load(string path)
        {
            using (StreamReader reader = new StreamReader(path))
            {
                string firstLine = reader.ReadLine();
                if (firstLine != "ply")
                {
                    throw new InvalidDataException("Not a PLY file.");
                }

                int vertexCount = 0;
                int faceCount = 0;
                PlyFormat format = PlyFormat.Unknown;
                string currentElement = string.Empty;
                List<VertexProperty> vertexProperties = new List<VertexProperty>();

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed == "end_header")
                    {
                        break;
                    }

                    string[] parts = Split(trimmed);
                    if (parts.Length == 0)
                    {
                        continue;
                    }

                    if (parts[0] == "format")
                    {
                        if (parts.Length > 1 && parts[1] == "ascii")
                        {
                            format = PlyFormat.Ascii;
                        }
                        else if (parts.Length > 1 && parts[1] == "binary_little_endian")
                        {
                            format = PlyFormat.BinaryLittleEndian;
                        }
                    }
                    else if (parts[0] == "element" && parts.Length >= 3)
                    {
                        currentElement = parts[1];
                        if (currentElement == "vertex")
                        {
                            vertexCount = int.Parse(parts[2], CultureInfo.InvariantCulture);
                        }
                        else if (currentElement == "face")
                        {
                            faceCount = int.Parse(parts[2], CultureInfo.InvariantCulture);
                        }
                    }
                    else if (parts[0] == "property" && currentElement == "vertex" && parts.Length >= 3)
                    {
                        vertexProperties.Add(new VertexProperty
                        {
                            Name = parts[parts.Length - 1],
                            Index = vertexProperties.Count,
                            Type = ParseScalarType(parts[1])
                        });
                    }
                }

                if (format == PlyFormat.Unknown)
                {
                    throw new NotSupportedException("Only ASCII and binary_little_endian PLY are supported.");
                }

                int xIndex = FindProperty(vertexProperties, "x");
                int yIndex = FindProperty(vertexProperties, "y");
                int zIndex = FindProperty(vertexProperties, "z");
                int rIndex = FindProperty(vertexProperties, "red", "r");
                int gIndex = FindProperty(vertexProperties, "green", "g");
                int bIndex = FindProperty(vertexProperties, "blue", "b");

                if (xIndex < 0 || yIndex < 0 || zIndex < 0)
                {
                    throw new InvalidDataException("PLY vertex properties must contain x, y, z.");
                }

                List<Vector3> vertices;
                List<Color32> colors;
                List<int> triangles;

                if (format == PlyFormat.Ascii)
                {
                    ReadAsciiBody(reader, vertexCount, faceCount, vertexProperties, xIndex, yIndex, zIndex, rIndex, gIndex, bIndex, out vertices, out colors, out triangles);
                }
                else
                {
                    ReadBinaryLittleEndianBody(reader.BaseStream, vertexCount, faceCount, vertexProperties, xIndex, yIndex, zIndex, rIndex, gIndex, bIndex, out vertices, out colors, out triangles);
                }

                Mesh mesh = new Mesh();
                if (vertices.Count > 65535)
                {
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                }

                mesh.SetVertices(vertices);
                if (colors.Count == vertices.Count)
                {
                    mesh.SetColors(colors);
                }

                if (triangles.Count > 0)
                {
                    mesh.SetTriangles(triangles, 0);
                    mesh.RecalculateNormals();
                }
                else
                {
                    int[] indices = new int[vertices.Count];
                    for (int i = 0; i < indices.Length; i++)
                    {
                        indices[i] = i;
                    }

                    mesh.SetIndices(indices, MeshTopology.Points, 0);
                }

                mesh.RecalculateBounds();
                return mesh;
            }
        }

        private static int FindProperty(List<VertexProperty> properties, params string[] names)
        {
            for (int i = 0; i < properties.Count; i++)
            {
                for (int j = 0; j < names.Length; j++)
                {
                    if (string.Equals(properties[i].Name, names[j], StringComparison.OrdinalIgnoreCase))
                    {
                        return properties[i].Index;
                    }
                }
            }

            return -1;
        }

        private static void ReadAsciiBody(
            StreamReader reader,
            int vertexCount,
            int faceCount,
            List<VertexProperty> vertexProperties,
            int xIndex,
            int yIndex,
            int zIndex,
            int rIndex,
            int gIndex,
            int bIndex,
            out List<Vector3> vertices,
            out List<Color32> colors,
            out List<int> triangles)
        {
            vertices = new List<Vector3>(vertexCount);
            colors = new List<Color32>(vertexCount);
            triangles = new List<int>(Mathf.Max(faceCount, 1) * 3);
            bool hasColor = rIndex >= 0 && gIndex >= 0 && bIndex >= 0;

            for (int i = 0; i < vertexCount; i++)
            {
                string line = reader.ReadLine();
                if (line == null)
                {
                    throw new EndOfStreamException("Unexpected end of vertex data.");
                }

                string[] values = Split(line);
                vertices.Add(new Vector3(ParseFloat(values[xIndex]), ParseFloat(values[yIndex]), ParseFloat(values[zIndex])));

                if (hasColor)
                {
                    colors.Add(new Color32(ParseByte(values[rIndex]), ParseByte(values[gIndex]), ParseByte(values[bIndex]), 255));
                }
            }

            for (int i = 0; i < faceCount; i++)
            {
                string line = reader.ReadLine();
                if (line == null)
                {
                    throw new EndOfStreamException("Unexpected end of face data.");
                }

                string[] values = Split(line);
                if (values.Length < 4)
                {
                    continue;
                }

                int count = int.Parse(values[0], CultureInfo.InvariantCulture);
                if (values.Length < count + 1 || count < 3)
                {
                    continue;
                }

                int first = int.Parse(values[1], CultureInfo.InvariantCulture);
                for (int j = 2; j < count; j++)
                {
                    triangles.Add(first);
                    triangles.Add(int.Parse(values[j], CultureInfo.InvariantCulture));
                    triangles.Add(int.Parse(values[j + 1], CultureInfo.InvariantCulture));
                }
            }
        }

        private static void ReadBinaryLittleEndianBody(
            Stream stream,
            int vertexCount,
            int faceCount,
            List<VertexProperty> vertexProperties,
            int xIndex,
            int yIndex,
            int zIndex,
            int rIndex,
            int gIndex,
            int bIndex,
            out List<Vector3> vertices,
            out List<Color32> colors,
            out List<int> triangles)
        {
            vertices = new List<Vector3>(vertexCount);
            colors = new List<Color32>(vertexCount);
            triangles = new List<int>(Mathf.Max(faceCount, 1) * 3);
            bool hasColor = rIndex >= 0 && gIndex >= 0 && bIndex >= 0;

            using (BinaryReader binaryReader = new BinaryReader(stream))
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    float x = 0f;
                    float y = 0f;
                    float z = 0f;
                    byte r = 255;
                    byte g = 255;
                    byte b = 255;

                    for (int propertyIndex = 0; propertyIndex < vertexProperties.Count; propertyIndex++)
                    {
                        VertexProperty property = vertexProperties[propertyIndex];
                        double value = ReadScalarAsDouble(binaryReader, property.Type);

                        if (propertyIndex == xIndex)
                        {
                            x = (float)value;
                        }
                        else if (propertyIndex == yIndex)
                        {
                            y = (float)value;
                        }
                        else if (propertyIndex == zIndex)
                        {
                            z = (float)value;
                        }
                        else if (propertyIndex == rIndex)
                        {
                            r = ClampByte(value);
                        }
                        else if (propertyIndex == gIndex)
                        {
                            g = ClampByte(value);
                        }
                        else if (propertyIndex == bIndex)
                        {
                            b = ClampByte(value);
                        }
                    }

                    vertices.Add(new Vector3(x, y, z));
                    if (hasColor)
                    {
                        colors.Add(new Color32(r, g, b, 255));
                    }
                }

                for (int i = 0; i < faceCount; i++)
                {
                    int count = binaryReader.ReadByte();
                    if (count < 3)
                    {
                        for (int j = 0; j < count; j++)
                        {
                            binaryReader.ReadInt32();
                        }

                        continue;
                    }

                    int first = binaryReader.ReadInt32();
                    int previous = binaryReader.ReadInt32();
                    for (int j = 2; j < count; j++)
                    {
                        int current = binaryReader.ReadInt32();
                        triangles.Add(first);
                        triangles.Add(previous);
                        triangles.Add(current);
                        previous = current;
                    }
                }
            }
        }

        private static PlyScalarType ParseScalarType(string value)
        {
            switch (value)
            {
                case "char":
                case "int8":
                    return PlyScalarType.Char;
                case "uchar":
                case "uint8":
                    return PlyScalarType.UChar;
                case "short":
                case "int16":
                    return PlyScalarType.Short;
                case "ushort":
                case "uint16":
                    return PlyScalarType.UShort;
                case "int":
                case "int32":
                    return PlyScalarType.Int;
                case "uint":
                case "uint32":
                    return PlyScalarType.UInt;
                case "float":
                case "float32":
                    return PlyScalarType.Float;
                case "double":
                case "float64":
                    return PlyScalarType.Double;
                default:
                    return PlyScalarType.Invalid;
            }
        }

        private static double ReadScalarAsDouble(BinaryReader reader, PlyScalarType type)
        {
            switch (type)
            {
                case PlyScalarType.Char:
                    return reader.ReadSByte();
                case PlyScalarType.UChar:
                    return reader.ReadByte();
                case PlyScalarType.Short:
                    return reader.ReadInt16();
                case PlyScalarType.UShort:
                    return reader.ReadUInt16();
                case PlyScalarType.Int:
                    return reader.ReadInt32();
                case PlyScalarType.UInt:
                    return reader.ReadUInt32();
                case PlyScalarType.Float:
                    return reader.ReadSingle();
                case PlyScalarType.Double:
                    return reader.ReadDouble();
                default:
                    throw new NotSupportedException("Unsupported PLY scalar type.");
            }
        }

        private static string[] Split(string value)
        {
            return value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private static float ParseFloat(string value)
        {
            return float.Parse(value, CultureInfo.InvariantCulture);
        }

        private static byte ParseByte(string value)
        {
            float parsed = ParseFloat(value);
            return (byte)Mathf.Clamp(Mathf.RoundToInt(parsed), 0, 255);
        }

        private static byte ClampByte(double value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt((float)value), 0, 255);
        }
    }

    private static class ObjLoader
    {
        private struct ObjVertexKey : IEquatable<ObjVertexKey>
        {
            public int PositionIndex;
            public int TexCoordIndex;
            public int NormalIndex;

            public bool Equals(ObjVertexKey other)
            {
                return PositionIndex == other.PositionIndex && TexCoordIndex == other.TexCoordIndex && NormalIndex == other.NormalIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is ObjVertexKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = PositionIndex;
                    hash = (hash * 397) ^ TexCoordIndex;
                    hash = (hash * 397) ^ NormalIndex;
                    return hash;
                }
            }
        }

        private sealed class MaterialInfo
        {
            public Color DiffuseColor = Color.white;
            public string DiffuseTexturePath;
        }

        public sealed class Result
        {
            public Mesh Mesh;
            public Material[] Materials;
        }

        public static Result Load(string path, Material fallbackMaterial)
        {
            List<Vector3> sourcePositions = new List<Vector3>();
            List<Vector2> sourceTexCoords = new List<Vector2>();
            List<Vector3> sourceNormals = new List<Vector3>();
            List<Vector3> vertices = new List<Vector3>();
            List<Vector2> texCoords = new List<Vector2>();
            List<Vector3> normals = new List<Vector3>();
            List<List<int>> submeshTriangles = new List<List<int>>();
            List<string> submeshMaterialNames = new List<string>();
            Dictionary<ObjVertexKey, int> vertexLookup = new Dictionary<ObjVertexKey, int>();
            Dictionary<string, MaterialInfo> materialInfos = new Dictionary<string, MaterialInfo>(StringComparer.OrdinalIgnoreCase);
            string currentMaterialName = string.Empty;
            int currentSubmeshIndex = GetOrCreateSubmesh(currentMaterialName, submeshMaterialNames, submeshTriangles);

            using (StreamReader reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }

                    string[] parts = Split(trimmed);
                    if (parts.Length == 0)
                    {
                        continue;
                    }

                    if (parts[0] == "v" && parts.Length >= 4)
                    {
                        sourcePositions.Add(new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3])));
                    }
                    else if (parts[0] == "vt" && parts.Length >= 3)
                    {
                        sourceTexCoords.Add(new Vector2(ParseFloat(parts[1]), ParseFloat(parts[2])));
                    }
                    else if (parts[0] == "vn" && parts.Length >= 4)
                    {
                        sourceNormals.Add(new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3])).normalized);
                    }
                    else if (parts[0] == "mtllib" && parts.Length >= 2)
                    {
                        string mtlPath = ResolveRelativePath(Path.GetDirectoryName(path), JoinParts(parts, 1));
                        LoadMaterialLibrary(mtlPath, materialInfos);
                    }
                    else if (parts[0] == "usemtl" && parts.Length >= 2)
                    {
                        currentMaterialName = JoinParts(parts, 1);
                        currentSubmeshIndex = GetOrCreateSubmesh(currentMaterialName, submeshMaterialNames, submeshTriangles);
                    }
                    else if (parts[0] == "f" && parts.Length >= 4)
                    {
                        List<int> triangles = submeshTriangles[currentSubmeshIndex];
                        int first = AddFaceVertex(parts[1], sourcePositions, sourceTexCoords, sourceNormals, vertices, texCoords, normals, vertexLookup);
                        int previous = AddFaceVertex(parts[2], sourcePositions, sourceTexCoords, sourceNormals, vertices, texCoords, normals, vertexLookup);

                        for (int i = 3; i < parts.Length; i++)
                        {
                            int current = AddFaceVertex(parts[i], sourcePositions, sourceTexCoords, sourceNormals, vertices, texCoords, normals, vertexLookup);
                            triangles.Add(first);
                            triangles.Add(previous);
                            triangles.Add(current);
                            previous = current;
                        }
                    }
                }
            }

            RemoveEmptySubmeshes(submeshMaterialNames, submeshTriangles);

            if (vertices.Count == 0 || submeshTriangles.Count == 0)
            {
                throw new InvalidDataException("OBJ must contain vertices and faces.");
            }

            Mesh mesh = new Mesh();
            if (vertices.Count > 65535)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(vertices);
            mesh.subMeshCount = submeshTriangles.Count;
            for (int i = 0; i < submeshTriangles.Count; i++)
            {
                mesh.SetTriangles(submeshTriangles[i], i);
            }

            if (texCoords.Count == vertices.Count)
            {
                mesh.SetUVs(0, texCoords);
            }

            if (normals.Count == vertices.Count)
            {
                mesh.SetNormals(normals);
            }
            else
            {
                mesh.RecalculateNormals();
            }

            mesh.RecalculateBounds();
            return new Result
            {
                Mesh = mesh,
                Materials = CreateMaterials(path, submeshMaterialNames, materialInfos, fallbackMaterial)
            };
        }

        private static int AddFaceVertex(
            string value,
            List<Vector3> sourcePositions,
            List<Vector2> sourceTexCoords,
            List<Vector3> sourceNormals,
            List<Vector3> vertices,
            List<Vector2> texCoords,
            List<Vector3> normals,
            Dictionary<ObjVertexKey, int> vertexLookup)
        {
            ObjVertexKey key = ParseFaceVertex(value, sourcePositions.Count, sourceTexCoords.Count, sourceNormals.Count);
            if (vertexLookup.TryGetValue(key, out int existingIndex))
            {
                return existingIndex;
            }

            if (key.PositionIndex < 0 || key.PositionIndex >= sourcePositions.Count)
            {
                throw new InvalidDataException("OBJ face references an invalid vertex index.");
            }

            int index = vertices.Count;
            vertices.Add(sourcePositions[key.PositionIndex]);

            if (key.TexCoordIndex >= 0 && key.TexCoordIndex < sourceTexCoords.Count)
            {
                texCoords.Add(sourceTexCoords[key.TexCoordIndex]);
            }
            else if (texCoords.Count > 0)
            {
                texCoords.Add(Vector2.zero);
            }

            if (key.NormalIndex >= 0 && key.NormalIndex < sourceNormals.Count)
            {
                normals.Add(sourceNormals[key.NormalIndex]);
            }
            else if (normals.Count > 0)
            {
                normals.Add(Vector3.up);
            }

            vertexLookup.Add(key, index);
            return index;
        }

        private static ObjVertexKey ParseFaceVertex(string value, int positionCount, int texCoordCount, int normalCount)
        {
            string[] parts = value.Split('/');
            int positionIndex = ParseObjIndex(parts[0], positionCount);
            int texCoordIndex = -1;
            int normalIndex = -1;

            if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]))
            {
                texCoordIndex = ParseObjIndex(parts[1], texCoordCount);
            }

            if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]))
            {
                normalIndex = ParseObjIndex(parts[2], normalCount);
            }

            return new ObjVertexKey
            {
                PositionIndex = positionIndex,
                TexCoordIndex = texCoordIndex,
                NormalIndex = normalIndex
            };
        }

        private static int GetOrCreateSubmesh(string materialName, List<string> materialNames, List<List<int>> submeshTriangles)
        {
            for (int i = 0; i < materialNames.Count; i++)
            {
                if (string.Equals(materialNames[i], materialName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            materialNames.Add(materialName);
            submeshTriangles.Add(new List<int>());
            return submeshTriangles.Count - 1;
        }

        private static void RemoveEmptySubmeshes(List<string> materialNames, List<List<int>> submeshTriangles)
        {
            for (int i = submeshTriangles.Count - 1; i >= 0; i--)
            {
                if (submeshTriangles[i].Count == 0)
                {
                    submeshTriangles.RemoveAt(i);
                    materialNames.RemoveAt(i);
                }
            }
        }

        private static void LoadMaterialLibrary(string path, Dictionary<string, MaterialInfo> materialInfos)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning("[RuntimeModelViewer] MTL file not found: " + path);
                return;
            }

            string mtlDirectory = Path.GetDirectoryName(path);
            string currentName = null;
            MaterialInfo currentInfo = null;

            using (StreamReader reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }

                    string[] parts = Split(trimmed);
                    if (parts.Length == 0)
                    {
                        continue;
                    }

                    if (parts[0] == "newmtl" && parts.Length >= 2)
                    {
                        currentName = JoinParts(parts, 1);
                        currentInfo = new MaterialInfo();
                        materialInfos[currentName] = currentInfo;
                    }
                    else if (currentInfo != null && parts[0] == "Kd" && parts.Length >= 4)
                    {
                        currentInfo.DiffuseColor = new Color(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]), 1f);
                    }
                    else if (currentInfo != null && parts[0] == "map_Kd" && parts.Length >= 2)
                    {
                        currentInfo.DiffuseTexturePath = ResolveRelativePath(mtlDirectory, StripTextureOptions(JoinParts(parts, 1)));
                    }
                }
            }
        }

        private static Material[] CreateMaterials(string objPath, List<string> materialNames, Dictionary<string, MaterialInfo> materialInfos, Material fallbackMaterial)
        {
            Material[] materials = new Material[materialNames.Count];
            for (int i = 0; i < materialNames.Count; i++)
            {
                MaterialInfo info;
                if (!materialInfos.TryGetValue(materialNames[i], out info))
                {
                    materials[i] = new Material(fallbackMaterial);
                    continue;
                }

                Material material = new Material(fallbackMaterial);
                material.name = string.IsNullOrEmpty(materialNames[i]) ? Path.GetFileNameWithoutExtension(objPath) : materialNames[i];
                material.color = info.DiffuseColor;

                if (!string.IsNullOrEmpty(info.DiffuseTexturePath))
                {
                    Texture2D texture = LoadTexture(info.DiffuseTexturePath);
                    if (texture != null)
                    {
                        material.mainTexture = texture;
                    }
                }

                materials[i] = material;
            }

            return materials;
        }

        private static Texture2D LoadTexture(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning("[RuntimeModelViewer] Texture file not found: " + path);
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            texture.name = Path.GetFileNameWithoutExtension(path);
            return texture;
        }

        private static string ResolveRelativePath(string baseDirectory, string relativePath)
        {
            string normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
            {
                return normalized;
            }

            return Path.GetFullPath(Path.Combine(baseDirectory ?? string.Empty, normalized));
        }

        private static string StripTextureOptions(string value)
        {
            string[] parts = Split(value);
            if (parts.Length == 0)
            {
                return value;
            }

            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string candidate = parts[i];
                string extension = Path.GetExtension(candidate).ToLowerInvariant();
                if (extension == ".jpg" || extension == ".jpeg" || extension == ".png")
                {
                    return candidate;
                }
            }

            return value;
        }

        private static string JoinParts(string[] parts, int startIndex)
        {
            if (startIndex >= parts.Length)
            {
                return string.Empty;
            }

            return string.Join(" ", parts, startIndex, parts.Length - startIndex);
        }

        private static int ParseObjIndex(string value, int count)
        {
            int index = int.Parse(value, CultureInfo.InvariantCulture);
            if (index > 0)
            {
                return index - 1;
            }

            if (index < 0)
            {
                return count + index;
            }

            throw new InvalidDataException("OBJ indices are 1-based and cannot be zero.");
        }

        private static string[] Split(string value)
        {
            return value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private static float ParseFloat(string value)
        {
            return float.Parse(value, CultureInfo.InvariantCulture);
        }
    }
}
