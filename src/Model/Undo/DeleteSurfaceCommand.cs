namespace SplineSculptor.Model.Undo
{
    public class DeleteSurfaceCommand : ICommand
    {
        private readonly Polysurface _polysurface;
        private readonly SculptSurface _surface;

        public string Description => "Delete surface";

        public DeleteSurfaceCommand(Polysurface polysurface, SculptSurface surface)
        {
            _polysurface = polysurface;
            _surface = surface;
        }

        public void Execute() => _polysurface.RemoveSurface(_surface);
        public void Undo()    => _polysurface.AddSurface(_surface);
    }
}
