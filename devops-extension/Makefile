
publish:
	@node -e 'const fs = require("fs"); const f = "vss-extension.json"; const d = JSON.parse(fs.readFileSync(f, "utf8")); const p = d.version.split(".").map(Number); p[1] += 1; p[2] = 0; d.version = p.join("."); fs.writeFileSync(f, JSON.stringify(d, null, 2) + "\n"); console.log("==> Bumped version to " + d.version);'
	tfx extension publish --manifest-globs vss-extension.json --token EhTXhaCadc2UCtYkNLXa2c1HWHCCkjPbKLMzqhgzm53FAILIOh2SJQQJ99CHACAAAAAWcGBqAAASAZDO2hLF 
