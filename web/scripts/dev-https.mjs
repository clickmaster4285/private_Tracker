/**
 * next dev with HTTPS using TLS_* paths from web/.env (same certs as the Go API).
 * Also sets NODE_EXTRA_CA_CERTS so the rewrite proxy trusts the local mkcert CA.
 */
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const envPath = resolve(webRoot, ".env");

function loadDotEnv(file) {
  if (!existsSync(file)) return;
  for (const raw of readFileSync(file, "utf8").split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const i = line.indexOf("=");
    if (i <= 0) continue;
    const key = line.slice(0, i).trim();
    let value = line.slice(i + 1).trim();
    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1);
    }
    if (!(key in process.env)) process.env[key] = value;
  }
}

function resolveFromWeb(p) {
  if (!p) return null;
  const abs = resolve(webRoot, p);
  return existsSync(abs) ? abs : null;
}

loadDotEnv(envPath);

const cert = resolveFromWeb(process.env.TLS_CERT_FILE || "../certs/local.pem");
const key = resolveFromWeb(process.env.TLS_KEY_FILE || "../certs/local-key.pem");
const ca = resolveFromWeb(
  process.env.TLS_CA_FILE || process.env.NODE_EXTRA_CA_CERTS || "../certs/rootCA.pem",
);

if (!cert || !key) {
  console.error(
    "[dev-https] Missing TLS cert/key. Set TLS_CERT_FILE / TLS_KEY_FILE in web/.env (expected ../certs/local.pem).",
  );
  process.exit(1);
}

if (ca) {
  process.env.NODE_EXTRA_CA_CERTS = ca;
}

const args = [
  "next",
  "dev",
  "--experimental-https",
  "--experimental-https-cert",
  cert,
  "--experimental-https-key",
  key,
];
if (ca) {
  args.push("--experimental-https-ca", ca);
}

console.log(`[dev-https] cert=${cert}`);
console.log(`[dev-https] key=${key}`);
if (ca) console.log(`[dev-https] ca=${ca}`);

const child = spawn("npx", args, {
  cwd: webRoot,
  stdio: "inherit",
  shell: true,
  env: process.env,
});

child.on("exit", (code, signal) => {
  if (signal) process.kill(process.pid, signal);
  process.exit(code ?? 1);
});
