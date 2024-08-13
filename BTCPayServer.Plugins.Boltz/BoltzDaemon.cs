#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Configuration;
using BTCPayServer.Hwi.Deployment;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Lightning.Eclair;
using BTCPayServer.Lightning.LND;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Org.BouncyCastle.Bcpg.OpenPgp;
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
    private Task? _updateTask;
    private static CancellationTokenSource? _daemonCancel;
    public string StorageDir => Path.Combine(dataDirectories.Value.StorageDir, "Boltz");
    public string LogFile => Path.Combine(StorageDir, "boltz.log");
    public string ConfigPath => Path.Combine(StorageDir, "boltz.toml");
    public string DaemonBinary => Path.Combine(StorageDir, "bin", $"linux_{Architecture}", "boltzd");
    private readonly List<string> _output = new();

    public BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
    public string? AdminMacaroon { get; private set; }
    public Release? LatestRelease { get; private set; }
    public string? CurrentVersion { get; private set; }
    public bool UpdateAvailable => LatestRelease!.TagName != CurrentVersion;
    public BoltzClient? AdminClient { get; private set; }
    public bool Running => AdminClient is not null;
    public readonly TaskCompletionSource<bool> InitialStart = new();
    public event EventHandler<GetSwapInfoResponse>? SwapUpdate;
    public ILightningClient? Node;
    public string? NodeError { get; private set; }
    public string? Error { get; private set; }
    public string? LatestStdout { get; private set; }
    public string? LatestStderr { get; private set; }
    public string RecentOutput => string.Join("\n", _output);

    private const int MaxLogLines = 200;

    readonly SemaphoreSlim _semaphore = new(1, 1);

    private string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        _ => ""
    };

    public async Task<bool> Wait(CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(StorageDir, "macaroons", "admin.macaroon");
            while (!File.Exists(path))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }

            logger.LogDebug("Admin macaroon found");

            var reader = await File.ReadAllBytesAsync(path, cancellationToken);
            AdminMacaroon = Convert.ToHexString(reader).ToLower();
            var client = new BoltzClient(_defaultUri, AdminMacaroon);

            while (true)
            {
                try
                {
                    await client.GetInfo(cancellationToken);
                    logger.LogInformation("Client running");
                    AdminClient = client;
                    AdminClient.SwapUpdate += SwapUpdate;
                    return true;
                }
                catch (RpcException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
        }
        catch (TaskCanceledException)
        {
            logger.LogInformation("Daemon start timed out");
            await Stop();
        }

        return false;
    }

    public async Task<Release> GetLatestRelease()
    {
        return await _githubClient.Repository.Release.GetLatest("BoltzExchange", "boltz-client");
    }

    public string ArchiveName(string version)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return $"boltz-client-linux-{Architecture}-{version}.tar.gz";
        }

        return "";
    }

    public async Task Download()
    {
        await Download(LatestRelease!.TagName);
    }

    private async Task Update()
    {
        await Stop();
        await Download();
        await Start();
    }

    public void StartUpdate()
    {
        _updateTask = Update();
    }

    public async Task Download(string version)
    {
        logger.LogInformation($"Downloading boltz client {version}");
        var archive = ArchiveName(version);
        using var client = new HttpClient();
        string url =
            $"https://github.com/BoltzExchange/boltz-client/releases/download/{version}/{archive}";
        await using var s = await client.GetStreamAsync(url);
        _downloadStream = s;
        await using var gzip = new GZipStream(s, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, StorageDir, true);

        _downloadStream = null;
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
        logger.LogInformation($"Extracting: tar -xzf \"{tarGzPath}\" -C \"{outputDir}\"");
        var (exit, stdout, stderr) = await RunCommand("tar", $"-xzf \"{tarGzPath}\" -C \"{outputDir}\"");
        if (exit != 0)
        {
            logger.LogError(stdout);
            logger.LogError(stderr);

            throw new Exception("failed to extract tar archive");
        }
    }

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

        string shared = $"""
                         network = "{networkName}"
                         referralId = "btcpay"

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
            await File.WriteAllTextAsync(ConfigPath, GetConfig(node));
            await Stop();
            InitialStart.TrySetResult(await Start());
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public event EventHandler? OnDaemonExit;

    public async Task Init()
    {
        logger.LogDebug("Initializing");
        if (!Directory.Exists(StorageDir))
        {
            Directory.CreateDirectory(StorageDir);
        }

        LatestRelease = await GetLatestRelease();
        if (!File.Exists(DaemonBinary))
        {
            await Download();
        }
        else
        {
            var (code, stdout, _) = await RunCommand(DaemonBinary, "--version");
            if (code != 0)
            {
                await Download();
            }
            else
            {
                CurrentVersion = stdout.Split("\n").First().Split("-").First();
            }
        }
    }

    private async Task<bool> Start()
    {
        logger.LogInformation("Starting daemon");
        _daemonCancel = new CancellationTokenSource();
        _ = Task.Factory.StartNew(async () =>
        {
            while (!_daemonCancel.Token.IsCancellationRequested)
            {
                _output.Clear();
                using var process = StartProcess(DaemonBinary, $"--datadir {StorageDir}");

                MonitorStream(process.StandardOutput, _daemonCancel.Token);
                MonitorStream(process.StandardError, _daemonCancel.Token);

                try
                {
                    await process.WaitForExitAsync(_daemonCancel.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    throw;
                }

                OnDaemonExit?.Invoke(this, EventArgs.Empty);

                if (process.ExitCode != 0)
                {
                    logger.LogError($"Process exited with code {process.ExitCode}\n{RecentOutput}");
                    await Task.Delay(5000, _daemonCancel.Token);
                }
                else
                {
                    await _daemonCancel.CancelAsync();
                    _daemonCancel = null;
                    return;
                }
            }
        }, _daemonCancel.Token);

        var source = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        EventHandler handler = (_, _) => { source.Cancel(); };
        OnDaemonExit += handler;
        var res = await Wait(source.Token);
        OnDaemonExit -= handler;
        return res;
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
                AdminClient = null;
            }

            await _daemonCancel.CancelAsync();
        }
    }

    private Process StartProcess(string fileName, string args)
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
        Process process = new Process { StartInfo = processStartInfo };
        process.Start();
        return process;
    }

    async Task<(int, string, string)> RunCommand(string fileName, string args,
        CancellationToken cancellationToken = default)
    {
        using Process process = StartProcess(fileName, args);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        return (process.ExitCode, stdout, stderr);
    }

    private void MonitorStream(StreamReader streamReader, CancellationToken cancellationToken)
    {
        Task.Factory.StartNew(async () =>
        {
            while (!streamReader.EndOfStream)
            {
                var line = await streamReader.ReadLineAsync(cancellationToken);
                if (line != null)
                {
                    {
                        if (line.Contains("error") || line.Contains("fatal"))
                        {
                            logger.LogError(line);
                        }

                        if (_output.Count >= MaxLogLines)
                        {
                            _output.RemoveAt(0);
                        }

                        _output.Add(line);
                    }
                }
            }
        }, cancellationToken);
    }
}