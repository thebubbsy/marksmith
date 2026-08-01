using System;
using System.Security.Cryptography;
using System.Text;

namespace MdToPdf.Core.AdvancedFeatures
{
    public static class StableIdGenerator
    {
        /// <summary>
        /// Generates a deterministic GUID from a document ID and block text.
        /// The same inputs always produce the same output, which is critical for
        /// linking Word Content Controls (w:sdt) to Custom XML Parts across exports.
        /// </summary>
        public static string Generate(string documentId, string blockText, int index = 0)
        {
            // SHA256.HashData is static, thread-safe, and allocates no disposable SHA256 instance —
            // it replaces the per-call SHA256.Create() + Dispose this method used to do (one crypto
            // object per feature node per export).
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}|{index}|{blockText}"));
            // Take first 16 bytes of the SHA-256 hash to form a valid GUID
            return new Guid(hash.AsSpan(0, 16)).ToString("D");
        }
    }
}
