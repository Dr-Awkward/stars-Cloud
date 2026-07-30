---
name: Technical debt
about: Code that works today and is going to cost us later
title: ''
labels: technical debt
assignees: ''

---

For code that runs correctly but is expensive to live with. If it produces wrong
results, that is a bug, not debt.

**What the code does now**

Point at the file, class, or subsystem, and describe the current shape in a few
sentences. Link to the lines if you can.

**Why it is a problem**

The specific cost. Slow to change, easy to break, impossible to test, duplicated
in three places, tied to a desktop assumption that the cloud port breaks. Name
the cost rather than calling it ugly.

**What breaks if we leave it alone**

Be concrete. The honest answer is sometimes "nothing, it just stays annoying",
and that is a fine answer; it tells us the priority. If the answer is that turn
generation goes nondeterministic, or that a future migration cannot land, say
that plainly, because it changes everything about when this gets done.

**What it blocks**

The features, fixes, or refactors that are harder or impossible until this is
paid down. Link the issues if they exist.

**Proposed change**

The shape of the fix, not the whole design. Include the smallest version that
buys most of the benefit, and say what you would leave alone.

**Risk of doing it**

What could regress, and how we would know. Turn resolution has to stay
deterministic, so anything touching the engine needs a test that proves the same
inputs still produce the same turn.

**Effort**

Rough size (an afternoon, a week, a month) and how confident you are in that
number. A low-confidence guess is more useful than no guess.
