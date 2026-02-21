using System;
using System.Collections.Generic;
using Godot;
using SplineSculptor.Math;

namespace SplineSculptor.Model
{
    /// <summary>
    /// A named group of SculptSurfaces with edge constraints and a world transform.
    /// </summary>
    public class Polysurface
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; } = "Polysurface";
        public Transform3D Transform { get; set; } = Transform3D.Identity;
        public List<SculptSurface> Surfaces { get; } = new();
        public List<EdgeConstraint> Constraints { get; } = new();

        public event Action<SculptSurface>? SurfaceAdded;
        public event Action<SculptSurface>? SurfaceRemoved;

        // ─── Surface management ───────────────────────────────────────────────────

        public void AddSurface(SculptSurface s)
        {
            Surfaces.Add(s);
            SurfaceAdded?.Invoke(s);
        }

        public void RemoveSurface(SculptSurface s)
        {
            Surfaces.Remove(s);
            // Remove any constraints that reference this surface
            Constraints.RemoveAll(c => c.SurfaceA == s || c.SurfaceB == s);
            SurfaceRemoved?.Invoke(s);
        }

        public void AddConstraint(EdgeConstraint c)
        {
            Constraints.Add(c);
        }

        // ─── Patch attachment ─────────────────────────────────────────────────────

        /// <summary>
        /// Attach a new Bezier patch to the given edge of an existing surface.
        /// The new patch's opposing edge is initialised to match `existing`'s edge (G0).
        /// Returns the new SculptSurface (not yet added to the Polysurface — caller does that).
        /// </summary>
        public SculptSurface AttachPatch(SculptSurface existing, SurfaceEdge edge)
        {
            var newSurf = new SculptSurface(NurbsSurface.CreateBezierPatch());
            var newGeo  = newSurf.Geometry;
            var exGeo   = existing.Geometry;

            // Copy the boundary row of `existing` to the corresponding boundary of `newSurf`
            // and offset the remaining rows outward.
            SurfaceEdge newEdge = OppositeEdge(edge);

            int len = System.Math.Min(GetEdgeLength(exGeo, edge), GetEdgeLength(newGeo, newEdge));

            for (int k = 0; k < len; k++)
            {
                var (eu, ev)     = GetEdgeCP(exGeo, edge, k, boundary: true);
                var (eu2, ev2)   = GetEdgeCP(exGeo, edge, k, boundary: false);
                var (nu, nv)     = GetEdgeCP(newGeo, newEdge, k, boundary: true);
                var (nu2, nv2)   = GetEdgeCP(newGeo, newEdge, k, boundary: false);
                var (nu3, nv3)   = GetEdgeCP(newGeo, OppositeEdge(newEdge), k, boundary: true);
                var (nu4, nv4)   = GetEdgeCP(newGeo, OppositeEdge(newEdge), k, boundary: false);

                Vector3 boundary = exGeo.ControlPoints[eu, ev];
                Vector3 inner    = exGeo.ControlPoints[eu2, ev2];
                // Tangent direction from the existing surface's inner row
                Vector3 tangent  = boundary - inner;

                newGeo.ControlPoints[nu, nv]   = boundary;               // seam row
                newGeo.ControlPoints[nu2, nv2] = boundary + tangent;     // inner (defines G1 tangent)
                // Distribute the two outer rows evenly so the control polygon has no kink
                newGeo.ControlPoints[nu4, nv4] = boundary + 1.5f * tangent; // second inner
                newGeo.ControlPoints[nu3, nv3] = boundary + 2.0f * tangent; // far edge
            }

            // Add a G0 constraint between the existing surface's edge and the new surface
            var constraint = new EdgeConstraint(existing, edge, newSurf, newEdge, Continuity.G0);
            Constraints.Add(constraint);

            Surfaces.Add(newSurf);
            SurfaceAdded?.Invoke(newSurf);
            return newSurf;
        }

        /// <summary>Split the given surfaces out into a new Polysurface.</summary>
        public Polysurface Split(List<SculptSurface> toSplit)
        {
            var newPoly = new Polysurface { Name = Name + "_Split" };

            foreach (var s in toSplit)
            {
                Surfaces.Remove(s);
                SurfaceRemoved?.Invoke(s);
                newPoly.AddSurface(s);

                // Move any constraints fully contained in the split set
                for (int i = Constraints.Count - 1; i >= 0; i--)
                {
                    var c = Constraints[i];
                    if (toSplit.Contains(c.SurfaceA) && toSplit.Contains(c.SurfaceB))
                    {
                        newPoly.AddConstraint(c);
                        Constraints.RemoveAt(i);
                    }
                }
            }

            return newPoly;
        }

        // ─── Constraint enforcement ───────────────────────────────────────────────

        /// <summary>
        /// Enforce all constraints that touch the given surface (post-move pass).
        /// </summary>
        public void EnforceConstraints(SculptSurface movedSurface)
        {
            foreach (var c in Constraints)
            {
                if (c.SurfaceA == movedSurface || c.SurfaceB == movedSurface)
                    c.Enforce(movedSurface);
            }
        }

        // ─── Edge helpers (mirrored from EdgeConstraint) ──────────────────────────

        private static SurfaceEdge OppositeEdge(SurfaceEdge e) => e switch
        {
            SurfaceEdge.UMin => SurfaceEdge.UMax,
            SurfaceEdge.UMax => SurfaceEdge.UMin,
            SurfaceEdge.VMin => SurfaceEdge.VMax,
            SurfaceEdge.VMax => SurfaceEdge.VMin,
            _ => e
        };

        private static int GetEdgeLength(NurbsSurface geo, SurfaceEdge edge) => edge switch
        {
            SurfaceEdge.UMin or SurfaceEdge.UMax => geo.CpCountV,
            SurfaceEdge.VMin or SurfaceEdge.VMax => geo.CpCountU,
            _ => 0
        };

        private static (int u, int v) GetEdgeCP(NurbsSurface geo, SurfaceEdge edge, int k, bool boundary)
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
