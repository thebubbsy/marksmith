import sys, re

path = r"MdToPdf.Avalonia\Controls\Polyfills.cs"
with open(path, "r", encoding="utf-8") as f:
    lines = f.read()

repl = '''    public class ProgressRing : global::Avalonia.Controls.ProgressBar
    {
        public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<ProgressRing, bool>("IsActive");
        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsActiveProperty)
            {
                IsIndeterminate = IsActive;
                IsVisible = IsActive;
            }
        }
    }
'''

lines = re.sub(r'    public class ProgressRing.*', repl + "}", lines, flags=re.DOTALL)
with open(path, "w", encoding="utf-8") as f:
    f.write(lines)
