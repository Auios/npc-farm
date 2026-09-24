namespace NpcFarm.Simulation;

public sealed class TownMap {
    public const int Width = 40;
    public const int Height = 28;
    public const int TileSize = 48;

    public bool[,] Walkable { get; }
    public List<Building> Buildings { get; } = new();
    public Dictionary<string, Building> Homes { get; } = new();
    public Dictionary<JobKind, Building> Workplaces { get; } = new();

    public TownMap() {
        Walkable = new bool[Width, Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Walkable[x, y] = true;

        // Border trees / water impassable rim.
        for (int x = 0; x < Width; x++) {
            Walkable[x, 0] = false;
            Walkable[x, Height - 1] = false;
        }
        for (int y = 0; y < Height; y++) {
            Walkable[0, y] = false;
            Walkable[Width - 1, y] = false;
        }

        AddBuilding(new Building { Id = "square", Name = "Town Square", Kind = BuildingKind.Square, Origin = new(17, 12), Width = 6, Height = 5 });
        AddBuilding(new Building { Id = "well", Name = "Well", Kind = BuildingKind.Well, Origin = new(19, 14), Width = 2, Height = 2 });
        AddBuilding(new Building { Id = "market", Name = "Market", Kind = BuildingKind.Market, Origin = new(25, 10), Width = 5, Height = 4 });
        AddBuilding(new Building { Id = "tavern", Name = "Tavern", Kind = BuildingKind.Tavern, Origin = new(10, 10), Width = 5, Height = 4 });
        AddBuilding(new Building { Id = "farm", Name = "Farm", Kind = BuildingKind.Farm, Origin = new(3, 4), Width = 6, Height = 5 });
        AddBuilding(new Building { Id = "bakery", Name = "Bakery", Kind = BuildingKind.Bakery, Origin = new(30, 5), Width = 4, Height = 3 });
        AddBuilding(new Building { Id = "mill", Name = "Mill", Kind = BuildingKind.Mill, Origin = new(32, 18), Width = 4, Height = 4 });
        AddBuilding(new Building { Id = "forge", Name = "Forge", Kind = BuildingKind.Forge, Origin = new(5, 18), Width = 4, Height = 3 });
        AddBuilding(new Building { Id = "guard", Name = "Guard Post", Kind = BuildingKind.GuardPost, Origin = new(18, 3), Width = 3, Height = 3 });
        AddBuilding(new Building { Id = "herbs", Name = "Herb Shop", Kind = BuildingKind.HerbShop, Origin = new(28, 20), Width = 4, Height = 3 });
        AddBuilding(new Building { Id = "computer", Name = "Town Computer", Kind = BuildingKind.Computer, Origin = new(15, 11), Width = 2, Height = 2 });

        Workplaces[JobKind.Farmer] = Buildings.First(b => b.Id == "farm");
        Workplaces[JobKind.Baker] = Buildings.First(b => b.Id == "bakery");
        Workplaces[JobKind.Miller] = Buildings.First(b => b.Id == "mill");
        Workplaces[JobKind.Merchant] = Buildings.First(b => b.Id == "market");
        Workplaces[JobKind.Innkeeper] = Buildings.First(b => b.Id == "tavern");
        Workplaces[JobKind.Blacksmith] = Buildings.First(b => b.Id == "forge");
        Workplaces[JobKind.Guard] = Buildings.First(b => b.Id == "guard");
        Workplaces[JobKind.Herbalist] = Buildings.First(b => b.Id == "herbs");

        // Homes along bottom and left.
        var homeSpots = new (int x, int y)[]
        {
            (4, 22), (8, 22), (12, 22), (16, 22), (20, 22), (24, 22), (28, 22),
            (4, 25), (8, 25), (12, 25), (16, 25), (20, 25), (24, 25), (28, 25),
            (34, 10), (34, 14)
        };
        for (int i = 0; i < homeSpots.Length; i++) {
            var (x, y) = homeSpots[i];
            var home = new Building {
                Id = $"home_{i}",
                Name = $"Home {i + 1}",
                Kind = BuildingKind.Home,
                Origin = new(x, y),
                Width = 3,
                Height = 2
            };
            AddBuilding(home);
            Homes[home.Id] = home;
        }

        // Mark building interiors non-walkable except doors and square/well/market areas.
        foreach (var b in Buildings) {
            if (b.Kind is BuildingKind.Square or BuildingKind.Well or BuildingKind.Market or BuildingKind.Farm or BuildingKind.Computer)
                continue;
            for (int y = b.Origin.Y; y < b.Origin.Y + b.Height; y++)
                for (int x = b.Origin.X; x < b.Origin.X + b.Width; x++) {
                    if (InBounds(x, y))
                        Walkable[x, y] = false;
                }
            var door = b.Door;
            if (InBounds(door.X, door.Y))
                Walkable[door.X, door.Y] = true;
            var approach = new TilePos(door.X, Math.Min(Height - 2, door.Y + 1));
            if (InBounds(approach.X, approach.Y))
                Walkable[approach.X, approach.Y] = true;
        }
    }

    private void AddBuilding(Building b) {
        Buildings.Add(b);
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public bool IsWalkable(TilePos p) => InBounds(p.X, p.Y) && Walkable[p.X, p.Y];

    public TilePos ClampWalkable(TilePos p) {
        if (IsWalkable(p)) return p;
        for (int r = 1; r < 8; r++) {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++) {
                    var c = new TilePos(p.X + dx, p.Y + dy);
                    if (IsWalkable(c)) return c;
                }
        }
        return new TilePos(Width / 2, Height / 2);
    }

    public Building WorkplaceFor(JobKind job) => Workplaces[job];
}
