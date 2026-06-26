using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Physics-driven pinball flipper. Drives a HingeJoint motor between a rest
/// angle and a flip angle so the swing imparts real momentum to the marble.
/// Code-driven config (no manual JointMotor/JointLimits setup in Inspector).
/// </summary>
[RequireComponent(typeof(HingeJoint))]
[RequireComponent(typeof(Rigidbody))]
public class PinballFlipper : MonoBehaviour
{
    [Header("Swing Geometry (degrees, relative to hinge axis)")]
    [SerializeField] float restAngle = -25f;   // parked position
    [SerializeField] float flipAngle = 25f;    // raised position

    [Header("Motor")]
    [SerializeField] float flipSpeed   = 1500f; // deg/sec toward flip
    [SerializeField] float returnSpeed = 700f;  // deg/sec back to rest
    [SerializeField] float motorForce  = 1500f; // available torque -> kick strength

    [Header("Input")]
    [SerializeField] Key flipKey = Key.Space;

    HingeJoint hinge;
    Rigidbody  rb;
    InputAction flipAction;
    bool flipped;

    void Awake()
    {
        hinge = GetComponent<HingeJoint>();
        rb    = GetComponent<Rigidbody>();

        rb.useGravity   = false;          // motor holds it; prevents sag
        rb.isKinematic  = false;          // must be dynamic for motor + impact
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        hinge.useLimits = true;
        hinge.limits = new JointLimits { min = restAngle, max = flipAngle };
        hinge.useMotor = true;
        SetMotor(-returnSpeed);           // park at rest on start

        flipAction = new InputAction(
            type: InputActionType.Button,
            binding: $"<Keyboard>/{flipKey.ToString().ToLower()}");
    }

    void OnEnable()  => flipAction.Enable();
    void OnDisable() => flipAction.Disable();
    void OnDestroy() => flipAction.Dispose();

    void Update()
    {
        bool pressed = flipAction.IsPressed();
        if (pressed == flipped) return;   // edge only
        flipped = pressed;
        SetMotor(flipped ? flipSpeed : -returnSpeed);
    }

    void SetMotor(float targetVelocity)
    {
        hinge.motor = new JointMotor
        {
            targetVelocity = targetVelocity,
            force = motorForce,
            freeSpin = false
        };
    }
}