import os
import subprocess
from transformers import AutoTokenizer

MODEL_DIR = "./final-model"
OUTPUT_DIR = "./onnx-output"
os.makedirs(OUTPUT_DIR, exist_ok=True)

# 1. Export vocab.txt once
print("Exporting vocab.txt...")
tokenizer = AutoTokenizer.from_pretrained(MODEL_DIR)
vocab = tokenizer.get_vocab()
sorted_tokens = [t for t, idx in sorted(vocab.items(), key=lambda x: x[1])]

vocab_path = os.path.join(OUTPUT_DIR, "vocab.txt")
with open(vocab_path, "w", encoding="utf-8") as f:
	for token in sorted_tokens:
		f.write(token + "\n")

# 2. Export ONNX model once
print("Exporting model.onnx...")
subprocess.run([
	"optimum-cli", "export", "onnx","--task","text-classification",
	"--model", MODEL_DIR,
	OUTPUT_DIR
], check=True)

print(f"Finished! All deployment files are ready in: {OUTPUT_DIR}")
