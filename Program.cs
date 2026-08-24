using System.Text.Json.Nodes;
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

var app = builder.Build();



using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}


app.MapPost("/", async (JsonNode payload,AppDbContext db,IHttpClientFactory httpFactory) =>
{
//maybe shoul split completed and remaining work value change request 
        //Console.WriteLine(payload.ToString());
//get user
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
        //get work times
		var completedWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"];
		var remainingWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.RemainingWork"];
        var iterationPath = payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"];

        int completedWorkNewValue=-1,completedWorkOldValue=-1,remainingWorkNewValue=-1,remainingWorkOldValue=-1;

		if (completedWork !=null)
		{
            completedWorkNewValue = (int) (completedWork["newValue"]?.GetValue<double>() ?? 0);
            completedWorkOldValue = (int)(completedWork["oldValue"]?.GetValue<double>() ?? 0);
		}
        if(remainingWork!=null){
            remainingWorkNewValue = (int)(remainingWork["newValue"]?.GetValue<double>() ?? 0);
            remainingWorkOldValue= (int)(remainingWork["oldValue"]?.GetValue<double>() ?? 0);
        }


        var todayTransactions = db.Transactions
            .Where(t => t.UserId == user.Id && t.Timestamp >= todayUtc);

        int completedPointsToday = await todayTransactions.Where(t=>t.Type=="Completed Work")
            .SumAsync(t => t.DeltaPoints);

        int remainingPointsToday = await todayTransactions
            .SumAsync(t => t.PointsEarnedRemaining);

        //check which is smaller budget remaining for today or the pointsEarned this transaction
        int pointsToGiveAfterLimitCompleted=0,pointsToGiveAfterLimitRemaining=0;
        if(assignedUser==editorUser&&IterationSyncBackgroundService.CurrentIterationPath==iterationPath?.ToString())
        {
            //only give points if the assigned user is editing it
            pointsToGiveAfterLimitCompleted=Math.Clamp(completedWorkNewValue-completedWorkOldValue,-8-completedPointsToday,8-completedPointsToday);
            pointsToGiveAfterLimitRemaining=Math.Clamp(remainingWorkNewValue-remainingWorkOldValue,-8-remainingPointsToday,8-remainingPointsToday);
        }
        user.Points += pointsToGiveAfterLimitCompleted+pointsToGiveAfterLimitRemaining;


        var transaction = new PointTransaction
        {
            User=user,
            UserId = user.Id,
            DeltaPoints = pointsToGiveAfterLimitCompleted+pointsToGiveAfterLimitRemaining,//shouldnt cause a issue in seperating them since they cant call at the same time
            WorkItemId = workItemId,
            Description= $"Completed(or remaining) work has been updated from {completedWorkOldValue} to {completedWorkNewValue} and after applying the limit {pointsToGiveAfterLimitCompleted} is rewarded/substracted",
            Timestamp = DateTime.UtcNow
        };
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
    if(payload?["resource"]?["revision"]?["fields"]?["System.IterationPath"]?.ToString()!=IterationSyncBackgroundService.CurrentIterationPath){

        var pointsEarnedInTask = await db.Transactions
            .Where(t => t.UserId == user.Id && t.WorkItemId==workItemId).SumAsync(t=> t.DeltaPoints);
            user.Points-=pointsEarnedInTask;
        Console.WriteLine(user.Points);
    }
    await db.SaveChangesAsync();
    return Results.Ok();
}

);
app.MapPost("/statechanged", async (JsonNode payload,AppDbContext db) => 
{

    var editorUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
    int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();
    var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == editorUser);
    if (user == null)
    {
        user = new User { UserName = editorUser, Points = 0 };
        db.Users.Add(user);
    }

    var todayUtc=DateTime.Today;
    if(payload?["resource"]?["fields"]?["System.State"]?["newValue"]?.ToString()=="Closed"&& await db.Transactions.AnyAsync(t=> t.User==user&& t.Timestamp>=todayUtc)){
        user.Points += (int?)payload?["resource"]?["revision"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"]?.GetValue<double>() ?? 0;
    }
    await db.SaveChangesAsync();
    return Results.Ok();
}

);
const int meaningfullCommentPoints=50;
app.MapPost("/commentadded", async (JsonNode payload,AppDbContext db) => 
{
    Console.WriteLine(payload?["resource"]?["fields"]?["System.CommentCount"]?.GetValue<int>());
    if(payload?["resource"]?["fields"]?["System.CommentCount"]?.GetValue<int>()!=1)return Results.Ok();

    var editorUser = payload?["resource"]?["fields"]?["System.ChangedBy"]?.ToString();
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

    if(meaningfulRegex.IsMatch(cleanText)){
        user.Points+=meaningfullCommentPoints;
    }

    await db.SaveChangesAsync();
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
