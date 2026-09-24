namespace NpcFarm.Simulation;

public sealed class InventoryItem {
    public required string Id { get; init; }
    public required string Name { get; set; }
    public int Quantity { get; set; }
    public int UnitValue { get; set; }
    public bool IsLivelihoodAsset { get; set; }
    public List<string> Tags { get; } = new();

    public string Label => Quantity > 1 ? $"{Name} x{Quantity}" : Name;
}

public sealed class Inventory {
    public const int MaxSlots = 12;
    public const int MaxStack = 100;

    public List<InventoryItem> Items { get; } = new();

    public InventoryItem? FindById(string id) =>
        Items.FirstOrDefault(i => i.Id == id);

    public InventoryItem? FindByName(string name) =>
        Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<InventoryItem> Giftable =>
        Items.Where(i => i.Quantity > 0 && !i.IsLivelihoodAsset);

    public IEnumerable<InventoryItem> Stealable =>
        Items.Where(i => i.Quantity > 0);

    /// <summary>Adds quantity, stacking by name. Returns false if a new slot cannot fit.</summary>
    public bool TryAdd(InventoryItem incoming) {
        if (incoming.Quantity <= 0) return false;
        var existing = FindByName(incoming.Name);
        if (existing is not null) {
            int room = MaxStack - existing.Quantity;
            if (room <= 0) return false;
            int moved = Math.Min(room, incoming.Quantity);
            existing.Quantity += moved;
            if (incoming.IsLivelihoodAsset)
                existing.IsLivelihoodAsset = true;
            foreach (string tag in incoming.Tags)
                if (!existing.Tags.Contains(tag))
                    existing.Tags.Add(tag);
            return true;
        }

        if (Items.Count >= MaxSlots) return false;
        incoming.Quantity = Math.Min(incoming.Quantity, MaxStack);
        Items.Add(incoming);
        return true;
    }

    public bool TryRemove(string itemId, int quantity) {
        var item = FindById(itemId);
        if (item is null || quantity <= 0 || item.Quantity < quantity) return false;
        item.Quantity -= quantity;
        if (item.Quantity <= 0)
            Items.Remove(item);
        return true;
    }

    public InventoryItem? TakeOne(string itemId) {
        var item = FindById(itemId);
        if (item is null || item.Quantity <= 0) return null;
        var copy = new InventoryItem {
            Id = item.Id + "#1",
            Name = item.Name,
            Quantity = 1,
            UnitValue = item.UnitValue,
            IsLivelihoodAsset = item.IsLivelihoodAsset
        };
        copy.Tags.AddRange(item.Tags);
        TryRemove(itemId, 1);
        return copy;
    }

    public string Summary(int max = 4) {
        if (Items.Count == 0) return "empty";
        return string.Join(", ", Items.Take(max).Select(i => i.IsLivelihoodAsset ? $"{i.Label}*" : i.Label));
    }
}

public static class ItemFactory {
    private static readonly Dictionary<JobKind, string[]> LivelihoodNames = new() {
        [JobKind.Farmer] = ["seed sack", "scythe", "plow"],
        [JobKind.Baker] = ["oven peel", "sourdough starter"],
        [JobKind.Miller] = ["millstone", "grindstone"],
        [JobKind.Merchant] = ["ledger", "coin scale"],
        [JobKind.Innkeeper] = ["stew kettle", "ale cask"],
        [JobKind.Blacksmith] = ["smithing hammer", "tongs"],
        [JobKind.Guard] = ["watch spear", "town badge"],
        [JobKind.Herbalist] = ["mortar", "drying rack"]
    };

    private static readonly Dictionary<JobKind, string[]> ProducedNames = new() {
        [JobKind.Farmer] = ["wheat", "barley", "carrots"],
        [JobKind.Baker] = ["bread loaf", "rye rolls"],
        [JobKind.Miller] = ["flour sack", "ground meal"],
        [JobKind.Merchant] = ["cloth bolt", "spice pouch"],
        [JobKind.Innkeeper] = ["bowl of stew", "mug of ale"],
        [JobKind.Blacksmith] = ["iron nails", "horseshoe"],
        [JobKind.Guard] = ["whetstone"],
        [JobKind.Herbalist] = ["dried herbs", "healing salve"]
    };

    private static readonly string[] FoodWords = ["bread", "loaf", "roll", "wheat", "barley", "carrot", "stew", "ale", "herb", "meal", "flour", "spice"];

    public static void Seed(Npc npc, Random rng) {
        var toolName = Pick(LivelihoodNames[npc.Job], rng);
        npc.Inventory.TryAdd(Normalize(toolName, npc.Job, rng, forceLivelihood: true));
        int extras = rng.Next(1, 3);
        for (int i = 0; i < extras; i++) {
            var produced = ProduceFor(npc.Job, rng);
            npc.Inventory.TryAdd(produced);
        }
    }

    public static InventoryItem ProduceFor(JobKind job, Random rng) {
        string name = Pick(ProducedNames[job], rng);
        return Normalize(name, job, rng, forceLivelihood: false);
    }

    public static InventoryItem Meal(Random rng) =>
        Normalize("market meal", JobKind.Innkeeper, rng, forceLivelihood: false);

    public static InventoryItem Normalize(string name, JobKind job, Random rng, bool forceLivelihood = false) {
        string clean = CanonicalName(name);
        bool livelihood = forceLivelihood || IsLivelihoodName(clean, job);
        int value = livelihood
            ? 12 + Math.Abs(clean.GetHashCode()) % 9
            : 1 + Math.Abs(clean.GetHashCode()) % 8;
        // Slight jitter so identical names stay stable but different goods differ.
        if (!livelihood)
            value = Math.Clamp(value + rng.Next(0, 2), 1, 10);

        var item = new InventoryItem {
            Id = Slug(clean) + "_" + Math.Abs(clean.GetHashCode() % 10000),
            Name = clean,
            Quantity = 1,
            UnitValue = value,
            IsLivelihoodAsset = livelihood
        };
        if (FoodWords.Any(w => clean.Contains(w, StringComparison.OrdinalIgnoreCase)))
            item.Tags.Add("food");
        if (livelihood)
            item.Tags.Add("livelihood");
        return item;
    }

    public static string CanonicalName(string name) {
        if (string.IsNullOrWhiteSpace(name)) return "goods";
        var parts = name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string joined = string.Join(' ', parts.Take(6).Select(TitleWord));
        if (joined.Length > 40)
            joined = joined[..40].Trim();
        return joined.Length == 0 ? "goods" : joined;
    }

    private static bool IsLivelihoodName(string name, JobKind job) {
        foreach (string known in LivelihoodNames[job]) {
            if (name.Contains(known, StringComparison.OrdinalIgnoreCase) ||
                known.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string TitleWord(string word) {
        if (word.Length == 0) return word;
        if (word.Length == 1) return word.ToUpperInvariant();
        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }

    private static string Slug(string name) {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars).Trim('_');
    }

    private static string Pick(string[] options, Random rng) => options[rng.Next(options.Length)];
}
