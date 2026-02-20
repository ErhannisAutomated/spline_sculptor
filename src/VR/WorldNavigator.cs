using Godot;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Two-hand world pan / scale / rotate.
    /// Attach to the XROrigin3D (or a scene-root wrapper) as a child.
    /// </summary>
    [GlobalClass]
    public partial class WorldNavigator : Node3D
    {
        [Export] public NodePath LeftControllerPath  { get; set; } = "../LeftController";
        [Export] public NodePath RightControllerPath { get; set; } = "../RightController";
        [Export] public NodePath WorldRootPath       { get; set; } = "../../SceneRoot";

        private XRController3D? _left;
        private XRController3D? _right;
        private Node3D?         _world;

        // State from the previous frame for delta computation
        private bool    _wasActive = false;
        private Vector3 _prevMidpoint;
        private float   _prevSpan;
        private float   _prevAngle;

        public override void _Ready()
        {
            _left  = GetNodeOrNull<XRController3D>(LeftControllerPath);
            _right = GetNodeOrNull<XRController3D>(RightControllerPath);
            _world = GetNodeOrNull<Node3D>(WorldRootPath);
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_left == null || _right == null || _world == null) return;

            bool leftGrip  = _left.IsButtonPressed(JoyButton.LeftShoulder);
            bool rightGrip = _right.IsButtonPressed(JoyButton.RightShoulder);
            bool bothGrip  = leftGrip && rightGrip;

            if (bothGrip)
            {
                Vector3 lPos = _left.GlobalPosition;
                Vector3 rPos = _right.GlobalPosition;
                Vector3 mid  = (lPos + rPos) * 0.5f;
                float   span  = lPos.DistanceTo(rPos);

                // Project to horizontal plane for rotation angle
                Vector2 lr2d  = new Vector2(rPos.X - lPos.X, rPos.Z - lPos.Z);
                float   angle = Mathf.Atan2(lr2d.Y, lr2d.X);

                if (_wasActive)
                {
                    // Translation: world midpoint follows hand midpoint
                    Vector3 translation = mid - _prevMidpoint;
                    _world.GlobalPosition += translation;

                    // Scale: proportional to span change
                    float scaleRatio = _prevSpan > 0.001f ? span / _prevSpan : 1.0f;
                    scaleRatio = Mathf.Clamp(scaleRatio, 0.5f, 2.0f);
                    _world.Scale *= scaleRatio;

                    // Rotation around Y axis
                    float dAngle = angle - _prevAngle;
                    _world.RotateY(dAngle);
                }

                _prevMidpoint = mid;
                _prevSpan     = span;
                _prevAngle    = angle;
                _wasActive    = true;
            }
            else
            {
                _wasActive = false;
            }
        }
    }
}
