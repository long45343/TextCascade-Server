using System.Text.Json;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class UsersFileTests
{
    private const string ValidHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA";

    [Fact]
    public void ValidateRejectsWhenNextTokenVersionMissing()
    {
        var users = new UsersFile { NextTokenVersion = 0, Users = new() { new("alice", ValidHash, 1) } };
        Assert.Throws<InvalidOperationException>(() => UsersFile.ValidateUsers(users));
    }

    [Fact]
    public void ValidateRejectsWhenTokenVersionNotLessThanWatermark()
    {
        var users = new UsersFile { NextTokenVersion = 3, Users = new() { new("alice", ValidHash, 3) } };
        Assert.Throws<InvalidOperationException>(() => UsersFile.ValidateUsers(users));
    }

    [Fact]
    public void ValidateRejectsDuplicateUsernames()
    {
        var users = new UsersFile { NextTokenVersion = 3, Users = new() { new("alice", ValidHash, 1), new("alice", ValidHash, 2) } };
        Assert.Throws<InvalidOperationException>(() => UsersFile.ValidateUsers(users));
    }

    [Fact]
    public void ValidateRejectsBadHash()
    {
        var users = new UsersFile { NextTokenVersion = 2, Users = new() { new("alice", "not-a-hash", 1) } };
        Assert.Throws<InvalidOperationException>(() => UsersFile.ValidateUsers(users));
    }

    [Fact]
    public void RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "users-" + Guid.NewGuid().ToString("N") + ".json");
        var users = new UsersFile { NextTokenVersion = 2, Users = new() { new("alice", ValidHash, 1) } };
        UsersFile.SaveUsers(path, users);
        var loaded = UsersFile.LoadUsers(path);
        Assert.Equal(2, loaded.NextTokenVersion);
        Assert.Single(loaded.Users);
        File.Delete(path);
    }

    [Fact]
    public void BuildUserLookupIsReadOnly()
    {
        var users = new UsersFile { NextTokenVersion = 2, Users = new() { new("alice", ValidHash, 1) } };
        var lookup = UsersFile.BuildUserLookup(users);
        Assert.True(lookup.ContainsKey("alice"));
    }
}
