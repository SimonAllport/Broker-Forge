using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// Runs tools/mock-api/server.js for the test collection. The API key is a dummy value, never a real secret.
/// Call <see cref="ResetAsync"/> at the start of each test.
/// </summary>
public sealed class MockApiFixture : IAsyncLifetime
{
    public const string ApiKey = "bf-test-dummy-key";
    public const string InventoryKey = "bf-test-dummy-inventory-key";
    public const int SlowMs = 5000;
    private Process? _process;

    public static string RepoRoot { get; } = FindRoot();
    public string BaseUrl { get; private set; } = "";

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BrokerForge.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("BrokerForge.slnx not found above the test directory.");
    }

    public async Task InitializeAsync()
    {
        var info = new ProcessStartInfo("node", Path.Combine(RepoRoot, "tools", "mock-api", "server.js"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["PORT"] = "0";
        info.Environment["MOCK_API_KEY"] = ApiKey;
        info.Environment["MOCK_INVENTORY_KEY"] = InventoryKey;
        info.Environment["MOCK_SLOW_MS"] = SlowMs.ToString();
        foreach (var (k, v) in Tickets.MockEnvironment())
            info.Environment[k] = v;
        _process = Process.Start(info) ?? throw new InvalidOperationException("could not start node");

        var read = _process.StandardOutput.ReadLineAsync();
        if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15))) != read)
            throw new TimeoutException("mock API did not start within 15 seconds");
        var line = await read ?? throw new InvalidOperationException("mock API exited: " + await _process.StandardError.ReadToEndAsync());
        var match = Regex.Match(line, @"listening (\d+)");
        if (!match.Success)
            throw new InvalidOperationException("unexpected mock API output: " + line);
        BaseUrl = $"http://127.0.0.1:{match.Groups[1].Value}";
    }

    public async Task ResetAsync()
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/__reset");
        request.Headers.Add("X-Api-Key", ApiKey);
        (await http.SendAsync(request)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        if (_process is { HasExited: false })
            _process.Kill(entireProcessTree: true);
        _process?.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class MockApiCollection : ICollectionFixture<MockApiFixture>
{
    public const string Name = "mock api";
}
