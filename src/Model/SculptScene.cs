using System.Collections.Generic;
using SplineSculptor.Model.Undo;

namespace SplineSculptor.Model
{
    /// <summary>
    /// Root data model: owns all Polysurfaces and the undo stack.
    /// </summary>
    public class SculptScene
    {
        public List<Polysurface> Polysurfaces { get; } = new();
        public UndoStack UndoStack { get; } = new();
        public string? FilePath { get; set; }

        public Polysurface AddPolysurface(string? name = null)
        {
            var poly = new Polysurface { Name = name ?? $"Polysurface {Polysurfaces.Count + 1}" };
            var cmd = new AddPolysurfaceCommand(this, poly);
            UndoStack.Execute(cmd);
            return poly;
        }

        public void RemovePolysurface(Polysurface p)
        {
            var cmd = new DeletePolysurfaceCommand(this, p);
            UndoStack.Execute(cmd);
        }

        // ─── Internal (used by commands) ──────────────────────────────────────────

        internal void InternalAdd(Polysurface p)
        {
            if (!Polysurfaces.Contains(p))
                Polysurfaces.Add(p);
        }

        internal void InternalRemove(Polysurface p)
        {
            Polysurfaces.Remove(p);
        }
    }
}
