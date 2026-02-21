using Godot;

namespace SplineSculptor.Model.Undo
{
    public class AttachPatchCommand : ICommand
    {
        private readonly Polysurface _polysurface;
        private readonly SculptSurface _existing;
        private readonly SurfaceEdge _edge;
        private SculptSurface? _newSurface;

        public string Description => $"Attach patch to {_edge}";

        public AttachPatchCommand(Polysurface polysurface, SculptSurface existing, SurfaceEdge edge)
        {
            _polysurface = polysurface;
            _existing = existing;
            _edge = edge;
        }

        public void Execute()
        {
            GD.Print($"[Cmd] AttachPatch {_edge} on '{_polysurface.Name}'");
            _newSurface = _polysurface.AttachPatch(_existing, _edge);
        }

        public void Undo()
        {
            if (_newSurface != null)
            {
                GD.Print($"[Cmd] Undo AttachPatch {_edge} on '{_polysurface.Name}'");
                _polysurface.RemoveSurface(_newSurface);
            }
        }
    }
}
