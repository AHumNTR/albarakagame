using System.Text.Json.Nodes;

public class IterationSyncBackgroundService : BackgroundService
{
	// Public static property accessible from anywhere across files
	public static string? CurrentIterationPath { get;  set; }

	private readonly IHttpClientFactory _httpClientFactory;
	private readonly ILogger<IterationSyncBackgroundService> _logger;

	public IterationSyncBackgroundService(
		IHttpClientFactory httpClientFactory,
		ILogger<IterationSyncBackgroundService> logger)
	{
		_httpClientFactory = httpClientFactory;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await FetchCurrentIterationAsync(stoppingToken);

		while (!stoppingToken.IsCancellationRequested)
		{
			var now = DateTime.Now;
			var targetRun = now.Date.AddHours(12);

			if (now >= targetRun)
			{
				targetRun = targetRun.AddDays(1);
			}

			var delay = targetRun - now;
			_logger.LogInformation("Next iteration sync scheduled at {TargetTime} (in {Hours:N1} hours)", targetRun, delay.TotalHours);

			try
			{
				await Task.Delay(delay, stoppingToken);
				await FetchCurrentIterationAsync(stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	private async Task FetchCurrentIterationAsync(CancellationToken cancellationToken)
	{
		try
		{
			var client = _httpClientFactory.CreateClient("DevopsHttpClient");
			string requestUri = "MyFirstProject/MyFirstProject%20Team/_apis/work/teamsettings/iterations?$timeframe=current&api-version=7.1-preview";

			using var response = await client.GetAsync(requestUri, cancellationToken);
			response.EnsureSuccessStatusCode();

			var data = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken);
			var path = data?["value"]?[0]?["path"]?.ToString();

			if (!string.IsNullOrEmpty(path))
			{
				CurrentIterationPath = path;
				_logger.LogInformation("Updated Current Iteration Path: {Path}", path);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error fetching current iteration from Azure DevOps");
		}
	}
}
