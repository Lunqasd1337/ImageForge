using System.Text;
using ImageForge.Core.Contracts;
using ImageForge.Core.Planning;
using ImageForge.Core.Registry.RegFile;
using ImageForge.Core.Rpc;
using ImageForge.Core.Tests.Workspace;
using ImageForge.Core.Workspace;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace ImageForge.Core.Tests.Rpc;

/// <summary>The real engine contract over the bounded transport, backed by a workspace session with fake servicing.</summary>
public sealed class EngineContractTests : IAsyncDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly List<JsonRpc> _connections = [];

    public EngineContractTests()
    {
        _dir.WriteFile("media/sources/install.wim", "WIM");
        _dir.WriteFile("media/sources/boot.wim", "BOOT");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var rpc in _connections)
        {
            rpc.Dispose();
            await rpc.Completion.ConfigureAwait(false);
        }

        _dir.Dispose();
    }

    private IEngineService Connect(Func<string, string, string, IReadOnlyList<LocationProblem>> validate)
    {
        var session = new WorkspaceSession(_ => new FakeServicing(), Guid.NewGuid, dir => new TestPaths(dir), validate);
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        var server = EngineRpc.Create(serverStream, RpcLimits.Engine);
        server.AddLocalRpcTarget<IEngineService>(new SessionEngine(session), null);
        server.StartListening();
        var client = EngineRpc.Create(clientStream, RpcLimits.Client);
        var engine = client.Attach<IEngineService>();
        client.StartListening();
        _connections.AddRange([client, server]);
        return engine;
    }

    [Fact]
    public async Task Open_plan_start_and_recover_round_trip()
    {
        var engine = Connect((_, _, _) => []);
        var output = _dir.Combine("out", "Win11");
        var opened = await engine.OpenWorkspaceAsync(new OpenWorkspaceRequest(_dir.Combine("media"), _dir.Combine("work"), output), Ct);
        opened.Problems.ShouldBeEmpty();
        opened.WorkspaceId.ShouldNotBe(Guid.Empty);

        var plan = await engine.PlanAsync(new PlanOptions(null), Ct);
        var preview = plan.Preview.ShouldHaveSingleItem();
        preview.SubKey.ShouldBe(@"ImageForge\Validation");
        preview.NewValue.ShouldNotBeNull().Text.ShouldBe("M1");

        var lines = new List<string>();
        var outcome = await engine.StartAsync(new StartPlanRequest(plan.PlanId, plan.WorkspaceRevision, Guid.NewGuid()), new Progress(lines), Ct);

        outcome.Rejection.ShouldBe(StartPlanRejection.None);
        outcome.Status.ShouldBe(RunStatus.Published, outcome.Error);
        outcome.PublishedPath.ShouldBe(output);
        var summary = await engine.GetWorkspaceAsync(Ct);
        summary.Target.ShouldBe(TargetStatus.Completed);
        summary.LastRun.ShouldNotBeNull().Status.ShouldBe(RunStatus.Published);
        (await engine.RecoverAsync(null, Ct)).Outcome.ShouldBe(RecoveryOutcome.NothingToDo);
    }

    [Fact]
    public async Task Location_problems_and_rejections_reach_the_client()
    {
        var engine = Connect((_, _, _) => [new LocationProblem(LocationProblemKind.Same, WorkspaceLocations.Source, WorkspaceLocations.OutputDir)]);

        var opened = await engine.OpenWorkspaceAsync(new OpenWorkspaceRequest("a", "b", "c"), Ct);

        opened.Problems.ShouldBe([new LocationProblem(LocationProblemKind.Same, WorkspaceLocations.Source, WorkspaceLocations.OutputDir)]);
        (await engine.StartAsync(new StartPlanRequest(Guid.NewGuid(), 1, Guid.NewGuid()), null, Ct)).Rejection.ShouldBe(StartPlanRejection.NotOpen);
        await Should.ThrowAsync<RemoteInvocationException>(() => engine.PlanAsync(new PlanOptions(null), Ct));
    }

    [Fact]
    public async Task A_reg_file_and_its_problems_cross_the_channel()
    {
        var engine = Connect((_, _, _) => []);
        await engine.OpenWorkspaceAsync(new OpenWorkspaceRequest(_dir.Combine("media"), _dir.Combine("work"), _dir.Combine("out", "Win11")), Ct);
        static RegFileInput File(string body) => new(
            "t.reg",
            [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Windows Registry Editor Version 5.00\r\n\r\n" + body)],
            RegFileEncoding.Auto,
            1251);

        var good = await engine.PlanAsync(new PlanOptions(File("[HKEY_CURRENT_USER\\Software\\Classes\\.x]\r\n@=\"y\"\r\n")), Ct);
        var bad = await engine.PlanAsync(new PlanOptions(File("[HKEY_LOCAL_MACHINE\\SOFTWARE\\X]\r\n\"a\"=dword:zz\r\n")), Ct);

        good.PlanId.ShouldNotBe(Guid.Empty);
        good.FileEncoding.ShouldBe("UTF-16");
        good.Preview.ShouldContain(p => p.FirstLogon && p.NewValue != null && p.NewValue.Text == "y");
        bad.PlanId.ShouldBe(Guid.Empty);
        bad.FileDiagnostics.Single().Line.ShouldBe(4);
    }

    private sealed class Progress(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines)
            {
                lines.Add(value);
            }
        }
    }

    private sealed class SessionEngine(WorkspaceSession session) : IEngineService
    {
        public Task<EngineInfo> GetInfoAsync(CancellationToken ct) =>
            Task.FromResult(new EngineInfo(EngineProtocol.Version, "test", Guid.NewGuid(), Environment.ProcessId, []));

        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<WorkspaceSummary> OpenWorkspaceAsync(OpenWorkspaceRequest request, CancellationToken ct) => Task.FromResult(session.Open(request));

        public Task<WorkspaceSummary> GetWorkspaceAsync(CancellationToken ct) => Task.FromResult(session.Summary());

        public Task<PlanSummary> PlanAsync(PlanOptions options, CancellationToken ct) => Task.FromResult(session.Plan(options));

        public Task<RunOutcome> StartAsync(StartPlanRequest request, IProgress<string>? progress, CancellationToken ct) => session.StartAsync(request, progress, ct);

        public Task<RecoveryResult> RecoverAsync(IProgress<string>? progress, CancellationToken ct) => session.RecoverAsync(progress, ct);
    }
}
