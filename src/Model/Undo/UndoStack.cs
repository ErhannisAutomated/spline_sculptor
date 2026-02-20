using System;
using System.Collections.Generic;

namespace SplineSculptor.Model.Undo
{
    public class UndoStack
    {
        private readonly Stack<ICommand> _undoStack = new();
        private readonly Stack<ICommand> _redoStack = new();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public string? UndoDescription => CanUndo ? _undoStack.Peek().Description : null;
        public string? RedoDescription => CanRedo ? _redoStack.Peek().Description : null;

        /// <summary>Execute a command and push it onto the undo stack. Clears redo stack.</summary>
        public void Execute(ICommand cmd)
        {
            cmd.Execute();
            _undoStack.Push(cmd);
            _redoStack.Clear();
        }

        public void Undo()
        {
            if (!CanUndo) return;
            var cmd = _undoStack.Pop();
            cmd.Undo();
            _redoStack.Push(cmd);
        }

        public void Redo()
        {
            if (!CanRedo) return;
            var cmd = _redoStack.Pop();
            cmd.Execute();
            _undoStack.Push(cmd);
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }
}
