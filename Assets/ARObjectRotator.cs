using UnityEngine;

public class ARObjectRotator : MonoBehaviour
{
    private float rotationSpeed = 0.5f; // 회전 속도
    private ARPlacementManager placementManager;

    void Start()
    {
        placementManager = FindAnyObjectByType<ARPlacementManager>();
    }

    void Update()
    {
        if (placementManager != null)
        {
            if (placementManager.GetCurrentState() != ARPlacementManager.PlacementState.Previewing || 
                placementManager.SpawnedObject != gameObject)
            {
                return;
            }
        }

        // 두 손가락이 터치되었을 때 회전 작동
        if (Input.touchCount == 2)
        {
            Touch touch0 = Input.GetTouch(0);
            Touch touch1 = Input.GetTouch(1);

            // ==========================================
            // 1. 회전 (Rotation)
            // ==========================================
            if (touch0.phase == TouchPhase.Moved || touch1.phase == TouchPhase.Moved)
            {
                // 두 손가락의 X축 움직임 평균을 구해 Y축 기준으로 회전시킵니다.
                float averageDeltaX = (touch0.deltaPosition.x + touch1.deltaPosition.x) / 2.0f;
                transform.Rotate(0f, -averageDeltaX * rotationSpeed, 0f, Space.World);
            }
        }
    }
}