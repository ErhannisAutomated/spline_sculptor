using System;
using System.Drawing;
using Godot;
using Rhino.FileIO;
using Rhino.DocObjects;
using SplineSculptor.Model;
using SplineSculptor.Math;

namespace SplineSculptor.IO
{
    /// <summary>
    /// Save and load .3dm files via the rhino3dm NuGet library.
    ///
    /// Save: each SculptSurface → one NurbsSurface object in the file.
    ///       Polysurface grouping is preserved via Rhino ObjectGroups.
    ///
    /// Load: reads Rhino.Geometry.NurbsSurface objects, reconstructs the scene.
    ///       G0 constraints are inferred from coincident boundary control points.
    ///
    /// Note on native libs: rhino3dm ships native binaries that must be next to
    /// the Godot executable at runtime. Copy them from the NuGet package's
    /// runtimes/ folder into the export directory.
    /// </summary>
    public static class Rhino3dmIO
    {
        private const double G0Tolerance = 1e-4; // world units

        // ─── Save ─────────────────────────────────────────────────────────────────

        public static void Save(SculptScene scene, string path)
        {
            var file = new File3dm();

            foreach (var poly in scene.Polysurfaces)
            {
                foreach (var surf in poly.Surfaces)
                {
                    var rhinoSurf = ToRhino(surf.Geometry);
                    var attr = new ObjectAttributes();
                    attr.ColorSource = ObjectColorSource.ColorFromObject;
                    attr.ObjectColor = GodotColorToDrawing(surf.SurfaceColor);
                    // Encode polysurface identity in the object name so we can reconstruct
                    // grouping on load without relying on the Rhino Group API.
                    attr.Name = $"SS|{poly.Id}|{poly.Name}|{surf.Id}";

                    file.Objects.AddSurface(rhinoSurf, attr);
                }
            }

            bool ok = file.Write(path, 0);
            if (!ok)
                GD.PrintErr($"[Rhino3dmIO] Failed to write {path}");
            else
                GD.Print($"[Rhino3dmIO] Saved {path}");
        }

        // ─── Load ─────────────────────────────────────────────────────────────────

        public static SculptScene? Load(string path)
        {
            var file = File3dm.Read(path);
            if (file == null)
            {
                GD.PrintErr($"[Rhino3dmIO] Could not read {path}");
                return null;
            }

            var scene = new SculptScene { FilePath = path };

            // Map polysurface-guid-string → Polysurface (reconstructed from Name encoding)
            var groupMap = new System.Collections.Generic.Dictionary<string, Polysurface>();

            foreach (var obj in file.Objects)
            {
                if (obj.Geometry is not Rhino.Geometry.NurbsSurface rhinoSurf)
                    continue; // skip non-NURBS geometry silently

                var geo  = FromRhino(rhinoSurf);
                var surf = new SculptSurface(geo);

                // Recover color
                if (obj.Attributes.ColorSource == ObjectColorSource.ColorFromObject)
                    surf.SurfaceColor = DrawingColorToGodot(obj.Attributes.ObjectColor);

                // Recover polysurface grouping from encoded Name: "SS|polyGuid|polyName|surfGuid"
                string polyKey = "default";
                string polyName = "Polysurface";
                var nameParts = obj.Attributes.Name?.Split('|');
                if (nameParts?.Length >= 3 && nameParts[0] == "SS")
                {
                    polyKey  = nameParts[1];
                    polyName = nameParts[2];
                }

                if (!groupMap.TryGetValue(polyKey, out var poly))
                {
                    poly = new Polysurface { Name = polyName };
                    scene.InternalAdd(poly);
                    groupMap[polyKey] = poly;
                }

                poly.AddSurface(surf);
            }

            // Infer G0 constraints from coincident boundary control points
            InferG0Constraints(scene);

            GD.Print($"[Rhino3dmIO] Loaded {path}: {scene.Polysurfaces.Count} polysurface(s)");
            return scene;
        }

        // ─── Conversion helpers ───────────────────────────────────────────────────

        private static Rhino.Geometry.NurbsSurface ToRhino(NurbsSurface geo)
        {
            int uCount = geo.CpCountU;
            int vCount = geo.CpCountV;

            var rs = Rhino.Geometry.NurbsSurface.Create(
                3,          // dimension
                true,       // isRational
                geo.DegreeU + 1,
                geo.DegreeV + 1,
                uCount,
                vCount);

            // Knots (Rhino uses (n+p) knots, not (n+p+2) like The NURBS Book;
            // it omits the first and last knot values)
            for (int i = 1; i < geo.KnotsU.Length - 1; i++)
                rs.KnotsU[i - 1] = geo.KnotsU[i];
            for (int j = 1; j < geo.KnotsV.Length - 1; j++)
                rs.KnotsV[j - 1] = geo.KnotsV[j];

            // Control points
            for (int i = 0; i < uCount; i++)
                for (int j = 0; j < vCount; j++)
                {
                    var pt = geo.ControlPoints[i, j];
                    double w = geo.Weights[i, j];
                    rs.Points.SetPoint(i, j, new Rhino.Geometry.Point4d(pt.X * w, pt.Y * w, pt.Z * w, w));
                }

            return rs;
        }

        private static NurbsSurface FromRhino(Rhino.Geometry.NurbsSurface rs)
        {
            int uCount = rs.Points.CountU;
            int vCount = rs.Points.CountV;
            int degU   = rs.Degree(0);
            int degV   = rs.Degree(1);

            var geo = new NurbsSurface
            {
                DegreeU    = degU,
                DegreeV    = degV,
                SpanCountU = uCount - degU,
                SpanCountV = vCount - degV,
                ControlPoints = new Vector3[uCount, vCount],
                Weights       = new double[uCount, vCount],
                KnotsU = new double[rs.KnotsU.Count + 2],
                KnotsV = new double[rs.KnotsV.Count + 2],
            };

            // Reconstruct full knot vector (add back the first/last clamped values)
            geo.KnotsU[0] = rs.KnotsU[0];
            for (int i = 0; i < rs.KnotsU.Count; i++)
                geo.KnotsU[i + 1] = rs.KnotsU[i];
            geo.KnotsU[geo.KnotsU.Length - 1] = rs.KnotsU[rs.KnotsU.Count - 1];

            geo.KnotsV[0] = rs.KnotsV[0];
            for (int j = 0; j < rs.KnotsV.Count; j++)
                geo.KnotsV[j + 1] = rs.KnotsV[j];
            geo.KnotsV[geo.KnotsV.Length - 1] = rs.KnotsV[rs.KnotsV.Count - 1];

            // Control points — rhino3dm GetPoint uses an out parameter
            for (int i = 0; i < uCount; i++)
                for (int j = 0; j < vCount; j++)
                {
                    rs.Points.GetPoint(i, j, out Rhino.Geometry.Point4d pt4);
                    double w = pt4.W;
                    geo.Weights[i, j] = w;
                    geo.ControlPoints[i, j] = w > 1e-10
                        ? new Vector3((float)(pt4.X / w), (float)(pt4.Y / w), (float)(pt4.Z / w))
                        : new Vector3((float)pt4.X, (float)pt4.Y, (float)pt4.Z);
                }

            return geo;
        }

        // ─── G0 constraint inference ──────────────────────────────────────────────

        private static void InferG0Constraints(SculptScene scene)
        {
            // Collect all (surface, edge) pairs → boundary control point arrays
            // For each pair, check if boundary CPs match within tolerance.

            var allSurfaces = new System.Collections.Generic.List<(Polysurface poly, SculptSurface surf)>();
            foreach (var poly in scene.Polysurfaces)
                foreach (var s in poly.Surfaces)
                    allSurfaces.Add((poly, s));

            for (int a = 0; a < allSurfaces.Count; a++)
            {
                for (int b = a + 1; b < allSurfaces.Count; b++)
                {
                    var (polyA, surfA) = allSurfaces[a];
                    var (polyB, surfB) = allSurfaces[b];

                    foreach (SurfaceEdge eA in Enum.GetValues<SurfaceEdge>())
                    foreach (SurfaceEdge eB in Enum.GetValues<SurfaceEdge>())
                    {
                        if (BoundariesCoincide(surfA, eA, surfB, eB))
                        {
                            // Add to whichever poly owns both (or polyA if different polys)
                            var constraint = new EdgeConstraint(surfA, eA, surfB, eB, Continuity.G0);
                            polyA.AddConstraint(constraint);
                            goto nextPair;
                        }
                    }
                    nextPair:;
                }
            }
        }

        private static bool BoundariesCoincide(
            SculptSurface sA, SurfaceEdge eA,
            SculptSurface sB, SurfaceEdge eB)
        {
            var gA = sA.Geometry;
            var gB = sB.Geometry;

            int lenA = GetEdgeLen(gA, eA);
            int lenB = GetEdgeLen(gB, eB);
            if (lenA != lenB) return false;

            for (int k = 0; k < lenA; k++)
            {
                var (ua, va) = GetEdgeIdx(gA, eA, k);
                var (ub, vb) = GetEdgeIdx(gB, eB, k);
                float dist = gA.ControlPoints[ua, va].DistanceTo(gB.ControlPoints[ub, vb]);
                if (dist > G0Tolerance) return false;
            }
            return true;
        }

        private static int GetEdgeLen(NurbsSurface g, SurfaceEdge e) => e switch
        {
            SurfaceEdge.UMin or SurfaceEdge.UMax => g.CpCountV,
            _ => g.CpCountU
        };

        private static (int u, int v) GetEdgeIdx(NurbsSurface g, SurfaceEdge e, int k) => e switch
        {
            SurfaceEdge.UMin => (0, k),
            SurfaceEdge.UMax => (g.CpCountU - 1, k),
            SurfaceEdge.VMin => (k, 0),
            SurfaceEdge.VMax => (k, g.CpCountV - 1),
            _ => (0, 0)
        };

        // ─── Color conversion ─────────────────────────────────────────────────────

        private static Godot.Color DrawingColorToGodot(System.Drawing.Color c)
            => new Godot.Color(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        private static System.Drawing.Color GodotColorToDrawing(Godot.Color c)
            => System.Drawing.Color.FromArgb(
                (int)(c.A * 255), (int)(c.R * 255), (int)(c.G * 255), (int)(c.B * 255));
    }
}
