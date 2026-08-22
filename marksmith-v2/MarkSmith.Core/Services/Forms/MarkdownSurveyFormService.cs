using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Forms;

public enum SurveyQuestionType
{
    Text,
    Email,
    Choice,
    Rating,
    TextArea
}

public record SurveyQuestion(
    int QuestionIndex,
    string Prompt,
    SurveyQuestionType QuestionType,
    bool IsRequired,
    List<string> Options);

public class SurveyFormModel
{
    public string FormTitle { get; set; } = "Survey";
    public List<SurveyQuestion> Questions { get; } = new();
}

/// <summary>
/// Service that transforms Markdown survey questionnaires into interactive HTML5 forms with client-side response collection.
/// </summary>
public static class MarkdownSurveyFormService
{
    private static readonly Regex QuestionRegex = new(
        @"^\(\?\s*([^)]+)\)\s*\[([a-zA-Z]+)(?::\s*([^\]]+))?\]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parses survey questions from Markdown syntax.
    /// </summary>
    public static SurveyFormModel ParseSurvey(string markdown, string defaultTitle = "Survey")
    {
        var model = new SurveyFormModel { FormTitle = defaultTitle };
        if (string.IsNullOrWhiteSpace(markdown))
            return model;

        int index = 1;
        foreach (Match m in QuestionRegex.Matches(markdown))
        {
            string prompt = m.Groups[1].Value.Trim();
            string typeStr = m.Groups[2].Value.ToLowerInvariant();
            string paramStr = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "";

            bool isRequired = paramStr.Contains("required", StringComparison.OrdinalIgnoreCase) || prompt.EndsWith("*");
            if (prompt.EndsWith("*")) prompt = prompt.TrimEnd('*').Trim();

            var qType = typeStr switch
            {
                "email" => SurveyQuestionType.Email,
                "choice" or "select" => SurveyQuestionType.Choice,
                "rating" => SurveyQuestionType.Rating,
                "textarea" or "longtext" => SurveyQuestionType.TextArea,
                _ => SurveyQuestionType.Text
            };

            var options = new List<string>();
            if (qType == SurveyQuestionType.Choice && !string.IsNullOrEmpty(paramStr))
            {
                options = paramStr.Split('|').Select(o => o.Trim()).Where(o => !string.IsNullOrEmpty(o)).ToList();
            }

            model.Questions.Add(new SurveyQuestion(index++, prompt, qType, isRequired, options));
        }

        return model;
    }

    /// <summary>
    /// Renders an interactive HTML5 survey form component.
    /// </summary>
    public static string RenderFormHtml(SurveyFormModel form)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<form class=\"ms-survey-form\" id=\"survey-form\" onsubmit=\"submitSurvey(event)\">");
        sb.AppendLine($"  <h3 class=\"ms-survey-title\">{System.Net.WebUtility.HtmlEncode(form.FormTitle)}</h3>");

        foreach (var q in form.Questions)
        {
            string reqMark = q.IsRequired ? " <span class=\"ms-req\">*</span>" : "";
            sb.AppendLine($"  <div class=\"ms-form-group\" data-qidx=\"{q.QuestionIndex}\">");
            sb.AppendLine($"    <label class=\"ms-form-label\">{q.QuestionIndex}. {System.Net.WebUtility.HtmlEncode(q.Prompt)}{reqMark}</label>");

            switch (q.QuestionType)
            {
                case SurveyQuestionType.Email:
                    sb.AppendLine($"    <input type=\"email\" name=\"q_{q.QuestionIndex}\" class=\"ms-form-input\" {(q.IsRequired ? "required" : "")} />");
                    break;
                case SurveyQuestionType.Choice:
                    sb.AppendLine($"    <select name=\"q_{q.QuestionIndex}\" class=\"ms-form-select\" {(q.IsRequired ? "required" : "")}>");
                    sb.AppendLine("      <option value=\"\">-- Select an option --</option>");
                    foreach (var opt in q.Options)
                    {
                        sb.AppendLine($"      <option value=\"{System.Net.WebUtility.HtmlEncode(opt)}\">{System.Net.WebUtility.HtmlEncode(opt)}</option>");
                    }
                    sb.AppendLine("    </select>");
                    break;
                case SurveyQuestionType.Rating:
                    sb.AppendLine("    <div class=\"ms-rating-stars\">");
                    for (int i = 1; i <= 5; i++)
                    {
                        sb.AppendLine($"      <label><input type=\"radio\" name=\"q_{q.QuestionIndex}\" value=\"{i}\" /> &#9733; {i}</label>");
                    }
                    sb.AppendLine("    </div>");
                    break;
                case SurveyQuestionType.TextArea:
                    sb.AppendLine($"    <textarea name=\"q_{q.QuestionIndex}\" rows=\"3\" class=\"ms-form-textarea\" {(q.IsRequired ? "required" : "")}></textarea>");
                    break;
                default:
                    sb.AppendLine($"    <input type=\"text\" name=\"q_{q.QuestionIndex}\" class=\"ms-form-input\" {(q.IsRequired ? "required" : "")} />");
                    break;
            }

            sb.AppendLine("  </div>");
        }

        sb.AppendLine("  <button type=\"submit\" class=\"ms-form-submit-btn\">Submit Response</button>");
        sb.AppendLine("</form>");
        return sb.ToString();
    }
}
