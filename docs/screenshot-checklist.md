# Screenshot checklist

The screenshots used by the README and the project website are **out of date**. Every image currently
in `docs/imgs/` and `src/Web/public/imgs/` was captured on or before **2026-05-01**, and the
multi-page UI rewrite landed on **2026-05-11**.

| Image | Captured | Status |
| --- | --- | --- |
| `main_screen.png` | 2026-04-19 | Out of date — shows the old single-page layout |
| `Welcome.jpeg` | 2026-05-01 | Out of date — layout changed |
| `Main_Secondary.jpeg` | 2026-05-01 | Out of date — layout changed |
| `Settings.jpeg` | 2026-05-01 | Out of date — layout changed |
| `Update_Screen.jpeg` | 2026-05-01 | Out of date — layout changed |
| `Burst_Main.jpeg` | 2026-05-01 | **Wrong** — burst mode was removed |
| `Burst_Secondary.jpeg` | 2026-05-01 | **Wrong** — burst mode was removed |

## Why the burst images are wrong, not merely old

Burst mode was deliberately dropped when the multi-page UI was ported in:

```
dc2469e  Port multi-page UI from eulfn/streak-tiktok fork; drop burst mode and Windows TFM
```

There is no burst code left in the repository, yet the website kept advertising it with captions like
*"Run streaks in seconds"*. Their references were removed from
[`src/Web/src/data/screenshots.ts`](../src/Web/src/data/screenshots.ts); the image files were left on
disk so the history stays intact.

## Screens to capture

The app is now organised as **Dashboard / Friends / History / Profile**, so the current set needs
replacing with:

1. **Login** — the TikTok WebView before signing in.
2. **Dashboard** — the *Daily Automation Progress* card, with the progress bar and the
   Sent Today / Success / Remaining figures.
3. **Dashboard, run in progress** — the stop button and the progress notification.
4. **Friends** — the list with enable toggles and the add field, plus the **Send order** card with
   its `Reorder` view open, which is where entries are moved up and down the queue.
5. **History** — the success-rate chart plus a run row (ideally one showing the `Test run` label).
6. **Profile** — the settings: schedule, interval, streak risk alerts, test run, battery toggle.
7. **Update dialog** — the in-app update prompt.
8. **Import from your chats** — the scanner reading the TikTok inbox, and the review list it offers:
   the rows with their tick boxes, and a row opened for correction so the name, the username and the
   person-or-group choice are all visible.
9. **Backup & Restore** — the Profile card, ideally with the result line showing what a restore added.

Screenshots that show a feature added recently are worth including:

- **Test run** — the History row labelled *Test run — nothing was sent*.
- **Streak risk alert** — the notification, or the Profile toggle for it.

## Capturing

Use a device or emulator at a normal phone resolution, in the theme the marketing images use. Save into
both `docs/imgs/` and `src/Web/public/imgs/`, then update:

- `README.md` — the image near the top
- `src/Web/src/data/screenshots.ts` — the gallery and the "how it works" steps

Once the images are replaced, delete the two `Burst_*.jpeg` files and the staleness note in
`screenshots.ts`.
