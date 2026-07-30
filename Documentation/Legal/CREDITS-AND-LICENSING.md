# Credits and licensing: the Galaxies brief for counsel

**Status: DRAFT and ENGINEERING BRIEF. Not legal advice. Not a ruling.**

This document is an engineering-informed reading of where Galaxies stands on
credit, licensing, and naming. It was written by an engineer, not a lawyer, so
that a lawyer has something precise to correct rather than a blank page. Nothing
here is legal advice, and no statement in it should be relied on as a
determination of anything. Section 7 is the itemized list of questions counsel
must answer in writing before launch; that section is the actual point of this
document.

Drafted 20 July 2026 by Marcus Cooper (Farehard).

---

## Contents

1. [The misconception to kill](#1-the-misconception-to-kill)
2. [Three separate risks, which must not be collapsed](#2-three-separate-risks-which-must-not-be-collapsed)
3. [What the code actually says](#3-what-the-code-actually-says)
4. [GPL v2 obligations, and whether each attaches](#4-gpl-v2-obligations-and-whether-each-attaches)
5. [Recommendations](#5-recommendations)
6. [The credit lines, as they should appear](#6-the-credit-lines-as-they-should-appear)
7. [Questions counsel must answer in writing before launch](#7-questions-counsel-must-answer-in-writing-before-launch)

---

## 1. The misconception to kill

There is a belief, repeated casually in forum posts and occasionally by people
who should know better, that "Stars! Nova is a clone of the Stars! source code."
It is almost certainly wrong, we must not repeat it, and we must correct it where
we can, because saying it in our own marketing would manufacture a legal problem
we do not otherwise have.

The accurate statement, to the best of public knowledge:

- **The original Stars! (and Stars! Supernova) was proprietary commercial
  software.** It was developed and sold commercially, and to the best of public
  knowledge its source code was never released as open source. There is no
  public open-source grant from the Stars! rights holders that anyone can point
  to.
- **Stars! Nova is an independent clean-room reimplementation.** The Stars! Nova
  project wrote its own C# codebase to reproduce the game's design and behaviour,
  and released it under the GNU General Public License version 2. It is not a
  fork of, a port of, or a copy of the original Stars! source, because that
  source was never available to fork, port, or copy.
- **This repository is Stars! Nova's own code.** Every source file we have read
  carries the Stars! Nova header and the GPL v2 notice, with the stars-nova
  project and Ken Reed copyrights. The lineage in the file headers is Nova's,
  not the original Stars! team's.

So Galaxies is a cloud port of an independent GPL v2 reimplementation. Our
licence obligations run to the Stars! Nova project, under GPL v2. They do not
run to the original Stars! rights holders under any licence, because we have
never received or used their code.

That does not mean the original Stars! is irrelevant to us. It means the
relevance runs through a different channel, which is the next section.

## 2. Three separate risks, which must not be collapsed

This is the most important structural point in the document. There are three
distinct exposures here. They have different sources, different tests, and
different mitigations. Collapsing them into "the Stars! thing" produces bad
decisions in both directions: either paralysis over a risk that does not exist,
or blindness to one that does.

| # | Risk | Source | Test | Who decides | Our current posture |
|---|---|---|---|---|---|
| 1 | **Copyright and licence in the code we run and ship** | Stars! Nova's GPL v2 | Did we comply with GPL v2 for anything we distribute? | The Stars! Nova copyright holders | Comply fully, and go further than required by publishing the client source. See section 4 |
| 2 | **The Stars! name and trademark** | Trademark law, plus whatever rights the Stars! rights holders hold or have abandoned in the name | Is our use of "Stars!" likely to cause confusion as to source, sponsorship, or affiliation? Does nominative fair use cover a descriptive reference? | The Stars! rights holders, and ultimately a court | Brand the product "Galaxies". Never brand it "Stars!". Use "Stars!" only descriptively, with a disclaimer of affiliation. See section 5 |
| 3 | **Game-design similarity** | Copyright in the original game's expression, and any patent or trade dress rights (patents on a 1995 game are almost certainly expired) | Are we copying protectable expression (art, text, specific written content) as opposed to unprotectable game mechanics, rules, and systems? | The Stars! rights holders, and ultimately a court | We inherit Nova's design similarity; we add no new copying. Ask counsel to assess Nova's own exposure, since we would inherit it |

Three risks, three different mitigations. Risk 1 is fully within our control and
mostly a matter of discipline. Risk 2 is a naming decision we have already made
conservatively. Risk 3 is inherited from Nova and is the one we understand least,
which is precisely why counsel needs to look at it rather than us.

A fourth thing worth stating so it is not silently assumed: **the fact that the
Stars! Nova project has published a GPL v2 reimplementation for years without
apparent objection is not a licence, not a settlement, and not a defence we can
rely on.** It is a fact about the world that counsel may want to weigh; it is not
permission.

## 3. What the code actually says

For counsel's benefit, the concrete facts about this repository:

- Every source file carries a header of the form "This file is part of
  Stars-Nova ... This program is free software; you can redistribute it and/or
  modify it under the terms of the GNU General Public License version 2 as
  published by the Free Software Foundation", together with copyright notices
  for the stars-nova project and for Ken Reed.
- The codebase is roughly 73,000 lines of C#, originally targeting .NET
  Framework 4.8 and WinForms.
- Galaxies modifies it: the shared and server layers are being ported to headless
  .NET, the randomness is being seeded for determinism, the shared-folder file
  exchange is being replaced with cloud storage and an HTTP API, and a control
  plane, scheduler, and identity layer are being added as new code.
- The new cloud code (the API service, the control plane, the scheduler, the
  infrastructure definitions) is our own work and does not link into the GPL
  binaries as a single program; it talks to the engine over process and network
  boundaries. Whether that separation holds as a matter of law is question 5 in
  section 7.
- We distribute a modified client binary to players. We do not currently
  distribute a server binary.

## 4. GPL v2 obligations, and whether each attaches

This is our reading of GPL v2 as applied to what Galaxies actually does. Every
row needs counsel's confirmation; the confidence column is engineering
confidence, not legal opinion.

| Obligation | Attaches to us? | What we must do | Engineering confidence |
|---|---|---|---|
| **Preserve copyright and licence notices** (GPL v2 sections 1 and 2a) | **Yes** | Keep every per-file Stars! Nova header, every copyright line, and the `COPYING` / `LICENSE` text intact through the port. Do not strip headers when rewriting project files, converting to SDK-style csproj, reformatting, or moving files between assemblies. New files we author in the engine assemblies carry the same GPL v2 header plus our own copyright line. Modified files get a note that they were changed and when. | High |
| **Offer corresponding source for distributed binaries** (GPL v2 section 3) | **Yes, for the client we ship** | We hand players a modified WinForms client binary. Distributing that binary triggers the source obligation for the client and everything GPL-covered that it links. We publish the complete corresponding source for the exact client build we ship, from the same place the download lives, including the build scripts needed to produce it. | High |
| **Network-service source obligation** | **No, not from GPL v2 itself** | GPL v2 has no Affero-style network clause. Running modified server code (`ServerState`, `TurnGenerator`, the turn engine) as a hosted service, without handing anyone the server binary, does not by itself compel us to publish the server source. This is the whole difference between GPL v2 and AGPL v3. **Confirm with counsel**, and note this analysis changes the moment we distribute a server binary, for example a downloadable self-host build or a container image published for others to run | High as engineering reading, needs legal confirmation |
| **Derivative-work licensing** (GPL v2 section 2b) | **Yes, for what we distribute** | Our modified client is a derivative work of Stars! Nova. When we distribute it, it goes out under GPL v2 (or GPL-v2-compatible) terms, to all third parties, at no charge, with no additional restrictions. We cannot add our own terms on top of the binary we hand out | High |
| **No additional restrictions** (GPL v2 section 6) | **Yes, for what we distribute** | Our terms of service govern the hosted service, not the software. They must not purport to restrict what a recipient may do with the client binary or its source. `TERMS.md` section 11 says this explicitly and gives the GPL priority over our terms with respect to the software; counsel should check that carve-out is drafted correctly | High as intent, needs legal check on drafting |
| **Adding proprietary pieces** (the "mere aggregation" and derivative-work boundary) | **Careful** | Server-side services that talk to the engine across a process or network boundary (our API, control plane, scheduler, ad and analytics code, the marketing site) are a materially lower risk than statically linking proprietary code into the GPL client. The rule we work to: nothing proprietary links into or ships inside the GPL binaries; the boundary between our cloud code and the engine is a process boundary and a documented wire protocol. **Counsel must confirm where the boundary really sits** | Medium, needs legal |
| **Distributing to third parties who then redistribute** | **Yes** | Anyone who receives our client may redistribute it under GPL v2. We do not try to prevent that. Our only related rule (in `TERMS.md`) is that a modified client must not impersonate our hosted service, which is a service-and-trademark rule, not a software restriction. Counsel should confirm that rule does not read as a GPL section 6 additional restriction | Medium, needs legal |
| **Patent and warranty clauses** (GPL v2 sections 7, 11, 12) | Yes, as-is | Nothing we do triggers a section 7 conflict that we are aware of. The warranty disclaimer in the GPL applies to the software; our own service warranty disclaimer is separate and lives in `TERMS.md` section 9 | Medium |

**The plain reading, in one paragraph.** GPL v2 lets us run a modified server as
a service without publishing the server source, and the moment we hand a player a
modified client binary, that client's complete corresponding source must be
available to them. The honest and low-friction path, and the one we are taking,
is to keep the whole engine open anyway. That removes the ambiguity, matches how
we want to run this, and costs us nothing that we actually value.

## 5. Recommendations

These are the actions we intend to take. Counsel should confirm or correct each
one.

1. **Credit the Stars! team explicitly and by name.** The original Stars! was
   created by Jeff Johnson, Jeff McBride, and Jeromy Walsh (`<confirm the full
   and correct list of credited creators and the correct current rights holder
   before publishing any of these names>`). Say so on the marketing site, in the
   repository README, and in the in-product about screen. We stand on their work
   through two removes and should say it out loud.
2. **Credit Stars! Nova prominently.** Not in a footnote. The engine credit line
   sits next to the product name wherever the product is introduced: "Galaxies is
   built on the Stars! Nova engine." Link the Stars! Nova project and its
   licence.
3. **Keep every GPL v2 notice and per-file header intact.** Make it a mechanical
   check, not an act of memory: a pull-request template checklist item, and
   ideally a CI check that fails the build if a source file in the engine
   assemblies loses its licence header. A discipline that depends on remembering
   will fail eventually.
4. **Publish the modified client source.** We owe it for the client anyway.
   Publish it openly, from the same page as the download, with the build
   instructions and the commit hash the release was built from, so anyone can
   verify that the source matches the binary. Publish the engine changes too,
   even though GPL v2 does not compel the server side.
5. **Brand the product "Galaxies", with a standing "built on the Stars! Nova
   engine" line.** Our mark is Galaxies. The Stars! Nova credit is a credit, not
   a co-brand. The word "Stars!" never appears as our product name, in our
   domain, in an app name, in a logo, or in any position where it could read as
   the source of the product.
6. **Use "Stars!" descriptively only, and ask counsel to confirm nominative fair
   use.** Phrases we expect to use: "a reimagining of the classic Stars!",
   "built on the Stars! Nova engine, an open-source reimplementation of the
   classic Stars!". Phrases we will not use: anything that puts "Stars!" in a
   product-name position, anything implying endorsement, and any use of an
   original Stars! logo, box art, screenshot, or asset.
7. **Carry a disclaimer of affiliation** wherever the descriptive reference
   appears prominently: "Galaxies is not affiliated with, endorsed by, or
   sponsored by the creators or rights holders of Stars!."
8. **Ship no original Stars! assets.** No art, no sounds, no manual text, no data
   files traceable to the original game. If anything in the Nova asset tree is of
   uncertain provenance, find out before launch rather than after.
9. **Clear our own marks.** "Galaxies" is a common word and a crowded field.
   Have counsel run a trademark availability search on "Galaxies" for games and
   online game services in our target jurisdictions, and on "Farehard" and
   "Hearthlight" as company and product-system names, before we spend money on a
   domain, a logo, or an app store listing.
10. **Keep the proprietary code at arm's length.** Our cloud services, ad code,
    consent platform integration, and marketing site never link into the GPL
    binaries. The engine talks to them over the documented wire protocol. Keep
    the repositories, build outputs, and licence files separate enough that the
    boundary is visible to an outsider reading the tree.
11. **Re-open this analysis if we ever distribute a server binary.** A
    self-hostable server build, a published container image, or a desktop
    "single-player offline" package that embeds the engine server all change the
    GPL analysis, because they are distribution. Treat that as a decision that
    requires a fresh legal look, not a release-note item.
12. **Ask the Stars! Nova project.** Not required by anything, and worth doing:
    tell the project what we are building, confirm how they want to be credited,
    and ask whether they have views on the naming. Good manners are cheap and
    occasionally decisive.

## 6. The credit lines, as they should appear

Draft text, subject to counsel confirming the names in item 1 above.

**Standing engine credit (site footer, README, in-product about screen):**

> Galaxies is built on the Stars! Nova engine, an open-source (GPL v2)
> reimplementation of the classic Stars!. The engine stays open, and the client
> source is public.

**Full credit block (about screen, and the credits section of the site):**

> Galaxies is built on the Stars! Nova engine, released under the GNU General
> Public License version 2 by the Stars! Nova project. Our thanks to everyone who
> has contributed to Nova over the years.
>
> The original Stars! was created by `<creator names, to be confirmed>`. Stars!
> was proprietary commercial software and its source was never released; Stars!
> Nova is an independent reimplementation, not a copy of that source. Galaxies is
> not affiliated with, endorsed by, or sponsored by the creators or rights holders
> of Stars!.
>
> The Galaxies client is free software under GPL v2. You can read the source,
> change it, and run your own server. `<link to the source>`

**Repository `LICENSE` and per-file headers:** unchanged from Stars! Nova.
Nothing about the Galaxies port alters them.

## 7. Questions counsel must answer in writing before launch

This is the brief. Each question below needs a written answer we can keep on
file. Questions 1, 2, 5, and 8 are launch blockers; the rest should be answered
before launch but could, at a push, be answered in the weeks after if the answer
does not change a shipped artifact.

**On GPL v2 and the code**

1. **The network-service boundary.** Confirm that GPL v2 imposes no source
   obligation for running modified engine code as a hosted service where no
   server binary is distributed. Confirm what would change that: publishing a
   container image, offering a self-host build, bundling an offline server in a
   desktop package.
2. **The client source offer.** Confirm that publishing the complete
   corresponding source for the exact shipped client build, from the same
   download page, with build instructions, satisfies GPL v2 section 3. Confirm
   the retention obligation (how long the offer and the source must remain
   available) and whether a written offer is needed in addition to the download.
3. **Derivative-work scope.** Which of our new components are derivative works of
   Stars! Nova and which are independent? Specifically: the headless engine port
   itself (clearly derivative), the cloud storage adapters that subclass engine
   classes, the command registry that replaces the engine's order reader, the
   API service that serializes engine objects, the control plane, and the AI
   participant contract.
4. **The terms-of-service interaction.** Does `TERMS.md` section 11 correctly
   subordinate our terms to the GPL for the software? Does any clause in
   `TERMS.md` (particularly the rules on modified clients and on impersonating
   the service in section 4) read as an "additional restriction" prohibited by
   GPL v2 section 6? If so, redraft it.
5. **The proprietary boundary.** Where exactly is the line between our
   proprietary cloud code and the GPL engine? Is a process and network boundary
   with a documented wire protocol sufficient, or does the degree of coupling
   (shared data model, serialization of engine objects, subclassing engine
   classes in server-side code) pull our server code into the derivative work?
   If any of it is derivative, what are our options: relicense it, restructure
   it, or accept publishing it?
6. **Third-party dependencies.** Audit the licence of every library the client
   and engine link (NuGet packages, any bundled binaries) for GPL v2
   compatibility. Flag anything under Apache 2.0, since Apache 2.0 is generally
   treated as incompatible with GPL v2 specifically.
7. **Asset provenance.** Is there anything in the repository's asset tree (icons,
   component images, race art, `components.xml` data, documentation) whose
   provenance is unclear or which may derive from the original Stars!? What
   should we do about anything we cannot clear?

**On the name and trademark**

8. **Nominative fair use.** Confirm that descriptive references to "Stars!" of
   the form in section 6 are nominative fair use and not trademark infringement
   in the US, UK, and EU. Confirm the wording, including whether the disclaimer
   of affiliation is sufficient and where it must appear.
9. **The status of the Stars! mark.** Who holds it now, is it registered and
   live, and has it been abandoned through non-use? Does that change our
   analysis, and does it change whether the rights holder is a realistic
   complainant?
10. **Our own marks.** Are "Galaxies", "Farehard", and "Hearthlight" available in
    the relevant classes and territories? Should we register any of them, and in
    what order of priority given a near-zero budget?
11. **Domain and store listings.** Any constraint on the domain we register or on
    app-store and marketplace listing text, given the descriptive use of
    "Stars!"?

**On game-design similarity**

12. **Inherited exposure.** Stars! Nova reproduces the original game's design and
    behaviour. Copyright generally does not protect game mechanics and rules, but
    it does protect expression. Assess our exposure from operating Nova's
    reimplementation commercially (ad-supported), and identify anything in the
    game's text, art, naming, or data tables that crosses from mechanics into
    protectable expression and should be changed before launch.
13. **The commercial factor.** Does running an ad-supported service on a
    reimplementation of a commercial game materially change the analysis compared
    with the non-commercial hobby project Nova has been?

**On the operating posture**

14. **Entity and liability.** Which entity should operate this, and should the
    entity be formed before launch given the ad revenue and the licensing surface?
15. **Contributor terms.** If we accept outside contributions to the Galaxies
    fork, do we need a contributor licence agreement or a developer certificate
    of origin, and does either interact badly with GPL v2?
16. **What we say publicly.** Review section 1 of this document. Is our public
    statement about the lineage of Stars! and Stars! Nova accurate and safe to
    publish, and is there anything we should say less specifically?

---

**Status: DRAFT and ENGINEERING BRIEF. Not legal advice. Not a ruling.** Every
conclusion in this document is an engineer's reading, offered so a lawyer has
something concrete to correct. Do not treat any row of the obligations table or
any recommendation as settled until counsel has answered section 7 in writing.
