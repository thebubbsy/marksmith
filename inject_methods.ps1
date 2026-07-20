param([string]$targetFile)

$code = @"
    private static void RenderDatagrid(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        var lines = (node.InnerContent ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;

        var table = new W.Table(
            new W.TableProperties(
                new W.TableStyle { Val = "TableGrid" },
                new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                new W.TableLook { Val = "04A0", FirstRow = true, LastRow = false, FirstColumn = true, LastColumn = false, NoHorizontalBand = false, NoVerticalBand = true }
            ));
        
        bool isHeader = true;
        foreach (var line in lines)
        {
            var cells = line.Split(new[] { ',', '\t' });
            var row = new W.TableRow();
            foreach (var cellText in cells)
            {
                var tc = new W.TableCell(
                    new W.TableCellProperties(
                        new W.TableCellWidth { Width = "0", Type = W.TableWidthUnitValues.Auto },
                        new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = isHeader ? (ctx.PrimaryHex ?? "F3F4F6") : "FFFFFF" }
                    ),
                    new W.Paragraph(
                        new W.ParagraphProperties(new W.SpacingBetweenLines { After = "120" }),
                        new W.Run(
                            new W.RunProperties(
                                new W.Color { Val = isHeader ? "FFFFFF" : (ctx.TextHex ?? "000000") },
                                new W.Bold { Val = isHeader ? new DocumentFormat.OpenXml.Wordprocessing.BooleanDefaultType(true) : new DocumentFormat.OpenXml.Wordprocessing.BooleanDefaultType(false) }
                            ),
                            new W.Text(cellText.Trim())
                        )
                    )
                );
                row.Append(tc);
            }
            table.Append(row);
            isHeader = false;
        }

        target.Append(table);
        target.Append(new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "120" })));
    }

    private static void RenderChart(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        var labels = new List<string>();
        var values = new List<double>();
        
        if (node.InnerContent != null && node.InnerContent.TrimStart().StartsWith("{"))
        {
            try 
            {
                var j = JsonDocument.Parse(node.InnerContent);
                var data = j.RootElement.GetProperty("data");
                foreach (var l in data.GetProperty("labels").EnumerateArray()) labels.Add(l.GetString());
                foreach (var v in data.GetProperty("values").EnumerateArray()) values.Add(v.GetDouble());
            } 
            catch { }
        }
        else if (node.InnerContent != null)
        {
            var lines = node.InnerContent.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            bool first = true;
            foreach(var line in lines)
            {
                if (first) { first = false; continue; } // Skip header
                var parts = line.Split(',');
                if (parts.Length >= 2 && double.TryParse(parts[1], out double val))
                {
                    labels.Add(parts[0]);
                    values.Add(val);
                }
            }
        }

        if (labels.Count == 0 || labels.Count != values.Count) return;

        string chartType = node.Attributes.ContainsKey("type") ? node.Attributes["type"].ToLower() : "bar";

        var chartPart = ctx.MainPart.AddNewPart<ChartPart>();
        string chartRelId = ctx.MainPart.GetIdOfPart(chartPart);
        
        var packagePart = chartPart.AddNewPart<EmbeddedPackagePart>(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 
            "rId1"
        );

        using (var stream = packagePart.GetStream())
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook(new S.Sheets(new S.Sheet() { Id = "rId1", SheetId = 1, Name = "Sheet1" }));
            
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>("rId1");
            var sheetData = new S.SheetData();
            worksheetPart.Worksheet = new S.Worksheet(sheetData);

            var header = new S.Row() { RowIndex = 1 };
            header.Append(
                new S.Cell() { CellReference = "A1", DataType = S.CellValues.String, CellValue = new S.CellValue("Category") },
                new S.Cell() { CellReference = "B1", DataType = S.CellValues.String, CellValue = new S.CellValue("Value") }
            );
            sheetData.Append(header);

            for (int i = 0; i < labels.Count; i++)
            {
                uint rowIdx = (uint)(i + 2);
                var row = new S.Row() { RowIndex = rowIdx };
                row.Append(
                    new S.Cell() { CellReference = "A" + rowIdx, DataType = S.CellValues.String, CellValue = new S.CellValue(labels[i]) },
                    new S.Cell() { CellReference = "B" + rowIdx, DataType = S.CellValues.Number, CellValue = new S.CellValue(values[i].ToString(CultureInfo.InvariantCulture)) }
                );
                sheetData.Append(row);
            }
        }

        var categoryRef = new C.CategoryAxisData();
        var stringRef = new C.StringReference() { Formula = new C.Formula($"Sheet1!`$A`$2:`$A`${labels.Count + 1}") };
        var stringCache = new C.StringCache();
        stringCache.Append(new C.PointCount() { Val = (uint)labels.Count });
        for (int i = 0; i < labels.Count; i++)
        {
            stringCache.Append(new C.StringPoint() { Index = (uint)i, NumericValue = new C.NumericValue(labels[i]) });
        }
        stringRef.Append(stringCache);
        categoryRef.Append(stringRef);

        var valuesRef = new C.Values();
        var numRef = new C.NumberReference() { Formula = new C.Formula($"Sheet1!`$B`$2:`$B`${labels.Count + 1}") };
        var numCache = new C.NumberingCache();
        numCache.Append(new C.FormatCode("General"));
        numCache.Append(new C.PointCount() { Val = (uint)labels.Count });
        for (int i = 0; i < values.Count; i++)
        {
            numCache.Append(new C.NumericPoint() { Index = (uint)i, NumericValue = new C.NumericValue(values[i].ToString(CultureInfo.InvariantCulture)) });
        }
        numRef.Append(numCache);
        valuesRef.Append(numRef);

        var chartSpace = new C.ChartSpace();
        var chart = new C.Chart();
        var plotArea = new C.PlotArea();
        
        chart.Append(new C.AutoTitleDeleted() { Val = new DocumentFormat.OpenXml.BooleanValue(true) });

        if (chartType == "line")
        {
            var lineChart = new C.LineChart(new C.Grouping() { Val = C.GroupingValues.Standard });
            var series = new C.LineChartSeries(
                new C.Index() { Val = 0 },
                new C.Order() { Val = 0 },
                (C.CategoryAxisData)categoryRef.CloneNode(true),
                (C.Values)valuesRef.CloneNode(true)
            );
            lineChart.Append(series);
            lineChart.Append(new C.AxisId() { Val = 10000000 });
            lineChart.Append(new C.AxisId() { Val = 10000001 });
            plotArea.Append(lineChart);
        }
        else if (chartType == "pie")
        {
            var pieChart = new C.PieChart();
            var series = new C.PieChartSeries(
                new C.Index() { Val = 0 },
                new C.Order() { Val = 0 },
                (C.CategoryAxisData)categoryRef.CloneNode(true),
                (C.Values)valuesRef.CloneNode(true)
            );
            pieChart.Append(series);
            plotArea.Append(pieChart);
        }
        else
        {
            var barChart = new C.BarChart(
                new C.BarDirection() { Val = C.BarDirectionValues.Column },
                new C.BarGrouping() { Val = C.BarGroupingValues.Clustered }
            );
            var series = new C.BarChartSeries(
                new C.Index() { Val = 0 },
                new C.Order() { Val = 0 },
                (C.CategoryAxisData)categoryRef.CloneNode(true),
                (C.Values)valuesRef.CloneNode(true)
            );
            barChart.Append(series);
            barChart.Append(new C.AxisId() { Val = 10000000 });
            barChart.Append(new C.AxisId() { Val = 10000001 });
            plotArea.Append(barChart);
        }

        if (chartType != "pie")
        {
            plotArea.Append(new C.CategoryAxis(
                new C.AxisId() { Val = 10000000 },
                new C.Scaling(new C.Orientation() { Val = C.OrientationValues.MinMax }),
                new C.AxisPosition() { Val = C.AxisPositionValues.Bottom },
                new C.TickLabelPosition() { Val = C.TickLabelPositionValues.NextTo },
                new C.CrossingAxis() { Val = 10000001 },
                new C.Crosses() { Val = C.CrossesValues.AutoZero }
            ));
            plotArea.Append(new C.ValueAxis(
                new C.AxisId() { Val = 10000001 },
                new C.Scaling(new C.Orientation() { Val = C.OrientationValues.MinMax }),
                new C.AxisPosition() { Val = C.AxisPositionValues.Left },
                new C.MajorGridlines(),
                new C.TickLabelPosition() { Val = C.TickLabelPositionValues.NextTo },
                new C.CrossingAxis() { Val = 10000000 },
                new C.Crosses() { Val = C.CrossesValues.AutoZero },
                new C.CrossBetween() { Val = C.CrossBetweenValues.Between }
            ));
        }

        chart.Append(plotArea);
        chartSpace.Append(chart);
        
        chartSpace.Append(new C.ExternalData(new C.AutoUpdate() { Val = new DocumentFormat.OpenXml.BooleanValue(false) }) { Id = "rId1" });
        chartPart.ChartSpace = chartSpace;

        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent() { Cx = 5486400, Cy = 3200400 },
                new DW.EffectExtent() { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties() { Id = 1, Name = "Chart 1" },
                new DW.NonVisualGraphicFrameDrawingProperties(
                    new A.GraphicFrameLocks() { NoChangeAspect = true }
                ),
                new A.Graphic(
                    new A.GraphicData(
                        new C.ChartReference() { Id = chartRelId }
                    ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" }
                )
            )
        );

        target.Append(new W.Paragraph(new W.Run(drawing)));
    }

    private static void RenderCanvas(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        string svgContent = node.InnerContent?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(svgContent)) return;

        List<string> paths = new List<string>();
        double vBoxW = 100, vBoxH = 100;
        
        if (svgContent.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var xDoc = XDocument.Parse(svgContent);
                var svgElement = xDoc.Root;

                var viewBox = svgElement.Attribute("viewBox")?.Value;
                if (!string.IsNullOrEmpty(viewBox))
                {
                    var parts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4 && double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double w) &&
                        double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                    {
                        vBoxW = w;
                        vBoxH = h;
                    }
                }
                
                foreach (var el in svgElement.Descendants())
                {
                    var localName = el.Name.LocalName.ToLowerInvariant();
                    if (localName == "path")
                    {
                        var d = el.Attribute("d")?.Value;
                        if (!string.IsNullOrEmpty(d)) paths.Add(d);
                    }
                    else if (localName == "rect")
                    {
                        var x = el.Attribute("x")?.Value ?? "0";
                        var y = el.Attribute("y")?.Value ?? "0";
                        var w = el.Attribute("width")?.Value ?? "0";
                        var h = el.Attribute("height")?.Value ?? "0";
                        paths.Add($"M {x} {y} h {w} v {h} h -{w} Z");
                    }
                    else if (localName == "circle")
                    {
                        var cx = double.Parse(el.Attribute("cx")?.Value ?? "0", CultureInfo.InvariantCulture);
                        var cy = double.Parse(el.Attribute("cy")?.Value ?? "0", CultureInfo.InvariantCulture);
                        var r = double.Parse(el.Attribute("r")?.Value ?? "0", CultureInfo.InvariantCulture);
                        var kappa = 0.552284749831 * r;
                        paths.Add(
                            $"M {cx} {cy - r} " +
                            $"C {cx + kappa} {cy - r}, {cx + r} {cy - kappa}, {cx + r} {cy} " +
                            $"C {cx + r} {cy + kappa}, {cx + kappa} {cy + r}, {cx} {cy + r} " +
                            $"C {cx - kappa} {cy + r}, {cx - r} {cy + kappa}, {cx - r} {cy} " +
                            $"C {cx - r} {cy - kappa}, {cx - kappa} {cy - r}, {cx} {cy - r} Z"
                        );
                    }
                    else if (localName == "line")
                    {
                        var x1 = el.Attribute("x1")?.Value ?? "0";
                        var y1 = el.Attribute("y1")?.Value ?? "0";
                        var x2 = el.Attribute("x2")?.Value ?? "0";
                        var y2 = el.Attribute("y2")?.Value ?? "0";
                        paths.Add($"M {x1} {y1} L {x2} {y2}");
                    }
                    else if (localName == "polyline" || localName == "polygon")
                    {
                        var pts = el.Attribute("points")?.Value;
                        if (!string.IsNullOrEmpty(pts))
                        {
                            var coords = pts.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            if (coords.Length >= 2)
                            {
                                var sb = new System.Text.StringBuilder();
                                sb.Append($"M {coords[0]} {coords[1]} ");
                                for (int i = 2; i < coords.Length - 1; i += 2)
                                {
                                    sb.Append($"L {coords[i]} {coords[i + 1]} ");
                                }
                                if (localName == "polygon") sb.Append("Z");
                                paths.Add(sb.ToString());
                            }
                        }
                    }
                }
            }
            catch { }
        }
        else
        {
            paths.Add(svgContent);
        }

        if (paths.Count == 0) return;

        long emuPerUnit = 9525;
        long shapeWidthEmu = (long)(vBoxW * emuPerUnit);
        long shapeHeightEmu = (long)(vBoxH * emuPerUnit);

        var pathList = new A.PathList();

        foreach (var d in paths)
        {
            var tokens = Regex.Matches(d, @"[a-zA-Z]|[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?").Cast<Match>().Select(m => m.Value).ToList();
            var aPath = new A.Path() { Width = shapeWidthEmu, Height = shapeHeightEmu };
            
            char currentCommand = 'M';
            int index = 0;
            double currentX = 0, currentY = 0;

            while (index < tokens.Count)
            {
                string token = tokens[index];
                if (char.IsLetter(token[0]))
                {
                    currentCommand = token[0];
                    index++;
                }

                bool isRelative = char.IsLower(currentCommand);
                char cmd = char.ToUpperInvariant(currentCommand);

                double ReadNext() => double.Parse(tokens[index++], CultureInfo.InvariantCulture);
                
                A.AdjustPoint2DType P(double x, double y) => new A.AdjustPoint2DType 
                { 
                    X = new StringValue(((long)(x * emuPerUnit)).ToString()), 
                    Y = new StringValue(((long)(y * emuPerUnit)).ToString()) 
                };

                switch (cmd)
                {
                    case 'M':
                    case 'L':
                    case 'T':
                        if (index + 1 >= tokens.Count) break;
                        double mx = ReadNext(), my = ReadNext();
                        currentX = isRelative ? currentX + mx : mx;
                        currentY = isRelative ? currentY + my : my;
                        
                        if (cmd == 'M') aPath.Append(new A.MoveTo() { Point = P(currentX, currentY) });
                        else aPath.Append(new A.LineTo() { Point = P(currentX, currentY) });
                        
                        if (cmd == 'M') currentCommand = isRelative ? 'l' : 'L';
                        break;
                    case 'H':
                        if (index >= tokens.Count) break;
                        double hx = ReadNext();
                        currentX = isRelative ? currentX + hx : hx;
                        aPath.Append(new A.LineTo() { Point = P(currentX, currentY) });
                        break;
                    case 'V':
                        if (index >= tokens.Count) break;
                        double vy = ReadNext();
                        currentY = isRelative ? currentY + vy : vy;
                        aPath.Append(new A.LineTo() { Point = P(currentX, currentY) });
                        break;
                    case 'C':
                        if (index + 5 >= tokens.Count) break;
                        double cx1 = ReadNext(), cy1 = ReadNext();
                        double cx2 = ReadNext(), cy2 = ReadNext();
                        double cx3 = ReadNext(), cy3 = ReadNext();

                        if (isRelative)
                        {
                            cx1 += currentX; cy1 += currentY;
                            cx2 += currentX; cy2 += currentY;
                            cx3 += currentX; cy3 += currentY;
                        }

                        aPath.Append(new A.CubicBezierCurveTo(
                            new A.Point() { X = ((long)(cx1 * emuPerUnit)).ToString(), Y = ((long)(cy1 * emuPerUnit)).ToString() },
                            new A.Point() { X = ((long)(cx2 * emuPerUnit)).ToString(), Y = ((long)(cy2 * emuPerUnit)).ToString() },
                            new A.Point() { X = ((long)(cx3 * emuPerUnit)).ToString(), Y = ((long)(cy3 * emuPerUnit)).ToString() }
                        ));
                        currentX = cx3;
                        currentY = cy3;
                        break;
                    case 'Z':
                        aPath.Append(new A.CloseShapePath());
                        break;
                }
            }
            pathList.Append(aPath);
        }

        var customGeom = new A.CustomGeometry(pathList);

        var solidFill = new A.SolidFill(new A.RgbColorModelHex() { Val = ctx.TextHex ?? "000000" });
        var outline = new A.Outline(new A.SolidFill(new A.RgbColorModelHex() { Val = ctx.TextHex ?? "000000" })) { Width = 12700 };

        var wpsShape = new Wps.WordprocessingShape(
            new Wps.NonVisualDrawingProperties() { Id = 1U, Name = "SVG Shape" },
            new Wps.NonVisualDrawingShapeProperties(new A.ShapeLocks() { NoGrouping = true }),
            new Wps.ShapeProperties(
                new A.Transform2D(
                    new A.Offset() { X = 0L, Y = 0L },
                    new A.Extents() { Cx = shapeWidthEmu, Cy = shapeHeightEmu }),
                customGeom,
                solidFill,
                outline
            )
        );

        var inline = new DW.Inline(
            new DW.Extent() { Cx = shapeWidthEmu, Cy = shapeHeightEmu },
            new DW.EffectExtent() { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties() { Id = 1U, Name = "Picture" },
            new DW.NonVisualGraphicFrameDrawingProperties(),
            new A.Graphic(
                new A.GraphicData(wpsShape) { Uri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape" }
            )
        );

        var run = new W.Run(new W.Drawing(inline));
        
        if (target is W.Paragraph p)
        {
            p.Append(run);
        }
        else
        {
            target.Append(new W.Paragraph(run));
        }
    }
"@

$content = Get-Content $targetFile -Raw
$content = $content -replace '(?m)^\s*private static W\.Numbering AddNumbering\(MainDocumentPart main\)', ($code + "`n`n    private static W.Numbering AddNumbering(MainDocumentPart main)")
Set-Content -Path $targetFile -Value $content -Encoding UTF8
