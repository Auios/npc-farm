using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcFarm.Simulation;

namespace NpcFarm.Ai;

public sealed record InterestPerson(string Name, string Occupation, Dictionary<string, float> Personality);

public sealed record InterestResult(string Name, List<string> Interests);

public sealed class AiClient : IDisposable {
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AiClient(string baseUrl = "http://127.0.0.1:8765") {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default) {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            try {
                var resp = await _http.GetAsync("/health", ct);
                if (resp.IsSuccessStatusCode) {
                    var body = await resp.Content.ReadFromJsonAsync<HealthResponse>(_json, ct);
                    if (body?.Ok == true) return;
                }
            }
            catch (Exception ex) {
                last = ex;
            }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"AI server not ready within {timeout}. Last error: {last?.Message}");
    }

    public async Task<DecisionResult> DecideAsync(Dictionary<string, object> state, IReadOnlyList<ActionOption> actions) {
        // Use plain dictionaries so the wire JSON is unambiguous for the Python server.
        var payload = new Dictionary<string, object?> {
            ["state"] = state,
            ["actions"] = actions.Select(a => new Dictionary<string, string> {
                ["id"] = a.Id,
                ["description"] = a.Description
            }).ToList()
        };
        string body = JsonSerializer.Serialize(payload, _json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/decide", content);
        if (!resp.IsSuccessStatusCode) {
            string err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"decide failed {(int)resp.StatusCode}: {err}; sent={body}");
        }
        var doc = await resp.Content.ReadFromJsonAsync<DecideResponse>(_json)
                  ?? throw new InvalidOperationException("Empty decide response");
        return new DecisionResult {
            Choice = doc.Choice,
            Probabilities = doc.Probabilities ?? new(),
            Confidence = doc.Confidence
        };
    }

    public async Task<string> SpeakAsync(Npc speaker, Npc listener, float affinity, string? outcome = null) {
        var payload = new Dictionary<string, object?> {
            ["speaker"] = new Dictionary<string, object?> {
                ["name"] = speaker.Name,
                ["occupation"] = speaker.OccupationName,
                ["greed"] = speaker.Personality.Greed,
                ["sociability"] = speaker.Personality.Sociability,
                ["empathy"] = speaker.Personality.Empathy,
                ["honesty"] = speaker.Personality.Honesty,
                ["interests"] = speaker.Interests,
                ["hunger"] = speaker.Needs.Hunger,
                ["energy"] = speaker.Needs.Energy,
                ["social"] = speaker.Needs.Social
            },
            ["listener"] = new Dictionary<string, object?> {
                ["name"] = listener.Name,
                ["occupation"] = listener.OccupationName
            },
            ["affinity"] = affinity,
            ["memories"] = speaker.Memories.Items.ToList(),
            ["topic"] = "everyday town life",
            ["outcome"] = outcome
        };
        string body = JsonSerializer.Serialize(payload, _json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/speak", content);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<SpeakResponse>(_json)
                  ?? throw new InvalidOperationException("Empty speak response");
        return string.IsNullOrWhiteSpace(doc.Line) ? "..." : doc.Line;
    }

    public async Task<List<InterestResult>> GenerateInterestsAsync(IReadOnlyList<InterestPerson> people) {
        var payload = new Dictionary<string, object?> {
            ["people"] = people.Select(p => new Dictionary<string, object?> {
                ["name"] = p.Name,
                ["occupation"] = p.Occupation,
                ["personality"] = p.Personality
            }).ToList()
        };
        string body = JsonSerializer.Serialize(payload, _json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/interests", content);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<InterestsResponse>(_json)
                  ?? throw new InvalidOperationException("Empty interests response");
        return doc.People ?? new();
    }

    public void Dispose() => _http.Dispose();

    private sealed class HealthResponse {
        public bool Ok { get; set; }
    }

    private sealed class DecideResponse {
        public string Choice { get; set; } = "";
        public Dictionary<string, float>? Probabilities { get; set; }
        public float? Confidence { get; set; }
    }

    private sealed class SpeakResponse {
        public string Line { get; set; } = "";
    }

    private sealed class InterestsResponse {
        public List<InterestResult>? People { get; set; }
    }
}
