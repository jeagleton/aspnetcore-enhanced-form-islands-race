using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace IslandsRace.Tests;

// Publishes nothing itself: runs the Release publish of src/IslandsRace (artifacts/app, made by run.ps1 or
// `dotnet publish src/IslandsRace -c Release -o artifacts/app`) on a free port, and opens Chromium.
public sealed class AppFixture : IAsyncLifetime
{
    private Process? _app;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = "";
    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        string root = RepoRoot();
        // REPRO_APP_DIR: another publish of the app (e.g. artifacts/app-throw, built with BlazorDisableThrowNavigationException=false).
        string appDir = Environment.GetEnvironmentVariable("REPRO_APP_DIR") is { Length: > 0 } d ? Path.Combine(root, d) : Path.Combine(root, "artifacts", "app");
        string dll = Path.Combine(appDir, "IslandsRace.dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException($"{dll} not found. Run: dotnet publish src/IslandsRace -c Release -o artifacts/app");

        BaseUrl = $"http://127.0.0.1:{FreePort()}";
        var start = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls {BaseUrl}")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        _app = Process.Start(start)!;
        // A cancelled run (Ctrl+C) skips DisposeAsync; don't leave the app running and locking artifacts/.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _app.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } };
        _app.OutputDataReceived += (_, _) => { };
        _app.ErrorDataReceived += (_, _) => { };
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();

        using var http = new HttpClient();
        for (int i = 0; ; i++)
        {
            try { if ((await http.GetAsync(BaseUrl)).IsSuccessStatusCode) break; }
            catch (HttpRequestException) when (i < 100) { }
            await Task.Delay(100);
        }

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        if (_app is { HasExited: false }) _app.Kill(entireProcessTree: true);
        _app?.Dispose();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "IslandsRace.slnx"))) return dir.FullName;
        throw new InvalidOperationException("IslandsRace.slnx not found above the test's output folder.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
