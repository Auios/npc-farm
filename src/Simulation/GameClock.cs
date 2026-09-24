namespace NpcFarm.Simulation;

public sealed class GameClock {
    public const float MinutesPerDay = 24f * 60f;

    /// <summary>In-game minutes since midnight day 0.</summary>
    public float TotalMinutes { get; private set; } = 8f * 60f; // start at 08:00

    public float HourOfDay => (TotalMinutes % MinutesPerDay) / 60f;
    public int Day => (int)(TotalMinutes / MinutesPerDay) + 1;
    public bool IsPaused { get; set; }
    public float Speed { get; private set; } = 1f;

    /// <summary>Real seconds per in-game hour at 1x. Plan: ~60s/hour.</summary>
    public float RealSecondsPerGameHour { get; set; } = 60f;

    public bool IsNight => HourOfDay < 6f || HourOfDay >= 20f;
    public bool IsWorkHours => HourOfDay >= 8f && HourOfDay < 17f;
    public bool IsMarketOpen => HourOfDay >= 7f && HourOfDay < 20f;

    public void SetSpeed(float speed) => Speed = Math.Clamp(speed, 0f, 32f);

    public void CycleSpeed() {
        Speed = Speed switch {
            <= 0.01f => 1f,
            < 2f => 3f,
            < 5f => 8f,
            _ => 1f
        };
    }

    public float Advance(float realSeconds) {
        if (IsPaused || Speed <= 0f) return 0f;
        float gameHours = (realSeconds * Speed) / RealSecondsPerGameHour;
        TotalMinutes += gameHours * 60f;
        return gameHours;
    }

    public string FormatTime() {
        int h = (int)HourOfDay;
        int m = (int)((HourOfDay - h) * 60f);
        return $"Day {Day} {h:00}:{m:00}";
    }
}
