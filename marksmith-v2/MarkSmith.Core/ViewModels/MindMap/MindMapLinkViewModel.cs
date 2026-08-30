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

        [ObservableProperty]
        private bool _isHovered;

        /// <summary>Why the edge exists. Inferred links render fainter than ones a person drew.</summary>
        [ObservableProperty]
        private MindMapLinkKind _kind;

        [ObservableProperty]
        private double _weight;

        /// <summary>A link the auto-linker guessed rather than the user drawing it.</summary>
        public bool IsInferred => Kind != MindMapLinkKind.Manual;

        public string KindDescription => MindMapLinkKindRank.Describe(Kind);

        /// <summary>What to draw beside the line: the author's own words when they gave any,
        /// otherwise why the linker thinks these two belong together.</summary>
        public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? KindDescription : Label!.Trim();

        partial void OnKindChanged(MindMapLinkKind value)
        {
            OnPropertyChanged(nameof(IsInferred));
            OnPropertyChanged(nameof(KindDescription));
            OnPropertyChanged(nameof(DisplayLabel));
        }

        partial void OnLabelChanged(string? value) => OnPropertyChanged(nameof(DisplayLabel));

        public MindMapLinkViewModel(MindMapLink model)
        {
            Model = model;
            _label = model.Label;
            _colorHex = model.ColorHex ?? "#7C4DFF";
            _style = model.Style;
            _direction = model.Direction;
            _strokeThickness = model.StrokeThickness > 0 ? model.StrokeThickness : 2.0;
            _kind = model.Kind;
            _weight = model.Weight > 0 ? model.Weight : 1.0;
        }

        /// <summary>Flips which end the arrowhead sits on, for when a relationship was recorded
        /// backwards.</summary>
        public void ReverseDirection()
        {
            Direction = Direction switch
            {
                MindMapLinkDirection.SourceToTarget => MindMapLinkDirection.TargetToSource,
                MindMapLinkDirection.TargetToSource => MindMapLinkDirection.SourceToTarget,
                MindMapLinkDirection.Bidirectional => MindMapLinkDirection.None,
                _ => MindMapLinkDirection.Bidirectional
            };
        }

        public void SyncToModel()
        {
            Model.Label = Label;
            Model.ColorHex = ColorHex;
            Model.Style = Style;
            Model.Direction = Direction;
            Model.StrokeThickness = StrokeThickness;
            Model.Kind = Kind;
            Model.Weight = Weight;
        }
    }
}
