using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

class Program
{
    static void Main()
    {
        string xamlPath = @"MdToPdf\MainWindow.xaml";
        string backupPath = @"MdToPdf\MainWindow.original.txt";
        
        if (!File.Exists(backupPath))
        {
            File.Copy(xamlPath, backupPath);
        }

        string xamlText = File.ReadAllText(backupPath);
        
        // Load as XDocument
        XDocument doc = XDocument.Parse(xamlText);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var rootGrid = doc.Root.Element(ns + "Grid");
        if (rootGrid == null) { Console.WriteLine("Root grid not found"); return; }

        var mainGrid = rootGrid.Elements(ns + "Grid").FirstOrDefault(g => g.Attribute("ColumnSpacing")?.Value == "10");
        if (mainGrid == null) { Console.WriteLine("Main layout grid not found"); return; }

        // Find HistoryList
        var historyList = mainGrid.Descendants(ns + "ListView").FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "HistoryList");
        
        // Find Center Panel (PreviewWebView, StatusOverlay, EditorTextBox)
        var previewWebView = mainGrid.Descendants(ns + "WebView2").FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "PreviewWebView");
        var previewSpinner = mainGrid.Descendants(ns + "Grid").FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "PreviewSpinner");
        
        // Find StyleCard
        var styleCard = mainGrid.Descendants(ns + "Border").FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "StyleCard");
        
        // Find AutomationExpander
        var automationExpander = mainGrid.Descendants(ns + "Expander").FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "AutomationExpander");
        
        // Extract Export/Generate buttons
        var generatePdfBtn = mainGrid.Descendants(ns + "Button").FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "GeneratePdfButton");
        var exportDocxBtn = mainGrid.Descendants(ns + "Button").FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "ExportDocxButton");

        // InfoBar (StatusSeverity)
        var infoBar = mainGrid.Descendants(ns + "InfoBar").FirstOrDefault(e => e.Attribute("IsOpen")?.Value == "True");

        // Format selector (Output Format)
        var formatSelector = mainGrid.Descendants(ns + "ComboBox").FirstOrDefault(e => e.Attribute("Header")?.Value == "Output Format");

        // Delete all children of mainGrid
        mainGrid.RemoveNodes();

        // Update mainGrid attributes
        mainGrid.SetAttributeValue("Padding", "16,6,16,16");
        mainGrid.SetAttributeValue("ColumnSpacing", "10");
        mainGrid.SetAttributeValue("RowSpacing", null);
        mainGrid.SetAttributeValue("Margin", null);

        // Add 3 columns
        var colDefs = new XElement(ns + "Grid.ColumnDefinitions",
            new XElement(ns + "ColumnDefinition", new XAttribute("Width", "280")),
            new XElement(ns + "ColumnDefinition", new XAttribute("Width", "*")),
            new XElement(ns + "ColumnDefinition", new XAttribute("Width", "350"))
        );
        mainGrid.Add(colDefs);

        // Column 0: History
        var col0 = new XElement(ns + "Grid", 
            new XAttribute("Grid.Column", "0"),
            new XAttribute("RowSpacing", "10"),
            new XElement(ns + "Grid.RowDefinitions",
                new XElement(ns + "RowDefinition", new XAttribute("Height", "Auto")),
                new XElement(ns + "RowDefinition", new XAttribute("Height", "*"))
            ),
            new XElement(ns + "TextBlock", new XAttribute("Grid.Row", "0"), new XAttribute("Text", "History"), new XAttribute("FontWeight", "SemiBold"), new XAttribute("Style", "{StaticResource StepTitleStyle}")),
            historyList
        );
        mainGrid.Add(col0);

        // Column 1: Editor & Preview
        var col1 = new XElement(ns + "Grid", new XAttribute("Grid.Column", "1"));
        col1.Add(previewWebView);

        string lookingGlassOverlay = @"
            <Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
                <TextBox x:Name=""EditorTextBox""
                         Text=""{Binding PastedMarkdown, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}""
                         AcceptsReturn=""True"" TextWrapping=""Wrap""
                         FontFamily=""Cascadia Mono, Consolas, monospace"" FontSize=""13""
                         PlaceholderText=""Paste or type Markdown here...""
                         VerticalContentAlignment=""Top"" IsSpellCheckEnabled=""False""
                         ScrollViewer.VerticalScrollBarVisibility=""Auto""
                         Background=""Transparent""
                         PointerMoved=""OnEditorPointerMoved""
                         GotFocus=""OnEditorGotFocus""
                         LostFocus=""OnEditorLostFocus""
                         SelectionChanged=""OnEditorSelectionChanged""
                         Foreground=""Transparent""
                         Padding=""12"" />
                <TextBlock x:Name=""EditorOverlayText""
                           IsHitTestVisible=""False""
                           Text=""{Binding PastedMarkdown}""
                           TextWrapping=""Wrap""
                           FontFamily=""Cascadia Mono, Consolas, monospace""
                           FontSize=""13""
                           Padding=""12""
                           Foreground=""{ThemeResource TextFillColorPrimaryBrush}"" />
            </Grid>";
        var lgElements = XElement.Parse(lookingGlassOverlay);
        foreach (var el in lgElements.Elements())
        {
            col1.Add(el);
        }
        col1.Add(previewSpinner);
        mainGrid.Add(col1);

        // Column 2: Options & Actions
        var col2 = new XElement(ns + "Grid", 
            new XAttribute("Grid.Column", "2"),
            new XElement(ns + "Grid.RowDefinitions",
                new XElement(ns + "RowDefinition", new XAttribute("Height", "*")),
                new XElement(ns + "RowDefinition", new XAttribute("Height", "Auto"))
            ),
            new XElement(ns + "ScrollViewer",
                new XAttribute("Grid.Row", "0"),
                new XAttribute("VerticalScrollBarVisibility", "Auto"),
                new XAttribute("Padding", "0,0,14,0"),
                new XElement(ns + "StackPanel",
                    new XAttribute("Spacing", "16"),
                    formatSelector,
                    styleCard,
                    automationExpander
                )
            ),
            new XElement(ns + "StackPanel",
                new XAttribute("Grid.Row", "1"),
                new XAttribute("Spacing", "8"),
                new XAttribute("Margin", "0,10,0,0"),
                infoBar,
                generatePdfBtn,
                exportDocxBtn
            )
        );
        mainGrid.Add(col2);

        File.WriteAllText(xamlPath, doc.ToString());
        Console.WriteLine("XAML Transformation Complete.");
    }
}
