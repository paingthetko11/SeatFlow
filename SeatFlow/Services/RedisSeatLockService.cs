using StackExchange.Redis;

namespace SeatFlow.Services;

public sealed class RedisSeatLockService(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisSeatLockService> logger)
{
    private const string ReleaseIfOwnedScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly IDatabase _database = connectionMultiplexer.GetDatabase();

    public async Task<RedisSeatLockResult> TryAcquireManyAsync(
        int showId,
        IEnumerable<int> seatIds,
        Guid ownerToken,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var acquiredSeatIds = new List<int>();
        var owner = ownerToken.ToString("D");

        try
        {
            foreach (var seatId in seatIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var expiry = expiresAtUtc - DateTime.UtcNow;
                if (expiry <= TimeSpan.Zero)
                {
                    await ReleaseManyAsync(showId, acquiredSeatIds, ownerToken);
                    return new RedisSeatLockResult(false, [], seatId);
                }

                var acquired = await _database.StringSetAsync(
                    BuildKey(showId, seatId),
                    owner,
                    expiry,
                    When.NotExists);

                if (!acquired)
                {
                    await ReleaseManyAsync(showId, acquiredSeatIds, ownerToken);
                    return new RedisSeatLockResult(false, [], seatId);
                }

                acquiredSeatIds.Add(seatId);
            }

            return new RedisSeatLockResult(true, acquiredSeatIds, null);
        }
        catch
        {
            await ReleaseManyAsync(showId, acquiredSeatIds, ownerToken);
            throw;
        }
    }

    public async Task ReleaseManyAsync(int showId, IEnumerable<int> seatIds, Guid ownerToken)
    {
        var owner = ownerToken.ToString("D");
        var releases = seatIds.Select(seatId => ReleaseIfOwnedAsync(showId, seatId, owner));
        await Task.WhenAll(releases);
    }

    private async Task ReleaseIfOwnedAsync(int showId, int seatId, string owner)
    {
        try
        {
            await _database.ScriptEvaluateAsync(
                ReleaseIfOwnedScript,
                [BuildKey(showId, seatId)],
                [owner]);
        }
        catch (RedisException exception)
        {
            logger.LogWarning(exception, "Could not release Redis lock for show {ShowId}, seat {SeatId}.", showId, seatId);
        }
    }

    private static RedisKey BuildKey(int showId, int seatId) => $"seatflow:show:{showId}:seat:{seatId}";
}

public sealed record RedisSeatLockResult(
    bool Acquired,
    IReadOnlyList<int> AcquiredSeatIds,
    int? ContendedSeatId);
