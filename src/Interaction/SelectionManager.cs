using System;
using System.Collections.Generic;
using SplineSculptor.Model;

namespace SplineSculptor.Interaction
{
    /// <summary>
    /// Tracks which surfaces / polysurfaces are currently selected.
    /// </summary>
    public class SelectionManager
    {
        private readonly HashSet<SculptSurface>  _selectedSurfaces     = new();
        private readonly HashSet<Polysurface>    _selectedPolysurfaces = new();

        public event Action<SculptSurface>? SurfaceSelected;
        public event Action<SculptSurface>? SurfaceDeselected;
        public event Action<Polysurface>?   PolysurfaceSelected;
        public event Action<Polysurface>?   PolysurfaceDeselected;

        public IReadOnlyCollection<SculptSurface> SelectedSurfaces     => _selectedSurfaces;
        public IReadOnlyCollection<Polysurface>   SelectedPolysurfaces => _selectedPolysurfaces;

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

        public void ClearAll()
        {
            ClearSurfaces();
            ClearPolysurfaces();
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
