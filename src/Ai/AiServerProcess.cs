using System.Diagnostics;

namespace NpcFarm.Ai;

public sealed class AiServerProcess : IDisposable {
    private Process? _process;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public AiServerProcess(int port = 8765) => Port = port;

    public void Start(string repoRoot) {
        string python = Path.Combine(repoRoot, ".venv", "bin", "python");
        string server = Path.Combine(repoRoot, "ai", "server.py");
        if (!File.Exists(python))
            throw new FileNotFoundException("Python venv missing. Run scripts/setup.sh first.", python);
        if (!File.Exists(server))
            throw new FileNotFoundException("AI server script missing.", server);

        var psi = new ProcessStartInfo {
            FileName = python,
            Arguments = $"\"{server}\" --host 127.0.0.1 --port {Port}",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine($"[ai] {e.Data}");
        };
        _process.ErrorDataReceived += (_, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                Console.Error.WriteLine($"[ai] {e.Data}");
        };
        if (!_process.Start())
            throw new InvalidOperationException("Failed to start AI server process.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Dispose() {
        if (_process is null) return;
        try {
            if (!_process.HasExited) {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch {
            // ignore shutdown races
        }
        finally {
            _process.Dispose();
            _process = null;
        }
    }
}
