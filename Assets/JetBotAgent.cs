using UnityEngine;
using UnityEngine.InputSystem;

using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

[RequireComponent(typeof(Rigidbody))]
public class JetBotAgent : Agent
{
    // ============================================================
    // Wheel Colliders
    // ============================================================

    [Header("Wheel Colliders")]
    public WheelCollider frontLeftWheel;
    public WheelCollider rearLeftWheel;
    public WheelCollider frontRightWheel;
    public WheelCollider rearRightWheel;


    // ============================================================
    // Drive Settings
    // ============================================================

    [Header("Drive Settings")]

    [Tooltip("WheelCollider에 인가할 최대 토크")]
    public float maxMotorTorque = 5f;

    [Tooltip("정상 경로 추종 시 기본 전진 명령")]
    [Range(0f, 1f)]
    public float baseMoveCommand = 0.6f;

    [Tooltip("정지 시 적용할 브레이크 토크")]
    public float idleBrakeTorque = 1f;


    // ============================================================
    // ToF Sensors
    // ============================================================

    [Header("ToF Sensors")]

    public VirtualToFSensor tofLeft;
    public VirtualToFSensor tofFront;
    public VirtualToFSensor tofRight;

    [Tooltip("전방 ToF가 이 거리 안으로 들어오면 PPO 장애물 회피 활성화")]
    public float obstacleActivationDistance = 1.2f;

    [Tooltip("이 거리 이하면 danger = 1")]
    public float emergencyDistance = 0.25f;


    // ============================================================
    // Route
    // ============================================================

    [Header("Route")]

    [Tooltip("순서대로 따라갈 Route waypoint")]
    public Transform[] waypoints;

    [Tooltip("Waypoint 도착으로 인정할 거리")]
    public float waypointReachDistance = 0.7f;

    [Tooltip("이 각도 이상이면 최대 조향값 사용")]
    public float maxPathAngle = 45f;

    [Tooltip("기본 경로 조향 gain")]
    public float pathTurnGain = 0.7f;

    [Tooltip("급커브에서 최소 속도 비율")]
    [Range(0f, 1f)]
    public float minimumCornerSpeedScale = 0.4f;


    // ============================================================
    // PPO Avoidance
    // ============================================================

    [Header("PPO Avoidance")]

    [Tooltip("PPO가 기본 경로 조향에 추가할 수 있는 최대 correction")]
    public float maxSteeringCorrection = 1f;

    [Tooltip("장애물 회피 시 최소 속도 비율. 0이면 완전 정지 가능")]
    [Range(0f, 1f)]
    public float minimumSpeedScale = 0f;


    // ============================================================
    // Inspection
    // ============================================================

    [Header("Inspection")]

    [Tooltip("점검 시작/완료 로그 출력")]
    public bool inspectionDebugLog = true;


    // ============================================================
    // Reward
    // ============================================================

    [Header("Reward")]

    public float progressRewardScale = 1f;

    public float waypointReward = 1f;

    public float inspectionReward = 1f;

    public float goalReward = 10f;

    public float collisionPenalty = -5f;

    public float timePenalty = -0.0005f;

    public float obstaclePenaltyScale = 0.002f;

    public float steeringCorrectionPenaltyScale = 0.0001f;

    public float steeringChangePenaltyScale = 0.0002f;


    // ============================================================
    // Debug
    // ============================================================

    [Header("Debug")]

    public bool debugLog = false;

    public bool drawDebug = true;


    // ============================================================
    // Internal
    // ============================================================

    private Rigidbody rb;

    private Vector3 startPosition;
    private Quaternion startRotation;

    private int currentWaypoint = 0;

    private float moveCommand = 0f;
    private float turnCommand = 0f;

    private float previousSpeedAction = 0f;
    private float previousSteeringAction = 0f;

    private float leftDistance = 0f;
    private float frontDistance = 0f;
    private float rightDistance = 0f;

    private float previousWaypointDistance = 0f;

    private bool hasPreviousAction = false;
    private bool episodeEnding = false;

    // 점검 상태
    private bool isInspecting = false;
    private float inspectionTimer = 0f;
    private string currentInspectionName = "";


    // ============================================================
    // Initialize
    // ============================================================

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();

        startPosition = rb.position;
        startRotation = rb.rotation;

        // 필요하면 0으로 두고 무제한 테스트 가능
        // MaxStep = 10000;
    }


    // ============================================================
    // Episode Begin
    // ============================================================

    public override void OnEpisodeBegin()
    {
        episodeEnding = false;
        hasPreviousAction = false;

        isInspecting = false;
        inspectionTimer = 0f;
        currentInspectionName = "";

        currentWaypoint = 0;

        moveCommand = 0f;
        turnCommand = 0f;

        previousSpeedAction = 0f;
        previousSteeringAction = 0f;

        StopAllWheels();

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        rb.position = startPosition;
        rb.rotation = startRotation;

        if (waypoints != null && waypoints.Length > 0)
        {
            previousWaypointDistance =
                Vector3.Distance(
                    rb.position,
                    waypoints[currentWaypoint].position
                );
        }

        UpdateToFSensors();
    }


    // ============================================================
    // Observation
    //
    // 총 9개
    //
    // 1 ToF Left
    // 2 ToF Front
    // 3 ToF Right
    // 4 Path Turn
    // 5 Obstacle Danger
    // 6 Left - Right Free Space
    // 7 Yaw Rate
    // 8 Previous Speed Action
    // 9 Previous Steering Action
    // ============================================================

    public override void CollectObservations(VectorSensor sensor)
    {
        UpdateToFSensors();

        float leftNormalized =
            NormalizeDistance(leftDistance, tofLeft);

        float frontNormalized =
            NormalizeDistance(frontDistance, tofFront);

        float rightNormalized =
            NormalizeDistance(rightDistance, tofRight);


        // 1 ~ 3. ToF
        sensor.AddObservation(leftNormalized);
        sensor.AddObservation(frontNormalized);
        sensor.AddObservation(rightNormalized);


        // 4. Path turn
        float pathTurn = GetPathTurn();

        sensor.AddObservation(pathTurn);


        // 5. Obstacle danger
        float danger = GetObstacleDanger();

        sensor.AddObservation(danger);


        // 6. 좌/우 여유 공간 차이
        //
        // + : 왼쪽 공간이 더 큼
        // - : 오른쪽 공간이 더 큼
        float sideDifference =
            leftNormalized - rightNormalized;

        sensor.AddObservation(sideDifference);


        // 7. Yaw rate
        sensor.AddObservation(
            Mathf.Clamp(
                rb.angularVelocity.y / 5f,
                -1f,
                1f
            )
        );


        // 8 ~ 9. Previous PPO action
        sensor.AddObservation(previousSpeedAction);
        sensor.AddObservation(previousSteeringAction);
    }


    // ============================================================
    // Action
    //
    // Action 0:
    // -1 = 강한 감속
    // +1 = 기본 속도 유지
    //
    // Action 1:
    // -1 = 좌측 correction
    // +1 = 우측 correction
    //
    // 장애물이 없으면 PPO action은 무시됨.
    // ============================================================

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (episodeEnding)
            return;

        // 점검 중에는 PPO action 사용 안 함
        if (isInspecting)
            return;


        // --------------------------------------------------------
        // 직전 명령으로 실제 이동한 결과 평가
        // --------------------------------------------------------

        if (hasPreviousAction)
        {
            EvaluatePreviousMovement();

            if (episodeEnding || isInspecting)
                return;
        }


        // --------------------------------------------------------
        // 기본 Path Tracking
        // --------------------------------------------------------

        float pathTurn =
            GetPathTurn();


        // --------------------------------------------------------
        // 커브에서 자동 감속
        // --------------------------------------------------------

        float cornerAmount =
            Mathf.Abs(pathTurn);

        float cornerSpeedScale =
            Mathf.Lerp(
                1f,
                minimumCornerSpeedScale,
                cornerAmount
            );


        float baseSpeed =
            baseMoveCommand *
            cornerSpeedScale;


        // --------------------------------------------------------
        // PPO action
        // --------------------------------------------------------

        float speedAction =
            Mathf.Clamp(
                actions.ContinuousActions[0],
                -1f,
                1f
            );

        float steeringAction =
            Mathf.Clamp(
                actions.ContinuousActions[1],
                -1f,
                1f
            );


        float danger =
            GetObstacleDanger();


        // ========================================================
        // 정상 상황
        //
        // PPO 완전 무시
        // ========================================================

        if (danger <= 0f)
        {
            moveCommand =
                baseSpeed;

            turnCommand =
                pathTurn;
        }


        // ========================================================
        // 장애물 상황
        //
        // PPO가 감속 + 조향 correction 담당
        // ========================================================

        else
        {
            // speedAction [-1, +1]
            // ↓
            // [0, 1]
            float normalizedSpeedAction =
                (speedAction + 1f) * 0.5f;


            float ppoSpeedScale =
                Mathf.Lerp(
                    minimumSpeedScale,
                    1f,
                    normalizedSpeedAction
                );


            // 가까워질수록 강제 감속
            float safetySpeedScale =
                Mathf.Lerp(
                    minimumSpeedScale,
                    1f,
                    1f - danger
                );


            float finalSpeedScale =
                Mathf.Min(
                    ppoSpeedScale,
                    safetySpeedScale
                );


            moveCommand =
                baseSpeed *
                finalSpeedScale;


            // ----------------------------------------------------
            // PPO steering correction
            // ----------------------------------------------------

            float correction =
                steeringAction *
                maxSteeringCorrection *
                danger;


            turnCommand =
                Mathf.Clamp(
                    pathTurn + correction,
                    -1f,
                    1f
                );


            // ----------------------------------------------------
            // PPO 과도 조향 penalty
            // ----------------------------------------------------

            AddReward(
                -Mathf.Abs(steeringAction) *
                steeringCorrectionPenaltyScale
            );


            // ----------------------------------------------------
            // steering 급변 penalty
            // ----------------------------------------------------

            float steeringChange =
                Mathf.Abs(
                    steeringAction -
                    previousSteeringAction
                );


            AddReward(
                -steeringChange *
                steeringChangePenaltyScale
            );
        }


        previousSpeedAction =
            speedAction;

        previousSteeringAction =
            steeringAction;

        hasPreviousAction =
            true;
    }


    // ============================================================
    // FixedUpdate
    // ============================================================

    private void FixedUpdate()
    {
        if (episodeEnding)
        {
            StopAllWheels();
            return;
        }


        // ========================================================
        // 점검 중
        // ========================================================

        if (isInspecting)
        {
            StopAllWheels();

            inspectionTimer -=
                Time.fixedDeltaTime;


            if (inspectionTimer <= 0f)
            {
                FinishInspection();
            }

            return;
        }


        ApplyDrivePhysics();
    }


    // ============================================================
    // Path Tracking
    // ============================================================

    private float GetPathTurn()
    {
        if (
            waypoints == null ||
            waypoints.Length == 0 ||
            currentWaypoint >= waypoints.Length
        )
        {
            return 0f;
        }


        Vector3 localTarget =
            transform.InverseTransformPoint(
                waypoints[currentWaypoint].position
            );


        float angle =
            Mathf.Atan2(
                localTarget.x,
                localTarget.z
            ) *
            Mathf.Rad2Deg;


        float turn =
            angle /
            Mathf.Max(
                maxPathAngle,
                0.01f
            );


        turn *=
            pathTurnGain;


        return Mathf.Clamp(
            turn,
            -1f,
            1f
        );
    }


    // ============================================================
    // WheelCollider Physics
    // ============================================================

    private void ApplyDrivePhysics()
    {
        // 현재 정의:
        //
        // turn -1 = 좌회전
        // turn +1 = 우회전


        float leftTrackInput =
            moveCommand +
            turnCommand;

        float rightTrackInput =
            moveCommand -
            turnCommand;


        // --------------------------------------------------------
        // Differential mixing normalization
        // --------------------------------------------------------

        float maxMagnitude =
            Mathf.Max(
                1f,
                Mathf.Abs(leftTrackInput),
                Mathf.Abs(rightTrackInput)
            );


        leftTrackInput /=
            maxMagnitude;

        rightTrackInput /=
            maxMagnitude;


        float leftTorque =
            leftTrackInput *
            maxMotorTorque;

        float rightTorque =
            rightTrackInput *
            maxMotorTorque;


        ApplyTorque(
            frontLeftWheel,
            rearLeftWheel,
            leftTorque
        );


        ApplyTorque(
            frontRightWheel,
            rearRightWheel,
            rightTorque
        );


        // --------------------------------------------------------
        // 정지 명령
        // --------------------------------------------------------

        if (
            Mathf.Abs(moveCommand) < 0.03f &&
            Mathf.Abs(turnCommand) < 0.03f
        )
        {
            SetBrakeTorque(
                idleBrakeTorque
            );
        }
        else
        {
            SetBrakeTorque(0f);
        }
    }


    // ============================================================
    // Reward Evaluation
    // ============================================================

    private void EvaluatePreviousMovement()
    {
        if (
            waypoints == null ||
            waypoints.Length == 0 ||
            currentWaypoint >= waypoints.Length
        )
        {
            return;
        }


        float currentDistance =
            Vector3.Distance(
                rb.position,
                waypoints[currentWaypoint].position
            );


        // ========================================================
        // Progress Reward
        // ========================================================

        float progress =
            previousWaypointDistance -
            currentDistance;


        AddReward(
            progress *
            progressRewardScale
        );


        previousWaypointDistance =
            currentDistance;


        // ========================================================
        // Time penalty
        // ========================================================

        AddReward(
            timePenalty
        );


        // ========================================================
        // Obstacle proximity penalty
        // ========================================================

        float danger =
            GetObstacleDanger();


        if (danger > 0f)
        {
            AddReward(
                -danger *
                obstaclePenaltyScale
            );
        }


        // ========================================================
        // Waypoint 도착
        // ========================================================

        if (
            currentDistance <=
            waypointReachDistance
        )
        {
            AddReward(
                waypointReward
            );


            // ----------------------------------------------------
            // 점검 지점인지 확인
            // ----------------------------------------------------

            InspectionPoint inspection =
                waypoints[currentWaypoint]
                .GetComponent<InspectionPoint>();


            if (inspection != null)
            {
                StartInspection(
                    inspection
                );

                return;
            }


            MoveToNextWaypoint();
        }
    }


    // ============================================================
    // Inspection
    // ============================================================

    private void StartInspection(
        InspectionPoint point
    )
    {
        isInspecting =
            true;


        inspectionTimer =
            point.inspectionTime;


        currentInspectionName =
            point.inspectionName;


        moveCommand =
            0f;

        turnCommand =
            0f;


        StopAllWheels();


        if (inspectionDebugLog)
        {
            Debug.Log(
                "Inspection Start : "
                + currentInspectionName
            );
        }
    }


    private void FinishInspection()
    {
        if (!isInspecting)
            return;


        isInspecting =
            false;


        AddReward(
            inspectionReward
        );


        if (inspectionDebugLog)
        {
            Debug.Log(
                "Inspection Complete : "
                + currentInspectionName
            );
        }


        currentInspectionName =
            "";


        MoveToNextWaypoint();
    }


    // ============================================================
    // Next Waypoint
    // ============================================================

    private void MoveToNextWaypoint()
    {
        currentWaypoint++;


        // ========================================================
        // 모든 Route 완료
        // ========================================================

        if (
            currentWaypoint >=
            waypoints.Length
        )
        {
            AddReward(
                goalReward
            );


            if (debugLog)
            {
                Debug.Log(
                    "All route and inspections completed. "
                    +
                    "Reward = "
                    +
                    GetCumulativeReward()
                    .ToString("F3")
                );
            }


            FinishEpisode();

            return;
        }


        previousWaypointDistance =
            Vector3.Distance(
                rb.position,
                waypoints[currentWaypoint].position
            );


        hasPreviousAction =
            false;
    }


    // ============================================================
    // Obstacle Danger
    // ============================================================

    private float GetObstacleDanger()
    {
        if (tofFront == null)
            return 0f;


        if (
            frontDistance >=
            obstacleActivationDistance
        )
        {
            return 0f;
        }


        if (
            frontDistance <=
            emergencyDistance
        )
        {
            return 1f;
        }


        return Mathf.InverseLerp(
            obstacleActivationDistance,
            emergencyDistance,
            frontDistance
        );
    }


    // ============================================================
    // ToF
    // ============================================================

    private void UpdateToFSensors()
    {
        if (tofLeft != null)
        {
            leftDistance =
                tofLeft.ReadDistance();
        }


        if (tofFront != null)
        {
            frontDistance =
                tofFront.ReadDistance();
        }


        if (tofRight != null)
        {
            rightDistance =
                tofRight.ReadDistance();
        }
    }


    private float NormalizeDistance(
        float distance,
        VirtualToFSensor sensor
    )
    {
        if (sensor == null)
            return 1f;


        return Mathf.Clamp01(
            distance /
            sensor.maxDistance
        );
    }


    // ============================================================
    // Collision
    // ============================================================

    private void OnCollisionEnter(
        Collision collision
    )
    {
        if (episodeEnding)
            return;


        if (
            collision.gameObject.CompareTag("Wall") ||
            collision.gameObject.CompareTag("Obstacle") ||
            collision.gameObject.CompareTag("Human")
        )
        {
            AddReward(
                collisionPenalty
            );


            if (debugLog)
            {
                Debug.Log(
                    "Collision : "
                    +
                    collision.gameObject.name
                );
            }


            FinishEpisode();
        }
    }


    // ============================================================
    // Episode Finish
    // ============================================================

    private void FinishEpisode()
    {
        if (episodeEnding)
            return;


        episodeEnding =
            true;


        moveCommand =
            0f;

        turnCommand =
            0f;


        StopAllWheels();


        EndEpisode();
    }


    // ============================================================
    // Wheel Utilities
    // ============================================================

    private void ApplyTorque(
        WheelCollider front,
        WheelCollider rear,
        float torque
    )
    {
        if (front != null)
        {
            front.motorTorque =
                torque;
        }


        if (rear != null)
        {
            rear.motorTorque =
                torque;
        }
    }


    private void SetBrakeTorque(
        float torque
    )
    {
        if (frontLeftWheel != null)
        {
            frontLeftWheel.brakeTorque =
                torque;
        }


        if (rearLeftWheel != null)
        {
            rearLeftWheel.brakeTorque =
                torque;
        }


        if (frontRightWheel != null)
        {
            frontRightWheel.brakeTorque =
                torque;
        }


        if (rearRightWheel != null)
        {
            rearRightWheel.brakeTorque =
                torque;
        }
    }


    private void StopAllWheels()
    {
        if (frontLeftWheel != null)
        {
            frontLeftWheel.motorTorque =
                0f;
        }


        if (rearLeftWheel != null)
        {
            rearLeftWheel.motorTorque =
                0f;
        }


        if (frontRightWheel != null)
        {
            frontRightWheel.motorTorque =
                0f;
        }


        if (rearRightWheel != null)
        {
            rearRightWheel.motorTorque =
                0f;
        }


        SetBrakeTorque(
            idleBrakeTorque
        );
    }


    // ============================================================
    // Heuristic
    //
    // Behavior Type = Heuristic Only
    //
    // 장애물 회피 PPO 입력을
    // 사람이 테스트하기 위한 용도.
    // ============================================================

    public override void Heuristic(
        in ActionBuffers actionsOut
    )
    {
        ActionSegment<float> actions =
            actionsOut.ContinuousActions;


        float speedAction =
            1f;

        float steeringAction =
            0f;


        if (Keyboard.current != null)
        {
            // W = 정상 속도
            if (
                Keyboard.current
                .wKey
                .isPressed
            )
            {
                speedAction =
                    1f;
            }


            // S = 강한 감속
            else if (
                Keyboard.current
                .sKey
                .isPressed
            )
            {
                speedAction =
                    -1f;
            }


            // A = 왼쪽 회피 보정
            if (
                Keyboard.current
                .aKey
                .isPressed
            )
            {
                steeringAction =
                    -1f;
            }


            // D = 오른쪽 회피 보정
            else if (
                Keyboard.current
                .dKey
                .isPressed
            )
            {
                steeringAction =
                    1f;
            }
        }


        actions[0] =
            speedAction;

        actions[1] =
            steeringAction;
    }


    // ============================================================
    // Gizmos
    // ============================================================

    private void OnDrawGizmos()
    {
        if (!drawDebug)
            return;


        if (
            waypoints == null ||
            waypoints.Length == 0 ||
            currentWaypoint >= waypoints.Length
        )
        {
            return;
        }


        Gizmos.DrawLine(
            transform.position,
            waypoints[currentWaypoint].position
        );


        Gizmos.DrawWireSphere(
            waypoints[currentWaypoint].position,
            waypointReachDistance
        );
    }
}