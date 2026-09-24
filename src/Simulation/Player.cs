namespace NpcFarm.Simulation;

public sealed class PlayerChatMessage {
    public required string Speaker { get; init; }
    public required string Text { get; init; }
    public float GameHour { get; init; }
}

public sealed class PlayerActionOption {
    public required string Id { get; init; }
    public required string Label { get; init; }
}

/// <summary>The human overseer: pays wages, owns starting goods, talks via the town computer.</summary>
public sealed class Player {
    public const string Id = "player";
    public string Name { get; set; } = "Overseer";

    public int Gold { get; set; } = 100;
    public Inventory Inventory { get; } = new();

    public List<PlayerChatMessage> Chat { get; } = new();
    public bool TerminalOpen { get; set; }
    public string? ActiveNpcId { get; set; }
    public string? Notification { get; set; }
    public float NotificationSeconds { get; set; }

    /// <summary>NPC the player wants to speak with; messengers may relay this.</summary>
    public string? SpeakRequestTargetId { get; set; }

    public HashSet<string> SpeakRequestSeenBy { get; } = new(StringComparer.Ordinal);

    public void SeedStarterKit(Random rng) {
        Gold = 100;
        Inventory.Items.Clear();
        var apples = ItemFactory.Normalize("apple", JobKind.Farmer, rng, forceLivelihood: false);
        apples.Quantity = 100;
        apples.UnitValue = 1;
        if (!apples.Tags.Contains("food"))
            apples.Tags.Add("food");
        Inventory.TryAdd(apples);
    }

    public void Notify(string text, float seconds = 8f) {
        Notification = text;
        NotificationSeconds = seconds;
    }

    public void TickNotification(float realSeconds) {
        if (NotificationSeconds <= 0) return;
        NotificationSeconds -= realSeconds;
        if (NotificationSeconds <= 0)
            Notification = null;
    }

    public void OpenTerminal(Npc npc, float gameHour) {
        TerminalOpen = true;
        ActiveNpcId = npc.Id;
        Chat.Clear();
        Chat.Add(new PlayerChatMessage {
            Speaker = "system",
            Text = $"{npc.Name} connected on the town computer.",
            GameHour = gameHour
        });
    }

    public void CloseTerminal() {
        TerminalOpen = false;
        ActiveNpcId = null;
    }

    public void AddChat(string speaker, string text, float gameHour) {
        Chat.Add(new PlayerChatMessage { Speaker = speaker, Text = text, GameHour = gameHour });
        if (Chat.Count > 40)
            Chat.RemoveAt(0);
    }
}

public enum PlayerContactReason {
    JustTalk,
    ReportWork,
    NeedHelp,
    Summoned
}

public sealed class InterruptedPlan {
    public required NpcAction Action { get; init; }
    public string? TargetNpcId { get; init; }
    public TilePos? TargetTile { get; init; }
    public string? ItemId { get; init; }
    public int GoldAmount { get; init; }
}
