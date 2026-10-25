#!/usr/bin/env bash
# Scaffold a new platform app: scripts/scaffold-app.sh <slug> ["Display Name"]
# Creates apps/<slug>/ from scripts/templates/app/ and adds its backend to Devin.slnx. Never touches platform/.
set -euo pipefail

usage() { echo "usage: $0 <slug> [\"Display Name\"]   (slug: ^[a-z][a-z0-9-]{2,40}\$)" >&2; exit 2; }
[[ $# -ge 1 && $# -le 2 ]] || usage
slug="$1"
[[ "${slug}" =~ ^[a-z][a-z0-9-]{2,40}$ && "${slug}" != *- && "${slug}" != *--* ]] || usage

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
template_dir="${repo_root}/scripts/templates/app"
target="${repo_root}/apps/${slug}"
[[ -d "${template_dir}" ]] || { echo "error: templates not found at ${template_dir}" >&2; exit 1; }
[[ ! -e "${target}" ]] || { echo "error: ${target} already exists" >&2; exit 1; }

# kyc-case-reviewer -> Pascal "KycCaseReviewer", assembly "Kyc.CaseReviewer" (plan convention), display "Kyc Case Reviewer".
IFS='-' read -r -a parts <<< "${slug}"
pascal=""; display_default=""
for p in "${parts[@]}"; do
  cap="$(tr '[:lower:]' '[:upper:]' <<< "${p:0:1}")${p:1}"
  pascal+="${cap}"; display_default+="${display_default:+ }${cap}"
done
first="$(tr '[:lower:]' '[:upper:]' <<< "${parts[0]:0:1}")${parts[0]:1}"
assembly="${first}"
if [[ ${#parts[@]} -gt 1 ]]; then assembly="${first}.${pascal#"${first}"}"; fi
[[ "${pascal}" =~ ^[A-Z] ]] || { echo "error: slug must start with a letter" >&2; exit 2; }
display="${2:-${display_default}}"
[[ "${display}" =~ ^[A-Za-z0-9\ \-]{3,60}$ ]] || { echo "error: display name must be 3-60 letters, digits, spaces or dashes" >&2; exit 2; }
table_prefix="${slug//-/_}"
role="${display} User"

platform_before="$(git -C "${repo_root}" status --porcelain -- platform/ 2>/dev/null || true)"

render() {
  sed -e "s/__SLUG__/${slug}/g" -e "s/__DISPLAY_NAME__/${display}/g" -e "s/__ASSEMBLY__/${assembly}/g" \
      -e "s/__PASCAL__/${pascal}/g" -e "s/__TABLE_PREFIX__/${table_prefix}/g" -e "s/__ROLE__/${role}/g" "$1"
}

while IFS= read -r -d '' src; do
  rel="${src#"${template_dir}/"}"
  rel="${rel%.tmpl}"
  rel="${rel//__ASSEMBLY__/${assembly}}"
  rel="${rel//__PASCAL__/${pascal}}"
  dest="${target}/${rel}"
  mkdir -p "$(dirname "${dest}")"
  render "${src}" > "${dest}"
  echo "  created apps/${slug}/${rel}"
done < <(find "${template_dir}" -type f -name '*.tmpl' -print0 | sort -z)

csproj="apps/${slug}/backend/${assembly}.csproj"
(cd "${repo_root}" && dotnet sln Devin.slnx add --solution-folder apps "${csproj}")

platform_after="$(git -C "${repo_root}" status --porcelain -- platform/ 2>/dev/null || true)"
if [[ "${platform_before}" != "${platform_after}" ]]; then
  echo "error: platform/ changed during scaffolding; this must never happen" >&2
  exit 1
fi

cat <<MSG

Scaffolded apps/${slug} (assembly ${assembly}, class ${pascal}App).
Next: edit apps/${slug}/platform.yaml (owner, classification, roles, piiFields, auditedFields),
apps/${slug}/policy.yaml, entities + endpoints in backend/, pages in web/index.tsx; then request it in the Builder.
Check: git diff --stat main -- platform/   (must be empty)
MSG
