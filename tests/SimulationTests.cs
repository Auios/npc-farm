using NpcFarm.Simulation;

namespace NpcFarm.Tests;

public class GameClockTests {
    [Fact]
    public void Advance_RespectsPauseAndSpeed() {
        var clock = new GameClock { RealSecondsPerGameHour = 60f };
        float startHour = clock.HourOfDay;
        clock.IsPaused = true;
        Assert.Equal(0f, clock.Advance(10f));

        clock.IsPaused = false;
        clock.SetSpeed(1f);
        float hours = clock.Advance(60f);
        Assert.Equal(1f, hours, 3);
        Assert.Equal(startHour + 1f, clock.HourOfDay, 3);
    }

    [Fact]
    public void WorkAndMarketHours() {
        var clock = new GameClock { RealSecondsPerGameHour = 1f };
        // Starts at 08:00
        Assert.True(clock.IsWorkHours);
        Assert.True(clock.IsMarketOpen);
        Assert.False(clock.IsNight);

        clock.SetSpeed(1f);
        clock.Advance(13f); // -> 21:00
        Assert.True(clock.IsNight);
        Assert.False(clock.IsWorkHours);
        Assert.False(clock.IsMarketOpen);
    }
}

public class NeedsTests {
    [Fact]
    public void Tick_IncreasesHungerAndDrainsEnergy() {
        var needs = new Needs { Hunger = 0.2f, Energy = 0.8f, Social = 0.7f };
        needs.Tick(2f, resting: false, socializing: false);
        Assert.True(needs.Hunger > 0.2f);
        Assert.True(needs.Energy < 0.8f);
        Assert.True(needs.Social < 0.7f);
    }

    [Fact]
    public void Tick_RestingRestoresEnergy() {
        var needs = new Needs { Energy = 0.2f };
        needs.Tick(1f, resting: true, socializing: false);
        Assert.True(needs.Energy > 0.2f);
    }
}

public class PathfindingTests {
    [Fact]
    public void FindsPathAroundBlockedTiles() {
        var map = new TownMap();
        var start = map.ClampWalkable(new TilePos(5, 8));
        var goal = map.ClampWalkable(new TilePos(30, 20));
        var path = Pathfinding.FindPath(map, start, goal);
        Assert.True(path.Count >= 2);
        Assert.Equal(start, path[0]);
        Assert.Equal(goal, path[^1]);
        foreach (var step in path)
            Assert.True(map.IsWalkable(step));
    }

    [Fact]
    public void AvoidsOccupiedTilesWhenRouting() {
        var map = new TownMap();
        var start = map.ClampWalkable(new TilePos(10, 12));
        var goal = map.ClampWalkable(new TilePos(14, 12));
        var blocked = new HashSet<TilePos>
        {
            new(11, 12),
            new(12, 12),
            new(13, 12)
        };
        var path = Pathfinding.FindPath(map, start, goal, blocked);
        Assert.Equal(start, path[0]);
        Assert.Equal(goal, path[^1]);
        foreach (var step in path.Skip(1).SkipLast(1))
            Assert.DoesNotContain(step, blocked);
    }
}

public class ActionFilterTests {
    [Fact]
    public void AlwaysIncludesSafeIdles() {
        var world = new World(seed: 1);
        var npc = world.Npcs[0];
        npc.Needs.Hunger = 0.1f;
        npc.Needs.Energy = 0.9f;
        npc.Needs.Social = 0.9f;
        world.Clock.SetSpeed(1f);
        world.Clock.RealSecondsPerGameHour = 1f;
        while (world.Clock.HourOfDay < 10f)
            world.Clock.Advance(0.5f);

        var options = ActionFilter.BuildValidActions(world, npc);
        Assert.Contains(options, o => o.Id == "go_home");
        Assert.Contains(options, o => o.Id == "rest");
        Assert.Contains(options, o => o.Id == "work");
        Assert.True(options.Count <= 8);
    }

    [Fact]
    public void HungryNpcGetsEatOptions() {
        var world = new World(seed: 2);
        var npc = world.Npcs[0];
        npc.Needs.Hunger = 0.9f;
        npc.Gold = 20;
        world.Clock.RealSecondsPerGameHour = 1f;
        world.Clock.SetSpeed(1f);
        while (world.Clock.HourOfDay < 10f)
            world.Clock.Advance(0.5f);

        var options = ActionFilter.BuildValidActions(world, npc);
        Assert.Contains(options, o => o.Id is "eat" or "buy_food");
    }

    [Fact]
    public void DoesNotOfferImpossibleActions() {
        var world = new World(seed: 3);
        var npc = world.Npcs[^1];
        npc.Gold = 0;
        npc.Needs.Hunger = 0.9f;
        npc.Needs.Social = 0.9f;
        npc.Tile = world.Map.ClampWalkable(new TilePos(2, 2));
        npc.DrawX = npc.Tile.X;
        npc.DrawY = npc.Tile.Y;
        world.Clock.RealSecondsPerGameHour = 1f;
        world.Clock.SetSpeed(1f);
        while (world.Clock.HourOfDay < 22f)
            world.Clock.Advance(0.5f);

        var options = ActionFilter.BuildValidActions(world, npc);
        Assert.DoesNotContain(options, o => o.Id == "work");
        Assert.DoesNotContain(options, o => o.Id == "buy_food");
        Assert.DoesNotContain(options, o => o.Id == "tavern");
        Assert.Contains(options, o => o.Id == "sleep");
    }
}

public class WorldTests {
    [Fact]
    public void CreatesSixteenPersistentNpcs() {
        var world = new World(seed: 7);
        Assert.Equal(16, world.Npcs.Count);
        Assert.All(world.Npcs, n => Assert.False(string.IsNullOrWhiteSpace(n.Name)));
        Assert.All(world.Npcs, n => Assert.True(world.Map.Homes.ContainsKey(n.HomeId)));
    }

    [Fact]
    public void NpcsStartOnUniqueTiles() {
        var world = new World(seed: 11);
        Assert.Equal(world.Npcs.Count, world.Npcs.Select(n => n.Tile).Distinct().Count());
    }

    [Fact]
    public void SeparateOverlappingNpcsOnTick() {
        var world = new World(seed: 13) { OfflineFallback = true };
        var a = world.Npcs[0];
        var b = world.Npcs[1];
        var shared = world.Map.ClampWalkable(new TilePos(18, 14));
        a.Tile = shared;
        a.DrawX = shared.X;
        a.DrawY = shared.Y;
        b.Tile = shared;
        b.DrawX = shared.X;
        b.DrawY = shared.Y;

        world.Clock.SetSpeed(1f);
        world.Tick(0.1f);

        Assert.False(a.Tile.Equals(b.Tile));
    }

    [Fact]
    public void FindStandableNearSkipsOccupied() {
        var world = new World(seed: 15);
        var origin = world.Npcs[0].Tile;
        var occupied = new HashSet<TilePos> { origin };
        var free = world.FindStandableNear(origin, excludeId: null, occupied);
        Assert.DoesNotContain(free, occupied);
        Assert.True(world.Map.IsWalkable(free));
    }

    [Fact]
    public void ConversationShowsLastTwentyWithScroll() {
        var conv = new Conversation { Id = "c1", LastActivityHour = 0 };
        for (int i = 0; i < 25; i++) {
            conv.AddMessage(new ChatMessage {
                SpeakerId = "npc_0",
                SpeakerName = "Mara",
                Text = $"line {i}",
                GameHour = i
            });
        }

        Assert.Equal(0, conv.ScrollFromBottom);
        var visible = conv.VisibleMessages().Select(m => m.Text).ToList();
        Assert.Equal(20, visible.Count);
        Assert.Equal("line 5", visible[0]);
        Assert.Equal("line 24", visible[^1]);

        conv.ScrollFromBottom = 5;
        visible = conv.VisibleMessages().Select(m => m.Text).ToList();
        Assert.Equal("line 0", visible[0]);
        Assert.Equal("line 19", visible[^1]);
        Assert.Equal(5, conv.MaxScroll());
    }

    [Fact]
    public void OfflineTickProducesDecisionsAndMovement() {
        var world = new World(seed: 9) { OfflineFallback = true };
        world.Clock.SetSpeed(8f);
        world.Clock.RealSecondsPerGameHour = 60f;

        foreach (var npc in world.Npcs)
            world.RequestDecision(npc);

        // Allow AI worker tasks to complete offline heuristics.
        Thread.Sleep(200);
        for (int i = 0; i < 40; i++) {
            world.Tick(0.5f);
            Thread.Sleep(20);
        }

        Assert.Contains(world.Npcs, n => n.LastDecision is not null);
        Assert.True(world.Events.Count > 0);
        Assert.Equal(world.Npcs.Count, world.Npcs.Select(n => n.Tile).Distinct().Count());
    }

    [Fact]
    public void LeavingRemovesParticipantAndDeletesEmptyRoom() {
        var world = new World(seed: 3);
        var a = world.Npcs[0];
        var b = world.Npcs[1];
        var conv = world.JoinConversation(a, b);
        world.LeaveConversation(a);
        Assert.DoesNotContain(a.Id, conv.ParticipantIds);
        Assert.Contains(conv, world.Conversations);
        Assert.Contains(conv.Messages, m => m.Text.Contains("left"));
        world.LeaveConversation(b);
        Assert.DoesNotContain(world.Conversations, c => c.Id == conv.Id);
        Assert.Contains(a.Memories.Items, m => m.Contains("Left"));
    }

    [Fact]
    public void InventoryStacksAndClampsSlots() {
        var npc = new Npc {
            Id = "t",
            Name = "Test",
            Job = JobKind.Farmer,
            HomeId = "h",
            Personality = Personality.Random(new Random(1))
        };
        var rng = new Random(2);
        var first = ItemFactory.Normalize("wheat", JobKind.Farmer, rng);
        var second = ItemFactory.Normalize("wheat", JobKind.Farmer, rng);
        Assert.True(npc.Inventory.TryAdd(first));
        Assert.True(npc.Inventory.TryAdd(second));
        Assert.Single(npc.Inventory.Items);
        Assert.Equal(2, npc.Inventory.Items[0].Quantity);

        for (int i = 0; i < Inventory.MaxSlots + 2; i++) {
            var extra = ItemFactory.Normalize($"relic {i}", JobKind.Merchant, rng);
            npc.Inventory.TryAdd(extra);
        }
        Assert.Equal(Inventory.MaxSlots, npc.Inventory.Items.Count);
        Assert.True(ItemFactory.Normalize("scythe", JobKind.Farmer, rng).IsLivelihoodAsset);
    }

    [Fact]
    public void PlayerPaysFlexibleAmountAndNpcReportsWork() {
        var world = new World(seed: 11) { OfflineFallback = true };
        Assert.Equal(100, world.Player.Gold);
        Assert.Equal(100, world.Player.Inventory.FindByName("Apple")!.Quantity);

        var npc = world.Npcs[0];
        int beforeGold = npc.Gold;
        npc.AddReport("Worked as farmer and produced wheat.");
        Assert.Contains(npc.RecentReports, r => r.Contains("wheat") || r.Contains("Worked"));

        Assert.True(world.TryPayAmount(npc, 7));
        Assert.Equal(beforeGold + 7, npc.Gold);
        Assert.Equal(93, world.Player.Gold);

        world.Player.OpenTerminal(npc, 10f);
        world.SendPlayerChat("Hold off on more wheat for now.");
        Assert.Contains(world.Player.Chat, m => m.Speaker == world.Player.Name && m.Text.Contains("wheat"));
    }

    [Fact]
    public void SpeakRequestCanBeRelayedAndInterruptedPlanResumed() {
        var world = new World(seed: 5) { OfflineFallback = true };
        var messenger = world.Npcs[0];
        var target = world.Npcs[1];
        var computer = world.Map.Buildings.First(b => b.Kind == BuildingKind.Computer);
        var door = world.Map.ClampWalkable(computer.Door);
        messenger.Tile = door;
        messenger.DrawX = door.X;
        messenger.DrawY = door.Y;
        messenger.CurrentAction = NpcAction.Work;
        messenger.ActionRemainingHours = 1f;

        world.ApplyPlayerAction($"speak_{target.Id}");
        Assert.Equal(target.Id, world.Player.SpeakRequestTargetId);

        // Force notice while messenger stands at the computer.
        for (int i = 0; i < 5; i++)
            world.Tick(0.05f);

        Assert.True(messenger.CurrentAction == NpcAction.InformNpc
                    || world.Player.SpeakRequestSeenBy.Contains(messenger.Id)
                    || target.CurrentAction == NpcAction.ContactPlayer
                    || target.Interrupted is not null
                    || messenger.Interrupted is not null);
    }

    [Fact]
    public void GiftAndTradeRespectLivelihoodAndConsent() {
        var giver = Person("a", empathy: 0.9f, greed: 0.1f, honesty: 0.8f);
        var receiver = Person("b", empathy: 0.2f, greed: 0.2f, honesty: 0.5f);
        var tool = ItemFactory.Normalize("scythe", JobKind.Farmer, new Random(1), forceLivelihood: true);
        giver.Inventory.TryAdd(tool);
        var refused = TradeResolver.ResolveGift(giver, receiver, tool, 0f);
        Assert.False(refused.Success);
        Assert.NotNull(giver.Inventory.FindByName("Scythe"));

        var bread = ItemFactory.Normalize("bread loaf", JobKind.Baker, new Random(1));
        giver.Inventory.TryAdd(bread);
        var given = TradeResolver.ResolveGift(giver, receiver, bread, 0f);
        Assert.True(given.Success);
        Assert.NotNull(receiver.Inventory.FindByName("Bread Loaf"));

        var owner = Person("c", empathy: 0.1f, greed: 0.95f, honesty: 0.9f);
        owner.Personality.Courage = 0.9f;
        var asker = Person("d", empathy: 0.2f, greed: 0.2f, honesty: 0.2f);
        var mill = ItemFactory.Normalize("millstone", JobKind.Miller, new Random(1), forceLivelihood: true);
        owner.Inventory.TryAdd(mill);
        var blocked = TradeResolver.ResolveRequest(asker, owner, mill, 0f);
        Assert.False(blocked.Success);

        var flour = ItemFactory.Normalize("flour sack", JobKind.Miller, new Random(1));
        owner.Inventory.TryAdd(flour);
        owner.Affinity[asker.Id] = 0.8f;
        owner.Personality.Empathy = 0.95f;
        owner.Personality.Greed = 0.05f;
        var ok = TradeResolver.ResolveRequest(asker, owner, flour, 0f);
        Assert.True(ok.Success);
    }

    [Fact]
    public void TheftMovesItemAndHurtsAffinity() {
        var thief = Person("thief", empathy: 0.1f, greed: 0.9f, honesty: 0.05f);
        thief.Personality.Aggression = 1f;
        thief.Personality.Courage = 1f;
        var victim = Person("victim", empathy: 0.4f, greed: 0.2f, honesty: 0.8f);
        victim.Personality.Courage = 0f;
        victim.Personality.Loyalty = 0f;
        var herbs = ItemFactory.Normalize("dried herbs", JobKind.Herbalist, new Random(1));
        victim.Inventory.TryAdd(herbs);
        float before = victim.GetAffinity(thief.Id);
        var stolen = TradeResolver.ResolveTheft(thief, victim, herbs, 0f);
        victim.AdjustAffinity(thief.Id, stolen.TargetAffinityDelta);
        thief.AdjustAffinity(victim.Id, stolen.ActorAffinityDelta);
        Assert.True(stolen.Success);
        Assert.Null(victim.Inventory.FindByName("Dried Herbs"));
        Assert.NotNull(thief.Inventory.FindByName("Dried Herbs"));
        Assert.True(victim.GetAffinity(thief.Id) < before - 0.2f);

        var again = ItemFactory.Normalize("spice pouch", JobKind.Merchant, new Random(1));
        victim.Inventory.TryAdd(again);
        float mid = victim.GetAffinity(thief.Id);
        var failed = TradeResolver.ResolveTheft(thief, victim, again, 0.99f);
        victim.AdjustAffinity(thief.Id, failed.TargetAffinityDelta);
        Assert.False(failed.Success);
        Assert.NotNull(victim.Inventory.FindByName("Spice Pouch"));
        Assert.True(victim.GetAffinity(thief.Id) < mid);
    }

    [Fact]
    public void OfflineDecidePrefersTheftWhenCunningAndLeaveWhenSatisfied() {
        var world = new World(seed: 4) { OfflineFallback = true };
        var npc = world.Npcs[0];
        npc.Personality.Honesty = 0.05f;
        npc.Personality.Aggression = 1f;
        npc.Personality.Greed = 0.2f;
        npc.Needs.Social = 0.2f;
        npc.Gold = 40;
        var take = world.ChooseOffline(npc, [
            new ActionOption { Action = NpcAction.TakeWithoutConsent, Id = "take_without_consent", Description = "steal" },
            new ActionOption { Action = NpcAction.LeaveConversation, Id = "leave", Description = "leave" },
            new ActionOption { Action = NpcAction.Talk, Id = "continue_talk", Description = "talk" }
        ]);
        Assert.Equal("take_without_consent", take.Choice);

        npc.Needs.Social = 0.9f;
        npc.Personality.Honesty = 0.9f;
        npc.Personality.Aggression = 0.1f;
        var leave = world.ChooseOffline(npc, [
            new ActionOption { Action = NpcAction.LeaveConversation, Id = "leave", Description = "leave" },
            new ActionOption { Action = NpcAction.Talk, Id = "continue_talk", Description = "talk" }
        ]);
        Assert.Equal("leave", leave.Choice);
    }

    private static Npc Person(string id, float empathy, float greed, float honesty) => new() {
        Id = id,
        Name = id,
        Job = JobKind.Farmer,
        HomeId = "home",
        Personality = new Personality {
            Empathy = empathy,
            Greed = greed,
            Honesty = honesty,
            Courage = 0.5f,
            Aggression = 0.2f,
            Loyalty = 0.5f,
            Curiosity = 0.5f,
            Sociability = 0.5f
        }
    };
}
