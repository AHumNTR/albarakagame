using System.Text.Json.Nodes;
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


var app = builder.Build();



using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}


app.MapPost("/", async (JsonNode payload,AppDbContext db,IHttpClientFactory httpFactory) =>
{
        Console.WriteLine("Request Arrived");
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
        
        var client=httpFactory.CreateClient("IterationClient");
        string targetURI="MyFirstProject/MyFirstProject%20Team/_apis/work/teamsettings/iterations?$timeframe=current&api-version=7.1-preview";
        var currentIterationPath=await client.GetAsync(targetURI);
        var iterationResponse=((await currentIterationPath.Content.ReadFromJsonAsync<JsonNode>())?["value"]?[0]?["path"]?.ToString());
        Console.WriteLine(iterationResponse);
        var todayUtc=DateTime.Today;
        
        int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();
        //get work times
		var completedWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"];
		var remainingWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.RemainingWork"];

        //might not need to be double
        int completedWorkNewValue=-1,completedWorkOldValue=-1,remainingWorkNewValue=-1,remainingWorkOldValue=-1;

		if (completedWork !=null)
		{
            completedWorkNewValue = (int) (completedWork["newValue"]?.GetValue<double>() ?? -1);
            completedWorkOldValue = (int)(completedWork["oldValue"]?.GetValue<double>() ?? -1);
		}
        if(remainingWork!=null){
            remainingWorkNewValue = (int)(remainingWork["newValue"]?.GetValue<double>() ?? -1);
            remainingWorkOldValue= (int)(remainingWork["oldValue"]?.GetValue<double>() ?? -1);
        }


        var todayTransactions = db.Transactions
            .Where(t => t.UserId == user.Id && t.Timestamp >= todayUtc);

        int completedPointsToday = await todayTransactions
            .SumAsync(t => t.PointsEarnedCompleted);

        int remainingPointsToday = await todayTransactions
            .SumAsync(t => t.PointsEarnedRemaining);

        //check which is smaller budget remaining for today or the pointsEarned this transaction
        int pointsToGiveAfterLimitCompleted=0,pointsToGiveAfterLimitRemaining=0;
        if(assignedUser==editorUser)
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
            UserName=editorUser,
            PointsEarnedCompleted = pointsToGiveAfterLimitCompleted,
            PointsEarnedRemaining=pointsToGiveAfterLimitRemaining,
            CompletedNewValue=completedWorkNewValue,
            CompletedOldValue=completedWorkOldValue,
            RemainingNewValue=remainingWorkNewValue,
            RemainingOldValue=remainingWorkOldValue,
            WorkItemId = workItemId,
            Timestamp = DateTime.UtcNow
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
		return Results.Ok();});

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
    public int Id { get; set; }
    public required string UserName { get; set; }
    public int Points { get; set; }
}
public class PointTransaction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public string? UserName {get; set;}
    public int PointsEarnedCompleted { get; set; }
    public int PointsEarnedRemaining { get; set; }
    public int CompletedOldValue { get; set; }
    public int CompletedNewValue { get; set; }
    public int RemainingOldValue { get; set; }
    public int RemainingNewValue { get; set; }
    public int? WorkItemId { get; set; }
    public DateTime Timestamp { get; set; }
}
