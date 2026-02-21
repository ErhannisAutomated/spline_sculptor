using System;
using System.Collections.Generic;
using SplineSculptor.Model;

namespace SplineSculptor.Interaction
{
    /// <summary>
    /// Tracks which surfaces, polysurfaces, edges, and control-point handles are
    /// currently selected. All mutation goes through the Modify* / Clear* methods.
    /// </summary>
    public class SelectionManager
    {
        private readonly HashSet<SculptSurface>      _selectedSurfaces     = new();
        private readonly HashSet<Polysurface>         _selectedPolysurfaces = new();
        private readonly List<EdgeRef>                _selectedEdges        = new();
        private readonly HashSet<ControlPointHandle>  _selectedHandles      = new();

        public event Action<SculptSurface>?       SurfaceSelected;
        public event Action<SculptSurface>?       SurfaceDeselected;
        public event Action<Polysurface>?         PolysurfaceSelected;
        public event Action<Polysurface>?         PolysurfaceDeselected;
        public event Action<EdgeRef>?             EdgeSelected;
        public event Action?                      EdgeDeselected;
        public event Action<ControlPointHandle>?  HandleSelected;
        public event Action<ControlPointHandle>?  HandleDeselected;

        public IReadOnlyCollection<SculptSurface>      SelectedSurfaces     => _selectedSurfaces;
        public IReadOnlyCollection<Polysurface>        SelectedPolysurfaces => _selectedPolysurfaces;
        public IReadOnlyList<EdgeRef>                  SelectedEdges        => _selectedEdges;
        public IReadOnlyCollection<ControlPointHandle> SelectedHandles      => _selectedHandles;

        /// <summary>Backward-compat: first selected edge (or null).</summary>
        public EdgeRef? SelectedEdge => _selectedEdges.Count > 0 ? _selectedEdges[0] : null;

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

        // ─── Edge selection (multi) ───────────────────────────────────────────────

        public void ModifyEdgeSelection(EdgeRef er, SelectionModifier mod)
        {
            switch (mod)
            {
                case SelectionModifier.Replace:
                    ClearEdges();
                    AddEdge(er);
                    break;
                case SelectionModifier.Add:
                    AddEdge(er);
                    break;
                case SelectionModifier.XOR:
                    int xi = _selectedEdges.IndexOf(er);
                    if (xi >= 0)
                    {
                        _selectedEdges.RemoveAt(xi);
                        EdgeDeselected?.Invoke();
                    }
                    else
                        AddEdge(er);
                    break;
                case SelectionModifier.Remove:
                    int ri = _selectedEdges.IndexOf(er);
                    if (ri >= 0)
                    {
                        _selectedEdges.RemoveAt(ri);
                        EdgeDeselected?.Invoke();
                    }
                    break;
            }
        }

        public void ClearEdges()
        {
            if (_selectedEdges.Count > 0)
            {
                _selectedEdges.Clear();
                EdgeDeselected?.Invoke();
            }
        }

        private void AddEdge(EdgeRef er)
        {
            _selectedEdges.Add(er);
            EdgeSelected?.Invoke(er);
        }

        // ─── Handle selection (multi) ─────────────────────────────────────────────

        public void ModifyHandleSelection(ControlPointHandle h, SelectionModifier mod)
        {
            switch (mod)
            {
                case SelectionModifier.Replace:
                    ClearHandles();
                    AddHandle(h);
                    break;
                case SelectionModifier.Add:
                    AddHandle(h);
                    break;
                case SelectionModifier.XOR:
                    if (_selectedHandles.Remove(h))
                    {
                        h.IsSelected = false;
                        HandleDeselected?.Invoke(h);
                    }
                    else
                        AddHandle(h);
                    break;
                case SelectionModifier.Remove:
                    RemoveHandle(h);
                    break;
            }
        }

        public void ClearHandles()
        {
            foreach (var h in _selectedHandles)
            {
                h.IsSelected = false;
                HandleDeselected?.Invoke(h);
            }
            _selectedHandles.Clear();
        }

        private void AddHandle(ControlPointHandle h)
        {
            if (_selectedHandles.Add(h))
            {
                h.IsSelected = true;
                HandleSelected?.Invoke(h);
            }
        }

        private void RemoveHandle(ControlPointHandle h)
        {
            if (_selectedHandles.Remove(h))
            {
                h.IsSelected = false;
                HandleDeselected?.Invoke(h);
            }
        }

        // ─── Clear ────────────────────────────────────────────────────────────────

        public void ClearAll()
        {
            ClearSurfaces();
            ClearPolysurfaces();
            ClearEdges();
            ClearHandles();
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
