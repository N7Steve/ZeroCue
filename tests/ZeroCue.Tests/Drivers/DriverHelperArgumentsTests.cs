using ZeroCue.DataProbe.Services.Drivers;
using Xunit;

namespace ZeroCue.Tests.Drivers;

public class DriverHelperArgumentsTests
{
    [Fact]
    public void Round_trips_and_filters_package_hints()
    {
        var pipe = $"ZeroCue.Driver.{Guid.NewGuid():N}";
        var args = DriverHelperArguments.Build(DriverOperationKind.Restore, DriverTarget.Receiver, pipe, new[] { "oem55.inf", "oem32.inf" });
        args[^1] += @",..\evil.inf,oem 9.inf";

        Assert.True(DriverHelperArguments.TryParse(args, out var kind, out var target, out var pipeName, out var hints));
        Assert.Equal(DriverOperationKind.Restore, kind);
        Assert.Equal(DriverTarget.Receiver, target);
        Assert.Equal(pipe, pipeName);
        Assert.Equal(new[] { "oem55.inf", "oem32.inf" }, hints);
    }

    [Fact]
    public void Rejects_unexpected_pipe_names()
    {
        var args = new[] { DriverHelperArguments.Switch, "install", "gamepad", "--pipe", @"\\.\pipe\other" };
        Assert.False(DriverHelperArguments.TryParse(args, out _, out _, out _, out _));
    }

    [Fact]
    public void Result_messages_round_trip()
    {
        var result = DriverOperationResult.For(DriverOperationKind.Install, DriverTarget.Gamepad, DriverOperationStatus.SuccessWithWarnings);
        result.Warnings.Add("w");
        result.RebootRequired = true;

        var parsed = DriverHelperMessage.TryParse(new DriverHelperMessage { Type = "result", Result = result }.Serialize());

        Assert.NotNull(parsed?.Result);
        Assert.Equal(DriverOperationStatus.SuccessWithWarnings, parsed!.Result!.Status);
        Assert.True(parsed.Result.RebootRequired);
        Assert.True(parsed.Result.Succeeded);
    }
}
