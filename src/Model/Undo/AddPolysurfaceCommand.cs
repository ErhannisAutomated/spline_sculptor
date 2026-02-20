namespace SplineSculptor.Model.Undo
{
    public class AddPolysurfaceCommand : ICommand
    {
        private readonly SculptScene _scene;
        private readonly Polysurface _polysurface;

        public string Description => "Add polysurface";

        public AddPolysurfaceCommand(SculptScene scene, Polysurface polysurface)
        {
            _scene = scene;
            _polysurface = polysurface;
        }

        public void Execute() => _scene.InternalAdd(_polysurface);
        public void Undo()    => _scene.InternalRemove(_polysurface);
    }
}
