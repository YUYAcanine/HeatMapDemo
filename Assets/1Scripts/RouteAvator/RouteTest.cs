using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

public class RouteTest : MonoBehaviour
{
    [Header("Points")]
    [SerializeField] private Transform start;
    [SerializeField] private Transform goal;

    [Header("Route")]
    [SerializeField] private Material routeLineMaterial;
    [SerializeField] private float startSearchRadius = 2.0f;
    [SerializeField] private float goalSearchRadius = 0.3f;
    [SerializeField] private float routeLineWidth = 0.05f;
    [SerializeField] private float routeLineYOffset = 0.05f;
    [SerializeField] private float routeHeightSampleInterval = 0.2f;
    [SerializeField] private float routeHeightSampleRadius = 1.0f;
    [SerializeField] private float maxGoalSampleVerticalOffset = 0.2f;
    [SerializeField] private float maxGoalSampleHorizontalOffset = 0.5f;
    [SerializeField] private bool rejectGoalSampleTooFar = true;
    [SerializeField] private bool logRouteSearch = true;

    [Header("Avatar Movement")]
    [SerializeField] private Transform childAvatar;
    [SerializeField] private bool moveAvatarOnStart = true;
    [SerializeField] private bool resetAvatarWhenRouteGenerated = true;
    [SerializeField] private float walkSpeed = 0.8f;
    [SerializeField] private float climbSpeed = 0.3f;
    [SerializeField] private float jumpSpeed = 1.0f;
    [SerializeField] private float climbHeightThreshold = 0.2f;
    [SerializeField] private float jumpMaxHeightDiff = 0.2f;
    [SerializeField] private float jumpMinHorizontalDistance = 0.15f;
    [SerializeField] private float jumpMaxHorizontalDistance = 0.5f;
    [SerializeField] private int jumpArcSampleCount = 8;
    [SerializeField] private float jumpLinkEndpointTolerance = 0.15f;
    [SerializeField] private float avatarYOffset = 0.0f;
    [SerializeField] private float avatarTurnSpeed = 540.0f;
    [SerializeField] private float avatarArrivalDistance = 0.05f;
    [SerializeField] private float minAvatarRoutePointDistance = 0.05f;
    [SerializeField] private float verticalMovementHorizontalThreshold = 0.05f;
    [SerializeField] private bool loopAvatarMovement = false;
    [SerializeField] private Animator childAvatarAnimator;
    [SerializeField] private string animatorSpeedParameter = "Speed";
    [SerializeField] private string animatorClimbParameter = "IsClimbing";
    [SerializeField] private string animatorJumpParameter = "IsJumping";
    [SerializeField] private bool restartClimbAnimationOnEnter = true;
    [SerializeField] private string animatorClimbStateName = "climbing";
    [SerializeField] private float animatorClimbRestartTransitionTime = 0.05f;
    [SerializeField] private bool disableAnimatorRootMotion = true;

    [Header("Debug")]
    [SerializeField] private bool logActionState = true;
    [SerializeField] private bool logAvatarMovement = false;
    [SerializeField] private bool logRoutePointGeneration = false;

    private NavMeshPath path;
    private LineRenderer routeLine;
    private bool hasPath = false;
    private readonly List<Vector3> avatarRoutePoints = new();
    private readonly List<ClimbSection> climbSections = new();
    private readonly List<JumpSection> jumpSections = new();
    private int avatarRoutePointIndex = 0;
    private bool isAvatarMoving = false;
    private bool isClimbing = false;
    private bool isJumping = false;
    private AvatarMoveMode avatarMoveMode = AvatarMoveMode.Walking;
    private int avatarMoveFrameCount = 0;
    private bool warnedMissingAnimatorSpeedParameter = false;
    private bool warnedMissingAnimatorClimbParameter = false;
    private bool warnedMissingAnimatorJumpParameter = false;
    private bool warnedMissingAnimatorClimbState = false;

    private void Start()
    {
        path = new NavMeshPath();

        if (childAvatarAnimator == null &&
            childAvatar != null)
        {
            childAvatarAnimator =
                childAvatar.GetComponentInChildren<Animator>();
        }

        if (disableAnimatorRootMotion &&
            childAvatarAnimator != null)
        {
            childAvatarAnimator.applyRootMotion = false;
        }

        CreateRouteLine();
        GenerateRoute();
    }

    private void Update()
    {
        MoveAvatarAlongRoute();
    }

    private void CreateRouteLine()
    {
        routeLine = new GameObject("RouteTestLine").AddComponent<LineRenderer>();
        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;
        routeLine.positionCount = 0;
        routeLine.enabled = false;
    }

    private void GenerateRoute()
    {
        if (start == null || goal == null)
        {
            Debug.LogWarning("RouteTest: start or goal is not assigned.");
            ClearRoute();
            return;
        }

        if (!NavMesh.SamplePosition(
            start.position,
            out NavMeshHit startHit,
            startSearchRadius,
            NavMesh.AllAreas
        ))
        {
            Debug.LogWarning(
                $"RouteTest: start is not on NavMesh. start={start.position}, searchRadius={startSearchRadius:F2}"
            );
            ClearRoute();
            return;
        }

        if (!NavMesh.SamplePosition(
            goal.position,
            out NavMeshHit goalHit,
            goalSearchRadius,
            NavMesh.AllAreas
        ))
        {
            Debug.LogWarning(
                $"RouteTest: goal is not near NavMesh. goal={goal.position}, searchRadius={goalSearchRadius:F2}"
            );
            ClearRoute();
            return;
        }

        float goalVerticalOffset =
            Mathf.Abs(goal.position.y - goalHit.position.y);
        float goalHorizontalOffset =
            HorizontalDistance(goal.position, goalHit.position);

        if (logRouteSearch)
        {
            Debug.Log(
                $"RouteTest: goal sample check. requestedGoal={goal.position}, sampledGoal={goalHit.position}, verticalOffset={goalVerticalOffset:F3}, horizontalOffset={goalHorizontalOffset:F3}, rejectTooFar={rejectGoalSampleTooFar}"
            );
        }

        if (rejectGoalSampleTooFar &&
            (goalVerticalOffset > maxGoalSampleVerticalOffset ||
             goalHorizontalOffset > maxGoalSampleHorizontalOffset))
        {
            Debug.LogWarning(
                $"RouteTest: sampled goal is too far from requested goal. requestedGoal={goal.position}, sampledGoal={goalHit.position}, verticalOffset={goalVerticalOffset:F3}/{maxGoalSampleVerticalOffset:F3}, horizontalOffset={goalHorizontalOffset:F3}/{maxGoalSampleHorizontalOffset:F3}"
            );
            ClearRoute();
            return;
        }

        bool calculated = NavMesh.CalculatePath(
            startHit.position,
            goalHit.position,
            NavMesh.AllAreas,
            path
        );

        hasPath =
            calculated &&
            path.status == NavMeshPathStatus.PathComplete;

        if (logRouteSearch)
        {
            Vector3 lastCorner =
                path.corners.Length > 0
                    ? path.corners[path.corners.Length - 1]
                    : Vector3.zero;
            float lastToGoalDistance =
                path.corners.Length > 0
                    ? Vector3.Distance(lastCorner, goalHit.position)
                    : -1f;

            Debug.Log(
                $"RouteTest: path result. requestedGoal={goal.position}, sampledGoal={goalHit.position}, calculated={calculated}, status={path.status}, corners={path.corners.Length}, visible={hasPath}, lastCorner={lastCorner}, lastToSampledGoal={lastToGoalDistance:F3}"
            );
        }

        ApplyRouteLine();
    }

    private void ClearRoute()
    {
        hasPath = false;

        if (path != null)
            path.ClearCorners();

        if (routeLine != null)
        {
            routeLine.positionCount = 0;
            routeLine.enabled = false;
        }

        avatarRoutePoints.Clear();
        climbSections.Clear();
        jumpSections.Clear();
        avatarRoutePointIndex = 0;
        isAvatarMoving = false;
        SetMoveMode(AvatarMoveMode.Walking);
        avatarMoveFrameCount = 0;
        SetAnimatorMoveSpeed(0f);
    }

    private void ApplyRouteLine()
    {
        if (!hasPath ||
            path == null ||
            path.corners.Length < 2 ||
            routeLine == null)
        {
            ClearRoute();
            return;
        }

        routeLine.material = routeLineMaterial;
        routeLine.startWidth = routeLineWidth;
        routeLine.endWidth = routeLineWidth;

        List<Vector3> displayPoints =
            BuildHeightAwareRoutePoints(path, routeLineYOffset, true);

        if (displayPoints.Count < 2)
        {
            ClearRoute();
            return;
        }

        routeLine.positionCount = displayPoints.Count;
        routeLine.SetPositions(displayPoints.ToArray());
        routeLine.enabled = true;

        avatarRoutePoints.Clear();
        jumpSections.Clear();
        avatarRoutePoints.AddRange(
            BuildAvatarRoutePoints(path)
        );
        BuildClimbSections();

        if (logRoutePointGeneration)
        {
            Debug.Log($"Goal Position = {goal.position}");
            Debug.Log($"Avatar Route Count = {avatarRoutePoints.Count}");

            for (int i = 0; i < avatarRoutePoints.Count; i++)
            {
                Debug.Log($"Route[{i}] = {avatarRoutePoints[i]}");
            }

            if (avatarRoutePoints.Count > 0)
            {
                Debug.Log($"Last Route Point = {avatarRoutePoints[avatarRoutePoints.Count - 1]}");
            }
        }

        LogAvatarRoutePoints();

        if (resetAvatarWhenRouteGenerated)
            ResetAvatarToRouteStart();

        if (moveAvatarOnStart)
            StartAvatarMovement();
    }

    public void StartAvatarMovement()
    {
        if (childAvatar == null ||
            avatarRoutePoints.Count < 2 ||
            walkSpeed <= 0f)
        {
            if (logAvatarMovement)
            {
                Debug.LogWarning(
                    $"RouteTest: cannot start avatar movement. childAvatar={(childAvatar == null ? "null" : childAvatar.name)}, routePoints={avatarRoutePoints.Count}, walkSpeed={walkSpeed:F2}"
                );
            }

            SetAnimatorMoveSpeed(0f);
            return;
        }

        if (avatarRoutePointIndex <= 0 ||
            avatarRoutePointIndex >= avatarRoutePoints.Count)
        {
            avatarRoutePointIndex = 1;
        }

        isAvatarMoving = true;
        SetMoveMode(AvatarMoveMode.Walking);
        avatarMoveFrameCount = 0;
        SetAnimatorMoveSpeed(walkSpeed);
        LogActionState("started");

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: avatar movement started. current={childAvatar.position}, targetIndex={avatarRoutePointIndex}, target={avatarRoutePoints[avatarRoutePointIndex]}, distance3D={Vector3.Distance(childAvatar.position, avatarRoutePoints[avatarRoutePointIndex]):F3}, distanceXZ={HorizontalDistance(childAvatar.position, avatarRoutePoints[avatarRoutePointIndex]):F3}"
            );
        }
    }

    public void StopAvatarMovement()
    {
        isAvatarMoving = false;
        SetMoveMode(AvatarMoveMode.Walking);
        SetAnimatorMoveSpeed(0f);
    }

    public void RestartAvatarMovement()
    {
        ResetAvatarToRouteStart();
        StartAvatarMovement();
    }

    private void ResetAvatarToRouteStart()
    {
        if (childAvatar == null ||
            avatarRoutePoints.Count == 0)
            return;

        childAvatar.position = avatarRoutePoints[0];
        SetMoveMode(AvatarMoveMode.Walking);
        avatarRoutePointIndex =
            avatarRoutePoints.Count > 1 ? 1 : 0;

        if (avatarRoutePoints.Count > 1)
            FaceAvatarToward(avatarRoutePoints[1], true);

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: avatar reset. position={childAvatar.position}, nextIndex={avatarRoutePointIndex}, next={(avatarRoutePointIndex < avatarRoutePoints.Count ? avatarRoutePoints[avatarRoutePointIndex].ToString() : "none")}, routePoints={avatarRoutePoints.Count}"
            );
        }
    }

    private void MoveAvatarAlongRoute()
    {
        if (!isAvatarMoving ||
            childAvatar == null ||
            avatarRoutePoints.Count < 2 ||
            avatarRoutePointIndex <= 0 ||
            avatarRoutePointIndex >= avatarRoutePoints.Count)
        {
            if (isAvatarMoving)
            {
                FinishAvatarMovement(
                    $"invalid move state. childAvatar={(childAvatar == null ? "null" : childAvatar.name)}, routePoints={avatarRoutePoints.Count}, index={avatarRoutePointIndex}"
                );
            }

            return;
        }

        avatarMoveFrameCount++;
        Vector3 target = avatarRoutePoints[avatarRoutePointIndex];

        float currentMoveSpeed =
            UpdateMovementStateAndGetMoveSpeed(target);
        float distanceBeforeMove =
            Vector3.Distance(childAvatar.position, target);
        float horizontalDistanceBeforeMove =
            HorizontalDistance(childAvatar.position, target);
        bool isVerticalMovement =
            IsVerticalMovementSegment(childAvatar.position, target);
        Vector3 moveTarget =
            isVerticalMovement
                ? new Vector3(
                    childAvatar.position.x,
                    target.y,
                    childAvatar.position.z
                )
                : target;
        float moveTargetDistanceBeforeMove =
            Vector3.Distance(childAvatar.position, moveTarget);

        if (moveTargetDistanceBeforeMove <= avatarArrivalDistance)
        {
            AdvanceAvatarRoutePoint(
                $"already within arrival distance. distanceXZ={horizontalDistanceBeforeMove:F3}, distance3D={distanceBeforeMove:F3}, moveTargetDistance={moveTargetDistanceBeforeMove:F3}, verticalMove={isVerticalMovement}"
            );
            return;
        }

        float step = currentMoveSpeed * Time.deltaTime;

        childAvatar.position =
            Vector3.MoveTowards(
                childAvatar.position,
                moveTarget,
                step
            );

        if (!isVerticalMovement)
            FaceAvatarToward(target, false);

        float distanceAfterMove =
            Vector3.Distance(childAvatar.position, target);
        float horizontalDistanceAfterMove =
            HorizontalDistance(childAvatar.position, target);
        float moveTargetDistanceAfterMove =
            Vector3.Distance(childAvatar.position, moveTarget);

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: avatar moving. frame={avatarMoveFrameCount}, index={avatarRoutePointIndex}/{avatarRoutePoints.Count - 1}, before3D={distanceBeforeMove:F3}, after3D={distanceAfterMove:F3}, beforeXZ={horizontalDistanceBeforeMove:F3}, afterXZ={horizontalDistanceAfterMove:F3}, moveTargetDistance={moveTargetDistanceAfterMove:F3}, verticalMove={isVerticalMovement}, speed={currentMoveSpeed:F3}, step={step:F3}, position={childAvatar.position}, target={target}, moveTarget={moveTarget}"
            );
        }

        if (moveTargetDistanceAfterMove > avatarArrivalDistance)
            return;

        AdvanceAvatarRoutePoint(
            $"reached after move. distanceXZ={horizontalDistanceAfterMove:F3}, distance3D={distanceAfterMove:F3}, moveTargetDistance={moveTargetDistanceAfterMove:F3}, verticalMove={isVerticalMovement}"
        );
    }

    private void AdvanceAvatarRoutePoint(string reason)
    {
        int reachedIndex = avatarRoutePointIndex;
        avatarRoutePointIndex++;

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: avatar reached route point. reachedIndex={reachedIndex}, nextIndex={avatarRoutePointIndex}, routePoints={avatarRoutePoints.Count}, mode={avatarMoveMode}, isClimbing={isClimbing}, reason={reason}"
            );
        }

        if (avatarRoutePointIndex < avatarRoutePoints.Count)
            return;

        if (loopAvatarMovement)
        {
            ResetAvatarToRouteStart();
            StartAvatarMovement();
            return;
        }

        FinishAvatarMovement(
            $"route point index reached end. lastReachedIndex={reachedIndex}, routePoints={avatarRoutePoints.Count}"
        );
    }

    private void FinishAvatarMovement(string reason)
    {
        isAvatarMoving = false;
        avatarRoutePointIndex = avatarRoutePoints.Count;
        SetAnimatorMoveSpeed(0f);
        SetMoveMode(AvatarMoveMode.Walking);
        LogActionState("stopped");

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: avatar movement finished. reason={reason}, position={(childAvatar == null ? "null" : childAvatar.position.ToString())}, routePoints={avatarRoutePoints.Count}, frame={avatarMoveFrameCount}"
            );
        }
    }

    private void LogActionState(string reason)
    {
        if (!logActionState)
            return;

        Debug.Log(
            $"RouteTest Action: {avatarMoveMode}. reason={reason}, segment={Mathf.Max(avatarRoutePointIndex - 1, 0)}->{avatarRoutePointIndex}, index={avatarRoutePointIndex}/{Mathf.Max(avatarRoutePoints.Count - 1, 0)}, isClimbing={isClimbing}, isJumping={isJumping}"
        );
    }

    private void FaceAvatarToward(
        Vector3 target,
        bool instant
    )
    {
        if (childAvatar == null)
            return;

        Vector3 direction =
            target - childAvatar.position;
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.0001f)
            return;

        Quaternion targetRotation =
            Quaternion.LookRotation(direction.normalized);

        childAvatar.rotation =
            instant
                ? targetRotation
                : Quaternion.RotateTowards(
                    childAvatar.rotation,
                    targetRotation,
                    avatarTurnSpeed * Time.deltaTime
                );
    }

    private void SetAnimatorMoveSpeed(float speed)
    {
        if (childAvatarAnimator == null ||
            string.IsNullOrEmpty(animatorSpeedParameter))
            return;

        if (!HasAnimatorParameter(
            animatorSpeedParameter,
            AnimatorControllerParameterType.Float
        ))
        {
            if (!warnedMissingAnimatorSpeedParameter)
            {
                warnedMissingAnimatorSpeedParameter = true;
                Debug.LogWarning(
                    $"RouteTest: Animator parameter '{animatorSpeedParameter}' does not exist or is not Float. Add a Float parameter named '{animatorSpeedParameter}' to the Animator Controller, or clear Animator Speed Parameter in the Inspector."
                );
            }

            return;
        }

        childAvatarAnimator.SetFloat(
            animatorSpeedParameter,
            speed
        );
    }

    private float UpdateMovementStateAndGetMoveSpeed(Vector3 target)
    {
        AvatarMoveMode nextMode =
            GetCurrentRoutePointMoveMode();
        SetMoveMode(nextMode);

        float currentMoveSpeed =
            avatarMoveMode switch
            {
                AvatarMoveMode.Climbing => climbSpeed,
                AvatarMoveMode.Jumping => jumpSpeed,
                _ => walkSpeed
            };

        SetAnimatorMoveSpeed(currentMoveSpeed);

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: movement state. climbThreshold={climbHeightThreshold:F3}, jumpHeightMax={jumpMaxHeightDiff:F3}, jumpHorizontal={jumpMinHorizontalDistance:F3}-{jumpMaxHorizontalDistance:F3}, mode={avatarMoveMode}, isClimbing={isClimbing}, isJumping={isJumping}, targetIndex={avatarRoutePointIndex}, speed={currentMoveSpeed:F3}"
            );
        }

        return Mathf.Max(currentMoveSpeed, 0f);
    }

    private void SetMoveMode(AvatarMoveMode mode)
    {
        bool modeChanged =
            avatarMoveMode != mode;

        avatarMoveMode = mode;
        isClimbing = avatarMoveMode == AvatarMoveMode.Climbing;
        isJumping = avatarMoveMode == AvatarMoveMode.Jumping;
        SetAnimatorClimbing(isClimbing);
        SetAnimatorJumping(isJumping);

        if (modeChanged &&
            isClimbing)
        {
            RestartAnimatorClimbState();
        }

        if (logActionState &&
            modeChanged)
        {
            Debug.Log(
                $"RouteTest Action: {avatarMoveMode}. segment={Mathf.Max(avatarRoutePointIndex - 1, 0)}->{avatarRoutePointIndex}, index={avatarRoutePointIndex}/{Mathf.Max(avatarRoutePoints.Count - 1, 0)}, isClimbing={isClimbing}, isJumping={isJumping}"
            );
        }
    }

    private AvatarMoveMode GetCurrentRoutePointMoveMode()
    {
        if (IsCurrentRoutePointInJumpSection())
            return AvatarMoveMode.Jumping;

        if (IsCurrentRoutePointInClimbSection())
            return AvatarMoveMode.Climbing;

        return AvatarMoveMode.Walking;
    }

    private bool IsCurrentRoutePointInClimbSection()
    {
        int fromIndex =
            avatarRoutePointIndex - 1;
        int toIndex =
            avatarRoutePointIndex;

        foreach (ClimbSection section in climbSections)
        {
            if (fromIndex >= section.StartIndex &&
                toIndex <= section.EndIndex)
                return true;
        }

        return false;
    }

    private bool IsCurrentRoutePointInJumpSection()
    {
        int fromIndex =
            avatarRoutePointIndex - 1;
        int toIndex =
            avatarRoutePointIndex;

        foreach (JumpSection section in jumpSections)
        {
            if (fromIndex >= section.StartIndex &&
                toIndex <= section.EndIndex)
                return true;
        }

        return false;
    }

    private void SetAnimatorClimbing(bool isClimbing)
    {
        if (childAvatarAnimator == null ||
            string.IsNullOrEmpty(animatorClimbParameter))
            return;

        if (!HasAnimatorParameter(
            animatorClimbParameter,
            AnimatorControllerParameterType.Bool
        ))
        {
            if (!warnedMissingAnimatorClimbParameter)
            {
                warnedMissingAnimatorClimbParameter = true;
                Debug.LogWarning(
                    $"RouteTest: Animator parameter '{animatorClimbParameter}' does not exist or is not Bool. Add a Bool parameter named '{animatorClimbParameter}' to the Animator Controller, or clear Animator Climb Parameter in the Inspector."
                );
            }

            return;
        }

        childAvatarAnimator.SetBool(
            animatorClimbParameter,
            isClimbing
        );
    }

    private void RestartAnimatorClimbState()
    {
        if (!restartClimbAnimationOnEnter ||
            childAvatarAnimator == null ||
            string.IsNullOrEmpty(animatorClimbStateName))
            return;

        int stateHash =
            Animator.StringToHash(animatorClimbStateName);

        if (!childAvatarAnimator.HasState(0, stateHash))
        {
            if (!warnedMissingAnimatorClimbState)
            {
                warnedMissingAnimatorClimbState = true;
                Debug.LogWarning(
                    $"RouteTest: Animator state '{animatorClimbStateName}' was not found on layer 0. Set Animator Climb State Name to the exact state name, or turn off Restart Climb Animation On Enter."
                );
            }

            return;
        }

        childAvatarAnimator.CrossFadeInFixedTime(
            stateHash,
            Mathf.Max(animatorClimbRestartTransitionTime, 0f),
            0,
            0f
        );

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: climb animation restarted. state={animatorClimbStateName}, transition={animatorClimbRestartTransitionTime:F3}"
            );
        }
    }

    private void SetAnimatorJumping(bool isJumping)
    {
        if (childAvatarAnimator == null ||
            string.IsNullOrEmpty(animatorJumpParameter))
            return;

        if (!HasAnimatorParameter(
            animatorJumpParameter,
            AnimatorControllerParameterType.Bool
        ))
        {
            if (!warnedMissingAnimatorJumpParameter)
            {
                warnedMissingAnimatorJumpParameter = true;
                Debug.LogWarning(
                    $"RouteTest: Animator parameter '{animatorJumpParameter}' does not exist or is not Bool. Add a Bool parameter named '{animatorJumpParameter}' to the Animator Controller, or clear Animator Jump Parameter in the Inspector."
                );
            }

            return;
        }

        childAvatarAnimator.SetBool(
            animatorJumpParameter,
            isJumping
        );
    }


    private bool HasAnimatorParameter(
        string parameterName,
        AnimatorControllerParameterType parameterType
    )
    {
        foreach (AnimatorControllerParameter parameter in childAvatarAnimator.parameters)
        {
            if (parameter.name == parameterName &&
                parameter.type == parameterType)
                return true;
        }

        return false;
    }

    private List<Vector3> BuildAvatarRoutePoints(NavMeshPath targetPath)
    {
        List<Vector3> sampledPoints =
            BuildHeightAwareRoutePoints(
                targetPath,
                avatarYOffset,
                true,
                jumpSections
            );

        if (jumpSections.Count > 0)
            return sampledPoints;

        List<Vector3> filteredPoints = new();

        foreach (Vector3 point in sampledPoints)
        {
            float previousDistance =
                filteredPoints.Count > 0
                    ? Vector3.Distance(
                        filteredPoints[filteredPoints.Count - 1],
                        point
                    )
                    : float.PositiveInfinity;

            if (filteredPoints.Count > 0 &&
                previousDistance < minAvatarRoutePointDistance)
            {
                if (logRoutePointGeneration)
                {
                    Debug.Log(
                        $"RouteTest: avatar route point skipped as duplicate/near. point={point}, previous={filteredPoints[filteredPoints.Count - 1]}, distance3D={previousDistance:F3}, distanceXZ={HorizontalDistance(filteredPoints[filteredPoints.Count - 1], point):F3}, min={minAvatarRoutePointDistance:F3}"
                    );
                }

                continue;
            }

            filteredPoints.Add(point);
        }

        if (filteredPoints.Count == 1 &&
            sampledPoints.Count > 1)
        {
            filteredPoints.Add(sampledPoints[sampledPoints.Count - 1]);
        }

        return filteredPoints;
    }

    private void BuildClimbSections()
    {
        climbSections.Clear();

        int climbStartIndex = -1;
        int climbEndIndex = -1;

        for (int i = 0; i < avatarRoutePoints.Count - 1; i++)
        {
            float heightDiff =
                avatarRoutePoints[i + 1].y -
                avatarRoutePoints[i].y;

            if (heightDiff >= climbHeightThreshold)
            {
                if (climbStartIndex < 0)
                    climbStartIndex = i;

                climbEndIndex = i + 1;
                continue;
            }

            if (climbStartIndex >= 0)
            {
                AddClimbSection(climbStartIndex, climbEndIndex);
                climbStartIndex = -1;
                climbEndIndex = -1;
            }
        }

        if (climbStartIndex >= 0)
            AddClimbSection(climbStartIndex, climbEndIndex);
    }

    private void BuildJumpSections()
    {
        jumpSections.Clear();

        for (int i = 0; i < avatarRoutePoints.Count - 1; i++)
        {
            float heightDiff =
                Mathf.Abs(
                    avatarRoutePoints[i + 1].y -
                    avatarRoutePoints[i].y
                );
            float horizontalDistance =
                HorizontalDistance(
                    avatarRoutePoints[i],
                    avatarRoutePoints[i + 1]
                );

            if (heightDiff > jumpMaxHeightDiff ||
                horizontalDistance < jumpMinHorizontalDistance ||
                horizontalDistance > jumpMaxHorizontalDistance ||
                !IsNavMeshGapSegment(avatarRoutePoints[i], avatarRoutePoints[i + 1], avatarYOffset))
                continue;

            AddJumpSection(i, i + 1, heightDiff, horizontalDistance);
        }
    }

    private bool IsNavMeshGapSegment(
        Vector3 from,
        Vector3 to,
        float yOffset
    )
    {
        Vector3 fromOnNavMesh =
            from - Vector3.up * yOffset;
        Vector3 toOnNavMesh =
            to - Vector3.up * yOffset;

        return NavMesh.Raycast(
            fromOnNavMesh,
            toOnNavMesh,
            out _,
            NavMesh.AllAreas
        );
    }

    private void AddClimbSection(int startIndex, int endIndex)
    {
        climbSections.Add(
            new ClimbSection(startIndex, endIndex)
        );

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: climb section detected. startIndex={startIndex}, endIndex={endIndex}, start={avatarRoutePoints[startIndex]}, end={avatarRoutePoints[endIndex]}"
            );
        }
    }

    private void AddJumpSection(
        int startIndex,
        int endIndex,
        float heightDiff,
        float horizontalDistance
    )
    {
        jumpSections.Add(
            new JumpSection(startIndex, endIndex)
        );

        if (logAvatarMovement)
        {
            Debug.Log(
                $"RouteTest: jump section detected. startIndex={startIndex}, endIndex={endIndex}, heightDiff={heightDiff:F3}, horizontalDistance={horizontalDistance:F3}, start={avatarRoutePoints[startIndex]}, end={avatarRoutePoints[endIndex]}"
            );
        }
    }

    private void LogAvatarRoutePoints()
    {
        if (!logAvatarMovement)
            return;

        Debug.Log(
            $"RouteTest: avatar route generated. points={avatarRoutePoints.Count}, climbSections={climbSections.Count}, jumpSections={jumpSections.Count}, minPointDistance={minAvatarRoutePointDistance:F3}, arrivalDistance={avatarArrivalDistance:F3}"
        );

        for (int i = 0; i < avatarRoutePoints.Count; i++)
        {
            float previousDistance3D =
                i > 0
                    ? Vector3.Distance(
                        avatarRoutePoints[i - 1],
                        avatarRoutePoints[i]
                    )
                    : 0f;
            float previousDistanceXZ =
                i > 0
                    ? HorizontalDistance(
                        avatarRoutePoints[i - 1],
                        avatarRoutePoints[i]
                    )
                    : 0f;

            Debug.Log(
                $"RouteTest: avatarRoutePoints[{i}]={avatarRoutePoints[i]}, prevDistance3D={previousDistance3D:F3}, prevDistanceXZ={previousDistanceXZ:F3}"
            );
        }
    }

    private float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private bool IsVerticalMovementSegment(Vector3 from, Vector3 to)
    {
        float horizontalDistance =
            HorizontalDistance(from, to);
        float verticalDistance =
            Mathf.Abs(to.y - from.y);

        return horizontalDistance <= verticalMovementHorizontalThreshold &&
            verticalDistance > avatarArrivalDistance;
    }

    private List<Vector3> BuildHeightAwareRoutePoints(
        NavMeshPath targetPath,
        float yOffset,
        bool convertJumpSegments = false,
        List<JumpSection> detectedJumpSections = null
    )
    {
        List<Vector3> points = new();
        Vector3[] corners = targetPath.corners;

        for (int i = 0; i < corners.Length - 1; i++)
        {
            Vector3 from = corners[i];
            Vector3 to = corners[i + 1];

            if (convertJumpSegments &&
                IsJumpLinkSegment(from, to))
            {
                AddDirectJumpLinkRoutePoints(
                    points,
                    detectedJumpSections,
                    from,
                    to,
                    yOffset,
                    i
                );
                continue;
            }

            if (convertJumpSegments &&
                IsJumpCandidateSegment(from, to))
            {
                AddDirectJumpLinkRoutePoints(
                    points,
                    detectedJumpSections,
                    from,
                    to,
                    yOffset,
                    i
                );
                continue;
            }

            float distance = Vector3.Distance(from, to);
            int sampleCount = Mathf.Max(
                1,
                Mathf.CeilToInt(
                    distance /
                    Mathf.Max(routeHeightSampleInterval, 0.01f)
                )
            );

            for (int j = 0; j <= sampleCount; j++)
            {
                if (i > 0 && j == 0)
                    continue;

                Vector3 samplePoint =
                    Vector3.Lerp(
                        from,
                        to,
                        (float)j / sampleCount
                    );

                if (NavMesh.SamplePosition(
                    samplePoint,
                    out NavMeshHit heightHit,
                    routeHeightSampleRadius,
                    NavMesh.AllAreas
                ))
                {
                    Vector3 point =
                        heightHit.position +
                        Vector3.up * yOffset;
                    points.Add(point);

                    if (logRoutePointGeneration)
                    {
                        Debug.Log(
                            $"RouteTest: BuildHeightAwareRoutePoints add navmesh point. segment={i}, sample={j}/{sampleCount}, samplePoint={samplePoint}, hit={heightHit.position}, point={point}, yOffset={yOffset:F3}"
                        );
                    }
                }
                else
                {
                    Vector3 point =
                        samplePoint +
                        Vector3.up * yOffset;
                    points.Add(point);

                    if (logRoutePointGeneration)
                    {
                        Debug.LogWarning(
                            $"RouteTest: BuildHeightAwareRoutePoints add fallback point. segment={i}, sample={j}/{sampleCount}, samplePoint={samplePoint}, point={point}, yOffset={yOffset:F3}"
                        );
                    }
                }
            }
        }

        return points;
    }

    private bool IsJumpCandidateSegment(Vector3 from, Vector3 to)
    {
        float heightDiff =
            Mathf.Abs(to.y - from.y);
        float horizontalDistance =
            HorizontalDistance(from, to);

        return heightDiff <= jumpMaxHeightDiff &&
            horizontalDistance >= jumpMinHorizontalDistance &&
            horizontalDistance <= jumpMaxHorizontalDistance &&
            IsNavMeshGapSegment(from, to, 0f);
    }

    private bool IsJumpLinkSegment(Vector3 from, Vector3 to)
    {
        NavMeshLink[] links =
            FindObjectsOfType<NavMeshLink>();

        foreach (NavMeshLink link in links)
        {
            if (link == null ||
                !link.name.StartsWith("AutoJumpLink_"))
                continue;

            Vector3 linkStart =
                link.transform.TransformPoint(link.startPoint);
            Vector3 linkEnd =
                link.transform.TransformPoint(link.endPoint);

            bool sameDirection =
                Vector3.Distance(from, linkStart) <= jumpLinkEndpointTolerance &&
                Vector3.Distance(to, linkEnd) <= jumpLinkEndpointTolerance;
            bool reverseDirection =
                Vector3.Distance(from, linkEnd) <= jumpLinkEndpointTolerance &&
                Vector3.Distance(to, linkStart) <= jumpLinkEndpointTolerance;

            if (sameDirection ||
                reverseDirection)
                return true;
        }

        return false;
    }

    private void AddDirectJumpLinkRoutePoints(
        List<Vector3> points,
        List<JumpSection> detectedJumpSections,
        Vector3 from,
        Vector3 to,
        float yOffset,
        int segmentIndex
    )
    {
        int beforeCount =
            points.Count;
        int sampleCount =
            Mathf.Max(jumpArcSampleCount, 2);

        AddRoutePoint(
            points,
            from + Vector3.up * yOffset
        );

        int sectionStartIndex =
            Mathf.Max(beforeCount == 0 ? 1 : beforeCount, 1);

        for (int i = 1; i <= sampleCount; i++)
        {
            float t =
                (float)i / sampleCount;
            Vector3 point =
                Vector3.Lerp(from, to, t);
            AddRoutePoint(
                points,
                point + Vector3.up * yOffset
            );
        }

        int sectionEndIndex =
            points.Count - 1;

        if (detectedJumpSections != null &&
            sectionEndIndex >= sectionStartIndex)
        {
            detectedJumpSections.Add(
                new JumpSection(sectionStartIndex, sectionEndIndex)
            );
        }

        if (logRoutePointGeneration)
        {
            Debug.Log(
                $"RouteTest: direct jump link route generated. segment={segmentIndex}, startIndex={sectionStartIndex}, endIndex={sectionEndIndex}, from={from}, to={to}, heightDiff={Mathf.Abs(to.y - from.y):F3}, horizontalDistance={HorizontalDistance(from, to):F3}, samples={sampleCount}, yOffset={yOffset:F3}"
            );
        }
    }

    private void AddRoutePoint(
        List<Vector3> points,
        Vector3 point
    )
    {
        if (points.Count > 0 &&
            Vector3.Distance(points[points.Count - 1], point) < 0.001f)
            return;

        points.Add(point);
    }

    private void OnDrawGizmos()
    {
        if (!hasPath ||
            path == null ||
            path.corners.Length < 2)
            return;

        List<Vector3> displayPoints =
            BuildHeightAwareRoutePoints(path, routeLineYOffset, true);

        if (displayPoints.Count < 2)
            return;

        Gizmos.color = Color.cyan;

        for (int i = 0; i < displayPoints.Count - 1; i++)
        {
            Gizmos.DrawLine(
                displayPoints[i],
                displayPoints[i + 1]
            );
        }
    }

    private enum AvatarMoveMode
    {
        Walking,
        Climbing,
        Jumping
    }

    private readonly struct ClimbSection
    {
        public ClimbSection(int startIndex, int endIndex)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
        }

        public int StartIndex { get; }
        public int EndIndex { get; }
    }

    private readonly struct JumpSection
    {
        public JumpSection(int startIndex, int endIndex)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
        }

        public int StartIndex { get; }
        public int EndIndex { get; }
    }
}
