using System.Text.Json;
using NpcFarm.Ai;

namespace NpcFarm.Simulation;

public sealed class World {
    public GameClock Clock { get; } = new();
    public TownMap Map { get; } = new();
    public List<Npc> Npcs { get; } = new();
    public List<Conversation> Conversations { get; } = new();
    public List<WorldEvent> Events { get; } = new();
    public Player Player { get; } = new();
    public AiClient? Ai { get; set; }
    public bool OfflineFallback { get; set; }

    private int _nextConversationId;
    private int _lastPaydayDay = 1;

    private readonly Queue<Func<Task>> _aiJobs = new();
    private readonly object _aiLock = new();
    private bool _aiWorkerRunning;
    private readonly Random _rng;

    public string? SelectedNpcId { get; set; }
    public Npc? SelectedNpc => Npcs.FirstOrDefault(n => n.Id == SelectedNpcId);

    private static readonly string[] FirstNames =
    [
        "Mara", "Edwin", "Liora", "Tobin", "Sara", "Jonas", "Nell", "Petyr",
        "Anya", "Garr", "Iris", "Bram", "Cora", "Finn", "Hela", "Rook"
    ];

    private static readonly JobKind[] JobPool =
    [
        JobKind.Farmer, JobKind.Farmer, JobKind.Baker, JobKind.Miller,
        JobKind.Merchant, JobKind.Innkeeper, JobKind.Blacksmith, JobKind.Guard,
        JobKind.Herbalist, JobKind.Farmer, JobKind.Merchant, JobKind.Baker,
        JobKind.Guard, JobKind.Miller, JobKind.Herbalist, JobKind.Blacksmith
    ];

    public World(int seed = 42) {
        _rng = new Random(seed);
        Player.SeedStarterKit(_rng);
        CreateNpcs();
    }

    private void CreateNpcs() {
        var homeIds = Map.Homes.Keys.ToList();
        var used = new HashSet<TilePos>();
        for (int i = 0; i < 16; i++) {
            var home = Map.Homes[homeIds[i]];
            var start = FindStandableNear(Map.ClampWalkable(home.Door), excludeId: null, used);
            used.Add(start);
            var npc = new Npc {
                Id = $"npc_{i}",
                Name = FirstNames[i],
                Job = JobPool[i],
                HomeId = home.Id,
                Personality = Personality.Random(_rng),
                Gold = _rng.Next(12, 45)
            };
            npc.Needs.Hunger = (float)(_rng.NextDouble() * 0.4 + 0.1);
            npc.Needs.Energy = (float)(_rng.NextDouble() * 0.4 + 0.5);
            npc.Needs.Social = (float)(_rng.NextDouble() * 0.35 + 0.15); // often a bit lonely
            npc.Tile = start;
            npc.DrawX = start.X;
            npc.DrawY = start.Y;
            npc.Interests = FallbackInterests(npc.Job);
            ItemFactory.Seed(npc, _rng);
            Npcs.Add(npc);
        }

        // Cluster a few residents near the square so early conversations can happen.
        var square = Map.Buildings.First(b => b.Kind == BuildingKind.Square);
        used = new HashSet<TilePos>(Npcs.Skip(4).Select(n => n.Tile));
        for (int i = 0; i < 4; i++) {
            var preferred = Map.ClampWalkable(new TilePos(square.Origin.X + i, square.Origin.Y + 2));
            var spot = FindStandableNear(preferred, excludeId: null, used);
            used.Add(spot);
            Npcs[i].Tile = spot;
            Npcs[i].DrawX = spot.X;
            Npcs[i].DrawY = spot.Y;
            Npcs[i].Needs.Social = 0.2f;
        }

        // Seed a few relationships.
        for (int i = 0; i < Npcs.Count; i++) {
            for (int j = i + 1; j < Npcs.Count; j++) {
                if (_rng.NextDouble() < 0.25) {
                    float a = (float)(_rng.NextDouble() * 1.2 - 0.4);
                    Npcs[i].Affinity[Npcs[j].Id] = Math.Clamp(a, -1f, 1f);
                    Npcs[j].Affinity[Npcs[i].Id] = Math.Clamp(a + (float)(_rng.NextDouble() * 0.2 - 0.1), -1f, 1f);
                }
            }
        }
    }

    public static List<string> FallbackInterests(JobKind job) => job switch {
        JobKind.Farmer => ["crop rotation", "morning markets", "weather lore"],
        JobKind.Baker => ["sourdough", "village festivals", "herb gardens"],
        JobKind.Miller => ["river mills", "grain trade", "wood carving"],
        JobKind.Merchant => ["rare goods", "travel stories", "coin counting"],
        JobKind.Innkeeper => ["local gossip", "stew recipes", "card games"],
        JobKind.Blacksmith => ["tool forging", "metal polish", "folk songs"],
        JobKind.Guard => ["night patrols", "sparring", "town history"],
        JobKind.Herbalist => ["wild herbs", "healing teas", "forest walks"],
        _ => ["folk music", "market days", "river walks"]
    };

    public IEnumerable<Npc> NpcsNearby(Npc npc, float rangeTiles) {
        foreach (var other in Npcs) {
            if (other.Id == npc.Id) continue;
            float dx = other.DrawX - npc.DrawX;
            float dy = other.DrawY - npc.DrawY;
            if (dx * dx + dy * dy <= rangeTiles * rangeTiles)
                yield return other;
        }
    }

    public void Log(string text) {
        Events.Add(new WorldEvent { GameHour = Clock.TotalMinutes / 60f, Text = text });
        if (Events.Count > 80)
            Events.RemoveAt(0);
    }

    public void Tick(float realSeconds) {
        float hours = Clock.Advance(realSeconds);
        if (hours <= 0) {
            Player.TickNotification(realSeconds);
            PumpAiCompletions();
            return;
        }

        float nowHour = Clock.TotalMinutes / 60f;
        Conversations.RemoveAll(c => c.IsExpired(nowHour));
        MaybeTriggerEveningCheckIns();
        Player.TickNotification(realSeconds);
        NoticeSpeakRequestMessengers();

        foreach (var npc in Npcs) {
            if (npc.WaitingOnPlayer) continue;
            npc.TalkCooldownHours = Math.Max(0, npc.TalkCooldownHours - hours);
            bool resting = npc.CurrentAction is NpcAction.Sleep or NpcAction.Rest;
            bool socializing = npc.CurrentAction is { } act && ActionFilter.IsChatAction(act);
            npc.Needs.Tick(hours, resting, socializing);
            MaybeLeaveIfFar(npc);

            if (npc.IsMoving) {
                // ~40 tiles per game-hour so crossing town takes minutes, not hours.
                AdvanceNpcWithCollision(npc, 40f * hours);
                if (!npc.IsMoving)
                    OnArrived(npc);
            }
            else if (npc.ActionRemainingHours > 0) {
                npc.ActionRemainingHours -= hours;
                if (npc.ActionRemainingHours <= 0)
                    FinishAction(npc);
            }

            if (ActionFilter.NeedsUrgentDecision(npc, Clock))
                RequestDecision(npc);
        }

        SeparateOverlappingNpcs();
        PumpAiCompletions();
    }

    private HashSet<TilePos> OccupiedTiles(string? excludeId = null) {
        var set = new HashSet<TilePos>();
        foreach (var n in Npcs) {
            if (excludeId is not null && n.Id == excludeId) continue;
            set.Add(n.Tile);
        }
        return set;
    }

    private bool IsOccupied(TilePos tile, string excludeId) =>
        Npcs.Any(n => n.Id != excludeId && n.Tile.Equals(tile));

    /// <summary>Nearest walkable tile not in <paramref name="blocked"/>, searching outward from preferred.</summary>
    public TilePos FindStandableNear(TilePos preferred, string? excludeId, HashSet<TilePos>? blocked = null) {
        preferred = Map.ClampWalkable(preferred);
        var occupied = blocked ?? OccupiedTiles(excludeId);
        if (!occupied.Contains(preferred) && Map.IsWalkable(preferred))
            return preferred;

        for (int r = 1; r < 14; r++) {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++) {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    var c = new TilePos(preferred.X + dx, preferred.Y + dy);
                    if (!Map.IsWalkable(c)) continue;
                    if (occupied.Contains(c)) continue;
                    return c;
                }
        }

        return preferred;
    }

    private TilePos FindApproachTile(TilePos target, string excludeId) {
        var occupied = OccupiedTiles(excludeId);
        foreach (var d in Pathfinding.CardinalNeighbors) {
            var c = new TilePos(target.X + d.X, target.Y + d.Y);
            if (Map.IsWalkable(c) && !occupied.Contains(c))
                return c;
        }
        return FindStandableNear(target, excludeId, occupied);
    }

    private void AdvanceNpcWithCollision(Npc npc, float tilesToMove) {
        float remaining = tilesToMove;
        while (remaining > 0.0001f && npc.IsMoving) {
            var next = npc.NextPathTile!.Value;
            if (IsOccupied(next, npc.Id)) {
                // Goal blocked — pick a free standable tile nearby and repath.
                if (npc.PathIndex + 1 >= npc.Path.Count - 1) {
                    var alt = FindStandableNear(next, npc.Id);
                    if (!alt.Equals(npc.Tile)) {
                        var blocked = OccupiedTiles(npc.Id);
                        blocked.Remove(alt);
                        var path = Pathfinding.FindPath(Map, npc.Tile, alt, blocked);
                        if (path.Count > 1)
                            npc.SetPath(path);
                    }
                }
                break;
            }

            float dx = next.X - npc.DrawX;
            float dy = next.Y - npc.DrawY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= remaining) {
                npc.DrawX = next.X;
                npc.DrawY = next.Y;
                npc.Tile = next;
                npc.PathIndex++;
                remaining -= dist;
            }
            else {
                npc.DrawX += dx / dist * remaining;
                npc.DrawY += dy / dist * remaining;
                remaining = 0;
            }
        }
    }

    private void SeparateOverlappingNpcs() {
        var claimed = new Dictionary<TilePos, Npc>();
        foreach (var npc in Npcs.OrderBy(n => n.Id)) {
            if (!claimed.TryGetValue(npc.Tile, out _)) {
                claimed[npc.Tile] = npc;
                continue;
            }

            var blocked = new HashSet<TilePos>(claimed.Keys);
            var free = FindStandableNear(npc.Tile, npc.Id, blocked);
            TilePos? resumeGoal = npc.IsMoving ? npc.Path[^1] : null;
            npc.Tile = free;
            npc.DrawX = free.X;
            npc.DrawY = free.Y;
            claimed[free] = npc;

            if (resumeGoal is not null) {
                var goal = FindStandableNear(resumeGoal.Value, npc.Id);
                var pathBlocked = OccupiedTiles(npc.Id);
                pathBlocked.Remove(goal);
                var path = Pathfinding.FindPath(Map, free, goal, pathBlocked);
                npc.SetPath(path.Count > 0 ? path : [free]);
            }
        }
    }

    private void OnArrived(Npc npc) {
        if (npc.CurrentAction is null) return;
        switch (npc.CurrentAction) {
            case NpcAction.Work:
                npc.ActionRemainingHours = 1.2f;
                break;
            case NpcAction.BuyFood:
                npc.ActionRemainingHours = 0.15f;
                break;
            case NpcAction.Eat:
                npc.ActionRemainingHours = 0.2f;
                break;
            case NpcAction.Sleep:
                npc.ActionRemainingHours = Clock.IsNight ? 2.5f : 1.2f;
                break;
            case NpcAction.Tavern:
                npc.ActionRemainingHours = 0.8f;
                break;
            case NpcAction.Talk:
                npc.ActionRemainingHours = 0.25f;
                BeginTalk(npc, outcome: null);
                break;
            case NpcAction.OfferGift:
            case NpcAction.RequestItem:
            case NpcAction.ProposeTrade:
            case NpcAction.TakeWithoutConsent:
                npc.ActionRemainingHours = 0.2f;
                BeginExchange(npc);
                break;
            case NpcAction.LeaveConversation:
                LeaveConversation(npc);
                npc.ActionRemainingHours = 0.05f;
                break;
            case NpcAction.ContactPlayer:
                BeginPlayerContact(npc);
                break;
            case NpcAction.InformNpc:
                npc.ActionRemainingHours = 0.15f;
                DeliverSpeakRequest(npc);
                break;
            case NpcAction.ResumeInterrupted:
                npc.ActionRemainingHours = 0.05f;
                break;
            case NpcAction.GoHome:
            case NpcAction.Rest:
                npc.ActionRemainingHours = 0.4f;
                break;
        }
    }

    private void FinishAction(Npc npc) {
        switch (npc.CurrentAction) {
            case NpcAction.Work: {
                string? producedName = null;
                if (_rng.NextDouble() < 0.7) {
                    var produced = ItemFactory.ProduceFor(npc.Job, _rng);
                    if (npc.Inventory.TryAdd(produced)) {
                        producedName = produced.Name;
                        npc.Memories.Add($"Harvested {produced.Name} while working.");
                    }
                }
                string report = producedName is null
                    ? $"Worked a shift as {npc.OccupationName}."
                    : $"Worked as {npc.OccupationName} and produced {producedName}.";
                npc.AddReport(report);
                npc.Memories.Add(report);
                Log($"{npc.Name} finished a work shift.");
                break;
            }
            case NpcAction.BuyFood:
                if (npc.Gold >= 3) {
                    npc.Gold -= 3;
                    var meal = ItemFactory.Meal(_rng);
                    npc.Inventory.TryAdd(meal);
                    ConsumeFood(npc, fallbackHungerRelief: 0.55f);
                    npc.Memories.Add("Bought food at the market.");
                    Log($"{npc.Name} bought food.");
                }
                break;
            case NpcAction.Eat:
                if (!ConsumeFood(npc, fallbackHungerRelief: 0.15f))
                    npc.Memories.Add("Scrounged a thin meal.");
                break;
            case NpcAction.Sleep:
                npc.Needs.Energy = Math.Min(1f, npc.Needs.Energy + 0.55f);
                npc.Memories.Add("Slept at home.");
                Log($"{npc.Name} woke from rest.");
                break;
            case NpcAction.Tavern:
                if (npc.Gold >= 2) {
                    npc.Gold -= 2;
                    npc.Needs.Social = Math.Min(1f, npc.Needs.Social + 0.25f);
                    npc.Needs.Hunger = Math.Max(0, npc.Needs.Hunger - 0.1f);
                    npc.Memories.Add("Spent an evening at the tavern.");
                    Log($"{npc.Name} left the tavern.");
                }
                break;
            case NpcAction.Talk:
                npc.TalkCooldownHours = Math.Max(npc.TalkCooldownHours, 0.8f);
                LeaveConversation(npc);
                break;
            case NpcAction.OfferGift:
            case NpcAction.RequestItem:
            case NpcAction.ProposeTrade:
            case NpcAction.TakeWithoutConsent:
                npc.TalkCooldownHours = Math.Max(npc.TalkCooldownHours, 0.6f);
                break;
            case NpcAction.ContactPlayer:
                // Closed via player hang-up.
                break;
            case NpcAction.InformNpc:
                break;
            case NpcAction.ResumeInterrupted:
                ResumeInterrupted(npc);
                return;
        }

        npc.CurrentAction = null;
        npc.CurrentTargetNpcId = null;
        npc.PendingItemId = null;
        npc.PendingGold = 0;
        npc.Path.Clear();
        npc.PathIndex = -1;
        RequestDecision(npc);
    }

    private bool ConsumeFood(Npc npc, float fallbackHungerRelief) {
        var food = npc.Inventory.Items.FirstOrDefault(i => i.Tags.Contains("food") && !i.IsLivelihoodAsset && i.Quantity > 0);
        if (food is null) {
            npc.Needs.Hunger = Math.Max(0, npc.Needs.Hunger - fallbackHungerRelief);
            return false;
        }

        npc.Inventory.TryRemove(food.Id, 1);
        npc.Needs.Hunger = Math.Max(0, npc.Needs.Hunger - 0.5f);
        npc.Memories.Add($"Ate {food.Name}.");
        return true;
    }

    private void MaybeTriggerEveningCheckIns() {
        if (Clock.Day <= _lastPaydayDay) return;
        _lastPaydayDay = Clock.Day;
        // New calendar day: a few townsfolk may check in (work report, problem, or chat).
        var candidates = Npcs
            .Where(n => !n.WaitingOnPlayer && !n.DecisionPending && n.CurrentAction != NpcAction.ContactPlayer)
            .OrderByDescending(n => n.RecentReports.Count + (n.Needs.MoneyPressure(n.Gold) > 0.5f ? 2 : 0) + (n.Needs.Social < 0.35f ? 1 : 0))
            .Take(3)
            .ToList();
        if (candidates.Count == 0) return;
        Player.Notify("Morning check-ins — some townsfolk may visit the town computer.", 10f);
        Log("A new day — townsfolk may check in with the overseer.");
        if (Player.TerminalOpen) return;
        var first = candidates[0];
        first.ContactReason = first.RecentReports.Count > 0 ? PlayerContactReason.ReportWork
            : first.Needs.Hunger > 0.7f || first.Gold < 8 ? PlayerContactReason.NeedHelp
            : PlayerContactReason.JustTalk;
        SaveInterrupted(first);
        StartAction(first, ContactPlayerOption(first));
    }

    private ActionOption ContactPlayerOption(Npc npc) {
        var computer = Map.Buildings.First(b => b.Kind == BuildingKind.Computer);
        string desc = npc.ContactReason switch {
            PlayerContactReason.ReportWork => "use the town computer to tell the overseer what you worked on and produced",
            PlayerContactReason.NeedHelp => "use the town computer to ask the overseer for help with a problem",
            PlayerContactReason.Summoned => "use the town computer; the overseer asked to speak with you",
            _ => "use the town computer to talk with the overseer"
        };
        return new ActionOption {
            Action = NpcAction.ContactPlayer,
            Id = "contact_player",
            Description = desc,
            TargetTile = Map.ClampWalkable(computer.Door)
        };
    }

    private void BeginPlayerContact(Npc npc) {
        if (Player.TerminalOpen && Player.ActiveNpcId != npc.Id) {
            npc.ActionRemainingHours = 0.3f;
            npc.Memories.Add("The town computer was busy.");
            return;
        }

        if (Player.SpeakRequestTargetId == npc.Id)
            npc.ContactReason = PlayerContactReason.Summoned;

        Clock.SetSpeed(1f);
        Player.OpenTerminal(npc, Clock.TotalMinutes / 60f);
        Player.Notify($"{npc.Name} is contacting you on the town computer!", 10f);
        npc.WaitingOnPlayer = true;
        npc.ActionRemainingHours = 99f;

        string opener = BuildContactOpener(npc);
        Player.AddChat(npc.Name, opener, Clock.TotalMinutes / 60f);
        npc.LastSpokenLine = opener;
        npc.Memories.Add("Spoke with the overseer on the town computer.");
        Log($"{npc.Name} contacted the overseer ({npc.ContactReason}).");
        if (Player.SpeakRequestTargetId == npc.Id) {
            Player.SpeakRequestTargetId = null;
            Player.SpeakRequestSeenBy.Clear();
        }
    }

    private string BuildContactOpener(Npc npc) {
        if (npc.ContactReason == PlayerContactReason.Summoned)
            return "I heard you wanted to speak with me, Overseer.";

        if (npc.ContactReason == PlayerContactReason.ReportWork || npc.RecentReports.Count > 0) {
            string report = npc.RecentReports.Count > 0
                ? string.Join(" ", npc.RecentReports.TakeLast(2))
                : $"I put in a shift as {npc.OccupationName}.";
            return $"Hello, Overseer. {report} I wanted you to know. Pay is at your discretion.";
        }

        if (npc.ContactReason == PlayerContactReason.NeedHelp || npc.Needs.Hunger > 0.7f || npc.Gold < 6) {
            if (npc.Needs.Hunger > 0.7f)
                return $"Overseer, I'm struggling — hunger is bad and I'm not sure what to do.";
            if (npc.Gold < 6)
                return $"Overseer, coin is tight. I could use advice… or help, if you can spare it.";
            return "Overseer, I've got a problem I hoped you could help with.";
        }

        string interest = npc.Interests.FirstOrDefault() ?? "town life";
        return $"Hello, Overseer. Just wanted to talk — I've been thinking about {interest}.";
    }

    public void EndPlayerSession() {
        var npc = Npcs.FirstOrDefault(n => n.Id == Player.ActiveNpcId);
        Player.CloseTerminal();
        if (npc is null) return;
        npc.WaitingOnPlayer = false;
        npc.CurrentAction = null;
        npc.CurrentTargetNpcId = null;
        npc.PendingItemId = null;
        npc.PendingGold = 0;
        npc.Path.Clear();
        npc.PathIndex = -1;
        npc.ActionRemainingHours = 0;
        npc.ContactReason = PlayerContactReason.JustTalk;
        // Reports were delivered verbally; clear so they don't re-plead the same shift forever.
        npc.RecentReports.Clear();
        Player.AddChat("system", $"{npc.Name} disconnected.", Clock.TotalMinutes / 60f);
        Log($"Overseer closed the session with {npc.Name}.");
        RequestDecision(npc);
    }

    public List<PlayerActionOption> BuildPlayerActions() {
        var list = new List<PlayerActionOption>();
        if (!Player.TerminalOpen) {
            list.Add(new PlayerActionOption { Id = "open_roster", Label = "Request speak with an NPC…" });
            return list;
        }

        var npc = Npcs.FirstOrDefault(n => n.Id == Player.ActiveNpcId);
        if (npc is null) return list;

        list.Add(new PlayerActionOption { Id = "pay_1", Label = "Pay 1g" });
        list.Add(new PlayerActionOption { Id = "pay_5", Label = "Pay 5g" });
        list.Add(new PlayerActionOption { Id = "pay_10", Label = "Pay 10g" });
        list.Add(new PlayerActionOption { Id = "pay_25", Label = "Pay 25g" });
        if (Player.Inventory.FindByName("Apple") is { Quantity: > 0 })
            list.Add(new PlayerActionOption { Id = "give_apple", Label = "Give 1 apple" });
        list.Add(new PlayerActionOption { Id = "request_other", Label = "Ask them to fetch someone…" });
        list.Add(new PlayerActionOption { Id = "hang_up", Label = "End conversation" });
        return list;
    }

    public List<PlayerActionOption> BuildSpeakTargetActions() =>
        Npcs.OrderBy(n => n.Name)
            .Select(n => new PlayerActionOption { Id = $"speak_{n.Id}", Label = $"Ask to speak with {n.Name}" })
            .ToList();

    public void ApplyPlayerAction(string actionId) {
        float hour = Clock.TotalMinutes / 60f;
        if (actionId.StartsWith("speak_", StringComparison.Ordinal)) {
            string targetId = actionId["speak_".Length..];
            var target = Npcs.FirstOrDefault(n => n.Id == targetId);
            if (target is null) return;
            Player.SpeakRequestTargetId = targetId;
            Player.SpeakRequestSeenBy.Clear();
            Player.Notify($"Speak request posted for {target.Name}. Nearby NPCs may relay it.", 8f);
            Player.AddChat("system", $"You asked to speak with {target.Name}.", hour);
            Log($"Overseer requested to speak with {target.Name}.");
            NoticeSpeakRequestMessengers();
            return;
        }

        if (actionId == "open_roster")
            return;

        var npc = Npcs.FirstOrDefault(n => n.Id == Player.ActiveNpcId);
        if (npc is null || !Player.TerminalOpen) return;

        switch (actionId) {
            case "pay_1":
                TryPayAmount(npc, 1);
                break;
            case "pay_5":
                TryPayAmount(npc, 5);
                break;
            case "pay_10":
                TryPayAmount(npc, 10);
                break;
            case "pay_25":
                TryPayAmount(npc, 25);
                break;
            case "give_apple":
                GiveApple(npc);
                break;
            case "request_other":
                break;
            case "hang_up":
                EndPlayerSession();
                break;
        }
    }

    public bool TryPayAmount(Npc npc, int amount) {
        if (amount <= 0) return false;
        if (Player.Gold < amount) {
            Player.AddChat("system", $"Not enough gold (need {amount}g, have {Player.Gold}g).", Clock.TotalMinutes / 60f);
            Player.Notify($"Cannot pay {amount}g — you only have {Player.Gold}g.", 6f);
            return false;
        }

        Player.Gold -= amount;
        npc.Gold += amount;
        float hour = Clock.TotalMinutes / 60f;
        Player.AddChat(Player.Name, $"Here's {amount} gold.", hour);
        EnqueueNpcReply(npc, $"The overseer just paid you {amount} gold. Reply briefly in character.");
        npc.Memories.Add($"Overseer paid me {amount} gold.");
        Log($"Overseer paid {npc.Name} {amount}g.");
        return true;
    }

    public void SendPlayerChat(string text) {
        if (!Player.TerminalOpen || string.IsNullOrWhiteSpace(text)) return;
        var npc = Npcs.FirstOrDefault(n => n.Id == Player.ActiveNpcId);
        if (npc is null) return;

        string cleaned = text.Trim();
        if (cleaned.Length > 240)
            cleaned = cleaned[..240];
        float hour = Clock.TotalMinutes / 60f;
        Player.AddChat(Player.Name, cleaned, hour);
        npc.Memories.Add($"Overseer said: \"{cleaned}\"");
        EnqueueNpcReply(npc, $"The overseer said to you: \"{cleaned}\". Reply with one short spoken line in character. You may discuss work you did, problems, or just converse. Do not invent that you were paid unless they paid you.");
    }

    private void EnqueueNpcReply(Npc npc, string outcomeFact) {
        EnqueueAi(async () => {
            if (!Player.TerminalOpen || Player.ActiveNpcId != npc.Id) return;
            string line;
            if (Ai is null || OfflineFallback) {
                line = OfflinePlayerReply(npc, outcomeFact);
            }
            else {
                try {
                    // Reuse speak endpoint with a synthetic listener representing the overseer.
                    var overseer = new Npc {
                        Id = Player.Id,
                        Name = Player.Name,
                        Job = JobKind.Merchant,
                        HomeId = npc.HomeId,
                        Personality = Personality.Random(_rng)
                    };
                    line = await Ai.SpeakAsync(npc, overseer, 0.4f, outcomeFact);
                }
                catch (Exception ex) {
                    Log($"Overseer chat fallback ({ex.Message}).");
                    line = OfflinePlayerReply(npc, outcomeFact);
                }
            }

            if (!Player.TerminalOpen || Player.ActiveNpcId != npc.Id) return;
            Player.AddChat(npc.Name, line, Clock.TotalMinutes / 60f);
            npc.LastSpokenLine = line;
        });
    }

    private static string OfflinePlayerReply(Npc npc, string outcomeFact) {
        if (outcomeFact.Contains("paid you", StringComparison.OrdinalIgnoreCase))
            return "Thank you, Overseer. That helps a great deal.";
        if (npc.RecentReports.Count > 0)
            return $"As I said — {npc.RecentReports[^1]} What do you think?";
        if (npc.Needs.Hunger > 0.6f)
            return "I'm still hungry… any advice would help.";
        string interest = npc.Interests.FirstOrDefault() ?? "the town";
        return $"I hear you. About {interest} — I'm listening.";
    }

    private void GiveApple(Npc npc) {
        var apples = Player.Inventory.FindByName("Apple");
        if (apples is null || apples.Quantity <= 0) return;
        Player.Inventory.TryRemove(apples.Id, 1);
        var gift = ItemFactory.Normalize("apple", JobKind.Farmer, _rng);
        gift.Tags.Add("food");
        npc.Inventory.TryAdd(gift);
        float hour = Clock.TotalMinutes / 60f;
        Player.AddChat(Player.Name, "Have an apple.", hour);
        Player.AddChat(npc.Name, "Thank you for the apple.", hour);
        npc.Memories.Add("Overseer gave me an apple.");
    }

    private void NoticeSpeakRequestMessengers() {
        if (Player.SpeakRequestTargetId is null) return;
        var computer = Map.Buildings.First(b => b.Kind == BuildingKind.Computer);
        var door = Map.ClampWalkable(computer.Door);
        foreach (var npc in Npcs) {
            if (npc.Id == Player.SpeakRequestTargetId) continue;
            if (Player.SpeakRequestSeenBy.Contains(npc.Id)) continue;
            if (npc.WaitingOnPlayer || npc.DecisionPending) continue;
            if (npc.CurrentAction is NpcAction.ContactPlayer or NpcAction.InformNpc) continue;
            float dx = npc.DrawX - door.X;
            float dy = npc.DrawY - door.Y;
            if (dx * dx + dy * dy > ActionFilter.TalkRange * ActionFilter.TalkRange) continue;

            Player.SpeakRequestSeenBy.Add(npc.Id);
            SaveInterrupted(npc);
            var target = Npcs.First(n => n.Id == Player.SpeakRequestTargetId);
            npc.InformTargetNpcId = target.Id;
            StartAction(npc, new ActionOption {
                Action = NpcAction.InformNpc,
                Id = "inform_npc",
                Description = $"tell {target.Name} that the overseer wants to speak with them",
                TargetNpcId = target.Id,
                TargetTile = target.Tile
            });
            npc.Memories.Add($"Saw the overseer's request to speak with {target.Name}.");
            Log($"{npc.Name} is going to inform {target.Name}.");
            break;
        }
    }

    private void DeliverSpeakRequest(Npc messenger) {
        var target = Npcs.FirstOrDefault(n => n.Id == messenger.InformTargetNpcId)
                     ?? Npcs.FirstOrDefault(n => n.Id == messenger.CurrentTargetNpcId);
        messenger.InformTargetNpcId = null;
        if (target is null) return;
        target.Memories.Add($"{messenger.Name} said the overseer wants to speak with me.");
        Log($"{messenger.Name} informed {target.Name} of the overseer's request.");
        if (!target.WaitingOnPlayer && target.CurrentAction != NpcAction.ContactPlayer) {
            SaveInterrupted(target);
            StartAction(target, ContactPlayerOption(target));
        }
    }

    public void SaveInterrupted(Npc npc) {
        if (npc.CurrentAction is null
            or NpcAction.ContactPlayer
            or NpcAction.InformNpc
            or NpcAction.ResumeInterrupted
            or NpcAction.LeaveConversation)
            return;
        npc.Interrupted = new InterruptedPlan {
            Action = npc.CurrentAction.Value,
            TargetNpcId = npc.CurrentTargetNpcId,
            TargetTile = npc.Path.Count > 0 ? npc.Path[^1] : npc.Tile,
            ItemId = npc.PendingItemId,
            GoldAmount = npc.PendingGold
        };
    }

    private void ResumeInterrupted(Npc npc) {
        var plan = npc.Interrupted;
        npc.Interrupted = null;
        npc.CurrentAction = null;
        if (plan is null) {
            RequestDecision(npc);
            return;
        }

        StartAction(npc, new ActionOption {
            Action = plan.Action,
            Id = "resume",
            Description = $"resume {plan.Action}",
            TargetNpcId = plan.TargetNpcId,
            TargetTile = plan.TargetTile,
            ItemId = plan.ItemId,
            GoldAmount = plan.GoldAmount
        });
        npc.Memories.Add($"Returned to {plan.Action} after speaking with the overseer.");
    }

    private void MaybeLeaveIfFar(Npc npc) {
        var conv = ConversationOf(npc.Id);
        if (conv is null || npc.IsMoving) return;
        if (npc.CurrentAction is NpcAction.LeaveConversation) return;
        float limit = ActionFilter.TalkRange * 1.6f;
        bool near = false;
        foreach (string id in conv.ParticipantIds) {
            if (id == npc.Id) continue;
            var other = Npcs.FirstOrDefault(n => n.Id == id);
            if (other is null) continue;
            float dx = other.DrawX - npc.DrawX;
            float dy = other.DrawY - npc.DrawY;
            if (dx * dx + dy * dy <= limit * limit) {
                near = true;
                break;
            }
        }

        if (!near)
            LeaveConversation(npc);
    }

    public void LeaveConversation(Npc npc) {
        var conv = ConversationOf(npc.Id);
        if (conv is null) return;

        var others = conv.ParticipantIds
            .Where(id => id != npc.Id)
            .Select(id => Npcs.FirstOrDefault(n => n.Id == id))
            .Where(n => n is not null)
            .Cast<Npc>()
            .ToList();

        float close = conv.HadTheft ? -0.08f : conv.FriendlyScore > 0 ? 0.05f : 0.01f;
        foreach (var other in others) {
            npc.AdjustAffinity(other.Id, close);
            other.AdjustAffinity(npc.Id, close * 0.6f);
        }

        string who = others.Count == 0 ? "the group" : string.Join(", ", others.Select(o => o.Name));
        npc.Memories.Add(conv.HadTheft
            ? $"Left after trouble with {who}."
            : conv.FriendlyScore > 0
                ? $"Left a friendly chat with {who}."
                : $"Left the conversation with {who}.");

        if (_rng.NextDouble() < 0.35 && npc.Interests.Count < 5) {
            string? topic = conv.RecentItemNames.LastOrDefault()
                            ?? others.SelectMany(o => o.Interests).FirstOrDefault(i => !npc.Interests.Contains(i));
            if (!string.IsNullOrWhiteSpace(topic) && !npc.Interests.Contains(topic))
                npc.Interests.Add(topic);
        }

        conv.AddMessage(new ChatMessage {
            SpeakerId = "system",
            SpeakerName = "*",
            Text = $"{npc.Name} left the conversation",
            GameHour = Clock.TotalMinutes / 60f
        });
        conv.RemoveParticipant(npc.Id);
        if (conv.ParticipantIds.Count == 0)
            Conversations.Remove(conv);
        Log($"{npc.Name} left a conversation.");
    }

    private void BeginTalk(Npc speaker, string? outcome) {
        var listener = Npcs.FirstOrDefault(n => n.Id == speaker.CurrentTargetNpcId)
                       ?? NpcsNearby(speaker, ActionFilter.TalkRange).FirstOrDefault();
        if (listener is null) {
            speaker.CurrentAction = null;
            RequestDecision(speaker);
            return;
        }

        speaker.CurrentTargetNpcId = listener.Id;
        EnqueueAi(async () => {
            string line;
            if (Ai is null || OfflineFallback) {
                line = OfflineLine(speaker, listener, outcome);
            }
            else {
                try {
                    line = await Ai.SpeakAsync(speaker, listener, speaker.GetAffinity(listener.Id), outcome);
                }
                catch (Exception ex) {
                    Log($"Dialogue fallback ({ex.Message}).");
                    line = OfflineLine(speaker, listener, outcome);
                }
            }

            if (speaker.CurrentAction is not { } current || !ActionFilter.IsChatAction(current))
                return;

            speaker.LastSpokenLine = line;
            listener.LastSpokenLine = null;
            if (string.IsNullOrWhiteSpace(outcome)) {
                float delta = 0.05f + speaker.Personality.Empathy * 0.08f;
                speaker.AdjustAffinity(listener.Id, delta);
                listener.AdjustAffinity(speaker.Id, delta * 0.7f);
                speaker.Memories.Add($"Talked with {listener.Name}: \"{line}\"");
                listener.Memories.Add($"{speaker.Name} said: \"{line}\"");
            }
            speaker.Needs.Social = Math.Min(1f, speaker.Needs.Social + 0.3f);
            listener.Needs.Social = Math.Min(1f, listener.Needs.Social + 0.15f);
            speaker.TalkCooldownHours = 1.2f;
            listener.TalkCooldownHours = 0.6f;

            var conversation = JoinConversation(speaker, listener);
            if (string.IsNullOrWhiteSpace(outcome))
                conversation.FriendlyScore++;
            conversation.AddMessage(new ChatMessage {
                SpeakerId = speaker.Id,
                SpeakerName = speaker.Name,
                Text = line,
                GameHour = Clock.TotalMinutes / 60f
            });

            Log($"{speaker.Name} → {listener.Name}: {line}");
        });
    }

    private void BeginExchange(Npc actor) {
        var target = Npcs.FirstOrDefault(n => n.Id == actor.CurrentTargetNpcId);
        var item = actor.PendingItemId is null
            ? null
            : target?.Inventory.FindById(actor.PendingItemId) ?? actor.Inventory.FindById(actor.PendingItemId);
        if (target is null || item is null || actor.CurrentAction is null) {
            actor.CurrentAction = null;
            RequestDecision(actor);
            return;
        }

        var action = actor.CurrentAction.Value;
        float roll = (float)_rng.NextDouble();
        ExchangeOutcome outcome = action switch {
            NpcAction.OfferGift => TradeResolver.ResolveGift(actor, target, item, roll),
            NpcAction.RequestItem => TradeResolver.ResolveRequest(actor, target, item, roll),
            NpcAction.ProposeTrade => TradeResolver.ResolveTrade(target, actor, item, actor.PendingGold, roll),
            NpcAction.TakeWithoutConsent => TradeResolver.ResolveTheft(actor, target, item, roll),
            _ => throw new InvalidOperationException($"Not an exchange: {action}")
        };

        // ProposeTrade: seller is target, buyer is actor. Memories are written from seller's perspective
        // in ResolveTrade (ActorMemory = seller). Swap application below for trade.
        bool sellerIsTarget = action == NpcAction.ProposeTrade;
        var memoryActor = sellerIsTarget ? target : actor;
        var memoryTarget = sellerIsTarget ? actor : target;
        memoryActor.AdjustAffinity(memoryTarget.Id, outcome.ActorAffinityDelta);
        memoryTarget.AdjustAffinity(memoryActor.Id, outcome.TargetAffinityDelta);
        memoryActor.Memories.Add(outcome.ActorMemory);
        memoryTarget.Memories.Add(outcome.TargetMemory);

        var conversation = JoinConversation(actor, target);
        if (outcome.Kind == ExchangeKind.Theft)
            conversation.HadTheft = true;
        if (outcome.Success && outcome.Kind != ExchangeKind.Theft)
            conversation.FriendlyScore++;
        if (!string.IsNullOrWhiteSpace(outcome.ItemName))
            conversation.RecentItemNames.Add(outcome.ItemName);

        actor.CurrentTargetNpcId = target.Id;
        BeginTalk(actor, outcome.OutcomeFact);
        Log(outcome.OutcomeFact);
    }

    public Conversation? ConversationOf(string npcId) =>
        Conversations.FirstOrDefault(c => c.ParticipantIds.Contains(npcId));

    public Conversation JoinConversation(Npc a, Npc b) {
        var withA = ConversationOf(a.Id);
        var withB = ConversationOf(b.Id);
        if (withA is not null && withB is not null && withA.Id == withB.Id)
            return withA;

        bool active(Npc n) => n.CurrentAction is { } act && ActionFilter.IsChatAction(act);
        if (withA is not null && withB is not null && active(a) && active(b)) {
            MergeConversations(withA, withB);
            withA.AddParticipant(a.Id);
            withA.AddParticipant(b.Id);
            return withA;
        }

        if (withA is not null && withB is null) {
            withA.AddParticipant(b.Id);
            return withA;
        }

        if (withB is not null && withA is null) {
            withB.AddParticipant(a.Id);
            return withB;
        }

        if (withA is not null && withB is not null) {
            LeaveConversation(a);
            withB = ConversationOf(b.Id) ?? withB;
            withB.AddParticipant(a.Id);
            return withB;
        }

        var created = new Conversation {
            Id = $"conv_{++_nextConversationId}",
            LastActivityHour = Clock.TotalMinutes / 60f
        };
        created.AddParticipant(a.Id);
        created.AddParticipant(b.Id);
        Conversations.Add(created);
        return created;
    }

    public Conversation GetOrCreateConversation(Npc a, Npc b) => JoinConversation(a, b);

    private void MergeConversations(Conversation keep, Conversation absorb) {
        foreach (string id in absorb.ParticipantIds)
            keep.AddParticipant(id);
        keep.Messages.AddRange(absorb.Messages);
        keep.HadTheft |= absorb.HadTheft;
        keep.FriendlyScore += absorb.FriendlyScore;
        keep.RecentItemNames.AddRange(absorb.RecentItemNames);
        keep.Messages.Sort((x, y) => x.GameHour.CompareTo(y.GameHour));
        keep.LastActivityHour = Math.Max(keep.LastActivityHour, absorb.LastActivityHour);
        keep.ScrollFromBottom = 0;
        Conversations.Remove(absorb);
    }

    private static string OfflineLine(Npc speaker, Npc listener, string? outcome) {
        if (!string.IsNullOrWhiteSpace(outcome))
            return outcome;
        string interest = speaker.Interests.FirstOrDefault() ?? "the weather";
        return $"Good to see you, {listener.Name}. I've been thinking about {interest}.";
    }

    public void RequestDecision(Npc npc) {
        if (npc.DecisionPending) return;
        var options = ActionFilter.BuildValidActions(this, npc);
        if (options.Count == 0) return;
        npc.DecisionPending = true;

        EnqueueAi(async () => {
            DecisionResult decision;
            if (Ai is null || OfflineFallback)
                decision = OfflineDecide(npc, options);
            else {
                try {
                    decision = await Ai.DecideAsync(BuildState(npc), options);
                }
                catch (Exception ex) {
                    Log($"Decision fallback for {npc.Name}: {ex.Message}");
                    decision = OfflineDecide(npc, options);
                }
            }

            npc.LastDecision = decision;
            npc.DecisionPending = false;
            ApplyDecision(npc, options, decision.Choice);
        });
    }

    private void ApplyDecision(Npc npc, List<ActionOption> options, string choiceId) {
        var option = options.FirstOrDefault(o => o.Id == choiceId) ?? options[^1];
        if (option.Action == NpcAction.Talk && option.TargetNpcId is null) {
            var targets = ActionFilter.BuildTalkTargets(this, npc);
            if (targets.Count == 0) {
                option = options.First(o => o.Action != NpcAction.Talk);
            }
            else {
                EnqueueAi(async () => {
                    DecisionResult pick;
                    if (Ai is null || OfflineFallback)
                        pick = OfflineDecide(npc, targets);
                    else {
                        try {
                            pick = await Ai.DecideAsync(BuildState(npc), targets);
                        }
                        catch {
                            pick = OfflineDecide(npc, targets);
                        }
                    }

                    var target = targets.FirstOrDefault(t => t.Id == pick.Choice) ?? targets[0];
                    StartAction(npc, target);
                });
                return;
            }
        }

        StartAction(npc, option);
    }

    private void StartAction(Npc npc, ActionOption option) {
        if (option.Action is NpcAction.ContactPlayer or NpcAction.InformNpc)
            SaveInterrupted(npc);

        if (option.Action is not NpcAction.LeaveConversation
            && option.Action is not NpcAction.ContactPlayer
            && option.Action is not NpcAction.ResumeInterrupted
            && !ActionFilter.IsChatAction(option.Action))
            LeaveConversation(npc);

        npc.CurrentAction = option.Action;
        npc.CurrentTargetNpcId = option.TargetNpcId;
        npc.PendingItemId = option.ItemId;
        npc.PendingGold = option.GoldAmount;
        if (option.Action == NpcAction.InformNpc)
            npc.InformTargetNpcId = option.TargetNpcId;
        var goal = option.TargetTile ?? npc.Tile;
        if (option.TargetNpcId is not null) {
            var other = Npcs.FirstOrDefault(n => n.Id == option.TargetNpcId);
            if (other is not null)
                goal = FindApproachTile(other.Tile, npc.Id);
        }
        else {
            goal = FindStandableNear(Map.ClampWalkable(goal), npc.Id);
        }

        var blocked = OccupiedTiles(npc.Id);
        blocked.Remove(goal);
        var path = Pathfinding.FindPath(Map, npc.Tile, goal, blocked);
        if (path.Count == 1 && !path[0].Equals(goal)) {
            // No route around crowds — try without occupancy, then separate on arrival.
            path = Pathfinding.FindPath(Map, npc.Tile, goal);
        }

        npc.SetPath(path);
        if (!npc.IsMoving)
            OnArrived(npc);
        else {
            string label = option.Action == NpcAction.Talk && option.TargetNpcId is not null
                ? $"talk ({option.TargetNpcId})"
                : option.Id;
            Log($"{npc.Name} decided to {label}.");
        }
    }

    public Dictionary<string, object> BuildState(Npc npc) {
        return new Dictionary<string, object> {
            ["name"] = npc.Name,
            ["occupation"] = npc.OccupationName,
            ["hour"] = Math.Round(Clock.HourOfDay, 1),
            ["is_night"] = Clock.IsNight,
            ["hunger"] = Math.Round(npc.Needs.Hunger, 2),
            ["energy"] = Math.Round(npc.Needs.Energy, 2),
            ["social"] = Math.Round(npc.Needs.Social, 2),
            ["gold"] = npc.Gold,
            ["money_pressure"] = Math.Round(npc.Needs.MoneyPressure(npc.Gold), 2),
            ["inventory"] = npc.Inventory.Summary(),
            ["honesty"] = Math.Round(npc.Personality.Honesty, 2),
            ["aggression"] = Math.Round(npc.Personality.Aggression, 2),
            ["greed"] = Math.Round(npc.Personality.Greed, 2),
            ["sociability"] = Math.Round(npc.Personality.Sociability, 2),
            ["empathy"] = Math.Round(npc.Personality.Empathy, 2),
            ["curiosity"] = Math.Round(npc.Personality.Curiosity, 2),
            ["in_conversation"] = ConversationOf(npc.Id) is not null,
            ["recent_reports"] = string.Join("; ", npc.RecentReports.TakeLast(2)),
            ["has_interrupted"] = npc.Interrupted is not null
        };
    }

    public DecisionResult ChooseOffline(Npc npc, List<ActionOption> options) =>
        OfflineDecide(npc, options);

    private DecisionResult OfflineDecide(Npc npc, List<ActionOption> options) {
        // Heuristic weighted by needs when AI is offline.
        float Score(ActionOption o) => o.Action switch {
            NpcAction.Eat or NpcAction.BuyFood => npc.Needs.Hunger * 2f,
            NpcAction.Sleep => (1f - npc.Needs.Energy) * 2f + (Clock.IsNight ? 1f : 0f),
            NpcAction.Work => (Clock.IsWorkHours ? 1.2f : 0f) + npc.Needs.MoneyPressure(npc.Gold),
            NpcAction.Talk => (1f - npc.Needs.Social) * (0.5f + npc.Personality.Sociability),
            NpcAction.Tavern => (1f - npc.Needs.Social) * 0.8f,
            NpcAction.GoHome => Clock.IsNight ? 0.8f : 0.2f,
            NpcAction.OfferGift => npc.Personality.Empathy * (1.1f - npc.Personality.Greed),
            NpcAction.RequestItem => (npc.Needs.Hunger * 0.8f + npc.Needs.MoneyPressure(npc.Gold) * 0.4f) * (0.4f + npc.Personality.Empathy * 0.3f),
            NpcAction.ProposeTrade => npc.Personality.Greed * 0.9f + npc.Needs.MoneyPressure(npc.Gold) * 0.7f,
            NpcAction.TakeWithoutConsent => (1f - npc.Personality.Honesty) * npc.Personality.Aggression * 1.4f,
            NpcAction.LeaveConversation => npc.Needs.Social >= 0.65f || npc.Needs.MoneyPressure(npc.Gold) >= 0.6f ? 1.3f : 0.25f,
            NpcAction.ContactPlayer => npc.RecentReports.Count > 0 ? 1.6f
                : (Player.SpeakRequestTargetId == npc.Id ? 2.2f
                : (npc.Needs.MoneyPressure(npc.Gold) > 0.55f || npc.Needs.Hunger > 0.65f ? 1.3f
                : npc.Personality.Sociability * 0.45f)),
            NpcAction.InformNpc => 1.8f,
            NpcAction.ResumeInterrupted => 1.4f,
            _ => 0.15f
        };

        var ranked = options.OrderByDescending(Score).ToList();
        var best = ranked[0];
        var probs = options.ToDictionary(o => o.Id, o => 0.05f);
        probs[best.Id] = 0.7f;
        if (ranked.Count > 1) probs[ranked[1].Id] = 0.25f;
        float sum = probs.Values.Sum();
        foreach (string? key in probs.Keys.ToList())
            probs[key] /= sum;

        return new DecisionResult { Choice = best.Id, Probabilities = probs, Confidence = 0.5f };
    }

    private void EnqueueAi(Func<Task> job) {
        lock (_aiLock) {
            _aiJobs.Enqueue(job);
            if (!_aiWorkerRunning) {
                _aiWorkerRunning = true;
                _ = Task.Run(AiWorkerLoop);
            }
        }
    }

    private async Task AiWorkerLoop() {
        while (true) {
            Func<Task>? job = null;
            lock (_aiLock) {
                if (_aiJobs.Count == 0) {
                    _aiWorkerRunning = false;
                    return;
                }
                job = _aiJobs.Dequeue();
            }

            try {
                await job();
            }
            catch (Exception ex) {
                Log($"AI job error: {ex.Message}");
            }
        }
    }

    private void PumpAiCompletions() {
        // Task.Run completions mutate world on background thread.
        // For this prototype we accept that and keep mutations simple.
    }

    public async Task EnsureInterestsAsync(string cachePath) {
        if (File.Exists(cachePath)) {
            try {
                await using var stream = File.OpenRead(cachePath);
                var saved = await JsonSerializer.DeserializeAsync<Dictionary<string, List<string>>>(stream);
                if (saved is not null) {
                    foreach (var npc in Npcs) {
                        if (saved.TryGetValue(npc.Id, out var interests) && interests.Count > 0)
                            npc.Interests = interests;
                    }
                    Log("Loaded cached NPC interests.");
                    return;
                }
            }
            catch {
                // regenerate
            }
        }

        if (Ai is null || OfflineFallback) {
            await SaveInterestsAsync(cachePath);
            return;
        }

        try {
            var people = Npcs.Select(n => new InterestPerson(
                n.Name, n.OccupationName, n.Personality.ToDict())).ToList();
            var result = await Ai.GenerateInterestsAsync(people);
            foreach (var person in result) {
                var npc = Npcs.FirstOrDefault(n => n.Name == person.Name);
                if (npc is not null && person.Interests.Count > 0)
                    npc.Interests = person.Interests;
            }
            await SaveInterestsAsync(cachePath);
            Log("Generated NPC interests with local LLM.");
        }
        catch (Exception ex) {
            Log($"Interest generation failed ({ex.Message}); using fallbacks.");
            await SaveInterestsAsync(cachePath);
        }
    }

    private async Task SaveInterestsAsync(string cachePath) {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var map = Npcs.ToDictionary(n => n.Id, n => n.Interests);
        await using var stream = File.Create(cachePath);
        await JsonSerializer.SerializeAsync(stream, map, new JsonSerializerOptions { WriteIndented = true });
    }
}
