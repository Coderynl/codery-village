using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codery.Village.Server.Desk;

/// <summary>A refused call to GitHub. Only the status and the path: never the answer, a token or the key.</summary>
public sealed class GitHubException(int status, string path) : Exception($"GitHub answered {status} on {path}")
{
    public int Status { get; } = status;
    public string Path { get; } = path;
}

/// <summary>The village as a GitHub App: signs a JWT, trades it for an installation token and keeps that until shortly before it expires.</summary>
public sealed partial class GitHubApp(HttpClient http, DeskOptions options, TimeProvider time) : IVillageGitHub
{
    private const string Api = "https://api.github.com";
    private const int MaxPages = 50;
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<int> OpenIssue(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellation)
    {
        using var answer = await Post($"/repos/{options.Repository}/issues", new { title, body, labels }, cancellation);
        return answer.RootElement.GetProperty("number").GetInt32();
    }

    public async Task<long> Comment(int issue, string body, CancellationToken cancellation)
    {
        using var answer = await Post($"/repos/{options.Repository}/issues/{issue}/comments", new { body }, cancellation);
        return answer.RootElement.GetProperty("id").GetInt64();
    }

    public async Task<IReadOnlyList<IssueComment>> Comments(int issue, CancellationToken cancellation)
    {
        var comments = new List<IssueComment>();
        await Pages($"/repos/{options.Repository}/issues/{issue}/comments?per_page=100", item => comments.Add(new IssueComment(
            item.GetProperty("id").GetInt64(),
            Login(item),
            item.GetProperty("body").GetString() ?? "",
            item.GetProperty("created_at").GetDateTimeOffset())), cancellation);
        return comments;
    }

    public async Task<IReadOnlyList<IssueSummary>> OpenIssues(string label, CancellationToken cancellation)
    {
        var issues = new List<IssueSummary>();
        await Pages($"/repos/{options.Repository}/issues?state=open&labels={Uri.EscapeDataString(label)}&per_page=100", item =>
        {
            // GitHub lists pull requests as issues too
            if (!item.TryGetProperty("pull_request", out _))
                issues.Add(new IssueSummary(item.GetProperty("number").GetInt32(), item.GetProperty("title").GetString() ?? "", Login(item)));
        }, cancellation);
        return issues;
    }

    public async Task Label(int issue, string label, CancellationToken cancellation)
    {
        using var answer = await Post($"/repos/{options.Repository}/issues/{issue}/labels", new { labels = new[] { label } }, cancellation);
    }

    /// <summary>Each item of every page of the list at <paramref name="path"/>, at most <see cref="MaxPages"/> pages.</summary>
    private async Task Pages(string path, Action<JsonElement> read, CancellationToken cancellation)
    {
        var next = new Uri(Api + path);
        for (var pages = 0; next is not null && pages < MaxPages; pages++)
        {
            using var response = await Authorized(HttpMethod.Get, next, null, cancellation);
            using var page = await Read(response, path, cancellation);
            foreach (var item in page.RootElement.EnumerateArray()) read(item);
            next = NextPage(response, path);
        }
    }

    // a deleted account has no user
    private static string Login(JsonElement item) =>
        item.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? user.GetProperty("login").GetString() ?? "" : "";

    /// <summary>The <c>rel="next"</c> link of the <c>Link</c> header, or null at the last page. Only GitHub's own API is followed: the token goes along.</summary>
    private static Uri? NextPage(HttpResponseMessage response, string path)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        var match = NextLink().Match(string.Join(",", values));
        if (!match.Success) return null;
        if (!Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.github.com")
            throw new GitHubException(0, path);
        return uri;
    }

    [GeneratedRegex("<([^>]+)>\\s*;\\s*rel=\"next\"")]
    private static partial Regex NextLink();

    private async Task<JsonDocument> Post(string path, object body, CancellationToken cancellation)
    {
        using var response = await Authorized(HttpMethod.Post, new Uri(Api + path), body, cancellation);
        return await Read(response, path, cancellation);
    }

    /// <summary>A call with the installation token. A 401 means the cached token is no good (revoked): drop it and try once with a new one.</summary>
    private async Task<HttpResponseMessage> Authorized(HttpMethod method, Uri uri, object? body, CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = Request(method, uri, await Token(cancellation));
            if (body is not null) request.Content = JsonContent.Create(body);
            var response = await http.SendAsync(request, cancellation);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
            await Forget(cancellation);
        }
    }

    private async Task Forget(CancellationToken cancellation)
    {
        await _tokenLock.WaitAsync(cancellation);
        _token = null;
        _tokenLock.Release();
    }

    private static async Task<JsonDocument> Read(HttpResponseMessage response, string path, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode) throw new GitHubException((int)response.StatusCode, path);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), cancellationToken: cancellation);
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string bearer)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("codery-village");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private async Task<string> Token(CancellationToken cancellation)
    {
        await _tokenLock.WaitAsync(cancellation);
        try
        {
            if (_token is not null && time.GetUtcNow() < _expires - RenewBefore) return _token;

            using var found = await AsApp(HttpMethod.Get, $"/repos/{options.Repository}/installation", cancellation);
            var id = found.RootElement.GetProperty("id").GetInt64();
            using var made = await AsApp(HttpMethod.Post, $"/app/installations/{id}/access_tokens", cancellation);
            _token = made.RootElement.GetProperty("token").GetString()!;
            _expires = made.RootElement.GetProperty("expires_at").GetDateTimeOffset();
            return _token;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task<JsonDocument> AsApp(HttpMethod method, string path, CancellationToken cancellation)
    {
        using var request = Request(method, new Uri(Api + path), Jwt());
        using var response = await http.SendAsync(request, cancellation);
        return await Read(response, path, cancellation);
    }

    private string Jwt()
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 9 * 60, iss = options.AppId }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
