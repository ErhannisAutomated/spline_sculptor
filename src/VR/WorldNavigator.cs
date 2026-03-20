using Godot;

namespace SplineSculptor.VR
{
	/// <summary>
	/// Two-hand world pan / scale / rotate (full 6DOF).
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
		private Vector3 _prevLeft;
		private Vector3 _prevRight;

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
                Vector3 lPos = _left.GlobalPosition;
                Vector3 rPos = _right.GlobalPosition;
                Vector3 mid  = (lPos + rPos) * 0.5f;
                float   span = lPos.DistanceTo(rPos);

                if (_wasActive)
                {
                    // Scale: ratio of hand spans
                    float scaleRatio = _prevSpan > 0.001f
                        ? Mathf.Clamp(span / _prevSpan, 0.5f, 2.0f)
                        : 1.0f;

                    // Full 3D rotation: from previous hand-to-hand direction to current
                    Vector3 prevDir = _prevRight - _prevLeft;
                    Vector3 curDir  = rPos - lPos;
                    Basis rotBasis  = Basis.Identity;

                    if (prevDir.LengthSquared() > 0.0001f && curDir.LengthSquared() > 0.0001f)
                    {
                        prevDir = prevDir.Normalized();
                        curDir  = curDir.Normalized();
                        float dot  = Mathf.Clamp(prevDir.Dot(curDir), -1f, 1f);
                        Vector3 ax = prevDir.Cross(curDir);
                        if (ax.LengthSquared() > 0.0001f && dot < 0.9999f)
                            rotBasis = new Basis(new Quaternion(ax.Normalized(), Mathf.Acos(dot)));
                    }

                    // Apply: scale + rotate world around prevMidpoint, translate to new midpoint
                    var origin   = _world.GlobalTransform.Origin;
                    var newPos   = mid + rotBasis * ((origin - _prevMidpoint) * scaleRatio);
                    var newBasis = rotBasis * _world.GlobalTransform.Basis;
                    _world.GlobalTransform = new Transform3D(newBasis, newPos);

                    // Clamp scale to sane range
                    var s = _world.Scale;
                    s = s.Clamp(Vector3.One * 0.05f, Vector3.One * 20f);
                    _world.Scale = s;
                }

                _prevMidpoint = mid;
                _prevSpan     = span;
                _prevLeft     = lPos;
                _prevRight    = rPos;
                _wasActive    = true;
            }
            else
            {
                _wasActive = false;
            }
        }
    }
}
