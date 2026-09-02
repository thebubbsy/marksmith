using System.IO;
using MarkSmith.Models;

namespace MarkSmith.Core.Services;

public interface IInPlaceDocxPatcher
{
    PatchResult ApplyPatch(string docxPath, DocxPatchRequest request);
    PatchResult ApplyPatch(Stream docxStream, Stream outputStream, DocxPatchRequest request);
}
