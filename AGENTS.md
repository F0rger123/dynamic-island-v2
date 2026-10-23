# AI guide: dynamic-island-v2

**For AI assistants (Claude, ChatGPT, Codex, Cursor, Gemini).** Someone gave you a link to this repository. Use this page to explain it, set it up, or help them use it. Talk in plain English; the person may not be technical.

## What this is

A macOS-style Dynamic Island for Windows: a little pill at the top of the screen showing music, your next calendar event, AI coding agents and the weather.

**Usually used by:** A Windows user who wants to install it.

## Ask first

Ask these together in one short message, then repeat back what you heard in a line or two. Skip anything they already told you.

- Which Windows version?
- Do you have Windhawk installed?
- Which extras: Google Calendar, Claude Code, Codex, Gemini?

## Set it up

Do one step at a time. Check each one worked before moving on.

| # | Do this | It worked if |
|---|---|---|
| 1 | Install Windhawk from windhawk.net | Windhawk opens |
| 2 | Download the latest `DynamicIslandBridge-win-x64` from the repo's Actions or Releases and unzip it to a folder you'll keep | The folder has `DynamicIslandBridge.exe` and `sourcecode` |
| 3 | Run `Setup.exe` (or `DynamicIslandBridge.exe --setup`) and follow the wizard | The wizard's system check is green |
| 4 | In Windhawk: Create new mod, paste the contents of `sourcecode`, Compile, enable | The pill appears at the top of the screen |

## Use it

- Hover or click the pill to expand it; swipe between pages.
- Reopen setup any time with `DynamicIslandBridge.exe --setup`.

## How it works, in plain words

1. The pill is a Windhawk mod.
2. A small helper app talks to Google Calendar and the AI agents, privately on your PC.

## Never

- Never ask for, paste or print passwords, API keys or tokens. Tell the person where to put them (their own `.env` file or the hosting dashboard) and let them do it.
- Never deploy, push to `main`, change a live database or spend money without the person saying yes to that exact step.

## Read next

- [Full install guide](README.md)

## When you're done

Tell the person, in a few lines: what is set up and checked, what isn't yet, and two or three things they can ask you next.
