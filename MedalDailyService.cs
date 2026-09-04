using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;

public class DailyMedalAwardBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyMedalAwardBackgroundService> _logger;

    private const int DailyConsistencyThreshold = 10;

    // Static definitions for streaks and milestone promotions
    private static readonly FrozenDictionary<int, string> WarriorStreakRanks = new Dictionary<int, string>
    {
        [1] = "Albay",
        [2] = "Tuğgeneral",
        [3] = "Tümgeneral",
        [4] = "Orgeneral",
        [5] = "Genel Kurmay Başkanı"
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<int, string> CommenterStreakRanks = new Dictionary<int, string>
    {
        [1] = "Öğretim Görevlisi",
        [2] = "Doktor",
        [3] = "Doçent Doktor",
        [4] = "Profesör Doktor",
        [5] = "Ordinaryüs Profesör"
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<int, string> ConsistencyMilestones = new Dictionary<int, string>
    {
        [1] = "v3",
        [2] = "v6",
        [45] = "v8",
        [90] = "v10",
        [150] = "v12"
    }.ToFrozenDictionary();

    public DailyMedalAwardBackgroundService(
        IServiceProvider serviceProvider, 
        ILogger<DailyMedalAwardBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextMidnight = now.Date.AddDays(1);
            var delay = nextMidnight - now + TimeSpan.FromSeconds(5);

            _logger.LogInformation("Next daily medal evaluation scheduled in {Minutes:F1} minutes", delay.TotalMinutes);

            await Task.Delay(delay, stoppingToken);

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var yesterday = DateTime.UtcNow.Date.AddDays(-1);
                await AwardDailyMedalsForDateAsync(db, yesterday);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing end-of-day medals.");
            }
        }
    }

    public static async Task AwardDailyMedalsForDateAsync(AppDbContext db, DateTime targetDate)
    {
        var dayStart = targetDate.Date;
        var dayEnd = dayStart.AddDays(1);

        // Fetch yesterday's transactions to evaluate daily winners
        var dayTx = await db.Transactions
            .AsNoTracking()
            .Where(t => t.Timestamp >= dayStart && t.Timestamp < dayEnd)
            .Select(t => new { t.UserId, t.DeltaPoints, t.Type })
            .ToListAsync();

        if (dayTx.Count == 0) return;

        // 1. Evaluate Daily Warrior (top total points)
        var warrior = dayTx
            .GroupBy(t => t.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.DeltaPoints) })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Total)
            .FirstOrDefault();

        if (warrior != null)
        {
            await AwardDailyMedalAndCheckStreakAsync(db, warrior.UserId, "Daily Warrior", dayStart, WarriorStreakRanks);
        }

        // 2. Evaluate Daily Commenter (top comment points)
        var commenter = dayTx
            .Where(t => t.Type == "Comment Added")
            .GroupBy(t => t.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.DeltaPoints) })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Total)
            .FirstOrDefault();

        if (commenter != null)
        {
            await AwardDailyMedalAndCheckStreakAsync(db, commenter.UserId, "Daily Commenter", dayStart, CommenterStreakRanks);
        }

        // 3. Evaluate Consistency (v3-v12) for all active users
        var activeUserIds = dayTx.Select(t => t.UserId).Distinct().ToList();
        foreach (var userId in activeUserIds)
        {
            await EvaluateConsistencyMedalsAsync(db, userId, dayEnd);
        }

        await db.SaveChangesAsync();
    }

    private static async Task AwardDailyMedalAndCheckStreakAsync(
        AppDbContext db,
        int userId,
        string baseType,
        DateTime date,
        FrozenDictionary<int, string> streakTitles)
    {
        var start = date.Date;
        var end = start.AddDays(1);

        // Load all historical medals for this user and type once (solves AnyAsync ambiguity & eliminates loops)
        var userMedals = await db.Medals
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.Type, m.Timestamp })
            .ToListAsync();

        bool alreadyAwarded = userMedals.Any(m => m.Type == baseType && m.Timestamp >= start && m.Timestamp < end);
        if (alreadyAwarded) return;

        // Award base daily medal
        db.Medals.Add(new Medal
        {
            UserId = userId,
            Type = baseType,
            Description = $"Won {baseType} on {date:yyyy-MM-dd}",
            Timestamp = date.Date.AddHours(23).AddMinutes(59).AddSeconds(59)
        });

        // Calculate consecutive streak looking backwards
        var awardedDays = userMedals
            .Where(m => m.Type == baseType)
            .Select(m => m.Timestamp.Date)
            .ToHashSet();

        int streak = 1;
        var checkDate = start.AddDays(-1);
        while (awardedDays.Contains(checkDate))
        {
            streak++;
            checkDate = checkDate.AddDays(-1);
        }

        // Award rank promotion if milestone reached and not already owned
        if (streakTitles.TryGetValue(streak, out var rankTitle))
        {
            bool hasRank = userMedals.Any(m => m.Type == rankTitle);
            if (!hasRank)
            {
                db.Medals.Add(new Medal
                {
                    UserId = userId,
                    Type = rankTitle,
                    Description = $"Earned {rankTitle} by maintaining {baseType} for {streak} consecutive day(s)",
                    Timestamp = DateTime.UtcNow
                });
            }
        }
    }

    private static async Task EvaluateConsistencyMedalsAsync(AppDbContext db, int userId, DateTime evaluationCutoff)
    {
        // 1. Fetch user's existing medals to avoid awarding duplicates
        var existingMedalTypes = await db.Medals
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.Type)
            .ToListAsync();

        var existingSet = existingMedalTypes.Where(t => t != null).ToHashSet();

        // 2. Fetch past transaction history up to cutoff date
        var userTransactions = await db.Transactions
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.Timestamp < evaluationCutoff)
            .Select(t => new { t.Timestamp, t.DeltaPoints })
            .ToListAsync();

        int qualifiedDays = userTransactions
            .GroupBy(t => t.Timestamp.Date)
            .Count(g => g.Sum(x => x.DeltaPoints) >= DailyConsistencyThreshold);

        // 3. Check milestones against in-memory set (completely eliminates DB round-trips & AnyAsync type errors)
        foreach (var (requiredDays, medalType) in ConsistencyMilestones)
        {
            if (qualifiedDays >= requiredDays && !existingSet.Contains(medalType))
            {
                db.Medals.Add(new Medal
                {
                    UserId = userId,
                    Type = medalType,
                    Description = $"Earned {medalType} for reaching at least {DailyConsistencyThreshold} points on {requiredDays} separate days",
                    Timestamp = DateTime.UtcNow
                });

                existingSet.Add(medalType);
            }
        }
    }
}
