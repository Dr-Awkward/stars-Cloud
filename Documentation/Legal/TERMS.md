# Galaxies terms of service

**Status: DRAFT. Not in force. Pending legal review.**

This document has not been reviewed by a lawyer. It was written to an engineering
brief standard: clear about what the service actually does, honest about where
the real exposure sits, and explicit about the questions counsel has to answer.
It is not legal advice and it is not a ruling. Nobody should publish it, link it
from a sign-up flow, or rely on it until a qualified lawyer in the relevant
jurisdictions has reviewed and amended it.

Drafted 20 July 2026 by Marcus Cooper (Farehard). Effective date: not set.

Placeholders that must be filled before this goes live are written in angle
brackets, for example `<legal entity>`. Every one of them is a launch blocker.

---

## Contents

1. [What Galaxies is](#1-what-galaxies-is)
2. [Who may use Galaxies, and the age gate](#2-who-may-use-galaxies-and-the-age-gate)
3. [Your account](#3-your-account)
4. [Acceptable use](#4-acceptable-use)
5. [The API and automated play](#5-the-api-and-automated-play)
6. [Your games if you delete your account](#6-your-games-if-you-delete-your-account)
7. [Ads and donations](#7-ads-and-donations)
8. [What you write inside the game](#8-what-you-write-inside-the-game)
9. [The service is free, with no warranty and no SLA](#9-the-service-is-free-with-no-warranty-and-no-sla)
10. [Suspension and termination](#10-suspension-and-termination)
11. [The game engine is GPL v2, and what that means for you](#11-the-game-engine-is-gpl-v2-and-what-that-means-for-you)
12. [Changes to these terms](#12-changes-to-these-terms)
13. [Governing law and disputes](#13-governing-law-and-disputes)
14. [Contact](#14-contact)
15. [Open questions for counsel](#15-open-questions-for-counsel)

---

## 1. What Galaxies is

Galaxies is a free, ad-supported, asynchronous turn-based space strategy game
run as an online service by `<legal entity>` ("we", "us"). A game runs for
in-game decades across real-life weeks. Everyone plans a turn in secret, a
deadline passes, the galaxy resolves all at once, and then you wait for the next
deadline.

Galaxies is built on the Stars! Nova engine, an independent open-source
reimplementation released under the GNU General Public License version 2. See
section 11, and see `CREDITS-AND-LICENSING.md` in this folder for the full
credit and licensing position.

These terms cover the hosted service: the website, the lobby, the API, the game
client we distribute, and the games themselves. They do not change your rights
under the GPL v2 with respect to the engine source code. Where these terms and
the GPL v2 appear to conflict about the software itself, the GPL v2 governs the
software.

## 2. Who may use Galaxies, and the age gate

**You must be at least 16 years old to create an account or play.**

We ask you to confirm your age at sign-up. That confirmation is a required step;
you cannot complete sign-up without it. Deliberately giving a false age is a
breach of these terms and is grounds for removing the account.

Why 16 and why we ask at all, stated plainly because it is a real risk and not a
formality:

- Galaxies is ad-supported. Serving ads alongside a service that knowingly
  admits children is a genuine regulatory exposure, not a theoretical one. In
  the United States, COPPA attaches to services directed to or knowingly
  collecting data from children under 13. In the EU and UK, the age at which a
  child can consent to information-society services on their own runs from 13 to
  16 depending on the member state, and the UK Age Appropriate Design Code
  imposes design duties on any service likely to be accessed by children.
  Advertising regimes add their own restrictions on targeting minors.
- Running a single global minimum of 16, with an explicit confirmation at
  sign-up, is the simplest posture that clears the strictest of those thresholds
  without building per-country age logic on day one. It is a deliberate trade:
  it costs us some legitimate 13 to 15 year old players, and it buys a much
  simpler compliance story.
- A self-declared age confirmation is not age verification and we do not pretend
  it is. It establishes that we do not knowingly admit under-16s, and it gives
  us a defensible record. If we learn an account belongs to someone under 16, we
  close it and delete the associated personal data (see `PRIVACY.md`).

**Counsel must confirm:** whether 16 is the right single global floor for an
ad-supported game, whether a self-declared confirmation is sufficient in each
market we serve, and whether we need to disable personalized advertising rather
than merely gate the account.

You also may not use Galaxies if you are barred from doing so under applicable
sanctions or export controls, or if we have previously terminated your account.

## 3. Your account

Sign-in is Google only. There is no password to create, and there is no other
sign-in method. If you do not have or do not want a Google account, you cannot
use the hosted service. That is a real limit and we would rather say it here
than have you find out at the sign-in button.

You are responsible for your Google account's security. Anyone who can sign in
as you can read your empire's private intel and submit your orders, and the
service cannot tell the difference. One account per person. Do not create
additional accounts to occupy multiple seats in the same game, to evade a
suspension, or to inflate a ranking.

We do not receive or store your Google password. What we do receive and store is
listed in `PRIVACY.md`.

## 4. Acceptable use

The short version: play the game, do not make the game worse for other people,
and do not attack the service.

You agree not to:

- **Harass anyone.** No threats, no sexual harassment, no slurs, no targeted
  abuse, no doxxing, no hate speech, and no persistent unwanted contact through
  in-game messages, race names, empire names, or profile fields. Trash talk
  between empires at war is part of the genre; abuse of a person is not, and we
  draw the line at the person rather than the empire.
- **Cheat.** No exploiting a bug in the turn engine, the API, or the client to
  gain an advantage instead of reporting it. No manipulating the turn clock or
  submission process to obtain information other players do not have. No
  operating multiple seats in the same game, whether directly or through another
  person acting on your instructions. No arranged score or ranking manipulation.
- **Attempt to read another empire's data.** Fog of war is the game. Do not try
  to obtain another empire's orders, intel, or private state by any route other
  than playing: not by probing the API for another empire's identifiers, not by
  tampering with tokens or requests, not by exploiting an authorization defect,
  and not by social engineering us or another player's account. If you discover
  a way to see data you should not see, stop and report it (see section 14). We
  will not pursue good-faith security research that is reported promptly and not
  exploited, and the terms of that commitment live in `SECURITY.md`.
- **Abuse the service infrastructure.** No denial-of-service attempts, no
  credential stuffing, no scraping at a rate that degrades the service, no
  attempts to bypass rate limits or quotas, no probing or penetrating systems
  beyond what a normal client does, and no reverse engineering aimed at
  defeating authorization rather than at understanding the open-source client
  (which the GPL v2 expressly permits, see section 11).
- **Circumvent the ads.** You may use whatever browser and extensions you like;
  we are not going to police that, and there are no ads in the active game view
  anyway. What you may not do is redistribute a modified client that
  impersonates the official service, or operate a proxy that strips or replaces
  our advertising while presenting itself as Galaxies.
- **Break the law with it**, or use the service to distribute malware, illegal
  content, or content you have no right to post.

## 5. The API and automated play

Automated play is welcome; automated abuse is not. Galaxies runs an open AI
participant contract precisely so that bots, community AIs, and language-model
agents can play as first-class participants.

The rules for automation:

- An automated participant must present a registered agent credential and must
  occupy a seat that the game host has designated for it. Do not drive a human
  seat with a bot in a game where other players believe they are facing a human,
  unless the game's settings say that is allowed.
- Automated clients must respect published rate limits, must back off on `429`
  and `5xx` responses, and must not poll the API more aggressively than the game
  requires. A play-by-email game resolves once per cadence period; polling it
  every second serves nobody.
- Do not use the API to enumerate identifiers, harvest other players' data, or
  probe authorization boundaries. See section 4.
- We may revoke an agent credential at any time if it degrades the service, and
  we may impose per-account quotas.

## 6. Your games if you delete your account

You can delete your account yourself, at any time, from your profile. Here is
what actually happens, because this is the part people are usually not told.

- **Your account and your personal data go away.** We remove your email address,
  display name, and avatar, sever the link to your Google identity, and purge
  your sessions. The detail is in `PRIVACY.md`.
- **Your empires in live games do not vanish.** They cannot. A galaxy is a
  single shared simulation, and deleting an empire mid-game would corrupt other
  players' games: their scouted intel, their treaties, their war, and their
  score all reference that empire. So instead of deleting the empire, we detach
  it from you and anonymize it. The seat is relabelled (for example "Deleted
  player") and, per the game's settings, is either handed to an AI so the game
  keeps playing properly, or marked idle and excluded from the turn quorum so it
  never stalls anyone.
- **You cannot reclaim that empire later.** Once the link is severed, it is
  severed. If you sign up again with the same Google account, you get a new
  account with no history and no route back into that seat. We keep a
  non-reversible tombstone so the old record cannot be silently reclaimed.
- **Games you host keep running.** Deleting your account does not delete a game
  you created for other people. If you want the game gone, delete the game
  first; if it is already in progress, expect that we will not delete other
  players' game in progress on your say-so.
- **Finished games keep their history.** Completed games, final standings, and
  the game-over summary persist with your empire anonymized.

If you want a copy of your data before you delete, request an export first (see
`PRIVACY.md`). Deletion is not reversible and we cannot restore an account after
the fact.

## 7. Ads and donations

Galaxies is free and carries advertising. Being straight about it:

- Ads appear on the marketing site, the lobby and game browser, profile pages,
  and the game-over summary.
- **The active game view is a permanent ad-free zone.** No ads on the star map,
  the orders screens, or combat. No interstitial between you and submitting a
  turn. No ads on error pages or on the account-deletion flow. No autoplay
  audio.
- In the EU and UK we serve a Google-certified consent management platform, so
  you get the required choice before any personalized advertising loads. What
  the ad platform collects, and your choices, are described in `PRIVACY.md`.

Donations are optional, outbound, and have no effect on gameplay. They are links
to third-party platforms (GitHub Sponsors, Cash App). We do not take payments on
the site, there is no subscription, and nothing in the game is gated behind
money. A donation buys you no advantage, no priority, and no ad removal; it pays
for servers.

## 8. What you write inside the game

You keep ownership of what you write: empire and race names, in-game messages,
and profile text. You grant us a non-exclusive, worldwide, royalty-free licence
to store, transmit, and display that content as needed to run the service,
including delivering your messages to the players you sent them to and showing
your empire name in other players' games and in the game-over summary. That
licence lasts as long as the content is in the service.

We may remove content that breaks section 4, and we may retain moderation
records of removed content and of reports made against an account.

Anything you build that plugs into the AI participant contract is yours. The
contract is open and we do not claim rights in your AI.

## 9. The service is free, with no warranty and no SLA

Read this section as written; it is the honest description of a free service run
by one person.

- Galaxies is provided "as is" and "as available", without warranty of any kind,
  express or implied, including any implied warranty of merchantability, fitness
  for a particular purpose, or non-infringement, to the fullest extent
  applicable law allows.
- **There is no service level agreement.** No uptime commitment, no response-time
  commitment, no guarantee that a turn generates on time, and no guarantee that
  a game finishes. We aim to generate turns on schedule and we publish a status
  page, and that is an intention, not a promise.
- Games can be lost. We keep an immutable snapshot of every game turn. We have
  not yet rehearsed a restore, so treat recovery as intended rather than proven,
  and a serious failure could roll a game back to an earlier turn or end it. If
  that happens we will say so plainly rather than quietly patch over it.
- We may change, suspend, or discontinue any part of the service, including
  ending the service entirely. If we shut Galaxies down we will give notice and
  a data-export window if we are able to.
- To the fullest extent applicable law allows, we are not liable for indirect,
  incidental, special, consequential, or punitive damages, or for lost data, lost
  games, or lost time. Where liability cannot be excluded, it is limited to the
  greater of the amount you paid us in the preceding twelve months (which for a
  free service is zero) or the minimum the law requires.
- Nothing here excludes liability that cannot lawfully be excluded, including
  liability for death or personal injury caused by negligence, for fraud, or
  under any non-waivable consumer rights you have where you live.

**Counsel must confirm:** whether these limitations are enforceable in each
target market, particularly under UK and EU consumer law, and whether a free
service changes the analysis.

## 10. Suspension and termination

We may suspend or terminate an account, revoke an agent credential, remove a
game, or restrict access if we reasonably believe you have broken these terms,
if your use exposes us or other players to legal risk, or if it is necessary to
protect the service.

How we intend to run it:

- For anything short of serious abuse, we will tell you what happened and give
  you a chance to respond. For serious abuse (harassment, attacks on the
  service, attempts to read other empires' data), we act first and explain
  after.
- Suspension pauses your access. Your empires are treated the same way as for a
  deleted account in live games (section 6), so other players' games keep
  running.
- Termination is the end of the account. Data handling on termination follows
  `PRIVACY.md`.
- You can appeal by writing to the contact address in section 14. One person
  reads that mailbox, so expect a human reply and not a fast one.

You may stop using Galaxies at any time and delete your account yourself.

## 11. The game engine is GPL v2, and what that means for you

Galaxies runs on the Stars! Nova engine, which is licensed under the GNU General
Public License version 2. This matters to you, so here is the plain version.

- **The engine and the game client we distribute are free software.** You can
  get the source, study it, change it, and share your changes, on GPL v2 terms.
  We publish the source for the modified client we ship. All original copyright
  notices and per-file licence headers stay intact.
- **You can run your own server.** The client's base URL is configurable, so the
  same binary can point at a deployment you run. We consider that a feature, not
  an abuse, and section 4's rules about modified clients are about
  impersonating our service, not about self-hosting.
- **These terms do not restrict your GPL rights.** They govern our hosted
  service (accounts, games, the API, conduct), not your rights in the software.
  If a term here would purport to limit a right the GPL v2 grants you in the
  software, the GPL v2 wins.
- **The hosted service is not the same thing as the software.** GPL v2 has no
  network clause, so running the engine as a service does not by itself require
  us to publish our server changes. We intend to keep the engine open anyway.
  The full analysis, including where it needs a lawyer, is in
  `CREDITS-AND-LICENSING.md`.
- **Credit where it is owed.** The original Stars! was created by the Stars!
  team; Stars! Nova is an independent clean-room reimplementation by the Stars!
  Nova project. Galaxies is our own brand, built on the Stars! Nova engine. We
  are not affiliated with, endorsed by, or sponsored by the creators or current
  rights holders of Stars!.

## 12. Changes to these terms

We will change these terms as the service changes. When we do:

- We post the updated document with a new effective date and keep the previous
  version available.
- For a material change (anything that meaningfully affects your rights, your
  data, or what you may do), we give notice at least 30 days before it takes
  effect, by email to the address on your account and by a notice in the
  service.
- Continuing to play after the effective date means you accept the new terms. If
  you do not accept them, delete your account; section 6 tells you what happens
  to your games.
- We will not use a "we may change these at any time without notice" clause,
  because it is worth nothing to you and we would rather write terms that mean
  something.

## 13. Governing law and disputes

**Unresolved. This section is a placeholder and is a launch blocker.**

Governing law is `<jurisdiction>` and disputes go to the courts of
`<jurisdiction>`, without prejudice to any non-waivable right you have to bring
proceedings where you live.

We would rather resolve a problem by email than by anything more formal, so
please write to us first.

**Counsel must confirm:** the governing law and forum for a free, worldwide,
consumer-facing service operated from `<operating jurisdiction>`; whether
arbitration or a class-action waiver is appropriate or counterproductive; and
what mandatory consumer protections in the EU, UK, and other target markets
override the choice of law.

## 14. Contact

- General and account issues: `<support address>` (fallback today:
  coop@farehard.com)
- Privacy, data export, deletion: `<privacy address>`, see `PRIVACY.md`
- Security reports: `<security address>`, see `SECURITY.md`
- Legal entity and postal address: `<legal entity and registered address>`

Every one of those addresses must exist and be monitored before launch. Today
there is one person behind them.

---

## 15. Open questions for counsel

Answer these in writing before launch.

1. Is a self-declared minimum age of 16 the right gate for an ad-supported game
   in our target markets, and does it need to be paired with disabling
   personalized ads for all users rather than only in the EU and UK?
2. Do we need separate, jurisdiction-specific age handling (13 in the US under
   COPPA, 13 to 16 in the EU, the UK Age Appropriate Design Code) or does a
   single global floor of 16 suffice?
3. Are the warranty disclaimer and liability cap in section 9 enforceable
   against consumers in the EU, UK, and the US, and does the service being free
   change that?
4. Which entity contracts with players, and is `<legal entity>` formed,
   registered, and correctly named here?
5. Governing law, forum, and whether to include arbitration or a class-action
   waiver.
6. Does section 11's interaction between these terms and the GPL v2 hold up, and
   is the "GPL wins for the software" carve-out drafted correctly?
7. Does the in-game content licence in section 8 cover everything the service
   actually does with player content (delivery, display to other players,
   moderation retention, the public game-over summary)?
8. Are the suspension and termination provisions consistent with the EU Digital
   Services Act notice, statement-of-reasons, and redress obligations, if the
   DSA applies to us at our size?
9. Do we need a separate, explicit acceptance step for these terms at sign-up
   (clickwrap) rather than a notice beside the sign-in button?
10. Does an open API with registered third-party AI agents create obligations we
    have not covered here, and do we need a separate developer agreement?

---

**Status: DRAFT. Not in force. Pending legal review.** Nothing in this document
is legal advice, and it has not been checked by a lawyer. It is an engineering
brief written to give counsel something concrete to correct. Do not publish or
rely on it until a qualified lawyer has reviewed and amended it.
