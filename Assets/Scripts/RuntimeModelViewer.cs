using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GxrSdk;
using UnityEngine;

public sealed class RuntimeModelViewer : MonoBehaviour
{
    [Header("Model Search")]
    [SerializeField] private string androidModelDirectory = "/sdcard/CadModels";
    [SerializeField] private string editorModelDirectory = "CadModels";

    [Header("Placement")]
    [SerializeField] private float distanceFromCamera = 2.0f;
    [SerializeField] private float targetSizeMeters = 0.8f;
    [SerializeField] private Material defaultMaterial;

    private GameObject loadedModel;

    private IEnumerator Start()
    {
        EnsureSceneBasics();
        yield return WaitForViewReference();
        LoadFirstSupportedModel();
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

        string[] files = Directory.GetFiles(directory);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension == ".ply")
            {
                LoadPly(file);
                return;
            }

            if (extension == ".glb")
            {
                Debug.LogWarning("[RuntimeModelViewer] Found GLB, but no runtime GLB loader is installed. Convert it to ASCII PLY or add glTFast later: " + file);
            }
        }

        Debug.LogWarning("[RuntimeModelViewer] No supported .ply model found in: " + directory);
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

            if (loadedModel != null)
            {
                Destroy(loadedModel);
            }

            loadedModel = new GameObject(Path.GetFileNameWithoutExtension(path));
            loadedModel.transform.SetParent(transform, false);

            MeshFilter meshFilter = loadedModel.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = mesh;

            MeshRenderer meshRenderer = loadedModel.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = defaultMaterial != null ? defaultMaterial : CreateDefaultMaterial();

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
            Debug.Log("[RuntimeModelViewer] Loaded PLY: " + path);
        }
        catch (Exception ex)
        {
            Debug.LogError("[RuntimeModelViewer] PLY load error: " + ex.Message);
        }
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
}
