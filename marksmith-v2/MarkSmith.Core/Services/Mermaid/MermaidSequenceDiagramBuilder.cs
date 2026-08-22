using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Mermaid;

/// <summary>
/// Model for an actor/participant in a Mermaid sequence diagram.
/// </summary>
public record SequenceParticipant(string Id, string DisplayName, bool IsActor = false);

/// <summary>
/// Model for a message arrow between participants in a sequence diagram.
/// </summary>
public record SequenceMessage(
    string From,
    string To,
    string ArrowType,
    string Label,
    bool Activate = false,
    bool Deactivate = false);

/// <summary>
/// Model for a note box in a sequence diagram.
/// </summary>
public record SequenceNote(string Target, string Text, string Position = "over");

/// <summary>
/// Comprehensive AST builder and message reordering engine for Mermaid sequence diagrams.
/// </summary>
public class MermaidSequenceDiagramBuilder
{
    private static readonly Regex ParticipantRegex = new(
        @"^\s*(participant|actor)\s+([A-Za-z0-9_]+)(?:\s+as\s+""?([^""\r\n]+?)""?)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MessageRegex = new(
        @"^\s*([A-Za-z0-9_]+)\s*(\-\>>|\-\>|\-\-\>>|\-\-\>|\-\>x|\-\>>\+)\s*([A-Za-z0-9_]+)\s*:\s*(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex NoteRegex = new(
        @"^\s*Note\s+(over|left of|right of)\s+([A-Za-z0-9_,\s]+)\s*:\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public List<SequenceParticipant> Participants { get; } = new();
    public List<SequenceMessage> Messages { get; } = new();
    public List<SequenceNote> Notes { get; } = new();
    public bool AutoNumber { get; set; } = true;

    /// <summary>
    /// Parses a raw Mermaid sequence diagram code block into structured models.
    /// </summary>
    public static MermaidSequenceDiagramBuilder Parse(string mermaidCode)
    {
        var builder = new MermaidSequenceDiagramBuilder();
        if (string.IsNullOrWhiteSpace(mermaidCode))
            return builder;

        var lines = mermaidCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("%%") || line.Equals("sequenceDiagram", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.Equals("autonumber", StringComparison.OrdinalIgnoreCase))
            {
                builder.AutoNumber = true;
                continue;
            }

            var partMatch = ParticipantRegex.Match(line);
            if (partMatch.Success)
            {
                bool isActor = partMatch.Groups[1].Value.Equals("actor", StringComparison.OrdinalIgnoreCase);
                string id = partMatch.Groups[2].Value;
                string name = partMatch.Groups[3].Success ? partMatch.Groups[3].Value : id;
                if (!builder.Participants.Any(p => p.Id == id))
                {
                    builder.Participants.Add(new SequenceParticipant(id, name, isActor));
                }
                continue;
            }

            var noteMatch = NoteRegex.Match(line);
            if (noteMatch.Success)
            {
                string pos = noteMatch.Groups[1].Value;
                string target = noteMatch.Groups[2].Value.Trim();
                string noteText = noteMatch.Groups[3].Value.Trim();
                builder.Notes.Add(new SequenceNote(target, noteText, pos));
                continue;
            }

            var msgMatch = MessageRegex.Match(line);
            if (msgMatch.Success)
            {
                string from = msgMatch.Groups[1].Value;
                string arrow = msgMatch.Groups[2].Value;
                string to = msgMatch.Groups[3].Value;
                string label = msgMatch.Groups[4].Value;

                // Ensure participants exist
                if (!builder.Participants.Any(p => p.Id == from))
                    builder.Participants.Add(new SequenceParticipant(from, from));
                if (!builder.Participants.Any(p => p.Id == to))
                    builder.Participants.Add(new SequenceParticipant(to, to));

                builder.Messages.Add(new SequenceMessage(from, to, arrow, label));
            }
        }

        return builder;
    }

    /// <summary>
    /// Reorders a message at oldIndex to newIndex.
    /// </summary>
    public bool MoveMessage(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= Messages.Count || newIndex < 0 || newIndex >= Messages.Count)
            return false;

        var item = Messages[oldIndex];
        Messages.RemoveAt(oldIndex);
        Messages.Insert(newIndex, item);
        return true;
    }

    /// <summary>
    /// Serializes the structured sequence diagram back to Mermaid syntax.
    /// </summary>
    public string ToMermaidSyntax()
    {
        var sb = new StringBuilder();
        sb.AppendLine("sequenceDiagram");
        if (AutoNumber)
            sb.AppendLine("    autonumber");

        foreach (var p in Participants)
        {
            string keyword = p.IsActor ? "actor" : "participant";
            if (p.DisplayName != p.Id)
                sb.AppendLine($"    {keyword} {p.Id} as \"{p.DisplayName}\"");
            else
                sb.AppendLine($"    {keyword} {p.Id}");
        }

        foreach (var n in Notes)
        {
            sb.AppendLine($"    Note {n.Position} {n.Target}: {n.Text}");
        }

        foreach (var m in Messages)
        {
            sb.AppendLine($"    {m.From}{m.ArrowType}{m.To}: {m.Label}");
        }

        return sb.ToString().TrimEnd();
    }
}
