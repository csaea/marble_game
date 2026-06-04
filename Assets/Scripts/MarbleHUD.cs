
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

/// <summary>
/// Drives a screen-space telemetry readout for the marble.
/// Samples physics state in the fixed timestep and renders it once per frame.
/// </summary>
[DisallowMultipleComponent]
public class MarbleHUD : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The marble's Rigidbody. If left empty, searches for a Rigidbody on a GameObject tagged 'Player'.")]
    [SerializeField] private Rigidbody marbleBody;

    [Tooltip("TextMeshPro element that receives the formatted readout.")]
    [SerializeField] private TMP_Text readout;

    [Header("Fields")]
    [SerializeField] private bool showSpeed          = true;
    [SerializeField] private bool showGroundSpeed    = true;
    [SerializeField] private bool showVerticalSpeed  = true;
    [SerializeField] private bool showVelocityVector = true;
    [SerializeField] private bool showHeading        = true;
    [SerializeField] private bool showSpin           = false;
    [SerializeField] private bool showTimer          = true;

    [Header("Speed Readout Smoothing")]
    [Tooltip("Exponential smoothing for the displayed speed. 0 = raw value, ~0.85 = stable but responsive, near 1 = nearly frozen.")]
    [Range(0f, 0.99f)]
    [SerializeField] private float speedSmoothing = 0f;

    // Physics state, sampled in FixedUpdate so reads align with the simulation.
    private Vector3 cachedVelocity;
    private Vector3 cachedAngularVelocity;
    private float   displaySpeed;

    // Timer state.
    private float       timerSeconds = 0f;
    private bool        timerRunning = true;
    private InputAction resetAction;

    private readonly System.Text.StringBuilder builder = new System.Text.StringBuilder(256);

    private void Reset()
    {
        readout = GetComponent<TMP_Text>();
    }

    private void Awake()
    {
        if (marbleBody == null)
        {
            GameObject player = GameObject.FindWithTag("Player");
            if (player != null)
                marbleBody = player.GetComponent<Rigidbody>();
        }

        if (marbleBody == null)
            Debug.LogWarning("[MarbleHUD] No Rigidbody assigned and none found on a 'Player'-tagged object.", this);

        if (readout == null)
            Debug.LogError("[MarbleHUD] No TMP_Text target assigned.", this);

        resetAction = new InputAction(binding: "<Keyboard>/r");
        resetAction.Enable();
    }

    private void OnDestroy()
    {
        resetAction.Disable();
        resetAction.Dispose();
    }

    private void FixedUpdate()
    {
        if (marbleBody == null) return;

        cachedVelocity        = marbleBody.linearVelocity;
        cachedAngularVelocity = marbleBody.angularVelocity;
    }

    private void Update()
    {
        if (resetAction.WasPressedThisFrame())
            timerSeconds = 0f;

        if (timerRunning)
            timerSeconds += Time.deltaTime;
    }

    private void LateUpdate()
    {
        if (marbleBody == null || readout == null) return;

        float rawSpeed = cachedVelocity.magnitude;
        displaySpeed = Mathf.Lerp(rawSpeed, displaySpeed, speedSmoothing);

        builder.Clear();

        if (showTimer)
        {
            int totalMs = Mathf.FloorToInt(timerSeconds * 1000f);
            int minutes = totalMs / 60000;
            int seconds = (totalMs % 60000) / 1000;
            int millis  = totalMs % 1000;

            builder.AppendLine($"TIME      {minutes:00}:{seconds:00}.{millis:000}");
        }

        if (showSpeed)
            builder.AppendLine($"SPEED     {displaySpeed,7:0.00} m/s");

        if (showGroundSpeed)
        {
            float planar = new Vector2(cachedVelocity.x, cachedVelocity.z).magnitude;
            builder.AppendLine($"GROUND    {planar,7:0.00} m/s");
        }

        if (showVerticalSpeed)
            builder.AppendLine($"VERTICAL  {cachedVelocity.y,7:0.00} m/s");

        if (showVelocityVector)
            builder.AppendLine($"VEL       ({cachedVelocity.x,6:0.0},{cachedVelocity.y,6:0.0},{cachedVelocity.z,6:0.0})");

        if (showHeading)
        {
            float heading = Mathf.Atan2(cachedVelocity.x, cachedVelocity.z) * Mathf.Rad2Deg;
            if (heading < 0f) heading += 360f;
            builder.AppendLine($"HEADING   {heading,7:0.0} deg");
        }

        if (showSpin)
            builder.AppendLine($"SPIN      {cachedAngularVelocity.magnitude,7:0.00} rad/s");

        readout.text = builder.ToString();
    }
}
