# galaxies-web

The player-facing surface of Galaxies: the marketing site now, the browser
client later. It is static Firebase Hosting on GCP project `roybot`.

This is the one Galaxies service with a `cloudbuild.yaml` and no `Dockerfile`.
There is no port, no Cloud Run service, and no image to push. If you find
yourself writing a Dockerfile in this directory, you have wandered into the
wrong service; the containers live in `Api/`, `ServerHost/`, and the workers.

## What is actually here

The hosting and build configuration, and the marketing site it serves.
`marketing/` is the Hosting public root, so everything under it ships.

| File | What it does |
|---|---|
| `firebase.json` | Hosting config: the security headers, the three page rewrites, and the cache policy. |
| `.firebaserc` | Points the Firebase CLI at project `roybot` by default. |
| `cloudbuild.yaml` | Stamps `runtime-config.json`, stamps the API origin into the CSP, then deploys. |
| `marketing/index.html` | The marketing page, served at `/`. Also the only consumer of `runtime-config.json`: it reads the flags at load and decides what renders. |
| `marketing/support.html` | Served at `/support` through the rewrite. |
| `marketing/privacy.html` | Served at `/privacy` through the rewrite. |
| `marketing/status.html` | Served at `/status` through the rewrite. |
| `marketing/styles/tokens.css` | The Vigil tokens. Every color, type step, and spacing value the site uses starts here. |
| `marketing/styles/site.css` | The site layer built on those tokens, including the `@font-face` rules. |
| `marketing/runtime-config.json.tmpl` | The template the build stamps from the `_WEB_*` substitutions. |
| `marketing/runtime-config.json` | The checked-in all-off default, so a local preview is always the dark site. |
| `marketing/ads.txt` | AdSense ownership record. Placeholder publisher id, deliberately. |
| `marketing/downloads/appcast.json` | Desktop auto-update feed. Placeholder release, deliberately. |

## What is not here, and what is broken

Saying this plainly is cheaper than letting you find out by deploying.

- **The inline bootstrap in `index.html` violates our own CSP.** The flag reader
  at `index.html:564` is an inline `<script>`, and `script-src` allows `'self'`
  with no `'unsafe-inline'` and no hash. On any real deploy the browser refuses
  to run it, so the flags never load and the page stays in its all-off state.
  The AdSense loader lives inside that same block, which means the ads flip
  cannot work either until this is fixed. The emulator does not send the headers
  block, so a local pass looks perfectly healthy; this only shows up on a preview
  channel. The block even carries a comment predicting this. The fix is to move
  it into a small JS file served from the site itself, or to add its hash to
  `script-src`. Do not reach for `'unsafe-inline'`.
- **There is no `assets/` directory, so the fonts 404.** `index.html` preloads
  three woff2 files out of `assets/fonts/`, and `site.css` has the matching
  `@font-face` rules, but the binaries are not committed and no build step
  fetches them. Every deploy today, and every local preview, falls back to the
  Georgia, system-ui, and ui-monospace stacks in `tokens.css`. That degrades
  cleanly, so it is a papercut rather than an outage, but it is not fixed.
- **Two links in `index.html` point at files that do not exist.** `LICENSE.txt`
  and `account-deletion.html` both 404 today. The privacy page also links
  `/account/delete`, which is an API route that galaxies-api does not serve yet.
- **The status page points at a placeholder host.** `status.html` links
  `https://status.galaxies.example`, which cannot resolve. Same class of
  placeholder as `ads.txt` and `appcast.json`, and it needs the same pass before
  the site goes live.
- **There is no browser client.** `browser-client/` is M7. `/play` is not
  configured here, and it should not be until there is something to route to.
- **The desktop installer is not published.** `appcast.json` describes a release
  that does not exist and points at a host that cannot resolve.

The configuration and the marketing site are both real. What is missing is the
font binaries, the browser client, and anything behind the download links. What
is broken is the inline bootstrap, and it is broken on every deploy, not just
the ones that flip a switch.

## Build and preview locally

Local preview never touches GCP. It reads the checked-in
`marketing/runtime-config.json`, which is all off, so what you see locally is
the dark site.

```bash
npm install -g firebase-tools     # or use npx, as the build does
cd galaxies-web
firebase emulators:start --only hosting
```

That serves on `http://localhost:5000`. The emulator applies the rewrites and
the cache rules but does **not** apply the `headers` block, so a local pass
tells you nothing about the Content-Security-Policy. To check the headers you
have to look at a real preview channel deploy (below) with
`curl -sSI <preview-url>`.

To preview with different flag values, stamp the template by hand into a scratch
file and copy it over. Do not commit the result.

```bash
sed -e 's|__WEB_SITE_ENABLED__|false|g' \
    -e 's|__WEB_SIGNIN_ENABLED__|true|g' \
    ... \
    marketing/runtime-config.json.tmpl > /tmp/runtime-config.json
```

## Deploy to a preview channel

This is the default and it is what you want almost every time. With
`_WEB_SITE_ENABLED` left at its default of `false`, the build deploys to a
preview channel that expires in seven days and prints a URL. The live domain is
untouched.

```bash
gcloud config set project roybot
gcloud builds submit galaxies-web --config=galaxies-web/cloudbuild.yaml
```

Smoke it against the URL the build printed:

```bash
PREVIEW=<preview-url>
curl -sS -o /dev/null -w "%{http_code}\n" "$PREVIEW"                  # expect 200
curl -sS "$PREVIEW/runtime-config.json" | jq '{site,signin,ads,download,browser,push}'
# expect all false
curl -sSI "$PREVIEW" | grep -i -E "content-security-policy|x-content-type|referrer-policy|permissions-policy"
```

## Flip a switch

Every switch defaults to `false`. A build with no substitutions ships dark. To
change one durably, set it on the galaxies-web Cloud Build trigger; to test one,
pass it on the command line.

```bash
gcloud builds submit galaxies-web --config=galaxies-web/cloudbuild.yaml \
  --substitutions=_WEB_SITE_ENABLED=true,_WEB_API_BASE_URL=https://api.galaxies.example/v1
```

| Substitution | Off (the default) | On |
|---|---|---|
| `_WEB_SITE_ENABLED` | built site goes to a preview channel; the live domain keeps whatever is on it | promoted to the live channel |
| `_WEB_SIGNIN_ENABLED` | sign-in is disabled and links to a waitlist note | Firebase Auth web flow, Google only |
| `_WEB_ADS_ENABLED` | no AdSense script loads; ad slots render empty | AdSense loads behind the CMP consent gate |
| `_WEB_DOWNLOAD_ENABLED` | downloads say "coming soon"; no installer, no appcast link | installers linked, `appcast.json` live |
| `_WEB_BROWSER_CLIENT_ENABLED` | `/play` says the browser client is not ready and points at the desktop client | `/play` serves the SPA |
| `_WEB_PUSH_ENABLED` | no service worker, no push prompt | FCM web push offered after first sign-in |

And the values behind them: `_WEB_API_BASE_URL`, `_WEB_FIREBASE_PROJECT`
(defaults to `roybot`), `_WEB_ADSENSE_CLIENT`, `_WEB_CMP_ID`,
`_WEB_UPDATE_FEED_URL`. These are not switches, so "false" means nothing to
them; empty is their off state.

The build refuses a few combinations rather than shipping them:

- ads on with no publisher id, no CMP id, or the placeholder still in `ads.txt`
- sign-in, browser client, or push on with no API base URL
- push on while the browser client is off
- downloads on with no feed URL, or with the placeholder still in `appcast.json`

Suggested flip order, one at a time, smoking each: site, ads, downloads, sign-in
(after galaxies-api is live), browser client, push.

## Rollback

Set the substitution back to `false` and rebuild. Or, faster:

```bash
firebase hosting:rollback --project roybot
```

Hosting keeps prior releases, so that restores the last good deploy without a
rebuild. Turning `_WEB_SITE_ENABLED` back to `false` does not remove a site that
is already live; it only stops the next build from promoting. To take a live
site down, roll back to the release you want.

## The cache rule, so it stays true

`firebase.json` marks `/assets/**` immutable for a year and gives `/styles/**`
five minutes. That only works if the convention holds, so here it is:

- **Fingerprinted files go in `/assets/`.** Fonts, images, and any build output
  whose name contains a content hash. The name changes when the bytes change,
  so a year is safe.
- **Stable names go in `/styles/`.** `tokens.css` and `site.css` keep their
  names across edits, so they get a short cache and revalidate.

`index.html`, every other `.html`, `runtime-config.json`, and
`downloads/appcast.json` are `no-cache`. That is what makes a flip land on the
next page view instead of whenever a CDN decides. Do not "optimize" those.

## The Content-Security-Policy, and one honest caveat

The policy in `firebase.json` allows exactly two families of third-party origin:
Google AdSense with its consent management platform, and Firebase Auth with the
Google sign-in handler. Everything else is `'self'`. `object-src` and
`frame-ancestors` are `'none'`.

Two things worth knowing before you touch it:

1. **`__WEB_API_ORIGIN__` is a build-time token.** `connect-src` has to name the
   API host, and the host is not known until deploy. `cloudbuild.yaml` replaces
   the token with the origin from `_WEB_API_BASE_URL`. If you run
   `firebase deploy` by hand from this directory, the token survives, the
   browser ignores it as an unknown source, and calls to the API are blocked
   with a console error. That is a loud failure rather than a quiet one, which
   is the trade we chose, but it is still a footgun. Deploy through Cloud Build.

2. **`script-src` does not allow `'unsafe-inline'`, and `index.html` needs it
   today.** The policy was written on the assumption that the flag reader and
   the ad initialization code would live in a file rather than an inline
   `<script>` tag. They do not; both sit in one inline block at
   `index.html:564`, so the policy blocks them. This is the open break described
   above, not a hypothetical. Fix it by moving the block into a file under
   `'self'`, which is the outcome the policy was designed around, or by adding
   its hash to `script-src` and re-stamping the hash whenever the block changes.
   Nothing here has been tested against live AdSense yet, because ads are off,
   so budget time at the ads flip for a second round of CSP surprises. Neither
   round is a reason to add a blanket `'unsafe-inline'`.

`style-src` does allow `'unsafe-inline'`, because ad iframes inject inline
styles and there is no practical way around it. That is a smaller hole than the
script one and it is a deliberate choice, not an oversight.

## Credentials for the deploy

The Cloud Build service account needs `roles/firebasehosting.admin` and
`roles/firebase.viewer` on `roybot`. The Firebase CLI picks up the build's
application default credentials. If your organization blocks that path, mount a
service account key from Secret Manager and set `GOOGLE_APPLICATION_CREDENTIALS`
on the deploy step; do not fall back to a `firebase login:ci` token, which is
deprecated and is a long-lived credential in a build log waiting to happen.

## Two rules the pages hold to

Written down here because they get broken by accident, not on purpose. Both
hold as of this writing; the point is keeping them true through the next edit.

- **The dedication appears exactly once per property, in the site footer.** Not
  in this README, not on `/support`, not on `/privacy`. Once, verbatim, in the
  footer of the site. It currently sits in the `index.html` footer, which is
  correct, and it is the only copy. Copy it from the Hearthlight assets; do not
  retype it, and do not add a second one when you build a new page.
- **The active game view is a permanent ad-free zone.** Ads belong on the
  marketing site, the lobby, profile pages, and the game-over summary. Never
  over the star map, never between a player and the submit button, never as an
  interstitial on a turn submission, and never on an error or account-deletion
  page.

## Related

- `Documentation/Cloud/specs/galaxies-web.md` is the full service specification,
  including the M1 desktop client adaptation and the M7 browser client.
- `Documentation/Cloud/GALAXIES-CLOUD-DESIGN.md` is the overall design. Section
  G.4, on the GPL boundary and the Stars! name, is an engineering brief for a
  lawyer and not a ruling. Treat it that way.

Galaxies is built on the Stars! Nova engine, an independent, clean-room,
GPL v2 reimplementation of the classic Stars! by the Stars! Nova team. The
original Stars! was proprietary and was never open source. Credit to the Stars!
team for the game, and to Stars! Nova for the engine this runs on. Every GPL v2
notice in this repository stays where it is.
