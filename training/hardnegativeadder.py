import json
import random

def generate_hard_negatives():
	# 1. Read the newly formatted dataset
	with open("dataset.jsonl", "r", encoding="utf-8") as f:
		data = [json.loads(line) for line in f]

	# 2. Extract positives and a list of all unique titles
	positives = [d for d in data if d["label"] == 1]
	all_titles = list(set([d["title"] for d in data]))

	hard_negatives = []
	random.seed(42)

	# Target 1000 hard negatives (adjust if needed)
	num_to_generate = min(1000, len(positives))
	
	if num_to_generate > 0:
		sampled_positives = random.sample(positives, num_to_generate)

		for pos in sampled_positives:
			original_title = pos["title"]
			comment = pos["detail"]
			
			# Pick a random title that is strictly different from the original
			random_title = random.choice(all_titles)
			while random_title == original_title:
				random_title = random.choice(all_titles)
				
			# Create the deceptive pair with separate fields and label it 0
			hard_negatives.append({
				"title": random_title,
				"detail": comment,
				"label": 0
			})

	# 3. Combine and shuffle the dataset
	augmented_dataset = data + hard_negatives
	random.shuffle(augmented_dataset)

	# 4. Save to a new file ready for training
	with open("dataset_augmented.jsonl", "w", encoding="utf-8") as f:
		for d in augmented_dataset:
			f.write(json.dumps(d, ensure_ascii=False) + "\n")

	print(f"Original dataset: {len(data)}")
	print(f"Generated Hard Negatives: {len(hard_negatives)}")
	print(f"Total entries for training: {len(augmented_dataset)}")

if __name__ == "__main__":
	generate_hard_negatives()
