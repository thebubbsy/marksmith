using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdToPdf.Views;

public sealed partial class SmartArtInsertControl : UserControl
{
    private string _selectedType = "process";

    public SmartArtInsertControl()
    {
        InitializeComponent();
        SetDefaultTemplate("phases");
        UpdatePreview();
    }

    public string SelectedType => _selectedType;

    public IReadOnlyList<string> Lines =>
        StepsBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public string GeneratedSnippet => Services.InsertSnippetBuilder.SmartArt(_selectedType, Lines);

    private void OnLayoutCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string type)
        {
            _selectedType = type;
            UpdateButtonStyles();
            UpdatePreview();
        }
    }

    private void UpdateButtonStyles()
    {
        ProcessCard.Style = _selectedType == "process" ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
        ListCard.Style = _selectedType == "list" ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
        CycleCard.Style = _selectedType == "cycle" ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
        HierarchyCard.Style = _selectedType == "hierarchy" ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
    }

    private void OnTemplateClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string templateKey)
        {
            SetDefaultTemplate(templateKey);
        }
    }

    private void SetDefaultTemplate(string key)
    {
        switch (key)
        {
            case "phases":
                _selectedType = "process";
                StepsBox.Text = "Phase 1: Discovery & Planning\nPhase 2: Design & Development\nPhase 3: Testing & Launch";
                break;
            case "pdca":
                _selectedType = "cycle";
                StepsBox.Text = "Plan: Define goals & targets\nDo: Implement the process\nCheck: Measure & evaluate results\nAct: Standardize & improve";
                break;
            case "org":
                _selectedType = "hierarchy";
                StepsBox.Text = "Executive Leadership\nProduct & Engineering\nSales & Marketing\nCustomer Success";
                break;
            case "features":
                _selectedType = "list";
                StepsBox.Text = "High Performance Engine\nNative OpenXML DOCX Export\nZero External Dependencies\nReal-time Live Preview";
                break;
        }
        UpdateButtonStyles();
        UpdatePreview();
    }

    private void OnStepsTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (ItemCountBadge != null)
        {
            int count = Lines.Count;
            ItemCountBadge.Text = $"{count} item{(count == 1 ? "" : "s")}";
        }

        if (PreviewCodeText != null)
        {
            PreviewCodeText.Text = GeneratedSnippet.Trim();
        }
    }
}
