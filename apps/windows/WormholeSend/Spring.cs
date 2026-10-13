namespace WormholeSend;

/// A value that follows its target like SwiftUI's
/// spring(response:dampingFraction:), so both apps move alike.
sealed class Spring(double value)
{
    // Unit mass: stiffness from the period, damping from the ratio.
    static readonly double Stiffness = Math.Pow(2 * Math.PI / Spec.SpringResponse, 2);
    static readonly double Damping = 4 * Math.PI * Spec.SpringDamping / Spec.SpringResponse;

    public double Value { get; private set; } = value;
    public double Target { get; set; } = value;
    double velocity;

    public bool IsSettled => Value == Target && velocity == 0;

    public void Snap(double value)
    {
        Value = Target = value;
        velocity = 0;
    }

    /// Advances by `seconds`; returns whether the value is still moving.
    public bool Step(double seconds)
    {
        if (IsSettled) return false;
        // Small fixed substeps keep the integration stable on slow frames.
        var remaining = Math.Min(seconds, 0.1);
        while (remaining > 0)
        {
            var dt = Math.Min(remaining, 1.0 / 240);
            var force = -Stiffness * (Value - Target) - Damping * velocity;
            velocity += force * dt;
            Value += velocity * dt;
            remaining -= dt;
        }
        if (Math.Abs(Value - Target) < 0.05 && Math.Abs(velocity) < 0.5)
        {
            Snap(Target);
            return false;
        }
        return true;
    }
}
