export interface Screenshot {
  /** File under `public/imgs/`. */
  src: string
  alt: string
  caption: string
}

// Build-time base URL injected by Vite (e.g. `/TiktokStreakSaver/`) so deployed
// links resolve correctly under the GitHub Pages project path.
const base = `${import.meta.env.BASE_URL}imgs/`

export const screenshots: Screenshot[] = [
  { src: base + 'Welcome.jpeg', alt: 'Welcome / login screen', caption: 'One-tap TikTok login' },
  { src: base + 'Main_Secondary.jpeg', alt: 'Dashboard', caption: 'Daily progress at a glance' },
  { src: base + 'Settings.jpeg', alt: 'Settings', caption: 'Schedule, retries, battery toggle' },
  { src: base + 'Update_Screen.jpeg', alt: 'Update screen', caption: 'In-app update checks' }
]

/**
 * NOTE ON STALENESS — these images predate the multi-page UI rewrite (2026-05-11).
 *
 * Two entries were removed because they advertised a feature that no longer exists: burst mode was
 * deliberately dropped when the multi-page UI was ported in, yet the site kept showing
 * `Burst_Main.jpeg` / `Burst_Secondary.jpeg` with captions about "run streaks in seconds". Anyone
 * installing the app and looking for burst mode would have found nothing.
 *
 * The four remaining images are still from the old single-page layout, so they are out of date rather
 * than wrong: the features they show all still exist, but the navigation around them changed to
 * Dashboard / Friends / History / Profile. They need re-capturing from a real device — see
 * `docs/screenshot-checklist.md` for exactly which screens are missing.
 */

export interface HowStep {
  number: number
  title: string
  description: string
  image: string
  alt: string
}

export const howItWorks: HowStep[] = [
  {
    number: 1,
    title: 'Log in to TikTok',
    description: 'Sign in once inside the in-app WebView. Cookies stay on your device.',
    image: base + 'Welcome.jpeg',
    alt: 'Welcome screen with login button'
  },
  {
    number: 2,
    title: 'Add your friends',
    description: 'Drop in usernames or group names. Toggle individuals on or off any time.',
    image: base + 'Main_Secondary.jpeg',
    alt: 'Friends management screen'
  },
  {
    number: 3,
    title: 'Pick a schedule',
    description: 'Run every N hours, or at a fixed daily time. Configure retries and the battery rule.',
    image: base + 'Settings.jpeg',
    alt: 'Settings screen'
  },
  {
    number: 4,
    title: 'Streaks send themselves',
    description: 'A foreground service quietly opens TikTok and sends each message. You just check the notification.',
    // Was Burst_Main.jpeg, which showed the since-removed burst mode.
    image: base + 'Main_Secondary.jpeg',
    alt: 'Dashboard after an automated run'
  }
]
