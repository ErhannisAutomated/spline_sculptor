using System;
using System.Collections.Generic;
using SplineSculptor.Math;

namespace SplineSculptor.Model.Undo
{
    /// <summary>
    /// Doubles the span count in U and V by inserting a knot at the midpoint of each
    /// existing span (Boehm's shape-preserving algorithm). The underlying surface shape
    /// is unchanged; only the number of control points increases.
    ///
    /// Note: neighbouring surfaces that share a G0 edge are NOT subdivided. Their
    /// boundary control points still match this surface's boundary CPs (which are
    /// preserved exactly), but the new intermediate boundary CPs on this surface have
    /// no counterpart on the neighbour. G0 at those intermediate points is broken.
    /// </summary>
    public class SubdivideCommand : ICommand
    {
        private readonly SculptSurface _surface;
        private NurbsSurface? _snapshotBefore;

        public string Description => "Subdivide surface";

        public SubdivideCommand(SculptSurface surface)
        {
            _surface = surface;
        }

        public void Execute()
        {
            _snapshotBefore = _surface.Geometry.Clone();

            InsertSpanMidpoints(_surface.Geometry, isU: true);
            InsertSpanMidpoints(_surface.Geometry, isU: false);

            // Fire GeometryChanged so ControlNetNode detects the new CP count
            // and rebuilds handles.
            _surface.ApplyControlPointMove(0, 0, _surface.Geometry.ControlPoints[0, 0]);
        }

        public void Undo()
        {
            if (_snapshotBefore == null) return;
            _surface.Geometry = _snapshotBefore;
            _surface.ApplyControlPointMove(0, 0, _surface.Geometry.ControlPoints[0, 0]);
        }

        // ─── Knot insertion ───────────────────────────────────────────────────────

        private static void InsertSpanMidpoints(NurbsSurface geo, bool isU)
        {
            // Compute midpoints from the current knot vector BEFORE any insertions.
            double[] midpoints = ComputeSpanMidpoints(isU ? geo.KnotsU : geo.KnotsV);

            foreach (double t in midpoints)
            {
                if (isU) geo.InsertKnotU(t);
                else     geo.InsertKnotV(t);
            }
        }

        /// <summary>
        /// Returns the midpoint of each span in the knot vector.
        /// "Spans" are the intervals between consecutive unique knot values.
        /// </summary>
        private static double[] ComputeSpanMidpoints(double[] knots)
        {
            var unique = new List<double>();
            foreach (double k in knots)
                if (unique.Count == 0 || k > unique[unique.Count - 1] + 1e-12)
                    unique.Add(k);

            if (unique.Count < 2) return Array.Empty<double>();

            var midpoints = new double[unique.Count - 1];
            for (int i = 0; i < midpoints.Length; i++)
                midpoints[i] = (unique[i] + unique[i + 1]) * 0.5;

            return midpoints;
        }
    }
}
