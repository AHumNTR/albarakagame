using Microsoft.ML.OnnxRuntime;

using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

public class ZeroShotCommentClassifier : IDisposable
{
	private readonly InferenceSession _session;
	private readonly Tokenizer _tokenizer;

	public ZeroShotCommentClassifier(string modelPath, string vocabPath)
	{
		var sessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions.MakeSessionOptionWithCudaProvider();
		sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
		sessionOptions.AddSessionConfigEntry("session.use_env_allocators", "1");
		_session = new InferenceSession(modelPath,sessionOptions);

		//not vocab path/stream anymore 
		using var vocabStream = File.OpenRead(vocabPath);
		if(vocabPath.Split('.')[1]=="model")
			_tokenizer =SentencePieceTokenizer.Create(vocabStream);
		else _tokenizer=BertTokenizer.Create(vocabStream);
	}

	public  double  Evaluate(string comment, string? workItemTitle,double baseline,double temperature)
	{
		if (string.IsNullOrWhiteSpace(comment) || comment.Trim().Length < 5)
		{
			return 0.0f;
		}

		// Format matching the exact dataset.jsonl pattern used during training
		string cleanTitle = string.IsNullOrWhiteSpace(workItemTitle) ? "Genel" : workItemTitle.Trim();
		string formattedInput = $"{cleanTitle} [SEP] {comment}";

		// Tokenize and cap at 128 tokens
		var tokenIds = _tokenizer.EncodeToIds(formattedInput);
		const int MaxTokens = 256;
		if (tokenIds.Count > MaxTokens)
		{
			tokenIds = tokenIds.Take(MaxTokens).ToList();
		}

		long[] inputIds = tokenIds.Select(id => (long)id).ToArray();
		long[] attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();

		var dimensions = new[] { 1, inputIds.Length };

		var inputs = new List<NamedOnnxValue>
		{
			NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dimensions)),
			NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, dimensions))
		};

		if (_session.InputMetadata.ContainsKey("token_type_ids"))
		{
			long[] tokenTypeIds = new long[inputIds.Length];
			inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, dimensions)));
		}

		using var results = _session.Run(inputs);
		var logits = results.First().AsTensor<float>();

		// Logits: [0 = Fail, 1 = Pass]

		double failLogit = logits[0, 0];
		double passLogit= logits[0, 1];
		// 1. Calculate the logit margin (confidence difference)
		double margin = passLogit - failLogit;

		// 2. Calibration hyperparameters:
		// baseline: Higher values penalize generic/short comments more aggressively
		// temperature: Controls steepness of the curve

		// 3. Sigmoid over the shifted margin
		double normalized = 1.0f / (1.0f + Math.Exp(-(margin - baseline) / temperature));

		// 4. Scale to 0 - 100 integer range
		double finalScore = (int)Math.Round(normalized * 100.0f);

		return  finalScore;
	}

		
	public void Dispose()
	{
		_session.Dispose();
	}
}
