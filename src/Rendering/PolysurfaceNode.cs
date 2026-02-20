using Godot;
using System.Collections.Generic;
using SplineSculptor.Model;

namespace SplineSculptor.Rendering
{
    /// <summary>
    /// Node3D that groups SurfaceNodes and ControlNetNodes for a single Polysurface.
    /// Listens to the Polysurface model for surface additions/removals.
    /// </summary>
    [GlobalClass]
    public partial class PolysurfaceNode : Node3D
    {
        private Polysurface? _polysurface;
        private readonly Dictionary<SculptSurface, (SurfaceNode surf, ControlNetNode net)> _nodes = new();

        public Polysurface? Data => _polysurface;

        public void Init(Polysurface polysurface)
        {
            _polysurface = polysurface;

            // Subscribe to future changes
            _polysurface.SurfaceAdded   += OnSurfaceAdded;
            _polysurface.SurfaceRemoved += OnSurfaceRemoved;

            // Add nodes for surfaces that already exist in the model
            foreach (var s in polysurface.Surfaces)
                AddNodesForSurface(s);

            // Apply transform
            Transform = polysurface.Transform;
        }

        private void OnSurfaceAdded(SculptSurface s)   => AddNodesForSurface(s);
        private void OnSurfaceRemoved(SculptSurface s) => RemoveNodesForSurface(s);

        private void AddNodesForSurface(SculptSurface s)
        {
            if (_nodes.ContainsKey(s) || _polysurface == null) return;

            var surfNode = new SurfaceNode();
            var netNode  = new ControlNetNode();

            AddChild(surfNode);
            AddChild(netNode);

            surfNode.Init(s);
            netNode.Init(s, _polysurface);

            _nodes[s] = (surfNode, netNode);
        }

        private void RemoveNodesForSurface(SculptSurface s)
        {
            if (!_nodes.TryGetValue(s, out var pair)) return;
            pair.surf.QueueFree();
            pair.net.QueueFree();
            _nodes.Remove(s);
        }

        /// <summary>Retrieve all ControlPointHandles across all surfaces in this node.</summary>
        public IEnumerable<Interaction.ControlPointHandle> AllHandles()
        {
            foreach (var (_, net) in _nodes.Values)
                foreach (var h in net.Handles)
                    yield return h;
        }

        /// <summary>Notify SurfaceNodes that a drag is starting (switch to low-res preview).</summary>
        public void BeginDrag()
        {
            foreach (var (surf, _) in _nodes.Values)
                surf.BeginDrag();
        }

        /// <summary>Notify SurfaceNodes that a drag ended (switch back to high-res).</summary>
        public void EndDrag()
        {
            foreach (var (surf, _) in _nodes.Values)
                surf.EndDrag();
        }
    }
}
