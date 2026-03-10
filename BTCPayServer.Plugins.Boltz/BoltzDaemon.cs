#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Boltzrpc;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Lightning.LND;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FileMode = System.IO.FileMode;
using FileStream = System.IO.FileStream;
using SHA256 = System.Security.Cryptography.SHA256;

namespace BTCPayServer.Plugins.Boltz;

public class LndConfig
{
    public string? Host { get; set; }
    public ulong? Port { get; set; }
    public string? Macaroon { get; set; }
    public string? Certificate { get; set; }
}

public class ClnConfig
{
    public string? Host { get; set; }
    public ulong? Port { get; set; }
    public string? DataDir { get; set; }
    public string? ServerName { get; set; }
}

public class DaemonConfig
{
    public string? LogLevel { get; set; }
}


public class BoltzDaemon(
    IOptions<DataDirectories> dataDirectories,
    ILogger<BoltzDaemon> logger,
    ILogger<BoltzClient> clientLogger,
    BTCPayNetworkProvider btcPayNetworkProvider
)
{
    private static readonly Version ClientVersion = new("2.11.1");
    private static readonly Lazy<string> ExpectedManifest = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("boltz-client-manifest.txt");
        if (stream is null) throw new InvalidOperationException("Embedded manifest resource not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private Stream? _downloadStream;
    private Task? _startTask;
    private Process? _daemonProcess;
    private readonly List<string> _output = new();
    private readonly HttpClient _httpClient = new();
    private const int MaxLogLines = 150;
    private string DataDir => Path.Combine(dataDirectories.Value.DataDir, "Plugins", "Boltz");
    private string DaemonBinary => Path.Combine(DataDir, "bin", $"linux_{Architecture}", "boltzd");
    private string DaemonCli => Path.Combine(DataDir, "bin", $"linux_{Architecture}", "boltzcli");
    private BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
    private readonly SemaphoreSlim _configSemaphore = new(1, 1);
    private string ConfigFile => Path.Combine(DataDir, "boltz.toml");

    public readonly Uri DefaultUri = new("https://127.0.0.1:9002");
    public string CertFile => Path.Combine(DataDir, "tls.cert");
    public bool Starting => _startTask is not null && !_startTask.IsCompleted;
    public string LogFile => Path.Combine(DataDir, "boltz.log");
    public string? AdminMacaroon { get; private set; }
    public BoltzClient? AdminClient { get; private set; }
    public bool Running => AdminClient is not null;
    public readonly TaskCompletionSource<bool> InitialStart = new();
    public event EventHandler<GetSwapInfoResponse>? SwapUpdate;
    public string? NodeError { get; private set; }
    public string? Error { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string RecentOutput => string.Join("\n", _output);

    private string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        _ => throw new NotSupportedException("Unsupported architecture")
    };

    public async Task Download()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new NotSupportedException("Only linux is supported");
        }

        var version = ClientVersion;
        logger.LogInformation($"Downloading boltz client {version}");

        string archiveName = $"boltz-client-linux-{Architecture}-v{version}.tar.gz";
        await using var s = await _httpClient.GetStreamAsync(ReleaseUrl(version) + archiveName);

        _downloadStream = s;
        await using var gzip = new GZipStream(s, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, DataDir, true);
        _downloadStream = null;

        CheckBinaries();
    }

    private string ReleaseUrl(Version version)
    {
        return $"https://github.com/BoltzExchange/boltz-client/releases/download/v{version}/";
    }

    private async Task<Stream> DownloadFile(string uri, string destination)
    {
        string path = Path.Combine(DataDir, destination);
        if (!File.Exists(path))
        {
            await using var s = await _httpClient.GetStreamAsync(uri);
            await using var fs = new FileStream(path, FileMode.Create);
            await s.CopyToAsync(fs);
        }

        return File.OpenRead(path);
    }

    private void CheckBinaries()
    {
        CheckShaSums(DaemonBinary);
        CheckShaSums(DaemonCli);
    }

    private void CheckShaSums(string fileToCheck)
    {
        // Compute the SHA256 hash of the file
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(fileToCheck);
        byte[] hashBytes = sha256.ComputeHash(stream);
        string computedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

        foreach (var line in ExpectedManifest.Value.Split("\n"))
        {
            var split = line.Split();
            if (fileToCheck.Contains(split.Last()))
            {
                var expectedHash = split.First();
                if (computedHash == expectedHash)
                {
                    return;
                }

                throw new Exception("SHA256 hash mismatch");
            }
        }

        throw new Exception("File not found in manifest");
    }

    public double DownloadProgress()
    {
        if (_downloadStream is null)
        {
            return 0;
        }

        return 100 * (double)_downloadStream.Position / _downloadStream.Length;
    }


    public async Task TryConfigure(DaemonConfig config)
    {
        try
        {
            await Configure(config);
        }
        finally
        {
            if (!Running)
            {
                logger.LogInformation("Could not start: " + Error);
                NodeError = null;
            }
            else if (NodeError != null)
            {
                logger.LogInformation("Could not connect to node: " + NodeError);
            }

            InitialStart.TrySetResult(Running);
        }
    }

    public string GetConfig(DaemonConfig config)
    {
        var networkName = BtcNetwork.NBitcoinNetwork.ChainName.ToString().ToLower();

        return $"""
        standalone = true
        network = "{networkName}"
        referralId = "btcpay"
        logmaxsize = 1
        loglevel = "{config.LogLevel ?? "info"}"

        [RPC]
        host = "{DefaultUri.Host}"
        port = {DefaultUri.Port}
        rest.disable = true
        """;
    }

    private async Task Configure(DaemonConfig config)
    {
        try
        {
            await _configSemaphore.WaitAsync();

            var newConfig = GetConfig(config);
            if (Path.Exists(ConfigFile))
            {
                var current = await File.ReadAllTextAsync(ConfigFile);
                if (current == newConfig && Running)
                {
                    return;
                }
            }

            await File.WriteAllTextAsync(ConfigFile, newConfig);
            await Start();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            InitialStart.TrySetResult(true);
            _configSemaphore.Release();
        }
    }

    public async Task Init()
    {
        logger.LogDebug("Initializing");
        try
        {
            if (!Directory.Exists(DataDir))
            {
                Directory.CreateDirectory(DataDir);
            }

            try
            {
                CheckBinaries();
            }
            catch (Exception e)
            {
                // CheckBinaries re-runs after Download, in which case its gonna succeed if they were just outdated
                // Or still fail if they are corrupted
                await Download();
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
            logger.LogError(e, "Failed to initialize");
        }
    }

    private async Task CheckStarted(CancellationToken token)
    {
        var path = Path.Combine(DataDir, "macaroons", "admin.macaroon");
        if (File.Exists(path) && File.Exists(CertFile))
        {
            var reader = await File.ReadAllBytesAsync(path, token);
            var macaroon = Convert.ToHexString(reader).ToLower();
            var client = new BoltzClient(clientLogger, DefaultUri, macaroon, CertFile, "all");

            if (
                RecentOutput.Contains("Boltz backend be unavailable") ||
                RecentOutput.Contains("Boltz backend is unavailable")
            )
            {
                // dont block startup if backend is unavailable - it will retry connecting in the background and
                // the grpc calls will simply fail until the backend is available
                logger.LogWarning("Boltz backend is unavailable, continuing with startup");
            }
            else
            {
                await client.GetInfo(token);
            }
            logger.LogInformation("Client started");
            AdminClient = client;
            AdminMacaroon = macaroon;
            Error = null;
        }
    }

    private async Task<Process?> StartDaemon(CancellationToken cancellationToken)
    {
        _output.Clear();
        CheckShaSums(DaemonBinary);
        var process = NewProcess(DaemonBinary, $"--datadir {DataDir}");
        process.EnableRaisingEvents = true;
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to start client process");
            Error = $"Failed to start process {e.Message}";
            return null;
        }


        var latestError = string.Empty;
        try
        {
            MonitorStream(process.StandardOutput);
            MonitorStream(process.StandardError, true);

            while (!process.HasExited && !Running && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await CheckStarted(cancellationToken);
                }
                catch (RpcException e)
                {
                    latestError = e.Status.Detail;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

        }
        catch (OperationCanceledException)
        {
            Error = latestError;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Unexpected error during daemon startup");
            Error = $"Unexpected error during startup: {e.Message}";
        }

        if (Running)
        {
            process.Exited += async (_, _) =>
            {
                // non-zero exit and null AdminClient means that the graceful stop from `Stop` timed out and we killed the process ourselves.
                if (process.ExitCode != 0 && AdminClient is not null)
                {
                    Clear();
                    Error = $"Exited with code {process.ExitCode}. Check logs for more information.";
                    logger.LogError(Error);
                    logger.LogInformation(RecentOutput);

                    logger.LogInformation("Restarting in 10 seconds");
                    await Task.Delay(10000);
                    InitiateStart();
                }
            };
            return process;
        }
        await Stop();
        return null;
    }

    private async Task Start()
    {
        await Stop();
        logger.LogInformation("Starting client process");
        var wait = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        _daemonProcess = await StartDaemon(wait.Token);
        if (Running)
        {
            AdminClient!.SwapUpdate += SwapUpdate!;
        }
    }

    public void InitiateStart()
    {
        if (!Starting)
        {
            _startTask = Start();
        }
    }

    private void Clear()
    {
        BoltzClient.Clear();
        AdminClient = null;
        _daemonProcess = null;
    }

    public async Task Stop()
    {
        if (_daemonProcess is not null && !_daemonProcess.HasExited)
        {
            var source = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (AdminClient is not null)
            {
                try
                {
                    logger.LogInformation("Stopping gracefully");
                    await AdminClient.Stop(source.Token);
                }
                catch (RpcException)
                {
                    logger.LogInformation("Graceful stop timed out, killing client process");
                    logger.LogInformation(RecentOutput);
                    _daemonProcess.Kill();
                }
            }
            else
            {
                _daemonProcess.Kill();
            }

            Clear();

            logger.LogInformation("Client stopped");
        }
    }

    private Process NewProcess(string fileName, string args)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        //processStartInfo.EnvironmentVariables.Add("GRPC_GO_LOG_VERBOSITY_LEVEL", "99");
        //processStartInfo.EnvironmentVariables.Add("GRPC_GO_LOG_SEVERITY_LEVEL", "info");
        return new Process { StartInfo = processStartInfo };
    }

    async Task<(int, string, string)> RunCommand(string fileName, string args,
        CancellationToken cancellationToken = default)
    {
        using Process process = NewProcess(fileName, args);
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        return (process.ExitCode, stdout, stderr);
    }

    private void MonitorStream(StreamReader streamReader, bool logAll = false)
    {
        Task.Factory.StartNew(async () =>
        {
            while (!streamReader.EndOfStream)
            {
                try
                {
                    var line = await streamReader.ReadLineAsync();
                    if (line != null)
                    {
                        {
                            if (line.Contains("ERROR") || line.Contains("WARN"))
                            {
                                logger.LogWarning(line);
                            }

                            if (_output.Count >= MaxLogLines && !logAll)
                            {
                                _output.RemoveAt(0);
                            }

                            _output.Add(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to read line from stream");
                }
            }
        });
    }

    public BoltzClient? GetClient(BoltzSettings? settings)
    {
        if (settings is null) return null;
        return settings.CredentialsPopulated() && (settings.GrpcUrl != DefaultUri || Running)
            ? new BoltzClient(clientLogger, settings.GrpcUrl!, settings.Macaroon!, settings.CertFilePath!)
            : null;
    }
}
