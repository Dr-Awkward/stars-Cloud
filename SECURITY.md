# Security

Galaxies is a small project. It is a cloud port of the open-source Stars! Nova
engine, run as a free, ad-supported, play-by-email game, and it is maintained by
one person with help from contributors. That shapes everything on this page. You
will not find a bug bounty, a triage team, or an enterprise response clock here,
because none of those exist. What you will find is a real address, an honest
timeline, and a promise not to come after you for looking.

If you have found something, thank you. Please read the reporting section before
you post it anywhere public.

## How to report

Use either channel. Both reach the maintainer directly.

1. **GitHub private security advisories (preferred).** Open a draft advisory at
   <https://github.com/Dr-Awkward/stars-Cloud/security/advisories/new>. This keeps
   the report private until there is a fix, gives us one place to talk, and
   handles the CVE paperwork if it comes to that.
2. **Email: security@farehard.com.** Use this if you do not have a GitHub account
   or the issue is about the running service rather than the code.

**Note on the address:** `security@farehard.com` is the intended address and is
listed here so the policy is complete. Confirm it is live and monitored before
launch; until the domain and mail routing are provisioned, the GitHub advisory
channel is the one that is certain to reach a human. If mail bounces, that is the
reason, so please fall back to the advisory link.

Please do not open a public GitHub issue for a security problem, and please do
not post it in a game chat or a forum thread first. Public issues are readable by
anyone, including whoever is currently at war with you.

### What to put in the report

Include what you have; a partial report beats no report.

- What you found, in a sentence or two.
- Where: the API, the desktop client, the turn generator, the marketing site, or
  the sign-in flow.
- Steps to reproduce, starting from a state we can reach. Exact request bodies,
  URLs, and response text help more than a description of them.
- What an attacker gets out of it. Reading another empire's intel is a different
  severity from a stack trace in an error page, and knowing which one you have
  helps us schedule the fix honestly.
- Game id and turn year if it happened inside a game. Those two values are how a
  turn gets pulled and replayed.
- Whether you told anyone else, and whether you have a disclosure date in mind.

If you need to send something sensitive (a session token, a captured response,
another player's data you stumbled into), say so and we will arrange a way to get
it across that is not a plaintext email. Do not attach another player's private
data to a public advisory comment.

## Scope

### In scope

- **The public API** (`galaxies-api`) and everything behind it: orders
  submission, intel reads, lobby and game management, the turn clock endpoints,
  and the AI-participant endpoints.
- **Authentication and session handling.** Google sign-in verification, the
  first-party session JWTs, refresh-token rotation and revocation, session
  fixation, and anything that lets one account act as another.
- **Per-empire fog-of-war authorization.** See the section below; this is the one
  that matters most.
- **The client.** The desktop client and its transport layer, including anything
  that lets a modified or hostile client get data or effects the server should
  have refused.
- **Turn generation integrity.** Getting an order applied to an empire you do not
  own, getting a turn generated twice, or getting the deadline scheduler to skip
  or replay a turn.
- **Account lifecycle.** Account deletion that does not delete, export that
  returns someone else's data, invitations that grant a seat they should not.
- **Infrastructure exposure we control.** Publicly readable storage buckets,
  leaked service-account keys, secrets committed to this repository.

### Out of scope

These are real concerns in general; they are just not things a report here can
usefully act on.

- **Third-party ad scripts on the deployed marketing site.** Galaxies is
  ad-supported and serves standard ad tags on the marketing site, the lobby, the
  profile pages, and the game-over summary. Findings about the ad vendor's own
  scripts, their cookies, or their tracking behavior belong with that vendor. If
  you find an ad unit appearing somewhere it is forbidden (the active game view,
  an error page, or the account-deletion flow), report that; it is a real bug
  against our own placement rules.
- **Social engineering.** Phishing the maintainer, contributors, players, or a
  support channel. Please do not test this against real people.
- **Volumetric denial of service.** Traffic floods, load tests, and
  resource-exhaustion runs against the live service. We are on a shared cloud
  budget and a flood costs real money that would otherwise pay for hosting. An
  *algorithmic* denial of service (a single cheap request that hangs turn
  generation, or an order payload that makes the engine loop forever) is very
  much in scope; send that one.
- Reports generated entirely by an automated scanner with no evidence of impact.
- Missing hardening headers, TLS configuration preferences, or best-practice
  findings with no demonstrated attack. Send them as normal issues; they are
  welcome, just not security reports.
- Vulnerabilities in upstream Stars! Nova that do not reach the cloud service.
  Those belong upstream, though a heads-up is appreciated.

## The one property that matters most: your view is yours

Galaxies is a game of secrets. Everyone plans in private, the galaxy resolves all
at once, and the entire experience depends on one property holding:

> A participant, human or AI, must only ever be able to read their own empire's
> fog-of-war view.

Not another empire's owned stars, owned fleets, orders, research, production
queues, designs, or private messages. Not an ally's, not a dead player's while
the game is live, not through an admin path, not through a spectator view, not
through a history scrubber, not through a shared game id, not through a race name
in a filename. If you can read data an empire owns and you are not that empire,
the game is broken in the way that matters most, whatever else is working.

This is enforced server-side at the API boundary. The caller never names their
own empire; the server derives it from the session and the game membership, and a
client-supplied empire id or race name that disagrees is rejected rather than
corrected. If you find any way around that derivation, we want to know today.

**How to report a suspected leak of another empire's data**

- Report it through the private advisory channel, not a public issue, and not in
  the game.
- Include the game id, the turn year, your empire, and the empire whose data you
  could see.
- Include the exact request that returned it and enough of the response to prove
  the data is not yours (a star name, a fleet id, an order you did not give). Do
  not paste the whole response body if it contains another player's plans;
  redact and describe.
- Say whether you have used it in a live game, and whether other players are
  affected. Nobody is going to be banned for reporting honestly. Farming it for
  weeks first and then reporting is a different conversation.
- If a live game is compromised, we may pause that game's clock while we
  investigate. Players in the affected game will be told what happened and what
  was exposed, in plain words.

We treat fog-of-war leaks as the highest severity class in this project, above
availability and above almost everything else.

## What to expect from us, honestly

This is a side project, not a staffed product. Here is the real timeline, not an
SLA:

- **Acknowledgement: within a few days.** Usually sooner. If you have heard
  nothing after a week, ping the other channel; assume the message went missing
  rather than that it was ignored.
- **First assessment: within about two weeks.** We will tell you whether we can
  reproduce it, what severity we think it is, and roughly when it will be fixed.
- **Fix: it depends, and we will say so.** A fog-of-war or authentication bug
  gets worked on immediately and shipped as soon as it is tested. Lower-severity
  issues land in the normal release flow, which can mean weeks.
- **If we cannot fix it, we will say that too**, and explain why, rather than
  leaving your report to rot in a queue.

We may go quiet for a stretch (illness, work, life). Silence is not a
disagreement with your finding.

## Coordinated disclosure

We work on coordinated disclosure and we will not ask you to sit on something
indefinitely.

- Please give us **90 days** from your first report before publishing, or until a
  fix ships, whichever comes first.
- For a bug that is actively leaking live player data, we will try to move much
  faster than that, and we will keep you posted on the actual dates.
- If we need more time, we will ask, explain why, and accept your answer. It is
  your finding.
- If we go silent for 30 days after acknowledgement, treat that as an unlocked
  door: publish, with our blessing.
- Please do not disclose details of another player's exposed data at any point,
  before or after the fix, even once the bug is public.

When a fix ships we will publish what happened, what was exposed if anything, and
who found it. If you would rather not be named, say so and you will not be. If
you would like credit, tell us the name and link you want.

## Good-faith research: no legal threats

If you are researching in good faith under this policy, we will not pursue legal
action against you, we will not ask your employer or your hosting provider to,
and we will not report you to law enforcement. If someone else sends a complaint
about activity that was clearly within this policy, we will say plainly that it
was authorized.

Good faith, concretely, means:

- You test against your own account and your own empire, or against accounts you
  own. Use a test game rather than someone else's live one where you can.
- You stop as soon as you have confirmed a problem. You do not need to read
  another empire's whole turn to prove you could read one line of it.
- You do not modify, delete, or exfiltrate other people's data, and you delete
  any of it you incidentally captured once the report is closed.
- You do not degrade the service for other players (see the denial-of-service
  note above).
- You give us a reasonable window before publishing, per the section above.

This is a statement of intent from the maintainer, not a legal instrument, and it
cannot bind third parties (our cloud provider, the ad vendor, or anyone else's
terms). We will not test its edges if you do not.

## What is covered

Nothing is deployed yet, so today the scope is the code on `master`. Once the
service is live, `master` is the source of what is deployed and the running
service is what matters most. Older tags and any pre-cloud desktop-only history
are not maintained for security. Galaxies is built on the Stars! Nova engine
(GPL v2); vulnerabilities inherited from upstream and reachable through the cloud
service are in scope here, and we will coordinate upstream where it makes sense.

## Do not send

- Player-versus-player complaints. Cheating accusations, griefing, and diplomacy
  gone wrong are a moderation matter, not a security one. Open a normal issue or
  use the in-game report path.
- Requests to recover an account, a lost turn, or a deleted game. Those go
  through support, not here.
- Reports on the private data of a specific player who is not you, unless it is
  evidence for a leak you are reporting.
