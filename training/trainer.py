from transformers import EarlyStoppingCallback
import numpy as np
import torch
import evaluate
from datasets import load_dataset
from transformers import (
	AutoTokenizer,
	AutoModelForSequenceClassification,
	TrainingArguments,
	Trainer,
	DataCollatorWithPadding
)

# Enable TF32 for matrix multiplications and cuDNN
torch.backends.cuda.matmul.allow_tf32 = True
torch.backends.cudnn.allow_tf32 = True

def main():
	model_name = "ytu-ce-cosmos/modernbert-tr-base"
	torch.set_float32_matmul_precision("high")

	# 1. Load the scored dataset
	dataset = load_dataset("json", data_files="data_scored.jsonl", split="train")

	# Clean dataset: filter out rows where fields are missing or score is None

	# Format the target column as float 'label' for regression
	dataset = dataset.map(lambda x: {"label": float(x["accepted_score"])})

	# Split into train (90%) and validation (10%)
	dataset = dataset.train_test_split(test_size=0.1, seed=42)

	# 2. Load Tokenizer
	tokenizer = AutoTokenizer.from_pretrained(model_name)

	# 3. Tokenize pairs: (title, detail)
	def tokenize_function(examples):
		return tokenizer(
			examples["title"],
			examples["detail"],
			padding=False,
			truncation=True,
			max_length=256
		)

	tokenized_datasets = dataset.map(tokenize_function, batched=True)
	data_collator = DataCollatorWithPadding(tokenizer=tokenizer)

	# 4. Load Model for Regression (num_labels=1)
	model = AutoModelForSequenceClassification.from_pretrained(
		model_name,
		num_labels=1
	)

	# 5. Metrics for Regression (MSE and Pearson Correlation)
	metric_mse = evaluate.load("mse")

	def compute_metrics(eval_pred):
		predictions, labels = eval_pred
		predictions = np.squeeze(predictions)
		mse = metric_mse.compute(predictions=predictions, references=labels)
		return mse

	# 6. Training Arguments
	training_args = TrainingArguments(
		output_dir="./modernbert-tr-finetuned",
		learning_rate=3e-5,
		per_device_train_batch_size=8,
		per_device_eval_batch_size=8,
		num_train_epochs=5,
		warmup_ratio=0.1,
		weight_decay=0.01,
		eval_strategy="epoch",
		save_strategy="epoch",
		load_best_model_at_end=True,
		metric_for_best_model="loss",
		greater_is_better=False,
		push_to_hub=False,
		tf32=True,
	)

	# 7. Trainer
	trainer = Trainer(
		model=model,
		args=training_args,
		train_dataset=tokenized_datasets["train"],
		eval_dataset=tokenized_datasets["test"],
		processing_class=tokenizer,
		data_collator=data_collator,
		compute_metrics=compute_metrics,
		callbacks=[EarlyStoppingCallback(early_stopping_patience=2)]
	)

	# 8. Train
	print("Starting regression training...")
	trainer.train()

	# 9. Save
	print("Saving model to ./final-model")
	trainer.save_model("./final-model")
	tokenizer.save_pretrained("./final-model")

if __name__ == "__main__":
	main()
