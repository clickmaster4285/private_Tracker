# Shared shift PC — three Windows accounts

One PC, three shifts, one Windows account per shift. Each account carries one employee's
tracker login. No auto-logon, no scheduled shift switching, no restarts.

| | Morning | Evening | Night |
|---|---|---|---|
| Windows account (Standard user) | `Shift-Morning` | `Shift-Evening` | `Shift-Night` |
| Shift | 06:00–14:00 | 14:00–22:00 | 22:00–06:00 |
| Tracker login | Employee A | Employee B | Employee C |
| Face enrolled | A | B | C |
| Tracker data | `C:\Users\Shift-Morning\AppData\Local\AlphaAITracker` | `…\Shift-Evening\…` | `…\Shift-Night\…` |

## Daily flow

1. Employee picks their shift account on the Windows sign-in screen.
2. The tracker starts in that session and loads that account's saved employee.
3. Face verification runs full-screen. Wrong face → stays locked (retry or escalate).
4. Verified → monitoring starts. **Attendance arrival = the verified face time**, not the
   Windows sign-in time (web Attendance: `arrivalSource = face_verified`).
5. End of shift: Start → profile → **Sign out**. Night shift stays one record across midnight.

## What the product enforces (Alpha AI Tracker 3.0.7+)

| Rule | How |
|---|---|
| Tracker runs in the signed-in user's session | Run key per user (`client.exe --background`) |
| DLP keeps enforcing with the tracker closed | `AlphaAITracker.Service` runs as LocalSystem |
| Employee bound to one Windows account | Login sends the Windows SID; the server refuses the same employee from a different account on the same PC (`409`). An admin releases it with **Employees → Remove Current Device**. |
| "Switch user" available | Shown by default, so a forgotten sign-out never leaves only one account on the lock screen. Set `ALPHA_DLP_HIDE_SWITCH_USER=true` in `service.env` to hide it again (prior value restored on pause / uninstall). |
| One employee session at a time | If someone works at the PC and another user's session stays disconnected (lock screen → "Other user") longer than `ALPHA_DLP_SINGLE_SESSION_GRACE_SEC` (120 s), the DLP service signs that session out. Never touches the active session. Exempt IT accounts with `ALPHA_DLP_SINGLE_SESSION_EXEMPT_USERS=itadmin;PC\other`. Switch off with `ALPHA_DLP_SINGLE_SESSION=false`. |
| Windows lock → verify again | Tracker relaunches face verification on session unlock |
| Offline / power loss | Events queue in the account's local SQLite and sync later |
| Startup apps | All off except the tracker and Microsoft Defender |

### Known limit — read before production

The face screen is a full-screen window inside the user's session, not a Windows
credential provider. It is hardened (topmost, apps killed, relaunch supervisor, AppLocker
blocks Task Manager / cmd / PowerShell by default) but it is **not** equivalent to the
Windows secure desktop. A credential provider (face check before the session opens) is a
separate native component and is not part of this setup.

## One-time setup (per PC, as Administrator)

1. Install Alpha AI Tracker 3.0.7+ (the DLP service is installed with it).
2. Run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\setup-shift-accounts.ps1
   ```
   You are asked for a password per account. `-AllowBlankPassword` creates passwordless
   accounts — then anyone at the PC can open any shift account, and Windows blocks them from
   network sign-in.
3. Restart. The three accounts are on the sign-in screen.
4. **Per account:** sign in → open Alpha AI Tracker → enter that shift's Employee ID and
   secret key → complete face verification → confirm the dashboard shows the right
   employee → sign out.
5. Web: **Employees → Enroll Face** for each employee — the face must belong to the employee
   whose ID was entered in that account.
6. Run the checks:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\verify-shift-setup.ps1
   ```

## Acceptance checklist

| # | Check | How to test | Script |
|---|---|---|---|
| 1 | Three accounts at startup | Sign-in screen | ✔ |
| 2 | Each account loads its own Employee ID | Sign in to each; tracker shows the right name | ✔ (data folder) |
| 3 | Each account keeps its own saved login | Restart; tracker resumes without a login prompt | — |
| 4 | Correct employee passes face verification | Employee A on `Shift-Morning` | manual |
| 5 | Wrong employee cannot reach the desktop | Employee B's face on `Shift-Morning` | manual |
| 6 | Lock requires re-verification | Win+L → unlock → face screen appears | manual |
| 7 | Sign out stops the tracker cleanly | Sign out; `quser` shows no session for that account | ✔ |
| 8 | Switch user available | Lock screen shows the other accounts / "Switch user" | ✔ |
| 9 | Only one employee session | Lock → "Other user" → sign in another shift; after 2 min the first session is signed out (`quser`) | ✔ |
| 10 | Same employee from another account refused | Log Employee A in on `Shift-Evening` → "already signed in from Windows account …" | manual |
| 11 | DLP active with the tracker closed | End `client.exe`; a blocked site/app stays blocked | ✔ (service) |
| 12 | Offline events survive sign-out / restart | Unplug network, work, sign out, restart, reconnect; events appear | manual |
| 13 | Attendance = verified arrival | Sign in 08:50, verify 09:05 → arrival 09:05 | manual |
| 14 | Night shift across midnight is one record | 22:00–06:00 shows one work date | manual |
| 15 | No access to another employee's local data | Profiles private to their account | ✔ |
