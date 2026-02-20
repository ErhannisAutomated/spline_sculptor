namespace SplineSculptor.Model.Undo
{
    public class DeletePolysurfaceCommand : ICommand
    {
        private readonly SculptScene _scene;
        private readonly Polysurface _polysurface;

        public string Description => "Delete polysurface";

        public DeletePolysurfaceCommand(SculptScene scene, Polysurface polysurface)
        {
            _scene = scene;
            _polysurface = polysurface;
        }

        public void Execute() => _scene.InternalRemove(_polysurface);
        public void Undo()    => _scene.InternalAdd(_polysurface);
    }
}
