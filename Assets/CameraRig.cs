using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Orbit-center rig for the marble camera.
///
///   FOLLOW   — tracks the marble's position (position only).
///   ORBIT    — Turn input moves freeYaw (single source of truth for yaw).
///              Offset from the marble's heading is derived, re-centered via
///              a WEIGHTED alignment gate, clamped, and recomposed.
///   PITCH    — rigPitch; driven by free-look, eased to level by gameplay.
///   FREE-LOOK (while held) — unclamped yaw + clamped pitch.
///   ZOOM     — child Camera distance tightens as the marble speeds up.
///   COLLISION — a predictive spherecast from the marble to the desired
///              camera position. When geometry blocks the view, the camera
///              blends PROPORTIONALLY toward a high overhead "save" framing
///              so the marble stays visible. Collision overrides speed-zoom
///              while active; speed-zoom resumes smoothly once the path clears.
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

    [Range(-1f, 1f)]
    [SerializeField] private float reCenterAlignment = 0.25f;
    [Range(-1f, 1f)]
    [SerializeField] private float reCenterFullAlignment = 0.7f;

    [SerializeField] private float pitchReturnSharpness = 3f;

    [Header("Free-Look (while held)")]
    [SerializeField] private float freeLookSpeed = 180f;
    [SerializeField] private float minPitch = -60f;
    [SerializeField] private float maxPitch = 75f;

    [Header("Speed Zoom")]
    [SerializeField] private Vector3 restOffset = new Vector3(0f, 3f, -8f);
    [SerializeField] private Vector3 fastOffset = new Vector3(0f, 2.2f, -4.5f);
    [SerializeField] private float zoomMaxSpeed = 16f;
    [SerializeField] private AnimationCurve zoomCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    [SerializeField] private float zoomSharpness = 4f;

    [Header("Camera Collision")]
    [Tooltip("Layers the camera must not clip into — your ground/level geometry.")]
    [SerializeField] private LayerMask collisionMask;

    [Tooltip("Radius of the probe cast from the marble toward the camera. "
           + "Roughly the camera's near-clip 'thickness'; keeps thin walls "
           + "from slipping between samples.")]
    [SerializeField] private float collisionProbeRadius = 0.3f;

    [Tooltip("Camera offset used when the view is FULLY blocked: high above "
           + "the marble, only slightly behind, looking down. The camera "
           + "blends toward this proportionally to how obstructed it is.")]
    [SerializeField] private Vector3 obstructedOffset = new Vector3(0f, 7f, -3f);

    [Tooltip("How quickly the camera blends toward / away from the overhead "
           + "save framing. Higher = snappier reaction to obstruction.")]
    [SerializeField] private float collisionBlendSharpness = 8f;

    // --- Input ---
    private PlayerControls controls;
    private Vector2 turnInput;
    private bool freeLookHeld;

    // --- Rig rotation state ---
    private float freeYaw;
    private float rigPitch;

    // --- Collision blend state ---
    // 0 = clear (pure speed-zoom), 1 = fully obstructed (pure overhead).
    private float obstructionBlend;

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
        UpdateCameraOffset(dt);
    }

    private void FollowTarget(float dt)
    {
        float t = 1f - Mathf.Exp(-followSharpness * dt);
        transform.position = Vector3.Lerp(transform.position, target.position, t);
    }

    private void UpdateGameplayOrbit(float dt)
    {
        freeYaw += turnInput.x * orbitSpeed * dt;

        Vector3 vel = target.linearVelocity;
        vel.y = 0f;
        float speed = vel.magnitude;

        if (speed >= reCenterMinSpeed)
        {
            float baseHeading = Quaternion.LookRotation(vel.normalized).eulerAngles.y;
            float offset = Mathf.DeltaAngle(baseHeading, freeYaw);

            Vector3 camForwardFlat = transform.forward;
            camForwardFlat.y = 0f;
            camForwardFlat.Normalize();
            float dot = Vector3.Dot(vel.normalized, camForwardFlat);

            float weight = Mathf.SmoothStep(
                0f, 1f,
                Mathf.InverseLerp(reCenterAlignment, reCenterFullAlignment, dot));

            float t = 1f - Mathf.Exp(-reCenterSharpness * weight * dt);
            offset = Mathf.Lerp(offset, 0f, t);

            if (Mathf.Abs(offset) <= maxOrbitAngle)
                offset = Mathf.Clamp(offset, -maxOrbitAngle, maxOrbitAngle);

            freeYaw = baseHeading + offset;
        }

        float pt = 1f - Mathf.Exp(-pitchReturnSharpness * dt);
        rigPitch = Mathf.Lerp(rigPitch, 0f, pt);
    }

    private void UpdateFreeLook(float dt)
    {
        freeYaw  += turnInput.x * freeLookSpeed * dt;
        rigPitch -= turnInput.y * freeLookSpeed * dt;
        rigPitch  = Mathf.Clamp(rigPitch, minPitch, maxPitch);
    }

    private void ApplyRotation()
    {
        transform.rotation = Quaternion.Euler(rigPitch, freeYaw, 0f);
    }

    /// <summary>
    /// Decides the camera's local offset. Speed-zoom produces the DESIRED
    /// offset; a predictive spherecast measures how obstructed that position
    /// is; the camera then blends proportionally toward the overhead save
    /// framing. Collision overrides speed-zoom while active.
    /// </summary>
    private void UpdateCameraOffset(float dt)
    {
        if (cameraChild == null) return;

        // 1. Speed-zoom: the offset the camera would use with a clear view.
        float speed01 = Mathf.Clamp01(target.linearVelocity.magnitude / zoomMaxSpeed);
        float curved  = zoomCurve.Evaluate(speed01);
        Vector3 zoomOffset = Vector3.Lerp(restOffset, fastOffset, curved);

        // 2. Predictive cast from the marble out to where the camera wants to be.
        //    targetObstruction is 0 (clear) .. 1 (fully blocked).
        Vector3 pivot        = transform.position;            // marble position
        Vector3 desiredWorld = transform.TransformPoint(zoomOffset);
        Vector3 dir          = desiredWorld - pivot;
        float   dist         = dir.magnitude;
        float   targetObstruction = 0f;

        if (dist > 0.001f)
        {
            dir /= dist;
            if (Physics.SphereCast(pivot, collisionProbeRadius, dir,
                    out RaycastHit hit, dist, collisionMask,
                    QueryTriggerInteraction.Ignore))
            {
                // Closer hit => more obstructed. hit.distance / dist is how far
                // along the path the geometry sits; invert it for obstruction.
                targetObstruction = 1f - Mathf.Clamp01(hit.distance / dist);
            }
        }

        // 3. Smoothly ease the blend value toward the measured obstruction so
        //    grazing objects never causes a hard pop.
        float bt = 1f - Mathf.Exp(-collisionBlendSharpness * dt);
        obstructionBlend = Mathf.Lerp(obstructionBlend, targetObstruction, bt);

        // 4. Blend the final offset: speed-zoom when clear, overhead when blocked.
        Vector3 finalOffset = Vector3.Lerp(zoomOffset, obstructedOffset, obstructionBlend);

        // 5. Ease the camera to the final offset (keeps motion smooth even if
        //    the blend itself moved this frame).
        float ct = 1f - Mathf.Exp(-zoomSharpness * dt);
        cameraChild.localPosition = Vector3.Lerp(cameraChild.localPosition, finalOffset, ct);

        cameraChild.LookAt(transform.position);
    }
}