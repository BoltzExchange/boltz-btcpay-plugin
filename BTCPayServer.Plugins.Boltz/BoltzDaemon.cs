#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Lightning.LND;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using FileMode = System.IO.FileMode;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzDaemon(
    IOptions<DataDirectories> dataDirectories,
    ILogger<BoltzDaemon> logger,
    BTCPayNetworkProvider btcPayNetworkProvider
)
{
    private readonly Uri _defaultUri = new("http://127.0.0.1:9002");
    private readonly GitHubClient _githubClient = new(new ProductHeaderValue("Boltz"));
    private Stream? _downloadStream;
    private static CancellationTokenSource? _daemonCancel;
    public BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");

    public string? AdminMacaroon { get; private set; }
    public string? LatestVersion { get; private set; }
    public string? CurrentVersion { get; private set; }
    public bool UpdateAvailable => LatestVersion != CurrentVersion;
    public BoltzClient? AdminClient { get; private set; }
    public string? Error { get; private set; }
    public bool Running => AdminClient is not null;

    public event EventHandler? OnDaemonStart;
    public readonly TaskCompletionSource InitialStart = new();

    readonly SemaphoreSlim _semaphore = new(1, 1);

    private string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        _ => ""
    };

    public Task<bool> Wait()
    {
        var tcs = new TaskCompletionSource<bool>();

        var source = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        OnDaemonExit += (_, _) => { source.Cancel(); };

        Task.Factory.StartNew(async () =>
        {
            try
            {
                var path = Path.Combine(StorageDir, "macaroons", "admin.macaroon");
                while (!File.Exists(path))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50));
                }

                logger.LogDebug("Admin macaroon found");

                var reader = await File.ReadAllBytesAsync(path);
                AdminMacaroon = Convert.ToHexString(reader).ToLower();
                var client = new BoltzClient(_defaultUri, AdminMacaroon);

                while (true)
                {
                    try
                    {
                        await client.GetInfo();
                        logger.LogInformation("Client running");
                        AdminClient = client;
                        tcs.TrySetResult(true);
                        return;
                    }
                    catch (RpcException)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken: source.Token);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                logger.LogInformation("Daemon start timed out");
                AdminClient = null;
                tcs.TrySetResult(false);
            }
        }, source.Token);

        return tcs.Task;
    }

    public async Task<string> GetLatestClientVersion()
    {
        return "v2.1.0";
        var latest = await _githubClient.Repository.Release.GetLatest("BoltzExchange", "boltz-client");
        return latest.TagName;
    }

    public string ArchiveName(string version)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return $"boltz-client-linux-{Architecture}-{version}.tar.gz";
        }

        return "";
    }

    public async Task Download(string version)
    {
        var archive = ArchiveName(version);
        var archivePath = Path.Combine(StorageDir, archive);
        if (!File.Exists(archivePath))
        {
            using var client = new HttpClient();
            string url =
                $"https://github.com/BoltzExchange/boltz-client/releases/download/{version}/{archive}";
            await using var s = await client.GetStreamAsync(url);
            _downloadStream = s;
            await using var fs = new FileStream(archivePath, FileMode.OpenOrCreate);
            await s.CopyToAsync(fs);
            _downloadStream = null;
        }

        await ExtractTar(archivePath, StorageDir);
        CurrentVersion = version;
    }

    public double DownloadProgress()
    {
        if (_downloadStream is null)
        {
            return 0;
        }

        return 100 * (double)_downloadStream.Position / _downloadStream.Length;
    }


    public async Task ExtractTar(string tarGzPath, string outputDir)
    {
        var (exit, _, _) = await RunProcess("tar", $"-xzf \"{tarGzPath}\" -C \"{outputDir}\"");
        if (exit != 0)
        {
            throw new Exception("failed to extract tar archive");
        }
    }

    public ILightningClient? Node;
    private string StorageDir => Path.Combine(dataDirectories.Value.StorageDir, "Boltz");
    private string ConfigPath => Path.Combine(StorageDir, "boltz.toml");
    public string? NodeError { get; private set; }
    public string? LatestStdout { get; private set; }
    public string? LatestStderr { get; private set; }

    public async Task<bool> TryConfigure(ILightningClient? node)
    {
        try
        {
            await Configure(node);
            Node = node;
            return true;
        }
        catch (Exception e)
        {
            if (node != null)
            {
                NodeError = e.Message;
                return await TryConfigure(null);
            }

            return false;
        }
    }

    private string GetConfig(ILightningClient? node)
    {
        var networkName = BtcNetwork.NBitcoinNetwork.ChainName.ToString().ToLower();

        // TODO: set defaults in daemon for regtest
        string shared = $"""
                         network = "{networkName}"

                         [RPC]
                         noMacaroons = false
                         noTls = true
                         host = "{_defaultUri.Host}"
                         port = {_defaultUri.Port}
                         rest.disable = true
                         """;
        if (node is null)
        {
            return $"""
                    standalone = true
                    {shared}
                    """;
        }

        switch (node)
        {
            case CLightningClient cln:
                if (cln.Address.Scheme == "unix")
                {
                    var path = cln.Address.AbsoluteUri.Remove(0, "unix:".Length);
                    if (!path.StartsWith("/"))
                        path = "/" + path;

                    var split = path.Split("/");
                    path = "/" + Path.Combine(split.Take(split.Length - 2).ToArray());

                    return $"""
                            {shared}

                            [CLN]
                            dataDir = "{path}"
                            port = 9736
                            host = "127.0.0.1"
                            """;
                }

                throw new Exception("Unsupported lightning connection string");

            case LndClient lnd:
                var url = new Uri(lnd.SwaggerClient.BaseUrl);
                var kv = LightningConnectionStringHelper.ExtractValues(lnd.ToString(), out _);
                if (!kv.TryGetValue("macaroon", out var macaroon))
                {
                    throw new Exception("No macaroon found in lnd connection string");
                }

                return $"""
                        {shared}

                        [LND]
                        host = "{url.Host}"
                        macaroon = "{macaroon}"
                        """;
            default:
                throw new Exception("Unsupported lightning client");
        }
    }

    private async Task Configure(ILightningClient? node)
    {
        await _semaphore.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(StorageDir, "boltz.toml"), GetConfig(node));
            await Start();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public event EventHandler? OnDaemonExit;

    public async Task Init()
    {
        logger.LogInformation("Initializing");
        if (!Directory.Exists(StorageDir))
        {
            Directory.CreateDirectory(StorageDir);
        }

        LatestVersion = await GetLatestClientVersion();
        var daemon = Path.Combine(StorageDir, "bin", $"linux_{Architecture}", "boltzd");

        if (!File.Exists(daemon))
        {
            await Download(LatestVersion);
        }
        else
        {
            var (code, stdout, _) = await RunProcess(daemon, "--version");
            if (code != 0)
            {
                await Download(LatestVersion);
            }
            else
            {
                CurrentVersion = stdout.Split("\n").First().Split("-").First();
            }
        }
    }

    public async Task EnsureRunning(ILightningClient? client)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (!Running)
            {
                await Init();
                await TryConfigure(client);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task Start()
    {
        await Stop();
        logger.LogInformation("Starting daemon");
        _daemonCancel?.Cancel();
        _daemonCancel = new CancellationTokenSource();
        Task.Factory.StartNew(async () =>
        {
            while (true)
            {
                var daemon = Path.Combine(StorageDir, "bin", $"linux_{Architecture}", "boltzd");
                var (exitCode, stdout, stderr) = await RunProcess(daemon, $"--datadir {StorageDir}");

                LatestStderr = stderr == "" ? null : stderr;
                LatestStdout = stdout == "" ? null : stdout;

                OnDaemonExit?.Invoke(this, EventArgs.Empty);
                if (exitCode != 0)
                {
                    logger.LogError($"Process exited with code {exitCode}");
                    logger.LogError(stdout);
                    logger.LogError(stderr);
                    await Task.Delay(5000);
                }
                else
                {
                    _daemonCancel?.Cancel();
                    _daemonCancel = null;
                    return;
                }
            }
        }, _daemonCancel.Token);
        if (!await Wait())
        {
            throw new Exception("Daemon start timed out");
        }

        InitialStart.TrySetResult();
    }

    public async Task Stop()
    {
        if (_daemonCancel is not null)
        {
            if (AdminClient is not null)
            {
                logger.LogInformation("Stopping gracefully");
                await AdminClient.Stop();
                AdminClient.Dispose();
            }

            await _daemonCancel.CancelAsync();
        }
    }

    async Task<(int, string, string)> RunProcess(string fileName, string args)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = new Process();
        process.StartInfo = processStartInfo;
        process.Start();
        await process.WaitForExitAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        return (process.ExitCode, stdout, stderr);
    }
}