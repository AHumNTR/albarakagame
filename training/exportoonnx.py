from optimum.onnxruntime import ORTModelForSequenceClassification
from transformers import AutoTokenizer

def export_model():
	print("Loading fine-tuned model and tokenizer...")
	model_dir = "./final-model"
	output_dir = "./OnnxModels"

	# Load and export to ONNX in one step
	ort_model = ORTModelForSequenceClassification.from_pretrained(model_dir, export=True)

	# Save the ONNX graph and tokenizer files to the target folder
	print(f"Saving ONNX model to {output_dir}...")
	ort_model.save_pretrained(output_dir)
	print("Export complete!")

if __name__ == "__main__":
	export_model()
