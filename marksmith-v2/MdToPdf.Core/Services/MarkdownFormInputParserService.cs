using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MdToPdf.Core.Services
{
    public class FormInputField
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "text"; // text, checkbox, select, radio
        public string Label { get; set; } = string.Empty;
        public string DefaultValue { get; set; } = string.Empty;
        public List<string> Options { get; set; } = new();
    }

    public class MarkdownFormInputParserService
    {
        private static readonly Regex TextInputRegex = new Regex(@"\[input:(?<name>[a-zA-Z0-9_\-]+)(?:\s+(?<type>text|number|date|email))?(?:\s+(?:""(?<default>[^""]*)""|'(?<default>[^']*)'))?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SelectRegex = new Regex(@"\[select:(?<name>[a-zA-Z0-9_\-]+)\s+options=\[(?<options>[^\]]+)\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parses custom form input syntax from markdown text and extracts structured field definitions.
        /// Syntax examples:
        ///   [input:user_name "John Doe"]
        ///   [select:country options=[USA, UK, Canada, Australia]]
        /// </summary>
        public List<FormInputField> ExtractFormInputs(string markdown)
        {
            var fields = new List<FormInputField>();
            if (string.IsNullOrWhiteSpace(markdown)) return fields;

            var textMatches = TextInputRegex.Matches(markdown);
            foreach (Match match in textMatches)
            {
                fields.Add(new FormInputField
                {
                    Name = match.Groups["name"].Value,
                    Type = match.Groups["type"].Success ? match.Groups["type"].Value.ToLowerInvariant() : "text",
                    Label = match.Groups["name"].Value.Replace("_", " "),
                    DefaultValue = match.Groups["default"].Success ? match.Groups["default"].Value : string.Empty
                });
            }

            var selectMatches = SelectRegex.Matches(markdown);
            foreach (Match match in selectMatches)
            {
                var optionsRaw = match.Groups["options"].Value;
                var options = new List<string>();
                foreach (var opt in optionsRaw.Split(','))
                {
                    var trimmed = opt.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) options.Add(trimmed);
                }

                fields.Add(new FormInputField
                {
                    Name = match.Groups["name"].Value,
                    Type = "select",
                    Label = match.Groups["name"].Value.Replace("_", " "),
                    Options = options
                });
            }

            return fields;
        }

        /// <summary>
        /// Converts form input markup into HTML form elements for live preview rendering.
        /// </summary>
        public string RenderInputsToHtml(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return markdown;

            string result = TextInputRegex.Replace(markdown, m =>
            {
                string name = m.Groups["name"].Value;
                string type = m.Groups["type"].Success ? m.Groups["type"].Value.ToLowerInvariant() : "text";
                string defVal = m.Groups["default"].Success ? m.Groups["default"].Value : string.Empty;
                return $"<input type=\"{type}\" name=\"{name}\" value=\"{defVal}\" class=\"md-form-input\" style=\"padding:4px 8px; border:1px solid #ccc; border-radius:4px; font-family:inherit;\" />";
            });

            result = SelectRegex.Replace(result, m =>
            {
                string name = m.Groups["name"].Value;
                string optionsRaw = m.Groups["options"].Value;
                var sb = new System.Text.StringBuilder();
                sb.Append($"<select name=\"{name}\" class=\"md-form-select\" style=\"padding:4px 8px; border:1px solid #ccc; border-radius:4px; font-family:inherit;\">");
                foreach (var opt in optionsRaw.Split(','))
                {
                    var trimmed = opt.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        sb.Append($"<option value=\"{trimmed}\">{trimmed}</option>");
                    }
                }
                sb.Append("</select>");
                return sb.ToString();
            });

            return result;
        }
    }
}
