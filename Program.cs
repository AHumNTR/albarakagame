using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(3000);
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));
builder.Services.AddHttpClient("IterationClient", client =>
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

builder.Services.AddHostedService<IterationSyncBackgroundService>();
builder.Services.AddHostedService<DailyMedalAwardBackgroundService>();
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
	FileProvider = new PhysicalFileProvider("/home/humn/albarakagame/devops-extension"),
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

app.MapPost("/workitemupdated", async (JsonNode payload, AppDbContext db) =>
{
    var fields = payload?["resource"]?["fields"];
    if (fields == null) return Results.Ok();

    var editorUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
    var assignedUser = payload?["resource"]?["revision"]?["fields"]?["System.AssignedTo"]?.ToString();
    var iterationPath = payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"]?.ToString();
    int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();

    if (editorUser == null) return Results.Ok();

    // 0. Load the scoring toggles once for this request
    var settings = await db.ScoringSettings.FirstOrDefaultAsync(s => s.Id == 1) ?? new ScoringSettings();

    // 1. Ensure user exists before processing any transactions
    var user = await GetOrCreateUserAsync(db, editorUser);
    var todayUtc = DateTime.UtcNow.Date;

    // 2. Handle Iteration Update
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
            IterationPath=iterationPath,
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

    // 3. Handle Completed Work Changed
    if (fields["Microsoft.VSTS.Scheduling.CompletedWork"] != null)
    {
        if (assignedUser != null)
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
                IterationPath=iterationPath,
                Timestamp = DateTime.UtcNow
            };

            int pointsToGiveAfterLimitCompleted = 0;
            if (!settings.CompletedWorkEnabled)
            {
                transaction.Description = "Completed work updated but scoring for this rule is currently disabled";
            }
            else if (assignedUser == editorUser)
            {
                if (IterationSyncBackgroundService.CurrentIterationPath == iterationPath)
                {
                    pointsToGiveAfterLimitCompleted = Math.Clamp(completedWorkNewValue - completedWorkOldValue, -8 - completedPointsToday, 8 - completedPointsToday);
                    transaction.Description = $"Completed work has been updated from {completedWorkOldValue} to {completedWorkNewValue} and after applying the limit {pointsToGiveAfterLimitCompleted} is rewarded/substracted";
                }
                else transaction.Description = "Completed work updated but no points are granted since iteration is not current";
            }
            else transaction.Description = "Completed work updated but no points are granted since assigned user isnt the one editing";

            user.Points += pointsToGiveAfterLimitCompleted;
            transaction.DeltaPoints = pointsToGiveAfterLimitCompleted;

            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
        }
    }

    // 4. Handle Remaining Work Changed
    if (fields["Microsoft.VSTS.Scheduling.RemainingWork"] != null)
    {
        if (assignedUser != null)
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

                IterationPath=iterationPath,
                Timestamp = DateTime.UtcNow
            };

            int pointsToGiveAfterLimitRemaining = 0;
            if (!settings.RemainingWorkEnabled)
            {
                transaction.Description = "Remaining work updated but scoring for this rule is currently disabled";
            }
            else if (assignedUser == editorUser)
            {
                if (IterationSyncBackgroundService.CurrentIterationPath == iterationPath)
                {
                    pointsToGiveAfterLimitRemaining = Math.Clamp(-(remainingWorkNewValue - remainingWorkOldValue), -8 - remainingPointsToday, 8 - remainingPointsToday);
                    transaction.Description = $"Remaining work has been updated from {remainingWorkOldValue} to {remainingWorkNewValue} and after applying the limit {pointsToGiveAfterLimitRemaining} is rewarded/substracted";
                }
                else transaction.Description = "Remaining work updated but no points are granted since iteration is not current";
            }
            else transaction.Description = "Remaining work updated but no points are granted since assigned user isnt the one editing";

            user.Points += pointsToGiveAfterLimitRemaining;
            transaction.DeltaPoints = pointsToGiveAfterLimitRemaining;

            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
        }
    }

    // 5. Handle State Changed
    if (fields["System.State"] != null)
    {
        if (editorUser == assignedUser)
        {
            var transaction = new PointTransaction
            {
                User = user,
                UserId = user.Id,
                Type = "State Changed",
                DeltaPoints = 0,
                WorkItemId = workItemId,
                Description = "State changed to something other than done not granting any points",

                IterationPath=iterationPath,
                Timestamp = DateTime.UtcNow
            };

            int earnedPointTotal = 0;
            if (!settings.StateChangedEnabled)
            {
                transaction.Description = "State changed but scoring for this rule is currently disabled";
            }
            else if (iterationPath == IterationSyncBackgroundService.CurrentIterationPath)
            {
                if (fields["System.State"]?["newValue"]?.ToString() == "Closed")
                {
                    // Checks if any completed work was logged today before granting closing points
                    if (await db.Transactions.AnyAsync(t => t.UserId == user.Id && t.Timestamp >= todayUtc && t.Type == "Completed Work Updated"))
                    {
                        earnedPointTotal = (int?)payload?["resource"]?["revision"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"]?.GetValue<double>() ?? 0;
                        transaction.Description = $"State changed to done granting {earnedPointTotal} points";
                    }
                    else transaction.Description = "State changed to done but points not granted due to not completing any work today";
                }
                else transaction.Description = $"State changed to done but no points were granted since the iteration was {iterationPath} and not the current which is {IterationSyncBackgroundService.CurrentIterationPath}";

                transaction.DeltaPoints = earnedPointTotal;
                user.Points += earnedPointTotal;
            }

            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
        }
    }

    return Results.Ok();
});

const int meaningfullCommentPoints = 50;
app.MapPost("/commentadded", async (JsonNode payload, AppDbContext db, ZeroShotCommentClassifier classifier) =>
{
    //give the meaningfullCommentPoints amount points to the user if its the assigned user and its the first meaningfull comment

    var editorUser = payload?["resource"]?["fields"]?["System.ChangedBy"]?.ToString();
    var assignedUser = payload?["resource"]?["fields"]?["System.AssignedTo"]?.ToString();
    if (editorUser != assignedUser) return Results.Ok();
    int? workItemId = payload?["resource"]?["id"]?.GetValue<int>();
    string? message = payload?["resource"]?["fields"]?["System.History"]?.ToString();
    string? title = payload?["resource"]?["fields"]?["System.Title"]?.ToString();
    var iterationPath = payload?["resource"]?["fields"]?["System.IterationPath"]?.ToString();
    string cleanText = WebUtility.HtmlDecode(

    Regex.Replace(message ?? string.Empty, "<.*?>", " ")
    );

    var settings = await db.ScoringSettings.FirstOrDefaultAsync(s => s.Id == 1) ?? new ScoringSettings();

    var user = await GetOrCreateUserAsync(db, editorUser);


    var transaction = new PointTransaction
    {
        User = user,
        UserId = user.Id,
        Type = "Comment Added",
        DeltaPoints = 0,//shouldnt cause a issue in seperating them since they cant call at the same time
        WorkItemId = workItemId,
        Description = $"",
        IterationPath=iterationPath,
        Timestamp = DateTime.UtcNow
    };

    if (!settings.CommentsEnabled)
    {
        transaction.Description = $"Comment added but scoring for this rule is currently disabled. The comment added: {cleanText}";
        transaction.DeltaPoints = 0;
    }
    else
    {
        double evaulation = classifier.Evaluate(cleanText, title) / 100;

        Console.WriteLine(evaulation);
        if (evaulation >= 0.5)
        {
            //check if this is the first meaningfull comment (checking the points to see if its the first)
            if (!(await db.Transactions.AnyAsync(t => t.WorkItemId == workItemId && t.User == user && t.Type == "Comment Added" && t.DeltaPoints > 0)))
            {
                transaction.Description = $"Meaningfull comment added to task granting the user {(int)(evaulation * meaningfullCommentPoints)} points. The comment added: {cleanText}";
                transaction.DeltaPoints = (int)(evaulation * meaningfullCommentPoints);
                user.Points += (int)(evaulation * meaningfullCommentPoints);
            }
            else
            {
                transaction.Description = $"Meaningfull comment added to task but not granting any points since the user already gained points from this task for meaningful comments. The comment added: {cleanText}";
                transaction.DeltaPoints = 0;
            }
        }
        else
        {
            transaction.Description = $"Low effort or automated comment added to the task granting no points. The comment added: {cleanText}";
            transaction.DeltaPoints = 0;
        }
    }

    db.Transactions.Add(transaction);
    await db.SaveChangesAsync();
    return Results.Ok();
}

);
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

// ---------------------------------------------------------------------
// Dashboard data APIs
// ---------------------------------------------------------------------

app.MapGet("/api/leaderboard", async (
	HttpContext httpContext,
	AppDbContext db,
	IHttpClientFactory httpClientFactory,
	string scope = "all",
	int page = 1,
	int pageSize = 15) =>
{
	page = Math.Max(1, page);
	pageSize = Math.Clamp(pageSize, 1, 100);
	var todayUtc = DateTime.UtcNow.Date;

	var caller = await GetCallerIdentityAsync(httpContext, httpClientFactory);

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
	string? workItemId,
	int page = 1,
	int pageSize = 20) =>
{
	page = Math.Max(1, page);
	pageSize = Math.Clamp(pageSize, 1, 100);

	var caller = await GetCallerIdentityAsync(httpContext, httpClientFactory);

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

app.MapPost("/api/settings", async (ScoringSettingsRequest req, AppDbContext db) =>
{
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

app.MapGet("/dashboard", () => {return Results.File("/home/humn/albarakagame/devops-extension/dashboard.html", "text/html");});
app.MapPost("/api/admin/run-eod", async (AppDbContext db, string? date) =>
{
    // Defaults to yesterday UTC if no date is provided
    DateTime targetDate = string.IsNullOrWhiteSpace(date)
        ? DateTime.UtcNow.Date.AddDays(-1)
        : DateTime.Parse(date).Date;

    await DailyMedalAwardBackgroundService.AwardDailyMedalsForDateAsync(db, targetDate);

    return Results.Ok(new { message = $"Medals calculated for {targetDate:yyyy-MM-dd}" });
});

app.MapGet("/", async (AppDbContext db) =>
        {
            return Results.Redirect("dashboard");
        }
        );
app.Run();

static async Task<string?> GetCallerIdentityAsync(HttpContext context, IHttpClientFactory httpClientFactory)
{
	var authHeader = context.Request.Headers.Authorization.ToString();
	if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
		return null;

	var token = authHeader["Bearer ".Length..].Trim();
	var client = httpClientFactory.CreateClient();
	client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

	try
	{
		using var res = await client.GetAsync("https://vssps.dev.azure.com/albarakatech/_apis/profile/profiles/me?api-version=7.1-preview");
		if (!res.IsSuccessStatusCode) return null;
		var json = await res.Content.ReadFromJsonAsync<JsonNode>();
		return json?["displayName"]?.ToString() ?? json?["emailAddress"]?.ToString();
	}
	catch
	{
		return null;
	}
}
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
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>()
            .HasIndex(u => u.UserName)
            .IsUnique();
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
    public string? IterationPath{get;set;}
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
