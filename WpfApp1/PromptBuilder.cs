using System;
using System.Collections.Generic;
using System.Text;

namespace WpfApp1;

/// <summary>
/// Builds prompts for the LLM planner from the parsed world state.
/// Ported from ai_orchestrator/prompt_builder.py.
/// </summary>
public static class PromptBuilder
{
    private static readonly Dictionary<byte, string> RoomTypeNames = new()
    {
        [0] = "Undefined",
        [1] = "Living_Room",
        [2] = "Kitchen",
        [3] = "Bathroom",
        [4] = "Corridor",
    };

    public static readonly string SystemPrompt =
        "You are an apartment layout planner. You analyze room conflicts and " +
        "SNiP-style spacing violations and propose minimal fix commands. " +
        "You may only use the commands 'MOVE', 'SWAP', 'ADD', 'REMOVE' or 'DOOR'. " +
        "Return at most 3 commands per response.\n" +
        "\n" +
        "Response format: write a human-readable explanation in Russian, " +
        "then append a single [CMD: ...] block with commands separated by ;\n" +
        "\n" +
        "Example:\n" +
        "Я увеличил кухню до 15 м² и сдвинул гостиную вправо на 2 метра.\n" +
        "[CMD: MOVE kitchen 5.0 0.0; MOVE living_room 7.0 0.0]\n" +
        "\n" +
        "Command syntax:\n" +
        "  MOVE room_id new_x new_y\n" +
        "  ADD room_id type_name x y width height\n" +
        "  REMOVE room_id\n" +
        "  SWAP room_id_a room_id_b\n" +
        "  DOOR room_a room_b x y\n" +
        "\n" +
        "Type names: Undefined, Living_Room, Kitchen, Bathroom, Corridor\n" +
        "Coordinates are in meters. The [CMD:] block must be the last line.";

    /// <summary>
    /// Build the user prompt from the grid world state string.
    /// </summary>
    public static string BuildUserPrompt(
        string stateString,
        float minX, float minY, float maxX, float maxY,
        string snipContext = "",
        string userMessage = "")
    {
        // Parse the state string manually (simple format from C++ engine)
        var state = ParseState(stateString);
        var sb = new StringBuilder();

        sb.AppendLine("Current apartment layout:");
        sb.AppendLine("========================================");
        foreach (var room in state.Rooms)
        {
            var typeName = RoomTypeNames.GetValueOrDefault(room.Type, $"Type{room.Type}");
            sb.AppendLine($"- Room {room.Id}: {typeName}, position ({room.X:F1}, {room.Y:F1}), size {room.W:F1} x {room.H:F1}");
        }

        sb.AppendLine();
        sb.AppendLine($"Apartment bounds: x in [{minX:F1}, {maxX:F1}], y in [{minY:F1}, {maxY:F1}]");

        if (state.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Detected conflicts (overlapping rooms):");
            sb.AppendLine("----------------------------------------");
            foreach (var c in state.Conflicts)
                sb.AppendLine($"- Conflict between {c.IdA} and {c.IdB}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No conflicts detected.");
        }

        if (state.Doors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Doors (room connectivity):");
            sb.AppendLine("----------------------------------------");
            foreach (var d in state.Doors)
                sb.AppendLine($"- Door {d.Id}: connects {d.RoomA} and {d.RoomB}, at ({d.X:F1}, {d.Y:F1})");
        }

        if (!string.IsNullOrWhiteSpace(snipContext))
        {
            sb.AppendLine();
            sb.AppendLine("Relevant SNiP building norms (Техэксперт context to follow):");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine(snipContext.Trim());
        }

        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            sb.AppendLine();
            sb.AppendLine("User request:");
            sb.AppendLine("----------------------------------------");
            sb.AppendLine(userMessage.Trim());
        }

        sb.AppendLine();
        sb.AppendLine(
            "Write a short explanation in Russian, then append [CMD: ...] with the commands. " +
            "Example: [CMD: MOVE kitchen 5.0 0.0; ADD bathroom Bathroom 15.0 0.0 3.0 3.0]");

        return sb.ToString();
    }

    // -------------------------------------------------------------------
    // Simple state parser (ported from state_parser.py)
    // -------------------------------------------------------------------

    private record ParsedRoom(string Id, byte Type, float X, float Y, float W, float H);
    private record ParsedConflict(string IdA, string IdB);
    private record ParsedDoor(string Id, string RoomA, string RoomB, float X, float Y);

    private class ParsedState
    {
        public List<ParsedRoom> Rooms { get; } = new();
        public List<ParsedConflict> Conflicts { get; } = new();
        public List<ParsedDoor> Doors { get; } = new();
    }

    private static ParsedState ParseState(string stateString)
    {
        var state = new ParsedState();
        if (string.IsNullOrWhiteSpace(stateString)) return state;

        var parts = stateString.Split('|');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            switch (tokens[0])
            {
                case "R" when tokens.Length >= 7: // Room
                    state.Rooms.Add(new ParsedRoom(
                        tokens[1],
                        byte.TryParse(tokens[2], out var rt) ? rt : (byte)0,
                        float.TryParse(tokens[3], out var rx) ? rx : 0,
                        float.TryParse(tokens[4], out var ry) ? ry : 0,
                        float.TryParse(tokens[5], out var rw) ? rw : 0,
                        float.TryParse(tokens[6], out var rh) ? rh : 0));
                    break;

                case "C" when tokens.Length >= 3: // Conflict
                    state.Conflicts.Add(new ParsedConflict(tokens[1], tokens[2]));
                    break;

                case "D" when tokens.Length >= 6: // Door
                    state.Doors.Add(new ParsedDoor(
                        tokens[1], tokens[2], tokens[3],
                        float.TryParse(tokens[4], out var dx) ? dx : 0,
                        float.TryParse(tokens[5], out var dy) ? dy : 0));
                    break;
            }
        }

        return state;
    }
}