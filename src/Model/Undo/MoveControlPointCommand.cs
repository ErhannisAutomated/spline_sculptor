using Godot;

namespace SplineSculptor.Model.Undo
{
    /// <summary>
    /// Moves a single NURBS control point, with optional constraint enforcement.
    /// </summary>
    public class MoveControlPointCommand : ICommand
    {
        private readonly SculptSurface _surface;
        private readonly Polysurface? _polysurface;
        private readonly int _u;
        private readonly int _v;
        private readonly Vector3 _oldPos;
        private readonly Vector3 _newPos;

        public string Description => "Move control point";

        public MoveControlPointCommand(
            SculptSurface surface,
            int u, int v,
            Vector3 oldPos, Vector3 newPos,
            Polysurface? polysurface = null)
        {
            _surface = surface;
            _polysurface = polysurface;
            _u = u;
            _v = v;
            _oldPos = oldPos;
            _newPos = newPos;
        }

        public void Execute()
        {
            _surface.ApplyControlPointMove(_u, _v, _newPos);
            _polysurface?.EnforceConstraints(_surface);
        }

        public void Undo()
        {
            _surface.ApplyControlPointMove(_u, _v, _oldPos);
            _polysurface?.EnforceConstraints(_surface);
        }
    }
}
