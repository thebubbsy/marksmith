using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public enum FormFieldType
{
    Checkbox,
    TextInput,
    DateInput,
    DropdownChoice
}

public record FormFieldDescriptor(
    string Name,
    FormFieldType FieldType,
    string? DefaultValue = null,
    string? Placeholder = null,
    List<string>? Options = null);

/// <summary>
/// Parser and generator for interactive form fields in Markdown documents.
/// </summary>
public static class PdfFormFieldService
{
    private static readonly Regex CheckboxRegex = new(@"\[([ xX])\]", RegexOptions.Compiled);
    private static readonly Regex TextInputRegex = new(@"\[text:([a-zA-Z0-9_]+)(?::([^\]]+))?\]", RegexOptions.Compiled);
    private static readonly Regex DateInputRegex = new(@"\[date:([a-zA-Z0-9_]+)\]", RegexOptions.Compiled);
    private static readonly Regex DropdownRegex = new(@"\[choice:([a-zA-Z0-9_]+):([^\]]+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Scans a Markdown string and extracts all declared interactive form fields.
    /// </summary>
    public static List<FormFieldDescriptor> ExtractFormFields(string markdown)
    {
        var list = new List<FormFieldDescriptor>();
        if (string.IsNullOrWhiteSpace(markdown))
            return list;

        // Checkboxes
        int cbIndex = 1;
        foreach (Match m in CheckboxRegex.Matches(markdown))
        {
            bool isChecked = m.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
            list.Add(new FormFieldDescriptor($"checkbox_{cbIndex++}", FormFieldType.Checkbox, DefaultValue: isChecked ? "true" : "false"));
        }

        // Text inputs
        foreach (Match m in TextInputRegex.Matches(markdown))
        {
            string name = m.Groups[1].Value;
            string placeholder = m.Groups[2].Success ? m.Groups[2].Value : "";
            list.Add(new FormFieldDescriptor(name, FormFieldType.TextInput, Placeholder: placeholder));
        }

        // Date inputs
        foreach (Match m in DateInputRegex.Matches(markdown))
        {
            string name = m.Groups[1].Value;
            list.Add(new FormFieldDescriptor(name, FormFieldType.DateInput));
        }

        // Dropdown choices
        foreach (Match m in DropdownRegex.Matches(markdown))
        {
            string name = m.Groups[1].Value;
            var opts = new List<string>(m.Groups[2].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            list.Add(new FormFieldDescriptor(name, FormFieldType.DropdownChoice, Options: opts));
        }

        return list;
    }

    /// <summary>
    /// Transforms Markdown form field syntax into interactive HTML input controls for preview rendering.
    /// </summary>
    public static string TransformToHtmlInputs(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        string result = TextInputRegex.Replace(markdown, m =>
        {
            string name = m.Groups[1].Value;
            string placeholder = m.Groups[2].Success ? m.Groups[2].Value : "";
            return $"<input type=\"text\" name=\"{name}\" placeholder=\"{placeholder}\" class=\"ms-form-input\" />";
        });

        result = DateInputRegex.Replace(result, m =>
        {
            string name = m.Groups[1].Value;
            return $"<input type=\"date\" name=\"{name}\" class=\"ms-form-date\" />";
        });

        result = DropdownRegex.Replace(result, m =>
        {
            string name = m.Groups[1].Value;
            var opts = m.Groups[2].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sb = new StringBuilder();
            sb.Append($"<select name=\"{name}\" class=\"ms-form-select\">");
            foreach (var opt in opts)
            {
                sb.Append($"<option value=\"{opt}\">{opt}</option>");
            }
            sb.Append("</select>");
            return sb.ToString();
        });

        return result;
    }
}
