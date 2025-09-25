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
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Lightning.LND;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Bcpg.OpenPgp;
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

public class NodeConfig
{
    public LndConfig? Lnd { get; set; }
    public ClnConfig? Cln { get; set; }
}

public class BoltzDaemon(
    IOptions<DataDirectories> dataDirectories,
    ILogger<BoltzDaemon> logger,
    ILogger<BoltzClient> clientLogger,
    BTCPayNetworkProvider btcPayNetworkProvider
)
{
    private static readonly Version ClientVersion = new("2.8.7");

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
    public NodeConfig? Node;
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

    public async Task Download(Version version)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new NotSupportedException("Only linux is supported");
        }

        logger.LogInformation($"Downloading boltz client {version}");

        string archiveName = $"boltz-client-linux-{Architecture}-v{version}.tar.gz";
        await using var s = await _httpClient.GetStreamAsync(ReleaseUrl(version) + archiveName);

        _downloadStream = s;
        await using var gzip = new GZipStream(s, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, DataDir, true);
        _downloadStream = null;

        await CheckBinaries(version);
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

    private async Task CheckBinaries(Version version)
    {
        string releaseUrl = ReleaseUrl(version);
        string manifestName = $"boltz-client-manifest-v{version}.txt";
        string sigName = $"boltz-client-manifest-v{version}.txt.sig";
        string pubKey = "boltz.asc";
        string pubKeyUrl = "https://canary.boltz.exchange/pgp.asc";

        await using var sigStream = await DownloadFile(releaseUrl + sigName, sigName);
        await using var pubKeyStream = await DownloadFile(pubKeyUrl, pubKey);

        var keyRing = new PgpPublicKeyRing(PgpUtilities.GetDecoderStream(pubKeyStream));
        var pgpFactory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(sigStream));
        PgpSignatureList sigList = (PgpSignatureList)pgpFactory.NextPgpObject();
        PgpSignature sig = sigList[0];

        var manifest = await _httpClient.GetByteArrayAsync(releaseUrl + manifestName);
        string manifestPath = Path.Combine(DataDir, manifestName);
        await File.WriteAllBytesAsync(manifestPath, manifest);
        PgpPublicKey publicKey = keyRing.GetPublicKey(sig.KeyId);
        sig.InitVerify(publicKey);
        sig.Update(manifest);
        if (!sig.Verify())
        {
            throw new Exception("Signature verification failed.");
        }

        CheckShaSums(DaemonBinary, manifestPath);
        CheckShaSums(DaemonCli, manifestPath);
    }

    private void CheckShaSums(string fileToCheck, string manifestFile)
    {
        // Compute the SHA256 hash of the file
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(fileToCheck);
        byte[] hashBytes = sha256.ComputeHash(stream);
        string computedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

        foreach (var line in File.ReadLines(manifestFile))
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


    public async Task TryConfigure(NodeConfig? node)
    {
        try
        {
            if (node != null)
            {
                _output.Clear();
                await Configure(node);
                if (Running)
                {
                    Node = node;
                    NodeError = null;
                    return;
                }

                NodeError = String.IsNullOrEmpty(RecentOutput) ? Error : RecentOutput;
            }

            await Configure(node: null);
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

    public NodeConfig? GetNodeConfig(ILightningClient? node)
    {
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

                    return new NodeConfig
                    {
                        Cln = new ClnConfig
                        {
                            DataDir = path,
                            Port = 9736,
                            Host = "127.0.0.1"
                        }
                    };
                }

                throw new Exception("Unsupported lightning connection string");

            case LndClient lnd:
                var url = new Uri(lnd.SwaggerClient.BaseUrl);
                var kv = LightningConnectionStringHelper.ExtractValues(lnd.ToString(), out _);
                if (!kv.TryGetValue("macaroonfilepath", out var macaroon))
                {
                    if (!kv.TryGetValue("macaroon", out macaroon))
                    {
                        throw new Exception("No macaroon found in lnd connection string");
                    }

                    throw new Exception("No macaroon found in lnd connection string");
                }

                if (!kv.TryGetValue("certfilepath", out var cert))
                {
                    var dir = Path.GetDirectoryName(macaroon);
                    var dataDir = dir?.Split("/data/chain/bitcoin").FirstOrDefault();
                    if (dataDir != null)
                    {
                        cert = Path.Combine(dataDir, "tls.cert");
                    }
                    else
                    {
                        throw new Exception("No cert found in lnd connection string");
                    }
                }

                return new NodeConfig
                {
                    Lnd = new LndConfig
                    {
                        Host = url.Host,
                        Macaroon = macaroon,
                        Certificate = cert,
                        Port = 10009,
                    }
                };
            case null:
                return null;
            default:
                throw new Exception("Unsupported lightning client");
        }
    }

    public string GetConfig(NodeConfig? nodeConfig)
    {
        var networkName = BtcNetwork.NBitcoinNetwork.ChainName.ToString().ToLower();

        string shared = $"""
                         network = "{networkName}"
                         referralId = "btcpay"
                         logmaxsize = 1

                         [RPC]
                         host = "{DefaultUri.Host}"
                         port = {DefaultUri.Port}
                         rest.disable = true
                         """;

        if (nodeConfig?.Lnd != null)
        {
            return $"""
                    node = "lnd"
                    {shared}

                    [LND]
                    host = "{nodeConfig.Lnd.Host}"
                    port = {nodeConfig.Lnd.Port}
                    macaroon = "{nodeConfig.Lnd.Macaroon}"
                    certificate = "{nodeConfig.Lnd.Certificate}"
                    """;
        }

        if (nodeConfig?.Cln != null)
        {
            return $"""
                    node = "cln"
                    {shared}

                    [CLN]
                    host = "{nodeConfig.Cln.Host}"
                    port = {nodeConfig.Cln.Port}
                    dataDir = "{nodeConfig.Cln.DataDir}"
                    """;
        }

        return $"""
                standalone = true
                {shared}
                """;
    }

    private async Task Configure(NodeConfig? node)
    {
        try
        {
            await _configSemaphore.WaitAsync();

            var newConfig = GetConfig(node);
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

            string? currentVersion = null;
            if (Path.Exists(DaemonBinary))
            {
                var (code, stdout, _) = await RunCommand(DaemonBinary, "--version");
                if (code != 0)
                {
                    logger.LogInformation($"Failed to get current client version: {stdout}");
                }
                else
                {
                    currentVersion = stdout.Split("\n").First().Split("-").First().TrimStart('v');
                }
            }

            Version.TryParse(currentVersion, out var current);
            if (current == null || current.CompareTo(ClientVersion) < 0)
            {
                if (current != null)
                {
                    logger.LogInformation("Client version outdated");
                }

                await Download(ClientVersion);
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
            MonitorStream(process.StandardOutput, cancellationToken);
            MonitorStream(process.StandardError, cancellationToken, true);

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

    private void MonitorStream(StreamReader streamReader, CancellationToken cancellationToken, bool logAll = false)
    {
        Task.Factory.StartNew(async () =>
        {
            while (!streamReader.EndOfStream)
            {
                var line = await streamReader.ReadLineAsync(cancellationToken);
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
        }, cancellationToken);
    }

    public BoltzClient? GetClient(BoltzSettings? settings)
    {
        if (settings is null) return null;
        return settings.CredentialsPopulated() && (settings.GrpcUrl != DefaultUri || Running)
            ? new BoltzClient(clientLogger, settings.GrpcUrl!, settings.Macaroon!, settings.CertFilePath!)
            : null;
    }
}
