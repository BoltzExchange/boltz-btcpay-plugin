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
    private static CancellationTokenSource? _daemonCancel;
    private readonly List<string> _output = new();
    private readonly HttpClient _httpClient = new();
    private const int MaxLogLines = 200;
    private string? CurrentVersion { get; set; }
    private string DataDir => Path.Combine(dataDirectories.Value.DataDir, "Plugins", "Boltz");
    private string ConfigPath => Path.Combine(DataDir, "boltz.toml");
    private string DaemonBinary => Path.Combine(DataDir, "bin", $"linux_{Architecture}", "boltzd");
    private string DaemonCli => Path.Combine(DataDir, "bin", $"linux_{Architecture}", "boltzcli");
    private BTCPayNetwork BtcNetwork => btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
    private readonly SemaphoreSlim _configSemaphore = new(1, 1);

    public string LogFile => Path.Combine(DataDir, "boltz.log");
    public string? AdminMacaroon { get; private set; }
    public Release? LatestRelease { get; private set; }
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
        _updateTask = Update();
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

    private const string PgpKey = """
                                  -----BEGIN PGP PUBLIC KEY BLOCK-----

                                  mQINBFqdS8wBEAC5QzhAhXb0yeDLKrH8XLtSIxsc3f/ydC29uxQKowhVL1hrgCQO
                                  fNAhms3l5l13wqLav4eEeKhte8Fqd7kBk5Aw0dGsuEsMhpCCkh1hu7AY17xouzYH
                                  hoUf5EsFrEilpqoNHAcShDahnXYqZtIn8n8IVX/C9ihpQu56AsuLtKfWcohoP4+c
                                  /fcSryiDQtF/gy6QigfLA0YkViK34hDctmNpsnLN3Xw/2VHCcxx+QJ1f636FIH8C
                                  9/Jkzc2VsI+ju3i4PtW/LpPNbkdxr4NCuqiPjBC+5zckq3bU0ZQHn6jMOpasNGzs
                                  t8JQmDteHSaPpwqdroZS/t9kACh83LLFj1aUn+A7kX/vKu3ZSOFO/P/Y7j1VYeAb
                                  mYToeYeI63Rp4wM/dUKlp3p+c1QxdDPYFsaHHCTB/4WwvKOdtE8vZQUug8pPcc/b
                                  6xTIMn/El3GIaGYZpQbQZ+3rV8EwYkDoOU5vG8pQyvNlOqcBbSQkVE927UNpRFpM
                                  0VTxwtlaFTVSB9jjsNQobHuc1CjW0aPG0hwgVQc4V6FYq6URDkduNc57xeKiWYn/
                                  7tvcTVvOdSUJvKHTMmI4xJmQYiBjzi3+CGWVr+AcDbEfklk3eAfKQDDDPr72QQ6z
                                  +5c69kWjTM/VFIWIpXs5AauL5046Aq0VtJtYeF1JlHWguQXBxkV4ld48SQARAQAB
                                  tBtNaWNoYWVsIDxtZUBtaWNoYWVsMTAxMS5hdD6JAlQEEwEIAD4CGyMFCwkIBwIG
                                  FQoJCAsCBBYCAwECHgECF4AWIQTCZA9jBXD17e3gLeaE0km6cWhdRgUCZBXmtAUJ
                                  DTsB5QAKCRCE0km6cWhdRlA4D/90oA971y8i/hnGGbi3oK/30YOZKlNZ9/izqlHg
                                  5cyURBTA1+QzD9/TnDs59Z/MJXRINYlm/ZlYLvW2AjHXpIfdUl5oS0IFgFfr4HOQ
                                  yMA6VNnDYZTwZvzAwIf88qMFoXDQqaw89OuO8IOkKs/u8ivhDkM2NmyRmCdm4QQb
                                  Ule73mkGgwN9iUFS1woujVx5SoynK9uAQI4wWU8Osejft0AzZ2wSegQ8bujbLoqB
                                  vNB/sA5Whcbg8kG/gCO+hNJfDK7mROGbdizO8Y26ixQwps2NzKMFiPr6mpy9gbZl
                                  l5NTYlfg+TzcmtSnFFSC6lsX8iXDsYWdGcX+ERvhLe+TWcd6ztlaB0RUZX8SIa/H
                                  2yd8Kiu3sIQz/xTr45hK1hXRM0HUKYw0wrmVjmFGlKto2Aw3pRBfAxZQByB0It02
                                  sEBAApyQRpBWTHBy3yjCBz1GpeY5upR9KS+dqLveLdDPRlCQnMyEJKX0+hQVUnfv
                                  GD+02Q5LcLqsFq4NtY2jMI6OSXjMH5PwvDJmzKilqT70ebcKH6jCfo0/Nn39yyPo
                                  ICkkfjZo9i1pXUyhtOXBj8xEAazsRB/gnNn/itl8czs8aDMuGUl+uPbdsYShVI8J
                                  8BrGF4eRlzNLNIs3PJRGxJ0G9RS4rBGulbMUnPDt4t2NRX2E/4nCNLRN4MSMI29O
                                  eczS0YkCVAQTAQgAPhYhBMJkD2MFcPXt7eAt5oTSSbpxaF1GBQJanVAOAhsjBQkJ
                                  ZgGABQsJCAcCBhUKCQgLAgQWAgMBAh4BAheAAAoJEITSSbpxaF1GtjcP/0gXYhlo
                                  IVXTtXvnShE4a1ZtUtU9EttX8icZfDe0gXn5D4lQwTgknWoDryvqeyDpsGtjHB13
                                  XpCJ32W/l3A067FyQ8/tUlrBX9cgmIbzSBiWw+Oe7HPIp9KcI2vudXAM0CHzID0l
                                  2dW+IVBalWJ2Bq/6kLHf+jHQAkfYzDrdfU1fWfVl0NEwhYiDY9qtP2WuTGUP1RVa
                                  /685HxQyiHfyiYWoJd3uDm03VAzZfeRnijNdAVZ76cwfyg2TA8g+eE2RZkcmZFm/
                                  dgB7DEvr0rPaM92vFAsxDQhUvgT38M1U+tTLjp31rLrb83IJkMNrjyVXXkFUqMac
                                  lvtAZLD+GJHGdDot2i/rdO2at+pvSzyLZBsSmSeMlH6yz3NScPeFuvkO3WNnl0D8
                                  ko/rAH85XZuoGcUGVHy5RE+9mzmU8QjBXnAeFV+etmlpUaJPzXeg+cmgFTpXuHwA
                                  u9zJqWzJNqKMw55tOa5MQyE0eN+GLLnn4x3rpnmtOkgP575tjqFlPCKONlNKEQRC
                                  4Dr6pC8729Bc2icvVYq/KuqwdDpj/iomjqh0Hm+GDydXdMOQRh1qO2fXw51uxIck
                                  S09lGIRUpvSU0tvpShde1RiGUXXw6a7PU5Yxqa9Mf/nkkcb/yddazOTuUjP4hceT
                                  qlgHitqXRx/Nl7GrO984ioRiVEKIsPteEiiMtB5NaWNoYWVsIDxtaWNoYWVsMTAx
                                  MTAxQG1lLmNvbT6JAlQEEwEIAD4CGyMFCwkIBwIGFQoJCAsCBBYCAwECHgECF4AW
                                  IQTCZA9jBXD17e3gLeaE0km6cWhdRgUCZBXmsQUJDTsB5QAKCRCE0km6cWhdRsds
                                  D/0YMb24yEnDZczVBZUYkFrEbERJ6ozeNwTeJt6W7YpEdkOyY0HcmPJFhRV0fclt
                                  P6EnIpbOEMAQtAxPiZAnWrT24PsP059BX/UxLqplvEgaAKEX6dVpLilI1y1dP7Dn
                                  wl2Z91QdF0gnI3+0Lan34J1BW67EmoPJUIdg+FIEGyuhsxVOhL4CJNqLU5KoicZi
                                  KeUm5EGXtQOnRyjq5rnrDQQtlu4T7gN3OtPDEOUwFunKyfb73+YIzK4XigPbmE8T
                                  pFHoffn9CD+g75NsnbRKB90opNIQgt9Q3rQ3l4nJrpK9FtP8wRjKjm+WtrfDJzpj
                                  s3DWQ6/I6viVyeWF5tQQbGL9YwhBRWO+SRNf80EGn8IEsmp3kxCAqKFhOSrS6JrK
                                  ZUpdkuDUTMxa1OXL2F3aSmcH1v+Fyd0mCUrhQTMn600gL8jaKHdJOPKhXGb+giQO
                                  QdSt3KSgOmJhldASBG1tyiyP/EaprqO4Yg8gtNyL1JXWFnRigklhK2UN/P37X3jA
                                  n+I+In0ABqUa0PQxZz4zR3C1r9n7mRuWviC650QQFAwJ55ddhw8211LF5caU89kx
                                  AcDcDgBj7s7Efau2Q4LAcbap5M51GFFxzeweXrD+RCXGG8rExKqwf7FEcfZY3gHw
                                  cs+1Jbi2B/A8Y+EcxNKZBcmvC+wB9vrPfL/ag7l7LthiD4kCVAQTAQgAPhYhBMJk
                                  D2MFcPXt7eAt5oTSSbpxaF1GBQJanUvMAhsjBQkJZgGABQsJCAcCBhUKCQgLAgQW
                                  AgMBAh4BAheAAAoJEITSSbpxaF1GK2AP/28AI/BMPb8NUmNnBhae6DLSNsRU4rat
                                  Eb9kQrOVui13wdO5xr1aEKc+Wd+cGxIHiTG+lTT59Fw4G+OP1BLP9I9iwRCRU3q5
                                  BIuEmblPamyXvoh2lHThu7Rjqv32eMolLQOiWGjb1gIEitdo7CPv41/F/v2xHcfh
                                  8ZvRAQjLlUkTYBoXKmtdOXp2Jb3u6cOmCokO2fZf+HxkHQw14aiCK5tIYrEFDbxh
                                  +eraFk+JBLFAbn3jU5htvlnumh+AwbuISCHJ7PepfRgEesOFUR5Hd+RiPw1jR9Bu
                                  qrTCNQ9CZUyQyau8Qp4+Mn8Sgr+QZhWPUr4tYxKGDQa6FBt5ai1qWPGdKV8WlVMX
                                  8fHWnEjjPdweHYwNIEnLV1seABqkSCwZescfVcxKvzFZNP7B94RvOWMUjJfjXJ5D
                                  jLC1pGRtai+UGyE2wi+EczysgSLbp43N0g9zuGZmPRSmzpPp9nIJ4qouOAUd7CMB
                                  bnhRFTgSRvJ5XwykZLwbIU+OMRvK9EUvrXXEDZ0pMXfloQpEW5TkmeSZ3HNeAQaU
                                  lYp2eHR0nhoNQ1czjPQvEebg9vRw+Wfd7tWyxjQTmozC6vr2LMLD4wcsF9VfDMrt
                                  0QABAxhM9QRMYoht+hQlwHWy7Pcd2rLAZND/2FRgQZEn+aX1YBRXPZxotR77oQhQ
                                  hdCzkdrB1RJxuQINBFqdS8wBEADKQSi+BGpepbVmAxpogj/kctp68Y9ZiEwfVjOU
                                  uUYB/yPmWpvqjCPikvJ86DOE2jQOMq9HK68/6YYl9LLcwULnoOatn8t6PrySxcx0
                                  lAEmsOY/9MeylS7rqmLBUFCiV1sTPdzYrAr8hbpG308YKOu6L31c/uedbknMBZh+
                                  pmY2vvr7RFP8JJGfPS2pFKr5z/EyhB5J4mckpEAgMVbqmQdHV3aWvK9qbs/fHHoZ
                                  vL+l66Qf1w1Ut0WI8h/RmF4WTvnsopVwVK7S3Va9+vxkwfaslEFgaGBlebKD/D4m
                                  17QRhy1ljaIYNMo3GIP9v8k8Xms8gce52bALPR3f2MWdxmjDQZM/w9yPR09r77bn
                                  /DfZNg9Uqx4oJlewaKfrjnIIMP+OLMGn8Ey1ARw5o0rw/u0x9WauEo8DSrchyeoV
                                  XVJPrspPiZTbI/t8sMvegWS1HhTXhx/jNdSI3JpPLua57Jy4lDC0unTVAUykV6me
                                  +59fVdlbtC9WsBZ+n2G54rShQruoKJCoWrovT/0lVNq0p7IKkgTi2HqsOVQVuxpe
                                  i/57DGXeb/4C4gkMbWfMAnrl1nYhn5OBQH+5k6vNG/8SW81AD4oPo/m7bE7oCCTN
                                  T0Pz7mfX8S0Veky0gTDxksPcwlznkooHVkb7LE+P1iJSDye/8AmWJt+LYvmtLst8
                                  NiNnOQARAQABiQI8BBgBCAAmAhsMFiEEwmQPYwVw9e3t4C3mhNJJunFoXUYFAmQV
                                  52YFCQ07ApoACgkQhNJJunFoXUa/5w/8CTP/s3dcqnXAHp3Js3XA6Sigi2zLcjjH
                                  8AjreFySKK0nE6Lyu774wuna0sWIyD/RNFodWoh9MzLTpeaMKzdL852GWs2ZiwJt
                                  gH3baNlBKq29wjFbaqOEgToNOPn8H7mtU7jo33M2ZMLBlpmXepqwjUl0W5bXAFVd
                                  sOIjpLDnsED9Yr7bCxJ+DLRpioIdSRBmMWxnGc/8ZXEeW4mGbhFCV7zCvGhpaIEt
                                  zX3d1yOMA7xn3W+U1gSjAVkOrDRVppp/PQ0PlpSZ+o8BhODkK8xsPnevhWmrmgNI
                                  HKFZwFJsPRxvdis/uueUMXU6lPaY+ocJl99b1TQiTk/X3sJb7Q0ERaYX1jYB4HBX
                                  CPPoWBcdv0h3ZZ6pvzhpZNrtPCABoRzydz+nD5ZtpPZ2grNt8KTXXkW2FPtRAwPA
                                  OxZKo2biVKtMydpCZ6SJtLMpREpWRAe+cPXv51nDrKK5BrY+0gq6bpKYd/Opir60
                                  3/GdTOel+DmRDmIzJHpET3umEYN1BWZwiZuo94AuS1hv0MYaN4HaImwoHYlbMSMW
                                  23NfGBCbOA6w/qgNYQ8Pod/E1Bvhr/BIOzK05ofc/CXP3zF/HknW+6PEF5YoctQH
                                  X98vuKVOWALnr6Ulmh/pHqKU1AKPzxOhIaYMLg1RS6I4BNobcR/IY3bp20BMPsre
                                  ikvlnCs353I=
                                  =RzTC
                                  -----END PGP PUBLIC KEY BLOCK-----
                                  """;
}