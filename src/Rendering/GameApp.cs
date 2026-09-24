using System.Numerics;
using NpcFarm.Simulation;
using Raylib_cs;

namespace NpcFarm.Rendering;

public sealed class GameApp {
    private const float MinZoom = 0.45f;
    private const float MaxZoom = 8f;

    private readonly World _world;
    private Camera2D _camera;
    private readonly int _screenW;
    private readonly int _screenH;
    private readonly List<ConversationPanel> _panels = new();
    private string? _scrollDragConvId;
    private Font _font;
    private bool _showSpeakRoster;
    private string _chatDraft = "";
    private bool _chatInputFocused;
    private const float FontSpacing = 0.5f;
    private const string FontRelativePath = "assets/fonts/JetBrainsMono-Regular.ttf";

    public GameApp(World world, int screenW = 1280, int screenH = 800) {
        _world = world;
        _screenW = screenW;
        _screenH = screenH;
        _camera = new Camera2D {
            Target = new Vector2(TownMap.Width * TownMap.TileSize / 2f, TownMap.Height * TownMap.TileSize / 2f),
            Offset = new Vector2(screenW / 2f, screenH / 2f),
            Rotation = 0,
            Zoom = 1.1f
        };
    }

    public void Run() {
        // Init first so monitor queries work, then go fullscreen.
        Raylib.SetConfigFlags(ConfigFlags.HighDpiWindow);
        Raylib.InitWindow(_screenW, _screenH, "NPC Farm — Observer Town");
        int monitor = Raylib.GetCurrentMonitor();
        int width = Raylib.GetMonitorWidth(monitor);
        int height = Raylib.GetMonitorHeight(monitor);
        if (width > 0 && height > 0)
            Raylib.SetWindowSize(width, height);
        Raylib.ToggleFullscreen();
        Raylib.SetTargetFPS(60);
        _font = LoadUiFont();
        SyncCameraOffset();
        Console.WriteLine($"Window ready ({Raylib.GetScreenWidth()}x{Raylib.GetScreenHeight()}, fullscreen). Esc to quit · F/F11 toggles fullscreen.");

        while (!Raylib.WindowShouldClose()) {
            if (Raylib.IsWindowResized())
                SyncCameraOffset();

            float dt = Raylib.GetFrameTime();
            HandleInput(dt);
            _world.Tick(dt);
            Draw();
        }

        Raylib.UnloadFont(_font);
        Raylib.CloseWindow();
    }

    private static Font LoadUiFont() {
        string path = Path.Combine(AppContext.BaseDirectory, FontRelativePath);
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", FontRelativePath));
        if (!File.Exists(path))
            throw new FileNotFoundException($"UI font not found: {FontRelativePath}");

        // Cover ASCII + Latin-1 + common punctuation LLMs emit (curly quotes, dashes, ellipsis).
        var codepoints = new List<int>();
        for (int cp = 32; cp <= 126; cp++)
            codepoints.Add(cp);
        for (int cp = 160; cp <= 255; cp++)
            codepoints.Add(cp);
        for (int cp = 0x2000; cp <= 0x206F; cp++)
            codepoints.Add(cp); // general punctuation
        for (int cp = 0x20A0; cp <= 0x20CF; cp++)
            codepoints.Add(cp); // currency
        codepoints.Add(0x00B7); // middle dot used in HUD hints

        var font = Raylib.LoadFontEx(path, 32, codepoints.ToArray(), codepoints.Count);
        Raylib.SetTextureFilter(font.Texture, TextureFilter.Bilinear);
        return font;
    }

    private float MeasureUi(string text, float size) =>
        Raylib.MeasureTextEx(_font, text, size, FontSpacing).X;

    private void DrawUi(string text, float x, float y, float size, Color color) =>
        Raylib.DrawTextEx(_font, text, new Vector2(x, y), size, FontSpacing, color);

    private void SyncCameraOffset() {
        _camera.Offset = new Vector2(Raylib.GetScreenWidth() / 2f, Raylib.GetScreenHeight() / 2f);
    }

    private void HandleInput(float dt) {
        if (Raylib.IsKeyPressed(KeyboardKey.Space) && !_world.Player.TerminalOpen)
            _world.Clock.IsPaused = !_world.Clock.IsPaused;
        if (!_world.Player.TerminalOpen) {
            if (Raylib.IsKeyPressed(KeyboardKey.One))
                _world.Clock.SetSpeed(1f);
            if (Raylib.IsKeyPressed(KeyboardKey.Two))
                _world.Clock.SetSpeed(3f);
            if (Raylib.IsKeyPressed(KeyboardKey.Three))
                _world.Clock.SetSpeed(8f);
            if (Raylib.IsKeyPressed(KeyboardKey.Tab))
                _world.Clock.CycleSpeed();
        }

        if (HandleTerminalClicks())
            return;

        if (_world.Player.TerminalOpen)
            HandleChatTyping();

        float pan = 280f * dt / Math.Max(0.4f, _camera.Zoom);
        if (Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up))
            _camera.Target.Y -= pan;
        if (Raylib.IsKeyDown(KeyboardKey.S) || Raylib.IsKeyDown(KeyboardKey.Down))
            _camera.Target.Y += pan;
        if (Raylib.IsKeyDown(KeyboardKey.A) || Raylib.IsKeyDown(KeyboardKey.Left))
            _camera.Target.X -= pan;
        if (Raylib.IsKeyDown(KeyboardKey.D) || Raylib.IsKeyDown(KeyboardKey.Right))
            _camera.Target.X += pan;

        float wheel = Raylib.GetMouseWheelMove();
        var mouseScreen = Raylib.GetMousePosition();
        var mouseWorld = Raylib.GetScreenToWorld2D(mouseScreen, _camera);
        var hoveredPanel = _panels.LastOrDefault(p => Raylib.CheckCollisionPointRec(mouseWorld, p.Rect));
        if (hoveredPanel is not null && Math.Abs(wheel) > 0.01f) {
            hoveredPanel.Conversation.ScrollFromBottom = Math.Clamp(
                hoveredPanel.Conversation.ScrollFromBottom + (int)MathF.Round(wheel * 3f),
                0,
                hoveredPanel.Conversation.MaxScroll());
        }
        else if (hoveredPanel is null && Math.Abs(wheel) > 0.01f) {
            SyncCameraOffset();
            float oldZoom = _camera.Zoom;
            float newZoom = Math.Clamp(oldZoom * MathF.Pow(1.15f, wheel), MinZoom, MaxZoom);
            if (Math.Abs(newZoom - oldZoom) > 0.0001f) {
                Vector2 worldUnderMouse = Raylib.GetScreenToWorld2D(mouseScreen, _camera);
                _camera.Zoom = newZoom;
                Vector2 worldAfter = Raylib.GetScreenToWorld2D(mouseScreen, _camera);
                _camera.Target += worldUnderMouse - worldAfter;
            }
        }

        if (Raylib.IsMouseButtonPressed(MouseButton.Left)) {
            _scrollDragConvId = null;
            foreach (var panel in _panels) {
                if (Raylib.CheckCollisionPointRec(mouseWorld, panel.ScrollTrack)) {
                    _scrollDragConvId = panel.Conversation.Id;
                    ApplyScrollFromThumb(panel, mouseWorld.Y);
                    break;
                }
            }

            if (_scrollDragConvId is null && hoveredPanel is null) {
                float mx = mouseWorld.X / TownMap.TileSize;
                float my = mouseWorld.Y / TownMap.TileSize;
                Npc? hit = null;
                float best = 1.2f;
                foreach (var npc in _world.Npcs) {
                    float dx = npc.DrawX + 0.5f - mx;
                    float dy = npc.DrawY + 0.5f - my;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d < best) {
                        best = d;
                        hit = npc;
                    }
                }
                _world.SelectedNpcId = hit?.Id;
            }
        }

        if (_scrollDragConvId is not null && Raylib.IsMouseButtonDown(MouseButton.Left)) {
            var panel = _panels.FirstOrDefault(p => p.Conversation.Id == _scrollDragConvId);
            if (panel is not null)
                ApplyScrollFromThumb(panel, mouseWorld.Y);
        }

        if (Raylib.IsMouseButtonReleased(MouseButton.Left))
            _scrollDragConvId = null;

        if (Raylib.IsKeyPressed(KeyboardKey.F11) || Raylib.IsKeyPressed(KeyboardKey.F)) {
            Raylib.ToggleFullscreen();
            SyncCameraOffset();
        }
    }

    private static void ApplyScrollFromThumb(ConversationPanel panel, float mouseY) {
        var conv = panel.Conversation;
        int maxScroll = conv.MaxScroll();
        if (maxScroll <= 0) {
            conv.ScrollFromBottom = 0;
            return;
        }

        float trackTop = panel.ScrollTrack.Y;
        float trackH = panel.ScrollTrack.Height;
        float t = Math.Clamp((mouseY - trackTop) / Math.Max(1f, trackH), 0f, 1f);
        // Top of track = oldest (max scroll), bottom = newest (0).
        conv.ScrollFromBottom = (int)MathF.Round((1f - t) * maxScroll);
        conv.ClampScroll();
    }

    private void Draw() {
        Raylib.BeginDrawing();
        var sky = SkyColor(_world.Clock.HourOfDay);
        Raylib.ClearBackground(sky);

        Raylib.BeginMode2D(_camera);
        DrawMap();
        DrawNpcs();
        DrawConversations();
        Raylib.EndMode2D();

        DrawHud();
        DrawInspector();
        DrawPlayerTerminal();
        DrawNotification();
        Raylib.EndDrawing();
    }

    private static Color SkyColor(float hour) {
        // Blend day blue → dusk → night.
        if (hour >= 7 && hour < 18)
            return new Color(120, 170, 110, 255);
        if (hour >= 18 && hour < 20)
            return new Color(70, 90, 100, 255);
        if (hour >= 5 && hour < 7)
            return new Color(90, 110, 95, 255);
        return new Color(25, 30, 45, 255);
    }

    private void DrawMap() {
        int ts = TownMap.TileSize;
        for (int y = 0; y < TownMap.Height; y++)
            for (int x = 0; x < TownMap.Width; x++) {
                var color = _world.Map.Walkable[x, y]
                    ? new Color(90, 140, 80, 255)
                    : new Color(45, 70, 50, 255);
                if (!_world.Map.Walkable[x, y] && (x == 0 || y == 0 || x == TownMap.Width - 1 || y == TownMap.Height - 1))
                    color = new Color(30, 55, 35, 255);
                Raylib.DrawRectangle(x * ts, y * ts, ts - 1, ts - 1, color);
            }

        foreach (var b in _world.Map.Buildings) {
            var color = BuildingColor(b.Kind);
            Raylib.DrawRectangle(b.Origin.X * ts, b.Origin.Y * ts, b.Width * ts, b.Height * ts, color);
            if (_world.Clock.IsNight && b.Kind == BuildingKind.Home) {
                Raylib.DrawRectangle(b.Origin.X * ts + 6, b.Origin.Y * ts + 6, 6, 6, new Color(255, 220, 120, 220));
            }
            DrawUi(b.Name, b.Origin.X * ts + 4, b.Origin.Y * ts + 4, 14, Color.Black);
        }
    }

    private static Color BuildingColor(BuildingKind kind) => kind switch {
        BuildingKind.Home => new Color(180, 140, 100, 255),
        BuildingKind.Farm => new Color(150, 170, 70, 255),
        BuildingKind.Bakery => new Color(210, 170, 120, 255),
        BuildingKind.Mill => new Color(160, 160, 170, 255),
        BuildingKind.Market => new Color(220, 180, 80, 255),
        BuildingKind.Tavern => new Color(160, 90, 70, 255),
        BuildingKind.Well => new Color(100, 140, 180, 255),
        BuildingKind.Square => new Color(170, 160, 130, 255),
        BuildingKind.Forge => new Color(120, 100, 100, 255),
        BuildingKind.GuardPost => new Color(100, 110, 140, 255),
        BuildingKind.HerbShop => new Color(100, 160, 100, 255),
        BuildingKind.Computer => new Color(60, 90, 140, 255),
        _ => Color.Gray
    };

    private void DrawNpcs() {
        int ts = TownMap.TileSize;
        const int nameFont = 12;
        foreach (var npc in _world.Npcs) {
            int cx = (int)((npc.DrawX + 0.5f) * ts);
            int cy = (int)((npc.DrawY + 0.5f) * ts);
            var fill = NpcColor(npc);
            if (npc.Id == _world.SelectedNpcId)
                Raylib.DrawCircle(cx, cy, ts * 0.55f, Color.Yellow);
            Raylib.DrawCircle(cx, cy, ts * 0.38f, fill);

            int nameW = (int)MeasureUi(npc.Name, nameFont);
            int nameX = cx - nameW / 2;
            int nameY = cy - (int)(ts * 0.55f) - nameFont - 2;
            // Soft outline so names stay readable on light and dark tiles.
            DrawUi(npc.Name, nameX + 1, nameY + 1, nameFont, new Color(0, 0, 0, 180));
            DrawUi(npc.Name, nameX, nameY, nameFont, Color.RayWhite);
        }
    }

    private static Color NpcColor(Npc npc) => npc.Job switch {
        JobKind.Farmer => new Color(80, 160, 70, 255),
        JobKind.Baker => new Color(220, 170, 90, 255),
        JobKind.Miller => new Color(170, 170, 190, 255),
        JobKind.Merchant => new Color(220, 140, 60, 255),
        JobKind.Innkeeper => new Color(180, 80, 70, 255),
        JobKind.Blacksmith => new Color(120, 120, 130, 255),
        JobKind.Guard => new Color(70, 90, 160, 255),
        JobKind.Herbalist => new Color(70, 150, 110, 255),
        _ => Color.SkyBlue
    };

    private sealed class ConversationPanel {
        public required Conversation Conversation { get; init; }
        public Rectangle Rect { get; init; }
        public Rectangle ScrollTrack { get; init; }
        public Rectangle ScrollThumb { get; init; }
    }

    private void DrawConversations() {
        _panels.Clear();
        int ts = TownMap.TileSize;
        // Fixed world-space size (scales with zoom along with the town).
        float panelW = ts * 4.6f;
        float headerH = 20f;
        float lineH = 12f;
        float pad = 6f;
        float scrollW = 8f;
        float gap = 10f;
        int font = 11;
        int nickFont = 11;
        float bodyH = Conversation.VisibleLineCount * lineH + pad;
        float panelH = headerH + bodyH + pad;

        var bg = new Color(18, 20, 26, 235);
        var headerBg = new Color(28, 32, 42, 255);
        var border = new Color(70, 80, 100, 255);
        var textCol = new Color(210, 215, 225, 255);
        var dim = new Color(140, 150, 165, 255);
        var trackCol = new Color(40, 44, 54, 255);
        var thumbCol = new Color(90, 100, 120, 255);
        var linkCol = new Color(120, 160, 220, 170);

        var names = _world.Npcs.ToDictionary(n => n.Id, n => n.Name);
        var layouts = new List<(Conversation Conv, float HomeX, float HomeY, float DesireX, float DesireY, float MaxCenterY, List<Vector2> Tips)>();

        foreach (var conv in _world.Conversations) {
            if (conv.Messages.Count == 0) continue;
            var participants = _world.Npcs.Where(n => conv.ParticipantIds.Contains(n.Id)).ToList();
            if (participants.Count == 0) continue;

            float ax = participants.Average(n => (n.DrawX + 0.5f) * ts);
            float ay = participants.Average(n => (n.DrawY + 0.5f) * ts);
            float highestHead = participants.Min(n => (n.DrawY + 0.5f) * ts - ts * 0.55f - 14f);
            // Panel sits fully above participant heads/names.
            float maxCenterY = highestHead - gap - panelH * 0.5f;
            float homeX = ax;
            float homeY = maxCenterY;
            var tips = participants
                .Select(n => new Vector2((n.DrawX + 0.5f) * ts, (n.DrawY + 0.5f) * ts - ts * 0.38f))
                .ToList();
            layouts.Add((conv, homeX, homeY, homeX + conv.PanelOffsetX, homeY + conv.PanelOffsetY, maxCenterY, tips));
        }

        // Resolve overlaps in world space — always push further upward / sideways, never down onto NPCs.
        for (int pass = 0; pass < 48; pass++) {
            bool moved = false;
            for (int i = 0; i < layouts.Count; i++)
                for (int j = i + 1; j < layouts.Count; j++) {
                    var a = layouts[i];
                    var b = layouts[j];
                    float aLeft = a.DesireX - panelW * 0.5f;
                    float aTop = a.DesireY - panelH * 0.5f;
                    float bLeft = b.DesireX - panelW * 0.5f;
                    float bTop = b.DesireY - panelH * 0.5f;
                    float ox = MathF.Min(aLeft + panelW, bLeft + panelW) - MathF.Max(aLeft, bLeft) + gap;
                    float oy = MathF.Min(aTop + panelH, bTop + panelH) - MathF.Max(aTop, bTop) + gap;
                    if (ox <= 0 || oy <= 0) continue;

                    moved = true;
                    if (ox <= oy * 1.2f) {
                        float half = ox * 0.5f;
                        if (a.DesireX <= b.DesireX) {
                            a.DesireX -= half;
                            b.DesireX += half;
                        }
                        else {
                            a.DesireX += half;
                            b.DesireX -= half;
                        }
                    }
                    else {
                        // Stack upward only.
                        float lift = oy;
                        if (a.DesireY <= b.DesireY)
                            a.DesireY -= lift;
                        else
                            b.DesireY -= lift;
                    }

                    a.DesireY = Math.Min(a.DesireY, a.MaxCenterY);
                    b.DesireY = Math.Min(b.DesireY, b.MaxCenterY);
                    layouts[i] = a;
                    layouts[j] = b;
                }

            for (int i = 0; i < layouts.Count; i++) {
                var it = layouts[i];
                it.DesireX += (it.HomeX - it.DesireX) * 0.03f;
                it.DesireY += (it.HomeY - it.DesireY) * 0.03f;
                it.DesireY = Math.Min(it.DesireY, it.MaxCenterY);
                layouts[i] = it;
            }

            if (!moved) break;
        }

        float dt = Math.Clamp(Raylib.GetFrameTime(), 0.001f, 0.05f);
        float blend = 1f - MathF.Exp(-8f * dt);
        float eased = blend * blend * (3f - 2f * blend);

        foreach (var layout in layouts) {
            float targetOffX = layout.DesireX - layout.HomeX;
            float targetOffY = layout.DesireY - layout.HomeY;
            layout.Conv.PanelOffsetX += (targetOffX - layout.Conv.PanelOffsetX) * eased;
            layout.Conv.PanelOffsetY += (targetOffY - layout.Conv.PanelOffsetY) * eased;

            float cx = layout.HomeX + layout.Conv.PanelOffsetX;
            float cy = Math.Min(layout.HomeY + layout.Conv.PanelOffsetY, layout.MaxCenterY);
            float left = cx - panelW * 0.5f;
            float top = cy - panelH * 0.5f;
            var rect = new Rectangle(left, top, panelW, panelH);

            // Connect panel to each participant.
            float attachX = cx;
            float attachY = top + panelH;
            foreach (var tip in layout.Tips) {
                Raylib.DrawLineEx(new Vector2(attachX, attachY), tip, 1.5f, linkCol);
                Raylib.DrawCircleV(tip, 2.5f, linkCol);
            }

            Raylib.DrawRectangleRec(rect, bg);
            Raylib.DrawRectangle((int)left, (int)top, (int)panelW, (int)headerH, headerBg);
            Raylib.DrawRectangleLinesEx(rect, 1f, border);

            string title = "# " + layout.Conv.Title(names);
            float titleMax = panelW - pad * 2;
            if (MeasureUi(title, font) > titleMax) {
                while (title.Length > 1 && MeasureUi(title + "…", font) > titleMax)
                    title = title[..^1];
                title = title.TrimEnd() + "…";
            }
            DrawUi(title, left + pad, top + 4, font, new Color(180, 200, 255, 255));

            float bodyTop = top + headerH + 3f;
            float trackX = left + panelW - scrollW - 3f;
            float trackH = bodyH - 4f;
            var track = new Rectangle(trackX, bodyTop, scrollW, trackH);
            Raylib.DrawRectangleRec(track, trackCol);

            float textWidth = panelW - scrollW - pad * 3;
            var lines = BuildConversationLines(layout.Conv, textWidth, font, nickFont);
            layout.Conv.CachedVisualLineCount = lines.Count;
            layout.Conv.ClampScroll();

            int maxScroll = layout.Conv.MaxScroll();
            float thumbH = maxScroll <= 0
                ? trackH
                : Math.Max(14f, trackH * (Conversation.VisibleLineCount / (float)Math.Max(Conversation.VisibleLineCount, lines.Count)));
            float travel = Math.Max(0f, trackH - thumbH);
            float t = maxScroll <= 0 ? 1f : 1f - layout.Conv.ScrollFromBottom / (float)maxScroll;
            var thumb = new Rectangle(track.X, track.Y + travel * t, track.Width, thumbH);
            Raylib.DrawRectangleRec(thumb, thumbCol);

            int endExclusive = lines.Count - layout.Conv.ScrollFromBottom;
            int start = Math.Max(0, endExclusive - Conversation.VisibleLineCount);
            float y = bodyTop;
            float bodyBottom = bodyTop + Conversation.VisibleLineCount * lineH;
            for (int i = start; i < endExclusive && y < bodyBottom - 0.5f; i++) {
                var line = lines[i];
                if (line.Nick is not null)
                    DrawUi(line.Nick, left + pad, y, nickFont, line.NickColor);
                DrawUi(line.Text, left + pad + line.TextOffset, y, font, textCol);
                y += lineH;
            }

            _panels.Add(new ConversationPanel {
                Conversation = layout.Conv,
                Rect = rect,
                ScrollTrack = track,
                ScrollThumb = thumb
            });
        }
    }

    private readonly record struct ChatDrawLine(string? Nick, Color NickColor, string Text, float TextOffset);

    private List<ChatDrawLine> BuildConversationLines(Conversation conv, float textWidth, float fontSize, float nickFontSize) {
        var lines = new List<ChatDrawLine>();
        foreach (var msg in conv.Messages) {
            var nickCol = NickColor(msg.SpeakerName);
            string nick = "<" + msg.SpeakerName + ">";
            float nickW = MeasureUi(nick, nickFontSize) + 5f;
            float firstWidth = Math.Max(16f, textWidth - nickW);
            float contWidth = Math.Max(16f, textWidth - nickW);
            var wrapped = WrapWords(msg.Text, firstWidth, contWidth, fontSize);
            for (int i = 0; i < wrapped.Count; i++) {
                lines.Add(new ChatDrawLine(
                    i == 0 ? nick : null,
                    nickCol,
                    wrapped[i],
                    nickW));
            }
        }
        return lines;
    }

    /// <summary>Word-wrap without breaking mid-word unless a single word exceeds the width.</summary>
    private List<string> WrapWords(string text, float firstLineWidth, float nextLineWidth, float fontSize) {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) {
            result.Add("");
            return result;
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) {
            result.Add("");
            return result;
        }

        string current = "";
        float maxW = firstLineWidth;
        foreach (var word in words) {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (MeasureUi(candidate, fontSize) <= maxW) {
                current = candidate;
                continue;
            }

            if (current.Length > 0) {
                result.Add(current);
                current = "";
                maxW = nextLineWidth;
            }

            if (MeasureUi(word, fontSize) <= maxW) {
                current = word;
                continue;
            }

            // Hard-break an overlong token.
            string chunk = "";
            foreach (char c in word) {
                string next = chunk + c;
                if (chunk.Length > 0 && MeasureUi(next, fontSize) > maxW) {
                    result.Add(chunk);
                    chunk = c.ToString();
                    maxW = nextLineWidth;
                }
                else {
                    chunk = next;
                }
            }
            current = chunk;
        }

        if (current.Length > 0 || result.Count == 0)
            result.Add(current);
        return result;
    }

    private static Color NickColor(string name) {
        int hash = 0;
        foreach (char c in name)
            hash = hash * 31 + c;
        byte r = (byte)(90 + Math.Abs(hash) % 140);
        byte g = (byte)(90 + Math.Abs(hash / 7) % 140);
        byte b = (byte)(110 + Math.Abs(hash / 13) % 120);
        return new Color((int)r, (int)g, (int)b, 255);
    }

    private void DrawHud() {
        Raylib.DrawRectangle(0, 0, Raylib.GetScreenWidth(), 36, new Color(0, 0, 0, 160));
        int apples = _world.Player.Inventory.FindByName("Apple")?.Quantity ?? 0;
        string status = $"{_world.Clock.FormatTime()}  |  speed {_world.Clock.Speed:0}x" +
                        (_world.Clock.IsPaused ? "  PAUSED" : "") +
                        (_world.Clock.IsNight ? "  NIGHT" : "  DAY") +
                        $"  |  You: {_world.Player.Gold}g · {apples} apples";
        DrawUi(status, 12, 10, 18, Color.RayWhite);
        DrawUi("WASD pan · wheel zoom · C computer panel · Space pause · 1/2/3 speed · F/F11 fullscreen · Esc quit", 12, Raylib.GetScreenHeight() - 24, 14, Color.LightGray);

        int y = 44;
        foreach (var ev in _world.Events.TakeLast(6)) {
            DrawUi(ev.Text, 12, y, 12, new Color(230, 230, 230, 220));
            y += 14;
        }
    }

    private void DrawNotification() {
        if (string.IsNullOrEmpty(_world.Player.Notification)) return;
        int w = Math.Min(720, Raylib.GetScreenWidth() - 40);
        int x = (Raylib.GetScreenWidth() - w) / 2;
        int y = 48;
        Raylib.DrawRectangle(x, y, w, 44, new Color(40, 30, 10, 230));
        Raylib.DrawRectangleLines(x, y, w, 44, new Color(255, 200, 80, 255));
        DrawUi(_world.Player.Notification, x + 14, y + 12, 18, new Color(255, 230, 160, 255));
    }

    private Rectangle TerminalPanelRect() {
        int w = 560;
        int h = 520;
        int x = 16;
        int y = Raylib.GetScreenHeight() - h - 36;
        return new Rectangle(x, y, w, h);
    }

    private void HandleChatTyping() {
        if (!_chatInputFocused && !Raylib.IsMouseButtonPressed(MouseButton.Left)) {
            // Still accept typing when terminal is open — focus on first key.
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter)) {
            if (!string.IsNullOrWhiteSpace(_chatDraft)) {
                _world.SendPlayerChat(_chatDraft);
                _chatDraft = "";
            }
            _chatInputFocused = true;
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && _chatDraft.Length > 0) {
            _chatDraft = _chatDraft[..^1];
            _chatInputFocused = true;
        }

        int ch;
        while ((ch = Raylib.GetCharPressed()) > 0) {
            if (ch >= 32 && ch < 127 && _chatDraft.Length < 180)
                _chatDraft += (char)ch;
            _chatInputFocused = true;
        }
    }

    private void DrawPlayerTerminal() {
        bool show = _world.Player.TerminalOpen || _showSpeakRoster;
        if (!show) {
            int dockW = 220;
            int dockH = 56;
            int dx = 16;
            int dy = Raylib.GetScreenHeight() - dockH - 36;
            Raylib.DrawRectangle(dx, dy, dockW, dockH, new Color(12, 16, 28, 220));
            Raylib.DrawRectangleLines(dx, dy, dockW, dockH, new Color(90, 120, 180, 255));
            DrawUi("Town Computer", dx + 10, dy + 8, 16, new Color(180, 200, 255, 255));
            DrawUi("[C] open · request speak", dx + 10, dy + 30, 12, Color.LightGray);
            return;
        }

        var panel = TerminalPanelRect();
        Raylib.DrawRectangleRec(panel, new Color(12, 16, 28, 235));
        Raylib.DrawRectangleLinesEx(panel, 2f, new Color(90, 120, 180, 255));
        float x = panel.X;
        float y = panel.Y;
        DrawUi("Town Computer — Overseer Terminal", x + 12, y + 10, 18, new Color(180, 200, 255, 255));
        DrawUi($"Gold {_world.Player.Gold} · Apples {_world.Player.Inventory.FindByName("Apple")?.Quantity ?? 0}", x + 12, y + 34, 13, Color.LightGray);

        if (_showSpeakRoster && !_world.Player.TerminalOpen) {
            DrawUi("Who should come speak with you?", x + 12, y + 60, 14, Color.RayWhite);
            float ay = y + 88;
            foreach (var opt in _world.BuildSpeakTargetActions().Take(12)) {
                var btn = new Rectangle(x + 12, ay, panel.Width - 24, 26);
                Raylib.DrawRectangleRec(btn, new Color(28, 36, 52, 255));
                DrawUi(opt.Label, btn.X + 8, btn.Y + 5, 13, Color.RayWhite);
                ay += 30;
            }
            return;
        }

        if (!_world.Player.TerminalOpen) return;

        var npc = _world.Npcs.FirstOrDefault(n => n.Id == _world.Player.ActiveNpcId);
        string link = npc is null
            ? "Connected"
            : $"Linked: {npc.Name} ({npc.OccupationName}) · {npc.ContactReason}";
        DrawUi(link, x + 12, y + 56, 14, Color.RayWhite);

        float chatTop = y + 78;
        float chatH = 200;
        float chatPad = 6f;
        float chatMaxW = panel.Width - 40;
        float lineH = 15f;
        Raylib.DrawRectangle((int)(x + 12), (int)chatTop, (int)(panel.Width - 24), (int)chatH, new Color(8, 10, 16, 255));

        var wrappedLines = new List<(string Text, Color Color)>();
        foreach (var msg in _world.Player.Chat) {
            string prefix = msg.Speaker == "system" ? "* "
                : msg.Speaker == _world.Player.Name ? $"<{msg.Speaker}> "
                : $"<{msg.Speaker}> ";
            var col = msg.Speaker == "system" ? new Color(160, 170, 190, 255)
                : msg.Speaker == _world.Player.Name ? new Color(180, 220, 160, 255)
                : new Color(200, 210, 255, 255);
            float prefixW = MeasureUi(prefix, 12);
            float firstW = Math.Max(24f, chatMaxW - prefixW);
            var bodyLines = WrapWords(msg.Text, firstW, chatMaxW, 12);
            for (int i = 0; i < bodyLines.Count; i++) {
                string text = i == 0 ? prefix + bodyLines[i] : bodyLines[i];
                wrappedLines.Add((text, col));
            }
        }

        int maxVisible = Math.Max(1, (int)((chatH - chatPad * 2) / lineH));
        var visible = wrappedLines.Count <= maxVisible
            ? wrappedLines
            : wrappedLines.Skip(wrappedLines.Count - maxVisible).ToList();
        float cy = chatTop + chatPad;
        foreach (var (text, col) in visible) {
            DrawUi(text, x + 18, cy, 12, col);
            cy += lineH;
        }

        // Chat input
        float inputY = chatTop + chatH + 8;
        var inputRect = new Rectangle(x + 12, inputY, panel.Width - 100, 28);
        Raylib.DrawRectangleRec(inputRect, new Color(20, 24, 36, 255));
        Raylib.DrawRectangleLinesEx(inputRect, 1f, _chatInputFocused ? new Color(120, 180, 255, 255) : new Color(70, 80, 100, 255));
        string draftShow = string.IsNullOrEmpty(_chatDraft) ? "Type a message… (Enter to send)" : _chatDraft + (_chatInputFocused ? "|" : "");
        if (MeasureUi(draftShow, 13) > inputRect.Width - 16) {
            while (draftShow.Length > 1 && MeasureUi(draftShow, 13) > inputRect.Width - 16)
                draftShow = draftShow[1..];
        }
        DrawUi(draftShow, inputRect.X + 8, inputRect.Y + 6, 13,
            string.IsNullOrEmpty(_chatDraft) ? new Color(120, 130, 150, 255) : Color.RayWhite);

        var sendBtn = new Rectangle(x + panel.Width - 80, inputY, 68, 28);
        Raylib.DrawRectangleRec(sendBtn, new Color(40, 90, 140, 255));
        DrawUi("Send", sendBtn.X + 16, sendBtn.Y + 6, 14, Color.RayWhite);

        DrawUi("Actions", x + 12, inputY + 40, 14, new Color(180, 200, 255, 255));
        float ay2 = inputY + 62;
        var actions = _showSpeakRoster
            ? _world.BuildSpeakTargetActions().Take(6).ToList()
            : _world.BuildPlayerActions().Where(a => a.Id != "hang_up").ToList();
        foreach (var opt in actions) {
            var btn = new Rectangle(x + 12, ay2, panel.Width - 24, 24);
            Raylib.DrawRectangleRec(btn, new Color(28, 36, 52, 255));
            DrawUi(opt.Label, btn.X + 8, btn.Y + 4, 13, Color.RayWhite);
            ay2 += 28;
            if (ay2 > panel.Y + panel.Height - 56) break;
        }

        // Always-visible end conversation
        var endBtn = new Rectangle(x + 12, panel.Y + panel.Height - 40, panel.Width - 24, 28);
        Raylib.DrawRectangleRec(endBtn, new Color(120, 50, 50, 255));
        DrawUi("End conversation", endBtn.X + 12, endBtn.Y + 5, 15, Color.RayWhite);
    }

    private bool HandleTerminalClicks() {
        if (Raylib.IsKeyPressed(KeyboardKey.C) && !_world.Player.TerminalOpen) {
            _showSpeakRoster = !_showSpeakRoster;
            return true;
        }

        if (!Raylib.IsMouseButtonPressed(MouseButton.Left))
            return false;

        var mouse = Raylib.GetMousePosition();
        int dockW = 220;
        int dockH = 56;
        int dx = 16;
        int dy = Raylib.GetScreenHeight() - dockH - 36;
        if (!_world.Player.TerminalOpen && !_showSpeakRoster
            && Raylib.CheckCollisionPointRec(mouse, new Rectangle(dx, dy, dockW, dockH))) {
            _showSpeakRoster = true;
            return true;
        }

        if (!_world.Player.TerminalOpen && !_showSpeakRoster)
            return false;

        var panel = TerminalPanelRect();
        if (!Raylib.CheckCollisionPointRec(mouse, panel)) {
            _chatInputFocused = false;
            return false;
        }

        if (_showSpeakRoster && !_world.Player.TerminalOpen) {
            float ay = panel.Y + 88;
            foreach (var opt in _world.BuildSpeakTargetActions().Take(12)) {
                var btn = new Rectangle(panel.X + 12, ay, panel.Width - 24, 26);
                if (Raylib.CheckCollisionPointRec(mouse, btn)) {
                    _world.ApplyPlayerAction(opt.Id);
                    _showSpeakRoster = false;
                    return true;
                }
                ay += 30;
            }
            return true;
        }

        if (!_world.Player.TerminalOpen)
            return false;

        float chatTop = panel.Y + 78;
        float chatH = 200;
        float inputY = chatTop + chatH + 8;
        var inputRect = new Rectangle(panel.X + 12, inputY, panel.Width - 100, 28);
        var sendBtn = new Rectangle(panel.X + panel.Width - 80, inputY, 68, 28);
        var endBtn = new Rectangle(panel.X + 12, panel.Y + panel.Height - 40, panel.Width - 24, 28);

        if (Raylib.CheckCollisionPointRec(mouse, endBtn)) {
            _world.ApplyPlayerAction("hang_up");
            _chatDraft = "";
            _chatInputFocused = false;
            _showSpeakRoster = false;
            return true;
        }

        if (Raylib.CheckCollisionPointRec(mouse, sendBtn)) {
            if (!string.IsNullOrWhiteSpace(_chatDraft)) {
                _world.SendPlayerChat(_chatDraft);
                _chatDraft = "";
            }
            _chatInputFocused = true;
            return true;
        }

        if (Raylib.CheckCollisionPointRec(mouse, inputRect)) {
            _chatInputFocused = true;
            return true;
        }

        _chatInputFocused = false;

        if (_showSpeakRoster) {
            float ay2 = inputY + 62;
            foreach (var opt in _world.BuildSpeakTargetActions().Take(6)) {
                var btn = new Rectangle(panel.X + 12, ay2, panel.Width - 24, 24);
                if (Raylib.CheckCollisionPointRec(mouse, btn)) {
                    _world.ApplyPlayerAction(opt.Id);
                    _showSpeakRoster = false;
                    return true;
                }
                ay2 += 28;
            }
            return true;
        }

        float ay3 = inputY + 62;
        foreach (var opt in _world.BuildPlayerActions().Where(a => a.Id != "hang_up")) {
            var btn = new Rectangle(panel.X + 12, ay3, panel.Width - 24, 24);
            if (Raylib.CheckCollisionPointRec(mouse, btn)) {
                if (opt.Id == "request_other") {
                    _showSpeakRoster = true;
                    return true;
                }
                _world.ApplyPlayerAction(opt.Id);
                return true;
            }
            ay3 += 28;
            if (ay3 > panel.Y + panel.Height - 56) break;
        }

        return true;
    }

    private void DrawInspector() {
        var npc = _world.SelectedNpc;
        if (npc is null) return;
        int w = 300;
        int x = Raylib.GetScreenWidth() - w - 8;
        int y = 44;
        Raylib.DrawRectangle(x, y, w, 420, new Color(10, 10, 20, 210));
        Raylib.DrawRectangleLines(x, y, w, 420, Color.LightGray);
        int ty = y + 10;
        void Line(string s, int size = 14) {
            DrawUi(s, x + 10, ty, size, Color.RayWhite);
            ty += size + 4;
        }

        Line($"{npc.Name} — {npc.OccupationName}", 18);
        Line($"Gold: {npc.Gold}");
        Line("Pack: " + (npc.Inventory.Items.Count == 0
            ? "empty"
            : string.Join(", ", npc.Inventory.Items.Take(4).Select(i => i.IsLivelihoodAsset ? i.Label + "*" : i.Label))), 12);
        Line($"Hunger {npc.Needs.Hunger:0.00}  Energy {npc.Needs.Energy:0.00}  Social {npc.Needs.Social:0.00}");
        Line($"Action: {npc.CurrentAction?.ToString() ?? "idle"}{(npc.DecisionPending ? " (thinking…)" : "")}");
        Line($"Interests: {string.Join(", ", npc.Interests)}", 12);
        if (npc.LastDecision is not null) {
            Line($"Last Laya: {npc.LastDecision.Choice} conf={npc.LastDecision.Confidence:0.00}", 12);
            foreach (var (k, v) in npc.LastDecision.Probabilities.OrderByDescending(kv => kv.Value).Take(5))
                Line($"  {k}: {v:0.00}", 12);
        }
        if (!string.IsNullOrEmpty(npc.LastSpokenLine))
            Line($"Said: {npc.LastSpokenLine}", 12);
        Line("Memories:", 12);
        foreach (string? m in npc.Memories.Items.Reverse().Take(4))
            Line($"- {Trim(m, 40)}", 12);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
