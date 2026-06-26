using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Translates Move input into camera-relative force on the marble, with
/// ASYMMETRIC authority between ground and air:
///
///   GROUNDED — full control. Input is split, relative to travel direction:
///     THROTTLE  (along travel)  — capped by maxControlledSpeed when
///                                 accelerating; braking is always allowed.
///     STEERING  (perp to travel) — always applied.
///   AIRBORNE — the "letting go" moment. Only FAINT throttle and FAINT steer.
///              No speed cap. Bad jumps cannot be rescued — only nudged.
///
/// BRAKE (Spacebar, ground-only) — hold to decelerate at a constant rate
///   (brakeForce, in units/sec²). Brake overrides throttle so holding Space
///   and W simultaneously doesn't fight; steering is preserved so the player
///   can still aim while braking. Per-step velocity change is clamped so the
///   brake never overshoots into reverse.
///
/// LURCH (Shift, ground-only) — one-shot ADDITIVE velocity boost. Tap Shift
///   and the marble's horizontal speed increases by lurchSpeedBoost in the
///   current direction of travel, clamped to lurchMaxSpeed. Vertical
///   velocity is preserved so gravity is unaffected. A cooldown gates the
///   re-use; at or above the ceiling, the lurch fizzles entirely. Additive
///   means it cannot compound by chaining — and the ceiling is hard, not
///   a soft trigger threshold.
///
/// GRAVITY — applied entirely by this script. Rigidbody 'Use Gravity' is
///   forced off in Awake so this script is the single source of truth.
///
/// GROUND CHECK — downward spherecast + coyote grace buffer.
///
/// ANTI-STUCK ESCAPE — wedged in a sharp corner, nearly stationary, with
///   input held: apply an escape force along summed contact normals (open
///   space) blended with player input.
///
/// RESET — R returns the marble to its respawn point; checkpoint-ready.
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
    [Tooltip("Faint sideways nudge allowed mid-air. Deliberately small.")]
    [SerializeField] private float airSteerForce = 4f;

    [Tooltip("Faint forward/back nudge allowed mid-air. Positive input forward "
           + "of travel = slight acceleration; against travel = slight braking. "
           + "There is NO air speed cap — bad jumps still cannot be rescued.")]
    [SerializeField] private float airThrottleForce = 4f;

    [Header("Lurch (Shift)")]
    [Tooltip("Speed added (in units/sec) to the marble's horizontal velocity, "
           + "in the current direction of travel. Additive so chaining cannot "
           + "compound — every lurch is at most this much added speed.")]
    [SerializeField] private float lurchSpeedBoost = 10f;

    [Tooltip("Hard horizontal-speed ceiling for the lurch. If the marble is "
           + "already at or above this speed (e.g. from a long descent) the "
           + "lurch FIZZLES — the cooldown is NOT consumed. Below the ceiling, "
           + "the post-lurch speed is clamped to this value.")]
    [SerializeField] private float lurchMaxSpeed = 30f;

    [Tooltip("Seconds the player must wait between lurches. The lurch should "
           + "feel like a deliberate commit, not a held button.")]
    [SerializeField] private float lurchCooldown = 1.5f;

    [Tooltip("Minimum horizontal speed required to lurch. Below this there is "
           + "no momentum to amplify — the lurch fizzles and the cooldown is "
           + "NOT consumed. Forces the player to set up speed before lurching.")]
    [SerializeField] private float lurchMinSpeed = 0.5f;

    [Header("Brake (Spacebar)")]
    [Tooltip("Constant deceleration (units/sec^2) while Brake is held on the "
           + "ground. Significantly higher than moveForce so braking always "
           + "wins over throttle. Time to stop scales with speed: brake = 50, "
           + "speed = 30 stops in ~0.6s; speed = 12 stops in ~0.24s.")]
    [SerializeField] private float brakeForce = 50f;

    [Header("Gravity (this script is the ONLY source — Use Gravity is forced off)")]
    [Tooltip("Downward acceleration in units/sec^2. Applied mass-independently.")]
    [SerializeField] private float gravityStrength = 24f;

    [Header("Ground check")]
    [SerializeField] private LayerMask groundMask;
    [SerializeField] private float groundProbeDistance = 0.15f;
    [SerializeField] private float groundGraceWindow = 0.12f;

    [Header("Anti-stuck escape")]
    [Tooltip("Marble is considered 'wedged' only when its speed is below this.")]
    [SerializeField] private float stuckSpeedThreshold = 0.8f;

    [Tooltip("Two contact normals count as DIFFERENT surfaces (a real corner) "
           + "only if they differ by more than this angle, in degrees.")]
    [SerializeField] private float cornerNormalAngle = 25f;

    [Tooltip("Escape force applied to work the marble out of a corner.")]
    [SerializeField] private float escapeForce = 30f;

    [Tooltip("Blend of the escape: 1 = purely along the open-space normal, "
           + "0 = purely player input.")]
    [Range(0f, 1f)]
    [SerializeField] private float escapeNormalBias = 0.6f;

    [Header("Tuning")]
    [Tooltip("Below this speed the travel direction is unreliable; grounded "
           + "input is applied wholesale without throttle/steer decomposition.")]
    [SerializeField] private float decomposeMinSpeed = 0.5f;

    // --- Input ---
    private PlayerControls controls;
    private Vector2 moveInput;
    private bool boostRequested;          // set on Shift press, consumed in FixedUpdate
    private float nextLurchTime;          // earliest Time.time the next lurch may fire
    private bool brakeHeld;               // Spacebar held — overrides throttle, applies decel
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

    // --- Contact tracking (for anti-stuck) ---
    private readonly List<Vector3> contactNormals = new List<Vector3>();

    private void Awake()
    {
        rb     = GetComponent<Rigidbody>();
        sphere = GetComponent<SphereCollider>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

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
        brakeHeld = controls.Marble.Brake.IsPressed();

        // Tap, not hold — set a flag, consume it in FixedUpdate.
        if (controls.Marble.Boost.WasPressedThisFrame())
            boostRequested = true;

        if (controls.Marble.Reset.WasPressedThisFrame())
            resetRequested = true;
    }

    private void OnCollisionStay(Collision collision)
    {
        for (int i = 0; i < collision.contactCount; i++)
            contactNormals.Add(collision.GetContact(i).normal);
    }

    private void FixedUpdate()
    {
        if (resetRequested)
        {
            DoReset();
            resetRequested = false;
            contactNormals.Clear();
            return;
        }

        UpdateGroundState();
        ApplyGravity();

        // Resolve lurch BEFORE the per-frame force pass. If it fires, the
        // boosted velocity is what subsequent force code sees this step.
        if (boostRequested)
        {
            TryLurch();
            boostRequested = false;
        }

        // Brake runs before per-frame control. ApplyGroundForce will skip
        // throttle while brakeHeld so the two never fight.
        if (brakeHeld && isGrounded)
            ApplyBrake();

        bool hasInput = moveInput.sqrMagnitude > 0.0001f;
        Vector3 desiredDir = hasInput && cameraTransform != null
            ? CameraRelativeDirection(moveInput)
            : Vector3.zero;

        if (hasInput && TryEscapeCorner(desiredDir))
        {
            contactNormals.Clear();
            return;
        }

        if (hasInput && cameraTransform != null)
        {
            if (isGrounded) ApplyGroundForce(desiredDir);
            else            ApplyAirForce(desiredDir);
        }

        contactNormals.Clear();
    }

    private bool TryEscapeCorner(Vector3 desiredDir)
    {
        if (rb.linearVelocity.magnitude > stuckSpeedThreshold) return false;
        if (contactNormals.Count < 2) return false;

        bool realCorner = false;
        for (int i = 0; i < contactNormals.Count && !realCorner; i++)
            for (int j = i + 1; j < contactNormals.Count; j++)
                if (Vector3.Angle(contactNormals[i], contactNormals[j]) > cornerNormalAngle)
                {
                    realCorner = true;
                    break;
                }
        if (!realCorner) return false;

        Vector3 openDir = Vector3.zero;
        foreach (Vector3 n in contactNormals)
            openDir += n;
        openDir.y = 0f;

        if (openDir.sqrMagnitude < 0.0001f) return false;
        openDir.Normalize();

        Vector3 escapeDir = (openDir * escapeNormalBias
                          + desiredDir * (1f - escapeNormalBias));
        if (escapeDir.sqrMagnitude < 0.0001f) escapeDir = openDir;
        escapeDir.Normalize();

        rb.AddForce(escapeDir * escapeForce, ForceMode.Force);
        return true;
    }

    /// <summary>
    /// One-shot additive speed boost in the current direction of travel. Gates:
    ///   - Must be grounded (lurch belongs to setup, not the air "letting go").
    ///   - Cooldown must be clear.
    ///   - Marble must already have momentum above lurchMinSpeed.
    ///   - Marble must be BELOW lurchMaxSpeed (hard ceiling).
    /// If any gate fails the lurch fizzles and the cooldown is NOT consumed.
    /// The post-lurch speed is clamped to lurchMaxSpeed, so near the ceiling
    /// the effective boost is reduced. Additive design prevents chaining from
    /// compounding into unbounded speed.
    /// </summary>
    private void TryLurch()
    {
        if (!isGrounded) return;
        if (Time.time < nextLurchTime) return;

        Vector3 hVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        float speed = hVel.magnitude;

        if (speed < lurchMinSpeed) return;       // no momentum to amplify
        if (speed >= lurchMaxSpeed) return;      // already at the ceiling

        // Add boost magnitude, clamped to the ceiling. Direction preserved.
        float newSpeed = Mathf.Min(speed + lurchSpeedBoost, lurchMaxSpeed);
        Vector3 dir = hVel / speed;
        Vector3 newHVel = dir * newSpeed;
        rb.linearVelocity = new Vector3(newHVel.x, rb.linearVelocity.y, newHVel.z);

        nextLurchTime = Time.time + lurchCooldown;
    }

    /// <summary>
    /// Constant-rate deceleration on horizontal velocity. Direction is the
    /// opposite of current travel — pure decay, never redirection. Per-step
    /// change is clamped against the current speed so the brake never
    /// overshoots into reverse. Vertical velocity is preserved so gravity
    /// is unaffected. ForceMode.Acceleration so brakeForce is mass-independent.
    /// </summary>
    private void ApplyBrake()
    {
        Vector3 hVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        float speed = hVel.magnitude;
        if (speed < 0.01f) return;       // already effectively stopped

        // Velocity change the brake would produce this physics step.
        float dvThisStep = brakeForce * Time.fixedDeltaTime;
        if (dvThisStep >= speed)
        {
            // Would overshoot into reverse — zero horizontal velocity instead.
            rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);
            return;
        }

        Vector3 brakeDir = -hVel / speed;
        rb.AddForce(brakeDir * brakeForce, ForceMode.Acceleration);
    }

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

    /// <summary>
    /// Ground control: throttle/steer split, speed cap, braking via S-key.
    /// When brakeHeld (Spacebar) is true, throttle is SKIPPED entirely — the
    /// dedicated brake handles deceleration. Steering remains active so the
    /// player can aim while braking. The lurch is a separate one-shot path.
    /// </summary>
    private void ApplyGroundForce(Vector3 desiredDir)
    {
        Vector3 hVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        float speed = hVel.magnitude;

        if (speed < decomposeMinSpeed)
        {
            // Below the decomposition threshold the input is wholesale. If
            // brake is held, suppress that too — brake should always dominate.
            if (!brakeHeld)
                rb.AddForce(desiredDir * moveForce, ForceMode.Force);
            return;
        }

        Vector3 vHat = hVel / speed;

        float along = Vector3.Dot(desiredDir, vHat);
        Vector3 throttleDir = vHat * along;
        Vector3 steerDir    = desiredDir - throttleDir;

        Vector3 force = steerDir * steerForce;                 // steering always

        // Throttle suppressed while braking — Space dominates W/S throttle.
        if (!brakeHeld)
        {
            if (along < 0f)
                force += throttleDir * moveForce;              // braking via S
            else if (speed < maxControlledSpeed)
                force += throttleDir * moveForce;              // accelerating, under cap
        }

        rb.AddForce(force, ForceMode.Force);
    }

    /// <summary>
    /// Airborne: the "letting go" moment. Both components of input are applied
    /// at deliberately weak forces — perpendicular as steering, along-velocity
    /// as throttle (positive accelerates, negative brakes). No speed cap. Weak
    /// enough that a bad jump cannot be rescued, only nudged.
    /// </summary>
    private void ApplyAirForce(Vector3 desiredDir)
    {
        Vector3 hVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        float speed = hVel.magnitude;

        if (speed < decomposeMinSpeed) return;

        Vector3 vHat = hVel / speed;

        // Decompose against travel — same shape as ground, different magnitudes.
        float along = Vector3.Dot(desiredDir, vHat);
        Vector3 throttleDir = vHat * along;             // sign carries accel/brake
        Vector3 steerDir    = desiredDir - throttleDir; // perpendicular to travel

        Vector3 force = steerDir * airSteerForce
                      + throttleDir * airThrottleForce;

        rb.AddForce(force, ForceMode.Force);
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