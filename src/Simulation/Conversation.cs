namespace NpcFarm.Simulation;

public sealed class ChatMessage {
    public required string SpeakerId { get; init; }
    public required string SpeakerName { get; init; }
    public required string Text { get; init; }
    public float GameHour { get; init; }
}

/// <summary>Shared IRC-style channel between NPCs who are talking together.</summary>
public sealed class Conversation {
    public const int VisibleLineCount = 20;

    public required string Id { get; init; }
    public HashSet<string> ParticipantIds { get; } = new(StringComparer.Ordinal);
    public List<ChatMessage> Messages { get; } = new();
    public float LastActivityHour { get; set; }
    public float IdleCloseHours { get; set; } = 1.5f;

    /// <summary>How many visual lines above the newest to scroll. 0 = pinned to bottom.</summary>
    public int ScrollFromBottom { get; set; }

    /// <summary>
    /// Wrapped display-line count from the last render pass. Used for scrollbar range.
    /// Falls back to message count when unset (e.g. unit tests).
    /// </summary>
    public int CachedVisualLineCount { get; set; }

    /// <summary>World-space offset from the participant centroid used to keep panels from overlapping.</summary>
    public float PanelOffsetX { get; set; }
    public float PanelOffsetY { get; set; }

    public bool HadTheft { get; set; }
    public int FriendlyScore { get; set; }
    public List<string> RecentItemNames { get; } = new();

    public bool RemoveParticipant(string npcId) => ParticipantIds.Remove(npcId);

    public bool IsExpired(float currentHour) =>
        currentHour - LastActivityHour >= IdleCloseHours;

    public void AddParticipant(string npcId) => ParticipantIds.Add(npcId);

    public void AddMessage(ChatMessage message) {
        Messages.Add(message);
        LastActivityHour = message.GameHour;
        // Keep the view stuck to the latest line unless the user scrolled up.
        if (ScrollFromBottom == 0)
            return;
        // If they were scrolled, keep their relative place in history.
        ScrollFromBottom = Math.Min(ScrollFromBottom + 1, MaxScroll());
    }

    public int MaxScroll() {
        int total = CachedVisualLineCount > 0 ? CachedVisualLineCount : Messages.Count;
        return Math.Max(0, total - VisibleLineCount);
    }

    public void ClampScroll() =>
        ScrollFromBottom = Math.Clamp(ScrollFromBottom, 0, MaxScroll());

    public IEnumerable<ChatMessage> VisibleMessages() {
        ClampScroll();
        if (Messages.Count == 0)
            yield break;

        int endExclusive = Messages.Count - ScrollFromBottom;
        int start = Math.Max(0, endExclusive - VisibleLineCount);
        for (int i = start; i < endExclusive; i++)
            yield return Messages[i];
    }

    public string Title(IReadOnlyDictionary<string, string> namesById) {
        var names = ParticipantIds
            .Select(id => namesById.TryGetValue(id, out string? n) ? n : id)
            .OrderBy(n => n)
            .ToList();
        return names.Count == 0 ? "conversation" : string.Join(", ", names);
    }
}
