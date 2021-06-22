using LiteNetLibManager;
using StandardAssets.Characters.Physics;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace MultiplayerARPG
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    [RequireComponent(typeof(OpenCharacterController))]
    public class TidyRigidBodyEntityMovement_MountFly : BaseNetworkedGameEntityComponent<BaseGameEntity>, IEntityMovementComponent
    {
        /// <summary>
        /// Buffer to fix invalid teleport position
        /// </summary>
        public const byte FRAME_BUFFER = 3;
        protected static readonly RaycastHit[] findGroundRaycastHits = new RaycastHit[25];

        [Header("Movement AI")]
        [Range(0.01f, 1f)]
        public float stoppingDistance = 0.1f;

        [Header("Movement Settings")]
        public float jumpHeight = 2f;
        public ApplyJumpForceMode applyJumpForceMode = ApplyJumpForceMode.ApplyImmediately;
        public float applyJumpForceFixedDuration;
        public float backwardMoveSpeedRate = 0.75f;
        public float gravity = 9.81f;
        public float maxFallVelocity = 40f;
        [Tooltip("Delay before character change from grounded state to airborne")]
        public float airborneDelay = 0.01f;
        public bool doNotChangeVelocityWhileAirborne;
        public float landedPauseMovementDuration = 0f;
        [Range(0.1f, 1f)]
        public float underWaterThreshold = 0.75f;
        public bool autoSwimToSurface;

        [Header("Flying Settings")]
        public bool canFly = true;
        public bool useSmoothVelocity = true;

        [Header("Root Motion Settings")]
        public bool useRootMotionForMovement;
        public bool useRootMotionForAirMovement;
        public bool useRootMotionForJump;
        public bool useRootMotionForFall;
        public bool useRootMotionWhileNotMoving;
        public bool useRootMotionUnderWater;

        [Header("Networking Settings")]
        public float moveThreshold = 0.01f;
        public float snapThreshold = 5.0f;
        [Range(0.00825f, 0.1f)]
        public float clientSyncTransformInterval = 0.05f;
        [Range(0.00825f, 0.1f)]
        public float clientSendInputsInterval = 0.05f;
        [Range(0.00825f, 0.1f)]
        public float serverSyncTransformInterval = 0.05f;

        public Animator CacheAnimator { get; private set; }
        public Rigidbody CacheRigidbody { get; private set; }
        public CapsuleCollider CacheCapsuleCollider { get; private set; }
        public OpenCharacterController CacheOpenCharacterController { get; private set; }

        public float StoppingDistance
        {
            get { return stoppingDistance; }
        }

        public Queue<Vector3> navPaths { get; private set; }
        public bool HasNavPaths
        {
            get { return navPaths != null && navPaths.Count > 0; }
        }

        // Movement codes
        private float airborneElapsed;
        private bool isUnderWater;
        private bool isJumping;
        private bool applyingJumpForce;
        private float applyJumpForceCountDown;
        private Collider waterCollider;
        private float yRotation;
        private Vector3 platformMotion;
        private Transform groundedTransform;
        private Vector3 groundedLocalPosition;
        private Vector3 oldGroundedPosition;
        private long acceptedPositionTimestamp;
        private long acceptedJumpTimestamp;
        private Vector3? clientTargetPosition;
        private float? targetYRotation;
        private float yRotateLerpTime;
        private float yRotateLerpDuration;
        private bool acceptedJump;
        private bool sendingJump;
        private float lastServerSyncTransform;
        private float lastClientSyncTransform;
        private float lastClientSendInputs;
        private EntityMovementInput oldInput;
        private EntityMovementInput currentInput;
        private MovementState tempMovementState;
        private Vector3 tempInputDirection;
        private Vector3 tempMoveDirection;
        private Vector3 tempHorizontalMoveDirection;
        private Vector3 tempMoveVelocity;
        private Vector3 tempTargetPosition;
        private Vector3 tempCurrentPosition;
        private Vector3 tempPredictPosition;
        private Vector3 velocityBeforeAirborne;
        private float tempVerticalVelocity;
        private float tempSqrMagnitude;
        private float tempPredictSqrMagnitude;
        private float tempTargetDistance;
        private float tempEntityMoveSpeed;
        private float tempCurrentMoveSpeed;
        private CollisionFlags collisionFlags;
        private byte framesAfterTeleported;
        private Vector3 teleportedPosition;
        private float pauseMovementCountDown;
        private bool previouslyGrounded;
        private bool previouslyAirborne;
        private SyncFieldBool isFlying = new SyncFieldBool();
        private Vector3 tempLastMoveVelocity;

        public bool IsFlying
        {
            get { return isFlying.Value; }
            set { isFlying.Value = value; }
        }

        public override void EntityAwake()
        {
            // Prepare animator component
            CacheAnimator = GetComponent<Animator>();
            // Prepare rigidbody component
            CacheRigidbody = gameObject.GetOrAddComponent<Rigidbody>();
            // Prepare collider component
            CacheCapsuleCollider = gameObject.GetOrAddComponent<CapsuleCollider>();
            // Prepare open character controller
            float radius = CacheCapsuleCollider.radius;
            float height = CacheCapsuleCollider.height;
            Vector3 center = CacheCapsuleCollider.center;
            CacheOpenCharacterController = gameObject.GetOrAddComponent<OpenCharacterController>((comp) =>
            {
                comp.SetRadiusHeightAndCenter(radius, height, center, true, true);
            });
            CacheOpenCharacterController.collision += OnCharacterControllerCollision;
            // Disable unused component
            LiteNetLibTransform disablingComp = gameObject.GetComponent<LiteNetLibTransform>();
            if (disablingComp != null)
            {
                Logging.LogWarning("RigidBodyEntityMovement", "You can remove `LiteNetLibTransform` component from game entity, it's not being used anymore [" + name + "]");
                disablingComp.enabled = false;
            }
            // Setup
            StopMoveFunction();

            RegisterNetFunction<bool>(Cmd_TidyToggleFly);
        }

        public override void EntityStart()
        {
            yRotation = CacheTransform.eulerAngles.y;
            tempCurrentPosition = CacheTransform.position;
            CacheOpenCharacterController.SetPosition(tempCurrentPosition, true);
            tempVerticalVelocity = 0;
        }

        public override void EntityLateUpdate()
        {
            base.EntityLateUpdate();
            if (framesAfterTeleported > 0)
                framesAfterTeleported--;
        }

        public override void ComponentOnEnable()
        {
            CacheOpenCharacterController.enabled = true;
            CacheOpenCharacterController.SetPosition(CacheTransform.position, true);
            tempVerticalVelocity = 0;
        }

        public override void ComponentOnDisable()
        {
            CacheOpenCharacterController.enabled = false;
        }

        public override void EntityOnDestroy()
        {
            base.EntityOnDestroy();
            CacheOpenCharacterController.collision -= OnCharacterControllerCollision;
        }

        private void OnAnimatorMove()
        {
            if (!CacheAnimator)
                return;

            if (useRootMotionWhileNotMoving &&
                !Entity.MovementState.HasFlag(MovementState.Forward) &&
                !Entity.MovementState.HasFlag(MovementState.Backward) &&
                !Entity.MovementState.HasFlag(MovementState.Left) &&
                !Entity.MovementState.HasFlag(MovementState.Right) &&
                !Entity.MovementState.HasFlag(MovementState.IsJump))
            {
                // No movement, apply root motion position / rotation
                CacheAnimator.ApplyBuiltinRootMotion();
                return;
            }

            if (Entity.MovementState.HasFlag(MovementState.IsGrounded) && useRootMotionForMovement)
                CacheAnimator.ApplyBuiltinRootMotion();
            if (!Entity.MovementState.HasFlag(MovementState.IsGrounded) && useRootMotionForAirMovement)
                CacheAnimator.ApplyBuiltinRootMotion();
            if (Entity.MovementState.HasFlag(MovementState.IsUnderWater) && useRootMotionUnderWater)
                CacheAnimator.ApplyBuiltinRootMotion();
        }

        public void StopMove()
        {
            if (Entity.MovementSecure == MovementSecure.ServerAuthoritative)
            {
                // Send movement input to server, then server will apply movement and sync transform to clients
                this.ClientSendStopMove();
            }
            StopMoveFunction();
        }

        private void StopMoveFunction()
        {
            navPaths = null;
        }

        public void KeyMovement(Vector3 moveDirection, MovementState movementState)
        {
            if (!Entity.CanMove())
                return;
            if (this.CanPredictMovement())
            {
                // Always apply movement to owner client (it's client prediction for server auth movement)
                tempInputDirection = moveDirection;
                tempMovementState = movementState;
                if (tempInputDirection.sqrMagnitude > 0)
                    navPaths = null;
                if (!isJumping && !applyingJumpForce)
                    isJumping = CacheOpenCharacterController.isGrounded && tempMovementState.HasFlag(MovementState.IsJump);
            }
        }

        public void PointClickMovement(Vector3 position)
        {
            if (!Entity.CanMove())
                return;
            if (this.CanPredictMovement())
            {
                // Always apply movement to owner client (it's client prediction for server auth movement)
                tempMovementState = MovementState.Forward;
                SetMovePaths(position, true);
            }
        }

        public void SetLookRotation(Quaternion rotation)
        {
            if (!Entity.CanMove())
                return;
            if (this.CanPredictMovement())
            {
                // Always apply movement to owner client (it's client prediction for server auth movement)
                if (!HasNavPaths)
                    yRotation = rotation.eulerAngles.y;
            }
        }

        public Quaternion GetLookRotation()
        {
            return Quaternion.Euler(0f, yRotation, 0f);
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (!IsServer)
            {
                Logging.LogWarning("RigidBodyEntityMovement", "Teleport function shouldn't be called at client [" + name + "]");
                return;
            }
            this.ServerSendTeleport3D(position, rotation);
            yRotation = rotation.eulerAngles.y;
            OnTeleport(position);
        }

        public bool FindGroundedPosition(Vector3 fromPosition, float findDistance, out Vector3 result)
        {
            return PhysicUtils.FindGroundedPosition(fromPosition, findGroundRaycastHits, findDistance, GameInstance.Singleton.GetGameEntityGroundDetectionLayerMask(), out result, CacheTransform);
        }

        public override void EntityUpdate()
        {
            if (framesAfterTeleported > 0)
            {
                CacheOpenCharacterController.SetPosition(teleportedPosition, false);
                return;
            }

            UpdateMovement(Time.deltaTime);
            if (IsOwnerClient || (IsServer && Entity.MovementSecure == MovementSecure.ServerAuthoritative))
            {
                tempMovementState = tempMoveDirection.sqrMagnitude > 0f ? tempMovementState : MovementState.None;
                if (isUnderWater)
                    tempMovementState |= MovementState.IsUnderWater;
                if (CacheOpenCharacterController.isGrounded || airborneElapsed < airborneDelay)
                    tempMovementState |= MovementState.IsGrounded;
                Entity.SetMovement(tempMovementState);
            }

            #region Mount Fly
            if(IsOwnerClient)
            {
                if (InputManager.GetButtonDown("ToggleFly"))
                    CallNetFunction(Cmd_TidyToggleFly, FunctionReceivers.All, !IsFlying);
            }
            #endregion
        }

        #region Mount Fly
        private void Cmd_TidyToggleFly(bool flying)
        {
            TidyToggleFly(flying);
        }

        private void TidyToggleFly(bool flying)
        {
            IsFlying = flying;
        }
        #endregion

        public override void EntityFixedUpdate()
        {
            SyncTransform();
        }

        private void SyncTransform()
        {
            float currentTime = Time.fixedTime;
            if (Entity.MovementSecure == MovementSecure.NotSecure && IsOwnerClient && !IsServer)
            {
                // Sync transform from owner client to server (except it's both owner client and server)
                if (currentTime - lastClientSyncTransform > clientSyncTransformInterval)
                {
                    this.ClientSendSyncTransform3D();
                    if (sendingJump)
                    {
                        this.ClientSendJump();
                        sendingJump = false;
                    }
                    lastClientSyncTransform = currentTime;
                }
            }
            if (Entity.MovementSecure == MovementSecure.ServerAuthoritative && IsOwnerClient && !IsServer)
            {
                InputState inputState;
                if (currentTime - lastClientSendInputs > clientSendInputsInterval && this.DifferInputEnoughToSend(oldInput, currentInput, out inputState))
                {
                    this.ClientSendMovementInput3D(inputState, currentInput.MovementState, currentInput.Position, currentInput.Rotation);
                    oldInput = currentInput;
                    currentInput = null;
                    lastClientSendInputs = currentTime;
                }
            }
            if (IsServer)
            {
                // Sync transform from server to all clients (include owner client)
                if (currentTime - lastServerSyncTransform > serverSyncTransformInterval)
                {
                    this.ServerSendSyncTransform3D();
                    if (sendingJump)
                    {
                        this.ServerSendJump();
                        sendingJump = false;
                    }
                    lastServerSyncTransform = currentTime;
                }
            }
        }

        private void WaterCheck()
        {
            if (waterCollider == null)
            {
                // Not in water
                isUnderWater = false;
                return;
            }
            float footToSurfaceDist = waterCollider.bounds.max.y - CacheCapsuleCollider.bounds.min.y;
            float currentThreshold = footToSurfaceDist / (CacheCapsuleCollider.bounds.max.y - CacheCapsuleCollider.bounds.min.y);
            isUnderWater = currentThreshold >= underWaterThreshold;
        }

        private void UpdateMovement(float deltaTime)
        {
            tempCurrentPosition = CacheTransform.position;
            tempMoveVelocity = Vector3.zero;
            tempMoveDirection = Vector3.zero;
            tempTargetDistance = -1f;
            WaterCheck();

            bool isGrounded = CacheOpenCharacterController.isGrounded;
            bool isAirborne = !isGrounded && !isUnderWater && !IsFlying && airborneElapsed >= airborneDelay;

            // Update airborne elasped
            if (CacheOpenCharacterController.isGrounded)
                airborneElapsed = 0f;
            else
                airborneElapsed += deltaTime;

            if (HasNavPaths)
            {
                // Set `tempTargetPosition` and `tempCurrentPosition`
                tempTargetPosition = navPaths.Peek();
                tempMoveDirection = (tempTargetPosition - tempCurrentPosition).normalized;
                tempTargetDistance = Vector3.Distance(tempTargetPosition.GetXZ(), tempCurrentPosition.GetXZ());
                if (tempTargetDistance < StoppingDistance)
                {
                    navPaths.Dequeue();
                    if (!HasNavPaths)
                    {
                        StopMove();
                        tempMoveDirection = Vector3.zero;
                    }
                }
                else
                {
                    // Turn character to destination
                    yRotation = Quaternion.LookRotation(tempMoveDirection).eulerAngles.y;
                }
            }
            else if (clientTargetPosition.HasValue)
            {
                tempTargetPosition = clientTargetPosition.Value;
                tempMoveDirection = (tempTargetPosition - tempCurrentPosition).normalized;
                tempTargetDistance = Vector3.Distance(tempTargetPosition.GetXZ(), tempCurrentPosition.GetXZ());
                if (tempTargetDistance < StoppingDistance)
                {
                    clientTargetPosition = null;
                    StopMove();
                    tempMoveDirection = Vector3.zero;
                }
            }
            else if (tempInputDirection.sqrMagnitude > 0f)
            {
                tempMoveDirection = tempInputDirection.normalized;
                tempTargetPosition = tempCurrentPosition + tempMoveDirection;
            }

            if (!Entity.CanMove())
            {
                tempMoveDirection = Vector3.zero;
                isJumping = false;
                applyingJumpForce = false;
            }

            // Prepare movement speed
            tempEntityMoveSpeed = applyingJumpForce ? 0f : Entity.GetMoveSpeed();
            tempCurrentMoveSpeed = tempEntityMoveSpeed;

            // Calculate vertical velocity by gravity
            if (!isGrounded && !isUnderWater && !IsFlying)
            {
                if (!useRootMotionForFall)
                    tempVerticalVelocity = Mathf.MoveTowards(tempVerticalVelocity, -maxFallVelocity, gravity * deltaTime);
                else
                    tempVerticalVelocity = 0f;
            }
            else
            {
                // Not falling set verical velocity to 0
                tempVerticalVelocity = 0f;
            }

            // Jumping 
            if (acceptedJump || (pauseMovementCountDown <= 0f && isGrounded && isJumping && !CacheOpenCharacterController.startedSlide))
            {
                currentInput = this.SetInputJump(currentInput);
                sendingJump = true;
                airborneElapsed = airborneDelay;
                Entity.PlayJumpAnimation();
                applyingJumpForce = true;
                applyJumpForceCountDown = 0f;
                switch (applyJumpForceMode)
                {
                    case ApplyJumpForceMode.ApplyAfterFixedDuration:
                        applyJumpForceCountDown = applyJumpForceFixedDuration;
                        break;
                    case ApplyJumpForceMode.ApplyAfterJumpDuration:
                        if (Entity.Model is IJumppableModel)
                            applyJumpForceCountDown = (Entity.Model as IJumppableModel).GetJumpAnimationDuration();
                        break;
                }
            }

            if (applyingJumpForce && !IsFlying)
            {
                applyJumpForceCountDown -= Time.deltaTime;
                if (applyJumpForceCountDown <= 0f)
                {
                    applyingJumpForce = false;
                    if (!useRootMotionForJump)
                        tempVerticalVelocity = CalculateJumpVerticalSpeed();
                }
            }

            // Updating horizontal movement (WASD inputs)
            if (!isAirborne)
            {
                velocityBeforeAirborne = Vector3.zero;
            }
            if (pauseMovementCountDown <= 0f && tempMoveDirection.sqrMagnitude > 0f && (!isAirborne || !doNotChangeVelocityWhileAirborne))
            {
                // Calculate only horizontal move direction
                tempHorizontalMoveDirection = tempMoveDirection;
                tempHorizontalMoveDirection.y = 0;
                tempHorizontalMoveDirection.Normalize();

                // If character move backward
                if (Vector3.Angle(tempHorizontalMoveDirection, CacheTransform.forward) > 120)
                    tempCurrentMoveSpeed *= backwardMoveSpeedRate;

                // NOTE: `tempTargetPosition` and `tempCurrentPosition` were set above
                tempSqrMagnitude = (tempTargetPosition - tempCurrentPosition).sqrMagnitude;
                tempPredictPosition = tempCurrentPosition + (tempHorizontalMoveDirection * tempCurrentMoveSpeed * deltaTime);
                tempPredictSqrMagnitude = (tempPredictPosition - tempCurrentPosition).sqrMagnitude;
                if (HasNavPaths)
                {
                    // Check `tempSqrMagnitude` against the `tempPredictSqrMagnitude`
                    // if `tempPredictSqrMagnitude` is greater than `tempSqrMagnitude`,
                    // rigidbody will reaching target and character is moving pass it,
                    // so adjust move speed by distance and time (with physic formula: v=s/t)
                    if (tempPredictSqrMagnitude >= tempSqrMagnitude)
                        tempCurrentMoveSpeed *= tempTargetDistance / deltaTime / tempCurrentMoveSpeed;
                }
                tempMoveVelocity = tempHorizontalMoveDirection * tempCurrentMoveSpeed;
                velocityBeforeAirborne = tempMoveVelocity;
                // Set inputs
                currentInput = this.SetInputMovementState(currentInput, tempMovementState);
                if (HasNavPaths)
                {
                    currentInput = this.SetInputPosition(currentInput, tempTargetPosition);
                    currentInput = this.SetInputIsKeyMovement(currentInput, false);
                }
                else
                {
                    currentInput = this.SetInputPosition(currentInput, tempPredictPosition);
                    currentInput = this.SetInputIsKeyMovement(currentInput, true);
                }
            }
            // Moves by velocity before airborne
            if (isGrounded && previouslyAirborne)
            {
                pauseMovementCountDown = landedPauseMovementDuration;
            }
            else if (isAirborne && doNotChangeVelocityWhileAirborne)
            {
                tempMoveVelocity = velocityBeforeAirborne;
            }
            else
            {
                if (pauseMovementCountDown > 0f)
                    pauseMovementCountDown -= deltaTime;
            }
            // Updating vertical movement (Fall, WASD inputs under water)
            if (isUnderWater)
            {
                tempCurrentMoveSpeed = tempEntityMoveSpeed;
                // Move up to surface while under water
                if (autoSwimToSurface || Mathf.Abs(tempMoveDirection.y) > 0)
                {
                    if (autoSwimToSurface)
                        tempMoveDirection.y = 1f;
                    tempTargetPosition = Vector3.up * (waterCollider.bounds.max.y - (CacheCapsuleCollider.bounds.size.y * underWaterThreshold));
                    tempCurrentPosition = Vector3.up * CacheTransform.position.y;
                    tempTargetDistance = Vector3.Distance(tempTargetPosition, tempCurrentPosition);
                    tempSqrMagnitude = (tempTargetPosition - tempCurrentPosition).sqrMagnitude;
                    tempPredictPosition = tempCurrentPosition + (Vector3.up * tempMoveDirection.y * tempCurrentMoveSpeed * deltaTime);
                    tempPredictSqrMagnitude = (tempPredictPosition - tempCurrentPosition).sqrMagnitude;
                    // Check `tempSqrMagnitude` against the `tempPredictSqrMagnitude`
                    // if `tempPredictSqrMagnitude` is greater than `tempSqrMagnitude`,
                    // rigidbody will reaching target and character is moving pass it,
                    // so adjust move speed by distance and time (with physic formula: v=s/t)
                    if (tempPredictSqrMagnitude >= tempSqrMagnitude)
                        tempCurrentMoveSpeed *= tempTargetDistance / deltaTime / tempCurrentMoveSpeed;
                    // Swim up to surface
                    if (tempCurrentMoveSpeed < 0.01f)
                        tempMoveDirection.y = 0f;
                    tempMoveVelocity.y = tempMoveDirection.y * tempCurrentMoveSpeed;
                    if (!HasNavPaths)
                        currentInput = this.SetInputYPosition(currentInput, tempPredictPosition.y);
                }
            }
            else if(canFly && IsFlying)
            {
                if(isGrounded)
                {
                    CallNetFunction(Cmd_TidyToggleFly, FunctionReceivers.All, false);
                    return;
                }

                if (InputManager.GetButton("Jump"))
                    tempMoveDirection.y = 1f;

                tempMoveVelocity.y = tempMoveDirection.y * tempCurrentMoveSpeed;

                if(useSmoothVelocity)
                if (tempMoveDirection != Vector3.zero)
                {
                    tempLastMoveVelocity = tempMoveVelocity;
                }
                else
                {
                    tempMoveVelocity = new Vector3(Mathf.Lerp(tempLastMoveVelocity.x, 0, 1 * Time.deltaTime), Mathf.Lerp(tempLastMoveVelocity.y, tempMoveVelocity.y, 1 * Time.deltaTime), Mathf.Lerp(tempLastMoveVelocity.z, 0, 1 * Time.deltaTime));
                    tempLastMoveVelocity = tempMoveVelocity;
                    velocityBeforeAirborne = tempLastMoveVelocity;
                }
            }
            else
            {
                // Update velocity while not under water
                tempMoveVelocity.y = tempVerticalVelocity;

                if (isGrounded)
                    tempLastMoveVelocity = Vector3.zero;
            }

            // Don't applies velocity while using root motion
            if ((isGrounded && useRootMotionForMovement) ||
                (isAirborne && useRootMotionForAirMovement) ||
                (isUnderWater && useRootMotionUnderWater))
            {
                tempMoveVelocity.x = 0;
                tempMoveVelocity.z = 0;
            }

            platformMotion = Vector3.zero;
            if (isGrounded && !isUnderWater)
            {
                // Apply platform motion
                if (groundedTransform != null && deltaTime > 0.0f)
                {
                    Vector3 newGroundedPosition = groundedTransform.TransformPoint(groundedLocalPosition);
                    platformMotion = (newGroundedPosition - oldGroundedPosition) / deltaTime;
                    platformMotion.y = 0;
                    oldGroundedPosition = newGroundedPosition;
                }
            }

            collisionFlags = CacheOpenCharacterController.Move((tempMoveVelocity + platformMotion) * deltaTime);

            if (targetYRotation.HasValue)
            {
                yRotateLerpTime += deltaTime;
                float lerpTimeRate = yRotateLerpTime / yRotateLerpDuration;
                yRotation = Mathf.LerpAngle(yRotation, targetYRotation.Value, lerpTimeRate);
                if (lerpTimeRate >= 1f)
                    targetYRotation = null;
            }
            UpdateRotation();
            currentInput = this.SetInputRotation(currentInput, CacheTransform.rotation);
            isJumping = false;
            acceptedJump = false;
            previouslyGrounded = isGrounded;
            previouslyAirborne = isAirborne;
        }

        private void UpdateRotation()
        {
            CacheTransform.eulerAngles = new Vector3(0f, yRotation, 0f);
        }

        private void SetMovePaths(Vector3 position, bool useNavMesh)
        {
            if (useNavMesh)
            {
                NavMeshPath navPath = new NavMeshPath();
                NavMeshHit navHit;
                if (NavMesh.SamplePosition(position, out navHit, 5f, NavMesh.AllAreas) &&
                    NavMesh.CalculatePath(CacheTransform.position, navHit.position, NavMesh.AllAreas, navPath))
                {
                    navPaths = new Queue<Vector3>(navPath.corners);
                    // Dequeue first path it's not require for future movement
                    navPaths.Dequeue();
                }
            }
            else
            {
                // If not use nav mesh, just move to position by direction
                navPaths = new Queue<Vector3>();
                navPaths.Enqueue(position);
            }
        }

        private float CalculateJumpVerticalSpeed()
        {
            // From the jump height and gravity we deduce the upwards speed 
            // for the character to reach at the apex.
            return Mathf.Sqrt(2f * jumpHeight * gravity);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.gameObject.layer == PhysicLayers.Water)
            {
                // Enter water
                waterCollider = other;
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.gameObject.layer == PhysicLayers.Water)
            {
                // Exit water
                waterCollider = null;
            }
        }

        private void OnCharacterControllerCollision(OpenCharacterController.CollisionInfo info)
        {
            if (CacheOpenCharacterController.isGrounded)
            {
                groundedTransform = info.collider.transform;
                oldGroundedPosition = info.point;
                groundedLocalPosition = groundedTransform.InverseTransformPoint(oldGroundedPosition);
            }
        }

        public void HandleSyncTransformAtClient(MessageHandlerData messageHandler)
        {
            if (IsServer)
            {
                // Don't read and apply transform, because it was done at server
                return;
            }
            Vector3 position;
            float yAngle;
            long timestamp;
            messageHandler.Reader.ReadSyncTransformMessage3D(out position, out yAngle, out timestamp);
            if (acceptedPositionTimestamp < timestamp)
            {
                acceptedPositionTimestamp = timestamp;
                // Snap character to the position if character is too far from the position
                if (Vector3.Distance(position, CacheTransform.position) >= snapThreshold)
                {
                    if (Entity.MovementSecure == MovementSecure.ServerAuthoritative || !IsOwnerClient)
                    {
                        yRotation = yAngle;
                        CacheOpenCharacterController.SetPosition(position, false);
                    }
                }
                else if (!IsOwnerClient)
                {
                    targetYRotation = yAngle;
                    yRotateLerpTime = 0;
                    yRotateLerpDuration = serverSyncTransformInterval;
                    if (Vector3.Distance(position.GetXZ(), CacheTransform.position.GetXZ()) > moveThreshold)
                    {
                        clientTargetPosition = position;
                    }
                }
            }
        }

        public void HandleTeleportAtClient(MessageHandlerData messageHandler)
        {
            if (IsServer)
            {
                // Don't read and apply transform, because it was done (this is both owner client and server)
                return;
            }
            Vector3 position;
            float yAngle;
            long timestamp;
            messageHandler.Reader.ReadTeleportMessage3D(out position, out yAngle, out timestamp);
            if (acceptedPositionTimestamp < timestamp)
            {
                acceptedPositionTimestamp = timestamp;
                yRotation = yAngle;
                OnTeleport(position);
            }
        }

        public void HandleJumpAtClient(MessageHandlerData messageHandler)
        {
            if (IsOwnerClient || IsServer)
            {
                // Don't read and apply transform, because it was done
                return;
            }
            long timestamp;
            messageHandler.Reader.ReadJumpMessage(out timestamp);
            if (acceptedJumpTimestamp < timestamp)
            {
                acceptedJumpTimestamp = timestamp;
                acceptedJump = true;
            }
        }

        public void HandleMovementInputAtServer(MessageHandlerData messageHandler)
        {
            if (IsOwnerClient)
            {
                // Don't read and apply inputs, because it was done (this is both owner client and server)
                return;
            }
            if (Entity.MovementSecure == MovementSecure.NotSecure)
            {
                // Movement handling at client, so don't read movement inputs from client (but have to read transform)
                return;
            }
            if (!Entity.CanMove())
                return;
            InputState inputState;
            MovementState movementState;
            Vector3 position;
            float yAngle;
            long timestamp;
            messageHandler.Reader.ReadMovementInputMessage3D(out inputState, out movementState, out position, out yAngle, out timestamp);
            if (acceptedPositionTimestamp < timestamp)
            {
                acceptedPositionTimestamp = timestamp;
                navPaths = null;
                tempMovementState = movementState;
                clientTargetPosition = null;
                targetYRotation = null;
                if (inputState.HasFlag(InputState.PositionChanged))
                {
                    if (inputState.HasFlag(InputState.IsKeyMovement))
                    {
                        clientTargetPosition = position;
                    }
                    else
                    {
                        SetMovePaths(position, true);
                    }
                }
                if (inputState.HasFlag(InputState.RotationChanged))
                {
                    if (IsClient)
                    {
                        targetYRotation = yAngle;
                        yRotateLerpTime = 0;
                        yRotateLerpDuration = clientSendInputsInterval;
                    }
                    else
                    {
                        yRotation = yAngle;
                    }
                }
                isJumping = inputState.HasFlag(InputState.IsJump);
            }
        }

        public void HandleSyncTransformAtServer(MessageHandlerData messageHandler)
        {
            if (IsOwnerClient)
            {
                // Don't read and apply transform, because it was done (this is both owner client and server)
                return;
            }
            if (Entity.MovementSecure == MovementSecure.ServerAuthoritative)
            {
                // Movement handling at server, so don't read sync transform from client
                return;
            }
            Vector3 position;
            float yAngle;
            long timestamp;
            messageHandler.Reader.ReadSyncTransformMessage3D(out position, out yAngle, out timestamp);
            if (acceptedPositionTimestamp < timestamp)
            {
                acceptedPositionTimestamp = timestamp;
                yRotation = yAngle;
                if (Vector3.Distance(position.GetXZ(), CacheTransform.position.GetXZ()) > moveThreshold)
                {
                    if (!IsClient)
                    {
                        // If it's server only (not a host), set position follows the client immediately
                        CacheOpenCharacterController.SetPosition(position, false);
                    }
                    else
                    {
                        // It's both server and client, translate position
                        SetMovePaths(position, false);
                    }
                }
            }
        }

        public void HandleStopMoveAtServer(MessageHandlerData messageHandler)
        {
            if (IsOwnerClient)
            {
                // Don't read and apply inputs, because it was done (this is both owner client and server)
                return;
            }
            if (Entity.MovementSecure == MovementSecure.NotSecure)
            {
                // Movement handling at client, so don't read movement inputs from client (but have to read transform)
                return;
            }
            long timestamp;
            messageHandler.Reader.ReadStopMoveMessage(out timestamp);
            if (acceptedPositionTimestamp < timestamp)
            {
                acceptedPositionTimestamp = timestamp;
                StopMoveFunction();
            }
        }

        public void HandleJumpAtServer(MessageHandlerData messageHandler)
        {
            if (IsOwnerClient)
            {
                // Don't read and apply transform, because it was done (this is both owner client and server)
                return;
            }
            if (Entity.MovementSecure == MovementSecure.ServerAuthoritative)
            {
                // Movement handling at server, so don't read sync transform from client
                return;
            }
            long timestamp;
            messageHandler.Reader.ReadJumpMessage(out timestamp);
            if (acceptedJumpTimestamp < timestamp)
            {
                acceptedJumpTimestamp = timestamp;
                acceptedJump = true;
            }
        }

        private void OnTeleport(Vector3 position)
        {
            airborneElapsed = 0;
            tempVerticalVelocity = 0;
            framesAfterTeleported = FRAME_BUFFER;
            teleportedPosition = position;
        }

#if UNITY_EDITOR
        [ContextMenu("Applies Collider Settings To Controller")]
        public void AppliesColliderSettingsToController()
        {
            CapsuleCollider collider = gameObject.GetOrAddComponent<CapsuleCollider>();
            float radius = collider.radius;
            float height = collider.height;
            Vector3 center = collider.center;
            // Prepare open character controller
            OpenCharacterController controller = gameObject.GetOrAddComponent<OpenCharacterController>();
            controller.SetRadiusHeightAndCenter(radius, height, center, true, true);
        }
#endif
    }
}
