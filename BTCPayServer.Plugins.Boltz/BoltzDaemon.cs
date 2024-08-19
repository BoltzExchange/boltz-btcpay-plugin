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
using SHA256 = System.Security.Cryptography.SHA256;

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
    private Task? _startTask;
    private static CancellationTokenSource? _daemonCancel;
    private readonly List<string> _output = new();
    private readonly HttpClient _httpClient = new();
    private const int MaxLogLines = 200;
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
    public string RecentOutput => string.Join("\n", _output);
    public bool UpdateAvailable => LatestRelease!.TagName != CurrentVersion;

    private string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        _ => throw new NotSupportedException("Unsupported architecture")
    };

    public async Task<bool> Wait(CancellationToken cancellationToken)
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
            var client = new BoltzClient(_defaultUri, AdminMacaroon);

            while (true)
            {
                try
                {
                    await client.GetInfo(cancellationToken);
                    logger.LogInformation("Running");
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
            Error = "Daemon start failed";
            logger.LogInformation(Error);
            await Stop();
        }

        return false;
    }

    public async Task<Release> GetLatestRelease()
    {
        return await _githubClient.Repository.Release.GetLatest("BoltzExchange", "boltz-client");
    }

    public async Task TryDownload(string version)
    {
        try
        {
            await Download(version);
        }
        catch (Exception e)
        {
            logger.LogTrace(e, $"Failed to download client version {version}");
            Error = e.Message;
        }
    }

    public async Task TryDownload()
    {
        await TryDownload(LatestRelease!.TagName);
    }

    private async Task Update()
    {
        await Stop();
        await TryDownload();
        await Start();
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

        CurrentVersion = version;
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
            Error = e.Message;

            return false;
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
        await _configSemaphore.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(ConfigPath, GetConfig(node));
            await Stop();
            InitialStart.TrySetResult(await Start());
        }
        finally
        {
            _configSemaphore.Release();
        }
    }

    public event EventHandler? OnDaemonExit;

    public async Task Init()
    {
        logger.LogDebug("Initializing");
        if (!Directory.Exists(DataDir))
        {
            Directory.CreateDirectory(DataDir);
        }

        LatestRelease = await GetLatestRelease();
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

    private async Task<bool> Start()
    {
        await Stop();
        logger.LogInformation("Starting daemon");
        _daemonCancel = new CancellationTokenSource();
        _ = Task.Factory.StartNew(async () =>
        {
            while (!_daemonCancel.Token.IsCancellationRequested)
            {
                _output.Clear();
                using var process = StartProcess(DaemonBinary, $"--datadir {DataDir}");

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

    public void InitiateStart()
    {
        if (!Starting)
        {
            _startTask = Start();
        }
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

    public BoltzClient? GetClient(BoltzSettings? settings)
    {
        if (settings is null) return null;
        return settings.CredentialsPopulated() && (settings.GrpcUrl != _defaultUri || Running)
            ? new BoltzClient(settings.GrpcUrl!, settings.Macaroon!)
            : null;
    }
}