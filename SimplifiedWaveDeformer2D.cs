using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
public class SimplifiedWaveDeformer2D : MonoBehaviour
{
    [Header("Resources")]
    public ComputeShader waveCompute2D;
    public Texture2D deformationTexture;
    public Texture2D partialDerivativesDU;
    public Texture2D partialDerivativesDV;
    public float displacement = 0.01f;

    [Header("Animation Parameter")]
    public float animParamStart = 0.25f;
    public float animParamEnd = 0.50f;
    public float animSpeed = 0.1f;

    [Header("Movement Settings")]
    [Tooltip("Quad 区域在宽度上的归一化速度 (fraction/sec)")]
    public float quadSpeedNormalized = 0.1f;
    [Tooltip("子 Quad 划分数")]
    public int subQuadCount = 10;

    [Header("Z-Axis Scaling")]
    [Tooltip("Z轴缩放的起始值")]
    public float zScaleStart = 1.0f;
    [Tooltip("Z轴缩放的结束值")]
    public float zScaleEnd = 0.7f;
    [Tooltip("Z轴缩放的速度 (每秒缩放变化量)")]
    public float zScaleSpeed = 0.1f;
    [Tooltip("收缩目标端点 (0.0=向负端收缩, 1.0=向正端收缩)")]
    public float fixedZEnd = 0.0f;

    [Header("2D Thread Group Settings")]
    [Tooltip("线程组大小 (必须与compute shader中的numthreads匹配)")]
    public int threadGroupSize = 16;

    [Header("Performance Optimization")]
    [Tooltip("使用优化的Mesh更新方法")]
    public bool useOptimizedMeshUpdate = true;
    [Tooltip("使用异步GPU读取（实验性）")]
    public bool useAsyncGPUReadback = true;
    [Tooltip("数据读取频率控制（每N帧读取一次，0=每帧）")]
    [Range(0, 10)]
    public int readbackFrequency = 2;

    // 自动检测
    int gridSizeX, gridSizeZ;
    float regionWidthNorm = 0.25f;

    Mesh meshInstance;
    Vector3[] originalVerts, verts;
    Vector3[] originalNormals;
    Color[] vertexColors;
    Vector3[] normals;
    int vertexCount, kernelCS;
    float halfW, halfZ;

    // 性能优化相关
    int frameCounter = 0;
    bool isAsyncReadbackPending = false;
    UnityEngine.Rendering.AsyncGPUReadbackRequest asyncRequest;

    ComputeBuffer inputBuffer, inputNormalBuffer, outputBuffer, colorBuffer, normalBuffer;

    void Start()
    {
        // 1) 克隆网格
        var mf = GetComponent<MeshFilter>();
        meshInstance = Instantiate(mf.sharedMesh);
        mf.mesh = meshInstance;

        // 2) 缓存顶点和法线
        originalVerts = meshInstance.vertices;
        originalNormals = meshInstance.normals;
        vertexCount = originalVerts.Length;
        verts = new Vector3[vertexCount];
        vertexColors = new Color[vertexCount];
        normals = new Vector3[vertexCount];

        // 3) 计算半宽半深
        halfW = meshInstance.bounds.extents.x;
        halfZ = meshInstance.bounds.extents.z;
        Debug.Log($"[2D Debug] halfW={halfW:F3}, halfZ={halfZ:F3}");

        // 4) 自动检测行列数（去重 x,z 坐标）
        var setX = new HashSet<float>();
        var setZ = new HashSet<float>();
        foreach (var v in originalVerts)
        {
            setX.Add(Mathf.Round(v.x * 1000f) / 1000f);
            setZ.Add(Mathf.Round(v.z * 1000f) / 1000f);
        }
        gridSizeX = setX.Count;
        gridSizeZ = setZ.Count;
        Debug.Log($"[2D Debug] detected gridSizeX={gridSizeX}, gridSizeZ={gridSizeZ}");

        // 5) 验证网格结构
        if (gridSizeX * gridSizeZ != vertexCount)
        {
            Debug.LogWarning($"[2D Warning] Grid size mismatch: {gridSizeX}x{gridSizeZ}={gridSizeX * gridSizeZ} != {vertexCount} vertices");
        }

        // 6) 创建 ComputeBuffer
        inputBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        inputNormalBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        outputBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        colorBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 4);
        normalBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        inputBuffer.SetData(originalVerts);
        inputNormalBuffer.SetData(originalNormals);

        // 7) 绑定 Shader
        kernelCS = waveCompute2D.FindKernel("CSMain");
        waveCompute2D.SetBuffer(kernelCS, "_InputPositions", inputBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_InputNormals", inputNormalBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_OutputPositions", outputBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_VertexColors", colorBuffer);
        waveCompute2D.SetBuffer(kernelCS, "_OutputNormals", normalBuffer);
        waveCompute2D.SetTexture(kernelCS, "_DeformationTex", deformationTexture);
        waveCompute2D.SetTexture(kernelCS, "_PartialDerivativesDU", partialDerivativesDU);
        waveCompute2D.SetTexture(kernelCS, "_PartialDerivativesDV", partialDerivativesDV);

        // 8) 上传静态参数
        waveCompute2D.SetInt("_NumVertices", vertexCount);
        waveCompute2D.SetInt("_GridSizeX", gridSizeX);
        waveCompute2D.SetInt("_GridSizeZ", gridSizeZ);
        waveCompute2D.SetFloat("_HalfWidth", halfW);
        waveCompute2D.SetFloat("_HalfLength", halfZ);
        waveCompute2D.SetFloat("_Displacement", displacement);
        waveCompute2D.SetInt("_SubCount", subQuadCount);
        waveCompute2D.SetFloat("_RegionWidth", regionWidthNorm);

        // 9) 优化设置
        if (useOptimizedMeshUpdate)
        {
            meshInstance.MarkDynamic(); // 标记为动态网格，优化内存分配
        }
    }

    void Update()
    {
        // A) 推进动画参数
        animParamStart = Mathf.Repeat(animParamStart + animSpeed * Time.deltaTime, 1f);
        animParamEnd = Mathf.Repeat(animParamEnd + animSpeed * Time.deltaTime, 1f);

        // 确保纹理采样在 [0, 1-epsilon] 范围内
        float texelSize = 1.0f / 256.0f;
        float safeAnimStart = animParamStart * (1.0f - texelSize);
        float safeAnimEnd = animParamEnd * (1.0f - texelSize);

        waveCompute2D.SetFloat("_AnimStart", safeAnimStart);
        waveCompute2D.SetFloat("_AnimEnd", safeAnimEnd);

        // B) 计算 quad 区域滑动窗口
        float maxStart = 1f - regionWidthNorm;
        float regionStart = Mathf.Max(0f, maxStart - Time.time * quadSpeedNormalized);
        float regionEnd = regionStart + regionWidthNorm;
        waveCompute2D.SetFloat("_QuadStart", regionStart);
        waveCompute2D.SetFloat("_QuadEnd", regionEnd);

        // C) 计算 Z 轴缩放
        float zScaleProgress = Mathf.Clamp01(Time.time * zScaleSpeed);
        float currentZScale = Mathf.Lerp(zScaleStart, zScaleEnd, zScaleProgress);
        waveCompute2D.SetFloat("_ZScale", currentZScale);
        waveCompute2D.SetFloat("_FixedZEnd", fixedZEnd);

        // D) 派发 2D Compute Shader
        // 计算所需的线程组数量
        int groupsX = Mathf.CeilToInt((float)gridSizeX / threadGroupSize);
        int groupsZ = Mathf.CeilToInt((float)gridSizeZ / threadGroupSize);
        
        // 计算效率统计
        int totalThreadsDispatched = groupsX * groupsZ * threadGroupSize * threadGroupSize;
        float efficiency = (float)vertexCount / totalThreadsDispatched * 100f;
        
        // 性能调试信息
        if (Time.frameCount % 60 == 0)
        {
            Debug.Log($"[2D Thread Groups] Grid: {gridSizeX}x{gridSizeZ}, " +
                     $"Groups: {groupsX}x{groupsZ} (size={threadGroupSize}x{threadGroupSize}), " +
                     $"Vertices: {vertexCount}, Dispatched: {totalThreadsDispatched}, " +
                     $"Efficiency: {efficiency:F1}%");
        }
        
        // 2D Dispatch - 这是关键的性能优化
        waveCompute2D.Dispatch(kernelCS, groupsX, groupsZ, 1);

        // E) 读取结果 - 支持多种优化模式
        if (ShouldReadThisFrame())
        {
            if (useAsyncGPUReadback && !isAsyncReadbackPending)
            {
                StartAsyncReadback();
            }
            else if (!useAsyncGPUReadback)
            {
                ReadDataSynchronously();
            }
        }

        // 处理异步读取结果
        if (isAsyncReadbackPending && asyncRequest.done)
        {
            ProcessAsyncReadback();
        }
    }

    bool ShouldReadThisFrame()
    {
        if (readbackFrequency == 0) return true;
        return (frameCounter++ % (readbackFrequency + 1)) == 0;
    }

    void ReadDataSynchronously()
    {
        // 原始同步读取方式
        outputBuffer.GetData(verts);
        colorBuffer.GetData(vertexColors);
        normalBuffer.GetData(normals);

        UpdateMeshData();
    }

    void StartAsyncReadback()
    {
        // 异步读取（实验性功能）
        isAsyncReadbackPending = true;
        asyncRequest = UnityEngine.Rendering.AsyncGPUReadback.Request(outputBuffer);
    }

    void ProcessAsyncReadback()
    {
        if (asyncRequest.hasError)
        {
            Debug.LogError("[Async Readback] Error occurred during async GPU readback");
            isAsyncReadbackPending = false;
            return;
        }

        // 获取异步读取的数据
        var data = asyncRequest.GetData<Vector3>();
        if (data.Length == vertexCount)
        {
            data.CopyTo(verts);
            
            // 同步读取颜色和法线（可以进一步优化为多个异步请求）
            colorBuffer.GetData(vertexColors);
            normalBuffer.GetData(normals);
            
            UpdateMeshData();
        }

        isAsyncReadbackPending = false;
    }

    void UpdateMeshData()
    {
        if (useOptimizedMeshUpdate)
        {
            // 优化的更新方法 - 使用SetVertices等API
            meshInstance.SetVertices(verts);
            meshInstance.SetColors(vertexColors);
            meshInstance.SetNormals(normals);
        }
        else
        {
            // 原始更新方法
            meshInstance.vertices = verts;
            meshInstance.colors = vertexColors;
            meshInstance.normals = normals;
        }
    }

    void OnDestroy()
    {
        inputBuffer?.Release();
        inputNormalBuffer?.Release();
        outputBuffer?.Release();
        colorBuffer?.Release();
        normalBuffer?.Release();
    }

    // 调试方法：显示网格结构信息
    void OnDrawGizmosSelected()
    {
        if (meshInstance == null) return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(transform.position, meshInstance.bounds.size);
        
        // 显示网格线
        Gizmos.color = Color.red;
        for (int x = 0; x < gridSizeX; x++)
        {
            Vector3 start = new Vector3(-halfW + (x * 2.0f * halfW) / (gridSizeX - 1), 0, -halfZ);
            Vector3 end = new Vector3(-halfW + (x * 2.0f * halfW) / (gridSizeX - 1), 0, halfZ);
            Gizmos.DrawLine(transform.TransformPoint(start), transform.TransformPoint(end));
        }
        
        Gizmos.color = Color.blue;
        for (int z = 0; z < gridSizeZ; z++)
        {
            Vector3 start = new Vector3(-halfW, 0, -halfZ + (z * 2.0f * halfZ) / (gridSizeZ - 1));
            Vector3 end = new Vector3(halfW, 0, -halfZ + (z * 2.0f * halfZ) / (gridSizeZ - 1));
            Gizmos.DrawLine(transform.TransformPoint(start), transform.TransformPoint(end));
        }
    }
} 