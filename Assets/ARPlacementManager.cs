using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using GaussianSplatting.Runtime;

public class ARPlacementManager : MonoBehaviour
{
    public GameObject furniturePrefab; // 우리가 배치 시킬 의자 등등이 있는 칸
    
    [Header("Default Assets")]
    public GameObject woodenChairPrefab;
    public GaussianSplatAsset defaultSplatAsset;

    private GameObject spawnedObject;
    public GameObject SpawnedObject => spawnedObject;
    private ARRaycastManager raycastManager;
    private ARPlaneManager planeManager;
    static List<ARRaycastHit> hits = new List<ARRaycastHit>();

    // OnGUI Debugging & UI states
    private Vector2 lastInputPos;
    private bool lastRaycastSuccess = false;
    private Vector3 lastHitPos;
    private string debugMessage = "Initialized. Scan floor and tap screen to spawn.";

    // Minimum size threshold for AR planes (in meters)
    // Footprint of the chair is 0.54m x 0.68m. We require at least 0.55m x 0.60m.
    private float minPlaneWidth = 0.55f;
    private float minPlaneLength = 0.60f;

    // Track the lowest plane Y coordinate to identify the floor level
    private float lowestPlaneY = float.MaxValue;

    // State Machine definition
    public enum PlacementState
    {
        MainMenu,            // 시작 메인 메뉴 화면
        Scanning,            // 평면 탐색 중 (스캔 중)
        Previewing,          // 평면 선택 완료, 프리뷰 단계 (확정/취소 버튼 노출)
        Placed,              // 배치 완료 (의자 탭 시 메뉴 노출)
        SelectionOptions,    // 의자 탭하여 뜨는 팝업 메뉴 노출 상태
        AdjustingRotation,   // 회전 미세조정 상태
        StorageMenu          // 보관함 메뉴 상태
    }

    private PlacementState currentState = PlacementState.MainMenu;
    private PlacementState? pendingState = null;

    // Main Menu State Control
    [HideInInspector]
    public bool isPipelineModeActive = false;
    private ARRotationGizmo activeGizmo;

    // Splat path tracking
    private Dictionary<GameObject, string> splatBundlePaths = new Dictionary<GameObject, string>();

    public void RegisterSplatPath(GameObject obj, string path)
    {
        if (splatBundlePaths == null) splatBundlePaths = new Dictionary<GameObject, string>();
        splatBundlePaths[obj] = path;
    }

    // Fine rotation variables
    private float fineRotationSpeed = 8f; // 8 degrees per second (slow for fine adjustment)
    private float groundingOffset = 0.015f; // Sink legs by 1.5cm to ensure grounding
    private List<GameObject> placedObjects = new List<GameObject>();
    private bool isPitchingPlus = false;
    private bool isPitchingMinus = false;
    private bool isYawPlus = false;
    private bool isYawMinus = false;
    private bool isRollPlus = false;
    private bool isRollMinus = false;

    // UI Styling textures (created procedurally to avoid external dependencies)
    private Texture2D bgTexture;
    private Texture2D confirmTexture;
    private Texture2D confirmActiveTexture;
    private Texture2D cancelTexture;
    private Texture2D cancelActiveTexture;
    private Texture2D neutralTexture;
    private Texture2D neutralActiveTexture;
    private bool stylesInitialized = false;

    // Scroll and tab states for 10-slot storage
    private int activeStorageTab = 0; // 0 = Furniture, 1 = Layout
    private Vector2 furnitureScrollPos = Vector2.zero;
    private Vector2 layoutScrollPos = Vector2.zero;

    // Flash and screen capture variables
    private float flashDuration = 0.4f;
    private float flashTimer = 0f;
    private Texture2D whiteTexture;

    void Awake()
    {
        raycastManager = GetComponent<ARRaycastManager>();
        planeManager = GetComponent<ARPlaneManager>();

        if (raycastManager == null)
        {
            Debug.LogError("[ARPlacementManager] ARRaycastManager component is missing on this GameObject!");
        }
        if (planeManager == null)
        {
            Debug.LogWarning("[ARPlacementManager] ARPlaneManager component is missing on this GameObject.");
        }
    }

    void Start()
    {
        InitializeDefaultSlots();
    }

    private void InitializeDefaultSlots()
    {
        if (!PlayerPrefs.HasKey("InitializedDefaultSlots_V4"))
        {
            // Clear existing default slots to ensure clean overwrite
            for (int i = 0; i < 10; i++)
            {
                PlayerPrefs.DeleteKey("FurnitureSlot_" + i);
            }

            // Slot 1: Default Chair (furniturePrefab)
            if (furniturePrefab != null)
            {
                FurnitureSaveData data1 = new FurnitureSaveData();
                data1.prefabName = "기본 의자 (Default)";
                data1.position = Vector3.zero;
                data1.rotation = Quaternion.identity;
                data1.meshLocalPos = new Vector3(-0.01f, 0.75f, 0.44f);
                data1.meshLocalRot = Quaternion.Euler(35.23f, 9.40f, 184.27f);
                data1.assetBundlePath = "";

                PlayerPrefs.SetString("FurnitureSlot_0", JsonUtility.ToJson(data1));
            }

            // Slot 2: Wooden Chair (woodenChairPrefab)
            if (woodenChairPrefab != null)
            {
                FurnitureSaveData data2 = new FurnitureSaveData();
                data2.prefabName = "나무 의자 (Wooden)";
                data2.position = Vector3.zero;
                data2.rotation = Quaternion.identity;
                data2.meshLocalPos = Vector3.zero;
                data2.meshLocalRot = Quaternion.identity;
                data2.assetBundlePath = "INTERNAL_WOODEN_CHAIR";

                PlayerPrefs.SetString("FurnitureSlot_1", JsonUtility.ToJson(data2));
            }

            // Slot 3: Default Point Cloud Splat (defaultSplatAsset)
            if (defaultSplatAsset != null)
            {
                FurnitureSaveData data3 = new FurnitureSaveData();
                data3.prefabName = "스캔 가구 (기본)";
                data3.position = Vector3.zero;
                data3.rotation = Quaternion.identity;
                data3.meshLocalPos = new Vector3(-0.01f, 0.75f, 0.44f);
                data3.meshLocalRot = Quaternion.Euler(35.23f, 9.40f, 184.27f);
                data3.assetBundlePath = "INTERNAL_DEFAULT_SPLAT";

                PlayerPrefs.SetString("FurnitureSlot_2", JsonUtility.ToJson(data3));
            }

            PlayerPrefs.SetInt("InitializedDefaultSlots_V4", 1);
            PlayerPrefs.Save();
            Debug.Log("[ARPlacementManager] Initialized default storage slots (V3).");
        }
    }

    void OnEnable()
    {
        if (planeManager != null)
        {
            planeManager.planesChanged += OnPlanesChanged;
        }
    }

    void OnDisable()
    {
        if (planeManager != null)
        {
            planeManager.planesChanged -= OnPlanesChanged;
        }
    }

    void OnDestroy()
    {
        DisableRotationGizmo();

        // Clean up procedural textures to prevent memory leaks
        if (bgTexture != null) Destroy(bgTexture);
        if (confirmTexture != null) Destroy(confirmTexture);
        if (confirmActiveTexture != null) Destroy(confirmActiveTexture);
        if (cancelTexture != null) Destroy(cancelTexture);
        if (cancelActiveTexture != null) Destroy(cancelActiveTexture);
        if (neutralTexture != null) Destroy(neutralTexture);
        if (neutralActiveTexture != null) Destroy(neutralActiveTexture);
        if (whiteTexture != null) Destroy(whiteTexture);
    }

    // ARPlaneManager가 평면을 감지하거나 업데이트할 때 실행
    void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        foreach (var plane in args.added)
        {
            UpdateLowestPlaneY(plane);
        }
        foreach (var plane in args.updated)
        {
            UpdateLowestPlaneY(plane);
        }
    }

    private void UpdateLowestPlaneY(ARPlane plane)
    {
        // floor detection은 상향 수평면만 고려합니다.
        if (plane.alignment != PlaneAlignment.HorizontalUp) return;

        float planeY = plane.transform.position.y;
        float cameraY = Camera.main.transform.position.y;

        // 카메라 높이보다 1.8m 이상 낮은 평면은 측정 오류로 간주하여 바닥 기준(lowestPlaneY) 계산에서 제외합니다.
        if (cameraY - planeY > 1.8f) return;

        if (planeY < lowestPlaneY)
        {
            lowestPlaneY = planeY;
            Debug.Log($"[ARPlacementManager] New lowest floor Y detected: {lowestPlaneY:F3}m.");
        }
    }

    private void RecalculateLowestPlaneY()
    {
        if (planeManager == null) return;

        float lowestY = float.MaxValue;
        float cameraY = Camera.main.transform.position.y;
        foreach (var plane in planeManager.trackables)
        {
            // 현재 추적 중이고 바닥 방향인 수평 평면만 검사하여 바닥 높이를 실시간 갱신 (센서 드리프트 복구용)
            if (plane.trackingState == TrackingState.Tracking && plane.alignment == PlaneAlignment.HorizontalUp)
            {
                float planeY = plane.transform.position.y;

                // 카메라 높이보다 1.8m 이상 낮은 평면은 측정 오류로 간주하여 제외합니다.
                if (cameraY - planeY > 1.8f) continue;

                if (planeY < lowestY)
                {
                    lowestY = planeY;
                }
            }
        }

        if (lowestY != float.MaxValue)
        {
            lowestPlaneY = lowestY;
        }
    }

    private bool IsPlaneValid(ARPlane plane)
    {
        if (plane == null) return false;

        // 1. 방향 조건: 바닥과 평행한 상향 수평면만 유효
        if (plane.alignment != PlaneAlignment.HorizontalUp) return false;

        // 2. 크기 조건: 가로/세로 모두 최소 임계값 이상인지 확인 (의자 등받이/다리 사이 좁은 틈 평면 제거)
        float width = plane.size.x;
        float length = plane.size.y;
        if (width < minPlaneWidth || length < minPlaneLength) return false;

        // 3. 카메라 높이 기준 필터링: 바닥이나 책상은 항상 카메라보다 아래에 있습니다.
        // 카메라 높이와 최소 45cm 차이(아래)가 나는 평면만 유효로 판정하여 허공에 뜨는 유령 평면을 완전히 차단합니다.
        float planeY = plane.transform.position.y;
        float cameraY = Camera.main.transform.position.y;
        if (planeY > cameraY - 0.45f) return false;

        // 4. 이중 대역(바닥 & 책상/테이블) 높이 필터링:
        // - 바닥 대역: 가장 낮은 평면(바닥)과 오차가 8cm 이내인 평면만 허용
        // - 책상/테이블 대역: 바닥 기준 70cm ~ 82cm 높이에 위치한 평면만 허용
        // 이 범위들을 벗어나는 중복/유령 평면(예: 스툴 Y=50cm 또는 1m 이상 허공 표면)은 모두 차단합니다.
        if (lowestPlaneY != float.MaxValue)
        {
            float diff = planeY - lowestPlaneY;
            bool isFloor = Mathf.Abs(diff) <= 0.08f;
            bool isTable = diff >= 0.70f && diff <= 0.82f;

            if (!isFloor && !isTable) return false;
        }

        return true;
    }

    // 매 프레임 모든 평면의 컴포넌트(렌더러/콜라이더)를 활성화/비활성화
    // 중요: ARFoundation의 추적이 멈추는 것을 방지하기 위해 GameObject 자체를 비활성화(SetActive)하지 않고 컴포넌트만 끕니다.
    private void UpdatePlanesState()
    {
        if (planeManager == null) return;

        // 드리프트 복구를 위해 바닥 높이 재계산
        RecalculateLowestPlaneY();

        // 주황색 plane들이 뜨지 않도록 시각화는 항상 끕니다.
        bool showPlanes = false;

        foreach (var plane in planeManager.trackables)
        {
            bool isValid = IsPlaneValid(plane);

            var visualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
            var meshRenderer = plane.GetComponent<MeshRenderer>();
            var lineRenderer = plane.GetComponent<LineRenderer>();
            var meshCollider = plane.GetComponent<MeshCollider>();

            if (visualizer != null) visualizer.enabled = showPlanes;
            if (meshRenderer != null) meshRenderer.enabled = showPlanes;
            if (lineRenderer != null) lineRenderer.enabled = showPlanes;
            if (meshCollider != null) meshCollider.enabled = isValid;
        }
    }

    private Transform GetRotationTransform(GameObject obj)
    {
        if (obj == null) return null;
        Transform meshTrans = obj.transform.Find("Furniture_Mesh");
        if (meshTrans != null) return meshTrans;
        return obj.transform;
    }

    void Update()
    {
        if (pendingState.HasValue)
        {
            currentState = pendingState.Value;
            pendingState = null;
        }

        // 1. 평면의 유효성 검사 및 컴포넌트 제어
        UpdatePlanesState();

        // Update screen flash timer
        if (flashTimer > 0f)
        {
            flashTimer -= Time.deltaTime;
        }

        if (currentState == PlacementState.MainMenu || currentState == PlacementState.StorageMenu || isPipelineModeActive) return;

        // 1.5. Apply fine rotation if in AdjustingRotation state
        if (currentState == PlacementState.AdjustingRotation && spawnedObject != null)
        {
            Transform meshTransform = GetRotationTransform(spawnedObject);
            if (meshTransform != null)
            {
                if (isPitchingPlus) meshTransform.Rotate(Vector3.right, fineRotationSpeed * Time.deltaTime, Space.Self);
                if (isPitchingMinus) meshTransform.Rotate(Vector3.right, -fineRotationSpeed * Time.deltaTime, Space.Self);
                if (isYawPlus) meshTransform.Rotate(Vector3.up, fineRotationSpeed * Time.deltaTime, Space.Self);
                if (isYawMinus) meshTransform.Rotate(Vector3.up, -fineRotationSpeed * Time.deltaTime, Space.Self);
                if (isRollPlus) meshTransform.Rotate(Vector3.forward, fineRotationSpeed * Time.deltaTime, Space.Self);
                if (isRollMinus) meshTransform.Rotate(Vector3.forward, -fineRotationSpeed * Time.deltaTime, Space.Self);
            }
        }

        Vector2 inputPosition;
        bool isDrag;
        if (!TryGetInputPosition(out inputPosition, out isDrag)) return;

        // 2. 터치 좌표가 GUI 버튼 영역 내에 있는지 검사하여 오동작(클릭 관통) 방지
        if (IsPointerOverUI(inputPosition))
        {
            Debug.Log("[ARPlacementManager] Touch ignored because it is over UI.");
            return;
        }

        lastInputPos = inputPosition;

        // 3. 상태 머신 터치 핸들링
        if (currentState == PlacementState.Scanning)
        {
            if (!isDrag)
            {
                // Check if we hit an existing placed chair first!
                Ray ray = Camera.main.ScreenPointToRay(inputPosition);
                RaycastHit hitInfo;
                bool hitPlaced = false;
                if (Physics.Raycast(ray, out hitInfo))
                {
                    foreach (var obj in placedObjects)
                    {
                        if (obj != null && (hitInfo.transform == obj.transform || hitInfo.transform.IsChildOf(obj.transform)))
                        {
                            spawnedObject = obj;
                            currentState = PlacementState.SelectionOptions;
                            debugMessage = "Placed chair selected. Choose an option.";
                            Debug.Log("[ARPlacementManager] Placed chair hit! Opening selection menu.");
                            hitPlaced = true;
                            break;
                        }
                    }
                }

                if (!hitPlaced)
                {
                    // Tapping empty space in Placement Mode no longer spawns furniture automatically.
                    // Users must explicitly spawn furniture from the storage menu.
                    // HandleScanningTouch(inputPosition);
                }
            }
        }
        else if (currentState == PlacementState.Previewing)
        {
            HandlePreviewingTouch(inputPosition);
        }
        else if (currentState == PlacementState.Placed)
        {
            if (!isDrag)
            {
                HandlePlacedTouch(inputPosition);
            }
        }
    }

    private void HandleScanningTouch(Vector2 inputPosition)
    {
        if (raycastManager == null) return;

        TrackableType trackableTypes = TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds;
        if (raycastManager.Raycast(inputPosition, hits, trackableTypes))
        {
            ARRaycastHit validHit = default;
            bool foundValidHit = false;

            foreach (var hit in hits)
            {
                if (planeManager != null)
                {
                    ARPlane plane = planeManager.GetPlane(hit.trackableId);
                    if (IsPlaneValid(plane))
                    {
                        validHit = hit;
                        foundValidHit = true;
                        break;
                    }
                }
                else
                {
                    validHit = hit;
                    foundValidHit = true;
                    break;
                }
            }

            if (foundValidHit)
            {
                Pose hitPose = validHit.pose;
                lastRaycastSuccess = true;
                lastHitPos = hitPose.position;

                if (spawnedObject == null)
                {
                    if (furniturePrefab == null)
                    {
                        Debug.LogError("[ARPlacementManager] furniturePrefab is not assigned in the inspector!");
                        return;
                    }

                    // 의자가 기울어지지 않도록 강제로 월드 수직(Gravity Up) 축에 맞춰 회전값 설정
                    Vector3 forward = hitPose.rotation * Vector3.forward;
                    forward.y = 0; // 수평 투영
                    Quaternion uprightRotation;
                    if (forward.sqrMagnitude > 0.001f)
                    {
                        forward.Normalize();
                        uprightRotation = Quaternion.LookRotation(forward, Vector3.up);
                    }
                    else
                    {
                        uprightRotation = Quaternion.identity;
                    }

                    Debug.Log($"[ARPlacementManager] Instantiating preview '{furniturePrefab.name}' at {hitPose.position} with upright rotation.");
                    spawnedObject = Instantiate(furniturePrefab, hitPose.position, uprightRotation);
                    if (spawnedObject != null)
                    {
                        AdjustColliderAndPivot(spawnedObject);
                        currentState = PlacementState.Previewing;
                        debugMessage = "Preview spawned. Tap to move, or confirm/cancel below.";
                    }
                }
            }
        }
    }

    private void HandlePreviewingTouch(Vector2 inputPosition)
    {
        if (raycastManager == null || spawnedObject == null) return;

        TrackableType trackableTypes = TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds;
        if (raycastManager.Raycast(inputPosition, hits, trackableTypes))
        {
            ARRaycastHit validHit = default;
            bool foundValidHit = false;

            foreach (var hit in hits)
            {
                if (planeManager != null)
                {
                    ARPlane plane = planeManager.GetPlane(hit.trackableId);
                    if (IsPlaneValid(plane))
                    {
                        validHit = hit;
                        foundValidHit = true;
                        break;
                    }
                }
                else
                {
                    validHit = hit;
                    foundValidHit = true;
                    break;
                }
            }

            if (foundValidHit)
            {
                Pose hitPose = validHit.pose;
                lastRaycastSuccess = true;
                lastHitPos = hitPose.position;

                // 위치 이동 및 강제 수직 정렬 (기울어짐 원천 차단)
                spawnedObject.transform.position = hitPose.position;

                Vector3 forward = hitPose.rotation * Vector3.forward;
                forward.y = 0;
                if (forward.sqrMagnitude > 0.001f)
                {
                    forward.Normalize();
                    spawnedObject.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
                }
                else
                {
                    spawnedObject.transform.rotation = Quaternion.identity;
                }

                debugMessage = "Moved preview to: " + hitPose.position.ToString();
            }
        }
    }

    private void HandlePlacedTouch(Vector2 inputPosition)
    {
        Ray ray = Camera.main.ScreenPointToRay(inputPosition);
        RaycastHit hitInfo;

        if (Physics.Raycast(ray, out hitInfo))
        {
            // If spawnedObject is active, check it first
            if (spawnedObject != null)
            {
                if (hitInfo.transform == spawnedObject.transform || hitInfo.transform.IsChildOf(spawnedObject.transform))
                {
                    currentState = PlacementState.SelectionOptions;
                    debugMessage = "Chair selected. Choose an option.";
                    Debug.Log("[ARPlacementManager] Spawned chair hit! Opening selection menu.");
                    return;
                }
            }

            // Otherwise check all placed objects
            foreach (var obj in placedObjects)
            {
                if (obj != null && (hitInfo.transform == obj.transform || hitInfo.transform.IsChildOf(obj.transform)))
                {
                    spawnedObject = obj;
                    currentState = PlacementState.SelectionOptions;
                    debugMessage = "Placed chair selected. Choose an option.";
                    Debug.Log("[ARPlacementManager] Placed chair hit! Opening selection menu.");
                    return;
                }
            }
        }
    }

    private bool TryGetInputPosition(out Vector2 position, out bool isDrag)
    {
        position = Vector2.zero;
        isDrag = false;

        if (Application.isEditor)
        {
            if (Input.GetMouseButtonDown(0))
            {
                position = Input.mousePosition;
                return true;
            }
            else if (Input.GetMouseButton(0))
            {
                position = Input.mousePosition;
                isDrag = true;
                return true;
            }
        }
        else
        {
            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                position = touch.position;
                if (touch.phase == TouchPhase.Began)
                {
                    return true;
                }
                else if (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary)
                {
                    isDrag = true;
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsPointerOverUI(Vector2 screenPos)
    {
        float guiX = screenPos.x;
        float guiY = Screen.height - screenPos.y; // GUI 좌표계(0이 상단)로 변환

        // 1. 디버그 대시보드 Rect
        Rect debugRect = new Rect(10, 10, 750, 380);
        if (debugRect.Contains(new Vector2(guiX, guiY))) return true;

        // 1.2. Pipeline Panel Rect (좌측 중단부)
        Rect pipelinePanelRect = new Rect(20, Screen.height / 2f - 275f, 420, 550);
        if (pipelinePanelRect.Contains(new Vector2(guiX, guiY))) return true;

        // 1.5. 보관함 및 홈 버튼 영역 (우측 상단)
        if (currentState == PlacementState.Scanning || currentState == PlacementState.Placed)
        {
            Rect storageBtnRect = new Rect(Screen.width - 270, 20, 250, 110);
            if (storageBtnRect.Contains(new Vector2(guiX, guiY))) return true;

            Rect homeBtnRect = new Rect(Screen.width - 270, 145, 250, 110);
            if (homeBtnRect.Contains(new Vector2(guiX, guiY))) return true;
        }

        // 1.8. 캡처 버튼 영역 (우측 중단부)
        if (currentState == PlacementState.Placed)
        {
            Rect captureBtnRect = new Rect(Screen.width - 270, Screen.height / 2f - 60, 250, 120);
            if (captureBtnRect.Contains(new Vector2(guiX, guiY))) return true;
        }

        // 2. 프리뷰 상태 하단 버튼 영역
        if (currentState == PlacementState.Previewing)
        {
            Rect bottomArea = new Rect(Screen.width * 0.05f, Screen.height - 200, Screen.width * 0.9f, 180);
            if (bottomArea.Contains(new Vector2(guiX, guiY))) return true;
        }

        // 3. 옵션 모달 창 영역
        if (currentState == PlacementState.SelectionOptions)
        {
            Rect dialogRect = new Rect(Screen.width / 2f - 260, Screen.height / 2f - 300, 520, 580);
            if (dialogRect.Contains(new Vector2(guiX, guiY))) return true;
        }

        // 4. 회전 미세조정 패널 영역
        if (currentState == PlacementState.AdjustingRotation)
        {
            Rect adjustPanelRect = new Rect(Screen.width * 0.05f, Screen.height - 400, Screen.width * 0.9f, 380);
            if (adjustPanelRect.Contains(new Vector2(guiX, guiY))) return true;
        }

        // 5. 보관함 메뉴 영역
        if (currentState == PlacementState.StorageMenu)
        {
            Rect storagePanelRect = new Rect(Screen.width / 2f - 375, Screen.height / 2f - 425, 750, 850);
            if (storagePanelRect.Contains(new Vector2(guiX, guiY))) return true;
            return true; // Block everything in StorageMenu state
        }

        return false;
    }

    private void InitStyles()
    {
        if (stylesInitialized) return;

        bgTexture = MakeTex(2, 2, new Color(0.1f, 0.1f, 0.1f, 0.85f));
        confirmTexture = MakeTex(2, 2, new Color(0.1f, 0.65f, 0.3f, 1.0f));
        confirmActiveTexture = MakeTex(2, 2, new Color(0.08f, 0.5f, 0.25f, 1.0f));
        cancelTexture = MakeTex(2, 2, new Color(0.8f, 0.2f, 0.2f, 1.0f));
        cancelActiveTexture = MakeTex(2, 2, new Color(0.65f, 0.15f, 0.15f, 1.0f));
        neutralTexture = MakeTex(2, 2, new Color(0.35f, 0.4f, 0.45f, 1.0f));
        neutralActiveTexture = MakeTex(2, 2, new Color(0.25f, 0.3f, 0.35f, 1.0f));
        whiteTexture = MakeTex(2, 2, Color.white);

        stylesInitialized = true;
    }

    private Texture2D MakeTex(int width, int height, Color col)
    {
        Color[] pix = new Color[width * height];
        for (int i = 0; i < pix.Length; ++i)
        {
            pix[i] = col;
        }
        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        return result;
    }

    private void AdjustColliderAndPivot(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            debugMessage = "No renderers found on spawned object!";
            return;
        }

        float minY = float.MaxValue;
        float maxY = float.MinValue;
        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;

        foreach (var r in renderers)
        {
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Vector3[] vertices = mf.sharedMesh.vertices;
                foreach (var v in vertices)
                {
                    Vector3 worldV = r.transform.TransformPoint(v);
                    Vector3 localV = obj.transform.InverseTransformPoint(worldV);

                    if (localV.x < minX) minX = localV.x;
                    if (localV.x > maxX) maxX = localV.x;
                    if (localV.y < minY) minY = localV.y;
                    if (localV.y > maxY) maxY = localV.y;
                    if (localV.z < minZ) minZ = localV.z;
                    if (localV.z > maxZ) maxZ = localV.z;
                }
            }
        }

        if (minY != float.MaxValue)
        {
            float yOffset = -minY - groundingOffset;
            // Shift all immediate children by yOffset
            for (int i = 0; i < obj.transform.childCount; i++)
            {
                obj.transform.GetChild(i).localPosition += new Vector3(0, yOffset, 0);
            }

            // Update or Add BoxCollider
            BoxCollider boxCollider = obj.GetComponent<BoxCollider>();
            if (boxCollider == null)
            {
                boxCollider = obj.AddComponent<BoxCollider>();
            }

            float sizeX = maxX - minX;
            float sizeY = maxY - minY;
            float sizeZ = maxZ - minZ;

            boxCollider.center = new Vector3((minX + maxX) / 2.0f, sizeY / 2.0f, (minZ + maxZ) / 2.0f);
            boxCollider.size = new Vector3(sizeX, sizeY, sizeZ);

            string msg = $"[PivotAdjust] Shift Y: {yOffset:F3}. Collider Center: {boxCollider.center}, Size: {boxCollider.size}";
            Debug.Log(msg);
            debugMessage = msg;
        }
        else
        {
            debugMessage = "Failed to calculate vertex bounds for pivot correction.";
        }
    }

    private void EnableRotationGizmo()
    {
        if (spawnedObject == null) return;

        // 1. Temporarily disable the two-finger rotator to prevent conflicts
        ARObjectRotator rotator = spawnedObject.GetComponent<ARObjectRotator>();
        if (rotator != null)
        {
            rotator.enabled = false;
            Debug.Log("[ARPlacementManager] Disabled ARObjectRotator.");
        }

        // 2. Find the child mesh transform (where rotation is applied)
        Transform meshTransform = GetRotationTransform(spawnedObject);
        if (meshTransform == null) return;

        // 3. Create the gizmo GameObject
        GameObject gizmoGo = new GameObject("ARRotationGizmo");
        activeGizmo = gizmoGo.AddComponent<ARRotationGizmo>();

        // 4. Attach to meshTransform so it rotates with the chair
        gizmoGo.transform.SetParent(meshTransform, false);

        // 5. Position the gizmo at the visual center of the mesh in local space
        BoxCollider boxCollider = spawnedObject.GetComponent<BoxCollider>();
        if (boxCollider != null)
        {
            Vector3 worldCenter = spawnedObject.transform.TransformPoint(boxCollider.center);
            Vector3 localCenterToMesh = meshTransform.InverseTransformPoint(worldCenter);
            gizmoGo.transform.localPosition = localCenterToMesh;

            // Initialize gizmo radius based on collider bounds
            activeGizmo.Initialize(boxCollider.bounds);
        }
        else
        {
            gizmoGo.transform.localPosition = Vector3.zero;
        }

        Debug.Log("[ARPlacementManager] Spawning rotation gizmo at center of chair.");
    }

    private void DisableRotationGizmo()
    {
        // 1. Destroy active gizmo if exists
        if (activeGizmo != null)
        {
            Destroy(activeGizmo.gameObject);
            activeGizmo = null;
            Debug.Log("[ARPlacementManager] Destroyed rotation gizmo.");
        }

        // 2. Re-enable the two-finger rotator
        if (spawnedObject != null)
        {
            ARObjectRotator rotator = spawnedObject.GetComponent<ARObjectRotator>();
            if (rotator != null)
            {
                rotator.enabled = true;
                Debug.Log("[ARPlacementManager] Re-enabled ARObjectRotator.");
            }
        }
    }

    private void UpdateBoxColliderBounds(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        foreach (var r in renderers)
        {
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Vector3[] vertices = mf.sharedMesh.vertices;
                foreach (var v in vertices)
                {
                    Vector3 worldV = r.transform.TransformPoint(v);
                    Vector3 localV = obj.transform.InverseTransformPoint(worldV);

                    if (localV.x < minX) minX = localV.x;
                    if (localV.x > maxX) maxX = localV.x;
                    if (localV.y < minY) minY = localV.y;
                    if (localV.y > maxY) maxY = localV.y;
                    if (localV.z < minZ) minZ = localV.z;
                    if (localV.z > maxZ) maxZ = localV.z;
                }
            }
        }

        BoxCollider boxCollider = obj.GetComponent<BoxCollider>();
        if (boxCollider == null) boxCollider = obj.AddComponent<BoxCollider>();

        float sizeX = maxX - minX;
        float sizeY = maxY - minY;
        float sizeZ = maxZ - minZ;

        boxCollider.center = new Vector3((minX + maxX) / 2.0f, sizeY / 2.0f, (minZ + maxZ) / 2.0f);
        boxCollider.size = new Vector3(sizeX, sizeY, sizeZ);
    }

    private void SaveLayoutToSlot(int index)
    {
        LayoutSaveData layout = new LayoutSaveData();
        foreach (var obj in placedObjects)
        {
            if (obj != null)
            {
                FurnitureSaveData data = new FurnitureSaveData();
                
                var gsRenderer = obj.GetComponent<GaussianSplatRenderer>();
                if (gsRenderer != null)
                {
                    data.prefabName = "스캔 가구";
                    string splatPath = "";
                    if (splatBundlePaths != null && splatBundlePaths.TryGetValue(obj, out splatPath))
                    {
                        data.assetBundlePath = splatPath;
                    }
                }
                else
                {
                    string internalPath = "";
                    if (splatBundlePaths != null && splatBundlePaths.TryGetValue(obj, out internalPath) && internalPath == "INTERNAL_WOODEN_CHAIR")
                    {
                        data.prefabName = "나무 의자";
                        data.assetBundlePath = "INTERNAL_WOODEN_CHAIR";
                    }
                    else
                    {
                        data.prefabName = furniturePrefab.name;
                    }
                }

                data.position = obj.transform.position;
                data.rotation = obj.transform.rotation;

                Transform meshTrans = GetRotationTransform(obj);
                if (meshTrans != obj.transform)
                {
                    data.meshLocalPos = meshTrans.localPosition;
                    data.meshLocalRot = meshTrans.localRotation;
                }
                else
                {
                    data.meshLocalPos = Vector3.zero;
                    data.meshLocalRot = Quaternion.identity;
                }

                layout.items.Add(data);
            }
        }

        string json = JsonUtility.ToJson(layout);
        PlayerPrefs.SetString("LayoutSlot_" + index, json);
        PlayerPrefs.Save();
        debugMessage = $"Layout saved to Slot {index + 1} ({layout.items.Count} items).";
    }

    private void LoadLayoutFromSlot(int index)
    {
        string key = "LayoutSlot_" + index;
        if (!PlayerPrefs.HasKey(key))
        {
            debugMessage = $"No layout in Slot {index + 1}.";
            return;
        }

        ClearAllLayout();

        string json = PlayerPrefs.GetString(key);
        LayoutSaveData layout = JsonUtility.FromJson<LayoutSaveData>(json);
        if (layout == null || layout.items.Count == 0)
        {
            debugMessage = "Loaded layout is empty.";
            return;
        }

        foreach (var item in layout.items)
        {
            GameObject loaded = null;
            if (item.assetBundlePath == "INTERNAL_WOODEN_CHAIR")
            {
                if (woodenChairPrefab != null)
                {
                    loaded = Instantiate(woodenChairPrefab, item.position, item.rotation);
                    if (loaded != null)
                    {
                        RegisterSplatPath(loaded, "INTERNAL_WOODEN_CHAIR");
                        Transform meshTrans = GetRotationTransform(loaded);
                        if (meshTrans != loaded.transform)
                        {
                            if (item.meshLocalPos != Vector3.zero || item.meshLocalRot != Quaternion.identity)
                            {
                                meshTrans.localPosition = item.meshLocalPos;
                                meshTrans.localRotation = item.meshLocalRot;
                            }
                        }
                        UpdateBoxColliderBounds(loaded);
                    }
                }
            }
            else if (item.assetBundlePath == "INTERNAL_DEFAULT_SPLAT")
            {
                loaded = SpawnSplatFromDefaultAsset(item.position, item.rotation, item.meshLocalPos, item.meshLocalRot);
            }
            else if (!string.IsNullOrEmpty(item.assetBundlePath))
            {
                loaded = SpawnSplatFromBundle(item.assetBundlePath, item.position, item.rotation, item.meshLocalPos, item.meshLocalRot);
            }
            else
            {
                loaded = Instantiate(furniturePrefab, item.position, item.rotation);
                if (loaded != null)
                {
                    Transform meshTrans = GetRotationTransform(loaded);
                    if (meshTrans != loaded.transform)
                    {
                        if (item.meshLocalPos != Vector3.zero || item.meshLocalRot != Quaternion.identity)
                        {
                            meshTrans.localPosition = item.meshLocalPos;
                            meshTrans.localRotation = item.meshLocalRot;
                        }
                    }
                    UpdateBoxColliderBounds(loaded);
                }
            }

            if (loaded != null)
            {
                placedObjects.Add(loaded);
            }
        }

        SetCurrentState(PlacementState.Placed);
        debugMessage = $"Loaded Layout Slot {index + 1} ({placedObjects.Count} items).";
    }

    private void SpawnFurnitureFromData(FurnitureSaveData item)
    {
        Vector3 spawnPos = Camera.main.transform.position + Camera.main.transform.forward * 1.5f;
        if (lowestPlaneY != float.MaxValue)
        {
            spawnPos.y = lowestPlaneY;
        }

        GameObject loaded = null;
        if (item.assetBundlePath == "INTERNAL_WOODEN_CHAIR")
        {
            if (woodenChairPrefab != null)
            {
                loaded = Instantiate(woodenChairPrefab, spawnPos, item.rotation);
                if (loaded != null)
                {
                    RegisterSplatPath(loaded, "INTERNAL_WOODEN_CHAIR");
                    Transform meshTrans = GetRotationTransform(loaded);
                    if (meshTrans != loaded.transform)
                    {
                        if (item.meshLocalPos != Vector3.zero || item.meshLocalRot != Quaternion.identity)
                        {
                            meshTrans.localPosition = item.meshLocalPos;
                            meshTrans.localRotation = item.meshLocalRot;
                        }
                    }
                    UpdateBoxColliderBounds(loaded);
                }
            }
            else
            {
                Debug.LogError("[ARPlacementManager] woodenChairPrefab is not assigned!");
            }
        }
        else if (item.assetBundlePath == "INTERNAL_DEFAULT_SPLAT")
        {
            loaded = SpawnSplatFromDefaultAsset(spawnPos, item.rotation, item.meshLocalPos, item.meshLocalRot);
        }
        else if (!string.IsNullOrEmpty(item.assetBundlePath))
        {
            loaded = SpawnSplatFromBundle(item.assetBundlePath, spawnPos, item.rotation, item.meshLocalPos, item.meshLocalRot);
        }
        else
        {
            loaded = Instantiate(furniturePrefab, spawnPos, item.rotation);
            if (loaded != null)
            {
                Transform meshTrans = GetRotationTransform(loaded);
                if (meshTrans != loaded.transform)
                {
                    if (item.meshLocalPos != Vector3.zero || item.meshLocalRot != Quaternion.identity)
                    {
                        meshTrans.localPosition = item.meshLocalPos;
                        meshTrans.localRotation = item.meshLocalRot;
                    }
                }
                UpdateBoxColliderBounds(loaded);
            }
        }

        if (loaded != null)
        {
            if (loaded.GetComponent<ARObjectRotator>() == null)
            {
                loaded.AddComponent<ARObjectRotator>();
            }
            spawnedObject = loaded;
            SetCurrentState(PlacementState.Previewing);
            debugMessage = "Spawned preview from slot. Drag screen to position, or confirm/cancel.";
        }
    }

    private void DrawFurnitureSlot(int index, GUIStyle neutralStyle, GUIStyle confirmStyle, GUIStyle cancelStyle)
    {
        string key = "FurnitureSlot_" + index;
        bool exists = PlayerPrefs.HasKey(key);

        GUIStyle rowStyle = new GUIStyle(GUI.skin.box);
        rowStyle.normal.background = bgTexture;

        GUILayout.BeginHorizontal(rowStyle, GUILayout.Height(90));
        
        GUIStyle textStyle = new GUIStyle();
        textStyle.fontSize = 28;
        textStyle.normal.textColor = Color.white;
        textStyle.alignment = TextAnchor.MiddleLeft;

        if (exists)
        {
            string json = PlayerPrefs.GetString(key);
            FurnitureSaveData item = JsonUtility.FromJson<FurnitureSaveData>(json);
            GUILayout.Label($" #{index + 1}: {item.prefabName}", textStyle, GUILayout.Width(250), GUILayout.Height(70));

            if (GUILayout.Button("추가 (Add)", confirmStyle, GUILayout.Width(180), GUILayout.Height(70)))
            {
                SpawnFurnitureFromData(item);
            }
            GUILayout.Space(5);
            if (GUILayout.Button("삭제 (Del)", cancelStyle, GUILayout.Width(180), GUILayout.Height(70)))
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
                debugMessage = $"Cleared Furniture Slot {index + 1}.";
            }
        }
        else
        {
            GUILayout.Label($" #{index + 1}: 비어 있음", textStyle, GUILayout.Width(250), GUILayout.Height(70));

            GameObject objToSave = spawnedObject;
            if (objToSave == null && placedObjects.Count > 0)
            {
                objToSave = placedObjects[placedObjects.Count - 1];
            }

            if (objToSave != null)
            {
                if (GUILayout.Button("등록 (Save Selected)", confirmStyle, GUILayout.Width(365), GUILayout.Height(70)))
                {
                    FurnitureSaveData data = new FurnitureSaveData();
                    
                    var gsRenderer = objToSave.GetComponent<GaussianSplatRenderer>();
                    if (gsRenderer != null)
                    {
                        data.prefabName = "스캔 가구";
                        string splatPath = "";
                        if (splatBundlePaths != null && splatBundlePaths.TryGetValue(objToSave, out splatPath))
                        {
                            data.assetBundlePath = splatPath;
                        }
                    }
                    else
                    {
                        string internalPath = "";
                        if (splatBundlePaths != null && splatBundlePaths.TryGetValue(objToSave, out internalPath) && internalPath == "INTERNAL_WOODEN_CHAIR")
                        {
                            data.prefabName = "나무 의자";
                            data.assetBundlePath = "INTERNAL_WOODEN_CHAIR";
                        }
                        else
                        {
                            data.prefabName = furniturePrefab.name;
                        }
                    }

                    data.position = objToSave.transform.position;
                    data.rotation = objToSave.transform.rotation;

                    Transform meshTrans = GetRotationTransform(objToSave);
                    if (meshTrans != objToSave.transform)
                    {
                        data.meshLocalPos = meshTrans.localPosition;
                        data.meshLocalRot = meshTrans.localRotation;
                    }
                    else
                    {
                        data.meshLocalPos = Vector3.zero;
                        data.meshLocalRot = Quaternion.identity;
                    }

                    string json = JsonUtility.ToJson(data);
                    PlayerPrefs.SetString(key, json);
                    PlayerPrefs.Save();
                    debugMessage = $"Saved active furniture to Slot {index + 1}.";
                }
            }
            else
            {
                GUI.enabled = false;
                GUILayout.Button("저장할 가구 없음", neutralStyle, GUILayout.Width(365), GUILayout.Height(70));
                GUI.enabled = true;
            }
        }

        GUILayout.EndHorizontal();
    }

    private void DrawLayoutSlot(int index, GUIStyle neutralStyle, GUIStyle confirmStyle, GUIStyle cancelStyle)
    {
        string key = "LayoutSlot_" + index;
        bool exists = PlayerPrefs.HasKey(key);

        GUIStyle rowStyle = new GUIStyle(GUI.skin.box);
        rowStyle.normal.background = bgTexture;

        GUILayout.BeginHorizontal(rowStyle, GUILayout.Height(90));

        GUIStyle textStyle = new GUIStyle();
        textStyle.fontSize = 28;
        textStyle.normal.textColor = Color.white;
        textStyle.alignment = TextAnchor.MiddleLeft;

        if (exists)
        {
            string json = PlayerPrefs.GetString(key);
            LayoutSaveData layout = JsonUtility.FromJson<LayoutSaveData>(json);
            GUILayout.Label($" #{index + 1}: 가구 {layout.items.Count}개", textStyle, GUILayout.Width(250), GUILayout.Height(70));

            if (GUILayout.Button("불러오기 (Load)", neutralStyle, GUILayout.Width(130), GUILayout.Height(70)))
            {
                LoadLayoutFromSlot(index);
            }
            GUILayout.Space(5);
            if (GUILayout.Button("덮어쓰기 (Over)", confirmStyle, GUILayout.Width(115), GUILayout.Height(70)))
            {
                SaveLayoutToSlot(index);
            }
            GUILayout.Space(5);
            if (GUILayout.Button("삭제 (Del)", cancelStyle, GUILayout.Width(110), GUILayout.Height(70)))
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
                debugMessage = $"Cleared Layout Slot {index + 1}.";
            }
        }
        else
        {
            GUILayout.Label($" #{index + 1}: 비어 있음", textStyle, GUILayout.Width(250), GUILayout.Height(70));

            if (placedObjects.Count > 0 || spawnedObject != null)
            {
                if (GUILayout.Button("현재 배치 저장", confirmStyle, GUILayout.Width(365), GUILayout.Height(70)))
                {
                    SaveLayoutToSlot(index);
                }
            }
            else
            {
                GUI.enabled = false;
                GUILayout.Button("저장할 배치 없음", neutralStyle, GUILayout.Width(365), GUILayout.Height(70));
                GUI.enabled = true;
            }
        }

        GUILayout.EndHorizontal();
    }

    private void ClearAllLayout()
    {
        foreach (var obj in placedObjects)
        {
            if (obj != null) Destroy(obj);
        }
        placedObjects.Clear();
        if (spawnedObject != null)
        {
            Destroy(spawnedObject);
            spawnedObject = null;
        }
        SetCurrentState(PlacementState.Scanning);
        debugMessage = "All placed objects cleared.";
        Debug.Log("[ARPlacementManager] Cleared all placed objects.");
    }

    // Public API Helpers for ARPipelineManager
    public void AddPlacedObject(GameObject obj)
    {
        if (placedObjects == null) placedObjects = new List<GameObject>();
        placedObjects.Add(obj);
    }

    public void RunAdjustColliderAndPivot(GameObject obj)
    {
        AdjustColliderAndPivot(obj);
    }

    public void RunUpdateBoxColliderBounds(GameObject obj)
    {
        UpdateBoxColliderBounds(obj);
    }

    public float GetLowestPlaneY()
    {
        return lowestPlaneY;
    }

    public void SetCurrentState(PlacementState state)
    {
        if (Event.current != null)
        {
            pendingState = state;
        }
        else
        {
            currentState = state;
        }
    }

    public PlacementState GetCurrentState()
    {
        return currentState;
    }

    void OnGUI()
    {
        InitStyles();
        PlacementState stateAtStart = currentState;

        // Styles
        GUIStyle debugStyle = new GUIStyle();
        debugStyle.fontSize = 28;
        debugStyle.normal.textColor = Color.yellow;

        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.fontSize = 36;
        buttonStyle.fontStyle = FontStyle.Bold;
        buttonStyle.normal.textColor = Color.white;
        buttonStyle.hover.textColor = Color.white;
        buttonStyle.active.textColor = Color.lightGray;
        buttonStyle.alignment = TextAnchor.MiddleCenter;

        GUIStyle confirmStyle = new GUIStyle(buttonStyle);
        confirmStyle.normal.background = confirmTexture;
        confirmStyle.hover.background = confirmTexture;
        confirmStyle.active.background = confirmActiveTexture;

        GUIStyle cancelStyle = new GUIStyle(buttonStyle);
        cancelStyle.normal.background = cancelTexture;
        cancelStyle.hover.background = cancelTexture;
        cancelStyle.active.background = cancelActiveTexture;

        GUIStyle neutralStyle = new GUIStyle(buttonStyle);
        neutralStyle.normal.background = neutralTexture;
        neutralStyle.hover.background = neutralTexture;
        neutralStyle.active.background = neutralActiveTexture;

        GUIStyle panelStyle = new GUIStyle(GUI.skin.box);
        panelStyle.normal.background = bgTexture;

        GUIStyle headerStyle = new GUIStyle();
        headerStyle.fontSize = 38;
        headerStyle.fontStyle = FontStyle.Bold;
        headerStyle.normal.textColor = Color.white;
        headerStyle.alignment = TextAnchor.MiddleCenter;

        GUIStyle statusStyle = new GUIStyle();
        statusStyle.fontSize = 32;
        statusStyle.normal.textColor = Color.white;
        statusStyle.alignment = TextAnchor.MiddleCenter;

        // NEW: Main Menu UI Block
        if (stateAtStart == PlacementState.MainMenu)
        {
            float panelW = 750f;
            float panelH = 600f;
            float panelX = (Screen.width - panelW) / 2f;
            float panelY = (Screen.height - panelH) / 2f;

            GUI.Box(new Rect(panelX, panelY, panelW, panelH), "", panelStyle);

            GUILayout.BeginArea(new Rect(panelX + 30, panelY + 40, panelW - 60, panelH - 80));

            GUILayout.Label("AR 가구 배치 시스템", headerStyle);
            GUILayout.Space(20);
            
            GUIStyle descStyle = new GUIStyle(statusStyle);
            descStyle.fontSize = 28;
            descStyle.normal.textColor = Color.lightGray;
            GUILayout.Label("원하는 모드를 선택해 주세요.", descStyle);
            GUILayout.Space(40);

            // Button 1: 가구 스캔하기 (Scan mode)
            if (GUILayout.Button("가구 스캔하기 (Scan)", confirmStyle, GUILayout.Height(130)))
            {
                isPipelineModeActive = true;
                SetCurrentState(PlacementState.Scanning);
                debugMessage = "Entered Scan Mode. Use the 3DGS Pipeline to scan.";
                Debug.Log("[ARPlacementManager] Entered Scan Mode with Pipeline active.");
            }

            GUILayout.Space(25);

            // Button 2: 가구 배치하기 (Placement mode)
            if (GUILayout.Button("가구 배치하기 (Place)", neutralStyle, GUILayout.Height(130)))
            {
                isPipelineModeActive = false;
                SetCurrentState(PlacementState.Scanning);
                debugMessage = "Entered Placement Mode. Open Storage to load furniture.";
                Debug.Log("[ARPlacementManager] Entered Placement Mode (Pipeline inactive).");
            }

            GUILayout.EndArea();
            return; // Exit OnGUI early
        }

        // A. Draw Debug Dashboard
        int activePlanesCount = 0;
        if (planeManager != null)
        {
            foreach (var plane in planeManager.trackables)
            {
                if (IsPlaneValid(plane))
                {
                    activePlanesCount++;
                }
            }
        }

        GUI.Box(new Rect(10, 10, 750, 380), "AR Placement Debug Dashboard", panelStyle);

        GUILayout.BeginArea(new Rect(25, 40, 720, 340));
        GUILayout.Label($"State: {currentState}", debugStyle);
        GUILayout.Label($"Touch Count: {Input.touchCount}", debugStyle);
        GUILayout.Label($"Valid Planes Count: {activePlanesCount}", debugStyle);
        GUILayout.Label($"Last Input Screen Pos: {lastInputPos}", debugStyle);
        GUILayout.Label($"Last Raycast Hit: {lastRaycastSuccess} at {lastHitPos}", debugStyle);
        GUILayout.Label($"Spawned Object: {(spawnedObject != null ? spawnedObject.name : "None")}", debugStyle);
        GUILayout.Label($"Placed Objects Count: {placedObjects.Count}", debugStyle);
        GUILayout.Label($"Status/Log: {debugMessage}", debugStyle);
        GUILayout.EndArea();

        // B. Draw Preview Options at the bottom
        if (stateAtStart == PlacementState.Previewing)
        {
            float btnW = Screen.width * 0.42f;
            float btnH = 120f;
            float btnY = Screen.height - 160f;
            float leftX = Screen.width * 0.06f;
            float rightX = Screen.width * 0.52f;

            if (GUI.Button(new Rect(leftX, btnY, btnW, btnH), "설치 확정 (Confirm)", confirmStyle))
            {
                if (spawnedObject != null)
                {
                    placedObjects.Add(spawnedObject);
                    spawnedObject = null;
                }
                SetCurrentState(PlacementState.Placed);
                debugMessage = "Placement confirmed. Tap a chair to edit.";
                Debug.Log("[ARPlacementManager] Placement confirmed!");
            }

            if (GUI.Button(new Rect(rightX, btnY, btnW, btnH), "위치 취소 (Cancel)", cancelStyle))
            {
                if (spawnedObject != null)
                {
                    Destroy(spawnedObject);
                    spawnedObject = null;
                }
                SetCurrentState((placedObjects.Count > 0) ? PlacementState.Placed : PlacementState.Scanning);
                debugMessage = "Placement cancelled.";
                Debug.Log("[ARPlacementManager] Placement cancelled!");
            }
        }

        // C. Draw Selection Options Modal in Center
        if (stateAtStart == PlacementState.SelectionOptions)
        {
            float panelW = 520f;
            float panelH = 560f;
            float panelX = (Screen.width - panelW) / 2f;
            float panelY = (Screen.height - panelH) / 2f;

            GUI.Box(new Rect(panelX, panelY, panelW, panelH), "", panelStyle);

            GUILayout.BeginArea(new Rect(panelX + 20, panelY + 20, panelW - 40, panelH - 40));

            GUILayout.Label("의자 설정", headerStyle);
            GUILayout.Space(15);
            GUILayout.Label("원하시는 작업을 선택해 주세요.", statusStyle);
            GUILayout.Space(20);

            if (GUILayout.Button("의자 이동 (Move)", neutralStyle, GUILayout.Height(75)))
            {
                if (spawnedObject != null)
                {
                    placedObjects.Remove(spawnedObject);
                }
                SetCurrentState(PlacementState.Previewing);
                debugMessage = "Moving chair. Drag screen to position, then confirm/cancel.";
                Debug.Log("[ARPlacementManager] Entered repositioning (Move) state.");
            }

            GUILayout.Space(12);

            if (GUILayout.Button("회전 미세조정 (Rotate)", neutralStyle, GUILayout.Height(75)))
            {
                SetCurrentState(PlacementState.AdjustingRotation);
                EnableRotationGizmo();
                debugMessage = "Adjusting rotation. Hold buttons to rotate slowly.";
                Debug.Log("[ARPlacementManager] Entered rotation adjustment state.");
            }

            GUILayout.Space(12);

            if (GUILayout.Button("의자 삭제 (Remove)", cancelStyle, GUILayout.Height(75)))
            {
                if (spawnedObject != null)
                {
                    placedObjects.Remove(spawnedObject);
                    Destroy(spawnedObject);
                    spawnedObject = null;
                }
                SetCurrentState((placedObjects.Count > 0) ? PlacementState.Placed : PlacementState.Scanning);
                debugMessage = "Chair removed.";
                Debug.Log("[ARPlacementManager] Chair removed!");
            }

            GUILayout.Space(12);

            if (GUILayout.Button("닫기 (Close)", neutralStyle, GUILayout.Height(75)))
            {
                spawnedObject = null;
                SetCurrentState(PlacementState.Placed);
                debugMessage = "Menu closed.";
                Debug.Log("[ARPlacementManager] Options menu closed.");
            }

            GUILayout.EndArea();
        }

        // D. Draw Rotation Adjustment Panel
        if (stateAtStart == PlacementState.AdjustingRotation)
        {
            float panelW = Screen.width * 0.9f;
            float panelH = 370f;
            float panelX = Screen.width * 0.05f;
            float panelY = Screen.height - 390f;

            GUI.Box(new Rect(panelX, panelY, panelW, panelH), "", panelStyle);

            GUILayout.BeginArea(new Rect(panelX + 20, panelY + 15, panelW - 40, panelH - 30));

            GUILayout.Label("회전 미세조정 (Hold to Rotate)", headerStyle);
            GUILayout.Space(10);

            float btnW = (panelW - 60f) / 2f;
            float btnH = 65f;

            GUILayout.BeginHorizontal();
            isPitchingPlus = GUILayout.RepeatButton("앞으로 기울기 (Pitch +)", neutralStyle, GUILayout.Width(btnW), GUILayout.Height(btnH));
            isPitchingMinus = GUILayout.RepeatButton("뒤로 기울기 (Pitch -)", neutralStyle, GUILayout.Width(btnW), GUILayout.Height(btnH));
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            isYawPlus = GUILayout.RepeatButton("좌측 회전 (Yaw +)", neutralStyle, GUILayout.Width(btnW), GUILayout.Height(btnH));
            isYawMinus = GUILayout.RepeatButton("우측 회전 (Yaw -)", neutralStyle, GUILayout.Width(btnW), GUILayout.Height(btnH));
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            isRollPlus = GUILayout.RepeatButton("좌측 기울기 (Roll +)", neutralStyle, GUILayout.Width(btnW), GUILayout.Height(btnH));
            isRollMinus = GUILayout.RepeatButton("우측 기울기 (Roll -)", neutralStyle, GUILayout.Width(btnW), GUILayout.Height(btnH));
            GUILayout.EndHorizontal();

            GUILayout.Space(12);

            if (GUILayout.Button("조정 완료 (Done)", confirmStyle, GUILayout.Height(70)))
            {
                DisableRotationGizmo();

                if (spawnedObject != null)
                {
                    AdjustColliderAndPivot(spawnedObject);
                }
                
                isPitchingPlus = false;
                isPitchingMinus = false;
                isYawPlus = false;
                isYawMinus = false;
                isRollPlus = false;
                isRollMinus = false;

                SetCurrentState(PlacementState.SelectionOptions);
                debugMessage = "Rotation adjusted and saved.";
                Debug.Log("[ARPlacementManager] Rotation adjustment complete. Collider re-aligned.");
            }

            GUILayout.EndArea();
        }

        // Draw Main Menu and Storage buttons in top right if not in editing states
        if (stateAtStart == PlacementState.Scanning || stateAtStart == PlacementState.Placed)
        {
            // Storage Button
            Rect storageBtnRect = new Rect(Screen.width - 270, 20, 250, 110);
            if (GUI.Button(storageBtnRect, "보관함 (Storage)", neutralStyle))
            {
                if (storageBtnRect.Contains(Event.current.mousePosition))
                {
                    SetCurrentState(PlacementState.StorageMenu);
                    debugMessage = "Storage menu opened.";
                    Debug.Log("[ARPlacementManager] Storage menu opened.");
                }
            }

            // Home Button (Return to Main Menu)
            Rect homeBtnRect = new Rect(Screen.width - 270, 145, 250, 110);
            if (GUI.Button(homeBtnRect, "메인 메뉴 (Home)", neutralStyle))
            {
                if (homeBtnRect.Contains(Event.current.mousePosition))
                {
                    SetCurrentState(PlacementState.MainMenu);
                    debugMessage = "Returned to Main Menu.";
                    Debug.Log("[ARPlacementManager] Returned to Main Menu.");

                    // Reset pipeline state if active
                    var pipelineManager = GetComponent<ARPipelineManager>();
                    if (pipelineManager != null)
                    {
                        pipelineManager.CancelScan();
                    }
                }
            }
        }

        // E. Draw Storage Menu Modal in Center (Tabbed and Scrollable)
        if (stateAtStart == PlacementState.StorageMenu)
        {
            float panelW = 750f;
            float panelH = 850f;
            float panelX = (Screen.width - panelW) / 2f;
            float panelY = (Screen.height - panelH) / 2f;

            GUI.Box(new Rect(panelX, panelY, panelW, panelH), "", panelStyle);

            GUILayout.BeginArea(new Rect(panelX + 20, panelY + 20, panelW - 40, panelH - 40));

            GUILayout.Label("보관함 시스템", headerStyle);
            GUILayout.Space(10);

            // Tab selection
            GUILayout.BeginHorizontal();
            GUIStyle activeTabStyle = new GUIStyle(confirmStyle);
            activeTabStyle.fontSize = 32;
            GUIStyle inactiveTabStyle = new GUIStyle(neutralStyle);
            inactiveTabStyle.fontSize = 32;

            if (GUILayout.Button("가구 보관함 (Furniture)", activeStorageTab == 0 ? activeTabStyle : inactiveTabStyle, GUILayout.Height(70)))
            {
                activeStorageTab = 0;
            }
            if (GUILayout.Button("배치 보관함 (Layout)", activeStorageTab == 1 ? activeTabStyle : inactiveTabStyle, GUILayout.Height(70)))
            {
                activeStorageTab = 1;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(15);

            if (activeStorageTab == 0)
            {
                // Scroll view for 10 furniture slots
                furnitureScrollPos = GUILayout.BeginScrollView(furnitureScrollPos, GUILayout.Height(520));
                for (int i = 0; i < 10; i++)
                {
                    DrawFurnitureSlot(i, neutralStyle, confirmStyle, cancelStyle);
                    GUILayout.Space(10);
                }
                GUILayout.EndScrollView();
            }
            else
            {
                // Scroll view for 10 layout slots
                layoutScrollPos = GUILayout.BeginScrollView(layoutScrollPos, GUILayout.Height(520));
                for (int i = 0; i < 10; i++)
                {
                    DrawLayoutSlot(i, neutralStyle, confirmStyle, cancelStyle);
                    GUILayout.Space(10);
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(15);

            if (GUILayout.Button("닫기 (Close)", neutralStyle, GUILayout.Height(70)))
            {
                SetCurrentState((placedObjects.Count > 0) ? PlacementState.Placed : PlacementState.Scanning);
                debugMessage = "Storage menu closed.";
                Debug.Log("[ARPlacementManager] Storage menu closed.");
            }

            GUILayout.EndArea();
        }

        // F. Draw Capture Button in the middle-right of the screen in Placed state
        if (stateAtStart == PlacementState.Placed)
        {
            Rect captureBtnRect = new Rect(Screen.width - 270, Screen.height / 2f - 60, 250, 120);
            if (GUI.Button(captureBtnRect, "사진 캡처\n(Capture)", confirmStyle))
            {
                string filename = "AR_Layout_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                ScreenCapture.CaptureScreenshot(filename);
                debugMessage = $"Screenshot saved as {filename}";
                flashTimer = flashDuration; // Trigger flash effect
                Debug.Log($"[ARPlacementManager] Captured screenshot: {filename}");
            }
        }

        // G. Draw screen flash overlay
        if (flashTimer > 0f && whiteTexture != null)
        {
            Color originalColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, flashTimer / flashDuration);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), whiteTexture);
            GUI.color = originalColor;
        }
    }

    public GameObject SpawnSplatFromBundle(string localPath, Vector3 position, Quaternion rotation, Vector3 meshLocalPos, Quaternion meshLocalRot)
    {
        if (!System.IO.File.Exists(localPath))
        {
            Debug.LogError($"[ARPlacementManager] Local AssetBundle file not found: {localPath}");
            return null;
        }

        AssetBundle bundle = AssetBundle.LoadFromFile(localPath);
        if (bundle == null)
        {
            Debug.LogError($"[ARPlacementManager] Failed to load AssetBundle from file: {localPath}");
            return null;
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
            Debug.LogError("[ARPlacementManager] No GaussianSplatAsset found in AssetBundle.");
            bundle.Unload(true);
            return null;
        }

        var pipelineManager = GetComponent<ARPipelineManager>();
        GameObject splatPrefab = pipelineManager != null ? pipelineManager.splatRendererPrefab : null;

        if (splatPrefab == null)
        {
            splatPrefab = furniturePrefab;
        }

        if (splatPrefab == null)
        {
            Debug.LogError("[ARPlacementManager] Both splatRendererPrefab and furniturePrefab are null!");
            bundle.Unload(true);
            return null;
        }

        GameObject splatGo = Instantiate(splatPrefab, position, rotation);
        if (splatGo != null)
        {
            RegisterSplatPath(splatGo, localPath);

            Transform meshTrans = GetRotationTransform(splatGo);
            if (meshTrans != splatGo.transform)
            {
                if (meshLocalPos != Vector3.zero || meshLocalRot != Quaternion.identity)
                {
                    meshTrans.localPosition = meshLocalPos;
                    meshTrans.localRotation = meshLocalRot;
                }
                // Disable placeholder renderer
                var mr = meshTrans.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = false;
                foreach (var childMr in meshTrans.GetComponentsInChildren<MeshRenderer>())
                {
                    childMr.enabled = false;
                }
            }

            GaussianSplatRenderer renderer = splatGo.GetComponent<GaussianSplatRenderer>();
            if (renderer == null)
            {
                renderer = splatGo.AddComponent<GaussianSplatRenderer>();
            }

            if (renderer != null)
            {
                var assetField = renderer.GetType().GetField("m_Asset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (assetField != null)
                {
                    assetField.SetValue(renderer, splatAsset);
                    Debug.Log("[ARPlacementManager] Assigned GaussianSplatAsset via reflection during load.");
                }
                else
                {
                    Debug.LogError("[ARPlacementManager] Failed to find m_Asset field on GaussianSplatRenderer!");
                }
            }

            AdjustColliderAndPivot(splatGo);
        }

        bundle.Unload(false);
        return splatGo;
    }

    public bool AutoSaveSplatToStorage(string localPath, string assetName)
    {
        int targetSlot = -1;
        for (int i = 0; i < 10; i++)
        {
            if (!PlayerPrefs.HasKey("FurnitureSlot_" + i))
            {
                targetSlot = i;
                break;
            }
        }

        if (targetSlot == -1)
        {
            targetSlot = 0;
            Debug.LogWarning("[ARPlacementManager] All furniture slots are full! Overwriting Slot 1.");
        }

        FurnitureSaveData data = new FurnitureSaveData();
        data.prefabName = assetName;
        
        Vector3 spawnPos = Camera.main.transform.position + Camera.main.transform.forward * 1.5f;
        if (lowestPlaneY != float.MaxValue)
        {
            spawnPos.y = lowestPlaneY;
        }
        data.position = spawnPos;
        data.rotation = Quaternion.identity;
        data.meshLocalPos = Vector3.zero;
        data.meshLocalRot = Quaternion.identity;
        data.assetBundlePath = localPath;

        string json = JsonUtility.ToJson(data);
        PlayerPrefs.SetString("FurnitureSlot_" + targetSlot, json);
        PlayerPrefs.Save();

        debugMessage = $"Auto-saved newly scanned model to Furniture Slot {targetSlot + 1}.";
        Debug.Log($"[ARPlacementManager] Auto-saved splat to slot {targetSlot + 1}: {localPath}");
        return true;
    }

    public GameObject SpawnSplatFromDefaultAsset(Vector3 position, Quaternion rotation, Vector3 meshLocalPos, Quaternion meshLocalRot)
    {
        if (defaultSplatAsset == null)
        {
            Debug.LogError("[ARPlacementManager] defaultSplatAsset is not assigned!");
            return null;
        }

        var pipelineManager = GetComponent<ARPipelineManager>();
        GameObject splatPrefab = pipelineManager != null ? pipelineManager.splatRendererPrefab : null;

        if (splatPrefab == null)
        {
            splatPrefab = furniturePrefab;
        }

        if (splatPrefab == null)
        {
            Debug.LogError("[ARPlacementManager] Both splatRendererPrefab and furniturePrefab are null!");
            return null;
        }

        GameObject splatGo = Instantiate(splatPrefab, position, rotation);
        if (splatGo != null)
        {
            RegisterSplatPath(splatGo, "INTERNAL_DEFAULT_SPLAT");

            Transform meshTrans = GetRotationTransform(splatGo);
            if (meshTrans != splatGo.transform)
            {
                if (meshLocalPos != Vector3.zero || meshLocalRot != Quaternion.identity)
                {
                    meshTrans.localPosition = meshLocalPos;
                    meshTrans.localRotation = meshLocalRot;
                }
                // Disable placeholder renderer
                var mr = meshTrans.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = false;
                foreach (var childMr in meshTrans.GetComponentsInChildren<MeshRenderer>())
                {
                    childMr.enabled = false;
                }
            }

            GaussianSplatRenderer renderer = splatGo.GetComponent<GaussianSplatRenderer>();
            if (renderer == null)
            {
                renderer = splatGo.AddComponent<GaussianSplatRenderer>();
            }

            if (renderer != null)
            {
                var assetField = renderer.GetType().GetField("m_Asset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (assetField != null)
                {
                    assetField.SetValue(renderer, defaultSplatAsset);
                    Debug.Log("[ARPlacementManager] Assigned defaultSplatAsset via reflection.");
                }
                else
                {
                    Debug.LogError("[ARPlacementManager] Failed to find m_Asset field on GaussianSplatRenderer!");
                }
            }

            AdjustColliderAndPivot(splatGo);
        }

        return splatGo;
    }
}

[System.Serializable]
public class FurnitureSaveData
{
    public string prefabName;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 meshLocalPos;
    public Quaternion meshLocalRot;
    public string assetBundlePath;
}

[System.Serializable]
public class LayoutSaveData
{
    public System.Collections.Generic.List<FurnitureSaveData> items = new System.Collections.Generic.List<FurnitureSaveData>();
}