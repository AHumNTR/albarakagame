import json
import os
from google import genai
from google.genai import types

client = genai.Client(api_key=os.environ.get("GEMINI_API_KEY"))

# Refined prompt with few-shot calibration
SYSTEM_INSTRUCTION = """You are a technical data labeling classifier.
Your task is to classify whether a comment represents a detailed technical explanation, code change, root cause analysis, or test execution log for the given title.

Label TRUE if:
- It explains *how* or *why* a technical fix or implementation works.
- It details root cause analysis, error traces, or debugging steps.
- It describes concrete test cases, steps executed, or code changes.

Label FALSE if:
- It is a brief status update (e.g., "Merged PR", "Fixed in main", "Looking into this").
- It is an administrative note, meeting summary, holiday notice, or general chatter.
- It merely restates the title without actionable technical depth.

Respond only in true or false"""

CONFIG = types.GenerateContentConfig(
	system_instruction=SYSTEM_INSTRUCTION,
	temperature=0.0,
	max_output_tokens=150,  # Increased so the answer/reasoning is never cut off
)

def test_samples(input_path: str, sample_size: int = 5):
	samples = []
	with open(input_path, "r", encoding="utf-8") as f:
		for line in f:
			if line.strip():
				samples.append(json.loads(line))
			if len(samples) >= sample_size:
				break

	print(f"Testing {len(samples)} samples...\n" + "=" * 60)

	for i, row in enumerate(samples, 1):
		title = row.get("title", "")
		comment = row.get("detail", "")
		prompt = f"Title: {title}\n\nComment: {comment}"

		try:
			response = client.models.generate_content(
				model="gemini-3.5-flash-lite",
				contents=prompt,
				config=CONFIG,
			)
			output = response.text.strip()
			verdict = "true" in output.split("\n")[0].lower()

			print(f"\n--- Sample {i} ---")
			print(f"Title:   {title[:90]}...")
			print(f"Comment: {comment[:120]}...")
			print(f"Result:  {'ACCEPTED (TRUE)' if verdict else 'REJECTED (FALSE)'}")
			print(f"Model Output:\n{output}\n")
		except Exception as e:
			print(f"Error on sample {i}: {e}")

if __name__ == "__main__":
	test_samples("dataset.jsonl", sample_size=1)
