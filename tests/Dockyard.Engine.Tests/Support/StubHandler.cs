using System.Net;
using System.Text;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests.Support;

sealed class StubHandler : HttpMessageHandler
{
    readonly Queue<Func<HttpResponseMessage>> responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string?> Bodies { get; } = [];

    public static StubHandler WithPing(string version = "1.47", string os = "linux")
    {
        var stub = new StubHandler();
        stub.responses.Enqueue(() =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
            res.Headers.Add("Api-Version", version);
            res.Headers.Add("Ostype", os);
            return res;
        });
        return stub;
    }

    public StubHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        return this;
    }

    public StubHandler Empty(HttpStatusCode status = HttpStatusCode.NoContent)
    {
        responses.Enqueue(() => new HttpResponseMessage(status) { Content = new ByteArrayContent([]) });
        return this;
    }

    public StubHandler Bytes(byte[] body)
    {
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(body)) });
        return this;
    }

    public StubHandler Lines(params string[] lines) => Bytes(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    public DockerClient Client() => new(new DockerEndpoint(EndpointKind.Tcp, "test", 2375, "tcp://test:2375"), this);

    public string Path(int index) => Requests[index].RequestUri!.PathAndQuery;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        return responses.Dequeue()();
    }
}
