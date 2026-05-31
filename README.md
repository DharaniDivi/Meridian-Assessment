# SE Assessment — .NET + Angular

Solution for the four-layer SE assessment puzzle. The **API key stays on the backend**; Angular talks to your local ASP.NET Core API only.

## Structure

```
C:\AA-PROJECT\
├── Assessment.sln
├── src/
│   ├── Assessment.Core/     # Puzzle logic (fetch, hash, decrypt, search)
│   ├── Assessment.Api/      # ASP.NET Core Web API (proxy to assessment platform)
│   └── Assessment.Web/      # Angular dashboard
├── tests/
│   └── Assessment.Core.Tests/
└── challenges/              # Optional challenge artifacts
```

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/) and npm

## Configure credentials

**Do not commit your API key.**

### Option A — User secrets (recommended)

```powershell
cd C:\AA-PROJECT\src\Assessment.Api
dotnet user-secrets init
dotnet user-secrets set "Assessment:BaseUrl" "https://YOUR-BASE-URL"
dotnet user-secrets set "Assessment:ApiKey" "sa_YOUR_KEY"
```

### Option B — Environment variables

```powershell
$env:Assessment__BaseUrl = "https://YOUR-BASE-URL"
$env:Assessment__ApiKey = "sa_YOUR_KEY"
```

### Option C — appsettings.Development.json

Copy `appsettings.Development.example.json` → `appsettings.Development.json` and fill in values (file is gitignored).

## Run

### 1. Backend

```powershell
cd C:\AA-PROJECT\src\Assessment.Api
dotnet run
```

Swagger: https://localhost:7041/swagger

### 2. Frontend

```powershell
cd C:\AA-PROJECT\src\Assessment.Web
npm install
npm start
```

UI: http://localhost:4200

## Assessment clock

| Action | Starts 3h clock? |
|--------|------------------|
| `GET /api/health` (via backend → platform `/api/v1/health`) | **No** |
| Any authenticated platform call (Layer 1+, submit, time) | **Yes** |

**Recommended flow:**

1. Configure `BaseUrl` only → run health check (no key yet if you want zero risk)
2. Add `ApiKey` when ready to start
3. Run layers 1 → 2 → 3 → 4 in order
4. Submit `type: "repo"` **last** with your Git repository URL

## API endpoints (local)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/health` | Platform health (no clock) |
| GET | `/api/time` | Remaining assessment time |
| POST | `/api/layers/1/run` | Download dataset + SHA-256 |
| POST | `/api/layers/2/run` | Fetch key + decrypt |
| POST | `/api/layers/3/run` | Find alphabetic answer |
| POST | `/api/layers/4/run` | Generate analysis draft |
| POST | `/api/submit` | `{ type, value, notes? }` |

Cached files: `src/Assessment.Api/data/` (`dataset.bin`, `decrypted.jsonl`).

## Tests

```powershell
cd C:\AA-PROJECT
dotnet test
```

## Notes

- Endpoint paths (`/api/v1/dataset`, `/api/v1/key`, etc.) follow the assessment guide. If the platform uses different paths, update `AssessmentHttpClient.cs`.
- Layer 2 decryption supports plain JSON, base64, AES-GCM, and AES-CBC. Extend `Layer2Service` if the platform uses another format.
- Layer 3 candidate search is heuristic — inspect `data/decrypted.jsonl` and refine before submitting.
- Layer 4 analysis is a starting draft for human review.

## When you have credentials

Share `BASE_URL` and `API_KEY` and we can wire secrets, probe `/api/v1/health`, and run the layers. Say whether the clock should start immediately or scaffold-only first.
