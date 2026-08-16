using UnityEngine;

public class InspectionPoint : MonoBehaviour
{
    [Header("Inspection")]
    public string inspectionName = "Inspection Point";

    [Tooltip("이 위치에서 머무를 시간")]
    public float inspectionTime = 3f;
}