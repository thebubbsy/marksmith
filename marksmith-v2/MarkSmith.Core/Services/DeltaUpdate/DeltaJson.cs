using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkSmith.Services.DeltaUpdate;

internal static class DeltaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
