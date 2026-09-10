import os
from optimum.exporters.onnx import main_export

MODEL_DIR = "./final-model"
OUTPUT_DIR = "./onnx-output"
os.makedirs(OUTPUT_DIR, exist_ok=True)

print("Exporting model to ONNX...")
main_export(
    model_name_or_path=MODEL_DIR,
    output=OUTPUT_DIR,
    task="text-classification"
)

print(f"Finished! ONNX model ready in: {OUTPUT_DIR}")
