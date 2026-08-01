// This machine's configuration. JSON in, DOM built here — /api/machine-settings returns the key list, its current
// values, and per key whether a change bites now or waits for a restart. The server owns that list: a key it doesn't
// know is a 400, so this page cannot write arbitrary configuration into the process.
//
// Each field saves on blur (or on change, for a checkbox). There is no Save button because there is no form: every
// key is written on its own, the way the rest of the app's preferences are.
(function () {
  const $box = document.getElementById("machineFields");
  if (!$box) return;

  const api = {
    load: () => fetch("/api/machine-settings").then(r => r.ok ? r.json() : Promise.reject(new Error(`the server answered ${r.status}`))),
    save: (key, value) => fetch("/api/machine-settings", {
      method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ key, value }),
    }),
    probe: url => fetch("/api/machine-settings/probe", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ url }),
    }).then(r => r.json()),
  };

  function field(s) {
    const wrap = document.createElement("label");
    wrap.className = "mp-field machine-field";
    wrap.title = s.help;

    const label = document.createElement("span");
    label.className = "fld-label";
    label.textContent = s.label;
    if (!s.live) {
      const tag = document.createElement("span");
      tag.className = "machine-restart";
      tag.textContent = "restart to apply";
      tag.title = "Stored straight away. This one is read once while the app starts, so the running process keeps the old value.";
      label.appendChild(document.createTextNode(" "));
      label.appendChild(tag);
    }
    if (s.store === "file") {
      const tag = document.createElement("span");
      tag.className = "machine-restart";
      tag.textContent = "in the config file";
      tag.title = "Kept in the environment's appsettings file, because it is what opens the database every other setting lives in.";
      label.appendChild(document.createTextNode(" "));
      label.appendChild(tag);
    }

    let input;
    if (s.kind === "bool") {
      input = document.createElement("input");
      input.type = "checkbox";
      input.className = "fld-input";
      input.checked = String(s.value).toLowerCase() === "true";
      input.addEventListener("change", () => save(s, input.checked ? "true" : "false", input));
    } else {
      input = document.createElement("input");
      input.type = s.kind === "number" ? "number" : "text";
      input.className = "fld-input";
      input.value = s.value == null ? "" : s.value;
      input.autocomplete = "off";
      input.addEventListener("blur", () => {
        if (input.value === (s.value == null ? "" : s.value)) return;   // nothing typed, nothing to write
        save(s, input.value, input);
      });
    }

    wrap.appendChild(label);
    wrap.appendChild(input);
    if (s.kind === "bool") wrap.classList.add("mp-field-bool");
    return wrap;
  }

  async function save(spec, value, input) {
    input.disabled = true;
    try {
      const r = await api.save(spec.key, value);
      const body = await r.json().catch(() => null);
      if (!r.ok) { toast((body && body.error) || "Couldn't save"); return; }
      spec.value = value;
      toast(spec.live ? `Saved — ${spec.label} is live` : `Saved — ${spec.label} applies on restart`);
      if (spec.key === "ComfyUI:BaseUrl") probe(value);
    } catch (_) { toast("Couldn't save"); }
    finally { input.disabled = false; }
  }

  // Saving an address is not the same as it being right, and the difference only shows up at the next render —
  // by which time nobody connects the two. Ask the address whether anything is there, and say so.
  async function probe(url) {
    const r = await api.probe(url).catch(() => ({ ok: false, error: "no answer" }));
    toast(r.ok ? "The renderer answered" : `Nothing answered at that address — ${r.error}`);
  }

  (async () => {
    let data;
    try { data = await api.load(); }
    catch (e) { $box.innerHTML = `<p class="muted">Configuration couldn’t be loaded — ${escapeHtml(e.message)}.</p>`; return; }

    const head = document.getElementById("machineHead");
    if (head && data.machineName) head.textContent = data.machineName;

    $box.innerHTML = "";
    for (const s of data.settings) $box.appendChild(field(s));

    const note = document.createElement("p");
    note.className = "settings-desc";
    note.style.margin = "14px 0 0";
    note.textContent = `Keys marked "in the config file" are written to ${data.overrideFile}. Everything else is stored in the database, against this machine's name.`;
    $box.appendChild(note);
  })();
})();
