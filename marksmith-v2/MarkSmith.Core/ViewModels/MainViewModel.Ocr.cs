using CommunityToolkit.Mvvm.ComponentModel;
using MarkSmith.Models;
using MarkSmith.Ocr;

namespace MarkSmith.ViewModels;

// Settings › General › Opening files: which OCR engine reads scanned PDF pages and pictures of text.
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _ocrEngine = OcrEngines.Auto;

    /// <summary>The engines for the Settings picker, each saying whether it can run here.</summary>
    public IReadOnlyList<OcrEngineInfo> OcrEngineOptions => _ocrEngineOptions ??= OcrEngines.List();
    private IReadOnlyList<OcrEngineInfo>? _ocrEngineOptions;

    /// <summary>One line under the picker: what the chosen engine is, or why it can't run here.</summary>
    public string OcrEngineDescription
    {
        get
        {
            var info = OcrEngineOptions.FirstOrDefault(e => e.Id == OcrEngine) ?? OcrEngineOptions[0];
            return info.IsAvailable ? info.Description : $"{info.Unavailable} MarkSmith OCR is used instead.";
        }
    }

#pragma warning disable MVVMTK0034
    private void LoadOcrSettings(AppSettings settings) =>
        _ocrEngine = string.IsNullOrWhiteSpace(settings.OcrEngine) ? OcrEngines.Auto : settings.OcrEngine.ToLowerInvariant();
#pragma warning restore MVVMTK0034

    partial void OnOcrEngineChanged(string value)
    {
        _settingsService.Current.OcrEngine = string.IsNullOrWhiteSpace(value) ? OcrEngines.Auto : value;
        OnPropertyChanged(nameof(OcrEngineDescription));
        SaveSettingsDebounced();
    }
}
