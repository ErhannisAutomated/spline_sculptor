using System;
using Godot;
using SplineSculptor.Math;

namespace SplineSculptor.Model
{
    /// <summary>
    /// A NURBS surface with visual state (color, selection).
    /// Fires GeometryChanged when control points move.
    /// </summary>
    public class SculptSurface
    {
        public Guid Id { get; } = Guid.NewGuid();
        public NurbsSurface Geometry { get; set; }
        public Color SurfaceColor { get; set; } = new Color(0.4f, 0.6f, 0.9f, 0.75f);
        public bool IsSelected { get; set; } = false;

        /// <summary>Fired after any control point position change.</summary>
        public event Action? GeometryChanged;

        public SculptSurface(NurbsSurface geometry)
        {
            Geometry = geometry;
        }

        /// <summary>
        /// Move control point (u, v) to newPosition and fire GeometryChanged.
        /// Do not call directly — always wrap in a MoveControlPointCommand.
        /// </summary>
        internal void MoveControlPoint(int u, int v, Vector3 newPosition)
        {
            Geometry.ControlPoints[u, v] = newPosition;
            GeometryChanged?.Invoke();
        }

        /// <summary>Public for use by commands only.</summary>
        public void ApplyControlPointMove(int u, int v, Vector3 newPosition)
        {
            MoveControlPoint(u, v, newPosition);
        }

        public Vector3 GetControlPoint(int u, int v)
            => Geometry.ControlPoints[u, v];
    }
}
