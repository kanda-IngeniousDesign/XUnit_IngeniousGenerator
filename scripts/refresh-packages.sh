#!/bin/zsh
# Core と CSharp_IngeniousAnalyzer をビルドし直し、すべてのサンプルに同じビルドを行き渡らせる。
# 開発中は同じバージョン番号（0.1.0-dev / 3.5.0-dev）でパッケージを作り直すため、
# NuGet のキャッシュやサンプルの .packages に古いビルドが残ると、VS Code が古いアナライザーを読み込んでしまう
# （同じ名前・同じバージョンのアセンブリは1つしか読み込まれない）。
set -e
ROOT=${0:A:h:h}
ANALYZER=${ROOT:h}/CSharp_IngeniousAnalyzer
OUT=$ROOT/artifacts/packages

dotnet pack "$ROOT/IngeniousXUnitGenerator.Core" -c Release -o "$OUT" | { grep -E "error|Successfully" || true; }
rm -rf ~/.nuget/packages/ingeniousxunitgenerator.core/0.1.0-dev ~/.nuget/packages/csharp_ingeniousanalyzer/3.5.0-dev

# dotnet pack だけでは古いビルドの dll が同梱されることがあるため、Release でビルドしてから --no-build で作る
dotnet build "$ANALYZER/CSharp_IngeniousAnalyzer.csproj" -c Release | { grep -E "error|Error\(s\)" || true; }
dotnet pack "$ANALYZER/CSharp_IngeniousAnalyzer.csproj" -c Release --no-build -o "$OUT" | { grep -E "error|Successfully" || true; }

for sample in "$ROOT"/samples/*/; do
  rm -rf "$sample/.packages"
  for solution in "$sample"/*.slnx; do
    dotnet restore "$solution" | { grep -E "error|Restored" || true; }
  done
done
echo "完了: VS Code を開き直す（Developer: Reload Window）と、新しいアナライザーが読み込まれます"
