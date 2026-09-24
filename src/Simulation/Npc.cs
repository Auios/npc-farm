namespace NpcFarm.Simulation;

public sealed class Npc {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JobKind Job { get; init; }
    public required string HomeId { get; init; }
    public required Personality Personality { get; init; }
    public Needs Needs { get; } = new();
    public int Gold { get; set; } = 25;
    public Inventory Inventory { get; } = new();
    public List<string> Interests { get; set; } = new();
    public MemoryRing Memories { get; } = new();
    public Dictionary<string, float> Affinity { get; } = new();

    public TilePos Tile { get; set; }
    public float DrawX { get; set; }
    public float DrawY { get; set; }

    public NpcAction? CurrentAction { get; set; }
    public string? CurrentTargetNpcId { get; set; }
    public string? PendingItemId { get; set; }
    public int PendingGold { get; set; }
    public List<TilePos> Path { get; set; } = new();
    public int PathIndex { get; set; }
    public float ActionRemainingHours { get; set; }
    public bool DecisionPending { get; set; }
    public DecisionResult? LastDecision { get; set; }
    public string? LastSpokenLine { get; set; }
    public float TalkCooldownHours { get; set; }
    /// <summary>Recent work/production notes to report to the overseer (not a ledger of owed gold).</summary>
    public List<string> RecentReports { get; } = new();
    public PlayerContactReason ContactReason { get; set; } = PlayerContactReason.JustTalk;
    public InterruptedPlan? Interrupted { get; set; }
    public bool WaitingOnPlayer { get; set; }
    public string? InformTargetNpcId { get; set; }

    public string OccupationName => Job.ToString().ToLowerInvariant();

    public void AddReport(string report) {
        if (string.IsNullOrWhiteSpace(report)) return;
        RecentReports.Add(report.Trim());
        while (RecentReports.Count > 6)
            RecentReports.RemoveAt(0);
    }

    public float GetAffinity(string otherId) =>
        Affinity.TryGetValue(otherId, out float v) ? v : 0f;

    public void AdjustAffinity(string otherId, float delta) {
        float cur = GetAffinity(otherId);
        Affinity[otherId] = Math.Clamp(cur + delta, -1f, 1f);
    }

    public void SetPath(List<TilePos> path) {
        Path = path;
        PathIndex = path.Count > 0 ? 0 : -1;
        if (path.Count > 0) {
            Tile = path[0];
            DrawX = Tile.X;
            DrawY = Tile.Y;
        }
    }

    public bool IsMoving => Path.Count > 0 && PathIndex >= 0 && PathIndex < Path.Count - 1;

    public TilePos? NextPathTile =>
        IsMoving ? Path[PathIndex + 1] : null;

    /// <summary>
    /// Advance toward the next path tile by up to <paramref name="tilesToMove"/>.
    /// Returns true when the whole path is finished.
    /// </summary>
    public bool AdvanceAlongPath(float tilesToMove) {
        if (!IsMoving) return false;
        float remaining = tilesToMove;
        while (remaining > 0.0001f && PathIndex < Path.Count - 1) {
            var next = Path[PathIndex + 1];
            float dx = next.X - DrawX;
            float dy = next.Y - DrawY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= remaining) {
                DrawX = next.X;
                DrawY = next.Y;
                Tile = next;
                PathIndex++;
                remaining -= dist;
            }
            else {
                DrawX += dx / dist * remaining;
                DrawY += dy / dist * remaining;
                remaining = 0;
            }
        }
        return !IsMoving;
    }

    /// <summary>Advance at most onto the immediate next tile (used with occupancy checks).</summary>
    public bool AdvanceTowardNextTile(float tilesToMove) {
        if (!IsMoving) return false;
        var next = Path[PathIndex + 1];
        float dx = next.X - DrawX;
        float dy = next.Y - DrawY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist <= tilesToMove) {
            DrawX = next.X;
            DrawY = next.Y;
            Tile = next;
            PathIndex++;
            return !IsMoving;
        }

        DrawX += dx / dist * tilesToMove;
        DrawY += dy / dist * tilesToMove;
        return false;
    }
}
