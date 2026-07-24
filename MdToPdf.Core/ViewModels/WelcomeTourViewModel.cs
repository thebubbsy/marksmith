using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MdToPdf.ViewModels;

public class TourFeatureViewModel
{
    public string Glyph { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public class TourPageViewModel
{
    public string HeroGlyph { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Lead { get; set; } = string.Empty;
    public ObservableCollection<TourFeatureViewModel> Features { get; } = new();
    public bool IsFinalPage { get; set; }
}

public partial class WelcomeTourViewModel : ObservableObject
{
    public ObservableCollection<TourPageViewModel> Pages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    [NotifyPropertyChangedFor(nameof(CanSkip))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private int _currentIndex;

    [ObservableProperty]
    private bool _loadSampleRequested = true;

    public TourPageViewModel CurrentPage => Pages[CurrentIndex];
    public bool HasPrevious => CurrentIndex > 0;
    public bool CanSkip => CurrentIndex < Pages.Count - 1;
    public string NextButtonText => CurrentIndex >= Pages.Count - 1 ? "Get started" : "Next";
    public int TotalPages => Pages.Count;

    public event Action? TourCompleted;

    public WelcomeTourViewModel()
    {
        BuildPages();
    }

    private void BuildPages()
    {
        Pages.Add(new TourPageViewModel
        {
            HeroGlyph = "\xE8A5",
            Title = "Welcome to MarkSmith",
            Lead = "Turn messy AI-chat replies — ChatGPT, Gemini, Claude, Copilot — into polished, professional documents. Here's a 60-second tour of everything it can do.",
            Features =
            {
                new TourFeatureViewModel { Glyph = "\xE8A1", Text = "A left-to-right pipeline: Source → Style → Preview & Export. Everything updates live." }
            }
        });

        Pages.Add(new TourPageViewModel
        {
            HeroGlyph = "\xE70F",
            Title = "1 · Source",
            Lead = "Get your content in — however you have it.",
            Features =
            {
                new TourFeatureViewModel { Glyph = "\xE77F", Text = "Paste Markdown straight in, or drop / browse a .md file." },
                new TourFeatureViewModel { Glyph = "\xE8B7", Text = "The file picker finds real .md files across your Downloads, Documents, Desktop and OneDrive — newest first." },
                new TourFeatureViewModel { Glyph = "\xE8D4", Text = "MarkSmith detects which assistant wrote it and cleans the tell-tale quirks — LaTeX delimiters, citation pips, pseudo-headings." },
                new TourFeatureViewModel { Glyph = "\xE71B", Text = "The browser extension adds a \"Copy as Markdown\" button to each chat and sends it here in one click." }
            }
        });

        Pages.Add(new TourPageViewModel
        {
            HeroGlyph = "\xE790",
            Title = "2 · Style",
            Lead = "Make the output yours, not the model's default.",
            Features =
            {
                new TourFeatureViewModel { Glyph = "\xE2B1", Text = "Ten themes — or build your own with the 🎨 palette editor — plus page width, A4 lock, and a single-continuous-page mode for PDF." },
                new TourFeatureViewModel { Glyph = "\xE75C", Text = "Cleanup switches: em-dash handling, strip emoji, and the AI-quirk normalizer — all code-block-safe." },
                new TourFeatureViewModel { Glyph = "\xE8D2", Text = "Personalize the structure: shift every heading at once, and keep, remove or convert bold / italic. (Turn on Advanced mode in Settings to see these.)" }
            }
        });

        Pages.Add(new TourPageViewModel
        {
            HeroGlyph = "\xE9D9",
            Title = "Diagrams & math",
            Lead = "The features nobody else bothers with.",
            Features =
            {
                new TourFeatureViewModel { Glyph = "\xE9F5", Text = "Six diagram engines from one code fence each: Mermaid ships built in; PlantUML, Graphviz, D2, Typst and Vega-Lite install as one-click plugins (Settings → Plugins) and render offline." },
                new TourFeatureViewModel { Glyph = "\xE8B1", Text = "Every diagram engine becomes native, editable Word shapes (ShapeForge™) — not a flat picture. Big ones keep their exact layout via Web Layout." },
                new TourFeatureViewModel { Glyph = "\xE943", Text = "LaTeX / KaTeX math becomes real, clickable Word equations (OMML) — editable, not an image." }
            }
        });

        Pages.Add(new TourPageViewModel
        {
            HeroGlyph = "\xEDE1",
            Title = "3 · Preview & Export",
            Lead = "See the finished page live, then export.",
            Features =
            {
                new TourFeatureViewModel { Glyph = "\xEA90", Text = "Pixel-perfect PDF, or a proprietary MD-to-Word DOCX with a self-updating table of contents and tables that survive page breaks." },
                new TourFeatureViewModel { Glyph = "\xE8FD", Text = "Also exports PowerPoint (PPTX) and EPUB." },
                new TourFeatureViewModel { Glyph = "\xE8D4", Text = "Branding kit: a cover page with your logo and a document-wide font, so a chat lands as a client-ready deliverable." }
            }
        });

        Pages.Add(new TourPageViewModel
        {
            HeroGlyph = "\xE945",
            Title = "Automate & Pro",
            Lead = "Hands-free conversion, and what Pro unlocks.",
            Features =
            {
                new TourFeatureViewModel { Glyph = "\xE8B7", Text = "Watch a folder or the clipboard and convert automatically; batch-convert a whole folder to PDF." },
                new TourFeatureViewModel { Glyph = "\xE968", Text = "Drive it from scripts via the local REST API — the same one both the Windows and cross-platform builds expose." },
                new TourFeatureViewModel { Glyph = "\xE734", Text = "Free exports PDF. MarkSmith Pro adds Word export, automation and branding — with a 14-day trial." }
            }
        });

        Pages.Add(new TourPageViewModel
        {
            HeroGlyph = "\xE73E",
            Title = "You're all set",
            Lead = "Learn fastest by doing — load a ready-made sample and start playing:",
            IsFinalPage = true,
            Features =
            {
                new TourFeatureViewModel { Glyph = "\xE897", Text = "Replay this anytime from the ? button next to Settings." }
            }
        });
    }

    [RelayCommand]
    private void Next()
    {
        if (CurrentIndex < Pages.Count - 1)
        {
            CurrentIndex++;
        }
        else
        {
            TourCompleted?.Invoke();
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentIndex > 0)
        {
            CurrentIndex--;
        }
    }

    [RelayCommand]
    private void Skip()
    {
        LoadSampleRequested = false;
        TourCompleted?.Invoke();
    }
}
