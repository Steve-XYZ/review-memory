using System.Net;
using System.Text;
using ReviewMemory.GitHub;

namespace ReviewMemory.GitHub.Tests;

public sealed class GitHubGraphQLClientTests
{
    private const string PageOne = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "pageInfo": { "hasNextPage": true, "endCursor": "CURSOR_UNO" },
                  "nodes": [
                    {
                      "id": "PRRT_kwDOAaa111",
                      "isResolved": true,
                      "comments": { "nodes": [ { "databaseId": 2147483001 } ] }
                    },
                    {
                      "id": "PRRT_kwDOAaa222",
                      "isResolved": false,
                      "comments": { "nodes": [ { "databaseId": 2147483002 } ] }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    private const string PageTwo = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "pageInfo": { "hasNextPage": false, "endCursor": null },
                  "nodes": [
                    {
                      "id": "PRRT_kwDOAaa333",
                      "isResolved": true,
                      "comments": { "nodes": [ { "databaseId": 2147483003 } ] }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    private const string SinglePage = """
        {
          "data": {
            "repository": {
              "pullRequest": {
                "reviewThreads": {
                  "pageInfo": { "hasNextPage": false, "endCursor": null },
                  "nodes": [
                    {
                      "id": "PRRT_kwDOAaa111",
                      "isResolved": true,
                      "comments": { "nodes": [ { "databaseId": 2147483001 } ] }
                    },
                    {
                      "id": "PRRT_kwDOAaa222",
                      "isResolved": false,
                      "comments": { "nodes": [ { "databaseId": 2147483002 }, { "databaseId": 2147483009 } ] }
                    },
                    {
                      "id": "PRRT_kwDOAaa333",
                      "isResolved": true,
                      "comments": { "nodes": [ { "databaseId": 2147483003 } ] }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task Maps_isResolved_to_first_comments_databaseId()
    {
        var handler = new FakeHandler();
        handler.Enqueue(SinglePage);

        var resolved = await NewClient(handler).GetResolvedByRootCommentAsync("acme", "app", 42);

        Assert.Equal(3, resolved.Count);
        Assert.True(resolved[2147483001]);
        Assert.False(resolved[2147483002]);
        Assert.True(resolved[2147483003]);
        Assert.False(resolved.ContainsKey(2147483009));
    }

    [Fact]
    public async Task Pages_by_pageInfo_until_hasNextPage_false()
    {
        var handler = new FakeHandler();
        handler.Enqueue(PageOne);
        handler.Enqueue(PageTwo);

        var resolved = await NewClient(handler).GetResolvedByRootCommentAsync("acme", "app", 42);

        Assert.Equal(3, resolved.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"cursor\":null", handler.Requests[0]);
        Assert.Contains("\"cursor\":\"CURSOR_UNO\"", handler.Requests[1]);
    }

    [Fact]
    public async Task Pr_without_threads_returns_empty_dictionary()
    {
        var handler = new FakeHandler();
        handler.Enqueue("""
            {
              "data": {
                "repository": {
                  "pullRequest": { "reviewThreads": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [] } }
                }
              }
            }
            """);

        var resolved = await NewClient(handler).GetResolvedByRootCommentAsync("acme", "app", 42);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task Http_failure_throws_with_the_status()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.BadGateway);

        var exception = await Assert.ThrowsAsync<GitHubGraphQlException>(
            () => NewClient(handler).GetResolvedByRootCommentAsync("acme", "app", 42));

        Assert.Contains("502", exception.Message);
    }

    [Fact]
    public async Task Rate_limit_in_payload_throws_with_error_message()
    {
        var handler = new FakeHandler();
        handler.Enqueue("""
            {
              "data": null,
              "errors": [
                { "type": "RATE_LIMITED", "message": "API rate limit exceeded for installation ID 1234" }
              ]
            }
            """);

        var exception = await Assert.ThrowsAsync<GitHubGraphQlException>(
            () => NewClient(handler).GetResolvedByRootCommentAsync("acme", "app", 42));

        Assert.Contains("rate limit exceeded", exception.Message);
    }

    private static GitHubGraphQLClient NewClient(FakeHandler handler) =>
        new(new HttpClient(handler), "test-token");

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<string> Requests { get; } = [];

        public void Enqueue(string json) =>
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        public void Enqueue(HttpStatusCode status) =>
            _responses.Enqueue(new HttpResponseMessage(status));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }
}
