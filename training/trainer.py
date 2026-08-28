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
	model_name = "xlm-roberta-large"
	
	torch.set_float32_matmul_precision('high')
	# 1. Load the dataset
	dataset = load_dataset("json", data_files="dataset_augmented.jsonl", split="train")
	
	# Split into train (90%) and validation (10%) sets
	dataset = dataset.train_test_split(test_size=0.1, seed=42)
	
	# 2. Load the Tokenizer
	tokenizer = AutoTokenizer.from_pretrained(model_name)
	
	# 3. Tokenization function
	# We truncate to 128 tokens to prevent the memory issues you saw earlier
	def tokenize_function(examples):
		return tokenizer(
			examples["title"],
			examples["detail"],
			padding=False,
			truncation=True,
			max_length=256
		)
	
	tokenized_datasets = dataset.map(tokenize_function, batched=True)
	
	# Data collator handles dynamic padding for batches
	data_collator = DataCollatorWithPadding(tokenizer=tokenizer)
	
	# 4. Load the Model with a Sequence Classification head (2 labels: 0=Fail, 1=Pass)
	model = AutoModelForSequenceClassification.from_pretrained(
		model_name,
		num_labels=2
	)
	
	# 5. Define evaluation metrics (Accuracy and F1)
	metric_acc = evaluate.load("accuracy")
	metric_f1 = evaluate.load("f1")
	
	def compute_metrics(eval_pred):
		logits, labels = eval_pred
		predictions = np.argmax(logits, axis=-1)
		acc = metric_acc.compute(predictions=predictions, references=labels)
		f1 = metric_f1.compute(predictions=predictions, references=labels)
		return {**acc, **f1}
	
	# 6. Setup Training Arguments
	training_args = TrainingArguments(
		output_dir="./modernbert-tr-finetuned",
		learning_rate=2e-5,
		per_device_train_batch_size=8,
		per_device_eval_batch_size=8,
		num_train_epochs=5,
		weight_decay=0.01,
		eval_strategy="epoch",
		save_strategy="epoch",
		load_best_model_at_end=True,
		push_to_hub=False,
		
		tf32=True,
	)
	
	# 7. Initialize Trainer
	trainer = Trainer(
		model=model,
		args=training_args,
		train_dataset=tokenized_datasets["train"],
		eval_dataset=tokenized_datasets["test"],
		processing_class=tokenizer,
		data_collator=data_collator,
		compute_metrics=compute_metrics,
	)
	
	# 8. Train!
	print("Starting training...")
	trainer.train()
	
	# 9. Save the final model and tokenizer
	print("Saving model to ./final-model")
	trainer.save_model("./final-model")

if __name__ == "__main__":
	main()
