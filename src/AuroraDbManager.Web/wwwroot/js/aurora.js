// Aurora UI. Progressive enhancement only: every page works without this file.
//
//   - the menu button on small screens
//   - forms show that they were submitted, and cannot be submitted twice
//   - forms marked data-confirm ask before they are submitted
//   - a page that shows work in progress reloads itself, for a while, while it is looked at

(function () {
  "use strict";

  document.documentElement.classList.add("js");

  // The menu button.
  var toggle = document.querySelector("[data-nav-toggle]");
  if (toggle) {
    toggle.addEventListener("click", function () {
      var open = document.body.classList.toggle("nav-open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
  }

  // A menu that is a <details> closes when something else is clicked, or on Escape.
  document.addEventListener("click", function (event) {
    document.querySelectorAll("details[data-menu][open]").forEach(function (menu) {
      if (!menu.contains(event.target)) {
        menu.removeAttribute("open");
      }
    });
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      document.querySelectorAll("details[data-menu][open]").forEach(function (menu) {
        menu.removeAttribute("open");
        var summary = menu.querySelector("summary");
        if (summary) {
          summary.focus();
        }
      });
    }
  });

  function markSubmitted(form, submitter) {
    form.setAttribute("aria-busy", "true");
    form.querySelectorAll("button[type=submit], input[type=submit]").forEach(function (button) {
      button.setAttribute("aria-busy", "true");
      // Disabled after the browser has collected the form's values, so nothing is lost from the request.
      window.setTimeout(function () {
        button.disabled = true;
      }, 0);
    });
    if (submitter && submitter.dataset.busyLabel) {
      submitter.textContent = submitter.dataset.busyLabel;
    }
  }

  // Confirmation: <form data-confirm="Delete this?" data-confirm-action="Delete">
  var dialog = document.querySelector("[data-confirm-dialog]");
  var pending = null;

  document.addEventListener("submit", function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) {
      return;
    }

    if (form.dataset.confirm && !form.dataset.confirmed && dialog && typeof dialog.showModal === "function") {
      event.preventDefault();
      pending = { form: form, submitter: event.submitter };
      dialog.querySelector("[data-confirm-message]").textContent = form.dataset.confirm;
      dialog.querySelector("[data-confirm-accept]").textContent = form.dataset.confirmAction || "Confirm";
      dialog.showModal();
      return;
    }

    markSubmitted(form, event.submitter);
  });

  if (dialog) {
    dialog.querySelector("[data-confirm-accept]").addEventListener("click", function () {
      dialog.close();
      if (pending) {
        pending.form.dataset.confirmed = "true";
        if (typeof pending.form.requestSubmit === "function") {
          pending.form.requestSubmit(pending.submitter || undefined);
        } else {
          pending.form.submit();
        }
        pending = null;
      }
    });
    dialog.querySelector("[data-confirm-cancel]").addEventListener("click", function () {
      dialog.close();
      pending = null;
    });
  }

  // Work in progress: <p data-refresh="5"> reloads the page every 5 seconds. Only while the page
  // is visible, never under an open menu, dialog or submitted form, and at most so many times in
  // a row; the count travels in the address's fragment, which the server never sees. A reload is
  // an ordinary request for the page: a session that has ended leads to the sign-in page.
  var refreshLimit = 60;
  var refreshMark = /^#refresh-(\d+)$/.exec(window.location.hash);
  var refresh = document.querySelector("[data-refresh]");
  if (refresh) {
    var refreshSeconds = Math.max(3, parseInt(refresh.dataset.refresh, 10) || 5);
    var refreshCount = refreshMark ? parseInt(refreshMark[1], 10) : 0;
    var refreshTimer = null;

    var scheduleRefresh = function () {
      window.clearTimeout(refreshTimer);
      if (document.visibilityState !== "visible" || refreshCount >= refreshLimit) {
        return;
      }
      refreshTimer = window.setTimeout(function () {
        var busy = document.querySelector("form[aria-busy=true], details[data-menu][open], dialog[open]");
        if (busy) {
          scheduleRefresh();
          return;
        }
        // Replaced, not added: reloading must not fill the back button's history.
        if (window.history && typeof window.history.replaceState === "function") {
          window.history.replaceState(null, "", "#refresh-" + (refreshCount + 1));
        }
        window.location.reload();
      }, refreshSeconds * 1000);
    };

    document.addEventListener("visibilitychange", scheduleRefresh);
    scheduleRefresh();
  } else if (refreshMark && window.history && typeof window.history.replaceState === "function") {
    // The work is done: the address is the page's own again.
    window.history.replaceState(null, "", window.location.pathname + window.location.search);
  }
})();
