using System;
using System.Collections.Generic;
using SplineSculptor.Model;

namespace SplineSculptor.Interaction
{
    /// <summary>
    /// Tracks which surfaces, polysurfaces, and edges are currently selected.
    /// </summary>
    public class SelectionManager
    {
        private readonly HashSet<SculptSurface> _selectedSurfaces     = new();
        private readonly HashSet<Polysurface>   _selectedPolysurfaces = new();
        private EdgeRef? _selectedEdge;

        public event Action<SculptSurface>? SurfaceSelected;
        public event Action<SculptSurface>? SurfaceDeselected;
        public event Action<Polysurface>?   PolysurfaceSelected;
        public event Action<Polysurface>?   PolysurfaceDeselected;
        public event Action<EdgeRef>?       EdgeSelected;
        public event Action?                EdgeDeselected;

        public IReadOnlyCollection<SculptSurface> SelectedSurfaces     => _selectedSurfaces;
        public IReadOnlyCollection<Polysurface>   SelectedPolysurfaces => _selectedPolysurfaces;
        public EdgeRef?                           SelectedEdge         => _selectedEdge;

        // ─── Surface selection ────────────────────────────────────────────────────

        public void SelectSurface(SculptSurface s, bool additive = false)
        {
            if (!additive) ClearSurfaces();
            if (_selectedSurfaces.Add(s))
            {
                s.IsSelected = true;
                SurfaceSelected?.Invoke(s);
            }
        }

        public void DeselectSurface(SculptSurface s)
        {
            if (_selectedSurfaces.Remove(s))
            {
                s.IsSelected = false;
                SurfaceDeselected?.Invoke(s);
            }
        }

        // ─── Polysurface selection ────────────────────────────────────────────────

        public void SelectPolysurface(Polysurface p, bool additive = false)
        {
            if (!additive) ClearPolysurfaces();
            if (_selectedPolysurfaces.Add(p))
                PolysurfaceSelected?.Invoke(p);
        }

        public void DeselectPolysurface(Polysurface p)
        {
            if (_selectedPolysurfaces.Remove(p))
                PolysurfaceDeselected?.Invoke(p);
        }

        // ─── Edge selection ───────────────────────────────────────────────────────

        public void SelectEdge(EdgeRef e)
        {
            if (_selectedEdge.HasValue)
                EdgeDeselected?.Invoke();
            _selectedEdge = e;
            EdgeSelected?.Invoke(e);
        }

        public void DeselectEdge()
        {
            if (!_selectedEdge.HasValue) return;
            EdgeDeselected?.Invoke();
            _selectedEdge = null;
        }

        // ─── Clear ────────────────────────────────────────────────────────────────

        public void ClearAll()
        {
            ClearSurfaces();
            ClearPolysurfaces();
            DeselectEdge();
        }

        private void ClearSurfaces()
        {
            foreach (var s in _selectedSurfaces)
            {
                s.IsSelected = false;
                SurfaceDeselected?.Invoke(s);
            }
            _selectedSurfaces.Clear();
        }

        private void ClearPolysurfaces()
        {
            foreach (var p in _selectedPolysurfaces)
                PolysurfaceDeselected?.Invoke(p);
            _selectedPolysurfaces.Clear();
        }
    }
}
