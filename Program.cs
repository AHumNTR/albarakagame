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
		var completedWork = payload?["resource"]?["fields"]?["Microsoft.VSTS.Scheduling.CompletedWork"];
		if (completedWork is not null)
		{
            //double olmasina gerek olmayabilir
            double newValue = completedWork["newValue"]?.GetValue<double>() ?? 0;
            double oldValue = completedWork["oldValue"]?.GetValue<double>() ?? 0;
            double pointsEarned = newValue - oldValue;
            var rawUser = payload?["resource"]?["revision"]?["fields"]?["System.ChangedBy"]?.ToString();
            if(rawUser==null) return null;
            var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == rawUser);
            if (user == null)
            {
                user = new User { UserName = rawUser, Points = 0 };
                db.Users.Add(user);
            }
            user.Points += pointsEarned;
            await db.SaveChangesAsync();
		}

		return Results.Ok();});

app.MapGet("/", () => Results.Ok(new { message = "Webhook listener running on port 3000" 

}
));

app.Run();






public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<User> Users => Set<User>();
}

public class User
{
    public int Id { get; set; }
    public required string UserName { get; set; }
    public double Points { get; set; }
}
