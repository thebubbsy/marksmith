[xml]$doc = Get-Content MdToPdf\MainWindow.xaml -Raw
$ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
$ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$ns.AddNamespace('xaml', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')

$rootGrid = $doc.SelectSingleNode('/xaml:Window/xaml:Grid', $ns)
$legacyGrid = $doc.CreateElement('Grid', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$legacyGrid.SetAttribute('Visibility', 'Collapsed')
$legacyGrid.SetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml', 'LegacyUI_DoNotUse')

# Keep the first 4 elements: Grid.Resources, Grid.RowDefinitions, StackPanel, Grid (TitleBar)
$children = @()
foreach ($child in $rootGrid.ChildNodes) {
    if ($child.NodeType -eq 'Element') {
        $children += $child
    }
}

for ($i = 4; $i -lt $children.Length; $i++) {
    $el = $children[$i]
    $rootGrid.RemoveChild($el) | Out-Null
    $legacyGrid.AppendChild($el) | Out-Null
}

$rootGrid.AppendChild($legacyGrid) | Out-Null

[xml]$newUi = Get-Content C:\Users\Tony\.gemini\antigravity\scratch\LookingGlassViewGrid.xml -Raw
$importedNode = $doc.ImportNode($newUi.DocumentElement, $true)
$rootGrid.AppendChild($importedNode) | Out-Null

# Rename HistoryList
$legacyHistory = $legacyGrid.SelectSingleNode('.//xaml:ListView[@x:Name=""HistoryList""]', $ns)
if ($legacyHistory) {
    $legacyHistory.SetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml', 'LegacyHistoryList')
}

$doc.Save("C:\Users\Tony\.gemini\antigravity\scratch\marksmith\MdToPdf\MainWindow.xaml")
