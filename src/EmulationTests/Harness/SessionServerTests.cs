using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation;
using DuetEmulator;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// The session server refuses requests from other web sites. A page in the developer's browser can send
/// it a POST without a CORS preflight as long as the request is "simple" (text/plain), and the browser
/// then sends the page's own Origin with it; such a request must not reach the API
/// </summary>
[TestFixture]
public class SessionServerTests
{
    /// <summary>The Origin a browser sends with a request made by a page on another site</summary>
    private const string OtherSite = "https://attacker.example";

    private CancellationTokenSource _stop = null!;
    private SessionServer _server = null!;
    private Task _running = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task StartAsync()
    {
        _stop = new CancellationTokenSource();
        _server = new SessionServer(FreePort(), Runs.Directory(this));
        _running = _server.RunAsync(_stop.Token);
        _client = new HttpClient { BaseAddress = new Uri(_server.Prefix), Timeout = TimeSpan.FromMinutes(5) };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                (await _client.GetAsync("api/session")).EnsureSuccessStatusCode();
                break;
            }
            catch (HttpRequestException) when (attempt < 100)
            {
                await Task.Delay(50);
            }
        }
    }

    [TearDown]
    public async Task StopAsync()
    {
        _client.Dispose();
        await _stop.CancelAsync();
        await _running;
        await _server.DisposeAsync();
        _stop.Dispose();
    }

    [Test]
    public async Task ARequestFromAnotherSiteIsRefused()
    {
        using HttpResponseMessage response = await PostFromOtherSiteAsync("api/scenario", """{"path":"build/emulation/never-written.yaml"}""");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
                    $"a cross-site text/plain POST is refused before the API sees it; it answered {await response.Content.ReadAsStringAsync()}");
    }

    [Test]
    public async Task ABodyWithAnOriginOfItsOwnIsRefused()
    {
        using HttpResponseMessage response = await PostAsync("api/scenario", """{"path":"build/emulation/never-written.yaml"}""", "application/json", OtherSite);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), await response.Content.ReadAsStringAsync());
    }

    [TestCase("text/plain")]
    [TestCase("application/x-www-form-urlencoded")]
    [TestCase("multipart/form-data; boundary=x")]
    [TestCase(null)]
    public async Task ABodyABrowserSendsWithoutAPreflightIsRefused(string? contentType)
    {
        using HttpResponseMessage response = await PostAsync("api/scenario", """{"path":"build/emulation/never-written.yaml"}""", contentType, origin: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task ABodilessPostFromAnotherSiteIsRefused()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "api/save");
        request.Headers.Add("Origin", OtherSite);
        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), await response.Content.ReadAsStringAsync());
    }

    // A site whose name has been made to resolve to 127.0.0.1 sends its own name as both Host and Origin
    [Test]
    public async Task ARequestNamingAnotherHostIsRefused()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "api/scenarios");
        request.Headers.Host = $"attacker.example:{_client.BaseAddress!.Port}";
        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), await response.Content.ReadAsStringAsync());
    }

    [TestCase("127.0.0.1", null, "application/json")]
    [TestCase("127.0.0.1", null, "application/yaml")]
    [TestCase("localhost", null, "application/json; charset=utf-8")]
    [TestCase("localhost", 18770, "application/json")]
    public async Task ThePagesOwnRequestsAreAccepted(string host, int? forwardedPort, string contentType)
    {
        string authority = $"{host}:{forwardedPort ?? _client.BaseAddress!.Port}";
        using HttpRequestMessage request = new(HttpMethod.Post, "api/scenario")
        {
            Content = new StringContent("{}", Encoding.UTF8)
        };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Host = authority;
        request.Headers.Add("Origin", $"http://{authority}");
        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("No session"), "the request reaches the API, which has no session to export");
    }

    [Test]
    public async Task AScriptsRequestsAreAccepted()
    {
        using HttpResponseMessage response = await PostAsync("api/scenario", "{}", "application/yaml", origin: null);

        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("No session"), "the request reaches the API, which has no session to export");
    }

    [Test]
    [Category("Emulation")]
    [Category(nameof(Stack.Rrf))]
    public async Task AnotherSiteCannotStartASessionOrOverwriteARepositoryFile()
    {
        string relative = "build/emulation/session-server-tests/victim.yaml";
        string victim = Path.Combine(RepositoryPaths.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(victim)!);
        const string original = "original: true\n";
        await File.WriteAllTextAsync(victim, original);
        try
        {
            using HttpResponseMessage started = await PostFromOtherSiteAsync("api/session", """
                name: cross-site
                stacks: [rrf]
                boards:
                  1: EXP3HC
                """);
            using HttpResponseMessage saved = await PostFromOtherSiteAsync("api/scenario", $$"""{"path":"{{relative}}","overwrite":true}""");

            Assert.Multiple(async () =>
            {
                Assert.That(started.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
                            $"another site cannot start a session; it answered {await started.Content.ReadAsStringAsync()}");
                Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
                            $"another site cannot save a scenario; it answered {await saved.Content.ReadAsStringAsync()}");
                Assert.That(await File.ReadAllTextAsync(victim), Is.EqualTo(original), $"{relative} is left as it was");
            });
        }
        finally
        {
            File.Delete(victim);
        }
    }

    // What fetch(url, {method: 'POST', mode: 'no-cors', body}) sends from a page on another site
    private Task<HttpResponseMessage> PostFromOtherSiteAsync(string path, string body) => PostAsync(path, body, "text/plain", OtherSite);

    private async Task<HttpResponseMessage> PostAsync(string path, string body, string? contentType, string? origin)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };
        if (contentType is not null)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return await _client.SendAsync(request);
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
