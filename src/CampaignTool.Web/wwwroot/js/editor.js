// Unlayer interop: init, load design, export HTML. The only JavaScript in the app (SPEC "Conventions").
// Unlayer's free embed needs no project id; images upload to our own /assets/images endpoint.

const scriptUrl = "https://editor.unlayer.com/embed.js";
let loading;
let editor;

function loadScript() {
  loading ??= new Promise((resolve, reject) => {
    if (window.unlayer) return resolve();
    const s = document.createElement("script");
    s.src = scriptUrl;
    s.onload = () => resolve();
    s.onerror = () => reject(new Error("The email editor could not be loaded from editor.unlayer.com."));
    document.head.appendChild(s);
  });
  return loading;
}

export async function init(containerId, designJson, mergeTags) {
  await loadScript();
  editor = window.unlayer.createEditor({
    id: containerId,
    displayMode: "email",
    mergeTags,
    features: { preview: false },
  });
  editor.registerCallback("image", async (file, done) => {
    const body = new FormData();
    body.append("file", file.attachments[0]);
    const response = await fetch("/assets/images", { method: "POST", body, credentials: "same-origin" });
    if (!response.ok) { alert(await response.text()); return; }
    done({ progress: 100, url: (await response.json()).url });
  });
  await new Promise((resolve) => {
    editor.addEventListener("editor:ready", resolve);
    if (designJson) editor.loadDesign(JSON.parse(designJson));
  });
}

export function loadDesign(designJson) {
  editor.loadDesign(JSON.parse(designJson));
}

// Returns { design: string, html: string }.
export function exportHtml() {
  return new Promise((resolve) =>
    editor.exportHtml((data) => resolve({ design: JSON.stringify(data.design), html: data.html })));
}
