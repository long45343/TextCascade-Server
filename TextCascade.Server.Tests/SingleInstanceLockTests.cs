using System.Diagnostics;
using System.Globalization;
using System.Text;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class SingleInstanceLockTests
{
    [Fact]
    public void AcquireCreatesLockBesideUsersFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var usersFile = Path.Combine(tempDir, "users.json");
            var lockPath = Cli.CreateLockPath(usersFile);

            Assert.EndsWith("users.json.lock", lockPath, StringComparison.Ordinal);
            Assert.Equal(tempDir, Path.GetDirectoryName(lockPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SecondProcessCannotAcquireSameUsersFileLock()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var lockPath = Path.Combine(tempDir, "users.json.lock");
            using var handle1 = SingleInstanceLock.Acquire(lockPath, TimeSpan.FromMilliseconds(10));
            Assert.NotNull(handle1);

            using var handle2 = SingleInstanceLock.Acquire(lockPath, TimeSpan.FromMilliseconds(10));
            Assert.Null(handle2);

            handle1.Dispose();

            using var handle3 = SingleInstanceLock.Acquire(lockPath, TimeSpan.FromMilliseconds(10));
            Assert.NotNull(handle3);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void StaleLockIsRecovered()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var lockPath = Path.Combine(tempDir, "users.json.lock");
            // Find a non-existent PID (e.g. 999999)
            var deadPid = 999999;
            File.WriteAllText(lockPath, deadPid.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);

            using var handle = SingleInstanceLock.Acquire(lockPath, TimeSpan.FromMilliseconds(10));
            Assert.NotNull(handle);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AcquireRejectsPathWithoutDirectory()
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceLock.Acquire("users.json.lock"));
    }

    [Fact]
    public void DifferentUsersFilesCanLockIndependently()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var lockPath1 = Path.Combine(tempDir, "users1.json.lock");
            var lockPath2 = Path.Combine(tempDir, "users2.json.lock");

            using var handle1 = SingleInstanceLock.Acquire(lockPath1, TimeSpan.FromMilliseconds(10));
            using var handle2 = SingleInstanceLock.Acquire(lockPath2, TimeSpan.FromMilliseconds(10));

            Assert.NotNull(handle1);
            Assert.NotNull(handle2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
