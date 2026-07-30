// Galaxies, a cloud port of Stars! Nova.
// Copyright (C) 2026 Farehard. GPL v2; see LICENSE.txt at the repository root.
//
// The runtime flag reader and the consent-gated ad loader.
//
// This lives in its own file rather than inline in index.html for one concrete
// reason: firebase.json sets script-src 'self' with no 'unsafe-inline'. An
// inline block therefore needs a sha256 hash in the CSP, and that hash is bound
// to the exact bytes of the script, so any edit to it silently breaks the page
// on the deployed site while local previews stay healthy (the Firebase emulator
// does not send the headers block). That is a bad failure: the flag reader is
// what turns features on, so a stale hash pins the whole site to all-off with
// nothing visibly wrong. Served from 'self', it just works, and it keeps
// working after the next edit.

(function () {
  "use strict";

  var CONFIG_URL = "runtime-config.json";
  var CONFIG_TIMEOUT_MS = 3000;

  /* Every known flag, off. Anything the config does not mention stays off.

     These key names are a contract with runtime-config.json and
     runtime-config.json.tmpl, which publish "signin" and "ads". They used to
     read "googleSignIn" here, which no config has ever published, so the
     merge below resolved it to undefined and forced it false on every load:
     _WEB_SIGNIN_ENABLED=true could never surface the sign-in button. If you
     rename a flag, rename it in all three files in the same commit. */
  var flags = {
    signin: false,
    ads: false
  };

  /* Not a flag, so it is kept off the flags object: the merge below walks
     Object.keys(flags) and would coerce a string to false. The config key is
     "adsenseClient" (it was read as "adsenseClientId" here, which meant
     loadAds always returned early even with ads on). */
  var adsenseClient = "";

  /* The ad loader's reentrancy guard. Deliberately not a DOM attribute; see
     loadAds for why. */
  var adsRequested = false;

  function applyFlags() {
    var i, el, name, on;

    var shown = document.querySelectorAll("[data-flag]");
    for (i = 0; i < shown.length; i++) {
      el = shown[i];
      name = el.getAttribute("data-flag");
      el.hidden = flags[name] !== true;
    }

    var fallbacks = document.querySelectorAll("[data-flag-off]");
    for (i = 0; i < fallbacks.length; i++) {
      el = fallbacks[i];
      name = el.getAttribute("data-flag-off");
      on = flags[name] === true;
      el.hidden = on;
    }
  }

  function readConfig() {
    if (typeof window.fetch !== "function") {
      return Promise.resolve(null);
    }

    var controller = null;
    var options = { cache: "no-store", credentials: "omit" };

    if (typeof window.AbortController === "function") {
      controller = new AbortController();
      options.signal = controller.signal;
      window.setTimeout(function () { controller.abort(); }, CONFIG_TIMEOUT_MS);
    }

    return window.fetch(CONFIG_URL, options)
      .then(function (response) {
        if (!response.ok) { return null; }
        return response.json();
      })
      .catch(function () {
        /* Missing, blocked, slow, or malformed: stay dark, and say so once in
           the console so a broken deploy is findable. */
        if (window.console && console.warn) {
          console.warn("Galaxies: runtime-config.json unavailable, all flags off.");
        }
        return null;
      });
  }

  function mergeConfig(config) {
    if (!config || typeof config !== "object") { return; }
    var source = (config.flags && typeof config.flags === "object") ? config.flags : config;
    var missing = [];
    Object.keys(flags).forEach(function (key) {
      /* A flag this page knows about that the config never mentions is
         almost always a rename that only landed on one side. It still
         resolves to false, which is the safe answer, but it fails silently,
         which is how the googleSignIn / signin split survived. Say it out
         loud so the next rename is caught on the first load instead of after
         someone flips a build substitution and nothing happens. */
      if (!Object.prototype.hasOwnProperty.call(source, key)) { missing.push(key); }
      flags[key] = source[key] === true;
    });
    if (missing.length && window.console && console.warn) {
      console.warn(
        "Galaxies: runtime-config.json is missing these flags, so they stay off: " +
        missing.join(", ") + ". Check the key names against runtime-config.json.tmpl."
      );
    }
    /* Keep the ad client id available for the loader below, if present. An
       empty string is what the all-off default publishes, and it stays
       falsy, so loadAds still declines to run. */
    if (typeof config.adsenseClient === "string") {
      adsenseClient = config.adsenseClient;
    }
  }

  /* ---------------------------------------------------------------------
     CMP INSERTION POINT.

     Install the consent management platform here, before anything below
     runs, and have it expose:

       window.galaxiesConsent = function () { ... }

     returning a value (or a promise for a value) that is exactly true only
     once the user has granted consent for advertising storage in their
     region. Until that function exists and resolves true, no ad script is
     created, the ad slot stays hidden, and the page renders with no
     third-party requests at all. Do not move the AdSense tag above this
     point, and do not put it in the document head.
  --------------------------------------------------------------------- */
  function consentGranted() {
    if (typeof window.galaxiesConsent !== "function") {
      return Promise.resolve(false);
    }
    try {
      return Promise.resolve(window.galaxiesConsent()).then(function (value) {
        return value === true;
      }, function () {
        return false;
      });
    } catch (error) {
      return Promise.resolve(false);
    }
  }

  function loadAds() {
    var slot = document.getElementById("ad-slot-mid");
    var frame = slot ? slot.querySelector(".ad-slot-frame") : null;
    var client = adsenseClient;

    if (!slot || !frame || !client) { return; }

    /* Reentrancy is guarded by a local, not by data-loaded. site.css treats
       [data-loaded="true"] as a synonym for "filled" and strips the
       placeholder dressing when it sees it, so setting it up front would
       clear the dashed frame at request time and leave a bare labelled gap
       while the network was still working. Both DOM attributes now go on
       together, in onload, when the slot really is filled. */
    if (adsRequested) { return; }
    adsRequested = true;

    /* Reserve the space FIRST, before a byte of AdSense is requested.

       site.css section 10 promises that "the frame holds its height from
       first paint, so filling it never pushes the page around", and
       .ad-slot-frame carries the min-height that keeps that promise. But
       .ad-slot[hidden] collapses the whole slot to display: none, so while
       the slot is hidden the reservation does not exist. Unhiding inside the
       script's onload, as this used to, meant the reserved box appeared only
       after the network answered, and everything below the Depth section got
       shoved down at exactly the wrong moment. The two mechanisms were
       mutually exclusive.

       Now the decision is made at config time: ads on, plus consent granted,
       means the empty labelled frame paints immediately at its final height,
       and the creative drops into a box that is already the right size. With
       ads off the slot stays hidden and nothing is reserved, which is the
       correct outcome and unchanged. */
    slot.hidden = false;

    var tag = document.createElement("script");
    tag.async = true;
    tag.crossOrigin = "anonymous";
    tag.src = "https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=" +
              encodeURIComponent(client);

    tag.onload = function () {
      var unit = document.createElement("ins");
      unit.className = "adsbygoogle";
      unit.style.display = "block";
      unit.setAttribute("data-ad-client", client);
      unit.setAttribute("data-ad-slot", frame.getAttribute("data-ad-slot-id") || "");
      unit.setAttribute("data-ad-format", "auto");
      unit.setAttribute("data-full-width-responsive", "true");
      frame.appendChild(unit);

      (window.adsbygoogle = window.adsbygoogle || []).push({});

      /* data-ad-state is the attribute site.css actually reads. The
         placeholder dressing (dashed border, sunken fill, padding) is keyed
         off [data-ad-state="filled"], so setting only data-loaded, as this
         used to, left the dashed placeholder box drawn around the live
         creative forever. site.css matches data-loaded as a synonym, so both
         are set here and they can never disagree. */
      slot.setAttribute("data-ad-state", "filled");
      slot.setAttribute("data-loaded", "true");
    };

    tag.onerror = function () {
      /* Network call failed, so take the reservation back rather than leave
         an empty labelled box on the page. This is the one case where a
         layout shift is the lesser harm. */
      slot.hidden = true;
      slot.removeAttribute("data-ad-state");
    };

    document.body.appendChild(tag);
  }

  readConfig().then(function (config) {
    mergeConfig(config);
    applyFlags();

    if (flags.ads !== true) { return; }
    return consentGranted().then(function (granted) {
      if (granted === true) { loadAds(); }
    });
  });
})();
