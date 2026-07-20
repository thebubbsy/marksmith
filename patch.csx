using System;
using System.Linq;
using System.Xml.Linq;

var path = @"MdToPdf\MainWindow.xaml";
var doc = XDocument.Load(path);
XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

var rootGrid = doc.Root.Element(xaml + "Grid");
var legacyElements = rootGrid.Elements().Skip(3).ToList(); // Skip Grid.Resources, Grid.RowDefinitions, StackPanel, Grid (TitleBar)

var legacyGrid = new XElement(xaml + "Grid",
    new XAttribute("Visibility", "Collapsed"),
    new XAttribute(x + "Name", "LegacyUI_DoNotUse"));

foreach (var el in legacyElements) {
    el.Remove();
    legacyGrid.Add(el);
}
rootGrid.Add(legacyGrid);

var newUiStr = System.IO.File.ReadAllText(@"C:\Users\Tony\.gemini\antigravity\scratch\LookingGlassViewGrid.xml");
var newUi = XElement.Parse(newUiStr);
rootGrid.Add(newUi);

// Rename HistoryList in legacy UI to avoid conflict
var legacyHistoryList = legacyGrid.Descendants(xaml + "ListView").FirstOrDefault(e => (string)e.Attribute(x + "Name") == "HistoryList");
if (legacyHistoryList != null) {
    legacyHistoryList.SetAttributeValue(x + "Name", "LegacyHistoryList");
}

doc.Save(path);
Console.WriteLine("Done!");
