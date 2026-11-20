using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Codery.Village.Server.Desk;

namespace Codery.Village.Tests;

/// <summary>The village's GitHub App: signs in as the App, gets an installation token, and speaks to the issues.</summary>
public class GitHubAppTests
{
    private sealed class WallClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Seen(HttpMethod Method, string Path, string? Authorization, string Body);

    private sealed class FakeGitHub(Func<Seen, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation);
            var seen = new Seen(request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body);
            Requests.Add(seen);
            return answer(seen);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private readonly RSA _key = RSA.Create(2048);
    private readonly WallClock _clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly string _expires = "2026-11-20T13:00:00Z";
    private int _tokens;

    private DeskOptions Options() => new()
    {
        Enabled = true,
        Repository = "Coderynl/codery-village",
        AppId = "12345",
        PrivateKey = _key.ExportRSAPrivateKeyPem(),
    };

    /// <summary>GitHub as far as the App needs it; <paramref name="more"/> answers the repo calls.</summary>
    private FakeGitHub GitHub(Func<Seen, HttpResponseMessage> more) => new(seen =>
    {
        if (seen.Path == "/repos/Coderynl/codery-village/installation") return Json("""{"id": 77}""");
        if (seen.Path == "/app/installations/77/access_tokens")
            return Json($$"""{"token": "ghs_{{++_tokens}}", "expires_at": "{{_expires}}"}""");
        return more(seen);
    });

    private GitHubApp App(FakeGitHub handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") }, Options(), _clock);

    [Fact]
    public async Task Opening_an_issue_gets_a_token_once_and_sends_it()
    {
        var handler = GitHub(seen => Json("""{"number": 42}"""));
        var app = App(handler);

        var first = await app.OpenIssue("A wish", "Body", ["wish"], default);
        var second = await app.OpenIssue("Another", "Body", [], default);

        Assert.Equal(42, first);
        Assert.Equal(42, second);
        Assert.Single(handler.Requests, r => r.Path == "/app/installations/77/access_tokens");

        var issue = handler.Requests.First(r => r.Path == "/repos/Coderynl/codery-village/issues");
        Assert.Equal("Bearer ghs_1", issue.Authorization);
        using var sent = JsonDocument.Parse(issue.Body);
        Assert.Equal("A wish", sent.RootElement.GetProperty("title").GetString());
        Assert.Equal("wish", sent.RootElement.GetProperty("labels")[0].GetString());

        // The App's own calls carry the JWT: RS256, issued by the App id, valid for nine minutes, signed with the key.
        var jwt = handler.Requests.First(r => r.Path == "/repos/Coderynl/codery-village/installation").Authorization!;
        Assert.StartsWith("Bearer ", jwt);
        var parts = jwt["Bearer ".Length..].Split('.');
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("12345", payload.RootElement.GetProperty("iss").GetString());
        var now = _clock.Now.ToUnixTimeSeconds();
        Assert.Equal(now - 60, payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(now + 9 * 60, payload.RootElement.GetProperty("exp").GetInt64());
        Assert.True(_key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Decode64(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task A_token_is_renewed_before_it_expires()
    {
        var handler = GitHub(seen => Json("""{"id": 9}"""));
        var app = App(handler);

        await app.Comment(1, "one", default);
        _clock.Now += TimeSpan.FromMinutes(54); // still more than five minutes left
        await app.Comment(1, "two", default);
        Assert.Equal(1, _tokens);

        _clock.Now += TimeSpan.FromMinutes(2); // four minutes left
        await app.Comment(1, "three", default);
        Assert.Equal(2, _tokens);
        Assert.Equal("Bearer ghs_2", handler.Requests.Last().Authorization);
    }

    [Fact]
    public async Task Comments_are_read_page_by_page()
    {
        var handler = GitHub(seen => seen.Path.Contains("page=2")
            ? Json("""[{"id": 3, "user": {"login": "eddie"}, "body": "three", "created_at": "2026-11-20T10:02:00Z"}]""")
            : Page("""[{"id": 1, "user": {"login": "michael"}, "body": "one", "created_at": "2026-11-20T10:00:00Z"}, {"id": 2, "user": {"login": "eddie"}, "body": "two", "created_at": "2026-11-20T10:01:00Z"}]""",
                "<https://api.github.com/repositories/1/issues/5/comments?per_page=100&page=2>; rel=\"next\", <https://api.github.com/x?page=2>; rel=\"last\""));

        var comments = await App(handler).Comments(5, default);

        Assert.Equal([1L, 2L, 3L], comments.Select(c => c.Id));
        Assert.Equal("eddie", comments[2].Author);
        Assert.Equal("three", comments[2].Body);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 2, 0, TimeSpan.Zero), comments[2].At);
        Assert.StartsWith("/repos/Coderynl/codery-village/issues/5/comments?per_page=100", handler.Requests.First(r => r.Path.Contains("/comments")).Path);
    }

    [Fact]
    public async Task A_refusal_throws_with_status_and_path_but_without_the_answer()
    {
        var handler = GitHub(seen => Json("""{"message": "secret detail"}""", HttpStatusCode.UnprocessableEntity));

        var thrown = await Assert.ThrowsAsync<GitHubException>(() => App(handler).Label(5, "wish", default));

        Assert.Equal(422, thrown.Status);
        Assert.Equal("/repos/Coderynl/codery-village/issues/5/labels", thrown.Path);
        Assert.DoesNotContain("secret", thrown.Message);
    }

    private static HttpResponseMessage Page(string json, string link)
    {
        var response = Json(json);
        response.Headers.Add("Link", link);
        return response;
    }

    private static byte[] Decode64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
    private static string Decode(string s) => Encoding.UTF8.GetString(Decode64(s));
}
