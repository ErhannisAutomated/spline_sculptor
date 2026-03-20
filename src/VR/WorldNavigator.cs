using Godot;

namespace SplineSculptor.VR
{
	/// <summary>
	/// World pan / scale / rotate driven by controller grip buttons.
	///
	/// ONE hand held:
	///   World behaves as if parented to the controller — every translation and
	///   rotation of the controller is mirrored on the world (pivot = controller origin).
	///
	/// BOTH hands held (7 DOF):
	///   A "grip frame" is derived from both controllers each physics tick:
	///     - Origin    : midpoint between the two controllers
	///     - Scale     : distance between them
	///     - Orientation:
	///         X-axis = normalized hand-to-hand vector           (2 rotation DOF)
	///         roll   = average controller up, projected ⊥ X-axis (1 rotation DOF)
	///   The frame-to-frame delta (rotation + scale + translation) is applied to
	///   the world, pivoting around the previous frame's midpoint.
	/// </summary>
	[GlobalClass]
	public partial class WorldNavigator : Node3D
	{
		private XRController3D? _left;
		private XRController3D? _right;
		private Node3D?         _world;

		private enum GripState { None, Left, Right, Both }
		private GripState   _gripState = GripState.None;

		// Single-grip state
		private Transform3D _prevCtrlTransform;

		// Two-grip state
		private Vector3 _prevMidpoint;
		private Basis   _prevGripBasis;
		private float   _prevSpan;

		public WorldNavigator(Node3D world, XRController3D left, XRController3D right)
		{
			_world = world;
			_left  = left;
			_right = right;
		}

		public WorldNavigator() { }

		public override void _PhysicsProcess(double delta)
		{
			if (_left == null || _right == null || _world == null) return;

			bool lGrip = _left.IsButtonPressed("grip");
			bool rGrip = _right.IsButtonPressed("grip");

			if      (lGrip && rGrip)  HandleBothGrips();
			else if (lGrip)           HandleSingleGrip(_left,  GripState.Left);
			else if (rGrip)           HandleSingleGrip(_right, GripState.Right);
			else                      _gripState = GripState.None;
		}

		// ─── Single-hand ──────────────────────────────────────────────────────────

		private void HandleSingleGrip(XRController3D ctrl, GripState state)
		{
			var cur = ctrl.GlobalTransform;

			if (_gripState == state)
			{
				// world_new = T_curr × T_prev⁻¹ × world_old
				// (identical to the world being a child of the controller)
				var delta = cur * _prevCtrlTransform.AffineInverse();
				_world!.GlobalTransform = delta * _world.GlobalTransform;
			}

			_prevCtrlTransform = cur;
			_gripState = state;
		}

		// ─── Two-hand ─────────────────────────────────────────────────────────────

		private void HandleBothGrips()
		{
			var (midPos, span, gripBasis) = ComputeGripFrame();

			if (_gripState == GripState.Both)
			{
				// Rotation delta: from previous grip frame to current
				var deltaRot = gripBasis * _prevGripBasis.Transposed();

				// Scale delta: how much the hand separation changed
				float scaleRatio = _prevSpan > 0.001f
					? span / _prevSpan
					: 1.0f;

				// Apply:  scale + rotate around prevMidpoint,  then translate to midPos
				var origin   = _world!.GlobalTransform.Origin;
				var newPos   = midPos + deltaRot * ((origin - _prevMidpoint) * scaleRatio);
				var newBasis = deltaRot * _world.GlobalTransform.Basis;
				_world.GlobalTransform = new Transform3D(newBasis, newPos);

				_world.Scale = _world.Scale * scaleRatio;
			}

			_prevMidpoint  = midPos;
			_prevGripBasis = gripBasis;
			_prevSpan      = span;
			_gripState     = GripState.Both;
		}

		// ─── Grip frame ───────────────────────────────────────────────────────────

		/// <summary>
		/// Build the grip coordinate frame from both controllers.
		///
		/// X-axis: normalized right-hand − left-hand direction.
		///         Changes here cover 2 rotation DOF (pitch + yaw of the line).
		///
		/// Roll (3rd DOF): each controller's local Y-axis ("up") is projected
		///         perpendicular to X, then the two are averaged.
		///         This captures the twist around the primary axis — e.g. when both
		///         controllers roll inward, the grip frame rolls inward too.
		/// </summary>
		private (Vector3 mid, float span, Basis basis) ComputeGripFrame()
		{
			var lPos = _left!.GlobalPosition;
			var rPos = _right!.GlobalPosition;
			var mid  = (lPos + rPos) * 0.5f;
			float span = lPos.DistanceTo(rPos);

			// Primary axis: hand-to-hand direction
			Vector3 xAxis = span > 0.001f ? (rPos - lPos) / span : Vector3.Right;

			// Roll: average of each controller's up projected ⊥ to xAxis
			Vector3 lUp = _left.GlobalTransform.Basis.Y;
			Vector3 rUp = _right.GlobalTransform.Basis.Y;

			Vector3 lPerp = lUp - xAxis * lUp.Dot(xAxis);
			Vector3 rPerp = rUp - xAxis * rUp.Dot(xAxis);
			float lLen = lPerp.Length();
			float rLen = rPerp.Length();

			Vector3 avgUp;
			if (lLen > 0.001f && rLen > 0.001f)
			{
				lPerp /= lLen;
				rPerp /= rLen;
				// If pointing in roughly opposite directions, trust the left controller
				avgUp = lPerp.Dot(rPerp) > -0.5f
					? (lPerp + rPerp).Normalized()
					: lPerp;
			}
			else if (lLen > 0.001f) avgUp = lPerp / lLen;
			else if (rLen > 0.001f) avgUp = rPerp / rLen;
			else                    avgUp = Vector3.Up;

			// Build orthonormal basis: xAxis primary, avgUp secondary
			Vector3 zAxis = xAxis.Cross(avgUp);
			if (zAxis.LengthSquared() < 0.0001f)
				zAxis = xAxis.Cross(Vector3.Back); // degenerate fallback
			zAxis = zAxis.Normalized();
			Vector3 yAxis = zAxis.Cross(xAxis).Normalized();

			return (mid, span, new Basis(xAxis, yAxis, zAxis));
		}
	}
}
