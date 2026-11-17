# HR OS AI service: developer guide

Written for: .NET developers on the HRMS team who have not seen this project.

This guide says what the `dotnet/` code contains, how to plug it into HRMS, and how the database relates to the AI. Every class, endpoint and setting named here exists in the code.

## 1. What this is

An AI engine for recruitment. It **reads** CVs, **scores** candidates against a job, and **drafts** tests, emails and job descriptions. It does not decide, send or store anything.

Rules that bind it (they come from the product, not from taste):

- **Local models only.** Every call goes to a vLLM server inside the company network. No cloud AI.
- **A person decides.** The service gives a recommendation, a score and the evidence. HR decides. It never writes SELECTED or REJECTED.
- **CNIC never reaches a model or a log.** The CNIC is removed from every prompt (`AiGuardrails`) and is never logged.
- **Flag, don't delete.** A weak or uncertain result is flagged for HR (`requires_hr_review`). Nobody is rejected by the AI.

## 2. Solution layout

```
dotnet/
  HrOs.Ai.slnx
  src/HrOs.Ai/            the library (all the logic)
  src/HrOs.Ai.Host/       a small web API around the library
  tests/HrOs.Ai.Tests/    xUnit tests, no real model needed
  Dockerfile              builds the host (run from this folder)
  docs/DEVELOPER-GUIDE.md this file
```

Target `net10.0`. Packages: `PdfPig` (PDF text), `DocumentFormat.OpenXml` (Word text), `Microsoft.Extensions.Http`, `System.Text.Json`. OpenTelemetry `ActivitySource` name: `HrOs.Ai`.

## 3. What is in the box

| Area | Main classes | What it does |
|---|---|---|
| `Core` | `IAiClient`, `VllmAiClient`, `AiOptions`, `AiGuardrails`, `AiUnavailableException` | The one door to the model servers: chat as JSON, embeddings, health. Removes CNICs, control characters and over-long text from every prompt. |
| `Cv` | `CvTextReader`, `CvProfileExtractor`, `CvExtractionService`, `CvValidator`, `CandidateProfile` | PDF, DOCX and TXT to text, then a form-ready profile (name, skills, education, experience). Remembers recent results (SHA-256 of the file). No OCR. |
| `Embeddings` | `EmbeddingService`, `PoolSimilarity` | Vectors for a candidate and for a job; cosine, Jaccard and a combined score for the talent pool. |
| `Screening` | `ScreeningService`, `ScreeningPipeline`, `ScreeningReport`, `BlindCv`, `Criterion` | Seven steps that turn one CV and one job into a score with evidence. |
| `Generation` | `TestGeneratorService`, `EmailDraftService`, `JobAssistantService` | Drafts for HR to edit. They never send or save. |
| `Prompts` | `PromptLibrary`, `cv_profile_extraction.txt` + the shared prompts | The prompt texts. See section 8. |

### The seven screening steps

1. CV extraction: the CV text to a profile with dated education and experience, and the grade.
2. CV validation: missing parts, conflicting dates.
3. Job requirements: the job text to groups (education, experience, skills, certifications, mandatory, preferred, location).
4. Requirement matching: each requirement gets MATCH, PARTIAL, MISSING or UNCLEAR, with a quote from the CV.
5. Evidence verification: does the CV support each positive claim?
6. Uncertainty: what a person should confirm.
7. Recommendation: the written reason, and the score.

Steps 1 and 3 run together; 2 and 4 together; 5 and 6 together; then 7. The whole run has a time limit (`Ai:ScreeningTimeoutSeconds`, default 360); on timeout the host answers 504 and the caller falls back to something simpler.

### Criteria and score

- **Criteria, in order of importance:** qualification (degree, field, minimum grade), experience (years and kind of work), relevance (how closely the CV's work matches this role's duties; always judged, always the first requirement), then skills and certifications. Each result carries a `category`: `qualification`, `experience`, `relevance`, `skills`, `certifications` or `other`.
- **The score is computed in code, not by the model.** MATCH = 100, PARTIAL = 50, UNCLEAR = 25, MISSING = 0. Mandatory requirements weigh twice. Caps: any mandatory MISSING caps the score at 35, UNCLEAR at 55, PARTIAL at 80. Label: 70 and above MATCH, 40 and above PARTIAL, above 0 MISSING, 0 UNCLEAR. A required degree, years of work or skill counts as mandatory; certifications do not yet. **Category weights are not set yet** (open decision).
- **Blind screening.** The matching, evidence, uncertainty and recommendation steps never see the name, email, phone, address, father's name, religion, date of birth or marital status (`BlindCv.Redact`). They judge education, work and skills only.
- **Evidence must be real.** A MATCH or PARTIAL whose quoted evidence is not in the CV drops one level and the case is flagged (`BlindCv.IsGrounded`).
- **Model words are not trusted.** A reason containing SELECTED, REJECTED, HIRE or REJECT is replaced by a plain one. An email draft that shows a score, a rank or a removed ID is thrown away for the template.

## 4. What is NOT in the box

The HRMS team builds or already has these. The "Python reference" column is where the current behaviour is written down; it is the oracle to compare against.

| Missing here | Python reference (`apps/backend/app/`) |
|---|---|
| Database, entities, migrations | `db/models/*.py`, `alembic/versions/` |
| Login, roles, permissions, audit log | `services/auth_service.py`, `services/audit_service.py` |
| CNIC identity: one profile per CNIC, duplicate check, apply history | `services/identity.py`, `services/application_service.py`, `services/history_service.py` |
| Job rules: deadline, city, Sales or non-Sales, test required | `services/job_service.py`, `services/fit_service.py`, `services/department_rules.py` |
| Applications and the careers site endpoints | `api/routes/public.py`, `services/application_service.py` |
| Sending and reading email (SMTP, IMAP, reply agent) | `services/email_service.py`, `services/reply_agent_service.py` |
| Tests taken by candidates, pass or fail, interviews and invites | `services/assessment_service.py`, `services/interview_service.py` |
| Background queue and workers | `services/screening_queue_service.py` |
| Decision Inbox, dashboard numbers, talent pool matching in SQL | `services/talent_pool_service.py`, `services/ranking_service.py` |
| OCR for scanned CVs | `services/extraction_service.py` (`ai_client.ocr_images`) |

## 5. Integrating into HRMS

### Option A: call the host over HTTP (recommended first)

Run `HrOs.Ai.Host` as its own service. HRMS calls it. This keeps HRMS free of the AI libraries and lets the AI service move or scale on its own.

Run it:

```bash
# from the dotnet folder
docker build -t hros-ai-host .
docker run -p 8200:8200 \
  -e Service__ApiKey=<key HRMS will send> \
  -e Ai__ChatUrl=http://<gpu-machine>:8101/v1 \
  -e Ai__EmbedUrl=http://<gpu-machine>:8103/v1 \
  -e Ai__ApiKey=<the vLLM key> \
  hros-ai-host
```

Every endpoint except `/health` and `/openapi/v1.json` needs the header `X-Api-Key: <Service:ApiKey>`. JSON is snake_case, both ways.

| Endpoint | Body | Returns |
|---|---|---|
| `GET /health` | none | `{"status":"ok","ai_available":true}` |
| `POST /cv/extract` | multipart `file` (.pdf .doc .docx .txt, 10 MB) | `{"profile": {...}, "text": "..."}`; 400 with a message for a scanned or empty CV |
| `POST /screening` | `{"cv_text","job_title","job_description","job_requirements","application_id","candidate_id"}` | a `ScreeningReport` (below); 504 on timeout |
| `POST /embeddings/candidate` | a profile as returned by `/cv/extract` | `{"vector":[...]}` or `null` |
| `POST /embeddings/job` | `{"job_requirements": {...}}` | `{"vector":[...]}` or `null` |
| `POST /tests/draft` | `{"job_title","description","requirements","count"}` | `{"questions":[{"text","options","correct_index"}],"errors","model","prompt_version"}` |
| `POST /emails/draft` | `{"email_type","candidate_name","job_title","reason","context","hr_notes","tone","company_name","hr_name"}` | `{"subject","body","model","errors"}` |
| `POST /jobs/draft` | `{"job_title","user_note"}` | `{"description","errors","model","prompt_version"}` |

The screening report:

```json
{
  "recommendation": "PARTIAL",
  "score": 72,
  "reason": "plain sentences: qualification, experience, relevance, then skills",
  "evidence": [ { "requirement": "...", "status": "MATCH", "evidence": "quote from the CV",
                  "is_mandatory": true, "category": "qualification" } ],
  "missing":   [ { "requirement": "SAP", "status": "MISSING", "evidence": null } ],
  "uncertain": [ { "requirement": "End date unclear", "status": "UNCLEAR", "evidence": null } ],
  "requires_hr_review": true,
  "model": "vllm/qwen3:latest",
  "fallback_used": false,
  "fallback_model": null,
  "errors": [],
  "prompt_versions": { "requirement-matching": "requirement-matching-v4" }
}
```

### Option B: use the library in the HRMS process

```csharp
// Program.cs of the HRMS app
builder.Services.AddHrOsAi(builder.Configuration);   // reads the "Ai" section
```

Then inject what you need: `ScreeningService`, `CvExtractionService`, `EmbeddingService`, `TestGeneratorService`, `EmailDraftService`, `JobAssistantService`. They are registered as transient (the CV cache is the one singleton). `AddHrOsAi` registers an `HttpClient` for `IAiClient` and uses `TimeProvider.System` unless you registered another. It needs nothing else from HRMS.

```csharp
var report = await screening.ScreenAsync(
    new ScreeningInput(cvText, "Accounts Officer", jobDescription), ct);
```

Pick A if the HRMS team wants to start fast and keep the AI separate; pick B when HRMS and AI will always deploy together. Code written against the DTOs works with both.

### How a call flows

```
Candidate uploads a CV
  HRMS stores the file
  HRMS -> AI   /cv/extract (file)          -> text + profile
  HRMS stores  the text and the profile
  HRMS queues  a screening job (see section 6)
Worker runs
  HRMS loads   cv text, job title, job description, job requirements
  HRMS -> AI   /screening                  -> report
  HRMS stores  the report, sets status "HR review"
HR opens the application and reads the evidence. HR decides.
```

## 6. The database and how it relates to the AI

### The rule

**The AI code never touches a database.** The caller loads plain data, sends it to the AI, and saves the result. `dotnet/` has no connection string, no EF Core and no SQL. This keeps the AI testable offline and lets HRMS keep its own schema.

### What the caller reads, sends and stores

| AI function | Caller reads from the database | Sends | Gets back | Caller stores it in |
|---|---|---|---|---|
| CV extraction | the uploaded file (or its stored bytes) | file | text and profile | CV document: `extracted_text`; candidate: profile as JSON (`profile_data`) |
| Embedding | the profile | profile | a vector of numbers | candidate: `embedding` |
| Job embedding | the job's requirements | requirements | a vector | computed when matching the pool; can be cached on the job |
| Screening | CV text, job title, description, requirements | `ScreeningBody` | `ScreeningReport` | screening result: recommendation, score, `evidence` (JSON), missing (JSON), uncertainty (JSON), model, prompt versions; then application status and a status-history row |
| Test draft | job title, description, requirements | `TestRequest` | questions | test questions, saved **unapproved**; HR approves or edits |
| Email draft | candidate name, job, reason, HR and company names | `EmailDraftRequest` | subject and body | nothing until HR edits and sends |
| Job draft | title and HR's note | `JobAssistRequest` | description | the job form; HR edits and saves |

### The Python tables behind this (the reference)

| Table | Holds | Used by the AI flow |
|---|---|---|
| `candidates` | one row per person, unique on CNIC (encrypted, shown masked); `profile_data` (JSONB, the AI profile); `embedding` (vector) | CV extraction and embeddings write here |
| `applications` | candidate, job, `status` (APPLIED, PROCESSING, HR_REVIEW, SHORTLISTED, ... ) | screening moves it to HR_REVIEW |
| `cv_documents` | file path, hash, `extracted_text`, `extraction_status` | the text sent to screening |
| `screening_results` | `recommendation`, `score`, `evidence` (JSONB: items + reason), `missing_requirements`, `uncertainty` (JSONB: items + requires_hr_review), `model`, `hr_decision` (PENDING until HR decides) | screening writes here |
| `screening_queue` | `action`, `source`, `status`, `error_message`, times | background screening and its progress |
| `application_status_history` | from, to, who, why | every status change |
| `jobs` | `title`, `description`, `requirements` (JSONB), city, role type, deadline, `test_required` | the job side of screening |
| `assessments` and related | tests and results | test drafts, tests taken |
| `audit_log` | who did what | every HR action |

### Screening, step by step

```
screening_queue row (QUEUED)  ->  worker marks PROCESSING and commits
 -> loads application, cv_documents.extracted_text, job
 -> calls the AI: ScreenAsync(cv text, job title, description + requirements)
 -> ScreeningResult row: score, evidence JSON, missing JSON, uncertainty JSON, model
 -> application.status = HR_REVIEW, status_history row
 -> queue row COMPLETED (or FAILED with the error)
```

Why a queue: a screening takes up to six minutes on a local model. It must not run inside the web request, and it must survive a restart. The queue row is saved before the long call starts, so the dashboard can show "Screening 12 CVs" live. In HRMS use a hosted service (`BackgroundService`) or Hangfire, with the queue row in the HRMS database.

What HR sees comes from the JSON: each requirement with its status, the quoted evidence, whether it was mandatory, and its category; the missing list; the uncertain list; and the flag `requires_hr_review`. Keep `prompt_versions` and `model` with the result, so a score can be explained later.

### Embeddings and the talent pool

When a job is published, candidates in the pool whose city and role match are ranked by how close their embedding is to the job's. In the Python app this is done in SQL with pgvector (`1 - cosine_distance`). The .NET `PoolSimilarity` does the same sum in memory, and adds a skills-overlap score, if you do not have a vector column.

### Suggested HRMS data model

A sketch to translate into your own conventions (EF Core entities and your own migrations), not a migration:

| Table | Columns the AI output needs |
|---|---|
| `candidate` | id; full_name; email; phone; city; `cnic_encrypted`; `cnic_hash` (unique); `profile_json` (jsonb); `embedding` |
| `application` | id; candidate_id; job_id; status; created_at |
| `cv_document` | id; application_id; file_path; file_hash; `extracted_text`; extraction_status |
| `screening_result` | id; application_id; recommendation; score; `evidence_json`; `missing_json`; `uncertainty_json`; requires_hr_review; model; prompt_versions_json; hr_decision; created_at |
| `screening_queue` | id; application_id; action; status; error; queued_at; started_at; completed_at |
| `application_status_history` | id; application_id; from_status; to_status; changed_by; reason; created_at |
| `apply_history` | candidate_id; job_id; event (applied, tested, interviewed); at; one row per event |

Notes:

- **CNIC** is the unique key for a person. Store it encrypted, show it masked (`35202-*******-1`), and give the full number only to a user with that permission. **Never pass it to the AI service.** `/cv/extract` and `/screening` take no CNIC, and the library removes any that appears in text, but do not rely on that: leave it out.
- **Do not send** a CNIC, a photo, or fields the AI does not need. The screening body is `cv_text`, job title, job description and job requirements only.
- **JSON columns** for `evidence`, `missing`, `uncertainty` and `profile`: the shapes change with prompts and should not need a migration each time.
- **Soft delete** (`deleted_at`) is used for candidates and applications in the Python app.

### The HRMS database

The HRMS database is **PostgreSQL 16**: the compose stack `internal-app` runs `postgres:16-alpine` on port 5433. That image does **not** include the `pgvector` extension. So:

- `jsonb` columns work as they are.
- A `vector` column does not until you install the extension (use the `pgvector/pgvector:pg16` image, then `CREATE EXTENSION vector;`). This is the recommended path when the talent pool is built, because it keeps the match in SQL.
- Without it, store the vector as `real[]` (or `bytea`) and use `PoolSimilarity` in .NET. Fine for a few thousand candidates; it loads vectors into memory.

*This is read from the running containers; confirm it with the HRMS team before building on it.*

## 7. Configuration

Settings are read from `appsettings.json` or environment variables (a double underscore replaces the colon: `Ai__ChatUrl`).

| Key | Default | Meaning |
|---|---|---|
| `Service:ApiKey` | empty | The key HRMS must send as `X-Api-Key`. Empty means no check (development only). |
| `Service:MaxCvMegabytes` | 10 | Largest CV upload. |
| `Ai:ChatUrl` | `http://localhost:8101/v1` | The main chat model server. |
| `Ai:EmbedUrl` | `http://localhost:8103/v1` | The embedding server. |
| `Ai:EmailUrl` | empty | Optional small email model server. Empty means emails use the chat server. |
| `Ai:ApiKey` | empty | Bearer key for vLLM servers on another machine. Never logged. |
| `Ai:ChatModel` | `qwen3:latest` | The model name each server was started with (`--served-model-name`). |
| `Ai:ProfileModel` | empty | Optional faster model for CV reading. Empty means `ChatModel`. |
| `Ai:EvaluationModel` | `qwen3:latest` | The model that screens. |
| `Ai:FallbackEvaluationModel` | empty | Optional second opinion, used only when a screening is uncertain. Must be a model that is actually served. |
| `Ai:EmailModel` | `qwen3:1.7b` | The email model. |
| `Ai:EmbeddingModel` | `all-minilm` | The embedding model. |
| `Ai:ChatTimeoutSeconds`, `Ai:EmbedTimeoutSeconds` | 60 | Per-call time limits. |
| `Ai:MaxInputChars` | 48000 | Longest prompt text; longer is cut. |
| `Ai:ScreeningTimeoutSeconds` | 360 | Whole-screening limit. |
| `Ai:ProfileCacheSize`, `Ai:ProfileCacheMinutes` | 128, 60 | CV extraction cache. |

Example for a host that reaches the models by name (testing; fake CVs only):

```
Service__ApiKey=<host key given to the HRMS developers>
Ai__ChatUrl=https://hr-chat.nprevyol.online/v1
Ai__EmbedUrl=https://hr-embed.nprevyol.online/v1
Ai__EmailUrl=https://hr-email.nprevyol.online/v1
Ai__ApiKey=<vLLM key, never given to developers>
```

The two keys must differ. The tunnel setup and the checks are in `docs/deploy.md` (section 7) of the HR OS repository.

The models themselves are started separately (vLLM, one server per model). See `docs/vllm.md` and `docs/deploy.md` in the HR OS repository.

## 8. Prompts

- The prompts are plain text files in `src/HrOs.Ai/Prompts/text/`, embedded in the assembly at build time. They belong to this service. The Python reference reads the same folder, so the two cannot drift. `cv_profile_extraction.txt` (in `src/HrOs.Ai/Prompts/`) is used only by the form-filling step.
- `PromptLibrary.Get(name)` returns the prompt followed by the shared safety block (`_guardrails.txt`): the text between tags is untrusted data; never use or infer protected attributes; never decide to hire or reject; do not guess; answer with the JSON only.
- To change a prompt: edit the `.txt`, bump its version in `PromptVersions` (or the service's `PromptVersion`), update the tests that name the version, and run the tests. The version is saved with every result.
- `dotnet/` is self-contained: it can be moved to its own repository or into HRMS as it is, with its prompts.
- Treat the prompts as reviewed code. They are the part of the system most likely to bias a result; see `BlindCv` and `GuardrailTests` for what is enforced in code and not left to the model.

## 9. When the AI is down

Nothing breaks and nobody is rejected.

| Step | Fallback |
|---|---|
| CV extraction | Plain text rules read the contact details, skills and degree words. |
| Job requirements | One requirement made from the job title. |
| Matching | Word overlap between the requirement and the CV. |
| Evidence, uncertainty | Simple rules from the matching result. |
| Recommendation | The score from the rules, with a plain reason. |
| Email | A template. |
| Tests, job drafts | No questions or no draft, and `errors` says so; HR writes by hand. |

Any fallback adds an entry to `errors` and sets `requires_hr_review`. `/health` reports `ai_available`. HRMS should show a banner when it is false.

## 10. Testing

```bash
dotnet test dotnet/HrOs.Ai.slnx        # 168 tests, no model, no network
```

- `ScriptedAi` returns fixed answers in order; `RoutedAi` answers by which prompt is being sent (match on a phrase from the system prompt); `FakeModelServer` is a real local HTTP server for the client tests; `HostFactory` runs the host in memory.
- To add a screening step: add a node in `ScreeningNodes.cs`, a prompt in `src/HrOs.Ai/Prompts/text/`, a fixture answer in `RoutedAi`, and a test in `Screening/`.
- A **live check against a real vLLM** has not been run yet. Do it once the model servers are up: start the host, send one real CV to `/cv/extract`, then `/screening`.

## 11. Known gaps and open decisions

- **Never run against a real vLLM.** Only mocks and a fake server so far. JSON-schema output mode (a vLLM option that forces valid JSON) is not wired in.
- **Prompts are new and untested on the real model** (Qwen3-8B). A golden set of test CVs and "swap the name, gender or religion; the score must not change" pairs is planned, and needs a GPU.
- **Category weights** (how much qualification, experience and relevance count against skills and certifications) are not set. One place to change in code, and the Python judge (`app/evaluation/basic_judges.py`) has a copy of the score rule that must change with it.
- **OCR is not ported.** A scanned CV is refused by `/cv/extract`; the Python app has OCR (DeepSeek-OCR). Until it is ported, a scanned CV goes to the manual form.
- **Embedding size.** The Python column is `Vector(1536)`, but `all-MiniLM-L6-v2` produces 384 numbers. This looks like a mismatch (a 384-number vector cannot be saved in a 1536 column); I could not check it against a live database, because the HR OS database was not running when this was written. Use the model's real size when you create the HRMS column, and confirm the Python side.
- **Parity with Python** is not checked automatically. Python is the reference; run the same CVs through both before relying on .NET.
- **Open product questions** from `CLAUDE.md` still apply: fit thresholds per role, test pass mark, and how to handle candidates without a CNIC.
