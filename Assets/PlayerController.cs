using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Translates Move input into camera-relative force on the marble, with
/// ASYMMETRIC authority between ground and air:
///
///   GROUNDED — full control. Input is split, relative to travel direction:
///     THROTTLE  (along travel)  — capped by maxControlledSpeed when
///                                 accelerating; braking is always allowed.
///     STEERING  (perp to travel) — always applied.
///
///   AIRBORNE — the "letting go" moment. NO throttle, NO acceleration, NO
///     speed cap. Only a faint steering nudge.
///
/// GRAVITY — applied entirely by this script (Approach B). The Rigidbody's
///   own Use Gravity MUST be turned OFF; this script is then the single
///   source of truth for the marble's gravity, so it can be tuned strong
///   for a snappy marble-like fall and modulated later for cinematic moments.
///
/// Ground state uses a downward spherecast plus a coyote-style grace buffer.
///
/// RESET — R returns the marble to its respawn point and zeroes all motion.
///   Respawn point is checkpoint-ready via SetRespawnPoint().
///
/// "Forward" is always camera-relative, since a sphere has no facing.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(SphereCollider))]
public class PlayerController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The camera that defines 'forward'. Usually the Main Camera under CameraRig.")]
    [SerializeField] private Transform cameraTransform;

    [Header("Throttle — ground acceleration along travel")]
    [SerializeField] private float moveForce = 25f;

    [Tooltip("Horizontal speed (m/s) above which the player can no longer "
           + "ACCELERATE on the ground. Steering and braking still work.")]
    [SerializeField] private float maxControlledSpeed = 12f;

    [Header("Steering — ground turning force, perpendicular to travel")]
    [SerializeField] private float steerForce = 22f;

    [Header("Air control — the 'letting go' moment")]
    [Tooltip("Faint sideways nudge allowed mid-air. Deliberately small. "
           + "There is NO air throttle — the player cannot accelerate airborne.")]
    [SerializeField] private float airSteerForce = 4f;

    [Header("Gravity (this script is the ONLY source — turn OFF Use Gravity " +
            "on the Rigidbody)")]
    [Tooltip("Downward acceleration in units/sec^2. Unity's default is 9.81 "
           + "(real earth gravity). A game-scale marble usually wants MORE — "
           + "try 20-30 so it drops with weight instead of hanging. Applied "
           + "as acceleration, so it is correctly independent of mass.")]
    [SerializeField] private float gravityStrength = 24f;

    [Header("Ground check")]
    [Tooltip("Layers treated as ground. Set to your 'ground' layer.")]
    [SerializeField] private LayerMask groundMask;

    [Tooltip("How far below the marble's surface the spherecast probes.")]
    [SerializeField] private float groundProbeDistance = 0.15f;

    [Tooltip("Coyote grace: the marble is only AIRBORNE once the ground probe "
           + "has missed continuously for this long. Swallows small bounces.")]
    [SerializeField] private float groundGraceWindow = 0.12f;

    [Header("Tuning")]
    [Tooltip("Below this speed the travel direction is unreliable; grounded "
           + "input is applied wholesale without throttle/steer decomposition.")]
    [SerializeField] private float decomposeMinSpeed = 0.5f;

    // --- Input ---
    private PlayerControls controls;
    private Vector2 moveInput;
    private bool resetRequested;

    // --- Components ---
    private Rigidbody rb;
    private SphereCollider sphere;

    // --- Ground state ---
    private float lastGroundedTime = -999f;
    private bool isGrounded;

    // --- Respawn ---
    private Vector3 respawnPosition;
    private Quaternion respawnRotation;

    private void Awake()
    {
        rb     = GetComponent<Rigidbody>();
        sphere = GetComponent<SphereCollider>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        // Safety: this script owns gravity. If Use Gravity was left on, the
        // marble would fall under BOTH. Force it off and warn, so the
        // single-source-of-truth guarantee actually holds.
        if (rb.useGravity)
        {
            rb.useGravity = false;
            Debug.LogWarning("PlayerController: Rigidbody 'Use Gravity' was on. "
                + "Disabled it — this script is the sole gravity source.", this);
        }

        controls = new PlayerControls();
    }

    private void Start()
    {
        respawnPosition = transform.position;
        respawnRotation = transform.rotation;
    }

    private void OnEnable()  => controls.Marble.Enable();
    private void OnDisable() => controls.Marble.Disable();

    private void Update()
    {
        moveInput = controls.Marble.Move.ReadValue<Vector2>();

        if (controls.Marble.Reset.WasPressedThisFrame())
            resetRequested = true;
    }

    private void FixedUpdate()
    {
        if (resetRequested)
        {
            DoReset();
            resetRequested = false;
            return;
        }

        UpdateGroundState();
        ApplyGravity();

        if (cameraTransform == null) return;
        if (moveInput.sqrMagnitude < 0.0001f) return;

        Vector3 desiredDir = CameraRelativeDirection(moveInput);

        if (isGrounded) ApplyGroundForce(desiredDir);
        else            ApplyAirForce(desiredDir);
    }

    /// <summary>
    /// The marble's entire gravity. ForceMode.Acceleration ignores mass, so
    /// gravityStrength is a true acceleration (units/sec^2) — physically
    /// correct, and changing Mass never alters the fall rate.
    /// </summary>
    private void ApplyGravity()
    {
        rb.AddForce(Vector3.down * gravityStrength, ForceMode.Acceleration);
    }

    private void DoReset()
    {
        rb.linearVelocity  = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        rb.MovePosition(respawnPosition);
        rb.MoveRotation(respawnRotation);

        lastGroundedTime = -999f;
    }

    public void SetRespawnPoint(Vector3 position, Quaternion rotation)
    {
        respawnPosition = position;
        respawnRotation = rotation;
    }

    private void UpdateGroundState()
    {
        float radius = sphere.radius * MaxAbsComponent(transform.lossyScale);
        Vector3 origin = transform.position;
        float castDistance = groundProbeDistance + 0.05f;

        bool hit = Physics.SphereCast(
            origin, radius * 0.95f, Vector3.down,
            out _, castDistance, groundMask, QueryTriggerInteraction.Ignore);

        if (hit)
            lastGroundedTime = Time.time;

        isGrounded = (Time.time - lastGroundedTime) <= groundGraceWindow;
    }

    private void ApplyGroundForce(Vector3 desiredDir)
    {
        Vector3 hVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        float speed = hVel.magnitude;

        if (speed < decomposeMinSpeed)
        {
            rb.AddForce(desiredDir * moveForce, ForceMode.Force);
            return;
        }

        Vector3 vHat = hVel / speed;

        float along = Vector3.Dot(desiredDir, vHat);
        Vector3 throttleDir = vHat * along;
        Vector3 steerDir    = desiredDir - throttleDir;

        Vector3 force = steerDir * steerForce;

        if (along < 0f)
            force += throttleDir * moveForce;
        else if (speed < maxControlledSpeed)
            force += throttleDir * moveForce;

        rb.AddForce(force, ForceMode.Force);
    }

    private void ApplyAirForce(Vector3 desiredDir)
    {
        Vector3 hVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        float speed = hVel.magnitude;

        if (speed < decomposeMinSpeed) return;

        Vector3 vHat = hVel / speed;
        float along = Vector3.Dot(desiredDir, vHat);
        Vector3 steerDir = desiredDir - vHat * along;

        rb.AddForce(steerDir * airSteerForce, ForceMode.Force);
    }

    private Vector3 CameraRelativeDirection(Vector2 input)
    {
        Vector3 forward = cameraTransform.forward;
        Vector3 right   = cameraTransform.right;

        forward.y = 0f;
        right.y   = 0f;
        forward.Normalize();
        right.Normalize();

        return (forward * input.y + right * input.x).normalized;
    }

    private static float MaxAbsComponent(Vector3 v)
        => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    private void OnDrawGizmosSelected()
    {
        SphereCollider sc = GetComponent<SphereCollider>();
        if (sc == null) return;

        float radius = sc.radius * MaxAbsComponent(transform.lossyScale);
        Gizmos.color = isGrounded ? Color.green : Color.red;
        Vector3 probeEnd = transform.position
                         + Vector3.down * (groundProbeDistance + 0.05f);
        Gizmos.DrawWireSphere(probeEnd, radius * 0.95f);
    }
}