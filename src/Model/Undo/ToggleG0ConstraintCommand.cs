using Godot;
using SplineSculptor.Interaction;

namespace SplineSculptor.Model.Undo
{
    /// <summary>
    /// Adds a G0 constraint between two selected edges (snapping edgeB to edgeA),
    /// or removes an existing G0 constraint between them (restoring edgeB's positions).
    /// Direction is chosen automatically using a distance-sum heuristic.
    /// </summary>
    public class ToggleG0ConstraintCommand : ICommand
    {
        private readonly EdgeRef _erA;
        private readonly EdgeRef _erB;
        private readonly Polysurface _poly;

        // Set during Execute(); used by Undo.
        private bool _wasAdded;
        private EdgeConstraint? _constraint;
        private Vector3[,]? _savedCPs; // snapshot of erB surface before enforce

        public string Description => "Toggle G0 constraint";

        public ToggleG0ConstraintCommand(EdgeRef erA, EdgeRef erB)
        {
            _erA  = erA;
            _erB  = erB;
            _poly = erA.Poly;
        }

        public void Execute()
        {
            var existing = FindConstraint();
            if (existing != null)
            {
                // Remove existing constraint; no position change needed.
                _wasAdded  = false;
                _constraint = existing;
                _savedCPs  = null; // positions were already snapped — restore not needed on remove
                _poly.Constraints.Remove(existing);
            }
            else
            {
                // Add new constraint: snap edgeB to edgeA.
                _wasAdded = true;
                bool reversed = DetermineReversed();
                _savedCPs  = SnapshotCPs(_erB.Surface);
                _constraint = new EdgeConstraint(
                    _erA.Surface, _erA.Edge,
                    _erB.Surface, _erB.Edge,
                    Continuity.G0) { Reversed = reversed };
                _poly.Constraints.Add(_constraint);
                _constraint.Enforce(_erA.Surface);
                _poly.EnforceConstraints(_erA.Surface);
            }
        }

        public void Undo()
        {
            if (_wasAdded)
            {
                // We added a constraint → remove it and restore erB's CPs.
                _poly.Constraints.Remove(_constraint!);
                RestoreCPs(_erB.Surface, _savedCPs!);
            }
            else
            {
                // We removed a constraint → re-add it (positions are already snapped).
                _poly.Constraints.Add(_constraint!);
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private EdgeConstraint? FindConstraint()
        {
            foreach (var c in _poly.Constraints)
            {
                bool ab = c.SurfaceA == _erA.Surface && c.EdgeA == _erA.Edge
                       && c.SurfaceB == _erB.Surface && c.EdgeB == _erB.Edge;
                bool ba = c.SurfaceA == _erB.Surface && c.EdgeA == _erB.Edge
                       && c.SurfaceB == _erA.Surface && c.EdgeB == _erA.Edge;
                if (ab || ba) return c;
            }
            return null;
        }

        /// <summary>
        /// Compare cumulative endpoint distances in forward vs reversed order.
        /// Returns true if reversed mapping (A[k] → B[len-1-k]) is better.
        /// </summary>
        private bool DetermineReversed()
        {
            var geoA = _erA.Surface.Geometry;
            var geoB = _erB.Surface.Geometry;
            int len = System.Math.Min(EdgeLength(geoA, _erA.Edge), EdgeLength(geoB, _erB.Edge));
            if (len == 0) return false;

            float fwdDist = 0f, revDist = 0f;
            for (int k = 0; k < len; k++)
            {
                Vector3 ptA    = EdgePoint(geoA, _erA.Edge, k);
                Vector3 ptBFwd = EdgePoint(geoB, _erB.Edge, k);
                Vector3 ptBRev = EdgePoint(geoB, _erB.Edge, len - 1 - k);
                fwdDist += ptA.DistanceTo(ptBFwd);
                revDist += ptA.DistanceTo(ptBRev);
            }
            return revDist < fwdDist;
        }

        private static int EdgeLength(Math.NurbsSurface geo, SurfaceEdge edge) => edge switch
        {
            SurfaceEdge.UMin or SurfaceEdge.UMax => geo.CpCountV,
            SurfaceEdge.VMin or SurfaceEdge.VMax => geo.CpCountU,
            _ => 0
        };

        private static Vector3 EdgePoint(Math.NurbsSurface geo, SurfaceEdge edge, int k)
        {
            int n = geo.CpCountU - 1;
            int m = geo.CpCountV - 1;
            var (u, v) = edge switch
            {
                SurfaceEdge.UMin => (0, k),
                SurfaceEdge.UMax => (n, k),
                SurfaceEdge.VMin => (k, 0),
                SurfaceEdge.VMax => (k, m),
                _ => (0, 0)
            };
            return geo.ControlPoints[u, v];
        }

        private static Vector3[,] SnapshotCPs(SculptSurface surf)
        {
            var geo    = surf.Geometry;
            int uCount = geo.CpCountU;
            int vCount = geo.CpCountV;
            var snap   = new Vector3[uCount, vCount];
            for (int u = 0; u < uCount; u++)
                for (int v = 0; v < vCount; v++)
                    snap[u, v] = geo.ControlPoints[u, v];
            return snap;
        }

        private static void RestoreCPs(SculptSurface surf, Vector3[,] snap)
        {
            var geo    = surf.Geometry;
            int uCount = geo.CpCountU;
            int vCount = geo.CpCountV;
            for (int u = 0; u < uCount; u++)
                for (int v = 0; v < vCount; v++)
                    surf.ApplyControlPointMove(u, v, snap[u, v]);
        }
    }
}
