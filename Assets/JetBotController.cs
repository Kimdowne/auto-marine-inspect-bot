using UnityEngine;
using UnityEngine.InputSystem; // 신형 입력 시스템 사용을 위한 네임스페이스

[RequireComponent(typeof(Rigidbody))]
public class TrackedRobotController : MonoBehaviour
{
    [Header("Wheel Colliders")]
    [Tooltip("왼쪽 앞 바퀴 역할을 할 콜라이더")]
    public WheelCollider frontLeftWheel;
    [Tooltip("왼쪽 뒤 바퀴 역할을 할 콜라이더")]
    public WheelCollider rearLeftWheel;
    [Tooltip("오른쪽 앞 바퀴 역할을 할 콜라이더")]
    public WheelCollider frontRightWheel;
    [Tooltip("오른쪽 뒤 바퀴 역할을 할 콜라이더")]
    public WheelCollider rearRightWheel;

    [Header("Drive Settings")]
    public float maxMotorTorque = 50f; // 직진/후진 모터 힘

    // 물리 연산과 관련된 처리는 FixedUpdate에서 수행하는 것이 안정적입니다.
    private void FixedUpdate()
    {
        float moveInput = 0f;
        float turnInput = 0f;

        // 신형 입력 시스템(New Input System)의 키보드 입력 처리
        if (Keyboard.current != null)
        {
            // 전진(W) / 후진(S)
            if (Keyboard.current.wKey.isPressed) moveInput = 1f;
            else if (Keyboard.current.sKey.isPressed) moveInput = -1f;

            // 좌회전(A) / 우회전(D)
            if (Keyboard.current.aKey.isPressed) turnInput = -1f;
            else if (Keyboard.current.dKey.isPressed) turnInput = 1f;
        }

        // 스키드 스티어링 로직: 직진 입력과 회전 입력을 섞어서 양쪽 궤도의 출력을 계산
        float leftTrackTorque = (moveInput + turnInput) * maxMotorTorque;
        float rightTrackTorque = (moveInput - turnInput) * maxMotorTorque;

        // 양쪽 바퀴에 계산된 토크 적용
        ApplyTorque(frontLeftWheel, rearLeftWheel, leftTrackTorque);
        ApplyTorque(frontRightWheel, rearRightWheel, rightTrackTorque);
    }

    private void ApplyTorque(WheelCollider front, WheelCollider rear, float torque)
    {
        front.motorTorque = torque;
        rear.motorTorque = torque;
    }
}