using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using ProjectionMapper.Models;
using ProjectionMapper.ViewModels;

namespace ProjectionMapper.Services
{
    public interface IUndoRedoAction
    {
        string Description { get; }
        void Undo();
        void Redo();
    }

    public sealed class UndoRedoService : IDisposable
    {
        private readonly Stack<IUndoRedoAction> _undoStack = new();
        private readonly Stack<IUndoRedoAction> _redoStack = new();
        private bool _disposed;

        public event EventHandler? ActionRecorded;
        public event EventHandler? CanUndoChanged;
        public event EventHandler? CanRedoChanged;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public void RecordAction(IUndoRedoAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            try
            {
                _undoStack.Push(action);
                _redoStack.Clear();
                ActionRecorded?.Invoke(this, EventArgs.Empty);
                CanUndoChanged?.Invoke(this, EventArgs.Empty);
                CanRedoChanged?.Invoke(this, EventArgs.Empty);
                Debug.WriteLine($"UndoRedoService: Recorded action '{action.Description}' (undo count={_undoStack.Count})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UndoRedoService.RecordAction failed: {ex}");
            }
        }

        public void Undo()
        {
            if (_undoStack.Count == 0) return;
            try
            {
                var action = _undoStack.Pop();
                action.Undo();
                _redoStack.Push(action);
                CanUndoChanged?.Invoke(this, EventArgs.Empty);
                CanRedoChanged?.Invoke(this, EventArgs.Empty);
                Debug.WriteLine($"UndoRedoService: Undid action '{action.Description}' (undo count={_undoStack.Count})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UndoRedoService.Undo failed: {ex}");
            }
        }

        public void Redo()
        {
            if (_redoStack.Count == 0) return;
            try
            {
                var action = _redoStack.Pop();
                action.Redo();
                _undoStack.Push(action);
                CanUndoChanged?.Invoke(this, EventArgs.Empty);
                CanRedoChanged?.Invoke(this, EventArgs.Empty);
                Debug.WriteLine($"UndoRedoService: Redid action '{action.Description}' (undo count={_undoStack.Count})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UndoRedoService.Redo failed: {ex}");
            }
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            CanUndoChanged?.Invoke(this, EventArgs.Empty);
            CanRedoChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }

    public sealed class MeshPointChangeAction : IUndoRedoAction
    {
        private readonly LayerViewModel _layer;
        private readonly int _pointIndex;
        private readonly Vector2 _oldValue;
        private readonly Vector2 _newValue;
        private readonly bool _isOutputMesh;

        public string Description { get; }

        public MeshPointChangeAction(LayerViewModel layer, int pointIndex, Vector2 oldValue, Vector2 newValue, bool isOutputMesh)
        {
            _layer = layer ?? throw new ArgumentNullException(nameof(layer));
            _pointIndex = pointIndex;
            _oldValue = oldValue;
            _newValue = newValue;
            _isOutputMesh = isOutputMesh;
            Description = $"Move {(_isOutputMesh ? "output" : "input")} mesh point {_pointIndex} of {_layer.Name}";
        }

        public void Undo()
        {
            try
            {
                if (_isOutputMesh) _layer.SetOutputMeshPoint(_pointIndex, _oldValue);
                else _layer.SetMeshPoint(_pointIndex, _oldValue);
            }
            catch (Exception ex) { Debug.WriteLine($"MeshPointChangeAction.Undo failed: {ex}"); }
        }

        public void Redo()
        {
            try
            {
                if (_isOutputMesh) _layer.SetOutputMeshPoint(_pointIndex, _newValue);
                else _layer.SetMeshPoint(_pointIndex, _newValue);
            }
            catch (Exception ex) { Debug.WriteLine($"MeshPointChangeAction.Redo failed: {ex}"); }
        }
    }

    public sealed class CreateMeshAction : IUndoRedoAction
    {
        private readonly ImportedVideoViewModel _parent;
        private readonly LayerViewModel _layer;
        private int _index = -1;
        private readonly Action<LayerModel?>? _onCreated;

        public string Description { get; }

        public CreateMeshAction(ImportedVideoViewModel parent, LayerViewModel layer, Action<LayerModel?>? onCreated = null)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _layer = layer ?? throw new ArgumentNullException(nameof(layer));
            _onCreated = onCreated;
            Description = $"Create mesh '{_layer.Name}'";
        }

        public void Undo()
        {
            try
            {
                _index = _parent.MeshLayers.IndexOf(_layer);
                if (_index >= 0) _parent.MeshLayers.RemoveAt(_index);
                _onCreated?.Invoke(null);
            }
            catch (Exception ex) { Debug.WriteLine($"CreateMeshAction.Undo failed: {ex}"); }
        }

        public void Redo()
        {
            try
            {
                if (_index >= 0 && _index <= _parent.MeshLayers.Count) _parent.MeshLayers.Insert(_index, _layer);
                else _parent.MeshLayers.Add(_layer);
                _onCreated?.Invoke(_layer.Model);
            }
            catch (Exception ex) { Debug.WriteLine($"CreateMeshAction.Redo failed: {ex}"); }
        }
    }

    public sealed class DeleteMeshAction : IUndoRedoAction
    {
        private readonly ImportedVideoViewModel _parent;
        private readonly LayerViewModel _layer;
        private int _originalIndex = -1;
        private readonly Action<LayerModel?>? _onCreated;

        public string Description { get; }

        public DeleteMeshAction(ImportedVideoViewModel parent, LayerViewModel layer, Action<LayerModel?>? onCreated = null)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _layer = layer ?? throw new ArgumentNullException(nameof(layer));
            _onCreated = onCreated;
            Description = $"Delete mesh '{_layer.Name}'";
        }

        public void Undo()
        {
            try
            {
                if (_originalIndex >= 0 && _originalIndex <= _parent.MeshLayers.Count) _parent.MeshLayers.Insert(_originalIndex, _layer);
                else _parent.MeshLayers.Add(_layer);
                _onCreated?.Invoke(_layer.Model);
            }
            catch (Exception ex) { Debug.WriteLine($"DeleteMeshAction.Undo failed: {ex}"); }
        }

        public void Redo()
        {
            try
            {
                _originalIndex = _parent.MeshLayers.IndexOf(_layer);
                if (_originalIndex >= 0) _parent.MeshLayers.RemoveAt(_originalIndex);
                _onCreated?.Invoke(null);
            }
            catch (Exception ex) { Debug.WriteLine($"DeleteMeshAction.Redo failed: {ex}"); }
        }
    }
}
