using Godot;

namespace SplineSculptor.Interaction
{
    /// <summary>
    /// Implemented by any object that can be grabbed by a VR controller hand.
    /// </summary>
    public interface IGrabTarget
    {
        /// <summary>World-space position of the grab handle.</summary>
        Vector3 GlobalGrabPosition { get; }

        /// <summary>Called when a controller starts grabbing this target.</summary>
        void OnGrabStart(Node grabber);

        /// <summary>Called each frame while held, with the current controller world position.</summary>
        void OnGrabMove(Vector3 controllerWorldPos);

        /// <summary>Called when the controller releases the grab.</summary>
        void OnGrabEnd(Node grabber);

        /// <summary>Highlight state for hover/selection feedback.</summary>
        bool IsHovered { get; set; }
    }
}
