using Godot;

namespace SplineSculptor.Model.Undo
{
    public class DeleteSurfaceCommand : ICommand
    {
        private readonly Polysurface   _polysurface;
        private readonly SculptSurface _surface;

        public string Description => "Delete surface";

        public DeleteSurfaceCommand(Polysurface polysurface, SculptSurface surface)
        {
            _polysurface = polysurface;
            _surface     = surface;
        }

        public void Execute()
        {
            GD.Print($"[Cmd] DeleteSurface from '{_polysurface.Name}' ({_polysurface.Surfaces.Count} surfaces before)");
            _polysurface.RemoveSurface(_surface);
        }

        public void Undo()
        {
            GD.Print($"[Cmd] Undo DeleteSurface → restore to '{_polysurface.Name}'");
            _polysurface.AddSurface(_surface);
        }
    }
}
