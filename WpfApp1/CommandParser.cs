using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WpfApp1;

/// <summary>
/// Parses the [CMD: ...] block from LLM output and returns structured commands.
/// </summary>
public static class CommandParser
{
    private static readonly Regex CmdBlockRegex = new(
        @"\[CMD:\s*(.*?)\]",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public record ParsedCommand(
        string Action,      // MOVE, ADD, REMOVE, SWAP, DOOR
        string? RoomId,     // target room
        string? RoomIdB,    // second room (SWAP, DOOR)
        string? TypeName,   // room type (ADD)
        float? X, float? Y, // position
        float? W, float? H, // size (ADD)
        float? NewX, float? NewY, // new position (MOVE)
        float? DoorX, float? DoorY); // door position (DOOR)

    /// <summary>
    /// Extracts the [CMD: ...] block and returns (commentary, commands).
    /// If no [CMD:] block is found, returns the full text as commentary and empty commands.
    /// </summary>
    public static (string commentary, List<ParsedCommand> commands) Parse(string rawOutput)
    {
        var text = (rawOutput ?? "").Trim();
        var commands = new List<ParsedCommand>();
        var commentary = text;

        var match = CmdBlockRegex.Match(text);
        if (!match.Success)
            return (commentary, commands);

        // Remove the CMD block from commentary
        commentary = text[..match.Index].Trim();

        // Parse each command separated by ;
        var cmdText = match.Groups[1].Value.Trim();
        foreach (var part in cmdText.Split(';'))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            var action = tokens[0].ToUpperInvariant();
            try
            {
                var cmd = action switch
                {
                    "MOVE" when tokens.Length >= 4 => new ParsedCommand(
                        "move_target", tokens[1], null, null,
                        null, float.Parse(tokens[2]), null, null,
                        float.Parse(tokens[2]), float.Parse(tokens[3]), null, null),

                    "ADD" when tokens.Length >= 7 => new ParsedCommand(
                        "add_room", tokens[1], null, tokens[2],
                        float.Parse(tokens[3]), float.Parse(tokens[4]),
                        float.Parse(tokens[5]), float.Parse(tokens[6]),
                        null, null, null, null),

                    "REMOVE" when tokens.Length >= 2 => new ParsedCommand(
                        "remove_room", tokens[1], null, null,
                        null, null, null, null, null, null, null, null),

                    "SWAP" when tokens.Length >= 3 => new ParsedCommand(
                        "swap", tokens[1], tokens[2], null,
                        null, null, null, null, null, null, null, null),

                    "DOOR" when tokens.Length >= 5 => new ParsedCommand(
                        "add_door", tokens[1], tokens[2], null,
                        null, null, null, null, null, null,
                        float.Parse(tokens[3]), float.Parse(tokens[4])),

                    _ => null
                };
                if (cmd != null)
                    commands.Add(cmd);
            }
            catch (FormatException)
            {
                // skip malformed command
            }
        }

        return (commentary, commands);
    }
}