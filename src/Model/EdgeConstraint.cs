using System;
using Godot;
using SplineSculptor.Math;

namespace SplineSculptor.Model
{
    public enum SurfaceEdge { UMin, UMax, VMin, VMax }
    public enum Continuity { G0, G1 }

    /// <summary>
    /// Enforces G0 or G1 continuity between two surface boundary edges.
    /// After a control point moves on SurfaceA, Enforce() updates SurfaceB's boundary row.
    /// </summary>
    public class EdgeConstraint
    {
        public SculptSurface SurfaceA { get; set; }
        public SculptSurface SurfaceB { get; set; }
        public SurfaceEdge EdgeA { get; set; }
        public SurfaceEdge EdgeB { get; set; }
        public Continuity Type { get; set; }

        public EdgeConstraint(
            SculptSurface surfA, SurfaceEdge edgeA,
            SculptSurface surfB, SurfaceEdge edgeB,
            Continuity type = Continuity.G0)
        {
            SurfaceA = surfA;
            EdgeA = edgeA;
            SurfaceB = surfB;
            EdgeB = edgeB;
            Type = type;
        }

        /// <summary>
        /// After movedSurface's control points changed, propagate constraint to the other surface.
        /// G0: match boundary positions exactly.
        /// G1: additionally adjust the second row of B to match the tangent direction of A.
        /// </summary>
        public void Enforce(SculptSurface movedSurface)
        {
            SculptSurface src = movedSurface == SurfaceA ? SurfaceA : SurfaceB;
            SculptSurface dst = movedSurface == SurfaceA ? SurfaceB : SurfaceA;
            SurfaceEdge srcEdge = movedSurface == SurfaceA ? EdgeA : EdgeB;
            SurfaceEdge dstEdge = movedSurface == SurfaceA ? EdgeB : EdgeA;

            EnforceG0(src, srcEdge, dst, dstEdge);
            if (Type == Continuity.G1)
                EnforceG1(src, srcEdge, dst, dstEdge);
        }

        private static void EnforceG0(
            SculptSurface src, SurfaceEdge srcEdge,
            SculptSurface dst, SurfaceEdge dstEdge)
        {
            var srcGeo = src.Geometry;
            var dstGeo = dst.Geometry;

            int srcLen = GetEdgeLength(srcGeo, srcEdge);
            int dstLen = GetEdgeLength(dstGeo, dstEdge);

            // They must have the same number of control points along the shared edge
            int len = System.Math.Min(srcLen, dstLen);

            for (int k = 0; k < len; k++)
            {
                var (srcU, srcV) = GetEdgeCP(srcGeo, srcEdge, k, boundary: true);
                var (dstU, dstV) = GetEdgeCP(dstGeo, dstEdge, k, boundary: true);
                dstGeo.ControlPoints[dstU, dstV] = srcGeo.ControlPoints[srcU, srcV];
            }

            dst.ApplyControlPointMove(0, 0, dstGeo.ControlPoints[0, 0]); // fires event
        }

        private static void EnforceG1(
            SculptSurface src, SurfaceEdge srcEdge,
            SculptSurface dst, SurfaceEdge dstEdge)
        {
            var srcGeo = src.Geometry;
            var dstGeo = dst.Geometry;

            int len = System.Math.Min(
                GetEdgeLength(srcGeo, srcEdge),
                GetEdgeLength(dstGeo, dstEdge));

            for (int k = 0; k < len; k++)
            {
                // Boundary row (already matched by G0)
                var (su, sv) = GetEdgeCP(srcGeo, srcEdge, k, boundary: true);
                // Second row of src (inner control point that defines tangent)
                var (su2, sv2) = GetEdgeCP(srcGeo, srcEdge, k, boundary: false);

                // Boundary row of dst
                var (du, dv) = GetEdgeCP(dstGeo, dstEdge, k, boundary: true);
                // Second row of dst (to be adjusted)
                var (du2, dv2) = GetEdgeCP(dstGeo, dstEdge, k, boundary: false);

                // G1: dst inner = 2 * boundary - src inner (reflection across boundary)
                Vector3 boundary = srcGeo.ControlPoints[su, sv];
                Vector3 srcInner = srcGeo.ControlPoints[su2, sv2];
                dstGeo.ControlPoints[du2, dv2] = 2.0f * boundary - srcInner;
            }
        }

        // ─── Edge utilities ───────────────────────────────────────────────────────

        private static int GetEdgeLength(Math.NurbsSurface geo, SurfaceEdge edge)
        {
            return edge switch
            {
                SurfaceEdge.UMin or SurfaceEdge.UMax => geo.CpCountV,
                SurfaceEdge.VMin or SurfaceEdge.VMax => geo.CpCountU,
                _ => 0
            };
        }

        /// <summary>
        /// Return the (u, v) index of control point k along an edge.
        /// boundary=true → the boundary row; boundary=false → the adjacent (inner) row.
        /// </summary>
        private static (int u, int v) GetEdgeCP(
            Math.NurbsSurface geo, SurfaceEdge edge, int k, bool boundary)
        {
            int n = geo.CpCountU - 1;
            int m = geo.CpCountV - 1;

            return edge switch
            {
                SurfaceEdge.UMin => (boundary ? 0 : 1, k),
                SurfaceEdge.UMax => (boundary ? n : n - 1, k),
                SurfaceEdge.VMin => (k, boundary ? 0 : 1),
                SurfaceEdge.VMax => (k, boundary ? m : m - 1),
                _ => (0, 0)
            };
        }
    }
}
