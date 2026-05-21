using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Orbit-center rig for the marble camera.
///
///   FOLLOW   — tracks the marble's position (position only).
///   ORBIT    — Turn input moves freeYaw (the rig's absolute yaw, the SINGLE
///              source of truth). When the marble has a heading, the offset
///              from that heading is DERIVED each frame, re-centered, clamped,
///              and recomposed. It is never accumulated independently.
///   RE-CENTER GATE — re-centering strength is WEIGHTED by how aligned the
///              marble's travel is with the camera's forward:
///                * below reCenterAlignment      -> weight 0 (dead zone:
///                  reverse / hard corners hold the player's manual orbit)
///                * reCenterAlignment..FullAlign -> smooth ramp
///                * above reCenterFullAlignment  -> weight 1 (full re-center)
///              The smooth ramp prevents the snap when the marble's heading
///              swings around (e.g. reverse, then steer, then forward).
///   PITCH    — rigPitch; driven by free-look, eased to level by gameplay.
///   FREE-LOOK (while held) — unclamped mouse yaw + clamped pitch.
///   ZOOM     — child Camera distance tightens as the marble speeds up.
///
/// This script goes on the empty CameraRig GameObject, NOT the Camera.
/// </summary>
public class CameraRig : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Rigidbody target;
    [SerializeField] private Transform cameraChild;

    [Header("Follow")]
    [SerializeField] private float followSharpness = 12f;

    [Header("Orbit (gameplay mode)")]
    [SerializeField] private float orbitSpeed = 120f;
    [SerializeField] private float maxOrbitAngle = 60f;
    [SerializeField] private float reCenterSharpness = 2.5f;
    [SerializeField] private float reCenterMinSpeed = 1.5f;

    [Tooltip("Lower edge of the re-center gate. Below this alignment (marble "
           + "travel vs camera forward, 1 = same way) re-centering is OFF — "
           + "the dead zone where reverse and hard corners hold manual orbit.")]
    [Range(-1f, 1f)]
    [SerializeField] private float reCenterAlignment = 0.25f;

    [Tooltip("Upper edge of the re-center gate. At/above this alignment "
           + "re-centering is at full strength. Between the two edges it "
           + "ramps smoothly. Must be greater than reCenterAlignment.")]
    [Range(-1f, 1f)]
    [SerializeField] private float reCenterFullAlignment = 0.7f;

    [Tooltip("How fast pitch eases back to level after a free-look release.")]
    [SerializeField] private float pitchReturnSharpness = 3f;

    [Header("Free-Look (while held)")]
    [SerializeField] private float freeLookSpeed = 180f;
    [SerializeField] private float minPitch = -60f;
    [SerializeField] private float maxPitch = 75f;

    [Header("Speed Zoom")]
    [SerializeField] private Vector3 restOffset = new Vector3(0f, 3f, -8f);
    [SerializeField] private Vector3 fastOffset = new Vector3(0f, 1.6f, -4.5f);
    [SerializeField] private float zoomMaxSpeed = 16f;
    [SerializeField] private AnimationCurve zoomCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    [SerializeField] private float zoomSharpness = 4f;

    // --- Input ---
    private PlayerControls controls;
    private Vector2 turnInput;
    private bool freeLookHeld;

    // --- Rig rotation state ---
    private float freeYaw;    // SINGLE source of truth for yaw, both modes
    private float rigPitch;   // driven by free-look, eased to 0 by gameplay

    private void Awake()
    {
        controls = new PlayerControls();
        if (cameraChild != null)
            cameraChild.localPosition = restOffset;

        freeYaw  = transform.eulerAngles.y;
        rigPitch = 0f;
    }

    private void OnEnable()  => controls.Marble.Enable();
    private void OnDisable() => controls.Marble.Disable();

    private void Update()
    {
        turnInput    = controls.Marble.Turn.ReadValue<Vector2>();
        freeLookHeld = controls.Marble.FreeLook.IsPressed();
    }

    private void LateUpdate()
    {
        if (target == null) return;

        float dt = Time.deltaTime;

        FollowTarget(dt);

        if (freeLookHeld) UpdateFreeLook(dt);
        else              UpdateGameplayOrbit(dt);

        ApplyRotation();
        UpdateZoom(dt);
    }

    private void FollowTarget(float dt)
    {
        float t = 1f - Mathf.Exp(-followSharpness * dt);
        transform.position = Vector3.Lerp(transform.position, target.position, t);
    }

    /// <summary>
    /// Gameplay orbit. freeYaw is moved by input, then — if the marble has a
    /// heading — the offset from that heading is derived, decayed toward 0 by
    /// WEIGHTED re-centering, clamped, and recomposed.
    /// </summary>
    private void UpdateGameplayOrbit(float dt)
    {
        freeYaw += turnInput.x * orbitSpeed * dt;

        Vector3 vel = target.linearVelocity;
        vel.y = 0f;
        float speed = vel.magnitude;

        if (speed >= reCenterMinSpeed)
        {
            float baseHeading = Quaternion.LookRotation(vel.normalized).eulerAngles.y;

            // Derive the offset from the single source of truth.
            float offset = Mathf.DeltaAngle(baseHeading, freeYaw);

            // --- Weighted re-center gate (option B) ---
            // How aligned is travel with where the camera looks? (-1..1)
            Vector3 camForwardFlat = transform.forward;
            camForwardFlat.y = 0f;
            camForwardFlat.Normalize();
            float dot = Vector3.Dot(vel.normalized, camForwardFlat);

            // 0 in the dead zone, smooth ramp up to 1 past the full-align edge.
            float weight = Mathf.SmoothStep(
                0f, 1f,
                Mathf.InverseLerp(reCenterAlignment, reCenterFullAlignment, dot));

            // Re-center decay, scaled by the gate weight. weight 0 => no pull.
            float t = 1f - Mathf.Exp(-reCenterSharpness * weight * dt);
            offset = Mathf.Lerp(offset, 0f, t);

            // Flip protection — always active, independent of the gate.
            // Hard-clamp only once inside the arc, so a large post-free-look
            // offset is eased in by re-centering rather than cut.
            if (Mathf.Abs(offset) <= maxOrbitAngle)
                offset = Mathf.Clamp(offset, -maxOrbitAngle, maxOrbitAngle);

            freeYaw = baseHeading + offset;
        }
        // else: no reliable heading — freeYaw keeps the player's manual orbit.

        // Pitch always eases toward level in gameplay.
        float pt = 1f - Mathf.Exp(-pitchReturnSharpness * dt);
        rigPitch = Mathf.Lerp(rigPitch, 0f, pt);
    }

    private void UpdateFreeLook(float dt)
    {
        freeYaw  += turnInput.x * freeLookSpeed * dt;
        rigPitch -= turnInput.y * freeLookSpeed * dt;
        rigPitch  = Mathf.Clamp(rigPitch, minPitch, maxPitch);
    }

    /// <summary>The single place that writes transform.rotation.</summary>
    private void ApplyRotation()
    {
        transform.rotation = Quaternion.Euler(rigPitch, freeYaw, 0f);
    }

    private void UpdateZoom(float dt)
    {
        if (cameraChild == null) return;

        float speed01 = Mathf.Clamp01(target.linearVelocity.magnitude / zoomMaxSpeed);
        float curved  = zoomCurve.Evaluate(speed01);

        Vector3 desiredOffset = Vector3.Lerp(restOffset, fastOffset, curved);
        float t = 1f - Mathf.Exp(-zoomSharpness * dt);
        cameraChild.localPosition = Vector3.Lerp(cameraChild.localPosition, desiredOffset, t);

        cameraChild.LookAt(transform.position);
    }
}