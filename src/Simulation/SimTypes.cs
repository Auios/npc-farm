namespace NpcFarm.Simulation;

public readonly record struct TilePos(int X, int Y) {
    public int Manhattan(TilePos other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y);
    public override string ToString() => $"({X},{Y})";
}

public enum JobKind {
    Farmer,
    Baker,
    Miller,
    Merchant,
    Innkeeper,
    Blacksmith,
    Guard,
    Herbalist
}

public enum BuildingKind {
    Home,
    Farm,
    Bakery,
    Mill,
    Market,
    Tavern,
    Well,
    Square,
    Forge,
    GuardPost,
    HerbShop,
    Computer
}

public enum NpcAction {
    Work,
    Eat,
    BuyFood,
    Sleep,
    Talk,
    Tavern,
    GoHome,
    Rest,
    OfferGift,
    RequestItem,
    ProposeTrade,
    TakeWithoutConsent,
    LeaveConversation,
    ContactPlayer,
    InformNpc,
    ResumeInterrupted
}

public sealed class Personality {
    public float Aggression { get; set; }
    public float Greed { get; set; }
    public float Honesty { get; set; }
    public float Courage { get; set; }
    public float Empathy { get; set; }
    public float Loyalty { get; set; }
    public float Curiosity { get; set; }
    public float Sociability { get; set; }

    public static Personality Random(Random rng) => new() {
        Aggression = Next(rng),
        Greed = Next(rng),
        Honesty = Next(rng),
        Courage = Next(rng),
        Empathy = Next(rng),
        Loyalty = Next(rng),
        Curiosity = Next(rng),
        Sociability = Next(rng)
    };

    private static float Next(Random rng) => Math.Clamp((float)(rng.NextDouble() * 0.8 + 0.1), 0f, 1f);

    public Dictionary<string, float> ToDict() => new() {
        ["aggression"] = Aggression,
        ["greed"] = Greed,
        ["honesty"] = Honesty,
        ["courage"] = Courage,
        ["empathy"] = Empathy,
        ["loyalty"] = Loyalty,
        ["curiosity"] = Curiosity,
        ["sociability"] = Sociability
    };
}

public sealed class Needs {
    /// <summary>0 = full, 1 = starving.</summary>
    public float Hunger { get; set; } = 0.25f;
    /// <summary>0 = exhausted, 1 = rested.</summary>
    public float Energy { get; set; } = 0.8f;
    /// <summary>0 = lonely craving, 1 = socially satisfied.</summary>
    public float Social { get; set; } = 0.6f;

    public float MoneyPressure(int gold) => gold switch {
        <= 5 => 0.9f,
        <= 15 => 0.6f,
        <= 40 => 0.3f,
        _ => 0.1f
    };

    public void Tick(float hours, bool resting, bool socializing) {
        Hunger = Math.Clamp(Hunger + 0.08f * hours, 0f, 1f);
        if (resting)
            Energy = Math.Clamp(Energy + 0.35f * hours, 0f, 1f);
        else
            Energy = Math.Clamp(Energy - 0.06f * hours, 0f, 1f);

        if (socializing)
            Social = Math.Clamp(Social + 0.4f * hours, 0f, 1f);
        else
            Social = Math.Clamp(Social - 0.05f * hours, 0f, 1f);
    }
}

public sealed class MemoryRing {
    private readonly Queue<string> _items = new();
    private readonly int _capacity;

    public MemoryRing(int capacity = 8) => _capacity = capacity;

    public IReadOnlyList<string> Items => _items.ToArray();

    public void Add(string memory) {
        if (string.IsNullOrWhiteSpace(memory)) return;
        _items.Enqueue(memory.Trim());
        while (_items.Count > _capacity)
            _items.Dequeue();
    }
}

public sealed class Building {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required BuildingKind Kind { get; init; }
    public required TilePos Origin { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public TilePos Door => new(Origin.X + Width / 2, Origin.Y + Height);

    public bool Contains(TilePos p) =>
        p.X >= Origin.X && p.X < Origin.X + Width &&
        p.Y >= Origin.Y && p.Y < Origin.Y + Height;
}

public sealed class ActionOption {
    public required NpcAction Action { get; init; }
    public required string Id { get; init; }
    public required string Description { get; init; }
    public string? TargetNpcId { get; init; }
    public string? ItemId { get; init; }
    public int GoldAmount { get; init; }
    public TilePos? TargetTile { get; init; }
}

public sealed class DecisionResult {
    public required string Choice { get; init; }
    public Dictionary<string, float> Probabilities { get; init; } = new();
    public float? Confidence { get; init; }
}

public sealed class WorldEvent {
    public required float GameHour { get; init; }
    public required string Text { get; init; }
}
