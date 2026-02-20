namespace SplineSculptor.Model.Undo
{
    public class AddSurfaceCommand : ICommand
    {
        private readonly Polysurface _polysurface;
        private readonly SculptSurface _surface;

        public string Description => "Add surface";

        public AddSurfaceCommand(Polysurface polysurface, SculptSurface surface)
        {
            _polysurface = polysurface;
            _surface = surface;
        }

        public void Execute() => _polysurface.AddSurface(_surface);
        public void Undo()    => _polysurface.RemoveSurface(_surface);
    }
}
