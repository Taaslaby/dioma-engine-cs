#!/usr/bin/env bash
# Самопроверка пакета com.dioma.engine без Unity-редактора.
#   scripts/verify.sh              → все четыре проверки
#   scripts/verify.sh parity|bio|skinner|arena
# Собирает Runtime прямо из исходников (Harness~/smoke.csproj), поэтому
# проверяется тот же код, который увидит редактор. Burst-кернелы сюда не
# попадают — их паритет ловят EditMode-тесты в dioma-unity.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# dotnet на этой машине лежит вне PATH — ищем по обоим адресам.
find_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return
  fi
  local candidate="${USERPROFILE:-$HOME}/.dotnet/dotnet.exe"
  candidate="${candidate//\\//}"
  if [[ -x "$candidate" ]]; then
    printf '%s\n' "$candidate"
    return
  fi
  echo "dotnet SDK не найден: нет в PATH и нет $candidate" >&2
  echo "поставь .NET 8 SDK или добавь его в PATH" >&2
  exit 127
}

DOTNET="$(find_dotnet)"
PROJECT="Harness~/smoke.csproj"
MODE="${1:-all}"

echo "── сборка харнесса ($("$DOTNET" --version)) ──"
"$DOTNET" build -c Release "$PROJECT" --nologo -v q

echo
"$DOTNET" run -c Release --project "$PROJECT" --no-build -- "$MODE"
