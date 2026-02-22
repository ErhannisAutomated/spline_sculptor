using Godot;

namespace SplineSculptor.Model.Undo
{
    /// <summary>
    /// Changes the continuity type (G0 ↔ G1) of a single EdgeConstraint and
    /// immediately enforces the new type. Undo restores the affected surface's
    /// control points and reverts the constraint type.
    /// </summary>
    public class SetConstraintTypeCommand : ICommand
    {
        private readonly Polysurface    _poly;
        private readonly EdgeConstraint _constraint;
        private readonly Continuity     _oldType;
        private readonly Continuity     _newType;

        // Snapshot of SurfaceB's control points taken before Execute().
        // G1 enforcement only modifies the inner row of the "destination" surface,
        // but we snapshot the full grid to keep the undo simple and robust.
        private readonly SculptSurface _dst;
        private readonly Vector3[,]    _savedCPs;

        public string Description =>
            $"Set constraint {_oldType} → {_newType}";

        public SetConstraintTypeCommand(
            Polysurface poly, EdgeConstraint constraint, Continuity newType)
        {
            _poly       = poly;
            _constraint = constraint;
            _oldType    = constraint.Type;
            _newType    = newType;

            // When enforcing, we apply from SurfaceA → SurfaceB.
            // Snapshot SurfaceB so Undo can restore any positions G1 changes.
            _dst      = constraint.SurfaceB;
            _savedCPs = (Vector3[,])_dst.Geometry.ControlPoints.Clone();
        }

        public void Execute()
        {
            _constraint.Type = _newType;
            if (_newType == Continuity.G1)
                _constraint.Enforce(_constraint.SurfaceA);
            // G0 downgrade: type changes but no positions are altered.
        }

        public void Undo()
        {
            // Restore SurfaceB's control points to pre-Execute state.
            var geo    = _dst.Geometry;
            int uCount = geo.CpCountU;
            int vCount = geo.CpCountV;
            for (int u = 0; u < uCount; u++)
                for (int v = 0; v < vCount; v++)
                    _dst.ApplyControlPointMove(u, v, _savedCPs[u, v]);

            _constraint.Type = _oldType;
        }
    }
}
