using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

internal sealed class UserFileWatcher : IDisposable
{
    private readonly string usersPath;
    private readonly SyncServer server;
    private readonly ILogger logger;
    private readonly TimeSpan debounce;
    private readonly TimeSpan pollFallback;

    private readonly object scheduleGate = new();
    private CancellationTokenSource? reloadDelay;
    private CancellationTokenSource? reloadExecution;
    private int reloadQueued;

    private FileSystemWatcher? watcher;
    private PeriodicTimer? pollTimer;
    private Task? pollTask;
    private bool started;
    private bool disposed;

    public UserFileWatcher(
        string usersPath,
        SyncServer server,
        ILogger logger,
        TimeSpan? debounce = null,
        TimeSpan? pollFallback = null)
    {
        this.usersPath = Path.GetFullPath(usersPath);
        this.server = server;
        this.logger = logger;
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        this.pollFallback = pollFallback ?? TimeSpan.FromSeconds(30);
    }

    public void Start()
    {
        lock (scheduleGate)
        {
            if (started || disposed) return;
            started = true;

            var directory = Path.GetDirectoryName(usersPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Users file path must include a parent directory.");
            }

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var fileName = Path.GetFileName(usersPath);

            watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnFileError;
            watcher.EnableRaisingEvents = true;

            pollTimer = new PeriodicTimer(pollFallback);
            reloadExecution = new CancellationTokenSource();
            var executionToken = reloadExecution.Token;

            pollTask = Task.Run(async () =>
            {
                try
                {
                    while (await pollTimer.WaitForNextTickAsync(executionToken))
                    {
                        ScheduleReload();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs eventArgs)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var fullPath = Path.GetFullPath(eventArgs.FullPath);
        if (string.Equals(fullPath, usersPath, comparison))
        {
            ScheduleReload();
        }
    }

    private void OnFileRenamed(object? sender, RenamedEventArgs eventArgs)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var fullPath = Path.GetFullPath(eventArgs.FullPath);
        var oldFullPath = Path.GetFullPath(eventArgs.OldFullPath);
        if (string.Equals(fullPath, usersPath, comparison) || string.Equals(oldFullPath, usersPath, comparison))
        {
            ScheduleReload();
        }
    }

    private void OnFileError(object? sender, ErrorEventArgs eventArgs)
    {
        logger.LogWarning(eventArgs.GetException(), "Users file watcher error encountered.");
    }

    private void ScheduleReload()
    {
        lock (scheduleGate)
        {
            if (disposed) return;

            // If a delay is already running, do not reset it
            if (reloadDelay is not null && !reloadDelay.IsCancellationRequested)
            {
                return;
            }

            reloadDelay = new CancellationTokenSource();
            var delayToken = reloadDelay.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(debounce, delayToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (scheduleGate)
                {
                    if (disposed) return;
                    reloadDelay?.Dispose();
                    reloadDelay = null;
                }

                if (Interlocked.CompareExchange(ref reloadQueued, 1, 0) == 0)
                {
                    try
                    {
                        await ReloadAsync();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref reloadQueued, 0);
                    }
                }
            });
        }
    }

    private async Task ReloadAsync()
    {
        UsersFile? users = null;
        Exception? lastException = null;

        // Try reading with short backoff (3 attempts, 50ms exponential backoff)
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                users = await Task.Run(() => UsersFile.LoadUsers(usersPath));
                lastException = null;
                break;
            }
            catch (Exception exception) when (
                exception is IOException
                or JsonException
                or DecoderFallbackException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
            }
        }

        if (users is not null)
        {
            try
            {
                server.ReplaceUserLookup(users);
                logger.LogInformation("Users file reloaded. users={Count}", users.Users.Count);
            }
            catch (Exception exception) when (exception is InvalidOperationException)
            {
                logger.LogWarning(exception, "Users file reload validation failed; retaining previous users. path={Path}", usersPath);
            }
        }
        else if (lastException is not null)
        {
            logger.LogWarning(lastException, "Users file reload failed; retaining previous users. path={Path}", usersPath);
        }
    }

    public void Dispose()
    {
        lock (scheduleGate)
        {
            if (disposed) return;
            disposed = true;

            try
            {
                reloadDelay?.Cancel();
                reloadDelay?.Dispose();
            }
            catch { }

            try
            {
                reloadExecution?.Cancel();
                reloadExecution?.Dispose();
            }
            catch { }

            if (watcher is not null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnFileChanged;
                watcher.Created -= OnFileChanged;
                watcher.Deleted -= OnFileChanged;
                watcher.Renamed -= OnFileRenamed;
                watcher.Error -= OnFileError;
                watcher.Dispose();
                watcher = null;
            }

            pollTimer?.Dispose();
            pollTimer = null;
        }
    }
}
