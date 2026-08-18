from flask import Flask, request, jsonify

app = Flask(__name__)

@app.route("/", methods=["POST"])
def webhook():
    # Headers
    headers = dict(request.headers)
    print("\n--- Headers ---")
    print(headers)

    # JSON payload
    payload = request.get_json(silent=True)
    if payload:
        print("\n--- JSON Payload ---")
        print(payload)
    else:
        print("\n--- Raw Data ---")
        print(request.get_data(as_text=True))

    return jsonify({"status": "success"}), 200

@app.route("/", methods=["GET"])
def health_check():
    return "Port 3000 listener is active", 200

if __name__ == "__main__":
    # Runs specifically on port 3000
    app.run(host="0.0.0.0", port=3000, debug=True)
