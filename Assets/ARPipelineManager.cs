using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;
using GaussianSplatting.Runtime; // Required for GaussianSplatRenderer/Asset

[System.Serializable]
public class ARCameraPose
{
    public int frame_index;
    public float timestamp;
    public Vector3 position;
    public Quaternion rotation;
}

[System.Serializable]
public class ARCameraTrajectory
{
    public List<ARCameraPose> keyframes = new List<ARCameraPose>();
}

[System.Serializable]
public class PipelineStatusResponse
{
    public string job_id;
    public string status; // pending, running, succeeded, failed
    public string download_url;
    public string error;
}

[System.Serializable]
public class PipelineUploadResponse
{
    public string job_id;
    public string status;
}

public class ARPipelineManager : MonoBehaviour
{
    [Header("Server Settings")]
    public string serverUrl = "http://192.168.0.10:8000"; // Replace with your GPU server IP

    [Header("Capture Settings")]
    public float captureInterval = 0.2f; // Log a keyframe and image every 0.2 seconds (5 FPS)
    public int maxFrames = 60; // Hard limit to prevent memory spikes

    [Header("Splat Rendering Prefab")]
    public GameObject splatRendererPrefab; // Prefab with GaussianSplatRenderer attached

    private ARPlacementManager placementManager;
    private bool isScanning = false;
    private float scanStartTime = 0f;
    private float lastCaptureTime = 0f;
    private int frameIndex = 0;

    private ARCameraTrajectory trajectory = new ARCameraTrajectory();
    
    // In-memory JPEG frame storage
    private class CapturedFrame
    {
        public string filename;
        public byte[] data;
        public CapturedFrame(string filename, byte[] data)
        {
            this.filename = filename;
            this.data = data;
        }
    }
    private List<CapturedFrame> capturedFrames = new List<CapturedFrame>();

    // Pipeline status tracking
    public enum PipelineState
    {
        Idle,
        Scanning,
        Captured,
        Uploading,
        Reconstructing,
        Downloading,
        Done,
        Failed
    }
    private PipelineState pipelineState = PipelineState.Idle;
    private string statusMessage = "Ready to start 3DGS scan.";
    private string currentJobId = "";
    private float serverStartTime = 0f;

    // GUI scroll and styling
    private Vector2 statusScrollPos = Vector2.zero;
    private Texture2D bgTexture;
    private Texture2D whiteTexture;
    private Texture2D confirmTexture;
    private Texture2D confirmActiveTexture;
    private Texture2D cancelTexture;
    private Texture2D cancelActiveTexture;
    private Texture2D neutralTexture;
    private Texture2D neutralActiveTexture;
    private bool stylesInitialized = false;

    // Real-time quality feedback variables
    private ARCameraManager cameraManager;
    private float currentBrightness = 0.5f;
    private Vector3 lastCamPos;
    private Quaternion lastCamRot;
    private float cameraVelocity = 0f;
    private float cameraAngularVelocity = 0f;

    // Angle tracking variables
    private Vector3 startCamForward;
    private float maxAngleDeviation = 0f;

    void Awake()
    {
        placementManager = GetComponent<ARPlacementManager>();
        cameraManager = FindObjectOfType<ARCameraManager>();
        if (placementManager == null)
        {
            Debug.LogError("[ARPipelineManager] ARPlacementManager is missing on this GameObject!");
        }
    }

    void OnEnable()
    {
        if (cameraManager != null)
        {
            cameraManager.frameReceived += OnFrameReceived;
        }
    }

    void OnDisable()
    {
        if (cameraManager != null)
        {
            cameraManager.frameReceived -= OnFrameReceived;
        }
    }

    void OnFrameReceived(ARCameraFrameEventArgs args)
    {
        if (args.lightEstimation.averageBrightness.HasValue)
        {
            currentBrightness = args.lightEstimation.averageBrightness.Value;
        }
    }

    void Update()
    {
        if (pipelineState != PipelineState.Scanning)
        {
            cameraVelocity = 0f;
            cameraAngularVelocity = 0f;
            return;
        }

        if (Camera.main != null)
        {
            Vector3 curPos = Camera.main.transform.position;
            Quaternion curRot = Camera.main.transform.rotation;
            float dt = Time.deltaTime;

            if (dt > 0.0001f)
            {
                cameraVelocity = (curPos - lastCamPos).magnitude / dt;
                cameraAngularVelocity = Quaternion.Angle(curRot, lastCamRot) / dt;
            }

            lastCamPos = curPos;
            lastCamRot = curRot;

            float dev = Vector3.Angle(Camera.main.transform.forward, startCamForward);
            if (dev > maxAngleDeviation)
            {
                maxAngleDeviation = dev;
            }
        }

        if (Time.time - lastCaptureTime >= captureInterval)
        {
            lastCaptureTime = Time.time;
            
            // Record ARCore Pose
            RecordPose();
            
            // Capture image frame
            StartCoroutine(CaptureFrameCoroutine());
        }
    }

    private void RecordPose()
    {
        if (Camera.main == null) return;

        ARCameraPose pose = new ARCameraPose();
        pose.frame_index = frameIndex;
        pose.timestamp = Time.time - scanStartTime;
        pose.position = Camera.main.transform.position;
        pose.rotation = Camera.main.transform.rotation;

        trajectory.keyframes.Add(pose);
        Debug.Log($"[ARPipeline] Recorded pose for frame {frameIndex} at position {pose.position}");
    }

    private IEnumerator CaptureFrameCoroutine()
    {
        yield return new WaitForEndOfFrame();

        if (capturedFrames.Count >= maxFrames)
        {
            StopScan();
            yield break;
        }

        // Capture half-resolution screenshot to save bandwidth
        int width = Screen.width / 2;
        int height = Screen.height / 2;
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        tex.Apply();

        // Compress to JPEG
        byte[] bytes = tex.EncodeToJPG(75);
        Destroy(tex);

        string filename = $"frame_{frameIndex}.jpg";
        capturedFrames.Add(new CapturedFrame(filename, bytes));
        Debug.Log($"[ARPipeline] Captured {filename} ({bytes.Length} bytes)");

        frameIndex++;
    }

    public void StartScan()
    {
        isScanning = true;
        scanStartTime = Time.time;
        lastCaptureTime = 0f;
        frameIndex = 0;
        trajectory.keyframes.Clear();
        capturedFrames.Clear();
        
        if (Camera.main != null)
        {
            lastCamPos = Camera.main.transform.position;
            lastCamRot = Camera.main.transform.rotation;
            startCamForward = Camera.main.transform.forward;
        }
        maxAngleDeviation = 0f;
        cameraVelocity = 0f;
        cameraAngularVelocity = 0f;

        pipelineState = PipelineState.Scanning;
        statusMessage = "Scanning floor... Move slowly around the furniture.";
        Debug.Log("[ARPipeline] Started 3DGS capture session.");
        
        if (placementManager != null)
        {
            placementManager.SetCurrentState(ARPlacementManager.PlacementState.Scanning);
        }
    }

    public void StopScan()
    {
        isScanning = false;
        pipelineState = PipelineState.Captured;
        statusMessage = $"Scan completed. Captured {capturedFrames.Count} frames.";
        Debug.Log($"[ARPipeline] Scan stopped. Total frames: {capturedFrames.Count}");
    }

    public void CancelScan()
    {
        isScanning = false;
        capturedFrames.Clear();
        trajectory.keyframes.Clear();
        pipelineState = PipelineState.Idle;
        statusMessage = "Scan cancelled.";
        Debug.Log("[ARPipeline] Scan session cancelled.");
    }

    public void TriggerUploadAndReconstruction()
    {
        if (capturedFrames.Count == 0)
        {
            statusMessage = "Error: No frames captured to upload!";
            return;
        }

        StartCoroutine(UploadAndProcessCoroutine());
    }

    private IEnumerator UploadAndProcessCoroutine()
    {
        pipelineState = PipelineState.Uploading;
        statusMessage = "Compiling trajectory and packaging files...";

        // Serialize trajectory JSON
        string trajectoryJson = JsonUtility.ToJson(trajectory, true);
        byte[] trajectoryBytes = System.Text.Encoding.UTF8.GetBytes(trajectoryJson);

        // Build Multi-part HTTP request
        List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
        
        // Add trajectory file
        formData.Add(new MultipartFormFileSection("trajectory", trajectoryBytes, "camera_trajectory.json", "application/json"));
        
        // Add all captured image frames
        for (int i = 0; i < capturedFrames.Count; i++)
        {
            formData.Add(new MultipartFormFileSection("files", capturedFrames[i].data, capturedFrames[i].filename, "image/jpeg"));
        }

        string uploadUrl = $"{serverUrl}/reconstruct";
        statusMessage = $"Uploading payload to {uploadUrl}...";
        Debug.Log($"[ARPipeline] Uploading {capturedFrames.Count} files to server.");

        UnityWebRequest www = UnityWebRequest.Post(uploadUrl, formData);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            pipelineState = PipelineState.Failed;
            statusMessage = $"Upload failed: {www.error}";
            Debug.LogError($"[ARPipeline] Upload error: {www.error}");
            yield break;
        }

        // Parse job_id
        string responseText = www.downloadHandler.text;
        Debug.Log($"[ARPipeline] Upload response: {responseText}");
        PipelineUploadResponse uploadResp = JsonUtility.FromJson<PipelineUploadResponse>(responseText);
        
        if (uploadResp == null || string.IsNullOrEmpty(uploadResp.job_id))
        {
            pipelineState = PipelineState.Failed;
            statusMessage = "Upload failed: Invalid server response.";
            yield break;
        }

        currentJobId = uploadResp.job_id;
        pipelineState = PipelineState.Reconstructing;
        serverStartTime = Time.time;
        statusMessage = "Upload successful. Server reconstruction started.";

        // Start polling status
        StartCoroutine(PollStatusCoroutine());
    }

    private IEnumerator PollStatusCoroutine()
    {
        string statusUrl = $"{serverUrl}/status/{currentJobId}";
        Debug.Log($"[ARPipeline] Starting polling status at {statusUrl}");

        while (pipelineState == PipelineState.Reconstructing)
        {
            yield return new WaitForSeconds(2.0f);

            UnityWebRequest www = UnityWebRequest.Get(statusUrl);
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string json = www.downloadHandler.text;
                PipelineStatusResponse statusResp = JsonUtility.FromJson<PipelineStatusResponse>(json);

                if (statusResp != null)
                {
                    float elapsed = Time.time - serverStartTime;
                    statusMessage = $"Server State: {statusResp.status}\nElapsed Time: {elapsed:F1}s";

                    if (statusResp.status == "succeeded")
                    {
                        pipelineState = PipelineState.Downloading;
                        statusMessage = "Reconstruction succeeded! Downloading AssetBundle...";
                        StartCoroutine(DownloadAssetBundleCoroutine(statusResp.download_url));
                        yield break;
                    }
                    else if (statusResp.status == "failed")
                    {
                        pipelineState = PipelineState.Failed;
                        statusMessage = $"Reconstruction failed on server: {statusResp.error}";
                        yield break;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[ARPipeline] Status polling failed: {www.error}");
            }
        }
    }

    private IEnumerator DownloadAssetBundleCoroutine(string downloadUrl)
    {
        Debug.Log($"[ARPipeline] Downloading AssetBundle from: {downloadUrl}");
        
        UnityWebRequest www = UnityWebRequest.Get(downloadUrl);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            pipelineState = PipelineState.Failed;
            statusMessage = $"Download failed: {www.error}";
            Debug.LogError($"[ARPipeline] AssetBundle download error: {www.error}");
            yield break;
        }

        string localPath = Path.Combine(Application.persistentDataPath, $"splat_{currentJobId}.unity3d");
        try
        {
            File.WriteAllBytes(localPath, www.downloadHandler.data);
            Debug.Log($"[ARPipeline] AssetBundle saved locally to: {localPath}");
        }
        catch (System.Exception ex)
        {
            pipelineState = PipelineState.Failed;
            statusMessage = $"Failed to save bundle file: {ex.Message}";
            Debug.LogError($"[ARPipeline] File save error: {ex.Message}");
            yield break;
        }

        AssetBundle bundle = AssetBundle.LoadFromFile(localPath);
        if (bundle == null)
        {
            pipelineState = PipelineState.Failed;
            statusMessage = "Download failed: Loaded AssetBundle is null.";
            yield break;
        }

        pipelineState = PipelineState.Done;
        statusMessage = "AssetBundle downloaded. Auto-saving and spawning!";
        Debug.Log("[ARPipeline] AssetBundle successfully loaded into memory.");

        SpawnSplatFurniture(bundle, localPath);
    }

    private void SpawnSplatFurniture(AssetBundle bundle, string localPath)
    {
        if (splatRendererPrefab == null)
        {
            statusMessage = "Spawn error: splatRendererPrefab is not assigned!";
            bundle.Unload(true);
            return;
        }

        GaussianSplatAsset splatAsset = null;
        string[] assetNames = bundle.GetAllAssetNames();
        foreach (var name in assetNames)
        {
            if (name.EndsWith(".asset"))
            {
                splatAsset = bundle.LoadAsset<GaussianSplatAsset>(name);
                break;
            }
        }

        if (splatAsset == null)
        {
            statusMessage = "Spawn error: No GaussianSplatAsset found in AssetBundle.";
            bundle.Unload(true);
            return;
        }

        Vector3 spawnPos = Camera.main.transform.position + Camera.main.transform.forward * 1.5f;
        if (placementManager != null && placementManager.GetLowestPlaneY() != float.MaxValue)
        {
            spawnPos.y = placementManager.GetLowestPlaneY();
        }

        GameObject splatGo = Instantiate(splatRendererPrefab, spawnPos, Quaternion.identity);
        if (splatGo != null)
        {
            if (placementManager != null)
            {
                placementManager.RegisterSplatPath(splatGo, localPath);
            }

            GaussianSplatRenderer renderer = splatGo.GetComponent<GaussianSplatRenderer>();
            if (renderer != null)
            {
                var assetField = renderer.GetType().GetField("m_Asset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (assetField != null)
                {
                    assetField.SetValue(renderer, splatAsset);
                    Debug.Log("[ARPipeline] Assigned GaussianSplatAsset via reflection.");
                }
                else
                {
                    Debug.LogError("[ARPipeline] Failed to find m_Asset field on GaussianSplatRenderer!");
                }
            }
            else
            {
                Debug.LogWarning("[ARPipeline] GaussianSplatRenderer component is missing on splatRendererPrefab!");
            }

            if (placementManager != null)
            {
                placementManager.RunAdjustColliderAndPivot(splatGo);
                placementManager.AddPlacedObject(splatGo);
                
                placementManager.AutoSaveSplatToStorage(localPath, "스캔 가구");
                placementManager.SetCurrentState(ARPlacementManager.PlacementState.Placed);
            }

            statusMessage = "Furniture spawned and saved to storage successfully!";
            Debug.Log("[ARPipeline] Spawned and auto-saved reconstructed furniture.");
        }

        bundle.Unload(false);
    }

    private void InitStyles()
    {
        if (stylesInitialized) return;
        bgTexture = MakeTex(2, 2, new Color(0.1f, 0.1f, 0.1f, 0.85f));
        whiteTexture = MakeTex(2, 2, Color.white);
        
        confirmTexture = MakeTex(2, 2, new Color(0.1f, 0.65f, 0.3f, 1.0f));
        confirmActiveTexture = MakeTex(2, 2, new Color(0.08f, 0.5f, 0.25f, 1.0f));
        cancelTexture = MakeTex(2, 2, new Color(0.8f, 0.2f, 0.2f, 1.0f));
        cancelActiveTexture = MakeTex(2, 2, new Color(0.65f, 0.15f, 0.15f, 1.0f));
        neutralTexture = MakeTex(2, 2, new Color(0.35f, 0.4f, 0.45f, 1.0f));
        neutralActiveTexture = MakeTex(2, 2, new Color(0.25f, 0.3f, 0.35f, 1.0f));
        
        stylesInitialized = true;
    }

    private Texture2D MakeTex(int width, int height, Color col)
    {
        Color[] pix = new Color[width * height];
        for (int i = 0; i < pix.Length; ++i) pix[i] = col;
        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        return result;
    }

    void OnDestroy()
    {
        if (bgTexture != null) Destroy(bgTexture);
        if (whiteTexture != null) Destroy(whiteTexture);
        if (confirmTexture != null) Destroy(confirmTexture);
        if (confirmActiveTexture != null) Destroy(confirmActiveTexture);
        if (cancelTexture != null) Destroy(cancelTexture);
        if (cancelActiveTexture != null) Destroy(cancelActiveTexture);
        if (neutralTexture != null) Destroy(neutralTexture);
        if (neutralActiveTexture != null) Destroy(neutralActiveTexture);
    }

    void OnGUI()
    {
        if (placementManager == null || 
            placementManager.GetCurrentState() == ARPlacementManager.PlacementState.MainMenu || 
            !placementManager.isPipelineModeActive)
        {
            return;
        }

        InitStyles();

        // GUI Styles
        GUIStyle headerStyle = new GUIStyle();
        headerStyle.fontSize = 34;
        headerStyle.fontStyle = FontStyle.Bold;
        headerStyle.normal.textColor = Color.yellow;
        headerStyle.alignment = TextAnchor.MiddleCenter;

        GUIStyle textStyle = new GUIStyle();
        textStyle.fontSize = 28;
        textStyle.normal.textColor = Color.white;
        
        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.fontSize = 30;
        buttonStyle.fontStyle = FontStyle.Bold;

        GUIStyle panelStyle = new GUIStyle(GUI.skin.box);
        panelStyle.normal.background = bgTexture;

        GUIStyle confirmStyle = new GUIStyle(buttonStyle);
        confirmStyle.normal.background = confirmTexture;
        confirmStyle.hover.background = confirmTexture;
        confirmStyle.active.background = confirmActiveTexture;
        confirmStyle.normal.textColor = Color.white;
        confirmStyle.alignment = TextAnchor.MiddleCenter;

        GUIStyle cancelStyle = new GUIStyle(buttonStyle);
        cancelStyle.normal.background = cancelTexture;
        cancelStyle.hover.background = cancelTexture;
        cancelStyle.active.background = cancelActiveTexture;
        cancelStyle.normal.textColor = Color.white;
        cancelStyle.alignment = TextAnchor.MiddleCenter;

        GUIStyle neutralStyle = new GUIStyle(buttonStyle);
        neutralStyle.normal.background = neutralTexture;
        neutralStyle.hover.background = neutralTexture;
        neutralStyle.active.background = neutralActiveTexture;
        neutralStyle.normal.textColor = Color.white;
        neutralStyle.alignment = TextAnchor.MiddleCenter;

        // Draw left-middle dashboard panel for 3DGS Pipeline controls
        float panelW = 420f;
        float panelH = 550f;
        float panelX = 20f;
        float panelY = Screen.height / 2f - 275f;

        // Block Raycasts/touches over the Pipeline GUI area
        float guiX = Input.mousePosition.x;
        float guiY = Screen.height - Input.mousePosition.y;
        Rect panelRect = new Rect(panelX, panelY, panelW, panelH);
        
        // Prevent placement manager from registering clicks when touching this panel
        if (placementManager != null && panelRect.Contains(new Vector2(guiX, guiY)))
        {
            // We just let standard touch ignore it
        }

        GUI.Box(panelRect, "", panelStyle);
        GUILayout.BeginArea(new Rect(panelX + 15, panelY + 15, panelW - 30, panelH - 30));

        GUILayout.Label("3DGS Pipeline", headerStyle);
        GUILayout.Space(10);

        GUILayout.Label($"State: {pipelineState}", textStyle);
        GUILayout.Label($"Frames: {capturedFrames.Count} / {maxFrames}", textStyle);
        
        GUILayout.Space(10);
        statusScrollPos = GUILayout.BeginScrollView(statusScrollPos, GUILayout.Height(150));
        GUILayout.Label(statusMessage, textStyle);
        GUILayout.EndScrollView();
        
        GUILayout.Space(15);

        // Control Buttons based on state
        if (pipelineState == PipelineState.Idle || pipelineState == PipelineState.Failed || pipelineState == PipelineState.Done)
        {
            if (GUILayout.Button("스캔 시작 (Start Scan)", buttonStyle, GUILayout.Height(80)))
            {
                StartScan();
            }
        }
        else if (pipelineState == PipelineState.Scanning)
        {
            if (GUILayout.Button("스캔 완료 (Finish Scan)", buttonStyle, GUILayout.Height(80)))
            {
                StopScan();
            }
            GUILayout.Space(10);
            if (GUILayout.Button("취소 (Cancel)", buttonStyle, GUILayout.Height(65)))
            {
                CancelScan();
            }
        }
        else if (pipelineState == PipelineState.Captured)
        {
            if (GUILayout.Button("서버 전송 및 복원 (Upload)", buttonStyle, GUILayout.Height(80)))
            {
                TriggerUploadAndReconstruction();
            }
            GUILayout.Space(10);
            if (GUILayout.Button("다시 촬영 (Retake)", buttonStyle, GUILayout.Height(65)))
            {
                CancelScan();
            }
        }
        else
        {
            // Uploading, Reconstructing, Downloading
            GUI.enabled = false;
            GUILayout.Button("처리 중... (Processing)", buttonStyle, GUILayout.Height(80));
            GUI.enabled = true;
        }

        GUILayout.Space(10);
        
        // Allow configuring server URL at runtime
        GUILayout.Label("Server IP:", textStyle);
        serverUrl = GUILayout.TextField(serverUrl, textStyle, GUILayout.Height(50));

        GUILayout.EndArea();

        // Draw centered target guide and Quality HUD if scanning or captured
        if (pipelineState == PipelineState.Scanning || pipelineState == PipelineState.Captured)
        {
            // 1. Draw Centered Target Brackets
            float cx = Screen.width / 2f;
            float cy = Screen.height / 2f;
            DrawCornerBrackets(cx, cy, 480f, 480f, 6f, 45f, new Color(1f, 0.9f, 0f, 0.8f));

            // Draw alignment text under brackets
            GUIStyle alignTextStyle = new GUIStyle();
            alignTextStyle.fontSize = 28;
            alignTextStyle.fontStyle = FontStyle.Bold;
            alignTextStyle.normal.textColor = Color.white;
            alignTextStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(new Rect(cx - 300, cy + 265, 600, 50), "이 영역 안에 가구를 정렬해 주세요", alignTextStyle);

            // 2. Draw Top-Center Quality HUD Panel
            float hudW = 640f;
            float hudH = 180f;
            float hudX = (Screen.width - hudW) / 2f;
            float hudY = 20f;

            GUI.Box(new Rect(hudX, hudY, hudW, hudH), "", panelStyle);

            GUILayout.BeginArea(new Rect(hudX + 15, hudY + 10, hudW - 30, hudH - 20));
            
            GUIStyle hudTitleStyle = new GUIStyle();
            hudTitleStyle.fontSize = 30;
            hudTitleStyle.fontStyle = FontStyle.Bold;
            hudTitleStyle.normal.textColor = Color.yellow;
            hudTitleStyle.alignment = TextAnchor.MiddleCenter;

            GUILayout.Label("촬영 가이드 (Quality Feedback)", hudTitleStyle);
            GUILayout.Space(8);

            GUIStyle alertStyle = new GUIStyle();
            alertStyle.fontSize = 26;
            alertStyle.fontStyle = FontStyle.Bold;

            // Speed warning
            if (cameraVelocity > 0.4f || cameraAngularVelocity > 35f)
            {
                alertStyle.normal.textColor = Color.red;
                GUILayout.Label("⚠️ 속도: 너무 빠릅니다! 천천히 움직여 주세요.", alertStyle);
            }
            else
            {
                alertStyle.normal.textColor = Color.green;
                GUILayout.Label("✓ 속도: 양호 (속도 안정적)", alertStyle);
            }

            // Lighting warning
            if (currentBrightness < 0.35f)
            {
                alertStyle.normal.textColor = Color.yellow;
                GUILayout.Label("⚠️ 조명: 너무 어둡습니다! 더 밝은 곳에서 촬영해 주세요.", alertStyle);
            }
            else
            {
                alertStyle.normal.textColor = Color.green;
                GUILayout.Label("✓ 조명: 양호 (밝기 충분함)", alertStyle);
            }

            // Angle coverage warning
            if (pipelineState == PipelineState.Scanning && maxAngleDeviation < 25f && capturedFrames.Count > 10)
            {
                alertStyle.normal.textColor = Color.cyan;
                GUILayout.Label("🔄 각도: 다른 각도(좌/우/위)에서도 촬영해 주세요.", alertStyle);
            }
            else
            {
                alertStyle.normal.textColor = Color.green;
                GUILayout.Label("✓ 각도: 양호 (다양한 각도 분산됨)", alertStyle);
            }

            // Framing prompt
            GUIStyle hintStyle = new GUIStyle();
            hintStyle.fontSize = 24;
            hintStyle.normal.textColor = Color.gray;
            hintStyle.alignment = TextAnchor.MiddleCenter;
            GUILayout.Label("가이드: 가구 전체가 화면에 들어오게 천천히 도세요.", hintStyle);

            GUILayout.EndArea();
        }

        // Draw Centered Bottom Scanning Button
        if (placementManager != null && placementManager.isPipelineModeActive)
        {
            float btnW = 460f;
            float btnH = 110f;
            float btnX = (Screen.width - btnW) / 2f;
            float btnY = Screen.height - 160f;

            Rect mainBtnRect = new Rect(btnX, btnY, btnW, btnH);

            if (pipelineState == PipelineState.Idle || pipelineState == PipelineState.Failed || pipelineState == PipelineState.Done)
            {
                if (GUI.Button(mainBtnRect, "스캔하기 (Start Scan)", confirmStyle))
                {
                    StartScan();
                }
            }
            else if (pipelineState == PipelineState.Scanning)
            {
                if (GUI.Button(mainBtnRect, "스캔 완료 (Finish Scan)", cancelStyle))
                {
                    StopScan();
                }
            }
            else if (pipelineState == PipelineState.Captured)
            {
                if (GUI.Button(mainBtnRect, "서버 전송 및 복원 (Upload)", confirmStyle))
                {
                    TriggerUploadAndReconstruction();
                }
            }
            else
            {
                GUI.enabled = false;
                GUI.Button(mainBtnRect, "처리 중... (Processing)", neutralStyle);
                GUI.enabled = true;
            }
        }
    }

    private void DrawCornerBrackets(float cx, float cy, float w, float h, float thickness, float length, Color color)
    {
        if (whiteTexture == null) return;
        
        Color prevColor = GUI.color;
        GUI.color = color;
        
        float halfW = w / 2f;
        float halfH = h / 2f;
        
        float left = cx - halfW;
        float right = cx + halfW;
        float top = cy - halfH;
        float bottom = cy + halfH;
        
        // Top-Left
        GUI.DrawTexture(new Rect(left, top, length, thickness), whiteTexture);
        GUI.DrawTexture(new Rect(left, top, thickness, length), whiteTexture);
        
        // Top-Right
        GUI.DrawTexture(new Rect(right - length, top, length, thickness), whiteTexture);
        GUI.DrawTexture(new Rect(right - thickness, top, thickness, length), whiteTexture);
        
        // Bottom-Left
        GUI.DrawTexture(new Rect(left, bottom - thickness, length, thickness), whiteTexture);
        GUI.DrawTexture(new Rect(left, bottom - length, thickness, length), whiteTexture);
        
        // Bottom-Right
        GUI.DrawTexture(new Rect(right - length, bottom - thickness, length, thickness), whiteTexture);
        GUI.DrawTexture(new Rect(right - thickness, bottom - length, thickness, length), whiteTexture);
        
        GUI.color = prevColor;
    }
}
