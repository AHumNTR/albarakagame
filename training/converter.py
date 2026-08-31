import json
import re
#converts test.json into the format we want for training

input_file = "/home/humn/albarakamltest/testOriginal.json"
output_file = "dataset.jsonl"

with open(input_file, "r", encoding="utf-8") as f:
    data = json.load(f)

transformed = []

for item in data:
    title = item.get("WorkItemTitle") or "Belirtilmemiş"
    clean_title = re.sub(r'^(talep|task|bug|iş emri)?\s*\d+\s*[-_:]\s*', '', title, flags=re.IGNORECASE).strip()
    detail = (item.get("Detail") or "").strip()
    reject_reason = item.get("RejectReason")
    
    if reject_reason == "LOW_QUALITY_COMMENT" or reject_reason is None:
        # Label 1 = Meaningful (null reject reason), Label 0 = Low quality / Trivial
        label = 0 if reject_reason else 1

        # output title and detail as separate fields
        transformed.append({
            "title": clean_title.strip(),
            "detail": detail,
        })

# Save as JSONL (one JSON object per line)
with open(output_file, "w", encoding="utf-8") as f:
    for row in transformed:
        f.write(json.dumps(row, ensure_ascii=False) + "\n")

print(f"Transformed {len(transformed)} items into {output_file}")
