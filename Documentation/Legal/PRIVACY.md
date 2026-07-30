# Galaxies privacy policy

**Status: DRAFT. Not in force. Pending legal review.**

This document has not been reviewed by a lawyer. It is written to an engineering
brief standard: it says exactly what the service collects, why, where it goes,
and how long it stays, and it flags every point where a qualified lawyer has to
rule. It is not legal advice. Do not publish it or link it from a sign-up flow
until counsel has reviewed and amended it.

Drafted 20 July 2026 by Marcus Cooper (Farehard). Effective date: not set.

Placeholders in angle brackets, for example `<legal entity>`, must be filled
before launch. Each one is a launch blocker.

The short version that appears on the site is a summary. This document is the
full policy and it governs.

---

## Contents

1. [Who runs Galaxies and who this applies to](#1-who-runs-galaxies-and-who-this-applies-to)
2. [What we collect and why](#2-what-we-collect-and-why)
3. [What we do not collect](#3-what-we-do-not-collect)
4. [Legal basis for each use](#4-legal-basis-for-each-use)
5. [Ads and the consent platform](#5-ads-and-the-consent-platform)
6. [Who else touches your data](#6-who-else-touches-your-data)
7. [How long we keep things](#7-how-long-we-keep-things)
8. [Your rights, and how to actually use them](#8-your-rights-and-how-to-actually-use-them)
9. [Getting a copy of your data](#9-getting-a-copy-of-your-data)
10. [Deleting your account, and what that does to live games](#10-deleting-your-account-and-what-that-does-to-live-games)
11. [Children and the age gate](#11-children-and-the-age-gate)
12. [Where your data is processed, and international transfers](#12-where-your-data-is-processed-and-international-transfers)
13. [Security](#13-security)
14. [Changes to this policy](#14-changes-to-this-policy)
15. [Contact and complaints](#15-contact-and-complaints)
16. [Open questions for counsel](#16-open-questions-for-counsel)

---

## 1. Who runs Galaxies and who this applies to

Galaxies is operated by `<legal entity>` of `<registered address>`. For UK and
EU data protection law, `<legal entity>` is the data controller for everything
described here. `<EU or UK representative, if required>`.

This policy covers the Galaxies website, the lobby and game browser, the game
client we distribute, the API, and the games themselves. It applies to you if
you sign in, and partly if you only visit the marketing site (sections 5 and 6
are the relevant ones then).

## 2. What we collect and why

Everything we hold, itemized. If it is not on this list, we do not have it.

### From Google, when you sign in

Sign-in is Google only. Google gives us a small, fixed set of fields.

| What | Example | Why we need it |
|---|---|---|
| Google subject id (`sub`) | an opaque string such as `1078...` | This is your account. It is the stable key that links you to your empires, and it is deliberately used instead of your email so that changing your email does not break your games. Without it there is no account. |
| Email address | you@example.com | Turn deadline reminders, game invitations, "the galaxy resolved" notices, security notices, and account recovery contact. It is also how we reach you about a suspension or a change of terms. |
| Display name | the name on your Google account | Shown to other players in the lobby, in game membership lists, and in the game-over summary. You can change it in your profile; the changed name is what other players see. |
| Avatar url | a link to the picture on your Google account | Shown next to your name in the lobby and on profiles. It is a link to Google's copy, not a copy we make. |
| Email verified flag | true or false | We require a verified Google email, because it is the cheapest floor against throwaway accounts and scripted abuse. |

We do not receive your Google password, your contacts, your Drive, your calendar,
or anything else. The sign-in scopes we request are the basic profile and email
scopes, and nothing more.

### That you create by playing

| What | Why we need it |
|---|---|
| Game membership: which games you are in, which empire slot you hold, your role (player or host), when you joined | It is the game. Without it we cannot show you your games, authorize your requests, or work out who a turn is waiting on. |
| Orders you submit for your empires | These are the turn. The engine reads them, advances the galaxy one year, and writes the result. |
| Per-empire intel: the private fog-of-war view the engine writes for your empire each turn | This is what you see when you play. It is generated per empire and is only ever served to that empire's owner. |
| In-game messages you send, and empire and race names you choose | Delivered to their recipients and shown in-game. Retained for moderation if reported. |
| Submission and deadline timestamps, missed-turn counts | The turn clock. They decide when a turn generates, whether an AI takes a seat temporarily, and who gets a reminder. |
| Game history and final standings | Finished games keep their result and their summary page. |

### That the service generates automatically

| What | Why we need it |
|---|---|
| Request logs: timestamp, IP address, request path and method, response status, user agent, and where you are signed in, your account id | Security and abuse defence (rate limiting, detecting credential stuffing and attempts to read other empires' data), diagnosing failures, and capacity planning. Sensitive fields are not logged. |
| Session and refresh token records | Keeping you signed in and letting you sign out everywhere. |
| Error reports from the client and server | Fixing crashes. These can include a stack trace and a game and turn identifier. |
| Audit events: sign-in, account changes, moderation actions, suspensions | Abuse history and accountability. These survive account deletion in anonymized form (see section 7). |
| Product analytics, if enabled at launch | Understanding which parts of the service people use. `<Confirm the analytics product and whether it is enabled at launch.>` |

### That you volunteer

Anything you write to us: support email, a bug report, a security report, an
appeal. We keep the correspondence so we can handle the issue and refer back to
it.

## 3. What we do not collect

- No password. There is nothing to leak.
- No payment data. Donations are outbound links to GitHub Sponsors and Cash App;
  those platforms handle the transaction and we never see a card number. We do
  not know who donated unless the platform tells you it is telling us.
- No location beyond what an IP address implies in a request log.
- No contacts, no address book, no device fingerprinting of our own.
- No sale of personal data, and no sharing of it for cross-context behavioural
  advertising beyond what the ad platform does under the consent choice you make
  (section 5). `<Counsel to confirm whether the ad platform's behaviour
  constitutes a "sale" or "share" under the CCPA/CPRA and whether we must offer
  a "Do Not Sell or Share" link.>`

## 4. Legal basis for each use

For people in the UK and EU, UK GDPR and GDPR require a legal basis for each
purpose. This is our reading; counsel must confirm it.

| Purpose | Data | Proposed legal basis |
|---|---|---|
| Creating and running your account, letting you play, generating turns, serving your intel | Google subject id, email, display name, avatar, membership, orders, intel | Performance of a contract (the terms of service) |
| Turn deadline reminders, game invitations, "your turn resolved" notices | Email, membership, submission timestamps | Performance of a contract; we treat these as service messages, not marketing |
| Security, abuse defence, rate limiting, fraud and cheating prevention | Request logs, audit events, session records | Legitimate interests (keeping the service usable and other players' games uncorrupted) |
| Diagnosing failures and improving the service | Error reports, aggregated analytics | Legitimate interests |
| Non-personalized advertising | Page context, coarse signals the ad platform uses | Legitimate interests, subject to the ePrivacy rules on storing or accessing information on your device |
| Personalized advertising in the EU and UK | Advertising identifiers and the signals the ad platform collects | Consent, collected through a Google-certified consent management platform (section 5) |
| Meeting legal obligations, responding to lawful requests, defending claims | Whatever is relevant | Legal obligation, or legitimate interests |

Where we rely on consent, you can withdraw it at any time and that does not
affect anything done before you withdrew. Where we rely on legitimate interests,
you can object; write to us and we will consider it and tell you the outcome.

**Counsel must confirm:** the basis for each row, especially whether service
emails are properly contract performance rather than direct marketing, and
whether legitimate interests is the right basis for request logging at this
scale (a legitimate interests assessment should be documented).

## 5. Ads and the consent platform

Galaxies is free and ad-supported. We would rather tell you how it works than
bury it.

**Where ads appear:** the marketing site, the lobby and game browser, profile
pages, and the game-over summary.

**Where they never appear:** the active game view (star map, orders, combat),
error pages, and the account-deletion flow. There are no interstitials on the
turn flow and no autoplay audio. The active game view is a permanent ad-free
zone; that is a product commitment, not a current configuration.

**The ad platform** is Google AdSense (with Google Ad Manager as a possible
later move). When an ad loads, Google may set or read cookies and similar
identifiers on your device and may process your IP address and interaction data
to select and measure ads. Google acts as an independent controller for much of
that processing. What Google does with it is governed by Google's own policies,
which we link from the ad slots and from the site footer.

**The consent platform.** If you are in the EU, the UK, or Switzerland, we serve
a Google-certified consent management platform. Before any personalized
advertising loads, you get a real choice, including a way to refuse
non-essential cookies that is no harder than accepting them. Your choice is
stored and you can change it at any time from the "Privacy settings" link in the
footer of the ad-supported pages. If you refuse, you still get the whole game;
you get non-personalized ads instead.

`<The "Privacy settings" footer link does not exist yet. The marketing site
footer carries Privacy, Delete your account, and Contact only, and the consent
management platform is not installed (the insertion point in the site's script
is still empty). The link is inert without the platform behind it, so it ships
with the platform rather than before it. Both must be in place before this
policy is published, and the label must read "Privacy settings" here, in
privacy.html, and in the footer itself.>`

Outside those regions we currently serve ads without a consent prompt, subject to
whatever regional rules apply (for example the US state privacy laws' opt-out
requirements). `<Counsel to confirm the correct posture per market, including
whether we must honour Global Privacy Control signals.>`

We do not run ads inside the game and we do not pass your account identity,
email, orders, or intel to the ad platform.

## 6. Who else touches your data

The complete list of third parties, what they get, and why.

| Third party | What they get | Role | Why |
|---|---|---|---|
| Google (Sign-In / Identity) | Your Google account authenticates with them; we receive the fields in section 2 | Independent controller for the Google account itself | It is the only sign-in method |
| Google Cloud Platform (project `roybot`): Cloud Run, Firestore, Cloud Storage, Cloud Tasks, Pub/Sub, Cloud Logging | All service data, at rest and in transit, in their infrastructure | Processor, on Google's Cloud data processing terms | It is where the service runs |
| Google AdSense (and possibly Google Ad Manager) | Ad-serving signals as described in section 5 | Independent controller for ad selection and measurement | It pays for the servers |
| Google-certified consent management platform | Your consent choice, stored on your device | Processor or independent controller depending on the vendor | Legally required consent capture in the EU and UK |
| Postmark (transactional email). Amazon SES is a possible later move if send volume makes cost dominate; this policy changes if it happens | Your email address and the content of service emails, plus delivery and bounce events | Processor | Sending deadline reminders and invitations reliably |
| Firebase Hosting | Static site requests and their logs | Processor | Serving the site and web client |
| Error reporting and monitoring (Google Cloud Error Reporting, Cloud Monitoring) | Stack traces, request metadata | Processor | Finding and fixing failures |

That is the whole list. If it changes, this policy changes with it.

We also disclose data when we are legally required to, when it is necessary to
establish or defend a legal claim, or when there is a genuine risk to someone's
safety. If the service is ever transferred to another entity, your data would
transfer with it and we would tell you before that happened.

## 7. How long we keep things

| Data | Retention |
|---|---|
| Account record (Google subject id, email, display name, avatar) | Until you delete your account, then removed as described in section 10 |
| Session and refresh tokens | Sessions expire on a short TTL; refresh records are purged on sign-out and on account deletion |
| Orders and per-empire intel for a live game | For the life of the game, plus the game's history window |
| Game state snapshots per turn | Kept for the life of the game. Snapshots transition to Coldline storage 30 days after they are written, whether or not the game has finished. There is no archive tier today. |
| Finished game history and final standings | Retained indefinitely, with deleted players' empires anonymized. `<Counsel to confirm whether "indefinitely" is defensible or whether we need a fixed ceiling.>` |
| Request logs | 30 days, then deleted. `<Confirm the log retention configuration matches this number before publishing.>` |
| Error reports | 90 days |
| Audit events (sign-in, moderation actions, suspensions) | 2 years, retained in anonymized form after account deletion so an abuse history survives an account being recreated |
| Support and security correspondence | 2 years from the last message |
| Abandoned games that never started | Deleted after `<N>` days |
| Consent records | As long as the consent platform's standard, and at least as long as needed to prove the choice was made |

If a number in this table does not match how the system is actually configured,
the system is wrong and gets fixed, not the table.

## 8. Your rights, and how to actually use them

If you are in the UK or EU you have the rights below under UK GDPR and GDPR.
Several US state laws (California, Colorado, Connecticut, Virginia and others)
grant similar rights. We intend to honour these for everyone, wherever you live,
because running two standards is more work than running one.

- **Access:** get a copy of what we hold about you (section 9).
- **Rectification:** correct anything inaccurate. Display name is editable in your
  profile; for anything else, write to us.
- **Erasure:** delete your account and your personal data (section 10), subject
  to the game-integrity carve-out explained there.
- **Restriction and objection:** ask us to pause a particular use, or object to
  processing based on legitimate interests.
- **Portability:** get the data you gave us, and your orders, in a
  machine-readable format.
- **Withdraw consent:** change your advertising choice at any time from the
  "Privacy settings" link in the footer of the ad-supported pages (section 5).
- **No automated decisions with legal effect:** we do not make any. Moderation
  and suspension decisions are made by a human.

To use any of them, write to `<privacy address>` from the email address on your
account, or use the in-product controls. We aim to respond within 30 days, which
is the statutory deadline in the UK and EU, and we will tell you if we need the
extension the law allows. There is no charge unless a request is manifestly
unfounded or excessive.

We do not require you to identify yourself beyond proving control of the account,
which for a Google-only sign-in means signing in.

## 9. Getting a copy of your data

There is a self-serve export in your profile. It produces a machine-readable
archive containing:

- your account record (Google subject id, email, display name, avatar url,
  account created date, status),
- every game you are or were a member of, with your empire slot and role,
- your submitted orders, per game and per turn,
- your in-game messages,
- your profile settings and your consent choice,
- the audit events tied to your account.

Two honest limits on the export:

- **Intel is not fully exportable while a game is live.** Your per-empire intel
  view is generated per turn by the engine and is large; we export the current
  turn's view, not every historical turn, while the game is in progress. After a
  game finishes you can export the full history for your empire.
- **We do not export other players' data**, even where it appears in your view.
  Your intel contains what your empire has seen of other empires; that is
  exported as part of your view, but we will not produce another player's
  orders, messages to third parties, or account details.

If the self-serve export fails or you need it in another form, write to
`<privacy address>` and a human will produce it.

## 10. Deleting your account, and what that does to live games

You can delete your account yourself, from your profile, at any time. No email,
no ticket, no retention flow trying to talk you out of it.

**What deletion removes:**

- Your email address, display name, and avatar url are erased.
- The link between your account and your Google identity is severed. We keep a
  non-reversible tombstone (a one-way hash), for one reason only: so that the
  same Google identity cannot silently reclaim the old record, and so a
  suspended account cannot be recreated instantly. The tombstone cannot be
  turned back into your Google id.
- Your sessions and refresh tokens are purged and your tokens revoked.
- Your account is marked deleted and disappears from lobbies, profiles, and
  member lists.

**What deletion cannot remove, and why:**

A galaxy is one shared simulation. Other players have scouted your planets,
fought your fleets, signed treaties with your empire, and hold intel that
references it. Deleting an empire out of a live game would corrupt every other
player's game in that galaxy. So we do the next best thing, and we would rather
be explicit than quietly keep your data:

- **Your empire stays in the game, detached and anonymized.** It is relabelled
  (for example "Deleted player") and no longer references your account.
- **The seat is handed on so the game keeps working.** Depending on the game's
  settings, an AI takes the seat and plays it, or the empire is marked idle and
  excluded from the turn quorum so its silence never stalls the other players.
  Either way, other people's games finish properly.
- **Orders you already submitted stay in the game record**, anonymized. They are
  part of the simulation's history and the turn results depend on them.
- **In-game messages you sent stay in the recipients' mailboxes**, attributed to
  the anonymized empire. We do not reach into other people's game history and
  edit it.
- **Finished games keep their standings**, with your empire anonymized.
- **Audit events survive in anonymized form** for abuse history, and backups age
  out on the normal backup cycle rather than being surgically edited. A deleted
  account's personal data is removed from live systems immediately and falls out
  of backups within `<backup retention window>`.

`<Counsel must confirm that this game-integrity carve-out is a valid limit on
the right to erasure, and on what basis: Article 17(3), legitimate interests in
the integrity of other users' data, or anonymization such that the residual
records are no longer personal data. This is the single most important open
question in this document.>`

**Deletion is not reversible.** You cannot get the account or the empire back. If
you want a copy of anything, export first (section 9).

## 11. Children and the age gate

Galaxies has a minimum age of 16, confirmed at sign-up. The reasoning, including
why an ad-supported service admitting minors without a gate is a real exposure
rather than a theoretical one, is set out in `TERMS.md` section 2.

Galaxies is not directed to children. We do not knowingly collect personal data
from anyone under 16. If we learn that an account belongs to someone under 16 we
close it and delete the personal data associated with it, following section 10,
with the same game-integrity carve-out.

If you believe a child under 16 has given us personal data, write to
`<privacy address>` and we will act on it.

**Counsel must confirm:** whether a self-declared age confirmation is sufficient,
whether we need per-jurisdiction ages (13 under COPPA, 13 to 16 across EU member
states), and whether the UK Age Appropriate Design Code applies to us on a
"likely to be accessed by children" test even with a 16 gate, given that a space
strategy game plainly appeals to teenagers.

## 12. Where your data is processed, and international transfers

Galaxies runs on Google Cloud Platform in project `roybot`, in region
`us-central1`. Google Cloud, Google Sign-In, AdSense, and the
transactional email provider are US-headquartered and operate globally, so your
data will be processed outside the UK and EEA.

For those transfers we rely on `<the EU Commission's adequacy decision for the
EU-US Data Privacy Framework where the recipient is certified, and the Standard
Contractual Clauses with the UK International Data Transfer Addendum
otherwise>`, together with the technical measures in section 13.

**Counsel must confirm:** which transfer mechanism applies to each processor,
whether a transfer risk assessment is required and has been done, and whether the
current Data Privacy Framework status is still good at the time of launch.

## 13. Security

What we actually do:

- Google-only sign-in, so we hold no passwords. Google's own account security
  (including any two-factor you have enabled) protects the front door.
- Short-lived session tokens with rotating refresh tokens, revocable server-side.
- Per-empire authorization enforced at the API boundary. The server derives your
  empire from your session and your game membership; a client cannot assert
  which empire it is. Intel is served only to the empire that owns it, and
  orders can only be written for an empire you hold.
- Private storage buckets with uniform bucket-level access. Nothing about a game
  is publicly readable.
- Encryption in transit (TLS) and at rest (Google Cloud default encryption).
- Per-turn game snapshots with object versioning, restore procedures that get
  tested rather than assumed, and a documented recovery point and recovery time
  objective.
- Structured logging with sensitive fields excluded, and alerting on error rates
  and abnormal traffic.

What we will not claim: that this is a mature security programme. It is one
person's careful engineering on managed infrastructure. If you find a hole, tell
us; `SECURITY.md` explains how and commits us not to pursue good-faith research.

If a breach affects your personal data and meets the legal threshold, we will
notify the relevant supervisory authority within 72 hours and tell you directly
where the risk to you is high. We would tell you anyway.

## 14. Changes to this policy

We update this policy when the service changes. The effective date is at the top
and previous versions stay available. For a material change we give notice at
least 30 days ahead, by email and in the service, and where the change requires
your consent we will ask for it rather than assume it.

## 15. Contact and complaints

- Privacy, exports, deletion: `<privacy address>` (fallback today:
  coop@farehard.com)
- General support: `<support address>`
- Security: `<security address>`, see `SECURITY.md`
- Data controller: `<legal entity>`, `<registered address>`
- Data protection officer: `<not appointed; counsel to confirm whether one is
  required>`

If you are in the UK you can complain to the Information Commissioner's Office
(ico.org.uk). If you are in the EU you can complain to your national supervisory
authority. We would rather you came to us first, but it is your right either
way.

---

## 16. Open questions for counsel

Answer these in writing before launch.

1. **The erasure carve-out in section 10.** Is anonymizing an empire and handing
   the seat to an AI a lawful limit on the right to erasure, and which basis do
   we rely on? Is the residual game record genuinely anonymous, or is it
   pseudonymous and therefore still personal data?
2. **The non-reversible tombstone.** Is retaining a one-way hash of the Google
   subject id after deletion lawful, and on what basis? Does it need its own
   retention limit?
3. **Legal bases table (section 4).** Confirm each row. Is a legitimate interests
   assessment required for request logging and abuse defence, and does it need
   to be documented and published in summary?
4. **Service emails.** Are deadline reminders and game invitations service
   messages under PECR and the ePrivacy Directive, or do they need an opt-in?
   Invitations sent by one player to another's email address are the sharper
   case; confirm whether the inviter or we are the sender.
5. **The consent platform.** Does our configuration meet the EU Digital Markets
   Act and Google's own consent requirements, is "reject all" as easy as "accept
   all", and do we need to run it outside the EU and UK as well?
6. **CCPA/CPRA.** Does serving AdSense constitute a "sale" or "share"? Do we need
   a "Do Not Sell or Share My Personal Information" link, and must we honour
   Global Privacy Control browser signals?
7. **Age.** Is a self-declared 16 gate sufficient, is per-jurisdiction handling
   required, and does the UK Age Appropriate Design Code apply on a "likely to be
   accessed by children" basis regardless of the gate?
8. **International transfers.** Which mechanism applies to each processor, is a
   transfer risk assessment required, and is the EU-US Data Privacy Framework
   status current?
9. **Controller or processor.** Confirm that Google Cloud is a processor and
   AdSense an independent controller in our configuration, and that we have the
   right terms in place with each (Cloud data processing terms, the AdSense
   terms, an email provider DPA).
10. **Records and appointments.** Do we need an Article 30 record of processing,
    a data protection officer, an EU representative under Article 27, or a UK
    representative?
11. **Retention.** Is "indefinitely" defensible for finished-game history and
    standings, or do we need a fixed ceiling? Is 30 days the right log retention?
12. **Breach.** Confirm the notification thresholds and the supervisory
    authorities we would notify, given the controller's establishment.

---

**Status: DRAFT. Not in force. Pending legal review.** Nothing in this document
is legal advice, and it has not been checked by a lawyer. It is an engineering
brief describing what the system genuinely does, written so counsel has
something concrete to correct. Do not publish or rely on it until a qualified
lawyer has reviewed and amended it.
