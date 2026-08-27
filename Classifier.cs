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

	public (bool IsMeaningful, float Score) Evaluate(string comment, string? workItemTitle, float passThreshold = 0.55f)
	{
		if (string.IsNullOrWhiteSpace(comment) || comment.Trim().Length < 5)
		{
			return (false, 0.0f);
		}

		// Clean up title for template injection

string cleanTitle = string.IsNullOrWhiteSpace(workItemTitle) ? "bu görev" : $"\"{workItemTitle.Trim()}\"";
	string hypothesis = $"Bu metin {cleanTitle} için yapılan somut teknik geliştirme, analiz veya test detaylarını içerir.";

	// Get raw logits: [0 = Contradiction, 1 = Neutral, 2 = Entailment]
	float[] logits = GetLogits(comment, hypothesis);

	// Softmax over all 3 MNLI classes
	float expContra = MathF.Exp(logits[0]);
	float expNeutral = MathF.Exp(logits[1]);
	float expEntail = MathF.Exp(logits[2]);

	float total = expContra + expNeutral + expEntail;
	float entailmentProb = expEntail / total;

	return (entailmentProb >= passThreshold, entailmentProb);
	}

	private float GetEntailmentScore(string premise, string hypothesis)
	{
		string formattedInput = $"{premise} [SEP] {hypothesis}";

		var tokenIds = _tokenizer.EncodeToIds(formattedInput);
		// const int MaxTokens = 256; // 128 is plenty for determining meaning
		// if (tokenIds.Count > MaxTokens)
		// {
		// 	tokenIds = tokenIds.Take(MaxTokens).ToList();
		// }
		long[] inputIds = tokenIds.Select(id => (long)id).ToArray();
		long[] attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();

		var dimensions = new[] { 1, inputIds.Length };

		var inputs = new List<NamedOnnxValue>
		{
			NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dimensions)),
			NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, dimensions))
		};

		// Only add token_type_ids if the ONNX model metadata explicitly requires it
		if (_session.InputMetadata.ContainsKey("token_type_ids"))
		{
			long[] tokenTypeIds = new long[inputIds.Length];
			inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, dimensions)));
		}

		using var results = _session.Run(inputs);
		var logits = results.First().AsTensor<float>();

		int entailmentIndex = logits.Dimensions[1] > 2 ? 2 : 1;
		return logits[0, entailmentIndex];
	}
		
	public void Dispose()
	{
		_session.Dispose();
	}
}
