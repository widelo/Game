// First-person CharacterController movement + mouse look.
// Old Input Manager only (ProjectSettings activeInputHandler=0): UnityEngine.Input, never the new Input System.
// Deliberately knows NOTHING about levels - spawning is done by Game.Debug.LevelDebugController.
// v2 moveset: faster walk, jump, crouch, and a sprint-into-crouch slide. No head bob (motion sickness + cheap).
using UnityEngine;

namespace Game.Player
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class FirstPersonController : MonoBehaviour
    {
        [Header("Look")]
        [Tooltip("Child transform that holds the camera. If null, the first child Camera is used.")]
        public Transform cameraPivot;
        public float mouseSensitivity = 2.0f;
        public float maxPitch = 85f;

        [Header("Move")]
        public float walkSpeed = 5.0f;
        public float sprintSpeed = 8.0f;
        public float crouchSpeed = 2.5f;
        [Tooltip("m/s^2 on the ground. walkSpeed/0.1s so direction changes bite immediately.")]
        public float groundAccel = 50f;
        [Tooltip("m/s^2 in the air. walkSpeed/0.4s - air control exists but is sluggish.")]
        public float airAccel = 12.5f;
        public float gravity = -22f;

        [Header("Jump")]
        public float jumpHeight = 1.0f;
        [Tooltip("Grace window after leaving the ground in which a jump still registers.")]
        public float coyoteTime = 0.1f;

        [Header("Crouch")]
        public float standHeight = 1.8f;
        public float crouchHeight = 1.0f;
        public float camStandY = 1.6f;
        public float camCrouchY = 0.9f;
        [Tooltip("Capsule/camera height blend rate, per second.")]
        public float crouchLerpSpeed = 10f;

        [Header("Slide")]
        [Tooltip("Minimum horizontal speed required to start a slide.")]
        public float slideEntrySpeed = 6.0f;
        public float slideSpeedBoost = 1.15f;
        public float slideFriction = 6.0f;
        [Tooltip("Fraction of ground accel usable for steering mid-slide.")]
        public float slideSteer = 0.15f;
        public float slideCooldown = 0.3f;
        public float slideCamDip = 0.1f;

        CharacterController _cc;
        Vector3 _horizVel;
        float _yaw, _pitch, _vertVel, _coyote, _slideCdLeft, _camY;
        bool _locked, _crouching, _sliding, _sprinting, _grounded;

        public Vector3 Velocity => new Vector3(_horizVel.x, _vertVel, _horizVel.z);
        public bool IsGrounded => _grounded;
        public bool IsCrouching => _crouching;
        public bool IsSliding => _sliding;
        public bool IsSprinting => _sprinting;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _cc.height = standHeight;
            _cc.center = new Vector3(0f, standHeight * 0.5f, 0f);
            _cc.stepOffset = 0.4f;
            _cc.slopeLimit = 45f;
            _cc.skinWidth = 0.08f;

            if (cameraPivot == null)
            {
                var cam = GetComponentInChildren<Camera>();
                if (cam != null) cameraPivot = cam.transform;
            }
            _yaw = transform.eulerAngles.y;
            _camY = camStandY;
            if (cameraPivot != null)
            {
                _pitch = cameraPivot.localEulerAngles.x;
                _camY = cameraPivot.localPosition.y;
            }
        }

        void Start() { SetLocked(true); }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) SetLocked(!_locked);
            if (_locked) Look();
            Move();
            ResolveCapsule();
        }

        void Look()
        {
            _yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
            _pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;
            _pitch = Mathf.Clamp(_pitch, -maxPitch, maxPitch);
            transform.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            if (cameraPivot != null) cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        void Move()
        {
            float dt = Time.deltaTime;
            _grounded = _cc.isGrounded;
            if (_slideCdLeft > 0f) _slideCdLeft -= dt;

            if (_grounded)
            {
                _coyote = coyoteTime;
                if (_vertVel < 0f) _vertVel = -2f; // keep the capsule pinned so isGrounded stays true
            }
            else _coyote -= dt;

            Vector3 wish = transform.right * Input.GetAxisRaw("Horizontal")
                         + transform.forward * Input.GetAxisRaw("Vertical");
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            wish.y = 0f;

            bool crouchHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);
            bool crouchDown = Input.GetKeyDown(KeyCode.LeftControl) || Input.GetKeyDown(KeyCode.C);
            _sprinting = Input.GetKey(KeyCode.LeftShift) && !_crouching && !_sliding;

            if (!_sliding && crouchDown && _grounded && _slideCdLeft <= 0f
                && Input.GetKey(KeyCode.LeftShift) && _horizVel.magnitude >= slideEntrySpeed)
                StartSlide(wish);

            if (_sliding) UpdateSlide(wish, crouchHeld, dt);
            else UpdateWalk(wish, crouchHeld, dt);

            if (Input.GetKeyDown(KeyCode.Space) && _coyote > 0f && !_sliding && CanStand())
            {
                _vertVel = Mathf.Sqrt(2f * Mathf.Abs(gravity) * jumpHeight);
                _coyote = 0f;
                _crouching = false;
            }
            else _vertVel += gravity * dt;

            Vector3 motion = _horizVel;
            motion.y = _vertVel;
            _cc.Move(motion * dt);
        }

        void UpdateWalk(Vector3 wish, bool crouchHeld, float dt)
        {
            _crouching = crouchHeld || (_crouching && !CanStand());
            float target = _crouching ? crouchSpeed : (_sprinting ? sprintSpeed : walkSpeed);
            float accel = _grounded ? groundAccel : airAccel;
            _horizVel = Vector3.MoveTowards(_horizVel, wish * target, accel * dt);
        }

        void StartSlide(Vector3 wish)
        {
            Vector3 dir = wish.sqrMagnitude > 0.01f ? wish.normalized : _horizVel.normalized;
            if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
            _horizVel = dir * (_horizVel.magnitude * slideSpeedBoost);
            _sliding = true;
            _crouching = true;
            _sprinting = false;
        }

        void UpdateSlide(Vector3 wish, bool crouchHeld, float dt)
        {
            float speed = Mathf.Max(0f, _horizVel.magnitude - slideFriction * dt);
            Vector3 dir = _horizVel.sqrMagnitude > 1e-4f ? _horizVel.normalized : transform.forward;
            dir = (dir + wish * slideSteer).normalized;   // tiny steering, never a speed gain
            _horizVel = dir * speed;

            if (speed < crouchSpeed || !crouchHeld || !_grounded) EndSlide();
        }

        void EndSlide()
        {
            _sliding = false;
            _slideCdLeft = slideCooldown;
            if (!CanStand()) _crouching = true;
        }

        /// <summary>Capsule height/centre and camera height follow the crouch state. Feet stay planted (center.y = height/2).</summary>
        void ResolveCapsule()
        {
            float dt = Time.deltaTime;
            float wantH = _crouching ? crouchHeight : standHeight;
            float h = Mathf.MoveTowards(_cc.height, wantH, (standHeight - crouchHeight) * crouchLerpSpeed * dt);
            _cc.height = h;
            _cc.center = new Vector3(0f, h * 0.5f, 0f);

            float wantCam = (_crouching ? camCrouchY : camStandY) - (_sliding ? slideCamDip : 0f);
            _camY = Mathf.Lerp(_camY, wantCam, 1f - Mathf.Exp(-crouchLerpSpeed * dt));
            if (cameraPivot != null)
            {
                var lp = cameraPivot.localPosition;
                cameraPivot.localPosition = new Vector3(lp.x, _camY, lp.z);
            }
        }

        /// <summary>True when there is headroom for the full standing capsule. The controller is disabled
        /// across the cast because a sphere that starts inside its own collider always reports a hit.</summary>
        bool CanStand()
        {
            if (_cc.height >= standHeight - 0.01f) return true;
            float r = Mathf.Max(0.01f, _cc.radius - 0.01f);
            Vector3 sphere = transform.position + Vector3.up * (_cc.height - r);
            float needed = standHeight - _cc.height + _cc.skinWidth;
            bool wasEnabled = _cc.enabled;
            _cc.enabled = false;
            bool blocked = Physics.SphereCast(sphere, r, Vector3.up, out _, needed,
                                              ~0, QueryTriggerInteraction.Ignore);
            _cc.enabled = wasEnabled;
            return !blocked;
        }

        /// <summary>Cursor capture. Public so a pause menu or the debug HUD can release the mouse.</summary>
        public void SetLocked(bool locked)
        {
            _locked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        public bool IsLocked => _locked;
    }
}
