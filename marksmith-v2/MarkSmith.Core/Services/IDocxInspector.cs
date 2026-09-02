using System.IO;
using MarkSmith.Models;

namespace MarkSmith.Core.Services;

public interface IDocxInspector
{
    DocxStructureReport Inspect(string docxPath, DocxInspectionOptions? options = null);
    DocxStructureReport Inspect(Stream docxStream, DocxInspectionOptions? options = null);
}
