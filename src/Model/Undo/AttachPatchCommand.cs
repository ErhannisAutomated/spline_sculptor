namespace SplineSculptor.Model.Undo
{
    public class AttachPatchCommand : ICommand
    {
        private readonly Polysurface _polysurface;
        private readonly SculptSurface _existing;
        private readonly SurfaceEdge _edge;
        private SculptSurface? _newSurface;

        public string Description => "Attach patch";

        public AttachPatchCommand(Polysurface polysurface, SculptSurface existing, SurfaceEdge edge)
        {
            _polysurface = polysurface;
            _existing = existing;
            _edge = edge;
        }

        public void Execute()
        {
            _newSurface = _polysurface.AttachPatch(_existing, _edge);
        }

        public void Undo()
        {
            if (_newSurface != null)
                _polysurface.RemoveSurface(_newSurface);
        }
    }
}
