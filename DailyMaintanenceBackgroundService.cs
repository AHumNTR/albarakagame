using Microsoft.Extensions.Caching.Memory;

public class DailyMaintenanceBackgroundService : BackgroundService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IMemoryCache _cache;
	private readonly ILogger<DailyMaintenanceBackgroundService> _logger;
	private readonly ModelTrainingService _trainingService;

	public DailyMaintenanceBackgroundService(
		IServiceScopeFactory scopeFactory,
		IHttpClientFactory httpClientFactory,
		IMemoryCache cache,
		ILogger<DailyMaintenanceBackgroundService> logger,
		ModelTrainingService trainingService)
	{
		_scopeFactory = scopeFactory;
		_httpClientFactory = httpClientFactory;
		_cache = cache;
		_logger = logger;
		_trainingService = trainingService;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			var now = DateTime.Now;
			var targetRun = now.Date.AddHours(2);

			if (now >= targetRun)
			{
				targetRun = targetRun.AddDays(1);
			}

			var delay = targetRun - now;
			_logger.LogInformation("Daily maintenance scheduled at {TargetTime}", targetRun);

			try
			{
				await Task.Delay(delay, stoppingToken);

				_logger.LogInformation("Starting daily maintenance tasks...");
				await RunTeamSyncAsync(stoppingToken);
				await _trainingService.TrainAndDeployAsync(stoppingToken);
				_logger.LogInformation("Daily maintenance completed.");
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Unexpected error in maintenance service.");
			}
		}
	}

	private async Task RunTeamSyncAsync(CancellationToken cancellationToken)
	{
		try
		{
			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
			await TeamSyncService.SyncTeamMembersAsync(db, _httpClientFactory, _cache);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error occurred while syncing team members.");
		}
	}
}
