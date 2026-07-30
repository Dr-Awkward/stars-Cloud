---
name: Bug report
about: Something in Galaxies behaves differently than it should
title: ''
labels: bug
assignees: ''

---

Fill in what you can. A partial report with a game id and a turn year is worth
more than a perfect report with neither. If a field does not apply, write "not
applicable" and move on.

**What went wrong**

One or two sentences, in your own words. What did the game do?

**Which surface**

Pick the one where you saw it. If it spans more than one, pick where you first
noticed and say so below.

- [ ] Desktop client
- [ ] API
- [ ] Turn generation (the overnight resolve)
- [ ] AI seat
- [ ] Marketing site
- [ ] Something else (say what)

**Game id and turn year**

If this happened inside a game, paste the game id and the turn year you were on
(for example, 2412). If you saw it before joining a game, say that instead.
These two values are how a turn gets pulled and replayed, so they matter more
than anything else here.

**Steps to reproduce**

Number them, starting from a state we can reach.

1.
2.
3.

**Expected behavior**

What you thought would happen, and why. If a rule from Stars! Nova or the
original Stars! is what set your expectation, say which.

**Actual behavior**

What happened instead. Exact numbers, ship names, planet names, and error text
help; paraphrase helps less.

**Reproducible or seen once**

- [ ] Reproducible. It happens every time I follow the steps above.
- [ ] Intermittent. It has happened more than once, not every time.
- [ ] Seen once. I cannot get it to happen again.

"Seen once" is still worth reporting. Turn generation runs on a schedule, so a
one-time failure often leaves a trace in the logs even when you cannot repeat
it.

**Screenshots, save files, or logs**

Attach what you have. A screenshot of the wrong number beats a description of
it. Please do not paste anything you would not want public; issues here are
readable by anyone.

**Anything else**

Other players in the game, unusual race settings, a slow connection, a recent
password or account change, whatever context you think is relevant.
