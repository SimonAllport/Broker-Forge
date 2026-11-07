using System.Net;
using System.Net.Sockets;

namespace BrokerForge.Auth.Tests;

/// <summary>
/// Azurite's table endpoint: one already listening on 127.0.0.1:10002 is used and never stopped. Otherwise this test
/// process starts its own with npx (Node is already needed for the mock API) on a free port, so no other process uses
/// it: the Auth and Contract test assemblies both compile this fixture and run in parallel, and with one shared port
/// the assembly that started Azurite stopped it while the other was still using it. If it cannot start, the tests fail
/// rather than skip.
/// </summary>
public sealed class AzuriteFixture : IDisposable
{
    private const int SharedPort = 10002;

    // Azurite's published development account; not a secret.
    private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="; // gitleaks:allow

    public static string ConnectionString { get; private set; } = For(SharedPort);

    private readonly System.Diagnostics.Process? _azurite;
    private readonly string? _folder;

    public AzuriteFixture()
    {
        if (Reachable(SharedPort))
            return;
        var port = FreePort();
        var info = new System.Diagnostics.ProcessStartInfo("npx",
            // --package with the azurite-table command: `npx azurite@… azurite-table` runs the package's default command,
            // all three services, and every copy's blob service wants port 10000.
            $"--yes --package azurite@3.33.0 azurite-table --tableHost 127.0.0.1 --tablePort {port} --inMemoryPersistence --skipApiVersionCheck --silent")
            { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _folder = Directory.CreateTempSubdirectory("bf-azurite").FullName };
        _azurite = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Could not start npx to run Azurite.");
        _azurite.OutputDataReceived += (_, e) => Keep(e.Data);
        _azurite.ErrorDataReceived += (_, e) => Keep(e.Data);
        _azurite.BeginOutputReadLine();
        _azurite.BeginErrorReadLine();
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!Reachable(port))
        {
            if (_azurite.HasExited)
                throw new InvalidOperationException($"Azurite exited with code {_azurite.ExitCode} before listening on 127.0.0.1:{port}. It said: {Said()}");
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"Azurite did not listen on 127.0.0.1:{port} within 90 seconds.");
            Thread.Sleep(250);
        }
        ConnectionString = For(port);
    }

    // The last lines npx and Azurite printed, for the message when it does not start.
    private readonly Queue<string> _said = new();

    private void Keep(string? line)
    {
        if (line == null)
            return;
        lock (_said)
        {
            _said.Enqueue(line);
            if (_said.Count > 20)
                _said.Dequeue();
        }
    }

    private string Said()
    {
        lock (_said)
            return _said.Count == 0 ? "nothing" : string.Join(" | ", _said);
    }

    private static string For(int port) =>
        $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={AccountKey};TableEndpoint=http://127.0.0.1:{port}/devstoreaccount1;";

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool Reachable(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_azurite is { HasExited: false })
            _azurite.Kill(entireProcessTree: true);
        _azurite?.Dispose();
        if (_folder != null)
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// One Azurite for every table test class. With a fixture per class, the class that started Azurite killed it when it
/// finished, while another class that had found it running was still using it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Azurite";
}
