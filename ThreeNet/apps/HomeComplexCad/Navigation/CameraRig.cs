using System.Numerics;
using ThreeNet;

namespace HomeComplexCad.Navigation;

public enum CameraMode
{
    FirstPerson,
    ThirdPerson,
    Fly,
}

/// <summary>Movement intent collected from keyboard and mouse this frame.</summary>
public struct NavigationInput
{
    public float Forward;
    public float Right;
    public float Up;
    public float LookYaw;
    public float LookPitch;
    public float Zoom;
    public bool Sprint;
}

/// <summary>
/// Explorer camera with three modes. First person walks at eye height, follows
/// floors and stairs and stops at walls; third person does the same for a small
/// avatar and orbits behind it; fly moves freely through the estate. Location
/// jumps glide the camera to the target instead of cutting.
/// </summary>
public sealed class CameraRig
{
    private const float EyeHeight = 1.62f;
    private const float WalkSpeed = 2.4f;
    private const float FlySpeed = 14f;
    private const float BodyRadius = 0.3f;

    private readonly Scene _scene;
    private readonly Node _camera;
    private readonly Node _avatar;
    private readonly Node _avatarBody;

    private Vector3 _feet;
    private float _yaw;
    private float _pitch;
    private float _verticalVelocity;
    private float _thirdPersonDistance = 4.2f;

    private bool _transition;
    private float _transitionTime;
    private Vector3 _fromEye;
    private Vector3 _toEye;
    private float _fromYaw;
    private float _toYaw;
    private float _fromPitch;
    private float _toPitch;
    private CameraMode _modeAfterTransition;

    public CameraRig(Scene scene, Node camera)
    {
        _scene = scene;
        _camera = camera;

        // A simple mannequin for third person: body, head and a hint of a face direction.
        _avatar = scene.CreateNode(null, "avatar");
        Material shirt = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.15f, 0.38f, 0.62f, 1f), 0f, 0.7f));
        Material skin = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.72f, 0.53f, 0.4f, 1f), 0f, 0.6f));
        Material trousers = scene.CreateMaterial(MaterialOptions.Pbr(new Vector4(0.12f, 0.12f, 0.14f, 1f), 0f, 0.8f));
        _avatarBody = scene.AddMesh(scene.CreateCylinderGeometry(0.2f, 0.17f, 0.62f, 14), shirt, _avatar, "avatar-torso");
        _avatarBody.Position = new Vector3(0f, 1.18f, 0f);
        Node head = scene.AddMesh(scene.CreateSphereGeometry(0.13f, 16, 12), skin, _avatar, "avatar-head");
        head.Position = new Vector3(0f, 1.65f, 0f);
        Node legs = scene.AddMesh(scene.CreateCylinderGeometry(0.16f, 0.12f, 0.86f, 12), trousers, _avatar, "avatar-legs");
        legs.Position = new Vector3(0f, 0.43f, 0f);
        Node cap = scene.AddMesh(scene.CreateBoxGeometry(0.2f, 0.05f, 0.14f), shirt, _avatar, "avatar-cap");
        cap.Position = new Vector3(0f, 1.76f, 0.08f);
        _avatar.Visible = false;
    }

    public CameraMode Mode { get; private set; } = CameraMode.Fly;

    public Vector3 EyePosition => _camera.Position;

    public float Yaw => _yaw;

    /// <summary>Forward direction of the view, used for "what am I looking at".</summary>
    public Vector3 ViewDirection => Direction(_yaw, _pitch);

    public bool InTransition => _transition;

    private static Vector3 Direction(float yaw, float pitch) =>
        new(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));

    public void SetMode(CameraMode mode)
    {
        if (mode == Mode)
        {
            return;
        }

        Vector3 eye = _camera.Position;
        if (mode != CameraMode.Fly)
        {
            // Drop onto whatever floor is below the current view.
            _feet = new Vector3(eye.X, GroundBelow(eye + new Vector3(0f, 0.5f, 0f)) ?? 0f, eye.Z);
            _verticalVelocity = 0f;
        }
        else
        {
            _feet = eye;
        }

        Mode = mode;
        _avatar.Visible = mode == CameraMode.ThirdPerson;
    }

    /// <summary>Glides to a viewpoint; interiors end in first person, exteriors in fly mode.</summary>
    public void GoTo(Vector3 eye, Vector3 target, bool interior)
    {
        Vector3 direction = Vector3.Normalize(target - eye);
        _fromEye = _camera.Position;
        _toEye = eye;
        _fromYaw = _yaw;
        _toYaw = MathF.Atan2(direction.X, direction.Z);
        // Take the short way round.
        while (_toYaw - _fromYaw > MathF.PI)
        {
            _toYaw -= MathF.Tau;
        }

        while (_toYaw - _fromYaw < -MathF.PI)
        {
            _toYaw += MathF.Tau;
        }

        _fromPitch = _pitch;
        _toPitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        _transition = true;
        _transitionTime = 0f;
        _modeAfterTransition = interior ? CameraMode.FirstPerson : CameraMode.Fly;
        Mode = CameraMode.Fly;
        _avatar.Visible = false;
    }

    public void Update(float dt, NavigationInput input)
    {
        if (_transition)
        {
            _transitionTime += dt / 1.6f;
            float t = Math.Clamp(_transitionTime, 0f, 1f);
            float eased = t * t * (3f - (2f * t));
            // Arc up and over when travelling far across the estate.
            float distance = Vector3.Distance(_fromEye, _toEye);
            float lift = MathF.Sin(eased * MathF.PI) * MathF.Min(distance * 0.25f, 25f);
            Vector3 eye = Vector3.Lerp(_fromEye, _toEye, eased) + new Vector3(0f, lift, 0f);
            _yaw = float.Lerp(_fromYaw, _toYaw, eased);
            _pitch = float.Lerp(_fromPitch, _toPitch, eased);
            Apply(eye);
            if (t >= 1f)
            {
                _transition = false;
                _feet = _modeAfterTransition == CameraMode.Fly ? _toEye : _toEye - new Vector3(0f, EyeHeight, 0f);
                Mode = _modeAfterTransition;
                _avatar.Visible = false;
            }

            return;
        }

        _yaw -= input.LookYaw;
        _pitch = Math.Clamp(_pitch - input.LookPitch, -1.45f, 1.45f);

        Vector3 flatForward = new(MathF.Sin(_yaw), 0f, MathF.Cos(_yaw));
        Vector3 flatRight = new(-MathF.Cos(_yaw), 0f, MathF.Sin(_yaw));

        switch (Mode)
        {
            case CameraMode.Fly:
            {
                float speed = FlySpeed * (input.Sprint ? 3f : 1f);
                Vector3 move = (Direction(_yaw, _pitch) * input.Forward) + (flatRight * input.Right) + (Vector3.UnitY * input.Up);
                _feet += move * speed * dt;
                _feet.Y = MathF.Max(_feet.Y, 0.5f);
                Apply(_feet);
                break;
            }

            default:
            {
                float speed = WalkSpeed * (input.Sprint ? 2.2f : 1f);
                Vector3 move = (flatForward * input.Forward) + (flatRight * input.Right);
                if (move.LengthSquared() > 1f)
                {
                    move = Vector3.Normalize(move);
                }

                Walk(move * speed * dt);

                // Gravity and floor following (stairs, plinths, second floors).
                float? ground = GroundBelow(_feet + new Vector3(0f, 0.55f, 0f));
                float floor = ground ?? 0f;
                if (_feet.Y > floor + 0.02f)
                {
                    _verticalVelocity -= 9.8f * dt;
                    _feet.Y = MathF.Max(floor, _feet.Y + (_verticalVelocity * dt));
                }
                else
                {
                    _verticalVelocity = 0f;
                    _feet.Y = float.Lerp(_feet.Y, floor, 1f - MathF.Exp(-18f * dt));
                }

                if (Mode == CameraMode.FirstPerson)
                {
                    Apply(_feet + new Vector3(0f, EyeHeight, 0f));
                }
                else
                {
                    _thirdPersonDistance = Math.Clamp(_thirdPersonDistance - (input.Zoom * 0.6f), 1.8f, 12f);
                    _avatar.Position = _feet;
                    _avatar.EulerAngles = new Vector3(0f, _yaw, 0f);
                    Vector3 pivot = _feet + new Vector3(0f, 1.55f, 0f);
                    Vector3 back = -Direction(_yaw, _pitch);
                    float distance = _thirdPersonDistance;
                    // Pull the camera in when a wall sits between it and the avatar.
                    if (Hit(pivot, back, distance) is { } blocked)
                    {
                        distance = MathF.Max(0.6f, blocked - 0.25f);
                    }

                    _camera.Position = pivot + (back * distance) + new Vector3(0f, 0.25f, 0f);
                    _camera.LookAt(pivot);
                }

                break;
            }
        }
    }

    /// <summary>Moves horizontally, sliding along walls instead of passing through.</summary>
    private void Walk(Vector3 delta)
    {
        if (delta.LengthSquared() < 1e-8f)
        {
            return;
        }

        // Probe at knee and chest height; each axis separately so we slide.
        foreach (Vector3 axis in new[] { new Vector3(delta.X, 0f, 0f), new Vector3(0f, 0f, delta.Z) })
        {
            float length = axis.Length();
            if (length < 1e-5f)
            {
                continue;
            }

            Vector3 direction = axis / length;
            bool blocked = false;
            foreach (float height in new[] { 0.45f, 1.3f })
            {
                if (Hit(_feet + new Vector3(0f, height, 0f), direction, length + BodyRadius) is not null)
                {
                    blocked = true;
                    break;
                }
            }

            if (!blocked)
            {
                _feet += axis;
            }
        }
    }

    private float? Hit(Vector3 origin, Vector3 direction, float maxDistance)
    {
        IReadOnlyList<RayHit> hits = _scene.Raycast(new Ray(origin, direction), new RaycastOptions
        {
            MaxDistance = maxDistance,
            VisibleOnly = true,
            IncludeBackFaces = true,
        });
        foreach (RayHit hit in hits)
        {
            // Ignore the avatar itself and see-through glass balustrades are still solid.
            if (hit.Node.Name.StartsWith("avatar", StringComparison.Ordinal))
            {
                continue;
            }

            return hit.Distance;
        }

        return null;
    }

    private float? GroundBelow(Vector3 origin)
    {
        IReadOnlyList<RayHit> hits = _scene.Raycast(new Ray(origin, -Vector3.UnitY), new RaycastOptions
        {
            MaxDistance = 30f,
            VisibleOnly = true,
            IncludeBackFaces = true,
        });
        foreach (RayHit hit in hits)
        {
            if (hit.Node.Name.StartsWith("avatar", StringComparison.Ordinal) || hit.Normal.Y < 0.5f)
            {
                continue;
            }

            return origin.Y - hit.Distance;
        }

        return null;
    }

    private void Apply(Vector3 eye)
    {
        _camera.Position = eye;
        _camera.LookAt(eye + Direction(_yaw, _pitch));
    }

    /// <summary>Places the camera without an animation (initial view).</summary>
    public void Teleport(Vector3 eye, Vector3 target)
    {
        Vector3 direction = Vector3.Normalize(target - eye);
        _yaw = MathF.Atan2(direction.X, direction.Z);
        _pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        _feet = eye;
        Mode = CameraMode.Fly;
        Apply(eye);
    }
}
