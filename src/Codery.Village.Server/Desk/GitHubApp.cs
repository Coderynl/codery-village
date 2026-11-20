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
        string? next = $"/repos/{options.Repository}/issues/{issue}/comments?per_page=100";
        while (next is not null)
        {
            using var request = Request(HttpMethod.Get, next, await Token(cancellation));
            using var response = await http.SendAsync(request, cancellation);
            using var page = await Read(response, next, cancellation);
            foreach (var item in page.RootElement.EnumerateArray())
                comments.Add(new IssueComment(
                    item.GetProperty("id").GetInt64(),
                    item.GetProperty("user").GetProperty("login").GetString() ?? "",
                    item.GetProperty("body").GetString() ?? "",
                    item.GetProperty("created_at").GetDateTimeOffset()));
            next = NextPage(response);
        }
        return comments;
    }

    public async Task Label(int issue, string label, CancellationToken cancellation)
    {
        using var answer = await Post($"/repos/{options.Repository}/issues/{issue}/labels", new { labels = new[] { label } }, cancellation);
    }

    /// <summary>The path and query of <c>rel="next"</c> in the <c>Link</c> header, or null at the last page.</summary>
    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        var match = NextLink().Match(string.Join(",", values));
        return match.Success ? new Uri(match.Groups[1].Value).PathAndQuery : null;
    }

    [GeneratedRegex("<([^>]+)>\\s*;\\s*rel=\"next\"")]
    private static partial Regex NextLink();

    private async Task<JsonDocument> Post(string path, object body, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Post, path, await Token(cancellation));
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellation);
        return await Read(response, path, cancellation);
    }

    private static async Task<JsonDocument> Read(HttpResponseMessage response, string path, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode) throw new GitHubException((int)response.StatusCode, path);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), cancellationToken: cancellation);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string bearer)
    {
        var request = new HttpRequestMessage(method, path);
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
        using var request = Request(method, path, Jwt());
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
