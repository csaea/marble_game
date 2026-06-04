using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// A trigger volume that signals the marble's MarbleBoost component to apply a
/// timed speed boost. The cube holds the tunable parameters; the marble owns the
/// state and timing.
/// </summary>
[RequireComponent(typeof(Collider))]
public class SpeedBoostCube : MonoBehaviour
{
    [Header("Boost Magnitude")]
    [Tooltip("Multiplier applied to the marble's incoming speed.")]
    [SerializeField] private float boostMultiplier = 1.5f;

    [Tooltip("Speed floor guaranteed after the boost. Handles near-stationary entry.")]
    [SerializeField] private float minimumBoostSpeed = 8f;

    [Tooltip("Speed ceiling. Caps the result to limit tunneling and physics instability.")]
    [SerializeField] private float maximumSpeed = 40f;

    [Header("Boost Window")]
    [Tooltip("Seconds the elevated speed is sustained before normal friction resumes.")]
    [SerializeField] private float boostDuration = 5f;

    [Tooltip("Linear damping applied during the window. 0 = frictionless glide.")]
    [SerializeField] private float boostDamping = 0f;

    [Header("Identification")]
    [Tooltip("Tag assigned to the player marble.")]
    [SerializeField] private string marbleTag = "Player";

    [Header("Feedback Hook")]
    [Tooltip("Invoked the moment a boost is applied. Wire particle bursts, SFX, " +
             "or a camera punch here from the Inspector.")]
    [SerializeField] private UnityEvent onBoost;

    // Ensures the collider is configured as a trigger when the component is first added.
    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(marbleTag)) return;

        MarbleBoost boost = other.GetComponentInParent<MarbleBoost>();
        if (boost == null) return;

        boost.ApplyBoost(boostMultiplier, minimumBoostSpeed, maximumSpeed,
                         boostDuration, boostDamping, transform.forward);
        onBoost?.Invoke();
    }
}