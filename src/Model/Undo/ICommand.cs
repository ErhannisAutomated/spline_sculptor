namespace SplineSculptor.Model.Undo
{
    public interface ICommand
    {
        void Execute();
        void Undo();
        string Description { get; }
    }
}
