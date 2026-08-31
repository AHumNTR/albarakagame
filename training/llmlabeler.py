import json
import os
import random
import re
import time
from google import genai
from google.genai import types

client = genai.Client(api_key=os.environ.get("GEMINI_API_KEY"))

SYSTEM_INSTRUCTION = """You evaluate enterprise software comments (Jira/TFS logs) relative to their titles on a scale of 1 to 5.

Scoring Standard:
1 - Non-technical / Administrative (greetings, holiday notices, "bakacağım").
2 - Status Update Only (deployment notices, "merged", "paket girişi yapıldı", no explanation).
3 - Minor Technical Detail (mentions a parameter/table/service name, but lacks root cause or logic detail).
4 - Solid Technical Fix (explains the root cause OR specifies the code change/fallback logic).
5 - Deep Technical Explanation (deadlock analysis, specific SQL/SP logic, transaction levels, stack traces, or step-by-step root cause analysis).

The comments have to be related to the title. If you are not sure assume they are. If you are sure they are unrelated give a maximum of 2 points

Examples:
Title: SLR 101: BES Hata Bildirimi
Comment: İyi bayramlar herkese, haftaya bakacağım.
Score: 1

Title: SLR 1413193: Kağıtsız Sigortacılık - Hayat/BES - BOP
Comment: ==> Prod için paketlerin girişi yapıldı....
Score: 2

Title: SLR 202: Hayat Poliçesi Prim Hesaplama Hatası
Comment: Hesaplama servisi içerisindeki BES prim katsayısı tablosunda eksik kayıt vardı, parametre tablosu güncellendi.
Score: 3

Title: SLR 303: NullPointerException in PaymentGateway
Comment: Root cause: Ödeme servisinde timeout durumunda response null dönüyordu. Null check eklendi ve fallback servisine yönlendirme yapıldı.
Score: 4

Title: SLR 404: Veritabanı Deadlock Hatası
Comment: Deadlock analizi yapıldı: BES_POLICY ve CUSTOMER_ACCOUNT tabloları ters sıra ile kilitleniyordu. SP_UPDATE_POLICY prosedürü revize edildi, transaction isolation level READ_COMMITTED olarak ayarlandı.
Score: 5

Output strictly a single integer from 1 to 5."""
CONFIG = types.GenerateContentConfig(
	system_instruction=SYSTEM_INSTRUCTION,
	temperature=0.0,
	max_output_tokens=20,
	thinking_config=types.ThinkingConfig(thinking_level="MINIMAL"),
)

def test_random_sample(input_jsonl_path: str):
	with open(input_jsonl_path, "r", encoding="utf-8") as f:
		rows = [json.loads(line) for line in f if line.strip()]

	if not rows:
		print("No rows found in file.")
		return

	sample = random.choice(rows)
	title = sample.get("title", "")
	comment = sample.get("detail", "")
	prompt = f"Title: {title}\n\nComment: {comment}"

	print("=== Running Single Random Test ===")
	print(f"Title:   {title}")
	print(f"Comment: {comment}...")

	response = client.models.generate_content(
		model="gemini-3.5-flash-lite",
		contents=prompt,
		config=CONFIG,
	)

	raw_score = response.text.strip()
	match = re.search(r"[1-5]", raw_score)
	score = int(match.group(0)) if match else None

	print(f"Model Raw Output: '{raw_score}'")
	print(f"Parsed Score:     {score}/5")
	if score is not None:
		print(f"Normalized Target (for ModernBERT): {(score - 1) / 4:.2f}")
	print("=" * 35)


# ----------------------------------------------------------------------
# 2. Batch API: Prepare batch JSONL payload
# ----------------------------------------------------------------------
def prepare_batch_input_file(input_path: str, batch_file_path: str):
	"""
	Converts source JSONL into the Gemini Batch API schema:
	{"key": "req-X", "request": {"contents": [...], "generationConfig": ...}}
	"""
	print(f"Preparing batch request file: {batch_file_path}...")
	count = 0
	with open(input_path, "r", encoding="utf-8") as infile, \
		 open(batch_file_path, "w", encoding="utf-8") as outfile:
		for i, line in enumerate(infile):
			if not line.strip():
				continue
			row = json.loads(line)
			prompt_text = f"Title: {row.get('title', '')}\n\nComment: {row.get('detail', '')}"

			batch_req = {
				"key": f"row-{i}",
				"request": {
					"contents": [{"parts": [{"text": prompt_text}], "role": "user"}],
					"systemInstruction": {"parts": [{"text": SYSTEM_INSTRUCTION}]},
					"generationConfig": {
						"temperature": 0.0,
						"maxOutputTokens": 20,
					}
				}
			}
			outfile.write(json.dumps(batch_req) + "\n")
			count += 1

	print(f"Generated {count} batch requests.")


# ----------------------------------------------------------------------
# 3. Batch API: Submit, Poll, and Merge Results
# ----------------------------------------------------------------------
def run_batch_job(batch_requests_path: str, original_jsonl_path: str, final_output_path: str):
	# Step A: Upload request file to the File API
	print("Uploading batch request file to Google AI...")
	uploaded_file = client.files.upload(
		file=batch_requests_path,
		config=types.UploadFileConfig(mime_type="jsonl", display_name="batch_data")
	)
	print(f"Uploaded file name: {uploaded_file.name}")

	# Step B: Create batch prediction job
	print("Creating Batch Job (50% cheaper, processed asynchronously)...")
	batch_job = client.batches.create(
		model="gemini-3.5-flash-lite",
		src=uploaded_file.name,
		config={"display_name": "modernbert_dataset_scoring"},
	)
	print(f"Batch Job ID: {batch_job.name}")

	# Step C: Poll for job completion
	while True:
		job = client.batches.get(name=batch_job.name)
		state = str(job.state).upper()
		print(f"Job state: {state} ... waiting 30 seconds")
		if "SUCCEEDED" in state or "COMPLETED" in state:
			break
		if "FAILED" in state or "CANCELLED" in state:
			raise RuntimeError(f"Batch job terminated with status: {state}")
		time.sleep(30)

	# Step D: Download results
	print(f"Job completed. Output file: {job.output_file}")
	result_file_bytes = client.files.download(name=job.output_file)
	raw_results = result_file_bytes.decode("utf-8").strip().split("\n")

	# Step E: Parse responses mapped by key
	scores_by_key = {}
	for line in raw_results:
		if not line.strip():
			continue
		res = json.loads(line)
		key = res.get("key")
		try:
			candidate = res["response"]["candidates"][0]["content"]["parts"][0]["text"].strip()
			match = re.search(r"[1-5]", candidate)
			score = int(match.group(0)) if match else None
		except (KeyError, IndexError, TypeError):
			score = None
		scores_by_key[key] = score
#
	# Step F: Merge back with original data and add normalized label
	with open(original_jsonl_path, "r", encoding="utf-8") as orig_f, \
		 open(final_output_path, "w", encoding="utf-8") as out_f:
		for i, line in enumerate(orig_f):
			if not line.strip():
				continue
			row = json.loads(line)
			score = scores_by_key.get(f"row-{i}")

			row["score"] = score
			# Continuous normalized score in [0.0, 1.0] for ModernBERT regression
			row["accepted_score"] = round((score - 1) / 4.0, 4) if score is not None else None
			# Optional binary threshold (4 or 5 = true)
			row["accepted"] = score >= 4 if score is not None else False

			out_f.write(json.dumps(row, ensure_ascii=False) + "\n")

	print(f"All done! Labeled dataset saved to: {final_output_path}")


if __name__ == "__main__":
	INPUT_FILE = "dataset_augmented.jsonl"
	BATCH_REQUESTS = "batch_payload.jsonl"
	FINAL_FILE = "data_scored.jsonl"
	prepare_batch_input_file(INPUT_FILE, BATCH_REQUESTS)
	run_batch_job(BATCH_REQUESTS, INPUT_FILE, FINAL_FILE)
