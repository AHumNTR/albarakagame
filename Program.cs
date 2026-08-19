using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(3000);
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));

var app = builder.Build();



using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}



app.MapPost("/", async (JsonNode payload,AppDbContext db) =>
{
        Console.WriteLine("Request Arrived");
//get user
        var rawUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
        if(rawUser==null) return null;
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == rawUser);
        if (user == null)
        {
            user = new User { UserName = rawUser, Points = 0 };
            db.Users.Add(user);
        }
        

        var todayUtc=DateTime.Today;
        
        int? workItemId = payload?["resource"]?["workItemId"]?.GetValue<int>();
        //get work times
		var completedWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"];
		var remainingWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.RemainingWork"];

        //might not need to be double
        int completedWorkNewValue=-1,completedOldValue=-1,remainingWorkNewValue=-1,remainingWorkOldValue=-1,pointsEarned=0;

		if (completedWork !=null)
		{
            completedWorkNewValue = (int) (completedWork["newValue"]?.GetValue<double>() ?? -1);
            completedOldValue = (int)(completedWork["oldValue"]?.GetValue<double>() ?? -1);
            pointsEarned += completedWorkNewValue - completedOldValue;
		}
        if(remainingWork!=null){
            remainingWorkNewValue = (int)(remainingWork["newValue"]?.GetValue<double>() ?? -1);
            remainingWorkOldValue= (int)(remainingWork["oldValue"]?.GetValue<double>() ?? -1);
            pointsEarned -= remainingWorkNewValue - remainingWorkOldValue;
        }
        int pointsEarnedToday = await db.Transactions
            .Where(t => t.UserId == user.Id && t.Timestamp >= todayUtc)
            .SumAsync(t => t.PointsEarned);


        //check which is smaller budget remaining for today or the pointsEarned this transaction
        int pointsToGiveAfterLimit= Math.Min(8-pointsEarnedToday,pointsEarned);//upper limit
        //same thing but for penalties use the upper bound variable pointsToGiveAfterLimit instead of pointsEarned
        pointsToGiveAfterLimit= Math.Max(-8-pointsEarnedToday,pointsToGiveAfterLimit);//lower limit
        user.Points += pointsToGiveAfterLimit;


        var transaction = new PointTransaction
        {
            User=user,
            UserId = user.Id,
            UserName=rawUser,
            PointsEarned = pointsToGiveAfterLimit,
            CompletedNewValue=completedWorkNewValue,
            CompletedOldValue=completedOldValue,
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
    public int PointsEarned { get; set; }
    public int CompletedOldValue { get; set; }
    public int CompletedNewValue { get; set; }
    public int RemainingOldValue { get; set; }
    public int RemainingNewValue { get; set; }
    public int? WorkItemId { get; set; }
    public DateTime Timestamp { get; set; }
}
