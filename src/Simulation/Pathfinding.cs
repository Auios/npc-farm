namespace NpcFarm.Simulation;

public static class Pathfinding {
    private static readonly TilePos[] Dirs =
    [
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1)
    ];

    public static IReadOnlyList<TilePos> CardinalNeighbors => Dirs;

    public static List<TilePos> FindPath(
        TownMap map,
        TilePos start,
        TilePos goal,
        HashSet<TilePos>? blocked = null) {
        start = map.ClampWalkable(start);
        goal = map.ClampWalkable(goal);
        if (start.Equals(goal))
            return [start];

        var open = new PriorityQueue<TilePos, int>();
        var cameFrom = new Dictionary<TilePos, TilePos>();
        var gScore = new Dictionary<TilePos, int> { [start] = 0 };
        open.Enqueue(start, start.Manhattan(goal));

        while (open.TryDequeue(out var current, out _)) {
            if (current.Equals(goal))
                return Reconstruct(cameFrom, current);

            int currentG = gScore[current];
            foreach (var d in Dirs) {
                var next = new TilePos(current.X + d.X, current.Y + d.Y);
                if (!map.IsWalkable(next)) continue;
                // Start/goal may be temporarily listed occupied; still allow leaving/arriving.
                if (blocked is not null
                    && blocked.Contains(next)
                    && !next.Equals(start)
                    && !next.Equals(goal))
                    continue;

                int tentative = currentG + 1;
                if (gScore.TryGetValue(next, out int existing) && tentative >= existing)
                    continue;
                cameFrom[next] = current;
                gScore[next] = tentative;
                open.Enqueue(next, tentative + next.Manhattan(goal));
            }
        }

        return [start];
    }

    private static List<TilePos> Reconstruct(Dictionary<TilePos, TilePos> cameFrom, TilePos current) {
        var path = new List<TilePos> { current };
        while (cameFrom.TryGetValue(current, out var prev)) {
            current = prev;
            path.Add(current);
        }
        path.Reverse();
        return path;
    }
}
