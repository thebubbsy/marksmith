namespace MarkSmith.Mermaid.Parser;

using System.Text.RegularExpressions;
using MarkSmith.Mermaid.Ast;

public static class SequenceParser
{
    private static readonly Regex ParticipantRegex = new(@"^(participant|actor)\s+(?:""([^""]+)""|([^\s]+))(?:\s+as\s+(?:""([^""]+)""|([^\s]+)))?$", RegexOptions.IgnoreCase);
    private static readonly Regex MessageRegex = new(@"^([^\s\-><+x\\]+)\s*(->>|-->>|->|-->|-x|-\\)\s*([+-])?([^\s:]+)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex ReverseMessageRegex = new(@"^([^\s\-><+x\\]+)\s*(<<--|<<-|<--|<-)\s*([+-])?([^\s:]+)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex NoteRegex = new(@"^Note\s+(left of|right of|over)\s+([^\s:]+(?:\s*,\s*[^\s:]+)*)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex BlockStartRegex = new(@"^(loop|alt|opt|par|critical)\s*(.*)$", RegexOptions.IgnoreCase);

    public static SequenceDiagramAst Parse(string code)
    {
        var ast = new SequenceDiagramAst();
        var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList();

        Stack<SequenceBlock> blockStack = new();

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

            if (lower == "autonumber")
            {
                ast.AutoNumber = true;
                continue;
            }

            if (lower.StartsWith("title "))
            {
                ast.Title = line.Substring(6).Trim();
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

                var msg = new SequenceMessage
                {
                    FromId = fromId,
                    ToId = toId,
                    Text = msgText,
                    MessageType = msgType,
                    ActivateTarget = actFlag == "+",
                    DeactivateTarget = actFlag == "-"
                };

                if (blockStack.Count > 0)
                {
                    var currentBlock = blockStack.Peek();
                    if (currentBlock.ElseBranches.Count > 0)
                    {
                        currentBlock.ElseBranches[^1].Messages.Add(msg);
                    }
                    else
                    {
                        currentBlock.Messages.Add(msg);
                    }
                }
                else
                {
                    ast.Messages.Add(msg);
                }
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

                var msg = new SequenceMessage
                {
                    FromId = rightId,
                    ToId = leftId,
                    Text = msgText,
                    MessageType = msgType,
                    ActivateTarget = actFlag == "+",
                    DeactivateTarget = actFlag == "-"
                };

                if (blockStack.Count > 0)
                {
                    var currentBlock = blockStack.Peek();
                    if (currentBlock.ElseBranches.Count > 0)
                    {
                        currentBlock.ElseBranches[^1].Messages.Add(msg);
                    }
                    else
                    {
                        currentBlock.Messages.Add(msg);
                    }
                }
                else
                {
                    ast.Messages.Add(msg);
                }
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
                ast.Notes.Add(note);
                continue;
            }

            var blockStartMatch = BlockStartRegex.Match(line);
            if (blockStartMatch.Success)
            {
                string bTypeStr = blockStartMatch.Groups[1].Value.ToLowerInvariant();
                string header = blockStartMatch.Groups[2].Value.Trim();

                var bType = bTypeStr switch
                {
                    "loop" => SequenceBlockType.Loop,
                    "alt" => SequenceBlockType.Alt,
                    "opt" => SequenceBlockType.Opt,
                    "par" => SequenceBlockType.Par,
                    "critical" => SequenceBlockType.Critical,
                    _ => SequenceBlockType.Loop
                };

                var block = new SequenceBlock { BlockType = bType, HeaderText = header };
                ast.Blocks.Add(block);
                blockStack.Push(block);
                continue;
            }

            if (lower.StartsWith("else"))
            {
                if (blockStack.Count > 0)
                {
                    string cond = line.Length > 4 ? line.Substring(4).Trim() : string.Empty;
                    blockStack.Peek().ElseBranches.Add((cond, new List<SequenceMessage>()));
                }
                continue;
            }

            if (lower == "end")
            {
                if (blockStack.Count > 0)
                {
                    blockStack.Pop();
                }
                continue;
            }
        }

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
