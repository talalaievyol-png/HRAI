# HR OS AI service (.NET)

The AI part of HR OS recruitment: reads CVs, screens candidates against a job, and drafts tests, emails and job descriptions. It talks to local vLLM servers and has no database.

- **Developer guide** (what is here, how to integrate it into HRMS, how it relates to the database): [docs/DEVELOPER-GUIDE.md](docs/DEVELOPER-GUIDE.md)
- **Tests** (no model needed): `dotnet test HrOs.Ai.slnx`
- **Run the host**: `dotnet run --project src/HrOs.Ai.Host` (listens on `http://127.0.0.1:8200`; set `Ai__ChatUrl` and `Ai__EmbedUrl` to your vLLM servers)
- **Docker**: from this folder, `docker build -t hros-ai-host .`
- **Prompts**: `src/HrOs.Ai/Prompts/text/` (embedded in the build; the Python reference reads the same files)
