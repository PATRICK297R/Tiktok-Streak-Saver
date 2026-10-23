// Every outbound link the showcase points at, in one place.
//
// These used to point at the upstream repository. They now point at this fork,
// which is where the APK is actually published - a visitor who clicked the old
// download button would have installed someone else's build.
export const REPO_OWNER = 'PATRICK297R'
export const REPO_NAME = 'Tiktok-Streak-Saver'

export const REPO_URL = `https://github.com/${REPO_OWNER}/${REPO_NAME}`
export const APK_URL = `${REPO_URL}/releases/latest/download/StreakSaver.apk`
export const RELEASES_URL = `${REPO_URL}/releases/latest`
export const PRIVACY_URL = `${REPO_URL}/blob/main/PRIVACY.md`
export const ISSUES_URL = `${REPO_URL}/issues`
export const LICENSE_URL = `${REPO_URL}/blob/main/LICENSE`
