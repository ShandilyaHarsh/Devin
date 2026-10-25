#!/usr/bin/env bash
# Local dev runner: ensures a random audit-chain key in .env, builds the SPA, runs the BFF host on :5080.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${repo_root}"

env_file=".env"
key_name="Platform__AuditChainKey"
url="http://localhost:5080"

for tool in openssl dotnet npm; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "error: '${tool}' is required (see README prerequisites)" >&2; exit 1; }
done

current_key=""
if [[ -f "${env_file}" ]]; then
  current_key="$(grep -E "^${key_name}=" "${env_file}" | tail -n 1 | cut -d= -f2- || true)"
fi

if [[ -z "${current_key}" ]]; then
  if [[ -f platform/host/App_Data/platform.db ]]; then
    echo "warning: generating a NEW audit key while platform/host/App_Data/platform.db exists;" >&2
    echo "         existing audit rows will fail verification. Delete the DB for a clean demo." >&2
  fi
  umask 077
  tmp="$(mktemp "${env_file}.XXXXXX")"
  if [[ -f "${env_file}" ]]; then
    grep -vE "^${key_name}=" "${env_file}" > "${tmp}" || true
  fi
  printf '%s=%s\n' "${key_name}" "$(openssl rand -hex 32)" >> "${tmp}"
  mv "${tmp}" "${env_file}"
  echo "Generated a random ${key_name} in ${env_file} (gitignored)."
fi

# Export KEY=VALUE lines without evaluating them as shell code.
while IFS= read -r line || [[ -n "${line}" ]]; do
  [[ "${line}" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || continue
  export "${line%%=*}=${line#*=}"
done < "${env_file}"

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

if [[ ! -d node_modules || package-lock.json -nt node_modules ]]; then
  npm ci
fi
npm run build

echo "Starting Barebone host on ${url} (Ctrl+C to stop)"
exec dotnet run --project platform/host --urls "${url}"
