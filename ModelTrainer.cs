using System.Diagnostics;

public class ModelTrainingService
{
	private readonly ILogger<ModelTrainingService> _logger;
	private readonly ZeroShotCommentClassifier _classifier;
	private static readonly SemaphoreSlim _lock = new(1, 1);

	private const string TrainingDirectory = "/home/humn/albarakagame/training";
	private const string VenvPythonPath = "/home/humn/albarakagame/training/venv/bin/python";
	private const string sourceModel = "/home/humn/albarakagame/training/onnx-output/model.onnx";
	private static readonly string targetModel = Path.Combine(AppContext.BaseDirectory, "onnx/model.onnx");

	public bool IsTraining => _lock.CurrentCount == 0;

	public ModelTrainingService(ILogger<ModelTrainingService> logger,ZeroShotCommentClassifier classifier)
	{
		_logger = logger;
		_classifier=classifier;
	}

	public async Task<(bool Success, string Message)> TrainAndDeployAsync(CancellationToken cancellationToken = default)
	{
		if (!await _lock.WaitAsync(0, cancellationToken))
		{
			return (false, "Eğitim işlemi şu anda arka planda devam ediyor.");
		}

		try
		{
			string pythonExe = VenvPythonPath ;

			_logger.LogInformation("Model eğitimi başlatılıyor (trainer.py)...");
			var trainOk = await ExecuteScriptAsync(pythonExe, "trainer.py", TrainingDirectory, cancellationToken);
			if (!trainOk)
			{
				return (false, "trainer.py çalıştırılırken hata oluştu.");
			}

			_logger.LogInformation("ONNX export başlatılıyor (exportoonnx.py)...");
			var exportOk = await ExecuteScriptAsync(pythonExe, "exportoonnx.py", TrainingDirectory, cancellationToken);
			if (!exportOk)
			{
				return (false, "exportoonnx.py çalıştırılırken hata oluştu.");
			}


			if (!File.Exists(sourceModel))
			{
				return (false, $"Export edilen model bulunamadı: {sourceModel}");
			}

			File.Copy(sourceModel, targetModel, overwrite: true);
			_classifier.Reload(targetModel);
			_logger.LogInformation("Model başarıyla eğitildi ve dağıtıldı.");
			return (true, "Model eğitimi ve dağıtımı başarıyla tamamlandı.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Model eğitimi sırasında beklenmedik hata oluştu.");
			return (false, $"Hata: {ex.Message}");
		}
		finally
		{
			_lock.Release();
		}
	}

	private async Task<bool> ExecuteScriptAsync(string executable, string scriptName, string workingDir, CancellationToken cancellationToken)
	{
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = executable,
				Arguments = scriptName,
				WorkingDirectory = workingDir,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			using var process = new Process { StartInfo = psi };

			process.OutputDataReceived += (s, e) =>
			{
				if (!string.IsNullOrWhiteSpace(e.Data))
					_logger.LogInformation("[{Script}] {Output}", scriptName, e.Data);
			};

			process.ErrorDataReceived += (s, e) =>
			{
				if (!string.IsNullOrWhiteSpace(e.Data))
					_logger.LogWarning("[{Script}] {Error}", scriptName, e.Data);
			};

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			await process.WaitForExitAsync(cancellationToken);
			return process.ExitCode == 0;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "{Script} çalıştırılamadı.", scriptName);
			return false;
		}
	}
}
