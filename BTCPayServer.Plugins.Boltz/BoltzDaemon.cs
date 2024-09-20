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
using Octokit;
using Org.BouncyCastle.Bcpg.OpenPgp;
using FileMode = System.IO.FileMode;
using FileStream = System.IO.FileStream;
using OperationCanceledException = System.OperationCanceledException;
using SHA256 = System.Security.Cryptography.SHA256;

namespace BTCPayServer.Plugins.Boltz;

public class BoltzDaemon(
    IOptions<DataDirectories> dataDirectories,
    ILogger<BoltzDaemon> logger,
    ILogger<BoltzClient> clientLogger,
    BTCPayNetworkProvider btcPayNetworkProvider
)
{
    private static readonly Version MinClientVersion = new("2.1.7");

    private readonly Uri _defaultUri = new("http://127.0.0.1:9002");
    private readonly GitHubClient _githubClient = new(new ProductHeaderValue("Boltz"));
    private Stream? _downloadStream;
    private Task? _updateTask;
    private Task? _startTask;
    private CancellationTokenSource? _daemonCancel;
    private Task? _daemonTask;
    private readonly List<string> _output = new();
    private readonly HttpClient _httpClient = new();
    private const int MaxLogLines = 150;
    private string DataDir => Path.Combine(dataDirectories.Value.DataDir, "Plugins", "Boltz");
    private string ConfigPath => Path.Combine(DataDir, "boltz.toml");
    private string DaemonBinary => Path.Combine(DataDir, "bin", $"linux_{Architecture}", "boltzd");
    private string DaemonCli => Path.Combine(DataDir, "bin", $"linux_{Architecture}", "boltzcli");
    private BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
    private readonly SemaphoreSlim _configSemaphore = new(1, 1);

    public bool Starting => _startTask is not null && !_startTask.IsCompleted;
    public bool Updating => _updateTask is not null && !_updateTask.IsCompleted;
    public string LogFile => Path.Combine(DataDir, "boltz.log");
    public string? AdminMacaroon { get; private set; }
    public Release? LatestRelease { get; private set; }
    public string? CurrentVersion { get; set; }
    public BoltzClient? AdminClient { get; private set; }
    public bool Running => AdminClient is not null;
    public readonly TaskCompletionSource<bool> InitialStart = new();
    public event EventHandler<GetSwapInfoResponse>? SwapUpdate;
    public ILightningClient? Node;
    public string? NodeError { get; private set; }
    public string? Error { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string RecentOutput => string.Join("\n", _output);
    public bool UpdateAvailable => LatestRelease!.TagName != CurrentVersion;

    private string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        _ => throw new NotSupportedException("Unsupported architecture")
    };

    private async Task SwapUpdateStream(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var stream = AdminClient!.GetSwapInfoStream("", cancellationToken);
                while (await stream.ResponseStream.MoveNext(cancellationToken))
                {
                    SwapUpdate?.Invoke(this, stream.ResponseStream.Current);
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(e, "Error in swap stream");
                await Task.Delay(3000, cancellationToken);
            }
        }
    }

    public async Task Wait(CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(DataDir, "macaroons", "admin.macaroon");
            while (!File.Exists(path))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }

            logger.LogDebug("Admin macaroon found");

            var reader = await File.ReadAllBytesAsync(path, cancellationToken);
            AdminMacaroon = Convert.ToHexString(reader).ToLower();
            var client = new BoltzClient(clientLogger, _defaultUri, AdminMacaroon, "all");

            while (true)
            {
                try
                {
                    var info = await client.GetInfo(cancellationToken);
                    logger.LogInformation("Running");
                    AdminClient = client;
                    CurrentVersion = info.Version.Split("-").First();
                    Error = null;
                    return;
                }
                catch (RpcException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
        }
        catch (TaskCanceledException)
        {
            Error = "Start failed";
        }
        catch (Exception e)
        {
            Error = e.Message;
        }

        await Stop();
    }

    public async Task CheckLatestRelease()
    {
        try
        {
            LatestRelease = await _githubClient.Repository.Release.GetLatest("BoltzExchange", "boltz-client");
        }
        catch (Exception e)
        {
            logger.LogWarning($"Could not get latest release from github: {e.Message}");
        }
    }


    public async Task<bool> TryDownload()
    {
        var version = LatestRelease!.TagName;
        try
        {
            await Download(version);
            return true;
        }
        catch (Exception e)
        {
            Error = $"Failed to download client version {version}: {e.Message}";
            logger.LogError(e, Error);
        }

        return false;
    }

    private async Task Update()
    {
        try
        {
            await Stop();
            await Download(LatestRelease!.TagName);
            await Start();
        }
        catch (Exception e)
        {
            Error = $"Failed to update: {e.Message}";
            logger.LogError(e, Error);
        }
    }

    public void StartUpdate()
    {
        if (!Updating)
        {
            _updateTask = Update();
        }
    }

    public async Task Download(string version)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new NotSupportedException("Only linux is supported");
        }

        logger.LogInformation($"Downloading boltz client {version}");

        string archiveName = $"boltz-client-linux-{Architecture}-{version}.tar.gz";
        await using var s = await _httpClient.GetStreamAsync(ReleaseUrl(version) + archiveName);

        _downloadStream = s;
        await using var gzip = new GZipStream(s, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, DataDir, true);
        _downloadStream = null;

        await CheckBinaries(version);
    }

    private string ReleaseUrl(string version)
    {
        return $"https://github.com/BoltzExchange/boltz-client/releases/download/{version}/";
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

    private async Task CheckBinaries(string version)
    {
        string releaseUrl = ReleaseUrl(version);
        string manifestName = $"boltz-client-manifest-{version}.txt";
        string sigName = $"boltz-client-manifest-{version}.txt.sig";
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


    public async Task TryConfigure(ILightningClient? node)
    {
        try
        {
            if (node != null)
            {
                _output.Clear();
                await Configure(node);
                if (String.IsNullOrEmpty(Error))
                {
                    Node = node;
                    return;
                }

                logger.LogInformation("Could not connect to node: " + Error);
                NodeError = String.IsNullOrEmpty(RecentOutput) ? Error : $"{Error}\nOutput:\n{RecentOutput}";
            }

            await Configure(null);
        }
        finally
        {
            InitialStart.TrySetResult(Running);
        }
    }

    private string GetConfig(ILightningClient? node)
    {
        var networkName = BtcNetwork.NBitcoinNetwork.ChainName.ToString().ToLower();

        string shared = $"""
                         network = "{networkName}"
                         referralId = "btcpay"
                         logmaxsize = 1

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
                            node = "cln"
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
                    if (dir != null)
                    {
                        cert = Path.Combine(dir, "tls.cert");
                    }
                    else
                    {
                        throw new Exception("No cert found in lnd connection string");
                    }
                }

                return $"""
                        node = "lnd"
                        {shared}

                        [LND]
                        host = "{url.Host}"
                        macaroon = "{macaroon}"
                        certificate = "{cert}"
                        """;
            default:
                throw new Exception("Unsupported lightning client");
        }
    }

    private async Task Configure(ILightningClient? node)
    {
        await _configSemaphore.WaitAsync();
        if (!File.Exists(DaemonBinary))
        {
            await Update();
        }

        try
        {
            await File.WriteAllTextAsync(ConfigPath, GetConfig(node));
            await Start(node == null);
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            _configSemaphore.Release();
        }
    }

    public async Task Init()
    {
        logger.LogDebug("Initializing");
        if (!Directory.Exists(DataDir))
        {
            Directory.CreateDirectory(DataDir);
        }

        await CheckLatestRelease();

        if (!File.Exists(DaemonBinary))
        {
            await TryDownload();
        }
        else
        {
            var (code, stdout, _) = await RunCommand(DaemonBinary, "--version");
            if (code != 0)
            {
                await TryDownload();
            }
            else
            {
                CurrentVersion = stdout.Split("\n").First().Split("-").First();
            }
        }
    }

    private async Task<bool> CheckVersion()
    {
        Version.TryParse(CurrentVersion?.Remove(0, 1), out var current);
        if (current == null || current.CompareTo(MinClientVersion) < 0)
        {
            logger.LogInformation("Client version too old, updating");
            return await TryDownload();
        }

        return true;
    }

    private async Task Start(bool logOutput = true)
    {
        await Stop();
        if (await CheckVersion())
        {
            logger.LogInformation("Starting client process");
            var daemonCancel = new CancellationTokenSource();
            _daemonTask = Task.Factory.StartNew(async () =>
            {
                _output.Clear();
                using var process = NewProcess(DaemonBinary, $"--datadir {DataDir}");
                try
                {
                    process.Start();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to start client process");
                    Error = $"Failed to start process {e.Message}";
                    return;
                }

                MonitorStream(process.StandardOutput, daemonCancel.Token);
                MonitorStream(process.StandardError, daemonCancel.Token, true);

                try
                {
                    await process.WaitForExitAsync(daemonCancel.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    throw;
                }

                var wasRunning = Running;
                AdminClient?.Dispose();
                AdminClient = null;

                if (process.ExitCode != 0)
                {
                    if (logOutput || wasRunning)
                    {
                        Error = $"Process exited with code {process.ExitCode}";
                        logger.LogError(Error);
                        logger.LogInformation(RecentOutput);
                    }

                    if (wasRunning)
                    {
                        logger.LogInformation("Restarting in 10 seconds");
                        await Task.Delay(10000, _daemonCancel.Token);
                        InitiateStart();
                    }
                }

                await daemonCancel.CancelAsync();
            }, daemonCancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var wait = CancellationTokenSource.CreateLinkedTokenSource(daemonCancel.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(60));
            _daemonCancel = daemonCancel;
            await Wait(wait.Token);
            if (Running)
            {
                _ = SwapUpdateStream(daemonCancel.Token);
            }
        }
    }

    public void InitiateStart()
    {
        if (!Starting)
        {
            _startTask = Start();
        }
    }

    public async Task Stop()
    {
        if (_daemonTask is not null)
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
                    if (_daemonCancel is not null)
                    {
                        await _daemonCancel.CancelAsync();
                    }
                }
            }

            await _daemonTask;
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
            UseShellExecute = false,
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
        return settings.CredentialsPopulated() && (settings.GrpcUrl != _defaultUri || Running)
            ? new BoltzClient(clientLogger, settings.GrpcUrl!, settings.Macaroon!)
            : null;
    }
}