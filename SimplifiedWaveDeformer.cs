using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter))]
public class SimplifiedWaveDeformer : MonoBehaviour
{
    [Header("Resources")]
    public ComputeShader waveCompute;
    public Texture2D deformationTexture;
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

    // 自动检测
    int gridSizeX, gridSizeZ;
    float regionWidthNorm = 0.25f; // 固定四分之一宽度

    Mesh meshInstance;
    Vector3[] originalVerts, verts;
    Color[] vertexColors;
    int vertexCount, kernelCS;
    float halfW, halfZ;

    ComputeBuffer inputBuffer, outputBuffer, colorBuffer;

    void Start()
    {
        // 1) 克隆网格
        var mf = GetComponent<MeshFilter>();
        meshInstance = Instantiate(mf.sharedMesh);
        mf.mesh = meshInstance;

        // 2) 缓存顶点
        originalVerts = meshInstance.vertices;
        vertexCount = originalVerts.Length;
        verts = new Vector3[vertexCount];
        vertexColors = new Color[vertexCount];

        // 3) 计算半宽半深
        halfW = meshInstance.bounds.extents.x;
        halfZ = meshInstance.bounds.extents.z;
        Debug.Log($"[Debug] halfW={halfW:F3}, halfZ={halfZ:F3}");

        // 4) 自动检测行列数（去重 x,z 坐标）
        var setX = new HashSet<float>();
        var setZ = new HashSet<float>();
        foreach (var v in originalVerts)
        {
            // 四舍五入去抖，保证重复点合并
            setX.Add(Mathf.Round(v.x * 1000f) / 1000f);
            setZ.Add(Mathf.Round(v.z * 1000f) / 1000f);
        }
        gridSizeX = setX.Count;
        gridSizeZ = setZ.Count;
        Debug.Log($"[Debug] detected gridSizeX={gridSizeX}, gridSizeZ={gridSizeZ}");

        // 5) 创建 ComputeBuffer
        inputBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        outputBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        colorBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 4);
        inputBuffer.SetData(originalVerts);

        // 6) 绑定 Shader
        kernelCS = waveCompute.FindKernel("CSMain");
        waveCompute.SetBuffer(kernelCS, "_InputPositions", inputBuffer);
        waveCompute.SetBuffer(kernelCS, "_OutputPositions", outputBuffer);
        waveCompute.SetBuffer(kernelCS, "_VertexColors", colorBuffer);
        waveCompute.SetTexture(kernelCS, "_DeformationTex", deformationTexture);

        // 7) 上传静态参数
        waveCompute.SetInt("_NumVertices", vertexCount);
        waveCompute.SetFloat("_HalfWidth", halfW);
        waveCompute.SetFloat("_HalfLength", halfZ);
        waveCompute.SetFloat("_Displacement", displacement);
        waveCompute.SetInt("_SubCount", subQuadCount);
        // 上传自动检测的行列数（如果 Compute Shader 需要的话）
        waveCompute.SetInt("_GridSizeX", gridSizeX);
        waveCompute.SetInt("_GridSizeZ", gridSizeZ);
        // 固定为四分之一宽度，也可以改成 gridSizeZ/gridSizeX
        waveCompute.SetFloat("_RegionWidth", regionWidthNorm);
    }

    void Update()
    {
        // A) 推进动画参数 - 修复循环以避免采样伪影
        animParamStart = Mathf.Repeat(animParamStart + animSpeed * Time.deltaTime, 1f);
        animParamEnd = Mathf.Repeat(animParamEnd + animSpeed * Time.deltaTime, 1f);
        
        // 确保纹理采样在 [0, 1-epsilon] 范围内以避免边界问题
        // 这防止了从 Y=1.0 采样，因为它可能与 Y=0.0 不匹配
        float texelSize = 1.0f / 256.0f; // 基于256x256纹理的纹理单元大小
        float safeAnimStart = animParamStart * (1.0f - texelSize);
        float safeAnimEnd = animParamEnd * (1.0f - texelSize);
        
        waveCompute.SetFloat("_AnimStart", safeAnimStart);
        waveCompute.SetFloat("_AnimEnd", safeAnimEnd);
        Debug.Log($"[Debug] _AnimStart={safeAnimStart:F3}, _AnimEnd={safeAnimEnd:F3}");

        // B) 计算 quad 区域在归一化 [0,1] 上的滑动窗口
        float maxStart = 1f - regionWidthNorm;
        float regionStart = Mathf.Max(0f, maxStart - Time.time * quadSpeedNormalized);
        float regionEnd = regionStart + regionWidthNorm;
        waveCompute.SetFloat("_QuadStart", regionStart);
        waveCompute.SetFloat("_QuadEnd", regionEnd);
        Debug.Log($"[Debug] QuadNormX=[{regionStart:F3}..{regionEnd:F3}]");

        // C) 计算 Z 轴缩放
        float zScaleProgress = Mathf.Clamp01(Time.time * zScaleSpeed);
        float currentZScale = Mathf.Lerp(zScaleStart, zScaleEnd, zScaleProgress);
        waveCompute.SetFloat("_ZScale", currentZScale);
        waveCompute.SetFloat("_FixedZEnd", fixedZEnd);
        Debug.Log($"[Debug] ZScale={currentZScale:F3} (progress={zScaleProgress:F3})");

        // D) 派发 Compute
        int groups = Mathf.CeilToInt(vertexCount / 128f);
        waveCompute.Dispatch(kernelCS, groups, 1, 1);

        // E) 读取回写顶点和颜色
        outputBuffer.GetData(verts);
        colorBuffer.GetData(vertexColors);
        meshInstance.vertices = verts;
        meshInstance.colors = vertexColors;
        meshInstance.RecalculateNormals();
        meshInstance.RecalculateBounds();
    }

    void OnDestroy()
    {
        inputBuffer?.Release();
        outputBuffer?.Release();
        colorBuffer?.Release();
    }
}
