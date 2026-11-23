using Moderato.Effects;
using Moderato.Results;
using Xunit;

namespace Mouthtrap.Core.Tests.Effects;

public sealed class EffectCompatibilityTests
{
    [Fact]
    public async Task RunExitAsync_TwoEnvironments_RunsLazilyInEachOwnEnvironment()
    {
        // Given
        var first = new ProbeDevices();
        var second = new ProbeDevices();
        var effect = Scope<ProbeError, ProbeEnvironment>.Gen(async scope =>
        {
            var devices = scope.Env.Resolve<ProbeDevices>();
            var device = await scope.Acquire(devices.Open<ProbeEnvironment>("camera"));
            return device;
        });

        Assert.Empty(first.Events);
        Assert.Empty(second.Events);

        // When / Then
        Assert.True(await effect.RunExitAsync(new(first), CancellationToken.None) is Ok<string>("camera"));
        Assert.Equal(["open:camera", "close:camera"], first.Events);
        Assert.Empty(second.Events);
        Assert.True(await effect.RunExitAsync(new(second), CancellationToken.None) is Ok<string>("camera"));
        Assert.Equal(["open:camera", "close:camera"], second.Events);
    }

    [Theory]
    [InlineData(true, "missing:C200")]
    [InlineData(false, "invalid:fps")]
    public async Task RunAsync_StepFails_WidensErrorIntoUnionAndSkipsLaterSteps(bool missing, string expected)
    {
        // Given
        var reached = false;
        var effect = Scope<ProbeError, EmptyEnvironment>.Gen(async scope =>
        {
            if (missing)
                await scope.Run(Effect<EmptyEnvironment>.Fail(new DeviceMissing("C200")));
            else
                await scope.Run(Effect<EmptyEnvironment>.Fail(new InvalidSettings("fps")));

            reached = true;
            return 1;
        });

        // When
        var result = await effect.RunAsync(CancellationToken.None);

        // Then
        var message = result switch
        {
            Ok<int>(var value) => $"ok:{value}",
            Err<ProbeError>(DeviceMissing(var id)) => $"missing:{id}",
            Err<ProbeError>(InvalidSettings(var field)) => $"invalid:{field}",
        };
        Assert.Equal(expected, message);
        Assert.False(reached);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("defect")]
    [InlineData("cancel")]
    public async Task RunExitAsync_AnyOutcome_ReleasesInReverseOrder(string outcome)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var devices = new ProbeDevices();
        var defect = new InvalidOperationException("inference failed");
        var effect = Scope<ProbeError, ProbeEnvironment>.Gen(async scope =>
        {
            await scope.Acquire(devices.Open<ProbeEnvironment>("camera"));
            await scope.Acquire(devices.Open<ProbeEnvironment>("model"));
            if (outcome == "error") throw scope.Fail(new DeviceMissing("C200"));
            if (outcome == "defect") throw defect;
            if (outcome == "cancel")
            {
                cancellation.Cancel();
                scope.CancellationToken.ThrowIfCancellationRequested();
            }
            return 42;
        });

        // When
        var exit = await effect.RunExitAsync(new(devices), cancellation.Token);

        // Then
        var actual = exit switch
        {
            Ok<int>(42) => "success",
            Ok<int> => "unexpected value",
            Err<ProbeError>(DeviceMissing("C200")) => "error",
            Err<ProbeError> => "unexpected error",
            Die(var exception) when ReferenceEquals(exception, defect) => "defect",
            Die => "unexpected defect",
            Interrupted => "cancel",
        };
        Assert.Equal(outcome, actual);
        Assert.Equal(["open:camera", "open:model", "close:model", "close:camera"], devices.Events);
    }

    [Fact]
    public async Task RunExitAsync_AlreadyCancelled_DoesNotAcquire()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var devices = new ProbeDevices();
        var effect = devices.Open<ProbeEnvironment>("camera")
            .Use(_ => Effect<ProbeEnvironment>.Succeed(1));

        // When
        var exit = await effect.RunExitAsync(new(devices), cancellation.Token);

        // Then
        Assert.True(exit is Interrupted);
        Assert.Empty(devices.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunExitAsync_CancelledDuringAcquisition_ReleasesOnceEvenIfBodyIgnoresIt(bool observeCancellation)
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var released = 0;
        var used = false;
        var cleanupCancelled = true;
        var resource = Resource.Make(
            Effect<EmptyEnvironment>.Sync(() =>
            {
                cancellation.Cancel();
                return "camera";
            }),
            (_, _) => Effect<EmptyEnvironment>.Async(ct =>
            {
                cleanupCancelled = ct.IsCancellationRequested;
                released++;
                return new ValueTask<Unit>(Unit.Value);
            }));
        var effect = resource.Use(_ => Effect<EmptyEnvironment>.Async(ct =>
        {
            if (observeCancellation) ct.ThrowIfCancellationRequested();
            used = true;
            return new ValueTask<int>(1);
        }));

        // When
        var exit = await effect.RunExitAsync(cancellation.Token);

        // Then
        Assert.True(observeCancellation ? exit is Interrupted : exit is Ok<int>(1));
        Assert.Equal(1, released);
        Assert.Equal(!observeCancellation, used);
        Assert.False(cleanupCancelled);
    }

    [Fact]
    public async Task RunExitAsync_CancelledDuringAsyncBody_WaitsForUncancelledCleanup()
    {
        // Given
        using var cancellation = new CancellationTokenSource();
        var bodyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitForCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false;
        var cleanupCancelled = true;
        var resource = Resource.Make(
            Effect<EmptyEnvironment>.Succeed("camera"),
            (_, _) => Effect<EmptyEnvironment>.Async(async ct =>
            {
                cleanupCancelled = ct.IsCancellationRequested;
                releaseEntered.SetResult();
                await allowRelease.Task;
                released = true;
                return Unit.Value;
            }));
        var effect = resource.Use(_ => Effect<EmptyEnvironment>.Async(async ct =>
        {
            bodyEntered.SetResult();
            await waitForCancellation.Task.WaitAsync(ct);
            return Unit.Value;
        }));
        var run = effect.RunExitAsync(cancellation.Token).AsTask();

        try
        {
            await bodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // When
            cancellation.Cancel();

            // Then
            await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.False(released);
        }
        finally
        {
            cancellation.Cancel();
            allowRelease.TrySetResult();
        }

        var exit = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(exit is Interrupted);
        Assert.True(released);
        Assert.False(cleanupCancelled);
    }

    [Fact]
    public async Task RunExitAsync_AcquisitionFails_ReleasesOnlyEarlierResources()
    {
        // Given
        var devices = new ProbeDevices();
        var releasedMissingDevice = false;
        Effect<string, ProbeError, ProbeEnvironment> failed =
            Effect<ProbeEnvironment>.Fail<ProbeError>(new DeviceMissing("C200"));
        var missing = Resource.Make(failed, (_, _) => Effect<ProbeEnvironment>.Sync(() =>
        {
            releasedMissingDevice = true;
            return Unit.Value;
        }));
        var effect = Scope<ProbeError, ProbeEnvironment>.Gen(async scope =>
        {
            await scope.Acquire(devices.Open<ProbeEnvironment>("first"));
            await scope.Acquire(missing);
            return Unit.Value;
        });

        // When
        var exit = await effect.RunExitAsync(new(devices), CancellationToken.None);

        // Then
        Assert.True(exit is Err<ProbeError>(DeviceMissing("C200")));
        Assert.False(releasedMissingDevice);
        Assert.Equal(["open:first", "close:first"], devices.Events);
    }

    [Fact]
    public async Task RunExitAsync_ReleaseThrows_KeepsBodyErrorAndContinuesOtherReleases()
    {
        // Given
        var devices = new ProbeDevices();
        var defect = new IOException("camera stop failed");
        var broken = Resource.Make(
            Effect<ProbeEnvironment>.Succeed("broken"),
            (_, _) => Effect<ProbeEnvironment>.Sync<Unit>(() => throw defect));
        var effect = Scope<ProbeError, ProbeEnvironment>.Gen<Unit>(async scope =>
        {
            await scope.Acquire(devices.Open<ProbeEnvironment>("first"));
            await scope.Acquire(broken);
            throw scope.Fail(new InvalidSettings("fps"));
        });

        // When
        var exit = await effect.RunExitAsync(new(devices), CancellationToken.None);

        // Then
        var die = Assert.IsType<Die>(exit.Value);
        Assert.Same(defect, die.Exception);
        Assert.Contains(die.Suppressed, item => item.Case is Failed(InvalidSettings("fps")));
        Assert.Equal(["open:first", "close:first"], devices.Events);
    }

    [Fact]
    public async Task RunAsync_Defect_RethrowsOriginalException()
    {
        // Given
        var defect = new IOException("camera failed");
        var effect = Effect<EmptyEnvironment>.Sync<int>(() => throw defect);

        // When
        var thrown = await Assert.ThrowsAsync<IOException>(async () => await effect.RunAsync(CancellationToken.None));

        // Then
        Assert.Same(defect, thrown);
    }

    [Fact]
    public async Task Try_DeclaredAndOtherException_MapsOnlyDeclaredType()
    {
        // Given
        var expected = Effect<EmptyEnvironment>.Try(
            () => int.Parse("invalid"), (FormatException _) => new InvalidSettings("fps"));
        var unexpected = Effect<EmptyEnvironment>.Try<FormatException, int, InvalidSettings>(
            () => throw new IOException("device failed"), exception => new InvalidSettings(exception.Message));

        // When / Then
        Assert.True(await expected.RunExitAsync(CancellationToken.None) is Err<InvalidSettings>);
        Assert.True(await unexpected.RunExitAsync(CancellationToken.None) is Die(IOException));
    }
}
