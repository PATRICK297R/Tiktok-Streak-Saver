export interface Screenshot {
  /** File under `public/imgs/`. */
  src: string
  alt: string
  caption: string
}

// Build-time base URL injected by Vite (e.g. `/TiktokStreakSaver/`) so deployed
// links resolve correctly under the GitHub Pages project path.
const base = `${import.meta.env.BASE_URL}imgs/`

// All captured from a real device at 720x1612 (status bar cropped, 738x1600 output).
export const screenshots: Screenshot[] = [
  {
    src: base + 'Dashboard.jpeg',
    alt: 'Dashboard showing a finished run',
    caption: 'Daily progress, 8 of 8 sent'
  },
  {
    src: base + 'Dashboard_Live.jpeg',
    alt: 'Dashboard during a run',
    caption: 'Fills in live, chat by chat'
  },
  {
    src: base + 'Friends.jpeg',
    alt: 'Friend list with per-chat health',
    caption: 'Per-chat health at a glance'
  },
  {
    src: base + 'History.jpeg',
    alt: 'History with success rate',
    caption: 'Success rate and every run'
  },
  {
    src: base + 'Profile.jpeg',
    alt: 'Profile settings',
    caption: 'Schedule, retries, battery rule'
  },
  {
    src: base + 'Import.jpeg',
    alt: 'Import from chats',
    caption: 'Import everyone you already chat with'
  }
]

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
    image: base + 'Dashboard.jpeg',
    alt: 'Streak Saver dashboard'
  },
  {
    number: 2,
    title: 'Add your friends',
    description: 'Import everyone you already chat with, or add usernames by hand. Toggle individuals on or off any time.',
    image: base + 'Import.jpeg',
    alt: 'Import from chats screen'
  },
  {
    number: 3,
    title: 'Pick a schedule',
    description: 'Run every N hours, or at several fixed times a day. Configure retries and the battery rule.',
    image: base + 'Profile.jpeg',
    alt: 'Profile settings screen'
  },
  {
    number: 4,
    title: 'Streaks send themselves',
    description: 'A foreground service quietly opens TikTok and sends each message. Watch it fill in, or just read the notification.',
    image: base + 'Dashboard_Live.jpeg',
    alt: 'Dashboard during an automated run'
  }
]
