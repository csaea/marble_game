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
///   AIRBORNE — the "letting go" moment. NO throttle, only a faint steer.
///
/// GRAVITY — applied entirely by this script. Rigidbody 'Use Gravity' is
///   forced off in Awake so this script is the single source of truth.
///
/// GROUND CHECK — downward spherecast + coyote grace buffer.
///
/// ANTI-STUCK ESCAPE — when the marble is wedged in a sharp corner (touching
///   2+ differently-facing surfaces), is nearly stationary, AND the player is
///   giving input, an escape force is applied along the summed contact
///   normals (the open-space direction), blended with player input. This
///   guarantees the player can always work the marble free of a corner.
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
    [Tooltip("Faint sideways nudge allowed mid-air. NO air throttle.")]
    [SerializeField] private float airSteerForce = 4f;

    [Header("Gravity (this script is the ONLY source — Use Gravity is forced off)")]
    [Tooltip("Downward acceleration in units/sec^2. Applied mass-independently.")]
    [SerializeField] private float gravityStrength = 24f;

    [Header("Ground check")]
    [SerializeField] private LayerMask groundMask;
    [SerializeField] private float groundProbeDistance = 0.15f;
    [SerializeField] private float groundGraceWindow = 0.12f;

    [Header("Anti-stuck escape")]
    [Tooltip("Marble is considered 'wedged' only when its speed is below this. "
           + "Above it, the marble is clearly already moving and needs no help.")]
    [SerializeField] private float stuckSpeedThreshold = 0.8f;

    [Tooltip("Two contact normals count as DIFFERENT surfaces (a real corner) "
           + "only if they differ by more than this angle, in degrees. Stops "
           + "two contacts on one flat wall from triggering the escape.")]
    [SerializeField] private float cornerNormalAngle = 25f;

    [Tooltip("Escape force applied to work the marble out of a corner.")]
    [SerializeField] private float escapeForce = 30f;

    [Tooltip("Blend of the escape: 1 = purely along the open-space normal, "
           + "0 = purely player input. Mid values leave the corner biased "
           + "toward where the player is steering.")]
    [Range(0f, 1f)]
    [SerializeField] private float escapeNormalBias = 0.6f;

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

    // --- Contact tracking (for anti-stuck) ---
    // Contact normals gathered this physics step, cleared each FixedUpdate.
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

        if (controls.Marble.Reset.WasPressedThisFrame())
            resetRequested = true;
    }

    // Gather every contact normal the marble currently has. OnCollisionStay
    // fires each physics step for every collider still in contact.
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

        bool hasInput = moveInput.sqrMagnitude > 0.0001f;
        Vector3 desiredDir = hasInput && cameraTransform != null
            ? CameraRelativeDirection(moveInput)
            : Vector3.zero;

        // Anti-stuck escape runs BEFORE normal control and, when it fires,
        // replaces normal control for this step.
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

        // Contacts are per-step; clear them for the next step's gathering.
        contactNormals.Clear();
    }

    /// <summary>
    /// If the marble is wedged in a sharp corner and nearly stationary, applies
    /// an escape force along the summed contact normals (open-space direction)
    /// blended with player input. Returns true if the escape fired.
    /// </summary>
    private bool TryEscapeCorner(Vector3 desiredDir)
    {
        // Must be nearly stationary — a moving marble is not stuck.
        if (rb.linearVelocity.magnitude > stuckSpeedThreshold) return false;

        // Need at least two contacts to form a corner.
        if (contactNormals.Count < 2) return false;

        // Confirm the contacts represent genuinely DIFFERENT surfaces — not
        // several contact points on one flat wall. Find any pair of normals
        // that differ by more than cornerNormalAngle.
        bool realCorner = false;
        for (int i = 0; i < contactNormals.Count && !realCorner; i++)
            for (int j = i + 1; j < contactNormals.Count; j++)
                if (Vector3.Angle(contactNormals[i], contactNormals[j]) > cornerNormalAngle)
                {
                    realCorner = true;
                    break;
                }
        if (!realCorner) return false;

        // Sum the normals → a direction pointing away from all touched
        // surfaces, i.e. toward open space. Flatten Y so the escape pushes
        // the marble along the ground, not up into the air.
        Vector3 openDir = Vector3.zero;
        foreach (Vector3 n in contactNormals)
            openDir += n;
        openDir.y = 0f;

        if (openDir.sqrMagnitude < 0.0001f) return false;   // degenerate — give up gracefully
        openDir.Normalize();

        // Blend the open-space direction with the player's input.
        Vector3 escapeDir = (openDir * escapeNormalBias
                          + desiredDir * (1f - escapeNormalBias));
        if (escapeDir.sqrMagnitude < 0.0001f) escapeDir = openDir;
        escapeDir.Normalize();

        rb.AddForce(escapeDir * escapeForce, ForceMode.Force);
        return true;
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