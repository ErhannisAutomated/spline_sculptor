using SplineSculptor.Model;

namespace SplineSculptor.Interaction
{
    /// <summary>
    /// Controls what DesktopInteraction targets on a left-click.
    /// Auto: point > edge > surface (smallest first).
    /// Q/W/E/R keyboard shortcuts switch between tools.
    /// </summary>
    public enum SelectionTool { Auto, Point, Edge, Surface }

    /// <summary>
    /// How a click modifies the current selection.
    /// No modifier = Replace, Shift = Add, Ctrl = XOR, Ctrl+Shift = Remove.
    /// </summary>
    public enum SelectionModifier { Replace, Add, XOR, Remove }

    /// <summary>
    /// Identifies one boundary edge of one NURBS surface within a polysurface.
    /// </summary>
    public readonly struct EdgeRef
    {
        public readonly SculptSurface Surface;
        public readonly Polysurface   Poly;
        public readonly SurfaceEdge   Edge;

        public EdgeRef(SculptSurface surface, Polysurface poly, SurfaceEdge edge)
        {
            Surface = surface;
            Poly    = poly;
            Edge    = edge;
        }
    }
}
