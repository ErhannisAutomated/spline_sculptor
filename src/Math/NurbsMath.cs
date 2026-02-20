using System;

namespace SplineSculptor.Math
{
    /// <summary>
    /// Cox-de Boor NURBS basis function utilities.
    /// </summary>
    public static class NurbsMath
    {
        /// <summary>
        /// Find the knot span index i such that knots[i] &lt;= t &lt; knots[i+1].
        /// Uses binary search (Algorithm A2.1 from "The NURBS Book").
        /// n = number of basis functions - 1 = controlPointCount - 1
        /// </summary>
        public static int FindSpan(int n, int degree, double t, double[] knots)
        {
            // Edge case: t at end of domain
            if (t >= knots[n + 1])
                return n;

            int low = degree;
            int high = n + 1;
            int mid = (low + high) / 2;

            while (t < knots[mid] || t >= knots[mid + 1])
            {
                if (t < knots[mid])
                    high = mid;
                else
                    low = mid;

                mid = (low + high) / 2;
            }

            return mid;
        }

        /// <summary>
        /// Compute all non-zero basis functions N_{span-degree,degree}(t) .. N_{span,degree}(t).
        /// Returns array of length (degree+1).
        /// Algorithm A2.2 from "The NURBS Book".
        /// </summary>
        public static double[] BasisFunctions(int span, double t, int degree, double[] knots)
        {
            double[] N = new double[degree + 1];
            double[] left = new double[degree + 1];
            double[] right = new double[degree + 1];

            N[0] = 1.0;

            for (int j = 1; j <= degree; j++)
            {
                left[j] = t - knots[span + 1 - j];
                right[j] = knots[span + j] - t;
                double saved = 0.0;

                for (int r = 0; r < j; r++)
                {
                    double temp = N[r] / (right[r + 1] + left[j - r]);
                    N[r] = saved + right[r + 1] * temp;
                    saved = left[j - r] * temp;
                }

                N[j] = saved;
            }

            return N;
        }

        /// <summary>
        /// Compute basis functions and their derivatives up to order nDerivs.
        /// Returns a 2D array ders[k][j] where k is the derivative order and j is the basis index.
        /// k=0: basis functions, k=1: first derivatives, etc.
        /// Algorithm A2.3 from "The NURBS Book".
        /// </summary>
        public static double[,] BasisFunctionDerivatives(
            int span, double t, int degree, int nDerivs, double[] knots)
        {
            double[,] ndu = new double[degree + 1, degree + 1];
            double[] left = new double[degree + 1];
            double[] right = new double[degree + 1];

            ndu[0, 0] = 1.0;

            for (int j = 1; j <= degree; j++)
            {
                left[j] = t - knots[span + 1 - j];
                right[j] = knots[span + j] - t;
                double saved = 0.0;

                for (int r = 0; r < j; r++)
                {
                    // Lower triangle
                    ndu[j, r] = right[r + 1] + left[j - r];
                    double temp = ndu[r, j - 1] / ndu[j, r];
                    // Upper triangle
                    ndu[r, j] = saved + right[r + 1] * temp;
                    saved = left[j - r] * temp;
                }
                ndu[j, j] = saved;
            }

            int d = System.Math.Min(nDerivs, degree);
            double[,] ders = new double[d + 1, degree + 1];

            // Load basis functions
            for (int j = 0; j <= degree; j++)
                ders[0, j] = ndu[j, degree];

            // Compute derivatives
            double[,] a = new double[2, degree + 1];
            for (int r = 0; r <= degree; r++)
            {
                int s1 = 0, s2 = 1;
                a[0, 0] = 1.0;

                for (int k = 1; k <= d; k++)
                {
                    double dd = 0.0;
                    int rk = r - k;
                    int pk = degree - k;

                    if (r >= k)
                    {
                        a[s2, 0] = a[s1, 0] / ndu[pk + 1, rk];
                        dd = a[s2, 0] * ndu[rk, pk];
                    }

                    int j1 = rk >= -1 ? 1 : -rk;
                    int j2 = (r - 1) <= pk ? k - 1 : degree - r;

                    for (int j = j1; j <= j2; j++)
                    {
                        a[s2, j] = (a[s1, j] - a[s1, j - 1]) / ndu[pk + 1, rk + j];
                        dd += a[s2, j] * ndu[rk + j, pk];
                    }

                    if (r <= pk)
                    {
                        a[s2, k] = -a[s1, k - 1] / ndu[pk + 1, r];
                        dd += a[s2, k] * ndu[r, pk];
                    }

                    ders[k, r] = dd;
                    // Swap rows
                    int temp2 = s1; s1 = s2; s2 = temp2;
                }
            }

            // Multiply through by the correct factors
            int factor = degree;
            for (int k = 1; k <= d; k++)
            {
                for (int j = 0; j <= degree; j++)
                    ders[k, j] *= factor;
                factor *= (degree - k);
            }

            return ders;
        }

        /// <summary>
        /// Build a clamped uniform knot vector for the given degree and number of control points.
        /// cpCount = degree + spanCount + 1 (so n = cpCount - 1)
        /// Resulting knot count = cpCount + degree + 1
        /// </summary>
        public static double[] ClampedKnotVector(int degree, int cpCount)
        {
            int n = cpCount - 1;
            int m = n + degree + 1;
            double[] knots = new double[m + 1];

            // First degree+1 knots = 0
            for (int i = 0; i <= degree; i++)
                knots[i] = 0.0;

            // Interior knots (uniform)
            int internalCount = m - 2 * degree - 1; // = n - degree
            for (int i = 1; i <= internalCount; i++)
                knots[degree + i] = (double)i / (internalCount + 1);

            // Last degree+1 knots = 1
            for (int i = 0; i <= degree; i++)
                knots[m - degree + i] = 1.0;

            return knots;
        }

        /// <summary>
        /// Build the standard clamped Bezier knot vector for a single span of the given degree.
        /// e.g., degree=3 → [0,0,0,0, 1,1,1,1]
        /// </summary>
        public static double[] BezierKnotVector(int degree)
        {
            double[] knots = new double[2 * (degree + 1)];
            for (int i = 0; i <= degree; i++)
            {
                knots[i] = 0.0;
                knots[degree + 1 + i] = 1.0;
            }
            return knots;
        }
    }
}
