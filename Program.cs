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
    string token =  "EhTXhaCadc2UCtYkNLXa2c1HWHCCkjPbKLMzqhgzm53FAILIOh2SJQQJ99CHACAAAAAWcGBqAAASAZDO2hLF";
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    
}
);
builder.Services.AddHostedService<IterationSyncBackgroundService>();
builder.Services.AddSingleton(sp => 
	new ZeroShotCommentClassifier("onnx/model.onnx", "onnx/tokenizer.json"));
var app = builder.Build();



using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/evaluate-test-file", async (ZeroShotCommentClassifier classifier) =>
{
	if (!File.Exists("/home/humn/albarakamltest/test.json"))
	{
		return Results.NotFound("test.json not found in project directory.");
	}

	string jsonText = await File.ReadAllTextAsync("/home/humn/albarakamltest/test.json");
	var items = JsonSerializer.Deserialize<List<TestCaseItem>>(jsonText, new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	});

	if (items == null) return Results.BadRequest("Invalid JSON file.");

	Console.WriteLine("\n--- Batch Evaluation Results ---");
    int correct=0,total=0;
	foreach (var item in items)
	{
        if(item.RejectReason==null || item.RejectReason=="LOW_QUALITY_COMMENT")
        {
            var (isMeaningful, score) = classifier.Evaluate(item.Detail, item.WorkItemTitle, passThreshold: 0.65f);
            total++;
            bool expected = item.RejectReason == null;
            if(expected==isMeaningful)correct++;
        }
	}
    Console.WriteLine($"correct={correct}  total={total}");
	return Results.Ok();
});


app.MapPost("/completedworkchanged", async (JsonNode payload,AppDbContext db) =>
{
        Console.WriteLine(payload.ToString());
        var editorUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
        var assignedUser = payload?["resource"]?["revision"]?["fields"]?["System.AssignedTo"]?.ToString();
        if(editorUser==null||assignedUser==null) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == editorUser);
        if (user == null)
        {
            user = new User { UserName = editorUser, Points = 0 };
            db.Users.Add(user);
        }
        

        var todayUtc=DateTime.Today;
        
        int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();

        var transaction = new PointTransaction
        {
            User=user,
            UserId = user.Id,
            Type="Completed Work Updated",
            DeltaPoints = 0,//shouldnt cause a issue in seperating them since they cant call at the same time
            WorkItemId = workItemId,
            Description= $"",
            Timestamp = DateTime.UtcNow
        };
        
        //get work times
		var completedWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"];
        var iterationPath = payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"];

        int completedWorkNewValue=-1,completedWorkOldValue=-1;

        if(completedWork!=null){
            completedWorkNewValue = (int)(completedWork["newValue"]?.GetValue<double>() ?? 0);
            completedWorkOldValue= (int)(completedWork["oldValue"]?.GetValue<double>() ?? 0);
        }


        int completedPointsToday = await db.Transactions.Where(t => t.UserId == user.Id && t.Timestamp >= todayUtc).Where(t=>t.Type=="Completed Work Updated")
            .SumAsync(t => t.DeltaPoints);

        //check which is smaller budget remaining for today or the pointsEarned this transaction
        int pointsToGiveAfterLimitCompleted=0;
        if(assignedUser==editorUser)
            if(IterationSyncBackgroundService.CurrentIterationPath==iterationPath?.ToString())
            { 
                pointsToGiveAfterLimitCompleted=Math.Clamp(completedWorkNewValue-completedWorkOldValue,-8-completedPointsToday,8-completedPointsToday);
                transaction.Description= $"Completed work has been updated from {completedWorkOldValue} to {completedWorkNewValue} and after applying the limit {pointsToGiveAfterLimitCompleted} is rewarded/substracted";
            }
            else transaction.Description="Completed work updated but no points are granted since iteration is not current";
            
        else transaction.Description="Completed work updated but no points are granted since assigned user isnt the one editing";

        user.Points += pointsToGiveAfterLimitCompleted;
        transaction.DeltaPoints=pointsToGiveAfterLimitCompleted;

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

		return Results.Ok();});
app.MapPost("/remainingworkchanged", async (JsonNode payload,AppDbContext db) =>
{
        var editorUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
        var assignedUser = payload?["resource"]?["revision"]?["fields"]?["System.AssignedTo"]?.ToString();
        if(editorUser==null||assignedUser==null) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == editorUser);
        if (user == null)
        {
            user = new User { UserName = editorUser, Points = 0 };
            db.Users.Add(user);
        }
        
        var todayUtc=DateTime.Today;
        
        int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();
        var transaction = new PointTransaction
        {
            User=user,
            UserId = user.Id,
            Type="Completed Work Updated",
            DeltaPoints = 0,//shouldnt cause a issue in seperating them since they cant call at the same time
            WorkItemId = workItemId,
            Description= $"",
            Timestamp = DateTime.UtcNow
        };
        //get work times
		var remainingWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.RemainingWork"];
        var iterationPath = payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"];

        int remainingWorkNewValue=-1,remainingWorkOldValue=-1;

        if(remainingWork!=null){
            remainingWorkNewValue = (int)(remainingWork["newValue"]?.GetValue<double>() ?? 0);
            remainingWorkOldValue= (int)(remainingWork["oldValue"]?.GetValue<double>() ?? 0);
        }


        int remainingPointsToday = await db.Transactions.Where(t => t.UserId == user.Id && t.Timestamp >= todayUtc).Where(t=>t.Type=="Remaining Work Updated")
            .SumAsync(t => t.DeltaPoints);

        //check which is smaller budget remaining for today or the pointsEarned this transaction
        int pointsToGiveAfterLimitRemaining=0;
        if(assignedUser==editorUser)
            if(IterationSyncBackgroundService.CurrentIterationPath==iterationPath?.ToString())
            { 
                pointsToGiveAfterLimitRemaining=Math.Clamp(-(remainingWorkNewValue-remainingWorkOldValue),-8-remainingPointsToday,8-remainingPointsToday);
                transaction.Description= $"Remaining work has been updated from {remainingWorkOldValue} to {remainingWorkNewValue} and after applying the limit {pointsToGiveAfterLimitRemaining} is rewarded/substracted";
            }
            else transaction.Description="Remaining work updated but no points are granted since iteration is not current";
            
        else transaction.Description="Remaining work updated but no points are granted since assigned user isnt the one editing";

        user.Points += pointsToGiveAfterLimitRemaining;
        transaction.DeltaPoints=pointsToGiveAfterLimitRemaining;

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

		return Results.Ok();});
app.MapPost("/iterationupdate", async (JsonNode payload,AppDbContext db) => 
{
    var editorUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
    int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();
    var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == editorUser);
    if (user == null)
    {
        user = new User { UserName = editorUser, Points = 0 };
        db.Users.Add(user);
    }
    var transaction = new PointTransaction
    {
        User=user,
        UserId = user.Id,
        Type="Iteration Changed",
        DeltaPoints = 0,//shouldnt cause a issue in seperating them since they cant call at the same time
        WorkItemId = workItemId,
        Description= $"Iteration changed to current iteration not changing anything",
        Timestamp = DateTime.UtcNow
    };
    if(payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"]?.ToString()!=IterationSyncBackgroundService.CurrentIterationPath){

        var pointsEarnedInTask = await db.Transactions
            .Where(t => t.UserId == user.Id && t.WorkItemId==workItemId).SumAsync(t=> t.DeltaPoints);
        user.Points-=pointsEarnedInTask;
        transaction.DeltaPoints=-pointsEarnedInTask;
        transaction.Description=$"Iteration changed to non current iteration revoking the {pointsEarnedInTask} granted in this task";
    }
    db.Transactions.Add(transaction);
    await db.SaveChangesAsync();
    return Results.Ok();
}

);
app.MapPost("/statechanged", async (JsonNode payload,AppDbContext db) => 
{
    var editorUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
    var assignedUser = payload?["resource"]?["revision"]?["fields"]?["System.AssignedTo"]?.ToString();
    if(editorUser!=assignedUser)return Results.Ok();
    int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();
    var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == editorUser);
    Console.WriteLine(user.Points);
    if (user == null)
    {
        user = new User { UserName = editorUser, Points = 0 };
        db.Users.Add(user);
    }

    var iterationPath = payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"];
    var transaction = new PointTransaction
    {
        User=user,
        UserId = user.Id,
        Type="State Changed",
        DeltaPoints = 0,//shouldnt cause a issue in seperating them since they cant call at the same time
        WorkItemId = workItemId,
        Description= $"State changed to something other than done not granting any points",
        Timestamp = DateTime.UtcNow
    };
    var todayUtc=DateTime.Today;
    int earnedPointTotal=0;
    //what if this happens before the completed work update transaction ??????
    if(iterationPath.ToString()==IterationSyncBackgroundService.CurrentIterationPath)
    {
        if(payload?["resource"]?["fields"]?["System.State"]?["newValue"]?.ToString()=="Closed"){
        if(await db.Transactions.AnyAsync(t=> t.User==user&& t.Timestamp>=todayUtc&&t.Type=="Completed Work Updated")){
            earnedPointTotal=(int?)payload?["resource"]?["revision"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"]?.GetValue<double>() ?? 0;
            transaction.Description=$"State changed to done granting {earnedPointTotal} points";
        }
        else transaction.Description= "State changed to done but points not granted due to not completing any work today" ;
    }
    else transaction.Description=$"State changed to done but no points were granted since the iteration was {iterationPath.ToString()} and not the current which is {IterationSyncBackgroundService.CurrentIterationPath}";


        transaction.DeltaPoints=earnedPointTotal;
        user.Points += earnedPointTotal;
    }

    db.Transactions.Add(transaction);
    await db.SaveChangesAsync();
    return Results.Ok();
}

);
const int meaningfullCommentPoints=50;
app.MapPost("/commentadded", async (JsonNode payload,AppDbContext db,ZeroShotCommentClassifier classifier) => 
{
    //give the meaningfullCommentPoints amount points to the user if its the assigned user and its the first meaningfull comment

    var editorUser = payload?["resource"]?["fields"]?["System.ChangedBy"]?.ToString();
    var assignedUser = payload?["resource"]?["fields"]?["System.AssignedTo"]?.ToString();
    if(editorUser!=assignedUser)return Results.Ok();
    int? workItemId = payload?["resource"]?["id"]?.GetValue<int>();
    string? message= payload?["resource"]?["fields"]?["System.History"]?.ToString();
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
        Type="First Meaningfull Comment Added",
        DeltaPoints = 0,//shouldnt cause a issue in seperating them since they cant call at the same time
        WorkItemId = workItemId,
        Description= $"",
        Timestamp = DateTime.UtcNow
    };
    bool isSubstantive = classifier.Evaluate(cleanText,"placeholer" ,passThreshold: 0.65f).IsMeaningful;
    Console.WriteLine(isSubstantive);
    // if(meaningfulRegex.IsMatch(cleanText)){
    //     if(!(await db.Transactions.AnyAsync(t=> t.WorkItemId==workItemId&&t.User==user&&t.Type=="First Meaningfull Comment Added"))){
    //         transaction.Description=$"Meaningfull comment added to task granting the user {meaningfullCommentPoints} points";
    //         transaction.DeltaPoints=meaningfullCommentPoints;
    //         user.Points+=meaningfullCommentPoints;
    //         db.Transactions.Add(transaction);
    //         await db.SaveChangesAsync();
    //     }
    // }
    //else Console.WriteLine("bad comment");
    //maybe should still add a transaction even if the comment isnt meaningfull or first
    return Results.Ok();
}

);
app.MapGet("/", () => Results.Ok(new { message = "Webhook listener running on port 3000" 

}
));

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
	public string? WorkItemTitle { get; set; }
	public required string Detail { get; set; }
	public string? RejectReason { get; set; }
}
