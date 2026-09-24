using NpcFarm.Ai;
using NpcFarm.Rendering;
using NpcFarm.Simulation;

namespace NpcFarm;

public static class Program {
    // Sync entry point so Raylib/GLFW runs on the macOS main thread.
    // async Main resumes after await on a thread-pool thread, which breaks Cocoa windows.
    public static int Main(string[] args) {
        bool headless = args.Contains("--headless");
        bool offline = args.Contains("--offline");
        float hours = 3f;
        for (int i = 0; i < args.Length - 1; i++) {
            if (args[i] == "--hours" && float.TryParse(args[i + 1], out float h))
                hours = h;
        }

        string repoRoot = FindRepoRoot();
        string dataDir = Path.Combine(repoRoot, "data");
        Directory.CreateDirectory(dataDir);
        string interestsCache = Path.Combine(dataDir, "interests.json");

        var world = new World(seed: 42);
        AiServerProcess? server = null;
        AiClient? client = null;

        try {
            if (!offline) {
                Console.WriteLine("Starting local AI server (Laya + Qwen)…");
                server = new AiServerProcess(8765);
                server.Start(repoRoot);
                client = new AiClient(server.BaseUrl);
                client.WaitUntilReadyAsync(TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
                world.Ai = client;
                world.OfflineFallback = false;
                Console.WriteLine("AI server ready.");
            }
            else {
                world.OfflineFallback = true;
                Console.WriteLine("Running offline (heuristic decisions, canned dialogue).");
            }

            world.EnsureInterestsAsync(interestsCache).GetAwaiter().GetResult();

            if (headless)
                return RunHeadlessAsync(world, hours).GetAwaiter().GetResult();

            Console.WriteLine("Opening game window…");
            var app = new GameApp(world);
            app.Run();
            return 0;
        }
        catch (Exception ex) {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally {
            client?.Dispose();
            server?.Dispose();
        }
    }

    private static async Task<int> RunHeadlessAsync(World world, float hours) {
        Console.WriteLine($"Headless simulate {hours:0.##} in-game hours…");
        world.Clock.SetSpeed(8f);
        float startHours = world.Clock.TotalMinutes / 60f;

        foreach (var npc in world.Npcs)
            world.RequestDecision(npc);

        float elapsed = 0f;
        while (elapsed < hours) {
            world.Tick(0.25f);
            elapsed = world.Clock.TotalMinutes / 60f - startHours;
            await Task.Delay(15);
        }

        for (int i = 0; i < 400; i++) {
            await Task.Delay(50);
            if (world.Npcs.All(n => !n.DecisionPending) && i > 60)
                break;
        }

        int decisions = world.Npcs.Count(n => n.LastDecision is not null);
        int talks = world.Events.Count(e => e.Text.Contains('→'));
        int workers = world.Events.Count(e => e.Text.Contains("work", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"Done. Time={world.Clock.FormatTime()} elapsedGameHours={elapsed:0.00} decisions={decisions} talkEvents={talks} workEvents={workers}");
        Console.WriteLine("Recent events:");
        foreach (var ev in world.Events.TakeLast(25))
            Console.WriteLine($"  {ev.Text}");

        foreach (var npc in world.Npcs.Take(4)) {
            Console.WriteLine($"{npc.Name}: gold={npc.Gold} hunger={npc.Needs.Hunger:0.00} energy={npc.Needs.Energy:0.00} action={npc.CurrentAction} interests=[{string.Join(", ", npc.Interests)}]");
            if (npc.LastDecision is not null)
                Console.WriteLine($"  last={npc.LastDecision.Choice} probs={string.Join(", ", npc.LastDecision.Probabilities.Select(kv => $"{kv.Key}:{kv.Value:0.00}"))}");
        }

        return decisions > 0 ? 0 : 2;
    }

    private static string FindRepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) {
            if (File.Exists(Path.Combine(dir.FullName, "ai", "server.py")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
