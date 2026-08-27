using System.Text;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class CliWatermarkTests
{
    private const string ValidHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA";

    private sealed class StaticPasswordHasher : IPasswordHasher
    {
        public string Hash(string password, Isopoh.Cryptography.Argon2.Argon2Config config) =>
            "$argon2id$v=19$m=19456,t=2,p=1$" + Convert.ToBase64String(Encoding.UTF8.GetBytes(password).AsSpan(0, Math.Min(4, password.Length))) + "$" + Convert.ToBase64String("hashbytes"u8);

        public bool Verify(string password, string encodedHash) => encodedHash == Hash(password, null!);

        public bool NeedsRehash(string encodedHash, Isopoh.Cryptography.Argon2.Argon2Config config) => false;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Runs the real CLI command set against a temp users.json. Password-consuming commands
    /// (user add) must be wrapped with <see cref="WithStdin"/> so --password-stdin reads the fed line.
    /// </summary>
    private static int WithStdin(string stdinLine, string[] args)
    {
        var original = Console.In;
        try
        {
            Console.SetIn(new StringReader(stdinLine));
            return Cli.RunCli(args, new StaticPasswordHasher());
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    private static UsersFile LoadUsers(string path) => UsersFile.LoadUsers(path);

    private static void WriteUsersFile(string path, string json) => File.WriteAllText(path, json, Encoding.UTF8);

    private static RuntimeConfig ConfigFor(string usersPath)
    {
        var config = TextCascade.Server.Config.CreateDefaultConfig();
        return config with { Files = new FilesConfig(usersPath, Path.Combine(Path.GetDirectoryName(usersPath)!, "state.json")) };
    }

    // U13
    [Fact]
    public void AddUser_Allocates_FromWatermark_Increments()
    {
        var dir = NewTempDir();
        try
        {
            var usersPath = Path.Combine(dir, "users.json");
            WriteUsersFile(usersPath, $$"""
                {
                  "nextTokenVersion": 7,
                  "users": [
                    {"username": "old", "passwordHash": "{{ValidHash}}", "tokenVersion": 3, "disabled": false}
                  ]
                }
                """);

            var config = ConfigFor(usersPath);
            var exit = WithStdin("test-password", ["user", "add", "--username", "newuser", "--password-stdin", "--config", ConfigPathFor(config)]);
            Assert.Equal(Cli.Ok, exit);

            var users = LoadUsers(usersPath);
            var added = Assert.Single(users.Users, user => user.Username == "newuser");
            Assert.Equal(7, added.TokenVersion);
            Assert.Equal(8, users.NextTokenVersion);
            Assert.Equal(3, users.Users.Single(user => user.Username == "old").TokenVersion);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // U14
    [Fact]
    public void DeleteUser_RecreateSameName_GetsFreshHigherVersion()
    {
        var dir = NewTempDir();
        try
        {
            var usersPath = Path.Combine(dir, "users.json");
            WriteUsersFile(usersPath, $$"""
                {
                  "nextTokenVersion": 5,
                  "users": [
                    {"username": "alice", "passwordHash": "{{ValidHash}}", "tokenVersion": 2, "disabled": false}
                  ]
                }
                """);

            var config = ConfigFor(usersPath);
            Assert.Equal(Cli.Ok, Cli.RunCli(["user", "delete", "--username", "alice", "--config", ConfigPathFor(config)], new StaticPasswordHasher()));
            Assert.Empty(LoadUsers(usersPath).Users);

            Assert.Equal(Cli.Ok, WithStdin("test-password", ["user", "add", "--username", "alice", "--password-stdin", "--config", ConfigPathFor(config)]));

            var users = LoadUsers(usersPath);
            var recreated = Assert.Single(users.Users);
            Assert.Equal("alice", recreated.Username);
            Assert.Equal(5, recreated.TokenVersion);
            Assert.NotEqual(2, recreated.TokenVersion);
            Assert.Equal(6, users.NextTokenVersion);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // U15
    [Fact]
    public void RevokeTokens_Sets_Watermark_Increments()
    {
        var dir = NewTempDir();
        try
        {
            var usersPath = Path.Combine(dir, "users.json");
            WriteUsersFile(usersPath, $$"""
                {
                  "nextTokenVersion": 9,
                  "users": [
                    {"username": "bob", "passwordHash": "{{ValidHash}}", "tokenVersion": 4, "disabled": false}
                  ]
                }
                """);

            var config = ConfigFor(usersPath);
            Assert.Equal(Cli.Ok, Cli.RunCli(["user", "revoke-tokens", "--username", "bob", "--config", ConfigPathFor(config)], new StaticPasswordHasher()));

            var users = LoadUsers(usersPath);
            Assert.Equal(9, users.Users.Single(user => user.Username == "bob").TokenVersion);
            Assert.Equal(10, users.NextTokenVersion);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // U16
    [Fact]
    public void AddUser_At_LongMaxWatermark_FailsFast_FileUnchanged()
    {
        var dir = NewTempDir();
        try
        {
            var usersPath = Path.Combine(dir, "users.json");
            var originalJson = $$"""
                {
                  "nextTokenVersion": 9223372036854775807,
                  "users": [
                    {"username": "old", "passwordHash": "{{ValidHash}}", "tokenVersion": 1, "disabled": false}
                  ]
                }
                """;
            WriteUsersFile(usersPath, originalJson);

            var config = ConfigFor(usersPath);
            Assert.Equal(Cli.Error, WithStdin("test-password", ["user", "add", "--username", "newuser", "--password-stdin", "--config", ConfigPathFor(config)]));

            Assert.Equal(originalJson.ReplaceLineEndings(), File.ReadAllText(usersPath, Encoding.UTF8).ReplaceLineEndings());
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // U17
    [Fact]
    public void Revoke_At_LongMaxWatermark_FailsFast()
    {
        var dir = NewTempDir();
        try
        {
            var usersPath = Path.Combine(dir, "users.json");
            var originalJson = $$"""
                {
                  "nextTokenVersion": 9223372036854775807,
                  "users": [
                    {"username": "bob", "passwordHash": "{{ValidHash}}", "tokenVersion": 1, "disabled": false}
                  ]
                }
                """;
            WriteUsersFile(usersPath, originalJson);

            var config = ConfigFor(usersPath);
            Assert.Equal(Cli.Error, Cli.RunCli(["user", "revoke-tokens", "--username", "bob", "--config", ConfigPathFor(config)], new StaticPasswordHasher()));
            Assert.Equal(originalJson.ReplaceLineEndings(), File.ReadAllText(usersPath, Encoding.UTF8).ReplaceLineEndings());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // U18
    [Fact]
    public void ValidateUsers_NextMustExceed_AllUserVersions()
    {
        var users = new UsersFile { NextTokenVersion = 5, Users = new() { new("alice", ValidHash, 5) } };
        Assert.Throws<InvalidOperationException>(() => UsersFile.ValidateUsers(users));
    }

    // U19
    [Fact]
    public void ValidateUsers_Rejects_NonPositiveVersion()
    {
        Assert.Throws<InvalidOperationException>(() =>
            UsersFile.ValidateUsers(new UsersFile { NextTokenVersion = 5, Users = new() { new("alice", ValidHash, 0) } }));
        Assert.Throws<InvalidOperationException>(() =>
            UsersFile.ValidateUsers(new UsersFile { NextTokenVersion = 5, Users = new() { new("alice", ValidHash, -1) } }));
        Assert.Throws<InvalidOperationException>(() =>
            UsersFile.ValidateUsers(new UsersFile { NextTokenVersion = 0, Users = [] }));
    }

    // U20
    [Fact]
    public void SaveUsers_AtomicWrite_LeavesOriginal_OnValidationFailure()
    {
        var dir = NewTempDir();
        try
        {
            var usersPath = Path.Combine(dir, "users.json");
            WriteUsersFile(usersPath, $$"""
                {
                  "nextTokenVersion": 5,
                  "users": [
                    {"username": "alice", "passwordHash": "{{ValidHash}}", "tokenVersion": 1, "disabled": false}
                  ]
                }
                """);
            var originalContent = File.ReadAllText(usersPath, Encoding.UTF8);

            var invalid = new UsersFile { NextTokenVersion = 5, Users = new() { new("alice", ValidHash, 5) } };
            Assert.Throws<InvalidOperationException>(() => UsersFile.SaveUsers(usersPath, invalid));

            Assert.Equal(originalContent.ReplaceLineEndings(), File.ReadAllText(usersPath, Encoding.UTF8).ReplaceLineEndings());
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string ConfigPathFor(RuntimeConfig config)
    {
        // Write a minimal TOML that pins users_file to the test path.
        var path = Path.Combine(Path.GetDirectoryName(config.Files.UsersFile)!, "textcascade.toml");
        File.WriteAllText(path, $"""
            [files]
            users_file = "{config.Files.UsersFile.Replace("\\", "\\\\")}"
            state_file = "{config.Files.StateFile.Replace("\\", "\\\\")}"
            """, Encoding.UTF8);
        return path;
    }
}