# AGENTS.md

FilmBatch: a batch film-look tool in C# / .NET 10 + Avalonia, with a WebGPU/WGSL image engine and a CPU path as reference.
Rules below apply to every contributor, human or AI.

## Read first

- Doc map (find docs by task): [docs/README.md](docs/README.md)
- Architecture and data flow: [docs/architecture.md](docs/architecture.md) §2–3
- Verification entry: [VERIFY.md](VERIFY.md); build and test: [docs/building.md](docs/building.md)

## Workflow

1. **Understand before implementing.** Read [docs/architecture.md](docs/architecture.md) first. After analysis, give the contributor the options for *how* to implement it, plus a recommended plan.
2. **State the impact.** Use [docs/history.md](docs/history.md) to see how the project got here. Every plan must say which existing parts will change and which features may be affected.
3. **Small features** (e.g. a download button, layout tweaks): reuse existing code as much as possible. Example: after a download, show the saved path in the existing input box instead of adding a new line of small text.
4. **Large features** (e.g. a photo-frame feature, changes to the RAW decode pipeline): follow TDD and MVVM.
5. **Code review after implementing**, as an expert, against these three checks:
   1. Anything redundant or bloated? E.g. new code written for something existing code with the same logic (such as the download path) already handles, or several functions used to implement one simple feature.
   2. Does it meet [VERIFY.md](VERIFY.md)?
   3. Can the code just implemented or changed be optimized further?
6. **Stop on problems.** If something goes wrong, stop and tell the contributor what happened, then offer two choices:
   - **Continue**: list the ways to continue and let the contributor pick one.
   - **Stop for now**: commit the work so far and record the problem.

## Where to change things

Follow the recipes in [docs/architecture.md](docs/architecture.md) §5.1–5.6 (new parameter, effect module, undoable photo operation, UI text, dialog, film look).

- The parameter schema lives only in `Core/Params/ModuleCatalog.cs`; UI, save files and paste derive from it.
- Rendering has a CPU and a GPU path. Change both and run the consistency tests.
- After touching a shader: `dotnet run build/CheckShaders.cs`
- UI text goes in both `Strings.resx` and `Strings.en.resx`; XAML uses `{l:Tr Key}`.
- Check appearance: `dotnet run --project src/FilmBatch.App -- --gallery`

## Ground rules

- Current contracts are [architecture.md](docs/architecture.md) (and `docs/architecture/`), [building.md](docs/building.md) (and [testing](docs/testing.md), [release](docs/release.md), [cli](docs/cli.md)) and [film-looks.md](docs/film-looks.md). [docs/plan.md](docs/plan.md) and `docs/superpowers/plans/` are history, not requirements.
- Survey conclusions (e.g. object removal T1 is NO-GO) are research, not supported features.
- A commit title is not proof of verification; use the dated evidence and [VERIFY.md](VERIFY.md).
- Check licences before adding native dependencies, LUTs or models: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
- When behaviour changes, update the matching doc in the same change.
- One commit per finished item when a request has several items.
