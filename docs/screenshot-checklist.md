# Screenshot checklist

Status: **current**. All images in `src/Web/public/imgs/` were re-captured from a real
device (OPPO CPH2591, 720x1612) on **2026-10-24**, against app version 2.5.5.

| Image | Screen | Status |
| --- | --- | --- |
| `Dashboard.jpeg` | Dashboard after a completed run (8/8, 100%) | Current |
| `Dashboard_Live.jpeg` | Dashboard mid-run (3/8, Stop button visible) | Current |
| `Friends.jpeg` | Friends list with per-chat health dots | Current |
| `History.jpeg` | History with the success-rate chart | Current |
| `Profile.jpeg` | Profile settings, showing two daily run times | Current |
| `Import.jpeg` | Import from chats, review list | Current |

## Removed

`Burst_Main.jpeg`, `Burst_Secondary.jpeg`, `Main_Secondary.jpeg`, `Welcome.jpeg`,
`Settings.jpeg` and `Update_Screen.jpeg` were deleted. Burst mode was dropped when the
multi-page UI was ported in (`dc2469e`), so those two advertised a feature that no longer
exists; the rest showed the old single-page layout.

## Still worth capturing

These would add to the gallery but are not required for it to be accurate:

- **Login** — the TikTok WebView before signing in. Needs a logged-out device.
- **Update dialog** — the in-app prompt with its changelog.
- **Streak risk alert** — the notification, or the Profile toggle.
- **Backup & Restore** — the Profile card with a restore result line.
- **Arranging mode** — the `Reorder` view with its grab handles and Save order button.

## Capturing

Use a device at a normal phone resolution. Crop the Android status bar and resize to
**738x1600**, which is what the current set uses, then save as JPEG (quality ~88) into
`src/Web/public/imgs/`.

Update `src/Web/src/data/screenshots.ts` for the gallery and the "how it works" steps, and
`HeroSection.vue` for the hero image.
