using SplineSculptor.Math;

namespace SplineSculptor.Model.Undo
{
    public enum KnotDirection { U, V }

    public class InsertKnotCommand : ICommand
    {
        private readonly SculptSurface _surface;
        private readonly double _t;
        private readonly KnotDirection _direction;
        private NurbsSurface? _snapshotBefore;

        public string Description => $"Insert knot {_direction} at {_t:F3}";

        public InsertKnotCommand(SculptSurface surface, double t, KnotDirection direction)
        {
            _surface = surface;
            _t = t;
            _direction = direction;
        }

        public void Execute()
        {
            // Save snapshot before modification
            _snapshotBefore = _surface.Geometry.Clone();

            if (_direction == KnotDirection.U)
                _surface.Geometry.InsertKnotU(_t);
            else
                _surface.Geometry.InsertKnotV(_t);

            // Notify listeners that geometry changed
            _surface.ApplyControlPointMove(0, 0, _surface.Geometry.ControlPoints[0, 0]);
        }

        public void Undo()
        {
            if (_snapshotBefore != null)
            {
                _surface.Geometry = _snapshotBefore;
                _surface.ApplyControlPointMove(0, 0, _surface.Geometry.ControlPoints[0, 0]);
            }
        }
    }
}
