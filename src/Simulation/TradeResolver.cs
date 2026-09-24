namespace NpcFarm.Simulation;

public enum ExchangeKind {
    Gift,
    Request,
    Trade,
    Theft
}

public sealed class ExchangeOutcome {
    public required ExchangeKind Kind { get; init; }
    public required bool Success { get; init; }
    /// <summary>Fact for the dialogue model: what already happened.</summary>
    public required string OutcomeFact { get; init; }
    public required string ActorMemory { get; init; }
    public required string TargetMemory { get; init; }
    public float ActorAffinityDelta { get; init; }
    public float TargetAffinityDelta { get; init; }
    public string? ItemName { get; init; }
    public int GoldMoved { get; init; }
}

/// <summary>
/// Engine-owned consent, prices, and transfers. Dialogue only narrates the result.
/// <paramref name="roll"/> is in [0,1); lower rolls succeed more often.
/// </summary>
public static class TradeResolver {
    public static ExchangeOutcome ResolveGift(Npc giver, Npc receiver, InventoryItem item, float roll) {
        if (item.IsLivelihoodAsset || !giver.Inventory.TryRemove(item.Id, 1)) {
            return Fail(ExchangeKind.Gift, item.Name,
                $"{giver.Name} refused to give away {item.Name}; it is needed for their work.",
                $"Kept {item.Name}; I depend on it.",
                $"{giver.Name} almost gave me {item.Name}, then thought better of it.");
        }

        var moved = CloneOne(item);
        if (!receiver.Inventory.TryAdd(moved)) {
            giver.Inventory.TryAdd(moved);
            return Fail(ExchangeKind.Gift, item.Name,
                $"{receiver.Name} had no room for {item.Name}.",
                $"{receiver.Name} could not carry {item.Name}.",
                $"Could not take {item.Name} from {giver.Name}; my pack is full.");
        }
        float warmth = 0.08f + giver.Personality.Empathy * 0.1f;
        return new ExchangeOutcome {
            Kind = ExchangeKind.Gift,
            Success = true,
            ItemName = item.Name,
            OutcomeFact = $"{giver.Name} successfully gave {item.Name} to {receiver.Name} as a gift.",
            ActorMemory = $"Gave {item.Name} to {receiver.Name}.",
            TargetMemory = $"{giver.Name} gave me {item.Name}.",
            ActorAffinityDelta = warmth * 0.6f,
            TargetAffinityDelta = warmth
        };
    }

    public static ExchangeOutcome ResolveRequest(Npc requester, Npc owner, InventoryItem item, float roll) {
        if (item.IsLivelihoodAsset) {
            return Fail(ExchangeKind.Request, item.Name,
                $"{owner.Name} refused to hand over {item.Name}; it is a livelihood asset.",
                $"{owner.Name} would not give up {item.Name}.",
                $"Refused to give {requester.Name} my {item.Name}; I need it to work.");
        }

        float affinity = owner.GetAffinity(requester.Id);
        float kindness = owner.Personality.Empathy * 0.45f
                         + (1f - owner.Personality.Greed) * 0.25f
                         + owner.Personality.Honesty * 0.1f
                         + Math.Clamp(affinity, -1f, 1f) * 0.25f;
        // Naive: trusting and not brave enough to refuse a friend.
        if (owner.Personality.Honesty > 0.65f && owner.Personality.Courage < 0.4f && affinity >= 0f)
            kindness += 0.2f;
        if (requester.Needs.Hunger > 0.7f && item.Tags.Contains("food"))
            kindness += 0.15f;

        bool consent = kindness >= 0.55f || (kindness >= 0.35f && roll < kindness);
        if (!consent || !owner.Inventory.TryRemove(item.Id, 1)) {
            return Fail(ExchangeKind.Request, item.Name,
                $"{owner.Name} refused {requester.Name}'s request for {item.Name}.",
                $"{owner.Name} refused to give me {item.Name}.",
                $"Turned down {requester.Name}'s request for {item.Name}.");
        }

        var moved = CloneOne(item);
        if (!requester.Inventory.TryAdd(moved)) {
            owner.Inventory.TryAdd(moved);
            return Fail(ExchangeKind.Request, item.Name,
                $"{requester.Name} had no room for {item.Name}.",
                $"Could not carry {item.Name}.",
                $"{requester.Name} asked for {item.Name} but had no room.");
        }
        return new ExchangeOutcome {
            Kind = ExchangeKind.Request,
            Success = true,
            ItemName = item.Name,
            OutcomeFact = $"{owner.Name} agreed and gave {item.Name} to {requester.Name}.",
            ActorMemory = $"{owner.Name} let me have {item.Name}.",
            TargetMemory = $"Let {requester.Name} have {item.Name}.",
            ActorAffinityDelta = 0.06f,
            TargetAffinityDelta = 0.04f + owner.Personality.Empathy * 0.05f
        };
    }

    public static ExchangeOutcome ResolveTrade(Npc seller, Npc buyer, InventoryItem item, int offerGold, float roll) {
        if (item.IsLivelihoodAsset) {
            return Fail(ExchangeKind.Trade, item.Name,
                $"{seller.Name} refused to sell {item.Name}; their work depends on it.",
                $"Would not sell {item.Name} to {buyer.Name}.",
                $"{seller.Name} refused to trade away {item.Name}.");
        }

        int fair = Math.Max(1, item.UnitValue);
        int asking = Math.Max(1, (int)MathF.Round(fair * (0.8f + seller.Personality.Greed * 0.6f)));
        int price = offerGold > 0 ? offerGold : asking;
        float affinity = seller.GetAffinity(buyer.Id);
        float generosity = seller.Personality.Empathy * 0.2f + Math.Max(0f, affinity) * 0.15f;
        int minAccept = Math.Max(1, (int)MathF.Round(fair * (0.7f - generosity)));
        bool priceOk = price >= minAccept && buyer.Gold >= price;
        bool willing = priceOk && (seller.Personality.Greed < 0.85f || price >= fair);
        if (seller.Personality.Honesty < 0.3f && seller.Personality.Greed > 0.7f && price < fair)
            willing = false;
        if (!willing && priceOk && seller.Personality.Empathy > 0.8f && roll < 0.2f)
            willing = true;

        if (!willing || !seller.Inventory.TryRemove(item.Id, 1)) {
            return Fail(ExchangeKind.Trade, item.Name,
                $"Trade failed: {seller.Name} would not sell {item.Name} for {price} gold.",
                $"{buyer.Name} and I could not agree on a price for {item.Name}.",
                $"{seller.Name} refused my offer of {price} gold for {item.Name}.");
        }

        var moved = CloneOne(item);
        if (!buyer.Inventory.TryAdd(moved)) {
            seller.Inventory.TryAdd(moved);
            return Fail(ExchangeKind.Trade, item.Name,
                $"Trade failed: {buyer.Name} had no room for {item.Name}.",
                $"{buyer.Name} could not carry {item.Name}.",
                $"No room in my pack for {item.Name}.");
        }
        buyer.Gold -= price;
        seller.Gold += price;
        float bump = 0.05f + Math.Clamp(affinity, 0f, 1f) * 0.05f;
        return new ExchangeOutcome {
            Kind = ExchangeKind.Trade,
            Success = true,
            ItemName = item.Name,
            GoldMoved = price,
            OutcomeFact = $"{buyer.Name} bought {item.Name} from {seller.Name} for {price} gold.",
            ActorMemory = $"Sold {item.Name} to {buyer.Name} for {price} gold.",
            TargetMemory = $"Bought {item.Name} from {seller.Name} for {price} gold.",
            ActorAffinityDelta = bump,
            TargetAffinityDelta = bump
        };
    }

    public static ExchangeOutcome ResolveTheft(Npc thief, Npc victim, InventoryItem item, float roll) {
        float affinity = thief.GetAffinity(victim.Id);
        float chance = 0.12f
                       + thief.Personality.Aggression * 0.35f
                       + thief.Personality.Courage * 0.2f
                       + (1f - thief.Personality.Honesty) * 0.15f
                       - victim.Personality.Courage * 0.22f
                       - victim.Personality.Loyalty * 0.12f
                       - Math.Max(0f, affinity) * 0.2f;
        chance = Math.Clamp(chance, 0.05f, 0.85f);
        bool success = roll < chance && victim.Inventory.TryRemove(item.Id, 1);
        if (success) {
            var moved = CloneOne(item);
            if (!thief.Inventory.TryAdd(moved)) {
                victim.Inventory.TryAdd(moved);
                success = false;
            }
        }

        // Taking without consent always damages the relationship, success or not.
        float hit = success ? 0.35f + victim.Personality.Loyalty * 0.15f : 0.12f;
        string fact = success
            ? $"{thief.Name} took {item.Name} from {victim.Name} without consent."
            : $"{thief.Name} tried to take {item.Name} from {victim.Name} and failed.";
        return new ExchangeOutcome {
            Kind = ExchangeKind.Theft,
            Success = success,
            ItemName = item.Name,
            OutcomeFact = fact,
            ActorMemory = success
                ? $"Took {item.Name} from {victim.Name} without asking."
                : $"Failed to take {item.Name} from {victim.Name}.",
            TargetMemory = success
                ? $"{thief.Name} took my {item.Name} without consent."
                : $"{thief.Name} tried to take my {item.Name}.",
            ActorAffinityDelta = -hit * 0.5f,
            TargetAffinityDelta = -hit
        };
    }

    public static int SuggestedPrice(Npc seller, InventoryItem item) =>
        Math.Max(1, (int)MathF.Round(Math.Max(1, item.UnitValue) * (0.8f + seller.Personality.Greed * 0.6f)));

    private static ExchangeOutcome Fail(
        ExchangeKind kind,
        string itemName,
        string fact,
        string actorMemory,
        string targetMemory) => new() {
        Kind = kind,
        Success = false,
        ItemName = itemName,
        OutcomeFact = fact,
        ActorMemory = actorMemory,
        TargetMemory = targetMemory,
        ActorAffinityDelta = kind == ExchangeKind.Request ? -0.02f : 0f,
        TargetAffinityDelta = kind == ExchangeKind.Request ? -0.01f : 0f
    };

    private static InventoryItem CloneOne(InventoryItem item) {
        var copy = new InventoryItem {
            Id = item.Id + "_mv",
            Name = item.Name,
            Quantity = 1,
            UnitValue = item.UnitValue,
            IsLivelihoodAsset = false
        };
        copy.Tags.AddRange(item.Tags);
        return copy;
    }
}
