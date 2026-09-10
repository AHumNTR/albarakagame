using Microsoft.ML.OnnxRuntime;

using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

public class ZeroShotCommentClassifier : IDisposable
{
	private InferenceSession _session;
	private readonly Tokenizer _tokenizer;

	public ZeroShotCommentClassifier(string modelPath, string vocabPath)
	{
		var sessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions.MakeSessionOptionWithCudaProvider();
		sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
		sessionOptions.AddSessionConfigEntry("session.use_env_allocators", "1");
		_session = new InferenceSession(modelPath,sessionOptions);

		//not vocab path/stream anymore 
		_tokenizer=WordPieceTokenizer.Create(vocabPath);
	}

	public  double  Evaluate(string comment, string? workItemTitle)
	{
		if (string.IsNullOrWhiteSpace(comment) || comment.Trim().Length < 5)
		{
			return 0.0;
		}

		string cleanTitle = string.IsNullOrWhiteSpace(workItemTitle) ? "Genel" : workItemTitle.Trim();
		string cleanComment = comment.Trim();

		// ModernBERT-TR WordPiece uses [CLS] and [SEP]
		int clsId = _tokenizer.EncodeToIds("[CLS]")[0];
		int sepId = _tokenizer.EncodeToIds("[SEP]")[0];

		var titleIds = _tokenizer.EncodeToIds(cleanTitle);
		var commentIds = _tokenizer.EncodeToIds(cleanComment);

		// Assemble pair sequence: [CLS] title [SEP] comment [SEP]
		var tokenIds = new List<int> { clsId };
		tokenIds.AddRange(titleIds);
		tokenIds.Add(sepId);
		tokenIds.AddRange(commentIds);
		tokenIds.Add(sepId);

		// Truncate to MaxTokens
		if (tokenIds.Count > 256)
		{
			tokenIds = tokenIds.Take(256 - 1).ToList();
			tokenIds.Add(sepId); // Keep trailing [SEP]
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
		var outputTensor = results.First().AsTensor<float>();

		// Model regression target is 0.0 to 1.0
		float rawNormalizedScore = outputTensor[0, 0];
		double clampedScore = Math.Clamp((double)rawNormalizedScore, 0.0, 1.0);

		return Math.Round(clampedScore * 100.0, 1);
	}
	public void Reload(string modelPath)
	{
		_session?.Dispose();
		var sessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions.MakeSessionOptionWithCudaProvider();
		sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
		sessionOptions.AddSessionConfigEntry("session.use_env_allocators", "1");
		_session = new InferenceSession(modelPath,sessionOptions);
	}
		
	public void Dispose()
	{
		_session.Dispose();
	}
}
