# BuffStrap website

This is a static site: `index.html` lives in the repository root, and the styles, scripts, fonts, and artwork live in `site/`. Deploy both together; there is no build step or server-side dependency.

`alpha/index.html` is the pre-release page served at `/alpha`. It lists the newest GitHub pre-release and downloads its `BuffStrap-alpha-win-x64.zip` test build; it never replaces the stable download.

The download buttons and PowerShell command use the latest stable `BuffStrapSetup.exe` release asset. Release version and download totals are loaded from the public GitHub API when available; if the API is blocked or rate-limited, the page falls back to links to the releases page. No GitHub star count is displayed.

Scripts are split so both pages share behaviour: `ui.js` (theme, copy button, mobile menu, GitHub helper), `main.js` (stable release badge and download totals) and `alpha.js` (pre-release details). `theme.js` runs before the stylesheet to avoid a theme flash.

BuffStrap's existing logo and wordmarks are included here for a self-contained deployment. The Inter font is distributed under the license in `fonts/INTER-LICENSE.txt`.
