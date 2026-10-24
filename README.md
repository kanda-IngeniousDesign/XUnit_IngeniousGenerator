# XUnit_IngeniousGenerator

Generates xUnit tests from C# code deterministically with Roslyn — no LLM, no external services.

This repository contains `XUnit_IngeniousGenerator.Core`, the generation engine used by the TEST001 / TEST002 rules of [CSharp_IngeniousAnalyzer](https://github.com/kanda-IngeniousDesign/CSharp_IngeniousAnalyzer). To use it, install CSharp_IngeniousAnalyzer and enable the rules in `.editorconfig`; the engine is bundled in the analyzer package.

```
dotnet_diagnostic.TEST001.severity = warning
dotnet_diagnostic.TEST002.severity = warning
```

What it generates:

- Exception tests from argument guards (`if (...) throw`, `ThrowIf...`, `?? throw`), including `ParamName` checks.
- Boundary-value tests whose expected values are computed from the code by a static interpreter.
- Snapshot tests (Verify) for values that can't be computed statically, with dependencies mocked by Moq.
- No-exception checks for methods without a return value.

Members that use files, databases or the network are reported (TEST002) instead of getting tests.

## 日本語

C#のコードから、xUnitのテストコードをRoslynだけで決定的に生成します（LLMや外部サービスは使いません）。

このリポジトリは、[CSharp_IngeniousAnalyzer](https://github.com/kanda-IngeniousDesign/CSharp_IngeniousAnalyzer) の TEST001・TEST002 が使う生成エンジン `XUnit_IngeniousGenerator.Core` です。利用するには CSharp_IngeniousAnalyzer をインストールし、`.editorconfig` で上記のルールを有効にしてください。エンジンはアナライザーのパッケージに同梱されています。

## License

MIT
