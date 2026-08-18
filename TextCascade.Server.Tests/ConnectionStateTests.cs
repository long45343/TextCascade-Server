using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class ConnectionStateTests
{
    [Fact]
    public void UnsolicitedPongIsRejectedUntilPingIsAwaited()
    {
        var state = new ConnectionStateBag(TextCascade.Server.Config.CreateDefaultConfig());

        Assert.False(state.TryTakePongAwaiting());
    }

    [Fact]
    public void ExpectedPongIsAcceptedOnceAndThenRejectedAgain()
    {
        var state = new ConnectionStateBag(TextCascade.Server.Config.CreateDefaultConfig());

        state.MarkPingAwaitingPong();
        Assert.True(state.TryTakePongAwaiting());
        Assert.False(state.TryTakePongAwaiting());
    }
}

public class CliPasswordInputTests
{
    [Fact]
    public void DetectsPasswordStdinFlag()
    {
        Assert.True(Cli.HasPasswordStdin(new[] { "add", "--username", "alice", "--password-stdin" }));
        Assert.False(Cli.HasPasswordStdin(new[] { "add", "--username", "alice" }));
    }

    [Fact]
    public void PasswordStdinReadsOneLineWithoutConsoleKeyInput()
    {
        var original = Console.In;
        try
        {
            Console.SetIn(new StringReader("secret-password\n"));
            var args = new[] { "add", "--username", "alice", "--password-stdin" };
            Assert.Equal("secret-password", Cli.ReadPassword("Password: ", args));
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Fact]
    public void PasswordStdinRejectsEmptyInput()
    {
        var original = Console.In;
        try
        {
            Console.SetIn(new StringReader(string.Empty));
            var args = new[] { "hash", "--password-stdin" };
            Assert.Throws<ArgumentException>(() => Cli.ReadPassword("Password: ", args));
        }
        finally
        {
            Console.SetIn(original);
        }
    }
}

