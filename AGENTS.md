# Shared workspace: check files out, commit only your own

On the maintainer's machine this repository is a **shared** working tree. Several AI agents —
Claude Code sessions, Codex, and others — work in the same checkout at the same time, coordinated
by a local tool called huddle, and none of them can see the others. Source files, the Git index,
and build outputs such as `bin/`, `obj/` and `dist/` are all shared. Your task is not the only
work in progress.

If you cloned this repository yourself and huddle is not installed, none of this applies: work
normally.

Two agents editing one file corrupts it or throws away someone's work. Git cannot help, because
the collision happens in the working tree before any commit exists, so there is no second version
for it to merge and the losing edit simply disappears.

So the files are a library. A file is either **available** or **checked out**, and you check out
the ones you are about to edit — every one: code, tests, docs, and this file too.

## Three commands

**1. See what is out.** Needs nothing — no identity, no setup:

```
huddle --catalog
```

**2. Check out what you are about to edit,** before your first edit. Paths are relative to the
folder you are running in, the way you would name them to git. `--as` is any stable name you pick
for yourself:

```
huddle --checkout --as codex:refactor README.md
```

It either succeeds or tells you who holds the file and until when. **A refusal means do not edit
that file** — pick different work, or wait for the due date to lapse. Nothing arbitrates this for
you. A set is all-or-nothing, so if one file is held you get none of them.

**3. Check in when you have committed:**

```
huddle --checkin --as codex:refactor --all
```

`--all` returns everything you hold, wherever you checked it out.

## Working alongside the others

- **Commit only your own changes, with explicit paths:** `git add <path> ...`. Never `git add -A`
  or `git add .` — the index is shared, and a sweep commits other agents' unfinished work under
  your name. Never stage, revert or clean another agent's changes.
- **Do not build unless you were asked to.** A build, clean or publish — including
  `build-truedat.cmd`, and a test command that compiles — overwrites shared outputs another agent
  may be using and can invalidate their results. A checkout reserves a file for editing; it does
  not reserve the build outputs. If you did not build, say "edited but not built" and state what
  you did check.

## The things worth knowing

- **Where huddle is.** If `huddle` is not on your PATH, it is `huddle.exe` in the `publish` folder
  of the huddle install on this machine. It finds the shared ledger by itself; there is nothing to
  configure and no environment to set.
- **Checkouts expire.** Default an hour. Run the same `--checkout` again to renew before the due
  date, or `huddle --catalog --renew --as <name>` to extend everything you hold. This is why a
  crashed agent does not lock a file forever — and why your own checkout can lapse under you on a
  long task.
- **Say who you are, the same way each time.** The name is your identity: it is how renewal,
  check-in and "this is mine" all work. A different name each run means you cannot return your own
  books.
- **One file:** `huddle --status <path>` says available, or who has it and whether it has changed
  since they took it.
- **Only yours:** `huddle --catalog --mine --as <name>`. **Late ones:** `huddle --catalog --overdue`.
- **If the commands fail**, say plainly in your next message that you could not check out, before
  you edit anything.

## Everything else about this project

This file covers one thing: working in a shared tree without colliding with the other agents. For
what Truedat is, how to build it, and its conventions, read `README.md` and `CLAUDE.md` in this
folder.
