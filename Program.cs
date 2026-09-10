using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(3000);
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));
builder.Services.AddHttpClient("DevopsHttpClient", client =>
{

    client.BaseAddress = new Uri("https://dev.azure.com/albarakatech/");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    string token = "EhTXhaCadc2UCtYkNLXa2c1HWHCCkjPbKLMzqhgzm53FAILIOh2SJQQJ99CHACAAAAAWcGBqAAASAZDO2hLF";
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

}
);

builder.Services.AddCors(options =>
{
    options.AddPolicy("DevOpsCors", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
var userSyncLocks = new ConcurrentDictionary<int, SemaphoreSlim>();
builder.Services.AddMemoryCache();
builder.Services.AddHostedService<IterationSyncBackgroundService>();
builder.Services.AddHostedService<DailyMedalAwardBackgroundService>();
builder.Services.AddHostedService<DailyMaintenanceBackgroundService>();
builder.Services.AddSingleton<ModelTrainingService>();
builder.Services.AddSingleton(sp =>
    new ZeroShotCommentClassifier("onnx/model.onnx", "onnx/vocab.txt"));
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Seed a single row of scoring toggles if it doesn't exist yet.
    if (!db.ScoringSettings.Any())
    {
        db.ScoringSettings.Add(new ScoringSettings { Id = 1 });
        db.SaveChanges();
    }
}

app.UseCors("DevOpsCors");
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider("/home/humn/albarakagame/devops-extension"),//tried using relaitve paths didnt work will fix later
    RequestPath = ""
});
app.MapGet("/evaluate-test-file", async (ZeroShotCommentClassifier classifier) =>
{
    var items = new List<TestCaseItem>();
    foreach (var line in File.ReadLines("/home/humn/albarakagame/training/data_scored.jsonl"))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var item = JsonSerializer.Deserialize<TestCaseItem>(line);
        if (item != null)
        {
            items.Add(item);
        }
    }

    if (items == null) return Results.BadRequest("Invalid JSON file.");
    var outputResults = new List<object>();
    Console.WriteLine("\n--- Batch Evaluation Results ---");
    int correct = 0, total = 0;
    foreach (var item in items)
    {
        var score = classifier.Evaluate(item.detail, item.title);
        int predictedScore = Math.Clamp((int)Math.Round((score / 25.0) + 1), 1, 5);

        total++;
        if (item.score == predictedScore) correct++;

        outputResults.Add(new
        {
            item.title,
            item.detail,
            RawScore = score,
            PredictedScore = predictedScore,
            ActualScore = item.score,
            Pass = score >= 50.0
        });
    }
    var serializeOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };
    string outputJson = JsonSerializer.Serialize(outputResults, serializeOptions);
    await File.WriteAllTextAsync("/home/humn/albarakamltest/evaluation_results.json", outputJson);
    Console.WriteLine($"correct={correct}  total={total}");
    return Results.Ok();
});

app.MapPost("/workitemupdated", async (JsonNode payload, AppDbContext db, IMemoryCache cache) =>
{
	var fields = payload?["resource"]?["fields"];
	if (fields == null) return Results.Ok();

	var editorUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
	var assignedUser = payload?["resource"]?["revision"]?["fields"]?["System.AssignedTo"]?.ToString();
	var iterationPath = payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"]?.ToString();
	int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();

	if (editorUser == null) return Results.Ok();

	var settings = await db.ScoringSettings.FirstOrDefaultAsync(s => s.Id == 1) ?? new ScoringSettings();
	var user = await GetOrCreateUserAsync(db, editorUser);
	var todayUtc = DateTime.UtcNow.Date;

	var userLock = userSyncLocks.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
	await userLock.WaitAsync();

	try
	{
		await db.Entry(user).ReloadAsync();

		if (fields["System.IterationPath"] != null)
		{
			var transaction = new PointTransaction
			{
				User = user,
				UserId = user.Id,
				Type = "Iteration Changed",
				DeltaPoints = 0,
				WorkItemId = workItemId,
				Description = "Iteration changed to current iteration not changing anything",
				Timestamp = DateTime.UtcNow
			};

			if (!settings.IterationChangedEnabled)
			{
				transaction.Description = "Iteration changed but scoring for this rule is currently disabled";
			}
			else if (iterationPath != IterationSyncBackgroundService.CurrentIterationPath)
			{
				var pointsEarnedInTask = await db.Transactions
					.Where(t => t.UserId == user.Id && t.WorkItemId == workItemId)
					.SumAsync(t => t.DeltaPoints);

				user.Points -= pointsEarnedInTask;
				transaction.DeltaPoints = -pointsEarnedInTask;
				transaction.Description = $"Iteration changed to non current iteration revoking the {pointsEarnedInTask} granted in this task";
			}
			db.Transactions.Add(transaction);
			await db.SaveChangesAsync();
		}

		if (fields["Microsoft.VSTS.Scheduling.CompletedWork"] != null && assignedUser != null)
		{
			var completedWork = fields["Microsoft.VSTS.Scheduling.CompletedWork"];
			int completedWorkNewValue = (int)(completedWork["newValue"]?.GetValue<double>() ?? 0);
			int completedWorkOldValue = (int)(completedWork["oldValue"]?.GetValue<double>() ?? 0);

			int completedPointsToday = await db.Transactions
				.Where(t => t.UserId == user.Id && t.Timestamp >= todayUtc && t.Type == "Completed Work Updated")
				.SumAsync(t => t.DeltaPoints);

			var transaction = new PointTransaction
			{
				User = user,
				UserId = user.Id,
				Type = "Completed Work Updated",
				DeltaPoints = 0,
				WorkItemId = workItemId,
				Timestamp = DateTime.UtcNow
			};

			int pointsToGive = 0;
			if (!settings.CompletedWorkEnabled)
			{
				transaction.Description = "Completed work updated but scoring for this rule is currently disabled";
			}
			else if (assignedUser == editorUser)
			{
				if (IterationSyncBackgroundService.CurrentIterationPath == iterationPath)
				{
					pointsToGive = Math.Clamp(completedWorkNewValue - completedWorkOldValue, -8 - completedPointsToday, 8 - completedPointsToday);
					transaction.Description = $"Completed work has been updated from {completedWorkOldValue} to {completedWorkNewValue} and after applying the limit {pointsToGive} is rewarded/substracted";
				}
				else transaction.Description = "Completed work updated but no points are granted since iteration is not current";
			}
			else transaction.Description = "Completed work updated but no points are granted since assigned user isnt the one editing";

			user.Points += pointsToGive;
			transaction.DeltaPoints = pointsToGive;
			db.Transactions.Add(transaction);
			await db.SaveChangesAsync();
		}

		if (fields["Microsoft.VSTS.Scheduling.RemainingWork"] != null && assignedUser != null)
		{
			var remainingWork = fields["Microsoft.VSTS.Scheduling.RemainingWork"];
			int remainingWorkNewValue = (int)(remainingWork["newValue"]?.GetValue<double>() ?? 0);
			int remainingWorkOldValue = (int)(remainingWork["oldValue"]?.GetValue<double>() ?? 0);

			int remainingPointsToday = await db.Transactions
				.Where(t => t.UserId == user.Id && t.Timestamp >= todayUtc && t.Type == "Remaining Work Updated")
				.SumAsync(t => t.DeltaPoints);

			var transaction = new PointTransaction
			{
				User = user,
				UserId = user.Id,
				Type = "Remaining Work Updated",
				DeltaPoints = 0,
				WorkItemId = workItemId,
				Timestamp = DateTime.UtcNow
			};

			int pointsToGive = 0;
			if (!settings.RemainingWorkEnabled)
			{
				transaction.Description = "Remaining work updated but scoring for this rule is currently disabled";
			}
			else if (assignedUser == editorUser)
			{
				if (IterationSyncBackgroundService.CurrentIterationPath == iterationPath)
				{
					pointsToGive = Math.Clamp(-(remainingWorkNewValue - remainingWorkOldValue), -8 - remainingPointsToday, 8 - remainingPointsToday);
					transaction.Description = $"Remaining work has been updated from {remainingWorkOldValue} to {remainingWorkNewValue} and after applying the limit {pointsToGive} is rewarded/substracted";
				}
				else transaction.Description = "Remaining work updated but no points are granted since iteration is not current";
			}
			else transaction.Description = "Remaining work updated but no points are granted since assigned user isnt the one editing";

			user.Points += pointsToGive;
			transaction.DeltaPoints = pointsToGive;
			db.Transactions.Add(transaction);
			await db.SaveChangesAsync();
		}

		if (fields["System.State"] != null && editorUser == assignedUser)
		{
			var newState = fields["System.State"]?["newValue"]?.ToString();

			var transaction = new PointTransaction
			{
				User = user,
				UserId = user.Id,
				Type = "State Changed",
				DeltaPoints = 0,
				WorkItemId = workItemId,
				Timestamp = DateTime.UtcNow
			};

			if (!settings.StateChangedEnabled)
			{
				transaction.Description = "State changed but scoring for this rule is currently disabled";
			}
			else if (newState == "Closed")
			{
				if (iterationPath == IterationSyncBackgroundService.CurrentIterationPath)
				{
					if (await db.Transactions.AnyAsync(t => t.UserId == user.Id && t.Timestamp >= todayUtc && t.Type == "Completed Work Updated"))
					{
						int earnedPointTotal = (int?)payload?["resource"]?["revision"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"]?.GetValue<double>() ?? 0;
						transaction.DeltaPoints = earnedPointTotal;
						user.Points += earnedPointTotal;
						transaction.Description = $"State changed to done granting {earnedPointTotal} points";
					}
					else
					{
						transaction.Description = "State changed to done but points not granted due to not completing any work today";
					}
				}
				else
				{
					transaction.Description = $"State changed to done but no points were granted since the iteration was {iterationPath} and not the current which is {IterationSyncBackgroundService.CurrentIterationPath}";
				}
			}
			else
			{
				// Task reopened: Check if points were previously awarded for closing this task and revert them
				var previouslyAwarded = await db.Transactions
					.Where(t => t.UserId == user.Id && t.WorkItemId == workItemId && t.Type == "State Changed")
					.SumAsync(t => t.DeltaPoints);

				if (previouslyAwarded > 0)
				{
					user.Points -= previouslyAwarded;
					transaction.DeltaPoints = -previouslyAwarded;
					transaction.Description = $"State changed from Closed to {newState ?? "not closed"}, revoking {previouslyAwarded} points previously awarded";
				}
				else
				{
					transaction.Description = $"State changed to {newState ?? "not closed"}, not granting or revoking any points";
				}
			}

			db.Transactions.Add(transaction);
			await db.SaveChangesAsync();
		}
	}
	finally
	{
		userLock.Release();
	}

	InvalidateLeaderboardCache(cache);
	return Results.Ok();
});

const int meaningfullCommentPoints = 50;
app.MapPost("/commentadded", async (
	JsonNode payload,
	AppDbContext db,
	ZeroShotCommentClassifier classifier,
	IMemoryCache cache) =>
{
	var text = payload?["resource"]?["text"]?.ToString();
	var userUniqueName = payload?["resource"]?["author"]?["uniqueName"]?.ToString();
	var userDisplayName = payload?["resource"]?["author"]?["displayName"]?.ToString();
	var title = payload?["resource"]?["revision"]?["fields"]?["System.Title"]?.ToString();
	int? workItemId = payload?["resource"]?["revision"]?["id"]?.GetValue<int>();

	if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(userUniqueName))
		return Results.Ok();

	string author = !string.IsNullOrWhiteSpace(userDisplayName)
		? $"{userDisplayName} <{userUniqueName}>"
		: userUniqueName;

	var settings = await db.ScoringSettings.FirstOrDefaultAsync(s => s.Id == 1) ?? new ScoringSettings();
	var user = await GetOrCreateUserAsync(db, author);

	var userLock = userSyncLocks.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
	await userLock.WaitAsync();

	try
	{
		await db.Entry(user).ReloadAsync();

		string cleanText = Regex.Replace(text, "<.*?>", string.Empty).Trim();

		var transaction = new PointTransaction
		{
			User = user,
			UserId = user.Id,
			Type = "Comment Added",
			DeltaPoints = 0,
			WorkItemId = workItemId,
			Timestamp = DateTime.UtcNow
		};

		int pointsAwarded = 0;
		double modelScore = classifier.Evaluate(cleanText, title);

		if (settings.CommentsEnabled)
		{
			double evaluation = modelScore / 100.0;
			if (evaluation >= 0.5)
			{
				bool alreadyRewarded = await db.Transactions.AnyAsync(t =>
					t.WorkItemId == workItemId &&
					t.UserId == user.Id &&
					t.Type == "Comment Added" &&
					t.DeltaPoints > 0);

				if (!alreadyRewarded)
				{
					pointsAwarded = (int)Math.Round(evaluation * meaningfullCommentPoints);
					transaction.Description = $"Meaningfull comment added to task granting the user {pointsAwarded} points. The comment added: {cleanText}";
					transaction.DeltaPoints = pointsAwarded;
					user.Points += pointsAwarded;
				}
				else
				{
					transaction.Description = $"Meaningfull comment added to task but not granting any points since the user already gained points from this task for meaningful comments. The comment added: {cleanText}";
				}
			}
			else
			{
				transaction.Description = $"Low effort or automated comment added to the task granting no points. The comment added: {cleanText}";
			}
		}
		else
		{
			transaction.Description = "Comment added but scoring for comments is currently disabled";
		}

		db.Transactions.Add(transaction);

		int predicted1to5 = Math.Clamp((int)Math.Round((modelScore / 25.0) + 1), 1, 5);

		var commentRecord = new EvaluatedComment
		{
			WorkItemId = workItemId,
			UserId = user.Id,
			Title = title,
			Detail = cleanText,
			ModelScore = modelScore,
			PredictedScore = predicted1to5,
			PointsAwarded = pointsAwarded,
			Timestamp = DateTime.UtcNow
		};
		db.EvaluatedComments.Add(commentRecord);

		await db.SaveChangesAsync();
	}
	finally
	{
		userLock.Release();
	}

	InvalidateLeaderboardCache(cache);
	return Results.Ok();
});
app.MapPost("/api/test-score", async (TestRequest req, ZeroShotCommentClassifier classifier) =>
{
    double score = classifier.Evaluate(req.Comment, req.Title);
    return Results.Ok(new
    {
        title = req.Title,
        comment = req.Comment,
        score = score,
        pointsAwarded = 0,
        passed = score >= 50
    });
});
app.MapGet("/api/admin/check", async (
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache) =>
{
    var caller = await GetCallerIdentityAsync(httpContext, httpClientFactory, cache);
    var isAdmin = await IsCallerAdminAsync(httpContext, httpClientFactory, cache);

    return Results.Ok(new
    {
        user = caller,
        isAdmin = isAdmin
    });
});
app.MapPost("/api/admin/clear-cache", async (
	HttpContext httpContext,
	IHttpClientFactory httpClientFactory,
	IMemoryCache cache) =>
{
	if (!await IsCallerAdminAsync(httpContext, httpClientFactory, cache))
		return Results.Unauthorized();

	if (cache is MemoryCache memoryCache)
	{
		memoryCache.Clear();
        return Results.Ok("Tüm önbellek başarıyla temizlendi.");
	}

	return Results.Ok(new { message = "Not Cleared Somehow" });
});
const string DatasetPath = "/home/humn/albarakagame/training/data_scored.jsonl";

app.MapGet("/api/admin/comments", async (
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    AppDbContext db,
    int page = 1,
    int pageSize = 20) =>
{
    if (!await IsCallerAdminAsync(httpContext, httpClientFactory, cache))
        return Results.Unauthorized();

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);

    var query = db.EvaluatedComments.OrderByDescending(c => c.Timestamp);
    var total = await query.CountAsync();
    var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

    return Results.Ok(new { items, total, page, pageSize });
});

app.MapPost("/api/admin/comments/{id}/correct", async (
	HttpContext httpContext,
	IHttpClientFactory httpClientFactory,
	IMemoryCache cache,
	AppDbContext db,
	int id,
	CommentCorrectionRequest req) =>
{
	if (!await IsCallerAdminAsync(httpContext, httpClientFactory, cache))
		return Results.Unauthorized();

	var comment = await db.EvaluatedComments.FindAsync(id);
	if (comment == null) return Results.NotFound();

	int clampedScore = Math.Clamp(req.Score, 1, 5);
	int oldPoints = comment.PointsAwarded;

	// Score >= 3 passes evaluation threshold (e.g. 3 => 25p, 4 => 38p, 5 => 50p)
	int newPoints = 0;
	if (clampedScore >= 3)
	{
		double normalized = (clampedScore - 1) / 4.0;
		newPoints = (int)Math.Round(normalized * meaningfullCommentPoints);
	}

	int delta = newPoints - oldPoints;
	int previousDisplayScore = comment.CorrectedScore ?? comment.PredictedScore;

	string description = delta > 0
		? $"Admin changed score of the comment from {previousDisplayScore} to {clampedScore}, granting {delta} points"
		: delta < 0
			? $"Admin changed score of the comment from {previousDisplayScore} to {clampedScore}, revoking {-delta} points"
			: $"Admin changed score of the comment from {previousDisplayScore} to {clampedScore}, not changing the points";

	comment.CorrectedScore = clampedScore;
	comment.PointsAwarded = newPoints;

	// Resolve the user who wrote the comment
	User? user = await db.Users.FindAsync(comment.UserId);
    if(user==null) return Results.NotFound();

    var userLock = userSyncLocks.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
    await userLock.WaitAsync();
    try{
        await db.Entry(user).ReloadAsync();
        user.Points += delta;

        var transaction = new PointTransaction
        {
            UserId = user.Id,
            User = user,
            Type = "Comment Score Corrected",
            DeltaPoints = delta,
            WorkItemId = comment.WorkItemId,
            Description = description,
            Timestamp = DateTime.UtcNow
        };

        db.Transactions.Add(transaction);


        await db.SaveChangesAsync();
    }

    finally
	{
		userLock.Release();
	}
	InvalidateLeaderboardCache(cache);


	return Results.Ok(new
	{
		comment,
		deltaPoints = delta,
		description
	});
});

app.MapPost("/api/admin/comments/{id}/add-to-dataset", async (
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    AppDbContext db,
    int id) =>
{
    if (!await IsCallerAdminAsync(httpContext, httpClientFactory, cache))
        return Results.Unauthorized();

    var comment = await db.EvaluatedComments.FindAsync(id);
    if (comment == null) return Results.NotFound();

    int finalScore = comment.CorrectedScore ?? comment.PredictedScore;

    var datasetItem = new
    {
        title = comment.Title ?? string.Empty,
        detail = comment.Detail,
        score = finalScore,
        accepted_score = ((float)(finalScore - 1)) / 4,
        accepted = finalScore >= 3,

    };
    var options = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    string jsonLine = JsonSerializer.Serialize(datasetItem, options) + "\n";
    await File.AppendAllTextAsync(DatasetPath, jsonLine, Encoding.UTF8);

    comment.IsAddedToDataset = true;
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Veri setine başarıyla eklendi.", finalScore });
});

// ---------------------------------------------------------------------
// Dashboard data APIs
// ---------------------------------------------------------------------
static async Task<string?> GetCallerIdentityAsync(HttpContext context, IHttpClientFactory httpClientFactory, IMemoryCache cache)
{
    var authHeader = context.Request.Headers.Authorization.ToString();
    #if DEBUG
    authHeader = "Bearer EhTXhaCadc2UCtYkNLXa2c1HWHCCkjPbKLMzqhgzm53FAILIOh2SJQQJ99CHACAAAAAWcGBqAAASAZDO2hLF";
    #endif

    if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return null;

    var token = authHeader["Bearer ".Length..].Trim();
    var cacheKey = $"devops_caller_{token}";

    // Return cached identity if already verified recently
    if (cache.TryGetValue(cacheKey, out string? cachedIdentity))
        return cachedIdentity;

    var client = httpClientFactory.CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);


    try
    {
        using var res = await client.GetAsync("https://vssps.dev.azure.com/albarakatech/_apis/profile/profiles/me?api-version=7.1-preview");
        if (!res.IsSuccessStatusCode) return null;
        var json = await res.Content.ReadFromJsonAsync<JsonNode>();
        var identity = json?["displayName"]?.ToString() ?? json?["emailAddress"]?.ToString();

        if (!string.IsNullOrEmpty(identity))
        {
            // Cache verified DevOps caller identity for 30 minutes
            cache.Set(cacheKey, identity, TimeSpan.FromMinutes(30));
        }
        return identity;
    }
    catch
    {
        return null;
    }
}
app.MapGet("/api/leaderboard", async (
    HttpContext httpContext,
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    string scope = "all",
    int page = 1,
    int pageSize = 15) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    var todayUtc = DateTime.UtcNow.Date;

    var caller = await GetCallerIdentityAsync(httpContext, httpClientFactory, cache);

    // Determine transaction date boundary
    DateTime? filterStartDate = scope switch
    {
        "today" => todayUtc,
        "7days" => DateTime.UtcNow.AddDays(-7),
        "sprint" => DateTime.UtcNow.AddDays(-14),
        _ => null
    };

    Dictionary<int, int> scopedScores = new();
    if (filterStartDate.HasValue)
    {
        scopedScores = await db.Transactions
            .AsNoTracking()
            .Where(t => t.Timestamp >= filterStartDate.Value)
            .GroupBy(t => t.UserId)
            .Select(g => new { UserId = g.Key, Points = g.Sum(x => x.DeltaPoints) })
            .ToDictionaryAsync(x => x.UserId, x => x.Points);
    }

    var allUsers = await db.Users.AsNoTracking().ToListAsync();

    // Order by scoped points if filtered, otherwise by total points
    var rankedList = allUsers
        .Select(u => new
        {
            u.Id,
            u.UserName,
            Points = filterStartDate.HasValue ? scopedScores.GetValueOrDefault(u.Id, 0) : u.Points
        })
        .OrderByDescending(u => u.Points)
        .ThenBy(u => u.UserName)
        .ToList();

    var totalCount = rankedList.Count;
    var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
    var pageUsers = rankedList.Skip((page - 1) * pageSize).Take(pageSize).ToList();
    var userIds = pageUsers.Select(u => u.Id).ToList();

    var todayTransactions = await db.Transactions
        .AsNoTracking()
        .Where(t => t.Timestamp >= todayUtc)
        .Select(t => new { t.UserId, t.DeltaPoints, t.Type })
        .ToListAsync();

    var topWarriorUserId = todayTransactions
        .GroupBy(t => t.UserId)
        .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.DeltaPoints) })
        .Where(x => x.Total > 0)
        .OrderByDescending(x => x.Total)
        .Select(x => (int?)x.UserId)
        .FirstOrDefault();

    var topCommenterUserId = todayTransactions
        .Where(t => t.Type == "Comment Added")
        .GroupBy(t => t.UserId)
        .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.DeltaPoints) })
        .Where(x => x.Total > 0)
        .OrderByDescending(x => x.Total)
        .Select(x => (int?)x.UserId)
        .FirstOrDefault();

    var userMedals = await db.Medals
        .AsNoTracking()
        .Where(m => userIds.Contains(m.UserId))
        .OrderByDescending(m => m.Timestamp)
        .Select(m => new { m.UserId, m.Type, m.Description, m.Timestamp })
        .ToListAsync();

    var medalGroup = userMedals.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.ToList());

    var items = pageUsers.Select(u =>
    {
        bool isCaller = !string.IsNullOrEmpty(caller) && u.UserName.Contains(caller, StringComparison.OrdinalIgnoreCase);

        var dailyBadges = new List<string>();
        if (topWarriorUserId.HasValue && u.Id == topWarriorUserId.Value)
            dailyBadges.Add("⚔️ Daily Warrior");
        if (topCommenterUserId.HasValue && u.Id == topCommenterUserId.Value)
            dailyBadges.Add("💬 Daily Commenter");

        var permanent = medalGroup.TryGetValue(u.Id, out var mList) ? mList : new();

        return new
        {
            id = u.Id,
            userName = isCaller ? u.UserName : MaskUserName(u.UserName),
            points = u.Points,
            dailyBadges,
            medals = permanent.Select(m => new
            {
                type = m.Type,
                description = m.Description,
                date = m.Timestamp.ToString("yyyy-MM-dd")
            })
        };
    });

    return Results.Ok(new { items, totalCount, page, pageSize, totalPages });
});

app.MapGet("/api/transactions", async (
    HttpContext httpContext,
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    string? workItemId,
    int page = 1,
    int pageSize = 20) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    var caller = await GetCallerIdentityAsync(httpContext, httpClientFactory, cache);

    var query = db.Transactions
        .Include(t => t.User)
        .OrderByDescending(t => t.Timestamp)
        .AsQueryable();

    if (!string.IsNullOrEmpty(caller))
        query = query.Where(t => t.User != null && t.User.UserName.Contains(caller));
    else
        return Results.Ok(new { items = Array.Empty<object>(), totalCount = 0, page, pageSize, totalPages = 0 });

    if (!string.IsNullOrWhiteSpace(workItemId) && int.TryParse(workItemId, out int parsedId))
        query = query.Where(t => t.WorkItemId == parsedId);

    var totalCount = await query.CountAsync();
    var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

    var results = await query
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(t => new
        {
            t.Id,
            UserName = t.User != null ? t.User.UserName : "(unknown)",
            t.Type,
            t.DeltaPoints,
            t.WorkItemId,
            t.Description,
            t.IterationPath,
            t.Timestamp
        })
        .ToListAsync();

    return Results.Ok(new { items = results, totalCount, page, pageSize, totalPages });
});

app.MapGet("/api/currentiteration", async (
    ) =>
        {
            return Results.Ok(new
            {
                iterationPath = IterationSyncBackgroundService.CurrentIterationPath
            });
        });
app.MapGet("/api/users", async (AppDbContext db) =>
{
    var users = await db.Users
        .OrderBy(u => u.UserName)
        .Select(u => u.UserName)
        .ToListAsync();

    return Results.Ok(users);
});


// ---------------------------------------------------------------------
// Scoring rule toggles
// ---------------------------------------------------------------------

app.MapGet("/api/settings", async (AppDbContext db) =>
{
    var settings = await db.ScoringSettings.FirstOrDefaultAsync(s => s.Id == 1) ?? new ScoringSettings();

    return Results.Ok(new
    {
        commentsEnabled = settings.CommentsEnabled,
        completedWorkEnabled = settings.CompletedWorkEnabled,
        remainingWorkEnabled = settings.RemainingWorkEnabled,
        stateChangedEnabled = settings.StateChangedEnabled,
        iterationChangedEnabled = settings.IterationChangedEnabled
    });
});

app.MapPost("/api/settings", async (
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ScoringSettingsRequest req,
    AppDbContext db) =>
{
    if (!await IsCallerAdminAsync(httpContext, httpClientFactory, cache))
        return Results.Unauthorized();

    var settings = await db.ScoringSettings.FirstOrDefaultAsync(s => s.Id == 1);
    if (settings == null)
    {
        settings = new ScoringSettings { Id = 1 };
        db.ScoringSettings.Add(settings);
    }

    settings.CommentsEnabled = req.CommentsEnabled;
    settings.CompletedWorkEnabled = req.CompletedWorkEnabled;
    settings.RemainingWorkEnabled = req.RemainingWorkEnabled;
    settings.StateChangedEnabled = req.StateChangedEnabled;
    settings.IterationChangedEnabled = req.IterationChangedEnabled;

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        commentsEnabled = settings.CommentsEnabled,
        completedWorkEnabled = settings.CompletedWorkEnabled,
        remainingWorkEnabled = settings.RemainingWorkEnabled,
        stateChangedEnabled = settings.StateChangedEnabled,
        iterationChangedEnabled = settings.IterationChangedEnabled
    });
});
app.MapPost("/api/admin/train-model", async (
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ModelTrainingService trainingService) =>
{
    if (!await IsCallerAdminAsync(httpContext, httpClientFactory, cache))
        return Results.Unauthorized();
    var (success, message) = await trainingService.TrainAndDeployAsync();
    if (!success)
        return Results.BadRequest(new { message });

    return Results.Ok(new { message });
});
app.MapGet("/dashboard", () => { return Results.File("/home/humn/albarakagame/devops-extension/dashboard.html", "text/html"); });
app.MapPost("/api/admin/sync-team", async (
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    AppDbContext db) =>
{
    if (!await IsCallerAdminAsync(httpContext, httpClientFactory, cache))
        return Results.Unauthorized();
    try
    {
        var result = await TeamSyncService.SyncTeamMembersAsync(db, httpClientFactory, cache);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});
app.MapGet("/admin", () => Results.File("/home/humn/albarakagame/devops-extension/admin.html", "text/html"));
app.MapPost("/api/admin/run-eod", async (
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    AppDbContext db,
    string? date) =>
{
    if (!await IsCallerAdminAsync(httpContext, httpClientFactory, cache))
        return Results.Unauthorized();
    DateTime targetDate = string.IsNullOrWhiteSpace(date)
        ? DateTime.UtcNow.Date.AddDays(-1)
        : DateTime.Parse(date).Date;

    await DailyMedalAwardBackgroundService.AwardDailyMedalsForDateAsync(db, targetDate);

    return Results.Ok(new { message = $"Medals calculated for {targetDate:yyyy-MM-dd}" });
});
static void InvalidateLeaderboardCache(IMemoryCache cache)
{
    cache.Remove("leaderboard_agg_all");
    cache.Remove("leaderboard_agg_today");
    cache.Remove("leaderboard_agg_7days");
    cache.Remove("leaderboard_agg_sprint");
}
static async Task<HashSet<string>> GetAdminIdentitiesAsync(IHttpClientFactory httpClientFactory, IMemoryCache cache)
{
    const string cacheKey = "devops_gamemaster_members";
    if (cache.TryGetValue(cacheKey, out HashSet<string>? cached) && cached != null)
        return cached;

    var client = httpClientFactory.CreateClient("DevopsHttpClient");
    var admins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    try
    {
        using var res = await client.GetAsync("_apis/projects/MyFirstProject/teams/gamemasters/members");
        if (res.IsSuccessStatusCode)
        {
            var json = await res.Content.ReadFromJsonAsync<JsonNode>();
            var members = json?["value"]?.AsArray();
            if (members != null)
            {
                foreach (var member in members)
                {
                    var identity = member?["identity"];
                    var uniqueName = identity?["uniqueName"]?.ToString();
                    var displayName = identity?["displayName"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(uniqueName))
                        admins.Add(uniqueName.Trim());
                    if (!string.IsNullOrWhiteSpace(displayName))
                        admins.Add(displayName.Trim());
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to fetch gamemasters team members: {ex.Message}");
    }

    // Cache the members list for 10 minutes
    cache.Set(cacheKey, admins, TimeSpan.FromMinutes(10));
    return admins;
}

static async Task<bool> IsCallerAdminAsync(HttpContext context, IHttpClientFactory httpClientFactory, IMemoryCache cache)
{
    var caller = await GetCallerIdentityAsync(context, httpClientFactory, cache);
    if (string.IsNullOrWhiteSpace(caller))
        return false;

    var adminList = await GetAdminIdentitiesAsync(httpClientFactory, cache);

    return adminList.Contains(caller) ||
        adminList.Any(admin => caller.Contains(admin, StringComparison.OrdinalIgnoreCase) ||
                               admin.Contains(caller, StringComparison.OrdinalIgnoreCase));
}
app.MapGet("/", async (AppDbContext db) =>
        {
            return Results.Redirect("dashboard");
        }
        );
app.Run();

static string MaskUserName(string name)
{
    var clean = Regex.Replace(name, @"<[^>]+>", "").Trim();
    var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return string.Join(" ", parts.Select(p => p[0] + "***"));
}
static async Task<User> GetOrCreateUserAsync(AppDbContext db, string userName)
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName);
    if (user != null)
    {
        return user;
    }

    user = new User { UserName = userName, Points = 0 };
    db.Users.Add(user);

    try
    {
        await db.SaveChangesAsync();
        return user;
    }
    catch (DbUpdateException)
    {
        // Detach the failed duplicate entity so the context doesn't track it
        db.Entry(user).State = EntityState.Detached;

        // Fetch the user record successfully committed by the competing concurrent request
        return await db.Users.FirstAsync(u => u.UserName == userName);
    }
}


public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<User> Users => Set<User>();
    public DbSet<PointTransaction> Transactions => Set<PointTransaction>();
    public DbSet<ScoringSettings> ScoringSettings => Set<ScoringSettings>();
    public DbSet<Medal> Medals => Set<Medal>();
    public DbSet<EvaluatedComment> EvaluatedComments => Set<EvaluatedComment>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>()
            .HasIndex(u => u.UserName)
            .IsUnique();

        modelBuilder.Entity<PointTransaction>()
            .HasIndex(t => t.Timestamp);

        modelBuilder.Entity<PointTransaction>()
            .HasIndex(t => new { t.UserId, t.Timestamp });

        modelBuilder.Entity<PointTransaction>()
            .HasIndex(t => new { t.UserId, t.Type, t.Timestamp });
        modelBuilder.Entity<EvaluatedComment>()
            .HasIndex(c => c.Timestamp);
    }
}

public class User
{
    public int Id { get; set; }
    public required string UserName { get; set; }
    public int Points { get; set; }
}
public class PointTransaction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public string? Type { get; set; }
    public int DeltaPoints { get; set; }
    public int? WorkItemId { get; set; }
    public string? Description { get; set; }//maybe should add iteration?
    public string? IterationPath { get; set; }
    public DateTime Timestamp { get; set; }
}

//Daily commenter: Most points earned from comments that day
//Daily warrior: Most points earned that day
//Efsane get the daily warrior 5 days in a row
//Orgeneral get the daily warrior 4 days in a row
//Tumgeneral get the daily warrior 3 days in a row
//Tuggeneral get the daily warrior 2 days in a row
//Albay get the daily warrior once

//Ordinaryus Profesor get the daily commenter 5 days in a row
//Profesor Doktor get the daily commenter 4 days in a row
//Docent doktor get the daily commenter 3 days in a row
//Doktor get the daily commenter 2 days in a row
//Ogretim Gorevlisi get the daily commenter once

//v3: Get at least X points for 5 days
//v6: Get at least X points for 15 days
//v8: Get at least X points for 45 days
//v10: Get at least X points for 90 days
//v12: Get at least X points for 150 days
public static class TeamSyncService
{
    public static async Task<object> SyncTeamMembersAsync(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache)
    {
        var client = httpClientFactory.CreateClient("DevopsHttpClient");
        const string teamName = "MyFirstProject Team";
        var encodedTeam = Uri.EscapeDataString(teamName);

        using var res = await client.GetAsync($"_apis/projects/MyFirstProject/teams/{encodedTeam}/members");
        if (!res.IsSuccessStatusCode)
        {
            var errorBody = await res.Content.ReadAsStringAsync();
            throw new HttpRequestException($"DevOps API call failed ({res.StatusCode}): {errorBody}");
        }

        var json = await res.Content.ReadFromJsonAsync<JsonNode>();
        var members = json?["value"]?.AsArray() ?? new JsonArray();

        var incomingMembers = new List<(string DisplayName, string UniqueName, string CanonicalUserName)>();
        foreach (var member in members)
        {
            var identity = member?["identity"];
            var displayName = identity?["displayName"]?.ToString()?.Trim() ?? string.Empty;
            var uniqueName = identity?["uniqueName"]?.ToString()?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(uniqueName))
                continue;

            string canonical = (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(uniqueName) && uniqueName.Contains('@'))
                ? $"{displayName} <{uniqueName}>"
                : (!string.IsNullOrWhiteSpace(displayName) ? displayName : uniqueName);

            incomingMembers.Add((displayName, uniqueName, canonical));
        }

        var existingUsers = await db.Users.ToListAsync();
        var retainedUserIds = new HashSet<int>();
        int addedCount = 0;

        foreach (var member in incomingMembers)
        {
            var matchedUser = existingUsers.FirstOrDefault(u =>
            {
                if (u.UserName.Equals(member.CanonicalUserName, StringComparison.OrdinalIgnoreCase))
                    return true;

                var existingEmail = ExtractEmail(u.UserName);
                var memberEmail = ExtractEmail(member.UniqueName);
                if (!string.IsNullOrEmpty(existingEmail) && !string.IsNullOrEmpty(memberEmail) &&
                    existingEmail.Equals(memberEmail, StringComparison.OrdinalIgnoreCase))
                    return true;

                var existingCleanName = CleanDisplayName(u.UserName);
                return !string.IsNullOrEmpty(existingCleanName) &&
                    existingCleanName.Equals(member.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            if (matchedUser != null)
            {
                retainedUserIds.Add(matchedUser.Id);
            }
            else
            {
                var newUser = new User
                {
                    UserName = member.CanonicalUserName,
                    Points = 0
                };
                db.Users.Add(newUser);
                await db.SaveChangesAsync();
                retainedUserIds.Add(newUser.Id);
                addedCount++;
            }
        }

        var usersToRemove = existingUsers.Where(u => !retainedUserIds.Contains(u.Id)).ToList();
        int removedCount = usersToRemove.Count;

        if (usersToRemove.Any())
        {
            var removeIds = usersToRemove.Select(u => u.Id).ToList();

            var medalsToRemove = await db.Medals.Where(m => removeIds.Contains(m.UserId)).ToListAsync();
            var transactionsToRemove = await db.Transactions.Where(t => removeIds.Contains(t.UserId)).ToListAsync();

            db.Medals.RemoveRange(medalsToRemove);
            db.Transactions.RemoveRange(transactionsToRemove);
            db.Users.RemoveRange(usersToRemove);
        }

        await db.SaveChangesAsync();

        cache.Remove("leaderboard_agg_all");
        cache.Remove("leaderboard_agg_today");
        cache.Remove("leaderboard_agg_7days");
        cache.Remove("leaderboard_agg_sprint");

        return new
        {
            team = teamName,
            totalInTeam = incomingMembers.Count,
            added = addedCount,
            removed = removedCount,
            activeUsers = retainedUserIds.Count
        };
    }

    private static string ExtractEmail(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return string.Empty;
        var match = Regex.Match(str, @"<([^>]+)>");
        if (match.Success) return match.Groups[1].Value.Trim().ToLowerInvariant();
        if (str.Contains('@')) return str.Trim().ToLowerInvariant();
        return string.Empty;
    }

    private static string CleanDisplayName(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return string.Empty;
        return Regex.Replace(str, @"<[^>]+>", "").Trim();
    }
}
public class Medal
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public string? Type { get; set; }
    public string? Description { get; set; }
    public DateTime Timestamp { get; set; }
}
// Single-row table of on/off switches for each scoring rule. Id is always 1.
public class ScoringSettings
{
    public int Id { get; set; } = 1;
    public bool CommentsEnabled { get; set; } = true;
    public bool CompletedWorkEnabled { get; set; } = true;
    public bool RemainingWorkEnabled { get; set; } = true;
    public bool StateChangedEnabled { get; set; } = true;
    public bool IterationChangedEnabled { get; set; } = true;
}

public class ScoringSettingsRequest
{
    public bool CommentsEnabled { get; set; } = true;
    public bool CompletedWorkEnabled { get; set; } = true;
    public bool RemainingWorkEnabled { get; set; } = true;
    public bool StateChangedEnabled { get; set; } = true;
    public bool IterationChangedEnabled { get; set; } = true;
}

public class TestCaseItem
{
    public string? title { get; set; }
    public required string detail { get; set; }
    public int? score { get; set; }
    public float? accepted_score { get; set; }
    public bool? accepted { get; set; }
}
public class TestRequest
{
    public string? Title { get; set; }
    public string Comment { get; set; } = string.Empty;
}
public class EvaluatedComment
{
    public int Id { get; set; }
    public int UserId {get; set;}
    public int? WorkItemId { get; set; }
    public string? Title { get; set; }
    public string Detail { get; set; } = string.Empty;
    public double ModelScore { get; set; }
    public int PredictedScore { get; set; }
    public int PointsAwarded  {get; set;}
    public int? CorrectedScore { get; set; }
    public bool IsAddedToDataset { get; set; }
    public DateTime Timestamp { get; set; }
}

public record CommentCorrectionRequest(int Score);

