using UnityEngine;

/// <summary>
/// Owns the timed boost state for the marble. A boost records the pre-boost
/// speed, multiplies it, and suppresses linear damping so the elevated speed is
/// held for a fixed window. When the window expires, the marble eases back down
/// to its pre-boost speed over a deceleration ramp. Both the start of the revert
/// and its progress are gated on being grounded, so boosted speed is carried
/// through jumps and never bled off mid-air.
///
/// Ground state is inferred from collision contact normals (no LayerMask setup
/// required). Trigger colliders such as the boost cube never generate collision
/// contacts, so they cannot be mistaken for ground.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class MarbleBoost : MonoBehaviour
{
    [Header("Revert")]
    [Tooltip("Seconds of grounded time to ease from boosted speed back to pre-boost speed. " +
             "0 = instant snap.")]
    [SerializeField] private float revertDuration = 0.75f;

    [Header("Ground Detection")]
    [Tooltip("Minimum contact-normal Y to count as ground. 1 = flat only, " +
             "0.5 ~= up to 60 degrees from vertical, 0 = any surface including walls.")]
    [SerializeField] private float groundNormalThreshold = 0.5f;

    private Rigidbody rb;
    private float baseDamping;          // the marble's normal linear damping
    private float preBoostSpeed;        // speed magnitude before the boost began
    private float boostTimeRemaining;   // seconds left in the active window
    private float revertRate;           // speed units removed per second during the ramp
    private bool isBoosting;
    private bool windowExpired;         // timer done; ramp may begin once grounded
    private bool isGrounded;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        baseDamping = rb.linearDamping; // Pre-Unity 6: rb.drag
    }

    /// <summary>
    /// Applies a boost: scales current speed (preserving travel direction) and
    /// suppresses friction for the window. Captures the pre-boost speed for the
    /// eventual revert ramp.
    /// </summary>
    public void ApplyBoost(float multiplier, float minSpeed, float maxSpeed,
                           float duration, float boostDamping, Vector3 fallbackDirection)
    {
        Vector3 v = rb.linearVelocity; // Pre-Unity 6: rb.velocity
        float incomingSpeed = v.magnitude;
        Vector3 direction = incomingSpeed > 0.001f ? v.normalized : fallbackDirection.normalized;

        // Capture pre-boost speed only when starting from an unboosted state.
        // Chained cubes refresh the window but preserve the original revert target.
        if (!isBoosting)
            preBoostSpeed = incomingSpeed;

        float boostedSpeed = Mathf.Clamp(incomingSpeed * multiplier, minSpeed, maxSpeed);
        rb.linearVelocity = direction * boostedSpeed;

        rb.linearDamping = boostDamping;  // suppress friction for the window
        boostTimeRemaining = duration;    // (re)start / refresh the timer
        isBoosting = true;
        windowExpired = false;            // a fresh window cancels any pending revert
    }

    private void FixedUpdate()
    {
        // Reset each physics step; OnCollisionStay (called later this step) re-sets it.
        isGrounded = false;
    }

    private void OnCollisionStay(Collision collision)
    {
        for (int i = 0; i < collision.contactCount; i++)
        {
            if (collision.GetContact(i).normal.y >= groundNormalThreshold)
            {
                isGrounded = true;
                return;
            }
        }
    }

    private void Update()
    {
        if (!isBoosting) return;

        // Phase 1: count down the boost window.
        if (!windowExpired)
        {
            boostTimeRemaining -= Time.deltaTime;
            if (boostTimeRemaining <= 0f)
            {
                windowExpired = true;
                // Fix the ramp rate from the current (boosted) speed so the descent
                // spans revertDuration of grounded time regardless of the gap size.
                float gap = Mathf.Abs(rb.linearVelocity.magnitude - preBoostSpeed);
                revertRate = revertDuration > 0f ? gap / revertDuration : Mathf.Infinity;
            }
            return;
        }

        // Phase 2: ease back to pre-boost speed, but only while grounded.
        if (!isGrounded) return;

        Vector3 v = rb.linearVelocity;
        if (v.sqrMagnitude < 0.000001f) { EndBoost(); return; }

        float nextSpeed = Mathf.MoveTowards(v.magnitude, preBoostSpeed, revertRate * Time.deltaTime);
        rb.linearVelocity = v.normalized * nextSpeed;

        if (Mathf.Approximately(nextSpeed, preBoostSpeed))
            EndBoost();
    }

    private void EndBoost()
    {
        rb.linearDamping = baseDamping;   // restore normal friction
        isBoosting = false;
        windowExpired = false;
    }
}