using System.Text.Json.Nodes;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(3000);
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));
builder.Services.AddHttpClient("IterationClient",client=>{

    client.BaseAddress = new Uri("https://dev.azure.com/albarakatech/");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    // TODO: move this token out of source control (user-secrets / env var / Key Vault) and rotate it.
    string token =  "EhTXhaCadc2UCtYkNLXa2c1HWHCCkjPbKLMzqhgzm53FAILIOh2SJQQJ99CHACAAAAAWcGBqAAASAZDO2hLF";
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    
}
);
builder.Services.AddHostedService<IterationSyncBackgroundService>();
builder.Services.AddSingleton(sp => 
	new ZeroShotCommentClassifier("onnx/model.onnx", "onnx/vocab.txt"));
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

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
    int correct=0,total=0;
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
	Console.WriteLine(payload?.ToString());

	var fields = payload?["resource"]?["fields"];
	if (fields == null) return Results.Ok();

	var editorUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
	var assignedUser = payload?["resource"]?["revision"]?["fields"]?["System.AssignedTo"]?.ToString();
	var iterationPath = payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"]?.ToString();
	int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();
	
	if (editorUser == null) return Results.Ok();

	// 1. Ensure user exists before processing any transactions
	var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == editorUser);
	if (user == null)
	{
		user = new User { UserName = editorUser, Points = 0 };
		db.Users.Add(user);
		await db.SaveChangesAsync(); // Save immediately to generate user.Id
	}

	var todayUtc = DateTime.Today;

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
			Timestamp = DateTime.UtcNow
		};

		if (iterationPath != IterationSyncBackgroundService.CurrentIterationPath)
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
				Timestamp = DateTime.UtcNow
			};

			int pointsToGiveAfterLimitCompleted = 0;
			if (assignedUser == editorUser)
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
				Timestamp = DateTime.UtcNow
			};

			int pointsToGiveAfterLimitRemaining = 0;
			if (assignedUser == editorUser)
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
				Timestamp = DateTime.UtcNow
			};

			int earnedPointTotal = 0;
			if (iterationPath == IterationSyncBackgroundService.CurrentIterationPath)
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

const int meaningfullCommentPoints=50;
app.MapPost("/commentadded", async (JsonNode payload,AppDbContext db,ZeroShotCommentClassifier classifier) => 
{
    //give the meaningfullCommentPoints amount points to the user if its the assigned user and its the first meaningfull comment

    var editorUser = payload?["resource"]?["fields"]?["System.ChangedBy"]?.ToString();
    var assignedUser = payload?["resource"]?["fields"]?["System.AssignedTo"]?.ToString();
    if(editorUser!=assignedUser)return Results.Ok();
    int? workItemId = payload?["resource"]?["id"]?.GetValue<int>();
    string? message= payload?["resource"]?["fields"]?["System.History"]?.ToString();
    string? title= payload?["resource"]?["fields"]?["System.Title"]?.ToString();
    string cleanText = WebUtility.HtmlDecode(
        Regex.Replace(message ?? string.Empty, "<.*?>", " ")
    );

    var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == editorUser);
    if (user == null)
    {
        user = new User { UserName = editorUser, Points = 0 };
        db.Users.Add(user);
    }

    var meaningfulRegex = new Regex(
        @"^(?!\b(done|ok|okay|fixed|tested|wip|lgtm|\+1|asdf|test)\b$)(?=(?:.*\b[a-zA-Z]{2,}\b){4,}).{15,}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    var transaction = new PointTransaction
    {
        User=user,
        UserId = user.Id,
        Type="Comment Added",
        DeltaPoints = 0,//shouldnt cause a issue in seperating them since they cant call at the same time
        WorkItemId = workItemId,
        Description= $"",
        Timestamp = DateTime.UtcNow
    };
    double evaulation = classifier.Evaluate(cleanText,title)/100;

    Console.WriteLine(evaulation);
    if(evaulation>=0.5){
        //check if this is the first meaningfull comment (checking the points to see if its the first)
        if(!(await db.Transactions.AnyAsync(t=> t.WorkItemId==workItemId&&t.User==user&&t.Type=="Comment Added"&&t.DeltaPoints>0))){
            transaction.Description=$"Meaningfull comment added to task granting the user {(int)(evaulation*meaningfullCommentPoints)} points";
            transaction.DeltaPoints=(int)(evaulation*meaningfullCommentPoints);
            user.Points+=(int)(evaulation*meaningfullCommentPoints);
        }
        else{
            transaction.Description=$"Meaningfull comment added to task but not granting any points since the user already gained points from this task for meaningful comments";
            transaction.DeltaPoints=0;
        }
    }
    else {
        transaction.Description=$"Low effort or automated comment added to the task granting no points";
        transaction.DeltaPoints=0;
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
		pointsAwarded =0,
		passed = score >= 50
	});
});

// ---------------------------------------------------------------------
// Dashboard data APIs
// ---------------------------------------------------------------------

app.MapGet("/api/leaderboard", async (AppDbContext db) =>
{
    var leaderboard = await db.Users
        .OrderByDescending(u => u.Points)
        .ThenBy(u => u.UserName)
        .Select(u => new { u.Id, u.UserName, u.Points })
        .ToListAsync();

    return Results.Ok(leaderboard);
});

app.MapGet("/api/users", async (AppDbContext db) =>
{
    var users = await db.Users
        .OrderBy(u => u.UserName)
        .Select(u => u.UserName)
        .ToListAsync();

    return Results.Ok(users);
});

app.MapGet("/api/transactions", async (AppDbContext db, string? user, int? workItemId, int take = 100) =>
{
    take = Math.Clamp(take, 1, 500);

    var query = db.Transactions
        .Include(t => t.User)
        .OrderByDescending(t => t.Timestamp)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(user))
        query = query.Where(t => t.User != null && t.User.UserName == user);

    if (workItemId.HasValue)
        query = query.Where(t => t.WorkItemId == workItemId);

    var results = await query
        .Take(take)
        .Select(t => new
        {
            t.Id,
            UserName = t.User != null ? t.User.UserName : "(unknown)",
            t.Type,
            t.DeltaPoints,
            t.WorkItemId,
            t.Description,
            t.Timestamp
        })
        .ToListAsync();

    return Results.Ok(results);
});

app.MapGet("/dashboard", () =>
{
	string html = @"
<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Team Points Dashboard</title>
<style>
  :root {
    --bg: #f4f4f5;
    --card-bg: #ffffff;
    --text: #1b1b1f;
    --muted: #52525b;
    --border: #d4d4d8;
    --accent: #0060c2; /* AA contrast on white */
    --accent-dark: #00468f;
    --pass-bg: #eafaf0;
    --pass-border: #0f7a3d;
    --fail-bg: #fdecec;
    --fail-border: #b3261e;
    --focus: #ff8f00;
  }
  * { box-sizing: border-box; }
  body {
    font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
    max-width: 1000px;
    margin: 0 auto;
    padding: 20px;
    background: var(--bg);
    color: var(--text);
    line-height: 1.5;
  }
  .skip-link {
    position: absolute;
    left: -9999px;
    top: 0;
    background: var(--accent-dark);
    color: #fff;
    padding: 10px 16px;
    z-index: 100;
    border-radius: 0 0 6px 0;
  }
  .skip-link:focus {
    left: 0;
  }
  h1 { font-size: 1.6rem; margin-bottom: 4px; }
  .subtitle { color: var(--muted); margin-top: 0; margin-bottom: 20px; }
  .card {
    background: var(--card-bg);
    padding: 20px;
    border-radius: 8px;
    box-shadow: 0 2px 4px rgba(0,0,0,0.08);
    border: 1px solid var(--border);
  }
  [role=""tablist""] {
    display: flex;
    gap: 4px;
    margin-bottom: 16px;
    border-bottom: 2px solid var(--border);
  }
  [role=""tab""] {
    background: none;
    border: none;
    padding: 10px 18px;
    font-size: 1rem;
    font-weight: 600;
    color: var(--muted);
    cursor: pointer;
    border-bottom: 3px solid transparent;
    margin-bottom: -2px;
  }
  [role=""tab""][aria-selected=""true""] {
    color: var(--accent-dark);
    border-bottom-color: var(--accent);
  }
  [role=""tab""]:hover { color: var(--accent-dark); }
  [role=""tabpanel""] { padding-top: 4px; }
  [role=""tabpanel""][hidden] { display: none; }

  a, button, input, select, textarea, [tabindex] {
    outline-offset: 2px;
  }
  a:focus-visible, button:focus-visible, input:focus-visible,
  select:focus-visible, textarea:focus-visible, [role=""tab""]:focus-visible {
    outline: 3px solid var(--focus);
    outline-offset: 2px;
  }

  table { width: 100%; border-collapse: collapse; margin-top: 10px; }
  caption { text-align: left; font-weight: 600; margin-bottom: 8px; }
  th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); font-size: 0.95rem; }
  th { background: #ececef; position: sticky; top: 0; }
  tbody tr:nth-child(odd) { background: #fafafa; }
  .rank-1 { font-weight: 700; }
  .points-positive { color: #0f7a3d; font-weight: 600; }
  .points-negative { color: #b3261e; font-weight: 600; }
  .points-zero { color: var(--muted); }

  .filters { display: flex; flex-wrap: wrap; gap: 14px; margin-bottom: 14px; align-items: end; }
  .form-group { margin-bottom: 4px; }
  .flex-row { display: flex; gap: 15px; flex-wrap: wrap; }
  .flex-row .form-group { flex: 1; min-width: 180px; }
  label { display: block; font-weight: 600; margin-bottom: 5px; font-size: 0.92rem; }
  input, textarea, select {
    width: 100%;
    padding: 9px;
    border: 1px solid #8a8a92;
    border-radius: 4px;
    font-size: 1rem;
    background: #fff;
    color: var(--text);
  }
  small.hint { color: var(--muted); font-size: 12px; display: block; margin-top: 4px; }
  button.action {
    background: var(--accent);
    color: #fff;
    border: none;
    padding: 10px 20px;
    border-radius: 4px;
    cursor: pointer;
    font-size: 1rem;
    font-weight: 700;
  }
  button.action:hover { background: var(--accent-dark); }
  button.action:disabled { background: #9aa0a6; cursor: not-allowed; }

  pre {
    background: #1e1e1e;
    color: #d4d4d8;
    padding: 15px;
    border-radius: 4px;
    overflow-x: auto;
    font-size: 0.85rem;
  }
  .result-box {
    font-size: 1.2rem;
    font-weight: 700;
    margin-top: 18px;
    padding: 14px;
    border-radius: 6px;
    display: flex;
    align-items: center;
    gap: 10px;
  }
  .result-box.pass { background: var(--pass-bg); color: var(--pass-border); border: 1px solid var(--pass-border); }
  .result-box.fail { background: var(--fail-bg); color: var(--fail-border); border: 1px solid var(--fail-border); }
  .visually-hidden {
    position: absolute;
    width: 1px; height: 1px;
    margin: -1px; padding: 0; border: 0;
    clip: rect(0 0 0 0);
    overflow: hidden;
    white-space: nowrap;
  }
  .status-line { color: var(--muted); font-size: 0.9rem; margin-top: 6px; }
  .empty-state { color: var(--muted); font-style: italic; padding: 16px 0; }

  @media (prefers-reduced-motion: reduce) {
    * { animation: none !important; transition: none !important; }
  }
</style>
</head>
<body>
<a class=""skip-link"" href=""#main"">Skip to main content</a>

<header>
  <h1>Team Points Dashboard</h1>
  <p class=""subtitle"">Leaderboard, point transaction history, and the comment scorer tester.</p>
</header>

<main id=""main"" class=""card"">
  <div role=""tablist"" aria-label=""Dashboard sections"">
    <button role=""tab"" id=""tab-leaderboard"" aria-controls=""panel-leaderboard"" aria-selected=""true"" tabindex=""0"">Leaderboard</button>
    <button role=""tab"" id=""tab-transactions"" aria-controls=""panel-transactions"" aria-selected=""false"" tabindex=""-1"">Transactions</button>
    <button role=""tab"" id=""tab-tester"" aria-controls=""panel-tester"" aria-selected=""false"" tabindex=""-1"">Comment Tester</button>
  </div>

  <!-- LEADERBOARD -->
  <section id=""panel-leaderboard"" role=""tabpanel"" aria-labelledby=""tab-leaderboard"" tabindex=""0"">
    <div class=""filters"">
      <button class=""action"" id=""refreshLeaderboardBtn"" type=""button"">Refresh leaderboard</button>
    </div>
    <p id=""leaderboardStatus"" class=""status-line"" role=""status"" aria-live=""polite""></p>
    <div id=""leaderboardTableWrap""></div>
  </section>

  <!-- TRANSACTIONS -->
  <section id=""panel-transactions"" role=""tabpanel"" aria-labelledby=""tab-transactions"" tabindex=""0"" hidden>
    <form id=""transactionFilterForm"">
      <div class=""flex-row"">
        <div class=""form-group"">
          <label for=""txUserFilter"">Filter by user</label>
          <select id=""txUserFilter"">
            <option value="""">All users</option>
          </select>
        </div>
        <div class=""form-group"">
          <label for=""txWorkItemFilter"">Filter by work item ID</label>
          <input type=""number"" id=""txWorkItemFilter"" placeholder=""e.g. 4213"" min=""0"" />
        </div>
        <div class=""form-group"">
          <label for=""txLimit"">Rows to show</label>
          <input type=""number"" id=""txLimit"" value=""100"" min=""1"" max=""500"" />
        </div>
      </div>
      <button class=""action"" type=""submit"">Apply filters</button>
    </form>
    <p id=""transactionsStatus"" class=""status-line"" role=""status"" aria-live=""polite""></p>
    <div id=""transactionsTableWrap""></div>
  </section>

  <!-- COMMENT TESTER -->
  <section id=""panel-tester"" role=""tabpanel"" aria-labelledby=""tab-tester"" tabindex=""0"" hidden>
    <form id=""testerForm"">
      <div class=""form-group"">
        <label for=""title"">Work item title</label>
        <input type=""text"" id=""title"" name=""title"" value=""Veritabani yeni semaya gecis"" />
      </div>

      <div class=""form-group"">
        <label for=""comment"">Developer comment</label>
        <textarea id=""comment"" name=""comment"" rows=""4"">Eski transaction tablosundaki indeksler yeni kolon yapisina gore revize edildi.</textarea>
      </div>

      <button class=""action"" type=""submit"" id=""evaluateBtn"">Evaluate comment</button>
    </form>

    <div id=""resultBox"" class=""result-box"" hidden role=""status"" aria-live=""polite""></div>
    <h2 class=""visually-hidden"">Raw evaluation response</h2>
    <pre id=""jsonResult"" hidden></pre>
  </section>
</main>

<script>
(function () {
  // ---------- Tab logic (WAI-ARIA Tabs pattern, arrow-key + Home/End support) ----------
  var tabs = Array.prototype.slice.call(document.querySelectorAll('[role=""tab""]'));
  var panels = tabs.map(function (t) { return document.getElementById(t.getAttribute('aria-controls')); });

  function selectTab(index) {
    tabs.forEach(function (t, i) {
      var selected = i === index;
      t.setAttribute('aria-selected', selected ? 'true' : 'false');
      t.tabIndex = selected ? 0 : -1;
      panels[i].hidden = !selected;
    });
    tabs[index].focus();
  }

  tabs.forEach(function (tab, i) {
    tab.addEventListener('click', function () { selectTab(i); });
    tab.addEventListener('keydown', function (e) {
      var newIndex = null;
      if (e.key === 'ArrowRight') newIndex = (i + 1) % tabs.length;
      else if (e.key === 'ArrowLeft') newIndex = (i - 1 + tabs.length) % tabs.length;
      else if (e.key === 'Home') newIndex = 0;
      else if (e.key === 'End') newIndex = tabs.length - 1;
      if (newIndex !== null) {
        e.preventDefault();
        selectTab(newIndex);
      }
    });
  });

  // ---------- Helpers ----------
  function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/""/g, '&quot;');
  }

  function pointsClass(n) {
    if (n > 0) return 'points-positive';
    if (n < 0) return 'points-negative';
    return 'points-zero';
  }

  function formatTimestamp(ts) {
    try {
      return new Date(ts).toLocaleString();
    } catch (e) {
      return ts;
    }
  }

  // ---------- Leaderboard ----------
  var leaderboardWrap = document.getElementById('leaderboardTableWrap');
  var leaderboardStatus = document.getElementById('leaderboardStatus');

  async function loadLeaderboard() {
    leaderboardStatus.textContent = 'Loading leaderboard...';
    try {
      var res = await fetch('/api/leaderboard');
      if (!res.ok) throw new Error('Request failed with status ' + res.status);
      var data = await res.json();

      if (!data.length) {
        leaderboardWrap.innerHTML = '<p class=""empty-state"">No users yet.</p>';
        leaderboardStatus.textContent = 'No users found.';
        return;
      }

      var rows = data.map(function (u, i) {
        return '<tr' + (i === 0 ? ' class=""rank-1""' : '') + '>' +
          '<td>' + (i + 1) + '</td>' +
          '<td>' + escapeHtml(u.userName) + '</td>' +
          '<td class=""' + pointsClass(u.points) + '"">' + u.points + '</td>' +
          '</tr>';
      }).join('');

      leaderboardWrap.innerHTML =
        '<table>' +
        '<caption>Users ranked by total points</caption>' +
        '<thead><tr><th scope=""col"">Rank</th><th scope=""col"">User</th><th scope=""col"">Points</th></tr></thead>' +
        '<tbody>' + rows + '</tbody>' +
        '</table>';

      leaderboardStatus.textContent = 'Leaderboard updated. ' + data.length + ' users shown.';
    } catch (e) {
      leaderboardWrap.innerHTML = '';
      leaderboardStatus.textContent = 'Could not load leaderboard: ' + e.message;
    }
  }

  document.getElementById('refreshLeaderboardBtn').addEventListener('click', loadLeaderboard);

  // ---------- Transactions ----------
  var txWrap = document.getElementById('transactionsTableWrap');
  var txStatus = document.getElementById('transactionsStatus');
  var txUserFilter = document.getElementById('txUserFilter');

  async function loadUserFilterOptions() {
    try {
      var res = await fetch('/api/users');
      if (!res.ok) return;
      var users = await res.json();
      users.forEach(function (u) {
        var opt = document.createElement('option');
        opt.value = u;
        opt.textContent = u;
        txUserFilter.appendChild(opt);
      });
    } catch (e) {
      // Non-fatal: user filter dropdown just stays with ""All users"".
    }
  }

  async function loadTransactions() {
    var user = txUserFilter.value;
    var workItemId = document.getElementById('txWorkItemFilter').value;
    var limit = document.getElementById('txLimit').value || 100;

    var params = new URLSearchParams();
    if (user) params.set('user', user);
    if (workItemId) params.set('workItemId', workItemId);
    params.set('take', limit);

    txStatus.textContent = 'Loading transactions...';
    try {
      var res = await fetch('/api/transactions?' + params.toString());
      if (!res.ok) throw new Error('Request failed with status ' + res.status);
      var data = await res.json();

      if (!data.length) {
        txWrap.innerHTML = '<p class=""empty-state"">No transactions match these filters.</p>';
        txStatus.textContent = 'No transactions found.';
        return;
      }

      var rows = data.map(function (t) {
        return '<tr>' +
          '<td>' + formatTimestamp(t.timestamp) + '</td>' +
          '<td>' + escapeHtml(t.userName) + '</td>' +
          '<td>' + escapeHtml(t.type) + '</td>' +
          '<td class=""' + pointsClass(t.deltaPoints) + '"">' + t.deltaPoints + '</td>' +
          '<td>' + (t.workItemId != null ? t.workItemId : '—') + '</td>' +
          '<td>' + escapeHtml(t.description) + '</td>' +
          '</tr>';
      }).join('');

      txWrap.innerHTML =
        '<table>' +
        '<caption>Point transaction history</caption>' +
        '<thead><tr>' +
        '<th scope=""col"">Time</th><th scope=""col"">User</th><th scope=""col"">Type</th>' +
        '<th scope=""col"">Points</th><th scope=""col"">Work item</th><th scope=""col"">Description</th>' +
        '</tr></thead>' +
        '<tbody>' + rows + '</tbody>' +
        '</table>';

      txStatus.textContent = 'Showing ' + data.length + ' transactions.';
    } catch (e) {
      txWrap.innerHTML = '';
      txStatus.textContent = 'Could not load transactions: ' + e.message;
    }
  }

  document.getElementById('transactionFilterForm').addEventListener('submit', function (e) {
    e.preventDefault();
    loadTransactions();
  });

  // ---------- Comment tester ----------
  var testerForm = document.getElementById('testerForm');
  var evaluateBtn = document.getElementById('evaluateBtn');
  var resultBox = document.getElementById('resultBox');
  var jsonResult = document.getElementById('jsonResult');

  testerForm.addEventListener('submit', async function (e) {
    e.preventDefault();
    evaluateBtn.disabled = true;
    evaluateBtn.textContent = 'Evaluating...';

    var payload = {
      title: document.getElementById('title').value,
      comment: document.getElementById('comment').value
    };

    try {
      var res = await fetch('/api/test-score', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      var data = await res.json();

      var passed = data.score >= 50;
      resultBox.hidden = false;
      resultBox.className = 'result-box ' + (passed ? 'pass' : 'fail');
      resultBox.textContent = (passed ? '✅ Pass' : '❌ Fail') + ' — score ' + data.score + ' / 100';

      jsonResult.hidden = false;
      jsonResult.textContent = JSON.stringify(data, null, 2);
    } catch (err) {
      resultBox.hidden = false;
      resultBox.className = 'result-box fail';
      resultBox.textContent = 'Evaluation failed: ' + err.message;
      jsonResult.hidden = true;
    } finally {
      evaluateBtn.disabled = false;
      evaluateBtn.textContent = 'Evaluate comment';
    }
  });

  // ---------- Init ----------
  loadLeaderboard();
  loadUserFilterOptions();
  loadTransactions();
})();
</script>
</body>
</html>";

	return Results.Content(html, "text/html");
});
app.Run();






public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<User> Users => Set<User>();
    public DbSet<PointTransaction> Transactions => Set<PointTransaction>();
}

public class User
{
    public int Id{get; set;}
    public required string UserName{get; set;}
    public int Points {get; set;}
}
public class PointTransaction
{
    public int Id {get; set;}
    public int UserId {get; set;}
    public User? User {get; set;}
    public string? Type {get; set;}
    public int DeltaPoints {get; set;}
    public int? WorkItemId {get; set;}
    public string? Description {get; set;}//maybe should add iteration?
    public DateTime Timestamp {get; set;}
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
