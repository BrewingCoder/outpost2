// Outpost UI behaviour.
//
// Progressive enhancement throughout: every dialog here is a plain block of the page and
// every form inside one posts to the same endpoint it would have posted to anyway. With
// scripting off you lose the overlay and the live preview, not the ability to use the app.
(function () {
  'use strict';

  var supportsDialog = typeof HTMLDialogElement === 'function';

  // Dialogs are only hidden-by-default once we know we can reopen them. Without this a
  // browser with no <dialog> support would render every one of them inline and unstyled.
  if (supportsDialog) {
    document.documentElement.classList.add('has-dialog');
  }

  function open(id) {
    var d = document.getElementById(id);
    if (!d) return;
    if (supportsDialog && typeof d.showModal === 'function') {
      d.showModal();
      var first = d.querySelector('input:not([type=hidden]), button, select, textarea');
      if (first) first.focus();
    } else {
      d.setAttribute('open', '');
      d.scrollIntoView({ block: 'center' });
    }
  }

  function close(d) {
    if (!d) return;
    if (supportsDialog && typeof d.close === 'function') d.close();
    else d.removeAttribute('open');
  }

  document.addEventListener('click', function (e) {
    var opener = e.target.closest('[data-open]');
    if (opener) { e.preventDefault(); open(opener.getAttribute('data-open')); return; }

    var closer = e.target.closest('[data-close]');
    if (closer) { e.preventDefault(); close(closer.closest('dialog')); return; }

    // Click the backdrop to dismiss. The sheet stops propagation implicitly by being a
    // child, so this only fires on the dialog element itself.
    if (e.target.tagName === 'DIALOG') close(e.target);

    var step = e.target.closest('[data-goto-step]');
    if (step) { e.preventDefault(); showStep(step.closest('dialog'), step.getAttribute('data-goto-step')); }
  });

  // ---- two-step add dialog -------------------------------------------------
  function showStep(dialog, name) {
    if (!dialog) return;
    dialog.querySelectorAll('[data-step]').forEach(function (el) {
      el.hidden = el.getAttribute('data-step') !== name;
    });
    var label = dialog.querySelector('[data-step-label]');
    if (label) {
      label.innerHTML = name === '1'
        ? '<b>Step 1</b> of 2 &middot; Choose a service'
        : '<b>Step 2</b> of 2 &middot; Name it and connect';
    }
    var focus = dialog.querySelector('[data-step="' + name + '"] input:not([type=hidden]), [data-step="' + name + '"] button');
    if (focus) focus.focus();
  }

  // ---- "you'll say" preview ------------------------------------------------
  // Kept in step with CapabilityCatalog.SpokenExample on the server. If a phrase changes
  // there it must change here; there is no shared source for a string a browser needs
  // before any round trip.
  var PHRASES = {
    Microsoft: function (n) { return 'check ' + n + ' email'; },
    YouTrack: function (n) { return "what's open in " + n; },
    GitHub: function (n) { return 'what pull requests am I blocking on ' + n; }
  };

  function updateSay(input) {
    var target = input.closest('.op-field').querySelector('[data-say-preview]');
    if (!target) return;

    var name = (input.value || '').trim();
    var provider = input.getAttribute('data-say') || 'Microsoft';
    var phrase = PHRASES[provider] || PHRASES.Microsoft;

    if (!name) { target.textContent = '—'; return; }
    target.innerHTML = '“' + phrase('<b class="op-mono">' + escapeHtml(name) + '</b>') + '”';
  }

  function escapeHtml(s) {
    return s.replace(/[&<>"']/g, function (ch) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
    });
  }

  // A name that is hard to say is a name that will not work by voice, which is the whole
  // point of it. Warned rather than blocked: it is the user's name to choose.
  function checkName(input) {
    var name = (input.value || '').trim();
    var help = input.closest('.op-field').querySelector('[data-name-warning]');
    if (!help) return;

    var problem = '';
    if (name && /[^a-z0-9 -]/i.test(name)) problem = 'Punctuation is hard to say. Letters work best.';
    else if (/\d/.test(name)) problem = 'Digits are easy to mishear. A word works better.';
    else if (name && name.length < 3) problem = 'Very short names get lost in a sentence.';

    help.textContent = problem;
    help.hidden = !problem;
  }

  document.addEventListener('input', function (e) {
    var input = e.target.closest('[data-callsign]');
    if (!input) return;
    updateSay(input);
    checkName(input);
  });

  document.querySelectorAll('[data-callsign]').forEach(function (i) { updateSay(i); checkName(i); });

  // ---- confirmations -------------------------------------------------------
  // Switching ON something that writes, or acts as the user, restates what it allows.
  // Switching off never asks: making the safe direction cost a click trains the habit of
  // dismissing the dialog.
  document.addEventListener('submit', function (e) {
    var form = e.target;

    var destructive = form.getAttribute('data-confirm-submit');
    if (destructive && !window.confirm(destructive)) { e.preventDefault(); return; }

    var button = form.querySelector('[data-confirm]');
    if (button) {
      var message = button.getAttribute('data-confirm');
      if (message && !window.confirm(message)) { e.preventDefault(); }
    }
  });

  // ---- copy buttons --------------------------------------------------------
  document.addEventListener('click', function (e) {
    var copy = e.target.closest('[data-copy]');
    if (!copy) return;
    e.preventDefault();

    var value = copy.getAttribute('data-copy');
    var said = copy.querySelector('[data-copy-label]') || copy;
    var original = said.textContent;

    function done(ok) {
      said.textContent = ok ? 'Copied' : 'Press Ctrl+C';
      setTimeout(function () { said.textContent = original; }, 1600);
    }

    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(value).then(function () { done(true); }, function () { done(false); });
    } else {
      // Older browsers, and any context where the clipboard API is blocked: select the
      // text so the keyboard shortcut works rather than failing silently.
      var field = document.getElementById(copy.getAttribute('data-copy-field'));
      if (field) { field.select(); }
      done(false);
    }
  });
})();
