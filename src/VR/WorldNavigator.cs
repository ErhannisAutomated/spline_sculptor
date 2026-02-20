using Godot;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Two-hand world pan / scale / rotate.
    /// Initialised with direct node references by VRManager (no NodePath exports needed).
    /// </summary>
    [GlobalClass]
    public partial class WorldNavigator : Node3D
    {
        private XRController3D? _left;
        private XRController3D? _right;
        private Node3D?         _world;

        private bool    _wasActive;
        private Vector3 _prevMidpoint;
        private float   _prevSpan;
        private float   _prevAngle;

        /// <summary>Called by VRManager after creating the rig.</summary>
        public WorldNavigator(Node3D world, XRController3D left, XRController3D right)
        {
            _world = world;
            _left  = left;
            _right = right;
        }

        // Parameterless constructor required by Godot's [GlobalClass] registration.
        public WorldNavigator() { }

        public override void _PhysicsProcess(double delta)
        {
            if (_left == null || _right == null || _world == null) return;

            bool leftGrip  = _left.IsButtonPressed("grip");
            bool rightGrip = _right.IsButtonPressed("grip");

            if (leftGrip && rightGrip)
            {
                Vector3 lPos  = _left.GlobalPosition;
                Vector3 rPos  = _right.GlobalPosition;
                Vector3 mid   = (lPos + rPos) * 0.5f;
                float   span  = lPos.DistanceTo(rPos);
                Vector2 lr2d  = new Vector2(rPos.X - lPos.X, rPos.Z - lPos.Z);
                float   angle = Mathf.Atan2(lr2d.Y, lr2d.X);

                if (_wasActive)
                {
                    _world.GlobalPosition += mid - _prevMidpoint;

                    float scaleRatio = _prevSpan > 0.001f ? span / _prevSpan : 1.0f;
                    _world.Scale *= Mathf.Clamp(scaleRatio, 0.5f, 2.0f);

                    _world.RotateY(angle - _prevAngle);
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
