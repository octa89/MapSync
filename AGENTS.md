# Repository Guidelines

## Project Structure & Module Organization
- Single-project C# WPF app at the repository root. Core files are `*.xaml` and `*.xaml.cs` (UI) and `*.cs` (logic).
- Solution: `POSM_MR3.sln`; main project: `POSM_MR3_2.csproj`.
- Key folders: `Assets/` (images/icons), `Properties/` (assembly/resources), `publish/` (release output), and build artifacts `bin/`, `obj/`, `.vs/`.

## Build, Test, and Development Commands
- `dotnet restore` — restore NuGet packages.
- `dotnet build POSM_MR3.sln -c Debug` — compile for local dev.
- `dotnet run --project POSM_MR3_2.csproj` — launch the WPF app.
- `dotnet publish POSM_MR3_2.csproj -c Release -r win-x64 --self-contained true -o .\publish` — produce a distributable.
- `run_with_console.bat` — start with an attached console for logging.
Note: Build includes a `CopyArcGISRuntime` target that copies native files to `C:\POSM_Mapreader`. Ensure the path exists and you have permission.

## Coding Style & Naming Conventions
- C# with nullable enabled; use 4-space indentation and file-scoped namespaces when appropriate.
- Naming: `PascalCase` for types/public members; `camelCase` for locals/parameters; `_camelCase` for private fields.
- Keep UI concerns in `*.xaml.cs`; extract reusable logic into services (e.g., `PosmDatabaseService`). Prefer async/await and clear error handling.
- Optional: run `dotnet format` before committing to normalize style.

## Testing Guidelines
- No tests currently. If adding tests, use xUnit in `tests/` (e.g., `POSM_MR3.Tests`) and reference the main project.
- Commands: `dotnet new xunit -n POSM_MR3.Tests`, then `dotnet test`.
- Name test classes after the unit under test (e.g., `PosmDatabaseServiceTests`).

## Commit & Pull Request Guidelines
- Use small, focused commits. Prefer Conventional Commits:
  - `feat: add layer search source`
  - `fix: handle missing POSM.exe path`
- PRs should include: purpose, linked issues, screenshots for UI changes, and verification steps. Avoid unrelated changes.

## Security & Configuration Tips
- `config.json` stores user paths (e.g., `posmExecutablePath`). Do not commit secrets or machine-specific credentials.
- Respect ArcGIS Runtime licensing. Avoid adding absolute paths; prefer config or relative paths.

## MCP: GitHub Server (Optional)
- This repo includes a Claude MCP config to enable GitHub tools.
- Config lives in `.claude/settings.local.json` under `mcpServers.github` using `npx @modelcontextprotocol/server-github`.
- Requirements: Node.js and a `GITHUB_TOKEN` with repo scope in your environment.
- Example (PowerShell): `setx GITHUB_TOKEN "<token>"` then restart your editor.
- Verify: `npx -y @modelcontextprotocol/server-github --help`.
