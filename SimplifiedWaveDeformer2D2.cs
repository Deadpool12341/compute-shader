using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
public class SimplifiedWaveDeformer2D : MonoBehaviour
{
    [Header("Resources")]
    public ComputeShader waveCompute2D;
    public Texture2D deformationTexture;
    public Texture2D partialDerivativesDU; // ∂D/∂u (colorful texture)
    public Texture2D partialDerivativesDV; // ∂D/∂v (gray texture)

    [Header("NEW: UV Offset")]
    public Texture2D uvOffsetTexture; // RG = ΔU, ΔV offset texture
    [Range(0.0f, 2.0f)]
    public float uvOffsetScale = 1.0f; // Scale factor for UV offset intensity

    [Header("NEW: Feathering")]
    [Range(0.0f, 0.1f)]
    public float featherX = 0.02f; // Feather in normalized X (0..1 of width)
    [Range(0.0f, 0.1f)]
    public float featherZ = 0.02f; // Feather in logicalZ (0..1 of length)

    public float displacement = 0.01f;

    [Header("Animation Parameter")]
    public float animParamStart = 0.25f;
    public float animParamEnd = 0.50f;
    public float animSpeed = 0.1f;

    [Header("Movement Settings")]
    [Tooltip("Quad area normalized speed in width (fraction/sec)")]
    public float quadSpeedNormalized = 0.1f;
    [Tooltip("Sub-quad division count")]
    public int subQuadCount = 10;

    [Header("Z-Axis Scaling")]
    [Tooltip("Z-axis scaling start value")]
    public float zScaleStart = 1.0f;
    [Tooltip("Z-axis scaling end value")]
    public float zScaleEnd = 0.7f;
    [Tooltip("Z-axis scaling speed (scale change per second)")]
    public float zScaleSpeed = 0.1f;
    [Tooltip("Shrink target endpoint (0.0=shrink toward negative end, 1.0=shrink toward positive end)")]
    public float fixedZEnd = 0.0f;

    // Auto-detection
    int gridSizeX, gridSizeZ;
    float regionWidthNorm = 0.25f; // Fixed quarter width

    Mesh meshInstance;
    Vector3[] originalVerts, verts;
    Vector3[] originalNormals;
    Color[] vertexColors;
    Vector3[] normals;
    int vertexCount, kernelCS;
    float halfW, halfZ;

    ComputeBuffer inputBuffer, inputNormalBuffer, inputUV0Buffer, outputBuffer, colorBuffer, normalBuffer, uvCorrBuffer, foamIntensityBuffer;

    void Start()
    {
        // 1) Clone mesh
        var mf = GetComponent<MeshFilter>();
        meshInstance = Instantiate(mf.sharedMesh);
        mf.mesh = meshInstance;

        // 2) Cache vertices and normals
        originalVerts = meshInstance.vertices;
        originalNormals = meshInstance.normals;
        vertexCount = originalVerts.Length;
        verts = new Vector3[vertexCount];
        vertexColors = new Color[vertexCount];
        normals = new Vector3[vertexCount];

        // 3) Calculate half width and depth
        halfW = meshInstance.bounds.extents.x;
        halfZ = meshInstance.bounds.extents.z;
        Debug.Log($"[Debug] halfW={halfW:F3}, halfZ={halfZ:F3}");

        // 4) Auto-detect row and column count (deduplicate x,z coordinates)
        var setX = new HashSet<float>();
        var setZ = new HashSet<float>();
        foreach (var v in originalVerts)
        {
            // Round to eliminate jitter, ensure duplicate points merge
            setX.Add(Mathf.Round(v.x * 1000f) / 1000f);
            setZ.Add(Mathf.Round(v.z * 1000f) / 1000f);
        }
        gridSizeX = setX.Count;
        gridSizeZ = setZ.Count;
        Debug.Log($"[Debug] detected gridSizeX={gridSizeX}, gridSizeZ={gridSizeZ}");

        // 5) Create ComputeBuffers
        inputBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        inputNormalBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        inputUV0Buffer = new ComputeBuffer(vertexCount, sizeof(float) * 2); // Original UV0 buffer
        outputBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        colorBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 4);
        normalBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        uvCorrBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 2); // UV correction buffer
        foamIntensityBuffer = new ComputeBuffer(vertexCount, sizeof(float)); // Foam intensity buffer
        inputBuffer.SetData(originalVerts);
        inputNormalBuffer.SetData(originalNormals);
        inputUV0Buffer.SetData(meshInstance.uv); // Set original UV0 data

        // 6) Bind Shader
        kernelCS = waveCompute2D.FindKernel("CSMain");
        waveCompute2D.SetBuffer(kernelCS, "_InputPositions", inputBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_InputNormals", inputNormalBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_InputUV0", inputUV0Buffer); // Original UV0 buffer
        waveCompute2D.SetBuffer(kernelCS, "_OutputPositions", outputBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_VertexColors", colorBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_OutputNormals", normalBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_OutUVCorr", uvCorrBuffer); // UV correction buffer
        waveCompute2D.SetBuffer(kernelCS, "_FoamIntensity", foamIntensityBuffer); // Foam intensity buffer
        waveCompute2D.SetTexture(kernelCS, "_DeformationTex", deformationTexture);
        waveCompute2D.SetTexture(kernelCS, "_PartialDerivativesDU", partialDerivativesDU);
        waveCompute2D.SetTexture(kernelCS, "_PartialDerivativesDV", partialDerivativesDV);

        // NEW: Set UV Offset texture
        if (uvOffsetTexture != null)
        {
            waveCompute2D.SetTexture(kernelCS, "_UVOffsetTex", uvOffsetTexture);
        }

        // 7) Upload static parameters
        waveCompute2D.SetInt("_NumVertices", vertexCount);
        waveCompute2D.SetFloat("_HalfWidth", halfW);
        waveCompute2D.SetFloat("_HalfLength", halfZ);
        waveCompute2D.SetFloat("_Displacement", displacement);
        waveCompute2D.SetInt("_SubCount", subQuadCount);
        // Upload auto-detected row and column counts (if Compute Shader needs them)
        waveCompute2D.SetInt("_GridSizeX", gridSizeX);
        waveCompute2D.SetInt("_GridSizeZ", gridSizeZ);
        // Fixed to quarter width, can also be changed to gridSizeZ/gridSizeX
        waveCompute2D.SetFloat("_RegionWidth", regionWidthNorm);
    }

    void Update()
    {
        // A) Advance animation parameter - fix loop to avoid sampling artifacts
        animParamStart = Mathf.Repeat(animParamStart + animSpeed * Time.deltaTime, 1f);
        animParamEnd = Mathf.Repeat(animParamEnd + animSpeed * Time.deltaTime, 1f);

        // Ensure texture sampling is in [0, 1-epsilon] range to avoid boundary issues
        // This prevents sampling from Y=1.0, as it might not match Y=0.0
        float texelSize = 1.0f / 256.0f; // Texel size based on 256x256 texture
        float safeAnimStart = animParamStart * (1.0f - texelSize);
        float safeAnimEnd = animParamEnd * (1.0f - texelSize);

        waveCompute2D.SetFloat("_AnimStart", safeAnimStart);
        waveCompute2D.SetFloat("_AnimEnd", safeAnimEnd);
        Debug.Log($"[Debug] _AnimStart={safeAnimStart:F3}, _AnimEnd={safeAnimEnd:F3}");

        // B) Calculate quad area sliding window in normalized [0,1] space
        float maxStart = 1f - regionWidthNorm;
        float regionStart = Mathf.Max(0f, maxStart - Time.time * quadSpeedNormalized);
        float regionEnd = regionStart + regionWidthNorm;
        waveCompute2D.SetFloat("_QuadStart", regionStart);
        waveCompute2D.SetFloat("_QuadEnd", regionEnd);
        Debug.Log($"[Debug] QuadNormX=[{regionStart:F3}..{regionEnd:F3}]");

        // C) Calculate Z axis scaling
        float zScaleProgress = Mathf.Clamp01(Time.time * zScaleSpeed);
        float currentZScale = Mathf.Lerp(zScaleStart, zScaleEnd, zScaleProgress);
        waveCompute2D.SetFloat("_ZScale", currentZScale);
        waveCompute2D.SetFloat("_FixedZEnd", fixedZEnd);
        Debug.Log($"[Debug] ZScale={currentZScale:F3} (progress={zScaleProgress:F3})");

        // NEW: Set UV offset parameters
        waveCompute2D.SetFloat("_UvOffsetScale", uvOffsetScale);
        waveCompute2D.SetFloat("_UvOffsetBias", 188.0f); // Zero point for UV offset decoding

        // NEW: Set feathering parameters
        waveCompute2D.SetFloat("_FeatherX", featherX);
        waveCompute2D.SetFloat("_FeatherZ", featherZ);

        // D) Dispatch Compute - using 2D thread groups optimized for 16x16 = 256 threads
        int groupsX = Mathf.CeilToInt((float)gridSizeX / 16.0f);
        int groupsZ = Mathf.CeilToInt((float)gridSizeZ / 16.0f);

        // Performance debug information
        if (Time.frameCount % 60 == 0) // Output once every 60 frames
        {
            int totalThreadsDispatched = groupsX * groupsZ * 256; // 16x16 = 256 threads per group
            float efficiency = (float)vertexCount / totalThreadsDispatched * 100f;
            Debug.Log($"[2D Thread Groups] Vertices: {vertexCount}, " +
                     $"Groups: {groupsX}x{groupsZ} (256 threads each), " +
                     $"Total Dispatched: {totalThreadsDispatched}, " +
                     $"Efficiency: {efficiency:F1}%");
        }

        waveCompute2D.Dispatch(kernelCS, groupsX, groupsZ, 1);

        // E) Read back vertices, colors, normals and corrected UVs
        outputBuffer.GetData(verts);
        colorBuffer.GetData(vertexColors);
        normalBuffer.GetData(normals);

        // Read corrected UVs and set them to UV2 channel for Shader Graph
        var uv2 = new Vector2[vertexCount];
        uvCorrBuffer.GetData(uv2);

        meshInstance.vertices = verts;
        meshInstance.colors = vertexColors;
        meshInstance.normals = normals; // Use derivative-based normals
        meshInstance.SetUVs(1, new System.Collections.Generic.List<Vector2>(uv2)); // Channel 1 = UV2
        meshInstance.RecalculateBounds();
    }

    void OnDestroy()
    {
        inputBuffer?.Release();
        inputNormalBuffer?.Release();
        inputUV0Buffer?.Release(); // Cleanup UV0 buffer
        outputBuffer?.Release();
        colorBuffer?.Release();
        normalBuffer?.Release();
        uvCorrBuffer?.Release(); // Cleanup UV correction buffer
        foamIntensityBuffer?.Release(); // Cleanup foam intensity buffer
    }
}
