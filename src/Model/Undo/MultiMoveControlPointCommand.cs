using System.Collections.Generic;
using Godot;

namespace SplineSculptor.Model.Undo
{
    /// <summary>
    /// Moves multiple control points atomically. Used by DesktopInteraction when
    /// more than one handle is dragged together (multi-select).
    /// </summary>
    public class MultiMoveControlPointCommand : ICommand
    {
        private sealed record MoveEntry(
            SculptSurface Surf, int U, int V,
            Vector3 OldPos, Vector3 NewPos, Polysurface? Poly);

        private readonly List<MoveEntry> _moves;

        public string Description => $"Move {_moves.Count} control point(s)";

        public MultiMoveControlPointCommand(
            IEnumerable<(SculptSurface surf, int u, int v,
                         Vector3 oldPos, Vector3 newPos, Polysurface? poly)> entries)
        {
            _moves = new List<MoveEntry>();
            foreach (var (surf, u, v, oldPos, newPos, poly) in entries)
                _moves.Add(new MoveEntry(surf, u, v, oldPos, newPos, poly));
        }

        public void Execute() => ApplyAll(useNewPos: true);
        public void Undo()    => ApplyAll(useNewPos: false);

        private void ApplyAll(bool useNewPos)
        {
            // Apply all moves first, then enforce constraints once per poly+surf pair.
            var toEnforce = new HashSet<(Polysurface poly, SculptSurface surf)>();
            foreach (var m in _moves)
            {
                m.Surf.ApplyControlPointMove(m.U, m.V, useNewPos ? m.NewPos : m.OldPos);
                if (m.Poly != null)
                    toEnforce.Add((m.Poly, m.Surf));
            }
            foreach (var (poly, surf) in toEnforce)
                poly.EnforceConstraints(surf);
        }
    }
}
