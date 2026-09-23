# Kickoff prompt for Claude Code

Open Claude Code in this repo and paste everything below the line as the first message.

---

Read SPEC.md and CLAUDE.md fully before writing any code. Build the MVP exactly as specified, working through Phases 1 to 7 in order without stopping for approval.

Rules:
- Every row in "Decisions already made" is final. Do not ask about stack, structure, or scope.
- When you need something only I can provide (a secret, a DNS record, the ACS quota, a seed inbox, Azure resources), write the exact ask to BLOCKERS.md, use the in-memory IEmailSender and a local SQL database, and continue with the next task that is not blocked.
- If `az` is logged in, create the Azure resources from infra/main.bicep yourself; otherwise list the resource names and settings I must create in BLOCKERS.md.
- Commit at the end of each phase with tests green. Commit message: "Phase N: <what works now>".
- After each phase, update PROGRESS.md with what is done, what is verified in Azure, and what is waiting on me.
- Prefer the simplest implementation that meets the acceptance criteria. No extra abstractions and nothing from "Out of scope".
- If a library named in the spec is unavailable or broken, use the named fallback and note it in PROGRESS.md.
- Never put a secret in the repo, a log line, or a test fixture.

Start with Phase 1. Report back only when Phase 1's acceptance criteria are met or you are fully blocked.
