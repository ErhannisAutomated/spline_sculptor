using System;
using Godot;

namespace SplineSculptor.Math
{
    /// <summary>
    /// NURBS surface: degree, control points, knots, weights.
    /// Supports evaluation, tessellation, and knot insertion.
    /// </summary>
    public class NurbsSurface
    {
        public int DegreeU;
        public int DegreeV;

        /// <summary>Number of Bezier-equivalent spans in each direction.</summary>
        public int SpanCountU;
        public int SpanCountV;

        /// <summary>
        /// Control points [u_index, v_index].
        /// Size: (DegreeU + SpanCountU) × (DegreeV + SpanCountV)
        /// </summary>
        public Vector3[,] ControlPoints;

        /// <summary>Homogeneous weights, same size as ControlPoints. Default 1.0.</summary>
        public double[,] Weights;

        /// <summary>
        /// Knot vectors. Clamped.
        /// Length in U: cpCountU + DegreeU + 1
        /// </summary>
        public double[] KnotsU;
        public double[] KnotsV;

        public int CpCountU => DegreeU + SpanCountU;
        public int CpCountV => DegreeV + SpanCountV;

        // ─── Evaluation ──────────────────────────────────────────────────────────

        /// <summary>Evaluate the NURBS surface at parameter (u, v) ∈ [0,1]×[0,1].</summary>
        public Vector3 Evaluate(double u, double v)
        {
            int nu = CpCountU - 1;
            int nv = CpCountV - 1;

            int spanU = NurbsMath.FindSpan(nu, DegreeU, u, KnotsU);
            int spanV = NurbsMath.FindSpan(nv, DegreeV, v, KnotsV);

            double[] Nu = NurbsMath.BasisFunctions(spanU, u, DegreeU, KnotsU);
            double[] Nv = NurbsMath.BasisFunctions(spanV, v, DegreeV, KnotsV);

            // Rational evaluation: sum of weighted control points / sum of weights
            double wx = 0, wy = 0, wz = 0, w = 0;

            for (int i = 0; i <= DegreeU; i++)
            {
                int uIdx = spanU - DegreeU + i;
                for (int j = 0; j <= DegreeV; j++)
                {
                    int vIdx = spanV - DegreeV + j;
                    double basisW = Nu[i] * Nv[j] * Weights[uIdx, vIdx];
                    wx += basisW * ControlPoints[uIdx, vIdx].X;
                    wy += basisW * ControlPoints[uIdx, vIdx].Y;
                    wz += basisW * ControlPoints[uIdx, vIdx].Z;
                    w += basisW;
                }
            }

            return w > 1e-10 ? new Vector3((float)(wx / w), (float)(wy / w), (float)(wz / w))
                             : Vector3.Zero;
        }

        /// <summary>
        /// Evaluate surface point and partial derivatives dS/du, dS/dv.
        /// Uses rational derivative formula from The NURBS Book, Algorithm A4.4.
        /// </summary>
        public (Vector3 point, Vector3 dU, Vector3 dV) EvaluateWithDerivatives(double u, double v)
        {
            int nu = CpCountU - 1;
            int nv = CpCountV - 1;

            int spanU = NurbsMath.FindSpan(nu, DegreeU, u, KnotsU);
            int spanV = NurbsMath.FindSpan(nv, DegreeV, v, KnotsV);

            // Basis functions + 1st derivatives in each direction
            double[,] dersU = NurbsMath.BasisFunctionDerivatives(spanU, u, DegreeU, 1, KnotsU);
            double[,] dersV = NurbsMath.BasisFunctionDerivatives(spanV, v, DegreeV, 1, KnotsV);

            // Weighted homogeneous surface and derivatives
            double[,] Sw = new double[3, 3]; // [dim 0..2=xyz, 3=w][deriv 0..2]
            // Actually let's use 4 homogeneous coords: X, Y, Z, W
            double[] S  = new double[4]; // S[0..2]=xyz*w, S[3]=w (the surface point)
            double[] Su = new double[4]; // d/du
            double[] Sv = new double[4]; // d/dv

            for (int i = 0; i <= DegreeU; i++)
            {
                int uIdx = spanU - DegreeU + i;
                for (int j = 0; j <= DegreeV; j++)
                {
                    int vIdx = spanV - DegreeV + j;
                    double wij = Weights[uIdx, vIdx];
                    float px = ControlPoints[uIdx, vIdx].X;
                    float py = ControlPoints[uIdx, vIdx].Y;
                    float pz = ControlPoints[uIdx, vIdx].Z;

                    double b  = dersU[0, i] * dersV[0, j] * wij;
                    double bu = dersU[1, i] * dersV[0, j] * wij;
                    double bv = dersU[0, i] * dersV[1, j] * wij;

                    S[0] += b * px;  S[1] += b * py;  S[2] += b * pz;  S[3] += b;
                    Su[0] += bu * px; Su[1] += bu * py; Su[2] += bu * pz; Su[3] += bu;
                    Sv[0] += bv * px; Sv[1] += bv * py; Sv[2] += bv * pz; Sv[3] += bv;
                }
            }

            double winv = S[3] > 1e-10 ? 1.0 / S[3] : 0.0;
            var point = new Vector3((float)(S[0] * winv), (float)(S[1] * winv), (float)(S[2] * winv));

            // Rational first derivative: dP/du = (dA/du - w'*P) / w
            var dU = new Vector3(
                (float)((Su[0] - Su[3] * S[0] * winv) * winv),
                (float)((Su[1] - Su[3] * S[1] * winv) * winv),
                (float)((Su[2] - Su[3] * S[2] * winv) * winv));

            var dV = new Vector3(
                (float)((Sv[0] - Sv[3] * S[0] * winv) * winv),
                (float)((Sv[1] - Sv[3] * S[1] * winv) * winv),
                (float)((Sv[2] - Sv[3] * S[2] * winv) * winv));

            return (point, dU, dV);
        }

        /// <summary>Surface normal at (u,v), computed as cross(dU, dV).</summary>
        public Vector3 Normal(double u, double v)
        {
            var (_, dU, dV) = EvaluateWithDerivatives(u, v);
            var n = dU.Cross(dV);
            return n.LengthSquared() > 1e-10f ? n.Normalized() : Vector3.Up;
        }

        // ─── Tessellation ────────────────────────────────────────────────────────

        /// <summary>
        /// Sample the surface on a (samplesU × samplesV) grid and triangulate.
        /// Returns arrays suitable for Godot's ArrayMesh.
        /// </summary>
        public (Vector3[] verts, Vector3[] normals, int[] indices, Vector2[] uvs)
            Tessellate(int samplesU, int samplesV)
        {
            if (samplesU < 2) samplesU = 2;
            if (samplesV < 2) samplesV = 2;

            int vertCount = samplesU * samplesV;
            var verts   = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs     = new Vector2[vertCount];

            for (int i = 0; i < samplesU; i++)
            {
                double u = i / (double)(samplesU - 1);
                // Clamp u to avoid hitting exact 1.0 boundary which could cause span issues
                if (u > 1.0) u = 1.0;

                for (int j = 0; j < samplesV; j++)
                {
                    double v = j / (double)(samplesV - 1);
                    if (v > 1.0) v = 1.0;

                    int idx = i * samplesV + j;
                    var (pt, dU, dV) = EvaluateWithDerivatives(u, v);
                    verts[idx] = pt;
                    var n = dU.Cross(dV);
                    normals[idx] = n.LengthSquared() > 1e-10f ? n.Normalized() : Vector3.Up;
                    uvs[idx] = new Vector2((float)u, (float)v);
                }
            }

            // Build triangle index list (two triangles per quad)
            int quadCount = (samplesU - 1) * (samplesV - 1);
            var indices = new int[quadCount * 6];
            int idx2 = 0;
            for (int i = 0; i < samplesU - 1; i++)
            {
                for (int j = 0; j < samplesV - 1; j++)
                {
                    int a = i * samplesV + j;
                    int b = a + 1;
                    int c = (i + 1) * samplesV + j;
                    int d = c + 1;

                    // Triangle 1
                    indices[idx2++] = a;
                    indices[idx2++] = c;
                    indices[idx2++] = b;
                    // Triangle 2
                    indices[idx2++] = b;
                    indices[idx2++] = c;
                    indices[idx2++] = d;
                }
            }

            return (verts, normals, indices, uvs);
        }

        // ─── Knot Insertion (Boehm's algorithm) ──────────────────────────────────

        /// <summary>
        /// Insert knot t into KnotsU, refining control points accordingly.
        /// This increases SpanCountU by 1 and adds a row of control points.
        /// </summary>
        public void InsertKnotU(double t)
        {
            int n = CpCountU - 1; // last CP index in U
            int m = CpCountV - 1; // last CP index in V

            int knotSpan = NurbsMath.FindSpan(n, DegreeU, t, KnotsU);

            // New knot vector
            double[] newKnotsU = new double[KnotsU.Length + 1];
            for (int i = 0; i <= knotSpan; i++)
                newKnotsU[i] = KnotsU[i];
            newKnotsU[knotSpan + 1] = t;
            for (int i = knotSpan + 1; i < KnotsU.Length; i++)
                newKnotsU[i + 1] = KnotsU[i];

            // New control point grid: one extra row in U
            int newCpU = CpCountU + 1;
            var newCP = new Vector3[newCpU, CpCountV];
            var newW  = new double[newCpU, CpCountV];

            for (int j = 0; j <= m; j++)
            {
                // Alpha values for this column
                double[] alpha = ComputeInsertionAlpha(t, knotSpan, DegreeU, KnotsU);

                // New control points for column j
                for (int i = 0; i <= knotSpan - DegreeU; i++)
                {
                    newCP[i, j] = ControlPoints[i, j];
                    newW[i, j]  = Weights[i, j];
                }

                for (int i = knotSpan - DegreeU + 1; i <= knotSpan; i++)
                {
                    double a = alpha[i - (knotSpan - DegreeU)];
                    double wa = a * Weights[i, j] + (1 - a) * Weights[i - 1, j];
                    newCP[i, j] = (float)(a * Weights[i, j]) * ControlPoints[i, j]
                                + (float)((1 - a) * Weights[i - 1, j]) * ControlPoints[i - 1, j];
                    if (wa > 1e-10) newCP[i, j] /= (float)wa;
                    newW[i, j] = wa;
                }

                for (int i = knotSpan + 1; i < newCpU; i++)
                {
                    newCP[i, j] = ControlPoints[i - 1, j];
                    newW[i, j]  = Weights[i - 1, j];
                }
            }

            ControlPoints = newCP;
            Weights = newW;
            KnotsU = newKnotsU;
            SpanCountU++;
        }

        /// <summary>Insert knot t into KnotsV (symmetric to InsertKnotU).</summary>
        public void InsertKnotV(double t)
        {
            int n = CpCountU - 1;
            int m = CpCountV - 1;

            int knotSpan = NurbsMath.FindSpan(m, DegreeV, t, KnotsV);

            double[] newKnotsV = new double[KnotsV.Length + 1];
            for (int i = 0; i <= knotSpan; i++)
                newKnotsV[i] = KnotsV[i];
            newKnotsV[knotSpan + 1] = t;
            for (int i = knotSpan + 1; i < KnotsV.Length; i++)
                newKnotsV[i + 1] = KnotsV[i];

            int newCpV = CpCountV + 1;
            var newCP = new Vector3[CpCountU, newCpV];
            var newW  = new double[CpCountU, newCpV];

            for (int i = 0; i <= n; i++)
            {
                double[] alpha = ComputeInsertionAlpha(t, knotSpan, DegreeV, KnotsV);

                for (int j = 0; j <= knotSpan - DegreeV; j++)
                {
                    newCP[i, j] = ControlPoints[i, j];
                    newW[i, j]  = Weights[i, j];
                }

                for (int j = knotSpan - DegreeV + 1; j <= knotSpan; j++)
                {
                    double a = alpha[j - (knotSpan - DegreeV)];
                    double wa = a * Weights[i, j] + (1 - a) * Weights[i, j - 1];
                    newCP[i, j] = (float)(a * Weights[i, j]) * ControlPoints[i, j]
                                + (float)((1 - a) * Weights[i, j - 1]) * ControlPoints[i, j - 1];
                    if (wa > 1e-10) newCP[i, j] /= (float)wa;
                    newW[i, j] = wa;
                }

                for (int j = knotSpan + 1; j < newCpV; j++)
                {
                    newCP[i, j] = ControlPoints[i, j - 1];
                    newW[i, j]  = Weights[i, j - 1];
                }
            }

            ControlPoints = newCP;
            Weights = newW;
            KnotsV = newKnotsV;
            SpanCountV++;
        }

        private static double[] ComputeInsertionAlpha(
            double t, int knotSpan, int degree, double[] knots)
        {
            double[] alpha = new double[degree + 1];
            for (int i = 0; i <= degree; i++)
            {
                int idx = knotSpan - degree + i;
                double denom = knots[idx + degree] - knots[idx];
                alpha[i] = denom < 1e-10 ? 0.0 : (t - knots[idx]) / denom;
            }
            return alpha;
        }

        // ─── Factory Methods ──────────────────────────────────────────────────────

        /// <summary>
        /// Create a flat 4×4 Bezier patch (degree 3, single span, all weights=1).
        /// Control points form a 1×1 flat grid in XZ, centred at origin.
        /// </summary>
        public static NurbsSurface CreateBezierPatch()
        {
            const int degree = 3;
            const int cpCount = 4; // degree + 1 for a single span

            var surf = new NurbsSurface
            {
                DegreeU = degree,
                DegreeV = degree,
                SpanCountU = 1,
                SpanCountV = 1,
                KnotsU = NurbsMath.BezierKnotVector(degree),
                KnotsV = NurbsMath.BezierKnotVector(degree),
                ControlPoints = new Vector3[cpCount, cpCount],
                Weights = new double[cpCount, cpCount],
            };

            // Flat XZ grid from (-1,-1) to (1,1)
            for (int i = 0; i < cpCount; i++)
            {
                float x = -1.0f + 2.0f * i / (cpCount - 1);
                for (int j = 0; j < cpCount; j++)
                {
                    float z = -1.0f + 2.0f * j / (cpCount - 1);
                    surf.ControlPoints[i, j] = new Vector3(x, 0, z);
                    surf.Weights[i, j] = 1.0;
                }
            }

            return surf;
        }

        /// <summary>
        /// Create a flat multi-span grid of Bezier patches.
        /// Each span uses uniform interior knots.
        /// </summary>
        public static NurbsSurface CreateGrid(int spansU, int spansV)
        {
            const int degree = 3;
            int cpU = degree + spansU; // degree*spans + 1 for open B-spline; simplified here
            int cpV = degree + spansV;

            // For a proper clamped B-spline with spansU spans of degree d:
            // cpCount = d * spansU + 1... but for the simpler Bezier-segment approach:
            // cpCount = d + spansU (same as single span when spans=1 → cpCount=4)
            // Actually correct formula: n+1 control points, m+1 knots where m=n+p+1
            // For degree p and k internal knots (each with multiplicity 1):
            //   n = p + k + 1 (with k = spansU - 1 internal knots for spansU spans)
            //   so cpCount = p + spansU + 1 for a curve
            // For simplicity and to match the plan (cpCount = degree + spanCount):
            // We'll use the simpler interpretation: each "span" adds one CP and one internal knot.

            var surf = new NurbsSurface
            {
                DegreeU = degree,
                DegreeV = degree,
                SpanCountU = spansU,
                SpanCountV = spansV,
                KnotsU = NurbsMath.ClampedKnotVector(degree, cpU),
                KnotsV = NurbsMath.ClampedKnotVector(degree, cpV),
                ControlPoints = new Vector3[cpU, cpV],
                Weights = new double[cpU, cpV],
            };

            for (int i = 0; i < cpU; i++)
            {
                float x = -1.0f + 2.0f * i / (cpU - 1);
                for (int j = 0; j < cpV; j++)
                {
                    float z = -1.0f + 2.0f * j / (cpV - 1);
                    surf.ControlPoints[i, j] = new Vector3(x, 0, z);
                    surf.Weights[i, j] = 1.0;
                }
            }

            return surf;
        }

        // ─── Deep Clone ───────────────────────────────────────────────────────────

        public NurbsSurface Clone()
        {
            int cu = CpCountU;
            int cv = CpCountV;
            var clone = new NurbsSurface
            {
                DegreeU    = DegreeU,
                DegreeV    = DegreeV,
                SpanCountU = SpanCountU,
                SpanCountV = SpanCountV,
                KnotsU     = (double[])KnotsU.Clone(),
                KnotsV     = (double[])KnotsV.Clone(),
                ControlPoints = new Vector3[cu, cv],
                Weights       = new double[cu, cv],
            };

            for (int i = 0; i < cu; i++)
                for (int j = 0; j < cv; j++)
                {
                    clone.ControlPoints[i, j] = ControlPoints[i, j];
                    clone.Weights[i, j]        = Weights[i, j];
                }

            return clone;
        }
    }
}
