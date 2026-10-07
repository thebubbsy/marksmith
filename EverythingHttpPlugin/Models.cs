using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EverythingHttpPlugin
{
    public sealed class PluginConfig
    {
        public int Port { get; set; } = 8080;
        public string Host { get; set; } = "127.0.0.1";
        public string Username { get; set; } = "user";
        public string Password { get; set; } = "IntelLetni4789$";
        public bool AuthEnabled { get; set; } = true;
        public string HomeDirectory { get; set; } = "";
        public string EverythingInstance { get; set; } = "1.5a";
        public int EverythingPort { get; set; } = 8011;
        public string ThumbnailCacheDir { get; set; } = "";
        public bool AllowFileDownload { get; set; } = true;
        public string SessionSecret { get; set; } = Guid.NewGuid().ToString("N");
    }

    public sealed class SearchResultItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("fullPath")]
        public string FullPath { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("dateModified")]
        public long DateModified { get; set; }

        [JsonPropertyName("isFolder")]
        public bool IsFolder { get; set; }

        [JsonPropertyName("extension")]
        public string Extension { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "other";

        [JsonPropertyName("streamUrl")]
        public string StreamUrl { get; set; } = "";

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = "";

        [JsonPropertyName("thumbnailUrl")]
        public string ThumbnailUrl { get; set; } = "";

        [JsonPropertyName("sizeFormatted")]
        public string SizeFormatted { get; set; } = "-";

        [JsonPropertyName("dateModifiedRelative")]
        public string DateModifiedRelative { get; set; } = "";

        [JsonPropertyName("canPreviewImage")]
        public bool CanPreviewImage { get; set; }

        [JsonPropertyName("canPreviewVideo")]
        public bool CanPreviewVideo { get; set; }

        [JsonPropertyName("canPreviewAudio")]
        public bool CanPreviewAudio { get; set; }

        [JsonPropertyName("canPreviewText")]
        public bool CanPreviewText { get; set; }

        [JsonPropertyName("canPreviewPdf")]
        public bool CanPreviewPdf { get; set; }

        [JsonPropertyName("isGoogleDrive")]
        public bool IsGoogleDrive { get; set; }
    }

    public sealed class SearchResponse
    {
        [JsonPropertyName("totalResults")]
        public long TotalResults { get; set; }

        [JsonPropertyName("results")]
        public List<SearchResultItem> Results { get; set; } = new();

        [JsonPropertyName("offset")]
        public int Offset { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("query")]
        public string Query { get; set; } = "";

        [JsonPropertyName("timeMs")]
        public double TimeMs { get; set; }
    }

    public sealed class DriveInfoModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("volumeLabel")]
        public string VolumeLabel { get; set; } = "";

        [JsonPropertyName("driveType")]
        public string DriveType { get; set; } = "";

        [JsonPropertyName("totalSize")]
        public long TotalSize { get; set; }

        [JsonPropertyName("freeSpace")]
        public long FreeSpace { get; set; }

        [JsonPropertyName("isReady")]
        public bool IsReady { get; set; }
    }

    public sealed class GoogleDriveStatusModel
    {
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        [JsonPropertyName("running")]
        public bool Running { get; set; }

        [JsonPropertyName("mounted")]
        public bool Mounted { get; set; }

        [JsonPropertyName("mountPath")]
        public string MountPath { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
    }
}
