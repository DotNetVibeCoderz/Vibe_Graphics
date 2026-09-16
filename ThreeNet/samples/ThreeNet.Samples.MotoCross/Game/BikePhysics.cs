using System.Numerics;

namespace MotoCross.Game;

/// <summary>What the rider is asking for this frame, all in [-1, 1] or booleans.</summary>
public readonly record struct RiderInput(float Throttle, float Brake, float Steer, float Lean, bool Hop);

/// <summary>
/// Arcade motocross physics: a rigid body with two contact points, a spring
/// suspension per wheel, grip that depends on the surface and free air control.
/// It is deliberately forgiving - the bike self-levels in the air and lands on
/// its wheels - but speed, jumps and berms all behave the way a rider expects.
/// </summary>
public sealed class BikePhysics(TerrainField terrain)
{
    private const float Wheelbase = 1.45f;
    private const float RideHeight = 0.42f;
    private const float Gravity = 19.5f;
    private const float EnginePower = 24f;
    private const float MaxSpeed = 32f;
    private const float BrakePower = 26f;

    private readonly TerrainField _terrain = terrain;

    public Vector3 Position { get; private set; }

    public Vector3 Velocity { get; private set; }

    /// <summary>Heading in radians (0 = +Z).</summary>
    public float Yaw { get; private set; }

    /// <summary>Nose up / down angle in radians.</summary>
    public float Pitch { get; private set; }

    /// <summary>Lean into the corner, radians.</summary>
    public float Roll { get; private set; }

    public bool Grounded { get; private set; } = true;

    /// <summary>Forward speed in metres per second.</summary>
    public float Speed { get; private set; }

    /// <summary>Normalised engine revs, drives the audio and the HUD.</summary>
    public float Revs { get; private set; }

    public int Gear { get; private set; } = 1;

    public float WheelSpin { get; private set; }

    public float FrontCompression { get; private set; }

    public float RearCompression { get; private set; }

    /// <summary>Seconds spent in the air during the current jump.</summary>
    public float AirTime { get; private set; }

    /// <summary>Longest single jump of the session, in seconds.</summary>
    public float BestAirTime { get; private set; }

    /// <summary>Impact strength of the last landing (0 when there was none this frame).</summary>
    public float LandingImpact { get; private set; }

    /// <summary>How much the rear wheel is sliding sideways, 0..1.</summary>
    public float Slide { get; private set; }

    public float DirtWeight { get; private set; } = 1f;

    /// <summary>True while the bike is off the graded circuit.</summary>
    public bool OffTrack => DirtWeight < 0.25f;

    public void Reset(Vector3 position, float yaw)
    {
        Position = position with { Y = _terrain.HeightAt(position.X, position.Z) + RideHeight };
        Velocity = Vector3.Zero;
        Yaw = yaw;
        Pitch = 0f;
        Roll = 0f;
        Speed = 0f;
        Revs = 0f;
        AirTime = 0f;
        Grounded = true;
    }

    public Vector3 Forward => new(MathF.Sin(Yaw), 0f, MathF.Cos(Yaw));

    public Vector3 Right => new(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));

    public void Update(float dt, in RiderInput input)
    {
        dt = MathF.Min(dt, 1f / 30f);
        LandingImpact = 0f;

        Vector3 forward = Forward;
        Vector3 right = Right;

        // Contact points under the two wheels.
        Vector3 frontContact = Position + (forward * (Wheelbase * 0.5f));
        Vector3 rearContact = Position - (forward * (Wheelbase * 0.5f));
        GroundSample front = _terrain.Sample(frontContact.X, frontContact.Z);
        GroundSample rear = _terrain.Sample(rearContact.X, rearContact.Z);
        GroundSample centre = _terrain.Sample(Position.X, Position.Z);
        DirtWeight = centre.DirtWeight;

        float groundY = ((front.Height + rear.Height) * 0.5f) + RideHeight;
        float slopePitch = MathF.Atan2(front.Height - rear.Height, Wheelbase);

        Vector3 velocity = Velocity;
        float forwardSpeed = Vector3.Dot(velocity, forward);
        float lateralSpeed = Vector3.Dot(velocity, right);

        bool wasGrounded = Grounded;
        Grounded = Position.Y <= groundY + 0.06f;

        if (Grounded)
        {
            if (!wasGrounded)
            {
                // Landing: the suspension takes the vertical energy.
                LandingImpact = MathF.Min(1f, MathF.Abs(velocity.Y) / 14f);
                RearCompression = LandingImpact;
                FrontCompression = LandingImpact * 0.8f;
                BestAirTime = MathF.Max(BestAirTime, AirTime);
                velocity.Y *= -0.12f;
            }

            AirTime = 0f;
            Position = Position with { Y = float.Lerp(Position.Y, groundY, 1f - MathF.Exp(-28f * dt)) };
            velocity.Y = MathF.Max(velocity.Y, 0f);

            // Grip is much better on the graded dirt than out in the grass.
            float grip = float.Lerp(0.55f, 1f, centre.DirtWeight);

            // Engine and brakes.
            float throttle = Math.Clamp(input.Throttle, 0f, 1f);
            float drive = throttle * EnginePower * grip * (1f - (MathF.Max(forwardSpeed, 0f) / MaxSpeed));
            float braking = Math.Clamp(input.Brake, 0f, 1f) * BrakePower;
            forwardSpeed += (drive - (braking * MathF.Sign(MathF.Max(forwardSpeed, 0.01f)))) * dt;
            // Climbing costs speed, descending gains a little.
            forwardSpeed -= MathF.Sin(slopePitch) * Gravity * 0.45f * dt;
            forwardSpeed -= forwardSpeed * (0.35f + ((1f - centre.DirtWeight) * 0.9f)) * dt;
            forwardSpeed = Math.Clamp(forwardSpeed, -6f, MaxSpeed);
            // With no input the rider holds the bike on the brake instead of
            // letting it roll down the slope.
            if (throttle <= 0f && input.Brake <= 0f && MathF.Abs(forwardSpeed) < 1.5f)
            {
                forwardSpeed *= MathF.Exp(-8f * dt);
                lateralSpeed *= MathF.Exp(-8f * dt);
            }

            // Steering: the faster the bike goes, the wider it turns.
            float steerAuthority = Math.Clamp(MathF.Abs(forwardSpeed) / 9f, 0f, 1f) * (1.9f - (MathF.Abs(forwardSpeed) / MaxSpeed));
            Yaw -= input.Steer * steerAuthority * dt * MathF.Sign(forwardSpeed >= 0f ? 1f : -1f);

            // Sideways grip: what is left of the lateral velocity is the slide.
            float lateralRetained = MathF.Exp(-grip * 9f * dt);
            lateralSpeed *= lateralRetained;
            Slide = Math.Clamp(MathF.Abs(lateralSpeed) / 6f, 0f, 1f);

            if (input.Hop && forwardSpeed > 2f)
            {
                velocity.Y = 7.4f;
                Grounded = false;
                FrontCompression = 0.4f;
            }

            // The chassis follows the ground slope.
            Pitch = float.Lerp(Pitch, slopePitch, 1f - MathF.Exp(-10f * dt));
            // Positive steer is a left turn, and the bike leans into it.
            float targetRoll = Math.Clamp((input.Steer * MathF.Abs(forwardSpeed) / 26f) + (lateralSpeed * 0.05f), -0.6f, 0.6f);
            Roll = float.Lerp(Roll, targetRoll, 1f - MathF.Exp(-9f * dt));
        }
        else
        {
            AirTime += dt;
            velocity.Y -= Gravity * dt;
            forwardSpeed -= forwardSpeed * 0.06f * dt;
            lateralSpeed -= lateralSpeed * 0.4f * dt;

            // Air control: the rider shifts weight to rotate the bike, and it
            // levels itself out so landings stay survivable.
            Pitch += input.Lean * 1.7f * dt;
            Pitch = float.Lerp(Pitch, 0f, 1f - MathF.Exp(-1.6f * dt));
            Pitch = Math.Clamp(Pitch, -0.9f, 0.9f);
            Yaw -= input.Steer * 0.9f * dt;
            Roll = float.Lerp(Roll, input.Steer * 0.35f, 1f - MathF.Exp(-4f * dt));
            Slide = 0f;
        }

        velocity = (forward * forwardSpeed) + (right * lateralSpeed) + new Vector3(0f, velocity.Y, 0f);
        Position += velocity * dt;

        // Keep the rider inside the valley.
        float limit = (TerrainField.WorldSize * 0.5f) - 6f;
        Position = new Vector3(
            Math.Clamp(Position.X, -limit, limit),
            Position.Y,
            Math.Clamp(Position.Z, -limit, limit));

        Velocity = velocity;
        Speed = forwardSpeed;
        WheelSpin += (forwardSpeed / 0.34f) * dt;

        // Five ratios spread over the speed range, purely for the HUD and audio.
        float normalised = Math.Clamp(MathF.Abs(forwardSpeed) / MaxSpeed, 0f, 1f);
        Gear = Math.Clamp(1 + (int)(normalised * 4.999f), 1, 5);
        float gearBase = (Gear - 1) / 5f;
        float targetRevs = Math.Clamp(((normalised - gearBase) * 5f * 0.8f) + (input.Throttle * 0.25f), 0.08f, 1f);
        if (!Grounded)
        {
            // Free revving in the air.
            targetRevs = Math.Clamp(0.35f + (input.Throttle * 0.65f), 0.1f, 1f);
        }

        Revs = float.Lerp(Revs, targetRevs, 1f - MathF.Exp(-7f * dt));
        FrontCompression = float.Lerp(FrontCompression, Grounded ? 0.12f : 0f, 1f - MathF.Exp(-6f * dt));
        RearCompression = float.Lerp(RearCompression, Grounded ? 0.18f + (input.Throttle * 0.1f) : 0f, 1f - MathF.Exp(-6f * dt));
    }
}
