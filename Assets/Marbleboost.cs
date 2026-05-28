using UnityEngine;

/// <summary>
/// Owns the timed boost state for the marble. A boost multiplies current speed,
/// then suppresses linear damping for a fixed window so the elevated speed is
/// sustained as a glide. When the window expires, normal damping is restored and
/// friction resumes decaying the marble.
///
/// State lives here, on the marble, rather than on the boost cube. This is
/// deliberate: the true "base" damping is captured once at Awake and never
/// re-read while boosted, so passing through a second cube mid-window refreshes
/// the timer without corrupting the restore value.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class MarbleBoost : MonoBehaviour
{
    private Rigidbody rb;
    private float baseDamping;          // the marble's normal linear damping
    private float boostTimeRemaining;   // seconds left in the active window
    private bool isBoosting;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        baseDamping = rb.linearDamping; // Pre-Unity 6: rb.drag
    }

    /// <summary>
    /// Applies a boost. Scales current speed (preserving travel direction),
    /// then suppresses friction for the duration of the window.
    /// </summary>
    /// <param name="multiplier">Factor applied to incoming speed.</param>
    /// <param name="minSpeed">Speed floor after scaling.</param>
    /// <param name="maxSpeed">Speed ceiling after scaling.</param>
    /// <param name="duration">Seconds the glide is sustained.</param>
    /// <param name="boostDamping">Linear damping during the window. 0 = frictionless.</param>
    /// <param name="fallbackDirection">Direction used if the marble enters near-stationary.</param>
    public void ApplyBoost(float multiplier, float minSpeed, float maxSpeed,
                           float duration, float boostDamping, Vector3 fallbackDirection)
    {
        Vector3 v = rb.linearVelocity; // Pre-Unity 6: rb.velocity
        float incomingSpeed = v.magnitude;
        Vector3 direction = incomingSpeed > 0.001f ? v.normalized : fallbackDirection.normalized;

        float boostedSpeed = Mathf.Clamp(incomingSpeed * multiplier, minSpeed, maxSpeed);
        rb.linearVelocity = direction * boostedSpeed;

        rb.linearDamping = boostDamping;  // suppress friction for the window
        boostTimeRemaining = duration;    // (re)start / refresh the timer
        isBoosting = true;
    }

    private void Update()
    {
        if (!isBoosting) return;

        boostTimeRemaining -= Time.deltaTime;
        if (boostTimeRemaining <= 0f)
            EndBoost();
    }

    private void EndBoost()
    {
        rb.linearDamping = baseDamping;   // restore normal friction; it now takes over
        isBoosting = false;
    }
}