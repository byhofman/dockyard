# Dockyard.Engine Client Implementation Plan (A1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A UI-free, native-AOT-safe Docker Engine API client library (`Dockyard.Engine`) that the dashboard (A2/A3) builds on.

**Architecture:** `HttpClient` over a `SocketsHttpHandler` whose `ConnectCallback` opens a named pipe, unix socket or TCP connection to the engine. `DockerClient` negotiates the API version with `/_ping`, maps errors to typed exceptions and exposes containers, logs, events, stats, images, volumes, networks and system calls. All JSON goes through source-generated `JsonSerializerContext`s, one per API area.

**Tech Stack:** .NET 10 (`net10.0`), System.Text.Json source generation, xUnit v3.

Spec: `docs/superpowers/specs/2026-11-04-dockyard-dashboard-design.md` (sections "Projects", "Engine client", "Testing"). Plan series: A1 engine client (this plan) → A2 dashboard shell + containers + tray hook → A3 compose, images, volumes, networks, disk → A4 CI, AOT gate, measurement, README. A2–A4 are written after A1 lands, against its real API.

## Global Constraints

- `Dockyard.Engine` targets `net10.0` with no Windows-only APIs and no UI dependency.
- `IsAotCompatible=true` and `TreatWarningsAsErrors=true` on `Dockyard.Engine`: no reflection-based JSON, no trim/AOT warnings.
- Minimum engine API version `1.44`; the client uses the version the engine reports in `/_ping`.
- Endpoint is always resolved from the active docker context (`DOCKER_CONTEXT` > `DOCKER_HOST` > `currentContext` in `~/.docker/config.json`); Dockyard never sets `DOCKER_HOST`.
- No comments that narrate or justify design decisions; only comment genuinely non-obvious things.
- Commit messages carry no `Co-Authored-By` or other model attribution.
- Work on branch `ui-rework`.

---

## File Structure

```
Dockyard.slnx                                         solution (new)
src/Dockyard.Engine/Dockyard.Engine.csproj
src/Dockyard.Engine/Transport/DockerEndpoint.cs        endpoint URI parsing
src/Dockyard.Engine/Transport/DockerTransport.cs       SocketsHttpHandler + ConnectCallback
src/Dockyard.Engine/DockerContext.cs                   active context resolution
src/Dockyard.Engine/DockerClient.cs                    ping, version, send, errors, JSON helpers
src/Dockyard.Engine/Exceptions.cs                      DockerException, NotFound, Conflict, EngineUnavailable, DockerContextException
src/Dockyard.Engine/Internal/JsonLines.cs              newline-delimited JSON reader
src/Dockyard.Engine/Internal/RfcTime.cs                RFC 3339 nano timestamp parsing
src/Dockyard.Engine/Api/Containers.cs                  + Models/ContainerModels.cs
src/Dockyard.Engine/Api/Logs.cs                        + Internal/LogDemuxer.cs
src/Dockyard.Engine/Api/Events.cs                      + Models/EventModels.cs
src/Dockyard.Engine/Api/Stats.cs                       + Models/StatsModels.cs, StatsCalculator.cs
src/Dockyard.Engine/Api/Images.cs                      + Models/ImageModels.cs
src/Dockyard.Engine/Api/Volumes.cs                     + Models/VolumeModels.cs
src/Dockyard.Engine/Api/Networks.cs                    + Models/NetworkModels.cs
src/Dockyard.Engine/Api/System.cs                      + Models/SystemModels.cs
tests/Dockyard.Engine.Tests/Dockyard.Engine.Tests.csproj
tests/Dockyard.Engine.Tests/Support/StubHandler.cs     canned HttpMessageHandler
tests/Dockyard.Engine.Tests/Support/CannedHttp.cs      one-shot raw HTTP responder for transport tests
tests/Dockyard.Engine.Tests/*Tests.cs                  one file per area
tests/Dockyard.Engine.Tests/EngineIntegrationTests.cs  real engines, Category=Engine
```

---

### Task 1: Solution, projects and endpoint parsing

**Files:**
- Create: `Dockyard.slnx`, `src/Dockyard.Engine/Dockyard.Engine.csproj`, `src/Dockyard.Engine/Transport/DockerEndpoint.cs`
- Create: `tests/Dockyard.Engine.Tests/Dockyard.Engine.Tests.csproj`
- Test: `tests/Dockyard.Engine.Tests/DockerEndpointTests.cs`

**Interfaces:**
- Produces: `enum EndpointKind { Tcp, Unix, NamedPipe }`; `sealed record DockerEndpoint(EndpointKind Kind, string Address, int Port, string Uri)` with `static DockerEndpoint Parse(string uri)` (throws `FormatException`).

- [ ] **Step 1: Create the solution and projects**

Run from the repo root:

```pwsh
dotnet new sln -n Dockyard
mkdir src/Dockyard.Engine, tests/Dockyard.Engine.Tests
```

`src/Dockyard.Engine/Dockyard.Engine.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsAotCompatible>true</IsAotCompatible>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Dockyard.Engine.Tests" />
  </ItemGroup>

</Project>
```

`tests/Dockyard.Engine.Tests/Dockyard.Engine.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <Using Include="Xunit" />
    <ProjectReference Include="..\..\src\Dockyard.Engine\Dockyard.Engine.csproj" />
  </ItemGroup>

</Project>
```

```pwsh
dotnet add tests/Dockyard.Engine.Tests package xunit.v3
dotnet add tests/Dockyard.Engine.Tests package xunit.runner.visualstudio
dotnet add tests/Dockyard.Engine.Tests package Microsoft.NET.Test.Sdk
dotnet sln add src/Dockyard.Engine/Dockyard.Engine.csproj tests/Dockyard.Engine.Tests/Dockyard.Engine.Tests.csproj src/Dockyard/Dockyard.csproj
```

- [ ] **Step 2: Write the failing test**

`tests/Dockyard.Engine.Tests/DockerEndpointTests.cs`:

```csharp
using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests;

public sealed class DockerEndpointTests
{
    [Theory]
    [InlineData("npipe:////./pipe/docker_windows", EndpointKind.NamedPipe, "docker_windows", 0)]
    [InlineData("npipe://./pipe/docker_engine", EndpointKind.NamedPipe, "docker_engine", 0)]
    [InlineData("unix:///var/run/docker.sock", EndpointKind.Unix, "/var/run/docker.sock", 0)]
    [InlineData("tcp://127.0.0.1:2375", EndpointKind.Tcp, "127.0.0.1", 2375)]
    [InlineData("tcp://docker-host", EndpointKind.Tcp, "docker-host", 2375)]
    public void Parses_supported_endpoints(string uri, EndpointKind kind, string address, int port)
    {
        var e = DockerEndpoint.Parse(uri);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(address, e.Address);
        Assert.Equal(port, e.Port);
        Assert.Equal(uri, e.Uri);
    }

    [Theory]
    [InlineData("ssh://user@host")]
    [InlineData("npipe://server/pipe/x")]
    [InlineData("")]
    public void Rejects_unsupported_endpoints(string uri) =>
        Assert.Throws<FormatException>(() => DockerEndpoint.Parse(uri));
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~DockerEndpointTests"`
Expected: build FAIL, `The type or namespace name 'Transport' does not exist`.

- [ ] **Step 4: Implement**

`src/Dockyard.Engine/Transport/DockerEndpoint.cs`:

```csharp
namespace Dockyard.Engine.Transport;

public enum EndpointKind { Tcp, Unix, NamedPipe }

public sealed record DockerEndpoint(EndpointKind Kind, string Address, int Port, string Uri)
{
    const int DefaultTcpPort = 2375;

    public static DockerEndpoint Parse(string uri)
    {
        if (uri.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = uri["npipe://".Length..].Replace('\\', '/').TrimStart('/');
            if (!rest.StartsWith("./pipe/", StringComparison.Ordinal) || rest.Length == "./pipe/".Length)
                throw new FormatException($"Unsupported named pipe address: {uri}");
            return new(EndpointKind.NamedPipe, rest["./pipe/".Length..], 0, uri);
        }
        if (uri.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
            return new(EndpointKind.Unix, uri["unix://".Length..], 0, uri);
        if (uri.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri("http://" + uri["tcp://".Length..]);
            return new(EndpointKind.Tcp, u.Host, u.IsDefaultPort ? DefaultTcpPort : u.Port, uri);
        }
        throw new FormatException($"Unsupported docker endpoint: '{uri}'");
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~DockerEndpointTests"`
Expected: PASS (8 tests).

- [ ] **Step 6: Commit**

```pwsh
git add Dockyard.slnx src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Add Dockyard.Engine project with endpoint parsing"
```

---

### Task 2: Transport (named pipe, unix socket, TCP)

**Files:**
- Create: `src/Dockyard.Engine/Transport/DockerTransport.cs`
- Create: `tests/Dockyard.Engine.Tests/Support/CannedHttp.cs`
- Test: `tests/Dockyard.Engine.Tests/DockerTransportTests.cs`

**Interfaces:**
- Consumes: `DockerEndpoint` (Task 1).
- Produces: `static class DockerTransport { HttpMessageHandler CreateHandler(DockerEndpoint endpoint); ValueTask<Stream> Connect(DockerEndpoint endpoint, CancellationToken ct); }`. Requests go to base address `http://docker/`.

- [ ] **Step 1: Write the test helper and failing tests**

`tests/Dockyard.Engine.Tests/Support/CannedHttp.cs`:

```csharp
using System.Text;

namespace Dockyard.Engine.Tests.Support;

static class CannedHttp
{
    const string Response =
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nApi-Version: 1.47\r\nConnection: close\r\n\r\nOK";

    public static async Task ServeOnce(Stream stream)
    {
        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n"))
        {
            var n = await stream.ReadAsync(buffer);
            if (n == 0) return;
            received.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Response));
        await stream.FlushAsync();
    }
}
```

`tests/Dockyard.Engine.Tests/DockerTransportTests.cs`:

```csharp
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Dockyard.Engine.Tests.Support;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests;

public sealed class DockerTransportTests
{
    static async Task<string> Ping(DockerEndpoint endpoint)
    {
        using var http = new HttpClient(DockerTransport.CreateHandler(endpoint)) { BaseAddress = new Uri("http://docker/") };
        return await http.GetStringAsync("_ping", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tcp_endpoint_reaches_listener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await CannedHttp.ServeOnce(client.GetStream());
            });
            Assert.Equal("OK", await Ping(DockerEndpoint.Parse($"tcp://127.0.0.1:{port}")));
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Unix_endpoint_reaches_listener()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dy-{Guid.NewGuid():N}.sock");
        using var listen = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listen.Bind(new UnixDomainSocketEndPoint(path));
        listen.Listen(1);
        try
        {
            var server = Task.Run(async () =>
            {
                using var socket = await listen.AcceptAsync();
                using var stream = new NetworkStream(socket);
                await CannedHttp.ServeOnce(stream);
            });
            Assert.Equal("OK", await Ping(DockerEndpoint.Parse("unix://" + path)));
            await server;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Named_pipe_endpoint_reaches_listener()
    {
        var name = $"dockyard-test-{Guid.NewGuid():N}";
        var server = Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync();
            await CannedHttp.ServeOnce(pipe);
        });
        Assert.Equal("OK", await Ping(DockerEndpoint.Parse($"npipe:////./pipe/{name}")));
        await server;
    }

    [Fact]
    public async Task Unreachable_tcp_endpoint_throws_HttpRequestException()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Assert.ThrowsAsync<HttpRequestException>(() => Ping(DockerEndpoint.Parse($"tcp://127.0.0.1:{port}")));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~DockerTransportTests"`
Expected: build FAIL, `The name 'DockerTransport' does not exist`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Transport/DockerTransport.cs`:

```csharp
using System.IO.Pipes;
using System.Net.Sockets;

namespace Dockyard.Engine.Transport;

public static class DockerTransport
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static HttpMessageHandler CreateHandler(DockerEndpoint endpoint) => new SocketsHttpHandler
    {
        ConnectCallback = (_, ct) => Connect(endpoint, ct),
        ConnectTimeout = ConnectTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    };

    public static async ValueTask<Stream> Connect(DockerEndpoint endpoint, CancellationToken ct)
    {
        switch (endpoint.Kind)
        {
            case EndpointKind.NamedPipe:
                var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }

            case EndpointKind.Unix:
                return await ConnectSocket(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
                    s => s.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), ct));

            case EndpointKind.Tcp:
                return await ConnectSocket(new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true },
                    s => s.ConnectAsync(endpoint.Address, endpoint.Port, ct));

            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint.Kind, null);
        }
    }

    static async Task<Stream> ConnectSocket(Socket socket, Func<Socket, ValueTask> connect)
    {
        try
        {
            await connect(socket);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~DockerTransportTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine/Transport tests/Dockyard.Engine.Tests
git commit -m "Connect to the engine over named pipe, unix socket or TCP"
```

---

### Task 3: Active docker context resolution

**Files:**
- Create: `src/Dockyard.Engine/DockerContext.cs`, `src/Dockyard.Engine/Exceptions.cs`
- Test: `tests/Dockyard.Engine.Tests/DockerContextTests.cs`

**Interfaces:**
- Consumes: `DockerEndpoint.Parse` (Task 1).
- Produces:
  - `sealed record ResolvedContext(string Name, DockerEndpoint Endpoint)`
  - `static class DockerContext { ResolvedContext Resolve(string? configDir = null, Func<string, string?>? env = null); string ConfigDir(Func<string, string?> env); string DefaultHost { get; } }`
  - `Exceptions.cs`: `DockerContextException(string message)`, plus the engine exceptions used from Task 4 on:
    `DockerException(HttpStatusCode statusCode, string message)` with `HttpStatusCode StatusCode`, `NotFoundException(string)`, `ConflictException(string)`, `EngineUnavailableException(DockerEndpoint endpoint, Exception inner)` with `DockerEndpoint Endpoint`.

- [ ] **Step 1: Write the failing test**

`tests/Dockyard.Engine.Tests/DockerContextTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests;

public sealed class DockerContextTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dy-ctx-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    void WriteConfig(string currentContext) =>
        File.WriteAllText(Path.Combine(dir, "config.json"), $$"""{"auths":{},"currentContext":"{{currentContext}}"}""");

    void WriteContext(string name, string host)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        var metaDir = Directory.CreateDirectory(Path.Combine(dir, "contexts", "meta", hash)).FullName;
        File.WriteAllText(Path.Combine(metaDir, "meta.json"),
            $$"""{"Name":"{{name}}","Metadata":{},"Endpoints":{"docker":{"Host":"{{host}}","SkipTLSVerify":false}}}""");
    }

    static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Uses_current_context_from_config()
    {
        WriteConfig("linux");
        WriteContext("linux", "tcp://127.0.0.1:2375");
        var ctx = DockerContext.Resolve(dir, Env());
        Assert.Equal("linux", ctx.Name);
        Assert.Equal(EndpointKind.Tcp, ctx.Endpoint.Kind);
        Assert.Equal(2375, ctx.Endpoint.Port);
    }

    [Fact]
    public void DOCKER_CONTEXT_overrides_config()
    {
        WriteConfig("linux");
        WriteContext("linux", "tcp://127.0.0.1:2375");
        WriteContext("win", "npipe:////./pipe/docker_windows");
        var ctx = DockerContext.Resolve(dir, Env(("DOCKER_CONTEXT", "win")));
        Assert.Equal("win", ctx.Name);
        Assert.Equal("docker_windows", ctx.Endpoint.Address);
    }

    [Fact]
    public void DOCKER_HOST_selects_default_context()
    {
        WriteConfig("linux");
        var ctx = DockerContext.Resolve(dir, Env(("DOCKER_HOST", "tcp://10.0.0.5:2376")));
        Assert.Equal("default", ctx.Name);
        Assert.Equal("10.0.0.5", ctx.Endpoint.Address);
    }

    [Fact]
    public void Missing_config_falls_back_to_platform_default()
    {
        var ctx = DockerContext.Resolve(dir, Env());
        Assert.Equal("default", ctx.Name);
        Assert.Equal(DockerContext.DefaultHost, ctx.Endpoint.Uri);
    }

    [Fact]
    public void Unknown_context_throws()
    {
        WriteConfig("nope");
        var ex = Assert.Throws<DockerContextException>(() => DockerContext.Resolve(dir, Env()));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void DOCKER_CONFIG_sets_config_dir() =>
        Assert.Equal(dir, DockerContext.ConfigDir(Env(("DOCKER_CONFIG", dir))));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~DockerContextTests"`
Expected: build FAIL, `The name 'DockerContext' does not exist`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Exceptions.cs`:

```csharp
using System.Net;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public class DockerException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class NotFoundException(string message) : DockerException(HttpStatusCode.NotFound, message);

public sealed class ConflictException(string message) : DockerException(HttpStatusCode.Conflict, message);

public sealed class EngineUnavailableException(DockerEndpoint endpoint, Exception inner)
    : Exception($"Docker engine at {endpoint.Uri} is not reachable: {inner.Message}", inner)
{
    public DockerEndpoint Endpoint { get; } = endpoint;
}

public sealed class DockerContextException(string message) : Exception(message);
```

`src/Dockyard.Engine/DockerContext.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public sealed record ResolvedContext(string Name, DockerEndpoint Endpoint);

public static class DockerContext
{
    public static string DefaultHost =>
        OperatingSystem.IsWindows() ? "npipe:////./pipe/docker_engine" : "unix:///var/run/docker.sock";

    public static string ConfigDir(Func<string, string?> env) =>
        env("DOCKER_CONFIG") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");

    public static ResolvedContext Resolve(string? configDir = null, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        configDir ??= ConfigDir(env);

        var name = env("DOCKER_CONTEXT") is { Length: > 0 } explicitName ? explicitName
            : env("DOCKER_HOST") is { Length: > 0 } ? "default"
            : ReadCurrentContext(configDir) ?? "default";

        if (name == "default")
            return new("default", DockerEndpoint.Parse(env("DOCKER_HOST") is { Length: > 0 } host ? host : DefaultHost));

        var meta = Path.Combine(configDir, "contexts", "meta", Hash(name), "meta.json");
        if (!File.Exists(meta)) throw new DockerContextException($"Docker context '{name}' does not exist");

        var doc = JsonSerializer.Deserialize(File.ReadAllText(meta), ContextJson.Default.ContextMeta);
        var endpoint = doc?.Endpoints?.GetValueOrDefault("docker")?.Host
            ?? throw new DockerContextException($"Docker context '{name}' has no docker endpoint");
        return new(name, DockerEndpoint.Parse(endpoint));
    }

    static string? ReadCurrentContext(string configDir)
    {
        var file = Path.Combine(configDir, "config.json");
        if (!File.Exists(file)) return null;
        var config = JsonSerializer.Deserialize(File.ReadAllText(file), ContextJson.Default.CliConfig);
        return config?.CurrentContext is { Length: > 0 } c ? c : null;
    }

    static string Hash(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
}

sealed class CliConfig
{
    public string? CurrentContext { get; set; }
}

sealed class ContextMeta
{
    public Dictionary<string, ContextEndpoint>? Endpoints { get; set; }
}

sealed class ContextEndpoint
{
    public string? Host { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(ContextMeta))]
partial class ContextJson : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~DockerContextTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Resolve the active docker context like the CLI does"
```

---

### Task 4: DockerClient core (ping, version, errors, JSON helpers)

**Files:**
- Create: `src/Dockyard.Engine/DockerClient.cs`, `src/Dockyard.Engine/Internal/JsonLines.cs`, `src/Dockyard.Engine/Internal/RfcTime.cs`
- Create: `tests/Dockyard.Engine.Tests/Support/StubHandler.cs`
- Test: `tests/Dockyard.Engine.Tests/DockerClientTests.cs`, `tests/Dockyard.Engine.Tests/RfcTimeTests.cs`

**Interfaces:**
- Consumes: `DockerEndpoint`, `DockerTransport.CreateHandler`, exceptions (Tasks 1–3).
- Produces:
  - `public sealed partial class DockerClient : IDisposable`
    - `DockerClient(DockerEndpoint endpoint)`; `internal DockerClient(DockerEndpoint endpoint, HttpMessageHandler handler)`
    - `static DockerClient FromActiveContext()` and `ResolvedContext? Context { get; }`
    - `DockerEndpoint Endpoint`, `string? ApiVersion`, `string OsType` (`"linux"` or `"windows"`, from `/_ping`), `bool IsWindows`
    - `Task<PingResult> PingAsync(CancellationToken ct = default)`; `record PingResult(string ApiVersion, string OsType)`
    - `Task<VersionInfo> VersionAsync(CancellationToken ct = default)`; `VersionInfo { Version, ApiVersion, Os, Arch }`
    - internal helpers: `Send(HttpMethod, string path, HttpContent? body, bool stream, CancellationToken)`, `GetJson<T>(string path, JsonTypeInfo<T>, CancellationToken)`, `SendJson<T>(HttpMethod, string path, HttpContent? body, JsonTypeInfo<T>, CancellationToken)`, `SendNoContent(HttpMethod, string path, HttpContent? body, CancellationToken)`, `static string Esc(string)`, `static string Bool(bool)`, `static string Filters(params (string Key, string Value)[])`
    - `sealed class PruneResult { long SpaceReclaimed }` (used by Tasks 9–10)
  - `internal static class JsonLines { IAsyncEnumerable<T> ReadAsync<T>(Stream, JsonTypeInfo<T>, CancellationToken) }`
  - `internal static class RfcTime { DateTimeOffset? Parse(ReadOnlySpan<char>) }`

- [ ] **Step 1: Write the stub handler and failing tests**

`tests/Dockyard.Engine.Tests/Support/StubHandler.cs`:

```csharp
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
```

`tests/Dockyard.Engine.Tests/DockerClientTests.cs`:

```csharp
using System.Net;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class DockerClientTests
{
    [Fact]
    public async Task Ping_reads_api_version_and_os()
    {
        var stub = StubHandler.WithPing("1.47", "windows");
        using var client = stub.Client();
        var ping = await client.PingAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1.47", ping.ApiVersion);
        Assert.Equal("windows", client.OsType);
        Assert.True(client.IsWindows);
        Assert.Equal("/_ping", stub.Path(0));
    }

    [Fact]
    public async Task First_call_pings_then_uses_versioned_path()
    {
        var stub = StubHandler.WithPing("1.48").Json("""{"Version":"28.5.1","ApiVersion":"1.48","Os":"linux","Arch":"amd64"}""");
        using var client = stub.Client();
        var version = await client.VersionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("28.5.1", version.Version);
        Assert.Equal("/v1.48/version", stub.Path(1));
    }

    [Fact]
    public async Task Too_old_engine_is_rejected()
    {
        using var client = StubHandler.WithPing("1.41").Client();
        var ex = await Assert.ThrowsAsync<DockerException>(() => client.PingAsync(TestContext.Current.CancellationToken));
        Assert.Contains("1.44", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundException))]
    [InlineData(HttpStatusCode.Conflict, typeof(ConflictException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(DockerException))]
    public async Task Errors_map_to_typed_exceptions_with_engine_message(HttpStatusCode status, Type expected)
    {
        using var client = StubHandler.WithPing().Json("""{"message":"No such container: x"}""", status).Client();
        var ex = await Assert.ThrowsAnyAsync<DockerException>(() => client.VersionAsync(TestContext.Current.CancellationToken));
        Assert.IsType(expected, ex);
        Assert.Equal("No such container: x", ex.Message);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public void Filters_are_json_and_escaped()
    {
        var q = DockerClient.Filters(("type", "container"), ("type", "image"), ("dangling", "true"));
        Assert.Equal("""{"type":["container","image"],"dangling":["true"]}""", Uri.UnescapeDataString(q));
    }

    [Fact]
    public async Task Unreachable_engine_throws_EngineUnavailable()
    {
        using var client = new DockerClient(Transport.DockerEndpoint.Parse("tcp://127.0.0.1:1"));
        await Assert.ThrowsAsync<EngineUnavailableException>(() => client.PingAsync(TestContext.Current.CancellationToken));
    }
}
```

`tests/Dockyard.Engine.Tests/RfcTimeTests.cs`:

```csharp
using Dockyard.Engine.Internal;

namespace Dockyard.Engine.Tests;

public sealed class RfcTimeTests
{
    [Fact]
    public void Parses_nanosecond_utc() =>
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 1, 2, TimeSpan.Zero).AddTicks(1234567),
            RfcTime.Parse("2026-11-04T10:01:02.123456789Z"));

    [Fact]
    public void Parses_offset_without_fraction() =>
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 1, 2, TimeSpan.FromHours(1)), RfcTime.Parse("2026-11-04T10:01:02+01:00"));

    [Fact]
    public void Returns_null_for_garbage() => Assert.Null(RfcTime.Parse("hello"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~DockerClientTests|FullyQualifiedName~RfcTimeTests"`
Expected: build FAIL, `The type or namespace name 'DockerClient' could not be found`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Internal/RfcTime.cs`:

```csharp
using System.Globalization;

namespace Dockyard.Engine.Internal;

static class RfcTime
{
    // .NET parses at most 7 fractional digits; Docker sends 9.
    public static DateTimeOffset? Parse(ReadOnlySpan<char> s)
    {
        var dot = s.IndexOf('.');
        string text;
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
            var fraction = s[(dot + 1)..end];
            if (fraction.Length > 7) fraction = fraction[..7];
            text = $"{s[..dot]}.{fraction}{s[end..]}";
        }
        else text = s.ToString();

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }
}
```

`src/Dockyard.Engine/Internal/JsonLines.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dockyard.Engine.Internal;

static class JsonLines
{
    public static async IAsyncEnumerable<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return JsonSerializer.Deserialize(line, type)!;
        }
    }
}
```

`src/Dockyard.Engine/DockerClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public sealed record PingResult(string ApiVersion, string OsType);

public sealed class VersionInfo
{
    public string Version { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
}

public sealed class PruneResult
{
    public long SpaceReclaimed { get; set; }
}

sealed class ErrorResponse
{
    public string? Message { get; set; }
}

public sealed partial class DockerClient : IDisposable
{
    public static readonly Version MinApiVersion = new(1, 44);

    readonly HttpClient http;
    string? apiPrefix;

    public DockerClient(DockerEndpoint endpoint) : this(endpoint, DockerTransport.CreateHandler(endpoint)) { }

    internal DockerClient(DockerEndpoint endpoint, HttpMessageHandler handler)
    {
        Endpoint = endpoint;
        http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public static DockerClient FromActiveContext()
    {
        var context = DockerContext.Resolve();
        return new DockerClient(context.Endpoint) { Context = context };
    }

    public ResolvedContext? Context { get; private init; }
    public DockerEndpoint Endpoint { get; }
    public string? ApiVersion { get; private set; }
    public string OsType { get; private set; } = "linux";
    public bool IsWindows => OsType == "windows";

    public async Task<PingResult> PingAsync(CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try { res = await http.GetAsync("_ping", ct); }
        catch (HttpRequestException ex) { throw new EngineUnavailableException(Endpoint, ex); }

        using (res)
        {
            await EnsureSuccess(res, ct);
            var version = Header(res, "Api-Version")
                ?? throw new DockerException(res.StatusCode, "The engine did not report an API version");
            if (Version.Parse(version) < MinApiVersion)
                throw new DockerException(res.StatusCode, $"Docker engine API {version} is older than the minimum {MinApiVersion}");

            OsType = Header(res, "Ostype")?.ToLowerInvariant() ?? "linux";
            ApiVersion = version;
            apiPrefix = $"v{version}/";
            return new PingResult(version, OsType);
        }
    }

    public Task<VersionInfo> VersionAsync(CancellationToken ct = default) => GetJson("version", CoreJson.Default.VersionInfo, ct);

    internal async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? body, bool stream, CancellationToken ct)
    {
        if (apiPrefix is null) await PingAsync(ct);
        var request = new HttpRequestMessage(method, apiPrefix + path) { Content = body };
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex) { throw new EngineUnavailableException(Endpoint, ex); }
        await EnsureSuccess(res, ct);
        return res;
    }

    internal Task<T> GetJson<T>(string path, JsonTypeInfo<T> type, CancellationToken ct) => SendJson(HttpMethod.Get, path, null, type, ct);

    internal async Task<T> SendJson<T>(HttpMethod method, string path, HttpContent? body, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var res = await Send(method, path, body, stream: false, ct);
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        return (await JsonSerializer.DeserializeAsync(s, type, ct))!;
    }

    internal async Task SendNoContent(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var _ = await Send(method, path, body, stream: false, ct);
    }

    internal static HttpContent Body<T>(T value, JsonTypeInfo<T> type) => JsonContent.Create(value, type);

    internal static string Esc(string value) => Uri.EscapeDataString(value);

    internal static string Bool(bool value) => value ? "1" : "0";

    internal static string Filters(params (string Key, string Value)[] filters)
    {
        var map = new Dictionary<string, string[]>();
        foreach (var group in filters.GroupBy(f => f.Key))
            map[group.Key] = group.Select(f => f.Value).ToArray();
        return Esc(JsonSerializer.Serialize(map, CoreJson.Default.DictionaryStringStringArray));
    }

    static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotModified) return;

        var body = await res.Content.ReadAsStringAsync(ct);
        string? message;
        try { message = JsonSerializer.Deserialize(body, CoreJson.Default.ErrorResponse)?.Message; }
        catch (JsonException) { message = body; }
        if (string.IsNullOrWhiteSpace(message)) message = res.ReasonPhrase ?? res.StatusCode.ToString();
        message = message.Trim();

        var status = res.StatusCode;
        res.Dispose();
        throw status switch
        {
            HttpStatusCode.NotFound => new NotFoundException(message),
            HttpStatusCode.Conflict => new ConflictException(message),
            _ => new DockerException(status, message),
        };
    }

    public void Dispose() => http.Dispose();
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(PruneResult))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
partial class CoreJson : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~DockerClientTests|FullyQualifiedName~RfcTimeTests"`
Expected: PASS (10 tests). Also run `dotnet build src/Dockyard.Engine` and expect 0 warnings.

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Add DockerClient with API version negotiation and typed errors"
```

---

### Task 5: Containers API

**Files:**
- Create: `src/Dockyard.Engine/Models/ContainerModels.cs`, `src/Dockyard.Engine/Api/Containers.cs`
- Test: `tests/Dockyard.Engine.Tests/ContainersTests.cs`

**Interfaces:**
- Consumes: `DockerClient` internals (Task 4).
- Produces (on `DockerClient`):
  - `Task<List<ContainerSummary>> ListContainersAsync(bool all = true, CancellationToken ct = default)`
  - `Task<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default)`
  - `Task<string> CreateContainerAsync(CreateContainerRequest request, string? name = null, CancellationToken ct = default)` (returns the id)
  - `Task StartContainerAsync(string id, CancellationToken ct = default)`
  - `Task StopContainerAsync(string id, int? timeoutSeconds = null, CancellationToken ct = default)`
  - `Task RestartContainerAsync(string id, int? timeoutSeconds = null, CancellationToken ct = default)`
  - `Task RemoveContainerAsync(string id, bool force = false, bool removeVolumes = false, CancellationToken ct = default)`
  - Models: `ContainerSummary { Id, Names, Image, ImageID, Command, Created, State, Status, Ports, Labels; string Name }`, `PortMapping { IP, PrivatePort, PublicPort, Type }`, `ContainerInspect { Id, Name, Created, Platform, State, Config, HostConfig, NetworkSettings, Mounts }`, `ContainerState { Status, Running, ExitCode, StartedAt, FinishedAt, Health }`, `HealthState { Status }`, `ContainerConfig { Image, Env, Cmd, Entrypoint, Labels, Tty, WorkingDir }`, `HostConfigInfo { RestartPolicy }`, `RestartPolicy { Name }`, `NetworkSettingsInfo { Ports, Networks }`, `PortBinding { HostIp, HostPort }`, `EndpointSettings { IPAddress, NetworkID }`, `MountPoint { Type, Name, Source, Destination, RW }`, `CreateContainerRequest { Image, Cmd, Labels }`.

- [ ] **Step 1: Write the failing test**

`tests/Dockyard.Engine.Tests/ContainersTests.cs`:

```csharp
using System.Net;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class ContainersTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_parses_summaries()
    {
        var stub = StubHandler.WithPing().Json("""
            [{"Id":"abc123def4567890","Names":["/shop-api-1"],"Image":"node:22","ImageID":"sha256:1",
              "Command":"node server.js","Created":1793785824,"State":"running","Status":"Up 2 minutes",
              "Ports":[{"IP":"0.0.0.0","PrivatePort":80,"PublicPort":8080,"Type":"tcp"},{"PrivatePort":9229,"Type":"tcp"}],
              "Labels":{"com.docker.compose.project":"shop"}}]
            """);
        using var client = stub.Client();

        var list = await client.ListContainersAsync(ct: Ct);

        Assert.Equal("/v1.47/containers/json?all=1", stub.Path(1));
        var c = Assert.Single(list);
        Assert.Equal("shop-api-1", c.Name);
        Assert.Equal("running", c.State);
        Assert.Equal(8080, c.Ports[0].PublicPort);
        Assert.Null(c.Ports[1].PublicPort);
        Assert.Equal("shop", c.Labels!["com.docker.compose.project"]);
    }

    [Fact]
    public async Task Inspect_parses_details()
    {
        var stub = StubHandler.WithPing().Json("""
            {"Id":"abc","Name":"/web","Created":"2026-11-04T10:00:00.123456789Z","Platform":"linux",
             "State":{"Status":"running","Running":true,"ExitCode":0,"StartedAt":"2026-11-04T10:00:01Z","FinishedAt":"0001-01-01T00:00:00Z","Health":{"Status":"healthy"}},
             "Config":{"Image":"nginx","Env":["A=1"],"Cmd":["nginx","-g","daemon off;"],"Entrypoint":null,"Labels":{},"Tty":false,"WorkingDir":""},
             "HostConfig":{"RestartPolicy":{"Name":"unless-stopped"}},
             "NetworkSettings":{"Ports":{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"8080"}],"443/tcp":null},"Networks":{"bridge":{"IPAddress":"172.17.0.2","NetworkID":"n1"}}},
             "Mounts":[{"Type":"bind","Source":"/src","Destination":"/app","RW":true}]}
            """);
        using var client = stub.Client();

        var c = await client.InspectContainerAsync("abc", Ct);

        Assert.Equal("/v1.47/containers/abc/json", stub.Path(1));
        Assert.True(c.State.Running);
        Assert.Equal("healthy", c.State.Health!.Status);
        Assert.Equal("8080", c.NetworkSettings.Ports!["80/tcp"]![0].HostPort);
        Assert.Null(c.NetworkSettings.Ports["443/tcp"]);
        Assert.Equal("172.17.0.2", c.NetworkSettings.Networks!["bridge"].IPAddress);
        Assert.Equal("unless-stopped", c.HostConfig.RestartPolicy!.Name);
        Assert.Equal("/app", c.Mounts[0].Destination);
    }

    [Fact]
    public async Task Lifecycle_calls_use_expected_paths()
    {
        var stub = StubHandler.WithPing()
            .Json("""{"Id":"new1","Warnings":[]}""", HttpStatusCode.Created)
            .Empty().Empty().Empty().Empty();
        using var client = stub.Client();

        var id = await client.CreateContainerAsync(new CreateContainerRequest { Image = "alpine", Cmd = ["true"] }, "t1", Ct);
        await client.StartContainerAsync(id, Ct);
        await client.StopContainerAsync(id, 5, Ct);
        await client.RestartContainerAsync(id, ct: Ct);
        await client.RemoveContainerAsync(id, force: true, ct: Ct);

        Assert.Equal("new1", id);
        Assert.Equal("/v1.47/containers/create?name=t1", stub.Path(1));
        Assert.Equal("""{"Image":"alpine","Cmd":["true"]}""", stub.Bodies[1]);
        Assert.Equal("/v1.47/containers/new1/start", stub.Path(2));
        Assert.Equal("/v1.47/containers/new1/stop?t=5", stub.Path(3));
        Assert.Equal("/v1.47/containers/new1/restart", stub.Path(4));
        Assert.Equal("/v1.47/containers/new1?force=1&v=0", stub.Path(5));
        Assert.Equal(HttpMethod.Delete, stub.Requests[5].Method);
    }

    [Fact]
    public async Task Start_of_running_container_is_not_an_error()
    {
        using var client = StubHandler.WithPing().Empty(HttpStatusCode.NotModified).Client();
        await client.StartContainerAsync("abc", Ct);
    }

    [Fact]
    public async Task Remove_conflict_surfaces_engine_message()
    {
        using var client = StubHandler.WithPing()
            .Json("""{"message":"cannot remove container: container is running"}""", HttpStatusCode.Conflict).Client();
        var ex = await Assert.ThrowsAsync<ConflictException>(() => client.RemoveContainerAsync("abc", ct: Ct));
        Assert.Contains("container is running", ex.Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~ContainersTests"`
Expected: build FAIL, `'DockerClient' does not contain a definition for 'ListContainersAsync'`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Models/ContainerModels.cs`:

```csharp
namespace Dockyard.Engine;

public sealed class ContainerSummary
{
    public string Id { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Image { get; set; } = "";
    public string ImageID { get; set; } = "";
    public string Command { get; set; } = "";
    public long Created { get; set; }
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public List<PortMapping> Ports { get; set; } = [];
    public Dictionary<string, string>? Labels { get; set; }

    public string Name => Names.Count > 0 ? Names[0].TrimStart('/') : Id[..Math.Min(12, Id.Length)];
}

public sealed class PortMapping
{
    public string? IP { get; set; }
    public int PrivatePort { get; set; }
    public int? PublicPort { get; set; }
    public string Type { get; set; } = "";
}

public sealed class ContainerInspect
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Created { get; set; } = "";
    public string Platform { get; set; } = "";
    public ContainerState State { get; set; } = new();
    public ContainerConfig Config { get; set; } = new();
    public HostConfigInfo HostConfig { get; set; } = new();
    public NetworkSettingsInfo NetworkSettings { get; set; } = new();
    public List<MountPoint> Mounts { get; set; } = [];
}

public sealed class ContainerState
{
    public string Status { get; set; } = "";
    public bool Running { get; set; }
    public int ExitCode { get; set; }
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public HealthState? Health { get; set; }
}

public sealed class HealthState
{
    public string Status { get; set; } = "";
}

public sealed class ContainerConfig
{
    public string Image { get; set; } = "";
    public List<string>? Env { get; set; }
    public List<string>? Cmd { get; set; }
    public List<string>? Entrypoint { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public bool Tty { get; set; }
    public string? WorkingDir { get; set; }
}

public sealed class HostConfigInfo
{
    public RestartPolicy? RestartPolicy { get; set; }
}

public sealed class RestartPolicy
{
    public string Name { get; set; } = "";
}

public sealed class NetworkSettingsInfo
{
    public Dictionary<string, List<PortBinding>?>? Ports { get; set; }
    public Dictionary<string, EndpointSettings>? Networks { get; set; }
}

public sealed class PortBinding
{
    public string HostIp { get; set; } = "";
    public string HostPort { get; set; } = "";
}

public sealed class EndpointSettings
{
    public string IPAddress { get; set; } = "";
    public string NetworkID { get; set; } = "";
}

public sealed class MountPoint
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool RW { get; set; }
}

public sealed class CreateContainerRequest
{
    public string Image { get; set; } = "";
    public List<string>? Cmd { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

sealed class CreateContainerResponse
{
    public string Id { get; set; } = "";
}
```

`src/Dockyard.Engine/Api/Containers.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public Task<List<ContainerSummary>> ListContainersAsync(bool all = true, CancellationToken ct = default) =>
        GetJson($"containers/json?all={Bool(all)}", ContainerJson.Default.ListContainerSummary, ct);

    public Task<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default) =>
        GetJson($"containers/{Esc(id)}/json", ContainerJson.Default.ContainerInspect, ct);

    public async Task<string> CreateContainerAsync(CreateContainerRequest request, string? name = null, CancellationToken ct = default)
    {
        var path = "containers/create" + (name is null ? "" : $"?name={Esc(name)}");
        var res = await SendJson(HttpMethod.Post, path, Body(request, ContainerJson.Default.CreateContainerRequest),
            ContainerJson.Default.CreateContainerResponse, ct);
        return res.Id;
    }

    public Task StartContainerAsync(string id, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"containers/{Esc(id)}/start", null, ct);

    public Task StopContainerAsync(string id, int? timeoutSeconds = null, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"containers/{Esc(id)}/stop" + Timeout(timeoutSeconds), null, ct);

    public Task RestartContainerAsync(string id, int? timeoutSeconds = null, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"containers/{Esc(id)}/restart" + Timeout(timeoutSeconds), null, ct);

    public Task RemoveContainerAsync(string id, bool force = false, bool removeVolumes = false, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Delete, $"containers/{Esc(id)}?force={Bool(force)}&v={Bool(removeVolumes)}", null, ct);

    static string Timeout(int? seconds) => seconds is { } t ? $"?t={t}" : "";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ContainerSummary>))]
[JsonSerializable(typeof(ContainerInspect))]
[JsonSerializable(typeof(CreateContainerRequest))]
[JsonSerializable(typeof(CreateContainerResponse))]
partial class ContainerJson : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~ContainersTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Add containers API: list, inspect, create, start, stop, restart, remove"
```

---

### Task 6: Logs (multiplexed stream demultiplexing)

**Files:**
- Create: `src/Dockyard.Engine/Internal/LogDemuxer.cs`, `src/Dockyard.Engine/Api/Logs.cs`
- Test: `tests/Dockyard.Engine.Tests/LogsTests.cs`

**Interfaces:**
- Consumes: `Send`, `RfcTime.Parse` (Task 4).
- Produces:
  - `enum LogStream { Stdout = 1, Stderr = 2 }`
  - `readonly record struct LogLine(LogStream Stream, DateTimeOffset? Time, string Text)`
  - `sealed record LogOptions(bool Follow = true, int? Tail = 1000, bool Tty = false)` (`Tail = null` means all)
  - `IAsyncEnumerable<LogLine> LogsAsync(string id, LogOptions options, CancellationToken ct = default)` on `DockerClient`; always requests timestamps and splits them into `Time`.
  - `internal static class LogDemuxer { IAsyncEnumerable<(LogStream Stream, string Text)> ReadAsync(Stream, bool tty, CancellationToken) }`

- [ ] **Step 1: Write the failing test**

`tests/Dockyard.Engine.Tests/LogsTests.cs`:

```csharp
using System.Buffers.Binary;
using System.Text;
using Dockyard.Engine.Internal;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class LogsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static byte[] Frame(byte stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = stream;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    static async Task<List<(LogStream, string)>> Demux(byte[] bytes, bool tty)
    {
        var lines = new List<(LogStream, string)>();
        await foreach (var l in LogDemuxer.ReadAsync(new MemoryStream(bytes), tty, Ct)) lines.Add(l);
        return lines;
    }

    [Fact]
    public async Task Demuxes_frames_and_joins_partial_lines()
    {
        var bytes = Frame(1, "hello\nwor").Concat(Frame(1, "ld\n")).Concat(Frame(2, "oops\r\n")).Concat(Frame(1, "tail")).ToArray();
        Assert.Equal(
            [(LogStream.Stdout, "hello"), (LogStream.Stdout, "world"), (LogStream.Stderr, "oops"), (LogStream.Stdout, "tail")],
            await Demux(bytes, tty: false));
    }

    [Fact]
    public async Task Multibyte_character_split_across_frames_survives()
    {
        var euro = Encoding.UTF8.GetBytes("€\n");
        var bytes = FrameRaw(1, euro[..1]).Concat(FrameRaw(1, euro[1..])).ToArray();
        Assert.Equal([(LogStream.Stdout, "€")], await Demux(bytes, tty: false));

        static byte[] FrameRaw(byte stream, byte[] payload)
        {
            var frame = new byte[8 + payload.Length];
            frame[0] = stream;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
            payload.CopyTo(frame, 8);
            return frame;
        }
    }

    [Fact]
    public async Task Tty_stream_is_raw_stdout() =>
        Assert.Equal([(LogStream.Stdout, "a"), (LogStream.Stdout, "b")], await Demux(Encoding.UTF8.GetBytes("a\r\nb\n"), tty: true));

    [Fact]
    public async Task LogsAsync_splits_timestamps()
    {
        var stub = StubHandler.WithPing().Bytes(Frame(1, "2026-11-04T10:01:02.123456789Z listening on :80\n"));
        using var client = stub.Client();

        var lines = new List<LogLine>();
        await foreach (var l in client.LogsAsync("abc", new LogOptions(Follow: false, Tail: 50), Ct)) lines.Add(l);

        Assert.Equal("/v1.47/containers/abc/logs?stdout=1&stderr=1&timestamps=1&follow=0&tail=50", stub.Path(1));
        var line = Assert.Single(lines);
        Assert.Equal("listening on :80", line.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 1, 2, TimeSpan.Zero).AddTicks(1234567), line.Time);
    }

    [Fact]
    public async Task LogsAsync_without_tail_requests_all()
    {
        var stub = StubHandler.WithPing().Bytes([]);
        using var client = stub.Client();
        await foreach (var _ in client.LogsAsync("abc", new LogOptions(Follow: true, Tail: null), Ct)) { }
        Assert.EndsWith("follow=1&tail=all", stub.Path(1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~LogsTests"`
Expected: build FAIL, `The name 'LogDemuxer' does not exist`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Internal/LogDemuxer.cs`:

```csharp
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dockyard.Engine.Internal;

static class LogDemuxer
{
    public static IAsyncEnumerable<(LogStream Stream, string Text)> ReadAsync(Stream stream, bool tty, CancellationToken ct) =>
        tty ? ReadRaw(stream, ct) : ReadMultiplexed(stream, ct);

    static async IAsyncEnumerable<(LogStream, string)> ReadRaw(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line) yield return (LogStream.Stdout, line);
    }

    // Each frame: [stream:1][0:3][size:4 big-endian][payload:size]. Lines may span frames.
    static async IAsyncEnumerable<(LogStream, string)> ReadMultiplexed(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[8];
        var outBuffer = new LineBuffer();
        var errBuffer = new LineBuffer();

        while (await Fill(stream, header, 8, ct))
        {
            var kind = header[0] == 2 ? LogStream.Stderr : LogStream.Stdout;
            var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            var payload = new byte[size];
            if (!await Fill(stream, payload, size, ct)) break;

            var buffer = kind == LogStream.Stderr ? errBuffer : outBuffer;
            foreach (var line in buffer.Append(payload)) yield return (kind, line);
        }

        if (outBuffer.Rest() is { } outRest) yield return (LogStream.Stdout, outRest);
        if (errBuffer.Rest() is { } errRest) yield return (LogStream.Stderr, errRest);
    }

    static async Task<bool> Fill(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    sealed class LineBuffer
    {
        readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        readonly StringBuilder pending = new();

        public List<string> Append(byte[] bytes)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
            pending.Append(chars, 0, count);

            var lines = new List<string>();
            var text = pending.ToString();
            var start = 0;
            int newline;
            while ((newline = text.IndexOf('\n', start)) >= 0)
            {
                lines.Add(text[start..newline].TrimEnd('\r'));
                start = newline + 1;
            }
            pending.Clear().Append(text, start, text.Length - start);
            return lines;
        }

        public string? Rest() => pending.Length > 0 ? pending.ToString() : null;
    }
}
```

`src/Dockyard.Engine/Api/Logs.cs`:

```csharp
using System.Runtime.CompilerServices;
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public enum LogStream { Stdout = 1, Stderr = 2 }

public readonly record struct LogLine(LogStream Stream, DateTimeOffset? Time, string Text);

public sealed record LogOptions(bool Follow = true, int? Tail = 1000, bool Tty = false);

public sealed partial class DockerClient
{
    public async IAsyncEnumerable<LogLine> LogsAsync(string id, LogOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = $"containers/{Esc(id)}/logs?stdout=1&stderr=1&timestamps=1&follow={Bool(options.Follow)}&tail={options.Tail?.ToString() ?? "all"}";
        using var res = await Send(HttpMethod.Get, path, null, stream: true, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await foreach (var (kind, raw) in LogDemuxer.ReadAsync(stream, options.Tty, ct))
            yield return SplitTimestamp(kind, raw);
    }

    static LogLine SplitTimestamp(LogStream kind, string raw)
    {
        var space = raw.IndexOf(' ');
        if (space > 0 && RfcTime.Parse(raw.AsSpan(0, space)) is { } time)
            return new LogLine(kind, time, raw[(space + 1)..]);
        return new LogLine(kind, null, raw);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~LogsTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Stream container logs with stdout/stderr demultiplexing"
```

---

### Task 7: Events stream

**Files:**
- Create: `src/Dockyard.Engine/Models/EventModels.cs`, `src/Dockyard.Engine/Api/Events.cs`
- Test: `tests/Dockyard.Engine.Tests/EventsTests.cs`

**Interfaces:**
- Consumes: `Send`, `Filters`, `JsonLines.ReadAsync` (Task 4).
- Produces:
  - `DockerEvent { string Type, string Action, EventActor Actor, long Time, long TimeNano }`, `EventActor { string ID, Dictionary<string,string>? Attributes }`
  - `IAsyncEnumerable<DockerEvent> EventsAsync(IReadOnlyCollection<string>? types = null, DateTimeOffset? since = null, CancellationToken ct = default)`. With `since`, the engine replays events from that time before streaming new ones.

- [ ] **Step 1: Write the failing test**

`tests/Dockyard.Engine.Tests/EventsTests.cs`:

```csharp
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class EventsTests
{
    [Fact]
    public async Task Streams_events_with_filters_and_since()
    {
        var stub = StubHandler.WithPing().Lines(
            """{"status":"start","id":"c1","Type":"container","Action":"start","Actor":{"ID":"c1","Attributes":{"name":"web","image":"nginx"}},"scope":"local","time":1793785824,"timeNano":1793785824000000001}""",
            """{"Type":"container","Action":"health_status: healthy","Actor":{"ID":"c1","Attributes":{}},"time":1793785825,"timeNano":1793785825000000000}""");
        using var client = stub.Client();

        var events = new List<DockerEvent>();
        await foreach (var e in client.EventsAsync(["container", "image"], DateTimeOffset.FromUnixTimeSeconds(1793785800), TestContext.Current.CancellationToken))
            events.Add(e);

        var path = Uri.UnescapeDataString(stub.Path(1));
        Assert.Equal("""/v1.47/events?since=1793785800&filters={"type":["container","image"]}""", path);
        Assert.Equal(2, events.Count);
        Assert.Equal("start", events[0].Action);
        Assert.Equal("web", events[0].Actor.Attributes!["name"]);
        Assert.Equal(1793785824000000001, events[0].TimeNano);
        Assert.Equal("health_status: healthy", events[1].Action);
    }

    [Fact]
    public async Task No_filters_means_plain_path()
    {
        var stub = StubHandler.WithPing().Lines();
        using var client = stub.Client();
        await foreach (var _ in client.EventsAsync(ct: TestContext.Current.CancellationToken)) { }
        Assert.Equal("/v1.47/events", stub.Path(1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~EventsTests"`
Expected: build FAIL, `'DockerClient' does not contain a definition for 'EventsAsync'`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Models/EventModels.cs`:

```csharp
namespace Dockyard.Engine;

public sealed class DockerEvent
{
    public string Type { get; set; } = "";
    public string Action { get; set; } = "";
    public EventActor Actor { get; set; } = new();
    public long Time { get; set; }
    public long TimeNano { get; set; }
}

public sealed class EventActor
{
    public string ID { get; set; } = "";
    public Dictionary<string, string>? Attributes { get; set; }
}
```

`src/Dockyard.Engine/Api/Events.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public async IAsyncEnumerable<DockerEvent> EventsAsync(
        IReadOnlyCollection<string>? types = null, DateTimeOffset? since = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var query = new List<string>();
        if (since is { } s) query.Add($"since={s.ToUnixTimeSeconds()}");
        if (types is { Count: > 0 }) query.Add("filters=" + Filters(types.Select(t => ("type", t)).ToArray()));
        var path = "events" + (query.Count > 0 ? "?" + string.Join('&', query) : "");

        using var res = await Send(HttpMethod.Get, path, null, stream: true, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await foreach (var e in JsonLines.ReadAsync(stream, EventJson.Default.DockerEvent, ct)) yield return e;
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DockerEvent))]
partial class EventJson : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~EventsTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Stream engine events"
```

---

### Task 8: Stats stream and CPU/memory calculation

**Files:**
- Create: `src/Dockyard.Engine/Models/StatsModels.cs`, `src/Dockyard.Engine/StatsCalculator.cs`, `src/Dockyard.Engine/Api/Stats.cs`
- Test: `tests/Dockyard.Engine.Tests/StatsTests.cs`

**Interfaces:**
- Consumes: `Send`, `JsonLines`, `RfcTime`, `IsWindows` (Task 4).
- Produces:
  - `StatsSample` (snake_case JSON: `read`, `preread`, `num_procs`, `cpu_stats`, `precpu_stats`, `memory_stats`), `CpuStats { CpuUsage, SystemCpuUsage, OnlineCpus }`, `CpuUsage { TotalUsage, PercpuUsage }`, `MemoryStats { Usage, Limit, Stats, PrivateWorkingSet }`
  - `readonly record struct ContainerUsage(double CpuPercent, ulong MemoryBytes, ulong MemoryLimitBytes)` (`MemoryLimitBytes` is 0 on Windows)
  - `static class StatsCalculator { ContainerUsage Calculate(StatsSample sample, bool windows) }` — same formulas as `docker stats`
  - `IAsyncEnumerable<ContainerUsage> StatsAsync(string id, CancellationToken ct = default)` on `DockerClient`

- [ ] **Step 1: Write the failing test**

`tests/Dockyard.Engine.Tests/StatsTests.cs`:

```csharp
using System.Text.Json;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class StatsTests
{
    const string LinuxSample = """
        {"read":"2026-11-04T10:00:01.5Z","preread":"2026-11-04T10:00:00.5Z",
         "cpu_stats":{"cpu_usage":{"total_usage":200},"system_cpu_usage":2000,"online_cpus":4},
         "precpu_stats":{"cpu_usage":{"total_usage":100},"system_cpu_usage":1000,"online_cpus":4},
         "memory_stats":{"usage":1000,"limit":8000,"stats":{"inactive_file":200}}}
        """;

    const string WindowsSample = """
        {"read":"2026-11-04T10:00:01.000000000Z","preread":"2026-11-04T10:00:00.000000000Z","num_procs":2,
         "cpu_stats":{"cpu_usage":{"total_usage":15000000}},
         "precpu_stats":{"cpu_usage":{"total_usage":10000000}},
         "memory_stats":{"privateworkingset":52428800}}
        """;

    static StatsSample Parse(string json) => JsonSerializer.Deserialize(json, StatsJson.Default.StatsSample)!;

    [Fact]
    public void Linux_cpu_and_memory_match_docker_stats()
    {
        var u = StatsCalculator.Calculate(Parse(LinuxSample), windows: false);
        Assert.Equal(40.0, u.CpuPercent, 3);
        Assert.Equal(800UL, u.MemoryBytes);
        Assert.Equal(8000UL, u.MemoryLimitBytes);
    }

    [Fact]
    public void Linux_cgroup_v1_uses_total_inactive_file()
    {
        var u = StatsCalculator.Calculate(Parse(LinuxSample.Replace("\"inactive_file\"", "\"total_inactive_file\"")), windows: false);
        Assert.Equal(800UL, u.MemoryBytes);
    }

    [Fact]
    public void First_linux_sample_without_previous_reports_zero_cpu()
    {
        var u = StatsCalculator.Calculate(Parse(LinuxSample.Replace("\"system_cpu_usage\":1000", "\"system_cpu_usage\":0")), windows: false);
        Assert.Equal(0.0, u.CpuPercent);
    }

    [Fact]
    public void Windows_cpu_and_memory_match_docker_stats()
    {
        var u = StatsCalculator.Calculate(Parse(WindowsSample), windows: true);
        Assert.Equal(25.0, u.CpuPercent, 3);
        Assert.Equal(52428800UL, u.MemoryBytes);
        Assert.Equal(0UL, u.MemoryLimitBytes);
    }

    [Fact]
    public async Task StatsAsync_streams_usage()
    {
        var stub = StubHandler.WithPing().Lines(LinuxSample.ReplaceLineEndings(""), LinuxSample.ReplaceLineEndings(""));
        using var client = stub.Client();

        var samples = new List<ContainerUsage>();
        await foreach (var u in client.StatsAsync("abc", TestContext.Current.CancellationToken)) samples.Add(u);

        Assert.Equal("/v1.47/containers/abc/stats?stream=1", stub.Path(1));
        Assert.Equal(2, samples.Count);
        Assert.Equal(40.0, samples[0].CpuPercent, 3);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~StatsTests"`
Expected: build FAIL, `The type or namespace name 'StatsSample' could not be found`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Models/StatsModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed class StatsSample
{
    [JsonPropertyName("read")] public string Read { get; set; } = "";
    [JsonPropertyName("preread")] public string PreRead { get; set; } = "";
    [JsonPropertyName("num_procs")] public uint NumProcs { get; set; }
    [JsonPropertyName("cpu_stats")] public CpuStats CpuStats { get; set; } = new();
    [JsonPropertyName("precpu_stats")] public CpuStats PreCpuStats { get; set; } = new();
    [JsonPropertyName("memory_stats")] public MemoryStats MemoryStats { get; set; } = new();
}

public sealed class CpuStats
{
    [JsonPropertyName("cpu_usage")] public CpuUsage CpuUsage { get; set; } = new();
    [JsonPropertyName("system_cpu_usage")] public ulong SystemCpuUsage { get; set; }
    [JsonPropertyName("online_cpus")] public uint OnlineCpus { get; set; }
}

public sealed class CpuUsage
{
    [JsonPropertyName("total_usage")] public ulong TotalUsage { get; set; }
    [JsonPropertyName("percpu_usage")] public List<ulong>? PercpuUsage { get; set; }
}

public sealed class MemoryStats
{
    [JsonPropertyName("usage")] public ulong Usage { get; set; }
    [JsonPropertyName("limit")] public ulong Limit { get; set; }
    [JsonPropertyName("stats")] public Dictionary<string, ulong>? Stats { get; set; }
    [JsonPropertyName("privateworkingset")] public ulong PrivateWorkingSet { get; set; }
}

public readonly record struct ContainerUsage(double CpuPercent, ulong MemoryBytes, ulong MemoryLimitBytes);
```

`src/Dockyard.Engine/StatsCalculator.cs`:

```csharp
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public static class StatsCalculator
{
    public static ContainerUsage Calculate(StatsSample sample, bool windows) => windows ? Windows(sample) : Linux(sample);

    static ContainerUsage Linux(StatsSample s)
    {
        double cpu = 0;
        if (s.PreCpuStats.SystemCpuUsage > 0)
        {
            var cpuDelta = (double)s.CpuStats.CpuUsage.TotalUsage - s.PreCpuStats.CpuUsage.TotalUsage;
            var systemDelta = (double)s.CpuStats.SystemCpuUsage - s.PreCpuStats.SystemCpuUsage;
            var cpus = s.CpuStats.OnlineCpus > 0 ? s.CpuStats.OnlineCpus : (uint)(s.CpuStats.CpuUsage.PercpuUsage?.Count ?? 1);
            if (cpuDelta > 0 && systemDelta > 0) cpu = cpuDelta / systemDelta * cpus * 100;
        }

        var usage = s.MemoryStats.Usage;
        ulong cache = 0;
        if (s.MemoryStats.Stats is { } stats)
        {
            if (stats.TryGetValue("total_inactive_file", out var v1) && v1 < usage) cache = v1;
            else if (stats.TryGetValue("inactive_file", out var v2) && v2 < usage) cache = v2;
        }
        return new ContainerUsage(cpu, usage - cache, s.MemoryStats.Limit);
    }

    static ContainerUsage Windows(StatsSample s)
    {
        double cpu = 0;
        if (RfcTime.Parse(s.Read) is { } read && RfcTime.Parse(s.PreRead) is { } preRead && preRead.Year > 1 && s.NumProcs > 0)
        {
            var possible = (double)(read - preRead).Ticks * s.NumProcs;
            var used = (double)s.CpuStats.CpuUsage.TotalUsage - s.PreCpuStats.CpuUsage.TotalUsage;
            if (possible > 0 && used > 0) cpu = used / possible * 100;
        }
        return new ContainerUsage(cpu, s.MemoryStats.PrivateWorkingSet, 0);
    }
}
```

`src/Dockyard.Engine/Api/Stats.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public async IAsyncEnumerable<ContainerUsage> StatsAsync(string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var res = await Send(HttpMethod.Get, $"containers/{Esc(id)}/stats?stream=1", null, stream: true, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await foreach (var sample in JsonLines.ReadAsync(stream, StatsJson.Default.StatsSample, ct))
            yield return StatsCalculator.Calculate(sample, IsWindows);
    }
}

[JsonSerializable(typeof(StatsSample))]
partial class StatsJson : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~StatsTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Stream container stats with docker stats CPU and memory formulas"
```

---

### Task 9: Images API (list, remove, prune, pull with progress)

**Files:**
- Create: `src/Dockyard.Engine/Models/ImageModels.cs`, `src/Dockyard.Engine/Api/Images.cs`
- Test: `tests/Dockyard.Engine.Tests/ImagesTests.cs`

**Interfaces:**
- Consumes: `GetJson`, `SendJson`, `SendNoContent`, `Send`, `Filters`, `JsonLines`, `PruneResult`, `CoreJson` (Task 4).
- Produces:
  - `ImageSummary { Id, ParentId, RepoTags, RepoDigests, Created, Size, Labels, Containers }`
  - `sealed record PullProgress(string Status, long CurrentBytes, long TotalBytes)`
  - `Task<List<ImageSummary>> ListImagesAsync(CancellationToken ct = default)`
  - `Task RemoveImageAsync(string image, bool force = false, CancellationToken ct = default)`
  - `Task<PruneResult> PruneImagesAsync(bool danglingOnly = true, CancellationToken ct = default)`
  - `Task PullImageAsync(string reference, IProgress<PullProgress>? progress = null, CancellationToken ct = default)` (throws `DockerException` on an `error` line)
  - `internal static (string Image, string Tag) SplitReference(string reference)`

- [ ] **Step 1: Write the failing test**

`tests/Dockyard.Engine.Tests/ImagesTests.cs`:

```csharp
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class ImagesTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Collect : IProgress<PullProgress>
    {
        public List<PullProgress> Items { get; } = [];
        public void Report(PullProgress value) => Items.Add(value);
    }

    [Fact]
    public async Task List_parses_images_including_untagged()
    {
        var stub = StubHandler.WithPing().Json("""
            [{"Id":"sha256:a","ParentId":"","RepoTags":["alpine:3.20"],"RepoDigests":["alpine@sha256:x"],"Created":1793785824,"Size":7800000,"Labels":null,"Containers":-1},
             {"Id":"sha256:b","ParentId":"","RepoTags":null,"RepoDigests":null,"Created":1793785000,"Size":100,"Labels":{},"Containers":-1}]
            """);
        using var client = stub.Client();

        var images = await client.ListImagesAsync(Ct);

        Assert.Equal("/v1.47/images/json", stub.Path(1));
        Assert.Equal("alpine:3.20", images[0].RepoTags![0]);
        Assert.Null(images[1].RepoTags);
        Assert.Equal(7800000, images[0].Size);
    }

    [Fact]
    public async Task Remove_and_prune_use_expected_paths()
    {
        var stub = StubHandler.WithPing().Json("""[{"Untagged":"alpine:3.20"}]""").Json("""{"ImagesDeleted":null,"SpaceReclaimed":1234}""");
        using var client = stub.Client();

        await client.RemoveImageAsync("alpine:3.20", force: true, Ct);
        var pruned = await client.PruneImagesAsync(danglingOnly: true, Ct);

        Assert.Equal("/v1.47/images/alpine%3A3.20?force=1", stub.Path(1));
        Assert.Equal(HttpMethod.Delete, stub.Requests[1].Method);
        Assert.Equal("""/v1.47/images/prune?filters={"dangling":["true"]}""", Uri.UnescapeDataString(stub.Path(2)));
        Assert.Equal(1234, pruned.SpaceReclaimed);
    }

    [Theory]
    [InlineData("alpine", "alpine", "latest")]
    [InlineData("alpine:3.20", "alpine", "3.20")]
    [InlineData("localhost:5000/app", "localhost:5000/app", "latest")]
    [InlineData("localhost:5000/app:v1", "localhost:5000/app", "v1")]
    [InlineData("alpine@sha256:abc", "alpine@sha256:abc", "")]
    public void Splits_references(string reference, string image, string tag) =>
        Assert.Equal((image, tag), DockerClient.SplitReference(reference));

    [Fact]
    public async Task Pull_reports_aggregated_progress()
    {
        var stub = StubHandler.WithPing().Lines(
            """{"status":"Pulling from library/alpine","id":"3.20"}""",
            """{"status":"Downloading","progressDetail":{"current":50,"total":100},"progress":"[==>  ]","id":"l1"}""",
            """{"status":"Downloading","progressDetail":{"current":10,"total":40},"id":"l2"}""",
            """{"status":"Pull complete","progressDetail":{},"id":"l1"}""",
            """{"status":"Pull complete","progressDetail":{},"id":"l2"}""",
            """{"status":"Status: Downloaded newer image for alpine:3.20"}""");
        using var client = stub.Client();
        var progress = new Collect();

        await client.PullImageAsync("alpine:3.20", progress, Ct);

        Assert.Equal("/v1.47/images/create?fromImage=alpine&tag=3.20", stub.Path(1));
        Assert.Equal(HttpMethod.Post, stub.Requests[1].Method);
        Assert.Equal(new PullProgress("Downloading", 60, 140), progress.Items[2]);
        Assert.Equal(new PullProgress("Status: Downloaded newer image for alpine:3.20", 140, 140), progress.Items[^1]);
    }

    [Fact]
    public async Task Pull_error_line_throws()
    {
        using var client = StubHandler.WithPing().Lines(
            """{"errorDetail":{"message":"manifest unknown"},"error":"manifest unknown"}""").Client();
        var ex = await Assert.ThrowsAsync<DockerException>(() => client.PullImageAsync("nope:1", null, Ct));
        Assert.Equal("manifest unknown", ex.Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~ImagesTests"`
Expected: build FAIL, `'DockerClient' does not contain a definition for 'ListImagesAsync'`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Models/ImageModels.cs`:

```csharp
namespace Dockyard.Engine;

public sealed class ImageSummary
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public List<string>? RepoTags { get; set; }
    public List<string>? RepoDigests { get; set; }
    public long Created { get; set; }
    public long Size { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public long Containers { get; set; }
}

public sealed record PullProgress(string Status, long CurrentBytes, long TotalBytes);

sealed class PullMessage
{
    public string? Status { get; set; }
    public string? Id { get; set; }
    public ProgressDetail? ProgressDetail { get; set; }
    public string? Error { get; set; }
}

sealed class ProgressDetail
{
    public long Current { get; set; }
    public long Total { get; set; }
}
```

`src/Dockyard.Engine/Api/Images.cs`:

```csharp
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public Task<List<ImageSummary>> ListImagesAsync(CancellationToken ct = default) =>
        GetJson("images/json", ImageJson.Default.ListImageSummary, ct);

    public async Task RemoveImageAsync(string image, bool force = false, CancellationToken ct = default)
    {
        using var _ = await Send(HttpMethod.Delete, $"images/{Esc(image)}?force={Bool(force)}", null, stream: false, ct);
    }

    public Task<PruneResult> PruneImagesAsync(bool danglingOnly = true, CancellationToken ct = default) =>
        SendJson(HttpMethod.Post, "images/prune?filters=" + Filters(("dangling", danglingOnly ? "true" : "false")), null,
            CoreJson.Default.PruneResult, ct);

    public async Task PullImageAsync(string reference, IProgress<PullProgress>? progress = null, CancellationToken ct = default)
    {
        var (image, tag) = SplitReference(reference);
        var path = $"images/create?fromImage={Esc(image)}" + (tag.Length > 0 ? $"&tag={Esc(tag)}" : "");
        using var res = await Send(HttpMethod.Post, path, null, stream: true, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);

        var layers = new Dictionary<string, (long Current, long Total)>();
        await foreach (var m in JsonLines.ReadAsync(stream, ImageJson.Default.PullMessage, ct))
        {
            if (m.Error is { } error) throw new DockerException(HttpStatusCode.InternalServerError, error);

            if (m.Id is { } id && m.ProgressDetail is { Total: > 0 } detail)
                layers[id] = (detail.Current, detail.Total);
            else if (m.Id is { } done && m.Status is "Pull complete" or "Already exists" && layers.TryGetValue(done, out var layer))
                layers[done] = (layer.Total, layer.Total);

            progress?.Report(new PullProgress(m.Status ?? "", layers.Values.Sum(l => l.Current), layers.Values.Sum(l => l.Total)));
        }
    }

    internal static (string Image, string Tag) SplitReference(string reference)
    {
        if (reference.Contains('@')) return (reference, "");
        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        return colon > slash ? (reference[..colon], reference[(colon + 1)..]) : (reference, "latest");
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<ImageSummary>))]
[JsonSerializable(typeof(PullMessage))]
partial class ImageJson : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~ImagesTests"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Add images API with pull progress"
```

---

### Task 10: Volumes, networks and system (df, prunes)

**Files:**
- Create: `src/Dockyard.Engine/Models/VolumeModels.cs`, `src/Dockyard.Engine/Api/Volumes.cs`
- Create: `src/Dockyard.Engine/Models/NetworkModels.cs`, `src/Dockyard.Engine/Api/Networks.cs`
- Create: `src/Dockyard.Engine/Models/SystemModels.cs`, `src/Dockyard.Engine/Api/System.cs`
- Test: `tests/Dockyard.Engine.Tests/VolumesNetworksSystemTests.cs`

**Interfaces:**
- Consumes: Task 4 helpers, `PruneResult`, `CoreJson`, `ImageSummary` (Task 9).
- Produces:
  - Volumes: `Volume { Name, Driver, Mountpoint, CreatedAt, Labels, Scope, UsageData }`, `VolumeUsage { Size, RefCount }`; `ListVolumesAsync(ct)`, `RemoveVolumeAsync(string name, bool force = false, ct)`, `PruneVolumesAsync(ct)` (all unused, not only anonymous).
  - Networks: `NetworkSummary { Id, Name, Driver, Scope, Internal, Created, IPAM, Containers, Labels }`, `Ipam { Config }`, `IpamConfig { Subnet, Gateway }`, `NetworkContainer { Name, IPv4Address }`; `ListNetworksAsync(ct)`, `InspectNetworkAsync(string id, ct)`, `CreateNetworkAsync(string name, string driver = "bridge", ct)` (returns id), `RemoveNetworkAsync(string id, ct)`, `ConnectNetworkAsync(string network, string container, ct)`, `DisconnectNetworkAsync(string network, string container, bool force = false, ct)`, `PruneNetworksAsync(ct)`.
  - System: `SystemDf { LayersSize, Images, Containers, Volumes, BuildCache }`, `DfContainer { Id, Names, Image, SizeRw, SizeRootFs, State }`, `BuildCacheRecord { ID, Size, InUse, Shared }`; `DiskUsageAsync(ct)`, `PruneContainersAsync(ct)`, `PruneBuildCacheAsync(ct)`.

- [ ] **Step 1: Write the failing test**

`tests/Dockyard.Engine.Tests/VolumesNetworksSystemTests.cs`:

```csharp
using System.Net;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class VolumesNetworksSystemTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Volumes_list_remove_prune()
    {
        var stub = StubHandler.WithPing()
            .Json("""{"Volumes":[{"Name":"pgdata","Driver":"local","Mountpoint":"/var/lib/docker/volumes/pgdata/_data","CreatedAt":"2026-11-04T10:00:00Z","Labels":null,"Scope":"local"}],"Warnings":null}""")
            .Empty()
            .Json("""{"VolumesDeleted":["x"],"SpaceReclaimed":42}""");
        using var client = stub.Client();

        var volumes = await client.ListVolumesAsync(Ct);
        await client.RemoveVolumeAsync("pgdata", ct: Ct);
        var pruned = await client.PruneVolumesAsync(Ct);

        Assert.Equal("pgdata", Assert.Single(volumes).Name);
        Assert.Equal("/v1.47/volumes", stub.Path(1));
        Assert.Equal("/v1.47/volumes/pgdata?force=0", stub.Path(2));
        Assert.Equal("""/v1.47/volumes/prune?filters={"all":["true"]}""", Uri.UnescapeDataString(stub.Path(3)));
        Assert.Equal(42, pruned.SpaceReclaimed);
    }

    [Fact]
    public async Task Volumes_list_handles_null_array()
    {
        using var client = StubHandler.WithPing().Json("""{"Volumes":null,"Warnings":null}""").Client();
        Assert.Empty(await client.ListVolumesAsync(Ct));
    }

    [Fact]
    public async Task Networks_crud_and_connect()
    {
        var stub = StubHandler.WithPing()
            .Json("""[{"Name":"bridge","Id":"n1","Created":"2026-11-04T10:00:00Z","Scope":"local","Driver":"bridge","Internal":false,"IPAM":{"Config":[{"Subnet":"172.17.0.0/16","Gateway":"172.17.0.1"}]},"Containers":{},"Labels":{}}]""")
            .Json("""{"Name":"bridge","Id":"n1","Driver":"bridge","Containers":{"c1":{"Name":"web","IPv4Address":"172.17.0.2/16"}}}""")
            .Json("""{"Id":"n2","Warning":""}""", HttpStatusCode.Created)
            .Empty(HttpStatusCode.OK).Empty(HttpStatusCode.OK).Empty()
            .Json("""{"NetworksDeleted":["n3"]}""");
        using var client = stub.Client();

        var list = await client.ListNetworksAsync(Ct);
        var detail = await client.InspectNetworkAsync("n1", Ct);
        var id = await client.CreateNetworkAsync("app", ct: Ct);
        await client.ConnectNetworkAsync("n2", "c1", Ct);
        await client.DisconnectNetworkAsync("n2", "c1", ct: Ct);
        await client.RemoveNetworkAsync("n2", Ct);
        await client.PruneNetworksAsync(Ct);

        Assert.Equal("172.17.0.0/16", list[0].IPAM!.Config![0].Subnet);
        Assert.Equal("web", detail.Containers!["c1"].Name);
        Assert.Equal("n2", id);
        Assert.Equal("/v1.47/networks/create", stub.Path(3));
        Assert.Equal("""{"Name":"app","Driver":"bridge"}""", stub.Bodies[3]);
        Assert.Equal("/v1.47/networks/n2/connect", stub.Path(4));
        Assert.Equal("""{"Container":"c1"}""", stub.Bodies[4]);
        Assert.Equal("""{"Container":"c1","Force":false}""", stub.Bodies[5]);
        Assert.Equal(HttpMethod.Delete, stub.Requests[6].Method);
        Assert.Equal("/v1.47/networks/prune", stub.Path(7));
    }

    [Fact]
    public async Task System_df_and_prunes()
    {
        var stub = StubHandler.WithPing()
            .Json("""
                {"LayersSize":5000,
                 "Images":[{"Id":"sha256:a","RepoTags":["alpine:3.20"],"Size":3000,"Containers":1}],
                 "Containers":[{"Id":"c1","Names":["/web"],"Image":"nginx","SizeRw":10,"SizeRootFs":200,"State":"running"}],
                 "Volumes":[{"Name":"pgdata","Driver":"local","UsageData":{"Size":700,"RefCount":1}}],
                 "BuildCache":[{"ID":"b1","Size":900,"InUse":false,"Shared":false}]}
                """)
            .Json("""{"ContainersDeleted":["c2"],"SpaceReclaimed":11}""")
            .Json("""{"CachesDeleted":["b1"],"SpaceReclaimed":900}""");
        using var client = stub.Client();

        var df = await client.DiskUsageAsync(Ct);
        var containers = await client.PruneContainersAsync(Ct);
        var cache = await client.PruneBuildCacheAsync(Ct);

        Assert.Equal("/v1.47/system/df", stub.Path(1));
        Assert.Equal(5000, df.LayersSize);
        Assert.Equal(700, df.Volumes![0].UsageData!.Size);
        Assert.Equal(900, df.BuildCache![0].Size);
        Assert.Equal(10, df.Containers![0].SizeRw);
        Assert.Equal("/v1.47/containers/prune", stub.Path(2));
        Assert.Equal(11, containers.SpaceReclaimed);
        Assert.Equal("/v1.47/build/prune", stub.Path(3));
        Assert.Equal(900, cache.SpaceReclaimed);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~VolumesNetworksSystemTests"`
Expected: build FAIL, `'DockerClient' does not contain a definition for 'ListVolumesAsync'`.

- [ ] **Step 3: Implement**

`src/Dockyard.Engine/Models/VolumeModels.cs`:

```csharp
namespace Dockyard.Engine;

public sealed class Volume
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Mountpoint { get; set; } = "";
    public string? CreatedAt { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public string Scope { get; set; } = "";
    public VolumeUsage? UsageData { get; set; }
}

public sealed class VolumeUsage
{
    public long Size { get; set; }
    public long RefCount { get; set; }
}

sealed class VolumeList
{
    public List<Volume>? Volumes { get; set; }
}
```

`src/Dockyard.Engine/Api/Volumes.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public async Task<List<Volume>> ListVolumesAsync(CancellationToken ct = default) =>
        (await GetJson("volumes", VolumeJson.Default.VolumeList, ct)).Volumes ?? [];

    public Task RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Delete, $"volumes/{Esc(name)}?force={Bool(force)}", null, ct);

    public Task<PruneResult> PruneVolumesAsync(CancellationToken ct = default) =>
        SendJson(HttpMethod.Post, "volumes/prune?filters=" + Filters(("all", "true")), null, CoreJson.Default.PruneResult, ct);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VolumeList))]
partial class VolumeJson : JsonSerializerContext;
```

`src/Dockyard.Engine/Models/NetworkModels.cs`:

```csharp
namespace Dockyard.Engine;

public sealed class NetworkSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Scope { get; set; } = "";
    public bool Internal { get; set; }
    public string? Created { get; set; }
    public Ipam? IPAM { get; set; }
    public Dictionary<string, NetworkContainer>? Containers { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

public sealed class Ipam
{
    public List<IpamConfig>? Config { get; set; }
}

public sealed class IpamConfig
{
    public string? Subnet { get; set; }
    public string? Gateway { get; set; }
}

public sealed class NetworkContainer
{
    public string Name { get; set; } = "";
    public string IPv4Address { get; set; } = "";
}

sealed class NetworkCreateRequest
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
}

sealed class NetworkCreateResponse
{
    public string Id { get; set; } = "";
}

sealed class NetworkConnectRequest
{
    public string Container { get; set; } = "";
}

sealed class NetworkDisconnectRequest
{
    public string Container { get; set; } = "";
    public bool Force { get; set; }
}
```

`src/Dockyard.Engine/Api/Networks.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public Task<List<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default) =>
        GetJson("networks", NetworkJson.Default.ListNetworkSummary, ct);

    public Task<NetworkSummary> InspectNetworkAsync(string id, CancellationToken ct = default) =>
        GetJson($"networks/{Esc(id)}", NetworkJson.Default.NetworkSummary, ct);

    public async Task<string> CreateNetworkAsync(string name, string driver = "bridge", CancellationToken ct = default)
    {
        var body = Body(new NetworkCreateRequest { Name = name, Driver = driver }, NetworkJson.Default.NetworkCreateRequest);
        return (await SendJson(HttpMethod.Post, "networks/create", body, NetworkJson.Default.NetworkCreateResponse, ct)).Id;
    }

    public Task RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Delete, $"networks/{Esc(id)}", null, ct);

    public Task ConnectNetworkAsync(string network, string container, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"networks/{Esc(network)}/connect",
            Body(new NetworkConnectRequest { Container = container }, NetworkJson.Default.NetworkConnectRequest), ct);

    public Task DisconnectNetworkAsync(string network, string container, bool force = false, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"networks/{Esc(network)}/disconnect",
            Body(new NetworkDisconnectRequest { Container = container, Force = force }, NetworkJson.Default.NetworkDisconnectRequest), ct);

    public Task PruneNetworksAsync(CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, "networks/prune", null, ct);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<NetworkSummary>))]
[JsonSerializable(typeof(NetworkSummary))]
[JsonSerializable(typeof(NetworkCreateRequest))]
[JsonSerializable(typeof(NetworkCreateResponse))]
[JsonSerializable(typeof(NetworkConnectRequest))]
[JsonSerializable(typeof(NetworkDisconnectRequest))]
partial class NetworkJson : JsonSerializerContext;
```

`src/Dockyard.Engine/Models/SystemModels.cs`:

```csharp
namespace Dockyard.Engine;

public sealed class SystemDf
{
    public long LayersSize { get; set; }
    public List<ImageSummary>? Images { get; set; }
    public List<DfContainer>? Containers { get; set; }
    public List<Volume>? Volumes { get; set; }
    public List<BuildCacheRecord>? BuildCache { get; set; }
}

public sealed class DfContainer
{
    public string Id { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Image { get; set; } = "";
    public long SizeRw { get; set; }
    public long SizeRootFs { get; set; }
    public string State { get; set; } = "";
}

public sealed class BuildCacheRecord
{
    public string ID { get; set; } = "";
    public long Size { get; set; }
    public bool InUse { get; set; }
    public bool Shared { get; set; }
}
```

`src/Dockyard.Engine/Api/System.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public Task<SystemDf> DiskUsageAsync(CancellationToken ct = default) =>
        GetJson("system/df", SystemJson.Default.SystemDf, ct);

    public Task<PruneResult> PruneContainersAsync(CancellationToken ct = default) =>
        SendJson(HttpMethod.Post, "containers/prune", null, CoreJson.Default.PruneResult, ct);

    public Task<PruneResult> PruneBuildCacheAsync(CancellationToken ct = default) =>
        SendJson(HttpMethod.Post, "build/prune", null, CoreJson.Default.PruneResult, ct);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SystemDf))]
partial class SystemJson : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "FullyQualifiedName~VolumesNetworksSystemTests"`
Expected: PASS (4 tests). Then run the whole suite: `dotnet test tests/Dockyard.Engine.Tests` — all PASS.

- [ ] **Step 5: Commit**

```pwsh
git add src/Dockyard.Engine tests/Dockyard.Engine.Tests
git commit -m "Add volumes, networks and system disk usage and prune APIs"
```

---

### Task 11: Integration tests against the real engines

**Files:**
- Test: `tests/Dockyard.Engine.Tests/EngineIntegrationTests.cs`

**Interfaces:**
- Consumes: everything above. Engines: Linux at `tcp://127.0.0.1:2375` (Dockyard `linux` context), Windows at `npipe:////./pipe/docker_windows` (`win` context). Each test skips when its engine is not reachable.

- [ ] **Step 1: Make sure the engines run**

Start both engines from the Dockyard tray (or `docker --context linux info` and `Start-Service docker`). Check: `docker --context linux version` and `docker --context win version` both print a Server section.

- [ ] **Step 2: Write the tests**

`tests/Dockyard.Engine.Tests/EngineIntegrationTests.cs`:

```csharp
using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests;

[Trait("Category", "Engine")]
public sealed class EngineIntegrationTests
{
    [Fact]
    public Task Linux_container_lifecycle() =>
        Lifecycle("tcp://127.0.0.1:2375", "alpine:3.20", ["sh", "-c", "echo hello; echo oops >&2"]);

    [Fact]
    public Task Windows_container_lifecycle()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows engine only exists on Windows");
        return Lifecycle("npipe:////./pipe/docker_windows", "mcr.microsoft.com/windows/nanoserver:ltsc2025",
            ["cmd", "/c", "echo hello& echo oops 1>&2"]);
    }

    [Fact]
    public async Task Linux_lists_resources_and_disk_usage()
    {
        var client = await TryConnect("tcp://127.0.0.1:2375");
        if (client is null) Assert.Skip("Linux engine not running");
        using (client)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.NotEmpty(await client.ListNetworksAsync(ct));
            await client.ListImagesAsync(ct);
            await client.ListVolumesAsync(ct);
            var df = await client.DiskUsageAsync(ct);
            Assert.True(df.LayersSize >= 0);
        }
    }

    static async Task<DockerClient?> TryConnect(string uri)
    {
        var client = new DockerClient(DockerEndpoint.Parse(uri));
        try
        {
            await client.PingAsync();
            return client;
        }
        catch (EngineUnavailableException)
        {
            client.Dispose();
            return null;
        }
    }

    static async Task Lifecycle(string uri, string image, List<string> cmd)
    {
        var client = await TryConnect(uri);
        if (client is null) Assert.Skip($"No engine at {uri}");
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var ct = timeout.Token;
            var since = DateTimeOffset.UtcNow.AddSeconds(-2);

            await client.PullImageAsync(image, null, ct);
            var id = await client.CreateContainerAsync(
                new CreateContainerRequest { Image = image, Cmd = cmd, Labels = new() { ["dockyard.test"] = "1" } }, null, ct);
            try
            {
                await client.StartContainerAsync(id, ct);
                await WaitForEvent(client, id, "die", since, ct);

                var lines = new List<LogLine>();
                await foreach (var line in client.LogsAsync(id, new LogOptions(Follow: false, Tail: null), ct)) lines.Add(line);
                Assert.Contains(lines, l => l.Stream == LogStream.Stdout && l.Text.Trim() == "hello");
                Assert.Contains(lines, l => l.Stream == LogStream.Stderr && l.Text.Trim() == "oops");
                Assert.All(lines, l => Assert.NotNull(l.Time));

                Assert.Contains(await client.ListContainersAsync(all: true, ct), c => c.Id == id);
                Assert.False((await client.InspectContainerAsync(id, ct)).State.Running);
            }
            finally
            {
                await client.RemoveContainerAsync(id, force: true, ct: CancellationToken.None);
            }
            await WaitForEvent(client, id, "destroy", since, ct);
        }
    }

    static async Task WaitForEvent(DockerClient client, string id, string action, DateTimeOffset since, CancellationToken ct)
    {
        await foreach (var e in client.EventsAsync(["container"], since, ct))
            if (e.Actor.ID == id && e.Action == action) return;
    }
}
```

- [ ] **Step 3: Run the integration tests**

Run: `dotnet test tests/Dockyard.Engine.Tests --filter "Category=Engine"`
Expected: PASS (3 tests) with both engines running. The first Windows run pulls `nanoserver:ltsc2025` (~120 MB). With an engine stopped, its test reports Skipped, not Failed.

- [ ] **Step 4: Run the full suite and the AOT-safety build**

Run: `dotnet test` (repo root, whole solution)
Expected: all PASS; integration tests PASS or Skipped.

Run: `dotnet build src/Dockyard.Engine -c Release`
Expected: `0 Warning(s)`, `0 Error(s)` (any IL2xxx/IL3xxx trim/AOT warning fails the build because of `TreatWarningsAsErrors`).

- [ ] **Step 5: Commit**

```pwsh
git add tests/Dockyard.Engine.Tests/EngineIntegrationTests.cs
git commit -m "Add engine integration tests for the Linux and Windows engines"
```

---

## Self-review notes

- Spec coverage (A, "Engine client"): version negotiation via `/_ping` (Task 4), typed errors (Task 4), events (Task 7), demultiplexed logs (Task 6), stats with CLI formulas (Task 8), pull progress per layer (Task 9), connection handling via `SocketsHttpHandler` (Task 2), context resolution (Task 3), all resource APIs the dashboard needs (Tasks 5, 9, 10), unit + integration tests and the AOT warning gate (Tasks 1–11). Compose actions, the dashboard store, the measurement script and CI belong to A2–A4.
- `PruneNetworksAsync` returns nothing because the engine reports no reclaimed space for networks.
- Registry authentication for pulls (`X-Registry-Auth`) is out of scope for A1; private images fail with the engine's error message, as the spec allows (login stays on the CLI). A3 decides whether the pull dialog reads the CLI credentials.
