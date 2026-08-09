using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkSmith.Core.Glox;

namespace MarkSmith.Core.Resolver
{
    public class UrnResolutionException : Exception
    {
        public UrnResolutionException(string message) : base(message) { }
    }

    public class UrnResolver
    {
        private readonly Dictionary<string, GloxPackage> _registryByUrn = new Dictionary<string, GloxPackage>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public UrnResolver()
        {
            RegisterDefaultAliases();
        }

        private void RegisterDefaultAliases()
        {
            // Authoritative Microsoft Office URNs — mirrors SmartArtLayoutCatalog.RegisterAliases
            // so any pipeline wired through UrnResolver resolves the same layouts as the catalog.
            _aliasMap["hierarchy"] = "urn:microsoft.com/office/officeart/2005/8/layout/orgChart1";
            _aliasMap["orgchart"] = "urn:microsoft.com/office/officeart/2005/8/layout/orgChart1";
            _aliasMap["org"] = "urn:microsoft.com/office/officeart/2005/8/layout/orgChart1";
            _aliasMap["tree"] = "urn:microsoft.com/office/officeart/2005/8/layout/orgChart1";
            _aliasMap["cycle"] = "urn:microsoft.com/office/officeart/2005/8/layout/cycle1";
            _aliasMap["picturelist"] = "urn:microsoft.com/office/officeart/2005/8/layout/pList1";
            _aliasMap["picture"] = "urn:microsoft.com/office/officeart/2005/8/layout/pList1";
            _aliasMap["process"] = "urn:microsoft.com/office/officeart/2005/8/layout/process1";
            _aliasMap["list"] = "urn:microsoft.com/office/officeart/2005/8/layout/default";
            _aliasMap["default"] = "urn:microsoft.com/office/officeart/2005/8/layout/default";
            _aliasMap["matrix"] = "urn:microsoft.com/office/officeart/2005/8/layout/matrix1";
            _aliasMap["pyramid"] = "urn:microsoft.com/office/officeart/2005/8/layout/pyramid1";
            _aliasMap["relationship"] = "urn:microsoft.com/office/officeart/2009/3/layout/CircleRelationship";
            _aliasMap["composite"] = "urn:microsoft.com/office/officeart/2009/3/layout/CircleRelationship";
            _aliasMap["venn"] = "urn:microsoft.com/office/officeart/2005/8/layout/venn1";
        }

        public void RegisterLayout(GloxPackage package)
        {
            if (string.IsNullOrWhiteSpace(package.UniqueId)) return;
            _registryByUrn[package.UniqueId] = package;
            if (!string.IsNullOrWhiteSpace(package.Title))
            {
                _aliasMap[package.Title] = package.UniqueId;
            }
        }

        public GloxPackage Resolve(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new UrnResolutionException("Layout URN resolution failed: input query cannot be null or empty.");
            }

            string normalizedInput = NormalizeUrn(input);

            // 1. Direct URN match
            if (_registryByUrn.TryGetValue(normalizedInput, out var directMatch))
            {
                return directMatch;
            }

            // 2. Alias lookup
            if (_aliasMap.TryGetValue(normalizedInput, out var mappedUrn) && _registryByUrn.TryGetValue(mappedUrn, out var aliasMatch))
            {
                return aliasMatch;
            }

            // 3. Suffix / Tail matching
            var suffixMatch = _registryByUrn.Values.FirstOrDefault(p =>
                p.UniqueId.EndsWith("/" + normalizedInput, StringComparison.OrdinalIgnoreCase) ||
                p.UniqueId.EndsWith(normalizedInput, StringComparison.OrdinalIgnoreCase));

            if (suffixMatch != null) return suffixMatch;

            // 4. Fuzzy matching
            string? bestKey = null;
            int minDistance = int.MaxValue;

            foreach (var key in _aliasMap.Keys)
            {
                int dist = ComputeLevenshteinDistance(normalizedInput, key.ToLowerInvariant());
                if (dist < minDistance && dist <= 3)
                {
                    minDistance = dist;
                    bestKey = key;
                }
            }

            if (bestKey != null && _aliasMap.TryGetValue(bestKey, out var fuzzyUrn) && _registryByUrn.TryGetValue(fuzzyUrn, out var fuzzyMatch))
            {
                return fuzzyMatch;
            }

            // HARD FAIL - Zero Fallback Guarantee
            throw new UrnResolutionException($"Explicit error: Failed to resolve SmartArt layout URN for '{input}'. Zero-fallback guarantee strictly enforced.");
        }

        private static string NormalizeUrn(string input)
        {
            return input.Trim().ToLowerInvariant().Replace('\\', '/');
        }

        private static int ComputeLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; d[i, 0] = i++) { }
            for (int j = 0; j <= m; d[0, j] = j++) { }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }
    }
}
