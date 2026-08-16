using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MarkSmith.Models.MindMap;

namespace MarkSmith.ViewModels.MindMap
{
    public sealed partial class MindMapLinkViewModel : ObservableObject
    {
        public MindMapLink Model { get; }

        public string Id => Model.Id;
        public string SourceNodeId => Model.SourceNodeId;
        public string TargetNodeId => Model.TargetNodeId;

        [ObservableProperty]
        private string? _label;

        [ObservableProperty]
        private string _colorHex;

        [ObservableProperty]
        private MindMapLinkStyle _style;

        [ObservableProperty]
        private MindMapLinkDirection _direction;

        [ObservableProperty]
        private double _strokeThickness;

        [ObservableProperty]
        private bool _isSelected;

        public MindMapLinkViewModel(MindMapLink model)
        {
            Model = model;
            _label = model.Label;
            _colorHex = model.ColorHex ?? "#7C4DFF";
            _style = model.Style;
            _direction = model.Direction;
            _strokeThickness = model.StrokeThickness > 0 ? model.StrokeThickness : 2.0;
        }

        public void SyncToModel()
        {
            Model.Label = Label;
            Model.ColorHex = ColorHex;
            Model.Style = Style;
            Model.Direction = Direction;
            Model.StrokeThickness = StrokeThickness;
        }
    }
}
