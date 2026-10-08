namespace MarkSmith.Mermaid.Parser;

using System.Text.RegularExpressions;
using MarkSmith.Mermaid.Ast;

public static class SequenceParser
{
    private static readonly Regex ParticipantRegex = new(@"^(participant|actor)\s+(?:""([^""]+)""|([^\s]+))(?:\s+as\s+(?:""([^""]+)""|([^\s]+)))?$", RegexOptions.IgnoreCase);
    private static readonly Regex MessageRegex = new(@"^([^\s\-><+x\\]+)\s*(->>|-->>|->|-->|-x|-\\)\s*([+-])?([^\s:]+)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex ReverseMessageRegex = new(@"^([^\s\-><+x\\]+)\s*(<<--|<<-|<--|<-)\s*([+-])?([^\s:]+)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex NoteRegex = new(@"^Note\s+(left of|right of|over)\s+([^\s:]+(?:\s*,\s*[^\s:]+)*)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    // Word boundary after the keyword: "parse x" is not a par block, nor "options" an option.
    private static readonly Regex BlockStartRegex = new(@"^(loop|alt|opt|par_over|par|critical|break|rect)(?:\s+(.*))?$", RegexOptions.IgnoreCase);
    private static readonly Regex DividerRegex = new(@"^(else|and|option)(?:\s+(.*))?$", RegexOptions.IgnoreCase);
    private static readonly Regex ActivationRegex = new(@"^(activate|deactivate)\s+(\S+)$", RegexOptions.IgnoreCase);

    public static SequenceDiagramAst Parse(string code)
    {
        var ast = new SequenceDiagramAst();
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

        // Every body line lands in ast.Statements in the order it was written; Messages, Blocks
        // and Notes are derived from that at the end, so nesting and order survive a round trip.
        int depth = 0;
        int boxDepth = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("%%"))
            {
                if (line.StartsWith("%%{"))
                    ast.Directives.Add(line);
                else
                    ast.Comments.Add(line.Substring(2).Trim());
                continue;
            }

            string lower = line.ToLowerInvariant();
            if (lower == "sequencediagram")
                continue;

            // Before any message, "autonumber" is the diagram-wide flag; after one, it switches
            // numbering back on from that row, so it stays in place as a statement.
            if (lower == "autonumber" && !ast.Statements.Any(s => s.Kind == SequenceStatementKind.Message))
            {
                ast.AutoNumber = true;
                continue;
            }

            if (lower.StartsWith("title "))
            {
                ast.Title = line.Substring(6).Trim();
                continue;
            }

            // A participant box only groups headers; its participants are read as usual and the
            // grouping itself is not kept (it never was). Its "end" must not close a real block.
            if (lower == "box" || lower.StartsWith("box "))
            {
                boxDepth++;
                continue;
            }

            var partMatch = ParticipantRegex.Match(line);
            if (partMatch.Success)
            {
                string pTypeStr = partMatch.Groups[1].Value;
                string pId = !string.IsNullOrEmpty(partMatch.Groups[2].Value) ? partMatch.Groups[2].Value : partMatch.Groups[3].Value;
                string alias = !string.IsNullOrEmpty(partMatch.Groups[4].Value) ? partMatch.Groups[4].Value : (!string.IsNullOrEmpty(partMatch.Groups[5].Value) ? partMatch.Groups[5].Value : pId);

                var pType = pTypeStr.Equals("actor", StringComparison.OrdinalIgnoreCase) ? SequenceParticipantType.Actor : SequenceParticipantType.Participant;
                
                if (!ast.Participants.Any(p => p.Id.Equals(pId, StringComparison.OrdinalIgnoreCase)))
                {
                    ast.Participants.Add(new SequenceParticipant { Id = pId, Alias = alias, Type = pType });
                }
                continue;
            }

            var msgMatch = MessageRegex.Match(line);
            if (msgMatch.Success)
            {
                string fromId = msgMatch.Groups[1].Value.Trim();
                string arrow = msgMatch.Groups[2].Value.Trim();
                string actFlag = msgMatch.Groups[3].Value;
                string toId = msgMatch.Groups[4].Value.Trim();
                string msgText = msgMatch.Groups[5].Value.Trim();

                EnsureParticipant(ast, fromId);
                EnsureParticipant(ast, toId);

                var msgType = arrow switch
                {
                    "-->>" => SequenceMessageType.DashedArrow,
                    "->" => SequenceMessageType.SolidOpen,
                    "-->" => SequenceMessageType.DashedOpen,
                    "-x" => SequenceMessageType.CrossArrow,
                    "-\\" => SequenceMessageType.PointArrow,
                    _ => SequenceMessageType.SolidArrow
                };

                ast.Statements.Add(SequenceStatement.ForMessage(new SequenceMessage
                {
                    FromId = fromId,
                    ToId = toId,
                    Text = msgText,
                    MessageType = msgType,
                    ActivateTarget = actFlag == "+",
                    DeactivateTarget = actFlag == "-"
                }));
                continue;
            }

            var revMatch = ReverseMessageRegex.Match(line);
            if (revMatch.Success)
            {
                string leftId = revMatch.Groups[1].Value.Trim();
                string arrow = revMatch.Groups[2].Value.Trim();
                string actFlag = revMatch.Groups[3].Value;
                string rightId = revMatch.Groups[4].Value.Trim();
                string msgText = revMatch.Groups[5].Value.Trim();

                EnsureParticipant(ast, rightId);
                EnsureParticipant(ast, leftId);

                var msgType = arrow switch
                {
                    "<<--" => SequenceMessageType.DashedArrow,
                    "<<-" => SequenceMessageType.DashedOpen,
                    "<--" => SequenceMessageType.DashedOpen,
                    "<-" => SequenceMessageType.SolidOpen,
                    _ => SequenceMessageType.SolidArrow
                };

                ast.Statements.Add(SequenceStatement.ForMessage(new SequenceMessage
                {
                    FromId = rightId,
                    ToId = leftId,
                    Text = msgText,
                    MessageType = msgType,
                    ActivateTarget = actFlag == "+",
                    DeactivateTarget = actFlag == "-"
                }));
                continue;
            }

            var noteMatch = NoteRegex.Match(line);
            if (noteMatch.Success)
            {
                string placementStr = noteMatch.Groups[1].Value.Trim().ToLowerInvariant();
                string targetsStr = noteMatch.Groups[2].Value.Trim();
                string noteText = noteMatch.Groups[3].Value.Trim();

                var placement = placementStr switch
                {
                    "left of" => NotePlacement.LeftOf,
                    "right of" => NotePlacement.RightOf,
                    _ => NotePlacement.Over
                };

                var targets = targetsStr.Split(',').Select(t => t.Trim()).ToList();
                foreach (var target in targets)
                {
                    EnsureParticipant(ast, target);
                }

                var note = new SequenceNote
                {
                    Placement = placement,
                    Text = noteText
                };
                note.TargetParticipantIds.AddRange(targets);
                ast.Statements.Add(SequenceStatement.ForNote(note));
                continue;
            }

            var actMatch = ActivationRegex.Match(line);
            if (actMatch.Success)
            {
                string who = actMatch.Groups[2].Value;
                EnsureParticipant(ast, who);
                bool on = actMatch.Groups[1].Value.Equals("activate", StringComparison.OrdinalIgnoreCase);
                ast.Statements.Add(new SequenceStatement { Kind = on ? SequenceStatementKind.Activate : SequenceStatementKind.Deactivate, ParticipantId = who });
                continue;
            }

            var blockStartMatch = BlockStartRegex.Match(line);
            if (blockStartMatch.Success)
            {
                string keyword = blockStartMatch.Groups[1].Value.ToLowerInvariant();
                var bType = keyword switch
                {
                    "alt" => SequenceBlockType.Alt,
                    "opt" => SequenceBlockType.Opt,
                    "par" or "par_over" => SequenceBlockType.Par,
                    "critical" => SequenceBlockType.Critical,
                    "break" => SequenceBlockType.Break,
                    "rect" => SequenceBlockType.Rect,
                    _ => SequenceBlockType.Loop
                };
                ast.Statements.Add(new SequenceStatement
                {
                    Kind = SequenceStatementKind.BlockStart,
                    BlockType = bType,
                    Keyword = keyword,
                    Text = blockStartMatch.Groups[2].Value.Trim()
                });
                depth++;
                continue;
            }

            var dividerMatch = DividerRegex.Match(line);
            if (dividerMatch.Success && depth > 0)
            {
                ast.Statements.Add(new SequenceStatement
                {
                    Kind = SequenceStatementKind.BlockDivider,
                    Keyword = dividerMatch.Groups[1].Value.ToLowerInvariant(),
                    Text = dividerMatch.Groups[2].Value.Trim()
                });
                continue;
            }

            if (lower == "end")
            {
                if (depth > 0)
                {
                    ast.Statements.Add(new SequenceStatement { Kind = SequenceStatementKind.BlockEnd, Keyword = "end" });
                    depth--;
                }
                else if (boxDepth > 0)
                {
                    boxDepth--;
                }
                continue;
            }

            // Anything else Mermaid accepts that isn't modelled here (create/destroy, links,
            // "autonumber 10 5", ...) is kept verbatim in place instead of silently vanishing.
            // A participant line the regex couldn't read is still dropped: re-emitting it after
            // the messages would declare the participant a second time.
            if (!lower.StartsWith("participant ") && !lower.StartsWith("actor "))
                ast.Statements.Add(new SequenceStatement { Kind = SequenceStatementKind.Raw, Text = line });
        }

        // Close blocks the author left open so the generated code always balances.
        for (; depth > 0; depth--)
            ast.Statements.Add(new SequenceStatement { Kind = SequenceStatementKind.BlockEnd, Keyword = "end" });

        ast.RebuildIndexes();
        return ast;
    }

    private static void EnsureParticipant(SequenceDiagramAst ast, string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!ast.Participants.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            ast.Participants.Add(new SequenceParticipant { Id = id, Alias = id, Type = SequenceParticipantType.Participant });
        }
    }
}
